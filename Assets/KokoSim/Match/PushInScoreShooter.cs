#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】押し出し（打球の無い得点）で掲示板がその場で増えることの絵の確認（issue #628）。使い捨て。
    ///
    /// <para><b>探し方</b>: 満塁の四球・死球は珍しいので、実試合を流して自然発生を待つ。
    /// 狙いは「打球タイムラインが無い（<c>Play == null</c>）のに得点が入った打席」＝押し出しそのもの。
    /// <c>AdvanceForCapture</c> は打席を解決して掲示まで済ませるので、<b>呼んだ直後の同じフレームで</b>
    /// <c>CurrentPa</c> を見て要否を決める（#627 と同じ作法）。</para>
    ///
    /// <para>撮るのは①確定直後（得点コールが加点前スコアで開いた瞬間）②加点後。
    /// ラインスコアとスコアバグは①の時点でもう増えている＝「走者の到達を待たない」ことが1枚で分かる。</para>
    ///
    /// 出力: screenshots/push-in-score-628/*.png ／ out/push-in-score-628/plays.tsv
    /// </summary>
    public sealed class PushInScoreShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string ShotDir = "screenshots/push-in-score-628";
        private const string OutDir = "out/push-in-score-628";
        private const string SpawnFlag = "KokoSim.PushInScoreShooter.Spawn";

        private const int MaxGames = 24;
        private const int MaxPlatesPerGame = 200;

        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~PushInScoreAuto").AddComponent<PushInScoreShooter>();
        }

        private MatchLiveController _ctrl;
        private GameObject _liveGo;

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
            if (_ctrl == null) { Debug.LogError("[#628Shot] MatchLive/Controller が見つからない"); yield break; }
            _liveGo = _ctrl.gameObject;
            yield return null;

            var log = new StringBuilder("試合\t打席\t結果\t打球\t得点\t満塁前\t撮った\n");
            var shot = false;

            for (var game = 1; game <= MaxGames && !shot; game++)
            {
                if (game > 1) yield return RestartWithSeed((ulong)(70000 + game * 6151));

                for (var plate = 1; plate <= MaxPlatesPerGame && !_ctrl.IsGameOver; plate++)
                {
                    var at = _ctrl.CurrentAtBatForCapture;
                    var loadedBefore = (at?.BaseFirst ?? false) && (at?.BaseSecond ?? false) && (at?.BaseThird ?? false);

                    if (!_ctrl.AdvanceForCapture()) break;

                    var pa = _ctrl.CurrentPa;
                    // 打球の無い打席（Play==null）で点が入った＝押し出し。
                    var want = pa != null && pa.Play == null && pa.RunsScored > 0;
                    log.Append(game).Append('\t').Append(plate).Append('\t')
                       .Append(pa?.Result.ToString() ?? "-").Append('\t')
                       .Append(pa?.Play == null ? "無" : "有").Append('\t')
                       .Append(pa?.RunsScored ?? 0).Append('\t')
                       .Append(loadedBefore ? "満塁" : "—").Append('\t').Append(want ? "★" : "—").Append('\n');

                    if (!want)
                    {
                        // ⚠ 要らない打席も**掲示だけは追いつかせる**（issue #628 の絵の前提）。
                        // 素通しすると再生が一度も終わらないので掲示板が試合開始時のまま止まり、
                        // 狙いの打席で「1点ぶんの+1」ではなく溜まった点が一気に出る（初回実測:
                        // 2回裏なのに掲示は1回表 0-0 のままだった）。
                        _ctrl.FreezeCurrentAtResult();
                        yield return null;
                        continue;
                    }

                    // 狙いの打席は凍らせず、実装どおりの経路（投球列→打球なしの結果保持）を流す。
                    // 掲示は EnterBattedBallOrHold の else 側＝結果の据え置きに入った瞬間に出る。
                    var waited = 0;
                    while (waited++ < 3000
                           && !(_ctrl.StageNameForCapture == "Play" && _ctrl.PlayClockForCapture < 0))
                        yield return null;

                    shot = true;
                    yield return Shoot("1-confirmed");    // 得点コールが加点前スコアで開いた瞬間
                    yield return WaitFrames(20);
                    yield return Shoot("2-added");        // +1 が入った後
                    Debug.Log($"[#628Shot] 押し出しを撮影（{game}試合目 {plate}打席目 {pa.Result} {pa.RunsScored}点）");
                    break;
                }
            }

            File.WriteAllText(Path.Combine(OutDir, "plays.tsv"), log.ToString());
            Debug.Log($"[#628Shot] {(shot ? "撮れた" : "★押し出しが出ないまま打ち切り")} → {ShotDir}");
            Time.captureDeltaTime = 0f;
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

        private IEnumerator WaitFrames(int n)
        {
            for (int i = 0; i < n; i++) yield return null;
        }

        /// <summary>画面まるごと1枚（撮影の間は進行を凍らせる＝節目がずれない。#595 と同じ作法）。</summary>
        private IEnumerator Shoot(string file)
        {
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
