// SPIKE (not for merging): only compiled into DiskTree when publishing with -p:SpikeProbe=s3|both|s3sso.
// Creates a real client so the package is actually used (DiskTree does not trim anyway).
namespace DiskTree.Spike;

internal static class S3Probe
{
    public static object Create() =>
        new Amazon.S3.AmazonS3Client(
            new Amazon.Runtime.BasicAWSCredentials("x", "y"),
            new Amazon.S3.AmazonS3Config { ServiceURL = "http://127.0.0.1:9000", ForcePathStyle = true });
}
