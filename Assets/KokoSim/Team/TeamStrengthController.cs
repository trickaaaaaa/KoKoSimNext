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
        private RadarChartView _radar;

        private readonly List<AbilityBar> _factors = new List<AbilityBar>();

        private const float RadiusFactor = 0.36f;
        private const float LabelOffset = 1.20f;

        // 各指標の算出元（内訳表示）。
        private static readonly Dictionary<string, string> Composition = new Dictionary<string, string>
        {
            { "打撃力", "ミート / パワー / 弾道 / 選球眼" },
            { "投手力", "球速 / 制球 / スタミナ / キレ（エース偏重）" },
            { "守備力", "守備 / 捕球 / 肩" },
            { "機動力", "走力 / 盗塁" },
            { "選手層", "控えの厚み ＋ 投手の枚数" },
            { "精神力", "メンタル（主力平均）" },
        };

        private void OnEnable()
        {
            _state = new TeamStrengthState();
            _root = GetComponent<UIDocument>().rootVisualElement;

            // 共通トップバーの「今週を進める」（他のタブ画面と同じ配線）。
            var advance = _root.Q<Button>("advance");
            if (advance != null) advance.clicked += () => WeekAdvance.FromSideScreen(Render);

            // レーダーは部品辞書の共通部品（描画＋軸ラベル配置は RadarChartView が持つ）。
            _radar = new RadarChartView(_root.Q<VisualElement>("radar"), RadiusFactor, LabelOffset,
                RadarLabelSize.Large);

            Render();
        }

        private void Render()
        {
            RenderTopBar();
            var v = _state.BuildView();

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
                foreach (var f in v.Factors)
                {
                    var row = BuildFactor(f);
                    // 精神力の行の右にチーム状態の言葉を並べる（設計書23 §4・issue #1600）。
                    // ほかの行には同じ幅の空き枠を置く＝棒と数値の列がそろう（UI原則①）。
                    row.Add(CohesionTag(f.Label == "精神力" ? v.TeamState : null));
                    list.Add(row);
                }
            }

            // 分析コメント（弱点は実ウェイトの太字＋主文字色、助言は通常）。
            var weak = _root.Q<Label>("analysis-weak");
            var analysis = _root.Q<Label>("analysis");
            if (weak != null) weak.text = string.IsNullOrEmpty(v.AnalysisWeak) ? "" : v.AnalysisWeak + "。";
            if (analysis != null) analysis.text = string.IsNullOrEmpty(v.AnalysisWeak) ? "" : v.AnalysisAdvice;

            // レーダー描画データ（軸ラベル＋数値の生成/配置は共通部品に委ねる）。
            _factors.Clear(); _factors.AddRange(v.Factors);
            _radar.SetData(v.Radar, v.OverallGrade);
        }

        /// <summary>共通トップバーの動的値（週・メーター・校名・自校の総合ランク）。他画面と同じ単一ソース。</summary>
        private void RenderTopBar()
        {
            KokoSim.Unity.Components.ScoreboardStrip.Fill(_root);
            TopBarMeters.Fill(_root);
            var rank = _root.Q<VisualElement>("team-rank");
            if (rank == null) return;
            rank.Clear();
            rank.Add(UiComponents.RankChipLegacy(TeamOverall.GradeOf(RosterService.Active)));
        }

        private static VisualElement CohesionTag(string word)
        {
            var tag = new VisualElement();
            tag.AddToClassList("tsp-cohesion");
            if (word == null) return tag;
            var k = new Label("チーム状態");
            k.AddToClassList("tsp-cohesion__k");
            tag.Add(k);
            var w = new Label(word);
            w.AddToClassList("tsp-cohesion__v");
            tag.Add(w);
            return tag;
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

    }
}
