using System.Text.Json;
using Xunit;

namespace AnyDictation.Tests;

public class MicrophoneTests
{
    [Fact]
    public void MissingSettingPreservesWindowsDefault()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"Version\":1}")!;
        Assert.Null(settings.MicrophoneDeviceId);
        Assert.Equal(-1, MicrophoneSelection.Resolve(settings.MicrophoneDeviceId, Array.Empty<MicrophoneDevice>()));
    }

    [Fact]
    public void SameNameDevicesResolveByInterfacePathAfterReordering()
    {
        var devices = new[] { new MicrophoneDevice(2, "device-b", "USB microphone"), new MicrophoneDevice(7, "device-a", "USB microphone") };
        Assert.Equal(7, MicrophoneSelection.Resolve("DEVICE-A", devices));
        Assert.Equal(2, MicrophoneSelection.Resolve("device-b", devices));
    }

    [Fact]
    public void MissingSavedDeviceDoesNotFallbackToDefaultOrSameName()
    {
        var devices = new[] { new MicrophoneDevice(0, "replacement", "USB microphone") };
        Assert.Throws<InvalidOperationException>(() => MicrophoneSelection.Resolve("disconnected", devices));
    }

    [Fact]
    public void DuplicateInterfaceIsRejectedRatherThanSelectingFirstDevice()
    {
        var devices = new[] { new MicrophoneDevice(1, "dup", "a"), new MicrophoneDevice(2, "DUP", "b") };
        Assert.Throws<InvalidOperationException>(() => MicrophoneSelection.Resolve("dup", devices));
    }

    [Fact]
    public void UnidentifiedDeviceIsNotSelectedByNameOrEmptyIdentifier()
    {
        var devices = new[] { new MicrophoneDevice(0, null, "a"), new MicrophoneDevice(1, "", "b") };
        Assert.Throws<InvalidOperationException>(() => MicrophoneSelection.Resolve("", devices));
        Assert.Throws<InvalidOperationException>(() => MicrophoneSelection.Resolve("a", devices));
    }

    [Theory]
    [InlineData(SessionState.Recording)]
    [InlineData(SessionState.Recognizing)]
    [InlineData(SessionState.RetryPending)]
    public void TestCannotAcquireMicrophoneDuringDictation(SessionState state)
    {
        var gate = new MicrophoneUseGate();
        Assert.False(gate.TryBeginTest(state));
        Assert.False(gate.IsTesting);
    }

    [Fact]
    public void TestLeaseBlocksSecondTestAndCanBeReleasedAfterFailure()
    {
        var gate = new MicrophoneUseGate();
        Assert.True(gate.TryBeginTest(SessionState.Idle));
        Assert.True(gate.IsTesting);
        Assert.False(gate.TryBeginTest(SessionState.Idle));
        gate.EndTest();
        gate.EndTest();
        Assert.False(gate.IsTesting);
        Assert.True(gate.TryBeginTest(SessionState.Idle));
    }

    [Fact]
    public void TestTracksOnlyRecentPeakAndDistinguishesNoBuffersFromSilence()
    {
        var level = new InputLevelSession();
        Assert.Null(level.ReadPeak());
        level.Add(new byte[] { 0, 0 });
        Assert.Equal(0, level.ReadPeak());
        level.Add(new byte[] { 0xFF, 0x7F });
        level.Add(new byte[] { 10, 0 });
        Assert.Equal(32767, level.ReadPeak());
        Assert.Equal(0, level.ReadPeak());
    }

    [Fact]
    public void ClosingIgnoresLateBuffersWithoutAffectingNewTest()
    {
        var old = new InputLevelSession();
        old.Add(new byte[] { 0, 0x80 });
        old.Close();
        var current = new InputLevelSession();
        old.Add(new byte[] { 0xFF, 0x7F });
        Assert.False(old.IsOpen);
        Assert.Null(old.ReadPeak());
        Assert.Null(current.ReadPeak());
        current.Add(new byte[] { 100, 0 });
        Assert.Equal(100, current.ReadPeak());
    }

    [Fact]
    public async Task LevelReaderAndCallbackCanRaceWithClose()
    {
        var level = new InputLevelSession();
        await Task.WhenAll(Task.Run(() => { for (int i = 0; i < 500; i++) level.Add(new byte[] { 0, 0x80 }); }),
            Task.Run(() => { for (int i = 0; i < 500; i++) level.ReadPeak(); }), Task.Run(level.Close));
        Assert.Null(level.ReadPeak());
        Assert.False(level.IsOpen);
    }

    [Fact]
    public void MicrophoneChoiceRoundTripsThroughSettingsFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "AnyDictation-microphone-" + Guid.NewGuid());
        string path = Path.Combine(dir, "settings.json");
        try
        {
            var store = new JsonFileStore<AppSettings>(path, AppSettings.Validate);
            store.Load();
            store.Save(new AppSettings { MicrophoneDeviceId = "\\\\?\\usb#unique-interface" });
            var loaded = new JsonFileStore<AppSettings>(path, AppSettings.Validate);
            loaded.Load();
            Assert.False(loaded.IsCorrupt);
            Assert.Equal("\\\\?\\usb#unique-interface", loaded.Value.MicrophoneDeviceId);
            Assert.Empty(AppSettings.Validate(loaded.Value));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void EmptySavedIdentifierIsInvalidInsteadOfDefault(string id)
        => Assert.NotEmpty(AppSettings.Validate(new AppSettings { MicrophoneDeviceId = id }));

    [Fact]
    public void DefaultLabelNamesTheCurrentDeviceWithTheSameFormatAsTheList()
    {
        var devices = new[] { new MicrophoneDevice(0, "a", "Mic A"), new MicrophoneDevice(1, "b", "Mic B") };
        Assert.Equal("Windows の既定のマイク（現在: Mic B［2］）", MicrophoneSelection.DescribeDefault(1, devices));
        Assert.Equal("Mic B［2］", MicrophoneSelection.Display(devices[1]));
    }

    [Fact]
    public void DefaultLabelSeparatesNoDeviceFromUnreadable()
    {
        var devices = new[] { new MicrophoneDevice(0, "a", "Mic A") };
        Assert.Contains("利用できるマイクなし", MicrophoneSelection.DescribeDefault(-1, devices));
        Assert.Contains("取得できません", MicrophoneSelection.DescribeDefault(null, devices));
        Assert.Contains("取得できません", MicrophoneSelection.DescribeDefault(5, devices)); // 一覧にない番号は名前を推測しない
    }

    const string HandsFreeId = @"\\?\bthhfenum#bthhfpaudio#0&0&1#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\wave";

    [Theory]
    [InlineData(HandsFreeId, true)]
    [InlineData(@"\\?\BTHHFENUM#BthHFPAudio#x\wave", true)]
    [InlineData(@"\\?\usb#vid_0000&pid_0000&mi_00#0&0&0&0000#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\global", false)]
    [InlineData(null, false)]
    public void HandsFreeMicrophoneIsDetectedByInterfacePath(string? id, bool expected)
        => Assert.Equal(expected, MicrophoneSelection.IsBluetoothHandsFree(id));

    [Fact]
    public void HandsFreeMicrophoneIsMarkedInListAndDefaultLabel()
    {
        var devices = new[] { new MicrophoneDevice(0, HandsFreeId, "Headset (Hands-Free)") };
        Assert.Equal("Headset (Hands-Free)［1］（Bluetooth ハンズフリー: 開始に時間がかかることがあります）", MicrophoneSelection.Display(devices[0]));
        Assert.Contains("Bluetooth ハンズフリー", MicrophoneSelection.DescribeDefault(0, devices));
    }

    [Fact]
    public void SlowStartupIsDescribedOnlyAtOrAboveThreshold()
    {
        Assert.Null(new MicrophoneStartupNotice().Take(TimeSpan.FromMilliseconds(499)));
        Assert.NotNull(new MicrophoneStartupNotice().Take(MicrophoneStartupNotice.SlowThreshold));
        Assert.StartsWith("マイクの起動に 1.2 秒かかりました。", new MicrophoneStartupNotice().Take(TimeSpan.FromMilliseconds(1197)));
    }

    [Fact]
    public void SlowStartupIsDescribedOnlyOnceEvenAfterFastStartups()
    {
        var notice = new MicrophoneStartupNotice();
        Assert.Null(notice.Take(TimeSpan.FromMilliseconds(100))); // 速い起動は 1 回分に数えない
        Assert.NotNull(notice.Take(TimeSpan.FromSeconds(1)));
        Assert.Null(notice.Take(TimeSpan.FromSeconds(2)));
    }
}
