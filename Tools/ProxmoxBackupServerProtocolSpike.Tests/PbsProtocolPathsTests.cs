using ProxmoxBackupServerProtocolSpike;

namespace ProxmoxBackupServerProtocolSpike.Tests;

[TestFixture]
public sealed class PbsProtocolPathsTests
{
    [Test]
    public void UpgradePathEscapesSnapshotIdentityAndOptionalNamespace()
    {
        var snapshot = new PbsSnapshot("ci/store", "host", "Duplicati POC", 1_774_000_000, "team/a");

        var path = PbsProtocolPaths.BackupUpgrade(snapshot);

        Assert.That(path, Is.EqualTo(
            "/api2/json/backup?store=ci%2Fstore&backup-type=host&backup-id=Duplicati%20POC&backup-time=1774000000&ns=team%2Fa"));
    }

    [Test]
    public void DynamicIndexAppendSerializesListsAsJsonArrays()
    {
        var path = PbsProtocolPaths.DynamicIndexAppend(
            7,
            ["0123456789abcdef"],
            [40]);

        Assert.That(path, Is.EqualTo(
            "/dynamic_index?wid=7&digest-list=%5B%220123456789abcdef%22%5D&offset-list=%5B40%5D"));
    }
}
