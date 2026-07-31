#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Engine.Match.Timeline.Playback;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】試合画面 大改修（ScoreBug / イニング間 / HUD再配置 / 戦術カード）の受け入れスクショ。
    /// フェーズを足すたびにカットを増やす。使い捨て。α後に削除してよい。
    ///
    /// 撮り方は既存シューターと同じ作法（CaptureMode で自動進行を止め AdvanceForCapture で手動駆動、
    /// PanelSettings を一時的に RT へ差し替えて try/finally で必ず戻す）。
    /// </summary>
    public sealed class MatchLiveRenewalShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots/matchlive-renewal";

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[Renewal] MatchLive/Controller が見つからない"); yield break; }
            yield return null;
            Directory.CreateDirectory(Dir);

            yield return Capture(ctrl, "00-start");

            // 走者が出た場面＝塁ダイヤが点く絵。出なければそのまま先へ進む。
            yield return AdvanceUntil(ctrl, pa => pa.BaseFirstAfter || pa.BaseSecondAfter || pa.BaseThirdAfter, 60);
            yield return Capture(ctrl, "01-runners");

            // 得点が入った後＝両校の得点が動いた絵。
            yield return AdvanceUntil(ctrl, pa => pa.Inning >= 4, 120);
            yield return Capture(ctrl, "02-midgame");

            // 裏の攻撃＝攻撃中の印が下の校へ移る絵。
            yield return AdvanceUntil(ctrl, pa => !pa.IsTop, 40);
            yield return Capture(ctrl, "03-bottom-half");

            // 攻守交代のイニング間画面（CaptureMode では自動で出ないので強制表示）。
            ctrl.ShowInningBreakForCapture();
            yield return Capture(ctrl, "03b-inning-break");
            ctrl.HideInningBreakForCapture();

            // 打席の略記が3件以上たまった打者を探す（本日の打撃成績の並びを撮る）。
            yield return AdvanceUntil(ctrl, pa => pa.Inning >= 6, 200);
            yield return Capture(ctrl, "03c-batter-pa-line");

            // 采配カード（守備中＝6枚）。方針カードは点けたまま残るので、複数群の同時点灯も1枚に入れる。
            ctrl.SelectPitchDefenseForCapture(KokoSim.Engine.Match.Tactics.PitchPolicy.ControlFirst);
            ctrl.SetTacticsHotForCapture("def-infield-in");
            yield return Capture(ctrl, "04b-card-hover-defense");
            ctrl.SetTacticsHotForCapture(null);
            ctrl.SelectPitchDefenseForCapture(null);

            // 攻撃中（自校＝home なので裏）へ進めて打撃の10枚を撮る。
            // 選択（青）＋ホバー拡大＋走者なしで選べないカード（灰）が1枚に入るようにする。
            yield return AdvanceUntil(ctrl, pa => !pa.IsTop, 40);
            ctrl.SelectPitchBattingForCapture(KokoSim.Engine.Match.Tactics.PitchBattingOverride.ForceSwing);
            ctrl.SetTacticsHotForCapture("app-slug");
            yield return Capture(ctrl, "04c-card-offense-selected-hover");
            ctrl.SetTacticsHotForCapture(null);
            ctrl.SelectPitchBattingForCapture(null);

            // 走者が出た場面＝バント/盗塁/エンドランが選べるようになる絵。
            // カードの可否は「いま止まっている采配窓の塁」で引く（設計書15 §8。旧: 次の打席を先読みした
            // BaseFirstAfter）。一括経路の窓はこの打席の投球中の局面なので、打席開始時の塁で探す。
            yield return AdvanceUntil(ctrl, pa => !pa.IsTop && pa.BaseFirstBefore, 60);
            ctrl.SetTacticsHotForCapture("batting-squeeze");
            yield return Capture(ctrl, "04d-card-offense-runner-on");
            ctrl.SetTacticsHotForCapture(null);

            // 終盤（球数が閾値超で色が変わる・スタミナが減る）。カード群より後ろに置く＝カードの絵を
            // 撮り終える前に試合が終わらないようにする（終局後は打席が無く走者ありの絵が撮れない）。
            yield return AdvanceUntil(ctrl, pa => pa.Inning >= 8, 200);
            yield return Capture(ctrl, "04-late");

            var guard = 0;
            while (guard++ < 400 && ctrl.AdvanceForCapture()) { }
            yield return Capture(ctrl, "05-final");
            Debug.Log($"[Renewal] DONE gameOver={ctrl.IsGameOver}");
        }

        private static IEnumerator AdvanceUntil(MatchLiveController ctrl, System.Func<LivePlateAppearance, bool> cond, int max)
        {
            var guard = 0;
            while (guard++ < max && ctrl.AdvanceForCapture())
            {
                var pa = ctrl.CurrentPa;
                if (pa != null && cond(pa)) yield break;
            }
        }

        private IEnumerator Capture(MatchLiveController ctrl, string file)
        {
            ctrl.FreezeCurrentAtResult();
            yield return null;
            yield return new WaitForEndOfFrame();
            yield return null;
            // 対決ビューのステージを明示的に描き直す。パネルを撮影用RTへ差し替えている間は
            // ステージカメラの通常描画とタイミングが噛み合わず、素体が真っ黒（ライト無し）で
            // 焼ける回がある（実機は正常なのに絵だけ黒い＝スクショ経路だけの症状）。
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
                Debug.Log("[Renewal] wrote " + file);
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
