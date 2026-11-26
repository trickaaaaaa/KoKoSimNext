using System.Collections.Generic;
using KokoSim.Engine.Nation;
using KokoSim.Unity.Components;
using KokoSim.Unity.NewGame;
using KokoSim.Unity.Shell;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Tournament
{
    /// <summary>
    /// 勢力図マップ（issue #1494・design-06 §3.5）。県ごとに「その県の最強校の総合力」をランク色5段（S/A/B/C/D以下）で
    /// 塗り分ける。ランクは凡例のチップ＋札の文字を併記する（色だけに頼らない）。県を選んでの詳細は出さない。
    /// 総合力は他画面の相手校ランクと同じ <see cref="OpponentRank"/>（実ロスターから出す）、自校の県は自校の総合も候補に入れる。
    /// 表示専用＝乱数を引かず、全国の状態にも触れない。
    /// </summary>
    internal sealed class PowerMapPanel
    {
        private readonly JapanMapElement _map = new JapanMapElement();
        private readonly Dictionary<int, (Tier Step, Tier Real, int Points)> _byPrefecture = new Dictionary<int, (Tier, Tier, int)>();

        public PowerMapPanel(VisualElement mapHost, VisualElement legendHost)
        {
            if (mapHost != null)
            {
                mapHost.Clear();
                _map.style.flexGrow = 1f;
                mapHost.Add(_map);
            }
            _map.FillOf = f => _byPrefecture.TryGetValue(f.PrefectureId, out var v)
                ? RankPalette.Of(v.Step.ToString())
                : (UnityEngine.Color?)null;
            _map.TipOf = f => _byPrefecture.TryGetValue(f.PrefectureId, out var v)
                ? PrefectureNames.JapaneseOf(f.PrefectureId) + "　" + v.Real + "　" + v.Points
                : JapanMapElement.LabelOf(f);

            if (legendHost != null)
            {
                legendHost.Clear();
                foreach (var step in PrefecturePowerMap.Steps)
                {
                    var chip = UiComponents.RankChip(step.ToString());
                    chip.AddToClassList("tp-map-legend__chip");
                    legendHost.Add(chip);
                }
            }
        }

        /// <summary>今の全国の勢力で塗り直す（画面を開くたび・週が進むたび）。</summary>
        public void Refresh()
        {
            _byPrefecture.Clear();
            foreach (var kv in PrefecturePowerMap.StrongestByPrefecture(NationService.Nation.Schools))
                _byPrefecture[kv.Key] = Rate(OpponentRank.ProfileOf(kv.Value).Overall);

            // 自校は全国の学校一覧に含まれない（専用ID）ので、自校の県は自校の総合とも比べる。
            var mine = Rate(TeamOverall.Of(RosterService.Active));
            var home = NationService.PrefectureId;
            if (!_byPrefecture.TryGetValue(home, out var cur) || mine.Points > cur.Points)
                _byPrefecture[home] = mine;

            var map = JapanMapProvider.Map;
            if (map != null) _map.SetView(map.National);
            _map.Refresh();
        }

        // 点数は画面に出す整数（四捨五入）。ランクも同じ点数から決める＝点数とランクが食い違わない。
        private static (Tier Step, Tier Real, int Points) Rate(double overall)
        {
            var real = Tiers.FromDisplayed(overall);
            return (PrefecturePowerMap.StepOf(real), real, Tiers.DisplayPoints(overall));
        }
    }
}
