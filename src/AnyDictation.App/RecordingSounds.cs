using System;
using System.IO;
using System.Media;

namespace AnyDictation.App;

/// <summary>録音の開始と停止を短い上昇音・下降音で知らせる。</summary>
internal static class RecordingSounds
{
    static readonly SoundPlayer StartPlayer = Create(660, 880);
    static readonly SoundPlayer StopPlayer = Create(880, 660);

    public static void Started() => Play(StartPlayer);
    public static void Stopped() => Play(StopPlayer);

    static void Play(SoundPlayer player)
    {
        // 通知音の失敗で録音や認識を止めない。Play は非同期で再生する。
        try { player.Play(); }
        catch (Exception e) { Log.Write($"recording sound failed: {e.GetType().Name}"); }
    }

    static SoundPlayer Create(double first, double second)
    {
        const int rate = 16000;
        const int samplesPerTone = 960; // 60 ms x 2
        const int dataBytes = samplesPerTone * 2 * sizeof(short);
        var stream = new MemoryStream(44 + dataBytes);
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataBytes);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(rate);
            writer.Write(rate * sizeof(short));
            writer.Write((short)sizeof(short));
            writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(dataBytes);
            foreach (double frequency in new[] { first, second })
                for (int i = 0; i < samplesPerTone; i++)
                {
                    double envelope = Math.Min(1d, Math.Min(i, samplesPerTone - 1 - i) / 160d);
                    writer.Write((short)(6000 * envelope * Math.Sin(2 * Math.PI * frequency * i / rate)));
                }
        }
        stream.Position = 0;
        return new SoundPlayer(stream);
    }
}