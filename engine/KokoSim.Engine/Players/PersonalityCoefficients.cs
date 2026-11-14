using System;
using System.Collections.Generic;
using KokoSim.Engine.Core;

namespace KokoSim.Engine.Players;

/// <summary>
/// 生成の「種」になる既存8タイプ1つぶんの表（設計書23 §1.4）。効きの値は持たない＝効きは軸の式
/// （<see cref="PersonalityCoefficients"/>）だけが決める。ここにあるのは出やすさと、軸を引くときの中心（代表値）。
/// </summary>
public sealed record PersonalityProfile
{
    public required Personality Type { get; init; }
    /// <summary>生成重み（Normal は0＝生成対象外）。</summary>
    public double SpawnWeight { get; init; }
    /// <summary>①統率傾向: 生成時の統率傾向平均への偏り（主将向き+/不向き−）。</summary>
    public double LeadershipMeanOffset { get; init; }
    /// <summary>②素直さの代表値（1〜99。生成の中心・旧セーブの移行先）。</summary>
    public int Receptiveness { get; init; } = Personalities.AxisNeutral;
    /// <summary>③勤勉さの代表値。</summary>
    public int Diligence { get; init; } = Personalities.AxisNeutral;
    /// <summary>④試合での姿勢（高い＝献身／低い＝目立ちたがり）の代表値。</summary>
    public int Selflessness { get; init; } = Personalities.AxisNeutral;
}

/// <summary>生成で引いた性格（種タイプ＋②③④の軸）。①統率は精神系の生成と一緒に引く（種タイプのずらしを使う）。</summary>
public readonly record struct PersonalityDraw(Personality SeedType, int Receptiveness, int Diligence, int Selflessness);

/// <summary>
/// 性格の係数（設計書23 §1・YAML駆動 data/coefficients.yaml personalities:）。
///
/// <para>効きは軸の値の<b>片側ごとの一次式</b>: <c>d = (軸 − 50) / 50</c> として、d≥0 は <c>*SlopeHigh</c>、
/// d&lt;0 は <c>*SlopeLow</c> を掛ける（軸50で恒等・端で旧タイプ表の幅）。設計書23 §1.2 の初期値案は片側共通の傾きだったが、
/// ④の犠打（献身で+）とチャンス（目立ちたがりで+）を同じ軸の単一傾きに置くと、旧表の母集団平均
/// （犠打 +0.0089 とチャンス 1.0134 が<b>どちらも正</b>）を同時に満たせない＝旧表は献身側と目立ち側で幅が非対称だった。
/// そこで旧表の両端（例: チャンス 0.97〜1.10）をそのまま片側の傾きにした（OPEN-QUESTIONS Q41）。</para>
/// </summary>
public sealed record PersonalityCoefficients
{
    /// <summary>生成時、種タイプの代表値の周りに②③④を引く正規分布のσ（設計書23 §1.4 σ12案）。</summary>
    public double AxisSd { get; init; } = 12.0;

    /// <summary>自主成長（<see cref="SelfGrowthFactor"/>）の下限・上限。</summary>
    public double FactorMin { get; init; } = 0.75;
    public double FactorMax { get; init; } = 1.25;

    /// <summary>②素直さ→指導の入りやすさ: 素直側（d≥0）の傾き（端で 1.10）。</summary>
    public double ReceptivenessSlopeHigh { get; init; } = 0.10;
    /// <summary>②素直さ: 頑固側（d&lt;0）の傾き（端で 0.88）。</summary>
    public double ReceptivenessSlopeLow { get; init; } = 0.12;
    /// <summary>③勤勉さ→自主成長: コツコツ側の傾き（端で 1.10）。</summary>
    public double DiligenceSlopeHigh { get; init; } = 0.10;
    /// <summary>③勤勉さ: 気分屋側の傾き（端で 0.92）。</summary>
    public double DiligenceSlopeLow { get; init; } = 0.08;
    /// <summary>④姿勢→犠打の成功加算: 献身側の傾き（端で +0.05）。</summary>
    public double BuntSlopeHigh { get; init; } = 0.05;
    /// <summary>④姿勢→犠打: 目立ちたがり側の傾き（端で −0.06）。</summary>
    public double BuntSlopeLow { get; init; } = 0.06;
    /// <summary>④姿勢→チャンスの長打質倍率: 献身側の傾き（端で 0.97。献身ほど1未満）。</summary>
    public double ChanceSlopeHigh { get; init; } = 0.03;
    /// <summary>④姿勢→チャンス: 目立ちたがり側の傾き（端で 1.10）。</summary>
    public double ChanceSlopeLow { get; init; } = 0.10;
    /// <summary>④姿勢→伝令役の得意場面（#716）: 献身は投手へ +d×slope、目立ちたがりは打者へ −d×slope（設計書23 §1.2）。</summary>
    public double MessengerSlope { get; init; } = 0.06;

    public IReadOnlyList<PersonalityProfile> Profiles { get; init; } = DefaultProfiles();

    /// <summary>軸の値→d（−0.98〜+0.98）。</summary>
    public static double D(int axis) => (axis - Personalities.AxisNeutral) / 50.0;

    private static double Piecewise(int axis, double high, double low)
    {
        var d = D(axis);
        return d >= 0 ? d * high : d * low;
    }

    /// <summary>②素直さ→指導の入りやすさ（個別指導・伝令の落ち着き）。50で1。</summary>
    public double CoachingReceptivity(int receptiveness)
        => 1.0 + Piecewise(receptiveness, ReceptivenessSlopeHigh, ReceptivenessSlopeLow);

    /// <summary>③勤勉さ→自主成長（練習効率の個人倍率）。50で1。</summary>
    public double SelfGrowthFactor(int diligence)
        => MathUtil.Clamp(1.0 + Piecewise(diligence, DiligenceSlopeHigh, DiligenceSlopeLow), FactorMin, FactorMax);

    /// <summary>④姿勢→犠打の成功率への加算。50で0。</summary>
    public double BuntSuccessBonus(int selflessness) => Piecewise(selflessness, BuntSlopeHigh, BuntSlopeLow);

    /// <summary>④姿勢→得点圏の強攻時の長打質倍率。50で1。</summary>
    public double ChanceHitFactor(int selflessness) => 1.0 - Piecewise(selflessness, ChanceSlopeHigh, ChanceSlopeLow);

    /// <summary>④姿勢→伝令役の得意場面の加算（投手へ: +d×slope／打者へ: −d×slope）。50で0。</summary>
    public double MessengerBonus(int selflessness, bool toPitcher)
    {
        var v = D(selflessness) * MessengerSlope;
        return toPitcher ? v : -v;
    }

    /// <summary>タイプ→種の表（見つからなければ中立＝代表値50・重み0・ずらし0）。8件の線形探索で十分軽量。</summary>
    public PersonalityProfile Profile(Personality type)
    {
        foreach (var p in Profiles)
        {
            if (p.Type == type) return p;
        }
        return new PersonalityProfile { Type = type };
    }

    /// <summary>重み付きで種タイプを抽選（SpawnWeight&gt;0 のみ対象）。決定論。</summary>
    public Personality Sample(IRandomSource rng)
    {
        var total = 0.0;
        foreach (var p in Profiles) total += p.SpawnWeight;
        if (total <= 0) return Personality.Normal;

        var r = rng.NextDouble() * total;
        foreach (var p in Profiles)
        {
            if (p.SpawnWeight <= 0) continue;
            r -= p.SpawnWeight;
            if (r < 0) return p.Type;
        }
        return Personality.Normal; // 数値誤差の保険（実質到達しない）
    }

    /// <summary>
    /// 生成（設計書23 §1.4・Q36）: 種タイプを重みで1つ引き、その代表値を中心に②③④を正規分布（σ<see cref="AxisSd"/>）で引く。
    /// <paramref name="rng"/> から 1+3 回だけ引く（消費数固定＝決定論）。呼び出し側は専用 Fork を渡す。
    /// </summary>
    public PersonalityDraw SampleAxes(IRandomSource rng)
    {
        var seed = Sample(rng);
        var pp = Profile(seed);
        int Axis(int center) => (int)MathUtil.Clamp(Math.Round(rng.NextGaussian(center, AxisSd)), 1, 99);
        var r = Axis(pp.Receptiveness);
        var d = Axis(pp.Diligence);
        var s = Axis(pp.Selflessness);
        return new PersonalityDraw(seed, r, d, s);
    }

    /// <summary>
    /// 既定の種の表（8タイプ）。data/coefficients.yaml personalities.profiles と一致（issue #402・
    /// CoefficientsDefaultsConsistencyTests）。代表値は旧タイプ表の効き（指導・自主成長・犠打・チャンス）を
    /// 軸の式で再現する位置に置き、σ12 で引いた母集団の平均効果が旧表の重み付き平均
    /// （指導 1.0085／自主成長 1.0052／犠打 +0.0089／チャンス 1.0134）と許容差内に入るよう合わせた
    /// （PersonalityAxesTests の母集団テストが固定）。各値は設計書23 §1.3 のマスの側にある。
    /// </summary>
    private static IReadOnlyList<PersonalityProfile> DefaultProfiles() => new PersonalityProfile[]
    {
        //                                        w     ①Lead  ②素直 ③勤勉 ④献身
        new() { Type = Personality.HotBlood,     SpawnWeight = 1.0, LeadershipMeanOffset =  14, Receptiveness = 45, Diligence = 85, Selflessness = 95 },
        new() { Type = Personality.Hardworker,   SpawnWeight = 1.4, LeadershipMeanOffset =   4, Receptiveness = 80, Diligence = 92, Selflessness = 95 },
        new() { Type = Personality.Genius,       SpawnWeight = 0.5, LeadershipMeanOffset =   0, Receptiveness =  5, Diligence = 95, Selflessness =  5 },
        new() { Type = Personality.HonorStudent, SpawnWeight = 1.0, LeadershipMeanOffset =  12, Receptiveness = 95, Diligence = 52, Selflessness = 95 },
        new() { Type = Personality.MyPace,       SpawnWeight = 1.4, LeadershipMeanOffset =   0, Receptiveness = 30, Diligence = 28, Selflessness = 55 },
        new() { Type = Personality.MoodMaker,    SpawnWeight = 1.2, LeadershipMeanOffset =   4, Receptiveness = 58, Diligence = 12, Selflessness =  5 },
        new() { Type = Personality.LoneWolf,     SpawnWeight = 0.8, LeadershipMeanOffset = -16, Receptiveness = 18, Diligence = 42, Selflessness =  8 },
        new() { Type = Personality.Introvert,    SpawnWeight = 1.2, LeadershipMeanOffset = -14, Receptiveness = 88, Diligence = 28, Selflessness = 90 },
    };
}
