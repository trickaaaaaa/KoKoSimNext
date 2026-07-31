#if UNITY_EDITOR
using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】采配カードの攻守フェーズが「画面に映っている打席」と食い違う場面を数える計測。
    ///
    /// 打席ごとに、①いま映している打席の攻守（自校が打っているか） ②カード列が出している側（TacticsCardDeck の
    /// 内部フェーズ）を突き合わせ、食い違った打席をその局面（回・表裏・アウト）付きで並べる。
    /// 「たまに逆が出る」が偶発なのか、半回の変わり目に必ず起きるのかを絵でなく数で切り分けるため。使い捨て。
    /// </summary>
    public sealed class MatchLiveDeckPhaseProbe : MonoBehaviour
    {
        [SerializeField] private int maxPa = 90;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;   // 自動進行を止めて1打席ずつ手で送る
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            yield return null;

            MatchLiveController ctrl = null;
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid() || d.gameObject.name != "MatchLive") continue;
                ctrl = d.GetComponent<MatchLiveController>();
            }
            if (ctrl == null) { Debug.LogError("[DeckPhase] MatchLive が見つからない"); yield break; }

            const BindingFlags Bf = BindingFlags.NonPublic | BindingFlags.Instance;
            var fDeck = typeof(MatchLiveController).GetField("_signPad", Bf);
            var fAway = typeof(MatchLiveController).GetField("_managerIsAway", Bf);
            var fOffense = typeof(KokoSim.Unity.Components.TacticsCardDeck).GetField("_offense", Bf);
            var managerIsAway = (bool)fAway.GetValue(ctrl);

            var mismatch = 0;
            var total = 0;
            var mismatchAtHalfEnd = 0;
            for (var i = 0; i < maxPa && ctrl.AdvanceForCapture(); i++)
            {
                var pa = ctrl.CurrentPa;
                if (pa == null) continue;
                var deck = fDeck.GetValue(ctrl);
                if (deck == null) continue;
                var deckOffense = (bool)fOffense.GetValue(deck);

                // 映している打席で自校が打っているか（表=先攻の攻撃）。
                var shownOffense = pa.IsTop == managerIsAway;
                total++;
                if (deckOffense != shownOffense)
                {
                    mismatch++;
                    // その打席で半回が終わったか（＝3アウト目を映していたか）。
                    var endedHalf = pa.OutsBefore + OutsMadeIn(pa) >= 3;
                    if (endedHalf) mismatchAtHalfEnd++;
                    Debug.Log($"[DeckPhase] 食い違い {pa.Inning}回{(pa.IsTop ? "表" : "裏")} " +
                              $"開始{pa.OutsBefore}アウト 映像={(shownOffense ? "攻撃" : "守備")} " +
                              $"カード={(deckOffense ? "攻撃" : "守備")} 半回終わり={endedHalf}");
                }
                yield return null;
            }

            Debug.Log($"[DeckPhase] 結果 食い違い {mismatch}/{total} 打席"
                      + $"（うち半回の最後の打席 {mismatchAtHalfEnd} 件）");
        }

        // その打席で増えたアウト数（走者の憤死等は数えない粗い近似。半回終わりの判定にだけ使う）。
        private static int OutsMadeIn(KokoSim.Engine.Match.Timeline.Playback.LivePlateAppearance pa)
            => pa.Result is KokoSim.Engine.Match.AtBat.PlateAppearanceResult.Strikeout
                or KokoSim.Engine.Match.AtBat.PlateAppearanceResult.InPlayOut ? 1 : 0;

        private static void EnsureOnlyMatchLive()
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                d.gameObject.SetActive(d.gameObject.name == "MatchLive");
            }
        }
    }
}
#endif
