#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】背番号デカールの素体別オフセット確認（issue #453）。
    ///
    /// 実試合を実進行のまま流し、打席頭でデカールが立った瞬間に凍結して
    /// ① 既定構図のフル画面 ② 3役の背中の寄り（ステージカメラを一時的に動かす）
    /// ③ <see cref="DuelStageRig.BackNumberDiagForCapture"/> の実測ログ
    /// を購入素体→U2素体（<see cref="DuelStageRig.ForceU2BodyForCapture"/>）の順で撮る。
    /// ついでに打球プレー中の頭上フルネームラベルも1枚撮る（issue #453 完了条件④）。使い捨て。
    /// </summary>
    public sealed class Issue453BackNumberShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots/issue453-backnumber";

        [SerializeField] private float speed = 4f;

        [Tooltip("背番号パスを飛ばして頭上ラベルの確認だけ行う（ラベル待ちの切り分け用）")]
        [SerializeField] private bool labelsOnly = false;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = false;   // 実進行（打席頭で自然にデカールが立つ）
            DuelStageRig.ForceU2BodyForCapture = false;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            yield return null;

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[Issue453] MatchLive が見つからない"); yield break; }
            typeof(MatchLiveController)
                .GetField("_speed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(ctrl, speed);
            Directory.CreateDirectory(Dir);

            if (!labelsOnly) yield return CapturePass(ctrl, "vendor");

            // 打球プレー中の頭上ラベル（購入素体側で1枚だけ。ラベルは素体に依らずUI側）。
            // データ有無でなく**UI上に実際にラベルが見えている**ことを条件にする
            // （開始フレームはプール未反映・固定秒待ちはボール追従カットに乗る=どちらも実害）
            var doc = ctrl.GetComponent<UIDocument>();
            var lastDiag = -10f;
            yield return WaitFor(ctrl, () =>
            {
                var rig = ctrl.DuelRigForCapture;
                if (rig == null || !rig.FieldPlayActive) return false;
                var data = rig.CurrentFieldLabels(6).Count;
                var vis = 0;
                doc.rootVisualElement.Query<VisualElement>(className: "duel-field-label").ForEach(l =>
                {
                    if (l.resolvedStyle.display != DisplayStyle.None &&
                        l.resolvedStyle.visibility == Visibility.Visible) vis++;
                });
                if (Time.unscaledTime - lastDiag > 1f)
                {
                    lastDiag = Time.unscaledTime;
                    Debug.Log($"[Issue453] labels probe: data={data} vis={vis}");
                }
                return vis >= 2;
            }, 240f);
            yield return CaptureFrame(ctrl, "vendor-90-field-labels", "打球プレー中の頭上ラベル");

            if (!labelsOnly)
            {
                // U2素体（購入素体が無い環境のフォールバック）でも浮き・埋まりが出ないこと
                DuelStageRig.ForceU2BodyForCapture = true;
                ctrl.RebuildDuelRigForCapture();
                yield return CapturePass(ctrl, "u2");
                DuelStageRig.ForceU2BodyForCapture = false;
            }

            Debug.Log("[Issue453] DONE");
        }

        private void OnDestroy() => DuelStageRig.ForceU2BodyForCapture = false;

        /// <summary>デカールが立った打席頭で凍結し、フル画面＋3役の寄り＋実測ログを撮る。</summary>
        private IEnumerator CapturePass(MatchLiveController ctrl, string prefix)
        {
            yield return WaitFor(ctrl, () =>
            {
                var rig = ctrl.DuelRigForCapture;
                return ctrl.DuelViewOnForCapture && rig is { Available: true, FieldPlayActive: false }
                       && rig.HasBackNumbersForCapture;
            }, 240f);
            if (!_condHeld) { Debug.LogWarning($"[Issue453] {prefix}: デカールが立たなかった"); yield break; }

            // 既定構図のフル画面を**先に**撮る。寄りの後に回すと、寄りで動かしたカメラを
            // CameraPlan が当て直す前に CaptureFrame の凍結が入り、望遠FOV×寄り位置のまま
            // 「巨大な数字だけの絵」が焼かれる（run6〜8 実害）。
            yield return CaptureFrame(ctrl, prefix + "-00-default", "既定構図");

            // 実測と寄りは**同じ凍結の中**で撮る（凍結を跨ぐと4倍速で投球モーションへ進み、
            // 実測したポーズと絵のポーズが食い違う=3回目の実行で実害）。
            var rig2 = ctrl.DuelRigForCapture;
            ctrl.FreezeLiveForCapture(true);
            Debug.Log($"[Issue453] {prefix} 実測\n" + rig2.BackNumberDiagForCapture());
            var cam0 = rig2.CameraForCapture;
            var prevFov = cam0 != null ? cam0.fieldOfView : 0f;
            foreach (var role in new[] { "pitcher", "catcher", "batter" })
            {
                var c = rig2.BackNumberContainerForCapture(role);
                var cam = rig2.CameraForCapture;
                if (c == null || cam == null) { Debug.LogWarning($"[Issue453] {prefix}/{role}: 寄りが撮れない"); continue; }
                // コンテナ +Z=体の正面。デカールは -Z 側にあるので背面側から水平に狙う。
                // ステージカメラは望遠既定（垂直数度）なので、1.6m先で胴が入る画角へ明示的に広げる
                // （構図は縦に収めたい実寸で考える: 30°×1.6m ≒ 縦0.86m。design-18 §3.5.1）
                cam.transform.position = c.position - c.forward * 1.6f + Vector3.up * 0.1f;
                cam.transform.LookAt(c.position);
                cam.fieldOfView = 30f;
                // 捕手の背中は球審が画角を塞ぐので、撮る間だけ球審を伏せる
                var ump = rig2.UmpireGoForCapture;
                var umpWasActive = ump != null && ump.activeSelf;
                if (role == "catcher" && umpWasActive) ump.SetActive(false);
                rig2.RenderNow();
                yield return null;
                rig2.RenderNow();   // RT差し替え直後に真っ黒で焼ける回への保険（既知の回避策）
                SaveRt(rig2.Target, Path.Combine(Dir, $"{prefix}-back-{role}.png"));
                if (role == "catcher" && umpWasActive) ump.SetActive(true);
            }
            if (cam0 != null) cam0.fieldOfView = prevFov;
            ctrl.FreezeLiveForCapture(false);   // 解除＝次のTickでCameraPlanが構図を当て直す
        }

        // 直前の WaitFor が条件を満たして抜けたか。
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
            Debug.LogWarning($"[Issue453] 条件が {limitSeconds}s 以内に立たなかった");
        }

        /// <summary>UIごとフル画面を撮る（MatchLivePitchPumpShooter と同じ作法）。</summary>
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
                SaveRt(rt, Path.Combine(Dir, file + ".png"));
                Debug.Log($"[Issue453] wrote {file}（{what}）");
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
            }
            ctrl.FreezeLiveForCapture(false);
        }

        private static void SaveRt(RenderTexture rt, string path)
        {
            if (rt == null) return;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Destroy(tex);
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
