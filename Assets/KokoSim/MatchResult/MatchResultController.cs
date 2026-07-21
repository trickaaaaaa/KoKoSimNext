using System.Collections.Generic;
using KokoSim.Engine.Match.Game;
using KokoSim.Unity.Components;   // 部品辞書（BoxRow）
using KokoSim.Unity.Shell;        // ScreenRouter
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.MatchResult
{
    /// <summary>
    /// 試合結果画面のコントローラ（issue #13・B案＝左右2カラム）。ライブ観戦（MatchLive）の終局から
    /// 自動で開き、勝敗・スコア・イニングスコアと両校の野手／投手成績・打席結果を一望させる。
    /// 「閉じる」は MatchLive から預かった後処理（大会への結果反映）を呼び、ホームへ戻す。
    /// 表示専用＝ここではエンジンを進めない（帯不変・決定論に影響なし）。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class MatchResultController : MonoBehaviour
    {
        /// <summary>試合終了時に MatchLive が積む表示要求（一度きり）。</summary>
        public sealed class MatchResultRequest
        {
            public GameResult Result;
            public bool ManagerIsAway;
            public string AwayName;
            public string HomeName;
            /// <summary>「閉じる」で呼ぶ後処理（大会への結果反映＋画面遷移）。null ならホームへ戻すだけ。</summary>
            public System.Action OnClose;
        }

        public static MatchResultRequest Pending;

        private VisualElement _root;
        private Button _close;
        private System.Action _onClose;

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            _close = _root.Q<Button>("mr-close");
            if (_close != null) _close.clicked += OnClose;

            var req = Pending;
            Pending = null;
            _onClose = req?.OnClose;

            var result = req?.Result;
            var managerIsAway = req?.ManagerIsAway ?? false;
            var awayName = req?.AwayName ?? "先攻";
            var homeName = req?.HomeName ?? "後攻";
            if (result == null) return;   // 要求なしで開かれた（スクショ等）: 骨組みだけ出す

            Render(MatchResultState.Build(result, managerIsAway, awayName, homeName));
        }

        // 画面を離れるときに登録を解除する（OnEnable が毎回登録するため、外さないと往復のたび多重登録になる）。
        private void OnDisable()
        {
            if (_close != null) _close.clicked -= OnClose;
        }

        /// <summary>スクショ・デバッグ用: 要求を直接与えて描画する（Pending を経由しない）。</summary>
        public void RenderForCapture(GameResult result, bool managerIsAway, string awayName, string homeName)
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            Render(MatchResultState.Build(result, managerIsAway, awayName, homeName));
        }

        private void Render(MatchResultView v)
        {
            var outcome = _root.Q<Label>("mr-outcome");
            if (outcome != null)
            {
                outcome.text = v.OutcomeText;
                outcome.EnableInClassList("mr-outcome--win", v.ManagerWon);
            }
            SetText("mr-score", v.ScoreText);

            RenderLineScore(v);
            RenderSide("mr-left", v.Own);
            RenderSide("mr-right", v.Opponent);
        }

        // ── イニングスコア（見出し＋先攻・後攻の2行。列は両軍の長い方に合わせて伸びる） ──
        private void RenderLineScore(MatchResultView v)
        {
            var head = new List<(string, string)> { ("", "team") };
            foreach (var h in v.InningHeaders) head.Add((h, "inning"));
            head.Add(("計", "total"));
            head.Add(("安", "total"));
            head.Add(("失", "total"));
            Replace("mr-line-head", UiComponents.BoxRow(head, header: true));

            Replace("mr-line-away", LineRow(v.AwayLine));
            Replace("mr-line-home", LineRow(v.HomeLine));
        }

        private static VisualElement LineRow(MatchResultLineRow r)
        {
            var cells = new List<(string, string)> { (r.TeamName, "team") };
            foreach (var s in r.Innings) cells.Add((s, "inning"));
            cells.Add((r.Runs, "total runs"));
            cells.Add((r.Hits, "total"));
            cells.Add((r.Errors, "total"));
            return UiComponents.BoxRow(cells);
        }

        // ── 片チームぶんの3表（野手・投手・打席結果）。左右で完全に同じ組み方をする。 ──
        private void RenderSide(string prefix, MatchResultSideView side)
        {
            SetText(prefix + "-cap", side.SideCaption);
            SetText(prefix + "-name", side.TeamName);

            var bat = Host(prefix + "-bat");
            if (bat != null)
            {
                bat.Clear();
                bat.Add(UiComponents.BoxRow(new[]
                {
                    ("位", "pos"), ("選手", "name"), ("打", "num"), ("安", "num"), ("二", "num"), ("三", "num"),
                    ("本", "num"), ("点", "num"), ("四", "num"), ("三振", "num"), ("率", "avg"),
                }, header: true));
                for (var i = 0; i < side.Batting.Count; i++)
                {
                    var b = side.Batting[i];
                    var cells = new List<(string, string)> { (b.Pos, "pos"), (b.Name, "name") };
                    foreach (var n in b.Numbers) cells.Add((n, "num"));
                    cells.Add((b.Average, "avg"));
                    bat.Add(UiComponents.BoxRow(cells, alt: i % 2 == 1));
                }
            }

            var pit = Host(prefix + "-pit");
            if (pit != null)
            {
                pit.Clear();
                pit.Add(UiComponents.BoxRow(new[]
                {
                    ("", "mark"), ("投手", "name"), ("球数", "num"), ("回", "inn"),
                    ("奪三", "num"), ("失点", "num"), ("四球", "num"),
                }, header: true));
                for (var i = 0; i < side.Pitching.Count; i++)
                {
                    var p = side.Pitching[i];
                    pit.Add(UiComponents.BoxRow(new[]
                    {
                        (p.Mark, "mark"), (p.Name, "name"), (p.Pitches, "num"), (p.Innings, "inn"),
                        (p.StrikeOuts, "num"), (p.Runs, "num"), (p.Walks, "num"),
                    }, alt: i % 2 == 1));
                }
            }

            var pa = Host(prefix + "-pa");
            if (pa != null)
            {
                pa.Clear();
                var head = new List<(string, string)> { ("位", "pos"), ("打者", "paname") };
                for (var i = 1; i <= side.PaColumns; i++) head.Add((i + "打席", "pa"));
                pa.Add(UiComponents.BoxRow(head, header: true));

                for (var i = 0; i < side.PaRows.Count; i++)
                {
                    var r = side.PaRows[i];
                    var cells = new List<(string, string)> { (r.Order, "pos"), (r.Name, "paname") };
                    for (var c = 0; c < r.Cells.Count; c++)
                    {
                        var text = r.Cells[c];
                        // 未到達（打席が回らなかった）は「・」、凡打・三振は淡色（出塁だけスキャンで拾える）。
                        if (text.Length == 0) cells.Add(("・", "pa empty"));
                        else cells.Add((text, r.Reached[c] ? "pa" : "pa dim"));
                    }
                    pa.Add(UiComponents.BoxRow(cells, alt: i % 2 == 1));
                }
            }
        }

        private VisualElement Host(string name) => _root.Q<VisualElement>(name);

        // name を持つ空要素を1行の中身で置き換える（イニングスコアの3行はホストが行そのもの）。
        private void Replace(string name, VisualElement row)
        {
            var host = Host(name);
            if (host == null) return;
            host.Clear();
            host.Add(row);
        }

        private void SetText(string name, string text)
        {
            var l = _root.Q<Label>(name);
            if (l != null) l.text = text;
        }

        // 「閉じる」: MatchLive から預かった後処理（大会反映＋ホームへ）を呼ぶ。
        // クリック配信中に同期 Show するとUITKのイベント木が壊れるため、遷移は必ず遅延切替を使う。
        private void OnClose()
        {
            var cb = _onClose;
            _onClose = null;
            if (cb != null) cb();
            else ScreenRouter.Instance?.ShowDeferred("HomeDashboard");
        }
    }
}
