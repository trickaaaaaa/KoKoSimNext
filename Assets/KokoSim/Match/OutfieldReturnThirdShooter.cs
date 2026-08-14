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
    /// 【開発用・エディタ限定】単打で一塁走者が三塁まで進んだとき、返球が三塁へ行くことの絵の確認（issue #627）。使い捨て。
    ///
    /// <para><b>探し方</b>: engine 側の <c>FieldingPlay</c> は required メンバーを持ち Unity（C# 9）から
    /// 組めないので、場面はリグへ直接流さず実試合を流して自然発生を待つ。1打席あたりの出現率は
    /// 概算1%（走者一塁 × 外野への単打 × 三塁到達）なので、1試合で出なければシードを変えて試合ごと引き直す。</para>
    ///
    /// <para><b>速く回すこつ</b>: <c>AdvanceForCapture</c> は打席を解決して再生を始めるので、
    /// <b>呼んだ直後の同じフレームで</b> <c>CurrentPa.Play</c> を見て要否を決める。要らない打席は
    /// 再生を待たずに次を呼ぶ＝1試合を数秒で走査できる（待つと1試合で数分かかる）。</para>
    ///
    /// <para>セーフ（三塁到達）と憤死（三塁でアウト）の両方を撮る。憤死はさらに稀なので、
    /// 見つからなければセーフだけで打ち切ってログに残す。</para>
    ///
    /// 出力: screenshots/outfield-return-627/*.png ／ out/outfield-return-627/plays.tsv
    /// </summary>
    public sealed class OutfieldReturnThirdShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string ShotDir = "screenshots/outfield-return-627";
        private const string OutDir = "out/outfield-return-627";
        private const string SpawnFlag = "KokoSim.OutfieldReturnThirdShooter.Spawn";

        /// <summary>引き直す試合数の上限。</summary>
        private const int MaxGames = 24;
        /// <summary>1試合あたりの打席上限（保険。実際は gameOver で抜ける）。</summary>
        private const int MaxPlatesPerGame = 200;

        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~OutfieldReturnThirdAuto").AddComponent<OutfieldReturnThirdShooter>();
        }

        private MatchLiveController _ctrl;
        private GameObject _liveGo;
        private bool _shot;
        private readonly StringBuilder _log = new StringBuilder("試合\t打席\t結果\t返球先\t走者到達\t憤死\t撮った\n");

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
            if (_ctrl == null) { Debug.LogError("[#627Shot] MatchLive/Controller が見つからない"); yield break; }
            _liveGo = _ctrl.gameObject;
            yield return null;

            var gotSafe = false;
            var gotOut = false;

            for (var game = 1; game <= MaxGames && !(gotSafe && gotOut); game++)
            {
                if (game > 1) yield return RestartWithSeed((ulong)(90000 + game * 7919));

                for (var plate = 1; plate <= MaxPlatesPerGame && !_ctrl.IsGameOver; plate++)
                {
                    if (!_ctrl.AdvanceForCapture()) break;

                    // ⚠ 解決した直後の同じフレームで見る（次の Advance で再生ごと捨てられる）。
                    var play = _ctrl.CurrentPa?.Play;
                    var target = ThrowTargetOf(play);
                    var runnerToThird = RunnerReachingThird(play, out var runnerOut);
                    // 外野手が投げ始めた連鎖に限る（内野ゴロの三塁フォースを拾わない）。
                    var want = target == "三塁" && runnerToThird && FromOutfield(play)
                               && (runnerOut ? !gotOut : !gotSafe);

                    _log.Append(game).Append('\t').Append(plate).Append('\t')
                        .Append(play?.Result ?? "-").Append('\t').Append(target).Append('\t')
                        .Append(runnerToThird ? "三塁" : "—").Append('\t')
                        .Append(runnerOut ? "憤死" : "—").Append('\t').Append(want ? "★" : "—").Append('\n');

                    if (!want) continue;

                    var tag = runnerOut ? "out" : "safe";
                    _shot = false;
                    yield return ShootPlay(tag, ThrowArriveOf(play), game, plate);
                    if (!_shot) continue;               // 撮れていない＝この場面は無かったことにして探し直す
                    if (runnerOut) gotOut = true; else gotSafe = true;
                    if (gotSafe && gotOut) break;

                    // 撮った打席の再生を畳んでから次の打席へ（絵が混ざらない）。
                    var waited = 0;
                    while (_ctrl.FieldPlayActiveForCapture && waited++ < 900) yield return null;
                }
            }

            File.WriteAllText(Path.Combine(OutDir, "plays.tsv"), _log.ToString());
            Debug.Log($"[#627Shot] セーフ={(gotSafe ? "撮れた" : "★出ず")} 憤死={(gotOut ? "撮れた" : "★出ず")} → {ShotDir}");
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

        // --- 再生データの読み取り（構築はしない＝required メンバーに触れないので C# 9 で通る） ---

        private static bool NearBase(double x, double y, double bx, double by)
            => System.Math.Abs(x - bx) < 2.0 && System.Math.Abs(y - by) < 2.0;

        /// <summary>
        /// 最後に着く送球区間。⚠ <c>ThrowSegment</c> は<b>モック専用</b>（<c>PlaybackSamples</c>）で、
        /// 実試合の再生は <see cref="EndpointBallSegment"/> しか作らない（初回実測: 2013打席すべて
        /// 「送球なし」と判定してしまった）。種別は <c>Kind</c> で見る。
        /// </summary>
        private static EndpointBallSegment LastThrow(PlaybackPlay play)
        {
            if (play == null) return null;
            EndpointBallSegment last = null;
            foreach (var s in play.Ball)
                if (s is EndpointBallSegment e && e.Kind == KokoSim.Engine.Match.Timeline.BallSegmentKind.Throw
                    && (last == null || e.T1 > last.T1)) last = e;
            return last;
        }

        /// <summary>最後に着く送球の行き先（塁名は engine が積んだ ToBase をそのまま読む）。</summary>
        private static string ThrowTargetOf(PlaybackPlay play)
        {
            var last = LastThrow(play);
            if (last == null) return "—";
            switch (last.ToBase)
            {
                case "third": return "三塁";
                case "second": return "二塁";
                case "first": return "一塁";
                case "home": return "本塁";
            }
            var p = last.At(last.T1 - last.T0);
            if (NearBase(p.X, p.Y, -19.40, 19.40)) return "三塁";
            if (NearBase(p.X, p.Y, 0.0, 38.79)) return "二塁";
            if (NearBase(p.X, p.Y, 0.0, 0.0)) return "本塁";
            if (NearBase(p.X, p.Y, 19.40, 19.40)) return "一塁";
            return "他";
        }

        /// <summary>送球連鎖の1本目を外野手が投げているか（＝外野からの返球）。</summary>
        private static bool FromOutfield(PlaybackPlay play)
        {
            if (play == null) return false;
            EndpointBallSegment first = null;
            foreach (var s in play.Ball)
                if (s is EndpointBallSegment e && e.Kind == KokoSim.Engine.Match.Timeline.BallSegmentKind.Throw
                    && (first == null || e.T0 < first.T0)) first = e;
            var r = first?.FromRole;
            return r == KokoSim.Engine.Match.Field.FieldPosition.LeftField
                || r == KokoSim.Engine.Match.Field.FieldPosition.CenterField
                || r == KokoSim.Engine.Match.Field.FieldPosition.RightField;
        }

        private static double ThrowArriveOf(PlaybackPlay play)
        {
            var last = LastThrow(play);
            return last?.T1 ?? 0.0;
        }

        /// <summary>打者走者**以外**が三塁へ達しているか（三塁打と区別する）。</summary>
        private static bool RunnerReachingThird(PlaybackPlay play, out bool runnerOut)
        {
            runnerOut = false;
            if (play == null) return false;
            foreach (var r in play.Runners)
            {
                if (r.Label == "打" || r.Segs.Count == 0) continue;
                var lastLeg = r.Segs[r.Segs.Count - 1];
                if (!NearBase(lastLeg.To.X, lastLeg.To.Y, -19.40, 19.40)) continue;
                runnerOut = lastLeg.OutAtEnd;
                return true;
            }
            return false;
        }

        // --- 撮影 ---

        /// <summary>
        /// 送球の飛翔中と到達の瞬間を3D守備ビューで撮る。
        ///
        /// <para>2D俯瞰（デバッグ）で撮り直す道は<b>断念した</b>: <c>RestartCurrentPaForCapture</c> を
        /// 呼んでも守備再生が立ち上がらず（実測2回とも不発）、待ち方を変えても取れなかった。
        /// 3Dのカメラは返球先の塁を自然に追う（実測: 三塁ベース後方から中堅方向を見る画になり、
        /// 走者・三塁手・中継役が1枚に収まる）ので、返球先の確認は3Dの2枚で足りる。</para>
        /// </summary>
        private IEnumerator ShootPlay(string tag, double arriveAt, int game, int plate)
        {
            yield return WaitFieldPlay();
            if (!_ctrl.FieldPlayActiveForCapture)
            {
                // ⚠ ここで撮ると無関係な投球シーンが残る（初回実測: 別試合の1回表の対決ビューを保存した）。
                Debug.LogWarning($"[#627Shot] {tag}: 守備再生が始まらないまま打ち切り（{game}試合目 {plate}打席目）");
                yield break;
            }

            yield return WaitClock(System.Math.Max(0.0, arriveAt - 1.0));
            yield return Shoot(tag + "-1-throw");
            yield return WaitClock(arriveAt);
            yield return Shoot(tag + "-2-arrive");

            _shot = true;
            Debug.Log($"[#627Shot] {tag} 撮影（{game}試合目 {plate}打席目 送球到達={arriveAt:F2}s）");
        }

        /// <summary>守備再生が始まるまで待つ（打席の投球列を流し切るので長めに見る）。</summary>
        private IEnumerator WaitFieldPlay()
        {
            var waited = 0;
            while (!_ctrl.FieldPlayActiveForCapture && waited++ < 1800) yield return null;
        }

        private IEnumerator WaitClock(double t)
        {
            var guard = 0;
            while (_ctrl.FieldPlayActiveForCapture && _ctrl.PlayClockForCapture < t && guard++ < 900) yield return null;
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
