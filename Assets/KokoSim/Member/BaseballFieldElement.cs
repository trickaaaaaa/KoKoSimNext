using KokoSim.Engine.Match.Field;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Member
{
    /// <summary>
    /// 野球場の俯瞰図を「座標系」から描く VisualElement（設計書 mock-match-2d-view.html と同じ図法）。
    /// 本塁を原点、二塁方向を +Y、一塁側を +X とする球場座標系[m]を、要素サイズに合わせて画面座標へ
    /// 写す唯一の変換 <see cref="FieldToLocal"/> で全要素（芝/ファウルゾーン/ダート/ライン/ベース/フェンス）を
    /// 配置する。選手チップの立ち位置も必ず同じ変換を使うこと（Controller が FieldToLocal を参照）。
    ///
    /// スケール：内野ダイヤ（塁間27.4m ＝ 1塁〜3塁の横幅 38.8m）が描画幅の <see cref="DiamondFraction"/> を
    /// 占めるよう Sx を決める。外野が縦に収まりきらない分は非等方スケール（Sy = 0.8·Sx）で上方向へ圧縮する。
    /// 外野はフェンス弧で閉じ、弧の外側は背景色（球場の外）。
    /// </summary>
    public sealed class BaseballFieldElement : VisualElement
    {
        // 球場座標[m]。本塁=原点、二塁方向=+Y。エンジンの検算済み定数（MemberFieldLayout）を単一ソースに使う。
        private static Vector2 P((double X, double Y) t) => new Vector2((float)t.X, (float)t.Y);
        public static readonly Vector2 Home = P(MemberFieldLayout.Home);
        public static readonly Vector2 First = P(MemberFieldLayout.First);
        public static readonly Vector2 Second = P(MemberFieldLayout.Second);
        public static readonly Vector2 Third = P(MemberFieldLayout.Third);
        public static readonly Vector2 Mound = P(MemberFieldLayout.Mound);

        private const float DiamondWidthM = 38.8f;  // 1塁〜3塁の横幅
        private const float DiamondFraction = 0.50f; // 描画幅に占めるダイヤ横幅の割合
        private const float Aniso = 0.8f;            // 縦圧縮（Sy = Aniso·Sx）
        private const float HomeYFrac = 0.90f;       // 本塁の縦位置（下寄り）

        // 球場寸法[m]（メンバー画面は標準球場）。検算済み定数 MemberFieldLayout を単一ソースに使う。
        public float WingM = (float)MemberFieldLayout.WingFenceM;
        public float CenterM = (float)MemberFieldLayout.CenterFenceM;

        // 既定色は tokens と同値（CustomStyle 解決前・失敗時のフォールバック）。
        private Color _grass = new Color(0.102f, 0.200f, 0.145f);   // #1A3325 フェア芝
        private Color _foul = new Color(0.055f, 0.110f, 0.078f);    // #0E1C14 ファウル
        private Color _infield = new Color(0.133f, 0.224f, 0.169f); // #22392B 内野芝
        private Color _clay = new Color(0.431f, 0.290f, 0.188f);    // #6E4A30 ダート
        private Color _chalk = new Color(0.937f, 0.957f, 0.918f);   // #EFF4EA チョーク
        private Color _bg = new Color(0.078f, 0.137f, 0.106f);      // #14231B 球場の外
        private Color _fence = new Color(0.290f, 0.416f, 0.322f);   // #4A6A52 フェンス線

        private static readonly CustomStyleProperty<Color> GrassProp = new CustomStyleProperty<Color>("--field-grass");
        private static readonly CustomStyleProperty<Color> FoulProp = new CustomStyleProperty<Color>("--field-foul");
        private static readonly CustomStyleProperty<Color> InfieldProp = new CustomStyleProperty<Color>("--field-grass-infield");
        private static readonly CustomStyleProperty<Color> ClayProp = new CustomStyleProperty<Color>("--field-clay");
        private static readonly CustomStyleProperty<Color> ChalkProp = new CustomStyleProperty<Color>("--field-chalk");
        private static readonly CustomStyleProperty<Color> BgProp = new CustomStyleProperty<Color>("--field-outside");
        private static readonly CustomStyleProperty<Color> FenceProp = new CustomStyleProperty<Color>("--field-fence");

        public BaseballFieldElement()
        {
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.left = 0; style.top = 0; style.right = 0; style.bottom = 0;
            style.overflow = Overflow.Hidden;
            generateVisualContent += OnGenerate;
            RegisterCallback<CustomStyleResolvedEvent>(OnStyles);
            RegisterCallback<GeometryChangedEvent>(_ => MarkDirtyRepaint());
        }

        private void OnStyles(CustomStyleResolvedEvent e)
        {
            var cs = e.customStyle;
            if (cs.TryGetValue(GrassProp, out var g)) _grass = g;
            if (cs.TryGetValue(FoulProp, out var f)) _foul = f;
            if (cs.TryGetValue(InfieldProp, out var i)) _infield = i;
            if (cs.TryGetValue(ClayProp, out var c)) _clay = c;
            if (cs.TryGetValue(ChalkProp, out var ch)) _chalk = ch;
            if (cs.TryGetValue(BgProp, out var bg)) _bg = bg;
            if (cs.TryGetValue(FenceProp, out var fe)) _fence = fe;
            MarkDirtyRepaint();
        }

        private float Sx => contentRect.width * DiamondFraction / DiamondWidthM;
        private float Sy => Sx * Aniso;

        /// <summary>球場座標[m] → 要素ローカル座標[px]。全配置の単一変換（本塁=下中央・+Y=上・非等方）。</summary>
        public Vector2 FieldToLocal(Vector2 m)
        {
            var r = contentRect;
            return new Vector2(r.width * 0.5f + m.x * Sx, r.height * HomeYFrac - m.y * Sy);
        }

        // 中堅=0°・両翼(ファウル線上)=±45°。両翼距離と中堅距離を cos(2θ) で滑らかに補間したフェンス半径[m]。
        private float FenceRadius(float thetaRad)
        {
            return WingM + (CenterM - WingM) * Mathf.Cos(2f * thetaRad);
        }
        // θ[rad]（+Yから時計回り、+X側が正）のフェンス点[m]。
        private Vector2 FencePoint(float thetaRad)
        {
            var r = FenceRadius(thetaRad);
            return new Vector2(r * Mathf.Sin(thetaRad), r * Mathf.Cos(thetaRad));
        }

        private void OnGenerate(MeshGenerationContext mgc)
        {
            var r = contentRect;
            if (r.width < 4f || r.height < 4f) return;
            var p = mgc.painter2D;
            const float q = Mathf.PI / 4f; // 45°（両翼＝ファウル線の角度）

            // 1. 球場の外（背景色）で全面塗り
            FillRect(p, r, _bg);

            // 2. ファウルゾーン：フェア以外の角域（θ=45°〜315°）を大半径で塗る。クロップ外は見えない。
            p.fillColor = _foul;
            p.BeginPath();
            p.MoveTo(FieldToLocal(Home));
            for (var i = 0; i <= 60; i++)
            {
                var th = q + (2f * Mathf.PI - 2f * q) * i / 60f; // 45°→315°
                p.LineTo(FieldToLocal(new Vector2(CenterM * Mathf.Sin(th), CenterM * Mathf.Cos(th))));
            }
            p.ClosePath();
            p.Fill();

            // 3. フェア：本塁→フェンス弧（θ=-45°〜+45°）で閉じた扇を芝で塗る（外野はフェンスで閉じる）
            p.fillColor = _grass;
            p.BeginPath();
            p.MoveTo(FieldToLocal(Home));
            for (var i = 0; i <= 60; i++)
            {
                var th = -q + 2f * q * i / 60f;
                p.LineTo(FieldToLocal(FencePoint(th)));
            }
            p.ClosePath();
            p.Fill();

            // 4. 内野ダート（±45°の扇）。境界弧は共通ソース InfieldDirtRadius（マウンド中心・半径29m＝
            //    実球場のグラスライン準拠）。旧・本塁中心29m固定は二塁(38.8m)が土の外に浮く誤りだった。
            p.fillColor = _clay;
            p.BeginPath();
            p.MoveTo(FieldToLocal(Home));
            for (var i = 0; i <= 40; i++)
            {
                var th = -q + 2f * q * i / 40f;
                var dirt = (float)FieldDiagramGeometry.InfieldDirtRadius(th);
                p.LineTo(FieldToLocal(new Vector2(dirt * Mathf.Sin(th), dirt * Mathf.Cos(th))));
            }
            p.ClosePath();
            p.Fill();

            // 5. 内野芝ダイヤ（塁線に沿う）
            p.fillColor = _infield;
            p.BeginPath();
            p.MoveTo(FieldToLocal(new Vector2(0f, 4.5f)));
            p.LineTo(FieldToLocal(new Vector2(15f, 19.4f)));
            p.LineTo(FieldToLocal(new Vector2(0f, 34.4f)));
            p.LineTo(FieldToLocal(new Vector2(-15f, 19.4f)));
            p.ClosePath();
            p.Fill();

            // 6. 外野フェンス弧（球場を閉じる線・明るめ薄線）
            p.strokeColor = _fence;
            p.lineWidth = 3.5f;
            p.BeginPath();
            p.MoveTo(FieldToLocal(FencePoint(-q)));
            for (var i = 1; i <= 60; i++)
            {
                var th = -q + 2f * q * i / 60f;
                p.LineTo(FieldToLocal(FencePoint(th)));
            }
            p.Stroke();

            // 7. ファウルライン：本塁→両翼（フェンスとの交点＝θ=±45°, r=両翼）でチョーク2px。端まで延ばさない。
            p.strokeColor = _chalk;
            p.lineWidth = 2f;
            StrokeLine(p, FieldToLocal(Home), FieldToLocal(FencePoint(q)));
            StrokeLine(p, FieldToLocal(Home), FieldToLocal(FencePoint(-q)));

            // 8. 投手プレート（小さな土円）
            p.fillColor = _clay;
            p.BeginPath();
            p.Arc(FieldToLocal(Mound), Mathf.Max(3f, Sx * 2.2f), new Angle(0f, AngleUnit.Degree), new Angle(360f, AngleUnit.Degree));
            p.Fill();

            // 9. ベース（チョーク小四角）
            DrawBase(p, FieldToLocal(Home));
            DrawBase(p, FieldToLocal(First));
            DrawBase(p, FieldToLocal(Second));
            DrawBase(p, FieldToLocal(Third));
        }

        private static void FillRect(Painter2D p, Rect r, Color c)
        {
            p.fillColor = c;
            p.BeginPath();
            p.MoveTo(new Vector2(0f, 0f));
            p.LineTo(new Vector2(r.width, 0f));
            p.LineTo(new Vector2(r.width, r.height));
            p.LineTo(new Vector2(0f, r.height));
            p.ClosePath();
            p.Fill();
        }

        private static void StrokeLine(Painter2D p, Vector2 a, Vector2 b)
        {
            p.BeginPath();
            p.MoveTo(a);
            p.LineTo(b);
            p.Stroke();
        }

        private void DrawBase(Painter2D p, Vector2 c)
        {
            const float hw = 4f;
            p.fillColor = _chalk;
            p.BeginPath();
            p.MoveTo(new Vector2(c.x - hw, c.y - hw));
            p.LineTo(new Vector2(c.x + hw, c.y - hw));
            p.LineTo(new Vector2(c.x + hw, c.y + hw));
            p.LineTo(new Vector2(c.x - hw, c.y + hw));
            p.ClosePath();
            p.Fill();
        }
    }
}
