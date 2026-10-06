using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AnyDictation;

/// <summary>値は設定 JSON に文字列で保存される。既存の名前は変えず、追加は末尾へ。</summary>
public enum ProviderKind { AzureMai, OpenAiCompatible, AzureOpenAi, AzureOpenAiLive }

/// <summary>認識サービスへの接続先。APIキーは含めない(Windows 資格情報マネージャーに別保存)。</summary>
public sealed class Profile
{
    public const string AzureDefaultModel = "MAI-Transcribe-2";
    public const string OpenAiDefaultEndpoint = "https://api.openai.com/v1";
    public const string OpenAiDefaultModel = "gpt-4o-transcribe";

    public const string AzureOpenAiDefaultModel = "gpt-transcribe";
    public const string AzureOpenAiLiveDefaultModel = "gpt-live-transcribe";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public ProviderKind Provider { get; set; }
    public string Endpoint { get; set; } = "";
    public string Model { get; set; } = "";
    public string Language { get; set; } = "ja";
    public decimal? LiveUsdPerMinute { get; set; }

    /// <summary>
    /// このプロファイルのAPIキーを指す資格情報の ID(秘密ではない)。キーを変更するたびに新しい ID へ書き、
    /// 設定 JSON の保存成功でこの参照が切り替わる。null はキー未設定。
    /// </summary>
    public Guid? CredentialId { get; set; }

    /// <summary>Azure の接続先は利用者ごとのリソースなので既定値を持たず、空欄で作る。</summary>
    public static Profile CreateDefault(ProviderKind kind) => kind switch
    {
        ProviderKind.AzureMai => new Profile { Name = "Azure MAI", Provider = kind, Model = AzureDefaultModel },
        ProviderKind.OpenAiCompatible => new Profile { Name = "OpenAI", Provider = kind, Endpoint = OpenAiDefaultEndpoint, Model = OpenAiDefaultModel },
        ProviderKind.AzureOpenAi => new Profile { Name = "Azure OpenAI", Provider = kind, Model = AzureOpenAiDefaultModel },
        ProviderKind.AzureOpenAiLive => new Profile { Name = "Azure OpenAI Live", Provider = kind, Model = AzureOpenAiLiveDefaultModel },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>エンドポイント欄が空のときに表示する入力例。値としては保存しない。</summary>
    public static string EndpointPlaceholder(ProviderKind kind) => kind switch
    {
        ProviderKind.AzureMai => "https://<resource>.cognitiveservices.azure.com/",
        ProviderKind.AzureOpenAi or ProviderKind.AzureOpenAiLive => "https://<resource>.openai.azure.com/",
        _ => OpenAiDefaultEndpoint,
    };

    public Profile Clone() => (Profile)MemberwiseClone();
}

public static class ProfileValidator
{
    static readonly Regex LanguagePattern = new("^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$", RegexOptions.Compiled);

    /// <summary>HTTPS、または loopback(localhost / 127.x / ::1)に限り HTTP を許可する。</summary>
    public static bool IsAllowedEndpoint(string? endpoint, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var u)) return false;
        if (u.UserInfo.Length > 0 || u.Query.Length > 0 || u.Fragment.Length > 0) return false;
        bool ok = u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback);
        if (ok) uri = u;
        return ok;
    }

    public static List<string> Validate(Profile p)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(p.Name)) errors.Add("プロファイル名が空です。");
        if (!Enum.IsDefined(p.Provider)) errors.Add("サービス種別が不正です。");
        if (!IsAllowedEndpoint(p.Endpoint, out _))
            errors.Add("エンドポイントは https:// の URL にしてください(http:// は localhost / 127.0.0.1 / [::1] のみ可。認証情報・クエリ・フラグメントは不可)。");
        if (string.IsNullOrWhiteSpace(p.Model)) errors.Add("モデルが空です。");
        if (!string.IsNullOrEmpty(p.Language) && !LanguagePattern.IsMatch(p.Language))
            errors.Add("言語は ja や en-US のような言語コードにしてください(空欄で自動判定)。");
        if (p.LiveUsdPerMinute is <= 0 or > 1000000)
            errors.Add("Live の単価は 0 より大きく 1000000 以下の USD/分にしてください(空欄で未設定)。");
        return errors;
    }
}

public sealed class AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public Guid? ActiveProfileId { get; set; }
    public string? MicrophoneDeviceId { get; set; } // null は Windows の既定入力
    public List<Profile> Profiles { get; set; } = new();

    public Profile? ActiveProfile => Profiles.FirstOrDefault(p => p.Id == ActiveProfileId);

    public static List<string> Validate(AppSettings s)
    {
        var errors = new List<string>();
        if (s.MicrophoneDeviceId != null && (string.IsNullOrWhiteSpace(s.MicrophoneDeviceId) || s.MicrophoneDeviceId.Length > 32768))
            errors.Add("マイクの識別情報が不正です。");
        if (s.Version != CurrentVersion) errors.Add($"設定ファイルのバージョン {s.Version} には対応していません。");
        if (s.Profiles == null || s.Profiles.Any(p => p == null))
        {
            errors.Add("プロファイル一覧が不正です(null を含みます)。");
            return errors;
        }
        if (s.Profiles.GroupBy(p => p.Id).Any(g => g.Count() > 1)) errors.Add("プロファイル ID が重複しています。");
        if (s.Profiles.Where(p => p.CredentialId != null).GroupBy(p => p.CredentialId).Any(g => g.Count() > 1))
            errors.Add("複数のプロファイルが同じ資格情報を参照しています。");
        foreach (var p in s.Profiles)
            foreach (var e in ProfileValidator.Validate(p)) errors.Add($"[{p.Name}] {e}");
        if (s.ActiveProfileId is { } id && s.Profiles.All(p => p.Id != id))
            errors.Add("選択中のプロファイルが一覧に存在しません。");
        return errors;
    }
}

public sealed class HistoryEntry
{
    public DateTime Time { get; set; }
    public string ProfileName { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class HistoryData
{
    public const int CurrentVersion = 1;
    public const int MaxEntries = 20;

    public int Version { get; set; } = CurrentVersion;
    public List<HistoryEntry> Entries { get; set; } = new(); // 新しい順

    public static List<string> Validate(HistoryData d)
    {
        var errors = new List<string>();
        if (d.Version != CurrentVersion) errors.Add($"履歴ファイルのバージョン {d.Version} には対応していません。");
        if (d.Entries == null || d.Entries.Any(e => e == null || e.Text == null || e.ProfileName == null))
            errors.Add("履歴一覧が不正です(null を含みます)。");
        return errors;
    }
}

/// <summary>
/// JSON ファイルの読み書き。壊れたファイルは読み込み時に検出して保持し、
/// 利用者が明示的に退避するまで保存で上書きしない。
/// </summary>
public sealed class JsonFileStore<T> where T : class, new()
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    readonly Func<T, List<string>> _validate;

    public JsonFileStore(string path, Func<T, List<string>> validate)
    {
        Path = path;
        _validate = validate;
    }

    public string Path { get; }
    public T Value { get; private set; } = new();
    public string? CorruptReason { get; private set; }
    public bool IsCorrupt => CorruptReason != null;

    public void Load()
    {
        Value = new T();
        CorruptReason = null;
        if (!File.Exists(Path)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<T>(File.ReadAllText(Path), Options);
            if (loaded == null) { CorruptReason = "内容が空です。"; return; }
            var errors = _validate(loaded);
            if (errors.Count > 0) { CorruptReason = string.Join(" ", errors); return; }
            Value = loaded;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            CorruptReason = $"読み込めません: {e.Message}";
        }
        catch (JsonException)
        {
            CorruptReason = "JSON として読めません。";
        }
        catch (Exception e) when (e is NullReferenceException or InvalidOperationException or ArgumentException)
        {
            CorruptReason = "内容が不正です(想定外の null や型)。";
        }
    }

    public void Save() => Save(Value);

    /// <summary>candidate を原子的に書き込み、成功した場合だけ Value を candidate に差し替える。</summary>
    public void Save(T candidate)
    {
        if (IsCorrupt)
            throw new InvalidOperationException("壊れた保存ファイルがあるため保存しません。設定画面で退避してから保存してください。");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(candidate, Options));
        File.Move(tmp, Path, overwrite: true);
        Value = candidate;
    }

    /// <summary>壊れたファイルを別名へ退避し、空の状態に戻す。退避先のパスを返す。</summary>
    public string QuarantineCorruptFile()
    {
        var dest = $"{Path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
        if (File.Exists(Path)) File.Move(Path, dest);
        Value = new T();
        CorruptReason = null;
        return dest;
    }
}

public sealed class HistoryLog
{
    readonly JsonFileStore<HistoryData> _store;

    public HistoryLog(JsonFileStore<HistoryData> store) => _store = store;

    public IReadOnlyList<HistoryEntry> Entries => _store.Value.Entries;

    /// <summary>メモリ上へ追加し、直近 20 件に切り詰めて保存する。保存失敗は例外で伝える。</summary>
    public void Add(string text, string profileName, DateTime? now = null)
    {
        var list = _store.Value.Entries;
        list.Insert(0, new HistoryEntry { Time = now ?? DateTime.Now, ProfileName = profileName, Text = text });
        if (list.Count > HistoryData.MaxEntries) list.RemoveRange(HistoryData.MaxEntries, list.Count - HistoryData.MaxEntries);
        _store.Save();
    }

    public void Clear()
    {
        _store.Value.Entries.Clear();
        _store.Save();
    }
}
