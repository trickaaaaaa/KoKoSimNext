using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Nation.Roster;

/// <summary>
/// 永続ロスター（<see cref="AiSchoolRoster"/>）から試合可能な <see cref="Team"/> を組む（ベンチ20人選抜）。
/// 守備位置ごとに最良の在校生を先発へ、残りを控え・ブルペンへ。打順編成は既存 <see cref="LineupOrderer.Compose"/>
/// をそのまま通す＝展望（TournamentPreview）と実戦が同じここを通り自動で一致する（不変条件#2・rng不使用の決定論変換）。
/// 各 <see cref="Player.SourceId"/> は生成時の校内選手ID＝全校横断の成績帰属キー。
/// </summary>
public static class AiTeamBuilder
{
    private static readonly FieldPosition[] FieldSlots =
    {
        FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
        FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField,
        FieldPosition.CenterField, FieldPosition.RightField,
    };

    /// <summary>
    /// ロスターから Team を構成する。<paramref name="modernRules"/>/<paramref name="calendarYear"/> は
    /// DH使用判断用（既定 null＝DH不使用＝従来挙動）。rng を使わない純関数（決定論）。
    /// </summary>
    public static Team Build(AiSchoolRoster roster, School school, int yearIndex, AiRosterDeps deps,
        ModernRules? modernRules = null, int? calendarYear = null)
        => BuildFrom(roster.Players.Where(p => p.Grade is >= 1 and <= 3).ToList(), school, yearIndex, deps,
            modernRules, calendarYear);

    /// <summary>
    /// 指定の在校生リストから Team を組む（<see cref="AiRosterCycle"/> の逆算配分が怪物除外の総合力を測るのに使う）。
    /// </summary>
    internal static Team BuildFrom(IReadOnlyList<AiPlayer> active, School school, int yearIndex, AiRosterDeps deps,
        ModernRules? modernRules = null, int? calendarYear = null)
    {
        var pitchers = active.Where(p => p.IsPitcher).OrderByDescending(PitcherScore).ToList();
        var fielders = active.Where(p => !p.IsPitcher).ToList();

        var order = new List<Player>(9);
        var bench = new List<Player>();
        var usedFielders = new HashSet<int>();

        // 先発野手8人（守備位置ごとに最良の1人）。各コホートに全守備位置1人ずつ入るため常に候補が居る。
        foreach (var pos in FieldSlots)
        {
            var best = fielders
                .Where(p => p.Position == pos && !usedFielders.Contains(p.Id))
                .OrderByDescending(FielderScore)
                .FirstOrDefault();
            if (best is null) continue;   // 保険（コホート構成上は起きない）
            usedFielders.Add(best.Id);
            order.Add(best.Snapshot with { UniformNumber = PositionNumber(pos), Grade = best.Grade });
        }

        // エース（最良投手）を打順末尾（投手スロット8）へ。
        var ace = pitchers.FirstOrDefault();
        if (ace is not null)
            order.Add(ace.Snapshot with { UniformNumber = 1, Grade = ace.Grade });

        // 控え野手（各守備位置の次点＝守備固め供給）＋余り。背番号10〜。
        var benchFielders = fielders.Where(p => !usedFielders.Contains(p.Id))
            .OrderByDescending(FielderScore).ToList();
        var num = 10;
        foreach (var f in benchFielders)
            bench.Add(f.Snapshot with { UniformNumber = num++, Grade = f.Grade });

        // ブルペン（エース以外の投手）。背番号18〜。
        var bullpen = new List<Player>();
        var pnum = 18;
        foreach (var p in pitchers.Skip(1))
            bullpen.Add(p.Snapshot with { UniformNumber = pnum++, Grade = p.Grade });

        var team = new Team
        {
            Name = school.Name,
            BattingOrder = order,
            PitcherSlot = 8,
            Bullpen = bullpen,
            Bench = bench,
        };
        return LineupOrderer.Compose(team, deps.Roster.Lineup, modernRules, calendarYear);
    }

    /// <summary>投手の起用序列スコア（球速レベル＋制球＋キレ＋スタミナレベル）。</summary>
    private static double PitcherScore(AiPlayer p)
    {
        var a = p.Snapshot.Pitching;
        if (a is null) return 0;
        return PitcherAttributes.LevelFromVelocityKmh(a.MaxVelocityKmh) + a.Control + a.PitchRank
            + PitcherAttributes.LevelFromStaminaPitches(a.StaminaPitches);
    }

    /// <summary>野手の起用序列スコア（打撃＋守備＋走の素点和）。ランク付け専用（帯には効かない）。</summary>
    private static double FielderScore(AiPlayer p)
    {
        var s = p.Snapshot;
        return s.Contact + s.Power + s.Discipline + s.Speed + s.ArmStrength + s.Fielding + s.Catching;
    }

    private static int PositionNumber(FieldPosition pos) => pos switch
    {
        FieldPosition.Pitcher => 1,
        FieldPosition.Catcher => 2,
        FieldPosition.FirstBase => 3,
        FieldPosition.SecondBase => 4,
        FieldPosition.ThirdBase => 5,
        FieldPosition.Shortstop => 6,
        FieldPosition.LeftField => 7,
        FieldPosition.CenterField => 8,
        FieldPosition.RightField => 9,
        _ => 0,
    };
}
