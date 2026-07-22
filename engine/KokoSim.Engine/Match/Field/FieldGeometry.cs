using KokoSim.Engine.Core;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Field;

/// <summary>
/// 守備位置の役割。<see cref="DesignatedHitter"/> は打撃成績の表示専用（設計書09, issue #70）で、
/// 実際の守備には就かない＝<see cref="Fielder"/> や守備配置・2D俯瞰には現れない。
/// 選手の本来の守備適性計算（<see cref="Season.DevelopingPlayer.Aptitude"/>）には使わない。
/// </summary>
public enum FieldPosition
{
    Pitcher,
    Catcher,
    FirstBase,
    SecondBase,
    ThirdBase,
    Shortstop,
    LeftField,
    CenterField,
    RightField,
    DesignatedHitter,
}

/// <summary>1人の野手（守備位置＋能力）。</summary>
public sealed record Fielder(FieldPosition Position, Vector3D Location, FielderAttributes Attributes);

/// <summary>
/// 球場・守備配置の幾何（設計書13-stadiums ①物理層）。
/// 座標: 本塁を原点、+Z=センター方向、+X=一塁/右翼側、-X=三塁/左翼側、Y=高さ。
/// 方位角 φ は +Z から測り +X 方向を正（フェアは概ね ±45°）。
/// </summary>
public sealed record FieldGeometry
{
    /// <summary>中堅（センター）フェンスまでの距離 [m]。</summary>
    public double CenterFenceM { get; init; } = 118.0;

    /// <summary>左翼（三塁側=−X）ポールのフェンス距離 [m]。</summary>
    public double LeftFenceM { get; init; } = 95.0;

    /// <summary>右翼（一塁側=+X）ポールのフェンス距離 [m]。左右同値なら対称球場。</summary>
    public double RightFenceM { get; init; } = 95.0;

    /// <summary>両翼の代表値（左右平均）。旧 LineFenceM 互換の読み取り用。</summary>
    public double LineFenceM => (LeftFenceM + RightFenceM) / 2.0;

    public double FenceHeightM { get; init; } = 4.0;

    /// <summary>塁間27.43m（90ft）。一塁は45°右。</summary>
    public double BaseDistanceM { get; init; } = 27.43;

    /// <summary>
    /// 方位角 φ [rad] のフェンスまでの距離。φ=0 で Center、φ=±45°(=±π/4) で該当ポール。
    /// φ&gt;0（+X=右翼側）は Right、φ&lt;0（−X=左翼側）は Left を用い、cos(2φ) で中堅と補間。
    /// 左右同値のとき従来式 R = Line + (Center−Line)·cos(2φ) と一致する。
    /// </summary>
    public double FenceDistance(double bearingRad)
    {
        var lineFence = bearingRad >= 0 ? RightFenceM : LeftFenceM;
        return lineFence + (CenterFenceM - lineFence) * Math.Cos(2.0 * bearingRad);
    }

    public Vector3D FirstBase => new(BaseDistanceM * Math.Sin(Math.PI / 4), 0, BaseDistanceM * Math.Cos(Math.PI / 4));
    public Vector3D SecondBase => new(0, 0, BaseDistanceM * Math.Sqrt(2));
    public Vector3D ThirdBase => new(-BaseDistanceM * Math.Sin(Math.PI / 4), 0, BaseDistanceM * Math.Cos(Math.PI / 4));

    private static readonly (FieldPosition Pos, Vector3D Loc)[] Positions =
    {
        (FieldPosition.Pitcher, new Vector3D(0, 0, 18.44)),
        (FieldPosition.Catcher, new Vector3D(0, 0, -1.0)),
        (FieldPosition.FirstBase, new Vector3D(17.0, 0, 24.0)),
        (FieldPosition.SecondBase, new Vector3D(9.0, 0, 40.0)),
        (FieldPosition.Shortstop, new Vector3D(-9.0, 0, 40.0)),
        (FieldPosition.ThirdBase, new Vector3D(-17.0, 0, 24.0)),
        (FieldPosition.LeftField, new Vector3D(-37.6, 0, 70.6)),
        (FieldPosition.CenterField, new Vector3D(0, 0, 82.0)),
        (FieldPosition.RightField, new Vector3D(37.6, 0, 70.6)),
    };

    /// <summary>標準守備配置（全員 league-average, もしくは指定能力）。</summary>
    public IReadOnlyList<Fielder> StandardAlignment(FielderAttributes? attributes = null)
    {
        var a = attributes ?? FielderAttributes.LeagueAverage;
        var list = new List<Fielder>(Positions.Length);
        foreach (var (pos, loc) in Positions)
        {
            list.Add(new Fielder(pos, loc, a));
        }
        return list;
    }

    /// <summary>守備位置ごとの能力を指定して配置を作る（各チームの守備陣を反映）。</summary>
    public IReadOnlyList<Fielder> AlignmentFrom(IReadOnlyDictionary<FieldPosition, FielderAttributes> byPosition)
    {
        var list = new List<Fielder>(Positions.Length);
        foreach (var (pos, loc) in Positions)
        {
            var a = byPosition.TryGetValue(pos, out var attr) ? attr : FielderAttributes.LeagueAverage;
            list.Add(new Fielder(pos, loc, a));
        }
        return list;
    }
}
