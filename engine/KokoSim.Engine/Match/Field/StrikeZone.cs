namespace KokoSim.Engine.Match.Field;

/// <summary>
/// ストライクゾーン（本塁面の矩形）。本塁幅17インチ=0.432m、高さは膝〜胸元。
/// 座標は本塁面での X=左右[m]・Y=高さ[m]。
/// </summary>
public sealed record StrikeZone
{
    public double HalfWidthM { get; init; } = 0.216;   // 0.432 / 2
    public double BottomM { get; init; } = 0.50;        // 膝下
    public double TopM { get; init; } = 1.05;           // 胸元

    public bool Contains(double x, double y)
        => x >= -HalfWidthM && x <= HalfWidthM && y >= BottomM && y <= TopM;

    public double CenterY => (TopM + BottomM) / 2.0;

    public double HalfHeightM => (TopM - BottomM) / 2.0;

    /// <summary>
    /// ゾーン内の「縁への近さ」（無次元 [0,1]、issue #403）。中心=0・縁上=1。
    /// 横 |x|/HalfWidth と縦 |y−CenterY|/HalfHeight の大きい方＝最も近い縁への近さ。
    /// 「隅のいい球は見逃されやすい」をゾーン内スイング率の減衰（<see cref="Batting.BattingCoefficients.ZoneEdgeSwingPenalty"/>）
    /// に渡すための入力。ゾーン外の座標では1で頭打ち（呼び出し側はゾーン内でのみ使う想定）。
    /// </summary>
    public double EdgeProximity(double x, double y)
    {
        var px = System.Math.Abs(x) / HalfWidthM;
        var py = System.Math.Abs(y - CenterY) / HalfHeightM;
        var p = System.Math.Max(px, py);
        return p > 1.0 ? 1.0 : p;
    }

    /// <summary>
    /// ゾーン外への距離[m]（設計書15 Phase E-3）。ゾーン内なら0、外なら矩形の最近傍点までのユークリッド距離。
    /// 「ゾーンから遠いほど振らない」という既存の想定を、弾道由来のチェイス補正が上書きしないためのガード入力。
    /// </summary>
    public double DistanceOutsideM(double x, double y)
    {
        var dx = System.Math.Max(0.0, System.Math.Max(-HalfWidthM - x, x - HalfWidthM));
        var dy = System.Math.Max(0.0, System.Math.Max(BottomM - y, y - TopM));
        return System.Math.Sqrt(dx * dx + dy * dy);
    }
}
