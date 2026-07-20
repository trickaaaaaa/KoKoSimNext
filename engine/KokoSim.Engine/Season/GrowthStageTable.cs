namespace KokoSim.Engine.Season;

/// <summary>成長タイプ（隠しパラメータ）。</summary>
public enum GrowthType
{
    Early,     // 早熟
    Standard,  // 標準
    Late,      // 晩成
}

/// <summary>
/// 成長段階係数（設計書02 §5.2）。半期単位: [1年前半,1年後半,2年前半,2年後半,3年(4〜7月)]。
/// 在籍月数[6,6,6,6,4]で加重した生涯獲得量が 早熟28.8/標準29.2/晩成31.2 になる（＝ほぼ等価, 晩成わずかに上）。
/// </summary>
public sealed record GrowthStageTable
{
    /// <summary>各半期の在籍月数。</summary>
    public IReadOnlyList<double> StageMonths { get; init; } = new double[] { 6, 6, 6, 6, 4 };

    public IReadOnlyList<double> Early { get; init; } = new double[] { 1.4, 1.2, 1.0, 0.8, 0.6 };
    public IReadOnlyList<double> Standard { get; init; } = new double[] { 1.0, 1.0, 1.1, 1.1, 1.0 };
    public IReadOnlyList<double> Late { get; init; } = new double[] { 0.6, 0.8, 1.1, 1.5, 1.8 };

    public IReadOnlyList<double> For(GrowthType type) => type switch
    {
        GrowthType.Early => Early,
        GrowthType.Late => Late,
        _ => Standard,
    };

    /// <summary>指定半期(0〜4)の成長段階係数。</summary>
    public double Coefficient(GrowthType type, int stageIndex) => For(type)[stageIndex];

    /// <summary>在籍月数で加重した生涯獲得量（設計値: 早熟28.8/標準29.2/晩成31.2）。</summary>
    public double LifetimeTotal(GrowthType type)
    {
        var coefs = For(type);
        var sum = 0.0;
        for (var i = 0; i < StageMonths.Count; i++)
        {
            sum += StageMonths[i] * coefs[i];
        }
        return sum;
    }
}
