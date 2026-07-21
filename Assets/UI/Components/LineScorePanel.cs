using KokoSim.Engine.Match.Game;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Components
{
    /// <summary>
    /// LineScorePanel（部品辞書 / 設計書16 §4-2）の充填ロジック。
    /// 見た目は Components/components.uss の .lsc*、器は Components/LineScorePanel.uxml。
    ///
    /// 数値はすべてエンジンの観測データ（<see cref="LiveLineScore"/>）から引き、UI側で得点を組み立てない
    /// （不変条件: UIはエンジン出力の表示に徹する）。進行中の半回の得点は
    /// <see cref="LiveLineScore.PendingRuns"/> として分離して渡ってくるので、
    /// 「確定した回のマス」と「進行中の回のマス」を描き分けるだけでよい。
    /// </summary>
    public static class LineScorePanel
    {
        /// <summary>常時見せる回数（実物の掲示板と同じく9回ぶんは点が入る前から枠がある）。</summary>
        private const int MinInnings = 9;

        /// <summary>これを超えたらマス幅を詰める（潰して収めるのではなく詰めて収める）。</summary>
        private const int DenseFrom = 13;

        /// <summary>
        /// ラインスコア（左側の掲示板）を埋める。inning/isTop は現在の回（表裏）。
        /// finished=true なら進行中の回が無い扱いにして「現在の回」の点灯をやめる。
        ///
        /// 右袖の B/S/O ランプ・塁ダイヤは1球ごとに動くので、画面コントローラが
        /// <c>lsc-b1</c>/<c>lsc-base-1</c> 等の name を直接トグルする（この部品は器と見た目だけを持つ）。
        /// </summary>
        public static void Fill(
            VisualElement root, LiveLineScore away, LiveLineScore home,
            int inning, bool isTop, bool finished)
        {
            if (root == null || away == null || home == null) return;

            var panel = root.Q("lsc-root");
            if (panel == null) return;

            // 表示する回数＝9回と「実際に進んだ回」の大きい方。延長はここで自然に伸びる。
            var played = away.InningRuns.Count;
            if (home.InningRuns.Count > played) played = home.InningRuns.Count;
            if (!finished && inning > played) played = inning;
            var innings = played < MinInnings ? MinInnings : played;

            panel.EnableInClassList("lsc--dense", innings >= DenseFrom);

            var current = finished ? 0 : inning;
            var caption = finished ? "試合終了" : inning + (isTop ? "回表" : "回裏");
            BuildHead(root.Q("lsc-head"), innings, current, caption);
            BuildTeam(root.Q("lsc-away"), away, innings, battingNow: !finished && isTop);
            BuildTeam(root.Q("lsc-home"), home, innings, battingNow: !finished && !isTop);
        }

        /// <summary>見出し行＝左上に現在の回（実物の掲示板では空欄の位置）、続けて回番号と R / H / E。</summary>
        private static void BuildHead(VisualElement host, int innings, int currentInning, string caption)
        {
            if (host == null) return;
            host.Clear();

            var head = new Label(caption);
            head.AddToClassList("lsc-cell--inning");
            head.AddToClassList("f-dot");
            host.Add(head);

            for (var i = 1; i <= innings; i++)
            {
                var cell = Cell(i.ToString(), "lsc-cell");
                if (i == currentInning) cell.AddToClassList("lsc-cell--now");
                host.Add(cell);
            }
            host.Add(Total("R", first: true));
            host.Add(Total("H", first: false));
            host.Add(Total("E", first: false));
        }

        /// <summary>チーム行＝校名（太明朝）＋回別得点＋R/H/E。</summary>
        private static void BuildTeam(VisualElement host, LiveLineScore line, int innings, bool battingNow)
        {
            if (host == null) return;
            host.Clear();

            // 掲示板の中だが校名だけは太明朝（design-16 §1 のシグネチャ規則・2026-07-21 にユーザー判断で確定）。
            var name = new Label(line.Name);
            name.AddToClassList("lsc-cell--name");
            name.AddToClassList("f-display");
            host.Add(name);

            var done = line.InningRuns.Count;
            for (var i = 0; i < innings; i++)
            {
                string text;
                var lit = false;
                if (i < done)
                {
                    text = line.InningRuns[i].ToString();
                    lit = line.InningRuns[i] > 0;
                }
                else if (i == done && battingNow)
                {
                    // 進行中の半回。まだ確定していないので InningRuns には無い。
                    text = line.PendingRuns.ToString();
                    lit = line.PendingRuns > 0;
                }
                else
                {
                    text = "";   // まだ来ていない回は空マス
                }

                var cell = Cell(text, "lsc-cell");
                if (lit) cell.AddToClassList("lsc-cell--lit");
                else if (text.Length == 0) cell.AddToClassList("lsc-cell--empty");
                host.Add(cell);
            }

            host.Add(Total(line.Runs.ToString(), first: true));
            host.Add(Total(line.Hits.ToString(), first: false));
            host.Add(Total(line.Errors.ToString(), first: false));
        }

        private static Label Cell(string text, string cls)
        {
            var cell = new Label(text);
            cell.AddToClassList(cls);
            cell.AddToClassList("f-dot");
            return cell;
        }

        /// <summary>R/H/E の見出しマス。R の左に縦罫を入れて回と集計欄を分ける。</summary>
        private static Label Total(string text, bool first)
        {
            var cell = Cell(text, "lsc-cell");
            cell.AddToClassList("lsc-cell--total");
            if (first) cell.AddToClassList("lsc-cell--sep");
            return cell;
        }

    }
}
