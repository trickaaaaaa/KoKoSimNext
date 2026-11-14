#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】issue #935: 交代パネルで投・捕の枠を選んだとき、控えの候補ごとに
    /// バッテリーの相性が出ることを撮る。バッテリーの積み重ねはここで値を入れる。
    /// </summary>
    public sealed class Issue935SubShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots/issue-935";

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null;
            yield return null;
            EnsureOnlyMatchLive();
            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[935sub] MatchLive/Controller が見つからない"); yield break; }
            yield return null;
            Directory.CreateDirectory(Dir);

            foreach (var p in RosterService.Active)
                foreach (var c in RosterService.Active)
                    if (p.Id != c.Id) GameSession.Current.BatteryLedger.Add(p.Id, c.Id, (p.Id * 7 + c.Id * 3) % 90);

            for (var i = 0; i < 40 && ctrl.ManagerOnOffenseForCapture; i++) ctrl.AdvanceForCapture();
            ctrl.OpenSubstitutionForCapture();
            ctrl.PickSubstitutionSlotForCapture(KokoSim.Engine.Match.Field.FieldPosition.Pitcher);
            yield return Capture(ctrl, "sub-pitcher");
            ctrl.CloseSubstitutionForCapture();
            ctrl.OpenSubstitutionForCapture();
            ctrl.PickSubstitutionSlotForCapture(KokoSim.Engine.Match.Field.FieldPosition.Catcher);
            yield return Capture(ctrl, "sub-catcher");
            ctrl.CloseSubstitutionForCapture();
            ctrl.OpenSubstitutionForCapture();
            ctrl.PickSubstitutionSlotForCapture(KokoSim.Engine.Match.Field.FieldPosition.LeftField);
            yield return Capture(ctrl, "sub-leftfield");
            ctrl.CloseSubstitutionForCapture();
            Debug.Log("[935sub] DONE");
        }

        private IEnumerator Capture(MatchLiveController ctrl, string file)
        {
            ctrl.FreezeCurrentAtResult();
            yield return null;
            yield return new WaitForEndOfFrame();
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
                Debug.Log("[935sub] wrote " + file);
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
