using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NAudio.Wave;
using Windows.Win32;
using Windows.Win32.Media.Audio;

namespace AnyDictation.App;

internal static class Microphones
{
    // Mmddk.h: DRV_RESERVED + 12 / + 13. 番号は保存せず、デバイス interface path を保存する。
    const uint QueryInterfaceSize = 0x080D;
    const uint QueryInterface = 0x080C;
    static uint Message(IntPtr device, uint message, IntPtr parameter1, UIntPtr parameter2) =>
        PInvoke.waveInMessage(new HWAVEIN(device), message, (nuint)parameter1, parameter2);

    public static IReadOnlyList<MicrophoneDevice> List()
    {
        var result = new List<MicrophoneDevice>();
        int count = WaveIn.DeviceCount;
        for (int number = 0; number < count; number++)
            result.Add(new(number, ReadId(number), WaveIn.GetCapabilities(number).ProductName));
        return result;
    }

    static string? ReadId(int number)
    {
        var size = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(size, 0);
            if (Message(new IntPtr(number), QueryInterfaceSize, size, UIntPtr.Zero) != 0) return null;
            int bytes = Marshal.ReadInt32(size);
            if (bytes <= 2 || bytes > 65536 || bytes % 2 != 0) return null;
            var buffer = Marshal.AllocHGlobal(bytes);
            try
            {
                if (Message(new IntPtr(number), QueryInterface, buffer, new UIntPtr((uint)bytes)) != 0) return null;
                string? id = Marshal.PtrToStringUni(buffer, bytes / 2);
                if (id != null && id.IndexOf('\0') is int terminator && terminator >= 0) id = id[..terminator];
                return string.IsNullOrEmpty(id) ? null : id;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { Marshal.FreeHGlobal(size); }
    }

    // Mmddk.h: DRVM_MAPPER(0x2000) + 21。WAVE_MAPPER(UINT -1)を HWAVEIN として渡すと、デバイスを開かず現在の既定番号を返す。
    const uint PreferredGet = 0x2015;
    const long WaveMapper = uint.MaxValue;
    /// <summary>WAVE_MAPPER が選ぶ現在の既定マイクの番号。既定なしは -1、取得できなければ null。</summary>
    public static unsafe int? ReadDefaultNumber()
    {
        uint preferred = 0, flags = 0;
        if (PInvoke.waveInMessage(new HWAVEIN(new IntPtr(WaveMapper)), PreferredGet, (nuint)(&preferred), (nuint)(&flags)) != 0) return null;
        return unchecked((int)preferred);
    }

    public static int Resolve(string? id) => MicrophoneSelection.Resolve(id, List());
}

/// <summary>入力テスト専用。入力から振幅だけ計算し、音声蓄積・送信・履歴保存は行わない。</summary>
internal sealed class MicrophoneTester : IDisposable
{
    WaveIn? _wave;
    InputLevelSession? _session;
    public event Action<InputLevelSession, string>? Failed;
    public bool IsCurrent(InputLevelSession session) => ReferenceEquals(_session, session) && session.IsOpen;
    public int? ReadPeak() => _session?.ReadPeak();

    public void Start(string? id)
    {
        if (_wave != null) throw new InvalidOperationException("入力テストは開始済みです。");
        int number = Microphones.Resolve(id);
        var session = new InputLevelSession();
        var wave = new WaveIn { DeviceNumber = number, WaveFormat = new WaveFormat(CaptureSession.DefaultSampleRate, 16, 1), BufferMilliseconds = 100 };
        _session = session;
        _wave = wave;
        wave.DataAvailable += (_, e) => session.Add(e.Buffer.AsSpan(0, e.BytesRecorded));
        wave.RecordingStopped += (_, _) =>
        {
            if (session.IsOpen) Failed?.Invoke(session, "マイクの入力テストが中断されました。接続やマイク権限を確認してください。");
        };
        try { wave.StartRecording(); }
        catch { Stop(); throw; }
    }

    public void Stop()
    {
        _session?.Close();
        _session = null;
        var wave = _wave;
        _wave = null;
        if (wave == null) return;
        try { wave.StopRecording(); } catch { /* エラー後も必ずデバイスを解放する */ }
        wave.Dispose();
    }
    public void Dispose() => Stop();
}