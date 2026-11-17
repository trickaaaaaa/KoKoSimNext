#if UNITY_EDITOR
using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】issue #1078 の実測。試合を対決ビューで流し、捕手が捕った見逃し（ボール／見逃しストライク）を
    /// 集める（<see cref="DuelStageRig.TakenPitchSamplesForCapture"/>）。1球ごとに「判定点・捕球の瞬間の球・ミットへ置き直した後の球」
    /// がカメラから見て本塁面のどこに見えるかと、球審のコールを比べ、見えた位置とコールが逆になった球を原因別に数える。
    /// 結果は screenshots/taken-pitch-1078/（samples.csv と summary.txt）。
    /// </summary>
    public sealed class TakenPitchViewShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/taken-pitch-1078";
        /// <summary>集める見逃しの数。</summary>
        public static int Target = 150;
        /// <summary>再生速度。</summary>
        public static float Speed = 2f;
        /// <summary>打ち切りの実時間[s]。</summary>
        public static float TimeoutSeconds = 900f;
        /// <summary>自校が攻撃の投球で毎球「待て」を出す（見逃しを早く集める）。</summary>
        public static bool ForceTakeOnOffense = true;
        private bool _wasWindow;
        /// <summary>捕った直後の絵を残す枚数。</summary>
        public static int Shots = 6;
        private int _seen, _shots;
        private const int W = 1600, H = 900;

        private const float HalfW = 0.216f, Bottom = 0.50f, Top = 1.05f, BallR = 0.037f;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            DuelStageRig.TakenPitchSamplesForCapture.Clear();
            DuelStageRig.RecordTakenPitchesForCapture = true;
            MatchLiveController.ScenarioIdForCapture = null;
            MatchLiveController.GameSeedOverrideForCapture = 20261117;
            EnsureOnlyMatchLive(false);
            yield return null;
            EnsureOnlyMatchLive(true);
            yield return null; yield return null;
            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[1078] controller なし"); yield break; }
            if (!ctrl.DuelViewOnForCapture) ctrl.ToggleDuelViewForCapture();
            ctrl.SetSpeedForCapture(Speed);

            var t0 = Time.realtimeSinceStartup;
            while (DuelStageRig.TakenPitchSamplesForCapture.Count < Target
                   && Time.realtimeSinceStartup - t0 < TimeoutSeconds && !ctrl.IsGameOver)
            {
                if (!ctrl.DuelViewOnForCapture) ctrl.ToggleDuelViewForCapture();
                // 自校が攻撃の投球は毎球「待て」（見逃しを早く集める。相手校の打者は AI のまま）。
                // カードはトグルなので、采配の窓に入った最初のコマで1回だけ押す
                bool window = ctrl.AtWindowForCapture;
                if (ForceTakeOnOffense && window && !_wasWindow && ctrl.ManagerOnOffenseForCapture)
                    ctrl.SelectPitchBattingForCapture(KokoSim.Engine.Match.Tactics.PitchBattingOverride.ForceTake);
                _wasWindow = window;
                // 縁から8cm以内の見逃しは、捕った直後の絵を数枚残す（ミットの形の目視確認用）
                var list = DuelStageRig.TakenPitchSamplesForCapture;
                if (list.Count > _seen)
                {
                    _seen = list.Count;
                    var last = list[list.Count - 1];
                    if (_shots < Shots && Mathf.Abs(Edge(last.Plate)) < 0.08f)
                    {
                        _shots++;
                        yield return Capture(ctrl, "catch-" + _shots + (last.CalledStrike ? "-S" : "-B")
                            + "-y" + last.Plate.y.ToString("F2", CultureInfo.InvariantCulture)
                            + "-x" + last.Plate.x.ToString("F2", CultureInfo.InvariantCulture));
                    }
                }
                yield return null;
            }
            DuelStageRig.RecordTakenPitchesForCapture = false;
            Write();
            MatchLiveController.GameSeedOverrideForCapture = null;
            Debug.Log("[1078] DONE n=" + DuelStageRig.TakenPitchSamplesForCapture.Count);
        }

        private static bool Inside(Vector2 p) => p.x >= -HalfW && p.x <= HalfW && p.y >= Bottom && p.y <= Top;

        /// <summary>ゾーンの縁までの距離[m]（内側は正・外側は負）。</summary>
        private static float Edge(Vector2 p)
        {
            float dx = HalfW - Mathf.Abs(p.x);
            float dyLo = p.y - Bottom, dyHi = Top - p.y;
            if (Inside(p)) return Mathf.Min(dx, Mathf.Min(dyLo, dyHi));
            float ox = Mathf.Max(0f, -dx), oy = Mathf.Max(0f, Mathf.Max(-dyLo, -dyHi));
            return -Mathf.Sqrt(ox * ox + oy * oy);
        }

        private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);

        private static void Write()
        {
            var list = DuelStageRig.TakenPitchSamplesForCapture;
            var csv = new StringBuilder("call,plateX,plateY,seenPlateX,seenPlateY,catchX,catchY,mittX,mittY,gap,move,hidden,plateEdge,catchEdge,mittEdge\n");
            int n = list.Count, engineMismatch = 0, parallax = 0, catchFlip = 0, mittFlip = 0, mittFlipVisible = 0;
            int hidden = 0, near = 0, flipWithinBall = 0, catchFlipWithinBall = 0;
            float sumShift = 0f, maxShift = 0f, sumGap = 0f;
            var flips = new StringBuilder();
            foreach (var s in list)
            {
                bool strike = s.CalledStrike;
                csv.Append(strike ? "S" : "B").Append(',').Append(F(s.Plate.x)).Append(',').Append(F(s.Plate.y)).Append(',')
                   .Append(F(s.SeenPlate.x)).Append(',').Append(F(s.SeenPlate.y)).Append(',')
                   .Append(F(s.SeenCatch.x)).Append(',').Append(F(s.SeenCatch.y)).Append(',')
                   .Append(F(s.SeenMitt.x)).Append(',').Append(F(s.SeenMitt.y)).Append(',')
                   .Append(F(s.GapM)).Append(',').Append(F(s.MoveM)).Append(',').Append(s.Hidden ? 1 : 0).Append(',')
                   .Append(F(Edge(s.Plate))).Append(',').Append(F(Edge(s.SeenCatch))).Append(',').Append(F(Edge(s.SeenMitt))).Append('\n');
                if (Inside(s.Plate) != strike) engineMismatch++;
                if (Inside(s.SeenPlate) != strike) parallax++;
                if (Inside(s.SeenCatch) != strike) catchFlip++;
                bool mf = Inside(s.SeenMitt) != strike;
                if (mf) mittFlip++;
                if (mf && !s.Hidden) mittFlipVisible++;
                if (s.Hidden) hidden++;
                if (Mathf.Abs(Edge(s.Plate)) < 0.10f) near++;
                // 見えた球の縁が枠にかかっている（中心は逆側でも、目には「かかって」見える）＝原因3（中心点と見た目）
                if (mf && !s.Hidden && Mathf.Abs(Edge(s.SeenMitt)) < BallR) flipWithinBall++;
                if (Inside(s.SeenCatch) != strike && Mathf.Abs(Edge(s.SeenCatch)) < BallR) catchFlipWithinBall++;
                float shift = (s.SeenMitt - s.SeenPlate).magnitude;
                sumShift += shift; maxShift = Mathf.Max(maxShift, shift); sumGap += Mathf.Max(0f, s.GapM);
                if (mf && !s.Hidden)
                    flips.Append("  ").Append(strike ? "見逃しストライク" : "ボール")
                         .Append(" 判定点(").Append(F(s.Plate.x)).Append(',').Append(F(s.Plate.y)).Append(") 縁まで").Append(F(Edge(s.Plate)))
                         .Append(" → 捕球の瞬間(").Append(F(s.SeenCatch.x)).Append(',').Append(F(s.SeenCatch.y)).Append(") 縁まで").Append(F(Edge(s.SeenCatch)))
                         .Append(" → ミットの中(").Append(F(s.SeenMitt.x)).Append(',').Append(F(s.SeenMitt.y)).Append(") 縁まで").Append(F(Edge(s.SeenMitt)))
                         .Append(" ミット距離=").Append(F(s.GapM)).Append(" 動かした=").Append(F(s.MoveM)).Append('\n');
            }
            var sum = new StringBuilder();
            sum.AppendLine("見逃し " + n + " 球（うち判定点が縁から10cm以内 " + near + " 球）");
            sum.AppendLine("① engine の判定点とコールが逆: " + engineMismatch + "（0 のはず）");
            sum.AppendLine("② 判定点をカメラから見た位置とコールが逆（視差）: " + parallax);
            sum.AppendLine("③ 捕球の瞬間の球（判定面の奥まで運んだ点）とコールが逆: " + catchFlip + "（うち球の半径以内のかすり " + catchFlipWithinBall + "）");
            sum.AppendLine("④ ミットへ置き直した後の球とコールが逆: " + mittFlip + "（うち球を見せていた " + mittFlipVisible + "〔うち球の半径以内のかすり " + flipWithinBall + "〕・消した球 " + hidden + " 球）");
            sum.AppendLine("ミットの中の球の見かけの位置と判定点の見かけの位置の差: 平均 " + F(n > 0 ? sumShift / n : 0f) + "m・最大 " + F(maxShift) + "m");
            sum.AppendLine("捕球の瞬間のミット中心〜球心: 平均 " + F(n > 0 ? sumGap / n : 0f) + "m");
            var moves = new System.Collections.Generic.List<float>();
            foreach (var s in list) moves.Add(s.MoveM);
            moves.Sort();
            if (n > 0)
                sum.AppendLine("置き直しで球を動かした距離: 中央値 " + F(moves[n / 2]) + "m・90% " + F(moves[(int)(n * 0.9f)]) + "m・最大 " + F(moves[n - 1]) + "m");
            sum.AppendLine("--- 見えた位置（ミットの中）とコールが逆で、球を見せていた球 ---");
            sum.Append(flips);
            File.WriteAllText(Path.Combine(Dir, "samples.csv"), csv.ToString());
            File.WriteAllText(Path.Combine(Dir, "summary.txt"), sum.ToString());
        }

        private IEnumerator Capture(MatchLiveController ctrl, string file)
        {
            yield return new WaitForEndOfFrame();
            ctrl.DuelRigForCapture?.RenderNow();
            var ps = ctrl.GetComponent<UIDocument>().panelSettings;
            var prevRt = ps.targetTexture; var prevScale = ps.scaleMode; var prevRef = ps.referenceResolution;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32); rt.Create();
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(W, H);
            ps.targetTexture = rt;
            yield return null;
            yield return new WaitForEndOfFrame();
            Texture2D tex = null;
            try
            {
                tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
                var prevActive = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply();
                RenderTexture.active = prevActive;
                File.WriteAllBytes(Path.Combine(Dir, file + ".png"), tex.EncodeToPNG());
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
            }
        }

        private static void EnsureOnlyMatchLive(bool on)
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                d.gameObject.SetActive(on && d.gameObject.name == "MatchLive");
            }
        }

        private static MatchLiveController FindController()
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
                if (d.gameObject.scene.IsValid() && d.gameObject.name == "MatchLive") return d.GetComponent<MatchLiveController>();
            return null;
        }
    }
}
#endif
