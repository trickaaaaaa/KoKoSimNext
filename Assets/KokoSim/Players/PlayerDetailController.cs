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
        private readonly List<RadarAxis> _radarAxes = new List<RadarAxis>();

        // 球種変化チャート（プロスピ風）。
        private VisualElement _chart;
        private readonly List<(Vector2 End, Color Col)> _chartLines = new List<(Vector2, Color)>();
        private Vector2 _chartCenter;
        private const float BallR = 15f;

        private void OnEnable()
        {
            _state = new PlayerDetailState();
            _root = GetComponent<UIDocument>().rootVisualElement;

            var back = _root.Q<Button>("back-list");
            if (back != null) back.clicked += () => FindObjectOfType<ScreenRouter>()?.Show("PlayerList");

            // 主将に指名（設計書09 §8）: 新チーム発足時（夏の3年引退直後）だけ受け付ける。
            // 期間外・候補外は非活性にして理由を添えるため、ボタン参照を保持しておく。
            _designateButton = _root.Q<Button>("designate-captain");
            if (_designateButton != null) _designateButton.clicked += () =>
            {
                if (_state.DesignateCaptain(PlayerSelection.Index)) Render();
            };

            var radar = _root.Q<VisualElement>("radar");
            if (radar != null)
            {
                radar.generateVisualContent += OnPaintRadar;
                radar.RegisterCallback<GeometryChangedEvent>(_ => radar.MarkDirtyRepaint());
            }

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

            SetText("role", v.IsPitcher ? "P" : "F");
            SetText("role-sub", v.RoleLabel);
            SetText("number", v.Number);
            SetText("name", v.Name);
            SetText("cond", v.Condition);
            SetColor("cond", v.ConditionColorHex);
            SetDisplay("captain-badge", v.IsCaptain);
            // 既に主将なら指名ボタンを隠す（重複指名の抑止）。
            SetDisplay("designate-captain", !v.IsCaptain);
            // 指名ウィンドウ外・候補外は押せない。理由はボタン脇に添える（設計書09 §8）。
            if (_designateButton != null) _designateButton.SetEnabled(v.CanDesignateCaptain);
            SetDisplay("designate-reason", !v.IsCaptain && !v.CanDesignateCaptain);
            SetText("designate-reason", v.DesignateReason);
            SetText("meta-grade", v.GradeLabel);
            SetText("meta-pos", v.PosParen);
            SetText("meta-tb", v.ThrowsBats);
            SetDisplay("meta-style", v.IsPitcher);
            SetText("meta-style", v.PitchStyle);
            SetDisplay("meta-velo", v.IsPitcher);
            SetText("meta-velo", "最速 " + v.TopVelocityKmh + " km/h");

            var chip = _root.Q<VisualElement>("overall-chip");
            if (chip != null) { chip.Clear(); chip.Add(UiComponents.RankChip(v.OverallGrade, RankChipSize.Large)); }

            BuildList("pitcher-abils", v.PitcherAbilities, BuildAbil);
            BuildList("fielder-abils", v.FielderAbilities, BuildAbil);
            BuildList("hidden-list", v.Hidden, BuildHidden);

            // 球種変化チャート（プロスピ風・投手のみ）。
            BuildPitchChart(v.Pitches, v.HasPitchData);
            SetDisplay("pitch-empty", !v.HasPitchData);
            SetText("pitch-empty", v.IsPitcher ? "登録球種がありません" : "投手ではありません");

            // 特殊能力。
            BuildList("skills-list", v.Skills, BuildSkill);
            SetDisplay("skills-empty", !v.HasSkills);

            // 簡易成績。
            SetText("tourn-label", "今大会（データ未接続）");
            BuildStatRow("tourn-stats", v.TournamentStats);
            BuildStatRow("career-stats", v.CareerStats);

            // レーダー描画データ更新。
            _radarAxes.Clear();
            _radarAxes.AddRange(v.Radar);
            var radar = _root.Q<VisualElement>("radar");
            if (radar != null) radar.MarkDirtyRepaint();
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
            _chartLines.Clear();
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

        // 中心・半径から各ラベルを変化方向×変化量の位置へ置き、線の終点を記録する。
        private void LayoutChart()
        {
            if (_chart == null) return;
            var rect = _chart.contentRect;
            if (rect.width < 40f || rect.height < 40f) return;

            _chartCenter = new Vector2(rect.width * 0.5f, rect.height * 0.5f);
            var radius = Mathf.Min(rect.width, rect.height) * 0.40f;
            _chartLines.Clear();

            foreach (var child in _chart.Children())
            {
                var pt = child.userData as PitchData;
                if (pt == null) continue;

                var dir = new Vector2(pt.DirX, pt.DirY);
                if (dir.sqrMagnitude < 0.0001f) dir = new Vector2(0f, -1f);
                dir = dir.normalized;

                // 変化量が小さくても中心から十分離す（半径の60〜100%）。
                var reach = radius * (0.60f + 0.40f * Mathf.Clamp01(pt.Break01));
                var end = _chartCenter + dir * reach;

                var lw = child.resolvedStyle.width;
                var lh = child.resolvedStyle.height;
                if (lw < 1f) lw = 96f;
                if (lh < 1f) lh = 28f;
                child.style.left = end.x - lw * 0.5f;
                child.style.top = end.y - lh * 0.5f;

                _chartLines.Add((end, RankPalette.Of(pt.Kire)));
            }
            _chart.MarkDirtyRepaint();
        }

        private void OnPaintChart(MeshGenerationContext ctx)
        {
            var p = ctx.painter2D;
            var c = _chartCenter;
            if (c.x < 1f && c.y < 1f) return;

            // 変化方向の点線（中心ボール縁から終点まで、色＝キレ等級）。
            foreach (var ln in _chartLines)
            {
                var d = ln.End - c;
                var dist = d.magnitude;
                if (dist < 1f) continue;
                d /= dist;
                for (var s = BallR + 3f; s < dist; s += 7f)
                {
                    var pos = c + d * s;
                    p.BeginPath();
                    p.Arc(pos, 2.3f, new Angle(0f, AngleUnit.Degree), new Angle(360f, AngleUnit.Degree));
                    p.fillColor = ln.Col; p.Fill();
                }
            }

            // 中心のボール（白丸＋赤い縫い目2本）。
            p.BeginPath();
            p.Arc(c, BallR, new Angle(0f, AngleUnit.Degree), new Angle(360f, AngleUnit.Degree));
            p.fillColor = new Color(0.941f, 0.957f, 0.918f); p.Fill();
            p.BeginPath();
            p.Arc(c, BallR, new Angle(0f, AngleUnit.Degree), new Angle(360f, AngleUnit.Degree));
            p.strokeColor = new Color(0.30f, 0.35f, 0.30f); p.lineWidth = 1f; p.Stroke();

            var red = new Color(0.85f, 0.30f, 0.22f);
            p.strokeColor = red; p.lineWidth = 1.6f;
            p.BeginPath();
            p.Arc(c + new Vector2(-BallR * 1.35f, 0f), BallR * 1.55f,
                new Angle(-32f, AngleUnit.Degree), new Angle(32f, AngleUnit.Degree));
            p.Stroke();
            p.BeginPath();
            p.Arc(c + new Vector2(BallR * 1.35f, 0f), BallR * 1.55f,
                new Angle(148f, AngleUnit.Degree), new Angle(212f, AngleUnit.Degree));
            p.Stroke();
        }

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
                var val = new Label(s.Value); val.AddToClassList("pd2-stat__v"); cell.Add(val);
                var k = new Label(s.Label); k.AddToClassList("pd2-stat__k"); cell.Add(k);
                box.Add(cell);
            }
        }

        // ===== レーダー描画（Painter2D） =====

        private void OnPaintRadar(MeshGenerationContext ctx)
        {
            var n = _radarAxes.Count;
            if (n < 3) return;
            var rect = ctx.visualElement.contentRect;
            if (rect.width < 4 || rect.height < 4) return;

            var cx = rect.width * 0.5f;
            var cy = rect.height * 0.5f;
            var radius = Mathf.Min(rect.width, rect.height) * 0.42f;
            var p = ctx.painter2D;

            // グリッド（0.5, 1.0 リング）。
            var grid = new Color(0.24f, 0.33f, 0.27f);
            foreach (var ring in new[] { 0.5f, 1.0f })
            {
                p.BeginPath();
                for (var i = 0; i < n; i++)
                {
                    var pt = Vertex(cx, cy, radius * ring, i, n);
                    if (i == 0) p.MoveTo(pt); else p.LineTo(pt);
                }
                p.ClosePath();
                p.strokeColor = grid; p.lineWidth = 1f; p.Stroke();
            }
            // スポーク。
            for (var i = 0; i < n; i++)
            {
                p.BeginPath();
                p.MoveTo(new Vector2(cx, cy));
                p.LineTo(Vertex(cx, cy, radius, i, n));
                p.strokeColor = grid; p.lineWidth = 1f; p.Stroke();
            }
            // 値ポリゴン。
            p.BeginPath();
            for (var i = 0; i < n; i++)
            {
                var val = Mathf.Clamp01(_radarAxes[i].Value01);
                var pt = Vertex(cx, cy, radius * Mathf.Max(0.04f, val), i, n);
                if (i == 0) p.MoveTo(pt); else p.LineTo(pt);
            }
            p.ClosePath();
            p.fillColor = new Color(0.961f, 0.776f, 0.290f, 0.28f); // #F5C64A 28%
            p.Fill();
            p.strokeColor = new Color(0.961f, 0.776f, 0.290f, 1f);
            p.lineWidth = 2f; p.Stroke();
        }

        private static Vector2 Vertex(float cx, float cy, float r, int i, int n)
        {
            // 頂点は真上始点、時計回り。
            var ang = -Mathf.PI / 2f + (Mathf.PI * 2f) * i / n;
            return new Vector2(cx + Mathf.Cos(ang) * r, cy + Mathf.Sin(ang) * r);
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
