using System.Collections.Generic;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 観客の入り（issue #705）。観客そのものは焼いた球場メッシュの部位 <c>crowd_0..2</c>
    /// （<c>DuelCrowdBaker</c> が座席TSVから撒く）で、ここは何層まで見せるかを切り替えるだけ。
    /// 表示だけ＝engine・帯・決定論には触れない。
    /// </summary>
    public sealed partial class DuelStageRig
    {
        /// <summary>
        /// 見せる層の数（0=無人／1=まばら／2=半分／3=満員＝<see cref="KokoSim.Engine.Nation.Tournaments.CrowdTurnout"/> の値）。
        /// 球場を組むときに読む受け渡し窓（MatchLiveController.NewDuelRig が試合ごとに設定する）。
        /// </summary>
        public static int CrowdLayersShown = 3;

        private readonly List<(int layer, GameObject go)> _crowdLayers = new();

        private static bool TryCrowdLayer(string partName, out int layer)
        {
            layer = -1;
            const string prefix = "crowd_";
            return partName != null && partName.StartsWith(prefix)
                   && int.TryParse(partName.Substring(prefix.Length), out layer);
        }

        /// <summary>観客の入りを変える（層の表示を切り替えるだけ）。</summary>
        public void SetCrowdLayers(int shown)
        {
            CrowdLayersShown = Mathf.Clamp(shown, 0, 3);
            foreach (var (layer, go) in _crowdLayers)
                if (go != null) go.SetActive(layer < CrowdLayersShown);
        }
    }
}
