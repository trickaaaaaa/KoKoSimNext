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
/// 夏の節目で翌夏に向けた年間予算を <see cref="AiPlayer.PendingBudget"/> に積み（<see cref="AiRosterCycle.PlanNextYear"/>）、
/// <see cref="AiRosterCycle.ApplyGrowthNode"/> が節目ごとに消費する（issue #1058）。
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
/// <para><b>節目成長は翌夏に向けて育てる（issue #843 / #1058）</b>: 年間成長予算は<b>夏の節目</b>（3年引退の後）で
/// <see cref="PlanNextYear"/> が「残った1・2年＋翌年度の新入生（先読み）で組んだ翌夏のチームがターゲットに届く」よう
/// 逆算して <see cref="AiPlayer.PendingBudget"/> に<b>上書き</b>で積み、夏・秋・冬の3節目（<see cref="AiGrowthNode"/>）で
/// <see cref="ApplyGrowthNode"/> が <c>*_node_share</c> の割合ずつ消費する。4月（<see cref="BeginYear"/>）は代替わりの後、
/// ずれた分（学校の進化・新入生の先読みとの差・丸め）だけを即時に逆算配分で補正する＝夏の大会のチームがターゲットに一致する。
/// 旧実装（#843）は4月にその年度の予算を積んで夏の大会<b>後</b>から消費していたので、夏の大会の時点で全学年が
/// 1年分遅れていた（2年が1年と同じ水準・注目選手が1年ばかりになる）。</para>
///
/// <para><see cref="AdvanceOneYear"/> は<b>テストの参照実装（原子操作）</b>＝4月に1年分を即時適用する。暦どおりの経路
/// （<see cref="BeginYear"/> → 夏の節目 → 秋 → 冬）と夏の大会時点の学年の水準・チーム総合が一致する
/// （丸めの差は <see cref="PersistentRosterCoefficients.TargetTolerance"/> 内）。</para>
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
    /// 1年分の代替わり＋成長を4月に即時適用する（進級・新入生加入・3年引退 → 逆算配分）。
    /// テストの参照実装（原子操作）。Unity側は同じ夏の水準を「前年の夏の節目で翌夏に向けた予算を積み
    /// （<see cref="ApplySummerNode"/>）、秋・冬の節目で消費し、4月（<see cref="BeginYear"/>）でずれを補正する」形で届ける
    /// （issue #843 / #1058）。
    /// </summary>
    public static void AdvanceOneYear(AiSchoolRoster roster, School school, int newYearIndex, AiRosterDeps deps)
    {
        Promote(roster, school, newYearIndex, deps);

        // 怪物（返り＝進級した2・3年）は逆算配分の対象外だが節目成長には乗る（Q20 §1）＝固定の小成長。
        var pc = deps.Persistent;
        foreach (var p in roster.Players.Where(p => p.Phenom.IsPhenom() && p.Grade >= 2))
        {
            var w = p.Grade == 3 ? pc.SeniorGrowthFactor : 1.0;
            p.Snapshot = BumpTo(p.Snapshot, pc.AnnualGrowth * 0.5 * w, deps.Roster);
        }

        // 非怪物の在校生を逆算配分で成長。
        Settle(roster, school, newYearIndex, deps, NonPhenomGradeFactor(pc));
    }

    /// <summary>
    /// 年度替わり（4月・Unity側の入口）: 代替わり（3年除去・進級・新入生加入）を適用し、その場で<b>ずれた分だけ</b>
    /// 逆算配分で補正する（<see cref="Settle"/> と同じく即時）。年間の成長そのものは前年の夏の節目
    /// （<see cref="PlanNextYear"/>）で積んだ予算を夏・秋・冬で消費済み＝ここで足すのは、学校の進化による
    /// ターゲットの変化・先読みした新入生と実際の新入生の差（進化で入学中心が動く）・節目の丸めの残りだけ。
    /// 旧セーブ・夏の節目より後に生成されたロスターのように予算が無かった学校は、ここで1年分が一度に乗る。
    ///
    /// 補正は伸ばす向きだけ（g≥0）。ターゲットを上回っている学校（進化で弱くなった等）の能力は下げない。
    /// 怪物の固定成長は夏の節目で予算に積む（Q20 §1）＝ここでは足さない。
    /// </summary>
    public static void BeginYear(AiSchoolRoster roster, School school, int newYearIndex, AiRosterDeps deps)
    {
        // 前年度の予算の消し残し（冬の節目を通らなかった等）は代替わりの前に使い切る＝予算を年度をまたいで持ち越さない。
        foreach (var p in roster.Players)
        {
            if (p.PendingBudget <= 0 && p.PendingPitchBudget <= 0) continue;
            p.Snapshot = BumpTo(p.Snapshot, p.PendingBudget, deps.Roster, pitchAmount: p.PendingPitchBudget);
            p.PendingBudget = 0;
            p.PendingPitchBudget = 0;
        }

        Promote(roster, school, newYearIndex, deps);

        // ずれた分を即時に補正（非怪物の2・3年。新入生は当年成長なし）。
        Settle(roster, school, newYearIndex, deps, NonPhenomGradeFactor(deps.Persistent), correction: true);
    }

    /// <summary>
    /// 夏の節目（3年引退の後）: 翌年度（<paramref name="yearIndex"/>+1）の夏に向けた年間成長予算を逆算して
    /// <see cref="AiPlayer.PendingBudget"/> に<b>上書き</b>で積む（issue #1058）。
    /// <list type="bullet">
    /// <item>対象はいまの1・2年（＝翌年度の2・3年）。3年が残っていても翌年度には居ないので予算0。</item>
    /// <item>学年係数は<b>翌年度の学年</b>で引く（いまの1年→2年用の 1.0、いまの2年→3年用の <see cref="PersistentRosterCoefficients.SeniorGrowthFactor"/>）。</item>
    /// <item>翌夏のチームは「いまの1・2年を進級させた写し（精神力の学年アークも載せる）＋翌年度の新入生」で組む。新入生は
    ///   (校ID, 年度) の決定論シード（<see cref="AiRosterFactory.NewcomerCohort"/>）で先読みし、名簿には足さない
    ///   （採番も写しで行う＝<see cref="AiSchoolRoster.NextPlayerId"/> を進めない）。</item>
    /// <item>怪物は逆算配分の対象外（Q20 §1）＝翌年度の学年に応じた固定の小成長を積む。</item>
    /// </list>
    /// 予算は積み直し（上書き）。旧セーブ（4月方式の予算が残っている）は、消し残しを先に能力へ乗せてから積み直す
    /// ＝二重に乗らず、その年の夏の節目から新方式へ移る。
    /// 乱数を使わない逆算（新入生の先読みは専用シード＝主乱数を進めない）。
    /// </summary>
    public static void PlanNextYear(AiSchoolRoster roster, School school, int yearIndex, AiRosterDeps deps)
    {
        var pc = deps.Persistent;
        var nextYear = yearIndex + 1;

        // 旧方式（#843: 4月にその年度の予算を積み、夏の大会後から消費）の消し残しは、その場で能力へ乗せてから積み直す。
        // 消し残しは「本来この夏までに入っているはずだった成長」なので、捨てると旧セーブのいまの2年が1年分遅れたまま
        // 翌年度の3年になり、翌夏だけ 3年＜2年 に逆転する。積み直しは乗せた後の能力から逆算するので二重にはならない。
        // 新方式では夏の節目の時点の予算は常に0（冬明け・4月で使い切る）＝ここは旧セーブの移行でしか動かない。
        foreach (var p in roster.Players)
        {
            if (p.PendingBudget <= 0 && p.PendingPitchBudget <= 0) continue;
            p.Snapshot = BumpTo(p.Snapshot, p.PendingBudget, deps.Roster, pitchAmount: p.PendingPitchBudget);
            p.PendingBudget = 0;
            p.PendingPitchBudget = 0;
        }

        // 翌年度の学年に進めた写し（実体の Snapshot・学年は触らない）。
        var pairs = new List<(AiPlayer Real, AiPlayer Next)>();
        var projected = new List<AiPlayer>();
        foreach (var p in roster.Players)
        {
            if (p.Grade is not (1 or 2)) continue;   // 3年は翌年度に居ない＝予算0
            var g = p.Grade + 1;
            var snap = AiMentalModel.Promote(p.Snapshot with { Grade = g }, deps.Roster);
            var next = new AiPlayer(p.Id, p.EnrollmentYearIndex, p.Aptitudes.ToArray(), p.Phenom, g, snap);
            pairs.Add((p, next));
            projected.Add(next);
        }

        // 翌年度の新入生の先読み（本採用と同じ決定論シード・名簿には足さない）。
        var nextId = roster.NextPlayerId;
        projected.AddRange(AiRosterFactory.NewcomerCohort(school, nextYear, ref nextId, deps));
        var future = new AiSchoolRoster(roster.SchoolId, projected, nextId);

        // 怪物の固定成長（Q20 §1）。翌年度の学年で重みを引く。
        foreach (var (real, next) in pairs.Where(x => x.Real.Phenom.IsPhenom()))
        {
            real.PendingBudget = pc.AnnualGrowth * 0.5 * (next.Grade == 3 ? pc.SeniorGrowthFactor : 1.0);
            real.PendingPitchBudget = real.PendingBudget;
        }

        // 非怪物は翌夏のチームでターゲットを逆算（学年係数は翌年度の学年）。
        var gradeFactor = NonPhenomGradeFactor(pc);
        var growable = GrowableNonPhenomPlayers(future, gradeFactor);
        if (growable.Count == 0) return;
        var bases = growable.Select(p => (Player: p, Base: p.Snapshot)).ToList();
        var gSolved = SolveGrowthG(future, school, nextYear, deps, bases, gradeFactor);
        foreach (var (real, next) in pairs)
        {
            if (real.Phenom.IsPhenom()) continue;
            real.PendingBudget = gradeFactor(next.Grade) * gSolved.Bat;
            real.PendingPitchBudget = gradeFactor(next.Grade) * gSolved.Pitch;
        }
    }

    /// <summary>
    /// 夏の節目の一式（<see cref="PlanNextYear"/> で翌夏に向けた予算を積み直し、夏の割合ぶんを消費する）。
    /// 3年引退（<see cref="RetireGraduates"/>）の後に呼ぶ。
    /// </summary>
    public static void ApplySummerNode(AiSchoolRoster roster, School school, int yearIndex, AiRosterDeps deps)
    {
        PlanNextYear(roster, school, yearIndex, deps);
        ApplyGrowthNode(roster, deps, AiGrowthNode.Summer);
    }

    /// <summary>
    /// 年3節目（<see cref="AiGrowthNode"/>）の1つを消費する。<see cref="AiPlayer.PendingBudget"/> が0（怪物の
    /// 固定成長も含め既に消費済み・旧セーブ・成長対象外）の選手は何もしない＝冪等・年度途中のロード安全
    /// （issue #843完了条件）。予算を積むのは夏の節目の <see cref="PlanNextYear"/>（ここは消費だけ。
    /// 夏の節目は <see cref="ApplySummerNode"/> で積み直しと消費をまとめて行う）。
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
            if (p.PendingBudget <= 0 && p.PendingPitchBudget <= 0) continue;

            if (node == AiGrowthNode.Winter)
            {
                // 残り全額（頭打ちで溜まった残差・丸めで消える端数も含む）を使い切って必ず0へ。
                p.Snapshot = BumpTo(p.Snapshot, p.PendingBudget, deps.Roster, pitchAmount: p.PendingPitchBudget);
                p.PendingBudget = 0;
                p.PendingPitchBudget = 0;
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

            // 打者能力と投手能力（issue #1066）は別々の予算から同じ割合で引く。
            var delta = p.PendingBudget > 0 ? (int)Math.Round(Math.Min(p.PendingBudget * ratio, pc.MaxResidualPerNode)) : 0;
            var pitchDelta = p.PendingPitchBudget > 0 ? (int)Math.Round(Math.Min(p.PendingPitchBudget * ratio, pc.MaxResidualPerNode)) : 0;
            if (delta <= 0 && pitchDelta <= 0) continue;   // 丸めで0なら今節目では何もせず、予算はそのまま次節目へ残す。

            p.Snapshot = BumpTo(p.Snapshot, Math.Max(0, delta), deps.Roster, pitchAmount: Math.Max(0, pitchDelta));
            if (delta > 0) p.PendingBudget -= delta;
            if (pitchDelta > 0) p.PendingPitchBudget -= pitchDelta;
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
        Func<int, double> gradeFactor, bool correction = false)
    {
        var growable = GrowableNonPhenomPlayers(roster, gradeFactor);
        if (growable.Count == 0) return;
        var bases = growable.Select(p => (Player: p, Base: p.Snapshot)).ToList();
        SolveGrowthG(roster, school, yearIndex, deps, bases, gradeFactor, correction);   // 成長を適用したまま返す。
    }

    private static List<AiPlayer> GrowableNonPhenomPlayers(AiSchoolRoster roster, Func<int, double> gradeFactor)
        => roster.Players
            .Where(p => !p.Phenom.IsPhenom() && p.Grade is >= 1 and <= 3 && gradeFactor(p.Grade) > 0)
            .ToList();

    /// <summary>
    /// 二分探索の本体。<paramref name="bases"/>（成長前スナップショット）を <paramref name="gradeFactor"/>×g で
    /// 伸ばしたときのチーム総合が <see cref="GrowthTarget"/> に一致する g を返す。終了時、各選手の
    /// <c>Snapshot</c> は返り値の g で伸ばした状態になっている（呼び出し側が適用／予算化のどちらにも使える）。
    ///
    /// <para><paramref name="correction"/>＝4月の補正（<see cref="BeginYear"/>）。補正は<b>総合を下げない</b>ことを優先する:
    /// <list type="bullet">
    /// <item>伸ばさずにターゲットに届いていれば何もしない。</item>
    /// <item>二分探索の結果が伸ばさない場合より低ければ伸ばさない。</item>
    /// <item>上限まで伸ばしても届かない学校（最強豪）は、g を 5 刻みで当てて総合が最も高い最小の g を採る。</item>
    /// </list>
    /// 総合は g に対し単調でない。最強豪（ターゲット 90 前後）は能力が 99 に張り付いてターゲットに届かず、年1回の逆算
    /// （参照実装・夏の節目の積み直し）は毎年上限まで伸ばす。4月の補正でもう一度上限を当てる（または二分探索が谷に落ちる）と、
    /// 打てる控え投手が野手へ回って（二刀流の選抜）6指標の選手層が崩れ、総合がかえって下がる（実測: 強さ90の学校で 89.9 → 85.2）。</para>
    ///
    /// <para><b>打者の型（issue #1066）が入ってからは2段で解く</b>。
    /// <list type="number">
    /// <item>型を無視した一律の成長（<c>ignoreBatterArchetype:true</c>・打者も投手も同じ g）で g を解く＝型の導入前と同じ解。
    ///   <b>投手能力の成長量はこれで確定する</b>（Pitch）。</item>
    /// <item>投手能力をその量に固定したまま、打者能力だけ型ありの成長（長所は伸び短所は伸びない・やわらかい天井）で
    ///   チーム総合が目標に届く g を解き直す（Bat。上限 <c>GrowthSolveMaxG</c>）。</item>
    /// </list>
    /// 型ありの成長は天井で頭が寝るので、同じ g ではチーム総合が届かない（1段だけで解いた最初の実装は、強さ80で −2.9・
    /// 強さ85で −4.0 と強豪校が弱くなっていた）。かといって型ありの成長で1つの g を解くと、大きくなった g が
    /// 投手の球速にも同じ式で乗り、3年生の150km/h以上が 11人→34人に増えた（<c>VelocityDistributionByTierTests</c>）。
    /// 打者と投手の成長量を分けることで、学校の強さとチーム総合の対応と、投手の分布の両方を型の導入前と揃える。</para>
    /// </summary>
    private static (double Bat, double Pitch) SolveGrowthG(AiSchoolRoster roster, School school, int yearIndex, AiRosterDeps deps,
        List<(AiPlayer Player, Player Base)> bases, Func<int, double> gradeFactor, bool correction = false)
    {
        var target = GrowthTarget(school, deps);
        double Overall() => ProjectedOverall(roster, school, yearIndex, deps);

        // 1段目: 型を無視した一律の成長（打者・投手とも同じ g）＝型の導入前と同じ解。投手能力の成長量はこれで決まる。
        // issue #1080: 1段目は目標を PitcherGrowthTargetRestore だけ戻して解く＝投手の伸びを物差しを下げる前に揃える
        // （下げ幅は2段目＝打者だけで吸収）。下げた目標のままだと3年生の150km/h以上が年1〜4人（帯7〜18人）に減り、
        // 下げ幅を全部戻すと入学時の投手の +StrengthTargetDrop と二重になって19人・145km/h以上 1154人（main 895人）に増えた。
        var gPitch = SolveG(g =>
        {
            foreach (var (p, b) in bases)
                p.Snapshot = BumpTo(b, gradeFactor(p.Grade) * g, deps.Roster, ignoreBatterArchetype: true);
        }, Overall, target + deps.Roster.PitcherGrowthTargetRestore, correction);

        // 型を持つ選手がいなければ（自校由来・旧セーブ）1段目で確定（適用済み）。
        if (!bases.Any(x => x.Base.BatterArchetype is not null)) return (gPitch, gPitch);

        // 2段目: 投手能力は1段目の量に固定し、打者能力だけ型ありの成長でチーム総合が目標に届く量を解き直す。
        var gBat = SolveG(g =>
        {
            foreach (var (p, b) in bases)
            {
                var f = gradeFactor(p.Grade);
                p.Snapshot = BumpTo(b, f * g, deps.Roster, pitchAmount: f * gPitch);
            }
        }, Overall, target, correction, deps.Roster.BatterArchetypes.GrowthSolveMaxG);
        return (gBat, gPitch);
    }

    /// <summary>
    /// <see cref="SolveGrowthG"/> の二分探索の本体。<paramref name="apply"/>(g) で成長を当て、<paramref name="overall"/>
    /// でチーム総合を測る。終了時は返り値の g を当てた状態になっている。
    /// </summary>
    private static double SolveG(Action<double> apply, Func<double> overall, double target, bool correction, double maxG = 40.0)
    {
        double OverallAt(double g)
        {
            apply(g);
            return overall();
        }

        var overallAtZero = 0.0;
        if (correction)
        {
            overallAtZero = OverallAt(0);
            if (overallAtZero >= target) return 0;   // 伸ばさずに届いている（g=0 適用済み）
        }

        // Overall は g に対しおおむね単調非減少。g の最大値でも届かなければ上限で打ち切り（強豪はキャップで頭打ち＝許容）。
        double lo = 0.0, hi = maxG;
        var overallAtHi = OverallAt(hi);
        if (overallAtHi < target)
        {
            if (!correction) return hi;   // hi 適用済みで確定
            // 補正で届かない: 5 刻みで総合が最も高い最小の g（同点は小さい g＝余計に伸ばさない）。
            var bestG = 0.0;
            var bestOverall = overallAtZero;
            for (var gi = 5.0; gi < hi; gi += 5.0)
            {
                var o = OverallAt(gi);
                if (o > bestOverall) { bestOverall = o; bestG = gi; }
            }
            if (overallAtHi > bestOverall) bestG = hi;
            apply(bestG);
            return bestG;
        }

        for (var i = 0; i < 26; i++)
        {
            var mid = (lo + hi) / 2;
            if (OverallAt(mid) < target) lo = mid;
            else hi = mid;
        }
        if (correction && lo > 0 && OverallAt(lo) < overallAtZero)
        {
            apply(0);   // 谷に落ちた＝伸ばさないほうが高い。
            return 0;
        }
        apply(lo);   // target をわずかに下回る側（オーバーシュートしない）。
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

    /// <summary>
    /// スナップショットの中核能力を <paramref name="amount"/> だけ底上げする（弾道傾向・精神は伸ばさない）。
    /// 投手能力は <paramref name="pitchAmount"/>（省略時は <paramref name="amount"/> と同じ）だけ伸ばす（issue #1066）。
    /// <paramref name="ignoreBatterArchetype"/>=true は型を無視した旧来の一律成長（<c>Clamp(v+d,10,99)</c>）を使う
    /// （<see cref="SolveGrowthG"/> の1段目専用）。
    /// </summary>
    internal static Player BumpTo(Player b, double amount, RosterCoefficients rc, bool ignoreBatterArchetype = false,
        double? pitchAmount = null)
    {
        var d = Math.Max(0, (int)Math.Round(amount));
        var dp = Math.Max(0, (int)Math.Round(pitchAmount ?? amount));
        if (d <= 0 && dp <= 0) return b;
        // 打者の型（issue #1066）: 型を持つ選手（AI校の永続ロスターだけ・自校/旧セーブは null）は、
        // 長所ほど伸び短所はあまり伸びない配分＋やわらかい天井（RosterCoefficients.BatterSoftCap）で底上げする。
        // 旧実装（型なし・全員同じ Up）は全能力が平行に 99 へ溜まり、型の長所・短所が消えていた（issue #1066 原因1）。
        // 型を持たない選手・ignoreBatterArchetype=true の呼び出しは旧実装と1ビットも変えない（Up のまま）。
        var archetype = ignoreBatterArchetype ? null : b.BatterArchetype;
        int Up(int v, BatterAttribute attr)
        {
            if (archetype is not { } a) return (int)MathUtil.Clamp(v + d, 10, 99);
            var weight = BatterArchetypes.GrowthWeight(a, attr, b.BatterArchetypeFocus, b.BatterArchetypeIntensity, rc.BatterArchetypes);
            return rc.GrowBatterAttribute(v, d, weight);
        }
        var r = d <= 0 ? b : b with
        {
            Contact = Up(b.Contact, BatterAttribute.Contact),
            Power = Up(b.Power, BatterAttribute.Power),
            Discipline = Up(b.Discipline, BatterAttribute.Discipline),
            Speed = Up(b.Speed, BatterAttribute.Speed),
            ArmStrength = Up(b.ArmStrength, BatterAttribute.ArmStrength),
            ThrowAccuracy = Up(b.ThrowAccuracy, BatterAttribute.ThrowAccuracy),
            Fielding = Up(b.Fielding, BatterAttribute.Fielding),
            Catching = Up(b.Catching, BatterAttribute.Catching),
            Bunt = Up(b.Bunt, BatterAttribute.Bunt),
            Steal = Up(b.Steal, BatterAttribute.Steal),
            Baserunning = Up(b.Baserunning, BatterAttribute.Baserunning),
            Lead = Up(b.Lead, BatterAttribute.Lead),
        };
        // 投手能力も全選手ぶん伸ばす（#381）。旧実装は「投手能力を持つなら」＝マウンドに置かれた選手だけで、
        // 野手の投手能力は入学時のまま据え置かれていた（3年生が1年生と同じ肩で緊急登板する）。
        // 6指標（<see cref="ScoutedTeamProfile"/>）は投手として置かれた選手しか投手力に数えないので、
        // 逆算配分（<see cref="SettleToTarget"/>）のターゲット尺度は動かない＝校の総合力は不変。
        if (dp <= 0) return r;
        // 投手能力は pitchAmount（既定は amount と同じ量。issue #1066 で打者と分けた）で伸ばす。
        var a = b.Pitching;
        // 球速はやわらかい天井の手前（素の値）で底上げしてから天井を通す（issue #908）＝99 に張り付かない。
        // 膝より下は旧来の Up と同じ整数の足し算。
        var vRaw = rc.VelocitySoftCapInverse(PitcherAttributes.LevelFromVelocityKmhExact(a.MaxVelocityKmh));
        var vl = rc.VelocitySoftCap(Math.Max(10.0, vRaw + dp));
        // 制球・キレ・スタミナは型の上乗せを外した地力に足して 99 で切り詰め、上乗せを戻す（issue #908）。
        // 旧実装は上乗せ込みの値に足して切り詰めていたので、強豪校（毎年最大 +40）では型の個性が 99 に潰れ、
        // 剛腕の「制球 −22」も帳消しになっていた（実測: 全国の150km/h以上が全員 制球99）。
        // 剛腕の制球は割合（地力×0.55）なので、地力側に足して 99 で切ってから掛け直す＝溢れた分は制球に回らない
        // （オーナー決定「1＋2」）。型の当て方は PitcherArchetypes.Apply が単一ソース。
        var (_, _, os, or) = a.Archetype is { } type ? PitcherArchetypes.Offsets(type, rc.Archetypes) : (0.0, 0.0, 0.0, 0.0);
        int UpKeep(int v, double off) => (int)MathUtil.Clamp(Math.Round(MathUtil.Clamp(v - off + dp, 10, 99) + off), 10, 99);
        var baseControl = MathUtil.Clamp(PitcherArchetypes.BaseControl(a.Archetype, a.Control, rc.Archetypes) + dp, 10, 99);
        var (_, grownControl, _, _) = PitcherArchetypes.Apply(a.Archetype, 0, baseControl, 0, 0, rc.Archetypes);
        var sl = UpKeep(PitcherAttributes.LevelFromStaminaPitches(a.StaminaPitches), os);
        return r with
        {
            Pitching = a with
            {
                MaxVelocityKmh = PitcherAttributes.VelocityKmhFromLevel(vl),
                Control = (int)MathUtil.Clamp(Math.Round(grownControl), 10, 99),
                PitchRank = UpKeep(a.PitchRank, or),
                StaminaPitches = PitcherAttributes.StaminaPitchesFromLevel(sl),
            },
        };
    }
}
