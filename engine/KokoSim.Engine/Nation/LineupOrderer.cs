using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Nation;

/// <summary>
/// 打順編成＋DH使用判断の係数（issue #54, 設計書11 §4「オーダー編成」）。
/// 自校・AI校で共有する RosterCoefficients に同居させる（<see cref="Season.RosterCoefficients.Lineup"/>）。
/// </summary>
public sealed record LineupCoefficients
{
    // --- 1番: 出塁力＋走力 ---
    public double LeadoffDisciplineWeight { get; init; } = 0.45;
    public double LeadoffContactWeight { get; init; } = 0.30;
    public double LeadoffSpeedWeight { get; init; } = 0.25;

    // --- 2番: 小技・ミート ---
    public double SecondContactWeight { get; init; } = 0.55;
    public double SecondBuntWeight { get; init; } = 0.45;

    // --- 打撃総合（3〜5番の中軸＋6番以降の残り, 最強打者は4番） ---
    public double OverallContactWeight { get; init; } = 0.5;
    public double OverallPowerWeight { get; init; } = 0.5;

    // --- DH使用判断: 投手の打撃総合が野手平均よりこれ以上低ければDHを使う ---
    public double DhPitcherBattingGap { get; init; } = 15.0;
}

/// <summary>
/// 敵校の打順編成＋DH使用判断（issue #54, 設計書11 §4）。校ID＋年度で決定論生成済みの
/// <see cref="Team"/>（能力ロール済み）だけを材料にする純関数＝乱数を一切使わない。
/// 大会展望（<see cref="Tournaments.TournamentPreviewBuilder"/>）と実戦（Shell の
/// PlayerMatchResolver.BuildOpponentTeam）が同じ <see cref="StrengthTeamFactory"/> を通ることで
/// 展望と実戦は自動で一致する（不変条件#2）。
/// </summary>
public static class LineupOrderer
{
    /// <summary>打撃総合（ミート＋パワー）。DH判断・中軸〜残りの並べ替えで共通に使う指標。</summary>
    public static double BattingScore(Player p, LineupCoefficients c)
        => p.Contact * c.OverallContactWeight + p.Power * c.OverallPowerWeight;

    /// <summary>
    /// 打者集団を能力で並べる（守備位置は変えない・人数分だけ返す）。
    /// 1番=出塁力＋走力、2番=小技・ミート、中軸（最強打者を4番に置き3番→5番の順で補充）、
    /// 6番以降=残りを打撃総合の高い順（設計書11 §4「オーダー編成」の能力軸）。
    /// </summary>
    public static IReadOnlyList<Player> Arrange(IReadOnlyList<Player> batters, LineupCoefficients? coeff = null)
    {
        var c = coeff ?? new LineupCoefficients();
        var n = batters.Count;
        var pool = batters.Select((p, i) => (Player: p, Index: i)).ToList();
        var result = new Player?[n];

        Player Take(Func<Player, double> score)
        {
            var best = pool
                .Select(x => (x.Player, x.Index, Score: score(x.Player)))
                .OrderByDescending(x => x.Score).ThenBy(x => x.Index)
                .First();
            pool.RemoveAll(x => x.Index == best.Index);
            return best.Player;
        }

        double OnBase(Player p) => p.Discipline * c.LeadoffDisciplineWeight
            + p.Contact * c.LeadoffContactWeight + p.Speed * c.LeadoffSpeedWeight;
        double SmallBall(Player p) => p.Contact * c.SecondContactWeight + p.Bunt * c.SecondBuntWeight;
        double Overall(Player p) => BattingScore(p, c);

        void Fill(int slot, Func<Player, double> score)
        {
            if (slot < 0 || slot >= n || result[slot] is not null) return;
            result[slot] = Take(score);
        }

        Fill(0, OnBase);
        Fill(1, SmallBall);
        // 中軸: 最強打者を4番（3番目のスロット, index 3）に置き、次点を3番、その次を5番に置く。
        foreach (var slot in new[] { 3, 2, 4 }) Fill(slot, Overall);
        // 残り: 打撃総合の高い順に前詰め（6番以降）。
        for (var i = 0; i < n; i++) Fill(i, Overall);

        return result!;
    }

    /// <summary>投手の打撃総合が野手平均より係数以上低ければDHを使う（打撃型エースはDHを使わない）。</summary>
    public static bool ShouldUseDh(Player pitcher, IReadOnlyList<Player> fielders, LineupCoefficients c)
    {
        if (fielders.Count == 0) return false;
        var teamAvg = fielders.Average(p => BattingScore(p, c));
        return teamAvg - BattingScore(pitcher, c) >= c.DhPitcherBattingGap;
    }

    /// <summary>
    /// 生成済みチーム（8野手＋投手9番, <see cref="StrengthTeamFactory"/> 出力）へ能力ベースの打順編成と
    /// DH使用判断を適用する。渡された rng を一切使わない決定論関数（不変条件#2）。
    /// <paramref name="modernRules"/>/<paramref name="calendarYear"/> が両方揃ったときだけDHを検討する
    /// （既定 null＝DH不使用＝従来挙動と完全一致）。
    /// </summary>
    public static Team Compose(Team team, LineupCoefficients coeff, ModernRules? modernRules, int? calendarYear)
    {
        var c = coeff;
        var fielders = team.BattingOrder.Take(8).ToList();
        var pitcher = team.BattingOrder[8];

        var useDh = modernRules is not null && calendarYear is not null
            && modernRules.DhEnabled(calendarYear.Value) && team.Bench.Count > 0
            && ShouldUseDh(pitcher, fielders, c);

        if (!useDh)
        {
            var arrangedFielders = Arrange(fielders, c);
            var order = arrangedFielders.Append(pitcher).ToList();
            return team with { BattingOrder = order };
        }

        // ベンチから最強打者を繰り上げ、打順から抜けた投手は StartingPitcher へ（設計書09 §6）。
        var dhBatter = team.Bench.OrderByDescending(p => BattingScore(p, c)).First();
        var hitters = fielders.Append(dhBatter).ToList();
        var arranged = Arrange(hitters, c);
        var dhSlot = IndexOfReference(arranged, dhBatter);
        var bench = team.Bench.Where(p => !ReferenceEquals(p, dhBatter)).ToList();

        return team with
        {
            BattingOrder = arranged,
            DhSlot = dhSlot,
            StartingPitcher = pitcher,
            PitcherSlot = -1,
            Bench = bench,
        };
    }

    private static int IndexOfReference(IReadOnlyList<Player> list, Player target)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], target)) return i;
        }
        return -1;
    }
}
