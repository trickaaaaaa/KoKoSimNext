#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】交代ポップアップ（issue #209 表現）の受け入れスクショ。
    /// 継投3種（通常継投／投野交代／野手登板＋backfill）を盤面へ強制表示し、豆腐・レイアウト・
    /// アンバー点灯（野手系のみ）を目視確認する。使い捨て。検証後に削除してよい。
    /// </summary>
    public sealed class MatchSubCallShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots/subcall";

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[SubCall] MatchLive/Controller が見つからない"); yield break; }
            yield return null; // OnEnable で試合生成
            Directory.CreateDirectory(Dir);

            // 盤面に状態を作る（数打席消化して走者・スコア・球数を溜める）。
            var guard = 0;
            while (guard++ < 30 && ctrl.AdvanceForCapture())
            {
                var pa = ctrl.CurrentPa;
                if (pa != null && pa.Inning >= 3) break;
            }

            // 投球replayを結果で凍結する（_replaying=false）。これをしないと Capture がフレームを
            // 跨ぐ間に replay が進んで pitch-call を毎フレーム再表示し、交代ポップアップに重なる
            // （実プレーは打席終了時にdrainするので起きない、シューター固有の副作用の回避）。
            ctrl.FreezeCurrentAtResult();

            // ① 通常継投（控えからの登板）。タグ「投手交代」・アンバー無し。
            ctrl.ForceSubCallForCapture(new PitchingChangeEvent(
                Inning: 3, IsTopHalf: false, DefenseIsAway: false,
                Change: new PitchingChangeOutcome(
                    PitchingChangeKind.ReserveRelief,
                    OutPitcherName: "遠藤", OutPitcherNumber: 1, OutPitcherSourceId: null,
                    InPitcherName: "本間", InPitcherNumber: 11, InPitcherSourceId: null,
                    BackfillPosition: null, BackfillName: null, BackfillNumber: 0, BackfillSourceId: null)));
            yield return Capture(ctrl, "01-reserve-relief");

            // ② 投野交代（在フィールド野手→マウンド・旧投手は空き守備へ）。タグ「投野交代」・アンバー点灯。
            ctrl.ForceSubCallForCapture(new PitchingChangeEvent(
                Inning: 6, IsTopHalf: false, DefenseIsAway: false,
                Change: new PitchingChangeOutcome(
                    PitchingChangeKind.TwoWaySwap,
                    OutPitcherName: "遠藤", OutPitcherNumber: 1, OutPitcherSourceId: null,
                    InPitcherName: "西村", InPitcherNumber: 6, InPitcherSourceId: null,
                    BackfillPosition: FieldPosition.Shortstop, BackfillName: "遠藤", BackfillNumber: 1, BackfillSourceId: null)));
            yield return Capture(ctrl, "02-two-way-swap");

            // ③ 野手登板＋backfill（在フィールド野手が登板・旧投手退場・空き守備をベンチ野手が補充）。
            //    タグ「野手登板」・アンバー点灯。
            ctrl.ForceSubCallForCapture(new PitchingChangeEvent(
                Inning: 8, IsTopHalf: false, DefenseIsAway: false,
                Change: new PitchingChangeOutcome(
                    PitchingChangeKind.FielderReliefWithBackfill,
                    OutPitcherName: "遠藤", OutPitcherNumber: 1, OutPitcherSourceId: null,
                    InPitcherName: "西村", InPitcherNumber: 6, InPitcherSourceId: null,
                    BackfillPosition: FieldPosition.CenterField, BackfillName: "小田", BackfillNumber: 18, BackfillSourceId: null)));
            yield return Capture(ctrl, "03-fielder-relief-backfill");

            Debug.Log("[SubCall] DONE");
        }

        private IEnumerator Capture(MatchLiveController ctrl, string file)
        {
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
                Debug.Log("[SubCall] wrote " + file);
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
