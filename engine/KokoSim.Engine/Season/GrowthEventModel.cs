using System;
using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Season;

/// <summary>選手成長イベントの種別（設計書03 §5.5）。上昇はレアな福音、下降は乗り越えられる試練。</summary>
public enum GrowthEventType
{
    Awakening,     // 覚醒: 分野の能力が一段跳ね上がる（兆しの上に起きる）
    Breakthrough,  // フォーム開眼/コツを掴む: 特定能力がぐっと伸びる
    Slump,         // スランプ（一時的）: 一定期間の能力ダウン。立て直せる
    Plateau,       // 伸び悩み（恒久的）: 分野の伸びしろが頭打ちに（「ここが限界だった」）
    Yips,          // イップス（限定的・恒久寄り）: 送球/制球の不調。克服の道を残す
    YipsOvercome,  // イップス克服
}

/// <summary>発火した成長イベント（UIフィード・検証用の観測データ）。</summary>
public sealed record GrowthNotice(DevelopingPlayer Player, GrowthEventType Type, AbilityKind? Ability);

/// <summary>成長イベントの係数（設計書03 §5.5）。🟡覚醒率・恒久ダウン率は設計書07で体験を壊さない範囲に調整。</summary>
public sealed record GrowthEventCoefficients
{
    // 上昇（福音・レア）
    public double AwakeningWeeklyProb { get; init; } = 0.0015;
    /// <summary>覚醒の条件: 分野伸びしろがこれ以上（完全ランダムにしない=「兆し」）。</summary>
    public double AwakeningGrowthThreshold { get; init; } = 1.15;
    /// <summary>覚醒の条件: 調子がこれ以上（好調持続の上に起きる）。</summary>
    public double AwakeningConditionMin { get; init; } = 0.2;
    public int AwakeningGain { get; init; } = 4;

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
                var k = p.IsPitcher ? AbilityKind.Control : AbilityKind.ThrowAccuracy;
                Raise(p, k, c.YipsAbilityDrop); // 落ちた分を取り返す
                notices.Add(new GrowthNotice(p, GrowthEventType.YipsOvercome, k));
                continue;
            }

            // スランプ進行（回復）。
            if (p.SlumpWeeks > 0) p.SlumpWeeks--;

            // --- 上昇イベント（福音・レア） ---
            if (TryAwakening(p, rng, c, notices)) continue;
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
        var domain = BestGrowthDomain(p);
        if (domain.Multiplier < c.AwakeningGrowthThreshold) return false;
        if (!MathUtil.Chance(c.AwakeningWeeklyProb, rng)) return false;

        // 分野の主要能力から2つを一段跳ね上げる（才能上限は尊重）。
        var kinds = domain.Kinds;
        for (var i = 0; i < 2; i++)
        {
            Raise(p, kinds[rng.NextInt(0, kinds.Length)], c.AwakeningGain);
        }
        notices.Add(new GrowthNotice(p, GrowthEventType.Awakening, null));
        return true;
    }

    private static bool TryBreakthrough(DevelopingPlayer p, IRandomSource rng, GrowthEventCoefficients c,
        List<GrowthNotice> notices)
    {
        if (!MathUtil.Chance(c.BreakthroughWeeklyProb, rng)) return false;
        var kinds = p.IsPitcher ? AbilityKinds.Pitching : AbilityKinds.Batting;
        var k = kinds[rng.NextInt(0, kinds.Length)];
        Raise(p, k, c.BreakthroughGain);
        notices.Add(new GrowthNotice(p, GrowthEventType.Breakthrough, k));
        return true;
    }

    private static bool TrySlump(DevelopingPlayer p, IRandomSource rng, GrowthEventCoefficients c,
        List<GrowthNotice> notices)
    {
        if (p.SlumpWeeks > 0) return false;
        if (p.ConditionValue > c.SlumpConditionMax) return false; // 文脈: 不調時のみ
        if (!MathUtil.Chance(c.SlumpWeeklyProb, rng)) return false;

        p.SlumpWeeks = c.SlumpWeeksMin + rng.NextInt(0, c.SlumpWeeksMax - c.SlumpWeeksMin + 1);
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
        var k = p.IsPitcher ? AbilityKind.Control : AbilityKind.ThrowAccuracy;
        p.SetLevel(k, Math.Max(1, p.Level(k) - c.YipsAbilityDrop));
        notices.Add(new GrowthNotice(p, GrowthEventType.Yips, k));
    }

    /// <summary>能力を才能上限まででレベル上昇（上昇イベントが能力を下げることは決してない）。</summary>
    private static void Raise(DevelopingPlayer p, AbilityKind k, int amount)
    {
        var target = Math.Min(p.Cap(k), p.Level(k) + amount);
        if (target > p.Level(k)) p.SetLevel(k, target);
    }

    private static (double Multiplier, AbilityKind[] Kinds) BestGrowthDomain(DevelopingPlayer p)
    {
        if (p.IsPitcher || p.PitchingGrowth >= p.BattingGrowth && p.PitchingGrowth >= p.DefenseGrowth)
        {
            if (p.IsPitcher) return (p.PitchingGrowth, AbilityKinds.Pitching);
        }
        return p.BattingGrowth >= p.DefenseGrowth
            ? (p.BattingGrowth, new[] { AbilityKind.Contact, AbilityKind.Power, AbilityKind.Discipline })
            : (p.DefenseGrowth, new[] { AbilityKind.Fielding, AbilityKind.Catching, AbilityKind.ThrowAccuracy });
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
        if (p.IsPitcher && Gap(AbilityKinds.Pitching) <= proximity) return "pitching";
        if (Gap(new[] { AbilityKind.Contact, AbilityKind.Power, AbilityKind.Discipline }) <= proximity) return "batting";
        if (Gap(new[] { AbilityKind.Fielding, AbilityKind.Catching, AbilityKind.ThrowAccuracy }) <= proximity) return "defense";
        return null;
    }
}
