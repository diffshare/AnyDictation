using System.Text.Json;
using Xunit;

namespace AnyDictation.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AnyDictationTests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose() => Directory.Delete(Path, recursive: true); // 自分で作った正確なパスだけ消す
}

/// <summary>既定プロファイルの空の Azure エンドポイントを、検証を通る架空の URL で補う。</summary>
public static class TestProfiles
{
    public static Profile Create(ProviderKind kind)
    {
        var p = Profile.CreateDefault(kind);
        if (p.Endpoint.Length == 0)
            p.Endpoint = kind == ProviderKind.AzureMai ? "https://example.cognitiveservices.azure.com/" : "https://example.openai.azure.com/";
        return p;
    }
}

public class ProfileValidationTests
{
    [Theory]
    [InlineData("https://example.cognitiveservices.azure.com/", true)]
    [InlineData("https://api.openai.com/v1", true)]
    [InlineData("http://localhost:8080/v1", true)]
    [InlineData("http://127.0.0.1:11434/v1", true)]
    [InlineData("http://[::1]:8000/v1", true)]
    [InlineData("http://api.openai.com/v1", false)]
    [InlineData("http://192.168.1.10/v1", false)]
    [InlineData("ftp://localhost/v1", false)]
    [InlineData("https://user:pass@example.com/v1", false)]
    [InlineData("https://example.com/v1?key=abc", false)]
    [InlineData("https://example.com/v1#x", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    public void エンドポイントはHTTPSかloopbackのHTTPだけ(string endpoint, bool expected)
    {
        Assert.Equal(expected, ProfileValidator.IsAllowedEndpoint(endpoint, out _));
    }

    [Theory]
    [InlineData("ja", true)]
    [InlineData("en-US", true)]
    [InlineData("", true)]
    [InlineData("japanese!", false)]
    [InlineData("j", false)]
    public void 言語コードを検証する(string lang, bool ok)
    {
        var p = TestProfiles.Create(ProviderKind.AzureMai);
        p.Language = lang;
        Assert.Equal(ok, ProfileValidator.Validate(p).Count == 0);
    }

    [Fact]
    public void 既定プロファイルは仕様の値を持つ()
    {
        var az = Profile.CreateDefault(ProviderKind.AzureMai);
        Assert.Equal("", az.Endpoint); // 利用者のリソースを入力するまで保存できない
        Assert.Equal("MAI-Transcribe-2", az.Model);
        Assert.Equal("ja", az.Language);
        Assert.Single(ProfileValidator.Validate(az));
        var oa = Profile.CreateDefault(ProviderKind.OpenAiCompatible);
        Assert.Equal("https://api.openai.com/v1", oa.Endpoint);
        Assert.Empty(ProfileValidator.Validate(oa));
    }

    [Theory]
    [InlineData(ProviderKind.AzureMai, "https://<resource>.cognitiveservices.azure.com/")]
    [InlineData(ProviderKind.AzureOpenAi, "https://<resource>.openai.azure.com/")]
    [InlineData(ProviderKind.AzureOpenAiLive, "https://<resource>.openai.azure.com/")]
    [InlineData(ProviderKind.OpenAiCompatible, "https://api.openai.com/v1")]
    public void エンドポイントの入力例はサービスごとに異なり_保存できる値ではない(ProviderKind kind, string expected)
    {
        Assert.Equal(expected, Profile.EndpointPlaceholder(kind));
        if (kind != ProviderKind.OpenAiCompatible) Assert.False(ProfileValidator.IsAllowedEndpoint(expected, out _));
    }

    [Fact]
    public void 空の名前とモデルはエラー()
    {
        var p = TestProfiles.Create(ProviderKind.AzureMai);
        p.Name = " "; p.Model = "";
        Assert.Equal(2, ProfileValidator.Validate(p).Count);
    }

    [Fact]
    public void 設定の整合性を検証する()
    {
        var p = TestProfiles.Create(ProviderKind.AzureMai);
        var ok = new AppSettings { Profiles = { p }, ActiveProfileId = p.Id };
        Assert.Empty(AppSettings.Validate(ok));
        Assert.NotEmpty(AppSettings.Validate(new AppSettings { Profiles = { p }, ActiveProfileId = Guid.NewGuid() }));
        Assert.NotEmpty(AppSettings.Validate(new AppSettings { Profiles = { p, p } }));
        Assert.NotEmpty(AppSettings.Validate(new AppSettings { Version = 99 }));
    }
}

public class JsonFileStoreTests
{
    static JsonFileStore<AppSettings> Settings(string path) => new(path, AppSettings.Validate);

    [Fact]
    public void 保存した設定を読み戻せてAPIキー相当の項目を持たない()
    {
        using var dir = new TempDir();
        var store = Settings(dir.File("settings.json"));
        var p = TestProfiles.Create(ProviderKind.OpenAiCompatible);
        store.Value.Profiles.Add(p);
        store.Value.ActiveProfileId = p.Id;
        store.Save();

        var json = File.ReadAllText(dir.File("settings.json"));
        Assert.DoesNotContain("key", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"OpenAiCompatible\"", json);

        var reloaded = Settings(dir.File("settings.json"));
        reloaded.Load();
        Assert.False(reloaded.IsCorrupt);
        Assert.Equal(p.Id, reloaded.Value.ActiveProfile!.Id);
        Assert.Equal(ProviderKind.OpenAiCompatible, reloaded.Value.ActiveProfile!.Provider);
    }

    [Fact]
    public void テーマ項目のない既存の設定はWindowsに合わせるとして読む()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), "{\"Version\":1,\"Profiles\":[]}");
        var store = Settings(dir.File("settings.json"));
        store.Load();
        Assert.False(store.IsCorrupt);
        Assert.Equal(ThemePreference.System, store.Value.Theme);
    }

    [Fact]
    public void テーマを保存して読み戻せる()
    {
        using var dir = new TempDir();
        var store = Settings(dir.File("settings.json"));
        store.Save(new AppSettings { Theme = ThemePreference.Dark });
        Assert.Contains("\"Theme\": \"Dark\"", File.ReadAllText(dir.File("settings.json")));
        var reloaded = Settings(dir.File("settings.json"));
        reloaded.Load();
        Assert.Equal(ThemePreference.Dark, reloaded.Value.Theme);
    }

    [Theory]
    [InlineData("{\"Version\":1,\"Theme\":\"Sepia\"}")]
    [InlineData("{\"Version\":1,\"Theme\":7}")]
    public void 不正なテーマは壊れた設定として検出する(string content)
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), content);
        var store = Settings(dir.File("settings.json"));
        store.Load();
        Assert.True(store.IsCorrupt);
    }

    [Fact]
    public void WithThemeはテーマだけを変えた別の複製を返す()
    {
        var p = TestProfiles.Create(ProviderKind.AzureMai);
        var saved = new AppSettings { Profiles = { p }, ActiveProfileId = p.Id, MicrophoneDeviceId = "mic" };
        var changed = saved.WithTheme(ThemePreference.Light);
        Assert.Equal(ThemePreference.Light, changed.Theme);
        Assert.Equal(ThemePreference.System, saved.Theme);
        Assert.Equal(p.Id, changed.ActiveProfileId);
        Assert.Equal("mic", changed.MicrophoneDeviceId);
        Assert.Equal(p.Id, Assert.Single(changed.Profiles).Id);
        Assert.NotSame(p, changed.Profiles[0]); // 保存の失敗で元の設定を壊さない
    }

    [Fact]
    public void ファイルが無ければ空の設定で正常()
    {
        using var dir = new TempDir();
        var store = Settings(dir.File("settings.json"));
        store.Load();
        Assert.False(store.IsCorrupt);
        Assert.Empty(store.Value.Profiles);
    }

    [Theory]
    [InlineData("{ broken json")]
    [InlineData("null")]
    [InlineData("{\"Version\":99}")]
    [InlineData("{\"Version\":1,\"Profiles\":[{\"Name\":\"x\",\"Provider\":\"AzureMai\",\"Endpoint\":\"http://evil.example/\",\"Model\":\"m\"}]}")]
    public void 壊れた設定は検出され保存で上書きされない(string content)
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), content);
        var store = Settings(dir.File("settings.json"));
        store.Load();

        Assert.True(store.IsCorrupt);
        Assert.Throws<InvalidOperationException>(() => store.Save());
        Assert.Equal(content, File.ReadAllText(dir.File("settings.json")));
    }

    [Theory]
    [InlineData("{\"Version\":1,\"Profiles\":null}")]
    [InlineData("{\"Version\":1,\"Profiles\":[null]}")]
    [InlineData("[null]")]
    [InlineData("{\"Version\":1,\"Profiles\":[{\"Name\":null,\"Provider\":\"AzureMai\",\"Endpoint\":null,\"Model\":null,\"Language\":null}]}")]
    [InlineData("{\"Version\":1,\"Profiles\":{}}")]
    public void 有効なJSONでもnullや型違いを含む設定は壊れたデータとして保護される(string content)
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), content);
        var store = Settings(dir.File("settings.json"));
        store.Load();

        Assert.True(store.IsCorrupt);
        Assert.Throws<InvalidOperationException>(() => store.Save());
        Assert.Equal(content, File.ReadAllText(dir.File("settings.json")));
    }

    [Theory]
    [InlineData("{\"Version\":1,\"Entries\":null}")]
    [InlineData("{\"Version\":1,\"Entries\":[null]}")]
    [InlineData("{\"Version\":1,\"Entries\":[{\"Time\":\"2026-10-02T00:00:00\",\"ProfileName\":null,\"Text\":null}]}")]
    [InlineData("[null]")]
    public void nullを含む履歴ファイルは壊れたデータとして保護される(string content)
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("history.json"), content);
        var store = new JsonFileStore<HistoryData>(dir.File("history.json"), HistoryData.Validate);
        store.Load();

        Assert.True(store.IsCorrupt);
        Assert.Throws<InvalidOperationException>(() => new HistoryLog(store).Add("x", "p"));
        Assert.Equal(content, File.ReadAllText(dir.File("history.json")));
    }

    [Fact]
    public void 退避すると元の内容が別名に残り空の状態から保存できる()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), "{ broken");
        var store = Settings(dir.File("settings.json"));
        store.Load();

        var moved = store.QuarantineCorruptFile();

        Assert.Equal("{ broken", File.ReadAllText(moved));
        Assert.False(File.Exists(dir.File("settings.json")));
        Assert.False(store.IsCorrupt);
        store.Save();
        Assert.True(File.Exists(dir.File("settings.json")));
    }

    [Fact]
    public void 保存は一時ファイルを残さない()
    {
        using var dir = new TempDir();
        var store = Settings(dir.File("settings.json"));
        store.Save();
        Assert.Equal(new[] { "settings.json" }, Directory.GetFiles(dir.Path).Select(Path.GetFileName).ToArray());
    }
}

public class HistoryTests
{
    static (HistoryLog log, JsonFileStore<HistoryData> store) Create(string path)
    {
        var store = new JsonFileStore<HistoryData>(path, HistoryData.Validate);
        store.Load();
        return (new HistoryLog(store), store);
    }

    [Fact]
    public void 直近20件だけを新しい順に保持する()
    {
        using var dir = new TempDir();
        var (log, _) = Create(dir.File("history.json"));
        for (int i = 1; i <= 25; i++) log.Add($"text{i}", "p");

        Assert.Equal(20, log.Entries.Count);
        Assert.Equal("text25", log.Entries[0].Text);
        Assert.Equal("text6", log.Entries[^1].Text);
    }

    [Fact]
    public void 履歴は永続化され再読み込みできる()
    {
        using var dir = new TempDir();
        var (log, _) = Create(dir.File("history.json"));
        log.Add("こんにちは", "Azure MAI");

        var (log2, _) = Create(dir.File("history.json"));
        Assert.Equal("こんにちは", Assert.Single(log2.Entries).Text);
        Assert.Equal("Azure MAI", log2.Entries[0].ProfileName);
    }

    [Fact]
    public void 履歴ファイルは文章だけで音声データを含まない()
    {
        using var dir = new TempDir();
        var (log, _) = Create(dir.File("history.json"));
        log.Add("abc", "p");
        using var doc = JsonDocument.Parse(File.ReadAllText(dir.File("history.json")));
        var names = doc.RootElement.GetProperty("Entries")[0].EnumerateObject().Select(x => x.Name).OrderBy(x => x);
        Assert.Equal(new[] { "ProfileName", "Text", "Time" }, names);
    }

    [Fact]
    public void 一括削除で永続化も空になる()
    {
        using var dir = new TempDir();
        var (log, _) = Create(dir.File("history.json"));
        log.Add("a", "p"); log.Add("b", "p");
        log.Clear();
        Assert.Empty(log.Entries);
        var (log2, _) = Create(dir.File("history.json"));
        Assert.Empty(log2.Entries);
    }

    [Fact]
    public void 壊れた履歴ファイルは上書きされない()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("history.json"), "garbage");
        var (log, store) = Create(dir.File("history.json"));
        Assert.True(store.IsCorrupt);
        Assert.Throws<InvalidOperationException>(() => log.Add("x", "p"));
        Assert.Equal("garbage", File.ReadAllText(dir.File("history.json")));
    }
}

public class CredentialStoreTests
{
    [Fact]
    public void 資格情報マネージャーへ保存と読み出しと削除ができる()
    {
        // テスト専用ターゲット名にダミー値だけを書き、最後に削除する。既存の資格情報は読まない。
        var store = new WindowsCredentialStore();
        var target = $"AnyDictation/test/{Guid.NewGuid():N}";
        try
        {
            Assert.Null(store.Read(target));
            store.Write(target, "dummy-secret-日本語");
            Assert.Equal("dummy-secret-日本語", store.Read(target));
            store.Delete(target);
            Assert.Null(store.Read(target));
        }
        finally
        {
            store.Delete(target);
        }
    }

    [Fact]
    public void 対象名は資格情報IDから決まる()
    {
        var id = Guid.NewGuid();
        Assert.Equal($"AnyDictation/credential/{id:D}", CredentialTargets.TargetFor(id));
    }
}

public class AutoSaveTests
{
    static AppSettings Saved(params Profile[] profiles) =>
        new() { Profiles = profiles.Select(p => p.Clone()).ToList(), ActiveProfileId = profiles.FirstOrDefault()?.Id, Theme = ThemePreference.Dark };

    [Fact]
    public void 正しい下書きはその内容で保存する()
    {
        var p = TestProfiles.Create(ProviderKind.AzureMai);
        var saved = Saved(p);
        var draft = p.Clone();
        draft.Model = "edited";
        var plan = AutoSave.Plan(saved, [draft], p.Id, "mic");
        Assert.Equal("edited", Assert.Single(plan.Candidate.Profiles).Model);
        Assert.Contains(p.Id, plan.Applied);
        Assert.Equal(p.Id, plan.Candidate.ActiveProfileId);
        Assert.Equal("mic", plan.Candidate.MicrophoneDeviceId);
        Assert.Equal(ThemePreference.Dark, plan.Candidate.Theme); // テーマは保存済みの値を引き継ぐ
        Assert.NotSame(draft, plan.Candidate.Profiles[0]);
    }

    [Fact]
    public void 保存済みのプロファイルを不正な値に変えても保存済みの内容を保ちキーは適用しない()
    {
        var p = TestProfiles.Create(ProviderKind.AzureMai);
        var saved = Saved(p);
        var draft = p.Clone();
        draft.Endpoint = "http://evil.example/";
        var plan = AutoSave.Plan(saved, [draft], p.Id, null);
        Assert.Equal(p.Endpoint, Assert.Single(plan.Candidate.Profiles).Endpoint);
        Assert.DoesNotContain(p.Id, plan.Applied);
        Assert.Equal(p.Id, plan.Candidate.ActiveProfileId);
    }

    [Fact]
    public void 必要な項目がそろっていない新しいプロファイルは書かない()
    {
        var p = TestProfiles.Create(ProviderKind.AzureMai);
        var added = Profile.CreateDefault(ProviderKind.AzureOpenAi); // エンドポイントが空
        var plan = AutoSave.Plan(Saved(p), [p.Clone(), added], added.Id, null);
        Assert.Equal(p.Id, Assert.Single(plan.Candidate.Profiles).Id);
        Assert.DoesNotContain(added.Id, plan.Applied);
        Assert.Null(plan.Candidate.ActiveProfileId); // 書かないプロファイルを使用先にしない
        Assert.Empty(AppSettings.Validate(plan.Candidate));
    }

    [Fact]
    public void 一覧から消した下書きは保存しない()
    {
        var a = TestProfiles.Create(ProviderKind.AzureMai);
        var b = TestProfiles.Create(ProviderKind.OpenAiCompatible);
        var plan = AutoSave.Plan(Saved(a, b), [b.Clone()], a.Id, null);
        Assert.Equal(b.Id, Assert.Single(plan.Candidate.Profiles).Id);
        Assert.Null(plan.Candidate.ActiveProfileId);
    }

    [Fact]
    public void 項目ごとの検証は該当する項目を返す()
    {
        var p = Profile.CreateDefault(ProviderKind.AzureOpenAiLive);
        p.Name = " ";
        p.Language = "日本語";
        p.LiveUsdPerMinute = -1;
        var fields = ProfileValidator.ValidateFields(p).Select(e => e.Field).ToList();
        Assert.Equal([ProfileField.Name, ProfileField.Endpoint, ProfileField.Language, ProfileField.LiveRate], fields);
        Assert.Equal(ProfileValidator.ValidateFields(p).Select(e => e.Message), ProfileValidator.Validate(p));
    }
}
