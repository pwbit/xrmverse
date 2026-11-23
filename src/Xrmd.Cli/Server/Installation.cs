using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Cli.Server;

/// <summary>
/// One organization of an installation: its database (and service, Seed:OrgName, profile) name, host port, initial
/// administrator and, optionally, the token audience (ruling M10; null = the default of <see cref="EffectiveAudience"/>) and
/// the public host name (ruling H1, resolved by <see cref="Installation"/>; null = no public host) and the host port of its SQL
/// (TDS) endpoint (plan 3.3, ruling T2; null = no endpoint).
/// </summary>
public sealed record OrgEntry(string Name, int Port, string AdminObjectId, string? Audience = null, string? Host = null, int? TdsPort = null)
{
    /// <summary>What SQL clients connect to, <c>&lt;host&gt;,&lt;port&gt;</c> (SQL Server's form); null without an endpoint or a host.</summary>
    public string? TdsServer => TdsPort is { } port && Host is { } host ? $"{host},{port.ToString(CultureInfo.InvariantCulture)}" : null;

    /// <summary>The URL of the organization's server on this host.</summary>
    public string Url => $"http://localhost:{Port.ToString(CultureInfo.InvariantCulture)}/";

    /// <summary>The URL clients use through the proxy, <c>https://&lt;host&gt;/</c>; null without a public host.</summary>
    public string? PublicUrl => Host is null ? null : $"https://{Host}/";

    /// <summary>The <c>Auth:Audience</c> the server validates: <see cref="Audience"/>, else <see cref="PublicUrl"/> (I8), else <see cref="Url"/>.</summary>
    public string EffectiveAudience => Audience ?? PublicUrl ?? Url;

    /// <summary>The URL of the CLI profiles, <c>status</c> and <c>doctor</c> (H7): <see cref="PublicUrl"/>, else <see cref="Url"/>.</summary>
    public string ClientUrl => PublicUrl ?? Url;
}

/// <summary>
/// How the organizations with a public host are reached (ruling H2): no proxy (no organization has a host), the generated
/// Caddy proxy, or the customer's own proxy.
/// </summary>
public enum ProxyMode
{
    None,
    Caddy,
    External,
}

/// <summary>
/// The installation model of a <c>xrmd server</c> directory (ruling M1): <c>.env</c> is the source of truth, and
/// <c>docker-compose.yml</c> is always rendered from it by <see cref="ComposeRenderer"/>.
/// </summary>
/// <remarks>
/// Keys owned by the model: <c>XRMD_ORGS</c> (org names in creation order), <c>ORG_&lt;ORG&gt;_PORT</c>,
/// <c>ORG_&lt;ORG&gt;_ADMIN_OBJECT_ID</c> and the optional <c>ORG_&lt;ORG&gt;_AUDIENCE</c>, <c>ORG_&lt;ORG&gt;_HOST</c> and
/// <c>ORG_&lt;ORG&gt;_TDS_PORT</c> (plan 3.3) per org, <c>SQL_CONNECTION</c> (empty, or an external SQL Server without a
/// database) and <c>MSSQL_PID</c> (the bundled SQL Server's edition, ruling P5 of Plan 10). The public hosts (H1, H2, H5, H6)
/// are read from <c>XRMD_DOMAIN</c>, <c>PROXY_MODE</c>, <c>TLS_MODE</c>, <c>TLS_ACME_EMAIL</c>, <c>TRUSTED_PROXIES</c>,
/// <c>EDGE_SUBNET</c> and <c>EXPOSE_PORTS</c>, which <c>server init</c> writes. Every other line of <c>.env</c> (secrets, auth, files, user additions and comments) is kept as it is.
/// </remarks>
public sealed partial class Installation
{
    public const string EnvFileName = ".env";
    public const string ComposeFileName = "docker-compose.yml";
    public const string CaddyfileName = "Caddyfile";
    public const string OrgsKey = "XRMD_ORGS";
    public const string SqlConnectionKey = "SQL_CONNECTION";
    /// <summary>The engine of the organizations (plan 2.7a): <c>PostgreSql</c>, or absent for SQL Server; the host's <c>Storage:Provider</c>.</summary>
    public const string StorageProviderKey = "STORAGE_PROVIDER";
    /// <summary>The password of the <c>postgres</c> user of the bundled <c>postgres</c> service (plan 2.7a).</summary>
    public const string PostgresPasswordKey = "POSTGRES_PASSWORD";
    /// <summary>The <c>server init --database</c> value of PostgreSQL organizations; <c>sqlserver</c> is the default.</summary>
    public const string PostgresDatabase = "postgresql";
    public const string SaPasswordKey = "SA_PASSWORD";
    public const string AuthModeKey = "AUTH_MODE";
    public const string DevAuthMode = "Development";
    /// <summary>The <c>AUTH_MODE</c> of the server's own identity provider (plan 2.8).</summary>
    public const string LocalAuthMode = "Local";
    public const string SqlEditionKey = "MSSQL_PID";
    public const string ImageKey = "XRMD_IMAGE";

    /// <summary>The SDK mode of the server (<c>standard</c> or <c>none</c>, see <c>SdkModes</c>); absent is standard.</summary>
    public const string SdkModeKey = Xrmd.Abstractions.Sdk.SdkModes.EnvironmentVariable;
    /// <summary>
    /// <c>ACCEPT_EULA</c> (issue #16, option 1): <c>Y</c> once the user has accepted the SQL Server license terms, with
    /// <c>--accept-sql-server-eula</c> or at the prompt; only the bundled SQL Server has it. xrmd never writes it otherwise.
    /// </summary>
    public const string SqlEulaKey = "ACCEPT_EULA";
    /// <summary>The comment line above <c>ACCEPT_EULA</c> in <c>.env</c> (template and <see cref="AcceptSqlEula"/>).</summary>
    public const string SqlEulaComment =
        "# Y: you accepted the Microsoft SQL Server license terms of the edition in MSSQL_PID (`xrmd server init|up --accept-sql-server-eula`). See README, \"SQL Server editions\".";
    /// <summary>The comment line above <c>XRMD_IMAGE</c> in <c>.env</c> (template and <see cref="EnsureImage"/>).</summary>
    public const string ImageComment =
        "# The server image every organization runs. `xrmd server upgrade --to <version>` moves its tag; a custom or local image is yours to maintain.";
    /// <summary>The edition of a new installation (P5).</summary>
    public const string DefaultSqlEdition = "Express";
    /// <summary>The edition the mssql image runs without <c>MSSQL_PID</c>: that of every installation from before the key.</summary>
    public const string PreviousDefaultSqlEdition = "Developer";
    /// <summary>The comment line above <c>MSSQL_PID</c> in <c>.env</c> (template and <see cref="EnsureSqlEdition"/>).</summary>
    public const string SqlEditionComment =
        "# SQL Server edition of the sqlserver service: Express, Developer (not licensed for production), Standard, Enterprise, " +
        "EnterpriseCore or a product key. See README, \"SQL Server editions\".";
    public const string DeveloperEditionWarning =
        "This installation runs the SQL Server Developer edition, which is not licensed for production. Set MSSQL_PID in .env (see README, \"SQL Server editions\").";
    public const int DefaultFirstPort = 5001;

    public const string DomainKey = "XRMD_DOMAIN";
    public const string ProxyModeKey = "PROXY_MODE";
    public const string TlsModeKey = "TLS_MODE";
    public const string AcmeEmailKey = "TLS_ACME_EMAIL";
    public const string TrustedProxiesKey = "TRUSTED_PROXIES";
    public const string EdgeSubnetKey = "EDGE_SUBNET";
    public const string ExposePortsKey = "EXPOSE_PORTS";
    public const string CaddyProxyMode = "caddy";
    public const string ExternalProxyMode = "external";
    /// <summary>The subnet of the compose network <c>edge</c> when <c>EDGE_SUBNET</c> is empty (H3).</summary>
    public const string DefaultEdgeSubnet = "172.31.250.0/24";
    /// <summary>The host of the <c>proxy</c> service on the <c>edge</c> network: this offset from the subnet's address (H3).</summary>
    public const int ProxyHostOffset = 10;
    /// <summary>A public host name (H1): a lower-case DNS name of at least two labels.</summary>
    public const string HostRule = @"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$";
    /// <summary>The <c>TLS_MODE</c> values (H5).</summary>
    public static readonly IReadOnlyList<string> TlsModes = ["acme", "files", "internal"];

    public const string OrgNameRule = "^[a-z][a-z0-9]{0,31}$";

    /// <summary>The port the SQL (TDS) endpoint listens on inside every organization's container (<c>Tds:Port</c>, ruling T2).</summary>
    public const int TdsContainerPort = 5558;
    /// <summary>The TDS host port of the first organization when <c>server init --tds-port</c> is not given a value (ruling T2).</summary>
    public const int DefaultFirstTdsPort = 5558;
    /// <summary>Plan 3.3 (ruling T5): SQL logins exist only with the server's own identity provider; the endpoint is refused otherwise.</summary>
    public const string TdsNeedsLocalAuth = "The SQL endpoint needs the local identity provider (--auth local)";
    /// <summary>The certificate pair of the SQL endpoint behind the customer's own proxy, <c>certs/tds.crt</c> and <c>certs/tds.key</c>.</summary>
    public const string TdsPairName = "tds";
    /// <summary>The certificate pair of <c>TLS_MODE=files</c> (<c>certs/server.crt</c>, <c>certs/server.key</c>).</summary>
    public const string FilesPairName = "server";

    private static readonly string[] ReservedNames = ["master", "tempdb", "model", "msdb"];

    /// <summary>The <c>MSSQL_PID</c> editions accepted by <c>server init --sql-edition</c> and in <c>.env</c> (P5).</summary>
    public static readonly IReadOnlyList<string> SqlEditions = ["Express", "Developer", "Standard", "Enterprise", "EnterpriseCore"];

    private readonly List<string> _lines;
    private readonly List<OrgEntry> _orgs;

    private Installation(string directory, List<string> lines, List<OrgEntry> orgs, string sqlConnection)
    {
        Directory = directory;
        _lines = lines;
        _orgs = orgs;
        SqlConnection = sqlConnection;
    }

    /// <summary>The full path of the installation directory.</summary>
    public string Directory { get; }

    public string EnvPath => Path.Combine(Directory, EnvFileName);

    public string ComposePath => Path.Combine(Directory, ComposeFileName);

    /// <summary>The <c>Caddyfile</c> of the <c>proxy</c> service, rendered by <see cref="CaddyfileRenderer"/> in <see cref="ProxyMode.Caddy"/> mode.</summary>
    public string CaddyfilePath => Path.Combine(Directory, CaddyfileName);

    /// <summary>The SDK folder of the installation, <c>&lt;dir&gt;/sdk</c> (ruling B6): bind-mounted read-only into every organization's container.</summary>
    public string SdkPath => Path.Combine(Directory, "sdk");

    /// <summary>True when <see cref="SdkPath"/> holds a valid <c>sdk.json</c>.</summary>
    public bool HasSdk => Xrmd.Abstractions.Sdk.SdkFolder.TryRead(SdkPath) is not null;

    /// <summary>True when <c>.env</c> has <c>XRMD_SDK_MODE=none</c> (an SDK-free server); an absent key is standard.</summary>
    public bool IsSdkFree => IsSdkFreeValue(TryGetValue(SdkModeKey));

    internal static bool IsSdkFreeValue(string? value) =>
        Xrmd.Abstractions.Sdk.SdkModes.TryParse(value?.Trim('\'', '"'), out var mode) && mode == Xrmd.Abstractions.Sdk.SdkMode.None;

    public IReadOnlyList<OrgEntry> Orgs => _orgs;

    /// <summary>The external SQL Server connection string without a database; empty for the bundled <c>sqlserver</c> service.</summary>
    public string SqlConnection { get; }

    /// <summary>
    /// The engine of the organizations (plan 2.7a): <c>STORAGE_PROVIDER</c>, written by <c>server init --database postgresql</c>;
    /// absent or empty is SQL Server, the default. One engine per installation (ruling P2).
    /// </summary>
    public StorageProvider Provider => StorageSettings.ParseProvider(TryGetValue(StorageProviderKey));

    /// <summary>True when the organizations are PostgreSQL databases (<see cref="Provider"/>).</summary>
    public bool IsPostgres => Provider == StorageProvider.PostgreSql;

    /// <summary>True when the organizations use an external SQL Server (<c>--sql-connection</c>, ruling M8).</summary>
    public bool ExternalSql => SqlConnection.Length > 0;

    /// <summary>
    /// The <c>MSSQL_PID</c> value (ruling P5): the edition of the bundled SQL Server, or a product key. Empty when the key
    /// is absent: an external SQL Server (whose edition is its own), or a <c>.env</c> from before the key existed
    /// (see <see cref="EnsureSqlEdition"/>).
    /// </summary>
    public string SqlEdition => TryGetValue(SqlEditionKey) ?? "";

    /// <summary>
    /// P5: a <c>.env</c> of the bundled SQL Server without <c>MSSQL_PID</c> predates the key, and its SQL Server ran the
    /// image's default edition, Developer. Writes <c>MSSQL_PID=Developer</c> into the model (after <c>SA_PASSWORD</c>), so
    /// that the edition of a data volume never changes silently, and returns true; <see cref="Save"/> writes it. False
    /// when the key is there or the SQL Server is external.
    /// </summary>
    public bool EnsureSqlEdition()
    {
        if (ExternalSql || IsPostgres || TryGetValue(SqlEditionKey) is not null) return false;
        var sa = _lines.FindIndex(l => ParseLine(l)?.Key == SaPasswordKey);
        var at = sa < 0 ? _lines.Count : sa + 1;
        _lines.InsertRange(at, [SqlEditionComment, SqlEditionKey + "=" + PreviousDefaultSqlEdition]);
        return true;
    }

    /// <summary>
    /// Issue #16: true when the user has accepted the SQL Server license terms (<c>ACCEPT_EULA=Y</c>), or when there is no
    /// bundled SQL Server to accept them for (an external SQL Server, or PostgreSQL organizations).
    /// </summary>
    public bool SqlEulaAccepted => ExternalSql || IsPostgres || TryGetValue(SqlEulaKey) == "Y";

    /// <summary>
    /// Issue #16: writes <c>ACCEPT_EULA=Y</c> into the model (after <c>MSSQL_PID</c>, else after <c>SA_PASSWORD</c>) and returns
    /// true; <see cref="Save"/> writes it. The caller has the user's acceptance. False when there is nothing to write.
    /// </summary>
    public bool AcceptSqlEula()
    {
        if (SqlEulaAccepted) return false;
        var existing = _lines.FindIndex(l => ParseLine(l)?.Key == SqlEulaKey);
        if (existing >= 0)
        {
            _lines[existing] = SqlEulaKey + "=Y";
            return true;
        }
        var after = _lines.FindIndex(l => ParseLine(l)?.Key == SqlEditionKey);
        if (after < 0) after = _lines.FindIndex(l => ParseLine(l)?.Key == SaPasswordKey);
        var at = after < 0 ? _lines.Count : after + 1;
        _lines.InsertRange(at, [SqlEulaComment, SqlEulaKey + "=Y"]);
        return true;
    }

    /// <summary>The <c>XRMD_DOMAIN</c> value (H1): organization <c>&lt;org&gt;</c> gets the host <c>&lt;org&gt;.&lt;domain&gt;</c>; null when empty or absent.</summary>
    public string? Domain => NonEmpty(DomainKey);

    /// <summary>
    /// H2: <see cref="ProxyMode.None"/> while no organization has a public host (whatever <c>PROXY_MODE</c> says), else
    /// <see cref="ProxyMode.External"/> for <c>PROXY_MODE=external</c> and <see cref="ProxyMode.Caddy"/> otherwise (the default).
    /// </summary>
    public ProxyMode Proxy =>
        !_orgs.Any(o => o.Host is not null) ? ProxyMode.None
        : NonEmpty(ProxyModeKey) == ExternalProxyMode ? ProxyMode.External
        : ProxyMode.Caddy;

    /// <summary>The <c>TLS_MODE</c> value (H5: acme, files or internal); null when empty or absent.</summary>
    public string? TlsMode => NonEmpty(TlsModeKey);

    /// <summary>The <c>TLS_ACME_EMAIL</c> value; null when empty or absent.</summary>
    public string? AcmeEmail => NonEmpty(AcmeEmailKey);

    /// <summary>The <c>TRUSTED_PROXIES</c> entries (IP addresses and CIDR networks of the customer's proxy, external mode).</summary>
    public IReadOnlyList<string> TrustedProxies => SplitList(TryGetValue(TrustedProxiesKey));

    /// <summary>The subnet of the compose network <c>edge</c>: <c>EDGE_SUBNET</c>, else <see cref="DefaultEdgeSubnet"/>.</summary>
    public string EdgeSubnet => NonEmpty(EdgeSubnetKey) ?? DefaultEdgeSubnet;

    /// <summary>
    /// The fixed address of the <c>proxy</c> service on the <c>edge</c> network (H3): the <c>.10</c> host of <see cref="EdgeSubnet"/>.
    /// In <see cref="ProxyMode.Caddy"/> mode every organization with a host trusts forwarded headers from this address only.
    /// </summary>
    public string ProxyAddress
    {
        get
        {
            if (EdgeSubnetProblem(EdgeSubnet) is { } problem)
                throw new CliException(ExitCodes.Rejected, $"{EnvPath}: {EdgeSubnetKey} {problem}.");
            var bytes = IPNetwork.Parse(EdgeSubnet).BaseAddress.GetAddressBytes();
            bytes[3] += ProxyHostOffset; // a network of /27 or larger has its last five bits free
            return new IPAddress(bytes).ToString();
        }
    }

    /// <summary>
    /// The <c>ip_range</c> of the <c>edge</c> network: the upper half of <see cref="EdgeSubnet"/>, from which Docker assigns
    /// every other container (the organizations, the gateway). Docker assigns from the start of the subnet, so without it the
    /// ninth organization behind the proxy would take the proxy's fixed and trusted <c>.10</c>, which stays in the lower half
    /// (hence a subnet of /27 or larger).
    /// </summary>
    public string EdgeIpRange
    {
        get
        {
            _ = ProxyAddress; // exit 5 on an unusable EDGE_SUBNET
            var network = IPNetwork.Parse(EdgeSubnet);
            var bytes = network.BaseAddress.GetAddressBytes();
            var half = 1u << (32 - network.PrefixLength - 1);
            var value = ((uint)bytes[0] << 24 | (uint)bytes[1] << 16 | (uint)bytes[2] << 8 | bytes[3]) + half;
            var upper = new IPAddress(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
            return $"{upper}/{network.PrefixLength + 1}";
        }
    }

    /// <summary><c>EXPOSE_PORTS=true</c>: with the Caddy proxy, the organizations keep their loopback host ports for diagnostics (H6).</summary>
    public bool ExposePorts => NonEmpty(ExposePortsKey) == "true";

    /// <summary>The certificate pair of <c>TLS_MODE=files</c>, <c>&lt;dir&gt;/certs/server.crt</c> and <c>server.key</c> (H5).</summary>
    public string CertsPath => Path.Combine(Directory, "certs");

    /// <summary>The root certificate of Caddy's internal CA, exported by <c>server up</c> in <c>TLS_MODE=internal</c> (H7).</summary>
    public string CaddyRootPath => Path.Combine(CertsPath, "caddy-root.crt");

    /// <summary><c>TLS_MODE=internal</c> with the generated proxy: the public hosts are served with Caddy's local CA (H5, H7).</summary>
    public bool UsesInternalCa => Proxy == ProxyMode.Caddy && TlsMode == "internal";

    /// <summary>
    /// Exit 2 when the proxy settings of <c>.env</c> do not fit the organizations' hosts (an organization gets a host on an
    /// installation whose <c>.env</c> has no <c>TLS_MODE</c>, for instance), naming the key. <c>server org add</c> calls it
    /// before anything is written; <see cref="Load"/> refuses the same states with exit 5.
    /// </summary>
    internal void RequireProxySettings()
    {
        if (ProxySettingsProblem(TryGetValue, _orgs.Any(o => o.Host is not null)) is { } problem)
            throw new CliException(ExitCodes.Usage,
                $"{problem.Key} {problem.Problem} in {EnvPath}. Set it there first (see `xrmd server init --help` for the values), or give the " +
                $"installation its first public hosts with `xrmd server init --force --dir {Directory} --org <name>… --domain <domain>|--public-host <org>=<host> " +
                "--tls acme|files|internal …` (it keeps the secrets of .env), then add the organization.");
    }

    /// <summary>The <c>XRMD_IMAGE</c> value; empty when the key is absent or empty (see <see cref="EnsureImage"/>).</summary>
    public string Image => TryGetValue(ImageKey) ?? "";

    /// <summary>
    /// A <c>.env</c> without <c>XRMD_IMAGE</c> (or with it empty) predates the pinned image: the rendered compose file requires
    /// the key. Writes <c>XRMD_IMAGE=<paramref name="defaultImage"/></c> into the model (first, above the secrets) and returns
    /// true; <see cref="Save"/> writes it. False when the key already has a value.
    /// </summary>
    public bool EnsureImage(string defaultImage)
    {
        if (Image.Length > 0) return false;
        var existing = _lines.FindIndex(l => ParseLine(l)?.Key == ImageKey);
        if (existing >= 0)
        {
            _lines[existing] = ImageKey + "=" + defaultImage;
            return true;
        }
        var first = _lines.FindIndex(l => ParseLine(l) is not null);
        _lines.InsertRange(first < 0 ? _lines.Count : first, [ImageComment, ImageKey + "=" + defaultImage]);
        return true;
    }

    /// <summary>Sets <c>XRMD_IMAGE</c> (<c>server upgrade --to</c>); <see cref="Save"/> writes it. The key must be there (<see cref="EnsureImage"/>).</summary>
    internal void SetImage(string image) => SetValue(ImageKey, image);

    /// <summary>
    /// The canonical form of an <c>MSSQL_PID</c> value: one of <see cref="SqlEditions"/> (any case, written as listed) or a
    /// product key <c>XXXXX-XXXXX-XXXXX-XXXXX-XXXXX</c> (upper case); null for anything else.
    /// </summary>
    public static string? NormalizeSqlEdition(string value)
    {
        if (SqlEditions.FirstOrDefault(e => string.Equals(e, value, StringComparison.OrdinalIgnoreCase)) is { } edition) return edition;
        return ProductKeyPattern().IsMatch(value) ? value.ToUpperInvariant() : null;
    }

    /// <summary>The accepted <c>MSSQL_PID</c> values, for messages.</summary>
    public static string SqlEditionRule => $"{string.Join(", ", SqlEditions)} or a product key XXXXX-XXXXX-XXXXX-XXXXX-XXXXX";

    /// <summary>The <c>AUTH_MODE</c> value; empty or absent is <c>Development</c>, the default the compose file applies (<c>${AUTH_MODE:-Development}</c>).</summary>
    public string AuthMode => TryGetValue(AuthModeKey) is { Length: > 0 } mode ? mode : DevAuthMode;

    /// <summary>True for <c>AUTH_MODE=Local</c>: the compose file mounts the organization's secrets folder and hands each organization its initial administrator password file.</summary>
    public bool IsLocalAuth => AuthMode == LocalAuthMode;

    /// <summary>The folder of the one-shot initial administrator password files, <c>&lt;dir&gt;/secrets</c> (mounted at <c>/run/xrmd/secrets</c>).</summary>
    public string SecretsPath => Path.Combine(Directory, "secrets");

    /// <summary>The secrets folder of <paramref name="org"/>, <c>&lt;dir&gt;/secrets/&lt;org&gt;</c>: the only part of <c>secrets</c> its container mounts.</summary>
    public string OrgSecretsPath(string org) => Path.Combine(SecretsPath, org);

    /// <summary>
    /// The folder of the SQL endpoint certificates, <c>&lt;dir&gt;/tds</c> (plan 3.3): <c>tds-certs</c> copies each organization's
    /// pair into <c>tds/&lt;org&gt;/</c>, which only that organization mounts (read-only, at <c>/run/xrmd/tds</c>).
    /// </summary>
    public string TdsPath => Path.Combine(Directory, "tds");

    /// <summary>The SQL endpoint certificate folder of <paramref name="org"/>, <c>&lt;dir&gt;/tds/&lt;org&gt;</c>.</summary>
    public string OrgTdsPath(string org) => Path.Combine(TdsPath, org);

    /// <summary>The certificate the SQL endpoint of <paramref name="org"/> serves, <c>tds/&lt;org&gt;/&lt;host&gt;.crt</c>; null without a host.</summary>
    public string? TdsCertificatePath(OrgEntry org) => org.Host is { } host ? Path.Combine(OrgTdsPath(org.Name), host + ".crt") : null;

    /// <summary>True when an organization has a SQL endpoint (the <c>tds-certs</c> service is rendered).</summary>
    public bool HasTds => _orgs.Any(o => o.TdsPort is not null);

    /// <summary>
    /// Where <c>tds-certs</c> takes the pair from (ruling T12): empty for Caddy's storage (<c>TLS_MODE=acme|internal</c>, by host),
    /// <see cref="FilesPairName"/> for <c>TLS_MODE=files</c> (the given files), <see cref="TdsPairName"/> behind the customer's proxy.
    /// </summary>
    public string TdsPairSource => Proxy switch
    {
        ProxyMode.Caddy when TlsMode == "files" => FilesPairName,
        ProxyMode.Caddy => "",
        _ => TdsPairName,
    };

    /// <summary>
    /// Exit 2 when <paramref name="org"/> cannot have a SQL endpoint on <paramref name="tdsPort"/>: the installation is not in
    /// Local mode (ruling T5), the organization has no public host (its certificate names it), or the port is not free.
    /// </summary>
    internal void RequireTdsPort(string org, int tdsPort)
    {
        if (!IsLocalAuth) throw new CliException(ExitCodes.Usage, $"{TdsNeedsLocalAuth}: SQL logins are checked by it, and this installation uses {AuthMode} authentication.");
        if (tdsPort is < 1 or > 65535) throw new CliException(ExitCodes.Usage, $"--tds-port must be between 1 and 65535: {tdsPort}.");
        if (Proxy == ProxyMode.Caddy && tdsPort is 80 or 443)
            throw new CliException(ExitCodes.Usage, $"TDS port {tdsPort} is the proxy's port (80 and 443 are published by the proxy service): choose another one, such as {DefaultFirstTdsPort}.");
        if (_orgs.FirstOrDefault(o => o.Name == org) is { Host: null })
            throw new CliException(ExitCodes.Usage, $"The SQL endpoint of '{org}' needs a public host: its certificate names the host.");
        if (PortOwner(tdsPort, org) is { } owner)
            throw new CliException(ExitCodes.Usage, $"TDS port {tdsPort} is already used by {owner.Name}{(owner.Port == tdsPort ? " (its HTTP port)" : "")}.");
    }

    /// <summary>Gives <paramref name="org"/> its SQL endpoint on <paramref name="tdsPort"/> (checked by <see cref="RequireTdsPort"/>).</summary>
    internal OrgEntry SetTdsPort(string org, int tdsPort)
    {
        RequireTdsPort(org, tdsPort);
        var index = _orgs.FindIndex(o => o.Name == org);
        _orgs[index] = _orgs[index] with { TdsPort = tdsPort };
        return _orgs[index];
    }

    /// <summary>The organization whose HTTP host port, or another organization whose TDS host port, is <paramref name="port"/>; null when it is free.</summary>
    private OrgEntry? PortOwner(int port, string except) =>
        _orgs.FirstOrDefault(o => o.Port == port || (o.Name != except && o.TdsPort == port));

    /// <summary>
    /// Creates <c>tds/&lt;org&gt;</c> (0750, the host user's) for every organization with a SQL endpoint; <c>tds-certs</c> gives it
    /// the server's group. An existing folder is left as it is.
    /// </summary>
    internal void EnsureTdsFolders()
    {
        foreach (var org in _orgs.Where(o => o.TdsPort is not null))
        {
            var path = OrgTdsPath(org.Name);
            if (System.IO.Directory.Exists(path)) continue;
            if (OperatingSystem.IsWindows())
            {
                System.IO.Directory.CreateDirectory(path);
                continue;
            }
            if (!System.IO.Directory.Exists(TdsPath))
                System.IO.Directory.CreateDirectory(TdsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            System.IO.Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        }
    }

    /// <summary>
    /// The files the SQL endpoints need that are missing behind the customer's proxy (<c>certs/tds.crt</c>, <c>certs/tds.key</c>,
    /// ruling T12); empty otherwise. Paths relative to the installation.
    /// </summary>
    public IReadOnlyList<string> MissingTdsPair() =>
        HasTds && TdsPairSource == TdsPairName
            ? new[] { TdsPairName + ".crt", TdsPairName + ".key" }.Where(n => !File.Exists(Path.Combine(CertsPath, n))).Select(n => "certs/" + n).ToList()
            : [];

    /// <summary>The initial administrator password file of <paramref name="org"/> (<c>admin-password</c> in its secrets folder), deleted by the server once used.</summary>
    public string AdminPasswordPath(string org) => Path.Combine(OrgSecretsPath(org), "admin-password");

    /// <summary>
    /// Set by <c>server init --force</c> only: <see cref="Save"/> may then replace a compose file or a <c>Caddyfile</c> the
    /// renderer did not produce (a legacy or hand-written one, ruling M2). Otherwise such a file is never overwritten (M1).
    /// </summary>
    internal bool OverwriteForeignCompose { get; set; }

    /// <summary>The value of a key of <c>.env</c>, unquoted; null when the key is absent.</summary>
    public string? TryGetValue(string key)
    {
        foreach (var line in _lines)
            if (ParseLine(line) is { } kv && kv.Key == key) return kv.Value;
        return null;
    }

    /// <summary>
    /// A new installation from the <c>.env</c> template text (<c>server init</c>); no org yet. Validates
    /// <paramref name="sqlConnection"/> (M8: exit 2 when it does not parse or names a database).
    /// </summary>
    internal static Installation Create(string dir, string envTemplate, string sqlConnection)
    {
        var lines = envTemplate.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();
        var installation = new Installation(Path.GetFullPath(dir), lines, [], sqlConnection);
        installation.SetValue(SqlConnectionKey, sqlConnection.Length == 0 ? "" : "'" + sqlConnection + "'");
        installation.SetValue(OrgsKey, "");
        return installation;
    }

    /// <summary>
    /// Reads <c>.env</c> of <paramref name="dir"/>. Exit 2 when it is missing; exit 5 for a legacy installation (M2) or
    /// an inconsistent <c>.env</c>, naming the key.
    /// </summary>
    public static Installation Load(string dir)
    {
        var full = Path.GetFullPath(dir);
        var envPath = Path.Combine(full, EnvFileName);
        if (!File.Exists(envPath))
            throw new CliException(ExitCodes.Usage, $"{envPath} not found: run `xrmd server init --dir {dir}` first.");

        var lines = File.ReadAllText(envPath).Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var rawValues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (ParseLine(line) is not { } kv) continue;
            if (!values.TryAdd(kv.Key, kv.Value))
                throw Inconsistent(envPath, kv.Key, "is set more than once");
            rawValues[kv.Key] = line[(line.IndexOf('=') + 1)..].Trim();
        }

        if (!values.TryGetValue(OrgsKey, out var orgList))
            throw new CliException(ExitCodes.Rejected, LegacyMessage(full));

        var composePath = Path.Combine(full, ComposeFileName);
        if (File.Exists(composePath) && !ComposeRenderer.IsGenerated(File.ReadAllText(composePath)))
            throw new CliException(ExitCodes.Rejected, ForeignComposeMessage(composePath, full));

        if (orgList.Length == 0)
            throw Inconsistent(envPath, OrgsKey, "is empty: an installation has at least one organization");
        // Empty or absent means Development, as the compose file's ${AUTH_MODE:-Development}: the model never diverges from what runs.
        var isDev = !values.TryGetValue(AuthModeKey, out var authMode) || authMode.Length == 0 || authMode == DevAuthMode;
        // Plan 2.7a: the engine of the organizations, SQL Server when absent or empty.
        StorageProvider provider;
        try
        {
            provider = StorageSettings.ParseProvider(values.GetValueOrDefault(StorageProviderKey));
        }
        catch (InvalidOperationException)
        {
            throw Inconsistent(envPath, StorageProviderKey, $"must be empty (SQL Server) or PostgreSql: '{values[StorageProviderKey]}'");
        }
        var postgres = provider == StorageProvider.PostgreSql;
        var domain = values.TryGetValue(DomainKey, out var domainText) && domainText.Length > 0 ? domainText : null;
        if (domain is not null && ValidateHost(domain) is { } domainProblem)
            throw Inconsistent(envPath, DomainKey, $"is invalid: {domainProblem}");
        var names = orgList.Split(',');
        var orgs = new List<OrgEntry>();
        foreach (var name in names)
        {
            if (!IsValidOrgName(name, out var reason))
                throw Inconsistent(envPath, OrgsKey, $"lists '{name}', which is not a valid organization name ({reason})");
            if (orgs.Any(o => o.Name == name))
                throw Inconsistent(envPath, OrgsKey, $"lists '{name}' more than once");

            var portKey = PortKey(name);
            if (!values.TryGetValue(portKey, out var portText))
                throw Inconsistent(envPath, portKey, $"is missing although {OrgsKey} lists '{name}'");
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
                throw Inconsistent(envPath, portKey, $"must be a port number between 1 and 65535: '{portText}'");
            if (orgs.FirstOrDefault(o => o.Port == port) is { } other)
                throw Inconsistent(envPath, portKey, $"is {port}, the port of '{other.Name}' too");

            var adminKey = AdminObjectIdKey(name);
            if (!values.TryGetValue(adminKey, out var admin))
                throw Inconsistent(envPath, adminKey, $"is missing although {OrgsKey} lists '{name}'");
            if (!Guid.TryParse(admin, out var adminGuid))
                throw Inconsistent(envPath, adminKey, $"must be a GUID: '{admin}'");

            // H1: ORG_<ORG>_HOST, else <org>.XRMD_DOMAIN, else none. Hosts are unique across organizations.
            var hostKey = HostKey(name);
            string? host = null;
            var explicitHost = values.TryGetValue(hostKey, out var hostText) && hostText.Length > 0;
            if (explicitHost)
            {
                if (ValidateHost(hostText!) is { } hostProblem) throw Inconsistent(envPath, hostKey, $"is invalid: {hostProblem}");
                host = hostText;
            }
            else if (domain is not null)
            {
                host = DerivedHost(name, domain);
                if (ValidateHost(host) is { } hostProblem)
                    throw Inconsistent(envPath, DomainKey, $"gives '{name}' an invalid host: {hostProblem}");
            }
            if (host is not null && orgs.FirstOrDefault(o => o.Host == host) is { } sameHost)
                throw Inconsistent(envPath, explicitHost ? hostKey : DomainKey,
                    $"gives '{name}' the host {host}, the host of '{sameHost.Name}' too: hosts are unique across organizations");

            var audienceKey = AudienceKey(name);
            string? audience = null;
            if (values.TryGetValue(audienceKey, out var audienceText) && audienceText.Length > 0)
            {
                if (!IsAbsoluteAudience(audienceText))
                    throw Inconsistent(envPath, audienceKey, $"must be empty or an absolute URI (the API's Application ID URI): '{audienceText}'");
                if (isDev)
                    throw Inconsistent(envPath, audienceKey,
                        $"must be empty with {AuthModeKey}={DevAuthMode}: dev tokens carry the profile URL ({new OrgEntry(name, port, "", Host: host).ClientUrl}) as their audience");
                audience = audienceText;
            }

            // Plan 3.3: ORG_<ORG>_TDS_PORT, the host port of the organization's SQL endpoint (absent or empty: none).
            var tdsKey = TdsPortKey(name);
            int? tdsPort = null;
            if (values.TryGetValue(tdsKey, out var tdsText) && tdsText.Length > 0)
            {
                if (!int.TryParse(tdsText, NumberStyles.None, CultureInfo.InvariantCulture, out var tds) || tds is < 1 or > 65535)
                    throw Inconsistent(envPath, tdsKey, $"must be empty or a port number between 1 and 65535: '{tdsText}'");
                if (!values.TryGetValue(AuthModeKey, out var mode) || mode != LocalAuthMode)
                    throw Inconsistent(envPath, tdsKey, $"must be empty unless {AuthModeKey}={LocalAuthMode}: {TdsNeedsLocalAuth}");
                if (host is null)
                    throw Inconsistent(envPath, tdsKey, $"must be empty: '{name}' has no public host, which the SQL endpoint's certificate names");
                if (tds is 80 or 443 && values.GetValueOrDefault(ProxyModeKey) != ExternalProxyMode)
                    throw Inconsistent(envPath, tdsKey, $"is {tds}, a port of the proxy service");
                tdsPort = tds;
            }

            orgs.Add(new OrgEntry(name, port, adminGuid.ToString("D"), audience, host, tdsPort));
        }

        foreach (var org in orgs.Where(o => o.TdsPort is not null))
            if (orgs.FirstOrDefault(o => o.Port == org.TdsPort || (o.Name != org.Name && o.TdsPort == org.TdsPort)) is { } other)
                throw Inconsistent(envPath, TdsPortKey(org.Name), $"is {org.TdsPort}, a port of '{other.Name}' too");

        foreach (var key in values.Keys)
        {
            var match = OrgKeyPattern().Match(key);
            if (match.Success && !orgs.Any(o => o.Name.ToUpperInvariant() == match.Groups[1].Value))
                throw Inconsistent(envPath, key, $"belongs to no organization listed in {OrgsKey} ({orgList})");
        }

        if (!values.TryGetValue(SqlConnectionKey, out var sql))
            throw Inconsistent(envPath, SqlConnectionKey, "is missing (empty for the bundled SQL Server, or an external SQL Server without a database)");
        if (rawValues[SqlConnectionKey] is { Length: > 0 } rawSql && !(rawSql.Length >= 2 && rawSql[0] == '\'' && rawSql[^1] == '\''))
            throw Inconsistent(envPath, SqlConnectionKey,
                "must be empty or single-quoted ('…'): only single quotes keep $ and # literal for Docker Compose");
        if (sql.Length > 0 && ValidateSqlConnection(sql, provider) is { } sqlError)
            throw Inconsistent(envPath, SqlConnectionKey, sqlError);
        if (sql.Length == 0 && !postgres && (!values.TryGetValue(SaPasswordKey, out var sa) || sa.Length == 0))
            throw Inconsistent(envPath, SaPasswordKey, $"is missing or empty, and the bundled SQL Server needs it ({SqlConnectionKey} is empty)");
        if (sql.Length == 0 && postgres && (!values.TryGetValue(PostgresPasswordKey, out var pgPassword) || pgPassword.Length == 0))
            throw Inconsistent(envPath, PostgresPasswordKey, $"is missing or empty, and the bundled PostgreSQL needs it ({SqlConnectionKey} is empty)");
        // P5. Absent is a .env from before the key (EnsureSqlEdition); present, it is what the sqlserver service runs.
        if (values.TryGetValue(SqlEditionKey, out var edition))
        {
            if (postgres && edition.Length > 0)
                throw Inconsistent(envPath, SqlEditionKey, $"must not be set with {StorageProviderKey}=PostgreSql: there is no sqlserver service");
            if (sql.Length > 0 && edition.Length > 0)
                throw Inconsistent(envPath, SqlEditionKey,
                    $"must not be set with {SqlConnectionKey}: there is no sqlserver service, the external SQL Server's edition is its own");
            if (sql.Length == 0 && !postgres && NormalizeSqlEdition(edition) is null)
                throw Inconsistent(envPath, SqlEditionKey, $"must be {SqlEditionRule}: '{edition}'");
        }

        // Issue #16. Absent or empty: not accepted yet (the commands that render ask for it); present, it is Y.
        if (values.TryGetValue(SqlEulaKey, out var eula) && eula.Length > 0)
        {
            if (postgres)
                throw Inconsistent(envPath, SqlEulaKey,
                    $"must not be set with {StorageProviderKey}=PostgreSql: there is no sqlserver service whose license terms it accepts");
            if (sql.Length > 0)
                throw Inconsistent(envPath, SqlEulaKey,
                    $"must not be set with {SqlConnectionKey}: there is no sqlserver service, the external SQL Server's license is your own");
            if (eula != "Y")
                throw Inconsistent(envPath, SqlEulaKey,
                    "must be Y (written by `xrmd server init|up --accept-sql-server-eula`) or absent");
        }

        if (ProxySettingsProblem(key => values.GetValueOrDefault(key), orgs.Any(o => o.Host is not null)) is { } proxyProblem)
            throw Inconsistent(envPath, proxyProblem.Key, proxyProblem.Problem);

        return new Installation(full, lines, orgs, sql);
    }

    /// <summary>
    /// Adds an organization: <paramref name="port"/> null takes the next free port above the highest one in use
    /// (<see cref="DefaultFirstPort"/> for the first); <paramref name="host"/> null takes <c>&lt;name&gt;.XRMD_DOMAIN</c> when the
    /// installation has a domain (H1). Exit 2 on an invalid or duplicate name, a used port, a non-GUID admin, or an invalid or
    /// taken host.
    /// </summary>
    public OrgEntry Add(string name, int? port, string adminObjectId, string? host = null)
    {
        ValidateOrgName(name);
        if (_orgs.Any(o => o.Name == name))
            throw new CliException(ExitCodes.Usage, $"The organization '{name}' already exists in {EnvPath}.");

        var chosen = port ?? (_orgs.Count == 0 ? DefaultFirstPort : _orgs.Max(o => o.Port) + 1);
        // An automatic port skips the SQL endpoints' ports (plan 3.3); a given one is refused below.
        while (port is null && chosen <= 65535 && _orgs.Any(o => o.TdsPort == chosen)) chosen++;
        if (chosen is < 1 or > 65535)
            throw new CliException(ExitCodes.Usage, port is null
                ? $"No free port above {_orgs.Max(o => o.Port)} for '{name}': pass --port."
                : $"--port must be between 1 and 65535: {chosen}.");
        if (_orgs.FirstOrDefault(o => o.Port == chosen || o.TdsPort == chosen) is { } other)
            throw new CliException(ExitCodes.Usage, $"Port {chosen} is already used by the organization '{other.Name}'.");

        if (!Guid.TryParse(adminObjectId.Trim(), out var admin))
            throw new CliException(ExitCodes.Usage, $"--admin-object-id must be a GUID: '{adminObjectId}'.");

        var resolvedHost = host ?? (Domain is { } domain ? DerivedHost(name, domain) : null);
        if (resolvedHost is not null)
        {
            if (ValidateHost(resolvedHost) is { } problem)
                throw new CliException(ExitCodes.Usage, host is null
                    ? $"{DomainKey}={Domain} gives '{name}' an invalid host: {problem}."
                    : $"Invalid public host for '{name}': {problem}.");
            if (_orgs.FirstOrDefault(o => o.Host == resolvedHost) is { } sameHost)
                throw new CliException(ExitCodes.Usage,
                    $"The host {resolvedHost} is already the host of the organization '{sameHost.Name}': hosts are unique across organizations.");
        }

        var entry = new OrgEntry(name, chosen, admin.ToString("D"), Host: resolvedHost);
        _orgs.Add(entry);
        return entry;
    }

    /// <summary>Removes an organization from the model (exit 4 when it is not part of the installation).</summary>
    public void Remove(string name)
    {
        if (_orgs.RemoveAll(o => o.Name == name) == 0)
            throw new CliException(ExitCodes.NotFound,
                $"The organization '{name}' is not part of the installation in {Directory} ({OrgsKey}={string.Join(',', _orgs.Select(o => o.Name))}).");
    }

    /// <summary>
    /// Writes <c>.env</c> (mode 0600), then <c>docker-compose.yml</c> and the <c>Caddyfile</c> rendered from it (or removes a
    /// generated <c>Caddyfile</c> once there is no Caddy proxy, Review Focus 4). Everything is rendered before anything is
    /// written; a compose file or <c>Caddyfile</c> without the renderer's header is never overwritten (exit 5). True when the
    /// <c>Caddyfile</c> changed (the running proxy then needs a <c>caddy reload</c>).
    /// </summary>
    public bool Save()
    {
        var (compose, caddyfile) = RenderChecked();
        System.IO.Directory.CreateDirectory(Directory);
        // Every file is written in full and flushed beside its target before any of them replaces its target: an
        // interrupted Save (a full disk, say) never leaves a half-written file, nor a .env without its compose file.
        var envTemp = WriteTemp(EnvPath, RenderEnv(), secret: true);
        string? composeTemp = null;
        CaddyfileWrite caddy;
        try
        {
            composeTemp = WriteTemp(ComposePath, compose, secret: false);
            caddy = StageCaddyfile(caddyfile);
        }
        catch
        {
            File.Delete(envTemp);
            if (composeTemp is not null) File.Delete(composeTemp);
            throw;
        }
        File.Move(envTemp, EnvPath, overwrite: true);
        File.Move(composeTemp, ComposePath, overwrite: true);
        return ApplyCaddyfile(caddy);
    }

    /// <summary>
    /// Re-renders <c>docker-compose.yml</c> and the <c>Caddyfile</c> from the model (<c>server up</c>/<c>upgrade</c>), so
    /// that a hand-edited <c>.env</c> is what runs. Same refusals and result as <see cref="Save"/>; <c>.env</c> is not touched.
    /// </summary>
    public bool WriteRenderedFiles()
    {
        var (compose, caddyfile) = RenderChecked();
        var composeTemp = WriteTemp(ComposePath, compose, secret: false);
        CaddyfileWrite caddy;
        try
        {
            caddy = StageCaddyfile(caddyfile);
        }
        catch
        {
            File.Delete(composeTemp);
            throw;
        }
        File.Move(composeTemp, ComposePath, overwrite: true);
        return ApplyCaddyfile(caddy);
    }

    /// <summary>The refusals of <see cref="Save"/> (a hand-edited compose file or <c>Caddyfile</c>), without writing anything.</summary>
    internal void CheckWritable() => RenderChecked();

    /// <summary>What <see cref="ApplyCaddyfile"/> does: nothing, delete the generated file, or copy a staged temporary file over it.</summary>
    private readonly record struct CaddyfileWrite(bool Changed, string? Temp);

    /// <summary>
    /// The first half of the <c>Caddyfile</c> write: decides what changes and, for new content, flushes it to a temporary
    /// file beside the target (a full disk fails here, before <see cref="Save"/> replaces anything).
    /// </summary>
    private CaddyfileWrite StageCaddyfile(string? caddyfile)
    {
        var existing = File.Exists(CaddyfilePath) ? File.ReadAllText(CaddyfilePath) : null;
        if (caddyfile is null) // a file xrmd did not write is left alone
            return new(existing is not null && IsRegenerable(existing), null);
        if (existing == caddyfile) return new(false, null);
        return new(true, WriteTemp(CaddyfilePath, caddyfile, secret: false));
    }

    /// <summary>
    /// Writes the staged <c>Caddyfile</c> in place, or deletes the generated one. In place, not renamed over: the
    /// <c>proxy</c> container bind-mounts this one file, and keeps reading the old file (inode) after a rename, so that
    /// <c>caddy reload</c> would load the old sites. True when the content changed.
    /// </summary>
    private bool ApplyCaddyfile(CaddyfileWrite write)
    {
        if (!write.Changed) return false;
        if (write.Temp is not { } temp)
        {
            File.Delete(CaddyfilePath);
            return true;
        }
        try
        {
            var bytes = File.ReadAllBytes(temp);
            using var stream = new FileStream(CaddyfilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
            stream.SetLength(0);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            File.Delete(temp);
        }
        return true;
    }

    /// <summary>A <c>Caddyfile</c> xrmd may replace: one it generated, or an empty one (an interrupted in-place write).</summary>
    private static bool IsRegenerable(string caddyfile) => caddyfile.Length == 0 || ComposeRenderer.IsGenerated(caddyfile);

    /// <summary>
    /// <see cref="Load"/> for a multi-organization installation; null when <paramref name="dir"/> has no <c>.env</c> or a
    /// legacy one (no <c>XRMD_ORGS</c>, ruling M2), which <c>up</c>/<c>down</c>/<c>upgrade</c>/<c>logs</c> drive unchanged.
    /// </summary>
    public static Installation? LoadIfMultiOrg(string dir)
    {
        var envPath = Path.Combine(Path.GetFullPath(dir), EnvFileName);
        if (!File.Exists(envPath)) return null;
        var hasOrgs = File.ReadAllText(envPath).Replace("\r\n", "\n").Split('\n').Any(l => ParseLine(l)?.Key == OrgsKey);
        return hasOrgs ? Load(dir) : null;
    }

    /// <summary>The compose file and the <c>Caddyfile</c> (null without the Caddy proxy), after the refusals of <see cref="Save"/>.</summary>
    private (string Compose, string? Caddyfile) RenderChecked()
    {
        var compose = ComposeRenderer.Render(this);
        if (File.Exists(ComposePath) && !OverwriteForeignCompose && !ComposeRenderer.IsGenerated(File.ReadAllText(ComposePath)))
            throw new CliException(ExitCodes.Rejected, ForeignComposeMessage(ComposePath, Directory));
        if (Proxy != ProxyMode.Caddy) return (compose, null);
        var caddyfile = CaddyfileRenderer.Render(this);
        if (File.Exists(CaddyfilePath) && !OverwriteForeignCompose && !IsRegenerable(File.ReadAllText(CaddyfilePath)))
            throw new CliException(ExitCodes.Rejected,
                $"{CaddyfilePath} was not generated by xrmd (its first line is not '{ComposeRenderer.Header} …'); it is never overwritten. " +
                $"Move it away (xrmd renders it from .env), or re-create the installation with `xrmd server init --force --dir {Directory} …`.");
        return (compose, caddyfile);
    }

    private static string ForeignComposeMessage(string composePath, string dir) =>
        $"{composePath} was not generated by xrmd (its first line is not '{ComposeRenderer.Header} …'); it is never overwritten. " +
        $"Move it away, or re-create the installation with `xrmd server init --org <name> --force --dir {dir}`.";

    /// <summary>Writes <paramref name="content"/> to <c>&lt;target&gt;.tmp</c> (0600 from creation when secret) and flushes it to disk.</summary>
    private static string WriteTemp(string target, string content, bool secret) =>
        WriteTemp(target, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content), secret);

    /// <summary>
    /// Writes <paramref name="content"/> to <c>&lt;target&gt;.tmp</c> (0600 from creation when secret), flushes it to disk and
    /// returns its path; the caller renames it over the target. A failed write leaves no temporary file.
    /// </summary>
    internal static string WriteTemp(string target, byte[] content, bool secret)
    {
        var temp = target + ".tmp";
        File.Delete(temp); // a leftover of an interrupted Save: CreateNew below must create the file with its mode
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (secret && !OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using var stream = new FileStream(temp, options);
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
        return temp;
    }

    /// <summary>Exit 2 unless <paramref name="name"/> matches <see cref="OrgNameRule"/> and is not a SQL Server system database.</summary>
    public static void ValidateOrgName(string name)
    {
        if (!IsValidOrgName(name, out var reason))
            throw new CliException(ExitCodes.Usage, $"Invalid organization name '{name}': {reason}.");
    }

    /// <summary>Null when <paramref name="connection"/> is a valid external SQL Server connection string for SQL_CONNECTION; the reason otherwise.</summary>
    public static string? ValidateSqlConnection(string connection) => ValidateSqlConnection(connection, StorageProvider.SqlServer);

    /// <summary>
    /// Null when <paramref name="connection"/> is a valid external server connection string of <paramref name="provider"/> for
    /// SQL_CONNECTION (plan 2.7a: an Npgsql string with <c>Host</c> for PostgreSQL); the reason otherwise.
    /// </summary>
    public static string? ValidateSqlConnection(string connection, StorageProvider provider)
    {
        if (connection.Contains('\'') || connection.Contains('\n') || connection.Contains('\r'))
            return "must not contain a single quote or a line break (it is written single-quoted to .env)";
        StorageProvider given;
        try
        {
            given = StorageConnectionStrings.ProviderOf(connection);
        }
        catch (ArgumentException ex)
        {
            return $"is not a valid connection string: {ex.Message}";
        }
        // Plan 2.7a, Task 5 review (Minor 3): an Npgsql string is named as such, not refused by SqlClient's "Keyword not supported: 'host'".
        if (provider == StorageProvider.SqlServer && given == StorageProvider.PostgreSql)
            return "is a PostgreSQL connection string (it names a Host): PostgreSQL organizations are set up with --database postgresql";
        if (provider == StorageProvider.PostgreSql) return ValidatePostgresConnection(connection);
        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(connection);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        {
            return $"is not a valid SQL Server connection string: {ex.Message}";
        }
        if (!string.IsNullOrEmpty(builder.InitialCatalog))
            return $"must not set Database/Initial Catalog ('{builder.InitialCatalog}'): each organization's database is its name";
        if (!string.IsNullOrEmpty(builder.AttachDBFilename))
            return "must not set AttachDBFilename: each organization's database is its name";
        if (string.IsNullOrEmpty(builder.DataSource))
            return "must set Server/Data Source";
        return null;
    }

    /// <summary>Null when <paramref name="connection"/> is an Npgsql connection string with a <c>Host</c> and no database.</summary>
    private static string? ValidatePostgresConnection(string connection)
    {
        var builder = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connection };
        if (!builder.TryGetValue("Host", out var host) || host is not string { Length: > 0 })
            return "must set Host (the connection string of a PostgreSQL 16 or later server: Host=…;Username=…;Password=…)";
        if (StorageConnectionStrings.DatabaseName(connection) is { Length: > 0 } database)
            return $"must not set Database ('{database}'): each organization's database is its name";
        return null;
    }

    internal static string PortKey(string org) => $"ORG_{org.ToUpperInvariant()}_PORT";

    internal static string AdminObjectIdKey(string org) => $"ORG_{org.ToUpperInvariant()}_ADMIN_OBJECT_ID";

    internal static string AudienceKey(string org) => $"ORG_{org.ToUpperInvariant()}_AUDIENCE";

    internal static string HostKey(string org) => $"ORG_{org.ToUpperInvariant()}_HOST";

    internal static string TdsPortKey(string org) => $"ORG_{org.ToUpperInvariant()}_TDS_PORT";

    /// <summary>H1: the host an organization takes from <c>XRMD_DOMAIN</c>.</summary>
    internal static string DerivedHost(string org, string domain) => org + "." + domain;

    /// <summary>Null when <paramref name="host"/> is a valid public host name (<see cref="HostRule"/>); the reason otherwise.</summary>
    public static string? ValidateHost(string host) =>
        HostPattern().IsMatch(host)
            ? null
            : $"'{host}' is not a host name: it must be a lower-case DNS name of at least two labels, such as acme.example.com, matching {HostRule}";

    /// <summary>Null when <paramref name="entry"/> is an IP address or a CIDR network without host bits (<c>TRUSTED_PROXIES</c>); the reason otherwise.</summary>
    public static string? ValidateTrustedProxy(string entry)
    {
        var valid = entry.Contains('/')
            ? TryParseNetwork(entry, out _)
            : IPAddress.TryParse(entry, out var address) && (address.AddressFamily == AddressFamily.InterNetworkV6 || entry.Count(c => c == '.') == 3);
        return valid && !entry.Any(char.IsWhiteSpace)
            ? null
            : $"'{entry}' is not an IP address or a CIDR network (e.g. 10.0.0.5 or 10.1.0.0/16, without host bits after the prefix)";
    }

    /// <summary>
    /// A CIDR network whose written address is its base address. .NET 10 parses <c>172.31.250.5/24</c> as <c>172.31.250.0/24</c>
    /// without complaint, where .NET 8 refused it; a typo must not silently shift a trust boundary.
    /// </summary>
    private static bool TryParseNetwork(string text, out IPNetwork network)
    {
        network = default;
        var slash = text.IndexOf('/');
        return slash > 0 && IPNetwork.TryParse(text, out network)
               && IPAddress.TryParse(text[..slash], out var written) && written.Equals(network.BaseAddress);
    }

    /// <summary>
    /// Null when <paramref name="subnet"/> can be the <c>edge</c> network (IPv4 CIDR, /27 or larger, no host bits); the reason otherwise.
    /// /27 is the smallest network whose lower half holds the proxy's <c>.10</c> while the upper half is <see cref="EdgeIpRange"/>.
    /// </summary>
    public static string? EdgeSubnetProblem(string subnet) =>
        subnet.Contains('/') && TryParseNetwork(subnet, out var network)
            && network.BaseAddress.AddressFamily == AddressFamily.InterNetwork && network.PrefixLength <= 27
            ? null
            : $"must be an IPv4 CIDR network of /27 or larger, without host bits (e.g. {DefaultEdgeSubnet}): the proxy's address .{ProxyHostOffset} " +
              $"is in its lower half, the other containers get addresses from its upper half: '{subnet}'";

    /// <summary>Null when <paramref name="email"/> looks like an e-mail address (<c>TLS_ACME_EMAIL</c>); the reason otherwise.</summary>
    public static string? ValidateAcmeEmail(string email) =>
        AcmeEmailPattern().IsMatch(email) ? null : $"must be an e-mail address, such as ops@example.com: '{email}'";

    /// <summary>
    /// The first problem of the proxy settings (H2, H3, H5, H6) as (key, problem), or null. Values are checked whenever they
    /// are set; the requirements of a mode apply only when <paramref name="hasHosts"/> (without hosts there is no proxy).
    /// </summary>
    private static (string Key, string Problem)? ProxySettingsProblem(Func<string, string?> get, bool hasHosts)
    {
        string Value(string key) => get(key) ?? "";
        var mode = Value(ProxyModeKey);
        if (mode.Length > 0 && mode is not (CaddyProxyMode or ExternalProxyMode))
            return (ProxyModeKey, $"must be empty, {CaddyProxyMode} or {ExternalProxyMode}: '{mode}'");
        var tls = Value(TlsModeKey);
        if (tls.Length > 0 && !TlsModes.Contains(tls))
            return (TlsModeKey, $"must be empty or one of {string.Join(", ", TlsModes)}: '{tls}'");
        var email = Value(AcmeEmailKey);
        if (email.Length > 0 && ValidateAcmeEmail(email) is { } emailProblem) return (AcmeEmailKey, emailProblem);
        var subnet = Value(EdgeSubnetKey);
        if (subnet.Length > 0 && EdgeSubnetProblem(subnet) is { } subnetProblem) return (EdgeSubnetKey, subnetProblem);
        var expose = Value(ExposePortsKey);
        if (expose.Length > 0 && expose is not ("true" or "false"))
            return (ExposePortsKey, $"must be empty, true or false: '{expose}'");
        var proxies = SplitList(Value(TrustedProxiesKey));
        foreach (var entry in proxies)
            if (ValidateTrustedProxy(entry) is { } proxyProblem) return (TrustedProxiesKey, $"is invalid: {proxyProblem}");
        if (!hasHosts) return null;

        if (mode == ExternalProxyMode)
        {
            if (proxies.Count == 0)
                return (TrustedProxiesKey, $"is required with {ProxyModeKey}={ExternalProxyMode}: the addresses of your proxy, without which " +
                                           "the organizations would generate URLs with their own scheme and host");
            if (tls.Length > 0) return (TlsModeKey, $"must be empty with {ProxyModeKey}={ExternalProxyMode}: TLS is configured on your proxy");
            if (email.Length > 0) return (AcmeEmailKey, $"must be empty with {ProxyModeKey}={ExternalProxyMode}: TLS is configured on your proxy");
            if (subnet.Length > 0) return (EdgeSubnetKey, $"must be empty with {ProxyModeKey}={ExternalProxyMode}: there is no edge network");
            if (expose == "true") return (ExposePortsKey, $"must be empty with {ProxyModeKey}={ExternalProxyMode}: the ports are always published for your proxy");
            return null;
        }
        if (tls.Length == 0)
            return (TlsModeKey, $"is required with {ProxyModeKey}={CaddyProxyMode} (the default when an organization has a host): {string.Join(", ", TlsModes)}");
        if (tls == "acme" && email.Length == 0) return (AcmeEmailKey, $"is required with {TlsModeKey}=acme");
        if (tls != "acme" && email.Length > 0) return (AcmeEmailKey, $"must be empty unless {TlsModeKey}=acme");
        if (proxies.Count > 0)
            return (TrustedProxiesKey, $"must be empty with {ProxyModeKey}={CaddyProxyMode}: the organizations trust the proxy's address on the edge network only");
        return null;
    }

    private static IReadOnlyList<string> SplitList(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private string? NonEmpty(string key) => TryGetValue(key) is { Length: > 0 } value ? value : null;

    private static bool IsAbsoluteAudience(string value) =>
        !value.StartsWith('/') && Uri.TryCreate(value, UriKind.Absolute, out var uri) && !uri.IsFile && !value.Any(char.IsWhiteSpace);

    internal static string LegacyMessage(string dir) =>
        $"The installation in {dir} predates multi-organization support (a single `xrmd` service on the database 'xrmverse', " +
        $"no {OrgsKey} in .env), so it cannot be changed with `xrmd server org`. To move it (README, Multiple organizations, " +
        "Legacy installations): " +
        "1. back up the database with `xrmd org backup xrmverse` while it runs; " +
        $"2. `xrmd server down --dir {dir}` (without --volumes); " +
        $"3. `xrmd server init --org setup --force --dir {dir}` (keeps SA_PASSWORD and the dev signing key); " +
        $"4. `xrmd server up --dir {dir}` (removes the old `xrmd` container); " +
        "5. `xrmd org restore <name> --file <backup file>`, with <name> the old SEED_ORG_NAME (default xrmd); " +
        $"6. `xrmd server org add <name> --dir {dir}`; 7. `xrmd server org remove setup --drop-database --dir {dir}`. " +
        "Until then `xrmd server up|down|upgrade|logs` keep working on it unchanged.";

    /// <summary>
    /// The values of an existing <c>.env</c> as written (quotes included), first occurrence of each key: <c>server init --force</c>
    /// keeps its secrets. Empty when the file does not exist.
    /// </summary>
    internal static Dictionary<string, string> ReadRawValues(string envPath)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(envPath)) return values;
        foreach (var line in File.ReadAllText(envPath).Replace("\r\n", "\n").Split('\n'))
            if (ParseLine(line) is { } kv && kv.Value.Length > 0)
                values.TryAdd(kv.Key, line[(line.IndexOf('=') + 1)..].Trim());
        return values;
    }

    /// <summary>True when <paramref name="name"/> can be an organization of an installation; otherwise <paramref name="reason"/> says why.</summary>
    public static bool IsValidOrgName(string name, out string reason)
    {
        if (!OrgNamePattern().IsMatch(name))
        {
            reason = $"it must match {OrgNameRule} (a lower-case letter, then up to 31 lower-case letters or digits), because it is used " +
                     "verbatim as the database name, the compose service xrmd-<name>, Seed:OrgName and the CLI profile name";
            return false;
        }
        if (ReservedNames.Contains(name))
        {
            reason = $"'{name}' is a reserved SQL Server system database name ({string.Join(", ", ReservedNames)})";
            return false;
        }
        reason = "";
        return true;
    }

    private static CliException Inconsistent(string envPath, string key, string problem) =>
        new(ExitCodes.Rejected, $"{envPath} is inconsistent: {key} {problem}. Fix .env by hand; docker-compose.yml was not rendered.");

    /// <summary>The .env text: the kept lines, with XRMD_ORGS followed by every org's keys in order.</summary>
    private string RenderEnv()
    {
        var builder = new StringBuilder();
        foreach (var line in _lines)
        {
            var key = ParseLine(line)?.Key;
            if (key is not null && OrgKeyPattern().IsMatch(key)) continue; // re-emitted after XRMD_ORGS
            if (key != OrgsKey)
            {
                builder.Append(line).Append('\n');
                continue;
            }
            builder.Append(OrgsKey).Append('=').Append(string.Join(',', _orgs.Select(o => o.Name))).Append('\n');
            foreach (var org in _orgs)
            {
                builder.Append(PortKey(org.Name)).Append('=').Append(org.Port.ToString(CultureInfo.InvariantCulture)).Append('\n');
                builder.Append(AdminObjectIdKey(org.Name)).Append('=').Append(org.AdminObjectId).Append('\n');
                builder.Append(AudienceKey(org.Name)).Append('=').Append(org.Audience ?? "").Append('\n');
                // Only a host that XRMD_DOMAIN does not give already (H1): an installation without hosts keeps its .env unchanged.
                if (org.Host is not null && (Domain is not { } domain || org.Host != DerivedHost(org.Name, domain)))
                    builder.Append(HostKey(org.Name)).Append('=').Append(org.Host).Append('\n');
                // Only with a SQL endpoint (plan 3.3): an installation without one keeps its .env unchanged.
                if (org.TdsPort is { } tdsPort)
                    builder.Append(TdsPortKey(org.Name)).Append('=').Append(tdsPort.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
        }
        return builder.ToString();
    }

    private void SetValue(string key, string rawValue)
    {
        var index = _lines.FindIndex(l => ParseLine(l)?.Key == key);
        if (index < 0) throw new InvalidOperationException($"The .env template has no {key} line.");
        _lines[index] = key + "=" + rawValue;
    }

    /// <summary>A <c>KEY=value</c> line of a Compose env file; quotes around the whole value are removed. Null for comments and blank lines.</summary>
    private static KeyValuePair<string, string>? ParseLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed[0] == '#') return null;
        var eq = trimmed.IndexOf('=');
        if (eq <= 0) return null;
        var key = trimmed[..eq].Trim();
        var value = trimmed[(eq + 1)..].Trim();
        if (value.Length >= 2 && (value[0] == '\'' || value[0] == '"') && value[^1] == value[0]) value = value[1..^1];
        return new(key, value);
    }

    [GeneratedRegex(@"^[a-z][a-z0-9]{0,31}\z")] // OrgNameRule; \z so that a trailing line break never matches
    private static partial Regex OrgNamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9]{5}(-[A-Za-z0-9]{5}){4}\z")]
    private static partial Regex ProductKeyPattern();

    [GeneratedRegex("^ORG_([A-Z0-9]+)_(PORT|ADMIN_OBJECT_ID|AUDIENCE|HOST|TDS_PORT)$")]
    private static partial Regex OrgKeyPattern();

    [GeneratedRegex(@"^(?=.{1,253}\z)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}\z")] // HostRule; \z so that a trailing line break never matches
    private static partial Regex HostPattern();

    [GeneratedRegex(@"^[^@\s,;'""#$`{}\\]+@[^@\s,;'""#$`{}\\]+\.[^@\s,;'""#$`{}\\]+\z")] // pasted into .env and the Caddyfile verbatim
    private static partial Regex AcmeEmailPattern();
}
