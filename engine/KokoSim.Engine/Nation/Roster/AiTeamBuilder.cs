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
/// 守備位置ごとに最良の在校生を先発へ、残りを控えへ。打順編成は既存 <see cref="LineupOrderer.Compose"/>
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
    private const int ReservePitcherSlots = 3;

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
        // 誰がマウンドに立ち誰がどこを守るかは、選手が覚えている配置ではなく**毎回の選抜**で決める（issue #384）。
        // ① マウンド組＝投手能力（ReliefScore と同じ合成）の上位。全員が投手能力を持つ（#381）ので
        //    「投手として育った者」が自然に上へ来る。人数は投手適性が野手適性を上回る者の数（豊作/苦作の年が出る）。
        // ② 守備8枠＝残りから実効守備力（地力×適性）の総和が最大になる割当（ハンガリアン法）。
        var (pitchers, fielders) = SelectMoundGroup(active, deps.Roster, deps.Persistent);

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

        // 先発野手8人＝実効守備力の総和が最大になる割当（issue #384）。守備位置ごとに独立で最良を採ると
        // 「捕手も遊撃もこなせる1人」を先に取られた枠が総崩れするので、8枠を同時に解く。
        // 自校（RosterTeamBuilder）は同じ問題を 8! 全探索で解いており、本ソルバは同じ最適解を返す。
        var assignment = AssignFieldPositions(fielders, deps.Fielding);
        for (var slot = 0; slot < FieldSlots.Length; slot++)
        {
            var idx = assignment[slot];
            if (idx < 0) continue;   // 候補が8人未満（保険。コホート構成上は起きない）
            var best = fielders[idx];
            usedFielders.Add(best.Id);
            order.Add(Snap(best, PositionNumber(FieldSlots[slot])) with { Position = FieldSlots[slot] });
        }

        // 控え野手（各守備位置の次点＝守備固め供給）＋余り。背番号10〜17（ベンチ入り20人制、StrengthTeamFactoryと同じ規則）。
        var benchFielders = fielders.Where(p => !usedFielders.Contains(p.Id))
            .OrderByDescending(FielderScore).Take(BenchFielderSlots).ToList();
        var num = 10;
        foreach (var f in benchFielders)
            bench.Add(Snap(f, num++));

        // エース（最良投手）＝背番号は常に AceUniformNumber（1）。控え投手は背番号18〜20（最大3人）。
        // 番号は温存の有無に関わらずロスター順位で固定＝大会を跨いだ台帳キー（#41）の同定を安定させる。
        //
        // **守備位置は必ずここで投手へ置き直す**（issue #384）。マウンド組は生成時の役割でなく能力で選ぶので、
        // 野手として生成された選手が入り得る。置き直さないと打順に投手が居ないまま同じ守備位置が2人になり、
        // 守備網羅が崩れる（実測: 200校中30校で発生）。配置を決めるのは選抜側の責任（#366 §④）。
        Player SnapToMound(AiPlayer p, int number) => Snap(p, number) with { Position = FieldPosition.Pitcher };

        var trueAce = pitchers.FirstOrDefault();
        var snappedAce = trueAce is not null ? SnapToMound(trueAce, AceRestSelector.AceUniformNumber) : null;
        var pnum = 18;
        var relief = pitchers.Skip(1).Take(ReservePitcherSlots).Select(p => SnapToMound(p, pnum++)).ToList();

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
            Reserves = Match.Game.TeamReserves.Merge(bullpen, bench),
        };
        return LineupOrderer.Compose(team, deps.Roster.Lineup, modernRules, calendarYear);
    }

    /// <summary>
    /// マウンド組を選抜する（issue #384）。人数は「投手適性が最良守備適性を上回る者」の数＝
    /// 豊作の年も苦作の年も出る。判定式は自校と共通の <see cref="PitcherRoleResolver.IsNaturalPitcher"/>。
    ///
    /// 下限・上限の2つを選抜側で保証する（旧実装はコホート編成が構造的に担保していた）:
    /// - **下限** <see cref="PersistentRosterCoefficients.PitcherCountFloor"/>: 誰も投手に寄らない年でも
    ///   投手能力の最良者をマウンドへ立てる＝投手0人化を防ぐ
    /// - **上限** 残りが守備8枠を割り当てられる人数を下回らないこと＝守備網羅を防衛する
    ///   （投手寄りばかりの年に守備が組めなくなるのを防ぐ）
    ///
    /// 並びは投手能力の降順＝先頭がエース格。乱数を使わない純関数（決定論）。
    /// </summary>
    private static (List<AiPlayer> Mound, List<AiPlayer> Fielders) SelectMoundGroup(
        IReadOnlyList<AiPlayer> active, RosterCoefficients rc, PersistentRosterCoefficients pc)
    {
        // 投手能力の降順。同点は Id 昇順で決定論に倒す。
        var ranked = active
            .OrderByDescending(PitcherScore).ThenBy(p => p.Id)
            .ToList();

        var leaning = ranked.Count(p => PitcherRoleResolver.IsNaturalPitcher(
            p.Aptitude(FieldPosition.Pitcher), Season.AptitudeRoller.BestField(p.Aptitudes), rc));

        var maxMound = System.Math.Max(0, active.Count - FieldSlots.Length);
        var count = System.Math.Min(System.Math.Max(leaning, pc.PitcherCountFloor), maxMound);

        var mound = ranked.Take(count).ToList();
        var moundIds = new HashSet<int>(mound.Select(p => p.Id));
        var fielders = active.Where(p => !moundIds.Contains(p.Id)).ToList();
        return (mound, fielders);
    }

    /// <summary>
    /// 守備8枠への割当（issue #384）。重みは実効守備力＝地力(Fielding)×適性補正で、自校の
    /// <see cref="Season.EffectiveFielding"/> と同じ式を通す。戻り値は枠→候補の添字（候補不足なら −1）。
    /// </summary>
    private static int[] AssignFieldPositions(
        IReadOnlyList<AiPlayer> fielders, Match.Fielding.FieldingCoefficients coeff)
    {
        var empty = new int[FieldSlots.Length];
        for (var i = 0; i < empty.Length; i++) empty[i] = -1;
        if (fielders.Count < FieldSlots.Length) return empty;   // 保険（SelectMoundGroup の上限が防いでいる）

        var w = new double[FieldSlots.Length, fielders.Count];
        for (var slot = 0; slot < FieldSlots.Length; slot++)
            for (var i = 0; i < fielders.Count; i++)
                w[slot, i] = Season.EffectiveFielding.Raw(
                    fielders[i].Snapshot.Fielding, fielders[i].Aptitude(FieldSlots[slot]), coeff);

        return Core.AssignmentSolver.MaxWeight(w);
    }

    /// <summary>投手の起用序列スコア（球速レベル＋制球＋キレ＋スタミナレベル）。</summary>
    private static double PitcherScore(AiPlayer p)
    {
        var a = p.Snapshot.Pitching;
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
