using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Http2;
using Http2.Hpack;

namespace ProxmoxBackupServerProtocolSpike;

public sealed record PbsHttp2Response(int StatusCode, byte[] Body, IReadOnlyDictionary<string, string> Headers);

public sealed class PbsHttp2Session : IAsyncDisposable
{
    private readonly Connection _connection;
    private readonly string _authority;
    private bool _disposed;

    private PbsHttp2Session(Connection connection, string authority)
    {
        _connection = connection;
        _authority = authority;
    }

    public static async Task<PbsHttp2Session> OpenAsync(
        PbsConnectionOptions options,
        string protocol,
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        RejectHeaderInjection(protocol, nameof(protocol));
        RejectHeaderInjection(options.AuthenticationId, nameof(options.AuthenticationId));
        RejectHeaderInjection(options.TokenSecret, nameof(options.TokenSecret));

        var tcp = new TcpClient();
        SslStream? tls = null;
        try
        {
            await tcp.ConnectAsync(options.Server.Host, options.Server.Port, cancellationToken).ConfigureAwait(false);
            tcp.NoDelay = true;
            tls = new SslStream(
                tcp.GetStream(),
                leaveInnerStreamOpen: false,
                (_, certificate, _, errors) => ValidateCertificate(certificate, errors, options.CertificateFingerprint));
            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = options.Server.Host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                },
                cancellationToken).ConfigureAwait(false);

            var config = new ConnectionConfigurationBuilder(false)
                .UseSettings(Settings.Default)
                .UseHuffmanStrategy(HuffmanStrategy.IfSmaller)
                .Build();
            var upgrade = new ClientUpgradeRequestBuilder().SetHttp2Settings(config.Settings).Build();
            var authority = options.Server.IsDefaultPort ? options.Server.Host : $"{options.Server.Host}:{options.Server.Port}";
            var request =
                $"GET {path} HTTP/1.1\r\n" +
                $"Host: {authority}\r\n" +
                "User-Agent: Duplicati-PBS-Protocol-Spike/1\r\n" +
                $"Authorization: PBSAPIToken={options.AuthenticationId}:{options.TokenSecret}\r\n" +
                "Connection: Upgrade\r\n" +
                $"Upgrade: {protocol}\r\n\r\n";
            var requestBytes = Encoding.ASCII.GetBytes(request);
            await tls.WriteAsync(requestBytes, cancellationToken).ConfigureAwait(false);
            await tls.FlushAsync(cancellationToken).ConfigureAwait(false);

            var responseHeader = await ReadHttp1HeaderAsync(tls, cancellationToken).ConfigureAwait(false);
            ValidateUpgradeResponse(responseHeader, protocol);

            var streams = tls.CreateStreams();
            tls = null;
            var connection = new Connection(
                config,
                streams.ReadableStream,
                streams.WriteableStream,
                options: new Connection.Options { ClientUpgradeRequest = upgrade });
            var session = new PbsHttp2Session(connection, authority);
            try
            {
                var setupStream = await upgrade.UpgradeRequestStream.WaitAsync(cancellationToken).ConfigureAwait(false);
                var setupResponse = await session.ReadResponseAsync(setupStream, cancellationToken).ConfigureAwait(false);
                EnsureSuccess(setupResponse, "PBS protocol session setup");
                return session;
            }
            catch
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            tls?.Dispose();
            tcp.Dispose();
            throw;
        }
    }

    public async Task<PbsHttp2Response> SendAsync(
        string method,
        string path,
        ReadOnlyMemory<byte> body,
        string? contentType,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var headers = new List<HeaderField>
        {
            new() { Name = ":method", Value = method },
            new() { Name = ":scheme", Value = "https" },
            new() { Name = ":path", Value = path },
            new() { Name = ":authority", Value = _authority }
        };
        if (!body.IsEmpty)
        {
            headers.Add(new HeaderField { Name = "content-length", Value = body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            headers.Add(new HeaderField { Name = "content-type", Value = contentType ?? "application/octet-stream" });
        }

        var stream = await _connection.CreateStreamAsync(headers, body.IsEmpty).WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!body.IsEmpty)
                await stream.WriteAsync(new ArraySegment<byte>(body.ToArray()), true).WaitAsync(cancellationToken).ConfigureAwait(false);
            return await ReadResponseAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            stream.Cancel();
            throw;
        }
        finally
        {
            stream.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            await _connection.GoAwayAsync(ErrorCode.NoError, true).ConfigureAwait(false);
        }
        catch
        {
            // The peer may have already closed a completed protocol session.
        }
    }

    public static void EnsureSuccess(PbsHttp2Response response, string operation)
    {
        if (response.StatusCode is >= 200 and < 300)
            return;
        var text = Encoding.UTF8.GetString(response.Body);
        if (text.Length > 2048)
            text = text[..2048];
        throw new InvalidDataException($"{operation} failed with HTTP {response.StatusCode}: {text}");
    }

    private async Task<PbsHttp2Response> ReadResponseAsync(IStream stream, CancellationToken cancellationToken)
    {
        var fields = (await stream.ReadHeadersAsync().WaitAsync(cancellationToken).ConfigureAwait(false)).ToArray();
        var statusHeader = fields.FirstOrDefault(x => x.Name == ":status");
        if (statusHeader.Name is null)
            throw new InvalidDataException("HTTP/2 response has no :status header.");
        var statusText = statusHeader.Value;
        if (!int.TryParse(statusText, out var status))
            throw new InvalidDataException($"HTTP/2 response status is invalid: {statusText}.");

        using var body = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(new ArraySegment<byte>(buffer)).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (read.BytesRead > 0)
                body.Write(buffer, 0, read.BytesRead);
            if (read.EndOfStream)
                break;
            if (read.BytesRead == 0)
                throw new EndOfStreamException("HTTP/2 stream returned no bytes without ending.");
        }

        var headers = fields
            .Where(x => !x.Name.StartsWith((char)58))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => string.Join(",", x.Select(y => y.Value)), StringComparer.OrdinalIgnoreCase);
        return new PbsHttp2Response(status, body.ToArray(), headers);
    }

    private static bool ValidateCertificate(X509Certificate? certificate, SslPolicyErrors errors, string expectedFingerprint)
    {
        if (certificate is null)
            return false;
        if (errors == SslPolicyErrors.None)
            return true;

        var expected = NormalizeFingerprint(expectedFingerprint);
        using var cert = new X509Certificate2(certificate);
        var actual = NormalizeFingerprint(cert.GetCertHashString(HashAlgorithmName.SHA256));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expected));
    }

    private static string NormalizeFingerprint(string fingerprint)
    {
        var normalized = fingerprint.Replace(":", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();
        if (normalized.Length != 64 || normalized.Any(x => !Uri.IsHexDigit(x)))
            throw new InvalidOperationException("PBS_TEST_FINGERPRINT must be a SHA-256 certificate fingerprint.");
        return normalized;
    }

    private static async Task<string> ReadHttp1HeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var one = new byte[1];
        while (buffer.Length < 64 * 1024)
        {
            var count = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (count == 0)
                throw new EndOfStreamException("TLS stream ended before the HTTP upgrade response was complete.");
            buffer.WriteByte(one[0]);
            if (buffer.Length >= 4)
            {
                var bytes = buffer.GetBuffer();
                var end = (int)buffer.Length;
                if (bytes[end - 4] == 13 && bytes[end - 3] == 10 && bytes[end - 2] == 13 && bytes[end - 1] == 10)
                    return Encoding.ASCII.GetString(bytes, 0, end);
            }
        }
        throw new InvalidDataException("HTTP upgrade response header exceeds 64 KiB.");
    }

    private static void ValidateUpgradeResponse(string response, string protocol)
    {
        var lines = response.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || !lines[0].StartsWith("HTTP/1.1 101 ", StringComparison.Ordinal))
            throw new InvalidDataException($"PBS protocol upgrade failed: {lines.FirstOrDefault() ?? "empty response"}.");
        var fields = lines.Skip(1)
            .Select(x => x.Split((char)58, 2))
            .Where(x => x.Length == 2)
            .ToDictionary(x => x[0].Trim(), x => x[1].Trim(), StringComparer.OrdinalIgnoreCase);
        if (!fields.TryGetValue("Upgrade", out var accepted) || !string.Equals(accepted, protocol, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"PBS protocol upgrade response did not accept {protocol}.");
    }

    private static void RejectHeaderInjection(string value, string parameterName)
    {
        if (value.Contains((char)13) || value.Contains((char)10))
            throw new ArgumentException("HTTP header value contains a line break.", parameterName);
    }
}
