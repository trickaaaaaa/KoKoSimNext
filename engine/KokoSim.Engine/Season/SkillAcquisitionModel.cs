using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Season;

/// <summary>スキルの変化の種類（設計書10 §3・issue #1471）。</summary>
public enum SkillChangeKind
{
    /// <summary>新しく身についた（持っていなかったスキル）。</summary>
    Acquired,
    /// <summary>隠しで持っていた素質が表に出た（覚醒・実戦で発現）。</summary>
    Manifested,
    /// <summary>気づき（育成眼）で隠しスキルに気づいた。</summary>
    Revealed,
    /// <summary>負スキルを克服した（外れた）。</summary>
    Overcome,
}

/// <summary>スキルが変化したきっかけ（通知の文言・ノベルの会話を分ける）。</summary>
public enum SkillChangeCause
{
    Insight,    // 気づき（設計書03 §5.6）
    Awakening,  // 覚醒・大覚醒（設計書03 §5.5）
    Match,      // 公式戦での活躍（実戦経験）
    Grade,      // 学年が上がった（年度替わり）
}

/// <summary>スキルの変化1件（通知フィード・ノベル・計測用の観測データ）。適用は済んでいる。</summary>
public sealed record SkillChangeNotice(DevelopingPlayer Player, Skill Skill, SkillChangeKind Kind, SkillChangeCause Cause);

/// <summary>
/// 後天習得・克服の係数（設計書10 §3・issue #1471）。🟡頻度は控えめな既定値（OPEN-QUESTIONS Q58）。
/// 気づきでの開示の頻度は <see cref="InsightCoefficients.HiddenSkillTopicProb"/> が持つ。
/// </summary>
public sealed record SkillAcquisitionCoefficients
{
    /// <summary>覚醒（分野の能力が跳ねた）のとき、その分野の有益スキルが付く確率。</summary>
    public double AwakeningSkillProb { get; init; } = 0.15;
    /// <summary>大覚醒のとき、その分野の有益スキルが付く確率。</summary>
    public double GrandAwakeningSkillProb { get; init; } = 0.5;
    /// <summary>公式戦で活躍した選手に、その分野の有益スキルが付く確率（1試合あたり）。</summary>
    public double MatchSkillProb { get; init; } = 0.04;
    /// <summary>公式戦で活躍した選手が、その分野の負スキル（試合中の癖）を克服する確率（1試合あたり）。</summary>
    public double MatchOvercomeProb { get; init; } = 0.10;
    /// <summary>活躍の基準（打者）: 安打数。</summary>
    public int MatchMinHits { get; init; } = 3;
    /// <summary>活躍の基準（打者）: 打点。</summary>
    public int MatchMinRbi { get; init; } = 4;
    /// <summary>活躍の基準（先発）: アウト数（この数以上かつ失点 <see cref="MatchMaxRuns"/> 以下）。</summary>
    public int MatchMinOuts { get; init; } = 21;
    public int MatchMaxRuns { get; init; } = 2;
    /// <summary>活躍の基準（先発）: 奪三振（これ以上なら回・失点を問わない）。</summary>
    public int MatchMinStrikeOuts { get; init; } = 10;
    /// <summary>活躍の基準（救援）: 無失点で取ったアウト数。</summary>
    public int ReliefMinOuts { get; init; } = 6;
    /// <summary>年度替わりに、2・3年が体質の負スキル（サボり癖・ケガしやすい）を克服する確率（1年あたり・勤勉さ50のとき）。</summary>
    public double GradeOvercomeProb { get; init; } = 0.20;
    /// <summary>
    /// 学年の克服の勤勉さ（性格③）への連動幅（Q58 決着③）。確率＝<see cref="GradeOvercomeProb"/>＋(勤勉さ−50)/49×この値
    /// ＝勤勉さ1で −幅、99で ＋幅（既定 0.10 で 10%〜30%）。勤勉さの分布は50中心なので平均は変わらない。
    /// </summary>
    public double GradeOvercomeDiligenceSpan { get; init; } = 0.10;
    /// <summary>後天習得はこの数（怪物を除く）を超えては付けない。</summary>
    public int MaxSkills { get; init; } = 4;
}

/// <summary>
/// 特殊能力の後天習得・克服（設計書10 §3・issue #1471）。1.0 で入れる道は3つ＝オーナー判断 2026-11-23:
/// <list type="number">
/// <item>覚醒で有益スキル（覚醒イベントのとき、その分野の有益スキルをまれに付ける）</item>
/// <item>実戦経験（公式戦での活躍を条件に習得。同じ分野の負スキルの克服もここ）</item>
/// <item>負スキルの克服（体質のサボり癖・ケガしやすいは学年が上がるときに）</item>
/// </list>
/// 練習の積み重ねでの習得は入れない。隠しスキルが表に出る道は気づき（<see cref="InsightModel"/>）と、
/// 覚醒・実戦で付くはずのスキルを隠しで持っていた場合の発現。
/// <para>乱数はすべて呼び出し側が Fork した専用ストリーム（既存の育成・試合の乱数列を動かさない）。
/// 反対のスキル（<see cref="SkillNames.OppositeOf"/>）を持つ選手には、そのスキルを付けない。</para>
/// </summary>
public static class SkillAcquisitionModel
{
    /// <summary>覚醒・実戦で付きうる打撃系の有益スキル（両刃・行動特性は除く＝活躍の結果として不自然なもの）。</summary>
    internal static readonly Skill[] BattingGain =
    {
        Skill.SlowStarterBat, Skill.Grinder, Skill.ClutchHitter, Skill.FastballHitter,
        Skill.BreakingBallHitter, Skill.OppositeFieldHitter,
    };

    /// <summary>覚醒・先発の活躍で付きうる投手系の有益スキル。</summary>
    internal static readonly Skill[] PitchingGain =
    {
        Skill.SlowStarterPitch, Skill.DeceptiveBall, Skill.ClutchPitcher, Skill.QuickDelivery,
    };

    /// <summary>救援の活躍で付きうるスキル。</summary>
    internal static readonly Skill[] ReliefGain = { Skill.Fireman, Skill.ClutchPitcher };

    /// <summary>
    /// 守備・走塁の覚醒で付きうるスキル（Q58 決着④）。守備系スキルは廃止済み（#907）で走塁の巧拙はパラメータなので、
    /// 分野の練習の積み重ねにつながる体質・チーム系から選ぶ＝守備はお手本・故障しにくい、走塁はムードメーカー・故障しにくい。
    /// </summary>
    internal static readonly Skill[] DefenseGain = { Skill.RoleModel, Skill.Durable };
    internal static readonly Skill[] RunningGain = { Skill.Moodmaker, Skill.Durable };

    /// <summary>実戦の活躍で克服しうる負スキル（試合中の癖）。</summary>
    internal static readonly Skill[] BattingFlaws = { Skill.ChokeHitter };
    internal static readonly Skill[] PitchingFlaws =
    {
        Skill.ChokePitcher, Skill.ShakyStart, Skill.SecondTimeThrough, Skill.SlowDelivery,
    };

    /// <summary>学年で克服しうる体質の負スキル。</summary>
    internal static readonly Skill[] ConstitutionFlaws = { Skill.Lazy, Skill.InjuryProne };

    /// <summary>
    /// 覚醒・大覚醒の後処理。発火した成長イベントのうち覚醒（打撃・投球の分野）だけを見て、
    /// まれにその分野の有益スキルを付ける（守備・走塁も同じ確率・Q58 決着④）。
    /// </summary>
    public static IReadOnlyList<SkillChangeNotice> OnGrowthEvents(
        IReadOnlyList<GrowthNotice> growth, IRandomSource rng, SkillAcquisitionCoefficients c)
    {
        var notices = new List<SkillChangeNotice>();
        foreach (var g in growth)
        {
            double prob;
            if (g.Type == GrowthEventType.Awakening) prob = c.AwakeningSkillProb;
            else if (g.Type == GrowthEventType.GrandAwakening) prob = c.GrandAwakeningSkillProb;
            else continue;

            var pool = g.Domain switch
            {
                GrowthDomain.Batting => BattingGain,
                GrowthDomain.Pitching => PitchingGain,
                GrowthDomain.Defense => DefenseGain,
                GrowthDomain.Running => RunningGain,
                _ => null,
            };
            if (pool is null) continue;
            if (!MathUtil.Chance(prob, rng)) continue;
            if (TryGain(g.Player, pool, rng, c, SkillChangeCause.Awakening) is { } n) notices.Add(n);
        }
        return notices;
    }

    /// <summary>
    /// 公式戦1試合の後処理（実戦経験）。自校の出場者のうち活躍した選手について、まず同じ分野の負スキルの克服を、
    /// 起きなければ有益スキルの習得を判定する（1試合1人1件まで）。練習試合では呼ばない。
    /// 抽選は「活躍した選手」にだけ引く＝打順・登板順で固定の順序（決定論）。
    /// </summary>
    public static IReadOnlyList<SkillChangeNotice> AfterOfficialMatch(
        GameResult detail, bool managerIsAway, IReadOnlyList<DevelopingPlayer> roster,
        IRandomSource rng, SkillAcquisitionCoefficients c)
    {
        var notices = new List<SkillChangeNotice>();
        var byId = new Dictionary<int, DevelopingPlayer>(roster.Count);
        foreach (var dp in roster) if (dp.Id != 0) byId[dp.Id] = dp;
        var done = new HashSet<int>();

        var pitching = managerIsAway ? detail.AwayPitching : detail.HomePitching;
        for (var i = 0; i < pitching.Count; i++)
        {
            var l = pitching[i];
            if (l.SourceId is not { } id || done.Contains(id) || !byId.TryGetValue(id, out var dp)) continue;
            var relief = i > 0;
            var good = relief
                ? l.Outs >= c.ReliefMinOuts && l.Runs == 0
                : (l.Outs >= c.MatchMinOuts && l.Runs <= c.MatchMaxRuns) || l.StrikeOuts >= c.MatchMinStrikeOuts;
            if (!good) continue;
            done.Add(id);
            if (Judge(dp, PitchingFlaws, relief ? ReliefGain : PitchingGain, rng, c) is { } n) notices.Add(n);
        }

        foreach (var l in managerIsAway ? detail.AwayBatting : detail.HomeBatting)
        {
            if (l.SourceId is not { } id || done.Contains(id) || !byId.TryGetValue(id, out var dp)) continue;
            var good = l.Hits >= c.MatchMinHits || l.Rbi >= c.MatchMinRbi;
            if (!good) continue;
            done.Add(id);
            if (Judge(dp, BattingFlaws, BattingGain, rng, c) is { } n) notices.Add(n);
        }
        return notices;
    }

    /// <summary>
    /// 年度替わり（進級の後）の克服判定。2・3年（新入生は除く）が持つ体質の負スキルを、1つずつ
    /// <see cref="GradeOvercomeProb(DevelopingPlayer, SkillAcquisitionCoefficients)"/>（勤勉さに連動）で外す。隠しで持っていた負スキルは対象外
    /// （本人も監督も知らない癖を「克服した」とは言えない＝気づきで表に出てから克服の対象になる）。
    /// </summary>
    public static IReadOnlyList<SkillChangeNotice> OnYearAdvanced(
        IReadOnlyList<DevelopingPlayer> roster, IRandomSource rng, SkillAcquisitionCoefficients c)
    {
        var notices = new List<SkillChangeNotice>();
        foreach (var p in roster)
        {
            if (p.Grade < 2 || p.Grade > 3) continue;
            foreach (var flaw in ConstitutionFlaws)
            {
                if (!p.Skills.Visible.Contains(flaw)) continue;
                if (!MathUtil.Chance(GradeOvercomeProb(p, c), rng)) continue;
                p.Skills = p.Skills.Remove(flaw);
                notices.Add(new SkillChangeNotice(p, flaw, SkillChangeKind.Overcome, SkillChangeCause.Grade));
            }
        }
        return notices;
    }

    /// <summary>学年の克服の確率（勤勉さに連動・Q58 決着③）。0〜1 に収める。</summary>
    public static double GradeOvercomeProb(DevelopingPlayer p, SkillAcquisitionCoefficients c)
        => MathUtil.Clamp(c.GradeOvercomeProb + (p.Diligence - 50) / 49.0 * c.GradeOvercomeDiligenceSpan, 0.0, 1.0);

    private static SkillChangeNotice? Judge(DevelopingPlayer p, Skill[] flaws, Skill[] gains,
        IRandomSource rng, SkillAcquisitionCoefficients c)
    {
        // 克服が先（試練を乗り越える道を優先する）。見えている負スキルだけが対象。
        var flaw = flaws.Where(s => p.Skills.Visible.Contains(s)).ToList();
        if (flaw.Count > 0 && MathUtil.Chance(c.MatchOvercomeProb, rng))
        {
            var s = flaw[rng.NextInt(0, flaw.Count)];
            p.Skills = p.Skills.Remove(s);
            return new SkillChangeNotice(p, s, SkillChangeKind.Overcome, SkillChangeCause.Match);
        }
        if (!MathUtil.Chance(c.MatchSkillProb, rng)) return null;
        return TryGain(p, gains, rng, c, SkillChangeCause.Match);
    }

    /// <summary>
    /// 有益スキルを1つ付ける。隠しで持っている候補があればそれを発現させる（眠った素質が「兆し」の正体＝設計書10 §6）。
    /// 無ければ持っていない候補から1つ（反対のスキルを持つものは除く・上限を超えるなら付けない）。
    /// </summary>
    private static SkillChangeNotice? TryGain(DevelopingPlayer p, Skill[] pool, IRandomSource rng,
        SkillAcquisitionCoefficients c, SkillChangeCause cause)
    {
        var dormant = pool.Where(s => p.Skills.IsHidden(s)).ToList();
        if (dormant.Count > 0)
        {
            var s = dormant[rng.NextInt(0, dormant.Count)];
            p.Skills = p.Skills.Reveal(s);
            return new SkillChangeNotice(p, s, SkillChangeKind.Manifested, cause);
        }

        if (CountOrdinary(p.Skills) >= c.MaxSkills) return null;
        var candidates = pool.Where(s => !p.Skills.Has(s)
            && !(SkillNames.OppositeOf(s) is { } o && p.Skills.Has(o))).ToList();
        if (candidates.Count == 0) return null;
        var pick = candidates[rng.NextInt(0, candidates.Count)];
        p.Skills = p.Skills.Acquire(pick);
        return new SkillChangeNotice(p, pick, SkillChangeKind.Acquired, cause);
    }

    private static int CountOrdinary(SkillSet s)
        => s.Visible.Count(k => k != Skill.Monster) + s.Hidden.Count(k => k != Skill.Monster);
}
