using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Unity.Components; // 部品辞書（RankChip / AbilityRow）
using KokoSim.Unity.Shell;      // 部品辞書（ConditionFace）

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
        private Button _advanceBtn;

        private void OnEnable()
        {
            _state = new PlayerListState();
            _root = GetComponent<UIDocument>().rootVisualElement;

            var sortBtn = _root.Q<Button>("sort-btn");
            if (sortBtn != null) sortBtn.clicked += () => { _state.CycleSort(); Render(); };

            // 共通トップバーの「今週を進める」を配線（issue #134: 以前は未配線で死にボタンだった）。
            // ScreenRouter が SetActive で付け外しするだけなので、OnDisable で必ず外す（多重登録＝多重進週の防止）。
            _advanceBtn = _root.Q<Button>("advance");
            if (_advanceBtn != null) _advanceBtn.clicked += OnAdvance;

            Render();
        }

        private void OnDisable()
        {
            if (_advanceBtn != null) _advanceBtn.clicked -= OnAdvance;
        }

        // 全タブ共通の進週処理へ集約（大会モード中はホームへ回送して日送りへ引き継ぐ）。
        private void OnAdvance() => KokoSim.Unity.Shell.WeekAdvance.FromSideScreen(Render);

        private void Render()
        {
            var v = _state.BuildView();

            SetText("counts", "計" + v.Total + "名");
            SetText("sort-btn", "並び: " + v.SortLabel);

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
            // 学年は「2年」の混植なので数字だけ Oswald に載せる（決定2-B）。
            row.Add(KokoSim.Unity.Components.UiComponents.NumUnitAuto(r.GradeLabel, false, "cell cell--narrow"));

            // カテゴリ別ランク（打撃力/走力/守備力/投手力）: 総合ランクは廃止（Issue #30・2026-07-22 owner決定）。
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
            var cond = new VisualElement();
            cond.AddToClassList("cell");
            cond.AddToClassList("cell--narrow");
            if (injured)
            {
                var injuryLabel = new Label(r.Injury);
                injuryLabel.AddToClassList("cell");
                injuryLabel.AddToClassList("cond--worst");
                cond.Add(injuryLabel);
            }
            else
            {
                // 調子は表情顔（ConditionFace）に統一（issue #51）。文字表記は tooltip に残す。
                var face = new ConditionFace { tooltip = r.Condition };
                face.Set(r.ConditionLevel);
                cond.Add(face);
            }
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
    }
}
