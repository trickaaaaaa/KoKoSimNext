namespace KokoSim.Engine.Match.Batting;

/// <summary>
/// 表示層→物理層の打撃系変換・確率係数（不変条件#1の集約点、YAML駆動）。
/// すべて初期値であり、ヘッドレス1万打席の統計回帰で調整する（不変条件#5）。
/// </summary>
public sealed record BattingCoefficients
{
    // --- 配球（狙いの散らばり） ---
    /// <summary>投手が狙う位置のゾーン中心からの散布σ（横）[m]。</summary>
    public double AimSigmaXMeters { get; init; } = 0.10;
    /// <summary>投手が狙う位置のゾーン中心からの散布σ（縦）[m]。</summary>
    public double AimSigmaYMeters { get; init; } = 0.155;

    // --- 打者判断（スイング/見逃し） ---
    /// <summary>ゾーン内球へのスイング率の基準。</summary>
    public double ZoneSwingBase { get; init; } = 0.68;
    /// <summary>ゾーン外球への釣られ率（追いかけ）の基準。</summary>
    public double ChaseBase { get; init; } = 0.28;
    /// <summary>選球眼が追いかけ率を下げる強さ（Discipline−50 あたり）。</summary>
    public double ChaseDisciplineSlope { get; init; } = 0.0045;
    /// <summary>選球眼がゾーン内スイング率を上げる強さ。</summary>
    public double ZoneSwingDisciplineSlope { get; init; } = 0.0015;
    /// <summary>
    /// 誘発変化合成量[m]（設計書15 Phase E-3）あたりのスイング確率補正。単一係数を符号だけ反転して対称に効く
    /// （ゾーン外+＝釣られる、ゾーン内−＝見誤って見送る）。「見え方と実軌道のズレ」の代理指標。
    /// </summary>
    public double ChaseBreakSlope { get; init; } = 0.15;
    /// <summary>
    /// ゾーン外距離[m]（設計書15 Phase E-3, <see cref="KokoSim.Engine.Match.Field.StrikeZone.DistanceOutsideM"/>）
    /// あたりのスイング確率減衰。ChaseBreakSlope の一定加算が「大外れでも変化量だけで釣れる」不自然を作らないための
    /// ガード（ゾーン内は距離=0で常に無効）。
    /// </summary>
    public double ChaseDistanceSlope { get; init; } = 0.6;

    // --- コンタクト（空振り/ファウル/フェア） ---
    /// <summary>空振り率のロジスティック基準（球威=打者互角時の空振り対数オッズ）。</summary>
    public double WhiffIntercept { get; init; } = -1.68;
    /// <summary>ミートが空振りを下げる強さ。</summary>
    public double WhiffContactSlope { get; init; } = 0.030;
    /// <summary>ゾーン外スイング時の空振り加算（対数オッズ）。</summary>
    public double WhiffOutOfZonePenalty { get; init; } = 0.85;
    /// <summary>
    /// 誘発変化量の合成量[m]（設計書15 Phase E-2, <see cref="Pitching.PitchTrajectoryFeatures.BreakMagnitudeM"/>）
    /// あたりの空振り加算（対数オッズ）。球種ランク(PitchRank)は Sharpness→rpm→変化量の経路で既に弾道へ
    /// 織り込み済みのため、旧 StuffPerPitchRank（球種ランク直参照）を本項へ完全置換した（二重計上を避ける）。
    /// </summary>
    public double WhiffBreakSlope { get; init; } = 1.5;
    /// <summary>コンタクトのうちファウルになる割合。</summary>
    public double FoulShare { get; init; } = 0.40;

    // --- 球威（stuff）の算出 ---
    /// <summary>基準球速[km/h]（この球速で stuff=0 寄与）。</summary>
    public double StuffBaseVelocityKmh { get; init; } = 130.0;
    /// <summary>球速1km/hあたりの stuff 寄与（対数オッズ）。</summary>
    public double StuffPerKmh { get; init; } = 0.020;

    // --- 打球生成 ---
    /// <summary>打球初速上限[km/h] の切片（パワー0相当）。設計書02は 100 + P×0.6 だが伸び不足のため係数化。</summary>
    public double ExitVeloInterceptKmh { get; init; } = 125.0;
    /// <summary>打球初速上限[km/h] のパワー1あたり係数。</summary>
    public double ExitVeloPerPower { get; init; } = 0.82;

    /// <summary>コンタクト品質(0〜1)の平均（互角時）。</summary>
    public double QualityMean { get; init; } = 0.62;
    /// <summary>コンタクト品質へのミート寄与((Contact−50)あたり)。</summary>
    public double QualityContactSlope { get; init; } = 0.0020;
    /// <summary>コンタクト品質の標準偏差。</summary>
    public double QualitySigma { get; init; } = 0.17;
    /// <summary>打球角度: 弾道1のときの平均角[deg]。</summary>
    public double LaunchAngleAtLt1 { get; init; } = -5.0;
    /// <summary>打球角度: 弾道100のときの平均角[deg]。</summary>
    public double LaunchAngleAtLt100 { get; init; } = 26.0;
    /// <summary>打球角度の標準偏差[deg]。</summary>
    public double LaunchAngleSigma { get; init; } = 12.0;
    /// <summary>方位角（引っ張り/流し）の標準偏差[deg]。</summary>
    public double BearingSigma { get; init; } = 20.0;
    /// <summary>芯を外すと初速が落ちる度合い（品質1で満初速, 0で係数分だけ残る）。</summary>
    public double MinQualityVeloFactor { get; init; } = 0.55;

    /// <summary>打球の平均角[deg]を弾道値から線形補間。</summary>
    public double MeanLaunchAngle(int launchTendency)
        => LaunchAngleAtLt1 + (LaunchAngleAtLt100 - LaunchAngleAtLt1) * (launchTendency - 1) / 99.0;
}
