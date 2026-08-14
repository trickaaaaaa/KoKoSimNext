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
    /// 【開発用・エディタ限定】絵とセーフ/アウトのコールが一致していることの実機確認
    /// （issue match-picture-vs-safe-out-verdict）。使い捨て。
    ///
    /// <para>撮る場面（狙って作れないので実試合を流して自然発生を待つ）:</para>
    /// <list type="number">
    /// <item>2人以上が還る安打: <b>最後の走者が本塁を踏む瞬間</b>（送球はまだ届いていないはず）</item>
    /// <item>内野安打: <b>打者走者が一塁を踏む瞬間</b>（送球はまだ届いていないはず）</item>
    /// <item>内野ゴロの封殺: 同じ瞬間（こちらは送球が先に届いている＝アウトの絵）</item>
    /// </list>
    ///
    /// <para>絵だけでは0.1秒の前後は判定できないので、各コマの再生時計・走者到達・送球到達を
    /// TSV に並べて記録する（撮影と実測を対にする）。</para>
    ///
    /// 出力: screenshots/picture-vs-verdict/*.png ／ out/picture-vs-verdict/timing.tsv
    /// </summary>
    public sealed class PictureVsVerdictShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string ShotDir = "screenshots/picture-vs-verdict";
        private const string OutDir = "out/picture-vs-verdict";
        private const string SpawnFlag = "KokoSim.PictureVsVerdictShooter.Spawn";

        private const int MaxGames = 24;
        private const int MaxPlatesPerGame = 200;

        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~PictureVsVerdictAuto").AddComponent<PictureVsVerdictShooter>();
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

            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            _ctrl = FindController();
            if (_ctrl == null) { Debug.LogError("[絵と判定] MatchLive/Controller が見つからない"); yield break; }
            _liveGo = _ctrl.gameObject;
            yield return null;

            _log = new StringBuilder("場面\t結果\t撮った時計[s]\t走者到達[s]\t送球到達[s]\t差(送球−走者)[s]\n");
            var want = new HashSet<string> { "多点", "内野安打", "封殺" };

            for (var game = 1; game <= MaxGames && want.Count > 0; game++)
            {
                if (game > 1) yield return RestartWithSeed((ulong)(90000 + game * 5231));

                for (var plate = 1; plate <= MaxPlatesPerGame && !_ctrl.IsGameOver; plate++)
                {
                    if (!_ctrl.AdvanceForCapture()) break;
                    var pa = _ctrl.CurrentPa;
                    var play = pa?.Play;
                    if (play == null) { _ctrl.FreezeCurrentAtResult(); yield return null; continue; }

                    var kind = Classify(play, want);
                    if (kind == null) { _ctrl.FreezeCurrentAtResult(); yield return null; continue; }

                    want.Remove(kind);
                    var (runnerAt, throwAt) = kind == "多点" ? HomeRace(play) : FirstRace(play);
                    yield return ShootAt(kind, pa.Result.ToString(), runnerAt, throwAt);
                }
            }

            File.WriteAllText(Path.Combine(OutDir, "timing.tsv"), _log.ToString());
            foreach (var w in want) Debug.LogWarning("[絵と判定] 出なかった場面: " + w);
            Debug.Log("[絵と判定] DONE → " + ShotDir);
            Time.captureDeltaTime = 0f;
        }

        /// <summary>この打席が撮りたい場面のどれか（まだ撮っていないものだけ返す）。</summary>
        private static string Classify(PlaybackPlay play, HashSet<string> want)
        {
            var homeArrivals = 0;
            foreach (var r in play.Runners)
            {
                foreach (var s in r.Segs)
                {
                    if (Moved(s) && !s.OutAtEnd && NearHome(s)) homeArrivals++;
                }
            }
            // ⚠ 本塁送球つきに限る。本塁打も「2人以上が還る」を満たすが、送球が無いので
            //    アンカーの検証にならない（初回実測でホームランを撮ってしまった）。
            if (homeArrivals >= 2 && ThrowTo(play, "home") != null && want.Contains("多点")) return "多点";

            var toFirst = ThrowTo(play, "first");
            if (toFirst is null) return null;
            var batter = BatterLeg(play);
            if (batter is null) return null;
            if (!batter.OutAtEnd && want.Contains("内野安打")) return "内野安打";
            if (batter.OutAtEnd && want.Contains("封殺")) return "封殺";
            return null;
        }

        /// <summary>最後に還る走者の到達と本塁送球の到達。</summary>
        private static (double Runner, double Throw) HomeRace(PlaybackPlay play)
        {
            var last = 0.0;
            foreach (var r in play.Runners)
                foreach (var s in r.Segs)
                    if (Moved(s) && !s.OutAtEnd && NearHome(s) && s.T1 > last) last = s.T1;
            return (last, ThrowTo(play, "home") ?? -1.0);
        }

        /// <summary>打者走者の一塁到達と一塁送球の到達。</summary>
        private static (double Runner, double Throw) FirstRace(PlaybackPlay play)
        {
            var b = BatterLeg(play);
            return (b?.T1 ?? -1.0, ThrowTo(play, "first") ?? -1.0);
        }

        private static PlaybackRun? BatterLeg(PlaybackPlay play)
        {
            foreach (var r in play.Runners)
            {
                if (r.Label != "打") continue;
                foreach (var s in r.Segs)
                {
                    if (Moved(s)) return s;
                }
            }
            return null;
        }

        private static double? ThrowTo(PlaybackPlay play, string toBase)
        {
            double? first = null;
            foreach (var b in play.Ball)
            {
                if (b.ToBase != toBase) continue;
                if (first is null || b.T1 < first) first = b.T1;
            }
            return first;
        }

        private static bool Moved(PlaybackRun s)
        {
            var dx = s.To.X - s.From.X; var dy = s.To.Y - s.From.Y;
            return dx * dx + dy * dy > 0.25;
        }

        private static bool NearHome(PlaybackRun s) => s.To.X * s.To.X + s.To.Y * s.To.Y < 4.0;

        /// <summary>走者が塁を踏む瞬間まで流してから撮る。</summary>
        private IEnumerator ShootAt(string kind, string result, double runnerAt, double throwAt)
        {
            var waited = 0;
            while (waited++ < 3000 && !(_ctrl.StageNameForCapture == "Play" && _ctrl.PlayClockForCapture > 0))
                yield return null;
            var guard = 0;
            while (guard++ < 3000 && _ctrl.PlayClockForCapture < runnerAt && _ctrl.StageNameForCapture == "Play")
                yield return null;

            _log.Append(kind).Append('\t').Append(result).Append('\t')
                .Append(_ctrl.PlayClockForCapture.ToString("F2")).Append('\t')
                .Append(runnerAt.ToString("F2")).Append('\t').Append(throwAt.ToString("F2")).Append('\t')
                .Append((throwAt - runnerAt).ToString("F2")).Append('\n');
            Debug.Log($"[絵と判定] {kind}（{result}）走者{runnerAt:F2}s 送球{throwAt:F2}s 差{throwAt - runnerAt:+0.00;-0.00}s");
            yield return Shoot(FileNameOf(kind));
        }

        private static string FileNameOf(string kind) => kind switch
        {
            "多点" => "1-multi-run-home",
            "内野安打" => "2-infield-hit-first",
            _ => "3-force-out-first",
        };

        private IEnumerator RestartWithSeed(ulong seed)
        {
            MatchLiveController.GameSeedOverrideForCapture = seed;
            _liveGo.SetActive(false);
            yield return null;
            _liveGo.SetActive(true);
            yield return null; yield return null;
        }

        private IEnumerator Shoot(string file)
        {
            _ctrl.FreezeLiveForCapture(true);
            yield return null;
            yield return new WaitForEndOfFrame();
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
