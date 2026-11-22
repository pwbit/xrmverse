using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Host;
using Xrmd.Host.Idp;
using Xrmd.Tds;

namespace Xrmd.Integration.Tests.Tds;

/// <summary>
/// The xrmd host with the SQL endpoint on an ephemeral port (<c>Tds:Port = 0</c>) and a self-signed PEM certificate in a temporary
/// folder, with the production authenticator and query handler. With an <see cref="TdsTestOrg"/>, the host serves that organization
/// database (seeded at its start) in <c>Auth:Mode=Local</c>, and its System Administrator (<see cref="AdminUserName"/>) gets the
/// local password <see cref="AdminPassword"/>, set with <see cref="CredentialService"/> as the 2.8 tests do: SQL logins work.
/// Without one, or with <c>Auth:Mode</c> overridden in <see cref="TdsTestOrg.Settings"/>, the host is in Development mode, where
/// every SQL login is refused.
/// </summary>
public sealed class TdsTestHost : IAsyncDisposable
{
    /// <summary>An access token a client sends; refused, since the TDS server does not read FEDAUTH (ruling T5).</summary>
    public const string TestToken = "test-token";

    /// <summary>The System Administrator's user name (its <c>domainname</c>).</summary>
    public const string AdminUserName = "acme-admin";

    /// <summary>The System Administrator's local password (Local mode only).</summary>
    public const string AdminPassword = "acme-admin-tds-pass";

    public const string LocalAudience = "https://acme.local.test/";

    private readonly string _dir;

    private TdsTestHost(string dir, WebApplication app, int port)
    {
        _dir = dir;
        App = app;
        Port = port;
    }

    public WebApplication App { get; }

    /// <summary>Every log line of the host (message and exception), to check that no secret reaches the log.</summary>
    public LogSink Logs { get; private init; } = new();

    /// <summary>The endpoint's port; with a host started without its certificate, set by <see cref="WaitForListenerAsync"/>.</summary>
    public int Port { get; private set; }

    public string CertificatePath => Path.Combine(_dir, "tds.crt");

    public string PrivateKeyPath => Path.Combine(_dir, "tds.key");

    /// <param name="org">The organization served in Local mode; none: Development mode.</param>
    /// <param name="withCertificate">False: the certificate files do not exist yet when the host starts (<see cref="WriteCertificatePair"/> writes them).</param>
    public static async Task<TdsTestHost> StartAsync(TdsTestOrg? org = null, bool withCertificate = true)
    {
        var dir = Directory.CreateTempSubdirectory("xrmd-tds-").FullName;
        if (withCertificate) WriteCertificate(Path.Combine(dir, "tds.crt"), Path.Combine(dir, "tds.key"));

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ApplicationName = typeof(Program).Assembly.GetName().Name,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var mode = org?.Settings.GetValueOrDefault("Auth:Mode") ?? (org is null ? "Development" : "Local");
        builder.Configuration["Auth:Mode"] = mode;
        if (mode == "Local")
        {
            builder.Configuration["Auth:Audience"] = LocalAudience;
        }
        else
        {
            builder.Configuration["Auth:Audience"] = "http://127.0.0.1/";
            builder.Configuration["Auth:DevSigningKey"] = DevTokenFactory.SigningKey;
        }
        builder.Configuration["Tds:Port"] = "0";
        builder.Configuration["Tds:CertificatePath"] = Path.Combine(dir, "tds.crt");
        builder.Configuration["Tds:PrivateKeyPath"] = Path.Combine(dir, "tds.key");
        if (org is not null)
        {
            builder.Configuration["ConnectionStrings:Org"] = org.ConnectionString;
            builder.Configuration[StorageSettings.ProviderKey] = TestProvider.Setting(TestProvider.ProviderOf(org.ConnectionString)); // plan 2.7b
            builder.Configuration["Seed:OrgName"] = "acme";
            builder.Configuration["Seed:AdminObjectId"] = org.AdminObjectId.ToString();
            builder.Configuration["Seed:AdminName"] = AdminUserName;
            builder.Configuration["Pipeline:AsyncWorkerEnabled"] = "false";
            foreach (var (key, value) in org.Settings) builder.Configuration[key] = value;
        }
        builder.AddXrmd();
        var logs = new LogSink();
        builder.Services.AddSingleton<ILoggerProvider>(logs);

        var app = builder.Build();
        try
        {
            await app.UseXrmdAsync();
            await app.StartAsync();
            var bound = app.Services.GetRequiredService<TdsListener>().BoundPort;
            if (withCertificate && bound is null) throw new InvalidOperationException("The SQL endpoint did not start.");
            var port = bound ?? 0;
            var host = new TdsTestHost(dir, app, port) { Logs = logs };
            if (org is not null && mode == "Local")
                await host.SetPasswordAsync(await host.SystemUserIdAsync(org.AdminObjectId), AdminPassword);
            return host;
        }
        catch
        {
            await app.DisposeAsync();
            Directory.Delete(dir, recursive: true);
            throw;
        }
    }

    /// <summary>The <c>systemuserid</c> of the user with Entra object id <paramref name="objectId"/>.</summary>
    public async Task<Guid> SystemUserIdAsync(Guid objectId)
    {
        await using var conn = await App.Services.GetRequiredService<IDbConnectionFactory>().OpenAsync();
        await using var cmd = TestSql.Command(conn, "SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid");
        cmd.AddParameter("@oid", objectId);
        return (Guid)(await cmd.ExecuteScalarAsync() ?? throw new InvalidOperationException("No systemuser has this object id."));
    }

    /// <summary>Gives the user a local credential with <paramref name="password"/> (Local mode only).</summary>
    public Task SetPasswordAsync(Guid systemUserId, string password) =>
        WithCredentialsAsync(c => c.SetPasswordAsync(systemUserId, password, default));

    /// <summary>Runs <paramref name="action"/> with the host's <see cref="CredentialService"/> (Local mode only), in a scope of its own.</summary>
    public async Task WithCredentialsAsync(Func<CredentialService, Task> action)
    {
        await using var scope = App.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<CredentialService>());
    }

    /// <summary>A connection string to the endpoint: TLS on, the self-signed certificate trusted.</summary>
    public SqlConnectionStringBuilder ConnectionString() => new()
    {
        DataSource = $"127.0.0.1,{Port}",
        InitialCatalog = "acme",
        Encrypt = SqlConnectionEncryptOption.Mandatory,
        TrustServerCertificate = true,
        ConnectTimeout = 15,
        Pooling = false,
    };

    /// <summary><see cref="ConnectionString"/> with a SQL login: the System Administrator's by default.</summary>
    public SqlConnectionStringBuilder SqlLoginConnectionString(string userName = AdminUserName, string password = AdminPassword)
    {
        var builder = ConnectionString();
        builder.UserID = userName;
        builder.Password = password;
        return builder;
    }

    /// <summary>
    /// Sends a PRELOGIN with the given ENCRYPTION option (0 off, 1 on, 2 not supported), then, with
    /// <paramref name="cleartextLogin"/>, a well-formed LOGIN7 in clear with the valid SQL login of the System Administrator
    /// (<see cref="AdminUserName"/>, <see cref="AdminPassword"/>).
    /// Returns the server's ENCRYPTION answer, whether the server then closed the connection (the read ends within 3 s), and
    /// whether the bytes it sent back hold a LOGINACK token (0xAD).
    /// </summary>
    public async Task<(byte Encryption, bool Closed, bool LoginAck)> ProbePreLoginAsync(byte encryption, bool cleartextLogin = false,
        CancellationToken cancellationToken = default)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", Port, cancellationToken);
        var tds = new TdsPreLoginStream(client.GetStream());
        var answer = await tds.SendPreLoginAsync(encryption, cancellationToken);
        if (cleartextLogin) await client.GetStream().WriteAsync(Login7Packet(AdminUserName, AdminPassword, "acme"), cancellationToken);

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(TimeSpan.FromSeconds(3));
        var received = new MemoryStream();
        bool closed;
        try
        {
            // Whatever the server sends back (e.g. an error token or a TLS alert), it must then close the connection.
            var buffer = new byte[4096];
            int read;
            while ((read = await client.GetStream().ReadAsync(buffer, wait.Token)) > 0) received.Write(buffer, 0, read);
            closed = true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            closed = false;
        }
        catch (IOException)
        {
            closed = true;
        }

        return (ReadEncryptionOption(answer), closed, HasLoginAck(received.ToArray()));
    }

    /// <summary>Whether the TDS packets in <paramref name="bytes"/> hold a LOGINACK token (0xAD); conservative: any 0xAD byte in a payload.</summary>
    private static bool HasLoginAck(byte[] bytes)
    {
        for (var i = 0; i + 8 <= bytes.Length;)
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(i + 2));
            if (length < 8) return bytes.AsSpan(i).Contains((byte)0xAD);
            var end = Math.Min(bytes.Length, i + length);
            if (bytes.AsSpan(i + 8, end - i - 8).Contains((byte)0xAD)) return true;
            i = end;
        }

        return false;
    }

    /// <summary>A well-formed TDS 7.4 LOGIN7 packet with a SQL login (MS-TDS 2.2.6.4).</summary>
    private static byte[] Login7Packet(string user, string password, string database)
    {
        const int fixedLength = 94;
        string[] variables = ["localhost", user, password, "xrmd-tds-probe", "127.0.0.1", "", "xrmd-tds-probe", "", database];
        var data = new MemoryStream();
        var offsets = new List<(ushort Offset, ushort Length)>();
        for (var v = 0; v < variables.Length; v++)
        {
            var bytes = System.Text.Encoding.Unicode.GetBytes(variables[v]);
            // The password is obfuscated: each byte has its nibbles swapped, then is XORed with 0xA5.
            if (v == 2)
                for (var b = 0; b < bytes.Length; b++) bytes[b] = (byte)(((bytes[b] << 4) | (bytes[b] >> 4)) ^ 0xA5);
            offsets.Add(((ushort)(fixedLength + data.Length), (ushort)variables[v].Length));
            data.Write(bytes);
        }

        var body = new byte[fixedLength + data.Length];
        var span = body.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 0x74000004); // TDS 7.4
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], 4096); // Packet size
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 1); // Client PID
        span[24] = 0xE0; // OptionFlags1: USE_DB_ON, INIT_DB_FATAL, SET_LANG_ON
        span[25] = 0x03; // OptionFlags2: INIT_LANG_FATAL, ODBC_ON
        for (var v = 0; v < offsets.Count; v++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(span[(36 + v * 4)..], offsets[v].Offset);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(38 + v * 4)..], offsets[v].Length);
        }

        // ClientID (6 bytes at 72), then SSPI, AtchDBFile and ChangePassword (empty, at the end of the data), cbSSPILong 0.
        foreach (var at in new[] { 78, 82, 86 }) BinaryPrimitives.WriteUInt16LittleEndian(span[at..], (ushort)body.Length);
        data.ToArray().CopyTo(body, fixedLength);

        var packet = new byte[8 + body.Length];
        packet[0] = 0x10; // LOGIN7
        packet[1] = 0x01; // End of message.
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        packet[6] = 1;
        body.CopyTo(packet, 8);
        return packet;
    }

    private static byte ReadEncryptionOption(byte[] preLogin)
    {
        for (var i = 0; i + 5 <= preLogin.Length && preLogin[i] != 0xFF; i += 5)
        {
            if (preLogin[i] == 0x01) return preLogin[BinaryPrimitives.ReadUInt16BigEndian(preLogin.AsSpan(i + 1))];
        }

        throw new InvalidOperationException("The PRELOGIN answer has no ENCRYPTION option.");
    }

    /// <summary>Writes the PEM pair the host is configured with (key first, each file moved into place, as tds-certs does).</summary>
    public void WriteCertificatePair()
    {
        var crt = Path.Combine(_dir, "first.crt");
        var key = Path.Combine(_dir, "first.key");
        WriteCertificate(crt, key);
        File.Move(key, PrivateKeyPath, overwrite: true);
        File.Move(crt, CertificatePath, overwrite: true);
    }

    /// <summary>Waits until the endpoint listens, then returns (and keeps as <see cref="Port"/>) its port; null on timeout.</summary>
    public async Task<int?> WaitForListenerAsync(TimeSpan timeout)
    {
        var listener = App.Services.GetRequiredService<TdsListener>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (listener.BoundPort is { } port) return Port = port;
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
        return null;
    }

    /// <summary>Replaces the PEM files with a new self-signed certificate (each file moved into place) and returns its thumbprint.</summary>
    public string ReplaceCertificate()
    {
        var crt = Path.Combine(_dir, "new.crt");
        var key = Path.Combine(_dir, "new.key");
        var thumbprint = WriteCertificate(crt, key);
        File.Move(key, PrivateKeyPath, overwrite: true);
        File.Move(crt, CertificatePath, overwrite: true);
        return thumbprint;
    }

    /// <summary>The thumbprint of the certificate the endpoint presents now, read from the TLS handshake of a PRELOGIN probe.</summary>
    public async Task<string> ProbeThumbprintAsync(CancellationToken cancellationToken = default)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", Port, cancellationToken);
        var tds = new TdsPreLoginStream(client.GetStream());
        await tds.SendPreLoginAsync(0x01, cancellationToken);
        string? thumbprint = null;
        await using var tls = new SslStream(tds, leaveInnerStreamOpen: true, (_, certificate, _, _) =>
        {
            thumbprint = certificate?.GetCertHashString();
            return true;
        });
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, cancellationToken);
        return thumbprint ?? throw new InvalidOperationException("The endpoint presented no certificate.");
    }

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string WriteCertificate(string certificatePath, string keyPath)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        return certificate.Thumbprint;
    }

    /// <summary>
    /// The client side of TDS 7.x TLS negotiation: a PRELOGIN asking for encryption, then the TLS handshake carried in PRELOGIN
    /// packets (8-byte TDS header). Enough to read the server certificate; nothing is sent after the handshake.
    /// </summary>
    private sealed class TdsPreLoginStream(NetworkStream inner) : Stream
    {
        private const byte PreLogin = 0x12;
        private byte[] _payload = [];
        private int _offset;

        /// <summary>Sends a PRELOGIN with the ENCRYPTION option <paramref name="encryption"/>; returns the server's PRELOGIN answer.</summary>
        public async Task<byte[]> SendPreLoginAsync(byte encryption, CancellationToken cancellationToken)
        {
            // Options: VERSION (0x00, 6 bytes) and ENCRYPTION (0x01, 1 byte), then the terminator.
            byte[] body =
            [
                0x00, 0x00, 0x0B, 0x00, 0x06,
                0x01, 0x00, 0x11, 0x00, 0x01,
                0xFF,
                0x10, 0x00, 0x00, 0x00, 0x00, 0x00,
                encryption,
            ];
            await WritePacketAsync(body, cancellationToken);
            var answer = await ReadPacketAsync(cancellationToken);
            _payload = [];
            _offset = 0;
            return answer;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset >= _payload.Length)
            {
                _payload = await ReadPacketAsync(cancellationToken);
                _offset = 0;
            }

            var count = Math.Min(buffer.Length, _payload.Length - _offset);
            _payload.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            await WritePacketAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private async Task WritePacketAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            var packet = new byte[8 + payload.Length];
            packet[0] = PreLogin;
            packet[1] = 0x01; // End of message.
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
            packet[6] = 1;
            payload.CopyTo(packet.AsMemory(8));
            await inner.WriteAsync(packet, cancellationToken);
        }

        private async Task<byte[]> ReadPacketAsync(CancellationToken cancellationToken)
        {
            var header = new byte[8];
            await inner.ReadExactlyAsync(header, cancellationToken);
            var payload = new byte[BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2)) - 8];
            await inner.ReadExactlyAsync(payload, cancellationToken);
            return payload;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();
    }
}

/// <summary>An organization database for <see cref="TdsTestHost"/>, its System Administrator and extra settings (e.g. <c>Tds:MaxRows</c>).</summary>
public sealed record TdsTestOrg(string ConnectionString, Guid AdminObjectId, IReadOnlyDictionary<string, string?> Settings)
{
    public TdsTestOrg(string connectionString, Guid adminObjectId) : this(connectionString, adminObjectId, new Dictionary<string, string?>()) { }
}

/// <summary>Keeps the formatted log lines of a host (Information and above), with their exceptions.</summary>
public sealed class LogSink : ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines;

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

    public void Dispose() { }

    private sealed class Logger(string category, System.Collections.Concurrent.ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
        }
    }
}
