namespace AnyDictation;

/// <summary>16bit PCM のピーク振幅を追跡して無音録音を判定する。</summary>
public sealed class SilenceDetector
{
    public const int DefaultThreshold = 200; // 32768 に対して約 0.6%

    public int Peak { get; private set; }

    public void Add(ReadOnlySpan<byte> pcm16) => Peak = Math.Max(Peak, MeasurePeak(pcm16));

    public static int MeasurePeak(ReadOnlySpan<byte> pcm16)
    {
        int peak = 0;
        for (int i = 0; i + 1 < pcm16.Length; i += 2)
        {
            int v = Math.Abs((int)(short)(pcm16[i] | (pcm16[i + 1] << 8)));
            peak = Math.Max(peak, v);
        }
        return peak;
    }

    public bool IsSilent(int threshold = DefaultThreshold) => Peak < threshold;
}

public static class WavEncoder
{
    const int HeaderBytes = 44;

    /// <summary>
    /// Encode が書いた 16 bit / mono の WAV から、サンプルレートと PCM を取り出す。それ以外の形式は false。
    /// 保持した録音を Live(24 kHz 固定)へ再送できるか判断するために使う。
    /// </summary>
    public static bool TryReadPcm16Mono(byte[] wav, out int sampleRate, out ReadOnlyMemory<byte> pcm)
    {
        sampleRate = 0;
        pcm = default;
        if (wav.Length < HeaderBytes) return false;
        var h = wav.AsSpan(0, HeaderBytes);
        int dataLength = BitConverter.ToInt32(h[40..]);
        bool valid = h[..4].SequenceEqual("RIFF"u8) && h[8..12].SequenceEqual("WAVE"u8) && h[12..16].SequenceEqual("fmt "u8)
            && BitConverter.ToInt32(h[16..]) == 16 && BitConverter.ToInt16(h[20..]) == 1
            && BitConverter.ToInt16(h[22..]) == 1 && BitConverter.ToInt16(h[34..]) == 16
            && h[36..40].SequenceEqual("data"u8) && dataLength == wav.Length - HeaderBytes;
        if (!valid) return false;
        sampleRate = BitConverter.ToInt32(h[24..]);
        pcm = wav.AsMemory(HeaderBytes);
        return true;
    }

    public static byte[] Encode(ReadOnlySpan<byte> pcm, int sampleRate, short bitsPerSample, short channels)
    {
        var ms = new MemoryStream(44 + pcm.Length);
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            int byteRate = sampleRate * channels * bitsPerSample / 8;
            w.Write("RIFF"u8);
            w.Write(36 + pcm.Length);
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16);
            w.Write((short)1);
            w.Write(channels);
            w.Write(sampleRate);
            w.Write(byteRate);
            w.Write((short)(channels * bitsPerSample / 8));
            w.Write(bitsPerSample);
            w.Write("data"u8);
            w.Write(pcm.Length);
            w.Write(pcm);
        }
        return ms.ToArray();
    }
}
