using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Unity.Players; // AbilityBar / RadarAxis を共用
using KokoSim.Unity.Shell;   // ScreenRouter / RankPalette（ランク色の単一ソース）

namespace KokoSim.Unity.Squad
{
    /// <summary>
    /// チーム総合力パネル（設計決定 2026-07-18・C案＝選手詳細流儀）。
    /// 左に「チーム戦力バランス」レーダー（5段階グリッド／中心→外グラデ塗り＝総合ランク連動色／頂点ドット／
    /// 軸ラベル＋数値オーバーレイ）、右に6指標のバー（ランク連動色・内訳付き）＋弱点の分析コメント。
    /// 配色ルール: ランク色は RankPalette（＝tokens.uss --rank-*）に統一。黄アクセントはデータに使わない。
    /// 数値テキストは1色（chalk）で、強調はサイズ/太さのみ。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class TeamStrengthController : MonoBehaviour
    {
        private TeamStrengthState _state;
        private VisualElement _root;
        private VisualElement _radar;

        private readonly List<RadarAxis> _radarAxes = new List<RadarAxis>();
        private readonly List<AbilityBar> _factors = new List<AbilityBar>();
        private readonly List<VisualElement> _axisNodes = new List<VisualElement>();
        private string _overallGrade = "D";

        private const float RadiusFactor = 0.36f;
        private const float LabelOffset = 1.20f;

        // 各指標の算出元（内訳表示）。
        private static readonly Dictionary<string, string> Composition = new Dictionary<string, string>
        {
            { "打撃力", "ミート / パワー / 弾道 / 選球眼" },
            { "投手力", "球速 / 制球 / スタミナ / 球種（エース偏重）" },
            { "守備力", "守備 / 捕球 / 肩" },
            { "機動力", "走力 / 盗塁" },
            { "選手層", "控えの厚み ＋ 投手の枚数" },
            { "精神力", "メンタル（主力平均）" },
        };

        private void OnEnable()
        {
            _state = new TeamStrengthState();
            _root = GetComponent<UIDocument>().rootVisualElement;

            var back = _root.Q<Button>("back-home");
            if (back != null) back.clicked += () => FindObjectOfType<ScreenRouter>()?.Show("HomeDashboard");

            _radar = _root.Q<VisualElement>("radar");
            if (_radar != null)
            {
                _radar.generateVisualContent += OnPaintRadar;
                _radar.RegisterCallback<GeometryChangedEvent>(_ =>
                {
                    _radar.MarkDirtyRepaint();
                    RepositionAxes();
                });
            }

            Render();
        }

        private void Render()
        {
            var v = _state.BuildView();
            _overallGrade = v.OverallGrade;

            // ヘッダー: 総合ランク チップ ＋ (値) 形式「E (41)」（1.5倍拡大）。
            var chip = _root.Q<VisualElement>("overall-chip");
            if (chip != null) { chip.Clear(); chip.Add(XlGradeChip(v.OverallGrade)); }
            var ov = _root.Q<Label>("overall-value");
            if (ov != null) ov.text = "(" + v.OverallValue + ")";

            // 右カラム: 6指標バー（内訳付き）。
            var list = _root.Q<VisualElement>("factors");
            if (list != null)
            {
                list.Clear();
                foreach (var f in v.Factors) list.Add(BuildFactor(f));
            }

            // 分析コメント（弱点を強調色＋太字、助言は通常）。リッチテキストで部分装飾。
            var analysis = _root.Q<Label>("analysis");
            if (analysis != null)
            {
                analysis.enableRichText = true;
                analysis.text = string.IsNullOrEmpty(v.AnalysisWeak)
                    ? ""
                    : $"<b><color=#E68A4A>{v.AnalysisWeak}。</color></b> {v.AnalysisAdvice}";
            }

            // レーダー描画データ。
            _radarAxes.Clear(); _radarAxes.AddRange(v.Radar);
            _factors.Clear(); _factors.AddRange(v.Factors);

            BuildAxisLabels();
            RepositionAxes();
            if (_radar != null) _radar.MarkDirtyRepaint();
        }

        // ===== レーダー軸ラベル（数値は1色統一・強調はサイズ/太さ） =====

        private void BuildAxisLabels()
        {
            if (_radar == null) return;
            foreach (var n in _axisNodes) n.RemoveFromHierarchy();
            _axisNodes.Clear();

            foreach (var f in _factors)
            {
                var node = new VisualElement();
                node.AddToClassList("ts-axis");
                node.pickingMode = PickingMode.Ignore;
                var label = new Label(f.Label); label.AddToClassList("ts-axis__l");
                var val = new Label(f.Value.ToString()); val.AddToClassList("ts-axis__v");
                node.Add(label); node.Add(val);
                _radar.Add(node);
                _axisNodes.Add(node);
            }
        }

        private void RepositionAxes()
        {
            if (_radar == null) return;
            var rect = _radar.contentRect;
            if (rect.width < 4 || rect.height < 4) return;

            var cx = rect.width * 0.5f;
            var cy = rect.height * 0.5f;
            var radius = Mathf.Min(rect.width, rect.height) * RadiusFactor;
            var n = _axisNodes.Count;

            for (var i = 0; i < n; i++)
            {
                var ang = -Mathf.PI / 2f + (Mathf.PI * 2f) * i / n;
                var lx = cx + Mathf.Cos(ang) * radius * LabelOffset;
                var ly = cy + Mathf.Sin(ang) * radius * LabelOffset;
                var node = _axisNodes[i];
                node.style.left = lx;
                node.style.top = ly;
                node.style.translate = new Translate(Length.Percent(-50), Length.Percent(-50));
            }
        }

        // ===== 右カラムの指標バー（内訳＋ランク連動色） =====

        private static VisualElement BuildFactor(AbilityBar a)
        {
            var row = new VisualElement();
            row.AddToClassList("ts-frow");

            var nameCol = new VisualElement();
            nameCol.AddToClassList("ts-frow__name");
            var l = new Label(a.Label); l.AddToClassList("ts-frow__l"); nameCol.Add(l);
            var sub = new Label(Composition.TryGetValue(a.Label, out var c) ? c : "");
            sub.AddToClassList("ts-frow__sub"); nameCol.Add(sub);
            row.Add(nameCol);

            var val = new Label(a.Value.ToString()); val.AddToClassList("ts-frow__v"); row.Add(val);

            var bar = new VisualElement(); bar.AddToClassList("ts-frow__bar");
            var fill = new VisualElement(); fill.AddToClassList("ts-frow__fill");
            fill.style.width = Length.Percent(Mathf.Clamp01(a.Pct) * 100f);
            fill.style.backgroundColor = Hex(a.BarColorHex); // ＝RankPalette.Hex(grade)
            bar.Add(fill); row.Add(bar);

            row.Add(GradeChip(a.Grade));
            return row;
        }

        // ===== レーダー描画（Painter2D＋頂点カラーメッシュ・総合ランク連動色） =====

        private void OnPaintRadar(MeshGenerationContext ctx)
        {
            var n = _radarAxes.Count;
            if (n < 3) return;
            var rect = ctx.visualElement.contentRect;
            if (rect.width < 4 || rect.height < 4) return;

            var cx = rect.width * 0.5f;
            var cy = rect.height * 0.5f;
            var radius = Mathf.Min(rect.width, rect.height) * RadiusFactor;
            var p = ctx.painter2D;

            // グリッド: 5段階（20/40/60/80/100）。最外周だけ明るく、内側は暗く細く。
            var gridInner = new Color(0.17f, 0.24f, 0.20f);
            var gridOuter = new Color(0.40f, 0.52f, 0.45f);
            for (var r = 1; r <= 5; r++)
            {
                var isOuter = r == 5;
                p.BeginPath();
                for (var i = 0; i < n; i++)
                {
                    var pt = AxisPoint(cx, cy, radius * (r / 5f), i, n);
                    if (i == 0) p.MoveTo(pt); else p.LineTo(pt);
                }
                p.ClosePath();
                p.strokeColor = isOuter ? gridOuter : gridInner;
                p.lineWidth = isOuter ? 1.6f : 1f;
                p.Stroke();
            }
            for (var i = 0; i < n; i++)
            {
                p.BeginPath();
                p.MoveTo(new Vector2(cx, cy));
                p.LineTo(AxisPoint(cx, cy, radius, i, n));
                p.strokeColor = gridInner;
                p.lineWidth = 1f; p.Stroke();
            }

            // データ塗り: 総合ランク連動色で中心→外へグラデ（黄アクセントは使わない）。
            var rc = RankPalette.Of(_overallGrade);
            var centerCol = (Color32)new Color(rc.r, rc.g, rc.b, 0.28f);
            var rimCol = (Color32)new Color(rc.r * 0.68f, rc.g * 0.68f, rc.b * 0.68f, 0.62f);

            var rim = new Vector2[n];
            for (var i = 0; i < n; i++)
            {
                var val = Mathf.Clamp01(_radarAxes[i].Value01);
                rim[i] = AxisPoint(cx, cy, radius * Mathf.Max(0.04f, val), i, n);
            }
            var mesh = ctx.Allocate(n + 1, n * 3);
            mesh.SetNextVertex(new Vertex { position = new Vector3(cx, cy, Vertex.nearZ), tint = centerCol });
            for (var i = 0; i < n; i++)
                mesh.SetNextVertex(new Vertex { position = new Vector3(rim[i].x, rim[i].y, Vertex.nearZ), tint = rimCol });
            for (var i = 0; i < n; i++)
            {
                mesh.SetNextIndex(0);
                mesh.SetNextIndex((ushort)(1 + i));
                mesh.SetNextIndex((ushort)(1 + ((i + 1) % n)));
            }

            // 輪郭線（太め 2.5px）＋頂点ドット（4px）＝ランク連動色（濃いめ）。
            var line = new Color(rc.r, rc.g, rc.b, 1f);
            p.BeginPath();
            for (var i = 0; i < n; i++) { if (i == 0) p.MoveTo(rim[i]); else p.LineTo(rim[i]); }
            p.ClosePath();
            p.strokeColor = line; p.lineWidth = 2.5f; p.Stroke();

            foreach (var pt in rim)
            {
                p.BeginPath();
                p.MoveTo(new Vector2(pt.x - 2f, pt.y));
                p.LineTo(new Vector2(pt.x, pt.y - 2f));
                p.LineTo(new Vector2(pt.x + 2f, pt.y));
                p.LineTo(new Vector2(pt.x, pt.y + 2f));
                p.ClosePath();
                p.fillColor = line; p.Fill();
            }
        }

        private static Vector2 AxisPoint(float cx, float cy, float r, int i, int n)
        {
            var ang = -Mathf.PI / 2f + (Mathf.PI * 2f) * i / n; // 真上始点・時計回り
            return new Vector2(cx + Mathf.Cos(ang) * r, cy + Mathf.Sin(ang) * r);
        }

        // ===== 補助 =====

        private static Label GradeChip(string grade)
        {
            var c = new Label(grade);
            c.AddToClassList("grade");
            c.AddToClassList("grade--" + grade);
            return c;
        }

        private static Label XlGradeChip(string grade)
        {
            var c = GradeChip(grade);
            c.AddToClassList("grade--xl");
            return c;
        }

        private static Color Hex(string hex)
            => ColorUtility.TryParseHtmlString(hex, out var c) ? c : Color.white;
    }
}
