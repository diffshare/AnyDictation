using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace AnyDictation;

/// <summary>WebSocket を開く関数。テストでは別のサーバーへ向ける。</summary>
public delegate Task<WebSocket> LiveConnect(Uri uri, string apiKey, CancellationToken ct);

/// <summary>
/// Azure OpenAI の Realtime transcription(gpt-live-transcribe)への 1 回分の接続。録音 1 回(または保持音声の再送 1 回)に 1 つ作り、使い捨てる。
///   Enqueue(録音中、デバイスのスレッドから。ブロックしない) → 送信ループが session.updated の後に append を順に送る
///   Complete(最後のバッファまで Enqueue した後) → 送信キューを流し切り、commit を 1 回だけ送る
///   Result: commit で確定した項目の completed の全文。delta は途中表示だけで、結果にも履歴にも使わない
/// 失敗(切断・タイムアウト・error・不正な応答)は Result が TranscriptionException で終わる。自動再接続も別サービスへの fallback もしない。
/// 呼び出し側は音声を別に保持しておき、失敗したら手動で再送する(新しいセッションで全部送り直す)。
/// Cancel はこれ以上送らず接続を捨てる。送信済みの音声は取り消せない。
/// ログ(log)には段階名・経過時間・長さ・種別だけを渡す。音声、base64、発言、キー、ヘッダーは渡さない。
/// </summary>
public sealed class LiveTranscriptionSession
{
    public const int SampleRate = 24000;
    public const int ChunkBytes = SampleRate / 10 * 2; // 100 ms の PCM16 mono
    public const string RealtimePath = "/openai/v1/realtime?intent=transcription";
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DefaultFinishTimeout = TimeSpan.FromSeconds(90);

    const int MaxMessageBytes = 1 << 20;
    const string DeltaType = "conversation.item.input_audio_transcription.delta";
    const string CompletedType = "conversation.item.input_audio_transcription.completed";
    const string FailedType = "conversation.item.input_audio_transcription.failed";
    const string CommitMessage = "{\"type\":\"input_audio_buffer.commit\"}";

    // リダイレクトは追従しない(キー付きヘッダーを別ホストへ送らないため)。HTTP の取得と同じ設定を共有する
    static readonly HttpMessageInvoker Invoker = new(TranscriptionClients.CreateHandler(), disposeHandler: false);
    static int _nextId;

    readonly int _id = Interlocked.Increment(ref _nextId);
    readonly Profile _profile;
    readonly string _apiKey;
    readonly Uri _uri;
    readonly LiveConnect _connect;
    readonly Action<string>? _log;
    readonly TimeSpan _connectTimeout;
    readonly TimeSpan _finishTimeout;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly Channel<byte[]> _audio = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    readonly CancellationTokenSource _cts = new();
    readonly TaskCompletionSource<string> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    readonly object _gate = new();
    readonly StringBuilder _partial = new();
    readonly Dictionary<string, string> _completed = new();
    string? _committedItemId;
    bool _commitSent;
    bool _firstDeltaLogged;
    WebSocket? _ws;
    int _started;
    long _sentAudioBytes;
    int _sendInProgress;

    public LiveCostSnapshot GetCostSnapshot() => new(Interlocked.Read(ref _sentAudioBytes) / (decimal)(SampleRate * 2),
        _profile.LiveUsdPerMinute, Volatile.Read(ref _sendInProgress) != 0);

    /// <summary>エンドポイントかキーが不正なら、送信前に TranscriptionException(Configuration) を投げる。</summary>
    public LiveTranscriptionSession(Profile profile, string apiKey, LiveConnect? connect = null, Action<string>? log = null,
        TimeSpan? connectTimeout = null, TimeSpan? finishTimeout = null)
    {
        if (apiKey.Length == 0)
            throw new TranscriptionException(TranscriptionErrorKind.Configuration, "APIキーが未設定です。設定画面で入力してください。");
        _uri = BuildUri(profile);
        _profile = profile.Clone();
        _apiKey = apiKey;
        _connect = connect ?? ConnectAsync;
        _log = log;
        _connectTimeout = connectTimeout ?? DefaultConnectTimeout;
        _finishTimeout = finishTimeout ?? DefaultFinishTimeout;
    }

    public Profile Profile => _profile;

    /// <summary>確定した全文。失敗は TranscriptionException、Cancel は取消で終わる。</summary>
    public Task<string> Result => _result.Task;

    /// <summary>途中経過(delta)が増えた。受信スレッドから呼ばれる。UI は触らず、投げ直すこと。</summary>
    public event Action? PartialChanged;

    /// <summary>profile の HTTPS(loopback のみ HTTP)のベース URL から WSS(同 WS)の URL を作る。model や api-version、キーはクエリに置かない。</summary>
    public static Uri BuildUri(Profile profile)
    {
        string http = TranscriptionClients.BuildUri(profile, RealtimePath).AbsoluteUri;
        return new Uri(http.StartsWith("https:", StringComparison.Ordinal) ? "wss" + http[5..] : "ws" + http[4..]);
    }

    /// <summary>キーは api-key ヘッダーだけに載せる。HTTP のステータスで断られた場合は、バッチと同じ分類の日本語メッセージにする。</summary>
    public static async Task<WebSocket> ConnectAsync(Uri uri, string apiKey, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        try
        {
            ws.Options.SetRequestHeader("api-key", apiKey);
            ws.Options.CollectHttpResponseDetails = true; // 接続を断られたときの HTTP ステータスを分類に使う
            // 無音で切れた接続を、録音中でも検出して失敗にする
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
            await ws.ConnectAsync(uri, Invoker, ct).ConfigureAwait(false);
            return ws;
        }
        catch (WebSocketException)
        {
            var status = ws.HttpStatusCode;
            ws.Dispose();
            if ((int)status >= 300) throw TranscriptionClients.MapError(status, "", apiKey);
            throw;
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    /// <summary>接続を始める(待たない)。録音の開始と並行して呼ぶ。接続前に Enqueue された音声は順にためておく。</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) _ = RunAsync();
    }

    /// <summary>
    /// 音声(PCM16 mono 24 kHz)を送信キューへ積む。pcm は以後この対象が所有する。デバイスのスレッドから呼べ、ブロックしない。
    /// 失敗・取消・Complete 済みなら false(音声は呼び出し側が別に保持している)。
    /// </summary>
    public bool Enqueue(byte[] pcm) => _audio.Writer.TryWrite(pcm);

    /// <summary>保持した音声を 100 ms ごとに分けて積む(再送用)。</summary>
    public void EnqueueAll(ReadOnlyMemory<byte> pcm)
    {
        for (int i = 0; i < pcm.Length; i += ChunkBytes)
            Enqueue(pcm.Slice(i, Math.Min(ChunkBytes, pcm.Length - i)).ToArray());
    }

    /// <summary>これ以上音声は来ない。積んだ音声を送り切ってから commit を 1 回送る。2 回目以降は何もしない。</summary>
    public void Complete()
    {
        if (_result.Task.IsCompleted || !_audio.Writer.TryComplete()) return;
        Trace("audio_complete");
        _ = WatchFinishAsync();
    }

    /// <summary>以後は何も送らず、接続を捨てる。結果が出た後は何もしない。</summary>
    public void Cancel()
    {
        if (!_result.TrySetCanceled()) return;
        Trace("cancelled");
        Terminate(abort: true);
    }

    /// <summary>途中経過の末尾 maxChars 文字(改行は空白にする)。表示用で、結果ではない。</summary>
    public string GetPartialTail(int maxChars)
    {
        lock (_gate)
        {
            int length = _partial.Length;
            if (length <= maxChars) return Flatten(_partial.ToString());
            int start = length - (maxChars - 1);
            if (char.IsLowSurrogate(_partial[start])) start++;
            return "…" + Flatten(_partial.ToString(start, length - start));
        }
    }

    static string Flatten(string s) => s.Replace('\r', ' ').Replace('\n', ' ');

    // ---- 実行 ----

    async Task RunAsync()
    {
        WebSocket? ws = null;
        try
        {
            Trace("connect_started");
            ws = await ConnectWithTimeoutAsync().ConfigureAwait(false);
            _ws = ws;
            _cts.Token.ThrowIfCancellationRequested(); // 接続中に取消されていたら、何も送らず閉じる
            Trace("connected");
            var receive = ReceiveLoopAsync(ws);
            await SendTextAsync(ws, BuildSessionUpdate()).ConfigureAwait(false);
            try
            {
                await _ready.Task.WaitAsync(_connectTimeout, _cts.Token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TranscriptionException(TranscriptionErrorKind.Timeout,
                    $"サービスの準備完了が {_connectTimeout.TotalSeconds:0} 秒以内に届きませんでした(タイムアウト)。再送してください。");
            }
            await SendAudioAndCommitAsync(ws).ConfigureAwait(false);
            await receive.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Fail(Map(e));
        }
        finally
        {
            while (_audio.Reader.TryRead(out var chunk)) Array.Clear(chunk);
            if (ws != null) await CloseAsync(ws).ConfigureAwait(false);
        }
    }

    async Task<WebSocket> ConnectWithTimeoutAsync()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        cts.CancelAfter(_connectTimeout);
        try
        {
            return await _connect(_uri, _apiKey, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
        {
            throw new TranscriptionException(TranscriptionErrorKind.Timeout,
                $"サービスへの接続が {_connectTimeout.TotalSeconds:0} 秒以内に完了しませんでした(タイムアウト)。ネットワークとエンドポイントを確認し、再送してください。");
        }
    }

    /// <summary>
    /// ready 後、積まれた音声を順に送り、キューが閉じたら commit を 1 回送る。送信はここだけで行う(WebSocket は同時送信できない)。
    /// キューが閉じた理由が Complete でなく失敗・取消なら、結果は既に確定しているので commit しない。
    /// </summary>
    async Task SendAudioAndCommitAsync(WebSocket ws)
    {
        long sent = 0;
        await foreach (var chunk in _audio.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
        {
            if (sent == 0) Trace("send_started");
            Volatile.Write(ref _sendInProgress, 1);
            await SendTextAsync(ws, "{\"type\":\"input_audio_buffer.append\",\"audio\":\"" + Convert.ToBase64String(chunk) + "\"}").ConfigureAwait(false);
            Interlocked.Add(ref _sentAudioBytes, chunk.Length);
            Volatile.Write(ref _sendInProgress, 0);
            sent += chunk.Length;
            Array.Clear(chunk);
        }
        if (_result.Task.IsCompleted) return;
        if (sent == 0)
            throw new TranscriptionException(TranscriptionErrorKind.EmptyResult, "送信できる音声がありませんでした。");
        lock (_gate) _commitSent = true; // 応答(committed)が届く前に立てる
        await SendTextAsync(ws, CommitMessage).ConfigureAwait(false);
        Trace("commit_sent", $" audio_bytes={sent}");
    }

    async Task ReceiveLoopAsync(WebSocket ws)
    {
        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();
        try
        {
            while (!_result.Task.IsCompleted)
            {
                message.SetLength(0);
                ValueWebSocketReceiveResult r;
                do // 1 つの JSON が複数のフレームに分かれて届いても、EndOfMessage まで連結する
                {
                    r = await ws.ReceiveAsync(buffer.AsMemory(), _cts.Token).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        Trace("closed_by_server", $" close_status={ws.CloseStatus}");
                        throw new TranscriptionException(TranscriptionErrorKind.Network,
                            "サービスが接続を閉じました。結果を受け取る前に切断されています。");
                    }
                    if (r.MessageType != WebSocketMessageType.Text)
                        throw new TranscriptionException(TranscriptionErrorKind.InvalidResponse, "サービスの応答を解釈できませんでした(バイナリ形式のメッセージ)。");
                    message.Write(buffer, 0, r.Count);
                    if (message.Length > MaxMessageBytes)
                        throw new TranscriptionException(TranscriptionErrorKind.InvalidResponse, "サービスの応答を解釈できませんでした(メッセージが大きすぎます)。");
                } while (!r.EndOfMessage);
                Handle(message.GetBuffer().AsMemory(0, (int)message.Length));
            }
        }
        catch (Exception e)
        {
            Fail(Map(e));
        }
    }

    void Handle(ReadOnlyMemory<byte> message)
    {
        if (_result.Task.IsCompleted) return; // 取消・失敗・確定の後に届いた遅延イベントは、状態にも表示にも反映しない
        using var doc = JsonDocument.Parse(message);
        var root = doc.RootElement;
        string type = Str(root, "type") ?? throw Invalid("type がありません");
        switch (type)
        {
            case "session.created":
                Trace("session_created");
                break;
            case "session.updated":
                Trace("session_updated");
                _ready.TrySetResult();
                break;
            case "input_audio_buffer.committed":
                // どの項目が自分の commit の結果かを決める唯一の手がかり。無い/空なら、別項目の結果を取り違えかねないので失敗にする
                string committedId = Str(root, "item_id") is { Length: > 0 } id ? id : throw Invalid("committed に item_id がありません");
                lock (_gate)
                {
                    if (!_commitSent) throw Invalid("要求していない確定(committed)を受信しました");
                    _committedItemId = committedId;
                }
                Trace("committed");
                ResolveFinal();
                break;
            case DeltaType:
                OnDelta(Str(root, "delta") ?? throw Invalid("delta がありません"));
                break;
            case CompletedType:
                string transcript = Str(root, "transcript") ?? throw Invalid("transcript がありません");
                lock (_gate) _completed[Str(root, "item_id") ?? ""] = transcript;
                ResolveFinal();
                break;
            case FailedType:
            case "error":
                throw ServerError(Encoding.UTF8.GetString(message.Span));
            default:
                break; // conversation.item.added / done など、結果に関わらない通知
        }
    }

    void OnDelta(string delta)
    {
        bool first;
        lock (_gate)
        {
            _partial.Append(delta);
            first = !_firstDeltaLogged;
            _firstDeltaLogged = true;
        }
        if (first) Trace("first_delta");
        PartialChanged?.Invoke();
    }

    /// <summary>commit で確定した項目の completed が揃ったら結果にする。届く順序(committed と completed のどちらが先か)に依らない。</summary>
    void ResolveFinal()
    {
        string? text;
        lock (_gate)
        {
            if (_committedItemId == null) return;
            text = _completed.GetValueOrDefault(_committedItemId);
        }
        if (text == null) return;
        text = text.Trim();
        if (text.Length == 0) Fail(new TranscriptionException(TranscriptionErrorKind.EmptyResult, "音声から文字を認識できませんでした(結果が空)。"));
        else Succeed(text);
    }

    TranscriptionException ServerError(string raw)
    {
        Trace("server_error"); // サービスが返す文字列(code を含む)はログに出さない。画面向けの detail だけ下で作る
        string detail = TranscriptionClients.ExtractDetail(raw, _apiKey);
        return new(TranscriptionErrorKind.Other, "サービスがエラーを返しました。" + (detail.Length > 0 ? $" サービスのメッセージ: {detail}" : ""));
    }

    // ---- 終了 ----

    void Succeed(string text)
    {
        if (!_result.TrySetResult(text)) return;
        Trace("final", $" chars={text.Length}");
        Terminate(abort: false);
    }

    void Fail(TranscriptionException e)
    {
        if (!_result.TrySetException(e)) return;
        Trace("failed", $" kind={e.Kind}");
        Terminate(abort: true);
    }

    /// <summary>これ以上積ませず、待機中の送受信を止める。成功時は接続を abort せず、後始末で閉じる。</summary>
    void Terminate(bool abort)
    {
        _audio.Writer.TryComplete();
        _cts.Cancel();
        if (abort) _ws?.Abort();
    }

    async Task WatchFinishAsync()
    {
        try
        {
            await Task.Delay(_finishTimeout, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // 結果が出た(または取消された)
        }
        Fail(new TranscriptionException(TranscriptionErrorKind.Timeout,
            $"録音の終了から {_finishTimeout.TotalSeconds:0} 秒以内に認識結果が返りませんでした(タイムアウト)。再送してください。"));
    }

    static async Task CloseAsync(WebSocket ws)
    {
        try
        {
            if (ws.State == WebSocketState.Open && ws.CloseStatus == null)
            {
                using var close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, close.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // 閉じ方の失敗は結果に影響しない
        }
        ws.Abort();
        ws.Dispose();
    }

    // ---- 送信メッセージと補助 ----

    /// <summary>公開済みの契約: 24 kHz PCM16、languages は言語が空なら省略、turn_detection は null(手動 commit)。</summary>
    internal string BuildSessionUpdate()
    {
        var transcription = new Dictionary<string, object> { ["model"] = _profile.Model };
        if (!string.IsNullOrEmpty(_profile.Language)) transcription["languages"] = new[] { _profile.Language };
        transcription["delay"] = "low";
        var update = new Dictionary<string, object?>
        {
            ["type"] = "session.update",
            ["session"] = new Dictionary<string, object?>
            {
                ["type"] = "transcription",
                ["audio"] = new Dictionary<string, object?>
                {
                    ["input"] = new Dictionary<string, object?>
                    {
                        ["format"] = new Dictionary<string, object> { ["type"] = "audio/pcm", ["rate"] = SampleRate },
                        ["transcription"] = transcription,
                        ["turn_detection"] = null,
                    },
                },
            },
        };
        return JsonSerializer.Serialize(update);
    }

    ValueTask SendTextAsync(WebSocket ws, string json)
        => ws.SendAsync(Encoding.UTF8.GetBytes(json).AsMemory(), WebSocketMessageType.Text, endOfMessage: true, _cts.Token);

    static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static TranscriptionException Invalid(string what)
        => new(TranscriptionErrorKind.InvalidResponse, $"サービスの応答を解釈できませんでした({what})。");

    static TranscriptionException Map(Exception e) => e switch
    {
        TranscriptionException t => t,
        JsonException => new(TranscriptionErrorKind.InvalidResponse, "サービスの応答を解釈できませんでした(JSON が不正です)。", e),
        WebSocketException or IOException or HttpRequestException or ObjectDisposedException => new(TranscriptionErrorKind.Network,
            "サービスに接続できない、または通信が切れました。ネットワークとエンドポイントを確認してください。", e),
        OperationCanceledException => new(TranscriptionErrorKind.Other, "通信が中断されました。", e),
        _ => new(TranscriptionErrorKind.Other, $"想定外のエラーが発生しました({e.GetType().Name})。", e),
    };

    void Trace(string stage, string extra = "")
        => _log?.Invoke(FormattableString.Invariant($"live id={_id} stage={stage} elapsed_ms={_clock.Elapsed.TotalMilliseconds:F1}{extra}"));
}
