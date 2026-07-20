using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Unity.Components; // 部品辞書（RankChip / AbilityRow）

namespace KokoSim.Unity.Tournament
{
    /// <summary>
    /// 大会画面のコントローラ（設計書06 §3.5b）。UIDocument（TournamentPreview.uxml）に
    /// 2つのサブビューを持ち、画面内で切り替える（新規シーンGameObjectを作らない）。
    ///   1. トーナメント: 自校の勝ち上がり＋各回戦の結果（既定）
    ///   2. 大会展望: 優勝争いの構図＋注目選手＋登録メンバー
    /// 本機能は大会モード中のみ有効。開催中でなければ空状態を出す。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class TournamentPreviewController : MonoBehaviour
    {
        private VisualElement _root;
        private VisualElement _empty, _bracketView, _previewView;
        private bool _showPreview;   // false=トーナメント / true=大会展望

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            _empty = _root.Q<VisualElement>("tp-empty");
            _bracketView = _root.Q<VisualElement>("tp-view-bracket");
            _previewView = _root.Q<VisualElement>("tp-view-preview");

            // 画面を開き直すたびトーナメントから始める（大会の現況が最初に目に入る）。
            _showPreview = false;

            var go = _root.Q<Button>("tp-go-preview");
            if (go != null) go.clicked += () => { _showPreview = true; Render(); };
            var back = _root.Q<Button>("tp-back");
            if (back != null) back.clicked += () => { _showPreview = false; Render(); };

            // 共通トップバーの「今週を進める」で共有週を進める（選手/練習画面と同じ扱い＝GameClock 単一ソース）。
            var advance = _root.Q<Button>("advance");
            if (advance != null) advance.clicked += () => { GameClock.Advance(+1); Render(); };

            Render();
        }

        /// <summary>ビューを明示的に切り替える（スクショ・自動検証用。通常はCTA/戻るボタンから遷移する）。</summary>
        public void ShowPreviewView(bool preview)
        {
            _showPreview = preview;
            Render();
        }

        private void Render()
        {
            var session = GameSession.Current;
            var inTournament = session.InTournament;

            RenderTopBar();

            Show(_empty, !inTournament);
            Show(_bracketView, inTournament && !_showPreview);
            Show(_previewView, inTournament && _showPreview);

            if (!inTournament) return;

            if (_showPreview) RenderPreview();
            else RenderBracket(session.Runner.BuildBracketView());
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
            SetText("week", GameClock.CurrentLabel());

            var rank = _root.Q<VisualElement>("team-rank");
            if (rank == null) return;
            rank.Clear();
            var grade = TeamOverall.GradeOf(RosterService.Roster);
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
            var line = new Label(m.WinnerName + "  " + m.WinnerScore + " － " + m.LoserScore + "  " + m.LoserName);
            line.AddToClassList("tp-team-name");
            line.style.marginLeft = 8;
            sub.Add(line);
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
            Fill("tp-rosters", v.Rosters, BuildRoster);
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
            var name = new Label(c.Name);
            name.AddToClassList("tp-team-name");
            sub.Add(name);
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

        /// <summary>登録メンバー（校ヘッダ＋寸評＋背番号順の表）。表示は背番号・氏名・学年・投打まで。</summary>
        private static VisualElement BuildRoster(TournamentPreviewState.RosterBlock r)
        {
            var block = new VisualElement();
            block.AddToClassList("tp-roster");

            var head = new VisualElement();
            head.AddToClassList("tp-roster__head");
            var name = new Label(r.SchoolName);
            name.AddToClassList("tp-roster__name");
            head.Add(name);
            var tag = new Label(r.Tag);
            tag.AddToClassList("tp-roster__tag");
            head.Add(tag);
            block.Add(head);

            var sub = new Label(r.Sub);
            sub.AddToClassList("tp-roster__sub");
            block.Add(sub);

            block.Add(MemberHeader());
            foreach (var m in r.Members) block.Add(MemberRow(m));
            return block;
        }

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
            row.Add(Cell(m.Number, "tp-mc--no"));
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
