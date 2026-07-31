#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Text;
using KokoSim.Engine.Match.Timeline.Playback;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】3D一本化（打球処理の3D化 F9）の受け入れ。
    /// 実試合をそのまま流し、打球のたびに「3Dへ切り替わったか・判定が出たか・カメラが据え置かれたか」を
    /// 1コマずつ実測する。使い捨て。
    ///
    /// <b>なぜ実試合を流すか</b>: F9 で壊れ得るのは engine の出力ではなく<b>画面の配線</b>
    /// （器の置き場所・ビューの切替・打ち切りの後始末）で、モックのプレーを直接 rig へ流す
    /// <c>DuelFieldPlayShot</c> ではコントローラを一切通らない＝何も確かめられない。
    ///
    /// <b>時計の扱い</b>: <c>Time.captureDeltaTime</c> で実時間から切り離す＝スクショの待ちフレームで
    /// 場面が進まない。さらに撮影の間は <c>FreezeLiveForCapture</c> で進行そのものを凍らせる
    /// （固定dtでも「待った回数ぶん」は進むため）。
    ///
    /// 出力:
    ///   out/duel-stage/f9/plays.tsv   打球1本＝1行（カット時刻・判定の予定と実際・据え置き）
    ///   out/duel-stage/f9/frames.tsv  最初の数本の1コマごと（枠の高さ＝据え置きの裏取り）
    ///   screenshots/f9/*.png          節目の絵＋俯瞰トグルのA/B
    /// </summary>
    public sealed class MatchLiveFieldPlayShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string ShotDir = "screenshots/f9";
        private const string OutDir = "out/duel-stage/f9";
        private const string SpawnFlag = "KokoSim.MatchLiveFieldPlayShooter.Spawn";

        /// <summary>絵を撮る打球の本数（1本で4〜5枚出るので増やしすぎない）。</summary>
        private const int ShotPlays = 3;
        /// <summary>判定の出方を数える打球の本数。1イニングは概ね3〜6打席。</summary>
        private const int MaxPlays = 14;

        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~F9Auto").AddComponent<MatchLiveFieldPlayShooter>();
        }

        private MatchLiveController _ctrl;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Time.captureDeltaTime = 1f / 30f;
            Directory.CreateDirectory(ShotDir);
            Directory.CreateDirectory(OutDir);

            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            _ctrl = FindController();
            if (_ctrl == null) { Debug.LogError("[F9] MatchLive/Controller が見つからない"); yield break; }
            yield return null;

            var rig = _ctrl.DuelRigForCapture;
            Debug.Log("[F9] rig=" + (rig == null ? "null" : rig.Available ? "使える" : "組めない"));

            var plays = new StringBuilder(
                "#\t結果\t3Dへ\t決着ResAt\t全景カット\t終わり\t尾[s]\t判定予定\t判定表示\t文言"
                + "\t枠@カット前\t枠@カット後\t枠@最終\t据え位置\n");
            var frames = new StringBuilder("play\tt\t球x\t球y\t球h\t枠[m]\t注視y\t人px\t球px\tbu\tbv\t据えy\t判定\n");

            int playNo = 0;
            bool inPlay = false;
            double cutT = 0, resAt = 0, holdT = 0, endT = 0;
            double? expect = null;
            bool sawJudge = false;
            string judgeText = "", result = "", stationPlan = "";
            float frameBefore = -1f, frameAfter = -1f, frameLast = -1f;
            bool shotCut = false, shotRes = false, shotAfter = false;
            int missing = 0;

            int guard = 0;
            while (playNo < MaxPlays && !_ctrl.IsGameOver && guard++ < 40000)
            {
                yield return null;
                bool active = _ctrl.FieldPlayActiveForCapture;

                // ── プレーの頭 ─────────────────────────────────────────────
                if (active && !inPlay)
                {
                    inPlay = true;
                    playNo++;
                    var play = _ctrl.CurrentPa?.Play;
                    cutT = _ctrl.PlayClockForCapture;
                    resAt = play?.ResAt ?? 0;
                    holdT = resAt + CameraPlanBuilder.ReturnToWideDelaySeconds;
                    expect = play == null ? null : PlaybackEvaluator.LastJudgementTime(play);
                    result = play?.Result ?? "-";
                    stationPlan = rig != null ? rig.FieldStationPlanForCapture() : "-";
                    sawJudge = false; judgeText = "";
                    frameBefore = frameAfter = frameLast = -1f;
                    endT = cutT;
                    shotCut = shotRes = shotAfter = false;
                }

                // ── プレー中 ───────────────────────────────────────────────
                // ⚠ active も見る。コントローラの Update がこのフレームで打ち切っていると
                // rig は既に畳まれており、枠が -1（プランなし）で記録される＝**最終フレームの値が
                // 毎回 -1 になる**（初回の実測で「枠@最終 が全部 -1」として出た）。
                if (inPlay && active)
                {
                    double t = _ctrl.PlayClockForCapture;
                    if (_ctrl.JudgementVisibleForCapture)
                    {
                        sawJudge = true;
                        if (judgeText.Length == 0) judgeText = _ctrl.JudgementTextForCapture;
                    }
                    if (rig != null)
                    {
                        var d = rig.FieldFrameDiagForCapture((float)t);
                        // 列: name, maxZoom, margin, t, x, y, h, frameH, aimY, aimPx, ballPx, bu, bv, stationY
                        var c = d.Split('\t');
                        if (c.Length >= 14)
                        {
                            float fh;
                            if (float.TryParse(c[7], System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out fh))
                            {
                                frameLast = fh;
                                endT = t;
                                if (t < holdT) frameBefore = fh;
                                else if (frameAfter < 0f) frameAfter = fh;
                            }
                            if (playNo <= ShotPlays)
                                frames.Append(playNo).Append('\t').Append(t.ToString("F2")).Append('\t')
                                      .Append(c[4]).Append('\t').Append(c[5]).Append('\t').Append(c[6]).Append('\t')
                                      .Append(c[7]).Append('\t').Append(c[8]).Append('\t').Append(c[9]).Append('\t')
                                      .Append(c[10]).Append('\t').Append(c[11]).Append('\t').Append(c[12]).Append('\t')
                                      .Append(c[13]).Append('\t')
                                      .Append(_ctrl.JudgementVisibleForCapture ? _ctrl.JudgementTextForCapture : "-")
                                      .Append('\n');
                        }
                    }

                    if (playNo <= ShotPlays)
                    {
                        if (!shotCut) { shotCut = true; yield return Shoot(string.Format("p{0}-a-cut", playNo)); }
                        else if (!shotRes && t >= resAt)
                        {
                            shotRes = true;
                            yield return Shoot(string.Format("p{0}-b-res-3d", playNo));
                            yield return ShootToggled(string.Format("p{0}-c-res-2d", playNo));
                        }
                        else if (!shotAfter && t >= holdT + 0.8)
                        {
                            shotAfter = true;
                            yield return Shoot(string.Format("p{0}-d-hold", playNo));
                        }
                    }
                }

                // ── プレーの終わり ─────────────────────────────────────────
                if (!active && inPlay)
                {
                    inPlay = false;
                    bool expected = expect.HasValue;
                    if (expected && !sawJudge) missing++;
                    plays.Append(playNo).Append('\t').Append(result).Append('\t')
                         .Append(cutT.ToString("F2")).Append('\t').Append(resAt.ToString("F2")).Append('\t')
                         .Append(holdT.ToString("F2")).Append('\t').Append(endT.ToString("F2")).Append('\t')
                         .Append(System.Math.Max(0, endT - holdT).ToString("F2")).Append('\t')
                         .Append(expected ? expect.Value.ToString("F2") : "なし").Append('\t')
                         .Append(sawJudge ? "出た" : "出ない").Append('\t')
                         .Append(judgeText.Length == 0 ? "-" : judgeText).Append('\t')
                         .Append(frameBefore.ToString("F1")).Append('\t')
                         .Append(frameAfter.ToString("F1")).Append('\t')
                         .Append(frameLast.ToString("F1")).Append('\t')
                         .Append(stationPlan).Append('\n');
                }
            }

            File.WriteAllText(Path.Combine(OutDir, "plays.tsv"), plays.ToString());
            File.WriteAllText(Path.Combine(OutDir, "frames.tsv"), frames.ToString());
            Debug.Log("[F9] DONE 打球=" + playNo + "本 判定の取りこぼし=" + missing
                + "件 gameOver=" + _ctrl.IsGameOver + "\n" + plays);
            Time.captureDeltaTime = 0f;
        }

        /// <summary>俯瞰トグルを叩いた絵（＝従来の2Dへ戻るか）。撮り終えたら必ず3Dへ戻す。</summary>
        private IEnumerator ShootToggled(string file)
        {
            _ctrl.ToggleDuelViewForCapture();
            yield return Shoot(file);
            _ctrl.ToggleDuelViewForCapture();
        }

        /// <summary>
        /// 画面まるごと1枚。
        /// ⚠ 撮影の間は進行を凍らせる（<c>Time.captureDeltaTime</c> は実時間からは切り離すが、
        /// 待ったフレームの数ぶんは確実に進むので、凍らせないと節目の絵が節目からずれる）。
        /// </summary>
        private IEnumerator Shoot(string file)
        {
            _ctrl.FreezeLiveForCapture(true);
            yield return null;
            yield return new WaitForEndOfFrame();
            // シューター経路だけ選手が真っ黒に焼ける回がある（実機は正常）。明示的に描き直す。
            _ctrl.DuelRigForCapture?.RenderNow();
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
