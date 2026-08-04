#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】守備中の頭上フルネームラベルの実機確認（issue #454）。
    /// 実試合を実進行で流し、①打席の既定構図（背番号デカール撤去後＝背中が無地であること）
    /// ②打球プレー中に**UI上へ実際にラベルが出ている**瞬間、の2枚を撮る。
    /// ラベルはデータ有無でなく可視数で待つ（開始フレームはプール未反映・固定秒待ちは
    /// ボール追従カットに乗る＝どちらも issue #453 の検証で実害）。使い捨て。
    /// </summary>
    public sealed class Issue454LabelShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots/issue454-labels";

        [SerializeField] private float speed = 4f;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = false;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            yield return null;

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[Issue454] MatchLive が見つからない"); yield break; }
            typeof(MatchLiveController)
                .GetField("_speed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(ctrl, speed);
            Directory.CreateDirectory(Dir);

            // ① 打席の既定構図（背番号なしの背中）
            yield return WaitFor(ctrl, () =>
            {
                var rig = ctrl.DuelRigForCapture;
                return ctrl.DuelViewOnForCapture && rig is { Available: true, FieldPlayActive: false }
                       && ctrl.CurrentAtBatForCapture != null;
            }, 120f);
            yield return CaptureFrame(ctrl, "01-atbat-no-number", "打席の既定構図（背番号なし）");

            // ② 打球プレー中・ラベルがUIに2枚以上見えている瞬間
            var doc = ctrl.GetComponent<UIDocument>();
            yield return WaitFor(ctrl, () =>
            {
                var rig = ctrl.DuelRigForCapture;
                if (rig == null || !rig.FieldPlayActive) return false;
                var vis = 0;
                doc.rootVisualElement.Query<VisualElement>(className: "duel-field-label").ForEach(l =>
                {
                    if (l.resolvedStyle.display != DisplayStyle.None &&
                        l.resolvedStyle.visibility == Visibility.Visible) vis++;
                });
                return vis >= 2;
            }, 240f);
            yield return CaptureFrame(ctrl, "02-field-labels", "打球プレー中の頭上ラベル");

            Debug.Log("[Issue454] DONE");
        }

        private bool _condHeld;

        private IEnumerator WaitFor(MatchLiveController ctrl, System.Func<bool> cond, float limitSeconds)
        {
            ctrl.FreezeLiveForCapture(false);
            _condHeld = false;
            var t = 0f;
            while (t < limitSeconds)
            {
                if (cond()) { _condHeld = true; yield break; }
                t += Time.deltaTime;
                yield return null;
            }
            Debug.LogWarning($"[Issue454] 条件が {limitSeconds}s 以内に立たなかった");
        }

        private IEnumerator CaptureFrame(MatchLiveController ctrl, string file, string what)
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

            try
            {
                var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                File.WriteAllBytes(Path.Combine(Dir, file + ".png"), tex.EncodeToPNG());
                Destroy(tex);
                Debug.Log($"[Issue454] wrote {file}（{what}／{(_condHeld ? "条件成立" : "時間切れ")}）");
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
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
