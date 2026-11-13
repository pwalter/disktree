// SPIKE (not for merging): only compiled into DiskTree when publishing with -p:SpikeProbe=sftp|both.
namespace DiskTree.Spike;

internal static class SftpProbe
{
    public static object Create() => new Renci.SshNet.SftpClient("127.0.0.1", 22, "user", "pw");
}
