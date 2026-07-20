namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 秋季地区大会の枠配分（設計書05 §1.5, regional-tournaments.yaml / CHANGELOG 26-27）。
/// 県ごとの出場枠を、固定枠・隔年制（近畿）・開催県ボーナスから決める。地区王者は明治神宮大会へ。
/// </summary>
public sealed record RegionalFormat
{
    public string Region { get; init; } = "";
    /// <summary>都大会・全道大会など、地区大会が単一県で完結する形式。</summary>
    public bool SinglePrefecture { get; init; }
    /// <summary>開催県に+1（関東・北信越・中国 等）。</summary>
    public bool HostPrefectureBonus { get; init; }
    /// <summary>地区王者が明治神宮大会へ進むか。</summary>
    public bool ChampionToJingu { get; init; } = true;

    /// <summary>毎年固定の県別枠（近畿の大阪・兵庫=3 等）。</summary>
    public IReadOnlyDictionary<string, int> FixedBerths { get; init; } = new Dictionary<string, int>();
    /// <summary>通常の県別枠（大半の地区）。</summary>
    public IReadOnlyDictionary<string, int> BerthsPerPref { get; init; } = new Dictionary<string, int>();
    /// <summary>近畿の隔年制: 奇数年（例2025）の県別枠。</summary>
    public IReadOnlyDictionary<string, int>? OddYearBerths { get; init; }
    /// <summary>近畿の隔年制: 偶数年の県別枠。</summary>
    public IReadOnlyDictionary<string, int>? EvenYearBerths { get; init; }
    /// <summary>年→開催県。</summary>
    public IReadOnlyDictionary<int, string> HostByYear { get; init; } = new Dictionary<int, string>();

    /// <summary>指定県・年の地区大会出場枠（固定→隔年→開催県ボーナスの順で解決）。</summary>
    public int BerthsFor(string pref, int year)
    {
        var b = 0;
        if (FixedBerths.TryGetValue(pref, out var f)) b = f;
        else if (BerthsPerPref.TryGetValue(pref, out var p)) b = p;
        else
        {
            var map = year % 2 == 1 ? OddYearBerths : EvenYearBerths;
            if (map is not null && map.TryGetValue(pref, out var v)) b = v;
        }
        if (HostPrefectureBonus && HostByYear.TryGetValue(year, out var host) && host == pref) b += 1;
        return b;
    }
}

/// <summary>全10地区の枠定義（regional-tournaments.yaml のロード結果）。</summary>
public sealed record RegionalFormatSet(IReadOnlyList<RegionalFormat> Regions)
{
    public RegionalFormat? ForRegion(string region)
        => Regions.FirstOrDefault(r => r.Region == region);
}
