using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AnyDictation.ViewModels;
using static AnyDictation.App.AppLog;

namespace AnyDictation.App;

internal enum SettingsTab { Profile, Microphone, History, General, Help }

/// <summary>プロファイル一覧の 1 行。IsActive は使用中、NeedsInput は入力が足りず下書きのまま保存していない。</summary>
internal sealed record ProfileRow(Profile Profile, string Name, string Summary, bool IsActive, bool NeedsInput);

/// <summary>
/// プロファイル(接続先とAPIキー)、マイク、履歴、一般、使い方の画面。閉じるとトレイへ隠れる。
/// 変更はすぐに保存する(「保存」ボタンはない)。入力欄は抜けたとき(または Enter)、選択や追加・削除は操作した時点で保存する。
/// 画面は編集中の下書き(_draft)を持ち、保存してよい部分だけを書き込む(AutoSave.Plan)。
/// </summary>
internal partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow, IUserDialogs
{
    static readonly KeyValuePair<string, string>[] Shortcuts =
    [
        new("Ctrl + Win", "押して離すと録音開始。もう一度で停止して文字起こし"),
        new("Ctrl + Win 長押し", "0.5 秒以上押している間だけ録音"),
        new("Esc", "録音中に押すと取り消し"),
        new("Enter", "録音中に押すと停止して文字起こし。貼り付け成功時のみ Enter も送る"),
        new("Shift + Alt + Z", "履歴の直近の結果をもう一度貼り付け"),
        new("最大 5 分", "録音は 5 分で自動的に停止"),
    ];

    readonly JsonFileStore<AppSettings> _settings;
    readonly ICredentialStore _creds;
    readonly HistoryViewModel _historyView;
    readonly DictationController _controller;
    readonly MicrophoneTester _microphoneTester = new();
    readonly DispatcherTimer _microphoneTick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    string? _draftMicrophoneId;
    readonly Stopwatch _microphoneTestElapsed = new();

    List<Profile> _draft = new();
    Guid? _draftActive;
    // 保存待ちのキー操作。プロファイルが正しい内容で保存されるときに一緒に書く(AutoSavePlan.Applied)
    readonly Dictionary<Guid, string> _newKeys = new();
    readonly HashSet<Guid> _keyDeletes = new();
    Profile? _current;
    bool _loading;
    bool _persisting;

    public bool AllowClose { get; set; }
    public event Action? UpdateRequested;

    public SettingsWindow(JsonFileStore<AppSettings> settings, ICredentialStore creds,
        JsonFileStore<HistoryData> historyStore, HistoryLog history, DictationController controller)
    {
        _settings = settings;
        _creds = creds;
        _controller = controller;
        InitializeComponent();
        _historyView = new HistoryViewModel(history, historyStore, this);
        HistoryTab.DataContext = _historyView;
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
        ShortcutList.ItemsSource = Shortcuts;
        ThemeBox.Items.Add(new ComboBoxItem { Content = "Windows に合わせる", Tag = ThemePreference.System });
        ThemeBox.Items.Add(new ComboBoxItem { Content = "ライト", Tag = ThemePreference.Light });
        ThemeBox.Items.Add(new ComboBoxItem { Content = "ダーク", Tag = ThemePreference.Dark });
        PathText.Text = $"設定: {AppPaths.SettingsFile}\n履歴: {AppPaths.HistoryFile}\nログ: {AppPaths.LogFile}\n(APIキーは Windows 資格情報マネージャーの「AnyDictation/credential/…」に保存されます)";
        Reload();
        if (E2eMode.Enabled)
        {
            StartupBox.IsEnabled = false;
            PathText.Text = $"E2E テスト専用: {AppPaths.DataDir}（資格情報はメモリのみ）";
        }
    }

    /// <summary>設定画面を前面に出す。入力が足りず保存していない下書きは、アプリを終了するまで残す。</summary>
    public void Open(SettingsTab tab = SettingsTab.Profile)
    {
        if (!IsVisible)
        {
            _historyView.Refresh();
            StartupBox.IsChecked = StartupRegistration.IsEnabled();
        }
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
        Persist(); // 入力中の欄も確定して保存する
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide(); // 閉じるとトレイへ
        }
        base.OnClosing(e);
    }

    // ---- 読み込み ----

    /// <summary>保存済みの設定から画面を作り直す。起動時と、壊れたファイルを退避した後に呼ぶ。</summary>
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
            _historyView.Refresh();
            StartupBox.IsChecked = StartupRegistration.IsEnabled();
            ThemeBox.SelectedItem = ThemeBox.Items.Cast<ComboBoxItem>().First(i => (ThemePreference)i.Tag == _settings.Value.Theme);
        }
        finally
        {
            _loading = false;
        }
        ShowProfile(_draft.FirstOrDefault());
        SelectInList(_current);
    }

    void OnEndpointEdited(object sender, RoutedEventArgs e) => UpdateEndpointPlaceholder();

    void UpdateEndpointPlaceholder()
    {
        EndpointPlaceholder.Visibility = EndpointBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (ProviderBox.SelectedItem is ComboBoxItem { Tag: ProviderKind kind }) EndpointPlaceholder.Text = Profile.EndpointPlaceholder(kind);
    }

    void RefreshBanners()
    {
        SettingsCorruptBanner.Visibility = _settings.IsCorrupt ? Visibility.Visible : Visibility.Collapsed;
        SettingsCorruptText.Text = $"設定ファイルを読み込めませんでした: {_settings.CorruptReason}\n" +
            $"ファイルは上書きせず保持しています({AppPaths.SettingsFile})。内容を確認するか、下のボタンで別名へ退避して初期化してください。退避するまで設定は変更できません。";
        // 壊れたファイルは上書きしないため、保存につながる操作を止める
        bool editable = !_settings.IsCorrupt;
        AddButtons.IsEnabled = MicrophoneBox.IsEnabled = ThemeBox.IsEnabled = editable;
    }

    void RefreshProfileList(Profile? select)
    {
        bool was = _loading;
        _loading = true;
        ProfileList.ItemsSource = _draft.Select(p => new ProfileRow(p, p.Name, Summarize(p),
            p.Id == _settings.Value.ActiveProfileId, ProfileValidator.Validate(p).Count > 0)).ToList();
        _loading = was;
        SelectInList(select);
    }

    /// <summary>一覧の 2 行目。「モデル · 言語 · サービス」</summary>
    static string Summarize(Profile p) => string.Join(" · ", new[]
    {
        p.Model,
        p.Language.Length > 0 ? p.Language : "言語自動",
        p.Provider switch
        {
            ProviderKind.AzureMai => "Azure Speech",
            ProviderKind.OpenAiCompatible => "OpenAI 互換",
            ProviderKind.AzureOpenAi => "Azure OpenAI",
            _ => "Azure OpenAI Live · 録音中から送信",
        },
    }.Where(s => s.Length > 0));

    void SelectInList(Profile? p)
    {
        bool was = _loading;
        _loading = true;
        ProfileList.SelectedItem = ProfileList.Items.Cast<ProfileRow>().FirstOrDefault(r => r.Profile == p);
        _loading = was;
    }

    // ---- 即時保存 ----

    /// <summary>
    /// 入力中の欄を下書きへ反映し、保存してよい部分を書き込む。変更がなければ何もしない。
    /// 不正な欄や足りない欄は、該当する入力欄の下に示す(その下書きは直るまで保存しない)。
    /// </summary>
    SaveResult? Persist()
    {
        if (_loading || _persisting || _settings.IsCorrupt) return null;
        _persisting = true;
        try
        {
            CommitForm();
            var plan = AutoSave.Plan(_settings.Value, _draft, _draftActive, _draftMicrophoneId);
            var newKeys = _newKeys.Where(k => plan.Applied.Contains(k.Key)).ToDictionary(k => k.Key, k => k.Value);
            var keyDeletes = _keyDeletes.Where(plan.Applied.Contains).ToList();
            SaveResult? result = null;
            if (newKeys.Count > 0 || keyDeletes.Count > 0 || !SameContent(plan.Candidate, _settings.Value))
            {
                // キーは新しい資格情報 ID へ書かれ、JSON の保存成功で参照が切り替わる。失敗しても設定とキーは保存前の組のまま
                result = SettingsCommit.Commit(_settings, _creds, plan.Candidate, newKeys, keyDeletes);
                if (result.Value.Status == SaveStatus.Failed) Log.SettingsSaveFailed();
                else
                {
                    if (result.Value.Status == SaveStatus.SavedWithLeftovers) Log.SettingsSavedWithLeftovers();
                    foreach (var id in newKeys.Keys) _newKeys.Remove(id);
                    foreach (var id in keyDeletes) _keyDeletes.Remove(id);
                    // 下書きの資格情報の参照を、保存済みの値にそろえる(次の比較と保存で古い参照を使わない)
                    foreach (var saved in _settings.Value.Profiles)
                        if (_draft.FirstOrDefault(d => d.Id == saved.Id) is { } d) d.CredentialId = saved.CredentialId;
                }
            }
            ShowSaveResult(result);
            RefreshProfileList(_current);
            UpdateActiveText();
            UpdateKeyState();
            ShowFieldErrors();
            return result;
        }
        finally
        {
            _persisting = false;
        }
    }

    static bool SameContent(AppSettings a, AppSettings b) => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);

    /// <summary>保存の失敗と、保存はできたが古いキーを消せなかったときだけ示す。成功は表示しない。</summary>
    void ShowSaveResult(SaveResult? result)
    {
        if (result is not { } r || r.Status == SaveStatus.Saved)
        {
            SaveResultText.Text = "";
            return;
        }
        SaveResultText.SetResourceReference(ForegroundProperty, r.Status == SaveStatus.Failed ? "ErrorText" : "WarningText");
        SaveResultText.Text = r.Message;
    }

    void ShowFieldErrors()
    {
        var errors = _current == null ? [] : ProfileValidator.ValidateFields(_current);
        string For(ProfileField field) => string.Join("\n", errors.Where(e => e.Field == field).Select(e => e.Message));
        NameError.Text = For(ProfileField.Name);
        EndpointError.Text = For(ProfileField.Endpoint);
        ModelError.Text = For(ProfileField.Model);
        LanguageError.Text = For(ProfileField.Language);
        LiveRateError.Text = For(ProfileField.LiveRate);
    }

    void OnFormFocusLeft(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.OriginalSource is TextBox or PasswordBox) Persist();
    }

    void OnFormKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.OriginalSource is TextBox or PasswordBox) Persist();
    }

    // ---- プロファイル編集 ----

    void OnProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var selected = (ProfileList.SelectedItem as ProfileRow)?.Profile; // 保存で一覧を作り直す前に、選んだ行を控える
        Persist();
        ShowProfile(selected);
        SelectInList(selected);
    }

    void ShowProfile(Profile? p)
    {
        _loading = true;
        _current = p;
        Form.IsEnabled = p != null && !_settings.IsCorrupt;
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
        ShowFieldErrors();
        _loading = false;
    }

    /// <summary>このプロファイルが使用中か、入力が足りず保存していないかを示す。</summary>
    void UpdateActiveText()
    {
        if (_current == null)
        {
            ActiveText.Text = "";
            return;
        }
        bool valid = ProfileValidator.Validate(_current).Count == 0;
        bool saved = _settings.Value.Profiles.Any(p => p.Id == _current.Id);
        var active = _settings.Value.ActiveProfile;
        string key;
        if (!valid)
        {
            ActiveText.Text = saved
                ? "入力に誤りがあります。直すまでは保存済みの内容を使います。"
                : "入力が必要な項目があります。そろうと保存します。";
            key = "WarningText";
        }
        else if (active?.Id == _current.Id)
        {
            ActiveText.Text = "このプロファイルを使用中です。";
            key = "SuccessText";
        }
        else
        {
            ActiveText.Text = active != null ? $"使用中のプロファイル: 「{active.Name}」" : "使用中のプロファイルはありません。";
            key = "TextSecondary";
        }
        ActiveText.SetResourceReference(ForegroundProperty, key);
        UseButton.IsEnabled = valid && active?.Id != _current.Id;
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
        // 保存待ちのキー操作は、プロファイルの入力がそろって保存されるときに反映する
        if (_newKeys.ContainsKey(id)) { KeyStateText.Text = "新しいキーを入力済みです(入力がそろうと保存します)"; return; }
        if (_keyDeletes.Contains(id)) { KeyStateText.Text = "キーの削除を予約済みです(入力がそろうと反映します)"; return; }
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
        Persist();
        var p = Profile.CreateDefault(kind);
        string baseName = p.Name;
        for (int i = 2; _draft.Any(x => x.Name == p.Name); i++) p.Name = $"{baseName} {i}";
        _draft.Add(p);
        _draftActive ??= p.Id; // 使用中がなければ、入力がそろって保存された時点で使用中にする
        ShowProfile(p);
        Persist(); // 既定値だけで足りるサービス(OpenAI 互換)はすぐ保存される
        SelectInList(p);
    }

    void OnAddAzure(object s, RoutedEventArgs e) => AddProfile(ProviderKind.AzureMai);
    void OnAddOpenAi(object s, RoutedEventArgs e) => AddProfile(ProviderKind.OpenAiCompatible);
    void OnAddAzureOpenAi(object s, RoutedEventArgs e) => AddProfile(ProviderKind.AzureOpenAi);
    void OnAddAzureOpenAiLive(object s, RoutedEventArgs e) => AddProfile(ProviderKind.AzureOpenAiLive);

    void OnProviderChanged(object s, SelectionChangedEventArgs e)
    {
        var kind = (ProviderBox.SelectedItem as ComboBoxItem)?.Tag as ProviderKind?;
        var visibility = kind == ProviderKind.AzureOpenAiLive ? Visibility.Visible : Visibility.Collapsed;
        LiveNote.Visibility = LiveCostForm.Visibility = visibility;
        ModelLabel.Text = kind is ProviderKind.AzureOpenAi or ProviderKind.AzureOpenAiLive ? "デプロイ名" : "モデル";
        UpdateEndpointPlaceholder();
        Persist();
    }

    void OnDeleteProfile(object s, RoutedEventArgs e)
    {
        if (_current == null) return;
        if (MessageBox.Show(this, $"プロファイル「{_current.Name}」と保存済みのAPIキーを削除します。元には戻せません。よろしいですか?",
                "Any Dictation", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        var id = _current.Id;
        _draft.Remove(_current);
        _newKeys.Remove(id);
        _keyDeletes.Remove(id);
        if (_draftActive == id) _draftActive = null;
        _current = null;
        Persist();
        ShowProfile(_draft.FirstOrDefault());
        SelectInList(_current);
    }

    void OnDeleteKey(object s, RoutedEventArgs e)
    {
        if (_current == null) return;
        if (MessageBox.Show(this, $"プロファイル「{_current.Name}」の保存済みのAPIキーを削除します。元には戻せません。よろしいですか?",
                "Any Dictation", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        KeyBox.Clear();
        _newKeys.Remove(_current.Id);
        _keyDeletes.Add(_current.Id);
        Persist();
    }

    void OnUseProfile(object s, RoutedEventArgs e)
    {
        if (_current == null) return;
        _draftActive = _current.Id;
        Persist();
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

    // ---- 確認と通知(IUserDialogs) ----

    bool IUserDialogs.Confirm(string message) =>
        MessageBox.Show(this, message, "Any Dictation", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;

    void IUserDialogs.ShowMessage(string message) => MessageBox.Show(this, message, "Any Dictation");

    void IUserDialogs.ShowError(string message) =>
        MessageBox.Show(this, message, "Any Dictation", MessageBoxButton.OK, MessageBoxImage.Error);

    // E2E ではクリップボードに触れず、失敗の通知も出さない
    Task<bool> IUserDialogs.CopyToClipboardAsync(string text) =>
        E2eMode.Enabled ? Task.FromResult(true) : ClipboardHelper.SetTextAsync(text);

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
                MicrophoneTestText.Text = "保存したマイクが利用できません。接続を確認して一覧を再読み込みするか、別のマイクを選んでください。";
            }
            MicrophoneBox.SelectedItem = selected;
        }
        catch (Exception e)
        {
            MicrophoneTestText.Text = "マイク一覧を取得できません: " + e.Message;
            // 取得失敗時も保存済みの選択を維持する。既定マイクへの暗黙の切替えは行わない。
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
        var previous = _draftMicrophoneId;
        _draftMicrophoneId = (MicrophoneBox.SelectedItem as ComboBoxItem)?.Tag as string;
        var result = Persist();
        if (result is { Status: SaveStatus.Failed } failed)
        {
            // 保存できなければ選択を戻す(表示と保存済みの値を食い違わせない)
            _draftMicrophoneId = previous;
            RefreshMicrophones();
            MicrophoneTestText.Text = failed.Message;
            return;
        }
        MicrophoneTestText.Text = "選択を保存しました。次の録音から使います。";
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
        MicrophoneLevel.Push(peak is { } p ? Math.Sqrt(p / 32768d) : 0);
        MicrophoneDbText.Text = peak is > 0 ? $"{20 * Math.Log10(peak.Value / 32768d):0} dB" : "– dB";
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
            MicrophoneLevel.Clear();
            MicrophoneDbText.Text = "– dB";
            MicrophoneTestText.Text = message;
        }
    }

    // ---- 履歴 ----

    /// <summary>行の「コピー」。その行を選んでから、既存の「選択した履歴をコピー」と同じ処理をする。</summary>
    void OnCopyHistoryRow(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HistoryRow row) return;
        _historyView.SelectedRow = row;
        _historyView.CopySelectedCommand.Execute(null);
    }

    // ---- 一般 ----

    /// <summary>テーマは選んだ時点で反映して保存する。保存するのは保存済みの設定のテーマだけで、入力が足りない下書きは含めない。</summary>
    void OnThemeSelected(object s, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeBox.SelectedItem is not ComboBoxItem { Tag: ThemePreference theme } || theme == _settings.Value.Theme) return;
        try
        {
            _settings.Save(_settings.Value.WithTheme(theme));
            ThemeResultText.Text = "";
            AppTheme.Apply(theme);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _loading = true;
            ThemeBox.SelectedItem = ThemeBox.Items.Cast<ComboBoxItem>().First(i => (ThemePreference)i.Tag == _settings.Value.Theme);
            _loading = false;
            ThemeResultText.Text = "テーマを保存できませんでした: " + ex.Message;
        }
    }

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
