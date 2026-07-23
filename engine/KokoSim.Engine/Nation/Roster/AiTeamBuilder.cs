using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
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

    // ベンチ入り20人制（背番号1〜20）: 先発9（野手8＋エース1）＋控え野手8（10〜17）＋控え投手3（18〜20）＝20。
    // StrengthTeamFactory と同じ背番号規則（design-06 §3.3b）。
    private const int BenchFielderSlots = 8;
    private const int BullpenSlots = 3;

    /// <summary>
    /// ロスターから Team を構成する。<paramref name="modernRules"/>/<paramref name="calendarYear"/> は
    /// DH使用判断用（既定 null＝DH不使用＝従来挙動）。<paramref name="aceRest"/> はエース温存判断（issue #42）
    /// の入力（既定 null＝常時エース先発＝従来挙動）。rng を使わない純関数（決定論）。
    /// </summary>
    public static Team Build(AiSchoolRoster roster, School school, int yearIndex, AiRosterDeps deps,
        ModernRules? modernRules = null, int? calendarYear = null, AceRestContext? aceRest = null)
        => BuildFrom(roster.Players.Where(p => p.Grade is >= 1 and <= 3).ToList(), school, yearIndex, deps,
            modernRules, calendarYear, aceRest);

    /// <summary>
    /// 指定の在校生リストから Team を組む（<see cref="AiRosterCycle"/> の逆算配分が怪物除外の総合力を測るのに使う）。
    /// </summary>
    internal static Team BuildFrom(IReadOnlyList<AiPlayer> active, School school, int yearIndex, AiRosterDeps deps,
        ModernRules? modernRules = null, int? calendarYear = null, AceRestContext? aceRest = null)
    {
        var pitchers = active.Where(p => p.IsPitcher).OrderByDescending(PitcherScore).ToList();
        var fielders = active.Where(p => !p.IsPitcher).ToList();

        // 相手校の当季調子variance（Q21・2026-07-22）。(校ID, 年度, 選手ID) の決定論シードから
        // 自校と同じ定常分布 N(0, σ_stationary²) で抽選し、Player.Condition/ConditionValue へ焼く。
        // 展望（TournamentPreview）と実戦は同じ (校ID, 年度) でここを通るため観測は当季を通して固定＝
        // 「展望で見た調子がそのまま出る」一致契約と決定論（不変条件#2）を保つ。試合ごと（ラウンド単位）に
        // 揺らす per-round variance は展望との一致にラウンド識別子の伝播が要るため将来増分（OPEN-QUESTIONS Q21）。
        var conditionRoot = new Xoshiro256Random(
            0x0C0D_0000UL ^ (ulong)(long)school.Id ^ ((ulong)(long)yearIndex * 0x9E37_79B9UL));

        Player Snap(AiPlayer p, int number)
        {
            var v = FormModel.SampleInitialCondition(conditionRoot.Fork((ulong)(uint)p.Id), deps.Form);
            return p.Snapshot with
            {
                UniformNumber = number,
                Grade = p.Grade,
                ConditionValue = v,
                Condition = FormModel.Quantize(v),
            };
        }

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
            order.Add(Snap(best, PositionNumber(pos)));
        }

        // 控え野手（各守備位置の次点＝守備固め供給）＋余り。背番号10〜17（ベンチ入り20人制、StrengthTeamFactoryと同じ規則）。
        var benchFielders = fielders.Where(p => !usedFielders.Contains(p.Id))
            .OrderByDescending(FielderScore).Take(BenchFielderSlots).ToList();
        var num = 10;
        foreach (var f in benchFielders)
            bench.Add(Snap(f, num++));

        // エース（最良投手）＝背番号は常に AceUniformNumber（1）。控え投手は背番号18〜20（最大3人）。
        // 番号は温存の有無に関わらずロスター順位で固定＝大会を跨いだ台帳キー（#41）の同定を安定させる。
        var trueAce = pitchers.FirstOrDefault();
        var snappedAce = trueAce is not null ? Snap(trueAce, AceRestSelector.AceUniformNumber) : null;
        var pnum = 18;
        var relief = pitchers.Skip(1).Take(BullpenSlots).Select(p => Snap(p, pnum++)).ToList();

        // 温存判断（issue #42）。相手・残ラウンド・エースの消耗（#41台帳）・校風から次点投手を先発させるか決める。
        var starter = snappedAce;
        var bullpen = new List<Player>();
        if (snappedAce is not null && relief.Count > 0 && aceRest is not null
            && AceRestSelector.ShouldRestAce(school, aceRest, deps.EnemyAi))
        {
            starter = relief[0];
            bullpen.Add(snappedAce);
            bullpen.AddRange(relief.Skip(1));
        }
        else
        {
            bullpen.AddRange(relief);
        }
        if (starter is not null)
            order.Add(starter);

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
