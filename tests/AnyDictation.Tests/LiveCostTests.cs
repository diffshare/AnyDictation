using System.Text.Json;
using Xunit;
namespace AnyDictation.Tests;
public class LiveCostTests
{
    [Fact]
    public void 一分と再送の費用を単価ごとに合計する()
    {
        var text = LiveCost.Describe(new[] { new LiveCostSnapshot(60m, .017m), new LiveCostSnapshot(30m, .02m) });
        Assert.Contains("USD 0.027000", text);
        Assert.Contains("90.0 秒", text);
        Assert.Contains("再送分を含む", text);
        Assert.Contains("請求確定額ではありません", text);
    }
    [Fact]
    public void 未設定単価を無料扱いしない()
    {
        var text = LiveCost.Describe(new[] { new LiveCostSnapshot(15m, null), new LiveCostSnapshot(2m, .017m, true) });
        Assert.Contains("費用未設定", text);
        Assert.DoesNotContain("USD 0.", text);
        Assert.Contains("17.0 秒", text);
        Assert.Contains("実際の利用量が増える", text);
    }
    [Fact]
    public void 旧設定の単価は未設定で新しい単価は保存往復できる()
    {
        Assert.Null(JsonSerializer.Deserialize<Profile>("{}")!.LiveUsdPerMinute);
        var p = TestProfiles.Create(ProviderKind.AzureOpenAiLive);
        p.LiveUsdPerMinute = .017m;
        Assert.Equal(.017m, JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(p))!.LiveUsdPerMinute);
        Assert.Empty(ProfileValidator.Validate(p));
        p.LiveUsdPerMinute = -1m;
        Assert.NotEmpty(ProfileValidator.Validate(p));
        p.LiveUsdPerMinute = 0m;
        Assert.NotEmpty(ProfileValidator.Validate(p));
    }
}
