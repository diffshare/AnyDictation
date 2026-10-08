namespace AnyDictation.ViewModels;

/// <summary>ViewModel から利用者への確認・通知・クリップボードを頼むための窓口。実装は画面側に置き、テストでは fake に替える。</summary>
public interface IUserDialogs
{
    /// <summary>警告付きで OK / キャンセルを尋ね、OK なら true。</summary>
    bool Confirm(string message);

    void ShowMessage(string message);

    void ShowError(string message);

    /// <summary>クリップボードへ書き込み、成功したら true。</summary>
    Task<bool> CopyToClipboardAsync(string text);
}
