using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Nation.Roster;

/// <summary>
/// 年3節目の成長ノード（夏大会後=代替わり／秋大会後／冬明け。OPEN-QUESTIONS Q19 §1決定事項2）。
/// <see cref="AiRosterCycle.ApplyGrowthNode"/> が節目ごとに <see cref="AiPlayer.PendingBudget"/> を消費する。
/// </summary>
public enum AiGrowthNode
{
    /// <summary>夏の大会後（新チーム発足週＝<see cref="Season.CaptainSelector.IsNewTeamStartWeek"/>）。</summary>
    Summer,
    /// <summary>秋の大会後（中学生リストが出る週＝<see cref="Season.SeasonCalendar.RecruitListWeek"/>）。</summary>
    Autumn,
    /// <summary>冬明け（冬合宿の直後＝<see cref="Season.SeasonCalendar.WinterCampWeek"/>+1）。残り全額を使い切る。</summary>
    Winter,
}

/// <summary>
/// AI校の永続ロスターのライフサイクル駆動（設計書 OPEN-QUESTIONS Q19/Q20 / #80/#82）。
/// 代替わり（4月＝進級・新入生加入・3年引退）と節目成長（年間予算を逆算配分）を扱う。
///
/// 逆算配分（Q19 §1）: 在校生の成長量を「翌夏のチーム総合が学校Strengthに一致する」よう決定論で解く。
/// 選手から見れば自然な成長カーブ、学校から見れば従来どおりの生態系（AiSchoolModel.Evolve と整合）。
/// 怪物（Phenom）は逆算配分の対象外＝上乗せ例外（Q20 §1）。総合力の測定から怪物を除外して解くことで、
/// 怪物在籍校の実チーム総合は Strength を上回る（＝無名校の旋風の創発）。
///
/// **節目成長（Q19 §1決定事項2・issue #843/Phase C）**: 年間成長予算は4月に即時適用せず、
/// <see cref="BeginYear"/> が <see cref="AiPlayer.PendingBudget"/> に積んで、年3節目
/// （<see cref="AiGrowthNode"/>）で <see cref="ApplyGrowthNode"/> が <c>*_node_share</c> の割合ずつ消費する。
/// <see cref="AdvanceOneYear"/> は<b>テストの参照実装（原子操作）</b>として残す＝<see cref="BeginYear"/> +
/// 3節目を即座に通した場合と最終結果（チーム総合）が一致する
/// （丸めの蓄積差は <see cref="PersistentRosterCoefficients.TargetTolerance"/> 内）。
/// </summary>
public static class AiRosterCycle
{
    /// <summary>
    /// 夏大会後の代替わり: 3年生を引退させ、ロスターから除去する（秋の新チームは2学年＝下級生のみ）。
    /// カレンダーの夏引退週に Shell が呼ぶ。冪等（既に除去済みなら何もしない）。
    /// </summary>
    public static void RetireGraduates(AiSchoolRoster roster) => roster.RemoveWhere(p => p.Grade >= 3);

    /// <summary>ブートストラップ直後、上級生を過年度ぶん成長させてチーム総合を <see cref="GrowthTarget"/> 近傍へ寄せる。</summary>
    public static void SettleToTarget(AiSchoolRoster roster, School school, int yearIndex, AiRosterDeps deps)
        // 全員が新入生中心で生成されているので、grade-1 学年ぶん (grade-1)×g を積んで加齢差を作る。
        => Settle(roster, school, yearIndex, deps, gradeFactor: g => g - 1);

    /// <summary>非怪物の在校生の成長係数（2年＝満額・3年＝引退間際で控えめ・新入生＝当年成長なし）。</summary>
    private static Func<int, double> NonPhenomGradeFactor(PersistentRosterCoefficients pc) => g => g switch
    {
        2 => 1.0,
        3 => pc.SeniorGrowthFactor,
        _ => 0.0,
    };

    /// <summary>
    /// 代替わり（3年生除去→進級→新入生加入）。<see cref="AdvanceOneYear"/>・<see cref="BeginYear"/> 共通の前段。
    /// </summary>
    private static void Promote(AiSchoolRoster roster, School school, int newYearIndex, AiRosterDeps deps)
    {
        // 3年生は前年夏で引退済み＝ロスターから除去。
        roster.RemoveWhere(p => p.Grade >= 3);

        // 残りは進級（1→2, 2→3）。Snapshot の表示学年も同期。
        foreach (var p in roster.Players)
        {
            p.Grade++;
            // 精神力の学年アーク（issue ai-school-mental-fixed-50）: 進級ごとに +AiMentalPerGrade。能力の逆算配分とは別。
            p.Snapshot = AiMentalModel.Promote(p.Snapshot with { Grade = p.Grade }, deps.Roster);
        }

        // 新入生コホート（Grade=1）を加入。
        var nextId = roster.NextPlayerId;
        var cohort = AiRosterFactory.NewcomerCohort(school, newYearIndex, ref nextId, deps);
        foreach (var ai in cohort) roster.Add(ai);
        roster.NextPlayerId = nextId;
    }

    /// <summary>
    /// 1年分の代替わり＋節目成長を適用する（4月の進級・新入生加入・3年引退 → 逆算配分）。
    /// テストの参照実装（原子操作）。Unity側は同じ結果を年3節目に分割して届ける
    /// （<see cref="BeginYear"/> + <see cref="ApplyGrowthNode"/>×3・Phase C）。
    /// </summary>
    public static void AdvanceOneYear(AiSchoolRoster roster, School school, int newYearIndex, AiRosterDeps deps)
    {
        Promote(roster, school, newYearIndex, deps);

        // 怪物（返り＝進級した2・3年）は逆算配分の対象外だが節目成長には乗る（Q20 §1）＝固定の小成長。
        var pc = deps.Persistent;
        foreach (var p in roster.Players.Where(p => p.Phenom.IsPhenom() && p.Grade >= 2))
        {
            var w = p.Grade == 3 ? pc.SeniorGrowthFactor : 1.0;
            p.Snapshot = BumpTo(p.Snapshot, pc.AnnualGrowth * 0.5 * w);
        }

        // 非怪物の在校生を逆算配分で成長。
        Settle(roster, school, newYearIndex, deps, NonPhenomGradeFactor(pc));
    }

    /// <summary>
    /// 年度替わり（4月）: 代替わりは即座に適用するが、年間成長予算は <see cref="AiPlayer.PendingBudget"/> に
    /// 積むだけで即時適用しない（Unity側の入口・Phase C）。節目（<see cref="ApplyGrowthNode"/>）で消費する。
    /// </summary>
    public static void BeginYear(AiSchoolRoster roster, School school, int newYearIndex, AiRosterDeps deps)
    {
        Promote(roster, school, newYearIndex, deps);

        // 怪物の固定成長も節目成長に乗せる（Q20 §1）＝即時に Snapshot を伸ばさず予算として積む。
        var pc = deps.Persistent;
        foreach (var p in roster.Players.Where(p => p.Phenom.IsPhenom() && p.Grade >= 2))
        {
            var w = p.Grade == 3 ? pc.SeniorGrowthFactor : 1.0;
            p.PendingBudget = pc.AnnualGrowth * 0.5 * w;
        }

        // 非怪物の在校生の逆算配分も同様に予算として積む（Snapshot はまだ伸ばさない）。
        SolveYearBudgets(roster, school, newYearIndex, deps, NonPhenomGradeFactor(pc));
    }

    /// <summary>
    /// 年3節目（<see cref="AiGrowthNode"/>）の1つを消費する。<see cref="AiPlayer.PendingBudget"/> が0（怪物の
    /// 固定成長も含め既に消費済み・旧セーブ・成長対象外）の選手は何もしない＝冪等・年度途中のロード安全
    /// （issue #843完了条件）。
    ///
    /// 夏・秋は <c>*_node_share</c> の割合（残り節目ぶんで正規化）を <see cref="PersistentRosterCoefficients.MaxResidualPerNode"/>
    /// で頭打ちし、一気に寄せず複数節目でならす。消費する予算は<b>実際に能力へ乗った整数ぶんだけ</b>
    /// （<see cref="BumpTo"/> の丸めで0になった節目は予算を残したまま次へ回す）＝小さい残差が
    /// 複数節目の丸めで消えて原子操作より総量が減る事故を防ぐ。冬明けは<b>残り全額を使い切り、
    /// 丸めで消える端数ごと予算を0へ戻す</b>＝<see cref="AdvanceOneYear"/>（原子操作）と最終結果が一致する。
    /// </summary>
    public static void ApplyGrowthNode(AiSchoolRoster roster, AiRosterDeps deps, AiGrowthNode node)
    {
        var pc = deps.Persistent;
        foreach (var p in roster.Players)
        {
            if (p.PendingBudget <= 0) continue;

            if (node == AiGrowthNode.Winter)
            {
                // 残り全額（頭打ちで溜まった残差・丸めで消える端数も含む）を使い切って必ず0へ。
                p.Snapshot = BumpTo(p.Snapshot, p.PendingBudget);
                p.PendingBudget = 0;
                continue;
            }

            double ratio;
            if (node == AiGrowthNode.Summer)
            {
                var totalShare = pc.SummerNodeShare + pc.AutumnNodeShare + pc.WinterNodeShare;
                ratio = totalShare > 0 ? pc.SummerNodeShare / totalShare : 0.0;
            }
            else
            {
                var remainingShare = pc.AutumnNodeShare + pc.WinterNodeShare;
                ratio = remainingShare > 0 ? pc.AutumnNodeShare / remainingShare : 0.0;
            }

            var requested = Math.Min(p.PendingBudget * ratio, pc.MaxResidualPerNode);
            var delta = (int)Math.Round(requested);
            if (delta <= 0) continue;   // 丸めで0なら今節目では何もせず、予算はそのまま次節目へ残す。

            p.Snapshot = BumpTo(p.Snapshot, delta);
            p.PendingBudget -= delta;
        }
    }

    /// <summary>
    /// 逆算配分のターゲット＝<b>ティア圧縮後</b>の強さ（<see cref="RosterCoefficients.CompressStrength"/>）。
    /// 旧実装は raw の <c>school.Strength</c> を目標にしていたので、入学時の中心（<see cref="AiRosterFactory.RecruitCenter"/>）
    /// だけ圧縮しても在校生の成長で raw まで戻され、<b>実ゲームのAI校は圧縮前と同じ能力</b>だった
    /// （実測 2026-08-18: 強度80校の先発平均Lv 86・総合79／強度50校 Lv 54・総合49＝#578 の低ティア緩和も実ゲームには
    /// 届いていなかった。issue #402 と同型の「sim だけ直る」穴）。sim（<see cref="StrengthTeamFactory.Create"/>）・
    /// 集計モデル（<see cref="AggregateMatch"/>）・実ゲーム（ここ）の3経路が同じ写像を通る。
    /// </summary>
    internal static double GrowthTarget(School school, AiRosterDeps deps)
        => deps.Roster.CompressStrength(school.Strength);

    /// <summary>
    /// 逆算配分の一般形。非怪物の在校生を <paramref name="gradeFactor"/>×g（g は二分探索で決める強度）だけ
    /// 現スナップショットから成長させ、怪物を除いたチーム総合が <see cref="GrowthTarget"/> に一致する g を求める。
    /// 即時適用（<see cref="AdvanceOneYear"/> の参照実装）。
    /// </summary>
    private static void Settle(AiSchoolRoster roster, School school, int yearIndex, AiRosterDeps deps,
        Func<int, double> gradeFactor)
    {
        var growable = GrowableNonPhenomPlayers(roster, gradeFactor);
        if (growable.Count == 0) return;
        var bases = growable.Select(p => (Player: p, Base: p.Snapshot)).ToList();
        SolveGrowthG(roster, school, yearIndex, deps, bases, gradeFactor);   // 成長を適用したまま返す。
    }

    /// <summary>
    /// <see cref="Settle"/> と同じ二分探索で g を解くが、<b>Snapshot は成長前に戻し</b>、決まった
    /// 成長量（<c>gradeFactor(grade)×g</c>）を <see cref="AiPlayer.PendingBudget"/> に積むだけで止める
    /// （節目成長・<see cref="BeginYear"/> の入口）。
    /// </summary>
    private static void SolveYearBudgets(AiSchoolRoster roster, School school, int yearIndex, AiRosterDeps deps,
        Func<int, double> gradeFactor)
    {
        var growable = GrowableNonPhenomPlayers(roster, gradeFactor);
        if (growable.Count == 0) return;
        var bases = growable.Select(p => (Player: p, Base: p.Snapshot)).ToList();
        var g = SolveGrowthG(roster, school, yearIndex, deps, bases, gradeFactor);
        foreach (var (p, b) in bases)
        {
            p.Snapshot = b;   // 成長前へ戻す＝節目まで見た目は変わらない。
            p.PendingBudget = gradeFactor(p.Grade) * g;
        }
    }

    private static List<AiPlayer> GrowableNonPhenomPlayers(AiSchoolRoster roster, Func<int, double> gradeFactor)
        => roster.Players
            .Where(p => !p.Phenom.IsPhenom() && p.Grade is >= 1 and <= 3 && gradeFactor(p.Grade) > 0)
            .ToList();

    /// <summary>
    /// 二分探索の本体。<paramref name="bases"/>（成長前スナップショット）を <paramref name="gradeFactor"/>×g で
    /// 伸ばしたときのチーム総合が <see cref="GrowthTarget"/> に一致する g を返す。終了時、各選手の
    /// <c>Snapshot</c> は返り値の g で伸ばした状態になっている（呼び出し側が適用／予算化のどちらにも使える）。
    /// </summary>
    private static double SolveGrowthG(AiSchoolRoster roster, School school, int yearIndex, AiRosterDeps deps,
        List<(AiPlayer Player, Player Base)> bases, Func<int, double> gradeFactor)
    {
        var target = GrowthTarget(school, deps);

        void ApplyG(double g)
        {
            foreach (var (p, b) in bases) p.Snapshot = BumpTo(b, gradeFactor(p.Grade) * g);
        }

        // Overall は g に対し単調非減少。g の最大値でも届かなければ上限で打ち切り（強豪はキャップで頭打ち＝許容）。
        double lo = 0.0, hi = 40.0;
        ApplyG(hi);
        if (ProjectedOverall(roster, school, yearIndex, deps) < target) return hi;   // hi 適用済みで確定

        for (var i = 0; i < 26; i++)
        {
            var mid = (lo + hi) / 2;
            ApplyG(mid);
            if (ProjectedOverall(roster, school, yearIndex, deps) < target) lo = mid;
            else hi = mid;
        }
        ApplyG(lo);   // target をわずかに下回る側（オーバーシュートしない）。
        return lo;
    }

    /// <summary>怪物を除いた在校生でベンチ20 Team を組み、6指標の Overall を返す（逆算配分のターゲット尺度）。</summary>
    private static double ProjectedOverall(AiSchoolRoster roster, School school, int yearIndex, AiRosterDeps deps)
    {
        var active = roster.Players.Where(p => p.Grade is >= 1 and <= 3 && !p.Phenom.IsPhenom()).ToList();
        if (active.Count == 0) return GrowthTarget(school, deps);
        var team = AiTeamBuilder.BuildFrom(active, school, yearIndex, deps);
        return ScoutedTeamProfile.Compute(team, deps.TeamStrength).Overall;
    }

    /// <summary>スナップショットの中核能力を <paramref name="amount"/> だけ底上げする（弾道傾向・精神は伸ばさない）。</summary>
    internal static Player BumpTo(Player b, double amount)
    {
        var d = (int)Math.Round(amount);
        if (d <= 0) return b;
        int Up(int v) => (int)MathUtil.Clamp(v + d, 10, 99);
        var r = b with
        {
            Contact = Up(b.Contact),
            Power = Up(b.Power),
            Discipline = Up(b.Discipline),
            Speed = Up(b.Speed),
            ArmStrength = Up(b.ArmStrength),
            ThrowAccuracy = Up(b.ThrowAccuracy),
            Fielding = Up(b.Fielding),
            Catching = Up(b.Catching),
            Bunt = Up(b.Bunt),
            Steal = Up(b.Steal),
            Baserunning = Up(b.Baserunning),
            Lead = Up(b.Lead),
        };
        // 投手能力も全選手ぶん伸ばす（#381）。旧実装は「投手能力を持つなら」＝マウンドに置かれた選手だけで、
        // 野手の投手能力は入学時のまま据え置かれていた（3年生が1年生と同じ肩で緊急登板する）。
        // 6指標（<see cref="ScoutedTeamProfile"/>）は投手として置かれた選手しか投手力に数えないので、
        // 逆算配分（<see cref="SettleToTarget"/>）のターゲット尺度は動かない＝校の総合力は不変。
        var a = b.Pitching;
        var vl = Up(PitcherAttributes.LevelFromVelocityKmh(a.MaxVelocityKmh));
        var sl = Up(PitcherAttributes.LevelFromStaminaPitches(a.StaminaPitches));
        return r with
        {
            Pitching = a with
            {
                MaxVelocityKmh = PitcherAttributes.VelocityKmhFromLevel(vl),
                Control = Up(a.Control),
                PitchRank = Up(a.PitchRank),
                StaminaPitches = PitcherAttributes.StaminaPitchesFromLevel(sl),
            },
        };
    }
}
