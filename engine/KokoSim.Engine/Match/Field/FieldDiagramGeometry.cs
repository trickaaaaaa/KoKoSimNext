namespace KokoSim.Engine.Match.Field;

/// <summary>
/// 球場俯瞰図の「地理（座標）」の単一ソース（表示専用・純データ・検算可能）。
/// メンバー画面の図（<see cref="MemberFieldLayout"/>）と試合2D俯瞰再生（Playback / Match2DPlaybackElement）が
/// **同じ座標定義**を参照する。設計方針: **図法（投影）は各画面で別、地理（座標）は共通**。
///
/// 座標: 本塁=原点[m]、+Y=二塁方向、+X=一塁側（mock-match-2d-view.html と同一規約）。
/// ※ 実試合の守備解決は <see cref="FieldGeometry"/>（3D・+Z=センター）を用いる別系統。ここは表示のみ。
///
/// 両者で値が異なるもの（両翼/中堅フェンス距離・守備位置・ダート半径）は用途別（メンバー画面は
/// 端の見切れ回避で詰めた配置、試合は甲子園相当）なので、ここでは**共通する座標と式のみ**を集約し、
/// 異なる値は呼び出し側がパラメータで渡す（FenceRadius）か各画面が保持する（守備位置・ダート）。
/// </summary>
public static class FieldDiagramGeometry
{
    public const double BaseDistanceM = 27.4; // 塁間 = √2·19.4

    // 内野の固定5点[m]（本塁・一・二・三・マウンド）。member/match で完全一致＝共通。
    public static readonly (double X, double Y) Home = (0.0, 0.0);
    public static readonly (double X, double Y) First = (19.4, 19.4);
    public static readonly (double X, double Y) Second = (0.0, 38.8);
    public static readonly (double X, double Y) Third = (-19.4, 19.4);
    public static readonly (double X, double Y) Mound = (0.0, 18.4);

    /// <summary>芝土境界弧（グラスライン）の半径[m]。実球場準拠のマウンド中心・約95ft。</summary>
    public const double InfieldGrassArcRadiusM = 29.0;

    /// <summary>球場座標の方位角[rad]（+Y=中堅を0、+X側を正）。</summary>
    public static double BearingRad((double X, double Y) p) => System.Math.Atan2(p.X, p.Y);

    /// <summary>
    /// 方位角[rad]の内野ダート境界（芝土境界弧）までの本塁からの距離[m]。
    /// 弧は本塁中心ではなく**マウンド中心・半径 <see cref="InfieldGrassArcRadiusM"/>**（実球場の
    /// グラスライン準拠）。本塁中心の固定半径だと二塁（38.8m）が土の外に浮くための修正式。
    /// 導出: 点 r·(sinθ, cosθ) とマウンド (0, My) の距離 = R を r について解いた正根
    /// r(θ) = My·cosθ + √(R² − (My·sinθ)²)。中堅方向47.4m・±45°で約38.9m。
    /// </summary>
    public static double InfieldDirtRadius(double bearingRad)
    {
        var my = Mound.Y;
        const double r = InfieldGrassArcRadiusM;
        var s = my * System.Math.Sin(bearingRad);
        return my * System.Math.Cos(bearingRad) + System.Math.Sqrt(r * r - s * s);
    }

    /// <summary>
    /// 方位角[rad]の外野フェンスまでの距離[m]。中堅=0で中堅距離、±45°で両翼距離を cos(2θ) 補間。
    /// 標準=甲子園相当（両翼95・中堅118）。将来 stadiums.yaml 接続時は、その球場の
    /// 両翼 L・中堅 C を渡して <c>L + (C − L)·cos(2θ)</c> で導出する（式はこの一箇所に集約）。
    /// </summary>
    public static double FenceRadius(double bearingRad, double wingM, double centerM)
        => wingM + (centerM - wingM) * System.Math.Cos(2.0 * bearingRad);

    /// <summary>
    /// 試合2D俯瞰再生の標準守備位置[m]（mock DEF）。試合ビュー（Playback）が参照する単一ソース。
    /// ※ メンバー画面はより詰めた表示専用配置（<see cref="MemberFieldLayout.DefensivePosition"/>）を使う
    ///   （端の見切れ回避の framing が異なるため。両者を1つに寄せるかは要設計判断）。
    /// </summary>
    public static (double X, double Y) MatchFielderPosition(FieldPosition p) => p switch
    {
        FieldPosition.Pitcher => (0.0, 17.4),
        FieldPosition.Catcher => (0.0, -2.2),
        FieldPosition.FirstBase => (22.0, 26.0),
        FieldPosition.SecondBase => (14.0, 38.0),
        FieldPosition.ThirdBase => (-22.0, 26.0),
        FieldPosition.Shortstop => (-14.0, 38.0),
        FieldPosition.LeftField => (-36.0, 76.0),
        FieldPosition.CenterField => (0.0, 92.0),
        FieldPosition.RightField => (36.0, 76.0),
        _ => (0.0, 0.0),
    };
}
