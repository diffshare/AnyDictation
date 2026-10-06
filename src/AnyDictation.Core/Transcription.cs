using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AnyDictation;

public sealed record TranscriptionRequest(Profile Profile, string ApiKey, byte[] Wav);

/// <summary>認識サービス共通の契約。選択されたプロファイルの 1 サービスにだけ送り、自動 retry や別サービスへの fallback はしない。</summary>
public interface ITranscriptionClient
{
    Task<string> TranscribeAsync(TranscriptionRequest request, CancellationToken ct);
}

public enum TranscriptionErrorKind
{
    Configuration, Auth, BadRequest, RateLimit, Server, Redirect, Timeout, Network, InvalidResponse, EmptyResult, Other,
}

public sealed class TranscriptionException : Exception
{
    public TranscriptionException(TranscriptionErrorKind kind, string message, Exception? inner = null) : base(message, inner)
        => Kind = kind;

    public TranscriptionErrorKind Kind { get; }
}

public static class TranscriptionClients
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(90);

    /// <summary>リダイレクトは追従しない(キー付きヘッダーを別ホストへ送らないため)。Cookie も使わない。</summary>
    public static SocketsHttpHandler CreateHandler() => new() { AllowAutoRedirect = false, UseCookies = false };

    public static HttpClient CreateHttpClient() => new(CreateHandler(), disposeHandler: true) { Timeout = RequestTimeout };

    public static ITranscriptionClient Create(ProviderKind kind, HttpClient http) => kind switch
    {
        ProviderKind.AzureMai => new AzureMaiClient(http),
        ProviderKind.OpenAiCompatible => new OpenAiCompatibleClient(http),
        ProviderKind.AzureOpenAi => new OpenAiCompatibleClient(http, useAzureApiKey: true),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    internal static async Task<string> SendAsync(HttpClient http, HttpRequestMessage message, string apiKey, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TranscriptionException(TranscriptionErrorKind.Timeout,
                $"応答が {RequestTimeout.TotalSeconds:0} 秒以内に返りませんでした(タイムアウト)。ネットワークとサービスの状態を確認し、再送してください。");
        }
        catch (HttpRequestException e)
        {
            throw new TranscriptionException(TranscriptionErrorKind.Network,
                "サービスに接続できませんでした。ネットワークとエンドポイントを確認してください。", e);
        }

        using (response)
        {
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TranscriptionException(TranscriptionErrorKind.Timeout, "応答の受信がタイムアウトしました。再送してください。");
            }
            catch (HttpRequestException e)
            {
                throw new TranscriptionException(TranscriptionErrorKind.Network, "応答の受信中に通信が切れました。", e);
            }

            if (response.IsSuccessStatusCode) return body;
            throw MapError(response.StatusCode, body, apiKey);
        }
    }

    internal static TranscriptionException MapError(HttpStatusCode status, string body, string apiKey)
    {
        int code = (int)status;
        string detail = ExtractDetail(body, apiKey);
        string suffix = detail.Length > 0 ? $" サービスのメッセージ: {detail}" : "";
        if (code is >= 300 and < 400)
            return new(TranscriptionErrorKind.Redirect,
                $"サービスがリダイレクト(HTTP {code})を返しました。キー保護のため追従しません。エンドポイントの URL を確認してください。");
        return code switch
        {
            401 or 403 => new(TranscriptionErrorKind.Auth,
                $"認証に失敗しました(HTTP {code})。APIキー、エンドポイント、リソースの権限を確認してください。{suffix}"),
            404 => new(TranscriptionErrorKind.BadRequest,
                $"エンドポイントまたはモデルが見つかりません(HTTP 404)。URL とモデル名を確認してください。{suffix}"),
            413 => new(TranscriptionErrorKind.BadRequest, $"音声がサービスの上限を超えています(HTTP 413)。{suffix}"),
            429 => new(TranscriptionErrorKind.RateLimit,
                $"リクエスト数または利用枠の上限に達しました(HTTP 429)。しばらく待ってから再送してください。{suffix}"),
            >= 400 and < 500 => new(TranscriptionErrorKind.BadRequest, $"リクエストが拒否されました(HTTP {code})。{suffix}"),
            >= 500 => new(TranscriptionErrorKind.Server,
                $"サービス側でエラーが発生しました(HTTP {code})。時間をおいて再送してください。{suffix}"),
            _ => new(TranscriptionErrorKind.Other, $"想定外の応答です(HTTP {code})。{suffix}"),
        };
    }

    internal static string ExtractDetail(string body, string apiKey)
    {
        string text = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var err))
                {
                    if (err.ValueKind == JsonValueKind.String) text = err.GetString() ?? "";
                    else if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                        text = m.GetString() ?? "";
                }
                else if (root.TryGetProperty("message", out var m2) && m2.ValueKind == JsonValueKind.String)
                {
                    text = m2.GetString() ?? "";
                }
            }
        }
        catch (JsonException)
        {
            // JSON でない本文は表示しない(HTML エラーページ等)
        }
        if (apiKey.Length > 0) text = text.Replace(apiKey, "***", StringComparison.Ordinal);
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length > 200 ? text[..200] + "…" : text;
    }

    internal static ByteArrayContent WavPart(byte[] wav)
    {
        var part = new ByteArrayContent(wav);
        part.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        return part;
    }

    internal static Uri BuildUri(Profile profile, string path)
    {
        if (!ProfileValidator.IsAllowedEndpoint(profile.Endpoint, out var baseUri))
            throw new TranscriptionException(TranscriptionErrorKind.Configuration,
                "エンドポイントが不正です。https:// の URL(localhost のみ http:// 可)を設定してください。");
        return new Uri(baseUri!.AbsoluteUri.TrimEnd('/') + path);
    }
}

/// <summary>Azure Speech の MAI-Transcribe(Fast Transcription の enhancedMode)。整形は認識サービスの clean スタイルのみ。</summary>
public sealed class AzureMaiClient : ITranscriptionClient
{
    public const string ApiVersion = "2025-10-15";
    readonly HttpClient _http;

    public AzureMaiClient(HttpClient http) => _http = http;

    public async Task<string> TranscribeAsync(TranscriptionRequest request, CancellationToken ct)
    {
        var p = request.Profile;
        if (string.IsNullOrEmpty(request.ApiKey))
            throw new TranscriptionException(TranscriptionErrorKind.Configuration, "APIキーが未設定です。設定画面で入力してください。");
        var uri = TranscriptionClients.BuildUri(p, $"/speechtotext/transcriptions:transcribe?api-version={ApiVersion}");

        var definition = new Dictionary<string, object>
        {
            ["enhancedMode"] = new Dictionary<string, object>
            {
                ["enabled"] = true,
                ["model"] = p.Model,
                ["modelOptions"] = new Dictionary<string, string> { ["transcribeStyle"] = "clean" },
            },
        };
        if (!string.IsNullOrEmpty(p.Language)) definition["locales"] = new[] { p.Language };

        using var form = new MultipartFormDataContent();
        form.Add(TranscriptionClients.WavPart(request.Wav), "audio", "audio.wav");
        form.Add(new StringContent(JsonSerializer.Serialize(definition), Encoding.UTF8, new MediaTypeHeaderValue("application/json")), "definition");

        using var message = new HttpRequestMessage(HttpMethod.Post, uri) { Content = form };
        message.Headers.Add("Ocp-Apim-Subscription-Key", request.ApiKey);

        string body = await TranscriptionClients.SendAsync(_http, message, request.ApiKey, ct).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var phrases = doc.RootElement.GetProperty("combinedPhrases").EnumerateArray()
                .Select(e => e.GetProperty("text").GetString() ?? "")
                .Where(t => t.Length > 0);
            string text = string.Join("\n", phrases).Trim();
            if (text.Length == 0)
                throw new TranscriptionException(TranscriptionErrorKind.EmptyResult, "音声から文字を認識できませんでした(結果が空)。");
            return text;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new TranscriptionException(TranscriptionErrorKind.InvalidResponse,
                "サービスの応答を解釈できませんでした(combinedPhrases が見つかりません)。", e);
        }
    }
}

/// <summary>OpenAI / 互換と Azure OpenAI の共通音声ファイル形式。URL と認証方式はクライアント作成時に固定する。</summary>
public sealed class OpenAiCompatibleClient : ITranscriptionClient
{
    readonly HttpClient _http;

    readonly bool _useAzureApiKey;

    public OpenAiCompatibleClient(HttpClient http) : this(http, useAzureApiKey: false) { }

    internal OpenAiCompatibleClient(HttpClient http, bool useAzureApiKey)
    {
        _http = http;
        _useAzureApiKey = useAzureApiKey;
    }

    public async Task<string> TranscribeAsync(TranscriptionRequest request, CancellationToken ct)
    {
        var p = request.Profile;
        var path = _useAzureApiKey
            ? $"/openai/deployments/{Uri.EscapeDataString(p.Model)}/audio/transcriptions?api-version=2025-03-01-preview"
            : "/audio/transcriptions";
        var uri = TranscriptionClients.BuildUri(p, path);
        // ローカルの互換サーバーはキー不要なことがあるため、loopback に限り空キーを許す
        if (request.ApiKey.Length == 0 && (_useAzureApiKey || !uri.IsLoopback))
            throw new TranscriptionException(TranscriptionErrorKind.Configuration, "APIキーが未設定です。設定画面で入力してください。");

        using var form = new MultipartFormDataContent();
        form.Add(TranscriptionClients.WavPart(request.Wav), "file", "audio.wav");
        form.Add(new StringContent(p.Model), "model");
        if (!string.IsNullOrEmpty(p.Language)) form.Add(new StringContent(p.Language), "language");
        form.Add(new StringContent("json"), "response_format");

        using var message = new HttpRequestMessage(HttpMethod.Post, uri) { Content = form };
        if (_useAzureApiKey) message.Headers.Add("api-key", request.ApiKey);
        else if (request.ApiKey.Length > 0) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);

        string body = await TranscriptionClients.SendAsync(_http, message, request.ApiKey, ct).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(body);
            string text = (doc.RootElement.GetProperty("text").GetString() ?? "").Trim();
            if (text.Length == 0)
                throw new TranscriptionException(TranscriptionErrorKind.EmptyResult, "音声から文字を認識できませんでした(結果が空)。");
            return text;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new TranscriptionException(TranscriptionErrorKind.InvalidResponse,
                "サービスの応答を解釈できませんでした(text プロパティが見つかりません)。", e);
        }
    }
}
