using System;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Saves;

/// <summary>
/// 表示能力の物差し（issue #1080）のセーブ移行。セーブは保存時の Δ（<c>abilityDelta</c>・v42 以前は無い＝0）を持ち、
/// 読み込み時に「保存時の Δ − いまの Δ」だけ表示Lvをずらす＝物理Lv（今の物差し）は保存したときと同じ。
/// 対象: 自校の部員（能力・才能上限・精神力・リード・成長推移）、入部予定者（本人と見立て）、スカウト候補（本人と見立て）、
/// 全国の永続ロスター（AI校の選手）。弾道・守備位置適性・性格・km/h・目安投球数は物差しの外なので触らない。
/// </summary>
public static class AbilityScaleSaveMigration
{
    /// <summary>セーブに <c>abilityDelta</c> が無い（v42 以前）＝物差しを下げる前。</summary>
    public const int LegacyDelta = 0;

    public static void Apply(SaveState s, int savedDelta)
    {
        var by = savedDelta - AbilityScale.Delta;
        if (by == 0) return;

        foreach (var p in s.Roster) ShiftDeveloping(p, by);
        if (s.Incoming is { } inc)
        {
            foreach (var p in inc.Players) ShiftDeveloping(p, by);
            foreach (var r in inc.Reports)
            {
                r.Batting = Composite(r.Batting, by, BattingWeight);
                r.Mobility = Composite(r.Mobility, by, MobilityWeight);
                r.Defense = Composite(r.Defense, by, DefenseWeight);
                r.Pitching = Composite(r.Pitching, by, PitchingWeight);
            }
        }
        if (s.Scouting is { } sc)
        {
            foreach (var c in sc.Candidates)
            {
                ShiftDeveloping(c.Player, by);
                ShiftReport(c.Report, by);
            }
        }
        foreach (var roster in s.NationRosters)
            foreach (var ai in roster.Players)
                ai.Snapshot = AbilityScale.ShiftBy(ai.Snapshot, by);
    }

    private static void ShiftDeveloping(DevelopingPlayer p, int by)
    {
        DevelopingScale.Shift(p, by);
        for (var i = 0; i < p.History.Count; i++)
        {
            var h = p.History[i];
            var levels = new int[h.Levels.Count];
            for (var k = 0; k < levels.Length; k++)
                levels[k] = k == (int)AbilityKind.LaunchTendency ? h.Levels[k] : Move(h.Levels[k], by);
            p.History[i] = h with { Levels = levels };
        }
    }

    // 見立ての4分野はチーム総合と同じ合成（重みの和のぶんだけ動く。打撃は弾道を含むので 0.85）。
    private static readonly TeamStrengthCoefficients W = new();
    private static double BattingWeight => W.ContactWeight + W.PowerWeight + W.DisciplineWeight;
    private static double MobilityWeight => W.SpeedWeight + W.StealWeight;
    private static double DefenseWeight => W.FieldingWeight + W.CatchingWeight + W.ArmWeight;
    private static double PitchingWeight => W.VelocityWeight + W.ControlWeight + W.StaminaWeight + W.PitchRankWeight;

    private static double Composite(double v, int by, double weight) => Math.Max(0.0, Math.Min(100.0, v + by * weight));

    private static void ShiftReport(ScoutReport r, int by)
    {
        r.Batting = Composite(r.Batting, by, BattingWeight);
        r.Mobility = Composite(r.Mobility, by, MobilityWeight);
        r.Defense = Composite(r.Defense, by, DefenseWeight);
        r.Pitching = Composite(r.Pitching, by, PitchingWeight);
        double L(double v) => Math.Max(AbilityScale.DisplayMin, Math.Min(99.0, v + by));
        r.Contact = L(r.Contact); r.Power = L(r.Power); r.Discipline = L(r.Discipline); r.Speed = L(r.Speed);
        r.Fielding = L(r.Fielding); r.Catching = L(r.Catching); r.ThrowAccuracy = L(r.ThrowAccuracy);
        r.Baserunning = L(r.Baserunning); r.Velocity = L(r.Velocity); r.Control = L(r.Control);
        r.PitchRank = L(r.PitchRank); r.Stamina = L(r.Stamina);
    }

    private static int Move(int v, int by) => by < 0 ? Math.Max(AbilityScale.DisplayMin, v + by) : v + by;
}
