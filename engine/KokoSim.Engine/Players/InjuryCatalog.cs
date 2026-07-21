using KokoSim.Engine.Core;

namespace KokoSim.Engine.Players;

/// <summary>傷病の種類が取りうる部位と、その抽選重み（data/injuries.yaml 由来）。</summary>
public sealed record InjurySiteWeight(InjurySite Site, double Weight);

/// <summary>カタログから引いた1件の怪我（種類・部位・段階＋回復週の倍率）。全治週の確定は Season 層。</summary>
public readonly record struct InjuryDraw(
    InjuryType Type, InjurySite Site, InjurySeverity Severity, double RecoveryWeekFactor);

/// <summary>
/// 傷病1種類の定義（設計書03 §3.5, data/injuries.yaml 由来）。
/// 「取りうる部位」「段階分布」「回復週の倍率」「場面別の抽選重み」をすべてデータ側に持つ（不変条件#4）。
/// </summary>
public sealed record InjuryTypeEntry
{
    public required InjuryType Id { get; init; }
    /// <summary>表示名（日本語）。engine はこの文字列を判定に使わない（表示専用）。</summary>
    public required string Name { get; init; }
    /// <summary>取りうる部位と重み（空なら全部位一様）。</summary>
    public IReadOnlyList<InjurySiteWeight> Sites { get; init; } = Array.Empty<InjurySiteWeight>();
    /// <summary>段階分布（軽度/中度、残りが重度）。</summary>
    public double MinorShare { get; init; } = 0.70;
    public double ModerateShare { get; init; } = 0.25;
    /// <summary>段階ごとの基準回復週に掛ける倍率（骨折は長引く、打撲は短い）。</summary>
    public double RecoveryWeekFactor { get; init; } = 1.0;
    /// <summary>場面ごとの抽選重み（未掲載の場面は 0＝その場面では起きない）。</summary>
    public IReadOnlyDictionary<InjuryScene, double> SceneWeights { get; init; }
        = new Dictionary<InjuryScene, double>();

    public double WeightFor(InjuryScene scene)
        => SceneWeights.TryGetValue(scene, out var w) ? Math.Max(0.0, w) : 0.0;
}

/// <summary>
/// 傷病カタログ（設計書03 §3.5, data/injuries.yaml）。種類→部位→段階の一貫した抽選をここが担う。
/// 空カタログ（<see cref="Empty"/>）を渡すと種類なし＝部位一様抽選の従来挙動に落ちる。
/// </summary>
public sealed class InjuryCatalog
{
    /// <summary>種類なし（従来挙動）。テスト・データ未投入時のフォールバック。</summary>
    public static readonly InjuryCatalog Empty = new(Array.Empty<InjuryTypeEntry>());

    /// <summary>
    /// 組み込み既定カタログ。data/injuries.yaml の内容を写したもので、
    /// YAML を読めない実行環境（Unity は engine を DLL 参照するだけで data/ を読まない）の既定値。
    /// 調整は data/injuries.yaml 側で行い、こちらは同期のためだけに触る（InjuryCoefficients と同じ運用）。
    /// </summary>
    public static readonly InjuryCatalog Default = BuildDefault();

    private static InjuryCatalog BuildDefault()
    {
        static InjurySiteWeight S(InjurySite site, double w) => new(site, w);
        static Dictionary<InjuryScene, double> Sc(params (InjuryScene, double)[] xs)
        {
            var d = new Dictionary<InjuryScene, double>(xs.Length);
            foreach (var (k, v) in xs) d[k] = v;
            return d;
        }

        return new InjuryCatalog(new[]
        {
            new InjuryTypeEntry
            {
                Id = InjuryType.Sprain, Name = "捻挫", RecoveryWeekFactor = 0.8,
                MinorShare = 0.80, ModerateShare = 0.17,
                Sites = new[] { S(InjurySite.Ankle, 0.50), S(InjurySite.Knee, 0.25), S(InjurySite.Hand, 0.15), S(InjurySite.Back, 0.10) },
                SceneWeights = Sc((InjuryScene.Weekly, 1.0), (InjuryScene.HomeCollision, 2.0),
                    (InjuryScene.FenceCrash, 1.5), (InjuryScene.Sliding, 4.0)),
            },
            new InjuryTypeEntry
            {
                Id = InjuryType.Strain, Name = "肉離れ", RecoveryWeekFactor = 1.0,
                MinorShare = 0.65, ModerateShare = 0.30,
                Sites = new[] { S(InjurySite.Back, 0.35), S(InjurySite.Knee, 0.25), S(InjurySite.Shoulder, 0.20), S(InjurySite.Elbow, 0.20) },
                SceneWeights = Sc((InjuryScene.Weekly, 1.0), (InjuryScene.Sliding, 3.0), (InjuryScene.Overuse, 1.0)),
            },
            new InjuryTypeEntry
            {
                Id = InjuryType.Fracture, Name = "骨折", RecoveryWeekFactor = 2.0,
                MinorShare = 0.25, ModerateShare = 0.45,
                Sites = new[] { S(InjurySite.Hand, 0.45), S(InjurySite.Ankle, 0.20), S(InjurySite.Elbow, 0.15), S(InjurySite.Shoulder, 0.10), S(InjurySite.Back, 0.10) },
                SceneWeights = Sc((InjuryScene.Weekly, 0.15), (InjuryScene.HitByPitch, 2.0),
                    (InjuryScene.HomeCollision, 1.2), (InjuryScene.FenceCrash, 1.0)),
            },
            new InjuryTypeEntry
            {
                Id = InjuryType.Bruise, Name = "打撲", RecoveryWeekFactor = 0.5,
                MinorShare = 0.90, ModerateShare = 0.09,
                Sites = new[] { S(InjurySite.Hand, 0.30), S(InjurySite.Knee, 0.20), S(InjurySite.Back, 0.20), S(InjurySite.Shoulder, 0.15), S(InjurySite.Elbow, 0.15) },
                SceneWeights = Sc((InjuryScene.Weekly, 0.6), (InjuryScene.HitByPitch, 6.0),
                    (InjuryScene.HomeCollision, 4.0), (InjuryScene.FenceCrash, 4.0), (InjuryScene.Sliding, 2.0)),
            },
            new InjuryTypeEntry
            {
                Id = InjuryType.LigamentTear, Name = "靭帯損傷", RecoveryWeekFactor = 2.5,
                MinorShare = 0.10, ModerateShare = 0.40,
                Sites = new[] { S(InjurySite.Knee, 0.40), S(InjurySite.Elbow, 0.30), S(InjurySite.Ankle, 0.20), S(InjurySite.Shoulder, 0.10) },
                SceneWeights = Sc((InjuryScene.Weekly, 0.10), (InjuryScene.HomeCollision, 1.0),
                    (InjuryScene.FenceCrash, 0.8), (InjuryScene.Sliding, 0.6)),
            },
            new InjuryTypeEntry
            {
                Id = InjuryType.Inflammation, Name = "疲労性炎症", RecoveryWeekFactor = 1.2,
                MinorShare = 0.65, ModerateShare = 0.30,
                Sites = new[] { S(InjurySite.Shoulder, 0.40), S(InjurySite.Elbow, 0.35), S(InjurySite.Back, 0.25) },
                SceneWeights = Sc((InjuryScene.Weekly, 1.2), (InjuryScene.Overuse, 8.0)),
            },
        });
    }

    private readonly Dictionary<InjuryType, InjuryTypeEntry> _byId;

    public InjuryCatalog(IReadOnlyList<InjuryTypeEntry> types)
    {
        Types = types;
        _byId = new Dictionary<InjuryType, InjuryTypeEntry>(types.Count);
        foreach (var t in types) _byId[t.Id] = t;
    }

    public IReadOnlyList<InjuryTypeEntry> Types { get; }
    public bool IsEmpty => Types.Count == 0;

    public InjuryTypeEntry? Find(InjuryType id) => _byId.TryGetValue(id, out var e) ? e : null;

    /// <summary>表示名（未知＝空文字。UI 側で「部位＋段階のみ」に落とす）。</summary>
    public string DisplayName(InjuryType id) => Find(id)?.Name ?? string.Empty;

    /// <summary>
    /// 場面に応じて傷病の種類を重み抽選する。その場面に重みを持つ種類が無ければ null（＝発生させない）。
    /// 重み抽選は必ず1回だけ rng を消費する（呼び出し側の乱数消費数を場面によらず一定に保つ）。
    /// </summary>
    public InjuryTypeEntry? SampleType(InjuryScene scene, IRandomSource rng)
    {
        var total = 0.0;
        foreach (var t in Types) total += t.WeightFor(scene);
        if (total <= 0.0) return null;

        var r = rng.NextDouble() * total;
        var acc = 0.0;
        foreach (var t in Types)
        {
            acc += t.WeightFor(scene);
            if (r < acc) return t;
        }
        return Types[Types.Count - 1];
    }

    /// <summary>
    /// 場面に応じた「種類→部位→段階」の一貫抽選。その場面に重みを持つ種類が無ければ null。
    /// 全治週は段階ごとの基準週（Season の InjuryCoefficients）に <see cref="InjuryDraw.RecoveryWeekFactor"/>
    /// を掛けて決めるので、ここでは週数を持たない（Match 層から Season 層へ依存しないため）。
    /// </summary>
    public InjuryDraw? Draw(InjuryScene scene, IRandomSource rng)
    {
        var type = SampleType(scene, rng);
        if (type is null) return null;
        var site = SampleSite(type, rng);
        var severity = SampleSeverity(rng, type.MinorShare, type.ModerateShare);
        return new InjuryDraw(type.Id, site, severity, type.RecoveryWeekFactor);
    }

    /// <summary>段階の分布抽選（軽度/中度、残りが重度）。必ず rng を1回だけ消費する。</summary>
    public static InjurySeverity SampleSeverity(IRandomSource rng, double minorShare, double moderateShare)
    {
        var r = rng.NextDouble();
        if (r < minorShare) return InjurySeverity.Minor;
        if (r < minorShare + moderateShare) return InjurySeverity.Moderate;
        return InjurySeverity.Severe;
    }

    /// <summary>種類の取りうる部位を重み抽選する（定義が空なら全部位一様）。必ず rng を1回だけ消費する。</summary>
    public static InjurySite SampleSite(InjuryTypeEntry type, IRandomSource rng)
    {
        var total = 0.0;
        foreach (var s in type.Sites) total += Math.Max(0.0, s.Weight);
        if (total <= 0.0)
        {
            var all = (InjurySite[])Enum.GetValues(typeof(InjurySite));
            return all[(int)(rng.NextDouble() * all.Length) % all.Length];
        }

        var r = rng.NextDouble() * total;
        var acc = 0.0;
        foreach (var s in type.Sites)
        {
            acc += Math.Max(0.0, s.Weight);
            if (r < acc) return s.Site;
        }
        return type.Sites[type.Sites.Count - 1].Site;
    }
}
