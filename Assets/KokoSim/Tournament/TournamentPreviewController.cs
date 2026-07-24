using System.Linq;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Unity.Components; // 部品辞書（RankChip / AbilityRow）

namespace KokoSim.Unity.Tournament
{
    /// <summary>
    /// 大会画面のコントローラ（設計書06 §3.5b）。UIDocument（TournamentPreview.uxml）に
    /// 3つのサブビューを持ち、画面内で切り替える（新規シーンGameObjectを作らない）。
    ///   1. トーナメント: 自校の勝ち上がり＋各回戦の結果（既定）
    ///   2. 大会展望: 優勝争いの構図＋注目選手
    ///   3. 校別詳細: トーナメント表の高校名クリックで開く（ベンチ入りメンバー表＋チーム寸評, issue #189）
    /// 本機能は大会モード中のみ有効。開催中でなければ空状態を出す。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class TournamentPreviewController : MonoBehaviour
    {
        private VisualElement _root;
        private VisualElement _empty, _bracketView, _previewView, _detailView;

        /// <summary>3ビュー切替（Bracket=トーナメント / Preview=大会展望 / Detail=校別詳細）。</summary>
        private enum ViewMode { Bracket, Preview, Detail }
        private ViewMode _mode;
        private string _detailSchool;   // Detail 表示中の校名（クリック元＝樹形図の高校名, issue #189）

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            _empty = _root.Q<VisualElement>("tp-empty");
            _bracketView = _root.Q<VisualElement>("tp-view-bracket");
            _previewView = _root.Q<VisualElement>("tp-view-preview");
            _detailView = _root.Q<VisualElement>("tp-view-detail");

            // 画面を開き直すたびトーナメントから始める（大会の現況が最初に目に入る）。
            _mode = ViewMode.Bracket;

            var go = _root.Q<Button>("tp-go-preview");
            if (go != null) go.clicked += () => { _mode = ViewMode.Preview; Render(); };
            var back = _root.Q<Button>("tp-back");
            if (back != null) back.clicked += () => { _mode = ViewMode.Bracket; Render(); };
            var detailBack = _root.Q<Button>("tp-detail-back");
            if (detailBack != null) detailBack.clicked += () => { _mode = ViewMode.Bracket; Render(); };

            // 共通トップバーの「今週を進める」で共有週を進める（全タブ共通の進週処理へ集約, issue #134）。
            var advance = _root.Q<Button>("advance");
            if (advance != null) advance.clicked += () => WeekAdvance.FromSideScreen(Render);

            // 樹形図のラウンド名帯を縦スクロールに追従させる（横は列と一緒に動く）。
            var brk = _root.Q<ScrollView>("tp-bracket-scroll");
            if (brk != null)
            {
                brk.verticalScroller.valueChanged -= SyncBracketHead;
                brk.verticalScroller.valueChanged += SyncBracketHead;
            }

            Render();
        }

        /// <summary>ビューを明示的に切り替える（スクショ・自動検証用。通常はCTA/戻るボタンから遷移する）。</summary>
        public void ShowPreviewView(bool preview)
        {
            _mode = preview ? ViewMode.Preview : ViewMode.Bracket;
            Render();
        }

        /// <summary>校別詳細を明示的に開く（スクショ・自動検証用。通常は樹形図の高校名クリックから遷移する）。</summary>
        public void ShowSchoolDetail(string schoolName)
        {
            _detailSchool = schoolName;
            _mode = ViewMode.Detail;
            Render();
        }

        private void Render()
        {
            var session = GameSession.Current;
            var inTournament = session.InTournament;

            RenderTopBar();

            Show(_empty, !inTournament);
            Show(_bracketView, inTournament && _mode == ViewMode.Bracket);
            Show(_previewView, inTournament && _mode == ViewMode.Preview);
            Show(_detailView, inTournament && _mode == ViewMode.Detail);

            if (!inTournament) return;

            switch (_mode)
            {
                case ViewMode.Preview: RenderPreview(); break;
                case ViewMode.Detail: RenderDetail(); break;
                default: RenderBracket(session.Runner.BuildBracketView()); break;
            }
        }

        private static void Show(VisualElement e, bool visible)
        {
            if (e != null) e.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>
        /// 共通トップバー（Components/TopBar.uxml）の動的値を埋める。週は全画面共有の GameClock、
        /// 総合ランクは共有ロスター由来の TeamOverall を単一ソースとして引く（選手/メンバー/練習画面と同じ）。
        /// 部費・名声・信頼度は現状どの画面も固定表示のため TopBar の既定値をそのまま使う。
        /// </summary>
        private void RenderTopBar()
        {
            KokoSim.Unity.Components.ScoreboardStrip.Fill(_root);

            var rank = _root.Q<VisualElement>("team-rank");
            if (rank == null) return;
            rank.Clear();
            var grade = TeamOverall.GradeOf(RosterService.Active);
            rank.Add(UiComponents.RankChipLegacy(grade));
        }

        // ===== ビュー1: トーナメント（今大会の経過） =====

        private void RenderBracket(TournamentBracketView view)
        {
            var session = GameSession.Current;
            var runner = session.Runner;

            var status = view.ManagerIsChampion ? "自校 優勝！"
                : view.ManagerEliminated ? "自校 敗退"
                : "自校 進行中";
            SetText("tb-title", view.Title);
            SetText("tb-meta", "出場 " + session.Field.Count + "校 ／ " + status
                + " ／ 優勝校: " + (view.ChampionName ?? "未定"));

            // 次戦（終了していれば結果の総括）。
            string next;
            if (runner.Finished)
                next = view.ManagerIsChampion ? "全試合終了 ─ 優勝" : "自校は敗退しました";
            else if (session.ReachedMatchDay)
                next = "本日 試合　" + runner.RoundName + "　vs " + (runner.NextOpponent?.Name ?? "―");
            else
                next = "次戦　" + runner.RoundName + "　vs " + (runner.NextOpponent?.Name ?? "―")
                       + "　（あと " + session.DaysUntilNextMatch + " 日）";
            SetText("tb-next", next);

            RenderBracketTree(view);

            // 自校の勝ち上がりを先頭にまとめ、その下に他校の結果を置く（200校規模で自校が埋もれないように）。
            var mine = new System.Collections.Generic.List<BracketMatch>();
            var others = new System.Collections.Generic.List<BracketMatch>();
            foreach (var m in view.Matches) (m.ManagerInvolved ? mine : others).Add(m);

            var myHost = _root.Q<VisualElement>("tp-my-matches");
            if (myHost != null)
            {
                myHost.Clear();
                if (mine.Count == 0) myHost.Add(Note("まだ自校の試合は行われていません。"));
                else foreach (var m in mine) myHost.Add(BuildMatchRow(m));
            }

            var host = _root.Q<VisualElement>("tp-matches");
            if (host == null) return;
            host.Clear();
            if (others.Count == 0)
            {
                host.Add(Note("まだ試合は行われていません。"));
                return;
            }
            // 直近の回戦から表示し、打ち切った件数は明示する（黙って切り捨てない）。
            var shown = System.Math.Min(OtherMatchLimit, others.Count);
            for (var i = others.Count - shown; i < others.Count; i++) host.Add(BuildMatchRow(others[i]));
            if (others.Count > shown)
                host.Add(Note("ほか " + (others.Count - shown) + " 試合（直近 " + shown + " 試合を表示）"));
        }

        /// <summary>他校結果の表示上限（全部出すと200校規模でスクロールが破綻するため）。</summary>
        private const int OtherMatchLimit = 40;

        // ===== 樹形図（トーナメント表） =====

        /// <summary>
        /// 樹形図を描く。甲子園ブラケット風に左右2ブロック（各ラウンドの前半/後半スロット）へ分割し、
        /// 決勝を中央で向き合わせる（issue #188。ブロックサイズは常に2の冪なので必ず均等に割れる）。
        /// 全カードを省略なく出し、初期表示は自校の最新カードを中央に置く。
        /// </summary>
        private void RenderBracketTree(TournamentBracketView view)
        {
            var host = _root.Q<VisualElement>("tp-bracket");
            var head = _root.Q<VisualElement>("tp-bracket-head");
            if (host == null || head == null) return;
            host.Clear();
            head.Clear();

            var tree = TournamentPreviewState.BuildBracketTree(view);
            if (tree.Rounds.Count == 0)
            {
                host.Add(Note("トーナメント表はまだ組まれていません。"));
                return;
            }

            var finalRound = tree.Rounds.Count - 1;
            VisualElement focus = null;

            // 左ブロック：ラウンド0→準決勝まで、通常どおり左→右。
            for (var r = 0; r < finalRound; r++)
                host.Add(BuildBlockColumn(tree, r, finalRound, mirrored: false, head, ref focus, OpenSchoolDetail));

            // 中央：決勝＋優勝欄（1列にまとめる。両ブロックの準決勝がここへ直結する）。
            head.Add(ColumnHead(tree.Rounds[finalRound].Name, hasLead: false, hasElbow: true));
            head.Add(ColumnHead("優勝", hasLead: false, hasElbow: false));
            host.Add(BuildFinalColumn(tree, ref focus, OpenSchoolDetail));

            // 右ブロック：準決勝→ラウンド0を逆順に並べ、接続線を左右反転して決勝へ向き合わせる。
            for (var r = finalRound - 1; r >= 0; r--)
                host.Add(BuildBlockColumn(tree, r, finalRound, mirrored: true, head, ref focus, OpenSchoolDetail));

            var scroll = _root.Q<ScrollView>("tp-bracket-scroll");
            if (scroll != null && focus != null) CenterOn(scroll, focus);
        }

        /// <summary>
        /// 片ブロック1ラウンド分の列。mirrored=false は左ブロック（前半スロット・左→右に通常配置）、
        /// mirrored=true は右ブロック（後半スロット・接続線を左右反転して決勝側を向く）。
        /// </summary>
        private static VisualElement BuildBlockColumn(TournamentPreviewState.BracketTreeView tree,
            int r, int finalRound, bool mirrored, VisualElement head, ref VisualElement focus,
            System.Action<string> onSchoolClick)
        {
            var col = tree.Rounds[r];
            var half = col.Cards.Count / 2;
            var cards = mirrored ? col.Cards.Skip(half) : col.Cards.Take(half);
            var isBlockFinal = r == finalRound - 1;   // このブロックの準決勝＝決勝へまっすぐ1本

            head.Add(ColumnHead(col.Name, hasLead: r > 0, hasElbow: true, mirrored));

            var colEl = new VisualElement();
            colEl.AddToClassList("brk-col");
            var body = new VisualElement();
            body.AddToClassList("brk-col__body");

            foreach (var card in cards)
            {
                var slot = new VisualElement();
                slot.AddToClassList("brk-slot");
                var cardEl = BuildBracketCard(card, onSchoolClick);
                var outConnector = isBlockFinal
                    ? FlatConnector(card.ManagerAdvances)
                    : mirrored
                        ? MirroredElbow(card.SlotIndex % 2 == 0, card.ManagerAdvances)
                        : Elbow(card.SlotIndex % 2 == 0, card.ManagerAdvances);

                if (mirrored)
                {
                    // 右ブロックは決勝側（左）へ出る接続線を先に、自校の帯を最後（外側）に置く。
                    slot.Add(outConnector);
                    slot.Add(cardEl);
                    if (r > 0) slot.Add(Lead(card.ManagerInvolved));
                }
                else
                {
                    if (r > 0) slot.Add(Lead(card.ManagerInvolved));
                    slot.Add(cardEl);
                    slot.Add(outConnector);
                }

                body.Add(slot);
                if (ReferenceEquals(tree.ManagerFocus, card)) focus = cardEl;
            }
            colEl.Add(body);
            return colEl;
        }

        /// <summary>中央列：決勝カード＋優勝欄を1つの枠にまとめる（両ブロックの準決勝がここへ直結する）。</summary>
        private static VisualElement BuildFinalColumn(TournamentPreviewState.BracketTreeView tree, ref VisualElement focus,
            System.Action<string> onSchoolClick)
        {
            var col = new VisualElement();
            col.AddToClassList("brk-col");
            var body = new VisualElement();
            body.AddToClassList("brk-col__body");

            var slot = new VisualElement();
            slot.AddToClassList("brk-slot");

            var finalRound = tree.Rounds[tree.Rounds.Count - 1];
            if (finalRound.Cards.Count > 0)
            {
                var card = finalRound.Cards[0];
                var cardEl = BuildBracketCard(card, onSchoolClick);
                slot.Add(cardEl);
                if (ReferenceEquals(tree.ManagerFocus, card)) focus = cardEl;
            }
            slot.Add(FlatConnector(tree.ManagerIsChampion));
            slot.Add(ChampionBadge(tree));

            body.Add(slot);
            col.Add(body);
            return col;
        }

        /// <summary>ラウンド名帯を縦スクロール量ぶん下げて、ビューポート上端に貼り付ける。</summary>
        private void SyncBracketHead(float offsetY)
        {
            var head = _root.Q<VisualElement>("tp-bracket-head");
            if (head != null) head.style.top = offsetY;
        }

        /// <summary>
        /// ラウンド名の見出しセル。列と同じ寸法トークン（接続線の帯を空要素で置く）で組み、
        /// 見出しと列の横位置が必ず一致するようにする。mirrored=true は右ブロック用で、
        /// 列本体の接続線の並び（決勝側の帯が先、自校の帯が後）に合わせてスペーサーの順を反転する。
        /// </summary>
        private static VisualElement ColumnHead(string text, bool hasLead, bool hasElbow, bool mirrored = false)
        {
            var cell = new VisualElement();
            cell.AddToClassList("brk-hcell");
            if (mirrored)
            {
                if (hasElbow) cell.Add(Spacer("brk-elbow"));
                cell.Add(ColumnHeadLabel(text));
                if (hasLead) cell.Add(Spacer("brk-lead"));
            }
            else
            {
                if (hasLead) cell.Add(Spacer("brk-lead"));
                cell.Add(ColumnHeadLabel(text));
                if (hasElbow) cell.Add(Spacer("brk-elbow"));
            }
            return cell;
        }

        private static Label ColumnHeadLabel(string text)
        {
            var l = new Label(text);
            l.AddToClassList("brk-col__name");
            return l;
        }

        private static VisualElement Spacer(string cls)
        {
            var e = new VisualElement();
            e.AddToClassList(cls);
            return e;
        }

        private static VisualElement BuildBracketCard(TournamentPreviewState.BracketCardRow c,
            System.Action<string> onSchoolClick)
        {
            var card = new VisualElement();
            card.AddToClassList("brk-card");
            if (c.ManagerInvolved) card.AddToClassList("brk-card--fav");

            if (c.IsBye)
            {
                // 不戦勝は「（不戦勝）」の偽対戦相手を出さず、勝ち上がる校名だけを1行で見せる
                // （露出させない＝issue #188）。2行カードと違い対戦線を引かないので自然に区別がつく。
                // 不戦勝カードはクリック不可（issue #189: 未確定枠・不戦勝はクリック不可）。
                card.Add(SlotLine(c.Top.IsDetermined ? c.Top : c.Bottom, clickable: false, onSchoolClick));
                return card;
            }

            var top = SlotLine(c.Top, c.Top.IsDetermined, onSchoolClick);
            top.AddToClassList("brk-line--top");
            card.Add(top);
            card.Add(SlotLine(c.Bottom, c.Bottom.IsDetermined, onSchoolClick));
            if (c.MercyEnded)
            {
                var mercy = new Label("コールド");
                mercy.AddToClassList("brk-mercy");
                card.Add(mercy);
            }
            return card;
        }

        /// <summary>樹形図1行（校名＋スコア）。clickable=true かつ校名が確定していれば校別詳細へ遷移する
        /// （issue #189。未確定枠「（未定）」・不戦勝は clickable=false で渡ってくる）。</summary>
        private static VisualElement SlotLine(TournamentPreviewState.BracketSlotRow s, bool clickable,
            System.Action<string> onSchoolClick)
        {
            var row = new VisualElement();
            row.AddToClassList("brk-line");

            var name = new Label(s.Name);
            name.AddToClassList("brk-name");
            if (s.IsManager) name.AddToClassList("brk-name--fav");
            else if (!s.IsDetermined) name.AddToClassList("brk-name--tbd");
            else if (s.IsLoser) name.AddToClassList("brk-name--out");
            if (s.IsWinner) name.AddToClassList("brk-name--win");
            if (clickable && s.IsDetermined)
            {
                name.AddToClassList("brk-name--clickable");
                var schoolName = s.Name;
                name.RegisterCallback<ClickEvent>(_ => onSchoolClick(schoolName));
            }
            row.Add(name);

            var score = new Label(s.Score);
            score.AddToClassList("brk-score");
            score.AddToClassList("f-num");   // スコアは純数値＝コンデンス体（決定2-B）
            if (s.IsLoser) score.AddToClassList("brk-score--out");
            row.Add(score);
            return row;
        }

        /// <summary>優勝欄バッジ（決勝カードの隣に添える）。自校優勝のときだけアンバー。</summary>
        private static VisualElement ChampionBadge(TournamentPreviewState.BracketTreeView tree)
        {
            var card = new VisualElement();
            card.AddToClassList("brk-champ");
            var cap = new Label("CHAMPION");
            cap.AddToClassList("brk-champ__cap");
            card.Add(cap);
            var name = new Label(tree.ChampionName ?? "（未定）");
            name.AddToClassList("brk-champ__name");
            if (tree.ChampionName == null) name.AddToClassList("brk-champ__name--tbd");
            else if (tree.ManagerIsChampion) name.AddToClassList("brk-champ__name--fav");
            card.Add(name);
            return card;
        }

        /// <summary>前段から入る横線（帯の縦中央に1本）。</summary>
        private static VisualElement Lead(bool fav) => Connector("brk-lead", fav, upper: true);

        /// <summary>決勝→優勝欄のまっすぐな1本。</summary>
        private static VisualElement FlatConnector(bool fav)
        {
            var e = Connector("brk-elbow", fav, upper: true);
            e.AddToClassList("brk-elbow--flat");
            return e;
        }

        /// <summary>横線と縦線の直角。down=true でカード中央から下（＝組の上側カード）へ折れる。</summary>
        private static VisualElement Elbow(bool down, bool fav)
        {
            var e = new VisualElement();
            e.AddToClassList("brk-elbow");
            e.Add(Connector("brk-elbow__part", fav, upper: true));    // 左半分＝カードから出る横線
            e.Add(Vertical(down, fav));                               // 右半分の左端＝中点へ伸びる縦線
            return e;
        }

        /// <summary>Elbow の左右反転版（右ブロック用）。パーツの並びを逆にして、カードの左側から
        /// 決勝側（中点）へ向けて出る折れ線にする。down の意味（組の上/下どちら側か）は不変。</summary>
        private static VisualElement MirroredElbow(bool down, bool fav)
        {
            var e = new VisualElement();
            e.AddToClassList("brk-elbow");
            e.Add(Vertical(down, fav));                               // 左半分の右端＝中点から伸びる縦線
            e.Add(Connector("brk-elbow__part", fav, upper: true));    // 右半分＝カードへ入る横線
            return e;
        }

        /// <summary>上下2分割の帯。upper 側に横線（＝帯の縦中央に線が来る）。</summary>
        private static VisualElement Connector(string cls, bool fav, bool upper)
        {
            var e = new VisualElement();
            e.AddToClassList(cls);
            e.Add(Half(upper ? "brk-half--h" : null, fav && upper));
            e.Add(Half(upper ? null : "brk-half--h", fav && !upper));
            return e;
        }

        private static VisualElement Vertical(bool down, bool fav)
        {
            var e = new VisualElement();
            e.AddToClassList("brk-elbow__part");
            e.Add(Half(down ? null : "brk-half--v", fav && !down));
            e.Add(Half(down ? "brk-half--v" : null, fav && down));
            return e;
        }

        private static VisualElement Half(string lineClass, bool fav)
        {
            var e = new VisualElement();
            e.AddToClassList("brk-half");
            if (lineClass != null) e.AddToClassList(lineClass);
            if (lineClass != null && fav) e.AddToClassList("brk-half--fav");
            return e;
        }

        /// <summary>初期表示で自校のカードをビューポート中央に置く（レイアウト確定後に1回だけ）。</summary>
        private void CenterOn(ScrollView scroll, VisualElement target)
        {
            EventCallback<GeometryChangedEvent> cb = null;
            cb = _ =>
            {
                target.UnregisterCallback(cb);
                var view = scroll.contentViewport.layout.size;
                var content = scroll.contentContainer.layout.size;
                var pos = target.ChangeCoordinatesTo(scroll.contentContainer, Vector2.zero);
                scroll.scrollOffset = new Vector2(
                    Mathf.Clamp(pos.x + target.layout.width * 0.5f - view.x * 0.5f, 0f,
                        Mathf.Max(0f, content.x - view.x)),
                    Mathf.Clamp(pos.y + target.layout.height * 0.5f - view.y * 0.5f, 0f,
                        Mathf.Max(0f, content.y - view.y)));
                SyncBracketHead(scroll.scrollOffset.y);
            };
            target.RegisterCallback(cb);
        }

        private static Label Note(string text)
        {
            var l = new Label(text);
            l.AddToClassList("tp-note");
            return l;
        }

        private static VisualElement BuildMatchRow(BracketMatch m)
        {
            var row = new VisualElement();
            row.AddToClassList("tp-team");
            if (m.ManagerInvolved) row.AddToClassList("tp-team--fav");   // 自校の試合を強調

            var body = new VisualElement();
            body.AddToClassList("tp-body");

            var sub = new VisualElement();
            sub.AddToClassList("tp-team-sub");
            var round = new Label(m.RoundName);
            round.AddToClassList("tp-seed");
            sub.Add(round);
            // 校名は太明朝・スコアはコンデンス数字なので、1つの Label に混ぜず分割する
            // （Oswald は欧文専用で和文グリフを持たない＝設計書16 §2 の罠2）。
            var win = UiComponents.SchoolName(m.WinnerName);
            win.style.marginLeft = 8;
            sub.Add(win);
            var score = new Label(m.WinnerScore + " － " + m.LoserScore);
            score.AddToClassList("tp-mscore");
            score.AddToClassList("f-num");
            sub.Add(score);
            if (m.MercyEnded)
            {
                var mercy = new Label("コールド");
                mercy.AddToClassList("tp-seed");
                sub.Add(mercy);
            }
            sub.Add(UiComponents.SchoolName(m.LoserName));
            body.Add(sub);

            row.Add(body);
            return row;
        }

        // ===== ビュー2: 大会展望 =====

        private void RenderPreview()
        {
            var v = new TournamentPreviewState().Build();
            if (v == null) return;

            SetText("tp-title", v.Title);
            SetText("tp-meta", v.Meta);
            SetText("tp-lead", v.Lead);

            Fill("tp-contenders", v.Contenders, BuildCard);
            Fill("tp-notables", v.Notables, BuildNotable);
        }

        // ===== ビュー3: 校別詳細（トーナメント表の高校名クリック, issue #189） =====

        /// <summary>樹形図の高校名クリックの入口。未確定枠・不戦勝は SlotLine 側で clickable=false にして
        /// そもそもここへ来ないようにしている。</summary>
        private void OpenSchoolDetail(string schoolName) => ShowSchoolDetail(schoolName);

        private void RenderDetail()
        {
            var r = TournamentPreviewState.BuildSchoolDetail(_detailSchool);
            if (r == null) return;

            SetText("td-title", r.SchoolName);
            SetText("td-tag", r.Tag);
            SetText("td-blurb", r.Sub);

            var host = _root.Q<VisualElement>("td-roster");
            if (host == null) return;
            host.Clear();
            host.Add(MemberHeader());
            foreach (var m in r.Members) host.Add(MemberRow(m));
        }

        private void Fill<T>(string hostName, System.Collections.Generic.List<T> items,
            System.Func<T, VisualElement> build)
        {
            var host = _root.Q<VisualElement>(hostName);
            if (host == null) return;
            host.Clear();
            foreach (var it in items) host.Add(build(it));
        }

        private static VisualElement BuildCard(TournamentPreviewState.ContenderRow c)
        {
            var team = new VisualElement();
            team.AddToClassList("tp-team");
            if (c.IsFavorite) team.AddToClassList("tp-team--fav");

            // 格付けマーク（◎○▲ ＋ ラベル）
            var mark = new VisualElement();
            mark.AddToClassList("tp-mark");
            var sym = new Label(c.MarkSym);
            sym.AddToClassList("tp-mark__sym");
            sym.AddToClassList("tp-mark__sym--" + c.MarkClass);
            mark.Add(sym);
            var mlabel = new Label(c.MarkLabel);
            mlabel.AddToClassList("tp-mark__label");
            mark.Add(mlabel);
            team.Add(mark);

            var body = new VisualElement();
            body.AddToClassList("tp-body");

            var sub = new VisualElement();
            sub.AddToClassList("tp-team-sub");
            // 校名は常に太明朝＝SchoolName 部品（設計書16 §1 のシグネチャ規則）。
            sub.Add(UiComponents.SchoolName(c.Name));
            var chip = UiComponents.RankChipLegacy(c.TierLetter);
            chip.text = "総合 " + c.TierLetter;
            chip.style.marginLeft = 8;
            sub.Add(chip);
            var seed = new Label(c.SeedLabel);
            seed.AddToClassList("tp-seed");
            sub.Add(seed);
            body.Add(sub);

            var blurb = new Label(c.Blurb);
            blurb.AddToClassList("tp-blurb");
            body.Add(blurb);

            var bars = new VisualElement();
            bars.AddToClassList("tp-bars");
            bars.Add(Bar("打力", c.Batting, false));
            bars.Add(Bar("投手", c.Pitching, false));
            bars.Add(Bar("守備", c.Defense, true));
            body.Add(bars);

            team.Add(body);
            return team;
        }

        /// <summary>注目選手カード（背番号バッジ＋氏名＋所属サブ＋成績＋寸評）。</summary>
        private static VisualElement BuildNotable(TournamentPreviewState.NotableRow n)
        {
            var card = new VisualElement();
            card.AddToClassList("tp-pcard");

            var top = new VisualElement();
            top.AddToClassList("tp-pcard__top");
            var no = new Label(n.Number);
            no.AddToClassList("tp-pcard__no");
            no.AddToClassList("f-num");   // 背番号は純数値＝コンデンス体（決定2-B）
            if (!n.IsPitcher) no.AddToClassList("tp-pcard__no--field");   // 野手＝グリーン
            top.Add(no);
            var nm = new Label(n.Name);
            nm.AddToClassList("tp-pcard__nm");
            top.Add(nm);
            var sub = new Label(n.Sub);
            sub.AddToClassList("tp-pcard__sub");
            top.Add(sub);
            card.Add(top);

            var stat = new Label(n.StatLine);
            stat.AddToClassList("tp-pcard__stat");
            card.Add(stat);

            var blurb = new Label(n.Blurb);
            blurb.AddToClassList("tp-pcard__blurb");
            card.Add(blurb);
            return card;
        }

        /// <summary>登録メンバー表の見出し行（背番号・選手・学年・投打）。校別詳細ページの表で使う。</summary>
        private static VisualElement MemberHeader()
        {
            var th = new VisualElement();
            th.AddToClassList("tp-mth");
            th.Add(Cell("背番号", "tp-mc--no"));
            th.Add(Cell("選手", "tp-mc--name"));
            th.Add(Cell("学年", "tp-mc--grade"));
            th.Add(Cell("投打", "tp-mc--hand"));
            return th;
        }

        private static VisualElement MemberRow(TournamentPreviewState.MemberRow m)
        {
            var row = new VisualElement();
            row.AddToClassList("tp-mrow");
            var no = Cell(m.Number, "tp-mc--no");
            no.AddToClassList("f-num");   // 背番号はコンデンス数字（UI原則③）
            row.Add(no);
            row.Add(Cell(m.Name, "tp-mc--name"));
            row.Add(Cell(m.Grade, "tp-mc--grade"));
            row.Add(Cell(m.Hand, "tp-mc--hand"));
            return row;
        }

        private static Label Cell(string text, string cls)
        {
            var l = new Label(text);
            l.AddToClassList(cls);
            return l;
        }

        private static VisualElement Bar(string label, int value, bool defense)
        {
            var bar = new VisualElement();
            bar.AddToClassList("tp-bar");

            var top = new VisualElement();
            top.AddToClassList("tp-bar__top");
            var l = new Label(label);
            l.AddToClassList("tp-bar__label");
            var val = new Label(value.ToString());
            val.AddToClassList("tp-bar__val");
            val.AddToClassList("f-num");   // 数値はコンデンス書体（UI原則③・設計書16 §2）
            top.Add(l);
            top.Add(val);
            bar.Add(top);

            var track = new VisualElement();
            track.AddToClassList("tp-track");
            var fill = new VisualElement();
            fill.AddToClassList("tp-fill");
            if (defense) fill.AddToClassList("tp-fill--def");
            fill.style.width = Length.Percent(Mathf.Clamp(value, 0, 100));
            track.Add(fill);
            bar.Add(track);

            return bar;
        }

        private void SetText(string name, string text)
        {
            var label = _root.Q<Label>(name);
            if (label != null) label.text = text;
        }
    }
}
