#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】issue #1053（投手HUDに調子の顔）の受け入れスクショ。使い捨て。
    /// 表（ホームの投手）と裏（アウェーの投手）を1枚ずつ撮り、HUDの投手帯に出した調子をログで裏取りする。
    /// </summary>
    public sealed class PitcherHudConditionShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots/pitcher-hud-condition";

        [SerializeField] private float speed = 8f;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = false;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            yield return null;

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[1053] MatchLive が見つからない"); yield break; }
            typeof(MatchLiveController)
                .GetField("_speed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(ctrl, speed);
            Directory.CreateDirectory(Dir);

            yield return WaitFor(ctrl, () => ctrl.CurrentAtBatForCapture is { IsTop: true } && Thrown(ctrl) >= 3, 120f);
            Log(ctrl, "表");
            yield return Capture(ctrl, "01-top");

            yield return WaitFor(ctrl, () => ctrl.CurrentAtBatForCapture is { IsTop: false } && Thrown(ctrl) >= 3, 240f);
            Log(ctrl, "裏");
            yield return Capture(ctrl, "02-bottom");

            Debug.Log("[1053] DONE");
        }

        private static int Thrown(MatchLiveController ctrl)
        {
            var def = Pitcher(ctrl);
            return def == null ? 0 : def.Pitches + def.PendingPitches;
        }

        private static KokoSim.Engine.Match.Game.LivePitcherToday Pitcher(MatchLiveController ctrl)
        {
            var at = ctrl.CurrentAtBatForCapture;
            var snap = ctrl.SnapshotForCapture();
            if (at == null || snap == null) return null;
            return at.IsTop ? snap.Home.Pitcher : snap.Away.Pitcher;
        }

        private static void Log(MatchLiveController ctrl, string label)
        {
            var def = Pitcher(ctrl);
            if (def == null) { Debug.LogWarning($"[1053] {label}: 投手なし"); return; }
            var hud = ctrl.GetComponent<UIDocument>().rootVisualElement;
            var faces = hud.Query<VisualElement>(className: "mhud-face--after-pc").ToList();
            var r = faces.Count > 0 ? faces[0].worldBound : default;
            Debug.Log($"[1053] {label}: 投手={def.Name} 調子値={def.ConditionValue:0.00} 投手帯の顔={faces.Count}個 "
                + $"顔の矩形={r} 表示={(faces.Count > 0 ? faces[0].resolvedStyle.display.ToString() : "-")}");
        }

        private IEnumerator WaitFor(MatchLiveController ctrl, System.Func<bool> cond, float limitSeconds)
        {
            ctrl.FreezeLiveForCapture(false);
            var t = 0f;
            while (t < limitSeconds)
            {
                if (cond()) yield break;
                t += Time.deltaTime;
                yield return null;
            }
            Debug.LogWarning($"[1053] 条件が {limitSeconds}s 以内に立たなかった");
        }

        private IEnumerator Capture(MatchLiveController ctrl, string file)
        {
            ctrl.FreezeLiveForCapture(true);
            yield return null;
            yield return new WaitForEndOfFrame();
            yield return null;
            ctrl.DuelRigForCapture?.RenderNow();
            yield return null;

            var ps = ctrl.GetComponent<UIDocument>().panelSettings;
            var prevRt = ps.targetTexture; var prevScale = ps.scaleMode; var prevRef = ps.referenceResolution;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32); rt.Create();
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(W, H);
            ps.targetTexture = rt;

            yield return null;
            yield return new WaitForEndOfFrame();
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
                Debug.Log("[1053] wrote " + file);
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
            }
            ctrl.FreezeLiveForCapture(false);
        }

        private static void EnsureOnlyMatchLive()
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                d.gameObject.SetActive(d.gameObject.name == "MatchLive");
            }
        }

        private static MatchLiveController FindController()
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                if (d.gameObject.name == "MatchLive") return d.GetComponent<MatchLiveController>();
            }
            return null;
        }
    }
}
#endif
