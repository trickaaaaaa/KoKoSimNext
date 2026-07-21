using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Unity.Components; // 部品辞書（RankChip / AbilityRow）

namespace KokoSim.Unity.Players
{
    /// <summary>
    /// 選手一覧のコントローラ（設計書06 §3.2）。UIDocument（sourceAsset = PlayerList.uxml）へ
    /// PlayerListState（ViewModel）をバインドする。ソート/フィルタボタンで再描画。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class PlayerListController : MonoBehaviour
    {
        private PlayerListState _state;
        private VisualElement _root;

        private void OnEnable()
        {
            _state = new PlayerListState();
            _root = GetComponent<UIDocument>().rootVisualElement;

            var sortBtn = _root.Q<Button>("sort-btn");
            if (sortBtn != null) sortBtn.clicked += () => { _state.CycleSort(); Render(); };

            var filterBtn = _root.Q<Button>("filter-btn");
            if (filterBtn != null) filterBtn.clicked += () => { _state.CycleFilter(); Render(); };

            Render();
        }

        private void Render()
        {
            var v = _state.BuildView();

            SetText("counts", "計" + v.Total + "名（野" + v.BatterCount + "・投" + v.PitcherCount + "）");
            SetText("sort-btn", "並び: " + v.SortLabel);
            SetText("filter-btn", "表示: " + v.FilterLabel);

            // 共通トップバー（スコアボード）: 掲示板の升目（週・夏予選までの残り）とチーム総合力ランクを埋める。
            KokoSim.Unity.Components.ScoreboardStrip.Fill(_root);

            var rank = _root.Q<VisualElement>("team-rank");
            if (rank != null)
            {
                rank.Clear();
                rank.Add(UiComponents.RankChipLegacy(v.TeamRankGrade));
            }

            var rows = _root.Q<VisualElement>("player-rows");
            if (rows == null) return;
            rows.Clear();
            foreach (var r in v.Rows) rows.Add(BuildRow(r));
        }

        private VisualElement BuildRow(PlayerRow r)
        {
            var row = new VisualElement();
            row.AddToClassList("row");
            row.AddToClassList("row--click");

            // 行クリックで選手詳細へ（安定 index を受け渡す）。
            var idx = r.Index;
            row.RegisterCallback<ClickEvent>(_ =>
            {
                PlayerSelection.Index = idx;
                FindObjectOfType<KokoSim.Unity.Shell.ScreenRouter>()?.Show("PlayerDetail");
            });

            row.Add(Cell(r.Name, "cell--name"));
            row.Add(Cell(r.GradeLabel, "cell--narrow"));
            row.Add(Cell(r.Position, "cell--narrow"));

            // 総合（等級チップ）
            var overall = new VisualElement();
            overall.AddToClassList("cell");
            overall.AddToClassList("cell--narrow");
            overall.style.flexDirection = FlexDirection.Row;
            overall.style.alignItems = Align.Center;
            overall.Add(UiComponents.RankChipLegacy(r.OverallGrade));
            overall.Add(new Label(r.OverallValue.ToString()));
            row.Add(overall);

            // 主要能力（チップ＋ラベルの並び）
            var ability = new VisualElement();
            ability.AddToClassList("cell");
            ability.AddToClassList("cell--wide");
            ability.AddToClassList("ability-cell");
            foreach (var chip in r.Abilities)
            {
                ability.Add(UiComponents.RankChipLegacy(chip.Grade));
                var lbl = new Label(chip.Label);
                lbl.style.marginRight = 10;
                lbl.style.fontSize = 11;
                ability.Add(lbl);
            }
            row.Add(ability);

            // 調子／故障（設計書03 §3.5・UI原則⑥）: 怪我中はこの列が「傷病名・段階」の警告表示になる。
            // 離脱中の選手の調子は判断材料にならないので列は増やさず差し替える（詳細は行クリック→選手詳細）。
            var injured = r.Injury.Length > 0;
            var cond = Cell(injured ? r.Injury : r.Condition, "cell--narrow");
            cond.AddToClassList(injured ? "cond--worst" : CondClass(r.ConditionLevel));
            row.Add(cond);

            return row;
        }

        // ===== 補助 =====

        // 調子5段階 → 色クラス（tokens.uss の調子色を写した KokoSimTheme.uss .cond--* が単一ソース）。
        // 表示文字列で比較すると到達不能分岐を生むため、必ず enum で分岐する。
        private static string CondClass(KokoSim.Engine.Players.Condition c)
        {
            switch (c)
            {
                case KokoSim.Engine.Players.Condition.Excellent: return "cond--best";
                case KokoSim.Engine.Players.Condition.Good: return "cond--good";
                case KokoSim.Engine.Players.Condition.Poor: return "cond--bad";
                case KokoSim.Engine.Players.Condition.Terrible: return "cond--worst";
                default: return "cond--normal";
            }
        }

        private void SetText(string name, string text)
        {
            var label = _root.Q<Label>(name);
            if (label != null) { label.text = text; return; }
            var btn = _root.Q<Button>(name);
            if (btn != null) btn.text = text;
        }

        private static Label Cell(string text, string modifier)
        {
            var l = new Label(text);
            l.AddToClassList("cell");
            if (!string.IsNullOrEmpty(modifier)) l.AddToClassList(modifier);
            return l;
        }
    }
}
