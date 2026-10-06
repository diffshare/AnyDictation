using System.Collections.Concurrent;
using Xunit;

namespace AnyDictation.Tests;

// ---- 指摘 2: 録音の停止確定(最後のバッファを落とさない / UI スレッドで待っても詰まらない) ----

sealed class SingleThreadContext : SynchronizationContext, IDisposable
{
    readonly BlockingCollection<(SendOrPostCallback, object?)> _queue = new();
    readonly Thread _thread;

    public SingleThreadContext()
    {
        _thread = new Thread(() =>
        {
            SetSynchronizationContext(this);
            foreach (var (cb, state) in _queue.GetConsumingEnumerable()) cb(state);
        }) { IsBackground = true };
        _thread.Start();
    }

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public Task<T> RunAsync<T>(Func<Task<T>> f)
    {
        var tcs = new TaskCompletionSource<T>();
        Post(async _ =>
        {
            try { tcs.SetResult(await f()); }
            catch (Exception e) { tcs.SetException(e); }
        }, null);
        return tcs.Task;
    }

    public void Dispose() => _queue.CompleteAdding();
}

public class CaptureSessionTests
{
    static byte[] Chunk(short value, int samples)
    {
        var b = new byte[samples * 2];
        for (int i = 0; i < samples; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 2), value);
        return b;
    }

    static int DataBytes(CaptureResult r) => r.Wav.Length - 44;

    [Fact]
    public async Task 停止要求の後に届く最後のバッファも録音に含まれる()
    {
        var s = new CaptureSession();
        s.Append(Chunk(1000, 1600), 3200);
        var result = await s.FinishAsync(() => Task.Run(async () =>
        {
            await Task.Delay(50); // デバイスは停止要求後に最後の 100ms ぶんを渡す
            s.Append(Chunk(1000, 1600), 3200);
            s.MarkStopped(null);
        }), TimeSpan.FromSeconds(3));

        Assert.Equal(6400, DataBytes(result));
        Assert.Null(result.Warning);
        Assert.False(result.Silent);
    }

    [Fact]
    public async Task 無音に見える録音でも語尾の最後のバッファが有音なら無音扱いにならない()
    {
        var s = new CaptureSession();
        s.Append(Chunk(5, 1600), 3200);
        var result = await s.FinishAsync(() => Task.Run(() =>
        {
            s.Append(Chunk(4000, 1600), 3200); // 停止要求後に届いた語尾
            s.MarkStopped(null);
        }), TimeSpan.FromSeconds(3));
        Assert.False(result.Silent);
    }

    [Fact]
    public async Task 通知がUIスレッドのSynchronizationContextへ投げられてもデッドロックしない()
    {
        using var ui = new SingleThreadContext();
        var s = new CaptureSession();
        s.Append(Chunk(1000, 160), 320);

        // UI スレッド上で FinishAsync を await し、デバイスの通知は同じ UI スレッドへ Post される
        var task = ui.RunAsync(() => s.FinishAsync(
            () => ui.Post(_ => { s.Append(Chunk(1000, 160), 320); s.MarkStopped(null); }, null),
            TimeSpan.FromSeconds(5)));

        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(task, finished);
        Assert.Equal(640, DataBytes(await task));
    }

    [Fact]
    public async Task 終了通知が来ない場合はタイムアウトまでの音声を返して警告する()
    {
        var s = new CaptureSession();
        s.Append(Chunk(1000, 1600), 3200);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await s.FinishAsync(() => { }, TimeSpan.FromMilliseconds(150));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(3200, DataBytes(result));
        Assert.Contains("タイムアウト", result.Warning);
        Assert.Contains("欠けている可能性", result.Warning);
    }

    [Fact]
    public async Task 停止要求が例外でも音声は保持して警告で伝える()
    {
        var s = new CaptureSession();
        s.Append(Chunk(1000, 1600), 3200);
        var result = await s.FinishAsync(() => throw new InvalidOperationException("device gone"), TimeSpan.FromMilliseconds(100));
        Assert.Equal(3200, DataBytes(result));
        Assert.Contains("InvalidOperationException", result.Warning);
        Assert.DoesNotContain("device gone", result.Warning);
    }

    [Fact]
    public async Task デバイスが停止時にエラーを返した場合も音声を返して警告する()
    {
        var s = new CaptureSession();
        s.Append(Chunk(1000, 800), 1600);
        var result = await s.FinishAsync(() => Task.Run(() => s.MarkStopped(new IOException("x"))), TimeSpan.FromSeconds(3));
        Assert.Equal(1600, DataBytes(result));
        Assert.Contains("IOException", result.Warning);
    }

    [Fact]
    public void 中断では待たずにここまでの音声を確定し以降のデータは受けない()
    {
        var s = new CaptureSession();
        s.Append(Chunk(1000, 800), 1600);
        var result = s.Take("マイクの異常で録音が中断されました。");
        s.Append(Chunk(1000, 800), 1600);
        Assert.Equal(1600, DataBytes(result));
        Assert.Equal("マイクの異常で録音が中断されました。", result.Warning);
        Assert.Equal(0, DataBytes(s.Take())); // 確定後は蓄積を消してあり、後から届いた分も入らない
    }

    [Fact]
    public void 取消は蓄積を消し以降のデータを受けない()
    {
        var s = new CaptureSession();
        s.Append(Chunk(1000, 800), 1600);
        s.Discard();
        s.Append(Chunk(1000, 800), 1600);
        Assert.Equal(0, DataBytes(s.Take()));
    }
}

// ---- 指摘 4: 認識成功後の配送(中止との競合・終了・貼り付け直前の確認) ----

sealed class FakeEnv : IDeliveryEnvironment
{
    public bool Exiting { get; set; }
    public bool ClipboardOk { get; set; } = true;
    public bool Released { get; set; } = true;
    public bool SendOk { get; set; } = true;
    public bool EnterOk { get; set; } = true;
    public bool ModifierHeld { get; set; }
    public Queue<PasteContext> Contexts { get; } = new();
    public PasteContext Default { get; set; } = new(1000, 1000, false, TargetElevation.NotElevated, true);
    public Action? OnWait { get; set; }
    public Action? OnCapture { get; set; }
    public Action? OnSend { get; set; }

    public int ClipboardCalls, WaitCalls, CaptureCalls, SendCalls, EnterCalls;
    public string? Clipboard;

    public Task<bool> SetClipboardAsync(string text)
    {
        ClipboardCalls++;
        if (ClipboardOk) Clipboard = text;
        return Task.FromResult(ClipboardOk);
    }

    public Task<bool> WaitForModifierReleaseAsync()
    {
        WaitCalls++;
        OnWait?.Invoke();
        return Task.FromResult(Released);
    }

    public PasteContext CaptureContext()
    {
        CaptureCalls++;
        OnCapture?.Invoke();
        return Contexts.Count > 0 ? Contexts.Dequeue() : Default;
    }

    public bool SendPaste()
    {
        SendCalls++;
        OnSend?.Invoke();
        return SendOk;
    }

    public bool SendEnter()
    {
        EnterCalls++;
        return EnterOk;
    }
}

public class ResultDeliveryTests
{
    [Fact]
    public async Task 貼り付けに成功したときだけEnterを送る()
    {
        var env = new FakeEnv();
        var r = await ResultDelivery.RunAsync(env, "t", false, pressEnter: true);
        Assert.Equal(DeliveryOutcome.Pasted, r.Outcome);
        Assert.True(r.EnterSent);
        Assert.Equal(1, env.EnterCalls);
    }

    [Fact]
    public async Task Enterを求めなければ送らない()
    {
        var env = new FakeEnv();
        var r = await ResultDelivery.RunAsync(env, "t", false);
        Assert.False(r.EnterSent);
        Assert.Equal(0, env.EnterCalls);
    }

    [Fact]
    public async Task コピーのみの結果ではEnterを送らない()
    {
        var env = new FakeEnv { Default = new(1000, 2000, false, TargetElevation.NotElevated, true) }; // 前面が移っている
        var r = await ResultDelivery.RunAsync(env, "t", false, pressEnter: true);
        Assert.Equal(DeliveryOutcome.CopiedOnly, r.Outcome);
        Assert.Equal(0, env.EnterCalls);
    }

    [Fact]
    public async Task 貼り付けキーの送信失敗や中止済みではEnterを送らない()
    {
        var failed = new FakeEnv { SendOk = false };
        Assert.Equal(DeliveryOutcome.PasteSendFailed, (await ResultDelivery.RunAsync(failed, "t", false, pressEnter: true)).Outcome);
        Assert.Equal(0, failed.EnterCalls);

        var aborted = new FakeEnv();
        Assert.Equal(DeliveryOutcome.AbortedCopied, (await ResultDelivery.RunAsync(aborted, "t", true, pressEnter: true)).Outcome);
        Assert.Equal(0, aborted.EnterCalls);
    }

    [Fact]
    public async Task 貼り付けとEnterの間に終了したらEnterを送らない()
    {
        var env = new FakeEnv();
        env.OnSend = () => env.Exiting = true;
        var r = await ResultDelivery.RunAsync(env, "t", false, pressEnter: true);
        Assert.Equal(DeliveryOutcome.Pasted, r.Outcome);
        Assert.False(r.EnterSent);
        Assert.Equal(0, env.EnterCalls);
    }

    [Fact]
    public async Task 貼り付けとEnterの間に前面が移ったらEnterを送らない()
    {
        var env = new FakeEnv();
        env.OnSend = () => env.Default = new(1000, 2000, false, TargetElevation.NotElevated, true);
        var r = await ResultDelivery.RunAsync(env, "t", false, pressEnter: true);
        Assert.Equal(DeliveryOutcome.Pasted, r.Outcome);
        Assert.False(r.EnterSent);
        Assert.Equal(0, env.EnterCalls);
    }

    [Fact]
    public async Task 貼り付けとEnterの間に修飾キーが押されたらEnterを送らない()
    {
        var env = new FakeEnv();
        env.OnSend = () => env.ModifierHeld = true;
        var r = await ResultDelivery.RunAsync(env, "t", false, pressEnter: true);
        Assert.Equal(DeliveryOutcome.Pasted, r.Outcome);
        Assert.False(r.EnterSent);
        Assert.Equal(0, env.EnterCalls);
    }

    [Fact]
    public async Task Enter送信の失敗は結果に残る()
    {
        var env = new FakeEnv { EnterOk = false };
        var r = await ResultDelivery.RunAsync(env, "t", false, pressEnter: true);
        Assert.Equal(DeliveryOutcome.Pasted, r.Outcome);
        Assert.False(r.EnterSent);
        Assert.Equal(1, env.EnterCalls);
    }

    [Fact]
    public async Task 通常は待機後に貼り付ける()
    {
        var env = new FakeEnv();
        var r = await ResultDelivery.RunAsync(env, "text", userAborted: false);
        Assert.Equal(DeliveryOutcome.Pasted, r.Outcome);
        Assert.Equal("text", env.Clipboard);
        Assert.Equal(1, env.SendCalls);
        Assert.Equal(2, env.CaptureCalls); // 待機の前後で判断し直す
    }

    [Fact]
    public async Task 中止が先に要求されていたら完成した結果は貼り付けずクリップボードに保護する()
    {
        var env = new FakeEnv();
        var r = await ResultDelivery.RunAsync(env, "完成済みの結果", userAborted: true);
        Assert.Equal(DeliveryOutcome.AbortedCopied, r.Outcome);
        Assert.Equal("完成済みの結果", env.Clipboard);
        Assert.Equal(0, env.SendCalls);
        Assert.Equal(0, env.WaitCalls);
        Assert.Equal(0, env.CaptureCalls);
    }

    [Fact]
    public async Task クリップボードに書けなければ貼り付けない_APIの再送状態を示す結果は返らない()
    {
        var env = new FakeEnv { ClipboardOk = false };
        var r = await ResultDelivery.RunAsync(env, "t", false);
        Assert.Equal(DeliveryOutcome.ClipboardFailed, r.Outcome);
        Assert.Equal(0, env.SendCalls);
    }

    [Fact]
    public async Task 貼り付けキーの送信失敗はコピー済みの結果として返る()
    {
        var env = new FakeEnv { SendOk = false };
        var r = await ResultDelivery.RunAsync(env, "t", false);
        Assert.Equal(DeliveryOutcome.PasteSendFailed, r.Outcome);
        Assert.Equal("t", env.Clipboard);
    }

    [Fact]
    public async Task 開始前に終了中なら何もしない()
    {
        var env = new FakeEnv { Exiting = true };
        var r = await ResultDelivery.RunAsync(env, "t", false);
        Assert.Equal(DeliveryOutcome.ExitingSkipped, r.Outcome);
        Assert.Equal(0, env.ClipboardCalls);
        Assert.Equal(0, env.SendCalls);
    }

    [Fact]
    public async Task 修飾キー待機中に終了したら貼り付けない()
    {
        var env = new FakeEnv();
        env.OnWait = () => env.Exiting = true;
        var r = await ResultDelivery.RunAsync(env, "t", false);
        Assert.Equal(DeliveryOutcome.ExitingSkipped, r.Outcome);
        Assert.Equal(0, env.SendCalls);
    }

    [Fact]
    public async Task 貼り付けの直前の再判断中に終了したら貼り付けない()
    {
        var env = new FakeEnv();
        env.OnCapture = () => { if (env.CaptureCalls == 2) env.Exiting = true; };
        var r = await ResultDelivery.RunAsync(env, "t", false);
        Assert.Equal(DeliveryOutcome.ExitingSkipped, r.Outcome);
        Assert.Equal(0, env.SendCalls);
    }

    [Fact]
    public async Task 修飾キーが離されなければコピーのみ()
    {
        var env = new FakeEnv { Released = false };
        var r = await ResultDelivery.RunAsync(env, "t", false);
        Assert.Equal(DeliveryOutcome.CopiedOnly, r.Outcome);
        Assert.Equal(CopyReason.ModifierHeld, r.Decision.Reason);
        Assert.Equal(0, env.SendCalls);
    }

    [Fact]
    public async Task 待機中に別ウィンドウへ移ったらコピーのみ()
    {
        var env = new FakeEnv();
        env.Contexts.Enqueue(env.Default);                                    // 待機前は貼り付けてよい
        env.Contexts.Enqueue(env.Default with { ForegroundChangedSinceStop = true }); // 待機後は移動済み
        var r = await ResultDelivery.RunAsync(env, "t", false);
        Assert.Equal(DeliveryOutcome.CopiedOnly, r.Outcome);
        Assert.Equal(CopyReason.ForegroundChanged, r.Decision.Reason);
        Assert.Equal(0, env.SendCalls);
    }

    [Fact]
    public async Task 前面ウィンドウの監視が使えないなら待たずにコピーのみ()
    {
        var env = new FakeEnv { Default = new PasteContext(1000, 1000, false, TargetElevation.NotElevated, TrackingAvailable: false) };
        var r = await ResultDelivery.RunAsync(env, "t", false);
        Assert.Equal(DeliveryOutcome.CopiedOnly, r.Outcome);
        Assert.Equal(CopyReason.TrackingUnavailable, r.Decision.Reason);
        Assert.Equal(0, env.WaitCalls);
        Assert.Equal(0, env.SendCalls);
    }

    [Theory]
    [InlineData(TargetElevation.Elevated, CopyReason.TargetElevated)]
    [InlineData(TargetElevation.Unknown, CopyReason.ElevationUnknown)]
    public async Task 権限が高いまたは不明な貼り付け先にはキー送信しない(TargetElevation el, CopyReason reason)
    {
        var env = new FakeEnv { Default = new PasteContext(1000, 1000, false, el, true) };
        var r = await ResultDelivery.RunAsync(env, "t", false);
        Assert.Equal(DeliveryOutcome.CopiedOnly, r.Outcome);
        Assert.Equal(reason, r.Decision.Reason);
        Assert.Equal(0, env.SendCalls);
        Assert.Equal("t", env.Clipboard);
    }
}

// ---- 指摘 1: 設定とAPIキーの保存。キーは新しい資格情報 ID へ書き、JSON 保存の成功で参照を切り替える ----

sealed class FakeCreds : ICredentialStore
{
    public Dictionary<string, string> Data { get; } = new();
    public List<string> Reads { get; } = new();
    public List<string> Writes { get; } = new();
    public Func<string, bool>? FailWriteWhen { get; set; }
    public bool FailAllDeletes { get; set; }
    public bool FailAllReads { get; set; }

    public string? Read(string target)
    {
        Reads.Add(target);
        if (FailAllReads) throw new InvalidOperationException("read failed");
        return Data.GetValueOrDefault(target);
    }

    public void Write(string target, string secret)
    {
        Writes.Add(target);
        if (FailWriteWhen?.Invoke(secret) == true) throw new InvalidOperationException("write failed");
        Data[target] = secret;
    }

    public void Delete(string target)
    {
        if (FailAllDeletes) throw new InvalidOperationException("delete failed");
        Data.Remove(target);
    }

    public string? Get(Guid? credentialId) => credentialId is { } id ? Data.GetValueOrDefault(CredentialTargets.TargetFor(id)) : null;
}

public class SettingsCommitTests
{
    sealed class Fixture : IDisposable
    {
        public TempDir Dir = new();
        public JsonFileStore<AppSettings> Store;
        public FakeCreds Creds = new();
        public Profile P1 = TestProfiles.Create(ProviderKind.AzureMai);
        public Profile P2 = TestProfiles.Create(ProviderKind.OpenAiCompatible); // 削除される
        public Profile Untouched = TestProfiles.Create(ProviderKind.OpenAiCompatible);
        public readonly string OldEndpoint;

        public Fixture()
        {
            P2.Name = "to-remove";
            Untouched.Name = "untouched";
            P1.CredentialId = Guid.NewGuid();
            P2.CredentialId = Guid.NewGuid();
            Untouched.CredentialId = Guid.NewGuid();
            OldEndpoint = P1.Endpoint;
            Store = new JsonFileStore<AppSettings>(Dir.File("settings.json"), AppSettings.Validate);
            Store.Save(new AppSettings { Profiles = { P1.Clone(), P2.Clone(), Untouched.Clone() }, ActiveProfileId = P1.Id });
            Creds.Data[CredentialTargets.TargetFor(P1.CredentialId.Value)] = "old-key-1";
            Creds.Data[CredentialTargets.TargetFor(P2.CredentialId.Value)] = "key-2";
            Creds.Data[CredentialTargets.TargetFor(Untouched.CredentialId.Value)] = "key-untouched";
        }

        /// <summary>P1 のエンドポイントを変え(新キーを付けるかは呼び出し側)、P2 を削除し、P3 を追加する候補。</summary>
        public (AppSettings candidate, Profile p3) Candidate()
        {
            var p1 = P1.Clone();
            p1.Endpoint = "https://new.example.com/";
            var p3 = TestProfiles.Create(ProviderKind.OpenAiCompatible);
            p3.Name = "new";
            return (new AppSettings { Profiles = { p1, Untouched.Clone(), p3 }, ActiveProfileId = p1.Id }, p3);
        }

        public Dictionary<Guid, string> Keys(Profile p3) => new() { [P1.Id] = "new-key-1", [p3.Id] = "key-3" };

        public SaveResult Commit((AppSettings candidate, Profile p3) c, Dictionary<Guid, string>? keys = null, params Guid[] keyDeletes)
            => SettingsCommit.Commit(Store, Creds, c.candidate, keys ?? Keys(c.p3), keyDeletes);

        /// <summary>再起動の再現: ディスクから新しく読み直した store。</summary>
        public JsonFileStore<AppSettings> Restart()
        {
            var s = new JsonFileStore<AppSettings>(Store.Path, AppSettings.Validate);
            s.Load();
            Assert.False(s.IsCorrupt);
            return s;
        }

        /// <summary>JSON の保存だけを失敗させる(一時ファイルのパスをディレクトリで塞ぐ)。</summary>
        public void BreakJsonSave() => Directory.CreateDirectory(Store.Path + ".tmp");

        public void FixJsonSave() => Directory.Delete(Store.Path + ".tmp");

        public (string endpoint, string key) Resolve(JsonFileStore<AppSettings> store)
        {
            Assert.True(SendPreflight.TryResolve(store, Creds, out var t, out _));
            return (t!.Profile.Endpoint, t.ApiKey);
        }

        public void Dispose() => Dir.Dispose();
    }

    [Fact]
    public void 成功すると設定と新しいキーが揃って切り替わり古いキーと削除プロファイルのキーが消える()
    {
        using var f = new Fixture();
        var c = f.Candidate();

        var r = f.Commit(c);

        Assert.Equal(SaveStatus.Saved, r.Status);
        Assert.Same(c.candidate, f.Store.Value);
        var p1 = f.Store.Value.Profiles[0];
        Assert.NotEqual(f.P1.CredentialId, p1.CredentialId);         // 新しい資格情報 ID へ切り替わる
        Assert.Equal("new-key-1", f.Creds.Get(p1.CredentialId));
        Assert.Equal("key-3", f.Creds.Get(c.p3.CredentialId));
        Assert.Null(f.Creds.Get(f.P1.CredentialId));                  // 古いキーは保存成功の後に削除
        Assert.Null(f.Creds.Get(f.P2.CredentialId));
        Assert.Equal("key-untouched", f.Creds.Get(f.Untouched.CredentialId));
        Assert.Equal(("https://new.example.com/", "new-key-1"), f.Resolve(f.Restart()));
    }

    [Fact]
    public void 設定JSONの保存に失敗しても設定と参照するキーは保存前の組のままで_失敗時に書いた分は消える()
    {
        using var f = new Fixture();
        var before = File.ReadAllText(f.Store.Path);
        var c = f.Candidate();
        f.BreakJsonSave();

        var r = f.Commit(c);

        Assert.Equal(SaveStatus.Failed, r.Status);
        Assert.Equal(before, File.ReadAllText(f.Store.Path));
        Assert.Empty(f.Creds.Reads);                                  // 保存処理は既存の資格情報を読まない
        Assert.Equal(f.OldEndpoint, f.Store.Value.Profiles[0].Endpoint);
        Assert.Equal("key-2", f.Creds.Get(f.P2.CredentialId));       // 削除予定でも保存成功までは残る
        Assert.Equal(3, f.Creds.Data.Count);                          // 今回書いた新しいキーは残っていない
        Assert.Equal((f.OldEndpoint, "old-key-1"), f.Resolve(f.Store));
        Assert.DoesNotContain("new-key-1", r.Message);
    }

    [Fact]
    public void 後始末の削除にも失敗して再起動しても_旧エンドポイントに新しいキーは組み合わさらない()
    {
        using var f = new Fixture();
        var c = f.Candidate();
        f.BreakJsonSave();
        f.Creds.FailAllDeletes = true; // 失敗時に書いた新しいキーの削除も失敗する

        var r = f.Commit(c);

        Assert.Equal(SaveStatus.Failed, r.Status);
        Assert.Contains("削除できませんでした", r.Message);
        Assert.Contains("new-key-1", f.Creds.Data.Values);           // 未使用の資格情報は残っているが…

        // …再起動(ディスクから読み直し)しても、参照されているのは旧キーだけ
        var restarted = f.Restart();
        Assert.Equal((f.OldEndpoint, "old-key-1"), f.Resolve(restarted));
    }

    [Fact]
    public void 失敗後にキーを再入力せず保存し直しても_失敗時に書かれた未使用キーは結び付かない()
    {
        using var f = new Fixture();
        var c = f.Candidate();
        f.BreakJsonSave();
        f.Creds.FailAllDeletes = true;
        f.Commit(c); // 失敗し、未使用の new-key-1 が残る
        f.FixJsonSave();
        f.Creds.FailAllDeletes = false;

        // 画面の入力内容は消えている(再読み込み後)想定: エンドポイントだけ変えてキーは入力しない
        var again = f.Candidate();
        var r = f.Commit(again, new Dictionary<Guid, string>());

        Assert.Equal(SaveStatus.Saved, r.Status);
        var (endpoint, key) = f.Resolve(f.Restart());
        Assert.Equal("https://new.example.com/", endpoint);
        Assert.Equal("old-key-1", key);                               // 残っていた未使用キーを拾わない
        Assert.Equal(f.P1.CredentialId, f.Store.Value.Profiles[0].CredentialId);
    }

    [Fact]
    public void 修復後にキーを入力して保存すると新しいキーと設定が揃う()
    {
        using var f = new Fixture();
        f.BreakJsonSave();
        f.Commit(f.Candidate());
        f.FixJsonSave();

        var r = f.Commit(f.Candidate());

        Assert.Equal(SaveStatus.Saved, r.Status);
        Assert.Equal(("https://new.example.com/", "new-key-1"), f.Resolve(f.Restart()));
        Assert.DoesNotContain(f.Creds.Data, kv => kv.Value == "old-key-1");
    }

    [Fact]
    public void キー書き込みが途中で失敗したら書いた分を消し設定JSONは触らない()
    {
        using var f = new Fixture();
        var before = File.ReadAllText(f.Store.Path);
        var c = f.Candidate();
        f.Creds.FailWriteWhen = secret => secret == "key-3"; // 2 件目の書き込みで失敗

        var r = f.Commit(c);

        Assert.Equal(SaveStatus.Failed, r.Status);
        Assert.Equal(before, File.ReadAllText(f.Store.Path));
        Assert.Equal((f.OldEndpoint, "old-key-1"), f.Resolve(f.Store));
        Assert.Equal(3, f.Creds.Data.Count);
    }

    [Fact]
    public void 古いキーを削除できなくても保存は成功し設定は新しいキーを参照する()
    {
        using var f = new Fixture();
        f.Creds.FailAllDeletes = true;

        var r = f.Commit(f.Candidate());

        Assert.Equal(SaveStatus.SavedWithLeftovers, r.Status);
        Assert.Contains("削除できませんでした", r.Message);
        Assert.Equal(("https://new.example.com/", "new-key-1"), f.Resolve(f.Restart()));
    }

    [Fact]
    public void キーの削除は設定の保存と同時に確定し参照がなくなる()
    {
        using var f = new Fixture();
        var c = f.Candidate();

        var r = f.Commit(c, new Dictionary<Guid, string> { [c.p3.Id] = "key-3" }, f.P1.Id);

        Assert.Equal(SaveStatus.Saved, r.Status);
        Assert.Null(f.Store.Value.Profiles[0].CredentialId);
        Assert.Null(f.Creds.Get(f.P1.CredentialId));
        Assert.False(SendPreflight.TryResolve(f.Restart(), f.Creds, out _, out var problem));
        Assert.Contains("APIキー", problem);
    }

    [Fact]
    public void 保存済みの参照を優先し画面側から渡された資格情報IDは採用しない()
    {
        using var f = new Fixture();
        var c = f.Candidate();
        c.candidate.Profiles[0].CredentialId = f.Untouched.CredentialId; // 不正な値が混ざった想定

        f.Commit(c, new Dictionary<Guid, string>());

        Assert.Equal(f.P1.CredentialId, f.Store.Value.Profiles[0].CredentialId);
        Assert.Equal(f.Untouched.CredentialId, f.Store.Value.Profiles[1].CredentialId);
    }

    [Fact]
    public void 変更対象外の資格情報には触れない()
    {
        using var f = new Fixture();
        f.Commit(f.Candidate());
        Assert.DoesNotContain(CredentialTargets.TargetFor(f.Untouched.CredentialId!.Value), f.Creds.Writes);
        Assert.DoesNotContain(CredentialTargets.TargetFor(f.Untouched.CredentialId!.Value), f.Creds.Reads);
        Assert.Equal("key-untouched", f.Creds.Get(f.Untouched.CredentialId));
    }

    [Fact]
    public void 複数のプロファイルが同じ資格情報を参照する設定は壊れたデータとして扱う()
    {
        var shared = Guid.NewGuid();
        var a = TestProfiles.Create(ProviderKind.AzureMai); a.CredentialId = shared;
        var b = TestProfiles.Create(ProviderKind.OpenAiCompatible); b.CredentialId = shared;
        Assert.NotEmpty(AppSettings.Validate(new AppSettings { Profiles = { a, b } }));
    }
}

public class SendPreflightTests
{
    static (JsonFileStore<AppSettings> store, FakeCreds creds, Profile p) Setup(TempDir dir, ProviderKind kind, string? key, string? endpoint = null)
    {
        var p = TestProfiles.Create(kind);
        if (endpoint != null) p.Endpoint = endpoint;
        var creds = new FakeCreds();
        if (key != null)
        {
            p.CredentialId = Guid.NewGuid();
            creds.Data[CredentialTargets.TargetFor(p.CredentialId.Value)] = key;
        }
        var store = new JsonFileStore<AppSettings>(dir.File("settings.json"), AppSettings.Validate);
        store.Save(new AppSettings { Profiles = { p }, ActiveProfileId = p.Id });
        return (store, creds, p);
    }

    [Fact]
    public void 通常は接続先とキーを返す()
    {
        using var dir = new TempDir();
        var (store, creds, p) = Setup(dir, ProviderKind.AzureMai, "k");
        Assert.True(SendPreflight.TryResolve(store, creds, out var t, out _));
        Assert.Equal((p.Id, "k"), (t!.Profile.Id, t.ApiKey));
    }

    [Fact]
    public void キー未設定は拒否_ただしloopbackのOpenAI互換は許可する()
    {
        using var dir = new TempDir();
        var (s1, c1, _) = Setup(dir, ProviderKind.AzureMai, null);
        Assert.False(SendPreflight.TryResolve(s1, c1, out _, out var problem));
        Assert.Contains("APIキー", problem);

        using var dir2 = new TempDir();
        var (s2, c2, _) = Setup(dir2, ProviderKind.OpenAiCompatible, null, "http://localhost:8080/v1");
        Assert.True(SendPreflight.TryResolve(s2, c2, out _, out _));

        using var dir3 = new TempDir();
        var (s3, c3, _) = Setup(dir3, ProviderKind.OpenAiCompatible, null);
        Assert.False(SendPreflight.TryResolve(s3, c3, out _, out _));
    }

    [Fact]
    public void 設定が壊れている_プロファイル未選択_資格情報の読み取り失敗は拒否する()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), "{ broken");
        var broken = new JsonFileStore<AppSettings>(dir.File("settings.json"), AppSettings.Validate);
        broken.Load();
        Assert.False(SendPreflight.TryResolve(broken, new FakeCreds(), out _, out var p1));
        Assert.Contains("壊れています", p1);

        var empty = new JsonFileStore<AppSettings>(dir.File("none.json"), AppSettings.Validate);
        empty.Load();
        Assert.False(SendPreflight.TryResolve(empty, new FakeCreds(), out _, out var p2));
        Assert.Contains("選択されていません", p2);

        using var dir2 = new TempDir();
        var (store, creds, _) = Setup(dir2, ProviderKind.AzureMai, "k");
        creds.FailAllReads = true;
        Assert.False(SendPreflight.TryResolve(store, creds, out _, out _));
    }
}
public class InterruptedSessionTests
{
    [Fact]
    public void マイク中断では音声を保持した再送待ちになり新録音は始まらない()
    {
        var s = new SessionStateMachine();
        s.Toggle(); // Recording
        Assert.True(s.RecordingInterrupted());
        Assert.Equal(SessionState.RetryPending, s.State); // Recognizing に固定されない
        Assert.Equal(ToggleOutcome.RetryPending, s.Toggle());
        Assert.True(s.Retry());
        Assert.Equal(SessionState.Recognizing, s.State);
    }

    [Fact]
    public void 中断後に破棄すればIdleへ戻れる()
    {
        var s = new SessionStateMachine();
        s.Toggle();
        s.RecordingInterrupted();
        Assert.True(s.Discard());
        Assert.Equal(ToggleOutcome.Started, s.Toggle());
    }

    [Fact]
    public void 録音中以外では中断遷移できない()
    {
        var s = new SessionStateMachine();
        Assert.False(s.RecordingInterrupted());
        s.Toggle(); s.Toggle(); // Recognizing
        Assert.False(s.RecordingInterrupted());
    }
}
