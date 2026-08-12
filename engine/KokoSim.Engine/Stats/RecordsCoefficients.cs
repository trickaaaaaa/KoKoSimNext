namespace KokoSim.Engine.Stats;

/// <summary>
/// 記録室の規定打席・規定投球回（issue #600・不変条件#4＝<c>data/coefficients.yaml</c> の <c>records:</c> と同値）。
///
/// <para>高校野球は試合数が少なく、閾値が無いと「1打数1安打＝打率1.000」が校歴代1位に出る。
/// NPB と同じく<b>チームの試合数に比例</b>させる（絶対値で置くと、大会が短い年と長い年で意味が変わる）。</para>
///
/// <para>基準になる「チームの試合数」のカウンタが無いので、そのスコープ内の<b>選手の最大出場試合数</b>で
/// 代用する（全試合に出る主力が1人は居る）。<see cref="RecordRoom.QualificationOf"/> が唯一の入口。</para>
///
/// <para>表示専用の閾値＝乱数も物理層も通らないので、値を変えても統計帯は動かない。</para>
/// </summary>
public sealed class RecordsCoefficients
{
    /// <summary>規定打席＝チーム試合数×この係数（NPB は 3.1。高校は1試合の打席が少ないので 2.0）。</summary>
    public double QualifiedPlateAppearancesPerGame { get; set; } = 2.0;

    /// <summary>規定投球回＝チーム試合数×この係数[アウト]。3.0＝1試合あたり1回（NPB と同じ考え方）。</summary>
    public double QualifiedOutsPerGame { get; set; } = 3.0;
}
