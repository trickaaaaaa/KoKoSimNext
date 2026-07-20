using KokoSim.Engine.Core;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>県大会の結果。地区大会へ進む出場校（regional_berths）と最終順位。</summary>
public sealed record PrefTournamentResult(
    IReadOnlyList<School> Qualifiers,
    School Champion,
    IReadOnlyList<School> FinalPlacement)
{
    /// <summary>試合ごとの記録（球場割当つき）。recordMatches=false なら空（設計書13-stadiums §2-2）。</summary>
    public IReadOnlyList<TournamentMatchRecord> Matches { get; init; } = System.Array.Empty<TournamentMatchRecord>();
}

/// <summary>
/// 県フォーマット（設計書05 §1.5）を実行する。ステージ列（group_split→県大会 等）を順に処理し、
/// regional_berths 校を地区大会へ送り出す。予選免除（seed_exemption）の推薦校は県大会から登場。
/// エンジンは pref-format の型を解釈するだけで、県ごとの差異は全て data で表現される（不変条件#4）。決定論。
/// </summary>
public static class PrefTournamentEngine
{
    /// <summary>専用の球場抽選ストリームid（本編乱数を汚さない。Fork は親状態を進めない＝決定論維持）。</summary>
    private const ulong StadiumStreamId = 13; // 設計書13

    public static PrefTournamentResult Run(
        PrefFormat format, IReadOnlyList<School> entrants, NationCoefficients coeff, IRandomSource rng,
        School? recommended = null, bool recordMatches = false)
    {
        if (entrants.Count == 0) throw new System.ArgumentException("参加校が空です。");

        System.Func<School, int?>? districtOf = format.DistrictAssignment == DistrictAssignment.Geographic
            ? s => s.DistrictId
            : null;

        // 記録モードのときだけ recorder を組む。球場抽選は fork した専用ストリームで行うため、
        // 本編の rng 消費順は不変＝recordMatches の有無で大会結果は変わらない（不変条件#2）。
        var recorder = recordMatches
            ? new TournamentRecorder(format.Stadiums, rng.Fork(StadiumStreamId))
            : null;

        var pool = entrants.ToList();
        var berths = System.Math.Max(1, format.RegionalBerths);
        KnockoutResult? lastKnock = null;
        IReadOnlyList<Standing>? lastStandings = null;
        var recommendedAdded = false;

        foreach (var stage in format.Stages)
        {
            if (recorder is not null) recorder.CurrentStage = stage.Name;
            switch (stage.Type)
            {
                case StageType.GroupSplit:
                    pool = GroupSplit.Run(pool, stage, coeff, rng, districtOf, recorder).ToList();
                    lastStandings = null;
                    lastKnock = null;
                    break;

                case StageType.RoundRobin:
                    lastStandings = RoundRobin.Run(pool, coeff, rng, recorder);
                    pool = lastStandings.Select(s => s.School).ToList();
                    lastKnock = null;
                    break;

                case StageType.Knockout:
                    // 予選免除（設計書05 §1.5 / CHANGELOG 27）: 夏甲子園校を県大会（knockoutステージ）へ推薦。
                    if (format.SeedExemption && recommended is not null && !recommendedAdded)
                    {
                        pool.Add(recommended);
                        recommendedAdded = true;
                    }
                    lastKnock = Knockout.Run(pool, coeff, rng, stage.ThirdPlaceMatch, recorder);
                    pool = lastKnock.Placement.ToList();
                    lastStandings = null;
                    break;
            }
        }

        var matches = recorder?.Records ?? System.Array.Empty<TournamentMatchRecord>();

        // 最終ステージの型で出場校・優勝・順位を決める。
        if (lastKnock is not null)
        {
            return new PrefTournamentResult(lastKnock.Top(berths), lastKnock.Champion, lastKnock.Placement)
            {
                Matches = matches,
            };
        }
        if (lastStandings is not null)
        {
            var placement = lastStandings.Select(s => s.School).ToList();
            return new PrefTournamentResult(placement.Take(berths).ToList(), placement[0], placement)
            {
                Matches = matches,
            };
        }
        // 最終が group_split（進出校リストのみ）の場合はその並びを順位とみなす。
        return new PrefTournamentResult(pool.Take(berths).ToList(), pool[0], pool) { Matches = matches };
    }
}
