#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】タイム（中断ポップアップ）の見え方の確認用。
    /// ①実際の試合で守備中・攻撃中を1枚ずつ ②ViewModel を書き換えてアウト0〜2・走者の組み合わせ・
    /// 伝令0回・延長（枠1）を撮る（表示だけの差し替え＝エンジンに触れない）③伝令役の人選。
    /// 書体の監査（FontAudit）を timeout-fonts.txt に書く。
    /// </summary>
    public sealed class TimeoutPopupShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots/timeout-popup";

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            // 自校＝真値・相手＝推定の見え方で撮る（デモは校IDを持たないので差し込む）。
            MatchLiveController.ManagerSchoolIdForCapture = true;
            // 既に開いていた MatchLive はフラグ前の試合なので、一度閉じて作り直させる。
            FindDoc()?.gameObject.SetActive(false);
            yield return null;
            EnsureOnlyMatchLive();
            yield return null;
            yield return null;
            EnsureOnlyMatchLive();

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[Timeout] MatchLive/Controller が見つからない"); yield break; }
            yield return null;
            Directory.CreateDirectory(Dir);

            // ① 実試合の状態（先頭から進め、守備中と攻撃中の最初の局面を1枚ずつ）。
            ctrl.AdvanceForCapture();
            var firstSide = ctrl.ManagerOnOffenseForCapture;
            ctrl.OpenPauseMenuForCapture();
            yield return Capture(ctrl, firstSide ? "real-offense" : "real-defense");
            for (var i = 0; i < 40 && ctrl.ManagerOnOffenseForCapture == firstSide; i++) ctrl.AdvanceForCapture();
            // 攻守が替わった直後は「3アウト目の打席」が画面に残る＝もう1打席進めて新しい半イニングの局面で撮る。
            ctrl.AdvanceForCapture();
            ctrl.OpenPauseMenuForCapture();
            yield return Capture(ctrl, firstSide ? "real-defense" : "real-offense");

            // ② 表示の差し替え（いまの実データの上で、状況・伝令だけ書き換える）。
            yield return Variant(ctrl, "v1-o0-none", m => { m.Outs = 0; Runners(m, false, false, false); });
            yield return Variant(ctrl, "v2-o1-1b", m => { m.Outs = 1; Runners(m, true, false, false); });
            yield return Variant(ctrl, "v3-o2-2b3b", m => { m.Outs = 2; Runners(m, false, true, true); });
            yield return Variant(ctrl, "v4-o2-loaded", m => { m.Outs = 2; Runners(m, true, true, true); });
            yield return Variant(ctrl, "v5-1b3b-left1", m =>
            {
                m.Outs = 1; Runners(m, true, false, true);
                m.OffenseTimeoutsLeft = 1; m.DefenseTimeoutsLeft = 1;
            });
            yield return Variant(ctrl, "v6-timeout-0", m =>
            {
                m.OffenseTimeoutsLeft = 0; m.DefenseTimeoutsLeft = 0; m.TimeoutEnabled = false;
            });
            yield return Variant(ctrl, "v7-extra-inning", m =>
            {
                m.Inning = 10; m.TimeoutCap = 1; m.OffenseTimeoutsLeft = 1; m.DefenseTimeoutsLeft = 1;
                Runners(m, true, true, false); m.Outs = 0;
            });
            yield return Variant(ctrl, "v8-resolving", m => { m.TimeoutEnabled = false; m.ActionsEnabled = false; });

            // ③ 伝令役の人選。
            ctrl.OpenMessengerPickerForCapture();
            yield return Capture(ctrl, "messenger-picker");

            MatchLiveController.ManagerSchoolIdForCapture = false;
            Debug.Log("[Timeout] DONE");
        }

        private static void Runners(TimeoutPopupModel m, bool b1, bool b2, bool b3)
        {
            m.OnFirst = b1; m.OnSecond = b2; m.OnThird = b3;
        }

        private IEnumerator Variant(MatchLiveController ctrl, string file, Action<TimeoutPopupModel> edit)
        {
            ctrl.OpenPauseMenuForCapture();
            var m = ctrl.TimeoutModelForCapture();
            if (m == null) yield break;
            edit(m);
            ctrl.RenderTimeoutForCapture(m);
            yield return Capture(ctrl, file);
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
                File.AppendAllText(Path.Combine(Dir, "timeout-fonts.txt"),
                    file + ": " + FontAudit.Report(ctrl.PauseModalForCapture) + "\n");
                Debug.Log("[Timeout] wrote " + file);
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

        private static MatchLiveController FindController() => FindDoc()?.GetComponent<MatchLiveController>();

        private static UIDocument FindDoc()
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                if (d.gameObject.name == "MatchLive") return d;
            }
            return null;
        }
    }
}
#endif
