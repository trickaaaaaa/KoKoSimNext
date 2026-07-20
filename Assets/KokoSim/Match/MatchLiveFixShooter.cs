#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Engine.Match.AtBat;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】MatchLive UI挙動修正（9件）の目視確認用バッチ撮影。
    /// デモ試合を走査し、内野ゴロ(#1)/センターフライ(#2)/在塁走者(#3)/BSOカウント(#4,#5)/
    /// 二塁打の返球(#6)/生還のバックホーム(#7) の各シーンを見つけて撮る。α後に削除してよい。
    /// </summary>
    public sealed class MatchLiveFixShooter : MonoBehaviour
    {
        private const int W = 900;
        private const int H = 900;
        private const string Dir = "screenshots/match2d-live";

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[FixShot] MatchLive/Controller が見つからない"); yield break; }
            yield return null;
            Directory.CreateDirectory(Dir);

            bool gotRunners = false, gotDouble = false, gotGrounder = false, gotFly = false,
                 gotK = false, gotBackHome = false, gotInfieldFly = false, gotTriple = false;
            var guard = 0;
            while (guard++ < 1200 && ctrl.AdvanceForCapture())
            {
                var pa = ctrl.CurrentPa;
                if (pa == null) break;
                var res = pa.Play?.Result ?? "";
                var anyRunner = pa.BaseFirstBefore || pa.BaseSecondBefore || pa.BaseThirdBefore;

                if (!gotRunners && pa.Play != null && anyRunner)
                {
                    ctrl.FreezeCurrentAtResult(); yield return CaptureName("fix-3-runners-bso");
                    Debug.Log($"[FixShot] runners: {pa.Inning}回 res='{res}' 1={pa.BaseFirstBefore} 2={pa.BaseSecondBefore} 3={pa.BaseThirdBefore}");
                    gotRunners = true;
                }
                if (!gotDouble && pa.Result == PlateAppearanceResult.Double && pa.Play != null)
                {
                    ctrl.FreezeCurrentAtResult(); yield return CaptureName("fix-6-double-return");
                    Debug.Log($"[FixShot] double: {pa.Inning}回 res='{res}'");
                    gotDouble = true;
                }
                // 三塁打: 返球が走者を追い越さないか（走者到達直前・直後）を確認。
                if (!gotTriple && pa.Result == PlateAppearanceResult.Triple && pa.Play != null)
                {
                    ctrl.SeekForCapture(ctrl.CurrentPlayDuration * 0.80); yield return CaptureName("fixT-triple-runner-near3rd");
                    ctrl.SeekForCapture(ctrl.CurrentPlayDuration * 0.94); yield return CaptureName("fixT-triple-arrive");
                    Debug.Log($"[FixShot] triple: {pa.Inning}回 res='{res}' dur={ctrl.CurrentPlayDuration:F1}");
                    gotTriple = true;
                }
                if (!gotGrounder && res.Contains("ゴロ"))
                {
                    ctrl.SeekForCapture(ctrl.CurrentPlayDuration * 0.45); yield return CaptureName("fix-1-8-grounder-mid");
                    Debug.Log($"[FixShot] grounder: {pa.Inning}回 res='{res}'");
                    gotGrounder = true;
                }
                if (!gotFly && res.Contains("フライ"))
                {
                    ctrl.SeekForCapture(ctrl.CurrentPlayDuration * 0.45); yield return CaptureName("fix-2-fly-mid");
                    ctrl.SeekForCapture(ctrl.CurrentPlayDuration * 0.98); yield return CaptureName("fix-2-fly-late-return");
                    Debug.Log($"[FixShot] fly: {pa.Inning}回 res='{res}'");
                    gotFly = true;
                }
                // 内野手が捕るフライ（セカンド/ショート/サード/ファースト）。捕球者がボールへ・放置されない確認。
                var infieldFly = res.Contains("フライ") &&
                    (res.Contains("セカンド") || res.Contains("ショート") || res.Contains("サード") ||
                     res.Contains("ファースト") || res.Contains("ピッチャー") || res.Contains("キャッチャー"));
                if (!gotInfieldFly && infieldFly)
                {
                    ctrl.SeekForCapture(ctrl.CurrentPlayDuration * 0.55); yield return CaptureName("fixB-infield-fly-mid");
                    ctrl.FreezeCurrentAtResult(); yield return CaptureName("fixB-infield-fly-result");
                    Debug.Log($"[FixShot] infieldFly: {pa.Inning}回 res='{res}'");
                    gotInfieldFly = true;
                }
                if (!gotK && pa.Result == PlateAppearanceResult.Strikeout)
                {
                    ctrl.FreezeCurrentAtResult(); yield return CaptureName("fix-4-strikeout-count");
                    gotK = true;
                }
                if (!gotBackHome && pa.RunsScored > 0 && pa.BaseSecondBefore && pa.Play != null)
                {
                    ctrl.SeekForCapture(ctrl.CurrentPlayDuration * 0.85); yield return CaptureName("fix-7-backhome");
                    Debug.Log($"[FixShot] backhome: {pa.Inning}回 res='{res}' runs={pa.RunsScored}");
                    gotBackHome = true;
                }

                if (gotRunners && gotDouble && gotGrounder && gotFly && gotK && gotBackHome && gotInfieldFly && gotTriple) break;
            }

            Debug.Log($"[FixShot] DONE runners={gotRunners} double={gotDouble} grounder={gotGrounder} " +
                      $"fly={gotFly} K={gotK} backHome={gotBackHome} infieldFly={gotInfieldFly} triple={gotTriple}");
        }

        private IEnumerator CaptureName(string file)
        {
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
                Debug.Log("[FixShot] wrote " + file);
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
