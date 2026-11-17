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
    /// 【開発用・エディタ限定】issue #1089 の受け入れ実測。steal-sign-lab（1回表無死一塁・監督が攻撃側）から試合を始め、
    /// 監督の打者に毎打席「バント」を押す。バントが転がって守備ビューで再生されたら、再生の時計と球の高さを毎コマ記録し、
    /// 送球の途中を数コマ撮る。送球が低く速い（頂点が低い・飛翔が1秒を切る）ことを確かめる。
    /// 結果は screenshots/bunt-throw-1089/log.txt と *.png。
    /// </summary>
    public sealed class BuntThrowShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/bunt-throw-1089";
        private const int W = 1600, H = 900;
        /// <summary>撮るバントの再生の本数。</summary>
        public static int Target = 3;
        public static float TimeoutSeconds = 600f;
        /// <summary>撮る再生の時計[s]（接触は約0.85s）。</summary>
        public static float[] ShotClocks = { 2.6f, 2.9f, 3.2f, 3.5f, 3.8f, 4.3f };

        private readonly StringBuilder _log = new StringBuilder();

        /// <summary>1本ごとに試合をやり直す回数の上限（バントがファウル・空振りで打席が終わることがある）。</summary>
        public static int MaxAttempts = 15;
        /// <summary>1回のやり直しでバントの再生を待つ上限[s]。</summary>
        public static float AttemptSeconds = 90f;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            int got = 0;
            float total = 0f;
            for (int attempt = 0; attempt < MaxAttempts && got < Target && total < TimeoutSeconds; attempt++)
            {
                // 1本ごとに場面（1回表無死一塁・監督が攻撃側）から始め直す。シードを変えて犠打・内野安打・封殺を拾う。
                MatchLiveController.ScenarioIdForCapture = "steal-sign-lab";
                MatchLiveController.GameSeedOverrideForCapture = (ulong)(1 + attempt);
                MatchLiveController.ManagerIsAwayOverrideForCapture = true;
                EnsureOnlyMatchLive(false);
                yield return null;
                EnsureOnlyMatchLive(true);
                yield return null; yield return null;
                var ctrl = FindController();
                if (ctrl == null) { Debug.LogError("[1089] controller なし"); yield break; }
                if (!ctrl.DuelViewOnForCapture) ctrl.ToggleDuelViewForCapture();
                ctrl.SetSpeedForCapture(1f);

                bool prevWin = false, shotThis = false;
                float t = 0f;
                while (!shotThis && t < AttemptSeconds && !ctrl.IsGameOver)
                {
                    bool win = ctrl.AtWindowForCapture;
                    // バントの指示は1球ごとに消える（MatchLiveController.ResetPitchTacticsChoice）＝毎球の窓の頭で押す。
                    // 消えた後に押すので、トグルで消灯することはない。走者がいないと押しても落ちる（DropImpossiblePitchChoices）
                    // ＝打席が進んで走者が消えたらこのやり直しは打ち切る。
                    if (win && !prevWin)
                    {
                        if (!ctrl.ManagerOnOffenseForCapture || !ctrl.RunnerOnFirstForCapture) break;
                        ctrl.SelectPitchBattingForCapture(KokoSim.Engine.Match.Tactics.PitchBattingOverride.Bunt);
                    }
                    prevWin = win;
                    t += Time.deltaTime;
                    total += Time.deltaTime;
                    if (!(ctrl.BuntAttemptForCapture && ctrl.FieldPlayActiveForCapture)) { yield return null; continue; }

                    shotThis = true;
                    got++;
                    var result = ctrl.CurrentPa?.Play?.Result ?? "?";
                    _log.AppendLine("=== " + got + ": " + result);
                    int shot = 0;
                    float maxH = 0f; double maxAt = 0;
                    while (ctrl.FieldPlayActiveForCapture)
                    {
                        var clock = ctrl.PlayClockForCapture;
                        var diag = ctrl.DuelRigForCapture != null ? ctrl.DuelRigForCapture.BallHoldDiagForCapture() : "no-rig";
                        _log.Append("  clock=").Append(clock.ToString("F2", CultureInfo.InvariantCulture)).Append(' ').Append(diag).Append('\n');
                        var h = ParseHeight(diag);
                        if (clock > 1.6 && h > maxH) { maxH = h; maxAt = clock; }
                        if (shot < ShotClocks.Length && clock >= ShotClocks[shot])
                        {
                            yield return Capture(ctrl, got + "-" + Slug(result) + "-t" + ShotClocks[shot].ToString("0.0", CultureInfo.InvariantCulture));
                            shot++;
                        }
                        yield return null;
                    }
                    _log.AppendLine("  処理後の球の最高点=" + maxH.ToString("F2", CultureInfo.InvariantCulture) + "m（clock="
                                    + maxAt.ToString("F2", CultureInfo.InvariantCulture) + "）");
                    File.WriteAllText(Path.Combine(Dir, "log.txt"), _log.ToString());
                }
                if (!shotThis) _log.AppendLine("--- やり直し " + (attempt + 1) + ": バントが転がらないまま打席が進んだ（" + t.ToString("F0") + "s）");
            }
            _log.AppendLine("DONE got=" + got);
            File.WriteAllText(Path.Combine(Dir, "log.txt"), _log.ToString());
            MatchLiveController.ScenarioIdForCapture = null;
            MatchLiveController.GameSeedOverrideForCapture = null;
            MatchLiveController.ManagerIsAwayOverrideForCapture = null;
            Debug.Log("[1089] DONE got=" + got);
        }

        private static string Slug(string s) => s.Replace(' ', '_');

        private static float ParseHeight(string diag)
        {
            var i = diag.IndexOf("球高=", System.StringComparison.Ordinal);
            if (i < 0) return 0f;
            var j = diag.IndexOf('m', i);
            float v;
            return j > i && float.TryParse(diag.Substring(i + 3, j - i - 3), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0f;
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
