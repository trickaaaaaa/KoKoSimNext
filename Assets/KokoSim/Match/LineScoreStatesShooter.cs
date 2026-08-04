#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Engine.Match.Game;
using KokoSim.Unity.Components;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】ラインスコアの描き分け受け入れスクショ（issue #450）。
    /// 進行中の半回のマスが「0」で埋まらないことを、合成 LiveLineScore を
    /// LineScorePanel.Fill へ直接流して3状態（イニング間・得点進行中・試合終了）で確かめる。
    /// 使い捨て。確認後に削除してよい。
    /// </summary>
    public sealed class LineScoreStatesShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots/linescore-450";

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[LineScore450] MatchLive/Controller が見つからない"); yield break; }
            yield return null;
            Directory.CreateDirectory(Dir);

            var root = ctrl.GetComponent<UIDocument>().rootVisualElement;

            // ラインスコア盤は inning-break オーバーレイの中＝開かないと映らない。
            // ShowInningBreakForCapture は _current（進行中の打席）が無いと黙って何もしないので、
            // 先に1打席進めてから開く。開いた後に合成データで上書きする。
            ctrl.AdvanceForCapture();
            ctrl.FreezeCurrentAtResult();
            yield return null;
            ctrl.ShowInningBreakForCapture();
            yield return null;
            var caption = root.Q<Label>("ib-caption");

            // 状態A: 1回表が0点で終わり、1回裏はまだ1球も投げていない（イニング間）。
            // 修正前はこの home の1回マスに "0" が立っていた＝空欄が期待。
            if (caption != null) caption.text = "1回表 終了";
            var awayA = new LiveLineScore("東雲工", new[] { 0 }, 0, 0, 1, 0);
            var homeA = new LiveLineScore("桜ヶ丘", new int[0], 0, 0, 0, 0);
            LineScorePanel.Fill(root, awayA, homeA, inning: 1, isTop: false, finished: false);
            yield return Capture(ctrl, "01-inning-break-blank");

            // 状態B: 3回裏の途中で2点入っている＝進行中マスが点灯して数字を出す（従来挙動の維持）。
            if (caption != null) caption.text = "3回裏 進行中";
            var awayB = new LiveLineScore("東雲工", new[] { 0, 1, 0 }, 0, 1, 3, 0);
            var homeB = new LiveLineScore("桜ヶ丘", new[] { 0, 0 }, 2, 2, 4, 1);
            LineScorePanel.Fill(root, awayB, homeB, inning: 3, isTop: false, finished: false);
            yield return Capture(ctrl, "02-pending-runs-lit");

            // 状態C: 試合終了＝全マス確定（0 を含めて表示・点灯マスなしの回も 0 が入る）。
            if (caption != null) caption.text = "試合終了";
            var awayC = new LiveLineScore("東雲工", new[] { 0, 1, 0, 0, 2, 0, 0, 0, 1 }, 0, 4, 9, 0);
            var homeC = new LiveLineScore("桜ヶ丘", new[] { 0, 0, 2, 0, 0, 0, 1, 0, 0 }, 0, 3, 7, 1);
            LineScorePanel.Fill(root, awayC, homeC, inning: 9, isTop: false, finished: true);
            yield return Capture(ctrl, "03-finished-confirmed");

            ctrl.HideInningBreakForCapture();

            Debug.Log("[LineScore450] DONE → " + Dir);
        }

        private IEnumerator Capture(MatchLiveController ctrl, string file)
        {
            yield return null;
            yield return new WaitForEndOfFrame();
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
                Debug.Log("[LineScore450] wrote " + file);
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
