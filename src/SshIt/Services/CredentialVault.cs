using System.Runtime.InteropServices;
using System.Text;

namespace SshIt.Services;

/// <summary>
/// Secrets live in the Windows Credential Manager under the current user,
/// never in a JSON file next to the sessions.
/// </summary>
public static class CredentialVault
{
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

    public static string SessionTarget(string sessionId) => "SSH-it/session/" + sessionId;
    public static string AskPassTarget(string sessionId) => "SSH-it/askpass/" + sessionId;

    /// <summary>Stores a secret for the ssh-askpass helper and returns the lookup id.</summary>
    public static string StoreAskPass(string sessionId, string secret)
    {
        Write(AskPassTarget(sessionId), sessionId, secret);
        return sessionId;
    }

    public static string? ReadAskPass(string sessionId) => Read(AskPassTarget(sessionId));

    public static bool SaveSessionSecret(string sessionId, string secret)
        => Write(SessionTarget(sessionId), "ssh", secret);

    public static string? GetSessionSecret(string sessionId) => Read(SessionTarget(sessionId));

    public static void DeleteSessionSecret(string sessionId) => Delete(SessionTarget(sessionId));

    public static void ClearAskPass(string sessionId) => Delete(AskPassTarget(sessionId));

    public static bool Write(string target, string userName, string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                UserName = userName,
                CredentialBlob = blob,
                CredentialBlobSize = (uint)bytes.Length,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                Comment = "SSH-it"
            };
            return CredWrite(ref credential, 0);
        }
        catch (Exception ex)
        {
            Log.Warn("CredWrite failed: " + ex.Message);
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public static string? Read(string target)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            if (!CredRead(target, CRED_TYPE_GENERIC, 0, out handle)) return null;
            var credential = Marshal.PtrToStructure<CREDENTIAL>(handle);
            if (credential.CredentialBlobSize == 0) return "";
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            Log.Warn("CredRead failed: " + ex.Message);
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero) CredFree(handle);
        }
    }

    public static bool Delete(string target)
    {
        try { return CredDelete(target, CRED_TYPE_GENERIC, 0); }
        catch { return false; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
