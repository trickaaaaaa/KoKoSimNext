namespace KokoSim.Engine.Match.Field;

/// <summary>
/// 守備フォーカス中に盤面の隅へ出す小ダイヤモンド（issue #595）の<b>座標写像だけ</b>を持つ純関数。
///
/// <para>球場座標[m]（<see cref="FieldDiagramGeometry"/>＝本塁が原点・+Y が二塁方向・+X が一塁側）を、
/// 正方形の箱の正規化ローカル座標（u: 0=左..1=右, v: 0=上..1=下）へ落とす。v が下向きなのは
/// UI Toolkit / Painter2D の画面座標に合わせるため＝呼び出し側で符号を反転させない。</para>
///
/// <para><b>写像はここにしか置かない</b>（issue #595 の実装メモ「塁の実座標→ミニダイヤのローカル座標の
/// 写像は1箇所に集約する」）。塁の位置も走者ドットの位置も同じ関数を通る＝走者が「塁の上に居る」ことが
/// 図の上でも必ず一致する。倍率は箱の実寸[px]に依存しないので、枠の大きさを変えても写像は不変。</para>
///
/// <para>表示専用＝判定にも帯にも一切関与しない（<see cref="FieldDiagramGeometry"/> と同じ位置づけ）。</para>
/// </summary>
public static class MiniDiamondProjection
{
    /// <summary>
    /// 塁の外側に確保する余白[m]。一塁駆け抜け（オーバーラン, issue #443）は一塁の 3.5m 先で止まる＝
    /// 塁ちょうどで箱を切ると打者走者が枠から出る。5m 取れば余裕を持って収まる。
    /// </summary>
    public const double MarginM = 5.0;

    /// <summary>箱が覆う半幅[m]（本塁と二塁の中点を中心とする正方形の一辺の半分）。</summary>
    public static double HalfSpanM => FieldDiagramGeometry.First.X + MarginM;

    /// <summary>箱の中心[m]＝本塁と二塁の中点。ここを中心に取ると4つの塁が上下左右対称に並ぶ。</summary>
    public static (double X, double Y) CenterM => (0.0, FieldDiagramGeometry.Second.Y / 2.0);

    /// <summary>
    /// 球場座標[m] → 箱の正規化ローカル座標。箱の外へ出た点は端へ丸める
    /// （走者が枠外へ描かれるより、辺に張り付いたほうが誤読が少ない）。
    /// </summary>
    public static (double U, double V) ToLocal01(double x, double y)
    {
        var span = 2.0 * HalfSpanM;
        var c = CenterM;
        var u = (x - c.X) / span + 0.5;
        var v = 0.5 - (y - c.Y) / span;   // +Y（二塁方向）は画面の上＝v は反転
        return (Clamp01(u), Clamp01(v));
    }

    /// <summary>球場座標[m] → 箱の正規化ローカル座標（タプル版）。</summary>
    public static (double U, double V) ToLocal01((double X, double Y) p) => ToLocal01(p.X, p.Y);

    private static double Clamp01(double v) => v < 0.0 ? 0.0 : v > 1.0 ? 1.0 : v;
}
