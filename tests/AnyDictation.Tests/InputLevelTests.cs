using AnyDictation;
using Xunit;

namespace AnyDictation.Tests;

public sealed class InputLevelTests
{
    [Fact]
    public void NoBufferIsDifferentFromSilentBuffer()
    {
        var capture = new CaptureSession();
        Assert.Null(capture.ReadInputPeak());
        capture.Append(new byte[3200], 3200);
        Assert.Equal(0, capture.ReadInputPeak());
        Assert.Null(capture.ReadInputPeak());
    }

    [Fact]
    public void PeakCoversAllBuffersSinceLastReadAndFallsBackToSilence()
    {
        var capture = new CaptureSession();
        capture.Append(new byte[] { 0, 16 }, 2);
        capture.Append(new byte[] { 0, 8 }, 2);
        Assert.Equal(4096, capture.ReadInputPeak());
        capture.Append(new byte[2], 2);
        Assert.Equal(0, capture.ReadInputPeak());
    }

    [Fact]
    public void NegativeFullScaleDoesNotOverflow()
    {
        Assert.Equal(32768, SilenceDetector.MeasurePeak(new byte[] { 0, 128 }));
        Assert.Equal(32767, SilenceDetector.MeasurePeak(new byte[] { 255, 127 }));
        Assert.Equal(0, SilenceDetector.MeasurePeak(new byte[] { 255 }));
    }

    [Fact]
    public void MeterReadDoesNotConsumeAudioOrResetSilenceDetection()
    {
        var capture = new CaptureSession();
        capture.Append(new byte[] { 0, 16 }, 2);
        Assert.Equal(4096, capture.ReadInputPeak());
        var result = capture.Take();
        Assert.False(result.Silent);
        Assert.Equal(new byte[] { 0, 16 }, result.Wav[44..]);
        Assert.Null(capture.ReadInputPeak());
    }

    [Fact]
    public void CancelledRecordingCannotSupplyLevelToNewRecording()
    {
        var slot = new CaptureSlot();
        var old = slot.Begin();
        old.Append(new byte[] { 0, 16 }, 2);
        var current = slot.Begin();
        old.Append(new byte[] { 255, 127 }, 2);
        Assert.Null(old.ReadInputPeak());
        Assert.Null(current.ReadInputPeak());
        current.Append(new byte[2], 2);
        Assert.Equal(0, current.ReadInputPeak());
    }
}