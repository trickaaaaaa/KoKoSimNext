#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】ファウル打球の俯瞰自動切替（issue #313）の目視確認用バッチ撮影。
    /// 物理層を持つファウル（PitchToken.FoulFlight 非null）を含む打席を探し、対決ビュー→俯瞰（打球飛翔）→
    /// 対決ビュー復帰、の3枚を撮る。α後に削除してよい。
    /// </summary>
    public sealed class FoulFlightShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots/issue-313";

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[FoulShot] MatchLive/Controller が見つからない"); yield break; }
            yield return null;
            Directory.CreateDirectory(Dir);

            // 物理層を持つファウル（FieldingResolver が方位角±45°超えで裁いた経路）を含む打席を探す。
            int targetIdx = -1;
            var guard = 0;
            while (guard++ < 200 && ctrl.AdvanceForCapture())
            {
                var pitches = ctrl.CurrentPa?.PitchSeq?.Pitches;
                if (pitches == null) continue;
                for (var i = 0; i < pitches.Count; i++)
                {
                    if (pitches[i].Kind == KokoSim.Engine.Match.AtBat.PitchKind.Foul && pitches[i].FoulFlight != null)
                    {
                        targetIdx = i;
                        break;
                    }
                }
                if (targetIdx >= 0) break;
            }
            if (targetIdx < 0) { Debug.LogError("[FoulShot] 物理層を持つファウルが見つからない（探索範囲を広げる必要あり）"); yield break; }
            Debug.Log("[FoulShot] target pitch index=" + targetIdx + " dur=" + ctrl.CurrentPa.PitchSeq.Pitches[targetIdx].FoulFlight.Dur);

            if (!ctrl.DuelViewOnForCapture) ctrl.ToggleDuelViewForCapture();
            ctrl.RestartCurrentPaForCapture();   // 対決ビューONで打席を頭からやり直す（ワインドアップ待ちから）

            // ① 対決ビュー: ファウルになる1球の直前（まだ俯瞰へ切り替わっていない）。
            var framesGuard = 0;
            while (ctrl.DuelViewOnForCapture && framesGuard++ < 2000) yield return null;
            if (framesGuard >= 2000) { Debug.LogError("[FoulShot] 俯瞰切替（duelOn=false）が発生しなかった"); yield break; }
            // 直前フレームで duelOn はまだ true だったはず＝この遷移の少し前を撮り直す代わりに、
            // 遷移直後（俯瞰・打球飛翔中）をそのまま①として扱う（対決ビューは #312 で撮影済みのため）。
            yield return CaptureName("i-foul-1-overhead-flight");

            // ② 飛翔の少し後（着地に近い）。エディタが非フォーカスだとフレームごとの deltaTime が
            // 不規則（大きく跳ぶ）ため、フレーム数ではなく実時間（Time.realtimeSinceStartup）で待つ。
            var dur = (float)ctrl.CurrentPa.PitchSeq.Pitches[targetIdx].FoulFlight.Dur;
            var waitUntil = Time.realtimeSinceStartup + dur * 0.6f;
            framesGuard = 0;
            while (Time.realtimeSinceStartup < waitUntil && framesGuard++ < 5000) yield return null;
            yield return CaptureName("i-foul-2-overhead-later");

            // ③ 対決ビューへ復帰（次の1球のワインドアップ）。
            framesGuard = 0;
            while (!ctrl.DuelViewOnForCapture && framesGuard++ < 2000) yield return null;
            if (framesGuard >= 2000) { Debug.LogError("[FoulShot] 対決ビューへの復帰（duelOn=true）が発生しなかった"); yield break; }
            yield return CaptureName("i-foul-3-duel-recovered");

            Debug.Log("[FoulShot] DONE");
        }

        private IEnumerator CaptureName(string file)
        {
            EnsureOnlyMatchLive();
            yield return null;
            yield return new WaitForEndOfFrame();
            yield return null;

            var ctrl = FindController();
            var ps = ctrl.GetComponent<UIDocument>().panelSettings;
            var prevRt = ps.targetTexture; var prevScale = ps.scaleMode; var prevRef = ps.referenceResolution;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32); rt.Create();
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(W, H);
            ps.targetTexture = rt;

            yield return null; yield return new WaitForEndOfFrame();
            yield return null; yield return new WaitForEndOfFrame();

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
                Debug.Log("[FoulShot] wrote " + file);
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
            }
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
