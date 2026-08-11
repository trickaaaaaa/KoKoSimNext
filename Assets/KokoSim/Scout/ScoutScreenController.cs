using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Engine.Nation;   // Tier（ランクチップの入力）
using KokoSim.Unity.Components;  // 部品辞書（RankChip / CategoryRankChips）
using KokoSim.Unity.Shell;       // WeekAdvance / ScoreboardStrip / TopBarMeters / ScreenRouter

namespace KokoSim.Unity.Scout
{
    /// <summary>
    /// スカウト画面（設計書04 §2.1 受動枠 ／ 設計書06 §3.6・issue #587）。
    /// 左＝入部予定者の名簿、右＝選んだ1人の見立て（監督選択のB案＝選手一覧と同じマスターディテール）。
    /// 表示するのは <see cref="ProspectService"/> が持つ確定済みの学年だけで、ここでは何も抽選しない。
    /// スカウト網の契約はこの画面では変更しない（買い物の唯一の場所は経営タブ）。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class ScoutScreenController : MonoBehaviour
    {
        private ScoutState _state;
        private VisualElement _root;
        private ScoutView _view;
        private int _selected;
        private string _status = "";

        private void OnEnable()
        {
            _state = new ScoutState();
            _root = GetComponent<UIDocument>().rootVisualElement;

            var advance = _root.Q<Button>("advance");
            if (advance != null) advance.clicked += () => WeekAdvance.FromSideScreen(Render);

            var toManagement = _root.Q<Button>("sc-to-management");
            // 画面切替はイベント配信中に同期実行しない（配信中パネルの SetActive でイベント木が壊れる）。
            if (toManagement != null)
                toManagement.clicked += () => ScreenRouter.Instance?.ShowDeferred("ManagementScreen");

            var target = _root.Q<Button>("sc-target");
            if (target != null) target.clicked += ToggleTarget;
            var visit = _root.Q<Button>("sc-visit");
            if (visit != null) visit.clicked += Visit;

            _selected = 0;
            Render();
        }

        /// <summary>スクショ・自動検証用に行を選ぶ。</summary>
        public void Select(int index)
        {
            _selected = index;
            Render();
        }

        /// <summary>スクショ・自動検証用にタブを切り替える。</summary>
        public void SelectTab(ScoutTab tab)
        {
            _state.Tab = tab;
            _selected = 0;
            _status = "";
            Render();
        }

        /// <summary>勧誘対象の指定を切り替える（枠を超えたら断る）。</summary>
        public void ToggleTarget()
        {
            if (_view == null || _selected >= _view.Rows.Count || !_view.Rows[_selected].IsRecruit) return;
            _status = RecruitService.ToggleTarget(_selected)
                ? ""
                : "勧誘枠が埋まっている。他の対象を外してから指定する。";
            Render();
        }

        /// <summary>1回訪問する（費用は部費から）。</summary>
        public void Visit()
        {
            if (_view == null || _selected >= _view.Rows.Count || !_view.Rows[_selected].IsRecruit) return;
            switch (RecruitService.Visit(_selected))
            {
                case VisitResult.Visited: _status = "訪問した。手応えはあった。"; break;
                case VisitResult.VisitedWithRivalMove: _status = "訪問した。だが競合校も動いたようだ。"; break;
                case VisitResult.NotTargeted: _status = "先に勧誘対象へ指定する。"; break;
                case VisitResult.NotEnoughFunds: _status = "部費が足りない。"; break;
                default: _status = "いまは訪問できない（秋〜冬のあいだだけ）。"; break;
            }
            Render();
        }

        private void Render()
        {
            _view = _state.BuildView();
            if (_selected >= _view.Rows.Count) _selected = 0;

            RenderTopBar();
            RenderTabs();
            RenderList();
            RenderDetail();
            RenderFoot();
        }

        /// <summary>共通トップバーの動的値（週・メーター・自校の総合ランク）を埋める。他画面と同じ単一ソース。</summary>
        private void RenderTopBar()
        {
            ScoreboardStrip.Fill(_root);
            TopBarMeters.Fill(_root);
            var rank = _root.Q<VisualElement>("team-rank");
            if (rank == null) return;
            rank.Clear();
            rank.Add(UiComponents.RankChipLegacy(TeamOverall.GradeOf(RosterService.Active)));
        }

        // ── 名簿のタブ（入部予定者 / 中学生リスト） ───────────────────
        private void RenderTabs()
        {
            var host = _root.Q<VisualElement>("sc-tabs");
            if (host == null) return;
            host.Clear();
            // 寮が無いうちはタブを出さない（押しても何も無い項目を並べない）。
            if (!_view.HasRecruiting) return;
            host.Add(UiComponents.SegTabs(
                new[] { "入部予定者", "中学生リスト" }, (int)_view.Tab,
                i => SelectTab((ScoutTab)i)));
        }

        // ── 左：入部予定者の名簿 ────────────────────────────────
        private void RenderList()
        {
            var title = _root.Q<Label>("sc-list-title");
            if (title != null) title.text = _view.ListTitle;
            var sub = _root.Q<Label>("sc-list-sub");
            if (sub != null) sub.text = _view.ListSub;

            // 行が無いときは見出し行と一覧を伏せる（空の表だけが残ると「読み込み中」に見える）。
            var isEmpty = !string.IsNullOrEmpty(_view.EmptyText);
            var empty = _root.Q<Label>("sc-empty");
            if (empty != null)
            {
                empty.text = _view.EmptyText;
                empty.style.display = isEmpty ? DisplayStyle.Flex : DisplayStyle.None;
            }
            SetShown(_root.Q<VisualElement>("sc-thead"), !isEmpty);
            SetShown(_root.Q<VisualElement>("sc-scroll"), !isEmpty);

            var th2 = _root.Q<Label>("sc-th-2");
            if (th2 != null) th2.text = _view.Tab == ScoutTab.Recruits ? "出身" : "投打";
            var th4 = _root.Q<Label>("sc-th-4");
            if (th4 != null) th4.text = _view.Tab == ScoutTab.Recruits ? "競合" : "？";

            var host = _root.Q<VisualElement>("sc-rows");
            if (host == null) return;
            host.Clear();
            for (var i = 0; i < _view.Rows.Count; i++) host.Add(BuildRow(_view.Rows[i], i));
        }

        private static void SetShown(VisualElement e, bool shown)
        {
            if (e != null) e.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private VisualElement BuildRow(ProspectRow r, int index)
        {
            var row = new VisualElement();
            row.AddToClassList("row-panel");
            row.AddToClassList("row-panel--dense");   // 4軸を1段に畳んだので行を詰める（UI原則①）
            if (index == _selected) row.AddToClassList("row-panel--selected");

            // 人名は明朝（書体3役ルール）。勧誘対象は氏名の頭に印を付ける
            // ＝「いま追いかけている2人」が一覧を眺めるだけで拾える（UI原則⑥）。
            // ◆＝「自分から獲りにいった子」。中学生リストでは勧誘対象、入部予定者では勧誘で獲れた子。
            var marked = r.IsRecruit ? r.Targeted : r.Recruited;
            var name = new Label(marked ? "◆ " + r.Name : r.Name);
            name.AddToClassList("sc-cell");
            name.AddToClassList("sc-cell--name");
            name.AddToClassList("f-display");
            row.Add(name);

            // 中学生は投打より「どこの子か」が先に要る（距離が成功率に直結するため）。
            var hand = new Label(r.IsRecruit ? r.Hometown : r.Handedness);
            hand.AddToClassList("sc-cell");
            hand.AddToClassList("sc-cell--hand");
            row.Add(hand);

            var est = new VisualElement();
            est.AddToClassList("sc-cell");
            est.AddToClassList("sc-cell--est");
            if (r.HasEstimate)
            {
                // 1段の「打走守投」＋小チップ。2段組は3文字ラベルが小チップに食い込んで読めない。
                est.Add(UiComponents.CategoryRankChipsCompact(r.Estimate, small: true));
            }
            else
            {
                // 未契約＝報告書そのものが無い。雑音のランクを色付きで出さず「？」で通す。
                var none = new Label("？");
                none.AddToClassList("sc-cell--none");
                est.Add(none);
            }
            row.Add(est);

            // 入部予定者は「？」の残り数、中学生は競合校の格（＝いちばん効く不利要因）。
            var tail = new Label(r.IsRecruit
                ? r.RivalTier
                : r.UnknownCount > 0 ? "？×" + r.UnknownCount : "—");
            tail.AddToClassList("sc-cell");
            tail.AddToClassList("sc-cell--unknown");
            row.Add(tail);

            // 選択は PointerDown で採る（Click は down/up の対と合成に依存する・UI-BUILD-METHOD 既知の罠）。
            var captured = index;
            row.RegisterCallback<PointerDownEvent>(_ => Select(captured));
            return row;
        }

        // ── 右：選んだ1人の見立て ───────────────────────────────
        private void RenderDetail()
        {
            var detail = _root.Q<VisualElement>("sc-detail");
            var detailEmpty = _root.Q<Label>("sc-detail-empty");
            var has = _selected >= 0 && _selected < _view.Rows.Count;

            if (detail != null) detail.style.display = has ? DisplayStyle.Flex : DisplayStyle.None;
            if (detailEmpty != null)
            {
                detailEmpty.style.display = has ? DisplayStyle.None : DisplayStyle.Flex;
                detailEmpty.text = "確定した入部予定者が居ないので、見立てもまだ無い。";
            }
            if (!has) return;

            var r = _view.Rows[_selected];

            var name = _root.Q<Label>("sc-name");
            if (name != null) name.text = r.Name;
            var sub = _root.Q<Label>("sc-sub");
            if (sub != null)
                sub.text = r.IsRecruit
                    ? r.Handedness + " ・ " + r.Hometown + " ・ " + r.DistanceWord
                    : r.Handedness + " ・ " + _view.StateWord;
            var note = _root.Q<Label>("sc-est-note");
            if (note != null) note.text = _view.EstimateNote;

            RenderEstimate(r);
            RenderHidden(r);
            RenderRecruit(r);

            var comment = _root.Q<Label>("sc-comment");
            if (comment != null) comment.text = r.Comment;
        }

        /// <summary>
        /// 勧誘カード（中学生リストのときだけ・issue #588）。成功率と<b>その内訳</b>を素直に並べる
        /// ＝何が効いているかを隠さない（名声・距離・競合校の格・訪問回数）。
        /// </summary>
        private void RenderRecruit(ProspectRow r)
        {
            var card = _root.Q<VisualElement>("sc-recruit-card");
            SetShown(card, r.IsRecruit);
            if (!r.IsRecruit) return;

            var note = _root.Q<Label>("sc-recruit-note");
            if (note != null)
                note.text = r.Decided ? (r.Succeeded ? "入部が決まった" : "他校へ行った")
                    : _view.InRecruitSeason ? "秋〜冬のあいだ訪問できる"
                    : "いまは訪問できない";

            var host = _root.Q<VisualElement>("sc-recruit");
            if (host != null)
            {
                host.Clear();
                // 決着後に成功率を出し続けると「まだ何か変えられる」ように見える＝結果へ差し替える。
                if (r.Decided) host.Add(SmallRow("結果", r.Succeeded ? "入部が決まった" : "他校へ行った", !r.Succeeded));
                else host.Add(BigRow("成功率", (r.SuccessProbability * 100).ToString("0") + "%"));

                host.Add(SmallRow("名声", ManagerService.Manager.Fame.ToString("0"), false));
                // 同県は距離の減点そのものが無いので「寮でカバー」を書かない（効いていない注釈を出さない）。
                var hasPenalty = r.DistanceWord != "同県";
                host.Add(SmallRow("距離",
                    r.DistanceWord + (hasPenalty && RecruitService.HasDorm ? "（寮でカバー）" : ""), hasPenalty));
                host.Add(SmallRow("競合校", r.RivalTier, r.RivalIsStronger));
                host.Add(SmallRow("訪問", r.Visits + " 回 ／ 1回 " + r.VisitCost.ToString("0") + "万", false));
            }

            var target = _root.Q<Button>("sc-target");
            if (target != null)
            {
                target.text = r.Targeted ? "勧誘対象から外す" : "勧誘対象にする";
                target.SetEnabled(!r.Decided);
            }
            var visit = _root.Q<Button>("sc-visit");
            if (visit != null)
            {
                visit.text = "訪問する（" + r.VisitCost.ToString("0") + "万）";
                visit.SetEnabled(!r.Decided && r.Targeted && _view.InRecruitSeason);
            }
            var status = _root.Q<Label>("sc-recruit-status");
            if (status != null) status.text = _status;
        }

        private static VisualElement BigRow(string key, string value)
        {
            var row = new VisualElement();
            row.AddToClassList("sc-recruit__row");
            var k = new Label(key);
            k.AddToClassList("sc-recruit__key");
            row.Add(k);
            var v = new Label(value);
            v.AddToClassList("sc-recruit__prob");
            v.AddToClassList("f-num");   // 数字だけのセル（書体3役ルール）
            row.Add(v);
            return row;
        }

        private static VisualElement SmallRow(string key, string value, bool isMinus)
        {
            var row = new VisualElement();
            row.AddToClassList("sc-recruit__row");
            var k = new Label(key);
            k.AddToClassList("sc-recruit__key");
            row.Add(k);
            var v = new Label(value);
            v.AddToClassList("sc-recruit__val");
            if (isMinus) v.AddToClassList("sc-recruit__val--minus");
            row.Add(v);
            return row;
        }

        /// <summary>
        /// 4軸を縦に並べた見立て表。入部済みなら「実際」の列を足して答え合わせにする
        /// （＝「？が埋まった」ことが絵で分かる）。列見出しは実際の列があるときだけ出す。
        /// </summary>
        private void RenderEstimate(ProspectRow r)
        {
            var host = _root.Q<VisualElement>("sc-est");
            if (host == null) return;
            host.Clear();

            // 列は「見立て（契約しているときだけ）」と「実際（入部後だけ）」の2本。
            // 両方あるときだけ列見出しを出す（1本しか無い列に見出しは要らない）。
            var hasActual = r.Actual != null;
            if (r.HasEstimate && hasActual) host.Add(EstimateHead());
            host.Add(EstimateRow("打撃力", r, s => s.BattingTier, hasActual));
            host.Add(EstimateRow("走力", r, s => s.MobilityTier, hasActual));
            host.Add(EstimateRow("守備力", r, s => s.DefenseTier, hasActual));
            host.Add(EstimateRow("投手力", r, s => s.PitchingTier, hasActual));
        }

        private static VisualElement EstimateHead()
        {
            var row = new VisualElement();
            row.AddToClassList("sc-est__row");
            row.AddToClassList("sc-est__row--head");
            row.Add(HeadCell("", "sc-est__label"));
            row.Add(HeadCell("見立て", "sc-est__col"));
            row.Add(HeadCell("実際", "sc-est__col"));
            return row;
        }

        private static Label HeadCell(string text, string cls)
        {
            var l = new Label(text);
            l.AddToClassList(cls);
            l.AddToClassList("sc-est__th");
            return l;
        }

        private static VisualElement EstimateRow(
            string label, ProspectRow r, System.Func<PlayerStrength, Tier> axis, bool hasActual)
        {
            var row = new VisualElement();
            row.AddToClassList("sc-est__row");

            var l = new Label(label);
            l.AddToClassList("sc-est__label");
            row.Add(l);

            // 未契約なら見立ての列は「？」。入部後は実測の列を足す。
            row.Add(r.HasEstimate ? ChipCell(axis(r.Estimate)) : UnknownCell());
            if (hasActual) row.Add(ChipCell(axis(r.Actual)));
            return row;
        }

        private static VisualElement ChipCell(Tier tier)
        {
            var cell = new VisualElement();
            cell.AddToClassList("sc-est__col");
            cell.Add(UiComponents.RankChipLegacy(tier.ToString()));
            return cell;
        }

        private static VisualElement UnknownCell()
        {
            var cell = new VisualElement();
            cell.AddToClassList("sc-est__col");
            var l = new Label("？");
            l.AddToClassList("sc-est__none");
            cell.Add(l);
            return cell;
        }

        private void RenderHidden(ProspectRow r)
        {
            var host = _root.Q<VisualElement>("sc-hidden");
            if (host == null) return;
            host.Clear();
            host.Add(HiddenItem("成長タイプ", r.GrowthType));
            host.Add(HiddenItem("才能上限", r.Potential));
            host.Add(HiddenItem("性格", r.Personality));
            host.Add(HiddenItem("故障耐性", r.InjuryResistance));
        }

        private static VisualElement HiddenItem(string key, string value)
        {
            var item = new VisualElement();
            item.AddToClassList("sc-hidden__item");

            var k = new Label(key);
            k.AddToClassList("sc-hidden__key");
            item.Add(k);

            var known = !string.IsNullOrEmpty(value);
            var v = new Label(known ? value : "？");
            v.AddToClassList("sc-hidden__val");
            if (!known) v.AddToClassList("sc-hidden__val--unknown");
            item.Add(v);
            return item;
        }

        private void RenderFoot()
        {
            var slots = _root.Q<Label>("sc-slots");
            if (slots != null) slots.text = _view.SlotLine;
            var net = _root.Q<Label>("sc-net");
            if (net != null) net.text = _view.NetworkLine;
        }
    }
}
