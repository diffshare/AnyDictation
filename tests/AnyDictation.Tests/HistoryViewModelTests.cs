using AnyDictation.ViewModels;
using Xunit;

namespace AnyDictation.Tests;

sealed class FakeDialogs : IUserDialogs
{
    public bool ConfirmAnswer { get; set; } = true;
    public bool ClipboardSucceeds { get; set; } = true;
    public List<string> Confirms { get; } = new();
    public List<string> Messages { get; } = new();
    public List<string> Errors { get; } = new();
    public List<string> Copied { get; } = new();

    public bool Confirm(string message)
    {
        Confirms.Add(message);
        return ConfirmAnswer;
    }

    public void ShowMessage(string message) => Messages.Add(message);

    public void ShowError(string message) => Errors.Add(message);

    public Task<bool> CopyToClipboardAsync(string text)
    {
        Copied.Add(text);
        return Task.FromResult(ClipboardSucceeds);
    }
}

public class HistoryViewModelTests
{
    static (JsonFileStore<HistoryData> Store, HistoryLog Log) Open(TempDir dir)
    {
        var store = new JsonFileStore<HistoryData>(dir.File("history.json"), HistoryData.Validate);
        store.Load();
        return (store, new HistoryLog(store));
    }

    [Fact]
    public void 行は新しい順で改行を空白にし80文字で切る()
    {
        using var dir = new TempDir();
        var (store, log) = Open(dir);
        log.Add("古い", "A", new DateTime(2026, 1, 2, 3, 4, 5));
        log.Add("一行目\r\n" + new string('あ', 100), "B", new DateTime(2026, 1, 2, 3, 4, 6));

        var vm = new HistoryViewModel(log, store, new FakeDialogs());

        Assert.Equal(2, vm.Rows.Count);
        var newest = vm.Rows[0];
        Assert.Equal("2026-01-02 03:04:06", newest.TimeText);
        Assert.Equal("B", newest.ProfileName);
        Assert.Equal(81, newest.Preview.Length);
        Assert.StartsWith("一行目  あ", newest.Preview);
        Assert.EndsWith("…", newest.Preview);
        Assert.Equal("一行目\r\n" + new string('あ', 100), newest.Full);
        Assert.Equal("古い", vm.Rows[1].Preview);
    }

    [Fact]
    public async Task 選択した行の全文をコピーし失敗したら通知する()
    {
        using var dir = new TempDir();
        var (store, log) = Open(dir);
        log.Add("全文\nです", "A");
        var dialogs = new FakeDialogs();
        var vm = new HistoryViewModel(log, store, dialogs);

        Assert.False(vm.CopySelectedCommand.CanExecute(null)); // 未選択ではコピーできない
        vm.SelectedRow = vm.Rows[0];
        Assert.True(vm.CopySelectedCommand.CanExecute(null));

        await vm.CopySelectedCommand.ExecuteAsync(null);
        Assert.Equal(["全文\nです"], dialogs.Copied);
        Assert.Empty(dialogs.Messages);

        dialogs.ClipboardSucceeds = false;
        await vm.CopySelectedCommand.ExecuteAsync(null);
        Assert.Equal(["クリップボードへ書き込めませんでした。"], dialogs.Messages);
    }

    [Fact]
    public void すべて削除は確認でキャンセルすると何もしない()
    {
        using var dir = new TempDir();
        var (store, log) = Open(dir);
        log.Add("残る", "A");
        var dialogs = new FakeDialogs { ConfirmAnswer = false };
        var vm = new HistoryViewModel(log, store, dialogs);

        vm.ClearAllCommand.Execute(null);

        Assert.Single(dialogs.Confirms);
        Assert.Single(vm.Rows);
        Assert.Single(log.Entries);
    }

    [Fact]
    public void すべて削除はファイルへすぐ反映し一覧を更新する()
    {
        using var dir = new TempDir();
        var (store, log) = Open(dir);
        log.Add("消える", "A");
        var vm = new HistoryViewModel(log, store, new FakeDialogs());

        vm.ClearAllCommand.Execute(null);

        Assert.Empty(vm.Rows);
        var reloaded = new JsonFileStore<HistoryData>(store.Path, HistoryData.Validate);
        reloaded.Load();
        Assert.Empty(reloaded.Value.Entries);
    }

    [Fact]
    public void 壊れた履歴では削除に失敗したことを通知する()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("history.json"), "{ 壊れている");
        var (store, log) = Open(dir);
        var dialogs = new FakeDialogs();
        var vm = new HistoryViewModel(log, store, dialogs);

        Assert.True(vm.IsCorrupt);
        Assert.Contains(store.Path, vm.CorruptMessage);

        vm.ClearAllCommand.Execute(null);

        Assert.Single(dialogs.Errors);
        Assert.StartsWith("削除できませんでした: ", dialogs.Errors[0]);
        Assert.True(File.Exists(store.Path)); // 壊れたファイルは上書きしない
    }

    [Fact]
    public void 退避すると壊れた表示が消え保存できるようになる()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("history.json"), "{ 壊れている");
        var (store, log) = Open(dir);
        var dialogs = new FakeDialogs();
        var vm = new HistoryViewModel(log, store, dialogs);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.QuarantineCorruptFileCommand.Execute(null);

        Assert.False(vm.IsCorrupt);
        Assert.Contains(nameof(HistoryViewModel.IsCorrupt), changed);
        Assert.Single(dialogs.Messages);
        Assert.StartsWith("壊れた履歴ファイルを退避しました:", dialogs.Messages[0]);
        Assert.False(File.Exists(store.Path));
        Assert.Single(Directory.GetFiles(dir.Path, "history.json.corrupt-*"));

        log.Add("退避後", "A");
        vm.Refresh();
        Assert.Single(vm.Rows);
    }
}
