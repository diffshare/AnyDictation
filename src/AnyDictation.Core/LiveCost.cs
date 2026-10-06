using System.Globalization;
namespace AnyDictation;
public sealed record LiveCostSnapshot(decimal SentSeconds, decimal? UsdPerMinute, bool SendUncertain = false);
public static class LiveCost
{
    // 送信完了した PCM の時間による概算。サービス受信量・契約単価・課金丸めとは異なり得る。
    public static string Describe(IEnumerable<LiveCostSnapshot> attempts)
    {
        var list = attempts.ToArray();
        if (list.Length == 0) return "";
        string seconds = list.Sum(x => x.SentSeconds).ToString("0.0", CultureInfo.InvariantCulture);
        string retry = list.Length > 1 ? "（再送分を含む）" : "";
        string uncertain = list.Any(x => x.SendUncertain) ? "\n送信途中の音声は含まず、実際の利用量が増える可能性があります。" : "";
        if (list.Any(x => x.UsdPerMinute is null or <= 0 or > 1000000))
            return $"今回の Live: 送信済 {seconds} 秒{retry} / 費用未設定\n設定で Azure の単価（USD/分）を入力してください。" + uncertain;
        decimal cost = list.Sum(x => x.SentSeconds / 60m * x.UsdPerMinute!.Value);
        return $"今回の Live 概算: USD {cost.ToString("0.000000", CultureInfo.InvariantCulture)} / 送信済 {seconds} 秒{retry}\n請求確定額ではありません。" + uncertain;
    }
}
