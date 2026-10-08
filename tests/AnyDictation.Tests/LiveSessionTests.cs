using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AnyDictation.Tests;

/// <summary>
/// LiveTranscriptionSession を、本番と同じ ClientWebSocket で loopback のテストサーバー(LiveServer)へ接続して検証する。
/// サーバーがフレーム分割・切断・error・無応答を実際に起こすので、実装の内部手順をなぞらず、観測できる結果(受信メッセージ・Result・ログ)を見る。
/// </summary>
public class LiveSessionTests
{
    const string Key = "LIVE-TEST-KEY-123";
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    static byte[] Pcm(int bytes, int seed)
    {
        var b = new byte[bytes];
        for (int i = 0; i < bytes; i++) b[i] = (byte)(seed * 31 + i % 97);
        return b;
    }

    static byte[] Loud(int samples)
    {
        var b = new byte[samples * 2];
        for (int i = 0; i < samples; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 2), (short)4000);
        return b;
    }

    static LiveTranscriptionSession New(LiveServer? server, ConcurrentQueue<string>? log = null, string language = "ja",
        string? model = null, TimeSpan? connect = null, TimeSpan? finish = null, LiveConnect? connector = null)
    {
        var p = TestProfiles.Create(ProviderKind.AzureOpenAiLive);
        if (server != null) p.Endpoint = server.Endpoint.AbsoluteUri;
        p.Language = language;
        if (model != null) p.Model = model;
        return new LiveTranscriptionSession(p, Key, connector, log == null ? null : new QueueLogger(log), connect, finish);
    }

    static async Task<TranscriptionException> FailureOf(LiveTranscriptionSession s)
        => await Assert.ThrowsAsync<TranscriptionException>(() => s.Result.WaitAsync(Wait));

    /// <summary>commit を受け取って、確定とその全文を返すサーバー。</summary>
    static LiveServer Answering(string text, Action<List<byte[]>>? onAppended = null) => new(async c =>
    {
        await c.HandshakeAsync();
        var appended = await c.ReadUntilCommitAsync();
        onAppended?.Invoke(appended);
        await c.SendAsync(LiveConnection.Committed("item1"));
        await c.SendAsync(LiveConnection.Completed("item1", text));
    });

    static int IndexOf(byte[] haystack, string needle) => haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle));

    // ---- 接続先と契約 ----

    [Fact]
    public void エンドポイントはWSSへ変換され_modelやapi_versionやキーはクエリに載らない()
    {
        const string Expected = "wss://example.openai.azure.com/openai/v1/realtime?intent=transcription";
        foreach (var endpoint in new[] { "https://example.openai.azure.com/", "https://example.openai.azure.com" })
        {
            var p = TestProfiles.Create(ProviderKind.AzureOpenAiLive);
            p.Endpoint = endpoint;
            var uri = LiveTranscriptionSession.BuildUri(p);
            Assert.Equal(Expected, uri.AbsoluteUri);
            Assert.Equal("?intent=transcription", uri.Query);
        }
        var local = TestProfiles.Create(ProviderKind.AzureOpenAiLive);
        local.Endpoint = "http://localhost:8080/";
        Assert.Equal("ws://localhost:8080/openai/v1/realtime?intent=transcription", LiveTranscriptionSession.BuildUri(local).AbsoluteUri);
    }

    [Theory]
    [InlineData("http://example.com/", Key)]            // 非 loopback の HTTP
    [InlineData("ftp://example.com/", Key)]
    [InlineData("https://example.com/?x=1", Key)]       // クエリ付き
    [InlineData("not a url", Key)]
    [InlineData("https://example.com/", "")]            // キー未設定(loopback の OpenAI 互換のような例外は Live にない)
    [InlineData("http://localhost:8080/", "")]
    public void 不正なエンドポイントとキー未設定は接続せずに拒否する(string endpoint, string key)
    {
        var p = TestProfiles.Create(ProviderKind.AzureOpenAiLive);
        p.Endpoint = endpoint;
        var e = Assert.Throws<TranscriptionException>(() => new LiveTranscriptionSession(p, key));
        Assert.Equal(TranscriptionErrorKind.Configuration, e.Kind);
    }

    [Fact]
    public async Task キーはapi_keyヘッダーだけで送り_URLとsession_updateは確認済みの契約どおりになる()
    {
        string? update = null;
        await using var capturing = new LiveServer(async c =>
        {
            update = await c.HandshakeAsync();
            await c.ReadUntilCommitAsync();
            await c.SendAsync(LiveConnection.Committed("i"));
            await c.SendAsync(LiveConnection.Completed("i", "ok"));
        });
        var s = New(capturing);
        s.Start();
        s.Enqueue(Pcm(4800, 1));
        s.Complete();
        await s.Result.WaitAsync(Wait);
        await capturing.Done;

        Assert.Equal(Key, capturing.ApiKeyHeader);
        Assert.Null(capturing.AuthorizationHeader);
        Assert.Equal("/openai/v1/realtime?intent=transcription", capturing.PathAndQuery);
        Assert.DoesNotContain(Key, capturing.PathAndQuery);
        Assert.Equal(
            "{\"type\":\"session.update\",\"session\":{\"type\":\"transcription\",\"audio\":{\"input\":{\"format\":{\"type\":\"audio/pcm\",\"rate\":24000}," +
            "\"transcription\":{\"model\":\"gpt-live-transcribe\",\"languages\":[\"ja\"],\"delay\":\"low\"},\"turn_detection\":null}}}}",
            update);
    }

    [Fact]
    public async Task 言語が空ならlanguagesを省略し_モデルはデプロイ名をそのまま使う()
    {
        string? update = null;
        await using var server = new LiveServer(async c =>
        {
            update = await c.HandshakeAsync();
            await c.ReadUntilCommitAsync();
            await c.SendAsync(LiveConnection.Committed("i"));
            await c.SendAsync(LiveConnection.Completed("i", "ok"));
        });
        var s = New(server, language: "", model: "my-live-deploy");
        s.Start();
        s.Enqueue(Pcm(4800, 1));
        s.Complete();
        await s.Result.WaitAsync(Wait);
        using var doc = JsonDocument.Parse(update!);
        var transcription = doc.RootElement.GetProperty("session").GetProperty("audio").GetProperty("input").GetProperty("transcription");
        Assert.False(transcription.TryGetProperty("languages", out _));
        Assert.Equal("my-live-deploy", transcription.GetProperty("model").GetString());
        Assert.Equal("low", transcription.GetProperty("delay").GetString());
    }

    // ---- 正常系: 順序・commit 1 回・全文 ----

    [Fact]
    public async Task 録音中に音声を順に送り_停止でcommitを1回だけ送り_completedの全文だけを結果にする()
    {
        const string Final = "音声入力の接続を確認しています。明日の会議は午後三時に始まります。";
        var chunks = new[] { Pcm(4800, 1), Pcm(4800, 2), Pcm(1234, 3) };
        List<byte[]>? appended = null;
        await using var server = new LiveServer(async c =>
        {
            await c.HandshakeAsync();
            appended = await c.ReadUntilCommitAsync(async n =>
            {
                if (n == 1)
                {
                    await c.SendAsync(LiveConnection.Delta("item1", "音声入力の"));
                    await c.SendAsync(LiveConnection.Delta("item1", "接続"));
                }
            });
            await c.SendAsync(LiveConnection.Committed("item1"));
            await c.SendAsync(LiveConnection.Delta("item1", "(確定前の言い直し)")); // delta は結果ではない
            await c.SendAsync(LiveConnection.Completed("item1", Final));
        });
        var s = New(server);
        var partial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.PartialChanged += () => partial.TrySetResult();
        s.Start();
        foreach (var chunk in chunks) Assert.True(s.Enqueue((byte[])chunk.Clone()));

        await partial.Task.WaitAsync(Wait);
        Assert.False(s.Result.IsCompleted);                       // 途中の文字が届いただけでは確定しない
        Assert.StartsWith("音声入力の", s.GetPartialTail(100));

        s.Complete();
        s.Complete();                                             // 2 回目は何も起こさない
        string text = await s.Result.WaitAsync(Wait);
        await server.Done;
        await server.Connection!.Closed;

        Assert.Equal(Final, text);                                // delta の連結ではなく completed の全文
        Assert.Equal(chunks.Length, appended!.Count);
        for (int i = 0; i < chunks.Length; i++) Assert.Equal(chunks[i], appended[i]); // 順序と内容
        Assert.Equal(new[] { "session.update", "input_audio_buffer.append", "input_audio_buffer.append", "input_audio_buffer.append", "input_audio_buffer.commit" },
            server.Connection.SeenTypes);                         // commit は 1 回で、最後
    }

    [Fact]
    public async Task 接続の準備前に積んだ音声は順に保たれ_session_updatedの前には送られない()
    {
        var chunks = Enumerable.Range(1, 5).Select(i => Pcm(4800, i)).ToList();
        List<byte[]>? appended = null;
        bool quietBeforeReady = false;
        await using var server = new LiveServer(async c =>
        {
            await c.SendAsync("{\"type\":\"session.created\",\"session\":{}}");
            Assert.Equal("session.update", LiveConnection.TypeOf((await c.NextAsync())!));
            quietBeforeReady = await c.QuietAsync(400);           // updated を返すまで、クライアントは音声を送らない
            await c.SendAsync("{\"type\":\"session.updated\",\"session\":{}}");
            appended = await c.ReadUntilCommitAsync();
            await c.SendAsync(LiveConnection.Committed("item1"));
            await c.SendAsync(LiveConnection.Completed("item1", "遅い接続でも欠けない"));
        });
        var s = New(server);
        s.Start();
        foreach (var chunk in chunks) Assert.True(s.Enqueue((byte[])chunk.Clone())); // 接続前に録音が進む
        s.Complete();                                                                // 停止も接続前

        Assert.Equal("遅い接続でも欠けない", await s.Result.WaitAsync(Wait));
        await server.Done;
        Assert.True(quietBeforeReady);
        Assert.Equal(chunks.Count, appended!.Count);
        for (int i = 0; i < chunks.Count; i++) Assert.Equal(chunks[i], appended[i]);
    }

    [Fact]
    public async Task 保持した音声の再送は100msごとに分けて全部を送り_commitは1回()
    {
        var pcm = Pcm(LiveTranscriptionSession.ChunkBytes * 3 + 1000, 9);
        List<byte[]>? appended = null;
        await using var server = Answering("再送の結果", a => appended = a);
        var s = New(server);
        s.Start();
        s.EnqueueAll(pcm);
        s.Complete();

        Assert.Equal("再送の結果", await s.Result.WaitAsync(Wait));
        await server.Done;
        Assert.Equal(new[] { 4800, 4800, 4800, 1000 }, appended!.Select(a => a.Length));
        Assert.Equal(pcm, appended!.SelectMany(a => a).ToArray());
        await server.Connection!.Closed;
        Assert.Equal(1, server.Connection.SeenTypes.Count(t => t == "input_audio_buffer.commit"));
    }

    // ---- 受信: フレーム分割・順序 ----

    [Fact]
    public async Task 複数のフレームに分かれたJSONと大きな結果も連結して受け取る()
    {
        string finalText = string.Concat(Enumerable.Repeat("音声入力。", 4000)); // 約 60 KB(クライアントの受信バッファより大きい)
        await using var server = new LiveServer(async c =>
        {
            await c.HandshakeAsync();
            await c.ReadUntilCommitAsync();
            var delta = Encoding.UTF8.GetBytes(LiveConnection.Delta("item1", "あいう"));
            int inMiddleOfChar = IndexOf(delta, "い") + 1;        // UTF-8 の文字の途中でフレームを分ける
            await c.SendFragmentedAsync(LiveConnection.Delta("item1", "あいう"), 3, inMiddleOfChar);
            await c.SendAsync(LiveConnection.Committed("item1"));
            var completed = Encoding.UTF8.GetBytes(LiveConnection.Completed("item1", finalText));
            int mid = IndexOf(completed, "音") + 1;
            await c.SendFragmentedAsync(LiveConnection.Completed("item1", finalText), 5, mid, mid + 40, completed.Length - 3);
        });
        var s = New(server);
        s.Start();
        s.Enqueue(Pcm(4800, 1));
        s.Complete();
        Assert.Equal(finalText, await s.Result.WaitAsync(Wait));
        Assert.StartsWith("あいう", s.GetPartialTail(10)); // 途中の分割 delta も壊れずに届いている
    }

    [Fact]
    public async Task completedがcommittedより先に届いても_別項目のcompletedは結果にしない()
    {
        await using var server = new LiveServer(async c =>
        {
            await c.HandshakeAsync();
            await c.ReadUntilCommitAsync();
            await c.SendAsync(LiveConnection.Completed("other", "別の項目の文章"));
            await c.SendAsync(LiveConnection.Completed("item1", "対応する項目の全文"));
            await c.SendAsync(LiveConnection.Committed("item1"));
        });
        var s = New(server);
        s.Start();
        s.Enqueue(Pcm(4800, 1));
        s.Complete();
        Assert.Equal("対応する項目の全文", await s.Result.WaitAsync(Wait));
    }

    [Theory]
    [InlineData("{\"type\":\"input_audio_buffer.committed\"}")]
    [InlineData("{\"type\":\"input_audio_buffer.committed\",\"item_id\":\"\"}")]
    [InlineData("{\"type\":\"input_audio_buffer.committed\",\"item_id\":null}")]
    [InlineData("{\"type\":\"input_audio_buffer.committed\",\"item_id\":7}")]
    public async Task committedのitem_idが無い_空_文字列でなければ_別項目のcompletedを成功にせず失敗にする(string committed)
    {
        await using var server = new LiveServer(async c =>
        {
            await c.HandshakeAsync();
            await c.ReadUntilCommitAsync();
            await c.SendAsync(LiveConnection.Completed("other", "別の項目の文章"));   // 先に別項目の completed が届く
            await c.SendAsync(committed);
            await c.SendAsync(LiveConnection.Completed("item1", "後から届く別の文章"));
            await c.Closed;
        });
        var s = New(server);
        s.Start();
        s.Enqueue(Pcm(4800, 1));
        s.Complete();
        var e = await FailureOf(s);                     // 成功(貼り付け対象)にならず、保持音声の手動再送へ回る
        Assert.Equal(TranscriptionErrorKind.InvalidResponse, e.Kind);
        Assert.False(s.Result.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task 要求していないcommittedは音声が分割された恐れがあるため失敗にする()
    {
        await using var server = new LiveServer(async c =>
        {
            await c.HandshakeAsync();
            await c.SendAsync(LiveConnection.Committed("item1")); // クライアントは commit していない
            await c.Closed;
        });
        var s = New(server);
        s.Start();
        s.Enqueue(Pcm(4800, 1));
        var e = await FailureOf(s);
        Assert.Equal(TranscriptionErrorKind.InvalidResponse, e.Kind);
    }

    // ---- 失敗: 部分文字を結果にしない・保持して再送できる ----

    [Fact]
    public async Task deltaだけ届いて切断されても成功扱いにならず_途中の文字は結果にならない()
    {
        await using var server = new LiveServer(async c =>
        {
            await c.HandshakeAsync();
            await c.ReadUntilCommitAsync();
            await c.SendAsync(LiveConnection.Delta("item1", "途中までの文字"));
            await c.CloseAsync();
        });
        var s = New(server);
        s.Start();
        s.Enqueue(Pcm(4800, 1));
        s.Complete();
        var e = await FailureOf(s);
        Assert.Equal(TranscriptionErrorKind.Network, e.Kind);
        Assert.False(s.Result.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task 録音中に通信が切れても録音は最後の音声まで保持され_新しいセッションへ全部を再送できる()
    {
        await using var broken = new LiveServer(async c =>
        {
            await c.HandshakeAsync();
            await c.NextAsync();   // 最初の append を受けたところで切断
            c.Abort();
        });
        var live = New(broken);
        var capture = new CaptureSession(LiveTranscriptionSession.SampleRate, b => live.Enqueue(b));
        live.Start();
        capture.Append(Loud(2400), 4800);

        var e = await FailureOf(live);
        Assert.Equal(TranscriptionErrorKind.Network, e.Kind);
        Assert.True(SpinWait.SpinUntil(() => !live.Enqueue(new byte[2]), 3000)); // 失敗後は以後の音声を積まない

        // 録音デバイスは失敗の後もバッファを渡し続ける。送信には使われないが、保持している音声には含まれる
        capture.Append(Loud(2400), 4800);
        capture.Append(Loud(1000), 2000);
        var held = capture.Take();
        Assert.False(held.Silent);
        Assert.True(WavEncoder.TryReadPcm16Mono(held.Wav, out int rate, out var pcm));
        Assert.Equal(LiveTranscriptionSession.SampleRate, rate);
        Assert.Equal(4800 + 4800 + 2000, pcm.Length);

        // 手動再送: 新しいセッションで保持した音声全部を送り、commit は 1 回
        List<byte[]>? appended = null;
        await using var retry = Answering("再送で得た全文", a => appended = a);
        var again = New(retry);
        again.Start();
        again.EnqueueAll(pcm);
        again.Complete();
        Assert.Equal("再送で得た全文", await again.Result.WaitAsync(Wait));
        await retry.Done;
        Assert.Equal(pcm.ToArray(), appended!.SelectMany(a => a).ToArray());
    }

    [Fact]
    public async Task errorイベントは失敗になり_メッセージ中のキーは伏せ_codeを含む任意の文字列はログに出さない()
    {
        const string Spoken = "秘密の発言です";
        await using var server = new LiveServer(async c =>
        {
            await c.HandshakeAsync();
            // サービスが返す任意の文字列(code に API キーや発言相当の文字列)は、画面向けのメッセージにだけ使い、ログへ出さない
            await c.SendAsync($"{{\"type\":\"error\",\"error\":{{\"type\":\"invalid_request_error\",\"code\":\"{Key}\",\"message\":\"rejected {Key} {Spoken}\"}}}}");
            await c.Closed;
        });
        var log = new ConcurrentQueue<string>();
        var s = New(server, log);
        s.Start();
        s.Enqueue(Pcm(4800, 1));
        var e = await FailureOf(s);
        Assert.Equal(TranscriptionErrorKind.Other, e.Kind);
        Assert.Contains("rejected", e.Message);
        Assert.DoesNotContain(Key, e.Message);
        Assert.Contains(log, l => l.Contains(" stage=server_error "));
        Assert.All(log, l =>
        {
            Assert.DoesNotContain(Key, l);
            Assert.DoesNotContain(Spoken, l);
            Assert.DoesNotContain("code=", l);
        });
        Assert.Contains(Spoken, e.Message);               // 画面向けには残す(キーだけ伏せる)
    }

    [Fact]
    public async Task 文字起こしの失敗イベントも失敗になる()
    {
        await using var server = new LiveServer(async c =>
        {
            await c.HandshakeAsync();
            await c.ReadUntilCommitAsync();
            await c.SendAsync("{\"type\":\"conversation.item.input_audio_transcription.failed\",\"item_id\":\"item1\",\"error\":{\"code\":\"x\",\"message\":\"decode failed\"}}");
            await c.Closed;
        });
        var s = New(server);
        s.Start();
        s.Enqueue(Pcm(4800, 1));
        s.Complete();
        var e = await FailureOf(s);
        Assert.Equal(TranscriptionErrorKind.Other, e.Kind);
        Assert.Contains("decode failed", e.Message);
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("{\"nothing\":1}")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"type\":\"conversation.item.input_audio_transcription.delta\",\"item_id\":\"i\"}")]
    [InlineData("{\"type\":\"conversation.item.input_audio_transcription.completed\",\"item_id\":\"i\"}")]
    public async Task 壊れた応答は失敗になる(string payload)
    {
        await using var server = new LiveServer(async c =>
        {
            await c.HandshakeAsync();
            await c.SendAsync(payload);
            await c.Closed;
        });
        var s = New(server);
        s.Start();
        s.Enqueue(Pcm(4800, 1));
        var e = await FailureOf(s);
        Assert.Equal(TranscriptionErrorKind.InvalidResponse, e.Kind);
    }

    [Fact]
    public async Task バイナリと過大なメッセージは失敗になる()
    {
        await using var binary = new LiveServer(async c =>
        {
            await c.HandshakeAsync();
            await c.SendBinaryAsync();
            await c.Closed;
        });
        var s1 = New(binary);
        s1.Start();
        Assert.Equal(TranscriptionErrorKind.InvalidResponse, (await FailureOf(s1)).Kind);

        await using var huge = new LiveServer(async c =>
        {
            await c.HandshakeAsync();
            var json = LiveConnection.Delta("i", new string('a', 1_300_000));
            await c.SendFragmentedAsync(json, 400_000, 800_000);
            await c.Closed;
        });
        var s2 = New(huge);
        s2.Start();
        Assert.Equal(TranscriptionErrorKind.InvalidResponse, (await FailureOf(s2)).Kind);
    }

    [Fact]
    public async Task 空の全文は結果が空の失敗にする()
    {
        await using var server = Answering("  \n ");
        var s = New(server);
        s.Start();
        s.Enqueue(Pcm(4800, 1));
        s.Complete();
        Assert.Equal(TranscriptionErrorKind.EmptyResult, (await FailureOf(s)).Kind);
    }

    [Fact]
    public async Task 音声が1つも無いままCompleteしてもcommitは送らず失敗にする()
    {
        await using var server = new LiveServer(async c =>
        {
            await c.HandshakeAsync();
            await c.Closed;
        });
        var s = New(server);
        s.Start();
        s.Complete();
        Assert.Equal(TranscriptionErrorKind.EmptyResult, (await FailureOf(s)).Kind);
        await server.Done;
        Assert.Equal(new[] { "session.update" }, server.Connection!.SeenTypes);
    }

    [Theory]
    [InlineData(401, TranscriptionErrorKind.Auth)]
    [InlineData(403, TranscriptionErrorKind.Auth)]
    [InlineData(404, TranscriptionErrorKind.BadRequest)]
    [InlineData(429, TranscriptionErrorKind.RateLimit)]
    [InlineData(500, TranscriptionErrorKind.Server)]
    public async Task 接続を断られたらバッチと同じ分類の日本語メッセージで失敗し_キーは含まれない(int status, TranscriptionErrorKind kind)
    {
        await using var server = new LiveServer(_ => Task.CompletedTask, rejectStatus: status);
        var s = New(server);
        s.Start();
        var e = await FailureOf(s);
        Assert.Equal(kind, e.Kind);
        Assert.DoesNotContain(Key, e.Message);
        Assert.Contains($"HTTP {status}", e.Message);
        Assert.Equal(Key, server.ApiKeyHeader);
    }

    [Fact]
    public async Task リダイレクトは追従せず_リダイレクト先へキーを送らない()
    {
        await using var other = new LiveServer(async c => await c.Closed);
        await using var server = new LiveServer(_ => Task.CompletedTask, 302, other.Endpoint + "openai/v1/realtime?intent=transcription");
        var s = New(server);
        s.Start();
        var e = await FailureOf(s);
        Assert.Equal(TranscriptionErrorKind.Redirect, e.Kind);
        await Task.Delay(300);
        Assert.Null(other.ApiKeyHeader);
        Assert.Null(other.Connection);
    }

    [Fact]
    public async Task 接続できないエンドポイントはネットワークの失敗になる()
    {
        var p = TestProfiles.Create(ProviderKind.AzureOpenAiLive);
        p.Endpoint = "http://localhost:1/";
        var s = new LiveTranscriptionSession(p, Key);
        s.Start();
        var e = await FailureOf(s);
        Assert.Equal(TranscriptionErrorKind.Network, e.Kind);
        Assert.DoesNotContain(Key, e.Message);
    }

    // ---- タイムアウト ----

    [Fact]
    public async Task 接続が完了しなければタイムアウトで失敗する()
    {
        LiveConnect never = async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        };
        var s = New(null, connect: TimeSpan.FromMilliseconds(300), connector: never);
        s.Start();
        s.Enqueue(Pcm(4800, 1));
        Assert.Equal(TranscriptionErrorKind.Timeout, (await FailureOf(s)).Kind);
    }

    [Fact]
    public async Task session_updatedが届かなければタイムアウトで失敗し_音声は送らない()
    {
        await using var server = new LiveServer(async c =>
        {
            await c.SendAsync("{\"type\":\"session.created\",\"session\":{}}");
            await c.NextAsync();   // session.update は受けるが、updated は返さない
            await c.Closed;
        });
        var s = New(server, connect: TimeSpan.FromMilliseconds(400));
        s.Start();
        s.Enqueue(Pcm(4800, 1));
        s.Complete();
        Assert.Equal(TranscriptionErrorKind.Timeout, (await FailureOf(s)).Kind);
        await server.Done;
        Assert.Equal(new[] { "session.update" }, server.Connection!.SeenTypes);
    }

    [Fact]
    public async Task commitの後に全文が返らなければタイムアウトで失敗する()
    {
        await using var server = new LiveServer(async c =>
        {
            await c.HandshakeAsync();
            await c.ReadUntilCommitAsync();
            await c.Closed;        // 何も返さない
        });
        var s = New(server, finish: TimeSpan.FromMilliseconds(500));
        s.Start();
        s.Enqueue(Pcm(4800, 1));
        s.Complete();
        Assert.Equal(TranscriptionErrorKind.Timeout, (await FailureOf(s)).Kind);
    }

    [Fact]
    public async Task 終了待ちのタイムアウトは停止してから数え始める_録音中は待たされても失敗しない()
    {
        await using var server = Answering("録音が長くても大丈夫");
        var s = New(server, finish: TimeSpan.FromMilliseconds(300));
        s.Start();
        s.Enqueue(Pcm(4800, 1));
        await Task.Delay(900);                 // 終了待ちの 300 ms を超えて録音が続く
        Assert.False(s.Result.IsCompleted);
        s.Complete();
        Assert.Equal("録音が長くても大丈夫", await s.Result.WaitAsync(Wait));
    }

    // ---- 取消 ----

    [Fact]
    public async Task 接続中に取消したら何も送らず接続を閉じる()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LiveConnect delayed = async (uri, key, _) =>
        {
            await gate.Task;
            return await LiveTranscriptionSession.ConnectAsync(uri, key, CancellationToken.None); // 取消を無視して接続が完了してしまう場合
        };
        await using var server = new LiveServer(async c => await c.Closed);
        var s = New(server, connector: delayed);
        s.Start();
        s.Enqueue(Pcm(4800, 1));
        s.Complete();
        s.Cancel();
        gate.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Result.WaitAsync(Wait));
        await server.Done;
        Assert.Empty(server.Connection!.Seen);
    }

    [Fact]
    public async Task 準備完了の前に取消したら_積んだ音声もcommitも送らない()
    {
        var updateSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LiveServer(async c =>
        {
            await c.SendAsync("{\"type\":\"session.created\",\"session\":{}}");
            await c.NextAsync();
            updateSeen.SetResult();
            await c.Closed;
        });
        var s = New(server);
        s.Start();
        s.Enqueue(Pcm(4800, 1));
        s.Complete();                          // 停止済みだが、まだ ready ではない
        await updateSeen.Task.WaitAsync(Wait);
        s.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Result.WaitAsync(Wait));
        await server.Done;
        Assert.Equal(new[] { "session.update" }, server.Connection!.SeenTypes);
    }

    [Fact]
    public async Task 送信中の取消はそれ以降を送らず_commitしない()
    {
        var gotTwo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LiveServer(async c =>
        {
            await c.HandshakeAsync();
            await c.NextAsync();
            await c.NextAsync();
            gotTwo.SetResult();
            await c.Closed;
        });
        var s = New(server);
        s.Start();
        s.Enqueue(Pcm(4800, 1));
        s.Enqueue(Pcm(4800, 2));
        await gotTwo.Task.WaitAsync(Wait);
        s.Cancel();

        Assert.False(s.Enqueue(Pcm(4800, 3)));  // 取消後は積まれない
        s.Complete();                           // 取消後の Complete は何も起こさない
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Result.WaitAsync(Wait));
        await server.Done;
        Assert.Equal(new[] { "session.update", "input_audio_buffer.append", "input_audio_buffer.append" }, server.Connection!.SeenTypes);
    }

    [Fact]
    public async Task 結果が確定した後の取消は結果を変えない()
    {
        await using var server = Answering("確定済み");
        var s = New(server);
        s.Start();
        s.Enqueue(Pcm(4800, 1));
        s.Complete();
        Assert.Equal("確定済み", await s.Result.WaitAsync(Wait));
        s.Cancel();
        Assert.True(s.Result.IsCompletedSuccessfully);
        Assert.Equal("確定済み", await s.Result);
    }

    [Fact]
    public async Task 取消した旧セッションの遅延イベントは新しいセッションの結果にも表示にも混ざらない()
    {
        var oldGot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var oldServer = new LiveServer(async c =>
        {
            await c.HandshakeAsync();
            await c.NextAsync();
            oldGot.SetResult();
            await release.Task;
            try   // 取消されたクライアントへ、遅れて結果を送ろうとする(届かない、または届いても無視される)
            {
                await c.SendAsync(LiveConnection.Delta("old", "古い文字"));
                await c.SendAsync(LiveConnection.Committed("old"));
                await c.SendAsync(LiveConnection.Completed("old", "古い結果"));
            }
            catch (Exception) { }
        });
        var oldSession = New(oldServer);
        int oldPartialEvents = 0;
        oldSession.PartialChanged += () => Interlocked.Increment(ref oldPartialEvents);
        oldSession.Start();
        oldSession.Enqueue(Pcm(4800, 1));
        await oldGot.Task.WaitAsync(Wait);
        oldSession.Cancel();

        await using var newServer = new LiveServer(async c =>
        {
            await c.HandshakeAsync();
            await c.ReadUntilCommitAsync(async n => await c.SendAsync(LiveConnection.Delta("new", "新しい途中")));
            await c.SendAsync(LiveConnection.Committed("new"));
            await c.SendAsync(LiveConnection.Completed("new", "新しい結果"));
        });
        var newSession = New(newServer);
        newSession.Start();
        newSession.Enqueue(Pcm(4800, 2));
        release.SetResult();
        newSession.Complete();

        Assert.Equal("新しい結果", await newSession.Result.WaitAsync(Wait));
        await Task.Delay(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldSession.Result.WaitAsync(Wait));
        Assert.Equal(0, Volatile.Read(ref oldPartialEvents));
        Assert.Equal("", oldSession.GetPartialTail(100));
        Assert.StartsWith("新しい途中", newSession.GetPartialTail(100));
    }

    // ---- 途中表示 ----

    [Fact]
    public async Task 途中表示は末尾の指定文字数に収まり_改行を含まず_サロゲートペアを割らない()
    {
        string text = new string('あ', 300) + "\n" + new string('い', 40) + "𠮷" + new string('う', 48); // max=50 の切れ目が低位サロゲートに当たる
        await using var server = new LiveServer(async c =>
        {
            await c.HandshakeAsync();
            foreach (var part in new[] { text[..200], text[200..341], text[341..] })
                await c.SendAsync(LiveConnection.Delta("item1", part));
            await c.Closed;
        });
        var s = New(server);
        s.Start();
        s.Enqueue(Pcm(4800, 1));
        Assert.True(SpinWait.SpinUntil(() => s.GetPartialTail(100000).EndsWith("う"), 8000));
        Assert.True(SpinWait.SpinUntil(() => s.GetPartialTail(100000).Length >= text.Length, 8000));

        string tail = s.GetPartialTail(50);
        Assert.True(tail.Length <= 50);
        Assert.StartsWith("…", tail);
        Assert.EndsWith(new string('う', 48), tail);
        Assert.DoesNotContain('\n', tail);
        Assert.False(char.IsLowSurrogate(tail[1]));                        // 対の片方だけが残らない
        Assert.Equal(text.Replace('\n', ' '), s.GetPartialTail(text.Length)); // 収まる長さなら全部(改行は空白)
        s.Cancel();
    }

    // ---- 診断ログ ----

    [Fact]
    public async Task 診断ログは段階と時間と長さだけで_音声_base64_発言_キー_ヘッダーを含まない()
    {
        const string Secret = "秘密の発言です";
        var chunk = Pcm(4800, 5);
        string base64 = Convert.ToBase64String(chunk);
        await using var server = new LiveServer(async c =>
        {
            await c.HandshakeAsync();
            await c.ReadUntilCommitAsync(async n => await c.SendAsync(LiveConnection.Delta("i", Secret)));
            await c.SendAsync(LiveConnection.Committed("i"));
            await c.SendAsync(LiveConnection.Completed("i", Secret));
        });
        var log = new ConcurrentQueue<string>();
        var s = New(server, log);
        var partial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.PartialChanged += () => partial.TrySetResult();
        s.Start();
        s.Enqueue((byte[])chunk.Clone());
        await partial.Task.WaitAsync(Wait);   // 録音中に最初の delta が届いてから停止する(実際の流れ)
        s.Complete();
        Assert.Equal(Secret, await s.Result.WaitAsync(Wait));
        await server.Done;

        // final は結果を確定させた後に書くため、Result の完了より少し遅れることがある
        Assert.True(SpinWait.SpinUntil(() => log.Any(l => l.Contains(" stage=final ")), Wait));
        var lines = log.ToArray();
        // commit_sent は送信の完了後に書くため、受信側の committed と前後することがある。順序は audio_complete の後であることだけを見る
        string[] expectedOrder = { "connect_started", "connected", "session_updated", "send_started", "first_delta", "audio_complete", "committed", "final" };
        int at = 0;
        foreach (var stage in expectedOrder)
        {
            int found = Array.FindIndex(lines, at, l => l.Contains($" stage={stage} "));
            Assert.True(found >= 0, $"{stage} が順序どおりに記録されていません:\n{string.Join('\n', lines)}");
            at = found + 1;
        }
        Assert.True(Array.FindIndex(lines, l => l.Contains(" stage=commit_sent ")) > Array.FindIndex(lines, l => l.Contains(" stage=audio_complete ")),
            $"commit_sent が audio_complete の後に記録されていません:\n{string.Join('\n', lines)}");
        Assert.Contains(lines, l => l.Contains("stage=commit_sent") && l.Contains("audio_bytes=4800"));
        Assert.Contains(lines, l => l.Contains("stage=final") && l.Contains($"chars={Secret.Length}"));
        Assert.All(lines, l =>
        {
            Assert.StartsWith("live id=", l);
            Assert.Contains("elapsed_ms=", l);
            Assert.DoesNotContain(Secret, l);
            Assert.DoesNotContain(Key, l);
            Assert.DoesNotContain("api-key", l, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(base64[..24], l);
            Assert.DoesNotContain("{", l);
        });
    }

    [Fact]
    public async Task 失敗と取消も種別だけをログに残す()
    {
        await using var failing = new LiveServer(async c =>
        {
            await c.HandshakeAsync();
            await c.NextAsync();
            c.Abort();
        });
        var log = new ConcurrentQueue<string>();
        var s = New(failing, log);
        s.Start();
        s.Enqueue(Pcm(4800, 1));
        await FailureOf(s);
        // failed は結果を失敗にした後に書くため、Result の完了より少し遅れることがある
        Assert.True(SpinWait.SpinUntil(() => log.Any(l => l.Contains("stage=failed ")), Wait));
        Assert.Contains(log, l => l.Contains("stage=failed ") && l.EndsWith(" kind=Network"));

        await using var waiting = new LiveServer(async c =>
        {
            await c.HandshakeAsync();
            await c.Closed;
        });
        var log2 = new ConcurrentQueue<string>();
        var s2 = New(waiting, log2);
        s2.Start();
        s2.Enqueue(Pcm(4800, 1));
        Assert.True(SpinWait.SpinUntil(() => log2.Any(l => l.Contains("stage=send_started")), 8000));
        s2.Cancel();
        Assert.Contains(log2, l => l.Contains("stage=cancelled"));
        Assert.DoesNotContain(log2, l => l.Contains(Key));
    }
    [Fact]
    public async Task Live費用はキューを除外し送信完了したPCMを数える()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LiveServer(async c =>
        {
            await c.HandshakeAsync();
            await c.ReadUntilCommitAsync();
            received.TrySetResult();
            await c.SendAsync(LiveConnection.Committed("cost"));
            await c.SendAsync(LiveConnection.Completed("cost", "test"));
        });
        var profile = TestProfiles.Create(ProviderKind.AzureOpenAiLive);
        profile.Endpoint = server.Endpoint.AbsoluteUri;
        profile.LiveUsdPerMinute = 0.017m;
        var session = new LiveTranscriptionSession(profile, Key);
        session.Enqueue(new byte[48000]);
        Assert.Equal(0m, session.GetCostSnapshot().SentSeconds); // キューに積むだけでは課金対象と数えない
        session.Start();
        session.Complete();
        await session.Result.WaitAsync(Wait);
        await received.Task.WaitAsync(Wait);
        var usage = session.GetCostSnapshot();
        Assert.Equal(1m, usage.SentSeconds);
        Assert.Equal(0.017m, usage.UsdPerMinute);
        Assert.False(usage.SendUncertain);
        session.Cancel();
        Assert.Equal(usage, session.GetCostSnapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Live費用は取消やエラーでも送信済み分を保持する(bool serverError)
    {
        var appended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LiveServer(async c =>
        {
            await c.HandshakeAsync();
            Assert.Equal("input_audio_buffer.append", LiveConnection.TypeOf((await c.NextAsync())!));
            appended.SetResult();
            await finish.Task;
            if (serverError) await c.SendAsync("{\"type\":\"error\",\"error\":{\"code\":\"failed\"}}");
            else await c.Closed;
        });
        var session = New(server);
        session.Enqueue(new byte[48000]);
        session.Start();
        await appended.Task.WaitAsync(Wait);
        var deadline = DateTime.UtcNow + Wait;
        while (session.GetCostSnapshot().SentSeconds == 0 && DateTime.UtcNow < deadline) await Task.Delay(1);
        Assert.Equal(1m, session.GetCostSnapshot().SentSeconds);
        finish.SetResult();
        if (serverError) await FailureOf(session);
        else
        {
            session.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.Result.WaitAsync(Wait));
        }
        Assert.Equal(1m, session.GetCostSnapshot().SentSeconds);
        await server.Done;
    }

}
