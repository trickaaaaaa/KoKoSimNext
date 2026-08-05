#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】判定テキストとBSOランプ（issue #479）の受け入れスクショ。
    ///
    /// 撮るのは2種類:
    /// <list type="number">
    /// <item>判定4種（見逃し/空振り/ボール/ファール）＝色・影・大きさ・計器表示（球種/球速）の確認</item>
    /// <item>ランプの点灯パターン＝グローが乗っているか／消灯の凹みが出ているかの確認</item>
    /// </list>
    /// 文言や色の分岐は撮影側に持たせず、本番の <see cref="MatchLiveController.ShowPitchCallForCapture"/>
    /// を通す＝「撮った絵だけ正しい」を作らない。使い捨て。
    /// </summary>
    public sealed class BoardLampShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots/board-lamps-479";
        private const string SpawnFlag = "KokoSim.BoardLampShooter.Spawn";

        /// <summary>撮影を予約する（この後に Play へ入る）。Play 突入のドメインリロードを跨ぐので EditorPrefs 経由。</summary>
        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~BoardLampAuto").AddComponent<BoardLampShooter>();
        }

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[Lamp479] MatchLive/Controller が見つからない"); yield break; }
            yield return null;
            Directory.CreateDirectory(Dir);

            // 盤面に実プレーの絵を置く（判定を芝の上に重ねた状態で見たいので、静止画で固定する）。
            ctrl.AdvanceForCapture();
            ctrl.FreezeCurrentAtResult();
            yield return null;

            // ── 判定4種。カウントはその球の後の値で、ランプの点き方も同時に見る ──
            yield return Shot(ctrl, "01-called-strike",
                new PitchToken(PitchKind.CalledStrike, 1, 1, PitchType.Fastball, 143.0));
            yield return Shot(ctrl, "02-swinging-strike",
                new PitchToken(PitchKind.SwingingStrike, 1, 2, PitchType.Slider, 128.0));
            yield return Shot(ctrl, "03-ball",
                new PitchToken(PitchKind.Ball, 3, 2, PitchType.Curve, 116.0));
            yield return Shot(ctrl, "04-foul",
                new PitchToken(PitchKind.Foul, 3, 2, PitchType.Fork, 132.0));
            // 打席を終わらせる2種（文言が「三振」「フォアボール」へ変わる。カウントは0-0へ戻る前の値）。
            yield return Shot(ctrl, "05-strikeout",
                new PitchToken(PitchKind.SwingingStrike, 1, 3, PitchType.Fork, 131.0));
            yield return Shot(ctrl, "06-walk",
                new PitchToken(PitchKind.Ball, 4, 2, PitchType.Fastball, 141.0));
            // 実1球記録が無い打席（合成投球列）＝球種・球速を持たないので判定だけ出る。
            yield return Shot(ctrl, "07-no-readout",
                new PitchToken(PitchKind.CalledStrike, 0, 1));

            // ── 出現アニメ（160ms・オーバーシュート）が実際に走っているか ──
            // 走っていなくても「出た絵」は同じなので、静止画1枚では絶対に気づけない＝途中のコマを撮る。
            yield return MidAnimShot(ctrl);

            // ── ランプだけ: 消灯〜満了までの点き方（グローと凹みの確認用） ──
            yield return LampShot(ctrl, "10-lamps-0-0-0", 0, 0, 0);
            yield return LampShot(ctrl, "11-lamps-1-1-1", 1, 1, 1);
            yield return LampShot(ctrl, "12-lamps-3-2-2", 3, 2, 2);

            // ── グローの強さ比較（--lamp-glow-a をどこに置くかを絵で選ぶため） ──
            var glows = ctrl.GetComponent<UIDocument>().rootVisualElement
                .Query<VisualElement>(className: "sbug-lamp__glow").ToList();
            foreach (var a in new[] { 1.0f, 0.7f, 0.45f })
            {
                var pct = Mathf.RoundToInt(a * 100f);
                yield return Capture(ctrl, "20-glow-" + pct, () =>
                {
                    ctrl.HidePitchCallForCapture();
                    ctrl.SetCountForCapture(3, 2, 2);
                    foreach (var g in glows) g.style.opacity = a;
                });
            }
            foreach (var g in glows) g.style.opacity = StyleKeyword.Null;

            Debug.Log("[Lamp479] DONE → " + Dir);
        }

        /// <summary>
        /// 出現の途中（約80ms＝160msの折り返し）を撮り、拡大率と不透明度をログに出す。
        /// scale がちょうど1・opacity が1なら**アニメが走っていない**（is-enter を外すのが早すぎて
        /// transition の始点が記録されていない）。絵と数値の両方で判定する。
        /// </summary>
        private IEnumerator MidAnimShot(MatchLiveController ctrl)
        {
            var root = ctrl.GetComponent<UIDocument>().rootVisualElement;
            var stack = root.Q<Label>("pitch-call-judge").parent;
            var pt = new PitchToken(PitchKind.CalledStrike, 1, 1, PitchType.Fastball, 143.0);

            // 立ち上がりを毎フレーム実測してログへ残す（成分で見る＝Vector3.ToString は山を丸め潰す）。
            ctrl.HidePitchCallForCapture();
            yield return null; yield return null;
            ctrl.ShowPitchCallForCapture(pt);
            var t0 = Time.realtimeSinceStartup;
            for (var i = 0; i < 10; i++)
            {
                yield return null;
                Debug.Log("[Lamp479] anim t=" + Mathf.RoundToInt((Time.realtimeSinceStartup - t0) * 1000f)
                          + "ms scale=" + stack.resolvedStyle.scale.value.x.ToString("F4")
                          + " opacity=" + stack.resolvedStyle.opacity.ToString("F3"));
            }

            // 立ち上がり途中（約35ms）の絵。完成形（01）と並べると縮んで薄いことが目で分かる。
            yield return Capture(ctrl, "08-mid-anim", () => ctrl.ShowPitchCallForCapture(pt), 0.035f);
        }

        private IEnumerator Shot(MatchLiveController ctrl, string file, PitchToken pt)
        {
            yield return Capture(ctrl, file, () => ctrl.ShowPitchCallForCapture(pt));
        }

        private IEnumerator LampShot(MatchLiveController ctrl, string file, int b, int s, int o)
        {
            yield return Capture(ctrl, file, () =>
            {
                ctrl.HidePitchCallForCapture();
                ctrl.SetCountForCapture(b, s, o);
            });
        }

        private IEnumerator Capture(MatchLiveController ctrl, string file, System.Action show, float settle = 0.30f)
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

            // RT を差し替えてから出す（判定は自前クロックで消えるので、掲げ直してクロックを0へ戻す）。
            show();

            // 出現アニメ（160ms）が終わってから撮る。is-enter は「表示した次フレーム」に外れ、
            // そこから transition が走るので、数フレームでは足りない＝実時間で待つ。
            // settle を短くすればアニメ途中のコマになる（08-mid-anim）。
            yield return new WaitForSecondsRealtime(settle);
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
                Debug.Log("[Lamp479] wrote " + file);
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
