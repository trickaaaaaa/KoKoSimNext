#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Text;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】内野手の手さばき（issue #765）の見比べ。使い捨て。ThrowArcShooter と同じ作法。
    ///
    /// <para>実試合を進めて内野ゴロ→一塁の送球を拾い、捕ってから離すまで（打球区間の終わり→送球区間の始まり）を記録する。
    /// 短い保持（名手）と長い保持（もたつき）をそれぞれ最初の1本ずつ、捕球直後・保持の中ほど・リリース直前の3コマで撮る。
    /// 短い保持は送球モーションを速回しして収める（DuelStageRig の InfieldThrowMaxRate）ので、捕球直後のコマで
    /// 投げる動作の頭（捕った姿勢）が見え、途中へ飛んでいないことを確かめる。</para>
    ///
    /// 出力: screenshots/hand-speed-765/*.png ／ out/hand-speed-765/holds.tsv
    /// </summary>
    public sealed class HandSpeedShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string ShotDir = "screenshots/hand-speed-765";
        private const string OutDir = "out/hand-speed-765";
        private const string SpawnFlag = "KokoSim.HandSpeedShooter.Spawn";

        private const int MaxGames = 30;
        private const int MaxPlatesPerGame = 200;

        /// <summary>名手とみなす保持[s]（守備90の平均≈0.81s）。</summary>
        private const double FastHold = 0.85;
        /// <summary>もたつきとみなす保持[s]（握り直しの芝居 1.4s の手前＝芝居なしで遅いだけの保持）。</summary>
        private const double SlowHoldMin = 1.25, SlowHoldMax = 1.38;
        /// <summary>見比べの条件を揃えるため、送球距離はこの範囲（三遊間〜二遊間→一塁）に限る[m]。</summary>
        private const double MinDistM = 25.0, MaxDistM = 42.0;

        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~HandSpeedAuto").AddComponent<HandSpeedShooter>();
        }

        private MatchLiveController _ctrl;
        private GameObject _liveGo;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Time.captureDeltaTime = 1f / 30f;
            Directory.CreateDirectory(ShotDir);
            Directory.CreateDirectory(OutDir);

            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            _ctrl = FindController();
            if (_ctrl == null) { Debug.LogError("[#765Shot] MatchLive/Controller が見つからない"); yield break; }
            _liveGo = _ctrl.gameObject;
            yield return null;

            var log = new StringBuilder("試合\t打席\t投げ手\t距離[m]\t保持[s]\t撮った\n");
            var shotFast = false;
            var shotSlow = false;
            var throws = 0;
            var holdSum = 0.0;

            for (var game = 1; game <= MaxGames && !(shotFast && shotSlow); game++)
            {
                if (game > 1) yield return RestartWithSeed((ulong)(76500 + game * 4231));

                for (var plate = 1; plate <= MaxPlatesPerGame && !_ctrl.IsGameOver; plate++)
                {
                    if (!_ctrl.AdvanceForCapture()) break;

                    // ⚠ 解決した直後の同じフレームで見る（次の Advance で再生ごと捨てられる）。
                    var play = _ctrl.CurrentPa?.Play;
                    if (!FindGrounderThrow(play, out var ground, out var th)) continue;

                    var hold = th.T0 - ground.T1;
                    var a0 = th.At(0);
                    var b0 = th.At(th.T1 - th.T0);
                    var dist = System.Math.Sqrt((b0.X - a0.X) * (b0.X - a0.X) + (b0.Y - a0.Y) * (b0.Y - a0.Y));
                    throws++;
                    holdSum += hold;

                    string tag = null;
                    if (dist >= MinDistM && dist <= MaxDistM && !play.BobbleError)
                    {
                        if (!shotFast && hold <= FastHold) tag = "fast";
                        else if (!shotSlow && hold >= SlowHoldMin && hold <= SlowHoldMax) tag = "slow";
                    }

                    log.Append(game).Append('\t').Append(plate).Append('\t').Append(th.FromRole).Append('\t')
                       .Append(dist.ToString("F1")).Append('\t').Append(hold.ToString("F2")).Append('\t')
                       .Append(tag ?? "—").Append('\n');

                    if (tag == null) continue;
                    if (tag == "fast") shotFast = true; else shotSlow = true;
                    yield return ShootHold(tag, ground.T1, th.T0, dist, game, plate);
                }
            }

            File.WriteAllText(Path.Combine(OutDir, "holds.tsv"), log.ToString());
            Debug.Log($"[#765Shot] 内野ゴロ→一塁 {throws} 本 平均保持 {(throws > 0 ? holdSum / throws : 0):F2}s / 名手 {(shotFast ? "撮影" : "なし")}" +
                      $" / もたつき {(shotSlow ? "撮影" : "なし")} → {ShotDir}");
            Time.captureDeltaTime = 0f;
        }

        private IEnumerator RestartWithSeed(ulong seed)
        {
            MatchLiveController.GameSeedOverrideForCapture = seed;
            _liveGo.SetActive(false);
            yield return null;
            _liveGo.SetActive(true);
            yield return null; yield return null;
        }

        private static bool FindGrounderThrow(PlaybackPlay play, out EndpointBallSegment ground, out EndpointBallSegment th)
        {
            ground = null; th = null;
            if (play?.Ball == null || play.Fumbled) return false;
            foreach (var s in play.Ball)
            {
                var e = s as EndpointBallSegment;
                if (e == null) continue;
                if (e.Kind == BallSegmentKind.Flight) return false;
                if (ground == null)
                {
                    if (e.Kind == BallSegmentKind.Roll || e.Kind == BallSegmentKind.Bounce) ground = e;
                    continue;
                }
                if (e.Kind != BallSegmentKind.Throw || e.ToBase != "first" || e.FromRole == null) return false;
                var r = e.FromRole.Value;
                if (r != FieldPosition.SecondBase && r != FieldPosition.ThirdBase && r != FieldPosition.Shortstop) return false;
                th = e;
                return true;
            }
            return false;
        }

        private IEnumerator ShootHold(string tag, double catchT, double releaseT, double dist, int game, int plate)
        {
            var waited = 0;
            while (!_ctrl.FieldPlayActiveForCapture && waited++ < 1800) yield return null;
            if (!_ctrl.FieldPlayActiveForCapture)
            {
                Debug.LogWarning($"[#765Shot] 守備再生が始まらないまま打ち切り（{game}試合目 {plate}打席目）");
                yield break;
            }
            var hold = releaseT - catchT;
            yield return WaitClock(catchT + 0.04);
            yield return ShootOne($"{tag}-1-catch", false);
            yield return WaitClock(catchT + hold * 0.5);
            yield return ShootOne($"{tag}-2-mid", false);
            yield return WaitClock(releaseT - 0.04);
            yield return ShootOne($"{tag}-3-release", false);
            Debug.Log($"[#765Shot] {tag}: 保持 {hold:F2}s 送球 {dist:F1}m（{game}試合目 {plate}打席目）");
        }

        private IEnumerator WaitClock(double t)
        {
            var guard = 0;
            while (_ctrl.FieldPlayActiveForCapture && _ctrl.PlayClockForCapture < t && guard++ < 900) yield return null;
        }

        private IEnumerator ShootOne(string file, bool overhead)
        {
            _ctrl.FreezeLiveForCapture(true);
            yield return null;
            yield return new WaitForEndOfFrame();
            if (!overhead) _ctrl.DuelRigForCapture?.RenderNow();
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
