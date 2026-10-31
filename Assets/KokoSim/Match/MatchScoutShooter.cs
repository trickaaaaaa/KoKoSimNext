#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】試合中の選手能力ビューア（issue #602）のスクショ確認用。使い捨て。
    /// タイムの導線 →［野手能力］→［投手能力］→ 交代を挟んだ後（交代後の実体が出ること）を撮る。
    /// </summary>
    public sealed class MatchScoutShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots/match-scout";

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;   // 自動連続進行を止め、AdvanceForCapture で手動駆動する
            EnsureOnlyMatchLive();
            yield return null;
            yield return null;
            EnsureOnlyMatchLive();

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[Scout] MatchLive/Controller が見つからない"); yield break; }
            yield return null;   // OnEnable で試合生成

            Directory.CreateDirectory(Dir);

            // 何打席か進めて、控えの出入りと今日の並びが実体になった状態にする。
            for (var i = 0; i < 12; i++) ctrl.AdvanceForCapture();

            // ① タイムの導線（ボタン列に「選手能力」が並んでいること）。
            ctrl.OpenScoutForCapture();
            ctrl.CloseScoutForCapture();
            yield return Capture(ctrl, "scout-0-pause-menu");

            // ② 野手能力（既定タブ）。両校の打線・現投手・控えが左右に並ぶ。
            ctrl.OpenScoutForCapture();
            ctrl.SelectScoutTabForCapture(0);
            yield return Capture(ctrl, "scout-1-fielder");

            // ③ 投手能力（両校まとめて列が入れ替わる）。
            ctrl.SelectScoutTabForCapture(1);
            yield return Capture(ctrl, "scout-2-pitcher");
            ctrl.CloseScoutForCapture();

            // ④ 交代を挟んでから開き直す＝**交代後の実体**が出ていること（完了条件）。
            for (var i = 0; i < 40 && ctrl.ManagerOnOffenseForCapture; i++) ctrl.AdvanceForCapture();
            ctrl.OpenSubstitutionForCapture();
            ctrl.PickSubstitutionSlotForCapture(KokoSim.Engine.Match.Field.FieldPosition.Pitcher);
            ctrl.PickSubstitutionCandidateForCapture(0);   // 投枠→控え＝継投
            ctrl.ApplySubstitutionForCapture();
            ctrl.CloseSubstitutionForCapture();
            ctrl.OpenScoutForCapture();
            ctrl.SelectScoutTabForCapture(1);
            yield return Capture(ctrl, "scout-3-after-substitution");

            Debug.Log("[Scout] DONE");
        }

        private IEnumerator Capture(MatchLiveController ctrl, string file)
        {
            ctrl.FreezeCurrentAtResult();
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
                Debug.Log("[Scout] wrote " + file);
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
