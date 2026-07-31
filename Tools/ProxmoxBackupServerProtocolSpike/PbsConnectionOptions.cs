namespace ProxmoxBackupServerProtocolSpike;

public sealed record PbsConnectionOptions(
    Uri Server,
    string AuthenticationId,
    string TokenSecret,
    string CertificateFingerprint,
    PbsSnapshot Snapshot,
    string ArchiveName)
{
    public static PbsConnectionOptions FromEnvironment()
    {
        var serverText = Required("PBS_TEST_SERVER");
        if (!Uri.TryCreate(serverText, UriKind.Absolute, out var server) || server.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("PBS_TEST_SERVER must be an absolute https URL.");
        if (!string.IsNullOrEmpty(server.AbsolutePath.Trim((char)47)) || !string.IsNullOrEmpty(server.Query))
            throw new InvalidOperationException("PBS_TEST_SERVER must not contain a path or query.");

        var backupTime = Environment.GetEnvironmentVariable("PBS_TEST_BACKUP_TIME") is { Length: > 0 } value
            ? long.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var backupId = Environment.GetEnvironmentVariable("PBS_TEST_BACKUP_ID") is { Length: > 0 } configuredId
            ? configuredId
            : $"duplicati-poc-{Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

        return new PbsConnectionOptions(
            server,
            Required("PBS_TEST_AUTH_ID"),
            Required("PBS_TEST_TOKEN_SECRET"),
            Required("PBS_TEST_FINGERPRINT"),
            new PbsSnapshot(
                Required("PBS_TEST_DATASTORE"),
                "host",
                backupId,
                backupTime,
                Environment.GetEnvironmentVariable("PBS_TEST_NAMESPACE")),
            "duplicati-poc.didx");
    }

    private static string Required(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Required environment variable {name} is missing.");
}
