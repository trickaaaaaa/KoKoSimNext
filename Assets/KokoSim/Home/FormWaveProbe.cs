#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Text;
using KokoSim.Unity.Shell;
using UnityEngine;

namespace KokoSim.Unity.Home
{
    /// <summary>
    /// 【開発用・エディタ限定】調子（ConditionValue）が週送りで動いているかの実測プローブ。
    /// エースと打線の数人を追いかけ、週ごとの連続値と5段階を出す。
    /// 「絶不調のまま3年」を目で確かめるのは無理なので数値で見る。使い捨て。
    /// </summary>
    public sealed class FormWaveProbe : MonoBehaviour
    {
        private const int Weeks = 40;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory("out");
            var sb = new StringBuilder();

            for (var i = 0; i < 8; i++) { yield return null; yield return new WaitForEndOfFrame(); }

            var roster = RosterService.Active;
            if (roster.Count == 0) { Debug.LogError("[FW] 部員が居ない"); yield break; }

            // 追いかける5人（背番号順の先頭＝エース候補を含む）
            var watch = new System.Collections.Generic.List<KokoSim.Engine.Season.DevelopingPlayer>();
            foreach (var p in roster) { watch.Add(p); if (watch.Count >= 5) break; }

            sb.Append("week");
            foreach (var p in watch) sb.Append('\t').Append(p.Name);
            sb.AppendLine();

            var state = HomeState.Current;
            for (var w = 0; w <= Weeks; w++)
            {
                sb.Append(GameClock.Week);
                foreach (var p in watch)
                    sb.Append('\t').Append(p.ConditionValue.ToString("F2"))
                      .Append('(').Append(KokoSim.Engine.Players.FormModel.Quantize(p.ConditionValue)).Append(')');
                sb.AppendLine();

                state.AdvanceWeek();
                for (var f = 0; f < 2; f++) { yield return null; }
            }

            File.WriteAllText("out/form-wave.tsv", sb.ToString());
            Debug.Log("[FW] DONE out/form-wave.tsv\n" + sb);
        }
    }
}
#endif
