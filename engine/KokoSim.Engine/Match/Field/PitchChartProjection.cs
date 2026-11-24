namespace KokoSim.Engine.Match.Field;

/// <summary>
/// 対決ビューの左下に出す投球チャート（issue #1541）の<b>座標写像だけ</b>を持つ純関数。
///
/// <para>本塁面の着弾位置[m]（<c>PitchRecord.LocationX/Y</c>＝+X が一塁側・Y は地面からの高さ）を、
/// 図の正規化ローカル座標（u: 0=左..1=右, v: 0=上..1=下）へ落とす。図は<b>投手の後ろから見た向き</b>
/// （対決ビューの絵と同じ側）なので、一塁側（+X）は図の<b>左</b>に来る＝X を反転する。
/// 右打者の内角（三塁側＝−X）は図の右寄り、外角は左寄りになる。</para>
///
/// <para>図はストライクゾーン（<see cref="StrikeZone"/>）の外側に余白を持つ。ボール球の多くは余白に収まり、
/// それより外れた球は辺に張り付ける（枠外に描くより誤読が少ない＝<see cref="MiniDiamondProjection"/> と同じ扱い）。
/// <b>写像はここにしか置かない</b>（ゾーンの枠も印も同じ関数を通る＝印が枠に対して必ず正しい位置に乗る）。
/// 表示専用＝判定にも帯にも一切関与しない。</para>
/// </summary>
public static class PitchChartProjection
{
    /// <summary>ゾーンの左右の外に取る余白[m]（ボール2〜3個ぶん）。広く取るとゾーンが小さくなり、
    /// 印が9分割の1マスより大きくなる（2026-11-24 の撮影で 0.30 は広すぎた）。それより外れた球は辺に張り付く。</summary>
    public const double SideMarginM = 0.20;
    /// <summary>ゾーンの上に取る余白[m]。</summary>
    public const double TopMarginM = 0.22;
    /// <summary>ゾーンの下に取る余白[m]（低めのボール・ワンバウンド気味の変化球が多いので上より広い）。</summary>
    public const double BottomMarginM = 0.28;

    /// <summary>図の幅[m]。</summary>
    public static double SpanXM(StrikeZone zone) => 2.0 * (zone.HalfWidthM + SideMarginM);

    /// <summary>図の高さ[m]。</summary>
    public static double SpanYM(StrikeZone zone) => zone.TopM - zone.BottomM + TopMarginM + BottomMarginM;

    /// <summary>
    /// 本塁面[m] → 図の正規化ローカル座標（投手の後ろから見た向き）。図の外の点は辺へ丸める。
    /// </summary>
    public static (double U, double V) ToLocal01(double x, double y, StrikeZone zone)
    {
        var u = 0.5 - x / SpanXM(zone);                              // +X（一塁側）は投手側から見て左
        var v = (zone.TopM + TopMarginM - y) / SpanYM(zone);         // 高い球は上（画面座標は下向き）
        return (Clamp01(u), Clamp01(v));
    }

    /// <summary>ゾーンの枠（左上・右下）を図の正規化ローカル座標で。9分割の線もここから引く。</summary>
    public static (double U0, double V0, double U1, double V1) ZoneLocal01(StrikeZone zone)
    {
        var (u0, v0) = ToLocal01(zone.HalfWidthM, zone.TopM, zone);   // 一塁側の上端＝図の左上
        var (u1, v1) = ToLocal01(-zone.HalfWidthM, zone.BottomM, zone);
        return (u0, v0, u1, v1);
    }

    private static double Clamp01(double t) => t < 0.0 ? 0.0 : t > 1.0 ? 1.0 : t;
}
