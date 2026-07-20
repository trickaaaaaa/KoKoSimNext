namespace KokoSim.Engine.Match.Pitching;

/// <summary>
/// マウンド〜本塁の幾何。投球板〜本塁は18.44mだが、投手のリリースは前方(エクステンション)へ出るため、
/// 実効的なリリース〜本塁距離はそれより短い。弾道計算はこの実効距離で行う。
/// </summary>
public sealed record MoundGeometry
{
    /// <summary>リリース点から本塁到達までの水平距離[m]。18.44m − エクステンション約1.7m ≒ 16.74m。</summary>
    public double ReleaseToPlateDistanceM { get; init; } = 16.74;
}
