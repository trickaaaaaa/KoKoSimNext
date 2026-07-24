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
        private Button _advanceBtn;

        // マスターディテール化（issue #131）: 能力詳細は同じ UIDocument に埋め込まれた
        // PlayerDetailController が担う（別画面・別GameObjectではない。ScreenRouter は
        // 複数UIDocument同居を許さないため、詳細は一覧と同一画面に同居させる設計）。
        private PlayerDetailController _detail;

        // 初回描画で先頭行を既定選択にするための一度きりのフラグ（以降はホバーで更新）。
        private bool _hasHovered;

        private void OnEnable()
        {
            _state = new PlayerListState();
            _root = GetComponent<UIDocument>().rootVisualElement;
            _detail = GetComponent<PlayerDetailController>();

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
            KokoSim.Unity.Shell.TopBarMeters.Fill(_root);   // 部費残高・名声・信頼度（ManagerService 単一ソース）

            var rank = _root.Q<VisualElement>("team-rank");
            if (rank != null)
            {
                rank.Clear();
                rank.Add(UiComponents.RankChipLegacy(v.TeamRankGrade));
            }

            var rows = _root.Q<VisualElement>("player-rows");
            if (rows == null) return;

            // 初回描画（未ホバー）は先頭行を既定選択にする。ホバー後は上書きしない
            // （並び替え等の再描画のたびに選択が先頭へ戻らないようにする）。
            if (!_hasHovered && v.Rows.Count > 0) PlayerSelection.Index = v.Rows[0].Index;

            rows.Clear();
            foreach (var r in v.Rows) rows.Add(BuildRow(r));
        }

        private VisualElement BuildRow(PlayerRow r)
        {
            var row = new VisualElement();
            row.AddToClassList("row-panel");   // 共通部品：白パネル＋インク枠＋ベタ影（components.uss）

            // マスターディテール化（issue #131）: クリックで別画面へ遷移せず、ホバーで右ペインの
            // 能力詳細を差し替える。ハイライトは components.uss の .row-panel:hover（桜淡）をそのまま使う。
            var idx = r.Index;
            row.RegisterCallback<PointerEnterEvent>(_ =>
            {
                _hasHovered = true;
                PlayerSelection.Index = idx;
                _detail?.RenderForSelection();
            });

            row.Add(NumberCell(r.UniformNumber));
            row.Add(Cell(r.Name, "cell--name"));
            // 学年は「2年」の混植なので数字だけ Oswald に載せる（決定2-B）。列は固定幅
            // （flex-basis:0 の比例配分だと詰まった行で幅0になり文字が隣列へ重なる）。
            row.Add(KokoSim.Unity.Components.UiComponents.NumUnitAuto(r.GradeLabel, false, "cell cell--grade"));

            // カテゴリ別ランク（打撃力/走力/守備力/投手力）: 総合ランクは廃止（Issue #30・2026-07-22 owner決定）。
            // 2段組（上=打撃力・走力／下=守備力・投手力）＋右寄せで折返しの崩れをなくす（owner指示・2026-07-24）。
            var ability = new VisualElement();
            ability.AddToClassList("cell");
            ability.AddToClassList("cell--wide");
            ability.Add(UiComponents.CategoryRankChipsTwoRow(r.Strength));
            row.Add(ability);

            // 故障（設計書03 §3.5・UI原則⑥）: 怪我中だけ「傷病名・段階」を警告表示する。
            // 調子は試合にしか影響しないため日常画面には出さない（issue #214: 表示は試合系画面に限定）。
            var injury = new VisualElement();
            injury.AddToClassList("cell");
            injury.AddToClassList("cell--narrow");
            if (r.Injury.Length > 0)
            {
                var injuryLabel = new Label(r.Injury);
                injuryLabel.AddToClassList("cell");
                injuryLabel.AddToClassList("cond--worst");
                injury.Add(injuryLabel);
            }
            row.Add(injury);

            return row;
        }

        // ===== 補助 =====

        // 背番号セル（issue #131）: 数字だけなのでコンデンス体（f-num）。列幅・右揃えは .cell--no（PlayerList.uss）。
        private static Label NumberCell(string text)
        {
            var l = new Label(text);
            l.AddToClassList("cell");
            l.AddToClassList("cell--no");
            l.AddToClassList("f-num");
            return l;
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
