using System.Runtime.InteropServices;
using System.Text;

namespace AnyDictation;

/// <summary>APIキーの保存先。実体は Windows 資格情報マネージャー。テストでは fake を使う。</summary>
public interface ICredentialStore
{
    string? Read(string target);
    void Write(string target, string secret);
    void Delete(string target);
}

public static class CredentialTargets
{
    /// <summary>プロファイルの <see cref="Profile.CredentialId"/> から資格情報の対象名を作る。</summary>
    public static string TargetFor(Guid credentialId) => $"AnyDictation/credential/{credentialId:D}";
}

/// <summary>APIキーを Windows 資格情報マネージャー(汎用資格情報)へ保存する。設定 JSON とログには書かない。</summary>
public sealed class WindowsCredentialStore : ICredentialStore
{
    const int CredTypeGeneric = 1;
    const int CredPersistLocalMachine = 2;
    const int ErrorNotFound = 1168;

    public string? Read(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var ptr))
        {
            int err = Marshal.GetLastWin32Error();
            if (err == ErrorNotFound) return null;
            throw new InvalidOperationException($"資格情報マネージャーから読み取れません(Win32 エラー {err})。");
        }
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero) return "";
            return Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public void Write(string target, string secret)
    {
        var blob = Encoding.Unicode.GetBytes(secret);
        var handle = GCHandle.Alloc(blob, GCHandleType.Pinned);
        try
        {
            var cred = new CREDENTIAL
            {
                Type = CredTypeGeneric,
                TargetName = target,
                UserName = "AnyDictation",
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = handle.AddrOfPinnedObject(),
                Persist = CredPersistLocalMachine,
            };
            if (!CredWrite(ref cred, 0))
                throw new InvalidOperationException($"資格情報マネージャーへ保存できません(Win32 エラー {Marshal.GetLastWin32Error()})。");
        }
        finally
        {
            Array.Clear(blob);
            handle.Free();
        }
    }

    public void Delete(string target)
    {
        if (!CredDelete(target, CredTypeGeneric, 0) && Marshal.GetLastWin32Error() != ErrorNotFound)
            throw new InvalidOperationException($"資格情報を削除できません(Win32 エラー {Marshal.GetLastWin32Error()})。");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    static extern void CredFree(IntPtr buffer);
}
