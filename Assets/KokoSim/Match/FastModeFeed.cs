using System;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Unity.MatchResult;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 高速試合モードの勝率バーとテキスト速報（issue #1494・design-06 §3.4）。表示専用＝乱数を引かず、試合の進行にも触れない。
    /// 勝率は engine の <see cref="LiveWinProbability"/>（直前の打席の直後の場面から出す簡易の期待勝率）、
    /// 速報は実況ログ（<see cref="MatchProgression.PlayLog"/>）の打席結果を1行ずつ、新しいものを上に積む。
    /// </summary>
    internal sealed class FastModeFeed
    {
        private const int MaxLines = 12;

        private readonly Label _awayPct;
        private readonly Label _homePct;
        private readonly VisualElement _awaySeg;
        private readonly VisualElement _homeSeg;
        private readonly VisualElement _feed;
        private int _shown;

        public FastModeFeed(VisualElement board)
        {
            _awayPct = board.Q<Label>("fm-wp-away-pct");
            _homePct = board.Q<Label>("fm-wp-home-pct");
            _awaySeg = board.Q<VisualElement>("fm-wp-away");
            _homeSeg = board.Q<VisualElement>("fm-wp-home");
            _feed = board.Q<VisualElement>("fm-feed");
        }

        /// <summary>高速モードに入るとき。それまでの打席は積まず、ここから先だけを速報にする。</summary>
        public void Begin(MatchProgression prog)
        {
            _shown = prog?.PlayLog.Count ?? 0;
            _feed?.Clear();
        }

        public void Update(MatchProgression prog, string awayName, string homeName, double homePrior)
        {
            if (prog == null) return;
            var log = prog.PlayLog;
            UpdateBar(prog, log, awayName, homeName, homePrior);
            AppendLines(log);
        }

        private void UpdateBar(MatchProgression prog, System.Collections.Generic.IReadOnlyList<PlayLogEntry> log,
            string awayName, string homeName, double homePrior)
        {
            if (_awaySeg == null || _homeSeg == null) return;
            var home = LiveWinProbability.HomeWinProbability(
                log.Count > 0 ? log[log.Count - 1] : null, prog.AwayScore, prog.HomeScore,
                prog.RegulationInnings, homePrior);
            var homePct = Math.Min(99, Math.Max(1, (int)Math.Round(home * 100.0)));
            var awayPct = 100 - homePct;
            _awaySeg.style.flexGrow = awayPct;
            _homeSeg.style.flexGrow = homePct;
            if (_awayPct != null) _awayPct.text = awayName + "　" + awayPct + "%";
            if (_homePct != null) _homePct.text = homePct + "%　" + homeName;
        }

        private void AppendLines(System.Collections.Generic.IReadOnlyList<PlayLogEntry> log)
        {
            if (_feed == null) return;
            if (log.Count < _shown) _shown = log.Count;
            for (var i = _shown; i < log.Count; i++)
            {
                var e = log[i];
                var text = e.Inning + "回" + (e.IsTop ? "表" : "裏") + "　" + e.BatterName + "　" + MatchResultState.PaText(e)
                    + (e.RunsScored > 0 ? "　" + e.RunsScored + "点" : "");
                var line = new Label(text) { pickingMode = PickingMode.Ignore };
                line.AddToClassList("fm-feed__line");
                line.AddToClassList("f-body");
                if (e.RunsScored > 0) line.AddToClassList("fm-feed__line--run");
                _feed.Insert(0, line);
            }
            _shown = log.Count;
            while (_feed.childCount > MaxLines) _feed.RemoveAt(_feed.childCount - 1);
        }
    }
}
