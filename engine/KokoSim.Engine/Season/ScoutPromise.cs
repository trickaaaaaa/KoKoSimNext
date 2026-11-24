using System;
using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Season;

/// <summary>
/// 入部の条件（設計書04 §2.4「入部の条件（約束）」）。訪問のとき候補がたまに出してくる。
/// 約束すれば口説きやすくなり、入部後に公式戦で破ると信頼度と名声が下がる。
/// </summary>
public enum ScoutPromiseKind
{
    None,
    /// <summary>レギュラー確約（公式戦でスタメンから外さない）。</summary>
    Regular,
    /// <summary>1年から試合に出してほしい（途中出場でよい）。</summary>
    PlayAsFreshman,
    /// <summary>4番確約。</summary>
    Cleanup,
    /// <summary>上位打線（1〜5番）で使ってほしい。</summary>
    TopOfOrder,
    /// <summary>1年夏は公式戦に出さないでほしい（体づくり）。</summary>
    RestFirstSummer,
    /// <summary>代打を出さないでほしい。</summary>
    NoPinchHitter,
    /// <summary>バントさせないでほしい（打たせて）。</summary>
    NoBunt,
    /// <summary>連投させないでほしい。</summary>
    NoConsecutiveDays,
    /// <summary>先発で投げたい。</summary>
    StartingPitcher,
    /// <summary>一桁の背番号がほしい。</summary>
    SingleDigitNumber,
    /// <summary>主将にしてほしい。</summary>
    Captain,
    /// <summary>寮に入りたい。</summary>
    Dorm,
}

/// <summary>入部した選手が持つ約束の状態。</summary>
public enum ScoutPromiseStatus
{
    /// <summary>約束は無い。</summary>
    None,
    /// <summary>約束が生きている（期限内）。</summary>
    Active,
    /// <summary>守り切った。</summary>
    Kept,
    /// <summary>破った。</summary>
    Broken,
}

/// <summary>出しうる条件1つと、抽選の重み。</summary>
public readonly record struct ScoutPromiseOption(ScoutPromiseKind Kind, double Weight);

/// <summary>
/// 入部の条件の出し方（純ロジック・乱数は注入）。<b>実力に見合う条件だけ</b>を候補にし（自校の部員の中での順位で見る）、
/// <b>進路希望に合う条件ほど出やすく、矛盾する条件は出さない</b>。候補1人につき一度きり。
///
/// <para>実力は本人の本当の能力で見る（条件を出すのは本人＝自分の力は知っている）。投手系の条件は
/// 「投球がいちばん強い軸」のときだけ出す（<see cref="PitcherRoleResolver"/> の投手判定は使わない）。</para>
/// </summary>
public static class ScoutPromises
{
    // 野球の形から決まる人数（係数ではない）: 4番を争える＝打撃で部内2位まで／上位打線＝5位まで／
    // スタメン＝9位まで／ベンチ入り＝18位まで／先発を任せられる＝投手力で3位まで／登板が回ってくる＝5位まで。
    private const int CleanupRank = 2;
    private const int TopOrderRank = 5;
    private const int StarterRank = 9;
    private const int BenchRank = 18;
    private const int StartingPitcherRank = 3;
    private const int PitcherRank = 5;

    /// <summary>誰か1人としかできない条件（すでに約束していれば出さない）。</summary>
    public static bool IsExclusive(ScoutPromiseKind kind)
        => kind == ScoutPromiseKind.Cleanup || kind == ScoutPromiseKind.Captain;

    /// <summary>条件の短い名前（詳細カード・選手詳細・結果行）。</summary>
    public static string Label(ScoutPromiseKind kind) => kind switch
    {
        ScoutPromiseKind.Regular => "レギュラー確約",
        ScoutPromiseKind.PlayAsFreshman => "1年から試合に出す",
        ScoutPromiseKind.Cleanup => "4番確約",
        ScoutPromiseKind.TopOfOrder => "上位打線で使う",
        ScoutPromiseKind.RestFirstSummer => "1年夏は出さない",
        ScoutPromiseKind.NoPinchHitter => "代打を出さない",
        ScoutPromiseKind.NoBunt => "バントさせない",
        ScoutPromiseKind.NoConsecutiveDays => "連投させない",
        ScoutPromiseKind.StartingPitcher => "先発で投げさせる",
        ScoutPromiseKind.SingleDigitNumber => "一桁の背番号",
        ScoutPromiseKind.Captain => "主将にする",
        ScoutPromiseKind.Dorm => "寮に入れる",
        _ => "",
    };

    /// <summary>
    /// 約束の期限の短い言い方（選手詳細）。判定の節目（<see cref="ScoutPromiseJudge"/>）と揃える＝
    /// 起用の約束は1年秋の大会まで・果たす約束は1年のうち（3月の年度末で決着）。
    /// </summary>
    public static string DeadlineLabel(ScoutPromiseKind kind) => kind switch
    {
        ScoutPromiseKind.Regular or ScoutPromiseKind.Cleanup or ScoutPromiseKind.TopOfOrder
            or ScoutPromiseKind.NoPinchHitter or ScoutPromiseKind.NoBunt => "1年秋の大会まで",
        ScoutPromiseKind.PlayAsFreshman or ScoutPromiseKind.StartingPitcher => "1年のうちに",
        ScoutPromiseKind.RestFirstSummer => "1年夏の大会まで",
        ScoutPromiseKind.NoConsecutiveDays => "卒業まで",
        ScoutPromiseKind.SingleDigitNumber => "1年秋の背番号",
        ScoutPromiseKind.Captain => "2年秋の主将指名",
        ScoutPromiseKind.Dorm => "入部まで",
        _ => "",
    };

    /// <summary>
    /// 選手詳細に出す約束の1語（約束が無ければ空）。生きている約束は「条件・期限」、決着した約束は「条件（守った／破った）」。
    /// </summary>
    public static string DetailText(ScoutPromiseKind kind, ScoutPromiseStatus status) => status switch
    {
        ScoutPromiseStatus.Active => Label(kind) + "・" + DeadlineLabel(kind),
        ScoutPromiseStatus.Kept => Label(kind) + "（守った）",
        ScoutPromiseStatus.Broken => Label(kind) + "（破った）",
        _ => "",
    };

    /// <summary>条件を聞く会話のイベントID（<c>data/events.yaml</c>。例 <c>scout_promise_regular</c>）。</summary>
    public static string AskEventId(ScoutPromiseKind kind) => kind switch
    {
        ScoutPromiseKind.Regular => "scout_promise_regular",
        ScoutPromiseKind.PlayAsFreshman => "scout_promise_play_as_freshman",
        ScoutPromiseKind.Cleanup => "scout_promise_cleanup",
        ScoutPromiseKind.TopOfOrder => "scout_promise_top_of_order",
        ScoutPromiseKind.RestFirstSummer => "scout_promise_rest_first_summer",
        ScoutPromiseKind.NoPinchHitter => "scout_promise_no_pinch_hitter",
        ScoutPromiseKind.NoBunt => "scout_promise_no_bunt",
        ScoutPromiseKind.NoConsecutiveDays => "scout_promise_no_consecutive_days",
        ScoutPromiseKind.StartingPitcher => "scout_promise_starting_pitcher",
        ScoutPromiseKind.SingleDigitNumber => "scout_promise_single_digit_number",
        ScoutPromiseKind.Captain => "scout_promise_captain",
        ScoutPromiseKind.Dorm => "scout_promise_dorm",
        _ => "",
    };

    /// <summary>約束したあとの会話。</summary>
    public const string AcceptedEventId = "scout_promise_accepted";
    /// <summary>約束しなかったあとの会話。</summary>
    public const string DeclinedEventId = "scout_promise_declined";

    /// <summary>
    /// この候補が出しうる条件と重み。実力に見合わない条件・進路希望と矛盾する条件・すでに誰かと約束した排他の条件は入らない。
    /// </summary>
    /// <param name="taken">すでに誰かと約束している条件（部員の生きている約束＋今年の候補と交わした約束）。</param>
    public static IReadOnlyList<ScoutPromiseOption> Options(
        ScoutCandidate p, IReadOnlyList<DevelopingPlayer> roster, bool hasDorm,
        IReadOnlyCollection<ScoutPromiseKind> taken, TeamStrengthCoefficients tc, ScoutCoefficients c)
    {
        var me = PlayerStrengthProfile.Compute(p.Player, tc);
        var best = Best(me);
        int battingRank = 0, bestRank = 0, pitchingRank = 0;
        foreach (var r in roster)
        {
            var s = PlayerStrengthProfile.Compute(r, tc);
            if (s.Batting > me.Batting) battingRank++;
            if (Best(s) > best) bestRank++;
            if (s.Pitching > me.Pitching) pitchingRank++;
        }
        var pitchingIsBest = me.Pitching >= best;

        var list = new List<ScoutPromiseOption>();
        void Add(ScoutPromiseKind kind, bool fitsAbility)
        {
            if (!fitsAbility) return;
            if (IsExclusive(kind) && Contains(taken, kind)) return;
            var w = Affinity(p.Preference, kind, c);
            if (w > 0.0) list.Add(new ScoutPromiseOption(kind, w));
        }

        Add(ScoutPromiseKind.Cleanup, battingRank < CleanupRank);
        Add(ScoutPromiseKind.TopOfOrder, battingRank < TopOrderRank);
        Add(ScoutPromiseKind.Regular, bestRank < StarterRank);
        Add(ScoutPromiseKind.SingleDigitNumber, bestRank < StarterRank);
        Add(ScoutPromiseKind.NoPinchHitter, battingRank < StarterRank);
        Add(ScoutPromiseKind.NoBunt, battingRank < TopOrderRank
                                     && p.Player.Level(AbilityKind.Power) >= p.Player.Level(AbilityKind.Contact));
        Add(ScoutPromiseKind.PlayAsFreshman, bestRank < BenchRank);
        Add(ScoutPromiseKind.RestFirstSummer, bestRank >= StarterRank);
        Add(ScoutPromiseKind.StartingPitcher, pitchingIsBest && pitchingRank < StartingPitcherRank);
        Add(ScoutPromiseKind.NoConsecutiveDays, pitchingIsBest && pitchingRank < PitcherRank);
        Add(ScoutPromiseKind.Captain, p.Player.Leadership >= c.PromiseCaptainLeadershipMin);
        Add(ScoutPromiseKind.Dorm, !hasDorm && p.Distance != RecruitDistance.SamePrefecture);
        return list;
    }

    /// <summary>
    /// 訪問のとき条件を出すか引く。出すなら候補へ焼いて返す（<see cref="ScoutCandidate.Promise"/>）。
    /// もう条件を出した子・決着済みの子・出せる条件が無い子は <see cref="ScoutPromiseKind.None"/>。
    /// </summary>
    public static ScoutPromiseKind TryAsk(
        ScoutCandidate p, IReadOnlyList<DevelopingPlayer> roster, bool hasDorm,
        IReadOnlyCollection<ScoutPromiseKind> taken, TeamStrengthCoefficients tc, ScoutCoefficients c, IRandomSource rng)
    {
        if (p.Promise != ScoutPromiseKind.None || p.Decided) return ScoutPromiseKind.None;
        if (!MathUtil.Chance(c.PromiseAskProb, rng)) return ScoutPromiseKind.None;

        var options = Options(p, roster, hasDorm, taken, tc, c);
        if (options.Count == 0) return ScoutPromiseKind.None;

        var total = 0.0;
        foreach (var o in options) total += o.Weight;
        var roll = rng.NextDouble() * total;
        var picked = options[options.Count - 1].Kind;
        foreach (var o in options)
        {
            roll -= o.Weight;
            if (roll < 0.0) { picked = o.Kind; break; }
        }
        p.Promise = picked;
        p.Promised = false;
        return picked;
    }

    /// <summary>条件への返事。約束すれば成功率が上がる（<see cref="ScoutCoefficients.PromiseBonus"/>）。断っても下がらない。</summary>
    public static void Answer(ScoutCandidate p, bool accept)
    {
        if (p.Promise == ScoutPromiseKind.None) return;
        p.Promised = accept;
    }

    /// <summary>
    /// すでに誰かと約束している条件。部員の生きている約束と、今年の候補と交わした約束を合わせる
    /// （<paramref name="except"/> 本人は除く）。
    /// </summary>
    public static IReadOnlyCollection<ScoutPromiseKind> Taken(
        IReadOnlyList<DevelopingPlayer> roster, ScoutingState? state, ScoutCandidate? except)
    {
        var set = new HashSet<ScoutPromiseKind>();
        foreach (var r in roster)
            if (r.PromiseStatus == ScoutPromiseStatus.Active) set.Add(r.Promise);
        if (state != null)
            foreach (var cnd in state.Candidates)
                if (!ReferenceEquals(cnd, except) && cnd.Promised) set.Add(cnd.Promise);
        return set;
    }

    /// <summary>進路希望との相性（合う＝重い・中立・矛盾＝0で出さない）。</summary>
    private static double Affinity(CareerPathPreference pref, ScoutPromiseKind kind, ScoutCoefficients c)
    {
        switch (pref)
        {
            case CareerPathPreference.PlayingTime:
                // 試合に出られる学校を探している子が「出さないで」とは言わない。
                if (kind == ScoutPromiseKind.RestFirstSummer) return 0.0;
                return kind is ScoutPromiseKind.Regular or ScoutPromiseKind.PlayAsFreshman or ScoutPromiseKind.Cleanup
                    or ScoutPromiseKind.TopOfOrder or ScoutPromiseKind.NoPinchHitter or ScoutPromiseKind.StartingPitcher
                    or ScoutPromiseKind.SingleDigitNumber
                    ? c.PromiseWeightFit : c.PromiseWeightNeutral;
            case CareerPathPreference.Prestige:
                // 名門で腰を据えてやりたい子＝体づくり・無理をしない・寮・主将。
                return kind is ScoutPromiseKind.RestFirstSummer or ScoutPromiseKind.NoConsecutiveDays
                    or ScoutPromiseKind.Dorm or ScoutPromiseKind.Captain
                    ? c.PromiseWeightFit : c.PromiseWeightNeutral;
            default:
                // 地元でやりたい子が寮を条件にはしない。
                return kind == ScoutPromiseKind.Dorm ? 0.0 : c.PromiseWeightNeutral;
        }
    }

    private static bool Contains(IReadOnlyCollection<ScoutPromiseKind> taken, ScoutPromiseKind kind)
    {
        foreach (var k in taken) if (k == kind) return true;
        return false;
    }

    private static double Best(PlayerStrength s)
        => Math.Max(Math.Max(s.Batting, s.Mobility), Math.Max(s.Defense, s.Pitching));
}
