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
    /// 【開発用・エディタ限定】バント処理点の座標妥当性確認（issue bunt-no-take-on-ball-pitches, Q32未決②）。
    /// 使い捨て。ChargePlayShooter（issue #630）と同じ作法。
    ///
    /// <para>実試合を進め、バント経路（犠打成功/内野安打/一塁-三塁封殺=ゴロ処理点 BuntFieldedXM/ZM、
    /// 小フライ/小フライ併殺=BuntPopFieldedZM）で決着した打席を1つずつ見つけて、2D俯瞰を連続撮影する。
    /// 判定はテストで固定済み＝ここは「処理点の絵の位置が不自然でないか」だけを見る。</para>
    ///
    /// 出力: screenshots/bunt-fielded-point/*.png ／ out/bunt-fielded-point/plays.tsv
    /// </summary>
    public sealed class BuntFieldedPointShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string ShotDir = "screenshots/bunt-fielded-point";
        private const string OutDir = "out/bunt-fielded-point";
        private const string SpawnFlag = "KokoSim.BuntFieldedPointShooter.Spawn";

        private const int MaxGames = 20;
        private const int MaxPlatesPerGame = 200;

        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~BuntFieldedPointAuto").AddComponent<BuntFieldedPointShooter>();
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
            if (_ctrl == null) { Debug.LogError("[BuntFieldShot] MatchLive/Controller が見つからない"); yield break; }
            _liveGo = _ctrl.gameObject;
            yield return null;

            var log = new StringBuilder("試合\t打席\t結果\t種別\t撮った\n");
            var gotGround = false;
            var gotPop = false;

            for (var game = 1; game <= MaxGames && !(gotGround && gotPop); game++)
            {
                if (game > 1) yield return RestartWithSeed((ulong)(70000 + game * 4231));

                for (var plate = 1; plate <= MaxPlatesPerGame && !_ctrl.IsGameOver && !(gotGround && gotPop); plate++)
                {
                    if (!_ctrl.AdvanceForCapture()) break;

                    var play = _ctrl.CurrentPa?.Play;
                    var kind = ClassifyBunt(play?.Result);
                    if (kind == BuntKind.None) continue;
                    var want = (kind == BuntKind.Ground && !gotGround) || (kind == BuntKind.Pop && !gotPop);

                    log.Append(game).Append('\t').Append(plate).Append('\t').Append(play.Result).Append('\t')
                       .Append(kind).Append('\t').Append(want ? "★" : "—").Append('\n');
                    if (!want) continue;

                    if (kind == BuntKind.Ground) gotGround = true; else gotPop = true;
                    yield return ShootPlay(kind, play, game, plate);
                }
            }

            File.WriteAllText(Path.Combine(OutDir, "plays.tsv"), log.ToString());
            Debug.Log($"[BuntFieldShot] ゴロ処理点={(gotGround ? "撮影済" : "見つからず")} " +
                      $"小フライ処理点={(gotPop ? "撮影済" : "見つからず")} → {ShotDir}");
            Time.captureDeltaTime = 0f;
        }

        private enum BuntKind { None, Ground, Pop }

        /// <summary>PlayTimeline.Result のテキスト（TimelineBuilder.BuildBunt が組む resultText）で分類する。</summary>
        private static BuntKind ClassifyBunt(string result)
        {
            if (string.IsNullOrEmpty(result)) return BuntKind.None;
            if (result.Contains("小フライ")) return BuntKind.Pop; // 小フライ・小フライ併殺の両方を含む
            if (result is "犠打成功" or "内野安打" or "一塁封殺" or "二塁封殺" or "三塁封殺") return BuntKind.Ground;
            return BuntKind.None;
        }

        private IEnumerator RestartWithSeed(ulong seed)
        {
            MatchLiveController.GameSeedOverrideForCapture = seed;
            _liveGo.SetActive(false);
            yield return null;
            _liveGo.SetActive(true);
            yield return null; yield return null;
        }

        // --- 撮影（ChargePlayShooter と同じ作法。処理点/捕球点の正確な秒は再実装せず、
        //     接触(0.85s固定)からの経過で数コマ連続して撮る＝どの型でも当たりが写る） ---

        private IEnumerator ShootPlay(BuntKind kind, PlaybackPlay play, int game, int plate)
        {
            var waited = 0;
            while (!_ctrl.FieldPlayActiveForCapture && waited++ < 1800) yield return null;
            if (!_ctrl.FieldPlayActiveForCapture)
            {
                Debug.LogWarning($"[BuntFieldShot] 守備再生が始まらないまま打ち切り（{game}試合目 {plate}打席目, {kind}）");
                yield break;
            }

            var tag = $"{kind}-g{game}p{plate}";
            yield return WaitClock(0.90); yield return Shoot(tag + "-1-contact");
            yield return WaitClock(1.30); yield return Shoot(tag + "-2-field");
            yield return WaitClock(1.80); yield return Shoot(tag + "-3-throw");
            yield return WaitClock(2.30); yield return Shoot(tag + "-4-resolve");

            Debug.Log($"[BuntFieldShot] 撮影: {kind} 「{play.Result}」（{game}試合目 {plate}打席目）");
        }

        private IEnumerator WaitClock(double t)
        {
            var guard = 0;
            while (_ctrl.FieldPlayActiveForCapture && _ctrl.PlayClockForCapture < t && guard++ < 900) yield return null;
        }

        // 2D俯瞰トグルは静的な守備位置ラベルの表示専用で、PlayTimelineの野手移動（FielderMove）を
        // 描かない（Match2DPlaybackElement は FielderMove/BallSegment を参照しない）。バント処理点の
        // 実際の絵は3D（試合は3D一本化, design-18）でしか見えないため、3Dのみ撮る。
        private IEnumerator Shoot(string file) => ShootOne(file, false);

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
