using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnyDictation.ViewModels;

/// <summary>履歴一覧の 1 行。Preview は改行を空白にして 80 文字で切る。</summary>
public sealed record HistoryRow(string TimeText, string ProfileName, string Preview, string Full)
{
    const int PreviewLength = 80;

    public static HistoryRow From(HistoryEntry h)
    {
        var flat = h.Text.Replace('\r', ' ').Replace('\n', ' ');
        return new HistoryRow(h.Time.ToString("yyyy-MM-dd HH:mm:ss"), h.ProfileName,
            flat.Length > PreviewLength ? flat[..PreviewLength] + "…" : flat, h.Text);
    }
}

/// <summary>設定画面の履歴タブ。履歴の操作は「保存」を待たず、すぐファイルへ反映する。</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    readonly HistoryLog _history;
    readonly JsonFileStore<HistoryData> _store;
    readonly IUserDialogs _dialogs;

    public HistoryViewModel(HistoryLog history, JsonFileStore<HistoryData> store, IUserDialogs dialogs)
    {
        _history = history;
        _store = store;
        _dialogs = dialogs;
        Refresh();
    }

    [ObservableProperty]
    public partial IReadOnlyList<HistoryRow> Rows { get; private set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopySelectedCommand))]
    public partial HistoryRow? SelectedRow { get; set; }

    [ObservableProperty]
    public partial bool IsCorrupt { get; private set; }

    [ObservableProperty]
    public partial string CorruptMessage { get; private set; } = "";

    public void Refresh()
    {
        Rows = _history.Entries.Select(HistoryRow.From).ToList();
        IsCorrupt = _store.IsCorrupt;
        CorruptMessage = $"履歴ファイルを読み込めませんでした: {_store.CorruptReason}\n" +
            $"ファイルは上書きせず保持しています({_store.Path})。退避するまで履歴は保存されません。";
    }

    bool HasSelection() => SelectedRow != null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    async Task CopySelectedAsync()
    {
        if (SelectedRow is { } row && !await _dialogs.CopyToClipboardAsync(row.Full))
            _dialogs.ShowMessage("クリップボードへ書き込めませんでした。");
    }

    [RelayCommand]
    void ClearAll()
    {
        if (!_dialogs.Confirm("履歴をすべて削除します。よろしいですか?")) return;
        try
        {
            _history.Clear();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("削除できませんでした: " + ex.Message);
        }
        Refresh();
    }

    [RelayCommand]
    void QuarantineCorruptFile()
    {
        try
        {
            var moved = _store.QuarantineCorruptFile();
            _dialogs.ShowMessage($"壊れた履歴ファイルを退避しました:\n{moved}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("退避できませんでした: " + ex.Message);
        }
        Refresh();
    }
}
