using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xunit;

namespace AnyDictation.Tests;

sealed class FakeHandler : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Calls { get; } = new();
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string body = request.Content == null ? "" : Encoding.Latin1.GetString(await request.Content.ReadAsByteArrayAsync(ct));
        Calls.Add((request, body));
        return Respond(request);
    }
}

public class TranscriptionClientTests
{
    const string Key = "SECRET-KEY-12345";
    static readonly byte[] Wav = WavEncoder.Encode(new byte[640], 16000, 16, 1);

    static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    static (ITranscriptionClient client, FakeHandler handler) Make(ProviderKind kind, HttpStatusCode code, string json)
    {
        var h = new FakeHandler { Respond = _ => Json(code, json) };
        return (TranscriptionClients.Create(kind, new HttpClient(h)), h);
    }

    static TranscriptionRequest Req(ProviderKind kind, string key = Key, Action<Profile>? tweak = null)
    {
        var p = TestProfiles.Create(kind);
        tweak?.Invoke(p);
        return new TranscriptionRequest(p, key, Wav);
    }

    // ---- Azure MAI ----

    [Fact]
    public async Task Azure_URLとヘッダーとmultipartが仕様どおり()
    {
        var (client, h) = Make(ProviderKind.AzureMai, HttpStatusCode.OK, "{\"combinedPhrases\":[{\"text\":\"こんにちは\"}]}");

        var text = await client.TranscribeAsync(Req(ProviderKind.AzureMai), default);

        Assert.Equal("こんにちは", text);
        var (req, body) = Assert.Single(h.Calls);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://example.cognitiveservices.azure.com/speechtotext/transcriptions:transcribe?api-version=2025-10-15",
            req.RequestUri!.AbsoluteUri);
        Assert.Equal(Key, Assert.Single(req.Headers.GetValues("Ocp-Apim-Subscription-Key")));
        Assert.Null(req.Headers.Authorization);
        Assert.Equal("multipart/form-data", req.Content!.Headers.ContentType!.MediaType);
        Assert.Contains("name=audio", body);
        Assert.Contains("filename=audio.wav", body);
        Assert.Contains("Content-Type: audio/wav", body);
        Assert.Contains("RIFF", body);
        Assert.Contains("name=definition", body);
        Assert.Contains("Content-Type: application/json", body);
        Assert.DoesNotContain(Key, body);

        var json = body[(body.IndexOf("{\"enhancedMode\"", StringComparison.Ordinal))..];
        json = json[..(json.LastIndexOf('}') + 1)];
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("ja", root.GetProperty("locales")[0].GetString());
        var em = root.GetProperty("enhancedMode");
        Assert.True(em.GetProperty("enabled").GetBoolean());
        Assert.Equal("MAI-Transcribe-2", em.GetProperty("model").GetString());
        Assert.Equal("clean", em.GetProperty("modelOptions").GetProperty("transcribeStyle").GetString());
    }

    [Fact]
    public async Task Azure_言語を空にするとlocalesを送らない()
    {
        var (client, h) = Make(ProviderKind.AzureMai, HttpStatusCode.OK, "{\"combinedPhrases\":[{\"text\":\"x\"}]}");
        await client.TranscribeAsync(Req(ProviderKind.AzureMai, tweak: p => p.Language = ""), default);
        Assert.DoesNotContain("locales", h.Calls[0].Body);
    }

    [Fact]
    public async Task Azure_endpoint末尾スラッシュ有無で同じURL()
    {
        var (client, h) = Make(ProviderKind.AzureMai, HttpStatusCode.OK, "{\"combinedPhrases\":[{\"text\":\"x\"}]}");
        await client.TranscribeAsync(Req(ProviderKind.AzureMai, tweak: p => p.Endpoint = "https://example.cognitiveservices.azure.com"), default);
        Assert.Equal("https://example.cognitiveservices.azure.com/speechtotext/transcriptions:transcribe?api-version=2025-10-15",
            h.Calls[0].Request.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Azure_複数のcombinedPhrasesは改行で連結する()
    {
        var (client, _) = Make(ProviderKind.AzureMai, HttpStatusCode.OK, "{\"combinedPhrases\":[{\"text\":\"a\"},{\"text\":\"b\"}]}");
        Assert.Equal("a\nb", await client.TranscribeAsync(Req(ProviderKind.AzureMai), default));
    }

    [Fact]
    public async Task Azure_結果が空ならEmptyResult()
    {
        var (client, _) = Make(ProviderKind.AzureMai, HttpStatusCode.OK, "{\"combinedPhrases\":[{\"text\":\"\"}]}");
        var e = await Assert.ThrowsAsync<TranscriptionException>(() => client.TranscribeAsync(Req(ProviderKind.AzureMai), default));
        Assert.Equal(TranscriptionErrorKind.EmptyResult, e.Kind);
    }

    [Fact]
    public async Task Azure_想定外の応答形式はInvalidResponse()
    {
        var (client, _) = Make(ProviderKind.AzureMai, HttpStatusCode.OK, "{\"foo\":1}");
        var e = await Assert.ThrowsAsync<TranscriptionException>(() => client.TranscribeAsync(Req(ProviderKind.AzureMai), default));
        Assert.Equal(TranscriptionErrorKind.InvalidResponse, e.Kind);
    }

    [Fact]
    public async Task Azure_キー未設定は送信せずConfigurationエラー()
    {
        var (client, h) = Make(ProviderKind.AzureMai, HttpStatusCode.OK, "{}");
        var e = await Assert.ThrowsAsync<TranscriptionException>(() => client.TranscribeAsync(Req(ProviderKind.AzureMai, key: ""), default));
        Assert.Equal(TranscriptionErrorKind.Configuration, e.Kind);
        Assert.Empty(h.Calls);
    }

    // ---- OpenAI / 互換 ----

    [Fact]
    public async Task OpenAI_URLとBearerとフォーム項目が仕様どおり()
    {
        var (client, h) = Make(ProviderKind.OpenAiCompatible, HttpStatusCode.OK, "{\"text\":\" 認識結果 \"}");

        var text = await client.TranscribeAsync(Req(ProviderKind.OpenAiCompatible), default);

        Assert.Equal("認識結果", text);
        var (req, body) = Assert.Single(h.Calls);
        Assert.Equal("https://api.openai.com/v1/audio/transcriptions", req.RequestUri!.AbsoluteUri);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", Key), req.Headers.Authorization);
        Assert.Contains("name=file", body);
        Assert.Contains("filename=audio.wav", body);
        Assert.Contains("name=model", body);
        Assert.Contains("gpt-4o-transcribe", body);
        Assert.Contains("name=language", body);
        Assert.Contains("name=response_format", body);
        Assert.Contains("json", body);
        Assert.DoesNotContain(Key, body);
    }

    [Fact]
    public async Task OpenAI互換_localhostはHTTPかつキー無しで送れる()
    {
        var (client, h) = Make(ProviderKind.OpenAiCompatible, HttpStatusCode.OK, "{\"text\":\"ok\"}");
        var r = Req(ProviderKind.OpenAiCompatible, key: "", tweak: p => p.Endpoint = "http://localhost:8080/v1");
        Assert.Equal("ok", await client.TranscribeAsync(r, default));
        Assert.Equal("http://localhost:8080/v1/audio/transcriptions", h.Calls[0].Request.RequestUri!.AbsoluteUri);
        Assert.Null(h.Calls[0].Request.Headers.Authorization);
    }

    [Fact]
    public async Task OpenAI_リモートでキー無しは送信しない()
    {
        var (client, h) = Make(ProviderKind.OpenAiCompatible, HttpStatusCode.OK, "{\"text\":\"ok\"}");
        var e = await Assert.ThrowsAsync<TranscriptionException>(() => client.TranscribeAsync(Req(ProviderKind.OpenAiCompatible, key: ""), default));
        Assert.Equal(TranscriptionErrorKind.Configuration, e.Kind);
        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task OpenAI_textが無い応答はInvalidResponse_空textはEmptyResult()
    {
        var (c1, _) = Make(ProviderKind.OpenAiCompatible, HttpStatusCode.OK, "{\"x\":1}");
        var e1 = await Assert.ThrowsAsync<TranscriptionException>(() => c1.TranscribeAsync(Req(ProviderKind.OpenAiCompatible), default));
        Assert.Equal(TranscriptionErrorKind.InvalidResponse, e1.Kind);

        var (c2, _) = Make(ProviderKind.OpenAiCompatible, HttpStatusCode.OK, "{\"text\":\"  \"}");
        var e2 = await Assert.ThrowsAsync<TranscriptionException>(() => c2.TranscribeAsync(Req(ProviderKind.OpenAiCompatible), default));
        Assert.Equal(TranscriptionErrorKind.EmptyResult, e2.Kind);
    }

    // ---- 共通: エラー・リダイレクト・秘密 ----

    [Theory]
    [InlineData(ProviderKind.AzureMai, 401, TranscriptionErrorKind.Auth, "認証")]
    [InlineData(ProviderKind.OpenAiCompatible, 403, TranscriptionErrorKind.Auth, "認証")]
    [InlineData(ProviderKind.AzureMai, 404, TranscriptionErrorKind.BadRequest, "見つかりません")]
    [InlineData(ProviderKind.OpenAiCompatible, 400, TranscriptionErrorKind.BadRequest, "拒否")]
    [InlineData(ProviderKind.OpenAiCompatible, 413, TranscriptionErrorKind.BadRequest, "上限")]
    [InlineData(ProviderKind.AzureMai, 429, TranscriptionErrorKind.RateLimit, "上限")]
    [InlineData(ProviderKind.OpenAiCompatible, 500, TranscriptionErrorKind.Server, "サービス側")]
    [InlineData(ProviderKind.AzureMai, 503, TranscriptionErrorKind.Server, "サービス側")]
    public async Task HTTPエラーを日本語の種別付きエラーへ変換する(ProviderKind kind, int status, TranscriptionErrorKind expected, string phrase)
    {
        var (client, _) = Make(kind, (HttpStatusCode)status, "{}");
        var e = await Assert.ThrowsAsync<TranscriptionException>(() => client.TranscribeAsync(Req(kind), default));
        Assert.Equal(expected, e.Kind);
        Assert.Contains(phrase, e.Message);
        Assert.Contains(status.ToString(), e.Message);
    }

    [Theory]
    [InlineData(ProviderKind.AzureMai)]
    [InlineData(ProviderKind.OpenAiCompatible)]
    public async Task リダイレクトは追従せず一度しか送らない(ProviderKind kind)
    {
        var h = new FakeHandler
        {
            Respond = _ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri("https://evil.example/steal");
                return r;
            },
        };
        var client = TranscriptionClients.Create(kind, new HttpClient(h));
        var e = await Assert.ThrowsAsync<TranscriptionException>(() => client.TranscribeAsync(Req(kind), default));
        Assert.Equal(TranscriptionErrorKind.Redirect, e.Kind);
        Assert.Single(h.Calls);
        Assert.DoesNotContain("evil.example", e.Message);
    }

    [Fact]
    public void 実際のHttpHandlerは自動リダイレクトとCookieを無効にしている()
    {
        using var handler = TranscriptionClients.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        using var client = TranscriptionClients.CreateHttpClient();
        Assert.Empty(client.DefaultRequestHeaders); // キーは request ごとに付け、共有クライアントに持たせない
    }

    [Fact]
    public async Task キーはリクエストごとに付き共有HttpClientに残らない()
    {
        var h = new FakeHandler { Respond = _ => Json(HttpStatusCode.OK, "{\"text\":\"a\"}") };
        var http = new HttpClient(h);
        var client = TranscriptionClients.Create(ProviderKind.OpenAiCompatible, http);
        await client.TranscribeAsync(Req(ProviderKind.OpenAiCompatible, key: "KEY-A"), default);
        await client.TranscribeAsync(Req(ProviderKind.OpenAiCompatible, key: "KEY-B"), default);
        Assert.Equal("KEY-A", h.Calls[0].Request.Headers.Authorization!.Parameter);
        Assert.Equal("KEY-B", h.Calls[1].Request.Headers.Authorization!.Parameter);
        Assert.Null(http.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData(ProviderKind.AzureMai)]
    [InlineData(ProviderKind.OpenAiCompatible)]
    public async Task サーバーがキーを本文でエコーしてもエラー文に出さない(ProviderKind kind)
    {
        var (client, _) = Make(kind, HttpStatusCode.Unauthorized, $"{{\"error\":{{\"message\":\"Incorrect key {Key} provided\"}}}}");
        var e = await Assert.ThrowsAsync<TranscriptionException>(() => client.TranscribeAsync(Req(kind), default));
        Assert.DoesNotContain(Key, e.Message);
        Assert.Contains("***", e.Message);
        Assert.Contains("Incorrect key", e.Message);
    }

    [Theory]
    [InlineData("{\"error\":{\"message\":123}}")]
    [InlineData("{\"error\":{\"message\":[\"a\"]}}")]
    [InlineData("{\"error\":123}")]
    [InlineData("{\"error\":[1,2]}")]
    [InlineData("{\"message\":{\"x\":1}}")]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    [InlineData("null")]
    public async Task 形式不正なエラーJSONでもHTTPステータスの分類が保たれる(string errorBody)
    {
        var (client, _) = Make(ProviderKind.OpenAiCompatible, HttpStatusCode.Unauthorized, errorBody);
        var e = await Assert.ThrowsAsync<TranscriptionException>(() => client.TranscribeAsync(Req(ProviderKind.OpenAiCompatible), default));
        Assert.Equal(TranscriptionErrorKind.Auth, e.Kind);
        Assert.Contains("401", e.Message);
    }

    [Fact]
    public async Task JSONでないエラー本文は表示しない()
    {
        var h = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>secret page</html>") } };
        var client = TranscriptionClients.Create(ProviderKind.OpenAiCompatible, new HttpClient(h));
        var e = await Assert.ThrowsAsync<TranscriptionException>(() => client.TranscribeAsync(Req(ProviderKind.OpenAiCompatible), default));
        Assert.DoesNotContain("secret page", e.Message);
    }

    [Fact]
    public async Task タイムアウトとネットワーク断を区別する()
    {
        var timeout = new FakeHandler { Respond = _ => throw new TaskCanceledException("timeout") };
        var c1 = TranscriptionClients.Create(ProviderKind.OpenAiCompatible, new HttpClient(timeout));
        var e1 = await Assert.ThrowsAsync<TranscriptionException>(() => c1.TranscribeAsync(Req(ProviderKind.OpenAiCompatible), default));
        Assert.Equal(TranscriptionErrorKind.Timeout, e1.Kind);
        Assert.Contains("タイムアウト", e1.Message);

        var net = new FakeHandler { Respond = _ => throw new HttpRequestException("dns") };
        var c2 = TranscriptionClients.Create(ProviderKind.AzureMai, new HttpClient(net));
        var e2 = await Assert.ThrowsAsync<TranscriptionException>(() => c2.TranscribeAsync(Req(ProviderKind.AzureMai), default));
        Assert.Equal(TranscriptionErrorKind.Network, e2.Kind);
        Assert.DoesNotContain(Key, e2.Message);
    }

    [Fact]
    public async Task 利用者による取消はそのままOperationCanceledになる()
    {
        using var cts = new CancellationTokenSource();
        var h = new FakeHandler { Respond = _ => { cts.Cancel(); throw new TaskCanceledException(); } };
        var client = TranscriptionClients.Create(ProviderKind.OpenAiCompatible, new HttpClient(h));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.TranscribeAsync(Req(ProviderKind.OpenAiCompatible), cts.Token));
    }

    [Theory]
    [InlineData(ProviderKind.AzureMai)]
    [InlineData(ProviderKind.OpenAiCompatible)]
    public async Task 非loopbackのHTTP_endpointは送信前に拒否する(ProviderKind kind)
    {
        var (client, h) = Make(kind, HttpStatusCode.OK, "{}");
        var r = Req(kind, tweak: p => p.Endpoint = "http://example.com/v1");
        var e = await Assert.ThrowsAsync<TranscriptionException>(() => client.TranscribeAsync(r, default));
        Assert.Equal(TranscriptionErrorKind.Configuration, e.Kind);
        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task 選択されたプロファイルのエンドポイントにだけ送る()
    {
        var h = new FakeHandler { Respond = _ => Json(HttpStatusCode.InternalServerError, "{}") };
        var client = TranscriptionClients.Create(ProviderKind.AzureMai, new HttpClient(h));
        await Assert.ThrowsAsync<TranscriptionException>(() => client.TranscribeAsync(Req(ProviderKind.AzureMai), default));
        // 失敗しても別サービスへ fallback しない: 呼び出しは 1 回で、宛先は Azure のみ
        var call = Assert.Single(h.Calls);
        Assert.Equal("example.cognitiveservices.azure.com", call.Request.RequestUri!.Host);
    }
}
