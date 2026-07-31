using System.Text.Json;
using ProxmoxBackupServerProtocolSpike;

using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
try
{
    var options = PbsConnectionOptions.FromEnvironment();
    var evidence = await new PbsProtocolSpike().RunAsync(options, timeout.Token);
    Console.WriteLine(JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}
