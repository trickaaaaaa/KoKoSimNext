namespace KokoSim.Engine.Match.Field;

/// <summary>
/// メンバー画面の球場図（俯瞰）で使う固定幾何と標準守備位置（表示専用・純データ・検算可能）。
/// 座標: 本塁=原点[m]、+Y=二塁方向、+X=一塁側。
/// ※ 実試合の守備解決は <see cref="FieldGeometry"/>（3D・+Z=センター）を用いる別系統。ここは表示のみ。
/// </summary>
public static class MemberFieldLayout
{
    public const double BaseDistanceM = FieldDiagramGeometry.BaseDistanceM; // 塁間（共通ジオメトリ）
    // 内野ダート境界は共通ソース FieldDiagramGeometry.InfieldDirtRadius（マウンド中心弧）を使うこと。
    // 旧 InfieldDirtRadiusM（本塁基準29m固定）は二塁が土の外に浮く誤りだったため廃止（2026-07-20）。
    public const double WingFenceM = 98.0;         // 両翼フェンス（メンバー画面＝端見切れ回避で詰めた framing）
    public const double CenterFenceM = 122.0;      // 中堅フェンス

    // 内野の固定5点[m]（本塁・一・二・三・マウンド）。地理は共通ソース FieldDiagramGeometry を参照。
    public static readonly (double X, double Y) Home = FieldDiagramGeometry.Home;
    public static readonly (double X, double Y) First = FieldDiagramGeometry.First;
    public static readonly (double X, double Y) Second = FieldDiagramGeometry.Second;
    public static readonly (double X, double Y) Third = FieldDiagramGeometry.Third;
    public static readonly (double X, double Y) Mound = FieldDiagramGeometry.Mound;

    /// <summary>背番号1〜9の標準守備位置[m]。表示チップの立ち位置に使う。</summary>
    public static (double X, double Y) DefensivePosition(int uniformNumber) => uniformNumber switch
    {
        1 => (0.0, 18.4),    // 投（マウンド）
        2 => (0.0, -1.5),    // 捕（本塁直後・ファウル地帯）
        3 => (15.0, 21.0),   // 一（塁の左後方・ライン内側）
        4 => (11.0, 42.0),   // 二（二塁ベースより深く・右へ開く）
        5 => (-15.0, 21.0),  // 三（塁の右後方・ライン内側）
        6 => (-11.0, 42.0),  // 遊（二塁ベースより深く・左へ開く）
        7 => (-22.0, 78.0),  // 左（フェンスの2/3より奥・端の見切れ回避で横の開きは控えめ）
        8 => (0.0, 82.0),    // 中
        9 => (22.0, 78.0),   // 右
        _ => (0.0, 0.0),
    };

    /// <summary>球場座標の方位角[rad]（+Y=中堅を0、+X側を正）。</summary>
    public static double BearingRad((double X, double Y) p) => FieldDiagramGeometry.BearingRad(p);

    /// <summary>方位角[rad]の外野フェンスまでの距離[m]。式は共通ソース、両翼/中堅はメンバー画面の framing 値。</summary>
    public static double FenceRadiusM(double bearingRad) =>
        FieldDiagramGeometry.FenceRadius(bearingRad, WingFenceM, CenterFenceM);

    public static double Distance((double X, double Y) a, (double X, double Y) b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return System.Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>点がファウルラインの内側（フェア側）にある距離[m]。一塁線 y=x／三塁線 y=−x からの垂線距離。</summary>
    public static double InsideFoulLineM((double X, double Y) p, bool firstBaseSide)
    {
        // 一塁線 x−y=0、三塁線 x+y=0。フェア側（|x|<y）で正。
        return (firstBaseSide ? p.Y - p.X : p.Y + p.X) / System.Math.Sqrt(2.0);
    }
}
