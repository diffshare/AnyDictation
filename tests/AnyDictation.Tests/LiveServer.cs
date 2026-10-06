using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace AnyDictation.Tests;

/// <summary>
/// テスト用の Realtime transcription サーバー(loopback の HttpListener 上の本物の WebSocket)。
/// クライアントは本番と同じ ClientWebSocket で接続し、ヘッダー・URL・フレーム分割も実際の通信として検証できる。
/// script がサーバー側の振る舞い(いつ何を返すか)を書く。
/// </summary>
sealed class LiveServer : IAsyncDisposable
{
    readonly HttpListener _listener = new();
    readonly Func<LiveConnection, Task> _script;
    readonly int _rejectStatus;
    readonly string? _rejectLocation;
    readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Uri Endpoint { get; }
    public string? ApiKeyHeader { get; private set; }
    public string? AuthorizationHeader { get; private set; }
    public string? PathAndQuery { get; private set; }
    public LiveConnection? Connection { get; private set; }

    /// <summary>script が正常に終わると完了し、script 内の失敗(検証の例外)があればその例外で終わる。</summary>
    public Task Done => _done.Task.WaitAsync(TimeSpan.FromSeconds(15));

    public LiveServer(Func<LiveConnection, Task> script, int rejectStatus = 0, string? rejectLocation = null)
    {
        _script = script;
        _rejectStatus = rejectStatus;
        _rejectLocation = rejectLocation;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Endpoint = new Uri($"http://localhost:{port}/");
        _listener.Prefixes.Add(Endpoint.AbsoluteUri);
        _listener.Start();
        _ = AcceptAsync();
    }

    async Task AcceptAsync()
    {
        try
        {
            var context = await _listener.GetContextAsync();
            ApiKeyHeader = context.Request.Headers["api-key"];
            AuthorizationHeader = context.Request.Headers["Authorization"];
            PathAndQuery = context.Request.RawUrl;
            if (_rejectStatus != 0)
            {
                context.Response.StatusCode = _rejectStatus;
                if (_rejectLocation != null) context.Response.Headers["Location"] = _rejectLocation;
                context.Response.Close();
                _done.TrySetResult();
                return;
            }
            var ws = (await context.AcceptWebSocketAsync(null)).WebSocket;
            var connection = Connection = new LiveConnection(ws);
            await _script(connection);
            _done.TrySetResult();
        }
        catch (Exception e)
        {
            _done.TrySetException(e);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Connection?.Abort();
        _listener.Abort();
        await Task.WhenAny(_done.Task, Task.Delay(1000));
    }
}

/// <summary>サーバー側から見た 1 本の接続。受信は常時 1 つの待機で行い、メッセージ(フレームを連結済み)を順に取り出す。</summary>
sealed class LiveConnection
{
    readonly WebSocket _ws;
    readonly Channel<string?> _in = Channel.CreateUnbounded<string?>();
    readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>クライアントから届いたメッセージの全記録(順序どおり)。</summary>
    public ConcurrentQueue<string> Seen { get; } = new();

    /// <summary>クライアントの close または切断で受信が終わった。これ以降 Seen は増えない。</summary>
    public Task Closed => _closed.Task.WaitAsync(TimeSpan.FromSeconds(10));

    public IEnumerable<string> SeenTypes => Seen.Select(TypeOf);

    public LiveConnection(WebSocket ws)
    {
        _ws = ws;
        _ = PumpAsync();
    }

    async Task PumpAsync()
    {
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) { await _in.Writer.WriteAsync(null); return; }
                    message.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);
                string text = Encoding.UTF8.GetString(message.ToArray());
                Seen.Enqueue(text);
                await _in.Writer.WriteAsync(text);
            }
        }
        catch (Exception)
        {
            _in.Writer.TryWrite(null); // 接続が破棄された(クライアントの abort など)
        }
        finally
        {
            _in.Writer.TryComplete();
            _closed.TrySetResult();
        }
    }

    /// <summary>次のメッセージ。接続が閉じた/破棄されたなら null。来なければ TimeoutException。</summary>
    public async Task<string?> NextAsync(int timeoutMs = 8000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            return await _in.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("クライアントからのメッセージが届きませんでした。");
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    /// <summary>ms の間、クライアントから何も届かなかった(接続の終了も含めない)ことを確かめる。</summary>
    public async Task<bool> QuietAsync(int ms)
    {
        await Task.Delay(ms);
        return !_in.Reader.TryPeek(out _);
    }

    public static string TypeOf(string json) => JsonDocument.Parse(json).RootElement.GetProperty("type").GetString()!;

    public Task SendAsync(string json) => SendBytesAsync(Encoding.UTF8.GetBytes(json));

    Task SendBytesAsync(byte[] bytes) => _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>1 つの JSON を、指定のバイト位置で複数のフレームに分けて送る(UTF-8 の文字の途中でも分ける)。</summary>
    public async Task SendFragmentedAsync(string json, params int[] splitAt)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        int start = 0;
        foreach (int end in splitAt.Append(bytes.Length))
        {
            await _ws.SendAsync(bytes.AsMemory(start, end - start), WebSocketMessageType.Text, end == bytes.Length, CancellationToken.None);
            start = end;
        }
    }

    public Task SendBinaryAsync() => _ws.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, true, CancellationToken.None);

    public Task CloseAsync() => _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

    public void Abort() => _ws.Abort();

    /// <summary>session.created を送り、session.update を受け取って、session.updated を返す。受け取った session.update の JSON を返す。</summary>
    public async Task<string> HandshakeAsync()
    {
        await SendAsync("{\"type\":\"session.created\",\"session\":{}}");
        string update = await NextAsync() ?? throw new InvalidOperationException("session.update が届く前に切断されました。");
        if (TypeOf(update) != "session.update") throw new InvalidOperationException("最初のメッセージが session.update ではありません: " + TypeOf(update));
        await SendAsync("{\"type\":\"session.updated\",\"session\":{}}");
        return update;
    }

    /// <summary>
    /// commit が届くまでのメッセージを読む。append の PCM を順に返す。append と commit 以外が来たら失敗。
    /// afterAppend(何本目の append か)は、サーバー側で delta を返すなどの動作に使う。
    /// </summary>
    public async Task<List<byte[]>> ReadUntilCommitAsync(Func<int, Task>? afterAppend = null)
    {
        var appended = new List<byte[]>();
        while (true)
        {
            string text = await NextAsync() ?? throw new InvalidOperationException("commit が届く前に切断されました。");
            using var doc = JsonDocument.Parse(text);
            string type = doc.RootElement.GetProperty("type").GetString()!;
            if (type == "input_audio_buffer.commit") return appended;
            if (type != "input_audio_buffer.append") throw new InvalidOperationException("想定外のメッセージ: " + type);
            appended.Add(Convert.FromBase64String(doc.RootElement.GetProperty("audio").GetString()!));
            if (afterAppend != null) await afterAppend(appended.Count);
        }
    }

    // ---- サーバーから送るイベント(日本語は実サーバーと同じく生の UTF-8 で送る) ----

    static readonly JsonSerializerOptions Raw = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Delta(string item, string text) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["type"] = "conversation.item.input_audio_transcription.delta", ["item_id"] = item, ["content_index"] = 0, ["delta"] = text,
    }, Raw);

    public static string Completed(string item, string text) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["type"] = "conversation.item.input_audio_transcription.completed", ["item_id"] = item, ["content_index"] = 0, ["transcript"] = text,
    }, Raw);

    public static string Committed(string item) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["type"] = "input_audio_buffer.committed", ["item_id"] = item, ["previous_item_id"] = null,
    }, Raw);
}
