#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Engine.Match.Field;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】交代の予約と取り消し（issue #718）のスクショ確認用。
    /// ①控えを先に選ぶ（枠は未選択＝継投にならない）②その後で左翼の枠＝守備固めのプレビュー
    /// ③予約したあと（予約一覧＋「確定して試合へ戻る」）④全部やめるで元に戻ったか、を撮って検査する。
    /// </summary>
    public sealed class MatchSubDraftShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots/match-subdraft";

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null;
            yield return null;
            EnsureOnlyMatchLive();

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[SubDraft] MatchLive/Controller が見つからない"); yield break; }
            yield return null;
            Directory.CreateDirectory(Dir);

            for (var i = 0; i < 3; i++) ctrl.AdvanceForCapture();
            for (var i = 0; i < 40 && ctrl.ManagerOnOffenseForCapture; i++) ctrl.AdvanceForCapture();

            var beforeLf = ctrl.FielderForCapture(FieldPosition.LeftField)?.Name;
            var beforeP = ctrl.FielderForCapture(FieldPosition.Pitcher)?.Name;
            ctrl.OpenSubstitutionForCapture();
            ctrl.PickSubstitutionCandidateForCapture(2);
            yield return Capture(ctrl, "subdraft-1-candidate-first");
            Debug.Log("[SubDraft] note after candidate-first: " + ctrl.SubstitutionNoteForCapture);

            ctrl.PickSubstitutionSlotForCapture(FieldPosition.LeftField);
            yield return Capture(ctrl, "subdraft-2-then-slot");
            Debug.Log("[SubDraft] note after slot: " + ctrl.SubstitutionNoteForCapture);

            ctrl.ApplySubstitutionForCapture();
            ctrl.PickSubstitutionSlotForCapture(FieldPosition.SecondBase);
            ctrl.PickSubstitutionSlotForCapture(FieldPosition.Shortstop);
            ctrl.ApplySubstitutionForCapture();
            yield return Capture(ctrl, "subdraft-3-reserved");
            var midLf = ctrl.FielderForCapture(FieldPosition.LeftField)?.Name;

            ctrl.CancelSubstitutionForCapture();
            yield return null;
            var afterLf = ctrl.FielderForCapture(FieldPosition.LeftField)?.Name;
            var afterP = ctrl.FielderForCapture(FieldPosition.Pitcher)?.Name;
            Debug.Log("[SubDraft] LF before=" + beforeLf + " reserved=" + midLf + " afterCancel=" + afterLf
                + " / P before=" + beforeP + " after=" + afterP
                + (beforeLf == afterLf && beforeP == afterP && midLf != beforeLf ? "  OK" : "  NG"));
            yield return Capture(ctrl, "subdraft-4-after-cancel");
            Debug.Log("[SubDraft] DONE");
        }

        private IEnumerator Capture(MatchLiveController ctrl, string file)
        {
            yield return null;
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
