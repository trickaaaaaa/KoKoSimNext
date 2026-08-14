#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using KokoSim.Engine.Match.Timeline.Playback;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】得点が本塁到達より早く出ないことの実機確認
    /// （issue score-display-before-home-touch）。使い捨て。
    ///
    /// <para>撮るのは2種類:</para>
    /// <list type="number">
    /// <item>犠飛: ①捕球直後（走者はまだ三塁・点は入っていない） ②走っている途中 ③本塁到達直後（点が入る）</item>
    /// <item>押し出し: ④結果確定の瞬間（まだ点が入っていない） ⑤待ち（1.8秒）のあと（点が入る）</item>
    /// </list>
    ///
    /// <para>⚠ 掲示板の数字は<b>撮った瞬間の値</b>でしかないので、絵だけでは「いつ増えたか」が分からない。
    /// 各コマの再生時計と <c>PostedScoreForCapture</c> を TSV に並べて記録する（撮影と実測を対にする）。</para>
    ///
    /// 出力: screenshots/tagup-score/*.png ／ out/tagup-score/timing.tsv
    /// </summary>
    public sealed class TagUpScoreShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string ShotDir = "screenshots/tagup-score";
        private const string OutDir = "out/tagup-score";
        private const string SpawnFlag = "KokoSim.TagUpScoreShooter.Spawn";

        private const int MaxGames = 24;
        private const int MaxPlatesPerGame = 200;

        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~TagUpScoreAuto").AddComponent<TagUpScoreShooter>();
        }

        private MatchLiveController _ctrl;
        private GameObject _liveGo;
        private StringBuilder _log;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Time.captureDeltaTime = 1f / 30f;
            Directory.CreateDirectory(ShotDir);
            Directory.CreateDirectory(OutDir);

            // ⚠ MatchLive を活性化する**前**に立てる（OnEnable の自動連続進行を抑止する条件）。
            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            _ctrl = FindController();
            if (_ctrl == null) { Debug.LogError("[TagUpShot] MatchLive/Controller が見つからない"); yield break; }
            _liveGo = _ctrl.gameObject;
            yield return null;

            _log = new StringBuilder("種類\tコマ\t再生時計[s]\t掲示得点\t離塁[s]\t到達[s]\n");
            var gotSacFly = false;
            var gotPushIn = false;

            for (var game = 1; game <= MaxGames && !(gotSacFly && gotPushIn); game++)
            {
                if (game > 1) yield return RestartWithSeed((ulong)(81000 + game * 4703));

                for (var plate = 1; plate <= MaxPlatesPerGame && !_ctrl.IsGameOver; plate++)
                {
                    if (!_ctrl.AdvanceForCapture()) break;
                    var pa = _ctrl.CurrentPa;
                    if (pa == null) continue;

                    // 犠飛＝打者はアウト（InPlayOut）・打球あり・点が入る。安打での生還と混ざらないよう
                    // 「捕球を待って離塁している（離塁が接触より十分あと）」も条件に入れる。
                    if (!gotSacFly && pa.Play != null && pa.RunsScored > 0
                        && pa.Result == KokoSim.Engine.Match.AtBat.PlateAppearanceResult.InPlayOut
                        && TagUpLeg(pa.Play) is { } leg && leg.Depart > 1.5)
                    {
                        gotSacFly = true;
                        yield return ShootSacFly(leg.Depart, leg.Arrive);
                        continue;
                    }

                    if (!gotPushIn && pa.Play == null && pa.RunsScored > 0)
                    {
                        gotPushIn = true;
                        yield return ShootPushIn();
                        continue;
                    }

                    // ⚠ 要らない打席も**掲示だけは追いつかせる**（#628 の申し送り）。素通しすると再生が
                    // 一度も終わらないので掲示板が止まり、狙いの打席で溜まった点が一気に出る。
                    _ctrl.FreezeCurrentAtResult();
                    yield return null;
                }
            }

            File.WriteAllText(Path.Combine(OutDir, "timing.tsv"), _log.ToString());
            Debug.Log($"[TagUpShot] 犠飛={(gotSacFly ? "撮れた" : "★出ず")} 押し出し={(gotPushIn ? "撮れた" : "★出ず")} → {ShotDir}");
            Time.captureDeltaTime = 0f;
        }

        /// <summary>犠飛: 捕球直後・走行中・本塁到達直後の3コマ。</summary>
        private IEnumerator ShootSacFly(double depart, double arrive)
        {
            // ⚠ AdvanceForCapture の直後はまだ投球列を流している＝再生時計は動いていない。
            // 打球の再生が始まる（Play かつ時計が正）まで待たないと、3コマとも 0.00 秒で撮れてしまう。
            var waited = 0;
            while (waited++ < 3000
                   && !(_ctrl.StageNameForCapture == "Play" && _ctrl.PlayClockForCapture > 0))
                yield return null;

            yield return PlayUntil(depart - 0.05);
            yield return Shoot("1-sacfly-catch", "犠飛", "①捕球直後", depart, arrive);
            yield return PlayUntil((depart + arrive) * 0.5);
            yield return Shoot("2-sacfly-running", "犠飛", "②走行中", depart, arrive);
            yield return PlayUntil(arrive + 0.15);
            yield return Shoot("3-sacfly-home", "犠飛", "③本塁到達直後", depart, arrive);
            Debug.Log($"[TagUpShot] 犠飛を撮影（離塁 {depart:F2}s → 到達 {arrive:F2}s）");
        }

        /// <summary>押し出し: 結果確定の瞬間と、待ち（1.8秒）のあと。</summary>
        private IEnumerator ShootPushIn()
        {
            // 結果の据え置きに入った瞬間＝再生時計が負に振れたところ（#628 の申し送り）。
            var waited = 0;
            while (waited++ < 3000 && !(_ctrl.StageNameForCapture == "Play" && _ctrl.PlayClockForCapture < 0))
                yield return null;
            yield return Shoot("4-pushin-confirmed", "押し出し", "④確定の瞬間", 0, 0);

            var target = _ctrl.PlayClockForCapture + PlaybackDisplaySync.NonBattedScoreDelaySeconds + 0.2;
            waited = 0;
            while (waited++ < 3000 && _ctrl.PlayClockForCapture < target) yield return null;
            yield return Shoot("5-pushin-added", "押し出し", "⑤待ちのあと", 0, 0);
            Debug.Log("[TagUpShot] 押し出しを撮影");
        }

        /// <summary>再生時計が <paramref name="t"/> に達するまで流す（実装どおりの経路で進める）。</summary>
        private IEnumerator PlayUntil(double t)
        {
            var guard = 0;
            while (guard++ < 3000 && _ctrl.PlayClockForCapture < t && _ctrl.StageNameForCapture == "Play")
                yield return null;
        }

        /// <summary>
        /// タッチアップで本塁へ還る走者のレッグ（打者走者 "打" は打った瞬間に走るので対象外）。
        /// 見つからなければ null＝この打席は犠飛ではない。
        /// </summary>
        private static (double Depart, double Arrive)? TagUpLeg(PlaybackPlay play)
        {
            foreach (var r in play.Runners)
            {
                if (r.Label == "打") continue;
                double? depart = null;
                foreach (var s in r.Segs)
                {
                    var dx = s.To.X - s.From.X; var dy = s.To.Y - s.From.Y;
                    if (dx * dx + dy * dy < 0.25) continue;         // 待機・定点レッグ
                    depart ??= s.T0;
                    if (s.OutAtEnd) return null;                    // 憤死は得点が入らない
                    if (s.To.X * s.To.X + s.To.Y * s.To.Y < 4.0 && depart is { } d)
                        return (d, s.T1);                           // 本塁で終わるレッグ＝生還
                }
            }
            return null;
        }

        /// <summary>同じ画面のまま別シードで試合を引き直す（OnEnable が BuildGame をやり直す）。</summary>
        private IEnumerator RestartWithSeed(ulong seed)
        {
            MatchLiveController.GameSeedOverrideForCapture = seed;
            _liveGo.SetActive(false);
            yield return null;
            _liveGo.SetActive(true);
            yield return null; yield return null;
        }

        /// <summary>画面まるごと1枚（撮影の間は進行を凍らせる＝節目がずれない。#628 と同じ作法）。</summary>
        private IEnumerator Shoot(string file, string kind, string frame, double depart, double arrive)
        {
            _log.Append(kind).Append('\t').Append(frame).Append('\t')
                .Append(_ctrl.PlayClockForCapture.ToString("F2")).Append('\t')
                .Append(_ctrl.PostedScoreForCapture).Append('\t')
                .Append(depart.ToString("F2")).Append('\t').Append(arrive.ToString("F2")).Append('\n');

            _ctrl.FreezeLiveForCapture(true);
            yield return null;
            yield return new WaitForEndOfFrame();
            _ctrl.DuelRigForCapture?.RenderNow();   // シューター経路だけ選手が黒く焼ける回がある
            yield return null;

            var ps = _ctrl.GetComponent<UIDocument>().panelSettings;
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
                File.WriteAllBytes(Path.Combine(ShotDir, file + ".png"), tex.EncodeToPNG());
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
                _ctrl.FreezeLiveForCapture(false);
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
