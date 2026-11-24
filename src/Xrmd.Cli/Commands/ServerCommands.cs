using System.CommandLine;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xrmd.Abstractions.Storage;
using Xrmd.Cli.Auth;
using Xrmd.Cli.Config;
using Xrmd.Abstractions.Sdk;
using Xrmd.Cli.Output;
using Xrmd.Cli.Sdk;
using Xrmd.Cli.Server;
using Xrmd.Core.Errors;

namespace Xrmd.Cli.Commands;

/// <summary>
/// <c>xrmd server init|up|down|status|doctor|upgrade|versions|logs|org add|org remove</c>: a Docker Compose deployment of xrmverse
/// (<c>init</c> writes <c>docker-compose.yml</c> and <c>.env</c>; the rest drive <c>docker compose</c> and the Admin API).
/// </summary>
public static partial class ServerCommands
{
    public const string DockerHint = "Docker was not found. Install Docker (https://docs.docker.com/get-docker/) with the compose plugin and make sure `docker` is on PATH.";
    public const string ComposeFileName = "docker-compose.yml";
    public const string EnvFileName = ".env";

    private const string Lower = "abcdefghijklmnopqrstuvwxyz";
    private const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Digits = "0123456789";
    private const string Symbols = "!%^*-_+="; // safe in .env files and Compose interpolation
    private const int SigningKeyLength = 48;
    private const int PasswordLength = 24;

    private static readonly TimeSpan UpgradeTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The longest one organization's probe may take (<c>status</c>, <c>doctor</c>, <c>upgrade</c>): the HTTP client has no
    /// timeout, and one hung organization must not hold the report of the others (or upgrade's deadline).
    /// </summary>
    public static readonly TimeSpan OrgProbeTimeout = TimeSpan.FromSeconds(15);

    public static Command Build(CliContext ctx) => new("server", "Set up and run an xrmverse server with Docker Compose.")
    {
        Init(ctx),
        Up(ctx),
        Down(ctx),
        Status(ctx),
        Doctor(ctx),
        Upgrade(ctx),
        Versions(ctx),
        Logs(ctx),
        Org(ctx),
    };

    private static Option<string> DirOption() => new("--dir")
    {
        Description = "Directory holding docker-compose.yml and .env (default: current directory).",
        DefaultValueFactory = _ => ".",
    };

    private static Command Init(CliContext ctx)
    {
        var dir = DirOption();
        var org = new Option<string[]>("--org")
        {
            Description = $"Organization to create, repeatable (one database, xrmd-<org> service and CLI profile each); " +
                          $"must match {Installation.OrgNameRule}. Default: xrmd.",
        };
        var port = new Option<int>("--port")
        {
            Description = "Host port of the first organization; the others take the next ports.",
            DefaultValueFactory = _ => Installation.DefaultFirstPort,
        };
        var auth = new Option<string>("--auth")
        {
            Description = "dev | entra | local (the server's own identity provider: needs https organization URLs, " +
                          "generates each organization's administrator and a one-time initial password).",
            DefaultValueFactory = _ => "dev",
        };
        auth.AcceptOnlyFromAmong("dev", "entra", "local");
        var tenant = new Option<string?>("--tenant-id") { Description = "Entra tenant id (required with --auth entra)." };
        var admin = new Option<string[]>("--admin-object-id")
        {
            Description = "Object id (GUID) of each organization's initial System Administrator, repeatable in the order of --org " +
                          $"(required with --auth entra; dev default {DevTokenProvider.DefaultObjectId} for every organization; --auth local: generated).",
        };
        var database = new Option<string>("--database")
        {
            Description = "The engine of the organizations: sqlserver (a bundled SQL Server, or --sql-connection) or postgresql (a bundled " +
                          $"PostgreSQL 16, {ComposeRenderer.PostgresImage}, or --sql-connection; no SQL Server license terms to accept).",
            DefaultValueFactory = _ => "sqlserver",
        };
        database.AcceptOnlyFromAmong("sqlserver", Installation.PostgresDatabase);
        var sqlConnection = new Option<string?>("--sql-connection")
        {
            Description = "Use an external database server: its connection string, without Database (each organization is a database); " +
                          "with --database postgresql a PostgreSQL one (Host=…;Username=…;Password=…). No sqlserver or postgres service is generated.",
        };
        var sqlEdition = new Option<string?>("--sql-edition")
        {
            Description = $"Edition of the bundled SQL Server (MSSQL_PID): {Installation.SqlEditionRule}. Default: " +
                          $"{Installation.DefaultSqlEdition}; with --force, the existing .env's. Not with --sql-connection. " +
                          "See README, \"SQL Server editions\".",
        };
        var image = new Option<string?>("--image")
        {
            Description = $"Server image to run (XRMD_IMAGE). Default: {ServerImage.Repository}:<version of this CLI>, or {ServerImage.LocalImage} " +
                          "for a development CLI; with --force, the existing .env's.",
        };
        var force = new Option<bool>("--force") { Description = "Overwrite existing docker-compose.yml and .env." };
        var domain = new Option<string?>("--domain")
        {
            Description = "Give every organization the public host <org>.<domain> (XRMD_DOMAIN), served on https://<host>/ through a proxy.",
        };
        var publicHost = new Option<string[]>("--public-host")
        {
            Description = "<org>=<host>, repeatable: the public host of one organization (ORG_<ORG>_HOST), instead of <org>.<domain>.",
        };
        var tls = new Option<string?>("--tls")
        {
            Description = "With a public host and the generated proxy (required): acme (certificates from an ACME CA, needs --acme-email), " +
                          "files (--tls-cert and --tls-key, one pair for every host) or internal (Caddy's local CA).",
        };
        tls.AcceptOnlyFromAmong([.. Installation.TlsModes]);
        var acmeEmail = new Option<string?>("--acme-email") { Description = "Account e-mail of the ACME CA (--tls acme)." };
        var tlsCert = new Option<string?>("--tls-cert") { Description = "PEM certificate (chain) covering every host, copied to <dir>/certs/server.crt (--tls files)." };
        var tlsKey = new Option<string?>("--tls-key") { Description = "PEM private key of --tls-cert, copied to <dir>/certs/server.key with mode 600 (--tls files)." };
        var noProxy = new Option<bool>("--no-proxy")
        {
            Description = "With a public host: no generated proxy; your own proxy (nginx, Traefik, IIS ARR) terminates TLS and needs --trusted-proxy.",
        };
        var trustedProxy = new Option<string[]>("--trusted-proxy")
        {
            Description = "IP address or CIDR network of your proxy, repeatable (with --no-proxy, required): only its forwarded headers are honored.",
        };
        var exposePorts = new Option<bool>("--expose-ports")
        {
            Description = "With the generated proxy: keep publishing each organization's loopback port for diagnostics.",
        };
        var edgeSubnet = new Option<string?>("--edge-subnet")
        {
            Description = $"With the generated proxy: the subnet of the compose network edge (default {Installation.DefaultEdgeSubnet}); /27 or larger; the proxy has its .10 address, the other containers get addresses from the upper half.",
        };
        var accept = new Option<bool>("--accept-microsoft-license")
        {
            Description = $"Install the Microsoft SDK into <dir>/sdk now and accept Microsoft's license terms ({SdkPackage.LicenseUrl}) for {SdkCommands.Packages(withWorkflow: true)}. " +
                          "Without it, init asks at a terminal (default no) and otherwise installs nothing: run `xrmd sdk install --dir <dir>` later.",
        };

        var sdkFree = new Option<bool>("--sdk-free")
        {
            Description = "Experimental: an SDK-free server, for apps born on xrmverse only (no SOAP, no Microsoft SDK plug-ins, no exchange with Dataverse). See docs/microsoft-sdk.md.",
        };

        var tdsPort = new Option<int?>("--tds-port")
        {
            Description = "Give the organizations a SQL (TDS) endpoint, read-only: the host port of the first organization (e.g. 5558); the " +
                          "others take the next ports (ORG_<ORG>_TDS_PORT, published on XRMD_TDS_BIND, default every address). Needs " +
                          "--auth local (SQL logins) and a public host (the endpoint serves its certificate).",
        };
        var tdsCert = new Option<string?>("--tds-cert")
        {
            Description = "With --no-proxy and --tds-port (required): PEM certificate (chain) of the SQL endpoints, covering every host, copied to <dir>/certs/tds.crt.",
        };
        var tdsKey = new Option<string?>("--tds-key")
        {
            Description = "With --no-proxy and --tds-port (required): PEM private key of --tds-cert, copied to <dir>/certs/tds.key with mode 600.",
        };

        var acceptSqlEula = SqlEulaOption(
            "Accept the Microsoft license terms of the bundled SQL Server's edition and write ACCEPT_EULA=Y to .env. Without it, " +
            "init shows where the terms are and asks at a terminal (default no); otherwise it writes nothing. Not with --sql-connection.");

        var command = new Command("init", "Write docker-compose.yml and .env with generated secrets for one or more organizations.")
        {
            dir, org, port, auth, tenant, admin, database, sqlConnection, sqlEdition, acceptSqlEula, image, force, accept, sdkFree,
            domain, publicHost, tls, acmeEmail, tlsCert, tlsKey, noProxy, trustedProxy, exposePorts, edgeSubnet, tdsPort, tdsCert, tdsKey,
        };
        command.SetAction(async (parse, ct) =>
        {
            var sdkFreeFlag = parse.GetValue(sdkFree);
            if (sdkFreeFlag && parse.GetValue(accept))
                throw new CliException(ExitCodes.Usage, "--sdk-free and --accept-microsoft-license exclude each other: an SDK-free server never installs the Microsoft SDK.");
            var isEntra = parse.GetValue(auth) == "entra";
            var isLocal = parse.GetValue(auth) == "local";
            var tenantId = parse.GetValue(tenant);
            var portNumber = parse.GetValue(port);
            var orgNames = parse.GetValue(org) is { Length: > 0 } given ? given : ["xrmd"];
            var adminOids = parse.GetValue(admin) ?? [];
            var sql = parse.GetValue(sqlConnection)?.Trim().TrimEnd(';').Trim() ?? "";
            var isPostgres = parse.GetValue(database) == Installation.PostgresDatabase;
            // Task 8 review, Minor 2: --force over an existing .env keeps its engine, as it keeps its passwords and edition (P5):
            // without --database the engine is the .env's; another --database is refused, since the data volume of the engine
            // it runs (sqlserver's mssql-data, postgres's pg-data) holds every organization and would be left behind.
            if (parse.GetValue(force) && ExistingEngine(Path.Combine(Path.GetFullPath(parse.GetValue(dir)!), EnvFileName)) is { } existingEngine)
            {
                var existingPostgres = existingEngine == StorageProvider.PostgreSql;
                if (parse.GetResult(database) is not { Implicit: false }) isPostgres = existingPostgres;
                else if (isPostgres != existingPostgres)
                    throw new CliException(ExitCodes.Rejected,
                        $"The existing .env runs {(existingPostgres ? "PostgreSQL" : "SQL Server")} (" +
                        (existingPostgres ? $"{Installation.StorageProviderKey}={nameof(StorageProvider.PostgreSql)}" : $"no {Installation.StorageProviderKey}") +
                        "), and its data volume holds the organizations: " +
                        $"--force does not change the engine. Leave out --database, or use another --dir.");
            }
            var provider = isPostgres ? StorageProvider.PostgreSql : StorageProvider.SqlServer;

            if (portNumber is < 1 or > 65535) throw new CliException(ExitCodes.Usage, $"--port must be between 1 and 65535: {portNumber}.");
            var firstTdsPort = parse.GetValue(tdsPort);
            if (isPostgres)
            {
                // Plan 2.7a: no SQL Server, so none of its options.
                foreach (var sqlServerOnly in new Option[] { sqlEdition, acceptSqlEula })
                    if (parse.GetResult(sqlServerOnly) is { Implicit: false })
                        throw new CliException(ExitCodes.Usage, $"{sqlServerOnly.Name} applies only to SQL Server: --database {Installation.PostgresDatabase} runs PostgreSQL.");
            }
            if (firstTdsPort is not null && !isLocal)
                throw new CliException(ExitCodes.Usage, $"{Installation.TdsNeedsLocalAuth}: SQL logins are checked by it. Pass --auth local, or leave out --tds-port.");
            if (firstTdsPort is < 1 or > 65535) throw new CliException(ExitCodes.Usage, $"--tds-port must be between 1 and 65535: {firstTdsPort}.");
            foreach (var name in orgNames) Installation.ValidateOrgName(name); // verbatim: never lower-cased
            if (orgNames.GroupBy(n => n).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
                throw new CliException(ExitCodes.Usage, $"--org '{duplicate.Key}' is given more than once.");
            if (isEntra)
            {
                if (string.IsNullOrWhiteSpace(tenantId)) throw new CliException(ExitCodes.Usage, "--auth entra requires --tenant-id.");
                if (!Guid.TryParse(tenantId, out _)) throw new CliException(ExitCodes.Usage, $"--tenant-id must be a GUID: '{tenantId}'.");
                if (adminOids.Length == 0) throw new CliException(ExitCodes.Usage, "--auth entra requires --admin-object-id (one per --org).");
            }
            else if (tenantId is not null)
            {
                throw new CliException(ExitCodes.Usage, "--tenant-id applies only to --auth entra.");
            }
            if (adminOids.Length > 0 && adminOids.Length != orgNames.Length)
                throw new CliException(ExitCodes.Usage,
                    $"--admin-object-id is given {adminOids.Length} time(s) for {orgNames.Length} organization(s): give one per --org, in the same order.");
            if (parse.GetValue(sqlConnection) is not null && Installation.ValidateSqlConnection(sql, provider) is { } sqlError)
                throw new CliException(ExitCodes.Usage, $"--sql-connection {sqlError}.");
            var givenEdition = parse.GetValue(sqlEdition);
            if (givenEdition is not null && sql.Length > 0)
                throw new CliException(ExitCodes.Usage,
                    "--sql-edition applies only to the bundled SQL Server: with --sql-connection the external SQL Server's edition is its own.");
            var edition = givenEdition is null ? null : Installation.NormalizeSqlEdition(givenEdition.Trim())
                ?? throw new CliException(ExitCodes.Usage, $"--sql-edition must be {Installation.SqlEditionRule}: '{givenEdition}'.");

            var givenImage = parse.GetValue(image)?.Trim();
            if (givenImage is not null && !ImagePattern().IsMatch(givenImage))
                throw new CliException(ExitCodes.Usage, $"--image must be an image reference without spaces or quotes, such as {ServerImage.LocalImage}: '{givenImage}'.");
            var defaultImage = ServerImage.Default(ctx.Env.CliVersion); // before anything is written
            var hosts = ParseHostOptions(parse, orgNames, domain, publicHost, tls, acmeEmail, tlsCert, tlsKey, noProxy, trustedProxy, exposePorts, edgeSubnet,
                firstTdsPort is not null, tdsCert, tdsKey);

            var target = Path.GetFullPath(parse.GetValue(dir)!);
            var composePath = Path.Combine(target, ComposeFileName);
            var envPath = Path.Combine(target, EnvFileName);
            var forced = parse.GetValue(force);
            if (!forced)
            {
                foreach (var path in new[] { composePath, envPath })
                    if (File.Exists(path))
                        throw new CliException(ExitCodes.Rejected, $"{path} already exists; use --force to overwrite it.");
            }

            // --force over an existing .env keeps its secrets: SQL Server applies MSSQL_SA_PASSWORD only when the mssql-data
            // volume is first set up, so a new password would lock the services out of the existing instance; and a kept
            // signing key keeps the existing dev tokens valid.
            var existing = forced ? Installation.ReadRawValues(envPath) : [];
            // The SDK mode of an existing server never changes: --force keeps it, and --sdk-free over a standard one is refused.
            var existingSdkFree = existing.TryGetValue(Installation.SdkModeKey, out var keptSdkMode) && Installation.IsSdkFreeValue(keptSdkMode);
            if (sdkFreeFlag && forced && File.Exists(envPath) && !existingSdkFree)
                throw new CliException(ExitCodes.Usage, "The SDK mode of an existing server cannot be changed: it was created as a standard server.");
            var isSdkFree = sdkFreeFlag || existingSdkFree;
            if (isSdkFree && parse.GetValue(accept))
                throw new CliException(ExitCodes.Usage, "--accept-microsoft-license does not apply: the existing server is SDK-free and never installs the Microsoft SDK.");
            var kept = new List<string>();
            string Keep(string key, Func<string> generate)
            {
                if (existing.TryGetValue(key, out var value)) { kept.Add(key); return value; }
                return generate();
            }
            // P5: a data volume keeps the edition it ran. An existing .env of the bundled SQL Server without MSSQL_PID
            // predates the key: its SQL Server ran the image's default, Developer.
            var editionBeforeKey = false;
            var editionKept = false;
            if (sql.Length == 0 && edition is null && !isPostgres)
            {
                if (existing.TryGetValue(Installation.SqlEditionKey, out var keptEdition))
                {
                    edition = Installation.NormalizeSqlEdition(keptEdition.Trim('\'', '"'))
                        ?? throw new CliException(ExitCodes.Usage,
                            $"{envPath} has {Installation.SqlEditionKey}={keptEdition}, which is not {Installation.SqlEditionRule}: pass --sql-edition.");
                    editionKept = true;
                }
                else if (forced && File.Exists(envPath) && !existing.ContainsKey(Installation.SqlConnectionKey))
                {
                    edition = Installation.PreviousDefaultSqlEdition;
                    editionBeforeKey = true;
                }
                else
                {
                    edition = Installation.DefaultSqlEdition;
                }
            }
            // Issue #16: the user accepts the SQL Server license terms (flag or prompt) before anything is written; a .env
            // where they were accepted keeps it under --force.
            if (parse.GetValue(acceptSqlEula) && sql.Length > 0)
                throw new CliException(ExitCodes.Usage,
                    $"{SqlServerLicense.FlagName} applies only to the bundled SQL Server: with --sql-connection the external SQL Server's license is your own.");
            var eulaAccepted = false;
            var eulaKept = false;
            if (sql.Length == 0 && !isPostgres)
            {
                if (parse.GetValue(acceptSqlEula)) eulaAccepted = true;
                else if (existing.TryGetValue(Installation.SqlEulaKey, out var keptEula) && keptEula.Trim('\'', '"') == "Y") eulaAccepted = eulaKept = true;
                else if (await AskSqlEulaAsync(ctx, edition!)) eulaAccepted = true;
                else
                    throw new CliException(ExitCodes.Usage,
                        $"{SqlServerLicense.Notice(edition!)} Nothing was written: run `xrmd server init` again with {SqlServerLicense.FlagName} " +
                        "once you accept them, or use your own SQL Server with --sql-connection.");
            }
            // --image wins; else an existing .env keeps its image (it may be a pinned older version or a custom one); else the CLI's.
            var imageValue = givenImage ?? (existing.TryGetValue(Installation.ImageKey, out var keptImage) && keptImage.Trim('\'', '"').Length > 0
                ? keptImage.Trim('\'', '"')
                : defaultImage);
            if (givenImage is null && imageValue != defaultImage) kept.Add(Installation.ImageKey);
            var values = new Dictionary<string, string>
            {
                ["XRMD_IMAGE"] = imageValue,
                [Installation.SdkModeKey] = Xrmd.Abstractions.Sdk.SdkModes.ToSetting(isSdkFree ? Xrmd.Abstractions.Sdk.SdkMode.None : Xrmd.Abstractions.Sdk.SdkMode.Standard),
                [Installation.StorageProviderKey] = isPostgres ? nameof(StorageProvider.PostgreSql) : "",
                [Installation.PostgresPasswordKey] = isPostgres && sql.Length == 0 ? Keep(Installation.PostgresPasswordKey, () => RandomNumberGenerator.GetString(Lower + Upper + Digits, PostgresPasswordLength)) : "",
                ["SA_PASSWORD"] = sql.Length > 0 || isPostgres ? "" : Keep("SA_PASSWORD", GeneratePassword),
                ["MSSQL_PID"] = edition ?? "",
                [Installation.SqlEulaKey] = eulaAccepted ? "Y" : "",
                ["AUTH_MODE"] = isEntra ? "Entra" : isLocal ? Installation.LocalAuthMode : "Development",
                ["AUTH_TENANT_ID"] = isEntra ? tenantId!.Trim() : "",
                ["AUTH_DEV_SIGNING_KEY"] = isEntra || isLocal ? "" : Keep("AUTH_DEV_SIGNING_KEY", () => RandomNumberGenerator.GetString(Lower + Upper + Digits, SigningKeyLength)),
                [Installation.DomainKey] = hosts.Domain ?? "",
                [Installation.ProxyModeKey] = hosts.ProxyMode,
                [Installation.TlsModeKey] = hosts.TlsMode ?? "",
                [Installation.AcmeEmailKey] = hosts.AcmeEmail ?? "",
                [Installation.TrustedProxiesKey] = string.Join(',', hosts.TrustedProxies),
                [Installation.EdgeSubnetKey] = hosts.EdgeSubnet ?? "",
                [Installation.ExposePortsKey] = hosts.ExposePorts ? "true" : "",
            };
            if (editionKept) kept.Add(Installation.SqlEditionKey);
            var template = EnvTemplate(isPostgres, sql.Length > 0);
            var env = values.Aggregate(template, (text, kv) => text.Replace("{{" + kv.Key + "}}", kv.Value));

            // Every org is validated (name, port, admin) before anything is written.
            var installation = Installation.Create(target, env, sql);
            // Local re-init over a Local .env: an organization keeps its administrator (a new object id would seed a second one)
            // and gets no new password, its credential is already in the database. Over a Development or Entra .env nobody has a
            // credential yet, so every organization is new; an explicit different --admin-object-id is a new administrator too.
            var existingLocal = existing.TryGetValue(Installation.AuthModeKey, out var keptMode) && keptMode.Trim('\'', '"') == Installation.LocalAuthMode;
            var existingOrgs = existingLocal && existing.TryGetValue(Installation.OrgsKey, out var keptOrgs) ? keptOrgs.Trim('\'', '"').Split(',') : [];
            string? KeptAdmin(string name) => existingOrgs.Contains(name) && existing.TryGetValue(Installation.AdminObjectIdKey(name), out var kept) ? kept.Trim('\'', '"') : null;
            var newAdmins = new HashSet<string>();
            for (var i = 0; i < orgNames.Length; i++)
            {
                var oid = adminOids.Length > 0 ? adminOids[i]
                    : isLocal ? KeptAdmin(orgNames[i]) ?? Guid.NewGuid().ToString("D")
                    : DevTokenProvider.DefaultObjectId;
                if (isLocal && !(Guid.TryParse(KeptAdmin(orgNames[i]), out var keptGuid) && Guid.TryParse(oid, out var givenGuid) && keptGuid == givenGuid))
                    newAdmins.Add(orgNames[i]);
                installation.Add(orgNames[i], i == 0 ? portNumber : null, oid, hosts.Overrides.GetValueOrDefault(orgNames[i]));
            }
            installation.RequireProxySettings();
            if (isLocal) RequireHttpsOrganizations(installation);
            // Plan 3.3 (T2): the first organization's SQL endpoint on --tds-port, the next ones on the next ports.
            if (firstTdsPort is { } firstTds)
                for (var i = 0; i < orgNames.Length; i++)
                {
                    if (firstTds + i > 65535)
                        throw new CliException(ExitCodes.Usage,
                            $"--tds-port {firstTds}: the TDS port of '{orgNames[i]}' would be {firstTds + i}, above 65535. Choose a lower first port.");
                    installation.SetTdsPort(orgNames[i], firstTds + i);
                }
            installation.OverwriteForeignCompose = forced;
            // H5: the pair is written beside its targets first and moved into place only once the installation is saved.
            var staged = hosts.CertificatePair is { } pair ? StageCertificatePair(installation, pair.Cert, pair.Key) : [];
            if (hosts.TdsPair is { } tdsPair) staged.AddRange(StageCertificatePair(installation, tdsPair.Cert, tdsPair.Key, Installation.TdsPairName));
            // Local: the initial administrator passwords reach the server through one-shot files, never through .env. Written
            // before anything is saved, so a folder that cannot be written changes nothing; removed again if the save fails.
            var adminPasswords = new Dictionary<string, string>();
            var createdSecrets = isLocal && !Directory.Exists(installation.SecretsPath);
            var createdTds = installation.HasTds && !Directory.Exists(installation.TdsPath);
            try
            {
                if (isLocal)
                    foreach (var o in installation.Orgs.Where(o => newAdmins.Contains(o.Name)))
                        adminPasswords[o.Name] = WriteAdminPassword(installation, o);
                installation.EnsureTdsFolders();
                installation.Save();
            }
            catch
            {
                DiscardCertificatePair(installation, staged);
                foreach (var name in adminPasswords.Keys) DiscardOrgSecretsQuietly(installation, name);
                if (createdSecrets) RemoveEmptyDirectoryQuietly(installation.SecretsPath);
                if (createdTds) DeleteOrgFolderQuietly(installation.TdsPath); // only the empty folders it created
                throw;
            }
            foreach (var (temp, certificate) in staged) File.Move(temp, certificate, overwrite: true);

            var orgs = installation.HasTds
                ? installation.Orgs.Select(o => (object)new { org = o.Name, url = OrgUrl(o), profile = isEntra || isLocal ? null : o.Name, sqlServer = o.TdsServer }).ToList()
                : installation.Orgs.Select(o => (object)new { org = o.Name, url = OrgUrl(o), profile = isEntra || isLocal ? null : o.Name }).ToList();
            var dns = DnsRecords(installation);
            var lines = new List<string>
            {
                installation.Proxy == ProxyMode.Caddy
                    ? $"Wrote {composePath}, {installation.CaddyfilePath} and {envPath} (the latter holds generated secrets; keep it private)."
                    : $"Wrote {composePath} and {envPath} (the latter holds generated secrets; keep it private).",
            };
            if (kept.Where(k => k != Installation.ImageKey).ToList() is { Count: > 0 } keptSecrets)
                lines.Add($"Kept {string.Join(" and ", keptSecrets)} from the existing .env " + (isPostgres
                    ? "(the PostgreSQL data volume keeps its postgres password; existing dev tokens stay valid)."
                    : "(the SQL Server data volume keeps its sa password and edition; existing dev tokens stay valid)."));
            if (kept.Contains(Installation.ImageKey))
                lines.Add($"Kept {Installation.ImageKey}={imageValue} from the existing .env; pass --image to change it.");
            if (imageValue == ServerImage.LocalImage)
                lines.Add($"The server image is {ServerImage.LocalImage}: build it from the repository root before `xrmd server up`: {ServerImage.LocalBuildCommand}");
            if (eulaAccepted)
                lines.Add(eulaKept
                    ? $"Kept {Installation.SqlEulaKey}=Y from the existing .env: the SQL Server license terms were accepted there ({SqlServerLicense.TermsUrl(edition!)})."
                    : $"Wrote {Installation.SqlEulaKey}=Y: you accepted the SQL Server license terms of {SqlServerLicense.Describe(edition!)} ({SqlServerLicense.TermsUrl(edition!)}).");
            if (editionBeforeKey)
                lines.Add($"Wrote {Installation.SqlEditionKey}={Installation.PreviousDefaultSqlEdition}: the existing .env predates {Installation.SqlEditionKey}, " +
                          "so its SQL Server ran the image's default edition, Developer, and its data volume keeps it.");
            if (isEntra)
            {
                // M3: the app registration is not known, so no profile is created.
                lines.Add("Sign in to each organization once the server is up:");
                foreach (var o in installation.Orgs) lines.AddRange(EntraLoginHint(installation, o));
            }
            else if (isLocal)
            {
                // The one-time password is shown here and nowhere else; the profile is created by the first sign-in.
                foreach (var o in installation.Orgs)
                {
                    if (adminPasswords.TryGetValue(o.Name, out var shown))
                    {
                        lines.Add($"Initial admin password for {o.Name} (shown once): {shown}");
                        lines.Add($"  The user name is the administrator's name ({AdminUserName(installation)}); the server reads {installation.AdminPasswordPath(o.Name)} at its first start and deletes it.");
                    }
                    else
                        lines.Add($"{o.Name} keeps its administrator and credential; to set a password: xrmd idp user set-password {AdminUserName(installation)} --password-stdin --profile {o.Name}");
                }
                lines.Add("Sign in to each organization once the server is up:");
                foreach (var o in installation.Orgs) lines.Add("  " + LocalLoginCommand(installation, o));
            }
            else
            {
                // M3: one dev profile per org, named as the org; the first becomes current.
                var config = ctx.Profiles.Load();
                foreach (var o in installation.Orgs) config.Profiles[o.Name] = DevProfile(installation, o);
                config.CurrentProfile = installation.Orgs[0].Name;
                ctx.Profiles.Save(config);
                lines.AddRange(installation.Orgs.Select(o => $"  {o.Name}: {OrgUrl(o)} (profile '{o.Name}')"));
                lines.Add($"Current profile: {installation.Orgs[0].Name}. The profiles sign tokens with {DevTokenProvider.DefaultSigningKeyEnv}: " +
                          $"set it to AUTH_DEV_SIGNING_KEY from {envPath}.");
            }

            if (installation.HasTds) lines.AddRange(TdsLines(installation, installation.Orgs));

            if (dns.Count > 0)
            {
                lines.Add(installation.Proxy == ProxyMode.External
                    ? "Create these DNS records, pointing at your proxy:"
                    : "Create these DNS records, pointing at this server's public address (the proxy listens on ports 80 and 443):");
                lines.AddRange(dns.Select(r => "  " + r));
            }

            // Secrets (and a product key) are never printed: only where they were written.
            object result = dns.Count == 0
                ? new { dir = target, compose = composePath, env = envPath, image = imageValue, orgs, kept }
                : new { dir = target, compose = composePath, env = envPath, image = imageValue, orgs, kept, dns };
            if (isLocal) // json carries the passwords too (once, like the table's lines): the caller is the operator, never a log
                result = new { dir = target, compose = composePath, env = envPath, image = imageValue, orgs, kept, dns,
                               adminPasswords = adminPasswords.Select(kv => new { org = kv.Key, password = kv.Value }).ToList() };
            ctx.WriteResult(result, string.Join(Environment.NewLine, lines));
            WarnIfDeveloperEdition(ctx, installation);
            if (!isSdkFree) await OfferSdkAsync(ctx, installation, parse.GetValue(accept), ct);
            return ExitCodes.Ok;
        });
        return command;
    }

    /// <summary>The <c>--accept-sql-server-eula</c> option of <c>init</c>, <c>up</c> and <c>upgrade</c> (issue #16).</summary>
    private static Option<bool> SqlEulaOption(string description) => new(SqlServerLicense.FlagName) { Description = description };

    private const string SqlEulaOptionText =
        "Accept the Microsoft license terms of the bundled SQL Server's edition when .env has no ACCEPT_EULA=Y yet, and write it. " +
        "Without it such an installation is asked at a terminal (default no) and otherwise refused.";

    /// <summary>
    /// Issue #16: at a terminal, shows where the license terms of <paramref name="edition"/> are and asks (default no). False
    /// without a terminal: nothing is ever accepted for the user.
    /// </summary>
    private static async Task<bool> AskSqlEulaAsync(CliContext ctx, string edition)
    {
        if (!ctx.Env.IsInputTty) return false;
        await ctx.Env.Err.WriteLineAsync(SqlServerLicense.Notice(edition));
        await ctx.Env.Err.WriteAsync("Do you accept the SQL Server license terms? [y/N] ");
        await ctx.Env.Err.FlushAsync();
        var answer = (await ctx.Env.In.ReadLineAsync())?.Trim();
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Issue #16, for the commands that render the compose file of an installation (<c>up</c>, <c>upgrade</c>, <c>org add|remove</c>):
    /// a bundled SQL Server whose <c>.env</c> has no <c>ACCEPT_EULA=Y</c> (written before the key) gets it only from the user's
    /// flag or answer, in the model (the caller saves); otherwise exit 5 naming the flag, before anything changes. True when it wrote.
    /// </summary>
    private static async Task<bool> RequireSqlEulaAsync(CliContext ctx, Installation installation, bool flag)
    {
        if (installation.SqlEulaAccepted) return false;
        // A .env without MSSQL_PID ran the image's default edition (P5), which EnsureSqlEdition writes next.
        var edition = installation.SqlEdition.Length > 0 ? installation.SqlEdition : Installation.PreviousDefaultSqlEdition;
        if (flag || await AskSqlEulaAsync(ctx, edition))
        {
            installation.AcceptSqlEula();
            await ctx.Env.Err.WriteLineAsync($"Wrote {Installation.SqlEulaKey}=Y to {installation.EnvPath}: you accepted the SQL Server license terms of {SqlServerLicense.Describe(edition)}.");
            return true;
        }
        throw new CliException(ExitCodes.Rejected,
            $"{installation.EnvPath} has no {Installation.SqlEulaKey}=Y. {SqlServerLicense.Notice(edition)} " +
            $"Run `xrmd server up --dir {installation.Directory} {SqlServerLicense.FlagName}` once you accept them.");
    }

    /// <summary>
    /// Ruling B7: <c>init</c> never installs the Microsoft SDK silently. With <c>--accept-microsoft-license</c> (the user's own
    /// flag) it installs into <c>&lt;dir&gt;/sdk</c>; at a terminal it asks (default no) and then runs the acceptance prompt of
    /// <c>sdk install</c>; without a terminal it skips. The installation is already written, so a refusal or a failure here
    /// leaves it in place and names the command to run later.
    /// </summary>
    private static async Task OfferSdkAsync(CliContext ctx, Installation installation, bool acceptFlag, CancellationToken ct)
    {
        var folder = installation.SdkPath;
        if (installation.HasSdk) return; // --force over an installation that has its SDK already
        var later = $"xrmd sdk install --dir {installation.Directory}";
        var how = AcceptanceKind.Flag;
        if (!acceptFlag)
        {
            if (!ctx.Env.IsInputTty)
            {
                await ctx.Env.Err.WriteLineAsync($"The Microsoft SDK is not installed. Install it before `xrmd server up`: {later}");
                return;
            }
            await ctx.Env.Err.WriteAsync("Install the Microsoft SDK now? [y/N] ");
            await ctx.Env.Err.FlushAsync();
            var answer = (await ctx.Env.In.ReadLineAsync())?.Trim();
            if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) && !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
            {
                await ctx.Env.Err.WriteLineAsync($"The Microsoft SDK is not installed. Install it before `xrmd server up`: {later}");
                return;
            }
            try { how = await SdkCommands.AcceptAsync(ctx, flag: false); }
            catch (CliException ex)
            {
                await ctx.Env.Err.WriteLineAsync($"{ex.Message} Install it before `xrmd server up`: {later}");
                return;
            }
        }
        try { await SdkCommands.InstallAsync(ctx, [folder], from: null, fromWorkflow: null, how, ct); }
        catch (CliException ex)
        {
            throw new CliException(ex.ExitCode, $"{ex.Message} The installation itself is written; install the SDK later with: {later}", ex);
        }
    }

    /// <summary>The URL clients use for an organization: its public URL, else its URL on this host (H7).</summary>
    internal static string OrgUrl(OrgEntry org) => org.ClientUrl;

    /// <summary>The public host options of <c>server init</c>, validated (H1, H2, H3, H5, H6).</summary>
    private sealed record HostOptions(
        string? Domain,
        Dictionary<string, string> Overrides,
        string ProxyMode,
        string? TlsMode,
        string? AcmeEmail,
        (string Cert, string Key)? CertificatePair,
        IReadOnlyList<string> TrustedProxies,
        string? EdgeSubnet,
        bool ExposePorts,
        (string Cert, string Key)? TdsPair = null);

    /// <summary>
    /// Validates the public host options of <c>server init</c> before anything is written (Review Focus 5): exit 2 for an
    /// invalid host, a host for an organization that is not created, an option that does not apply to the chosen mode, or a
    /// mode without what it needs (H3: <c>--no-proxy</c> without <c>--trusted-proxy</c>; H5: <c>--tls</c>, its e-mail or its files).
    /// Uniqueness of the hosts is checked by <see cref="Installation.Add"/>, which sees the derived ones too.
    /// </summary>
    private static HostOptions ParseHostOptions(ParseResult parse, string[] orgNames, Option<string?> domainOption, Option<string[]> publicHostOption,
        Option<string?> tlsOption, Option<string?> acmeEmailOption, Option<string?> tlsCertOption, Option<string?> tlsKeyOption,
        Option<bool> noProxyOption, Option<string[]> trustedProxyOption, Option<bool> exposePortsOption, Option<string?> edgeSubnetOption,
        bool withTds, Option<string?> tdsCertOption, Option<string?> tdsKeyOption)
    {
        static CliException Usage(string message) => new(ExitCodes.Usage, message);

        // Plan 3.3 (T12): behind your own proxy the SQL endpoints need their own pair; the generated proxy's certificates are used otherwise.
        var tdsCert = parse.GetValue(tdsCertOption);
        var tdsKey = parse.GetValue(tdsKeyOption);
        if (!parse.GetValue(noProxyOption) && (tdsCert is not null || tdsKey is not null))
            throw Usage($"{(tdsCert is not null ? "--tds-cert" : "--tds-key")} applies only with --no-proxy: with the generated proxy the SQL endpoints serve its certificates.");
        if (!withTds && (tdsCert is not null || tdsKey is not null))
            throw Usage($"{(tdsCert is not null ? "--tds-cert" : "--tds-key")} applies only with --tds-port.");

        var domain = parse.GetValue(domainOption)?.Trim();
        if (domain is not null && Installation.ValidateHost(domain) is { } domainProblem)
            throw Usage($"--domain {domainProblem}.");
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in parse.GetValue(publicHostOption) ?? [])
        {
            var eq = entry.IndexOf('=');
            if (eq <= 0) throw Usage($"--public-host must be <org>=<host>, such as acme=acme.example.com: '{entry}'.");
            var (name, host) = (entry[..eq].Trim(), entry[(eq + 1)..].Trim());
            if (!orgNames.Contains(name))
                throw Usage($"--public-host names the organization '{name}', which is not created (--org: {string.Join(", ", orgNames)}).");
            if (Installation.ValidateHost(host) is { } hostProblem) throw Usage($"--public-host {name}: {hostProblem}.");
            if (!overrides.TryAdd(name, host)) throw Usage($"--public-host gives '{name}' a host more than once.");
        }
        var hasHosts = domain is not null || overrides.Count > 0;

        var tlsMode = parse.GetValue(tlsOption);
        var acmeEmail = parse.GetValue(acmeEmailOption)?.Trim();
        var cert = parse.GetValue(tlsCertOption);
        var key = parse.GetValue(tlsKeyOption);
        var noProxy = parse.GetValue(noProxyOption);
        var trusted = (parse.GetValue(trustedProxyOption) ?? []).Select(p => p.Trim()).ToList();
        var expose = parse.GetValue(exposePortsOption);
        var edgeSubnet = parse.GetValue(edgeSubnetOption)?.Trim();

        string? NotApplying()
        {
            if (tlsMode is not null) return "--tls";
            if (acmeEmail is not null) return "--acme-email";
            if (cert is not null) return "--tls-cert";
            if (key is not null) return "--tls-key";
            if (noProxy) return "--no-proxy";
            if (trusted.Count > 0) return "--trusted-proxy";
            if (expose) return "--expose-ports";
            if (edgeSubnet is not null) return "--edge-subnet";
            return null;
        }
        if (!hasHosts)
        {
            if (NotApplying() is { } option)
                throw Usage($"{option} applies only to organizations with a public host: pass --domain or --public-host.");
            return new HostOptions(null, overrides, "", null, null, null, [], null, false);
        }

        if (noProxy)
        {
            // H2/H3: the customer's proxy terminates TLS and must be named, else the generated URLs would be wrong, silently.
            foreach (var (given, option) in new[] { (tlsMode is not null, "--tls"), (acmeEmail is not null, "--acme-email"), (cert is not null, "--tls-cert"),
                         (key is not null, "--tls-key"), (expose, "--expose-ports"), (edgeSubnet is not null, "--edge-subnet") })
                if (given)
                    throw Usage($"{option} does not apply with --no-proxy: your own proxy terminates TLS and reaches the organizations on their published ports.");
            if (trusted.Count == 0)
                throw Usage("--no-proxy requires --trusted-proxy <ip|cidr> (repeatable): the address of your proxy, whose forwarded headers " +
                            "give the organizations their public scheme and host.");
            foreach (var entry in trusted)
                if (Installation.ValidateTrustedProxy(entry) is { } proxyProblem) throw Usage($"--trusted-proxy {proxyProblem}.");
            (string, string)? tdsPair = null;
            if (withTds)
            {
                if (tdsCert is null) throw Usage("--no-proxy with --tds-port requires --tds-cert (and --tds-key): the PEM certificate of the SQL endpoints, covering every host.");
                if (tdsKey is null) throw Usage("--no-proxy with --tds-port requires --tds-key (and --tds-cert): the PEM private key of the certificate.");
                tdsPair = CheckCertificatePair(Path.GetFullPath(tdsCert), Path.GetFullPath(tdsKey), "--tds-cert", "--tds-key");
            }
            return new HostOptions(domain, overrides, Installation.ExternalProxyMode, null, null, null, trusted, null, false, tdsPair);
        }

        if (trusted.Count > 0)
            throw Usage("--trusted-proxy applies only with --no-proxy: the generated proxy has its own fixed address, which the organizations trust.");
        if (tlsMode is null)
            throw Usage($"--tls is required with a public host and the generated proxy: {string.Join(", ", Installation.TlsModes)} (or --no-proxy for your own proxy).");
        if (edgeSubnet is not null && Installation.EdgeSubnetProblem(edgeSubnet) is { } subnetProblem)
            throw Usage($"--edge-subnet {subnetProblem}.");
        if (tlsMode == "acme")
        {
            if (acmeEmail is null) throw Usage("--tls acme requires --acme-email: the account e-mail of the ACME CA.");
            if (Installation.ValidateAcmeEmail(acmeEmail) is { } emailProblem) throw Usage($"--acme-email {emailProblem}.");
        }
        else if (acmeEmail is not null)
        {
            throw Usage("--acme-email applies only to --tls acme.");
        }
        (string, string)? pair = null;
        if (tlsMode == "files")
        {
            if (cert is null) throw Usage("--tls files requires --tls-cert (and --tls-key): one PEM certificate covering every host.");
            if (key is null) throw Usage("--tls files requires --tls-key (and --tls-cert): the PEM private key of the certificate.");
            pair = (CheckCertificatePair(Path.GetFullPath(cert), Path.GetFullPath(key)));
        }
        else if (cert is not null || key is not null)
        {
            throw Usage($"{(cert is not null ? "--tls-cert" : "--tls-key")} applies only to --tls files.");
        }
        return new HostOptions(domain, overrides, Installation.CaddyProxyMode, tlsMode, acmeEmail, pair, [], edgeSubnet, expose);
    }

    /// <summary>Exit 2 unless the files exist and hold a PEM certificate and its private key.</summary>
    private static (string Cert, string Key) CheckCertificatePair(string cert, string key, string certOption = "--tls-cert", string keyOption = "--tls-key")
    {
        foreach (var (path, option) in new[] { (cert, certOption), (key, keyOption) })
            if (!File.Exists(path)) throw new CliException(ExitCodes.Usage, $"{option}: {path} not found.");
        try
        {
            using var _ = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPemFile(cert, key);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new CliException(ExitCodes.Usage,
                $"{certOption} {cert} and {keyOption} {key} are not a PEM certificate and its unencrypted private key: {ex.Message}", ex);
        }
        return (cert, key);
    }

    /// <summary>
    /// H5: writes the certificate pair beside <c>&lt;dir&gt;/certs/server.crt</c> and <c>server.key</c> (mode 600, flushed),
    /// as (temporary file, target) pairs for the caller to move into place once the installation is saved.
    /// </summary>
    private static List<(string Temp, string Target)> StageCertificatePair(Installation installation, string cert, string key,
        string name = Installation.FilesPairName)
    {
        Directory.CreateDirectory(installation.CertsPath);
        var staged = new List<(string Temp, string Target)>();
        try
        {
            foreach (var (source, file) in new[] { (cert, name + ".crt"), (key, name + ".key") })
            {
                var target = Path.Combine(installation.CertsPath, file);
                staged.Add((Installation.WriteTemp(target, File.ReadAllBytes(source), secret: true), target));
            }
        }
        catch
        {
            DiscardCertificatePair(installation, staged);
            throw;
        }
        return staged;
    }

    /// <summary>Deletes the staged files of <see cref="StageCertificatePair"/>, and <c>certs</c> when that leaves it empty.</summary>
    private static void DiscardCertificatePair(Installation installation, List<(string Temp, string Target)> staged)
    {
        foreach (var (temp, _) in staged) File.Delete(temp);
        if (Directory.Exists(installation.CertsPath) && !Directory.EnumerateFileSystemEntries(installation.CertsPath).Any())
            Directory.Delete(installation.CertsPath);
    }

    /// <summary>Review Focus 6: exit 5 before anything is written or Docker runs when <c>TLS_MODE=files</c> lacks a certificate file, naming it.</summary>
    private static void RequireCertificates(Installation installation)
    {
        if (installation.MissingTdsPair() is { Count: > 0 } tdsMissing)
            throw new CliException(ExitCodes.Rejected, TdsPairMissingMessage(installation, tdsMissing));
        if (CaddyfileRenderer.MissingCertificates(installation) is not { Count: > 0 } missing) return;
        throw new CliException(ExitCodes.Rejected,
            $"{string.Join(" and ", missing)} not found in {installation.Directory}: {Installation.TlsModeKey}=files serves the certificate pair " +
            $"certs/server.crt and certs/server.key (PEM, the key unencrypted). Copy them there (mode 600), or re-create the installation with " +
            $"`xrmd server init --force --dir {installation.Directory} … --tls files --tls-cert <file> --tls-key <file>`.");
    }

    /// <summary>The arguments of H8's reload: Caddy re-reads the Caddyfile without dropping connections; the organizations are not touched.</summary>
    private static readonly string[] CaddyReload = ["exec", "-T", "proxy", "caddy", "reload", "--config", "/etc/caddy/Caddyfile"];

    /// <summary>The outcome of <see cref="ApplyCaddyfileAsync"/>: applied, skipped because the proxy is not running, or failed.</summary>
    private sealed record ProxyApply(CliException? Failure = null, bool NotRunning = false)
    {
        public static readonly ProxyApply Applied = new();
    }

    /// <summary>The line that says a rewritten <c>Caddyfile</c> waits for <c>server up</c>.</summary>
    private static string ProxyNotRunningNote(string composeDir) =>
        $"The proxy is not running: `xrmd server up --dir {composeDir}` starts it with the new Caddyfile.";

    /// <summary>
    /// H8: applies a rewritten <c>Caddyfile</c> to the proxy of an installation that had one before (<c>caddy reload</c>) or
    /// starts the proxy alone when the first host appears; a failure names the command that applies it later. A proxy that is
    /// not running (the stack was never started, or is down) is not reloaded: <c>server up</c> starts it with the new file.
    /// </summary>
    private static async Task<ProxyApply> ApplyCaddyfileAsync(CliContext ctx, string composeDir, bool proxyWasThere, CancellationToken ct)
    {
        try
        {
            if (proxyWasThere && (await ComposeAsync(ctx, composeDir, ["ps", "-q", "proxy"], ct)).StandardOutput.Trim().Length == 0)
                return new ProxyApply(NotRunning: true);
            // --no-deps: the organizations are started (or left running) on their own, never recreated for the proxy.
            await ComposeAsync(ctx, composeDir, proxyWasThere ? CaddyReload : ["up", "-d", "--no-deps", "--remove-orphans", "proxy"], ct);
            return ProxyApply.Applied;
        }
        catch (CliException ex)
        {
            return new ProxyApply(new CliException(ex.ExitCode,
                $"The proxy did not {(proxyWasThere ? "reload" : "start with")} the new Caddyfile: {ex.Message} Apply it with " +
                $"`docker {string.Join(' ', ComposeArguments(composeDir, proxyWasThere ? ["restart", "proxy"] : ["up", "-d", "--no-deps", "proxy"]))}`.", ex));
        }
    }

    /// <summary>
    /// The DNS records an installation needs: one wildcard <c>*.&lt;domain&gt;</c> for the hosts it derives from
    /// <c>XRMD_DOMAIN</c>, and one record per other host.
    /// </summary>
    private static List<string> DnsRecords(Installation installation)
    {
        var records = new List<string>();
        foreach (var org in installation.Orgs)
            if (DnsRecord(installation, org) is { } record && !records.Contains(record)) records.Add(record);
        return records;
    }

    /// <summary>The DNS record that serves <paramref name="org"/>'s host: <c>*.&lt;domain&gt;</c> for one label under XRMD_DOMAIN, else the host; null without a host.</summary>
    private static string? DnsRecord(Installation installation, OrgEntry org)
    {
        if (org.Host is not { } host) return null;
        var domain = installation.Domain;
        return domain is not null && host.EndsWith("." + domain, StringComparison.Ordinal) && !host[..^(domain.Length + 1)].Contains('.')
            ? "*." + domain
            : host;
    }

    /// <summary>Local mode: the identity provider's issuer, audience and WebFinger need https, so every organization needs an https URL (a public host, or an https audience).</summary>
    private static void RequireHttpsOrganizations(Installation installation)
    {
        foreach (var org in installation.Orgs)
            if (Installation.SplitAudiences(org.EffectiveAudience).Any(a => !a.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                throw new CliException(ExitCodes.Usage,
                    $"--auth local requires an https URL for organization '{org.Name}' (its audience is {org.EffectiveAudience}): " +
                    "give it a public host with --domain or --public-host, behind the generated proxy or your own.");
    }

    /// <summary>The sign-in name of the initial administrator (<c>SEED_ADMIN_NAME</c>, the user's domain name).</summary>
    private static string AdminUserName(Installation installation) =>
        installation.TryGetValue("SEED_ADMIN_NAME") is { Length: > 0 } name ? name : "Administrator";

    /// <summary>A new 20-character initial administrator password, written to <c>&lt;dir&gt;/secrets/&lt;org&gt;/admin-password</c> (0600); returned to be shown once.</summary>
    /// <remarks>
    /// Both folders (<c>secrets</c> and the organization's) are created 0700 for the host user, and the organization's container
    /// mounts only its own. <c>secrets-init</c> (root) then gives each organization folder group 1654 and mode 2770 (no other
    /// bits: nobody else can plant a file) and chowns only the regular, unlinked files in it owned by the host user to 1654, so
    /// the server can read and delete its 0600 file while the host user, as the folder's owner, can still replace files.
    /// </remarks>
    private static string WriteAdminPassword(Installation installation, OrgEntry org)
    {
        var password = RandomNumberGenerator.GetString(Lower + Upper + Digits, AdminPasswordLength);
        if (!Directory.Exists(installation.SecretsPath)) ConfigFiles.EnsureDirectory(installation.SecretsPath);
        ConfigFiles.EnsureDirectory(installation.OrgSecretsPath(org.Name));
        ConfigFiles.WriteAllBytes(installation.AdminPasswordPath(org.Name), Encoding.UTF8.GetBytes(password + "\n"));
        return password;
    }

    private const int AdminPasswordLength = 20;

    private static void RemoveEmptyDirectoryQuietly(string path)
    {
        try { if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Undoes <see cref="WriteAdminPassword"/>: the password file, then the organization's folder if it is empty.</summary>
    private static void DiscardOrgSecretsQuietly(Installation installation, string org)
    {
        DeleteQuietly(installation.AdminPasswordPath(org));
        RemoveEmptyDirectoryQuietly(installation.OrgSecretsPath(org));
    }

    /// <summary>Deletes the organization's secrets folder (what is left of it); a symbolic link is unlinked, never followed.</summary>
    private static void DeleteOrgSecretsQuietly(Installation installation, string org) => DeleteOrgFolderQuietly(installation.OrgSecretsPath(org));

    /// <summary>Deletes one organization's folder (secrets, SQL endpoint certificates); a symbolic link is unlinked, never followed.</summary>
    private static void DeleteOrgFolderQuietly(string path)
    {
        try
        {
            if (new DirectoryInfo(path).LinkTarget is not null) Directory.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The sign-in command of a Local organization; behind Caddy's internal CA it names the exported root (<c>server up</c> writes it).</summary>
    private static string LocalLoginCommand(Installation installation, OrgEntry org) =>
        $"xrmd auth login --profile {org.Name} --url {org.ClientUrl} --mode device-code --idp local" +
        (org.Host is not null && installation.UsesInternalCa ? $" --ca-file {installation.CaddyRootPath}" : "");

    /// <summary>
    /// M3: the dev profile of an organization, named as the organization. With a public host served by Caddy's internal CA it
    /// records the exported root (H7) once <c>server up</c> has written it.
    /// </summary>
    private static Profile DevProfile(Installation installation, OrgEntry org) => new()
    {
        Url = org.ClientUrl,
        Mode = AuthModes.Dev,
        ObjectId = org.AdminObjectId,
        SigningKeyEnv = DevTokenProvider.DefaultSigningKeyEnv,
        CaFile = org.Host is not null && installation.UsesInternalCa && File.Exists(installation.CaddyRootPath) ? installation.CaddyRootPath : null,
    };

    /// <summary>M3 (Entra): no profile is created; the command to create it, with the audience the server validates.</summary>
    private static IEnumerable<string> EntraLoginHint(Installation installation, OrgEntry org)
    {
        var tenant = installation.TryGetValue("AUTH_TENANT_ID") is { Length: > 0 } t ? t : "<tenant-id>";
        yield return $"  xrmd auth login --profile {org.Name} --url {org.ClientUrl} --mode device-code --client-id <client-id> --tenant-id {tenant}";
        yield return $"    (token audience {org.EffectiveAudience}; for another Application ID URI set {Installation.AudienceKey(org.Name)} in {installation.EnvPath}: " +
                     "the CLI's `auth login` then cannot sign in, see README, Multiple organizations, Known limitations)";
    }

    private static Command Org(CliContext ctx) => new("org", "Add or remove an organization of the installation in --dir (one database and xrmd-<org> service each).")
    {
        OrgAdd(ctx),
        OrgRemove(ctx),
    };

    private static Command OrgAdd(CliContext ctx)
    {
        var dir = DirOption();
        var name = new Argument<string>("name") { Description = $"The organization (database, service xrmd-<name>, profile); must match {Installation.OrgNameRule}." };
        var admin = new Option<string?>("--admin-object-id")
        {
            Description = $"Object id (GUID) of the initial System Administrator (required with Entra; dev default {DevTokenProvider.DefaultObjectId}).",
        };
        var port = new Option<int?>("--port") { Description = "Host port (default: the next free port above the highest one in use)." };
        var publicHost = new Option<string?>("--public-host")
        {
            Description = "Public host of the organization (ORG_<ORG>_HOST). Default: <name>.<XRMD_DOMAIN> when the installation has a domain, else none.",
        };
        var tdsPort = new Option<int?>("--tds-port")
        {
            Description = "Give the organization a SQL (TDS) endpoint, read-only, on this host port (ORG_<ORG>_TDS_PORT); needs the local " +
                          "identity provider (`server init --auth local`) and a public host. Behind your own proxy, <dir>/certs/tds.crt and tds.key must exist.",
        };
        var command = new Command("add", "Add an organization to the installation and start only its service (docker compose up -d xrmd-<name>).")
        {
            name, dir, admin, port, publicHost, tdsPort,
        };
        command.SetAction(async (parse, ct) =>
        {
            var composeDir = parse.GetValue(dir)!;
            var installation = Installation.Load(composeDir);
            var isDev = installation.AuthMode == Installation.DevAuthMode;
            var isLocal = installation.IsLocalAuth;
            var adminOid = parse.GetValue(admin) ?? (isLocal ? Guid.NewGuid().ToString("D") : null);
            if (!isDev && adminOid is null)
                throw new CliException(ExitCodes.Usage, $"--admin-object-id is required: the installation uses {installation.AuthMode} authentication.");

            var givenHost = parse.GetValue(publicHost)?.Trim();
            if (givenHost is not null && Installation.ValidateHost(givenHost) is { } hostProblem)
                throw new CliException(ExitCodes.Usage, $"--public-host {hostProblem}.");
            var proxyBefore = installation.Proxy == ProxyMode.Caddy;
            var org = installation.Add(parse.GetValue(name)!, parse.GetValue(port), adminOid ?? DevTokenProvider.DefaultObjectId, givenHost);
            installation.RequireProxySettings(); // a first host needs the proxy settings in .env (H2, H5)
            if (isLocal) RequireHttpsOrganizations(installation);
            // Plan 3.3: its SQL endpoint (Local mode, a host, a free port; behind your own proxy, the pair certs/tds.*).
            if (parse.GetValue(tdsPort) is { } tds)
            {
                org = installation.SetTdsPort(org.Name, tds);
                if (installation.MissingTdsPair() is { Count: > 0 } tdsMissing)
                    throw new CliException(ExitCodes.Usage, TdsPairMissingMessage(installation, tdsMissing));
            }
            var proxyChanges = org.Host is not null && installation.Proxy == ProxyMode.Caddy; // a new site in the Caddyfile (H8)
            if (proxyChanges) RequireCertificates(installation);
            await RequireSqlEulaAsync(ctx, installation, flag: false); // #16: and ACCEPT_EULA, asked before anything changes
            installation.EnsureSqlEdition(); // P5: the compose file it renders requires MSSQL_PID
            AddImageIfMissing(ctx, installation); // and XRMD_IMAGE
            // Local: the password file first, so a folder that cannot be written changes nothing.
            var createdSecrets = isLocal && !Directory.Exists(installation.SecretsPath);
            var createdTds = org.TdsPort is not null && !Directory.Exists(installation.OrgTdsPath(org.Name));
            string? adminPassword = null;
            try
            {
                adminPassword = isLocal ? WriteAdminPassword(installation, org) : null;
                installation.EnsureTdsFolders();
                installation.Save();
            }
            catch
            {
                if (isLocal) DiscardOrgSecretsQuietly(installation, org.Name);
                if (createdSecrets) RemoveEmptyDirectoryQuietly(installation.SecretsPath);
                if (createdTds) RemoveEmptyDirectoryQuietly(installation.OrgTdsPath(org.Name));
                throw;
            }
            WarnIfDeveloperEdition(ctx, installation);

            // The profile is created with the saved organization, before docker: if starting it fails, `server up` starts it
            // later and the profile is already there (`org add` changes nothing that needs a confirmation).
            string? profileName = null;
            if (isDev)
            {
                var config = ctx.Profiles.Load();
                config.Profiles[org.Name] = DevProfile(installation, org);
                config.CurrentProfile ??= org.Name;
                ctx.Profiles.Save(config);
                profileName = org.Name;
            }

            try
            {
                RequireSdk(composeDir, installation);
                // Only the new service: a bare `up -d` could recreate the organizations that are running (Review Focus 2). With a SQL
                // endpoint tds-certs too, recreated with the new organization in TDS_ORGS (it copies the pair at start).
                await ComposeAsync(ctx, composeDir,
                    org.TdsPort is null ? ["up", "-d", ServiceName(org.Name)] : ["up", "-d", TdsCertsService, ServiceName(org.Name)], ct);
            }
            catch (CliException ex)
            {
                throw new CliException(ex.ExitCode,
                    $"{ex.Message} The organization '{org.Name}' is saved in {installation.EnvPath} and {installation.ComposePath}" +
                    (profileName is null ? "" : $", with its CLI profile '{profileName}'") +
                    $"; once the problem is fixed (for a missing SDK: `xrmd sdk install --dir {composeDir}`), re-run `xrmd server up --dir {composeDir}` to start it.", ex);
            }
            // H8: the proxy learns the new site; the other organizations are not restarted.
            var proxy = proxyChanges ? await ApplyCaddyfileAsync(ctx, composeDir, proxyBefore, ct) : null;
            if (proxy?.Failure is { } proxyFailure)
                throw new CliException(proxyFailure.ExitCode,
                    $"The organization '{org.Name}' is saved and started. {proxyFailure.Message}", proxyFailure.InnerException);

            var lines = new List<string> { $"Added '{org.Name}': {org.ClientUrl} (service {ServiceName(org.Name)})." };
            if (proxy is { NotRunning: true })
                lines.Add(ProxyNotRunningNote(composeDir));
            else if (proxyChanges)
                lines.Add(proxyBefore
                    ? $"The proxy reloaded {installation.CaddyfilePath}; the other organizations kept running."
                    : $"Started the proxy (ports 80 and 443) with {installation.CaddyfilePath}: '{org.Name}' is the first organization with a public host.");
            // A host under XRMD_DOMAIN is covered by the wildcard record of init; any other host needs its own.
            if (org.Host is not null && DnsRecord(installation, org) == org.Host)
                lines.Add($"Create a DNS record for {org.Host}, pointing at " +
                          (installation.Proxy == ProxyMode.External ? "your proxy." : "this server's public address."));
            if (org.TdsPort is not null) lines.AddRange(TdsLines(installation, [org]));
            if (profileName is not null)
                lines.Add($"Profile '{org.Name}' (dev) created; use it with `--profile {org.Name}`.");
            else if (isLocal)
            {
                lines.Add($"Initial admin password for {org.Name} (shown once): {adminPassword}");
                lines.Add("Sign in to it once it is up: " + LocalLoginCommand(installation, org));
            }
            else
            {
                lines.Add("Sign in to it once it is up:");
                lines.AddRange(EntraLoginHint(installation, org));
            }
            ctx.WriteResult(isLocal
                    ? org.TdsServer is { } sqlServer
                        ? (object)new { org = org.Name, url = org.ClientUrl, service = ServiceName(org.Name), profile = profileName, adminPassword, sqlServer }
                        : new { org = org.Name, url = org.ClientUrl, service = ServiceName(org.Name), profile = profileName, adminPassword }
                    : new { org = org.Name, url = org.ClientUrl, service = ServiceName(org.Name), profile = profileName },
                string.Join(Environment.NewLine, lines));
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command OrgRemove(CliContext ctx)
    {
        var dir = DirOption();
        var name = new Argument<string>("name") { Description = "The organization to remove." };
        var drop = new Option<bool>("--drop-database")
        {
            Description = "Also drop its database (DELETE /admin/orgs/<name> through the first remaining organization's profile).",
        };
        var command = new Command("remove", "Stop and remove an organization's service (docker compose rm -s -f xrmd-<name>) and its profile; the database is kept unless --drop-database.")
        {
            name, dir, drop, ctx.YesOption,
        };
        command.SetAction(async (parse, ct) =>
        {
            var composeDir = parse.GetValue(dir)!;
            var orgName = parse.GetValue(name)!;
            var dropDatabase = parse.GetValue(drop);
            var installation = Installation.Load(composeDir);

            // Every refusal comes before anything changes.
            if (!installation.Orgs.Any(o => o.Name == orgName))
                throw new CliException(ExitCodes.NotFound,
                    $"The organization '{orgName}' is not part of the installation in {installation.Directory} " +
                    $"(organizations: {string.Join(", ", installation.Orgs.Select(o => o.Name))}).");
            if (installation.Orgs.Count == 1)
                throw new CliException(ExitCodes.Rejected,
                    $"'{orgName}' is the last organization of the installation, which needs at least one. To stop the server use " +
                    $"`xrmd server down --dir {composeDir}` (add `--volumes` to also delete the data).");
            var remaining = installation.Orgs.First(o => o.Name != orgName);
            Profile? dropProfile = null;
            if (dropDatabase)
            {
                dropProfile = ctx.Profiles.Load().Profiles.GetValueOrDefault(remaining.Name)
                    ?? throw new CliException(ExitCodes.Auth,
                        $"--drop-database calls DELETE /admin/orgs/{orgName} through the organization '{remaining.Name}', which has no profile: " +
                        $"run `xrmd auth login --profile {remaining.Name} --url {remaining.ClientUrl} …` first.");
                await ctx.ConfirmDestructiveAsync(
                    $"This removes the organization '{orgName}' and permanently drops its database '{orgName}'. Continue?");
            }

            await RequireSqlEulaAsync(ctx, installation, flag: false); // #16: the compose file it renders requires ACCEPT_EULA

            var proxyBefore = installation.Proxy == ProxyMode.Caddy;
            var hadHost = installation.Orgs.Single(o => o.Name == orgName).Host is not null;
            var hadTds = installation.Orgs.Single(o => o.Name == orgName).TdsPort is not null;
            var tdsBefore = installation.HasTds;
            installation.Remove(orgName);
            var proxyAfter = installation.Proxy == ProxyMode.Caddy;
            installation.CheckWritable(); // a hand-edited Caddyfile is refused before any container is touched

            await ComposeAsync(ctx, composeDir, ["rm", "-s", "-f", ServiceName(orgName)], ct);
            // Review Focus 4: the last host gone, the proxy goes too (while the compose file still has it); its Caddyfile is deleted by Save.
            if (proxyBefore && !proxyAfter) await ComposeAsync(ctx, composeDir, ["rm", "-s", "-f", "proxy"], ct);
            // Plan 3.3: the last SQL endpoint gone, tds-certs goes too (while the compose file still has it).
            if (tdsBefore && !installation.HasTds) await ComposeAsync(ctx, composeDir, ["rm", "-s", "-f", TdsCertsService], ct);
            installation.EnsureSqlEdition(); // P5: the compose file it renders requires MSSQL_PID
            AddImageIfMissing(ctx, installation); // and XRMD_IMAGE
            installation.Save();
            DeleteOrgSecretsQuietly(installation, orgName); // the removed organization's one-shot password folder, if it is still there
            DeleteOrgFolderQuietly(installation.OrgTdsPath(orgName)); // and its SQL endpoint certificates
            WarnIfDeveloperEdition(ctx, installation);
            // tds-certs is recreated without the removed organization in TDS_ORGS; a failure leaves it copying for nobody, harmlessly.
            if (hadTds && installation.HasTds)
            {
                try
                {
                    await ComposeAsync(ctx, composeDir, ["up", "-d", "--no-deps", TdsCertsService], ct);
                }
                catch (CliException ex)
                {
                    await ctx.Env.Err.WriteLineAsync($"{TdsCertsService} was not recreated: {ex.Message} `xrmd server up --dir {composeDir}` recreates it.");
                }
            }
            // H8: one of several hosts gone, the proxy only reloads; the other organizations are not restarted.
            var proxy = proxyAfter && hadHost ? await ApplyCaddyfileAsync(ctx, composeDir, proxyWasThere: true, ct) : null;
            var proxyFailure = proxy?.Failure;

            // M3: the profile goes too; a current profile on the removed org moves to the first remaining org (Review Focus 5).
            // The installation is already saved here: a failure is reported as such, never as "nothing happened".
            string? currentProfile = null;
            CliException? profileFailure = null;
            try
            {
                var config = ctx.Profiles.Load();
                config.Profiles.Remove(orgName);
                if (config.CurrentProfile == orgName)
                    config.CurrentProfile = config.Profiles.ContainsKey(remaining.Name) ? remaining.Name : null;
                ctx.Profiles.Save(config);
                currentProfile = config.CurrentProfile;
            }
            catch (Exception ex) when (ex is CliException or IOException or UnauthorizedAccessException)
            {
                profileFailure = new CliException(ex is CliException cli ? cli.ExitCode : ExitCodes.Unexpected,
                    $"The organization '{orgName}' was removed from the installation but the profile cleanup failed: {ex.Message} " +
                    $"Delete the profile with `xrmd auth logout --profile {orgName}`, and check the current profile.", ex);
            }

            if (dropProfile is not null)
            {
                try
                {
                    await ctx.CreateClient(dropProfile).AdminAsync(HttpMethod.Delete, $"orgs/{Uri.EscapeDataString(orgName)}", cancellationToken: ct);
                }
                catch (CliException ex)
                {
                    throw new CliException(ex.ExitCode,
                        $"{ex.Message} The organization '{orgName}' was removed from the installation, but its database was not dropped: " +
                        $"run `xrmd org delete {orgName} --profile {remaining.Name}`." +
                        (profileFailure is null ? "" : " Also: " + profileFailure.Message) +
                        (proxyFailure is null ? "" : " Also: " + proxyFailure.Message), ex);
                }
            }

            if (profileFailure is not null)
                throw dropProfile is null ? profileFailure : new CliException(profileFailure.ExitCode,
                    profileFailure.Message + " The database was dropped." + (proxyFailure is null ? "" : " Also: " + proxyFailure.Message),
                    profileFailure.InnerException);
            if (proxyFailure is not null)
                throw new CliException(proxyFailure.ExitCode,
                    $"The organization '{orgName}' was removed{(dropProfile is null ? "" : " and its database dropped")}. {proxyFailure.Message}",
                    proxyFailure.InnerException);

            var proxyNote = proxyBefore && !proxyAfter
                ? " The proxy and its Caddyfile are removed: no organization has a public host any more."
                : proxy is { NotRunning: true } ? " " + ProxyNotRunningNote(composeDir)
                : proxyAfter && hadHost ? " The proxy reloaded its Caddyfile." : "";
            ctx.WriteResult(
                new { removed = orgName, databaseDropped = dropProfile is not null, currentProfile },
                (dropProfile is not null
                    ? $"Removed '{orgName}' and dropped its database. Current profile: {currentProfile ?? "(none)"}."
                    : $"Removed '{orgName}'; its database is kept. Current profile: {currentProfile ?? "(none)"}.") + proxyNote);
            return ExitCodes.Ok;
        });
        return command;
    }

    private static string ServiceName(string org) => "xrmd-" + org;

    /// <summary>
    /// <c>up</c>/<c>upgrade</c> of a multi-organization installation: re-renders the compose file and the <c>Caddyfile</c> from
    /// <c>.env</c> (the caller has run the certificate check of Review Focus 6). A <c>.env</c> without <c>MSSQL_PID</c> gets
    /// <c>MSSQL_PID=Developer</c> first, the edition it ran (P5), and is saved. True when the <c>Caddyfile</c> changed.
    /// </summary>
    private static bool Render(CliContext ctx, Installation installation, bool eulaWritten)
    {
        var edition = installation.EnsureSqlEdition();
        var image = AddImageIfMissing(ctx, installation);
        installation.EnsureTdsFolders(); // a SQL endpoint added by hand in .env gets its certificate folder
        var caddyfileChanged = edition || image || eulaWritten ? installation.Save() : installation.WriteRenderedFiles();
        WarnIfDeveloperEdition(ctx, installation);
        return caddyfileChanged;
    }

    /// <summary>
    /// After <c>up -d</c>: a running proxy keeps the sites it loaded, so a <c>Caddyfile</c> that this render changed (a
    /// hand-edited <c>.env</c>) is applied by restarting the proxy alone. A restart rather than <c>caddy reload</c>: the proxy
    /// may have been created a moment ago, before its admin endpoint answers.
    /// </summary>
    private static async Task RestartProxyIfChangedAsync(CliContext ctx, string composeDir, Installation? installation, bool caddyfileChanged, CancellationToken ct)
    {
        if (installation?.Proxy == ProxyMode.Caddy && caddyfileChanged) await ComposeAsync(ctx, composeDir, ["restart", "proxy"], ct);
    }

    /// <summary>
    /// A <c>.env</c> without <c>XRMD_IMAGE</c> predates the pinned image: writes the CLI's default (the caller saves) and says so on
    /// stderr, since the containers it starts may differ from the ones it ran. True when it wrote.
    /// </summary>
    private static bool AddImageIfMissing(CliContext ctx, Installation installation)
    {
        var defaultImage = ServerImage.Default(ctx.Env.CliVersion);
        if (!installation.EnsureImage(defaultImage)) return false;
        ctx.Env.Err.WriteLine($"Added {Installation.ImageKey}={defaultImage} to {installation.EnvPath}: the compose file now names its image there. " +
                              $"Edit it to run another image; `xrmd server upgrade --to <version>` moves the tag.");
        return true;
    }

    /// <summary>P5: on stderr, every time a command renders an installation whose bundled SQL Server is the Developer edition.</summary>
    private static void WarnIfDeveloperEdition(CliContext ctx, Installation installation)
    {
        if (!installation.ExternalSql && string.Equals(installation.SqlEdition, Installation.PreviousDefaultSqlEdition, StringComparison.OrdinalIgnoreCase))
            ctx.Env.Err.WriteLine(Installation.DeveloperEditionWarning);
    }

    /// <summary>
    /// <c>up -d</c>, plus <c>--remove-orphans</c> for a multi-organization installation: its compose file is rendered by xrmd,
    /// so a container of a service it no longer has (the legacy <c>xrmd</c> service after a re-create, or an organization
    /// removed by hand from <c>.env</c>) is never wanted and would keep serving its database. A legacy directory is left as it was (M2).
    /// </summary>
    private static string[] UpArguments(Installation? installation) =>
        installation is null ? ["up", "-d"] : ["up", "-d", "--remove-orphans"];

    /// <summary>
    /// Ruling B7: no container starts without the SDK (the host exits 3 without it), for <c>up</c>, <c>upgrade</c> and <c>org add</c>.
    /// A legacy single-service directory (<paramref name="installation"/> null) is refused even with an SDK folder: its compose
    /// file never mounts it. Without compose file and .env there is nothing to start, and <c>ComposeAsync</c> reports that first (usage error).
    /// </summary>
    private static void RequireSdk(string composeDir, Installation? installation)
    {
        var fullDir = Path.GetFullPath(composeDir);
        if (!File.Exists(Path.Combine(fullDir, ComposeFileName)) || !File.Exists(Path.Combine(fullDir, EnvFileName))) return;
        // An SDK-free server (XRMD_SDK_MODE=none) never loads the SDK; an absent key is standard.
        if (installation is not null ? installation.IsSdkFree
            : Installation.ReadRawValues(Path.Combine(fullDir, EnvFileName)).TryGetValue(Installation.SdkModeKey, out var rawMode) && Installation.IsSdkFreeValue(rawMode)) return;
        var sdkFolder = Path.Combine(fullDir, "sdk");
        var installed = SdkFolder.TryRead(sdkFolder) is not null;
        if (installation is not null && installed) return;
        var message = $"{SdkPackage.MissingMessage(sdkFolder)} For this installation run: xrmd sdk install --dir {fullDir}";
        if (installation is null)
            message = SdkPackage.MissingMessage(sdkFolder) +
                      $" This installation uses the old single-service layout, which does not mount the SDK folder. Run 'xrmd server init --force --dir {fullDir}' to migrate it, then 'xrmd sdk install --dir {fullDir}'.";
        throw new CliException(ExitCodes.Unexpected, message);
    }

    [GeneratedRegex(@"^[^\s'""#$]+\z")]
    private static partial Regex ImagePattern();

    private static Command Up(CliContext ctx)
    {
        var dir = DirOption();
        var acceptSqlEula = SqlEulaOption(SqlEulaOptionText);
        var command = new Command("up",
            "Start the server (docker compose up -d). With TLS_MODE=internal it then exports the root certificate of Caddy's " +
            "internal CA to <dir>/certs/caddy-root.crt and records it in the dev profiles of the organizations with a public host, " +
            "which trust it for that host only.") { dir, acceptSqlEula };
        command.SetAction(async (parse, ct) =>
        {
            var composeDir = parse.GetValue(dir)!;
            var installation = Installation.LoadIfMultiOrg(composeDir);
            if (installation is not null) RequireCertificates(installation); // Review Focus 6: before anything is written
            RequireSdk(composeDir, installation); // ruling B7
            var eulaWritten = installation is not null && await RequireSqlEulaAsync(ctx, installation, parse.GetValue(acceptSqlEula)); // #16
            var caddyfileChanged = installation is not null && Render(ctx, installation, eulaWritten); // what runs is what .env says
            await ComposeAsync(ctx, composeDir, UpArguments(installation), ct);
            await RestartProxyIfChangedAsync(ctx, composeDir, installation, caddyfileChanged, ct);
            if (installation is not { UsesInternalCa: true })
            {
                ctx.WriteResult(new { started = true }, "Server started.");
                return ExitCodes.Ok;
            }

            // H7: the CLI trusts Caddy's local CA for the installation's hosts, through the profiles only.
            await ExportCaddyRootAsync(ctx, composeDir, installation, ct);
            var trusting = SetCaFile(ctx, installation);
            ctx.WriteResult(new { started = true, caddyRoot = installation.CaddyRootPath, profiles = trusting },
                $"Server started. Exported the root certificate of Caddy's internal CA to {installation.CaddyRootPath}" +
                (trusting.Count == 0 ? "." : $"; the profiles {string.Join(", ", trusting)} trust it for their host only.") +
                " Browsers and other clients do not: see the installation guide to trust it on this machine.");
            return ExitCodes.Ok;
        });
        return command;
    }

    /// <summary>Where Caddy keeps the root of its local CA, in the <c>caddy-data</c> volume of the <c>proxy</c> service.</summary>
    internal const string CaddyRootInContainer = "/data/caddy/pki/authorities/local/root.crt";

    private static readonly TimeSpan CaddyRootTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CaddyRootRetry = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// H7: <c>docker compose cp proxy:&lt;root.crt&gt; &lt;dir&gt;/certs/caddy-root.crt</c>, retried for up to 10 s (Caddy creates its
    /// CA on first start), then checked to be a PEM certificate. Exit 1 when it never appears, naming what to run.
    /// </summary>
    private static async Task ExportCaddyRootAsync(CliContext ctx, string composeDir, Installation installation, CancellationToken ct)
    {
        Directory.CreateDirectory(installation.CertsPath);
        var target = installation.CaddyRootPath;
        string[] copy = ["cp", "proxy:" + CaddyRootInContainer, target];
        var timeout = ctx.Env.CaddyRootTimeout ?? CaddyRootTimeout;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            string problem;
            try
            {
                await ComposeAsync(ctx, composeDir, copy, ct);
                using var root = Http.ScopedTrust.LoadRoot(target);
                return;
            }
            catch (CliException ex)
            {
                problem = ex.Message;
            }
            catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException)
            {
                problem = $"{target} is not a PEM certificate: {ex.Message}";
            }
            var left = timeout - watch.Elapsed;
            if (left <= TimeSpan.Zero)
                throw new CliException(ExitCodes.Unexpected,
                    $"The server started, but Caddy's root certificate could not be exported to {target} within " +
                    $"{timeout.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}s: {problem} " +
                    $"Check the proxy with `xrmd server logs --dir {composeDir}`, then run `xrmd server up --dir {composeDir}` again " +
                    $"(or copy it with `docker {string.Join(' ', ComposeArguments(composeDir, copy))}`).");
            await Task.Delay(left < CaddyRootRetry ? left : CaddyRootRetry, ct);
        }
    }

    /// <summary>
    /// H7: records the exported root as <see cref="Profile.CaFile"/> of the installation's dev and local-IdP profiles whose URL is their
    /// organization's public URL; returns their names. Other profiles and other hosts are not touched.
    /// </summary>
    private static List<string> SetCaFile(CliContext ctx, Installation installation)
    {
        var config = ctx.Profiles.Load();
        var names = new List<string>();
        foreach (var org in installation.Orgs.Where(o => o.Host is not null))
        {
            if (!config.Profiles.TryGetValue(org.Name, out var profile) || (profile.Mode != AuthModes.Dev && !profile.IsLocalIdp) || profile.Url != org.PublicUrl) continue;
            names.Add(org.Name);
            if (profile.CaFile != installation.CaddyRootPath) config.Profiles[org.Name] = profile with { CaFile = installation.CaddyRootPath };
        }
        if (names.Count > 0) ctx.Profiles.Save(config);
        return names;
    }

    private static Command Down(CliContext ctx)
    {
        var dir = DirOption();
        var volumes = new Option<bool>("--volumes") { Description = "Also delete the data volumes (the database is lost)." };
        var command = new Command("down", "Stop and remove the server containers.") { dir, volumes, ctx.YesOption };
        command.SetAction(async (parse, ct) =>
        {
            var withVolumes = parse.GetValue(volumes);
            if (withVolumes)
                await ctx.ConfirmDestructiveAsync("This deletes the server's data volumes, including the database and backups. Continue?");
            await ComposeAsync(ctx, parse.GetValue(dir)!, withVolumes ? ["down", "--volumes"] : ["down"], ct);
            ctx.WriteResult(new { stopped = true, volumesRemoved = withVolumes }, withVolumes ? "Server stopped; volumes removed." : "Server stopped.");
            return ExitCodes.Ok;
        });
        return command;
    }

    /// <summary>One organization of the installation in <c>--dir</c>, with its CLI profile (null when there is none).</summary>
    private sealed record OrgTarget(OrgEntry Org, Profile? Profile)
    {
        public string Url => Profile?.Url ?? Org.ClientUrl;
    }

    /// <summary>
    /// The organizations to query: those of the multi-org installation in <paramref name="dir"/>, each with the profile
    /// named as it; null without such an installation, or when <c>--profile</c>/<c>--url</c> names one server explicitly.
    /// </summary>
    private static List<OrgTarget>? OrgTargets(CliContext ctx, string dir, bool explicitTargetWins = true)
    {
        if (explicitTargetWins && (ctx.ProfileName is not null || ctx.Url is not null)) return null;
        var installation = Installation.LoadIfMultiOrg(dir);
        if (installation is null) return null;
        var profiles = ctx.Profiles.Load().Profiles;
        return installation.Orgs.Select(o => new OrgTarget(o, profiles.GetValueOrDefault(o.Name))).ToList();
    }

    private static string NoProfileMessage(OrgEntry org, bool dev, bool local = false) =>
        $"no CLI profile '{org.Name}'; run xrmd auth login --profile {org.Name} --url {org.ClientUrl} " +
        (dev ? $"--mode dev --object-id {org.AdminObjectId}" : local ? "--mode device-code --idp local" : "--mode device-code --client-id <client-id> --tenant-id <tenant-id>");

    /// <summary>The probe time limit of this environment (<see cref="OrgProbeTimeout"/> unless a test replaces it).</summary>
    private static TimeSpan ProbeTimeout(CliContext ctx) => ctx.Env.OrgProbeTimeout ?? OrgProbeTimeout;

    /// <summary>
    /// Runs one organization's probe for at most <paramref name="timeout"/>. A probe cut by that limit (and not by the
    /// command's own cancellation) becomes exit 6 "timed out after Ns".
    /// </summary>
    private static async Task<T> WithProbeTimeout<T>(TimeSpan timeout, CancellationToken ct, Func<CancellationToken, Task<T>> probe)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            return await probe(limit.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new CliException(ExitCodes.Unreachable,
                $"timed out after {timeout.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}s", ex);
        }
    }

    /// <summary>GET /admin/health of one organization, bounded by <paramref name="timeout"/>: the health (also from a 503 body), or the error and its exit code.</summary>
    private static async Task<(JsonElement? Health, string? Error, int ExitCode)> OrgHealthAsync(
        CliContext ctx, OrgTarget target, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var response = await WithProbeTimeout(timeout, ct,
                token => ctx.CreateClient(target.Profile!).AdminAsync(HttpMethod.Get, "health", cancellationToken: token));
            return response.Json is { ValueKind: JsonValueKind.Object } json
                ? (json, null, ExitCodes.Ok)
                : (null, "The server returned no JSON for GET /admin/health.", ExitCodes.Unexpected);
        }
        catch (CliException ex)
        {
            // An unhealthy server answers 503 with its health report.
            if (ex.HttpStatus == 503 && TryParseObject(ex.ResponseBody) is { } body) return (body, null, ExitCodes.Ok);
            return (null, ex.Message, ex.ExitCode);
        }
    }

    private static JsonElement? TryParseObject(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string HealthStatus(JsonElement health) =>
        health.TryGetProperty("status", out var s) && s.GetString() is { } status ? status : "?";

    private static Command Status(CliContext ctx)
    {
        var dir = DirOption();
        var command = new Command("status",
            "Show the server's health (GET /admin/health). When --dir holds a multi-organization installation (and neither --profile " +
            "nor --url is given): every organization through the profile named as it, one row per organization (a JSON array of " +
            "{org, url, publicUrl, health|error}; the table has a publicUrl column when an organization has a public host); each " +
            "probe gives up after 15 s; exit 1 unless all are Healthy. Otherwise: the current " +
            "profile's server (a JSON object).") { dir };
        command.SetAction(async (parse, ct) =>
        {
            var composeDir = parse.GetValue(dir)!;
            if (OrgTargets(ctx, composeDir) is not { } targets)
            {
                var response = await ctx.CreateClient().AdminAsync(HttpMethod.Get, "health", cancellationToken: ct);
                ctx.Write(response.Json ?? throw new CliException(ExitCodes.Unexpected, "The server returned no JSON for GET /admin/health."));
                return ExitCodes.Ok;
            }

            var installation = Installation.LoadIfMultiOrg(composeDir)!;
            var dev = installation.AuthMode == Installation.DevAuthMode;
            var hosts = installation.Orgs.Any(o => o.Host is not null);
            var rows = new List<(OrgTarget Target, JsonElement? Health, string? Error)>();
            foreach (var target in targets)
            {
                if (target.Profile is null)
                {
                    rows.Add((target, null, NoProfileMessage(target.Org, dev, installation.IsLocalAuth)));
                    continue;
                }
                var (health, error, _) = await OrgHealthAsync(ctx, target, ProbeTimeout(ctx), ct);
                rows.Add((target, health, error));
            }

            // H9: each organization's public URL (null without a host); the table has the column only when some organization has a host.
            // Plan 3.3: with a SQL endpoint in the installation, each organization's TDS port and its certificate's expiry too.
            var json = ctx.Format == OutputFormat.Json;
            ctx.Write(rows.Select(r =>
            {
                var row = OrgRow(r.Target, json || hosts);
                if (json) row["sdkMode"] = installation.IsSdkFree ? "none" : "standard";
                if (installation.HasTds)
                {
                    row["tdsPort"] = r.Target.Org.TdsPort;
                    if (json) row["tdsCertificateExpires"] = TdsCertificateExpiry(installation, r.Target.Org) is { } expiry ? Day(expiry) : null;
                    else row["tdsCertificate"] = TdsCertificateCell(installation, r.Target.Org);
                }
                if (!json) row["status"] = r.Health is { } h ? HealthStatus(h) : "error: " + r.Error;
                else if (r.Health is { } health) row["health"] = health;
                else row["error"] = r.Error;
                return row;
            }).ToList());

            if (!json && installation.IsSdkFree) await ctx.Env.Out.WriteLineAsync("SDK: not used (SDK-free server)");
            var failing = rows.Where(r => r.Health is not { } h || HealthStatus(h) != "Healthy").Select(r => r.Target.Org.Name).ToList();
            if (failing.Count == 0) return ExitCodes.Ok;
            await ctx.Env.Err.WriteLineAsync($"Not healthy: {string.Join(", ", failing)}.");
            return ExitCodes.Unexpected;
        });
        return command;
    }

    private static Command Doctor(CliContext ctx)
    {
        var dir = DirOption();
        var command = new Command("doctor",
            "Run the server's self-checks (GET /admin/doctor). When --dir holds a multi-organization installation (and neither " +
            "--profile nor --url is given): every organization through the profile named as it, one row per organization (a JSON array " +
            "of {org, url, publicUrl, checks, error}); each probe gives up after 15 s. An organization with a public host also gets " +
            "client-side checks: dns (the host resolves on this machine) and certificate (what is served on <host>:443: expired or " +
            "not covering the host = error, under 14 days = warning). Otherwise: the current profile's server (a JSON array of " +
            "checks; the multi-organization rows replace it, with each organization's checks as an object property). " +
            "Exit 1 if any check is an error or an organization cannot be checked.") { dir };
        command.SetAction(async (parse, ct) =>
        {
            var composeDir = parse.GetValue(dir)!;
            if (OrgTargets(ctx, composeDir) is not { } targets)
            {
                var response = await ctx.CreateClient().AdminAsync(HttpMethod.Get, "doctor", cancellationToken: ct);
                var checks = Checks(response.Json);
                ctx.Write(checks);
                var failed = FailedChecks(checks, "error");
                if (failed.Count == 0) return ExitCodes.Ok;
                await ctx.Env.Err.WriteLineAsync($"Doctor found errors: {string.Join(", ", failed)}.");
                return ExitCodes.Unexpected;
            }

            var installation = Installation.LoadIfMultiOrg(composeDir)!;
            var dev = installation.AuthMode == Installation.DevAuthMode;
            var hosts = installation.Orgs.Any(o => o.Host is not null);
            var rows = new List<(OrgTarget Target, JsonElement? Checks, string? Error)>();
            foreach (var target in targets)
            {
                JsonElement? serverChecks = null;
                string? error = null;
                if (target.Profile is null)
                    error = NoProfileMessage(target.Org, dev, installation.IsLocalAuth);
                else
                {
                    try
                    {
                        var response = await WithProbeTimeout(ProbeTimeout(ctx), ct,
                            token => ctx.CreateClient(target.Profile).AdminAsync(HttpMethod.Get, "doctor", cancellationToken: token));
                        serverChecks = Checks(response.Json);
                    }
                    catch (CliException ex)
                    {
                        error = ex.Message;
                    }
                }
                // H9: what this machine sees of the organization's public host, whether or not the server answered; plan 3.3:
                // the certificate of its SQL endpoint.
                var hostChecks = await HostChecksAsync(ctx, installation, target.Org, ct);
                hostChecks.AddRange(TdsChecks(ctx, installation, target.Org));
                rows.Add((target, CombineChecks(serverChecks, hostChecks), error));
            }

            var json = ctx.Format == OutputFormat.Json;
            ctx.Write(rows.Select(r =>
            {
                var row = OrgRow(r.Target, json || hosts);
                if (installation.HasTds) row["tdsPort"] = r.Target.Org.TdsPort; // plan 3.3; the certificate is the tds-certificate check
                if (!json) row["status"] = DoctorSummary(r.Checks, r.Error);
                else
                {
                    if (r.Checks is { } c) row["checks"] = c;
                    if (r.Error is { } e) row["error"] = e;
                }
                return row;
            }).ToList());

            var failing = rows
                .Select(r => (r.Target.Org.Name, Problems: (r.Checks is { } c ? FailedChecks(c, "error") : []).Concat(r.Error is { } e ? [e] : []).ToList()))
                .Where(r => r.Problems.Count > 0)
                .Select(r => $"{r.Name} ({string.Join(", ", r.Problems)})")
                .ToList();
            if (failing.Count == 0) return ExitCodes.Ok;
            await ctx.Env.Err.WriteLineAsync($"Doctor found errors in: {string.Join("; ", failing)}.");
            return ExitCodes.Unexpected;
        });
        return command;
    }

    /// <summary>The first columns of a <c>status</c> or <c>doctor</c> row: <c>org</c>, <c>url</c> and, when asked, <c>publicUrl</c> (H9).</summary>
    private static Dictionary<string, object?> OrgRow(OrgTarget target, bool publicUrl)
    {
        var row = new Dictionary<string, object?> { ["org"] = target.Org.Name, ["url"] = target.Url };
        if (publicUrl) row["publicUrl"] = target.Org.PublicUrl;
        return row;
    }

    private static JsonElement Checks(JsonElement? json)
    {
        var body = json ?? throw new CliException(ExitCodes.Unexpected, "The server returned no JSON for GET /admin/doctor.");
        return body.ValueKind == JsonValueKind.Object && body.TryGetProperty("checks", out var c) && c.ValueKind == JsonValueKind.Array
            ? c
            : throw new CliException(ExitCodes.Unexpected, "GET /admin/doctor did not return a checks array.");
    }

    private static List<string> FailedChecks(JsonElement checks, string status) =>
        checks.EnumerateArray()
            .Where(x => x.TryGetProperty("status", out var s) && s.GetString() == status)
            .Select(x => x.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?")
            .ToList();

    /// <summary>The table status of one org: <c>ok</c>, <c>warning: …</c> or <c>error: …</c> (why it could not be checked, and the failing checks).</summary>
    private static string DoctorSummary(JsonElement? checks, string? error)
    {
        if (error is not null)
            return "error: " + error + (checks is { } failed && FailedChecks(failed, "error") is { Count: > 0 } names ? "; " + string.Join(", ", names) : "");
        if (checks is not { } c) return "ok";
        if (FailedChecks(c, "error") is { Count: > 0 } errors) return "error: " + string.Join(", ", errors);
        if (FailedChecks(c, "warning") is { Count: > 0 } warnings) return "warning: " + string.Join(", ", warnings);
        return "ok";
    }

    private static Command Upgrade(CliContext ctx)
    {
        var dir = DirOption();
        var to = new Option<string?>("--to")
        {
            Description = $"Move the server image to this version (SemVer, e.g. 1.5.0) before pulling: rewrites the tag of XRMD_IMAGE in .env " +
                          $"({ServerImage.Repository} only; needs the installation written by `xrmd server init`). `latest` asks GitHub " +
                          $"for the newest release of {ServerReleases.ReleasesRepository} (pre-releases included) and writes its exact version.",
        };
        var latest = new Option<bool>("--latest") { Description = "Same as --to latest." };
        var allowCliMismatch = new Option<bool>("--allow-cli-mismatch")
        {
            Description = "With --to latest: go on when the newest release is newer than this CLI (by default it stops and asks to install that CLI first).",
        };
        var acceptSqlEula = SqlEulaOption(SqlEulaOptionText);
        var command = new Command("upgrade",
            "Pull the newest images, restart the server and wait until it is healthy. With a multi-organization installation in " +
            "--dir every organization restarts, so it waits for every organization (through the profile named as it) and ignores " +
            "--profile and --url; otherwise it waits for the current profile's server.") { dir, to, latest, allowCliMismatch, acceptSqlEula };
        command.SetAction(async (parse, ct) =>
        {
            var composeDir = parse.GetValue(dir)!;
            var version = parse.GetValue(to)?.Trim();
            if (parse.GetValue(latest))
            {
                if (version is not null && !IsLatest(version))
                    throw new CliException(ExitCodes.Usage, "--latest and --to <version> exclude each other.");
                version = "latest";
            }
            var toLatest = version is not null && IsLatest(version);
            if (parse.GetValue(allowCliMismatch) && !toLatest)
                throw new CliException(ExitCodes.Usage, "--allow-cli-mismatch applies only with --to latest.");
            var installation = Installation.LoadIfMultiOrg(composeDir);
            if (version is not null)
            {
                // All of it before anything is written, pulled or restarted.
                if (installation is null)
                    throw new CliException(ExitCodes.Rejected,
                        $"--to needs an installation written by `xrmd server init`, which {composeDir} does not hold (a legacy single-service layout or no installation).");
                var current = installation.Image.Length > 0 ? installation.Image : ServerImage.Default(ctx.Env.CliVersion);
                if (toLatest)
                {
                    ServerImage.RequireMovable(current); // before asking GitHub
                    version = await ResolveLatestAsync(ctx, current, parse.GetValue(allowCliMismatch), ct);
                }
                ServerImage.WithTag(current, version);
            }
            if (installation is not null) RequireCertificates(installation); // Review Focus 6: before .env is written
            RequireSdk(composeDir, installation); // ruling B7: before anything is pulled or restarted
            var eulaWritten = installation is not null && await RequireSqlEulaAsync(ctx, installation, parse.GetValue(acceptSqlEula)); // #16
            // Every organization restarts, so every organization is waited for, whatever --profile says.
            var targets = installation is null ? null : OrgTargets(ctx, composeDir, explicitTargetWins: false)!;
            var client = targets is null ? ctx.CreateClient() : null; // fail on a missing profile before touching containers
            var caddyfileChanged = false;
            if (installation is not null)
            {
                if (version is not null)
                {
                    var had = installation.Image.Length > 0;
                    installation.EnsureImage(ServerImage.Default(ctx.Env.CliVersion));
                    installation.SetImage(ServerImage.WithTag(installation.Image, version));
                    if (!had) // the only notice: the key was missing and is written with the requested tag
                        ctx.Env.Err.WriteLine($"Added {Installation.ImageKey}={installation.Image} to {installation.EnvPath}.");
                    caddyfileChanged = installation.Save(); // the tag is in .env before anything is pulled
                }
                caddyfileChanged |= Render(ctx, installation, eulaWritten); // what runs is what .env says
            }
            await ComposeAsync(ctx, composeDir, ["pull"], ct);
            await ComposeAsync(ctx, composeDir, UpArguments(installation), ct);
            await RestartProxyIfChangedAsync(ctx, composeDir, installation, caddyfileChanged, ct);

            var clock = ctx.Env.Clock;
            var deadline = clock.GetUtcNow() + UpgradeTimeout;
            if (targets is null)
            {
                while (true)
                {
                    try
                    {
                        var response = await client!.AdminAsync(HttpMethod.Get, "health", cancellationToken: ct);
                        if (response.Json is { ValueKind: JsonValueKind.Object } body && HealthStatus(body) == "Healthy")
                        {
                            ctx.WriteResult(new { upgraded = true }, "Server upgraded and healthy.");
                            return ExitCodes.Ok;
                        }
                    }
                    catch (CliException ex) when (ex.ExitCode is ExitCodes.Unexpected or ExitCodes.Unreachable)
                    {
                        // 503 (unhealthy) or not up yet: keep waiting.
                    }
                    if (clock.GetUtcNow() >= deadline)
                        throw new CliException(ExitCodes.Unexpected, $"The server did not become healthy within {UpgradeTimeout.TotalMinutes:0} minutes; see `xrmd server logs`.");
                    await Task.Delay(PollInterval, clock, ct);
                }
            }

            var dev = installation!.AuthMode == Installation.DevAuthMode;
            var pending = targets.Where(t => t.Profile is not null).ToList();
            var healthy = new List<string>();
            var lastState = new Dictionary<string, string>();
            while (pending.Count > 0)
            {
                foreach (var target in pending.ToList())
                {
                    // Each probe is bounded, and never runs past the deadline.
                    var remaining = deadline - clock.GetUtcNow();
                    var timeout = remaining < ProbeTimeout(ctx) ? (remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1)) : ProbeTimeout(ctx);
                    var (health, error, exitCode) = await OrgHealthAsync(ctx, target, timeout, ct);
                    if (exitCode == ExitCodes.Auth)
                        // A rejected token does not heal by waiting.
                        throw new CliException(ExitCodes.Auth,
                            $"The organization '{target.Org.Name}' refused the token of its profile ({target.Profile!.Url}): {error} " +
                            $"Check it with `xrmd auth status --profile {target.Org.Name}`; the server was upgraded.");
                    if (health is { } h && HealthStatus(h) == "Healthy")
                    {
                        pending.Remove(target);
                        healthy.Add(target.Org.Name);
                        continue;
                    }
                    // A health answer replaces the last state; an error (often a probe cut short near the deadline) is added
                    // to the last known health instead of hiding it.
                    lastState[target.Org.Name] = health is { } unhealthy
                        ? $"status {HealthStatus(unhealthy)}"
                        : lastState.TryGetValue(target.Org.Name, out var known) && known.StartsWith("status ", StringComparison.Ordinal)
                            ? $"{known}, then {error ?? "?"}"
                            : error ?? "?";
                }
                if (pending.Count == 0) break;
                if (clock.GetUtcNow() >= deadline)
                    throw new CliException(ExitCodes.Unexpected,
                        $"These organizations did not become healthy within {UpgradeTimeout.TotalMinutes:0} minutes: " +
                        $"{string.Join("; ", pending.Select(t => $"{t.Org.Name} (last: {lastState.GetValueOrDefault(t.Org.Name, "?")})"))}; see `xrmd server logs`.");
                await Task.Delay(PollInterval, clock, ct);
            }

            var withoutProfile = targets.Where(t => t.Profile is null).ToList();
            if (withoutProfile.Count > 0)
                throw new CliException(ExitCodes.Unexpected,
                    $"Upgraded; healthy: {(healthy.Count == 0 ? "(none checked)" : string.Join(", ", healthy))}. Not checked, because they have no CLI profile: " +
                    string.Join("; ", withoutProfile.Select(t => NoProfileMessage(t.Org, dev))) + ".");
            ctx.WriteResult(new { upgraded = true, healthy }, $"Server upgraded; every organization is healthy ({string.Join(", ", healthy)}).");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static bool IsLatest(string version) => string.Equals(version, "latest", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The exact version <c>--to latest</c> means: the newest release, refused (exit 5) when it is newer than this CLI unless
    /// <paramref name="allowCliMismatch"/> (the CLI writes the generated files, so it must know the server it runs).
    /// </summary>
    private static async Task<string> ResolveLatestAsync(CliContext ctx, string currentImage, bool allowCliMismatch, CancellationToken ct)
    {
        var release = await ServerReleases.LatestAsync(ctx.Http, ct);
        var cli = ctx.Env.CliVersion;
        var cliKnown = cli is not null && ServerImage.IsSemVer(cli) && ServerImage.Default(cli) != ServerImage.LocalImage;
        if (!allowCliMismatch && (!cliKnown || ServerImage.CompareSemVer(cli!, release.Version) < 0))
            throw new CliException(ExitCodes.Rejected,
                $"The newest release is {release.Version}, but this CLI is {(cliKnown ? cli : "a development build")}: install the CLI " +
                $"{release.Version} first (https://xrmverse.com/docs/install.html), then run the upgrade again, so that the generated " +
                "files match the server. --allow-cli-mismatch goes on anyway. Nothing was changed.");
        var image = ServerImage.WithTag(currentImage, release.Version);
        ctx.Env.Err.WriteLine(image == currentImage
            ? $"The newest release is {release.Version}, the version already in XRMD_IMAGE: pulling and restarting it."
            : $"The newest release is {release.Version}: upgrading XRMD_IMAGE to {image}.");
        return release.Version;
    }

    private static Command Versions(CliContext ctx)
    {
        var dir = DirOption();
        var command = new Command("versions",
            $"List the published server releases ({ServerReleases.ReleasesPage}, pre-releases included), newest first, marking the " +
            "version in XRMD_IMAGE of the installation in --dir (installed) and the version of this CLI (cli). Asks GitHub; offline it exits 6.") { dir };
        command.SetAction(async (parse, ct) =>
        {
            var composeDir = parse.GetValue(dir)!;
            var installed = Installation.LoadIfMultiOrg(composeDir) is { } installation ? ServerImage.TagOf(installation.Image) : null;
            var cli = ctx.Env.CliVersion;
            var releases = await ServerReleases.ListAsync(ctx.Http, ct);
            ctx.Write(releases.Select((r, i) => new
            {
                version = r.Version,
                published = r.PublishedAt?.UtcDateTime.ToString("yyyy-MM-dd"),
                prerelease = r.Prerelease,
                latest = i == 0,
                installed = r.Version == installed,
                cli = r.Version == cli,
            }).ToList());
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Logs(CliContext ctx)
    {
        var dir = DirOption();
        var follow = new Option<bool>("--follow", "-f") { Description = "Stream new log lines." };
        var tail = new Option<int?>("--tail") { Description = "Number of lines from the end of the logs." };
        var command = new Command("logs", "Show the server's container logs (docker compose logs).") { dir, follow, tail };
        command.SetAction(async (parse, ct) =>
        {
            var args = new List<string> { "logs", "--no-color" };
            if (parse.GetValue(follow)) args.Add("--follow");
            if (parse.GetValue(tail) is { } n)
            {
                if (n < 0) throw new CliException(ExitCodes.Usage, $"--tail must not be negative: {n}.");
                args.AddRange(["--tail", n.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            }
            await ComposeAsync(ctx, parse.GetValue(dir)!, args, ct, stream: true);
            return ExitCodes.Ok;
        });
        return command;
    }

    /// <summary>The exact arguments of <c>docker compose</c> for a deployment directory.</summary>
    internal static List<string> ComposeArguments(string dir, IEnumerable<string> subcommand)
    {
        var full = Path.GetFullPath(dir);
        var args = new List<string>
        {
            "compose", "-f", Path.Combine(full, ComposeFileName), "--env-file", Path.Combine(full, EnvFileName),
        };
        args.AddRange(subcommand);
        return args;
    }

    private static async Task<ProcessResult> ComposeAsync(CliContext ctx, string dir, IEnumerable<string> subcommand, CancellationToken ct, bool stream = false)
    {
        var full = Path.GetFullPath(dir);
        foreach (var file in new[] { ComposeFileName, EnvFileName })
            if (!File.Exists(Path.Combine(full, file)))
                throw new CliException(ExitCodes.Usage, $"{Path.Combine(full, file)} not found: run `xrmd server init --dir {dir}` first.");

        var spec = new ProcessSpec("docker", ComposeArguments(dir, subcommand))
        {
            OnOutputLine = stream ? line => { lock (ctx.Env.Out) ctx.Env.Out.WriteLine(line); } : null,
            OnErrorLine = stream ? line => { lock (ctx.Env.Err) ctx.Env.Err.WriteLine(line); } : null,
        };
        ProcessResult result;
        try
        {
            result = await ctx.Env.Process.RunAsync(spec, ct);
        }
        catch (ProcessNotFoundException ex)
        {
            throw new CliException(ExitCodes.Unexpected, DockerHint, ex);
        }
        if (result.ExitCode != 0)
        {
            var detail = result.StandardError.Trim();
            throw new CliException(ExitCodes.Unexpected,
                $"docker compose {subcommand.First()} failed (exit {result.ExitCode}){(detail.Length == 0 ? "." : ": " + detail)}");
        }
        return result;
    }

    /// <summary>The engine of the existing .env at <paramref name="envPath"/> (<c>STORAGE_PROVIDER</c>); null when there is none.</summary>
    private static StorageProvider? ExistingEngine(string envPath)
    {
        if (!File.Exists(envPath)) return null;
        var value = Installation.ReadRawValues(envPath).GetValueOrDefault(Installation.StorageProviderKey)?.Trim('\'', '"');
        try
        {
            return StorageSettings.ParseProvider(value);
        }
        catch (InvalidOperationException ex)
        {
            throw new CliException(ExitCodes.Rejected, $"{envPath}: {Installation.StorageProviderKey}={value} is not a storage provider: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The length of the generated <c>POSTGRES_PASSWORD</c>: letters and digits only, since it is pasted into each organization's
    /// PostgreSQL connection string, where <c>;</c>, quotes and <c>$</c> would need escaping.
    /// </summary>
    private const int PostgresPasswordLength = 32;

    private const string StorageProviderComment =
        "# The engine of every organization (`xrmd server init --database postgresql`): PostgreSql. Absent: SQL Server.";
    private const string PostgresPasswordComment =
        "# The password of the postgres user of the bundled postgres service, applied when its pg-data volume is first set up.";
    private const string SqlServerConnectionComment =
        "# Empty: the bundled sqlserver service. Otherwise an external SQL Server, without Database (each organization is a database).";
    private const string PostgresConnectionComment =
        "# Empty: the bundled postgres service. Otherwise an external PostgreSQL 16 or later (Host=…;Username=…;Password=…), " +
        "without Database (each organization is a database).";
    private const string SqlBackupDirectoryComment =
        "# With SQL_CONNECTION: the backup directory as a path on the SQL Server host (empty = /var/opt/mssql/backup).";

    /// <summary>
    /// <c>Templates/env.template</c> without the lines that do not apply: a SQL Server installation has no <c>STORAGE_PROVIDER</c>
    /// and no <c>POSTGRES_PASSWORD</c> (its <c>.env</c> is the one of before plan 2.7a), an external SQL Server no SA password and
    /// no edition (M8, P5); a PostgreSQL installation (plan 2.7a) has none of the SQL Server keys, and with an external server no
    /// <c>POSTGRES_PASSWORD</c> either.
    /// </summary>
    internal static string EnvTemplate(bool postgres, bool external)
    {
        var drop = new HashSet<string>(StringComparer.Ordinal);
        string[] sqlServerKeys = ["SA_PASSWORD={{SA_PASSWORD}}", "MSSQL_PID={{MSSQL_PID}}", Installation.SqlEditionComment,
            "ACCEPT_EULA={{ACCEPT_EULA}}", Installation.SqlEulaComment];
        string[] postgresPassword = [PostgresPasswordComment, "POSTGRES_PASSWORD={{POSTGRES_PASSWORD}}"];
        if (postgres)
        {
            drop.UnionWith(sqlServerKeys);
            drop.UnionWith([SqlServerConnectionComment, SqlBackupDirectoryComment, "SQL_BACKUP_DIRECTORY="]);
            if (external) drop.UnionWith(postgresPassword);
        }
        else
        {
            drop.UnionWith([StorageProviderComment, "STORAGE_PROVIDER={{STORAGE_PROVIDER}}", .. postgresPassword]);
            drop.Add(PostgresConnectionComment);
            if (external) drop.UnionWith(sqlServerKeys);
        }
        var lines = ComposeRenderer.ReadTemplate("env.template").Replace("\r\n", "\n").Split('\n').Where(l => !drop.Contains(l)).ToList();
        return string.Join('\n', lines);
    }

    private static string GeneratePassword()
    {
        // At least one of each class (SQL Server complexity), the rest from all classes, then shuffled.
        var chars = new List<char>
        {
            RandomNumberGenerator.GetString(Upper, 1)[0],
            RandomNumberGenerator.GetString(Lower, 1)[0],
            RandomNumberGenerator.GetString(Digits, 1)[0],
            RandomNumberGenerator.GetString(Symbols, 1)[0],
        };
        chars.AddRange(RandomNumberGenerator.GetString(Upper + Lower + Digits + Symbols, PasswordLength - chars.Count));
        var array = chars.ToArray();
        RandomNumberGenerator.Shuffle(array.AsSpan());
        return new string(array);
    }
}
