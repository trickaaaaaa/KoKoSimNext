using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Unity.Players; // AbilityBar / RadarAxis を共用
using KokoSim.Unity.Shell;   // ScreenRouter / RankPalette（ランク色の単一ソース）
using KokoSim.Unity.Components; // 部品辞書（RankChip / AbilityRow）

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
            if (chip != null) { chip.Clear(); chip.Add(UiComponents.RankChip(v.OverallGrade, RankChipSize.XLarge)); }
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
                var pt = RadarChart.AxisPoint(cx, cy, radius * LabelOffset, i, n);
                var node = _axisNodes[i];
                node.style.left = pt.x;
                node.style.top = pt.y;
                node.style.translate = new Translate(Length.Percent(-50), Length.Percent(-50));
            }
        }

        // ===== 右カラムの指標バー（内訳＋ランク連動色） =====

        private static VisualElement BuildFactor(AbilityBar a)
            => UiComponents.AbilityRow(new AbilityRowData
            {
                Label = a.Label,
                Sub = Composition.TryGetValue(a.Label, out var c) ? c : "",
                Value = a.Value.ToString(),
                Pct = a.Pct,
                Grade = a.Grade,
                Size = AbilityRowSize.Large,
            });

        // ===== レーダー描画（Painter2D＋頂点カラーメッシュ・総合ランク連動色） =====

        private void OnPaintRadar(MeshGenerationContext ctx)
            => RadarChart.Paint(ctx, _radarAxes, _overallGrade, RadiusFactor);
    }
}
