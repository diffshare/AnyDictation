using System.Text.Json;
using Xunit;

namespace AnyDictation.Tests;

/// <summary>Live のための録音側の変更: 録音ごとのサンプルレート、送信キューへの音声の複製、保持 WAV の読み出し、新しい接続種別。</summary>
public class LiveCaptureTests
{
    static byte[] Loud(int samples, short level = 4000)
    {
        var b = new byte[samples * 2];
        for (int i = 0; i < samples; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 2), level);
        return b;
    }

    // ---- 録音ごとのサンプルレート ----

    [Fact]
    public void 既定の録音は16kHzのままで_Liveの録音だけ24kHzのWAVになる()
    {
        var normal = new CaptureSession();
        normal.Append(Loud(160), 320);
        var wav16 = normal.Take().Wav;
        Assert.Equal(16000, BitConverter.ToInt32(wav16, 24));
        Assert.Equal(32000, BitConverter.ToInt32(wav16, 28));

        var live = new CaptureSession(LiveTranscriptionSession.SampleRate);
        live.Append(Loud(240), 480);
        var wav24 = live.Take().Wav;
        Assert.Equal(24000, BitConverter.ToInt32(wav24, 24));   // sample rate
        Assert.Equal(48000, BitConverter.ToInt32(wav24, 28));   // byte rate
        Assert.Equal(2, BitConverter.ToInt16(wav24, 32));       // block align
        Assert.Equal(16, BitConverter.ToInt16(wav24, 34));
        Assert.Equal(1, BitConverter.ToInt16(wav24, 22));
        Assert.Equal(480, BitConverter.ToInt32(wav24, 40));
        Assert.Equal(480 + 44, wav24.Length);
    }

    [Fact]
    public void CaptureSlotは録音ごとにレートと音声の転送先を持ち_前の録音の転送は新しい録音で止まる()
    {
        var slot = new CaptureSlot();
        var oldTap = new List<byte[]>();
        var old = slot.Begin(LiveTranscriptionSession.SampleRate, oldTap.Add);
        old.Append(Loud(100), 200);
        Assert.Single(oldTap);

        var newTap = new List<byte[]>();
        var current = slot.Begin(CaptureSession.DefaultSampleRate, newTap.Add);   // 前の録音は破棄される
        Assert.Equal(16000, current.SampleRate);
        Assert.Equal(24000, old.SampleRate);

        old.Append(Loud(100), 200);            // 旧デバイスからの遅れた音声
        current.Append(Loud(50), 100);
        Assert.Single(oldTap);                 // 旧録音には転送されない(新しい接続へも混ざらない)
        Assert.Equal(100, Assert.Single(newTap).Length);
    }

    // ---- 送信キューへの音声の複製 ----

    [Fact]
    public void 転送される音声はAppendの順のコピーで_その後バッファが書き換えられても変わらない()
    {
        var tap = new List<byte[]>();
        var session = new CaptureSession(LiveTranscriptionSession.SampleRate, tap.Add);
        var buffer = new byte[8];
        for (int i = 0; i < 8; i++) buffer[i] = (byte)(i + 1);
        session.Append(buffer, 6);               // 録音デバイスはバッファを使い回す。使われるのは先頭 6 バイトだけ
        Array.Fill(buffer, (byte)0xFF);
        session.Append(buffer, 4);

        Assert.Equal(2, tap.Count);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, tap[0]);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, tap[1]);
    }

    [Fact]
    public async Task 停止要求の後に届く最後のバッファも転送され_送った音声と保持した音声は一致する()
    {
        var tap = new List<byte[]>();
        var session = new CaptureSession(LiveTranscriptionSession.SampleRate, tap.Add);
        session.Append(Loud(2400), 4800);
        var result = await session.FinishAsync(() => Task.Run(() =>
        {
            session.Append(Loud(1200, 5000), 2400);    // 停止要求の後に届く最後のバッファ
            session.MarkStopped(null);
        }), TimeSpan.FromSeconds(3));

        Assert.Equal(2, tap.Count);
        Assert.Equal(2400, tap[1].Length);
        Assert.True(WavEncoder.TryReadPcm16Mono(result.Wav, out _, out var held));
        Assert.Equal(tap.SelectMany(b => b).ToArray(), held.ToArray());   // 送信キューに積んだ音声 = 再送用に保持した音声
    }

    [Fact]
    public void 確定または取消の後に届いた音声は転送されない()
    {
        var tapTaken = new List<byte[]>();
        var taken = new CaptureSession(LiveTranscriptionSession.SampleRate, tapTaken.Add);
        taken.Append(Loud(100), 200);
        taken.Take();
        taken.Append(Loud(100), 200);
        Assert.Single(tapTaken);

        var tapDiscarded = new List<byte[]>();
        var discarded = new CaptureSession(LiveTranscriptionSession.SampleRate, tapDiscarded.Add);
        discarded.Discard();
        discarded.Append(Loud(100), 200);
        Assert.Empty(tapDiscarded);
    }

    [Fact]
    public void 転送先の有無にかかわらず音声は保持される()
    {
        var a = new CaptureSession();
        var b = new CaptureSession(LiveTranscriptionSession.SampleRate, _ => { });
        a.Append(Loud(100), 200);
        b.Append(Loud(100), 200);
        Assert.Equal(200, a.Take().Wav.Length - 44);
        Assert.Equal(200, b.Take().Wav.Length - 44);
    }

    // ---- 保持した WAV の読み出し(再送時の互換) ----

    [Fact]
    public void 保持したWAVからレートとPCMを取り出せる()
    {
        var pcm = Loud(500);
        foreach (int rate in new[] { 16000, 24000 })
        {
            var wav = WavEncoder.Encode(pcm, rate, 16, 1);
            Assert.True(WavEncoder.TryReadPcm16Mono(wav, out int got, out var data));
            Assert.Equal(rate, got);
            Assert.Equal(pcm, data.ToArray());
        }
    }

    [Fact]
    public void 保持音声が16kHzならLiveの24kHzと一致しないため再送先の判断に使える()
    {
        var wav = WavEncoder.Encode(Loud(500), CaptureSession.DefaultSampleRate, 16, 1);
        Assert.True(WavEncoder.TryReadPcm16Mono(wav, out int rate, out _));
        Assert.NotEqual(LiveTranscriptionSession.SampleRate, rate);
    }

    [Fact]
    public void 想定外の形式や壊れたWAVは読まない()
    {
        var good = WavEncoder.Encode(Loud(500), 24000, 16, 1);
        Assert.False(WavEncoder.TryReadPcm16Mono(Array.Empty<byte>(), out _, out _));
        Assert.False(WavEncoder.TryReadPcm16Mono(good[..43], out _, out _));
        Assert.False(WavEncoder.TryReadPcm16Mono(good[..^1], out _, out _));                    // data 長と一致しない(途中で切れた)
        Assert.False(WavEncoder.TryReadPcm16Mono(good.Concat(new byte[] { 0 }).ToArray(), out _, out _));
        Assert.False(WavEncoder.TryReadPcm16Mono(WavEncoder.Encode(Loud(500), 24000, 16, 2), out _, out _));  // ステレオ
        Assert.False(WavEncoder.TryReadPcm16Mono(WavEncoder.Encode(new byte[500], 24000, 8, 1), out _, out _)); // 8 bit
        var notRiff = (byte[])good.Clone();
        notRiff[0] = (byte)'X';
        Assert.False(WavEncoder.TryReadPcm16Mono(notRiff, out _, out _));
    }
}

/// <summary>新しい接続種別 AzureOpenAiLive: 既存の設定・種別の互換と、キーが必須であること。</summary>
public class LiveProviderTests
{
    [Fact]
    public void 既存の種別の値は変わらず_Liveは末尾に追加される()
    {
        Assert.Equal(0, (int)ProviderKind.AzureMai);
        Assert.Equal(1, (int)ProviderKind.OpenAiCompatible);
        Assert.Equal(2, (int)ProviderKind.AzureOpenAi);
        Assert.Equal(3, (int)ProviderKind.AzureOpenAiLive);
        Assert.Equal(4, Enum.GetValues<ProviderKind>().Length);
    }

    [Fact]
    public void Liveの既定プロファイルは確認済みのモデルを持ち_エンドポイントを入力すると検証に通る()
    {
        var p = Profile.CreateDefault(ProviderKind.AzureOpenAiLive);
        Assert.Equal("gpt-live-transcribe", p.Model);
        Assert.Equal("", p.Endpoint);
        Assert.Equal("ja", p.Language);
        Assert.Equal(ProviderKind.AzureOpenAiLive, p.Provider);
        Assert.Single(ProfileValidator.Validate(p));
        p.Endpoint = "https://example.openai.azure.com/";
        Assert.Empty(ProfileValidator.Validate(p));
    }

    [Fact]
    public void 追加前から保存されている設定JSONはそのまま読め_Liveを含む設定も往復する()
    {
        using var dir = new TempDir();
        var existing = new[] { ProviderKind.AzureMai, ProviderKind.OpenAiCompatible, ProviderKind.AzureOpenAi }
            .Select(TestProfiles.Create).ToList();
        // 追加前の版が書いた形(種別は名前の文字列)をそのまま作る
        var json = "{\"Version\":1,\"ActiveProfileId\":\"" + existing[2].Id + "\",\"MicrophoneDeviceId\":null,\"Profiles\":[" +
            string.Join(",", existing.Select(p =>
                $"{{\"Id\":\"{p.Id}\",\"Name\":\"{p.Name}\",\"Provider\":\"{p.Provider}\",\"Endpoint\":\"{p.Endpoint}\",\"Model\":\"{p.Model}\",\"Language\":\"ja\",\"CredentialId\":null}}")) + "]}";
        File.WriteAllText(dir.File("old.json"), json);
        var old = new JsonFileStore<AppSettings>(dir.File("old.json"), AppSettings.Validate);
        old.Load();
        Assert.False(old.IsCorrupt, old.CorruptReason);
        Assert.Equal(existing.Select(p => p.Provider), old.Value.Profiles.Select(p => p.Provider));
        Assert.Equal(existing[2].Id, old.Value.ActiveProfileId);

        var live = TestProfiles.Create(ProviderKind.AzureOpenAiLive);
        var settings = new AppSettings { Profiles = { existing[0], live }, ActiveProfileId = live.Id };
        var store = new JsonFileStore<AppSettings>(dir.File("new.json"), AppSettings.Validate);
        store.Save(settings);
        Assert.Contains("\"AzureOpenAiLive\"", File.ReadAllText(dir.File("new.json")));
        var reloaded = new JsonFileStore<AppSettings>(dir.File("new.json"), AppSettings.Validate);
        reloaded.Load();
        Assert.False(reloaded.IsCorrupt, reloaded.CorruptReason);
        Assert.Equal(ProviderKind.AzureOpenAiLive, reloaded.Value.ActiveProfile!.Provider);
        Assert.Equal("gpt-live-transcribe", reloaded.Value.ActiveProfile.Model);
    }

    [Fact]
    public void 未知の種別名は壊れた設定として扱い_上書きしない()
    {
        using var dir = new TempDir();
        var p = TestProfiles.Create(ProviderKind.AzureMai);
        File.WriteAllText(dir.File("s.json"),
            $"{{\"Version\":1,\"ActiveProfileId\":\"{p.Id}\",\"Profiles\":[{{\"Id\":\"{p.Id}\",\"Name\":\"x\",\"Provider\":\"AzureOpenAiLiveV9\",\"Endpoint\":\"{p.Endpoint}\",\"Model\":\"m\",\"Language\":\"ja\"}}]}}");
        var store = new JsonFileStore<AppSettings>(dir.File("s.json"), AppSettings.Validate);
        store.Load();
        Assert.True(store.IsCorrupt);
    }

    [Fact]
    public void LiveはAPIキーが必須で_loopbackでも空キーを許さない()
    {
        using var dir = new TempDir();
        var p = TestProfiles.Create(ProviderKind.AzureOpenAiLive);
        p.Endpoint = "http://localhost:8080/";
        var store = new JsonFileStore<AppSettings>(dir.File("s.json"), AppSettings.Validate);
        store.Save(new AppSettings { Profiles = { p }, ActiveProfileId = p.Id });
        Assert.False(SendPreflight.TryResolve(store, new FakeCreds(), out _, out var problem));
        Assert.Contains("APIキー", problem);

        var creds = new FakeCreds();
        p.CredentialId = Guid.NewGuid();
        creds.Data[CredentialTargets.TargetFor(p.CredentialId.Value)] = "k";
        store.Save(new AppSettings { Profiles = { p }, ActiveProfileId = p.Id });
        Assert.True(SendPreflight.TryResolve(store, creds, out var target, out _));
        Assert.Equal(ProviderKind.AzureOpenAiLive, target!.Profile.Provider);
    }

    [Fact]
    public void Liveはバッチの認識クライアントとして作れない()
    {
        using var http = new HttpClient();
        Assert.Throws<ArgumentOutOfRangeException>(() => TranscriptionClients.Create(ProviderKind.AzureOpenAiLive, http));
    }
}
