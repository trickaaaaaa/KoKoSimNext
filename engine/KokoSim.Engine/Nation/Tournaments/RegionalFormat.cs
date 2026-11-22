namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 地区大会の「出場校決定戦」（近畿・2026年秋〜）。<see cref="BerthsPerPref"/> の県は直接の出場枠が<b>各県そのぶんだけ</b>で、
/// 県大会の <see cref="CandidateRank"/> 位の校どうしが抽選で2校ずつ当たり、勝った校が地区大会へ進む（負けた校は出られない）。
/// </summary>
public sealed record RegionalPlayoffRule
{
    /// <summary>この年（暦年）の秋から有効。これより前は隔年制（<see cref="RegionalFormat.OddYearBerths"/> 等）。</summary>
    public int FromYear { get; init; }
    /// <summary>決定戦に候補を出す県と、その県の直接枠。</summary>
    public IReadOnlyDictionary<string, int> BerthsPerPref { get; init; } = new Dictionary<string, int>();
    /// <summary>決定戦に出る県大会の順位（1始まり。近畿は3位）。</summary>
    public int CandidateRank { get; init; } = 3;
}

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
    /// <summary>出場校決定戦（近畿の2026年〜）。無い地区・無い年は null か <see cref="PlayoffActive"/> が false。</summary>
    public RegionalPlayoffRule? Playoff { get; init; }
    /// <summary>年→開催県。</summary>
    public IReadOnlyDictionary<int, string> HostByYear { get; init; } = new Dictionary<int, string>();

    /// <summary>その年は出場校決定戦で枠が決まる地区か。</summary>
    public bool PlayoffActive(int year) => Playoff is { } p && year >= p.FromYear;

    /// <summary>
    /// 指定県・年で、県大会の何番目（0基点の添字）の校が出場校決定戦の候補になるか。決定戦に候補を出さない県・年は null。
    /// </summary>
    public int? PlayoffCandidateIndex(string pref, int year)
        => Playoff is { } p && PlayoffActive(year) && p.BerthsPerPref.ContainsKey(pref) ? p.CandidateRank - 1 : null;

    /// <summary>指定県・年の地区大会への直接の出場枠（固定→決定戦の県→隔年→開催県ボーナスの順で解決。決定戦の勝ち上がりは含まない）。</summary>
    public int BerthsFor(string pref, int year)
    {
        var b = 0;
        if (FixedBerths.TryGetValue(pref, out var f)) b = f;
        else if (BerthsPerPref.TryGetValue(pref, out var p)) b = p;
        else if (PlayoffActive(year) && Playoff!.BerthsPerPref.TryGetValue(pref, out var pb)) b = pb;
        else
        {
            var map = year % 2 == 1 ? OddYearBerths : EvenYearBerths;
            if (map is not null && map.TryGetValue(pref, out var v)) b = v;
        }
        if (HostPrefectureBonus && HostFor(year) == pref) b += 1;
        return b;
    }

    /// <summary>
    /// 指定年の開催県。表（<see cref="HostByYear"/>）にある年はそのまま、無い年は直近の表の年を起点に
    /// 地区内の県をJIS順で1年1県ずつ持ち回りで巡回する（表より前の年は逆向き）。表が空なら null。
    /// </summary>
    public string? HostFor(int year)
    {
        if (HostByYear.Count == 0) return null;
        if (HostByYear.TryGetValue(year, out var known)) return known;

        var anchorYear = HostByYear.Keys.Where(y => y < year).DefaultIfEmpty(int.MinValue).Max();
        if (anchorYear == int.MinValue) anchorYear = HostByYear.Keys.Min();
        var anchorHost = HostByYear[anchorYear];

        var cycle = FixedBerths.Keys.Concat(BerthsPerPref.Keys).Concat(HostByYear.Values)
            .Concat(OddYearBerths?.Keys ?? Enumerable.Empty<string>())
            .Concat(EvenYearBerths?.Keys ?? Enumerable.Empty<string>())
            .Concat(Playoff?.BerthsPerPref.Keys ?? Enumerable.Empty<string>())
            .Distinct()
            .OrderBy(n => { var i = PrefectureNames.JisOrder.ToList().IndexOf(n); return i < 0 ? int.MaxValue : i; })
            .ThenBy(n => n, StringComparer.Ordinal)
            .ToList();
        var at = cycle.IndexOf(anchorHost);
        if (at < 0 || cycle.Count == 0) return anchorHost;
        var step = (year - anchorYear) % cycle.Count;
        return cycle[((at + step) % cycle.Count + cycle.Count) % cycle.Count];
    }
}

/// <summary>全10地区の枠定義（regional-tournaments.yaml のロード結果）。</summary>
public sealed record RegionalFormatSet(IReadOnlyList<RegionalFormat> Regions)
{
    public RegionalFormat? ForRegion(string region)
        => Regions.FirstOrDefault(r => r.Region == region);
}
