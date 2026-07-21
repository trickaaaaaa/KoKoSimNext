using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Unity.Components;  // 部品辞書（RankChip / AbilityRow）
using KokoSim.Unity.Players;     // AbilityBar / RadarAxis を共用
using KokoSim.Unity.Shell;       // GameClock / RosterService / RankPalette

namespace KokoSim.Unity.Practice
{
    /// <summary>
    /// 練習試合画面（設計書03 §週ターン③ 週末アクション・設計書04 §名声）。
    /// 左に同県内の申込先テーブル（行高32px・数値右揃え）、右に選択した相手の6角形＋6指標＋申込条件。
    /// 主要操作は「申し込む」1つだけ（UI原則⑦）。アクセント（lamp）もそのCTAだけに使う（UI原則②）。
    /// 6角形の描画はチーム総合力パネルと同じ流儀（Painter2D＋頂点カラーメッシュ・ランク連動色）で、
    /// 軸の並びも同一なので自校のレーダーと見比べられる。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class PracticeMatchController : MonoBehaviour
    {
        private PracticeMatchState _state;
        private VisualElement _root;
        private VisualElement _radar;

        private readonly List<RadarAxis> _radarAxes = new List<RadarAxis>();
        private readonly List<AbilityBar> _factors = new List<AbilityBar>();
        private readonly List<VisualElement> _axisNodes = new List<VisualElement>();
        private string _overallGrade = "D";
        private int _selectedId = int.MinValue;

        private const float RadiusFactor = 0.34f;
        private const float LabelOffset = 1.22f;

        private void OnEnable()
        {
            _state = new PracticeMatchState();
            _root = GetComponent<UIDocument>().rootVisualElement;

            var advance = _root.Q<Button>("advance");
            if (advance != null) advance.clicked += () => { GameClock.Advance(+1); Render(); };

            var request = _root.Q<Button>("pm-request");
            if (request != null) request.clicked += () => { _state.Request(); Render(); };

            _radar = _root.Q<VisualElement>("pm-radar");
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

        /// <summary>スクショ・自動検証用に相手を選ぶ（通常は行クリック）。</summary>
        public void SelectOpponent(int schoolId)
        {
            _selectedId = schoolId;
            _state.Select(schoolId);
            Render();
        }

        private void Render()
        {
            var v = _state.BuildView();

            RenderTopBar();
            SetText("pm-own", v.OwnLabel);
            SetText("pm-funds", v.FundsText);
            SetText("pm-cost", v.CostText);
            SetText("pm-week", v.WeekLabel);
            SetText("pm-count", v.Opponents.Count + "校");

            RenderList(v.Opponents);
            RenderDetail(v.Selected);

            var status = _root.Q<Label>("pm-status");
            if (status != null)
            {
                status.text = v.StatusText;
                status.EnableInClassList("pm-status--warn", v.StatusIsWarning);
            }

            var cta = _root.Q<Button>("pm-request");
            if (cta != null)
            {
                cta.text = v.ActionLabel;
                cta.SetEnabled(v.ActionEnabled);
                cta.EnableInClassList("pm-cta--off", !v.ActionEnabled);
            }
        }

        /// <summary>共通トップバーの動的値（週・自校の総合ランク）を埋める。他画面と同じ単一ソース。</summary>
        private void RenderTopBar()
        {
            SetText("week", GameClock.CurrentLabel());
            var rank = _root.Q<VisualElement>("team-rank");
            if (rank == null) return;
            rank.Clear();
            rank.Add(UiComponents.RankChipLegacy(TeamOverall.GradeOf(RosterService.Roster)));
        }

        private void RenderList(List<PracticeOpponentRow> rows)
        {
            var host = _root.Q<VisualElement>("pm-list");
            if (host == null) return;
            host.Clear();
            foreach (var r in rows) host.Add(BuildRow(r));
        }

        private VisualElement BuildRow(PracticeOpponentRow r)
        {
            var row = new VisualElement();
            row.AddToClassList("pm-row");
            if (r.SchoolId == _selectedId) row.AddToClassList("pm-row--on");

            var name = new Label(r.Name);
            name.AddToClassList("pm-row__name");
            row.Add(name);

            var tier = new VisualElement();
            tier.AddToClassList("pm-row__tier");
            tier.Add(UiComponents.RankChip(r.TierLetter));
            row.Add(tier);

            var overall = new Label(r.Overall.ToString());
            overall.AddToClassList("pm-row__num");
            row.Add(overall);

            var trad = new Label(r.Tradition);
            trad.AddToClassList("pm-row__trad");
            row.Add(trad);

            // 受諾見込み。低い相手は淡色にして「断られやすさ」を一目で拾えるようにする（UI原則⑥）。
            var accept = new Label(r.AcceptPercent + "%");
            accept.AddToClassList("pm-row__num");
            if (r.AcceptPercent < 40) accept.AddToClassList("pm-row__num--weak");
            row.Add(accept);

            var id = r.SchoolId;
            row.RegisterCallback<ClickEvent>(_ => SelectOpponent(id));
            return row;
        }

        private void RenderDetail(PracticeOpponentDetail d)
        {
            SetText("pm-detail-name", d == null
                ? "未選択"
                : d.Name + "　総合 " + d.TierLetter + "（" + d.Overall + "）　受諾見込み " + d.AcceptPercent + "%");

            _radarAxes.Clear();
            _factors.Clear();
            if (d != null)
            {
                _overallGrade = d.TierLetter;
                _radarAxes.AddRange(d.Radar);
                _factors.AddRange(d.Factors);
            }

            var host = _root.Q<VisualElement>("pm-factors");
            if (host != null)
            {
                host.Clear();
                if (d == null)
                {
                    var empty = new Label("左の一覧から相手校を選ぶと、6指標の戦力が表示されます。");
                    empty.AddToClassList("pm-empty");
                    host.Add(empty);
                }
                else
                {
                    foreach (var f in _factors)
                        host.Add(UiComponents.AbilityRow(new AbilityRowData
                        {
                            Label = f.Label,
                            Value = f.Value.ToString(),
                            Pct = f.Pct,
                            Grade = f.Grade,
                        }));
                }
            }

            BuildAxisLabels();
            RepositionAxes();
            if (_radar != null) _radar.MarkDirtyRepaint();
        }

        private void SetText(string name, string text)
        {
            var l = _root.Q<Label>(name);
            if (l != null) l.text = text;
        }

        // ===== レーダー（チーム総合力パネルと同じ描画流儀） =====

        private void BuildAxisLabels()
        {
            if (_radar == null) return;
            foreach (var n in _axisNodes) n.RemoveFromHierarchy();
            _axisNodes.Clear();

            foreach (var f in _factors)
            {
                var node = new VisualElement();
                node.AddToClassList("pm-axis");
                node.pickingMode = PickingMode.Ignore;
                var label = new Label(f.Label); label.AddToClassList("pm-axis__l");
                var val = new Label(f.Value.ToString()); val.AddToClassList("pm-axis__v");
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
                var node = _axisNodes[i];
                node.style.left = cx + Mathf.Cos(ang) * radius * LabelOffset;
                node.style.top = cy + Mathf.Sin(ang) * radius * LabelOffset;
                node.style.translate = new Translate(Length.Percent(-50), Length.Percent(-50));
            }
        }

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

            var gridInner = new Color(0.17f, 0.24f, 0.20f);
            var gridOuter = new Color(0.40f, 0.52f, 0.45f);
            for (var r = 1; r <= 5; r++)
            {
                p.BeginPath();
                for (var i = 0; i < n; i++)
                {
                    var pt = AxisPoint(cx, cy, radius * (r / 5f), i, n);
                    if (i == 0) p.MoveTo(pt); else p.LineTo(pt);
                }
                p.ClosePath();
                p.strokeColor = r == 5 ? gridOuter : gridInner;
                p.lineWidth = r == 5 ? 1.6f : 1f;
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
    }
}
