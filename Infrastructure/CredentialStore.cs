using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using DiskTree.Core.Storage;

namespace DiskTree.Infrastructure;

/// <summary>
/// Secrets of saved sources in the Windows Credential Manager ("Anmeldeinformationsverwaltung" → Windows-
/// Anmeldeinformationen → Generische Anmeldeinformationen) of the signed-in user: CredWrite / CredRead / CredDelete,
/// type GENERIC, persisted for this user on this machine (not roaming). Windows protects them with the user's logon
/// (DPAPI); nothing is written to DiskTree's own files. In portable mode the profiles travel with settings.json, the
/// secrets stay on the machine they were entered on.
/// </summary>
public sealed class CredentialStore : ISecretStore
{
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;
    private const int MaxBlobBytes = 5 * 512; // CRED_MAX_CREDENTIAL_BLOB_SIZE

    public static readonly CredentialStore Instance = new();

    public bool TryRead(string target, out string secret)
    {
        secret = "";
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var ptr))
        {
            int err = Marshal.GetLastWin32Error();
            if (err == ERROR_NOT_FOUND) return false;
            throw new Win32Exception(err, "Anmeldeinformation konnte nicht gelesen werden.");
        }
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero) return true;
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            secret = Encoding.Unicode.GetString(bytes);
            Array.Clear(bytes);
            return true;
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public void Write(string target, string userName, string secret)
    {
        var bytes = Encoding.Unicode.GetBytes(secret);
        if (bytes.Length > MaxBlobBytes)
            throw new ArgumentException("Das Geheimnis ist zu lang für die Windows-Anmeldeinformationsverwaltung.");
        var blob = Marshal.AllocHGlobal(Math.Max(1, bytes.Length));
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                Comment = "DiskTree – Zugangsdaten einer gespeicherten Quelle",
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = userName,
            };
            if (!CredWrite(ref cred, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Anmeldeinformation konnte nicht gespeichert werden.");
        }
        finally
        {
            // Do not leave the secret in unmanaged memory.
            for (int i = 0; i < bytes.Length; i++) Marshal.WriteByte(blob, i, 0);
            Marshal.FreeHGlobal(blob);
            Array.Clear(bytes);
        }
    }

    public void Delete(string target)
    {
        if (CredDelete(target, CRED_TYPE_GENERIC, 0)) return;
        int err = Marshal.GetLastWin32Error();
        if (err != ERROR_NOT_FOUND)
            throw new Win32Exception(err, "Anmeldeinformation konnte nicht gelöscht werden.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);
}
