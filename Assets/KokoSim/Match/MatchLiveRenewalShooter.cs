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
        private const string SpawnFlag = "KokoSim.MatchLiveRenewalShooter.Spawn";

        /// <summary>
        /// HUDの寄り矩形（画面座標・左上原点）。タイルの2px枠と17pxの見出しは全景1600×900では
        /// 評価できないので、コーナーだけを切り出して撮る。<see cref="CaptureCrop"/> がRTの
        /// 左下原点へ変換する。
        /// <b>HUDは画面ではなく盤面ホストにアンカーされている</b>ので、画面下端から逆算すると
        /// 投手HUDを外す（実測: matchup-hud は x=282,y=18,w=1036,h=651）。値は帯の worldBound の実測
        /// （打者: 841,18,477,161／投手: 841,463,477,206）に周囲8pxの余白を足したもの
        /// ＝帯が盤面の右端に密着していること自体を絵で確かめられるように右側も余白を取る。
        /// </summary>
        private static readonly Rect BatterHudRect = new Rect(833, 10, 493, 177);
        private static readonly Rect PitcherHudRect = new Rect(833, 455, 493, 222);

        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~RenewalAuto").AddComponent<MatchLiveRenewalShooter>();
        }

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[Renewal] MatchLive/Controller が見つからない"); yield break; }
            // 撮影の試合は LiveMatchRequest を通らないのでチーム配色が載っていない。HUDのチーム色チップが
            // 灰色のフォールバックで写って実物と違う絵になるため、パレット表から2校ぶん差し込む
            //（実試合の経路では上書きされない）。
            var pal = KokoSim.Engine.Nation.TeamColorPalette.Fallback;
            ctrl.FillTeamColorsForCapture(pal.ColorsFor(11), pal.ColorsFor(27));
            yield return null;
            Directory.CreateDirectory(Dir);

            yield return Capture(ctrl, "00-start");
            // HUDタイル：打席がまだ1つも無い段（打席の段が出ない絵）＋「—」の該当なし表示。
            yield return CaptureCrop(ctrl, "10-hud-batter-head", BatterHudRect);
            yield return CaptureCrop(ctrl, "10-hud-pitcher-head", PitcherHudRect);

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
            // HUDタイル：打席チップが複数枚並び、出塁した打席だけ点灯している絵。
            yield return CaptureCrop(ctrl, "11-hud-batter-pa", BatterHudRect);

            // HUDタイル：継投後＝「N番手」チップの地が沈む絵。継投が起きなければ飛ばす。
            yield return AdvanceUntilSnap(ctrl, s => DefensePitcher(ctrl, s)?.ReliefOrder > 1, 200);
            yield return CaptureCrop(ctrl, "12-hud-relief", PitcherHudRect);

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
            // HUDの球数タイルの色はイニング数でなく残量で待つ（閾値がスタミナ残量なので、
            // イニングで当てずっぽうに撮ると色が付かない回がある）。
            yield return AdvanceUntilSnap(ctrl, s => DefensePitcher(ctrl, s)?.StaminaRemaining < 0.6, 200);
            yield return CaptureCrop(ctrl, "13-hud-warn", PitcherHudRect);
            yield return AdvanceUntilSnap(ctrl, s => DefensePitcher(ctrl, s)?.StaminaRemaining < 0.3, 200);
            yield return CaptureCrop(ctrl, "14-hud-alert", PitcherHudRect);

            yield return AdvanceUntil(ctrl, pa => pa.Inning >= 8, 200);
            yield return Capture(ctrl, "04-late");
            yield return CaptureCrop(ctrl, "15-hud-batter-late", BatterHudRect);
            yield return CaptureCrop(ctrl, "15-hud-pitcher-late", PitcherHudRect);

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

        /// <summary>
        /// 観測スナップショットの状態で待つ版。HUDの状態コード（継投・スタミナ帯）は打席の属性では
        /// 引けないので、イニング数で当てずっぽうに待たずこちらを使う。
        /// </summary>
        private static IEnumerator AdvanceUntilSnap(
            MatchLiveController ctrl, System.Func<KokoSim.Engine.Match.Game.MatchLiveSnapshot, bool> cond, int max)
        {
            var guard = 0;
            while (guard++ < max && ctrl.AdvanceForCapture())
            {
                var snap = ctrl.SnapshotForCapture();
                if (snap != null && cond(snap)) yield break;
            }
        }

        /// <summary>いま守っている側の投手（HUDが映すのと同じ選び方）。</summary>
        private static KokoSim.Engine.Match.Game.LivePitcherToday DefensePitcher(
            MatchLiveController ctrl, KokoSim.Engine.Match.Game.MatchLiveSnapshot snap)
        {
            var offenseIsTop = ctrl.CurrentPa?.IsTop ?? snap.OffenseIsTop;
            return offenseIsTop ? snap.Home.Pitcher : snap.Away.Pitcher;
        }

        private IEnumerator Capture(MatchLiveController ctrl, string file) => Shoot(ctrl, file, null);

        /// <summary>
        /// 画面の一部だけを切り出して撮る（HUDタイルの寄り）。<paramref name="screenRect"/> は
        /// <b>画面座標＝左上原点</b>で指定し、ここでRTの左下原点へ変換する（上下を取り違えると
        /// 黙って別の場所が写るだけで、エラーは出ない）。
        /// </summary>
        private IEnumerator CaptureCrop(MatchLiveController ctrl, string file, Rect screenRect)
            => Shoot(ctrl, file, screenRect);

        private IEnumerator Shoot(MatchLiveController ctrl, string file, Rect? screenRect)
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

            // RT は左下原点なので、画面座標（左上原点）の y を反転して読み出し矩形を作る。
            var read = screenRect is { } r
                ? new Rect(r.x, H - r.y - r.height, r.width, r.height)
                : new Rect(0, 0, W, H);
            Texture2D tex = null;
            try
            {
                tex = new Texture2D((int)read.width, (int)read.height, TextureFormat.RGBA32, false);
                var prevActive = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(read, 0, 0);
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
