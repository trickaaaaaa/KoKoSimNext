using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Players
{
    /// <summary>
    /// 選手一覧のコントローラ（設計書06 §3.2）。UIDocument（sourceAsset = PlayerList.uxml）へ
    /// PlayerListState（ViewModel）をバインドする。ソート/フィルタボタンで再描画。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class PlayerListController : MonoBehaviour
    {
        private static readonly Color CondUp = new Color(0.725f, 0.831f, 0.353f);   // 絶好調
        private static readonly Color CondMid = new Color(0.937f, 0.957f, 0.918f);  // 普通
        private static readonly Color CondDown = new Color(0.91f, 0.416f, 0.29f);   // 疲れ

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

            // 共通トップバー（スコアボード）: 現在日付（全画面共有の GameClock）とチーム総合力ランクを埋める。
            SetText("week", KokoSim.Unity.Shell.GameClock.CurrentLabel());

            var rank = _root.Q<VisualElement>("team-rank");
            if (rank != null)
            {
                rank.Clear();
                var g = new Label(v.TeamRankGrade);
                g.AddToClassList("grade");
                g.AddToClassList("grade--" + v.TeamRankGrade);
                rank.Add(g);
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
            overall.Add(MakeGradeChip(r.OverallGrade));
            overall.Add(new Label(r.OverallValue.ToString()));
            row.Add(overall);

            // 主要能力（チップ＋ラベルの並び）
            var ability = new VisualElement();
            ability.AddToClassList("cell");
            ability.AddToClassList("cell--wide");
            ability.AddToClassList("ability-cell");
            foreach (var chip in r.Abilities)
            {
                ability.Add(MakeGradeChip(chip.Grade));
                var lbl = new Label(chip.Label);
                lbl.style.marginRight = 10;
                lbl.style.fontSize = 11;
                ability.Add(lbl);
            }
            row.Add(ability);

            // 調子
            var cond = Cell(r.Condition, "cell--narrow");
            cond.style.color = r.Condition == "絶好調" ? CondUp : r.Condition == "疲れ" ? CondDown : CondMid;
            row.Add(cond);

            return row;
        }

        // ===== 補助 =====

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

        private static Label MakeGradeChip(string grade)
        {
            var chip = new Label(grade);
            chip.AddToClassList("grade");
            chip.AddToClassList("grade--" + grade);
            return chip;
        }
    }
}
