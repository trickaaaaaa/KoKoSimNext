using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Timeline.Playback;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 試合2D俯瞰ビューの描画要素（設計書06 §3.4 守備俯瞰）。
    /// docs/design/mock-match-2d-view.html の drawField / drawToken / draw を Painter2D へ移植したもの。
    /// 座標変換（S=3.35・本塁下中央）、フェンス弧 r=両翼+(中堅-両翼)cos2θ、ダート扇半径30——モックの値のまま。
    /// 色は「盤面昼光セット」（tokens.uss の --field-*-day / --field-outside）を参照＝「夏の昼間の高校野球」。
    /// フェンス弧の外は暗いUI背景で塗り、フェア芝／ファウル芝を層で分けて「暗い額縁の中の明るい昼の球場」を作る
    /// （メンバー画面 BaseballFieldElement と同じ層構成）。スコアボード緑×アンバーのUIテーマ（盤面の外）は不変。
    ///
    /// Painter2D はテキストを描けないため、トークン円・ボール・影・軌跡は generateVisualContent で、
    /// 野手/走者のラベル（投捕一二三遊左中右・打走）は子 Label プールで重ねる二層方式。
    /// アクター位置は <see cref="SetTime"/> でメートル空間に確定し、描画時に画面座標へ射影する。
    /// </summary>
    public sealed class Match2DPlaybackElement : VisualElement
    {
        // ── モックのデザインキャンバス（760×520・S=3.35・本塁 CX/OY）。要素サイズへ uniform 拡縮して忠実再現 ──
        private const float DesignW = 760f;
        private const float DesignH = 520f;
        private const float S = 3.35f;     // px/m（デザイン空間）
        private const float CX = 380f;     // 本塁の横位置（W/2）
        private const float OY = 492f;     // 本塁の縦位置（下寄り）
        private const float HeightPxPerM = 2.6f; // ボール高さ→画面オフセット（mock: b.h*2.6）
        private const int TrailMax = 26;

        // 球場寸法[m]（試合ビュー＝甲子園相当 両翼95・中堅118。将来 stadiums.yaml から供給）。
        private float _wingM = 95f;
        private float _centerM = 118f;

        // ── 全景カラム・フレーミング（MatchLive の3カラム中央用・既定オフ＝MatchDetail は従来の uniform レターボックス） ──
        // オン時: フェンス弧頂点（中堅）を上端5%、本塁を下端8%（=92%位置）に置くスケールで縦にカラムを使い切り、
        // ファウル地帯の左右は overflow:hidden でクロップする（ビューポート化の既定＝全景時のデフォルトビューポート）。
        private const float FillTopFrac = 0.05f;   // フェンス弧頂点の縦位置
        private const float FillHomeFrac = 0.92f;  // 本塁の縦位置（下端から8%）
        private bool _fillColumn;

        /// <summary>全景カラム・フレーミングの切替（MatchLive のみオン。MatchDetail は既定オフで無影響）。</summary>
        public void SetColumnFraming(bool on)
        {
            _fillColumn = on;
            UpdateLabels();
            MarkDirtyRepaint();
        }

        // 固定ジオメトリ。塁座標・マウンドは共通ソース FieldDiagramGeometry を参照（地理は共通）。
        private static Vector2 V2((double X, double Y) t) => new((float)t.X, (float)t.Y);
        private static readonly Vector2 Home = V2(FieldDiagramGeometry.Home);
        private static readonly Vector2 First = V2(FieldDiagramGeometry.First);
        private static readonly Vector2 Second = V2(FieldDiagramGeometry.Second);
        private static readonly Vector2 Third = V2(FieldDiagramGeometry.Third);
        private static readonly Vector2 Mound = V2(FieldDiagramGeometry.Mound);

        // 野手の1文字ラベル（mock: DEF のキー 投捕一二三遊左中右）。
        private static readonly (FieldPosition Pos, string Label)[] FielderOrder =
        {
            (FieldPosition.Pitcher, "投"), (FieldPosition.Catcher, "捕"),
            (FieldPosition.FirstBase, "一"), (FieldPosition.SecondBase, "二"),
            (FieldPosition.ThirdBase, "三"), (FieldPosition.Shortstop, "遊"),
            (FieldPosition.LeftField, "左"), (FieldPosition.CenterField, "中"),
            (FieldPosition.RightField, "右"),
        };

        // ── 色：盤面昼光セット（「夏の昼間の高校野球」。tokens の --field-*-day / --field-outside を解決） ──
        //   既定値は tokens.uss の昼光セットと同値のフォールバック。フェア芝は太陽光下の明るい黄緑、
        //   ファウル芝は一段暗い緑、ダートは乾いた明るい茶。フェンス弧の外は暗いUI背景（額縁）。
        //   ★ナイター対応：将来 --field-*-night を tokens に足し、OnStyles で読むプロパティ群を
        //     フラグで差し替えれば、この要素のロジックを変えずに夜景へ切替できる（変数セット差し替え）。
        private Color _grass = Hex(0x4F7C3B);       // フェア芝（昼・明るい黄緑）
        private Color _foul = Hex(0x456D34);        // ファウルゾーン芝（フェアと同系統・明度−12%程度＝「山」に見えない）
        private Color _clay = Hex(0xA26E44);        // 内野ダート（甲子園様式＝内野扇は全面ダート）
        private Color _mound = Hex(0x855A38);       // マウンド（ダートより一段濃い土色）
        private Color _chalk = Hex(0xEFF4EA);       // チョーク白（ライン・ベース・野手文字）
        private Color _outside = Hex(0x14231B);     // フェンス弧の外＝暗いUI背景（額縁）
        private Color _fence = Hex(0x1F3A26);       // 外野フェンス弧（明芝に映える濃緑）
        private Color _standHi = Hex(0x3A423B);     // 観客席帯（外殻）内側＝壁前列（明るめ）
        private Color _standLo = Hex(0x212A24);     // 観客席帯（外殻）外側＝暗く場外背景へ
        private Color _tokenFill = Hex(0x16241B);   // 野手トークンの塗り（濃色ディスク）
        private Color _tokenEdge = Hex(0x0E1A12);   // 野手トークンの縁（静止＝濃色縁）
        private Color _runnerEdge = Hex(0x23402A);  // 走者トークン（アンバー）の縁（明芝で締める）
        private Color _ballEdge = Hex(0x0B0B0B);    // ボールの黒縁（純白が明芝に溶けないよう輪郭）
        private Color _lamp = Hex(0xF5C64A);        // 走者塗り・移動中野手の縁（--color-lamp）
        private Color _trailColor = Hex(0xE68A2E);  // 軌跡（明芝で視認できる濃いめアンバー）
        private Color _ball = Hex(0xFFFFFF);        // ボール本体（純白＝--ball-white）
        private Color _grassStripe = Hex(0x578646); // 外野芝の刈り込みストライプ（明側・ベース芝と交互）
        private Color _warning = Hex(0xA26E44);     // ウォーニングトラック（フェンス手前の土帯）

        private static readonly CustomStyleProperty<Color> GrassProp = new("--field-grass-day");
        private static readonly CustomStyleProperty<Color> FoulProp = new("--field-foul-day");
        private static readonly CustomStyleProperty<Color> ClayProp = new("--field-clay-day");
        private static readonly CustomStyleProperty<Color> MoundProp = new("--field-mound-day");
        private static readonly CustomStyleProperty<Color> ChalkProp = new("--field-chalk");
        private static readonly CustomStyleProperty<Color> OutsideProp = new("--field-outside");
        private static readonly CustomStyleProperty<Color> FenceProp = new("--field-fence-day");
        private static readonly CustomStyleProperty<Color> StandHiProp = new("--field-stand-hi-day");
        private static readonly CustomStyleProperty<Color> StandLoProp = new("--field-stand-lo-day");
        private static readonly CustomStyleProperty<Color> TokenFillProp = new("--field-token-fill-day");
        private static readonly CustomStyleProperty<Color> TokenEdgeProp = new("--field-token-edge-day");
        private static readonly CustomStyleProperty<Color> RunnerEdgeProp = new("--field-runner-edge-day");
        private static readonly CustomStyleProperty<Color> BallEdgeProp = new("--field-ball-edge-day");
        private static readonly CustomStyleProperty<Color> TrailProp = new("--field-trail-day");
        private static readonly CustomStyleProperty<Color> LampProp = new("--color-lamp");
        private static readonly CustomStyleProperty<Color> BallProp = new("--ball-white");
        private static readonly CustomStyleProperty<Color> GrassStripeProp = new("--field-grass-stripe-day");
        private static readonly CustomStyleProperty<Color> WarningProp = new("--field-warning-day");

        // ── 再生状態（SetTime でメートル空間に確定） ──
        private PlaybackPlay _play;
        private readonly List<(Vector2 PosM, bool Moving)> _fielders = new();
        private readonly List<(string Label, Vector2 PosM)> _runners = new();
        private (Vector2 GroundM, float H)? _ballState;
        private readonly List<(Vector2 PosM, float H)> _trail = new();

        // ラベルプール（子 Label）: 野手9固定＋走者可変。
        private readonly List<Label> _fielderLabels = new();
        private readonly List<Label> _runnerLabels = new();

        public Match2DPlaybackElement()
        {
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.left = 0; style.top = 0; style.right = 0; style.bottom = 0;
            style.overflow = Overflow.Hidden;
            generateVisualContent += OnGenerate;
            RegisterCallback<CustomStyleResolvedEvent>(OnStyles);
            RegisterCallback<GeometryChangedEvent>(_ => { UpdateLabels(); MarkDirtyRepaint(); });

            for (var i = 0; i < FielderOrder.Length; i++)
            {
                var l = MakeTokenLabel();
                l.text = FielderOrder[i].Label;
                _fielderLabels.Add(l);
                Add(l);
            }
        }

        private Label MakeTokenLabel()
        {
            var l = new Label { pickingMode = PickingMode.Ignore };
            l.style.position = Position.Absolute;
            l.style.unityTextAlign = TextAnchor.MiddleCenter;
            l.style.fontSize = 10;
            l.style.color = _chalk;
            // 既定 Label のマージン/パディングを消してトークン中心と厳密に一致させる（#5 文字ずれ）。
            l.style.marginLeft = 0; l.style.marginRight = 0; l.style.marginTop = 0; l.style.marginBottom = 0;
            l.style.paddingLeft = 0; l.style.paddingRight = 0; l.style.paddingTop = 0; l.style.paddingBottom = 0;
            return l;
        }

        private void OnStyles(CustomStyleResolvedEvent e)
        {
            var cs = e.customStyle;
            if (cs.TryGetValue(GrassProp, out var g)) _grass = g;
            if (cs.TryGetValue(FoulProp, out var fo)) _foul = fo;
            if (cs.TryGetValue(ClayProp, out var c)) _clay = c;
            if (cs.TryGetValue(MoundProp, out var mo)) _mound = mo;
            if (cs.TryGetValue(ChalkProp, out var ch)) _chalk = ch;
            if (cs.TryGetValue(OutsideProp, out var ou)) _outside = ou;
            if (cs.TryGetValue(FenceProp, out var fe)) _fence = fe;
            if (cs.TryGetValue(StandHiProp, out var sh)) _standHi = sh;
            if (cs.TryGetValue(StandLoProp, out var sl)) _standLo = sl;
            if (cs.TryGetValue(TokenFillProp, out var tf)) _tokenFill = tf;
            if (cs.TryGetValue(TokenEdgeProp, out var te)) _tokenEdge = te;
            if (cs.TryGetValue(RunnerEdgeProp, out var re)) _runnerEdge = re;
            if (cs.TryGetValue(BallEdgeProp, out var be)) _ballEdge = be;
            if (cs.TryGetValue(TrailProp, out var tr)) _trailColor = tr;
            if (cs.TryGetValue(LampProp, out var la)) _lamp = la;
            if (cs.TryGetValue(BallProp, out var bw)) _ball = bw;
            if (cs.TryGetValue(GrassStripeProp, out var gs)) _grassStripe = gs;
            if (cs.TryGetValue(WarningProp, out var wa)) _warning = wa;
            foreach (var l in _fielderLabels) l.style.color = _chalk;
            MarkDirtyRepaint();
        }

        /// <summary>再生するプレーを差し替える（軌跡クリア・t=0 相当）。</summary>
        public void SetPlay(PlaybackPlay play)
        {
            _play = play;
            _trail.Clear();
            SetTime(0);
        }

        /// <summary>
        /// 守備の絡まないプレー（三振・四球）や打席間でも盤面を空にしないための静止表示。
        /// Play を持たなくても 9 野手を定位置（<see cref="PlaybackEvaluator.DefaultPositions"/>）に、
        /// 占有塁に走者を置く。守備陣・走者を永続表示するために使う。
        /// </summary>
        public void SetResting(bool first, bool second, bool third)
        {
            _play = null;
            _trail.Clear();
            _fielders.Clear();
            _runners.Clear();
            _ballState = null;

            foreach (var (pos, _) in FielderOrder)
            {
                var d = PlaybackEvaluator.DefaultPositions[pos];
                _fielders.Add((new Vector2((float)d.X, (float)d.Y), false));
            }
            if (first) _runners.Add(("走", First));
            if (second) _runners.Add(("走", Second));
            if (third) _runners.Add(("走", Third));

            UpdateLabels();
            MarkDirtyRepaint();
        }

        /// <summary>時刻 t のアクター位置を確定し、軌跡へ1点積む（描画は次の repaint で）。</summary>
        public void SetTime(double t)
        {
            _fielders.Clear();
            _runners.Clear();
            _ballState = null;

            if (_play != null)
            {
                foreach (var (pos, _) in FielderOrder)
                {
                    var (p, moving) = PlaybackEvaluator.FielderAt(_play, pos, t);
                    _fielders.Add((new Vector2((float)p.X, (float)p.Y), moving));
                }

                foreach (var r in _play.Runners)
                {
                    var p = PlaybackEvaluator.RunnerAt(r, t);
                    if (p.HasValue) _runners.Add((r.Label, new Vector2((float)p.Value.X, (float)p.Value.Y)));
                }

                var b = PlaybackEvaluator.BallAt(_play, t);
                if (b.HasValue)
                {
                    var gm = new Vector2((float)b.Value.X, (float)b.Value.Y);
                    _ballState = (gm, (float)b.Value.H);
                    _trail.Add((gm, (float)b.Value.H));
                    if (_trail.Count > TrailMax) _trail.RemoveAt(0);
                }
            }

            UpdateLabels();
            MarkDirtyRepaint();
        }

        // ── 射影（mock: px(p)=[CX+p.x*S, OY-p.y*S] を要素サイズへ uniform 拡縮・レターボックス） ──
        private float Scale
        {
            get
            {
                var r = contentRect;
                if (_fillColumn)
                {
                    // 本塁(OY)〜フェンス弧頂点(OY-中堅*S) の縦 span を host 高さの [Top, Home] に収める。
                    var spanPx = _centerM * S;
                    return spanPx > 0f ? r.height * (FillHomeFrac - FillTopFrac) / spanPx : 1f;
                }
                return Mathf.Min(r.width / DesignW, r.height / DesignH);
            }
        }

        private Vector2 Project(Vector2 m)
        {
            var r = contentRect;
            var k = Scale;
            float offX, offY;
            if (_fillColumn)
            {
                // 本塁を横中央・縦 FillHomeFrac に固定（横はファウル地帯がはみ出す＝クロップ）。
                offX = r.width * 0.5f - CX * k;
                offY = r.height * FillHomeFrac - OY * k;
            }
            else
            {
                offX = (r.width - DesignW * k) * 0.5f;
                offY = (r.height - DesignH * k) * 0.5f;
            }
            return new Vector2(offX + (CX + m.x * S) * k, offY + (OY - m.y * S) * k);
        }

        // ボール本体の画面位置（地面位置から高さぶん上へ）。mock: by = g[1] - b.h*2.6。
        private Vector2 BallBody(Vector2 groundM, float h)
        {
            var g = Project(groundM);
            return new Vector2(g.x, g.y - h * HeightPxPerM * Scale);
        }

        // ── 描画 ──
        private void OnGenerate(MeshGenerationContext mgc)
        {
            var r = contentRect;
            if (r.width < 4f || r.height < 4f) return;
            var p = mgc.painter2D;
            var k = Scale;

            DrawField(p, r, k);
            DrawTrail(p, k);
            DrawFielders(p, k);
            DrawRunners(p, k);
            DrawBall(p, k);
        }

        // drawField（昼光版）。「暗い額縁の中の明るい昼の球場」を作るため層を分けて塗る：
        //   ①額縁→②ファウルゾーン芝→③フェア芝→④放射ストライプ→⑤本塁土円＋ダート扇→
        //   ⑥ウォーニングトラック→⑦観客席帯→⑧フェンス弧線→⑨ファウルライン→
        //   ⑩本塁周りチョーク→⑪マウンド→⑫ベース。
        // ストライプは本塁→フェンスの全扇で塗り、後段のダート扇・トラックが内外を上塗りする
        // ＝柄は自然に「外野芝のみ」に残る（ClaudeDesign 見本 2026-07-20 の視覚言語）。
        // 座標変換 Project は共通（fillColumn 対応）。
        private void DrawField(Painter2D p, Rect r, float k)
        {
            const float q = Mathf.PI / 4f; // 45°（両翼＝ファウル線の角度）

            // ① 全面を暗いUI背景で塗る（フェンス弧の外＝球場の外＝額縁）。
            p.fillColor = _outside;
            p.BeginPath();
            p.MoveTo(new Vector2(0, 0));
            p.LineTo(new Vector2(r.width, 0));
            p.LineTo(new Vector2(r.width, r.height));
            p.LineTo(new Vector2(0, r.height));
            p.ClosePath();
            p.Fill();

            // ② ファウルゾーン芝：フェア以外の角域（θ=45°〜315°を大半径で）。クロップ外は見えない。
            p.fillColor = _foul;
            p.BeginPath();
            p.MoveTo(Project(Home));
            for (var i = 0; i <= 60; i++)
            {
                var th = q + (2f * Mathf.PI - 2f * q) * i / 60f;
                p.LineTo(Project(new Vector2(_centerM * Mathf.Sin(th), _centerM * Mathf.Cos(th))));
            }
            p.ClosePath();
            p.Fill();

            // ③ フェア芝：本塁→フェンス弧（θ=-45°〜+45°）で閉じた扇（外野はフェンスで閉じ、その外は額縁）。
            p.fillColor = _grass;
            p.BeginPath();
            p.MoveTo(Project(Home));
            for (var i = 0; i <= 60; i++)
            {
                var th = -q + 2f * q * i / 60f;
                p.LineTo(Project(FencePoint(th)));
            }
            p.ClosePath();
            p.Fill();

            // ④ 放射ストライプ（外野芝の刈り込み）：±45°を11.25°×8ウェッジに割り、奇数番だけ明側色を
            //    本塁→フェンス弧の扇で重ね塗り（偶数番はベース芝が透ける＝2色交互）。
            p.fillColor = _grassStripe;
            for (var w = 1; w < 8; w += 2)
            {
                var th0 = -q + q * 2f * w / 8f;
                var th1 = -q + q * 2f * (w + 1) / 8f;
                p.BeginPath();
                p.MoveTo(Project(Home));
                for (var i = 0; i <= 8; i++)
                {
                    var th = th0 + (th1 - th0) * i / 8f;
                    p.LineTo(Project(FencePoint(th)));
                }
                p.ClosePath();
                p.Fill();
            }

            // ⑤ 本塁土円＋内野ダート扇（甲子園様式で全面ダート。内野芝ダイヤは廃止）。
            //    境界弧は本塁中心の固定半径ではなく共通ソース InfieldDirtRadius（マウンド中心・
            //    半径29m＝実球場のグラスライン準拠）。中堅方向47.4m＝二塁が必ず土の内側に載る。
            p.fillColor = _clay;
            p.BeginPath();
            p.Arc(Project(Home), 4.3f * S * k, new Angle(0, AngleUnit.Degree), new Angle(360, AngleUnit.Degree));
            p.Fill();
            p.BeginPath();
            p.MoveTo(Project(Home));
            for (var i = 0; i <= 40; i++)
            {
                var th = (-45f + 90f * i / 40f) * Mathf.Deg2Rad;
                var dirtR = (float)FieldDiagramGeometry.InfieldDirtRadius(th);
                p.LineTo(Project(new Vector2(dirtR * Mathf.Sin(th), dirtR * Mathf.Cos(th))));
            }
            p.ClosePath();
            p.Fill();

            // ⑥ ウォーニングトラック：フェンス弧の内側に沿う幅3.5mの土帯（打球のフェンス際が一目で分かる）。
            p.fillColor = _warning;
            p.BeginPath();
            for (var i = 0; i <= 60; i++) // 外側の弧：+45°→-45°（フェンス上）
            {
                var th = (45f - 90f * i / 60f) * Mathf.Deg2Rad;
                var pt = Project(FencePoint(th));
                if (i == 0) p.MoveTo(pt); else p.LineTo(pt);
            }
            for (var i = 0; i <= 60; i++) // 内側の弧：-45°→+45°（フェンス−3.5m）
            {
                var th = (-45f + 90f * i / 60f) * Mathf.Deg2Rad;
                var wr = FenceRadius(th) - 3.5f;
                p.LineTo(Project(new Vector2(wr * Mathf.Sin(th), wr * Mathf.Cos(th))));
            }
            p.ClosePath();
            p.Fill();

            // ⑦ 観客席帯（外殻）：フェンス弧の外側に沿った暗い帯（約11px・2段で濃色グラデ）。
            //    球場輪郭を場外の暗背景から切り離して閉じる。フェンス線の直前に敷き、線が壁上端になる。
            DrawStandBand(p, k, 5.5f, 11f, _standLo); // 外側（暗）
            DrawStandBand(p, k, 0f, 5.5f, _standHi);  // 内側（壁前列・明るめ）

            // ⑧ フェンス弧（θ=-45°..45°・r=両翼+(中堅-両翼)cos2θ・明芝に映える濃緑）。将来 stadiums.yaml 駆動へ。
            p.strokeColor = _fence;
            p.lineWidth = 4f * k;
            p.BeginPath();
            for (var i = 0; i <= 60; i++)
            {
                var th = (-45f + 90f * i / 60f) * Mathf.Deg2Rad;
                var pt = Project(FencePoint(th));
                if (i == 0) p.MoveTo(pt); else p.LineTo(pt);
            }
            p.Stroke();

            // ⑨ ファウルライン（本塁→[±67.2,67.2]）。昼の石灰ライン＝不透明度100%でくっきり。
            p.strokeColor = _chalk;
            p.lineWidth = 2f * k;
            StrokeLine(p, Project(Home), Project(new Vector2(67.2f, 67.2f)));
            StrokeLine(p, Project(Home), Project(new Vector2(-67.2f, 67.2f)));

            // ⑩ 本塁周りチョーク（バッターボックス左右・キャッチャーボックス）。
            //    実寸[m]で引く（ボックス1.22×1.83等）。線は細め＝ファウルラインより従の階層。
            //    ネクストバッターズサークルは不採用（2026-07-20 ユーザー判断）。
            p.strokeColor = _chalk;
            p.lineWidth = 1.2f * k;
            StrokeRectM(p, 0.37f, -0.91f, 1.59f, 0.92f);    // 右打席
            StrokeRectM(p, -1.59f, -0.91f, -0.37f, 0.92f);  // 左打席
            StrokeRectM(p, -1.2f, -3.1f, 1.2f, -0.7f);      // キャッチャーボックス（簡略形）

            // ⑪ マウンド（土円 r8・ダートより一段濃い土色）。
            p.fillColor = _mound;
            p.BeginPath();
            p.Arc(Project(Mound), 8f * k, new Angle(0, AngleUnit.Degree), new Angle(360, AngleUnit.Degree));
            p.Fill();

            // ⑫ ベース（チョーク小四角 8×8）。
            DrawBase(p, Project(Home), k);
            DrawBase(p, Project(First), k);
            DrawBase(p, Project(Second), k);
            DrawBase(p, Project(Third), k);
        }

        // 中堅=0°・両翼=±45°。フェンス弧の式は共通ソース FieldDiagramGeometry.FenceRadius に集約
        // （両翼/中堅は試合ビューの stadium 値を渡す。将来 stadiums.yaml 駆動）。
        private float FenceRadius(float thetaRad) => (float)FieldDiagramGeometry.FenceRadius(thetaRad, _wingM, _centerM);
        private Vector2 FencePoint(float thetaRad)
        {
            var rad = FenceRadius(thetaRad);
            return new Vector2(rad * Mathf.Sin(thetaRad), rad * Mathf.Cos(thetaRad));
        }

        // 観客席帯（外殻）：フェンス弧の「外側」を画面px幅で走る帯。Project は等方スケール S·k なので、
        // 画面px の外向きオフセットは field 半径へ px/(S·k) を足せば得られる（fillColumn でも同式）。
        // 帯は本塁側から見て弧の外＝場外の暗背景の上に乗り、球場輪郭を閉じる。
        private void DrawStandBand(Painter2D p, float k, float innerPx, float outerPx, Color col)
        {
            var sk = S * k;
            if (sk <= 0f) return;
            var dInner = innerPx / sk;
            var dOuter = outerPx / sk;
            p.fillColor = col;
            p.BeginPath();
            for (var i = 0; i <= 60; i++) // 外側の弧：+45°→-45°
            {
                var th = (45f - 90f * i / 60f) * Mathf.Deg2Rad;
                var r = FenceRadius(th) + dOuter;
                var pt = Project(new Vector2(r * Mathf.Sin(th), r * Mathf.Cos(th)));
                if (i == 0) p.MoveTo(pt); else p.LineTo(pt);
            }
            for (var i = 0; i <= 60; i++) // 内側の弧：-45°→+45°（フェンス寄り）
            {
                var th = (-45f + 90f * i / 60f) * Mathf.Deg2Rad;
                var r = FenceRadius(th) + dInner;
                p.LineTo(Project(new Vector2(r * Mathf.Sin(th), r * Mathf.Cos(th))));
            }
            p.ClosePath();
            p.Fill();
        }

        // mock: 軌跡（新しいほど濃い lamp のフェード線）。
        private void DrawTrail(Painter2D p, float k)
        {
            if (_trail.Count < 2) return;
            p.lineWidth = 2f * k;
            for (var i = 1; i < _trail.Count; i++)
            {
                var c = _trailColor; c.a = 0.55f * i / _trail.Count; // 明芝で飛ばないよう濃いめアンバー＋濃度上げ
                p.strokeColor = c;
                StrokeLine(p, BallBody(_trail[i - 1].PosM, _trail[i - 1].H), BallBody(_trail[i].PosM, _trail[i].H));
            }
        }

        // mock: drawToken。円 r10・塗り tokenFill・縁は移動中 lamp / 通常 chalk(.85)。ラベルは子 Label。
        private void DrawFielders(Painter2D p, float k)
        {
            for (var i = 0; i < _fielders.Count; i++)
            {
                var (posM, moving) = _fielders[i];
                var c = Project(posM);
                p.fillColor = _tokenFill;
                p.BeginPath();
                p.Arc(c, 10f * k, new Angle(0, AngleUnit.Degree), new Angle(360, AngleUnit.Degree));
                p.Fill();
                // 昼版：静止は濃色縁（白枠を廃止＝明芝で消えない）、移動中はアンバー縁で強調。
                var edge = moving ? _lamp : _tokenEdge;
                p.strokeColor = edge;
                p.lineWidth = 2f * k;
                p.BeginPath();
                p.Arc(c, 10f * k, new Angle(0, AngleUnit.Degree), new Angle(360, AngleUnit.Degree));
                p.Stroke();
            }
        }

        // mock: 走者トークン（円 r9・塗り lamp・ラベルは子 Label で ink 色）。
        private void DrawRunners(Painter2D p, float k)
        {
            foreach (var (_, posM) in _runners)
            {
                var c = Project(posM);
                p.fillColor = _lamp;
                p.BeginPath();
                p.Arc(c, 9f * k, new Angle(0, AngleUnit.Degree), new Angle(360, AngleUnit.Degree));
                p.Fill();
                // 昼版：明芝の上でアンバーが締まるよう濃緑の縁を付ける。
                p.strokeColor = _runnerEdge;
                p.lineWidth = 1.5f * k;
                p.BeginPath();
                p.Arc(c, 9f * k, new Angle(0, AngleUnit.Degree), new Angle(360, AngleUnit.Degree));
                p.Stroke();
            }
        }

        // mock: ボール（影 ellipse 4×2.4・本体 arc 4.2・白）。
        private void DrawBall(Painter2D p, float k)
        {
            if (!_ballState.HasValue) return;
            var g = Project(_ballState.Value.GroundM);
            // 影（黒半透明の楕円・昼版は濃度を上げて明芝でも接地点が読める）。
            p.fillColor = new Color(0, 0, 0, 0.62f);
            DrawEllipse(p, g, 4f * k, 2.4f * k);
            // 本体：黒縁（約1px）を先に敷き、その上に純白（明芝に溶けないよう輪郭を立てる）。
            var body = BallBody(_ballState.Value.GroundM, _ballState.Value.H);
            p.fillColor = _ballEdge;
            p.BeginPath();
            p.Arc(body, 4.2f * k + 1.3f, new Angle(0, AngleUnit.Degree), new Angle(360, AngleUnit.Degree));
            p.Fill();
            p.fillColor = _ball;
            p.BeginPath();
            p.Arc(body, 4.2f * k, new Angle(0, AngleUnit.Degree), new Angle(360, AngleUnit.Degree));
            p.Fill();
        }

        // ── ラベル配置（トークン中心へ） ──
        private void UpdateLabels()
        {
            var k = Scale;
            for (var i = 0; i < _fielderLabels.Count; i++)
            {
                var lbl = _fielderLabels[i];
                if (i < _fielders.Count)
                {
                    lbl.style.display = DisplayStyle.Flex;
                    PlaceLabel(lbl, Project(_fielders[i].PosM), _chalk, k);
                }
                else lbl.style.display = DisplayStyle.None;
            }

            while (_runnerLabels.Count < _runners.Count)
            {
                var l = MakeTokenLabel();
                _runnerLabels.Add(l);
                Add(l);
            }
            for (var i = 0; i < _runnerLabels.Count; i++)
            {
                var lbl = _runnerLabels[i];
                if (i < _runners.Count)
                {
                    lbl.style.display = DisplayStyle.Flex;
                    lbl.text = _runners[i].Label;
                    PlaceLabel(lbl, Project(_runners[i].PosM), Hex(0x1A1608), k); // 走者ラベルは ink（暗）
                }
                else lbl.style.display = DisplayStyle.None;
            }
        }

        // ラベル箱・文字はトークン円（半径 10f*k / 9f*k）と同じく k で拡縮しないと、スケール≠1 で中心がずれる。
        // 箱の中心をトークン中心へ厳密に一致させ、MiddleCenter で字を中央化する（#5 文字ずれ）。
        private static void PlaceLabel(Label lbl, Vector2 center, Color color, float k)
        {
            var w = 20f * k;
            var h = 14f * k;
            lbl.style.width = w; lbl.style.height = h;
            lbl.style.fontSize = 10f * k;
            lbl.style.left = center.x - w * 0.5f;
            lbl.style.top = center.y - h * 0.5f;
            lbl.style.color = color;
        }

        // ── Painter2D 補助 ──
        private static void StrokeLine(Painter2D p, Vector2 a, Vector2 b)
        {
            p.BeginPath(); p.MoveTo(a); p.LineTo(b); p.Stroke();
        }

        // 球場座標[m]の軸平行矩形をチョーク線で描く（本塁周りのボックス用。Project は等方なので形は保たれる）。
        private void StrokeRectM(Painter2D p, float x0, float y0, float x1, float y1)
        {
            p.BeginPath();
            p.MoveTo(Project(new Vector2(x0, y0)));
            p.LineTo(Project(new Vector2(x1, y0)));
            p.LineTo(Project(new Vector2(x1, y1)));
            p.LineTo(Project(new Vector2(x0, y1)));
            p.ClosePath();
            p.Stroke();
        }

        private void DrawBase(Painter2D p, Vector2 c, float k)
        {
            var hw = 4f * k;
            p.fillColor = _chalk;
            p.BeginPath();
            p.MoveTo(new Vector2(c.x - hw, c.y - hw));
            p.LineTo(new Vector2(c.x + hw, c.y - hw));
            p.LineTo(new Vector2(c.x + hw, c.y + hw));
            p.LineTo(new Vector2(c.x - hw, c.y + hw));
            p.ClosePath();
            p.Fill();
        }

        // 楕円（影用）。Painter2D に楕円プリミティブが無いため多角近似。
        private static void DrawEllipse(Painter2D p, Vector2 c, float rx, float ry)
        {
            p.BeginPath();
            for (var i = 0; i <= 24; i++)
            {
                var th = 2f * Mathf.PI * i / 24f;
                var pt = new Vector2(c.x + rx * Mathf.Cos(th), c.y + ry * Mathf.Sin(th));
                if (i == 0) p.MoveTo(pt); else p.LineTo(pt);
            }
            p.ClosePath();
            p.Fill();
        }

        private static Color Hex(int rgb)
            => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);
    }
}
