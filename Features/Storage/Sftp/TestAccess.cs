using System.Runtime.CompilerServices;

// The SFTP tests check the error mapping and the connection limits of the internal SftpConnection directly.
[assembly: InternalsVisibleTo("DiskTree.Tests")]
