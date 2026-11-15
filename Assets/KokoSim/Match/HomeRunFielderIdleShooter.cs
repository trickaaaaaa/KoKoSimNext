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
    /// 【開発用・エディタ限定】本塁打の一周の間、野手が思い思いの待機へばらけることの実機確認（issue #978）。
    ///
    /// <para>右翼・左翼・中堅へのそれぞれ1本について ①球が死んで全員が立ち終えた頃 ②一周の途中
    /// ③本塁到達 の3コマを撮る。どの野手がどの収録・どの位相・どの向きでいるかは静止画では読み切れないので
    /// <see cref="DuelStageRig.FielderIdleDiagForCapture"/> の表を TSV に添える。</para>
    ///
    /// <para>本塁打は強制発動（ForcedOutcome.HomeRun）では作れない（投球・打球の計算を飛ばすので打球の軌道が
    /// 出ず、守備ビューを通らない）。固定シード列の試合を打席単位で早送りし、方向ごとに最初の1本を撮る
    /// ＝何度回しても同じ本塁打が撮れる。</para>
    ///
    /// 出力: screenshots/hr-fielder-idle/&lt;方向&gt;-&lt;コマ&gt;.png ／ out/hr-fielder-idle/diag.tsv
    /// </summary>
    public sealed class HomeRunFielderIdleShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string ShotDir = "screenshots/hr-fielder-idle";
        private const string OutDir = "out/hr-fielder-idle";
        private const string SpawnFlag = "KokoSim.HomeRunFielderIdleShooter.Spawn";

        private const int MaxGames = 40;
        private const int MaxPlatesPerGame = 200;

        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~HomeRunFielderIdleAuto").AddComponent<HomeRunFielderIdleShooter>();
        }

        /// <summary>終わったら true（MCP からの待ち合わせ用）。</summary>
        public static bool Done;

        private MatchLiveController _ctrl;
        private GameObject _liveGo;
        private StringBuilder _log;

        private IEnumerator Start()
        {
            Done = false;
            Application.runInBackground = true;
            Time.captureDeltaTime = 1f / 30f;
            Directory.CreateDirectory(ShotDir);
            Directory.CreateDirectory(OutDir);
            Directory.CreateDirectory(Path.Combine(ShotDir, "close"));

            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            _ctrl = FindController();
            if (_ctrl == null) { Debug.LogError("[本塁打の待機] MatchLive/Controller が見つからない"); Done = true; yield break; }
            _liveGo = _ctrl.gameObject;
            yield return null;

            _log = new StringBuilder();
            var want = new HashSet<string> { "right", "left", "center" };

            for (var game = 1; game <= MaxGames && want.Count > 0; game++)
            {
                if (game > 1) yield return RestartWithSeed((ulong)(78000 + game * 4099));

                for (var plate = 1; plate <= MaxPlatesPerGame && !_ctrl.IsGameOver && want.Count > 0; plate++)
                {
                    if (!_ctrl.AdvanceForCapture()) break;
                    var play = _ctrl.CurrentPa?.Play;
                    var dir = HomeRunDirection(play);
                    var touch = play == null ? null : PlaybackEvaluator.BatterHomeTouchTime(play);
                    if (dir == null || !want.Contains(dir) || touch is not { } th)
                    {
                        _ctrl.FreezeCurrentAtResult();
                        yield return null;
                        continue;
                    }
                    want.Remove(dir);

                    var land = LandingTime(play) ?? 0;
                    yield return PlayUntil(land);
                    var dead = _ctrl.DuelRigForCapture?.HomeRunDeadTimeForCapture ?? (float)land;
                    _log.Append("## ").Append(dir).Append("\tgame=").Append(game).Append("\tplate=").Append(plate)
                        .Append("\t").Append(play.Result).Append(string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "\t着地={0:F2}\t死球={1:F2}\t本塁={2:F2}\n", land, dead, th));
                    Debug.Log($"[本塁打の待機] {dir}: {play.Result} 着地{land:F2} 死{dead:F2} 本塁{th:F2}");

                    yield return PlayUntil(dead + 0.3);
                    yield return Shoot(dir + "-0-just-dead", "0 球が死んだ直後（切り替え前）");
                    yield return PlayUntil(dead + 1.7);
                    yield return Shoot(dir + "-1-standing", "1 立ち終えた頃");
                    var mid = System.Math.Max(dead + 2.0, land + (th - land) * 0.55);
                    if (mid < th - 0.3)
                    {
                        yield return PlayUntil(mid);
                        yield return Shoot(dir + "-2-rounding", "2 一周の途中");
                    }
                    yield return PlayUntil(th + 0.1);
                    yield return Shoot(dir + "-3-home", "3 本塁到達");
                    _ctrl.FreezeCurrentAtResult();
                    yield return null;
                }
            }

            File.WriteAllText(Path.Combine(OutDir, "diag.tsv"), _log.ToString());
            Debug.Log($"[本塁打の待機] 撮影終わり（撮れなかった方向: {string.Join(",", want)}）→ {ShotDir}");
            Time.captureDeltaTime = 0f;
            Done = true;
        }

        /// <summary>本塁打なら打球の止まる先の方向（左・中・右。境は ±15°）。本塁打でなければ null。</summary>
        private static string HomeRunDirection(PlaybackPlay play)
        {
            if (play?.Result == null || !play.Result.Contains("ホームラン") || play.Ball == null || play.Ball.Count == 0)
                return null;
            double end = 0;
            foreach (var s in play.Ball) if (s.T1 > end) end = s.T1;
            var b = PlaybackEvaluator.BallAt(play, end);
            if (!b.HasValue) return null;
            // engine は +x＝一塁側（右翼側）
            var deg = Mathf.Atan2((float)b.Value.X, (float)b.Value.Y) * Mathf.Rad2Deg;
            return deg > 15f ? "right" : deg < -15f ? "left" : "center";
        }

        private static double? LandingTime(PlaybackPlay play)
        {
            foreach (var j in play.Judgements)
                if (j.Kind == KokoSim.Engine.Match.Timeline.JudgementKind.HomeRun) return j.T;
            return null;
        }

        private IEnumerator PlayUntil(double t)
        {
            var waited = 0;
            while (waited++ < 3000 && !(_ctrl.StageNameForCapture == "Play" && _ctrl.PlayClockForCapture > 0))
                yield return null;
            var guard = 0;
            while (guard++ < 3000 && _ctrl.PlayClockForCapture < t && _ctrl.StageNameForCapture == "Play")
                yield return null;
        }

        /// <summary>試合のカメラのまま1枚＋固定の俯瞰（外野まで全員）＋内野寄りの2枚。</summary>
        private IEnumerator Shoot(string file, string frame)
        {
            yield return ShootOne(file, frame);
            // 野手を1人ずつ寄りで撮る（全景では小さすぎて姿勢も向きも読めない）。カメラは本塁側から＝
            // 本塁／打者走者を向いていれば正面、着弾点を向いていれば背中が映る。
            // 頭上の名札とテロップは野手に被るので、この間だけ隠す
            SetOverlayHidden(true);
            var rig = _ctrl.DuelRigForCapture;
            foreach (var pos in Positions)
            {
                var p = rig?.FielderStagePosForCapture(pos);
                if (!p.HasValue) continue;
                var flat = new Vector3(p.Value.x, 0f, p.Value.z);
                var away = flat.sqrMagnitude < 1f ? Vector3.forward : flat.normalized;
                DuelStageRig.FieldCameraOverrideForCapture =
                    (flat - away * 8f + Vector3.up * 2.2f, flat + Vector3.up * 0.9f, 3.4f);
                yield return null;
                yield return ShootOne("close/" + file + "-" + pos, frame + "（" + pos + "）", log: false);
            }
            DuelStageRig.FieldCameraOverrideForCapture = null;
            SetOverlayHidden(false);
        }

        private static readonly KokoSim.Engine.Match.Field.FieldPosition[] Positions =
        {
            KokoSim.Engine.Match.Field.FieldPosition.Pitcher, KokoSim.Engine.Match.Field.FieldPosition.Catcher,
            KokoSim.Engine.Match.Field.FieldPosition.FirstBase, KokoSim.Engine.Match.Field.FieldPosition.SecondBase,
            KokoSim.Engine.Match.Field.FieldPosition.ThirdBase, KokoSim.Engine.Match.Field.FieldPosition.Shortstop,
            KokoSim.Engine.Match.Field.FieldPosition.LeftField, KokoSim.Engine.Match.Field.FieldPosition.CenterField,
            KokoSim.Engine.Match.Field.FieldPosition.RightField,
        };

        private void SetOverlayHidden(bool hidden)
        {
            var root = _ctrl.GetComponent<UIDocument>().rootVisualElement;
            var vis = hidden ? new StyleEnum<Visibility>(Visibility.Hidden) : new StyleEnum<Visibility>(StyleKeyword.Null);
            var judge = root.Q<VisualElement>("judgement-call");
            if (judge != null) judge.style.visibility = vis;
            root.Query<DuelViewElement>().ForEach(img =>
            {
                foreach (var c in img.Children()) c.style.visibility = vis;
            });
        }

        private IEnumerator ShootOne(string file, string frame, bool log = true)
        {
            _ctrl.FreezeLiveForCapture(true);
            yield return null;
            yield return new WaitForEndOfFrame();
            _ctrl.DuelRigForCapture?.RenderNow();
            yield return null;

            if (log) _log.Append("# ").Append(frame).Append("\tt=")
                .Append(_ctrl.PlayClockForCapture.ToString("F2")).Append('\n')
                .Append(_ctrl.DuelRigForCapture?.FielderIdleDiagForCapture() ?? "");

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

        private IEnumerator RestartWithSeed(ulong seed)
        {
            MatchLiveController.GameSeedOverrideForCapture = seed;
            _liveGo.SetActive(false);
            yield return null;
            _liveGo.SetActive(true);
            yield return null; yield return null;
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
