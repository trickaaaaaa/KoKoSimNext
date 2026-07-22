using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Unity.Shell;
using KokoSim.Unity.Components;

namespace KokoSim.Unity.Players
{
    /// <summary>
    /// 選手詳細のコントローラ（設計書06 §3.3、mock「選手詳細」）。
    /// PlayerSelection.Index の1名を PlayerDetailState から整形してバインドする。
    /// 能力バランスは Painter2D で実描画（現在能力から）。成長推移・公式戦成績はエンジン未接続で空状態。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class PlayerDetailController : MonoBehaviour
    {
        private PlayerDetailState _state;
        private VisualElement _root;
        private Button _designateButton;
        private RadarChartView _radar;

        // 能力バランスの半径比（軸ラベルは出さず、左の能力バー一覧が凡例を兼ねる）。
        private const float RadiusFactor = 0.42f;

        // 球種変化チャート（プロスピ風・扇形セクター塗り）。
        // 扇の長さ＝変化量 / 幅＝固定（ストレートのみ伸びで可変） / 色＝キレ等級（RankPalette）。
        private VisualElement _chart;
        private readonly List<PitchArc> _chartArcs = new List<PitchArc>();
        private Vector2 _chartCenter;
        private const float BallR = 22f;
        // 扇の内半径（ボール縁のすぐ外側）。ここまでは引き伸ばさず、外側だけ横に伸ばす。
        private const float ArcInner = BallR + 4f;

        // 扇1枚分の描画パラメータ（LayoutChart で確定し OnPaintChart が読む）。
        private struct PitchArc
        {
            public float AngleDeg;    // 変化方向（画面座標系・0度=右／時計回り）
            public float HalfDeg;     // 扇の半幅
            public float Outer;       // 外半径（＝変化量）
            public Color Col;         // キレ等級色
        }

        // チャート枠は横長（カード幅いっぱい・高さ300前後）なので、扇とラベルを横方向へ引き伸ばして
        // 余白を使い切る。伸長は全球種に同率でかかるので、方向ごとの相対比較は保たれる。
        private Vector2 _chartScale = Vector2.one;

        private void OnEnable()
        {
            _state = new PlayerDetailState();
            _root = GetComponent<UIDocument>().rootVisualElement;

            // 背番号は純数字（index+1）＝コンデンス数字書体（design-16 §2「純数字セルのみ」）。62px の大見出し数字なので f-num-bd。
            _root.Q<Label>("number")?.AddToClassList("f-num-bd");

            var back = _root.Q<Button>("back-list");
            if (back != null) back.clicked += () => FindObjectOfType<ScreenRouter>()?.Show("PlayerList");

            // 主将に指名（設計書09 §8）: 新チーム発足時（夏の3年引退直後）だけ受け付ける。
            // 期間外・候補外は非活性にして理由を添えるため、ボタン参照を保持しておく。
            _designateButton = _root.Q<Button>("designate-captain");
            if (_designateButton != null) _designateButton.clicked += () =>
            {
                if (_state.DesignateCaptain(PlayerSelection.Index)) Render();
            };

            // レーダーは部品辞書の共通部品（チーム総合力・練習試合・試合開始前と同一の描画）。
            _radar = new RadarChartView(_root.Q<VisualElement>("radar"), RadiusFactor,
                labelSize: RadarLabelSize.None);

            _chart = _root.Q<VisualElement>("pitch-chart");
            if (_chart != null)
            {
                _chart.generateVisualContent += OnPaintChart;
                _chart.RegisterCallback<GeometryChangedEvent>(_ => LayoutChart());
            }

            Render();
        }

        private void Render()
        {
            var v = _state.BuildView(PlayerSelection.Index);

            SetText("number", v.Number);
            SetText("name", v.Name);
            SetText("cond", v.Condition);
            SetColor("cond", v.ConditionColorHex);
            // 調子は表情顔（ConditionFace）が主。文字表記は詳細画面なので併記する（issue #51）。
            var condFaceHost = _root.Q<VisualElement>("cond-face");
            if (condFaceHost != null)
            {
                condFaceHost.Clear();
                var face = new ConditionFace();
                face.Set(v.ConditionLevel);
                condFaceHost.Add(face);
            }
            SetDisplay("captain-badge", v.IsCaptain);
            // 既に主将なら指名ボタンを隠す（重複指名の抑止）。
            SetDisplay("designate-captain", !v.IsCaptain);
            // 指名ウィンドウ外・候補外は押せない。理由はボタン脇に添える（設計書09 §8）。
            if (_designateButton != null) _designateButton.SetEnabled(v.CanDesignateCaptain);
            SetDisplay("designate-reason", !v.IsCaptain && !v.CanDesignateCaptain);
            SetText("designate-reason", v.DesignateReason);
            SetText("meta-grade", v.GradeLabel);
            SetText("meta-tb", v.ThrowsBats);
            // 投法・最速は全選手で表示（役割でゲートしない, Issue #93）。
            SetText("meta-style", v.PitchStyle);
            SetText("meta-velo", "最速 " + v.TopVelocityKmh + " km/h");
            // 故障（設計書03 §3.5）: 怪我している時だけ警告色で出す（UI原則②）。
            SetDisplay("meta-injury", v.Injury.Length > 0);
            SetText("meta-injury", v.Injury);

            var chip = _root.Q<VisualElement>("overall-chip");
            if (chip != null) { chip.Clear(); chip.Add(UiComponents.RankChip(v.OverallGrade, RankChipSize.Large)); }

            BuildList("pitcher-abils", v.PitcherAbilities, BuildAbil);
            BuildList("fielder-abils", v.FielderAbilities, BuildAbil);
            BuildList("hidden-list", v.Hidden, BuildHidden);

            // 球種変化チャート（全選手・未習得ならストレートのみ, Issue #93）。空状態文言は廃止した。
            BuildPitchChart(v.Pitches, v.HasPitchData);

            // 特殊能力。
            BuildList("skills-list", v.Skills, BuildSkill);
            SetDisplay("skills-empty", !v.HasSkills);

            // 簡易成績。
            SetText("tourn-label", "今大会（データ未接続）");
            BuildStatRow("tourn-stats", v.TournamentStats);
            BuildStatRow("career-stats", v.CareerStats);

            // レーダー描画データ更新（塗り色は総合ランク連動＝部品辞書の既定）。
            _radar.SetData(v.Radar, v.OverallGrade);
        }

        // ===== 行ビルダー =====

        private static VisualElement BuildAbil(AbilityBar a)
            => UiComponents.AbilityRow(new AbilityRowData
            {
                Label = a.Label,
                Value = a.Value.ToString(),
                Pct = a.Pct,
                Grade = a.Grade,
                Divided = true,
            });

        private static VisualElement BuildHidden(HiddenParam h)
        {
            var row = new VisualElement();
            row.AddToClassList("pd2-hidden-row");
            var k = new Label(h.Key); k.AddToClassList("pd2-hidden-k"); row.Add(k);
            var val = new Label(h.Known ? h.Value : "？");
            val.AddToClassList(h.Known ? "pd2-hidden-v" : "pd2-hidden-v--unknown");
            row.Add(val);
            return row;
        }

        // ===== 球種変化チャート（プロスピ風） =====

        // 各球種のラベルチップを配置（絶対配置）。位置は LayoutChart でレイアウト後に確定する。
        private void BuildPitchChart(List<PitchData> pitches, bool has)
        {
            if (_chart == null) return;
            _chart.Clear();            // ラベル除去（generateVisualContent はエレメント自体に残る）
            _chartArcs.Clear();
            _chart.style.display = has ? DisplayStyle.Flex : DisplayStyle.None;
            if (!has) { _chart.MarkDirtyRepaint(); return; }

            foreach (var pt in pitches)
            {
                var chip = new VisualElement();
                chip.AddToClassList("pd2-pchip");
                chip.userData = pt;
                var name = new Label(pt.Name); name.AddToClassList("pd2-pchip__name"); chip.Add(name);
                chip.Add(UiComponents.RankChip(pt.Kire));
                _chart.Add(chip);
            }
            LayoutChart();
            _chart.MarkDirtyRepaint();
        }

        // 中心・半径から扇の寸法を決め、各ラベルを扇の先端の外側へ置く。
        private void LayoutChart()
        {
            if (_chart == null) return;
            var rect = _chart.contentRect;
            if (rect.width < 40f || rect.height < 40f) return;

            _chartCenter = new Vector2(rect.width * 0.5f, rect.height * 0.5f);
            var radius = Mathf.Min(rect.width, rect.height) * 0.44f;
            _chartScale = new Vector2(Mathf.Clamp(rect.width / Mathf.Max(1f, rect.height) * 0.5f, 1f, 2.2f), 1f);
            _chartArcs.Clear();
            var chips = new List<VisualElement>();
            var boxes = new List<Rect>();

            foreach (var child in _chart.Children())
            {
                var pt = child.userData as PitchData;
                if (pt == null) continue;

                var dir = new Vector2(pt.DirX, pt.DirY);
                if (dir.sqrMagnitude < 0.0001f) dir = new Vector2(0f, -1f);
                dir = dir.normalized;

                // 扇の長さ＝変化量。ストレートは Break01 が短尺固定なので自然に短い扇になる。
                var outer = Mathf.Max(ArcInner + 10f, Mathf.Lerp(radius * 0.26f, radius * 0.92f, Mathf.Clamp01(pt.Break01)));
                // 幅は固定。ストレートだけ「伸び」を幅で表す（1-B: 長さではなく太さで読ませる）。
                var half = pt.IsFastball ? Mathf.Lerp(13f, 26f, Mathf.Clamp01(pt.Extend01)) : 15f;

                var angleDeg = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
                _chartArcs.Add(new PitchArc
                {
                    AngleDeg = angleDeg,
                    HalfDeg = half,
                    Outer = outer,
                    Col = RankPalette.Of(pt.Kire),
                });

                var lw = child.resolvedStyle.width;
                var lh = child.resolvedStyle.height;
                if (lw < 1f) lw = 96f;
                if (lh < 1f) lh = 28f;
                // ラベルは扇の先端の「外側」へ逃がす（チップの近い辺が先端に接する位置）。
                // 隣り合う球種どうしのチップ重なりも、方向ごとに外へ押し出すことで減る。
                var tip = PolarPoint(_chartCenter, outer + 12f, angleDeg);
                var end = tip + new Vector2(dir.x * lw * 0.5f, dir.y * lh * 0.5f);
                chips.Add(child);
                boxes.Add(new Rect(end.x - lw * 0.5f, end.y - lh * 0.5f, lw, lh));
            }

            SeparateChips(boxes, rect);
            for (var i = 0; i < chips.Count; i++)
            {
                chips[i].style.left = boxes[i].x;
                chips[i].style.top = boxes[i].y;
            }
            _chart.MarkDirtyRepaint();
        }

        // ラベルの重なりを解く（変化方向が近い球種同士＝落ち球が重なりやすい）。
        // 侵入量の小さい軸へ押し分ける AABB リラクゼーション。最後にチャート矩形へ収める。
        private static void SeparateChips(List<Rect> boxes, Rect rect)
        {
            const float Gap = 4f;
            for (var pass = 0; pass < 12; pass++)
            {
                var moved = false;
                for (var i = 0; i < boxes.Count; i++)
                for (var j = i + 1; j < boxes.Count; j++)
                {
                    var a = boxes[i];
                    var b = boxes[j];
                    var dx = (a.center.x - b.center.x);
                    var dy = (a.center.y - b.center.y);
                    var ox = (a.width + b.width) * 0.5f + Gap - Mathf.Abs(dx);
                    var oy = (a.height + b.height) * 0.5f + Gap - Mathf.Abs(dy);
                    if (ox <= 0f || oy <= 0f) continue;

                    if (oy <= ox)
                    {
                        var s = (dy >= 0f ? 1f : -1f) * oy * 0.5f;
                        a.y += s; b.y -= s;
                    }
                    else
                    {
                        var s = (dx >= 0f ? 1f : -1f) * ox * 0.5f;
                        a.x += s; b.x -= s;
                    }
                    boxes[i] = a; boxes[j] = b;
                    moved = true;
                }
                if (!moved) break;
            }

            for (var i = 0; i < boxes.Count; i++)
            {
                var b = boxes[i];
                b.x = Mathf.Clamp(b.x, 0f, Mathf.Max(0f, rect.width - b.width));
                b.y = Mathf.Clamp(b.y, 0f, Mathf.Max(0f, rect.height - b.height));
                boxes[i] = b;
            }
        }

        private void OnPaintChart(MeshGenerationContext ctx)
        {
            var p = ctx.painter2D;
            var c = _chartCenter;
            if (c.x < 1f && c.y < 1f) return;

            // 各球種の扇（塗り＝キレ等級色の半透明、先端に同色の帯で変化量の端を明示）。
            // 横方向へ引き伸ばすため Painter2D.Arc は使わず、円弧を等分サンプルした多角形で描く。
            const int Seg = 14;
            foreach (var a in _chartArcs)
            {
                var from = a.AngleDeg - a.HalfDeg;
                var to = a.AngleDeg + a.HalfDeg;

                p.BeginPath();
                p.MoveTo(PolarPoint(c, ArcInner, from));
                for (var i = 1; i <= Seg; i++) p.LineTo(PolarPoint(c, ArcInner, Mathf.Lerp(from, to, i / (float)Seg)));
                for (var i = Seg; i >= 0; i--) p.LineTo(PolarPoint(c, a.Outer, Mathf.Lerp(from, to, i / (float)Seg)));
                p.ClosePath();
                p.fillColor = new Color(a.Col.r, a.Col.g, a.Col.b, 0.26f);
                p.Fill();

                p.BeginPath();
                p.MoveTo(PolarPoint(c, a.Outer, from));
                for (var i = 1; i <= Seg; i++) p.LineTo(PolarPoint(c, a.Outer, Mathf.Lerp(from, to, i / (float)Seg)));
                p.strokeColor = new Color(a.Col.r, a.Col.g, a.Col.b, 0.90f);
                p.lineWidth = 2.5f;
                p.Stroke();
            }

            DrawBall(p, c);
        }

        // 中心のボール（2-A: ベジェ縫い目＋ステッチ、淡いハイライト、薄い影）。
        private static void DrawBall(Painter2D p, Vector2 c)
        {
            // 接地影（扇の上に浮かせず、ボールの下に薄く敷くだけ）。
            p.BeginPath();
            p.Arc(c + new Vector2(0f, 2.5f), BallR * 1.04f, Deg(0f), Deg(360f));
            p.fillColor = new Color(0f, 0f, 0f, 0.30f); p.Fill();

            // 球体。
            p.BeginPath();
            p.Arc(c, BallR, Deg(0f), Deg(360f));
            p.fillColor = new Color(0.941f, 0.957f, 0.918f); p.Fill();

            // 左上のハイライト（艶。装飾のグローではなく球形を示す面）。
            p.BeginPath();
            p.Arc(c + new Vector2(-BallR * 0.30f, -BallR * 0.32f), BallR * 0.46f, Deg(0f), Deg(360f));
            p.fillColor = new Color(1f, 1f, 1f, 0.55f); p.Fill();

            // 縁取り（背景から球を切り出す最小限の線）。
            p.BeginPath();
            p.Arc(c, BallR, Deg(0f), Deg(360f));
            p.strokeColor = new Color(0.30f, 0.35f, 0.30f); p.lineWidth = 1f; p.Stroke();

            // 縫い目2本＝左右対称のベジェ曲線。各6個のステッチ短線を曲線の接線に直交して置く。
            var red = new Color(0.85f, 0.30f, 0.22f);
            DrawSeam(p, c, -1f, red);
            DrawSeam(p, c, 1f, red);
        }

        // side = -1 で左の縫い目、+1 で右の縫い目（中心へ向かって弓なりに反る）。
        private static void DrawSeam(Painter2D p, Vector2 c, float side, Color red)
        {
            var p0 = c + new Vector2(side * BallR * 0.66f, -BallR * 0.80f);
            var c1 = c + new Vector2(-side * BallR * 0.24f, -BallR * 0.44f);
            var c2 = c + new Vector2(-side * BallR * 0.24f, BallR * 0.44f);
            var p3 = c + new Vector2(side * BallR * 0.66f, BallR * 0.80f);

            p.BeginPath();
            p.MoveTo(p0);
            p.BezierCurveTo(c1, c2, p3);
            p.strokeColor = red; p.lineWidth = 1.4f; p.Stroke();

            for (var i = 0; i < 6; i++)
            {
                var t = 0.10f + 0.16f * i;
                var pos = Cubic(p0, c1, c2, p3, t);
                var tan = Cubic(p0, c1, c2, p3, t + 0.02f) - Cubic(p0, c1, c2, p3, t - 0.02f);
                if (tan.sqrMagnitude < 0.0001f) continue;
                var n = new Vector2(-tan.y, tan.x).normalized * (BallR * 0.20f);
                p.BeginPath();
                p.MoveTo(pos - n);
                p.LineTo(pos + n);
                p.strokeColor = red; p.lineWidth = 1.1f; p.Stroke();
            }
        }

        private static Vector2 Cubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
        {
            var u = 1f - t;
            return u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
        }

        // 内半径ぶんは等方（ボール縁に必ず接する）、その外側だけ横へ引き伸ばす。
        private Vector2 PolarPoint(Vector2 c, float r, float deg)
        {
            var rad = deg * Mathf.Deg2Rad;
            var u = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
            return c + u * ArcInner + Scale(u * Mathf.Max(0f, r - ArcInner));
        }

        // 横長枠に合わせた非等方スケール（全球種同率なので相対比較は保たれる）。
        private Vector2 Scale(Vector2 v) => new Vector2(v.x * _chartScale.x, v.y * _chartScale.y);

        private static Angle Deg(float d) => new Angle(d, AngleUnit.Degree);

        private static VisualElement BuildSkill(SkillInfo s)
        {
            var wrap = new VisualElement();
            wrap.AddToClassList("pd2-skill");
            var name = new Label(s.Name); name.AddToClassList("pd2-skill__name"); wrap.Add(name);
            var desc = new Label(s.Desc); desc.AddToClassList("pd2-skill__desc"); wrap.Add(desc);
            return wrap;
        }

        private void BuildStatRow(string container, List<(string Label, string Value)> stats)
        {
            var box = _root.Q<VisualElement>(container);
            if (box == null) return;
            box.Clear();
            foreach (var s in stats)
            {
                var cell = new VisualElement(); cell.AddToClassList("pd2-stat");
                var val = new Label(s.Value); val.AddToClassList("pd2-stat__v"); val.AddToClassList("f-num-bd"); cell.Add(val);
                var k = new Label(s.Label); k.AddToClassList("pd2-stat__k"); cell.Add(k);
                box.Add(cell);
            }
        }

        // ===== 補助 =====

        private static Color Hex(string hex)
        {
            return ColorUtility.TryParseHtmlString(hex, out var c) ? c : Color.white;
        }

        private void SetText(string name, string text)
        {
            var l = _root.Q<Label>(name);
            if (l != null) { l.text = text; return; }
            var b = _root.Q<Button>(name);
            if (b != null) b.text = text;
        }

        private void SetColor(string name, string hex)
        {
            var l = _root.Q<Label>(name);
            if (l != null) l.style.color = Hex(hex);
        }

        private void SetDisplay(string name, bool visible)
        {
            var e = _root.Q<VisualElement>(name);
            if (e != null) e.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void BuildList<T>(string container, List<T> items, System.Func<T, VisualElement> builder)
        {
            var box = _root.Q<VisualElement>(container);
            if (box == null) return;
            box.Clear();
            foreach (var it in items) box.Add(builder(it));
        }
    }
}
