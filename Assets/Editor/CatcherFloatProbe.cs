#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Unity.Match;
using UnityEngine;

namespace KokoSim.UnityEditorTools
{
    /// <summary>
    /// 【開発用・エディタ限定】捕手の接地の実測（issue #607）。
    ///
    /// <para><b>症状</b>: 盗塁の捕手送球を挟むと、以降ずっと捕手が構えのまま宙に浮く。</para>
    ///
    /// <para><b>測るもの</b>: 接地誤差[m]＝低い方の足y −（接地面＋足首オフセット）。
    /// 正＝宙に浮き／負＝めり込み。定義は <c>DuelStageRig.FootClearance</c> 単一ソース
    /// （<c>GroundFix</c> が消す量そのもの＝プローブ側に写しを持たない。持つと
    /// 「直したのに別の式で測って NG に見える」が起きる）。あわせて立ち位置の基準
    /// <c>BasePos.y</c> も出す＝汚れた瞬間が読める（今回の原因の直接観測）。</para>
    ///
    /// <para><b>なぜ往復させるか</b>: 壊れているのは構えの描画単体ではなく
    /// 「対決ビュー → 盗塁の送球（<c>StepDuelThrow</c>。立ちクリップ）→ 守備ビュー →
    /// 対決ビューへ戻る」という<b>状態の往復</b>で、構えだけ描いても1周もしない＝何も確かめられない。
    /// 盗塁は <see cref="PlaybackSamples"/> に収録が無い（打球が出ないプレー）ので
    /// <see cref="TimelineBuilder.BuildSteal"/> で組む（<c>DuelStealThrowFacingShot</c> と同じ作法）。</para>
    ///
    /// 受け入れ: <b>盗塁の前後で構えの接地誤差が変わらない</b>（どちらも |誤差| ≦ 0.02m）。
    /// </summary>
    public static class CatcherFloatProbe
    {
        private const string Dir = "out/catcher-float-607";
        private const string ShotDir = "screenshots/catcher-float-607";
        private const int W = 960;
        private const int H = 540;

        /// <summary>受け入れの閾値[m]。捕球5方向クリップの足の高さの実測ばらつき（0.075〜0.086＝約0.011）より
        /// わずかに広い＝クリップ差ぶんは許し、足一つ分（0.1m級）の浮きは落とす。</summary>
        private const float Tolerance = 0.02f;

        [UnityEditor.MenuItem("KokoSim/Duel/#607 盗塁送球の前後で捕手の接地を測る")]
        public static void Run()
        {
            Directory.CreateDirectory(Dir);
            Directory.CreateDirectory(ShotDir);

            var play = BuildStealPlay();
            float tRelease = FirstThrowReleaseOf(play);
            if (tRelease < 0f) { Debug.LogError("[#607Probe] 盗塁プレーに送球区間が無い"); return; }

            var rig = new DuelStageRig();
            var log = new StringBuilder("局面\t接地誤差[m]\tBasePos.y[m]\n");
            try
            {
                if (!rig.Available) { Debug.LogError("[#607Probe] DuelStageRig が組めない（素体/クリップ未導入）"); return; }
                rig.EnsureSize(W, H);
                float step = 1f / DuelStageRig.PoseFps;

                // ① 盗塁の前の構え（何もしていない状態の基準）
                for (int i = 0; i < 24; i++) rig.Tick(step);
                float beforeClear = rig.CatcherFootClearanceForCapture();
                float beforeBaseY = rig.CatcherBaseYForCapture;
                log.Append("盗塁前\t").Append(beforeClear.ToString("F4")).Append('\t')
                   .Append(beforeBaseY.ToString("F4")).Append('\n');
                Shoot(rig, "1-before-steal");

                // ② 対決ビュー据え置きのまま捕手へ送球クリップを直乗せ（issue #512 の経路）
                if (!rig.TryBeginDuelThrow(play, out float fireAt))
                {
                    Debug.LogError("[#607Probe] TryBeginDuelThrow が false（投げ手が捕手/投手でない等）");
                    return;
                }
                for (float t = fireAt; t <= tRelease; t += step) { rig.Tick(step); rig.StepDuelThrow(t); }
                float throwClear = rig.CatcherFootClearanceForCapture();
                float throwBaseY = rig.CatcherBaseYForCapture;
                log.Append("送球中\t").Append(throwClear.ToString("F4")).Append('\t')
                   .Append(throwBaseY.ToString("F4")).Append('\n');
                Shoot(rig, "2-during-throw");

                // ③ リリースで守備ビューへ引き渡し、プレーを終わりまで流して畳む
                rig.EndDuelThrow();
                rig.BeginFieldPlay(play, tRelease);
                for (float t = tRelease; t <= (float)play.Dur; t += step) { rig.Tick(step); rig.SetFieldTime(t); }
                rig.EndFieldPlay();

                // ④ 対決ビューへ戻したあとの構え（ここが「以降ずっと」の観測点）
                rig.ResetForNewPlateAppearance();
                for (int i = 0; i < 24; i++) rig.Tick(step);
                float afterClear = rig.CatcherFootClearanceForCapture();
                float afterBaseY = rig.CatcherBaseYForCapture;
                log.Append("盗塁後\t").Append(afterClear.ToString("F4")).Append('\t')
                   .Append(afterBaseY.ToString("F4")).Append('\n');
                Shoot(rig, "3-after-steal");

                // ⑤ 盗塁の往復<b>のあと</b>で捕球が壊れていないこと（#440 低め・#466 高め・横外れの回帰）。
                // 立ち位置の基準が汚れる不具合なので、効くとしたら「往復のあとの捕球」＝ここで測る。
                float catchMaxAbs = 0f;
                var cases = new[]
                {
                    new KeyValuePair<string, DuelPitchFlight>("低め", new DuelPitchFlight(0f, 0.15f, 0f, 0.55f, 0.45f, 0.4f, 0f)),
                    new KeyValuePair<string, DuelPitchFlight>("高め", new DuelPitchFlight(0f, 0.95f, 0f, 0.55f, 0.45f, 0.4f, 0f)),
                    new KeyValuePair<string, DuelPitchFlight>("横", new DuelPitchFlight(0.45f, 0.55f, 0f, 0.55f, 0.45f, 0.4f, 0f)),
                };
                foreach (var kv in cases)
                {
                    rig.ResetForNewPlateAppearance();
                    for (int i = 0; i < 12; i++) rig.Tick(step);
                    rig.OnPitchWindup();
                    for (int i = 0; i < 4; i++) rig.Tick(step);
                    rig.OnPitchReleased(0f, DuelPitchOutcome.Take, kv.Value);
                    float worst = 0f;
                    for (float t = 0f; t < kv.Value.FlightSeconds + rig.CatcherLingerSecondsForCapture + 0.6f; t += step)
                    {
                        rig.Tick(step);
                        float c = rig.CatcherFootClearanceForCapture();
                        if (Mathf.Abs(c) > Mathf.Abs(worst)) worst = c;
                    }
                    if (Mathf.Abs(worst) > Mathf.Abs(catchMaxAbs)) catchMaxAbs = worst;
                    log.Append("捕球・").Append(kv.Key).Append('\t').Append(worst.ToString("F4"))
                       .Append('\t').Append(rig.CatcherBaseYForCapture.ToString("F4")).Append('\n');
                }

                File.WriteAllText(Path.Combine(Dir, "clearance.tsv"), log.ToString());
                bool ok = Mathf.Abs(beforeClear) <= Tolerance && Mathf.Abs(afterClear) <= Tolerance
                          && Mathf.Abs(catchMaxAbs) <= Tolerance;
                Debug.Log($"[#607Probe] {(ok ? "OK" : "★NG")} "
                          + $"盗塁前 誤差={beforeClear:F4}m BasePos.y={beforeBaseY:F4} ／ "
                          + $"送球中 誤差={throwClear:F4}m BasePos.y={throwBaseY:F4} ／ "
                          + $"盗塁後 誤差={afterClear:F4}m BasePos.y={afterBaseY:F4} ／ "
                          + $"往復後の捕球（低め/高め/横）の最悪={catchMaxAbs:F4}m"
                          + $"（前後の差={afterClear - beforeClear:F4}m 許容={Tolerance:F3}m）\n"
                          + Dir + " / " + ShotDir);
            }
            finally
            {
                rig.Dispose();
            }
        }

        /// <summary>二盗（セーフ）の再生データ。捕手→二塁の送球区間を1本持つ。</summary>
        private static PlaybackPlay BuildStealPlay()
        {
            var runner = new Player { Speed = 80, Steal = 75 };
            var catcher = new Player { Position = FieldPosition.Catcher, ArmStrength = 60 };
            return PlayTimelineAdapter.ToPlaybackPlay(
                TimelineBuilder.BuildSteal(runner, catcher, StealResult.Safe,
                    new BaserunningCoefficients(), new FieldGeometry()));
        }

        /// <summary>最初の送球区間のリリース時刻（区間の T0）。無ければ -1。</summary>
        private static float FirstThrowReleaseOf(PlaybackPlay play)
        {
            if (play?.Ball == null) return -1f;
            foreach (var seg in play.Ball)
            {
                var eb = seg as EndpointBallSegment;
                bool isThrow = eb != null ? eb.Kind == BallSegmentKind.Throw : seg is ThrowSegment;
                if (isThrow) return (float)seg.T0;
            }
            return -1f;
        }

        /// <summary>
        /// 足元の寄り1枚。接地は<b>地面と足の隙間</b>で読むので、目線を下げて水平に構える
        /// （斜めからだと前後のずれと浮きが分離できない＝design-18 §3.3 の「寄りの絵は対象の軸から」）。
        ///
        /// ⚠ 真後ろ（−z）からは撮らない。捕手の後ろには球審が立っていて、黒いズボンで
        /// 足元がまるごと隠れる（初回の絵で実害）。<b>横（+x側。打者は−x側）</b>から撮る＝
        /// 隙間が地面の線に対して素直に読める。
        /// </summary>
        private static void Shoot(DuelStageRig rig, string file)
        {
            var cal = DuelStageRig.CalibrationOverride ?? DuelCalibration.Default;
            var at = new Vector3(cal.CatcherX, 0.30f, cal.CatcherZ);
            rig.FramePreview(at + new Vector3(3.0f, 0.05f, 0f), at, 1.2f);
            rig.RenderNow();
            var rt = rig.Target;
            if (rt == null) return;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(Path.Combine(ShotDir, file + ".png"), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            rig.ClearPreviewFrame();
        }
    }
}
#endif
