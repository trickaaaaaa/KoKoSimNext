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
    /// 【開発用・エディタ限定】前進捕球（チャージ, issue #630）の受け入れ実測。使い捨て。
    ///
    /// <para>実試合を進めて内野ゴロを拾い、処理野手の<b>守備位置と捕球点</b>の本塁距離を実測する（前進[m]）。
    /// いちばん前へ出た当たりで守備ビューの絵を撮る＝<b>「待ち姿が消えた」は絵でしか確かめられない</b>
    /// （判定側は engine のテストと Balance CLI で見ている）。</para>
    ///
    /// 出力: screenshots/charge-630/*.png ／ out/charge-630/plays.tsv
    /// </summary>
    public sealed class ChargePlayShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string ShotDir = "screenshots/charge-630";
        private const string OutDir = "out/charge-630";
        private const string SpawnFlag = "KokoSim.ChargePlayShooter.Spawn";

        private const int MaxGames = 8;
        private const int MaxPlatesPerGame = 200;

        /// <summary>撮りたい前進量[m]（これを超えた最初の内野ゴロで撮る）。</summary>
        private const double WantAdvanceM = 2.5;

        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~ChargePlayAuto").AddComponent<ChargePlayShooter>();
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
            if (_ctrl == null) { Debug.LogError("[#630Shot] MatchLive/Controller が見つからない"); yield break; }
            _liveGo = _ctrl.gameObject;
            yield return null;

            var log = new StringBuilder("試合\t打席\t結果\t処理\t守備位置[m]\t捕球点[m]\t前進[m]\t捕球[s]\t撮った\n");
            var shot = false;
            var grounders = 0;
            var advanceSum = 0.0;
            var advanceMax = 0.0;

            for (var game = 1; game <= MaxGames && !shot; game++)
            {
                if (game > 1) yield return RestartWithSeed((ulong)(60000 + game * 4231));

                for (var plate = 1; plate <= MaxPlatesPerGame && !_ctrl.IsGameOver; plate++)
                {
                    if (!_ctrl.AdvanceForCapture()) break;

                    // ⚠ 解決した直後の同じフレームで見る（次の Advance で再生ごと捨てられる）。
                    var play = _ctrl.CurrentPa?.Play;
                    var m = MeasureInfieldGrounder(play);
                    if (m == null) continue;

                    grounders++;
                    advanceSum += m.Advance;
                    if (m.Advance > advanceMax) advanceMax = m.Advance;
                    var want = !shot && m.Advance >= WantAdvanceM;

                    log.Append(game).Append('\t').Append(plate).Append('\t').Append(play.Result).Append('\t')
                       .Append(m.Role).Append('\t').Append(m.StartDist.ToString("F1")).Append('\t')
                       .Append(m.CatchDist.ToString("F1")).Append('\t').Append(m.Advance.ToString("F1")).Append('\t')
                       .Append(m.CatchAt.ToString("F2")).Append('\t').Append(want ? "★" : "—").Append('\n');

                    if (!want) continue;

                    shot = true;
                    yield return ShootPlay(m, play, game, plate);
                    break;
                }
            }

            File.WriteAllText(Path.Combine(OutDir, "plays.tsv"), log.ToString());
            Debug.Log($"[#630Shot] 内野ゴロ {grounders} 件 / 前進 平均 {(grounders > 0 ? advanceSum / grounders : 0):F2}m" +
                      $" 最大 {advanceMax:F2}m → {ShotDir}");
            if (!shot) Debug.LogWarning($"[#630Shot] 前進 {WantAdvanceM}m 超の内野ゴロが出なかった（引き直し）");
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

        // --- 再生データの読み取り（構築はしない＝required メンバーに触れないので C# 9 で通る） ---

        private sealed class Measure
        {
            public FieldPosition Role;
            public double StartDist, CatchDist, Advance, CatchAt, ThrowArriveAt;
        }

        /// <summary>
        /// 内野ゴロなら「守備位置→捕球点」を実測する。内野ゴロでなければ null。
        /// 捕球点は一塁送球の投げ始め位置（＝ゴロ区間の終端）から採る
        /// （<see cref="EndpointBallSegment"/> は端点を公開しないので区間先頭の評価位置を使う）。
        /// </summary>
        private static Measure MeasureInfieldGrounder(PlaybackPlay play)
        {
            if (play?.Starts == null) return null;

            EndpointBallSegment toFirst = null;
            foreach (var s in play.Ball)
            {
                var e = s as EndpointBallSegment;
                if (e == null || e.Kind != BallSegmentKind.Throw || e.ToBase != "first") continue;
                if (e.FromRole == null || !IsInfield(e.FromRole.Value)) continue;
                toFirst = e;
                break;
            }
            if (toFirst?.FromRole == null) return null;

            PlaybackVec start;
            if (!play.Starts.TryGetValue(toFirst.FromRole.Value, out start)) return null;

            // 捕球の瞬間＝一塁送球より前に終わる区間のうち最後のもの（ゴロが野手へ届いた時刻）。
            // 見つからなければ投げ始めで代用する。
            var catchAt = 0.0;
            foreach (var s in play.Ball)
            {
                var e = s as EndpointBallSegment;
                if (e == null || e.Kind == BallSegmentKind.Throw) continue;
                if (e.T1 <= toFirst.T0 + 1e-6 && e.T1 > catchAt) catchAt = e.T1;
            }
            if (catchAt <= 0.0) catchAt = toFirst.T0;

            var at = toFirst.At(0);
            var startDist = System.Math.Sqrt(start.X * start.X + start.Y * start.Y);
            var catchDist = System.Math.Sqrt(at.X * at.X + at.Y * at.Y);
            return new Measure
            {
                Role = toFirst.FromRole.Value,
                StartDist = startDist,
                CatchDist = catchDist,
                Advance = startDist - catchDist,
                CatchAt = catchAt,
                ThrowArriveAt = toFirst.T1,
            };
        }

        private static bool IsInfield(FieldPosition p)
            => p == FieldPosition.Pitcher || p == FieldPosition.FirstBase || p == FieldPosition.SecondBase
               || p == FieldPosition.ThirdBase || p == FieldPosition.Shortstop;

        // --- 撮影 ---

        private IEnumerator ShootPlay(Measure m, PlaybackPlay play, int game, int plate)
        {
            var waited = 0;
            while (!_ctrl.FieldPlayActiveForCapture && waited++ < 1800) yield return null;
            if (!_ctrl.FieldPlayActiveForCapture)
            {
                Debug.LogWarning($"[#630Shot] 守備再生が始まらないまま打ち切り（{game}試合目 {plate}打席目）");
                yield break;
            }

            // ⚠ 接触は 0.85s 固定（TimelineBuilder.ContactAt）。ここより前を狙うと投球前の整列が写るだけ。
            yield return WaitClock(0.90);
            yield return Shoot("1-start");    // 打った直後＝野手はまだ守備位置
            yield return WaitClock(System.Math.Max(0.95, m.CatchAt - 0.40));
            yield return Shoot("2-charge");   // 突っ込んでいる最中
            yield return WaitClock(m.CatchAt);
            yield return Shoot("3-catch");    // 捕球の瞬間（本塁寄りで捕る）
            yield return WaitClock(m.ThrowArriveAt);
            yield return Shoot("4-throw");    // 一塁到達

            Debug.Log($"[#630Shot] 撮影: {m.Role} 守備位置 {m.StartDist:F1}m → 捕球点 {m.CatchDist:F1}m" +
                      $"（前進 {m.Advance:F1}m）捕球 {m.CatchAt:F2}s 「{play.Result}」（{game}試合目 {plate}打席目）");
        }

        private IEnumerator WaitClock(double t)
        {
            var guard = 0;
            while (_ctrl.FieldPlayActiveForCapture && _ctrl.PlayClockForCapture < t && guard++ < 900) yield return null;
        }

        /// <summary>
        /// 画面まるごと1枚（撮影の間は進行を凍らせる＝節目がずれない。#595/#629 と同じ作法）。
        /// 守備ビュー（3D）と2D俯瞰（デバッグ表示）を<b>同じ瞬間で</b>2枚撮る:
        /// 3Dのカメラは球を追って動くので、コマ間で野手が前へ出たかは<b>画角が固定の2Dでしか読めない</b>。
        /// </summary>
        private IEnumerator Shoot(string file)
        {
            yield return ShootOne(file, false);
            yield return ShootOne(file + "-2d", true);
        }

        /// <summary>2D俯瞰（デバッグ）の on/off。private フィールド＋private メソッドなのでリフレクション。</summary>
        private void SetOverhead(bool on)
        {
            var t = typeof(MatchLiveController);
            var f = t.GetField("_duelOn", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var m = t.GetMethod("ApplyDuelViewState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (f == null || m == null) return;
            f.SetValue(_ctrl, !on);
            m.Invoke(_ctrl, null);
        }

        private IEnumerator ShootOne(string file, bool overhead)
        {
            _ctrl.FreezeLiveForCapture(true);
            if (overhead) SetOverhead(true);
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
                if (overhead) SetOverhead(false);
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
