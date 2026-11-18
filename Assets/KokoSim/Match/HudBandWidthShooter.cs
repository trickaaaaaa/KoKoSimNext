#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Engine.Match.Game;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】issue #1145 の受け入れスクショ。投手HUD／打者HUDの名前行が帯（最小34%）に収まらないとき、
    /// 帯が左へ伸びて調子の顔まで見えることを撮る。「修正前」は帯の幅をインラインで34%に固定して再現する。
    /// 名前は撮影時だけ描画済みラベルの文字を差し替える（試合の中身・乱数には触れない）。使い捨て。
    /// </summary>
    public sealed class HudBandWidthShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots/hud-band";
        private const string LongName = "佐々木 健太郎丸";
        private const string ShortName = "田中 太郎";

        [SerializeField] private float speed = 8f;
        [SerializeField] private int weeklyLimitForCapture = 140;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = false;
            var limit = weeklyLimitForCapture;
            MatchLiveController.CaptureContextOverride = ctx => ctx with { WeeklyPitchLimit = limit };
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            yield return null;

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[hudband] MatchLive が見つからない"); yield break; }
            typeof(MatchLiveController)
                .GetField("_speed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(ctrl, speed);
            Directory.CreateDirectory(Dir);

            yield return WaitFor(ctrl, () => Thrown(ctrl) >= 25, 240f);
            ctrl.FreezeLiveForCapture(true);
            yield return null; yield return null;
            var root = ctrl.GetComponent<UIDocument>().rootVisualElement;

            SetNames(root, ShortName);
            SetCornerWidth(root, true);
            yield return Capture(ctrl, "01-short-name");

            SetNames(root, LongName);
            SetCornerWidth(root, true);
            yield return Capture(ctrl, "02-long-name-before");

            SetCornerWidth(root, false);
            yield return Capture(ctrl, "03-long-name-after");

            SetNames(root, ShortName);
            yield return Capture(ctrl, "04-short-name-after");

            Debug.Log("[hudband] DONE");
        }

        private static void SetNames(VisualElement root, string name)
        {
            foreach (var l in root.Query<Label>(className: "mhud-nm").ToList()) l.text = name;
        }

        // 修正前＝帯の幅34%固定。after＝インライン指定を外して USS（min-width:34%・中身で伸びる）に任せる。
        private static void SetCornerWidth(VisualElement root, bool fixed34)
        {
            foreach (var c in root.Query<VisualElement>(className: "mhud-corner").ToList())
            {
                if (fixed34) c.style.width = new Length(34, LengthUnit.Percent);
                else c.style.width = StyleKeyword.Null;
            }
        }

        private static void LogWidths(VisualElement root, string label)
        {
            foreach (var c in root.Query<VisualElement>(className: "mhud-corner").ToList())
                Debug.Log($"[hudband] {label} {c.name}{string.Join(".", c.GetClasses())} w={c.resolvedStyle.width:0} h={c.resolvedStyle.height:0} parent={c.parent.resolvedStyle.width:0}");
        }

        private static int Thrown(MatchLiveController ctrl)
        {
            var at = ctrl.CurrentAtBatForCapture;
            var snap = ctrl.SnapshotForCapture();
            if (at == null || snap == null) return 0;
            var def = at.IsTop ? snap.Home.Pitcher : snap.Away.Pitcher;
            return def == null ? 0 : def.Pitches + def.PendingPitches;
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
            Debug.LogWarning($"[hudband] 条件が {limitSeconds}s 以内に立たなかった");
        }

        private IEnumerator Capture(MatchLiveController ctrl, string file)
        {
            ctrl.FreezeLiveForCapture(true);   // 進行だけ止める（暗幕は出さない）
            yield return null;
            yield return new WaitForEndOfFrame();
            yield return null;
            ctrl.DuelRigForCapture?.RenderNow();   // RT差し替え中に真っ黒で焼ける回への対策
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

            LogWidths(ctrl.GetComponent<UIDocument>().rootVisualElement, file);
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
                Debug.Log("[hudband] wrote " + file);
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
