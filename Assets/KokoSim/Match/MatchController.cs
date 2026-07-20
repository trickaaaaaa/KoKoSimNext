using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 試合（高速モード）のコントローラ（設計書06 §3.4）。UIDocument（MatchScreen.uxml）へ
    /// MatchState をバインド。「試合開始」で GameEngine を回し、スコアボード＋速報を描画。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class MatchController : MonoBehaviour
    {
        private MatchState _state;
        private VisualElement _root;

        private void OnEnable()
        {
            _state = new MatchState();
            _root = GetComponent<UIDocument>().rootVisualElement;

            var play = _root.Q<Button>("play-btn");
            if (play != null) play.clicked += () => { _state.PlayGame(); Render(); };

            Render();
        }

        private void Render()
        {
            var v = _state.BuildView();

            SetText("away-team", v.AwayTeam);
            SetText("home-team", v.HomeTeam);

            var banner = _root.Q<Label>("result-banner");
            if (banner != null)
            {
                banner.text = v.Played ? v.ResultText : "";
                banner.EnableInClassList("result-banner--win", v.ResultKind == "win");
                banner.EnableInClassList("result-banner--lose", v.ResultKind == "lose");
                banner.EnableInClassList("result-banner--draw", v.ResultKind == "draw");
            }

            var play = _root.Q<Button>("play-btn");
            if (play != null) play.text = v.Played ? "もう一度 ▶" : "試合開始 ▶";

            var empty = _root.Q<Label>("board-empty");
            if (empty != null) empty.style.display = v.Played ? DisplayStyle.None : DisplayStyle.Flex;

            // 成績パネルは試合後のみ表示。
            SetDisplay("home-bat-panel", v.Played);
            SetDisplay("away-bat-panel", v.Played);
            SetDisplay("pit-panel", v.Played);

            BuildScoreboard(v);
            BuildBatTable("home-bat", v.HomeBat);
            BuildBatTable("away-bat", v.AwayBat);
            BuildPitTable("home-pit", v.HomePit);
            BuildPitTable("away-pit", v.AwayPit);
            BuildFeed(v);
        }

        // ===== 出場成績テーブル =====

        private static readonly string[] BatHead = { "順", "守", "選手", "打", "安", "点", "本", "四", "三", "率" };

        private void BuildBatTable(string name, System.Collections.Generic.List<BatRow> rows)
        {
            var c = _root.Q<VisualElement>(name);
            if (c == null) return;
            c.Clear();

            var head = StRow(true);
            head.Add(StCell("順", "st-ord", true));
            head.Add(StCell("守", "st-pos", true));
            head.Add(StCell("選手", "st-name", true));
            foreach (var h in new[] { "打", "安", "点", "本", "四", "三", "率" }) head.Add(StCell(h, "st-num", true));
            c.Add(head);

            foreach (var r in rows)
            {
                var row = StRow(false);
                row.Add(StCell(r.Order, "st-ord", false));
                row.Add(StCell(r.Pos, "st-pos", false));
                row.Add(StCell(r.Name, "st-name", false));
                row.Add(StCell(r.Ab.ToString(), "st-num", false));
                row.Add(StNum(r.H, r.H > 0));
                row.Add(StNum(r.Rbi, r.Rbi > 0));
                row.Add(StNum(r.Hr, r.Hr > 0));
                row.Add(StCell(r.Bb.ToString(), "st-num", false));
                row.Add(StCell(r.So.ToString(), "st-num", false));
                row.Add(StCell(r.Avg, "st-num", false));
                c.Add(row);
            }
        }

        private void BuildPitTable(string name, System.Collections.Generic.List<PitRow> rows)
        {
            var c = _root.Q<VisualElement>(name);
            if (c == null) return;
            c.Clear();

            var head = StRow(true);
            head.Add(StCell("投手", "st-name", true));
            head.Add(StCell("回", "st-ip", true));
            foreach (var h in new[] { "安", "失", "三", "四", "球" }) head.Add(StCell(h, "st-num", true));
            c.Add(head);

            foreach (var r in rows)
            {
                var row = StRow(false);
                row.Add(StCell(r.Name, "st-name", false));
                row.Add(StCell(r.Innings, "st-ip", false));
                row.Add(StCell(r.H.ToString(), "st-num", false));
                row.Add(StNum(r.R, false));
                row.Add(StNum(r.So, r.So > 0));
                row.Add(StCell(r.Bb.ToString(), "st-num", false));
                row.Add(StCell(r.Pitches.ToString(), "st-num", false));
                c.Add(row);
            }
        }

        private static VisualElement StRow(bool head)
        {
            var r = new VisualElement();
            r.AddToClassList("st-row");
            if (head) r.AddToClassList("st-row--head");
            return r;
        }

        private static Label StCell(string text, string widthClass, bool head)
        {
            var l = new Label(text);
            l.AddToClassList("st-cell");
            l.AddToClassList(widthClass);
            if (head) l.AddToClassList("st-cell--head");
            return l;
        }

        private static Label StNum(int value, bool highlight)
        {
            var l = StCell(value.ToString(), "st-num", false);
            if (highlight) l.AddToClassList("st-num--hi");
            return l;
        }

        private void SetDisplay(string name, bool visible)
        {
            var e = _root.Q<VisualElement>(name);
            if (e != null) e.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void BuildScoreboard(MatchView v)
        {
            var sb = _root.Q<VisualElement>("scoreboard");
            if (sb == null) return;
            sb.Clear();
            if (!v.Played) return;

            // 見出し行: [空] 1 2 3 … R H E
            var head = Row();
            head.Add(Cell("", "sb-cell--team", "sb-head"));
            foreach (var col in v.Board) head.Add(Cell(col.Label, "sb-head"));
            head.Add(Cell("R", "sb-cell--rhe", "sb-head"));
            head.Add(Cell("H", "sb-cell--rhe", "sb-head"));
            head.Add(Cell("E", "sb-cell--rhe", "sb-head"));
            sb.Add(head);

            sb.Add(TeamRow(v.AwayTeam, v.Board, away: true, v.AwayRuns, v.AwayHits, v.AwayErrors, v.HomeRuns));
            sb.Add(TeamRow(v.HomeTeam, v.Board, away: false, v.HomeRuns, v.HomeHits, v.HomeErrors, v.AwayRuns));
        }

        private VisualElement TeamRow(string name, System.Collections.Generic.List<InningCol> board,
            bool away, int runs, int hits, int errors, int oppRuns)
        {
            var row = Row();
            row.Add(Cell(name, "sb-cell--team"));
            foreach (var col in board) row.Add(Cell(away ? col.Away : col.Home, null));
            var rCell = Cell(runs.ToString(), "sb-cell--rhe");
            if (runs > oppRuns) rCell.AddToClassList("sb-cell--lead");
            row.Add(rCell);
            row.Add(Cell(hits.ToString(), "sb-cell--rhe"));
            row.Add(Cell(errors.ToString(), "sb-cell--rhe"));
            return row;
        }

        private void BuildFeed(MatchView v)
        {
            var list = _root.Q<VisualElement>("feed-list");
            if (list == null) return;
            list.Clear();
            foreach (var line in v.Feed)
            {
                var l = new Label(line.Text);
                l.AddToClassList("mfeed");
                l.AddToClassList(line.Kind == FeedKind.Header ? "mfeed--header"
                    : line.Kind == FeedKind.Run ? "mfeed--run" : "mfeed--hit");
                list.Add(l);
            }
        }

        // ===== 補助 =====

        private static VisualElement Row()
        {
            var r = new VisualElement();
            r.AddToClassList("sb-row");
            return r;
        }

        private static Label Cell(string text, string mod1, string mod2 = null)
        {
            var l = new Label(text);
            l.AddToClassList("sb-cell");
            if (!string.IsNullOrEmpty(mod1)) l.AddToClassList(mod1);
            if (!string.IsNullOrEmpty(mod2)) l.AddToClassList(mod2);
            return l;
        }

        private void SetText(string name, string text)
        {
            var label = _root.Q<Label>(name);
            if (label != null) { label.text = text; return; }
            var btn = _root.Q<Button>(name);
            if (btn != null) btn.text = text;
        }
    }
}
