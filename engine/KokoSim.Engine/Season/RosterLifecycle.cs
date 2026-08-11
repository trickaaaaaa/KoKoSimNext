using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Season;

/// <summary>新チーム発足（引退週フック）の結果。引退した3年と、その場で立てた暫定主将。</summary>
/// <param name="Retired">この処理で新たに引退扱いになった選手（元の順序のまま）。</param>
/// <param name="InterimCaptain">新チームの暫定主将（<see cref="CaptainSelector.SelectAuto"/> 選出）。新チームが空なら null。</param>
public sealed record NewTeamTransition(
    IReadOnlyList<DevelopingPlayer> Retired,
    DevelopingPlayer? InterimCaptain);

/// <summary>
/// 年度替わり（4月）の結果（<see cref="RosterLifecycle.AdvanceYear"/>）。卒業してロスターから外れた3年、
/// 新たに加入した新入生（1年）、次に空いている安定ID（採番した場合の続き番号）を返す。
/// </summary>
/// <param name="Graduated">卒業（Grade&gt;3）で除去された選手（元の順序のまま）。呼び出し側で記録用に退避してよい。</param>
/// <param name="Newcomers">この年度で加入した新入生（生成順）。</param>
/// <param name="NextId">次に割り当てるべき安定ID（採番していれば続き番号、未採番なら渡した値のまま）。</param>
public sealed record YearRollover(
    IReadOnlyList<DevelopingPlayer> Graduated,
    IReadOnlyList<DevelopingPlayer> Newcomers,
    int NextId);

/// <summary>
/// 部員の在籍ライフサイクル（設計書03 §2）。夏の第17週で3年生が引退するが、ロスターからは除去せず
/// <see cref="DevelopingPlayer.IsRetired"/> を立てて残す（記録は残しつつ、練習・試合・選手一覧から外す）。
/// 実際の除去は年度替わり（4月）の卒業でまとめて行う。
/// 純ロジック（乱数不要・決定論、不変条件#2）。
/// </summary>
public static class RosterLifecycle
{
    /// <summary>在籍中（＝引退していない）部員だけを抜き出す。順序は元ロスターのまま（決定論）。</summary>
    public static IReadOnlyList<DevelopingPlayer> Active(IReadOnlyList<DevelopingPlayer> roster)
        => roster.Where(p => !p.IsRetired).ToList();

    /// <summary>引退済みの部員だけを抜き出す。順序は元ロスターのまま。</summary>
    public static IReadOnlyList<DevelopingPlayer> Retired(IReadOnlyList<DevelopingPlayer> roster)
        => roster.Where(p => p.IsRetired).ToList();

    /// <summary>
    /// 3年生を引退扱いにする（フラグを立てるだけでロスターからは外さない）。
    /// すでに引退している選手は対象外＝冪等。戻り値＝この呼び出しで新たに引退した選手。
    /// </summary>
    public static IReadOnlyList<DevelopingPlayer> RetireThirdYears(IReadOnlyList<DevelopingPlayer> roster)
    {
        var retiring = roster.Where(p => !p.IsRetired && p.Grade >= 3).ToList();
        foreach (var p in retiring) p.IsRetired = true;
        return retiring;
    }

    /// <summary>
    /// 新チーム発足（引退週フック, 設計書09 §8）。3年生を引退扱いにし、残った新チームの最上級生から
    /// 暫定主将を立てる。引退者が持っていた背番号は解放し、ベンチ外の在籍部員で空き枠を詰める
    /// （<see cref="UniformNumberAssigner.FillVacancies"/>、issue #531）。以降
    /// <see cref="CaptainSelector.IsDesignationWindow"/> の間だけプレイヤーが <see cref="CaptainSelector.Designate"/>
    /// で差し替えられる。冪等（2度呼んでも同じ状態）。
    /// </summary>
    public static NewTeamTransition BeginNewTeam(IReadOnlyList<DevelopingPlayer> roster)
    {
        var retired = RetireThirdYears(roster);
        foreach (var p in retired) UniformNumberAssigner.Clear(p);
        UniformNumberAssigner.FillVacancies(Active(roster));
        var captain = CaptainSelector.BeginNewTeam(roster);
        return new NewTeamTransition(retired, captain);
    }

    /// <summary>
    /// 週を1つ進めた直後に呼ぶ遷移フック。新チーム発足週（<see cref="CaptainSelector.IsNewTeamStartWeek"/>）
    /// なら引退＋新チーム発足を行い、それ以外の週は null を返す（何もしない）。
    /// 週送りの呼び出し口を1本に集約するための入口。
    /// </summary>
    public static NewTeamTransition? OnWeekEntered(
        IReadOnlyList<DevelopingPlayer> roster, int week, SeasonCalendar calendar)
        => CaptainSelector.IsNewTeamStartWeek(week, calendar) ? BeginNewTeam(roster) : null;

    /// <summary>
    /// 入部予定者の確定（3月・設計書04 §2.1・issue #587）。<see cref="AdvanceYear"/> の③と<b>同じ入力・同じ
    /// 乱数消費</b>で新入生コホートを作るが、ロスターには入れずに返すだけ＝4月まで「予定者」として持ち回る。
    ///
    /// <para>③が渡す <c>existingNames</c> は「卒業を済ませた後のロスター」の氏名列だが、この時点ではまだ
    /// 卒業していない。<b>卒業後に残るのは現1・2年だけ</b>なので <c>Grade &lt;= 2</c> で絞れば、
    /// 順序まで含めて③と同一の列になる（＝下の名前の重複回避が1ビット一致する）。</para>
    ///
    /// <para>確定したコホートをそのまま <see cref="AdvanceYear"/> の <c>incoming</c> へ渡すと、
    /// 従来の1段生成と同じロスターになる（<c>ProspectSplitTests</c> が固定）。</para>
    /// </summary>
    /// <param name="roster">確定時点（3月）のロスター。引退した3年も含んでよい（学年で除外する）。</param>
    /// <param name="year">合流する年度（<see cref="AdvanceYear"/> へ渡すのと同じ値）。</param>
    public static IReadOnlyList<DevelopingPlayer> ConfirmIncoming(
        IReadOnlyList<DevelopingPlayer> roster, int year, RosterCoefficients coeff, IRandomSource rng,
        SkillCoefficients? skills = null, PersonalityCoefficients? personalities = null,
        FormCoefficients? form = null, double? talentCenter = null)
    {
        if (roster is null) throw new ArgumentNullException(nameof(roster));
        if (coeff is null) throw new ArgumentNullException(nameof(coeff));
        if (rng is null) throw new ArgumentNullException(nameof(rng));

        var survivors = roster.Where(p => p.Grade <= 2).Select(p => p.Name).ToList();
        return ProspectGenerator.Intake(year, coeff, rng, skills: skills, personalities: personalities,
            existingNames: survivors, form: form, talentCenter: talentCenter);
    }

    /// <summary>
    /// 年度替わり（4月）の一括処理（設計書03 §2「昇級 → 卒業 → 新入生」）。<paramref name="roster"/> を
    /// その場で更新する: ①全員を1つ進級（3年は Grade=4 になる）②卒業（Grade&gt;3）をロスターから除去して
    /// <see cref="YearRollover.Graduated"/> へ返す ③新入生（1年）を <see cref="ProspectGenerator.Intake"/> で
    /// 生成して加入 ④主将を確定（<see cref="CaptainSelector.EnsureCaptain"/>・引退で抜けたら選び直す）。
    ///
    /// <para>この関数は <see cref="SeasonEngine.Run"/> のべた書きだった年度替わりロジックの唯一の実体
    /// （二重実装を作らない・issue #535）。Unity（Shell）の週ループも同じ関数を呼ぶことで、自校ロスターの
    /// 進級・卒業・新入生が engine と1ビット一致で処理される。</para>
    ///
    /// <para><paramref name="nextId"/> は新入生へ振る安定ID（成績集計の帰属キー）の開始値。>0 のときだけ
    /// 新入生へ <paramref name="nextId"/> から昇順で連番を振り、続き番号を <see cref="YearRollover.NextId"/> へ返す
    /// （単調増加＝既存・卒業生の Id と衝突しない）。既定 0 は採番しない（<see cref="SeasonEngine.Run"/> は Id を
    /// 使わないので Id=0 のまま＝切り出し前と1ビット一致。乱数は Id 採番で1ビットも消費しない）。</para>
    ///
    /// <para>乱数は <paramref name="rng"/>（新入生生成のみ消費）。進級・卒業・主将選定・Id採番は乱数不使用
    /// （不変条件#2）。<paramref name="talentCenter"/> に名声由来の中心値（<see cref="RosterCoefficients.TalentCenterFromFame"/>）
    /// を渡すと新入生の質へ反映される（null＝既定中心・従来一致）。</para>
    /// </summary>
    /// <param name="incoming">
    /// 3月に確定済みの入部予定者（<see cref="ConfirmIncoming"/> の戻り値・issue #587）。渡すとこの集団が
    /// そのまま合流し、<b>生成を行わない＝<paramref name="rng"/> を1ビットも消費しない</b>。
    /// null（既定）なら従来どおりその場で生成する（旧セーブ・3月を跨がなかった進行のフォールバック）。
    /// </param>
    public static YearRollover AdvanceYear(
        List<DevelopingPlayer> roster, int year, RosterCoefficients coeff, IRandomSource rng,
        SkillCoefficients? skills = null, PersonalityCoefficients? personalities = null,
        FormCoefficients? form = null, double? talentCenter = null, int nextId = 0,
        IReadOnlyList<DevelopingPlayer>? incoming = null)
    {
        if (roster is null) throw new ArgumentNullException(nameof(roster));
        if (coeff is null) throw new ArgumentNullException(nameof(coeff));
        if (rng is null) throw new ArgumentNullException(nameof(rng));

        // ① 昇級（3年→4年）。② 卒業（Grade>3）を退避して除去。
        foreach (var p in roster) p.Grade++;
        var graduated = roster.Where(p => p.Grade > 3).ToList();
        roster.RemoveAll(p => p.Grade > 3);

        // ③ 新入生（1年）。3月に確定済み（issue #587）ならそれを合流させ、無ければその場で生成する。
        // 在籍部員の氏名を渡し、下の名前の重複を避ける（既存挙動を維持）。
        var newcomers = incoming ?? ProspectGenerator.Intake(year, coeff, rng, skills: skills,
            personalities: personalities, existingNames: roster.Select(p => p.Name).ToList(),
            form: form, talentCenter: talentCenter);

        // 新入生の安定ID採番（乱数不使用・単調増加）。nextId=0（既定・SeasonEngine）は採番せず Id=0 のまま。
        var id = nextId;
        if (id > 0)
            foreach (var p in newcomers) p.Id = id++;

        roster.AddRange(newcomers);

        // ④ 主将の年度更新（設計書09 §8）: 3年生引退で主将が抜けたら選び直す。手動指名が在籍なら尊重。
        CaptainSelector.EnsureCaptain(roster);

        return new YearRollover(graduated, newcomers, id);
    }
}
