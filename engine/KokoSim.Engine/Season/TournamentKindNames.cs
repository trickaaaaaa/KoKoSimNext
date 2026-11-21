namespace KokoSim.Engine.Season;

/// <summary>
/// 大会種別の呼び名の単一ソース。全国大会の名前は <see cref="DisplayNames"/>（data/display-names.yaml）から引く（issue #1319）。
/// 開幕バナー・日付の小書き・記録タブの歴代大会結果が同じ関数を引く。
/// <see cref="TournamentKind"/> を増やしたら <c>Of</c> の switch に足す（足し忘れは網羅テストが赤にする）。
/// </summary>
public static class TournamentKindNames
{
    /// <summary>呼び名（"夏" / "秋" / "甲子園" / "センバツ" / "地区大会" / "明治神宮大会" / "地区予選"）。</summary>
    public static string Of(TournamentKind kind) => kind switch
    {
        TournamentKind.Summer => "夏",
        TournamentKind.Autumn => "秋",
        TournamentKind.SummerKoshien => DisplayNames.Current.Koshien,
        TournamentKind.SenbatsuKoshien => DisplayNames.Current.Senbatsu,
        TournamentKind.Regional => "地区大会",
        TournamentKind.Jingu => DisplayNames.Current.Jingu,
        TournamentKind.AutumnDistrict => "地区予選",
        _ => throw new System.ArgumentOutOfRangeException(nameof(kind), kind, "未対応の大会種別"),
    };

    /// <summary>県単位の大会か（夏・秋季県大会・地区予選）。全国／地区ブロックの大会は県名を添えない。</summary>
    public static bool IsPrefectural(TournamentKind kind)
        => kind is TournamentKind.Summer or TournamentKind.Autumn or TournamentKind.AutumnDistrict;

    /// <summary>日付の小書き用（"夏の大会" / "甲子園" / "センバツ"）。夏・秋だけ「の大会」を足す。</summary>
    public static string Short(TournamentKind kind)
        => kind is TournamentKind.Summer or TournamentKind.Autumn ? Of(kind) + "の大会" : Of(kind);
}
