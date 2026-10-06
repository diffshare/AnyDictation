using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AnyDictation.App;

internal enum SettingsTab { Profile, Microphone, History, General, Help }

/// <summary>プロファイル(接続先とAPIキー)、マイク、履歴、一般、使い方の画面。閉じるとトレイへ隠れる。</summary>
internal partial class SettingsWindow : Window
{
    sealed record HistoryRow(string TimeText, string ProfileName, string Preview, string Full);

    readonly JsonFileStore<AppSettings> _settings;
    readonly JsonFileStore<HistoryData> _historyStore;
    readonly HistoryLog _history;
    readonly ICredentialStore _creds;
    readonly DictationController _controller;
    readonly MicrophoneTester _microphoneTester = new();
    readonly DispatcherTimer _microphoneTick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    string? _draftMicrophoneId;
    readonly Stopwatch _microphoneTestElapsed = new();

    List<Profile> _draft = new();
    Guid? _draftActive;
    readonly Dictionary<Guid, string> _newKeys = new();
    readonly HashSet<Guid> _keyDeletes = new();
    Profile? _current;
    bool _loading;
    bool _dirty;

    public bool AllowClose { get; set; }
    public event Action? UpdateRequested;

    public SettingsWindow(JsonFileStore<AppSettings> settings, ICredentialStore creds,
        JsonFileStore<HistoryData> historyStore, HistoryLog history, DictationController controller)
    {
        _settings = settings;
        _creds = creds;
        _historyStore = historyStore;
        _history = history;
        _controller = controller;
        InitializeComponent();
        if (E2eMode.Enabled) ShowActivated = false;
        _microphoneTick.Tick += (_, _) => UpdateMicrophoneTest();
        _microphoneTester.Failed += (session, message) => Dispatcher.BeginInvoke(() =>
        {
            if (_microphoneTester.IsCurrent(session)) StopMicrophoneTest(message);
        });
        ProviderBox.Items.Add(new ComboBoxItem { Content = "Azure Speech(MAI-Transcribe)", Tag = ProviderKind.AzureMai });
        ProviderBox.Items.Add(new ComboBoxItem { Content = "OpenAI / OpenAI 互換", Tag = ProviderKind.OpenAiCompatible });
        ProviderBox.Items.Add(new ComboBoxItem { Content = "Azure OpenAI", Tag = ProviderKind.AzureOpenAi });
        ProviderBox.Items.Add(new ComboBoxItem { Content = "Azure OpenAI Live(録音中に送信)", Tag = ProviderKind.AzureOpenAiLive });
        PathText.Text = $"設定: {AppPaths.SettingsFile}\n履歴: {AppPaths.HistoryFile}\nログ: {AppPaths.LogFile}\n(APIキーは Windows 資格情報マネージャーの「AnyDictation/credential/…」に保存されます)";
        Reload();
        if (E2eMode.Enabled)
        {
            StartupBox.IsEnabled = false;
            PathText.Text = $"E2E テスト専用: {AppPaths.DataDir}（資格情報はメモリのみ）";
        }
    }

    /// <summary>設定画面を前面に出す。</summary>
    public void Open(SettingsTab tab = SettingsTab.Profile)
    {
        if (!IsVisible) Reload();
        LastLiveCostText.Text = _controller.LiveCostText is { Length: > 0 } cost ? cost : "まだ Live を使用していません。";
        Tabs.SelectedItem = tab switch
        {
            SettingsTab.Profile => ProfileTab,
            SettingsTab.Microphone => MicrophoneTab,
            SettingsTab.History => HistoryTab,
            SettingsTab.General => GeneralTab,
            _ => HelpTab,
        };
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!E2eMode.Enabled) Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        StopMicrophoneTest();
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide(); // 閉じるとトレイへ
        }
        base.OnClosing(e);
    }

    // ---- 読み込み ----

    void Reload()
    {
        _loading = true;
        try
        {
            _draft = _settings.Value.Profiles.Select(p => p.Clone()).ToList();
            _draftActive = _settings.Value.ActiveProfileId;
            _draftMicrophoneId = _settings.Value.MicrophoneDeviceId;
            RefreshMicrophones();
            _newKeys.Clear();
            _keyDeletes.Clear();
            _current = null;
            SaveResultText.Text = "";
            RefreshBanners();
            RefreshProfileList(null);
            RefreshHistory();
            StartupBox.IsChecked = StartupRegistration.IsEnabled();
        }
        finally
        {
            _loading = false;
        }
        ShowProfile(_draft.FirstOrDefault());
        SelectInList(_current);
        _dirty = false;
        DirtyText.Visibility = Visibility.Collapsed;
    }

    /// <summary>利用者の編集操作で呼ぶ。読み込み中の画面更新では何もしない。</summary>
    void MarkDirty()
    {
        if (_loading || _dirty) return;
        _dirty = true;
        SaveResultText.Text = ""; // 直前の保存結果は、これ以降の編集には当てはまらない
        DirtyText.Visibility = Visibility.Visible;
    }

    void OnFormEdited(object sender, RoutedEventArgs e) => MarkDirty();

    void OnEndpointEdited(object sender, RoutedEventArgs e)
    {
        UpdateEndpointPlaceholder();
        MarkDirty();
    }

    void UpdateEndpointPlaceholder()
    {
        EndpointPlaceholder.Visibility = EndpointBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (ProviderBox.SelectedItem is ComboBoxItem { Tag: ProviderKind kind }) EndpointPlaceholder.Text = Profile.EndpointPlaceholder(kind);
    }

    void RefreshBanners()
    {
        SettingsCorruptBanner.Visibility = _settings.IsCorrupt ? Visibility.Visible : Visibility.Collapsed;
        SettingsCorruptText.Text = $"設定ファイルを読み込めませんでした: {_settings.CorruptReason}\n" +
            $"ファイルは上書きせず保持しています({AppPaths.SettingsFile})。内容を確認するか、下のボタンで別名へ退避して初期化してください。退避するまで保存はできません。";
        SaveButton.IsEnabled = !_settings.IsCorrupt;
        HistoryCorruptBanner.Visibility = _historyStore.IsCorrupt ? Visibility.Visible : Visibility.Collapsed;
        HistoryCorruptText.Text = $"履歴ファイルを読み込めませんでした: {_historyStore.CorruptReason}\n" +
            $"ファイルは上書きせず保持しています({AppPaths.HistoryFile})。退避するまで履歴は保存されません。";
    }

    void RefreshProfileList(Profile? select)
    {
        bool was = _loading;
        _loading = true;
        ProfileList.Items.Clear();
        foreach (var p in _draft)
            ProfileList.Items.Add(new ListBoxItem { Content = (p.Id == _draftActive ? "● " : "　") + p.Name, Tag = p });
        _loading = was;
        SelectInList(select);
    }

    void SelectInList(Profile? p)
    {
        bool was = _loading;
        _loading = true;
        ProfileList.SelectedItem = ProfileList.Items.Cast<ListBoxItem>().FirstOrDefault(i => i.Tag == p);
        _loading = was;
    }

    // ---- プロファイル編集 ----

    void OnProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        CommitForm();
        var selected = (ProfileList.SelectedItem as ListBoxItem)?.Tag as Profile;
        ShowProfile(selected);
    }

    void ShowProfile(Profile? p)
    {
        _loading = true;
        _current = p;
        Form.IsEnabled = p != null;
        if (p != null)
        {
            NameBox.Text = p.Name;
            foreach (ComboBoxItem item in ProviderBox.Items)
                if ((ProviderKind)item.Tag == p.Provider) ProviderBox.SelectedItem = item;
            EndpointBox.Text = p.Endpoint;
            ModelBox.Text = p.Model;
            LanguageBox.Text = p.Language;
            LiveRateBox.Text = p.LiveUsdPerMinute?.ToString(CultureInfo.InvariantCulture) ?? "";
            KeyBox.Clear();
            UpdateKeyState();
        }
        else
        {
            NameBox.Text = EndpointBox.Text = ModelBox.Text = LanguageBox.Text = LiveRateBox.Text = "";
            KeyBox.Clear();
            KeyStateText.Text = "";
        }
        UpdateActiveText();
        _loading = false;
    }

    /// <summary>保存済みの使用先と、保存後の使用先(編集中の _draftActive)を並べて示す。</summary>
    void UpdateActiveText()
    {
        if (_current == null)
        {
            ActiveText.Text = "";
            return;
        }
        var saved = _settings.Value.ActiveProfileId;
        string Describe(Guid? id, IEnumerable<Profile> source) =>
            id == null ? "なし"
            : id == _current.Id ? "このプロファイル"
            : source.FirstOrDefault(p => p.Id == id) is { } p ? $"「{p.Name}」" : "不明";
        string savedText = Describe(saved, _settings.Value.Profiles) +
            (saved != null && _draft.All(p => p.Id != saved) ? "(削除予定)" : "");
        string afterText = Describe(_draftActive, _draft);
        bool changed = saved != _draftActive;
        ActiveText.Text = $"現在の使用先(保存済み): {savedText}\n保存後の使用先: {afterText}" + (changed ? "" : "(変更なし)");
        ActiveText.Foreground = changed ? System.Windows.Media.Brushes.DarkOrange
            : _draftActive == _current.Id ? System.Windows.Media.Brushes.DarkGreen : System.Windows.Media.Brushes.Gray;
        UseButton.IsEnabled = _draftActive != _current.Id;
    }

    void CommitForm()
    {
        if (_current == null) return;
        _current.Name = NameBox.Text.Trim();
        if (ProviderBox.SelectedItem is ComboBoxItem { Tag: ProviderKind kind }) _current.Provider = kind;
        _current.Endpoint = EndpointBox.Text.Trim();
        _current.Model = ModelBox.Text.Trim();
        _current.Language = LanguageBox.Text.Trim();
        _current.LiveUsdPerMinute = string.IsNullOrWhiteSpace(LiveRateBox.Text) ? null :
            decimal.TryParse(LiveRateBox.Text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rate) ? rate : -1;
        if (KeyBox.Password.Length > 0)
        {
            _newKeys[_current.Id] = KeyBox.Password.Trim();
            _keyDeletes.Remove(_current.Id);
            KeyBox.Clear();
        }
    }

    void UpdateKeyState()
    {
        if (_current == null) return;
        var id = _current.Id;
        if (_newKeys.ContainsKey(id)) { KeyStateText.Text = "新しいキーを入力済み(保存で反映されます)"; return; }
        if (_keyDeletes.Contains(id)) { KeyStateText.Text = "キーを削除予定(保存で反映されます)"; return; }
        try
        {
            KeyStateText.Text = _current.CredentialId is { } credId && _creds.Read(CredentialTargets.TargetFor(credId)) != null
                ? "キーは保存済みです。変更する場合だけ入力してください。"
                : "キーは未設定です。";
        }
        catch (InvalidOperationException e)
        {
            KeyStateText.Text = e.Message;
        }
    }

    void AddProfile(ProviderKind kind)
    {
        CommitForm();
        var p = Profile.CreateDefault(kind);
        string baseName = p.Name;
        for (int i = 2; _draft.Any(x => x.Name == p.Name); i++) p.Name = $"{baseName} {i}";
        _draft.Add(p);
        _draftActive ??= p.Id;
        RefreshProfileList(p);
        ShowProfile(p);
        MarkDirty();
    }

    void OnAddAzure(object s, RoutedEventArgs e) => AddProfile(ProviderKind.AzureMai);
    void OnAddOpenAi(object s, RoutedEventArgs e) => AddProfile(ProviderKind.OpenAiCompatible);
    void OnAddAzureOpenAi(object s, RoutedEventArgs e) => AddProfile(ProviderKind.AzureOpenAi);
    void OnAddAzureOpenAiLive(object s, RoutedEventArgs e) => AddProfile(ProviderKind.AzureOpenAiLive);

    void OnProviderChanged(object s, SelectionChangedEventArgs e)
    {
        var visibility = ProviderBox.SelectedItem is ComboBoxItem { Tag: ProviderKind.AzureOpenAiLive } ? Visibility.Visible : Visibility.Collapsed;
        LiveNote.Visibility = LiveCostForm.Visibility = visibility;
        UpdateEndpointPlaceholder();
        MarkDirty();
    }

    void OnDeleteProfile(object s, RoutedEventArgs e)
    {
        if (_current == null) return;
        if (MessageBox.Show(this, $"プロファイル「{_current.Name}」と保存済みのAPIキーを削除します(「保存」で確定)。よろしいですか?",
                "Any Dictation", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        var id = _current.Id;
        _draft.Remove(_current);
        _newKeys.Remove(id);
        _keyDeletes.Remove(id);
        if (_draftActive == id) _draftActive = null;
        _current = null;
        RefreshProfileList(null);
        ShowProfile(_draft.FirstOrDefault());
        SelectInList(_current);
        MarkDirty();
    }

    void OnDeleteKey(object s, RoutedEventArgs e)
    {
        if (_current == null) return;
        KeyBox.Clear();
        _newKeys.Remove(_current.Id);
        _keyDeletes.Add(_current.Id);
        UpdateKeyState();
        MarkDirty();
    }

    void OnUseProfile(object s, RoutedEventArgs e)
    {
        if (_current == null) return;
        CommitForm();
        _draftActive = _current.Id;
        var cur = _current;
        RefreshProfileList(cur);
        ShowProfile(cur);
        MarkDirty();
    }

    void OnSave(object s, RoutedEventArgs e)
    {
        StopMicrophoneTest();
        CommitForm();
        var candidate = new AppSettings { Profiles = _draft, ActiveProfileId = _draftActive, MicrophoneDeviceId = _draftMicrophoneId };
        var errors = AppSettings.Validate(candidate);
        if (errors.Count > 0)
        {
            SaveResultText.Foreground = System.Windows.Media.Brushes.Firebrick;
            SaveResultText.Text = string.Join("\n", errors);
            return;
        }
        // 候補は複製を渡す。キーは新しい資格情報 ID へ書かれ、JSON の保存成功で参照が切り替わる。
        // 失敗しても設定と各プロファイルのキーは保存前の組のまま(入力中の内容はこの画面に残る)
        var committed = new AppSettings { Profiles = _draft.Select(p => p.Clone()).ToList(), ActiveProfileId = _draftActive, MicrophoneDeviceId = _draftMicrophoneId };
        var result = SettingsCommit.Commit(_settings, _creds, committed, _newKeys, _keyDeletes);
        if (result.Status == SaveStatus.Failed)
        {
            Log.Write("settings save failed");
            SaveResultText.Foreground = System.Windows.Media.Brushes.Firebrick;
            SaveResultText.Text = result.Message;
            return;
        }
        if (result.Status == SaveStatus.SavedWithLeftovers) Log.Write("settings saved; obsolete credentials left");

        // 保存済みの内容(新しい資格情報 ID を含む)から画面を読み直し、編集中のプロファイルを選び直す
        var curId = _current?.Id;
        Reload();
        var again = _draft.FirstOrDefault(p => p.Id == curId);
        if (again != null)
        {
            RefreshProfileList(again);
            ShowProfile(again);
        }
        SaveResultText.Foreground = result.Status == SaveStatus.Saved ? System.Windows.Media.Brushes.DarkGreen : System.Windows.Media.Brushes.DarkOrange;
        SaveResultText.Text = result.Status == SaveStatus.Saved && _draftActive == null && _draft.Count > 0
            ? "保存しました。ただし使用するプロファイルが選択されていません。"
            : result.Message;
    }

    void OnQuarantineSettings(object s, RoutedEventArgs e)
    {
        try
        {
            var moved = _settings.QuarantineCorruptFile();
            MessageBox.Show(this, $"壊れた設定ファイルを退避しました:\n{moved}", "Any Dictation");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "退避できませんでした: " + ex.Message, "Any Dictation", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        Reload();
    }

    // ---- 履歴 ----

    public void RefreshHistory()
    {
        HistoryList.ItemsSource = _history.Entries
            .Select(h => new HistoryRow(h.Time.ToString("yyyy-MM-dd HH:mm:ss"), h.ProfileName,
                h.Text.Replace('\r', ' ').Replace('\n', ' ') is { Length: > 80 } t ? t[..80] + "…" : h.Text.Replace('\r', ' ').Replace('\n', ' '),
                h.Text))
            .ToList();
        RefreshBanners();
    }

    async void OnCopyHistory(object s, RoutedEventArgs e)
    {
        if (E2eMode.Enabled) return;
        if (HistoryList.SelectedItem is HistoryRow row && !await ClipboardHelper.SetTextAsync(row.Full))
            MessageBox.Show(this, "クリップボードへ書き込めませんでした。", "Any Dictation");
    }

    void OnClearHistory(object s, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "履歴をすべて削除します。よろしいですか?", "Any Dictation",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try
        {
            _history.Clear();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "削除できませんでした: " + ex.Message, "Any Dictation", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        RefreshHistory();
    }

    void OnQuarantineHistory(object s, RoutedEventArgs e)
    {
        try
        {
            var moved = _historyStore.QuarantineCorruptFile();
            MessageBox.Show(this, $"壊れた履歴ファイルを退避しました:\n{moved}", "Any Dictation");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "退避できませんでした: " + ex.Message, "Any Dictation", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        RefreshHistory();
    }

    // ---- マイク ----

    void RefreshMicrophones()
    {
        StopMicrophoneTest();
        bool loading = _loading;
        _loading = true;
        try
        {
            MicrophoneBox.Items.Clear();
            var defaultItem = new ComboBoxItem { Content = "Windows の既定のマイク", Tag = null };
            MicrophoneBox.Items.Add(defaultItem);
            ComboBoxItem? selected = _draftMicrophoneId == null ? defaultItem : null;
            var devices = E2eMode.Enabled ? Array.Empty<MicrophoneDevice>() : Microphones.List();
            if (!E2eMode.Enabled) defaultItem.Content = MicrophoneSelection.DescribeDefault(Microphones.ReadDefaultNumber(), devices); // 表示だけ。保存値は null のまま既定に追随する
            foreach (var d in devices)
            {
                bool identifiable = !string.IsNullOrEmpty(d.Id) && devices.Count(x => string.Equals(x.Id, d.Id, StringComparison.OrdinalIgnoreCase)) == 1;
                var item = new ComboBoxItem { Content = MicrophoneSelection.Display(d) + (identifiable ? "" : "（識別できないため選択不可）"), Tag = d.Id, IsEnabled = identifiable };
                MicrophoneBox.Items.Add(item);
                if (identifiable && _draftMicrophoneId != null && string.Equals(_draftMicrophoneId, d.Id, StringComparison.OrdinalIgnoreCase)) selected = item;
            }
            if (selected == null)
            {
                selected = new ComboBoxItem { Content = "保存したマイク（未接続または識別できません）", Tag = _draftMicrophoneId, IsEnabled = false };
                MicrophoneBox.Items.Add(selected);
                MicrophoneTestText.Text = "保存したマイクが利用できません。接続を確認して一覧を再読み込みするか、別のマイクを選んで保存してください。";
            }
            MicrophoneBox.SelectedItem = selected;
        }
        catch (Exception e)
        {
            MicrophoneTestText.Text = "マイク一覧を取得できません: " + e.Message;
            // 取得失敗時も未保存の選択を維持する。既定マイクへの暗黙の切替えは行わない。
            var retained = new ComboBoxItem { Content = "現在の選択（一覧取得失敗）", Tag = _draftMicrophoneId };
            MicrophoneBox.Items.Add(retained);
            MicrophoneBox.SelectedItem = retained;
        }
        finally { _loading = loading; }
    }

    void OnRefreshMicrophones(object sender, RoutedEventArgs e) => RefreshMicrophones();

    void OnMicrophoneSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        StopMicrophoneTest();
        _draftMicrophoneId = (MicrophoneBox.SelectedItem as ComboBoxItem)?.Tag as string;
        MarkDirty();
        MicrophoneTestText.Text = "選択を変更しました。入力テストは未保存の選択を使います。本録音に使うには、下の「保存」を押してください。";
    }

    void OnBeginMicrophoneTest(object sender, RoutedEventArgs e)
    {
        if (!_controller.TryBeginMicrophoneTest())
        {
            MicrophoneTestText.Text = "録音・認識・再送待ち、または入力テスト中は開始できません。処理を終えてから試してください。";
            return;
        }
        try
        {
            _microphoneTester.Start(_draftMicrophoneId);
            _microphoneTestElapsed.Restart();
            MicrophoneStartButton.IsEnabled = false;
            MicrophoneStopButton.IsEnabled = true;
            MicrophoneTestText.Text = "マイクの入力を待っています。話しかけてください。15 秒で自動停止します。";
            _microphoneTick.Start();
        }
        catch (Exception ex)
        {
            StopMicrophoneTest("入力テストを開始できません: " + ex.Message + " マイクの接続と Windows のマイク権限を確認してください。");
        }
    }

    void OnStopMicrophoneTest(object sender, RoutedEventArgs e) => StopMicrophoneTest();

    void UpdateMicrophoneTest()
    {
        var elapsed = _microphoneTestElapsed.Elapsed;
        if (elapsed >= TimeSpan.FromSeconds(15))
        {
            StopMicrophoneTest("15 秒の入力テストが終了しました。音声は保存・送信していません。");
            return;
        }
        int? peak = _microphoneTester.ReadPeak();
        MicrophoneLevel.Value = peak is { } p ? Math.Sqrt(p / 32768d) * 100 : 0;
        string state = peak == null ? "マイクの入力待ち" : peak >= SilenceDetector.DefaultThreshold ? "入力あり" : peak > 0 ? "入力が小さい" : "無音";
        MicrophoneTestText.Text = $"{state}（あと {Math.Ceiling(15 - elapsed.TotalSeconds):0} 秒）";
    }

    void StopMicrophoneTest(string message = "入力テストは停止しています。音声は保存・送信しません。")
    {
        _microphoneTick.Stop();
        _microphoneTestElapsed.Stop();
        try { _microphoneTester.Stop(); }
        catch (Exception e) { message += " マイクの解放時にエラー: " + e.Message; }
        finally
        {
            _controller.EndMicrophoneTest();
            MicrophoneStartButton.IsEnabled = !E2eMode.Enabled;
            MicrophoneStopButton.IsEnabled = false;
            MicrophoneLevel.Value = 0;
            MicrophoneTestText.Text = message;
        }
    }

    // ---- 一般 ----

    void OnStartupClicked(object s, RoutedEventArgs e)
    {
        bool want = StartupBox.IsChecked == true;
        try
        {
            StartupRegistration.Set(want);
            StartupResultText.Text = "";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            StartupBox.IsChecked = !want;
            StartupResultText.Text = "自動起動の設定を変更できませんでした: " + ex.Message;
        }
    }

    public void ShowUpdateState(string text, bool canApply)
    {
        UpdateText.Text = text;
        UpdateButton.Visibility = canApply ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnUpdateClicked(object s, RoutedEventArgs e) => UpdateRequested?.Invoke();

    void OnOpenDataFolder(object s, RoutedEventArgs e)
    {
        if (E2eMode.Enabled) return;
        System.IO.Directory.CreateDirectory(AppPaths.DataDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDir}\"") { UseShellExecute = true });
    }
}
