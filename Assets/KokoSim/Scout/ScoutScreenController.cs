using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Engine.Nation;   // Tier（ランクチップの入力）
using KokoSim.Unity.Components;  // 部品辞書（RankChip / CategoryRankChips / SegTabs）
using KokoSim.Unity.Shell;       // WeekAdvance / ScoreboardStrip / TopBarMeters / ScreenRouter

namespace KokoSim.Unity.Scout
{
    /// <summary>
    /// スカウト画面（設計書04 §2.1 受動枠 ／ §2.4 スカウト・issue #587, #867）。
    /// 左＝名簿（入部予定者／スカウト候補のタブ切替）、右＝選んだ1人の詳細
    /// （監督選択のB案＝選手一覧と同じマスターディテール）。
    /// 表示するのは <see cref="ProspectService"/> / <see cref="ScoutService"/> が持つ確定済みの状態だけで、
    /// ここでは何も抽選しない。スカウト網の契約・探す分野の生成後の変更はここでは行わない
    /// （買い物の唯一の場所は経営タブ・分野は生成前だけ切り替えられる）。
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

        /// <summary>
        /// 1回訪問する（費用は部費から）。まだ対象にしていなければ枠の空きがある限り自動で対象にする
        /// （ワイヤー案A＝ボタンは1つ）。
        /// </summary>
        public void Visit()
        {
            if (_view == null || _selected >= _view.Rows.Count || !_view.Rows[_selected].IsScout) return;
            switch (ScoutService.Visit(_selected))
            {
                case VisitResult.Visited: _status = "訪問した。手応えはあった。"; break;
                case VisitResult.VisitedWithRivalMove: _status = "訪問した。だが競合校も動いたようだ。"; break;
                case VisitResult.SlotsFull: _status = "スカウト枠が埋まっている。"; break;
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
            RenderTopBand();
            RenderTabs();
            RenderList();
            RenderDetail();
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

        // ── 共通の上部帯（オーナー決定2026-11-09・ワイヤー案A） ───────────
        private void RenderTopBand()
        {
            var net = _root.Q<Label>("sc-net");
            if (net != null) net.text = _view.NetworkWord;
            var slots = _root.Q<Label>("sc-slots");
            if (slots != null) slots.text = _view.SlotWord;
            var milestone = _root.Q<Label>("sc-milestone");
            if (milestone != null) milestone.text = _view.MilestoneWord;

            var host = _root.Q<VisualElement>("sc-field-tabs");
            if (host == null) return;
            host.Clear();
            var tabs = UiComponents.SegTabs(_view.FieldOptions, _view.FieldIndex, i =>
            {
                _state.SelectField(i);
                Render();
            });
            tabs.SetEnabled(_view.CanChooseField);
            host.Add(tabs);
        }

        // ── 名簿のタブ（入部予定者 / スカウト候補） ───────────────────
        private void RenderTabs()
        {
            var host = _root.Q<VisualElement>("sc-tabs");
            if (host == null) return;
            host.Clear();
            // スカウト網が未契約のうちはタブを出さない（押しても何も無い項目を並べない）。
            if (!_view.HasScouting) return;
            host.Add(UiComponents.SegTabs(
                new[] { "入部予定者", "スカウト" }, (int)_view.Tab,
                i => SelectTab((ScoutTab)i)));
        }

        // ── 左：名簿 ────────────────────────────────────────────
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

            var isScout = _view.Tab == ScoutTab.Candidates;
            var th2 = _root.Q<Label>("sc-th-2");
            if (th2 != null) th2.text = isScout ? "地方" : "投打";
            var th3 = _root.Q<Label>("sc-th-3");
            if (th3 != null) th3.text = isScout ? "能力" : "事前の見立て";
            var th4 = _root.Q<Label>("sc-th-4");
            if (th4 != null) th4.text = isScout ? "進路" : "？";
            SetShown(_root.Q<VisualElement>("sc-th-5"), isScout);

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

            // 人名は明朝（書体3役ルール）。スカウトで獲れた子は氏名の頭に印を付ける
            // ＝「口説いて獲った子」が一覧を眺めるだけで拾える（UI原則⑥）。
            var marked = r.IsScout ? r.Targeted : r.Recruited;
            var name = new Label(marked ? "◆ " + r.Name : r.Name);
            name.AddToClassList("sc-cell");
            name.AddToClassList("sc-cell--name");
            name.AddToClassList("f-display");
            row.Add(name);

            // スカウト候補は投打より「どこの子か」が先に要る（距離が入部の相性に直結するため）。
            var hand = new Label(r.IsScout ? r.Hometown : r.Handedness);
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
                // 段1（寸評だけ）・未契約＝ランクそのものが無い。雑音のランクを色付きで出さず「？」で通す。
                var none = new Label("？");
                none.AddToClassList("sc-cell--none");
                est.Add(none);
            }
            row.Add(est);

            // 入部予定者は「？」の残り数、スカウト候補は進路希望の短語（＝いちばん効く相性要因）。
            var tail = new Label(r.IsScout
                ? r.CareerPathWord
                : r.UnknownCount > 0 ? "？×" + r.UnknownCount : "—");
            tail.AddToClassList("sc-cell");
            tail.AddToClassList("sc-cell--unknown");
            row.Add(tail);

            if (r.IsScout)
            {
                var visits = new Label(Dots(r.Visits));
                visits.AddToClassList("sc-cell");
                visits.AddToClassList("sc-cell--visits");
                row.Add(visits);
            }

            // 選択は PointerDown で採る（Click は down/up の対と合成に依存する・UI-BUILD-METHOD 既知の罠）。
            var captured = index;
            row.RegisterCallback<PointerDownEvent>(_ => Select(captured));
            return row;
        }

        /// <summary>訪問回数を丸ドット3つで表す（4回目以降は埋まったまま・issue #867 ワイヤー案A）。</summary>
        private static string Dots(int visits)
        {
            var filled = Mathf.Clamp(visits, 0, 3);
            return new string('●', filled) + new string('○', 3 - filled);
        }

        // ── 右：選んだ1人の詳細 ───────────────────────────────
        private void RenderDetail()
        {
            var detail = _root.Q<VisualElement>("sc-detail");
            var detailEmpty = _root.Q<Label>("sc-detail-empty");
            var has = _selected >= 0 && _selected < _view.Rows.Count;

            if (detail != null) detail.style.display = has ? DisplayStyle.Flex : DisplayStyle.None;
            if (detailEmpty != null)
            {
                detailEmpty.style.display = has ? DisplayStyle.None : DisplayStyle.Flex;
                detailEmpty.text = _view.Tab == ScoutTab.Candidates
                    ? "スカウト候補が居ないので、見立てもまだ無い。"
                    : "確定した入部予定者が居ないので、見立てもまだ無い。";
            }
            if (!has) return;

            var r = _view.Rows[_selected];

            var name = _root.Q<Label>("sc-name");
            if (name != null) name.text = r.Name;
            var sub = _root.Q<Label>("sc-sub");
            if (sub != null)
                sub.text = r.IsScout
                    ? r.Handedness + " ・ " + r.Hometown + " ・ " + r.DistanceWord
                    : r.Handedness + " ・ " + _view.StateWord;
            var note = _root.Q<Label>("sc-est-note");
            if (note != null) note.text = _view.EstimateNote;

            RenderEstimate(r);
            RenderHidden(r);
            RenderDetailAbilities(r);
            RenderScoutCard(r);

            var comment = _root.Q<Label>("sc-comment");
            SetShown(_root.Q<VisualElement>("sc-comment-card"), !r.IsScout);
            if (comment != null && !r.IsScout) comment.text = r.Comment;
        }

        /// <summary>
        /// スカウトカード（スカウト候補のときだけ・issue #867）。入部の見込み（%）は出さない。
        /// 寸評・進路希望・競合校・訪問回数から読ませる（オーナー決定・ワイヤー案A）。
        /// </summary>
        private void RenderScoutCard(ProspectRow r)
        {
            var card = _root.Q<VisualElement>("sc-scout-card");
            SetShown(card, r.IsScout);
            if (!r.IsScout) return;

            var blurb = _root.Q<Label>("sc-blurb");
            if (blurb != null) blurb.text = r.Blurb;

            var note = _root.Q<Label>("sc-scout-note");
            if (note != null)
                note.text = r.Decided ? (r.Succeeded ? "入部が決まった" : "他校へ行った")
                    : _view.InScoutSeason ? "秋〜冬のあいだ訪問できる"
                    : "いまは訪問できない";

            var host = _root.Q<VisualElement>("sc-scout");
            if (host != null)
            {
                host.Clear();
                if (r.Decided) host.Add(Row("結果", r.Succeeded ? "入部が決まった" : "他校へ行った", !r.Succeeded));
                host.Add(Row("進路希望", r.CareerPathSentence, false));
                host.Add(Row("競合校", r.RivalTier, r.RivalIsStronger));
                host.Add(Row("訪問", Dots(r.Visits) + " ・ " + r.Visits + "回", false));
            }

            var visit = _root.Q<Button>("sc-visit");
            if (visit != null)
            {
                visit.text = "訪問する（" + r.VisitCost.ToString("0") + "万）";
                visit.SetEnabled(!r.Decided && _view.InScoutSeason);
            }
            var status = _root.Q<Label>("sc-scout-status");
            if (status != null) status.text = _status;
        }

        private static VisualElement Row(string key, string value, bool isMinus)
        {
            var row = new VisualElement();
            row.AddToClassList("sc-scout__row");
            var k = new Label(key);
            k.AddToClassList("sc-scout__key");
            row.Add(k);
            var v = new Label(value);
            v.AddToClassList("sc-scout__val");
            if (isMinus) v.AddToClassList("sc-scout__val--minus");
            row.Add(v);
            return row;
        }

        /// <summary>
        /// 4軸を縦に並べた見立て表。入部済みなら「実際」の列を足して答え合わせにする
        /// （＝「？が埋まった」ことが絵で分かる）。入部予定者は段ゲートが無いので常に出す
        /// （未契約だけ「？」になる）。スカウト候補は段2未満では表自体を伏せ、
        /// 「まだ分からない」ことを欄の有無で示す（1マスずつ？を並べると雑音が色付きに見える）。
        /// </summary>
        private void RenderEstimate(ProspectRow r)
        {
            var host = _root.Q<VisualElement>("sc-est");
            if (host == null) return;
            host.Clear();

            if (r.IsScout && !r.HasEstimate)
            {
                var note = new Label("スカウト網 Lv2 以上で打・守・走・投のランクが見える。");
                note.AddToClassList("sc-est__none");
                note.style.whiteSpace = WhiteSpace.Normal;
                host.Add(note);
                return;
            }

            // 列は「見立て（契約しているときだけ）」と「実際（入部後だけ）」の2本。
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

        /// <summary>隠し素質（入部予定者だけ・issue #587）。スカウト候補では一切見せない（オーナー決定2）。</summary>
        private void RenderHidden(ProspectRow r)
        {
            SetShown(_root.Q<VisualElement>("sc-hidden-card"), !r.IsScout);
            if (r.IsScout) return;

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

        /// <summary>個別能力（スカウト候補・段3だけ・issue #867）。</summary>
        private void RenderDetailAbilities(ProspectRow r)
        {
            var card = _root.Q<VisualElement>("sc-detail-card");
            var show = r.IsScout && r.Tier >= 3;
            SetShown(card, show);
            if (!show) return;

            var host = _root.Q<VisualElement>("sc-detail-abilities");
            if (host == null) return;
            host.Clear();
            foreach (var a in r.Abilities)
            {
                var item = new VisualElement();
                item.AddToClassList("sc-detail-abilities__item");
                var k = new Label(a.Label);
                k.AddToClassList("sc-detail-abilities__key");
                item.Add(k);
                item.Add(UiComponents.RankChipLegacy(a.Rank.ToString()));
                host.Add(item);
            }
        }
    }
}
