using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Security.Credentials;

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
public sealed unsafe class WindowsCredentialStore : ICredentialStore
{
    const int ErrorNotFound = 1168;

    public string? Read(string target)
    {
        CREDENTIALW* cred;
        fixed (char* name = target)
        {
            if (!PInvoke.CredRead(name, CRED_TYPE.CRED_TYPE_GENERIC, 0, &cred))
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ErrorNotFound) return null;
                throw new InvalidOperationException($"資格情報マネージャーから読み取れません(Win32 エラー {err})。");
            }
        }
        try
        {
            if (cred->CredentialBlobSize == 0 || cred->CredentialBlob == null) return "";
            return new string((char*)cred->CredentialBlob, 0, (int)cred->CredentialBlobSize / 2);
        }
        finally
        {
            PInvoke.CredFree(cred);
        }
    }

    public void Write(string target, string secret)
    {
        var blob = Encoding.Unicode.GetBytes(secret);
        try
        {
            fixed (char* name = target)
            fixed (char* user = "AnyDictation")
            fixed (byte* data = blob)
            {
                var cred = new CREDENTIALW
                {
                    Type = CRED_TYPE.CRED_TYPE_GENERIC,
                    TargetName = name,
                    UserName = user,
                    CredentialBlobSize = (uint)blob.Length,
                    CredentialBlob = data,
                    Persist = CRED_PERSIST.CRED_PERSIST_LOCAL_MACHINE,
                };
                if (!PInvoke.CredWrite(&cred, 0))
                    throw new InvalidOperationException($"資格情報マネージャーへ保存できません(Win32 エラー {Marshal.GetLastWin32Error()})。");
            }
        }
        finally
        {
            Array.Clear(blob);
        }
    }

    public void Delete(string target)
    {
        fixed (char* name = target)
        {
            if (!PInvoke.CredDelete(name, CRED_TYPE.CRED_TYPE_GENERIC, 0) && Marshal.GetLastWin32Error() != ErrorNotFound)
                throw new InvalidOperationException($"資格情報を削除できません(Win32 エラー {Marshal.GetLastWin32Error()})。");
        }
    }
}
