using System;
using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Season;

/// <summary>選手成長イベントの種別（設計書03 §5.5）。上昇はレアな福音、下降は乗り越えられる試練。</summary>
public enum GrowthEventType
{
    Awakening,     // 覚醒: 分野の能力が一段跳ね上がる（兆しの上に起きる）。分野は GrowthNotice.Domain
    Breakthrough,  // フォーム開眼/コツを掴む: 特定能力がぐっと伸びる
    Slump,         // スランプ（一時的）: 一定期間の能力ダウン。立て直せる
    Plateau,       // 伸び悩み（恒久的）: 分野の伸びしろが頭打ちに（「ここが限界だった」）
    Yips,          // イップス（限定的・恒久寄り）: 送球/制球の不調。克服の道を残す
    YipsOvercome,  // イップス克服
    GrandAwakening, // 大覚醒（issue #694）: 1能力が大きく跳ね、才能上限も少し上がる。極レア
    GrowthSurge,    // 伸びしろアップ（issue #694）: 分野の伸びしろが恒久的に上がる＝Plateau の逆
    NewPitch,       // 新球種のコツ（issue #694）: 投手が変化球を1つ覚える
    PitchRefined,   // 球種を磨く（issue #694）: 持ち球が上限本数のとき、いちばん弱い球種が良くなる
    BigStage,       // 大舞台での化け（issue #694）: 公式戦の好投・好打の直後に精神力と分野の能力が伸びる
}

/// <summary>覚醒などが起きた分野（issue #694。会話と上がる能力を分ける）。</summary>
public enum GrowthDomain { Batting, Pitching, Defense, Running }

/// <summary>イベントで実際に動いた能力と量（上限で止まった分は含まない＝表示は実測で出す）。</summary>
public readonly record struct AbilityGain(AbilityKind Ability, int Amount);

/// <summary>発火した成長イベント（UIフィード・検証用の観測データ）。</summary>
public sealed record GrowthNotice(DevelopingPlayer Player, GrowthEventType Type, AbilityKind? Ability)
{
    /// <summary>実際に上がった能力と量（issue #694。上がった順）。下降イベント等では空。</summary>
    public IReadOnlyList<AbilityGain> Gains { get; init; } = Array.Empty<AbilityGain>();
    /// <summary>起きた分野（覚醒・大覚醒・伸びしろアップ・大舞台）。</summary>
    public GrowthDomain? Domain { get; init; }
    /// <summary>覚えた／磨いた球種（NewPitch / PitchRefined）。</summary>
    public PitchType? Pitch { get; init; }
    /// <summary>精神力の上昇量（大舞台）。</summary>
    public int MentalGain { get; init; }
    /// <summary>才能上限の上昇量（大覚醒・伸びしろアップ。1能力あたり）。</summary>
    public int CapLift { get; init; }
}

/// <summary>成長イベントの係数（設計書03 §5.5）。🟡覚醒率・恒久ダウン率は設計書07で体験を壊さない範囲に調整。</summary>
public sealed record GrowthEventCoefficients
{
    // 上昇（福音・レア）
    public double AwakeningWeeklyProb { get; init; } = 0.007;   // #694: 0.0015→0.007（年0.4回→約2回/校）
    /// <summary>覚醒の条件: 分野伸びしろがこれ以上（完全ランダムにしない=「兆し」）。</summary>
    public double AwakeningGrowthThreshold { get; init; } = 1.15;
    /// <summary>覚醒の条件: 調子がこれ以上（好調持続の上に起きる）。</summary>
    public double AwakeningConditionMin { get; init; } = 0.2;
    public int AwakeningGain { get; init; } = 4;
    /// <summary>覚醒発生時の調子押し上げ（設計書02 §3.3「イベントで上下」, issue #46）。</summary>
    public double AwakeningConditionBoost { get; init; } = 0.15;

    public double BreakthroughWeeklyProb { get; init; } = 0.003;
    public int BreakthroughGain { get; init; } = 3;

    // 下降（試練・文脈必須＝理不尽回避）
    public double SlumpWeeklyProb { get; init; } = 0.0025;
    /// <summary>スランプの条件: 調子がこれ以下（絶好調の選手が突然沈まない）。</summary>
    public double SlumpConditionMax { get; init; } = -0.2;
    public int SlumpWeeksMin { get; init; } = 3;
    public int SlumpWeeksMax { get; init; } = 6;
    /// <summary>スランプ中の能力一律係数（怪我より軽い）。</summary>
    public double SlumpPerformanceFactor { get; init; } = 0.93;
    /// <summary>スランプ発生時の調子押し下げ（設計書02 §3.3「イベントで上下」, issue #46）。</summary>
    public double SlumpConditionDrop { get; init; } = 0.15;

    /// <summary>伸び悩みの条件: 分野の現在値が上限にこの範囲まで迫っている（=「限界の顕在化」）。</summary>
    public double PlateauCapProximity { get; init; } = 3.0;
    public double PlateauWeeklyProb { get; init; } = 0.004;
    /// <summary>伸び悩み発生時、その分野の伸びしろに乗る恒久係数。</summary>
    public double PlateauGrowthFactor { get; init; } = 0.6;

    /// <summary>イップスの条件: 高疲労またはスランプ中のみ（脈絡ない劣化を作らない）。</summary>
    public double YipsWeeklyProb { get; init; } = 0.0008;
    public int YipsAbilityDrop { get; init; } = 5;
    /// <summary>イップス克服の週次確率（克服イベントで取り返す道）。</summary>
    public double YipsOvercomeWeeklyProb { get; init; } = 0.03;

    // --- issue #694 で足した上昇イベント（2026-10-28 監督決定: 4系統すべて） ---
    /// <summary>大覚醒の週次確率（極レア）。条件は覚醒と同じ「伸びしろ」に加え、より強い好調。</summary>
    public double GrandAwakeningWeeklyProb { get; init; } = 0.0006;
    public double GrandAwakeningConditionMin { get; init; } = 0.4;
    /// <summary>大覚醒で1能力が上がる量（才能上限も <see cref="GrandAwakeningCapLift"/> だけ上がる）。</summary>
    public int GrandAwakeningGain { get; init; } = 10;
    public int GrandAwakeningCapLift { get; init; } = 5;

    /// <summary>伸びしろアップの週次確率。好調（≥ConditionMin）の上に起きる。</summary>
    public double GrowthSurgeWeeklyProb { get; init; } = 0.0015;
    public double GrowthSurgeConditionMin { get; init; } = 0.1;
    /// <summary>分野の伸びしろに掛かる恒久倍率（<see cref="GrowthSurgeMax"/> で頭打ち）。</summary>
    public double GrowthSurgeFactor { get; init; } = 1.25;
    public double GrowthSurgeMax { get; init; } = 1.8;
    /// <summary>伸びしろアップで分野の各能力の才能上限が上がる量（上限99）。</summary>
    public int GrowthSurgeCapLift { get; init; } = 3;

    /// <summary>新球種のコツ（投手のみ）の週次確率。</summary>
    public double NewPitchWeeklyProb { get; init; } = 0.0025;
    /// <summary>持ち球（変化球）の上限本数。これ未満なら覚え、達していればいちばん弱い球種を磨く。</summary>
    public int NewPitchMaxBreaking { get; init; } = 4;
    /// <summary>覚えたての球種のランク（キレからのオフセット。低めで入る）。</summary>
    public int NewPitchOffset { get; init; } = -6;
    /// <summary>球種を磨いたときのオフセット上昇（上限+20）。</summary>
    public int PitchRefineGain { get; init; } = 5;

    /// <summary>大舞台での化け: 大会の格ごとの発火確率（好投・好打だった選手1人あたり）。</summary>
    public double BigStageProbKoshien { get; init; } = 0.4;
    public double BigStageProbRegional { get; init; } = 0.25;
    public double BigStageProbPrefectural { get; init; } = 0.1;
    public int BigStageMentalGain { get; init; } = 5;
    /// <summary>大舞台で分野の能力（2つ）に入る上昇量。</summary>
    public int BigStageGain { get; init; } = 3;
    /// <summary>好打の目安: 安打数・打点・本塁打（本塁打は安打2以上と組み合わせ）。</summary>
    public int BigStageMinHits { get; init; } = 3;
    public int BigStageMinRbi { get; init; } = 4;
    /// <summary>好投の目安: アウト数以上かつ失点以下、または奪三振以上。</summary>
    public int BigStageMinOuts { get; init; } = 21;
    public int BigStageMaxRuns { get; init; } = 2;
    public int BigStageMinStrikeOuts { get; init; } = 10;

    /// <summary>大会の格→大舞台の発火確率（練習試合は0）。</summary>
    public double BigStageProb(TournamentKind kind) => kind switch
    {
        TournamentKind.SummerKoshien or TournamentKind.SenbatsuKoshien => BigStageProbKoshien,
        TournamentKind.Regional or TournamentKind.Jingu => BigStageProbRegional,
        TournamentKind.Summer or TournamentKind.Autumn => BigStageProbPrefectural,
        _ => 0.0,
    };
}

/// <summary>
/// 選手成長イベント（設計書03 §5.5）。経験値の積み上げに「非線形な跳ね」を加える。
/// 原則: 下降には必ず文脈（限界・不調・怪我）。怪我由来の低下は InjuryModel が担い重複させない。
/// </summary>
public static class GrowthEventModel
{
    /// <summary>週次処理。効果を選手に適用し、発火一覧を返す（UIフィード・気づきイベントの素材）。</summary>
    public static IReadOnlyList<GrowthNotice> Week(
        IReadOnlyList<DevelopingPlayer> roster, IRandomSource rng, GrowthEventCoefficients c)
    {
        var notices = new List<GrowthNotice>();
        foreach (var p in roster)
        {
            // イップス克服（下降からの回復を先に判定: 取り返す道を残す）。
            if (p.HasYips && MathUtil.Chance(c.YipsOvercomeWeeklyProb, rng))
            {
                p.HasYips = false;
                var k = PitcherRoleResolver.LeansPitcher(p) ? AbilityKind.Control : AbilityKind.ThrowAccuracy;
                Raise(p, k, c.YipsAbilityDrop); // 落ちた分を取り返す
                notices.Add(new GrowthNotice(p, GrowthEventType.YipsOvercome, k));
                continue;
            }

            // スランプ進行（回復）。
            if (p.SlumpWeeks > 0) p.SlumpWeeks--;

            // --- 上昇イベント（福音・レア） ---
            if (TryGrandAwakening(p, rng, c, notices)) continue;
            if (TryAwakening(p, rng, c, notices)) continue;
            if (TryGrowthSurge(p, rng, c, notices)) continue;
            if (TryNewPitch(p, rng, c, notices)) continue;
            if (TryBreakthrough(p, rng, c, notices)) continue;

            // --- 下降イベント（試練・文脈必須） ---
            if (TrySlump(p, rng, c, notices)) continue;
            if (TryPlateau(p, rng, c, notices)) continue;
            TryYips(p, rng, c, notices);
        }
        return notices;
    }

    private static bool TryAwakening(DevelopingPlayer p, IRandomSource rng, GrowthEventCoefficients c,
        List<GrowthNotice> notices)
    {
        if (p.ConditionValue < c.AwakeningConditionMin) return false;
        var domains = EligibleDomains(p, c.AwakeningGrowthThreshold);
        if (domains.Count == 0) return false;
        if (!MathUtil.Chance(c.AwakeningWeeklyProb, rng)) return false;

        // 分野の主要能力から2つ（別々の能力）を一段跳ね上げる（才能上限は尊重）。
        var domain = domains[rng.NextInt(0, domains.Count)];
        var gains = RaiseDistinct(p, KindsOf(domain), 2, c.AwakeningGain, rng);
        if (gains.Count == 0) return false;   // 上限に張り付いていて何も上がらない＝起きなかったことにする
        p.ConditionValue = MathUtil.Clamp(p.ConditionValue + c.AwakeningConditionBoost, -1.0, 1.0);
        notices.Add(new GrowthNotice(p, GrowthEventType.Awakening, gains[0].Ability) { Gains = gains, Domain = domain });
        return true;
    }

    /// <summary>大覚醒（issue #694）: 1能力が大きく跳ね、その能力の才能上限も少し上がる（上限99）。</summary>
    private static bool TryGrandAwakening(DevelopingPlayer p, IRandomSource rng, GrowthEventCoefficients c,
        List<GrowthNotice> notices)
    {
        if (p.ConditionValue < c.GrandAwakeningConditionMin) return false;
        var domains = EligibleDomains(p, c.AwakeningGrowthThreshold);
        if (domains.Count == 0) return false;
        if (!MathUtil.Chance(c.GrandAwakeningWeeklyProb, rng)) return false;

        var domain = domains[rng.NextInt(0, domains.Count)];
        var kinds = KindsOf(domain);
        var k = kinds[rng.NextInt(0, kinds.Length)];
        var lift = LiftCap(p, k, c.GrandAwakeningCapLift);
        var gained = Raise(p, k, c.GrandAwakeningGain);
        if (gained == 0) return false;
        p.ConditionValue = MathUtil.Clamp(p.ConditionValue + c.AwakeningConditionBoost, -1.0, 1.0);
        notices.Add(new GrowthNotice(p, GrowthEventType.GrandAwakening, k)
        {
            Gains = new[] { new AbilityGain(k, gained) }, Domain = domain, CapLift = lift,
        });
        return true;
    }

    /// <summary>
    /// 伸びしろアップ（issue #694）: 分野の伸びしろ（*Growth）を恒久的に引き上げ、分野の才能上限も少し上げる。
    /// その場で能力は動かない＝以後の練習の伸びで差が出る（伸び悩み Plateau の逆向き）。
    /// </summary>
    private static bool TryGrowthSurge(DevelopingPlayer p, IRandomSource rng, GrowthEventCoefficients c,
        List<GrowthNotice> notices)
    {
        if (p.ConditionValue < c.GrowthSurgeConditionMin) return false;
        if (!MathUtil.Chance(c.GrowthSurgeWeeklyProb, rng)) return false;

        var domains = new List<GrowthDomain> { GrowthDomain.Batting, GrowthDomain.Defense };
        if (PitcherRoleResolver.LeansPitcher(p)) domains.Add(GrowthDomain.Pitching);
        var domain = domains[rng.NextInt(0, domains.Count)];
        var before = GrowthOf(p, domain);
        var after = Math.Min(c.GrowthSurgeMax, before * c.GrowthSurgeFactor);
        var lift = 0;
        foreach (var k in KindsOf(domain)) lift = Math.Max(lift, LiftCap(p, k, c.GrowthSurgeCapLift));
        if (after <= before && lift == 0) return false;   // 伸びしろも上限も頭打ち＝何も起きない
        SetGrowth(p, domain, Math.Max(before, after));
        notices.Add(new GrowthNotice(p, GrowthEventType.GrowthSurge, null) { Domain = domain, CapLift = lift });
        return true;
    }

    /// <summary>
    /// 新球種のコツ（issue #694・設計書03 §5.5）: 投手が変化球を1つ覚える。持ち球が上限本数に達していれば、
    /// いちばん弱い球種のランクを上げる（球種を磨く）。
    /// </summary>
    private static bool TryNewPitch(DevelopingPlayer p, IRandomSource rng, GrowthEventCoefficients c,
        List<GrowthNotice> notices)
    {
        if (!PitcherRoleResolver.LeansPitcher(p)) return false;
        if (!MathUtil.Chance(c.NewPitchWeeklyProb, rng)) return false;

        if (p.LearnedPitches.Count < c.NewPitchMaxBreaking)
        {
            var pool = new List<PitchType>();
            foreach (var t in BreakingTypes)
                if (!p.LearnedPitches.Exists(x => x.Type == t)) pool.Add(t);
            if (pool.Count == 0) return false;
            var type = pool[rng.NextInt(0, pool.Count)];
            p.LearnedPitches.Add(new LearnedPitch(type, c.NewPitchOffset, c.NewPitchOffset));
            notices.Add(new GrowthNotice(p, GrowthEventType.NewPitch, null) { Pitch = type });
            return true;
        }

        var weakest = -1;
        for (var i = 0; i < p.LearnedPitches.Count; i++)
        {
            var lp = p.LearnedPitches[i];
            if (lp.PowerOffset >= PitchOffsetMax && lp.SharpnessOffset >= PitchOffsetMax) continue;
            if (weakest < 0 || Sum(lp) < Sum(p.LearnedPitches[weakest])) weakest = i;
        }
        if (weakest < 0) return false;
        var w = p.LearnedPitches[weakest];
        p.LearnedPitches[weakest] = w with
        {
            PowerOffset = Math.Min(PitchOffsetMax, w.PowerOffset + c.PitchRefineGain),
            SharpnessOffset = Math.Min(PitchOffsetMax, w.SharpnessOffset + c.PitchRefineGain),
        };
        notices.Add(new GrowthNotice(p, GrowthEventType.PitchRefined, null) { Pitch = w.Type });
        return true;

        static int Sum(LearnedPitch lp) => lp.PowerOffset + lp.SharpnessOffset;
    }

    private static bool TryBreakthrough(DevelopingPlayer p, IRandomSource rng, GrowthEventCoefficients c,
        List<GrowthNotice> notices)
    {
        if (!MathUtil.Chance(c.BreakthroughWeeklyProb, rng)) return false;
        var kinds = PitcherRoleResolver.LeansPitcher(p) ? AbilityKinds.Pitching : AbilityKinds.Batting;
        var k = kinds[rng.NextInt(0, kinds.Length)];
        var gained = Raise(p, k, c.BreakthroughGain);
        if (gained == 0) return false;   // 上限に張り付いていて上がらない＝起きなかったことにする
        notices.Add(new GrowthNotice(p, GrowthEventType.Breakthrough, k) { Gains = new[] { new AbilityGain(k, gained) } });
        return true;
    }

    /// <summary>
    /// 大舞台での化け（issue #694・設計書03 §5.5）。公式戦1試合ぶん、好打・好投だった自校の出場者に
    /// <paramref name="prob"/> の確率で起きる（大会の格で決まる＝<see cref="GrowthEventCoefficients.BigStageProb"/>）。
    /// 精神力と、その活躍の分野（打者＝打撃／投手＝投球）の能力2つが伸びる。乱数は呼び出し側の専用ストリーム。
    /// </summary>
    public static IReadOnlyList<GrowthNotice> BigStage(
        Match.Game.GameResult detail, bool managerIsAway, IReadOnlyList<DevelopingPlayer> roster,
        double prob, IRandomSource rng, GrowthEventCoefficients c)
    {
        var notices = new List<GrowthNotice>();
        if (prob <= 0.0) return notices;
        var byId = new Dictionary<int, DevelopingPlayer>(roster.Count);
        foreach (var dp in roster) byId[dp.Id] = dp;
        var done = new HashSet<int>();   // 投打両方で活躍しても1試合1回まで

        foreach (var l in managerIsAway ? detail.AwayPitching : detail.HomePitching)
        {
            if (l.SourceId is not { } id || !byId.TryGetValue(id, out var dp) || done.Contains(id)) continue;
            var good = (l.Outs >= c.BigStageMinOuts && l.Runs <= c.BigStageMaxRuns) || l.StrikeOuts >= c.BigStageMinStrikeOuts;
            if (!good || !MathUtil.Chance(prob, rng)) continue;
            done.Add(id);
            AddBigStage(dp, GrowthDomain.Pitching, rng, c, notices);
        }
        foreach (var l in managerIsAway ? detail.AwayBatting : detail.HomeBatting)
        {
            if (l.SourceId is not { } id || !byId.TryGetValue(id, out var dp) || done.Contains(id)) continue;
            var good = l.Hits >= c.BigStageMinHits || l.Rbi >= c.BigStageMinRbi || (l.HomeRuns >= 1 && l.Hits >= 2);
            if (!good || !MathUtil.Chance(prob, rng)) continue;
            done.Add(id);
            AddBigStage(dp, GrowthDomain.Batting, rng, c, notices);
        }
        return notices;
    }

    private static void AddBigStage(DevelopingPlayer dp, GrowthDomain domain, IRandomSource rng,
        GrowthEventCoefficients c, List<GrowthNotice> notices)
    {
        var mental = Math.Max(0, Math.Min(dp.MentalCap, dp.Mental + c.BigStageMentalGain) - dp.Mental);
        dp.Mental += mental;
        var gains = RaiseDistinct(dp, KindsOf(domain), 2, c.BigStageGain, rng);
        if (mental == 0 && gains.Count == 0) return;
        dp.ConditionValue = MathUtil.Clamp(dp.ConditionValue + c.AwakeningConditionBoost, -1.0, 1.0);
        notices.Add(new GrowthNotice(dp, GrowthEventType.BigStage, gains.Count > 0 ? gains[0].Ability : null)
        {
            Gains = gains, Domain = domain, MentalGain = mental,
        });
    }

    private static bool TrySlump(DevelopingPlayer p, IRandomSource rng, GrowthEventCoefficients c,
        List<GrowthNotice> notices)
    {
        if (p.SlumpWeeks > 0) return false;
        if (p.ConditionValue > c.SlumpConditionMax) return false; // 文脈: 不調時のみ
        if (!MathUtil.Chance(c.SlumpWeeklyProb, rng)) return false;

        p.SlumpWeeks = c.SlumpWeeksMin + rng.NextInt(0, c.SlumpWeeksMax - c.SlumpWeeksMin + 1);
        p.ConditionValue = MathUtil.Clamp(p.ConditionValue - c.SlumpConditionDrop, -1.0, 1.0);
        notices.Add(new GrowthNotice(p, GrowthEventType.Slump, null));
        return true;
    }

    private static bool TryPlateau(DevelopingPlayer p, IRandomSource rng, GrowthEventCoefficients c,
        List<GrowthNotice> notices)
    {
        // 文脈: 分野の現在値が才能上限に迫っている場合のみ（「ここが限界だった」）。
        var domain = NearestCapDomain(p, c.PlateauCapProximity);
        if (domain is null) return false;
        if (!MathUtil.Chance(c.PlateauWeeklyProb, rng)) return false;

        switch (domain)
        {
            case "pitching": p.PitchingGrowth *= c.PlateauGrowthFactor; break;
            case "batting": p.BattingGrowth *= c.PlateauGrowthFactor; break;
            default: p.DefenseGrowth *= c.PlateauGrowthFactor; break;
        }
        notices.Add(new GrowthNotice(p, GrowthEventType.Plateau, null));
        return true;
    }

    private static void TryYips(DevelopingPlayer p, IRandomSource rng, GrowthEventCoefficients c,
        List<GrowthNotice> notices)
    {
        if (p.HasYips) return;
        // 文脈: スランプ中のみ。
        if (p.SlumpWeeks == 0) return;
        if (!MathUtil.Chance(c.YipsWeeklyProb, rng)) return;

        p.HasYips = true;
        var k = PitcherRoleResolver.LeansPitcher(p) ? AbilityKind.Control : AbilityKind.ThrowAccuracy;
        p.SetLevel(k, Math.Max(1, p.Level(k) - c.YipsAbilityDrop));
        notices.Add(new GrowthNotice(p, GrowthEventType.Yips, k));
    }

    /// <summary>能力を才能上限まででレベル上昇（上昇イベントが能力を下げることは決してない）。戻り値＝実際に上がった量。</summary>
    private static int Raise(DevelopingPlayer p, AbilityKind k, int amount)
    {
        var before = p.Level(k);
        var target = Math.Min(p.Cap(k), before + amount);
        if (target <= before) return 0;
        p.SetLevel(k, target);
        return target - before;
    }

    /// <summary>才能上限を上げる（上限99）。戻り値＝実際に上がった量。</summary>
    private static int LiftCap(DevelopingPlayer p, AbilityKind k, int amount)
    {
        var before = p.Cap(k);
        var after = Math.Min(99, before + amount);
        if (after <= before) return 0;
        p.SetCap(k, after);
        return after - before;
    }

    /// <summary><paramref name="kinds"/> から別々の能力を <paramref name="count"/> 個選んで上げる。上がった分だけ返す。</summary>
    private static List<AbilityGain> RaiseDistinct(DevelopingPlayer p, AbilityKind[] kinds, int count, int amount,
        IRandomSource rng)
    {
        var pool = new List<AbilityKind>(kinds);
        var gains = new List<AbilityGain>(count);
        for (var i = 0; i < count && pool.Count > 0; i++)
        {
            var idx = rng.NextInt(0, pool.Count);
            var k = pool[idx];
            pool.RemoveAt(idx);
            var g = Raise(p, k, amount);
            if (g > 0) gains.Add(new AbilityGain(k, g));
        }
        return gains;
    }

    private static readonly AbilityKind[] BattingKinds = { AbilityKind.Contact, AbilityKind.Power, AbilityKind.Discipline };
    private static readonly AbilityKind[] DefenseKinds =
        { AbilityKind.Fielding, AbilityKind.Catching, AbilityKind.ThrowAccuracy, AbilityKind.ArmStrength };
    private static readonly AbilityKind[] RunningKinds = { AbilityKind.Speed, AbilityKind.Steal, AbilityKind.Baserunning };

    private static readonly PitchType[] BreakingTypes =
    {
        PitchType.TwoSeam, PitchType.Cutter, PitchType.Slider, PitchType.Curve,
        PitchType.Fork, PitchType.Changeup, PitchType.Shuuto, PitchType.Sinker,
    };
    private const int PitchOffsetMax = 20;   // ProspectGenerator のオフセットの範囲（±20）と同じ

    /// <summary>分野の主要能力（覚醒・大覚醒・大舞台で上がる候補）。</summary>
    internal static AbilityKind[] KindsOf(GrowthDomain d) => d switch
    {
        GrowthDomain.Pitching => AbilityKinds.Pitching,
        GrowthDomain.Defense => DefenseKinds,
        GrowthDomain.Running => RunningKinds,
        _ => BattingKinds,
    };

    private static double GrowthOf(DevelopingPlayer p, GrowthDomain d) => d switch
    {
        GrowthDomain.Pitching => p.PitchingGrowth,
        GrowthDomain.Defense => p.DefenseGrowth,
        _ => p.BattingGrowth,   // 走塁は打撃分野の伸びしろに紐づく（DevelopingPlayer.GrowthMultiplier と同じ）
    };

    private static void SetGrowth(DevelopingPlayer p, GrowthDomain d, double v)
    {
        switch (d)
        {
            case GrowthDomain.Pitching: p.PitchingGrowth = v; break;
            case GrowthDomain.Defense: p.DefenseGrowth = v; break;
            default: p.BattingGrowth = v; break;
        }
    }

    /// <summary>
    /// 覚醒の起きうる分野＝伸びしろが閾値以上で、まだ上がる余地（上限まで空き）がある分野。
    /// 投球は投手寄りの選手だけ（従来の覚醒と同じ）。
    /// </summary>
    private static List<GrowthDomain> EligibleDomains(DevelopingPlayer p, double threshold)
    {
        var list = new List<GrowthDomain>(4);
        void Try(GrowthDomain d)
        {
            if (GrowthOf(p, d) < threshold) return;
            foreach (var k in KindsOf(d))
                if (p.Level(k) < p.Cap(k)) { list.Add(d); return; }
        }
        if (PitcherRoleResolver.LeansPitcher(p)) Try(GrowthDomain.Pitching);
        Try(GrowthDomain.Batting);
        Try(GrowthDomain.Defense);
        Try(GrowthDomain.Running);
        return list;
    }

    /// <summary>才能上限に迫っている分野（伸び悩みの文脈）。無ければ null。</summary>
    private static string? NearestCapDomain(DevelopingPlayer p, double proximity)
    {
        double Gap(AbilityKind[] kinds)
        {
            var sum = 0.0;
            foreach (var k in kinds) sum += p.Cap(k) - p.Level(k);
            return sum / kinds.Length;
        }
        if (PitcherRoleResolver.LeansPitcher(p) && Gap(AbilityKinds.Pitching) <= proximity) return "pitching";
        if (Gap(new[] { AbilityKind.Contact, AbilityKind.Power, AbilityKind.Discipline }) <= proximity) return "batting";
        if (Gap(new[] { AbilityKind.Fielding, AbilityKind.Catching, AbilityKind.ThrowAccuracy }) <= proximity) return "defense";
        return null;
    }
}
