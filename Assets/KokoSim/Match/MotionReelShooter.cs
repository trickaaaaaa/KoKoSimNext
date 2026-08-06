#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】試合中のモーションを種類ごとに「録画」して連番JPEGで吐くハーネス。
    ///
    /// <b>なぜ帯（等間隔の連写）でなく録画か</b>: 既存の <see cref="MidPaPlayShooter"/> は defect の判定用で、
    /// 0.25秒おきの静止画を撮る。モーションそのもの（振り・走り・タッチ・捕球）は 0.25秒刻みでは
    /// つながって見えず、良し悪しを見られない。ここは <see cref="Fps"/> の等速で撮り切る。
    ///
    /// <b>なぜ「前録り（プリロール）」が要るか</b>: 撮りたい絵の始まりは**種類が判った瞬間より前**にある。
    /// 打球プレーは「投球→スイング→当たる」の後に <c>Stage=Play</c> へ入るので、検知してから録り始めると
    /// スイングが丸ごと落ちる。毎フレーム輪（<see cref="PreRollFrames"/> コマ）へ入れ続け、種類が判った
    /// 時点で輪の中身を先に書き出す。
    ///
    /// <b>なぜ確率を持ち上げるか</b>: 牽制 0.05・暴投 0.0012・捕逸 0.0008・振り逃げ 0.001 は素の試合では
    /// 出ない（<see cref="MidPaPlayShooter"/> と同じ理由）。<see cref="MatchLiveController.CaptureContextOverride"/>
    /// で撮影中のこのプロセスだけ差し替える＝本番の乱数順・統計帯には触れない。
    ///
    /// <b>1試合で揃わない種類がある</b>（本塁打・併殺・失策は出ない試合がある）。撮れた種類はディレクトリの
    /// 有無で覚えていて、シードを変えて Play し直すと**足りないぶんだけ**撮り足す。
    ///
    /// 出力: out/motion-reel/&lt;種類&gt;/f0000.jpg …  ＋ index.tsv（種類・枚数・結果テキスト・シード）
    /// </summary>
    public sealed class MotionReelShooter : MonoBehaviour
    {
        private const int W = 1280;
        private const int H = 720;

        /// <summary>録画レート。ポーズ量子化が12fpsなので、その整数倍でないと1ポーズの尺が均等にならない。</summary>
        private const int Fps = 24;

        /// <summary>前録りの長さ[コマ]。球間（既定2.1秒）＋振りかぶり（1.3秒）を覆う 4.0秒。</summary>
        private const int PreRollFrames = 96;

        /// <summary>種類が消えてからの後録り[コマ]＝結果テロップの一呼吸ぶん。</summary>
        private const int TailFrames = 24;

        /// <summary>1本の上限[コマ]（約11秒）。長い場面で走り続けないための打ち切り。</summary>
        private const int MaxClipFrames = 264;

        /// <summary>打ち切り（この打席数を消化しても揃わなければ諦めて出力する）。</summary>
        private const int MaxPa = 200;

        private const string OutDir = "out/motion-reel";
        private const string SpawnFlag = "KokoSim.MotionReelShooter.Spawn";
        private const string SeedKey = "KokoSim.MotionReelShooter.Seed";
        private const string AwayKey = "KokoSim.MotionReelShooter.Away";

        /// <summary>撮りたい種類と見出し。ここに無い場面は録らない（四球は絵が無い）。</summary>
        private static readonly (string Slug, string Label)[] Wanted =
        {
            ("bunt",         "バント"),
            ("steal-safe",   "盗塁成功"),
            ("steal-out",    "タッチアウト（盗塁死）"),
            ("pickoff",      "牽制"),
            ("pickoff-out",  "牽制死"),
            ("wild-pitch",   "暴投・捕逸"),
            ("drop-third",   "振り逃げ"),
            ("strikeout",    "三振"),
            ("homerun",      "本塁打"),
            ("double-play",  "併殺"),
            ("ground-out",   "ゴロアウト"),
            ("fly-out",      "フライアウト"),
            ("hit",          "安打"),
            ("error",        "失策"),
            ("hit-by-pitch", "死球"),
        };

        /// <param name="seed">試合の乱数シード（0＝据え置き）。</param>
        /// <param name="managerIsAway">監督が持つ側。バント・盗塁は監督側の采配なので、走者が出る側へ回る。</param>
        public static void Arm(ulong seed, bool managerIsAway = false)
        {
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);
            UnityEditor.EditorPrefs.SetString(SeedKey, seed.ToString());
            UnityEditor.EditorPrefs.SetBool(AwayKey, managerIsAway);
        }

        /// <summary>もう撮れている種類（<see cref="Boost"/> から見るので static。撮影開始前に確定させる）。</summary>
        private static HashSet<string> _already = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            _already = ScanDone();
            // ⚠ 係数もシードも監督側も MatchProgression が組まれる前でないと効かない（試合開始時に1度だけ読む）。
            MatchLiveController.CaptureContextOverride = Boost;
            if (ulong.TryParse(UnityEditor.EditorPrefs.GetString(SeedKey, "0"), out var s) && s != 0)
                MatchLiveController.GameSeedOverrideForCapture = s;
            MatchLiveController.ManagerIsAwayOverrideForCapture =
                UnityEditor.EditorPrefs.GetBool(AwayKey, false);
            new GameObject("~MotionReel").AddComponent<MotionReelShooter>();
        }

        private static HashSet<string> ScanDone()
        {
            var set = new HashSet<string>();
            foreach (var (slug, _) in Wanted)
            {
                var dir = Path.Combine(OutDir, slug);
                if (Directory.Exists(dir) && Directory.GetFiles(dir, "*.jpg").Length > 0) set.Add(slug);
            }
            return set;
        }

        /// <summary>
        /// 稀プレーの確率を「必ず起きる」寄りへ持ち上げた係数。撮影中だけ効く。
        /// ⚠ <b>撮り終えた種類は素へ戻す</b>。持ち上げっぱなしだと試合そのものが壊れて
        ///（実測: 振り逃げ 0.50＋暴投/捕逸 0.06 で3回18-0）、弱い側がいつまでも塁に出ず
        /// バント・盗塁・併殺が一度も撮れない。
        /// </summary>
        private static GameContext Boost(GameContext ctx)
        {
            var b = ctx.Baserunning;
            if (!_already.Contains("pickoff") || !_already.Contains("pickoff-out"))
                b = b with { PickoffBaseProb = 0.35, PickoffMaxProb = 0.70 };   // 既定 0.05 / 0.08
            if (!_already.Contains("wild-pitch"))
                b = b with { WildPitchProb = 0.06, PassedBallProb = 0.06 };     // 既定 0.0012 / 0.0008
            if (!_already.Contains("drop-third"))
                b = b with { DropThirdStrikeReachProb = 0.50 };                 // 既定 0.001
            // 併殺は確率でなく「守備が幾何で二塁へ投げるか」の境界で決まる（Issue #369）。実測: 素の
            // デモ試合は2試合450打席流して1本も出なかった（弱い守備が常に確実な一塁アウトを取るため）。
            // 転送の見込み下限を下げて＝刺せそうにない相手にも二塁へ投げさせて起こす。
            if (!_already.Contains("double-play"))
                b = b with
                {
                    DoublePlayAttemptMinRelaySuccess = 0.10,   // 既定 0.80
                    DoublePlayPivotTransferSeconds = 0.45,     // 既定 0.90
                    DoublePlayRelayBias = -0.35,               // 既定 0.00（負＝打者アウト寄り）
                };
            ctx = ctx with { Baserunning = b };
            // 死球は「体側ウィンドウへ入った球を避けられる確率」の裏返しなので、回避を下げて起こす。
            if (!_already.Contains("hit-by-pitch"))
                ctx = ctx with { Pitching = ctx.Pitching with { HbpDodgeProb = 0.10 } };  // 既定 0.85
            return ctx;
        }

        private MatchLiveController _ctrl;

        // 前録りの輪（JPEGバイト列。生ピクセルで持つとメモリが持たない）
        private readonly byte[][] _ring = new byte[PreRollFrames][];
        private int _ringHead;      // 次に書き込む位置
        private int _ringCount;

        // 録画中の1本
        private string _slug, _sceneKey, _label;
        private int _clipFrames, _tailLeft;

        private readonly HashSet<string> _done = new();
        private readonly StringBuilder _index = new();

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Time.captureDeltaTime = 1f / Fps;
            Directory.CreateDirectory(OutDir);
            // 既に撮れている種類は飛ばす（シードを変えた撮り足しのため）
            foreach (var slug in _already) _done.Add(slug);
            Debug.Log("[reel] 既に撮れている: " + (_done.Count == 0 ? "なし" : string.Join(",", _done)));

            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            _ctrl = FindController();
            if (_ctrl == null) { Debug.LogError("[reel] MatchLive/Controller が見つからない"); yield break; }

            // 録画中の画面遷移を差し止める（試合終了ハンドオフが MatchLive を非活性化すると rig ごと消える）
            var router = KokoSim.Unity.Shell.ScreenRouter.Instance;
            if (router != null) router.enabled = false;
            for (int i = 0; i < 30; i++) yield return null;
            EnsureOnlyMatchLive();
            yield return null;

            var rig = _ctrl.DuelRigForCapture;
            Debug.Log("[reel] rig=" + (rig == null ? "null" : rig.Available ? "使える" : "組めない"));

            // ── パネルをRTへ結線（1度だけ）─────────────────────────────
            // ⚠ 1コマごとに差し替えると結線待ちで4フレーム飛び、録画が 1/6 秒刻みの飛び飛びになる。
            // 走り切るまで張りっぱなしにする（Game ビューには映らなくなるが撮影中だけの話）。
            var ps = _ctrl.GetComponent<UIDocument>().panelSettings;
            var prevRt = ps.targetTexture; var prevScale = ps.scaleMode; var prevRef = ps.referenceResolution;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32); rt.Create();
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(W, H);
            ps.targetTexture = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);

            int pa = 0, guard = 0;
            bool prevWindow = false, prevMid = false;
            int midEpoch = 0, paEpoch = 0;
            object prevPa = null;

            try
            {
                while (_done.Count < Wanted.Length && pa < MaxPa && !_ctrl.IsGameOver && guard++ < 400000)
                {
                    yield return new WaitForEndOfFrame();

                    // ── 場面の同一性を刻む（同じ種類が続けて起きても別の1本にする）──
                    var midNow = _ctrl.MidPaPlayForCapture != null;
                    if (midNow && !prevMid) midEpoch++;
                    prevMid = midNow;
                    if (!ReferenceEquals(_ctrl.CurrentPa, prevPa)) { prevPa = _ctrl.CurrentPa; paEpoch++; }

                    // ── 采配（窓の頭で1回だけ）───────────────────────────
                    bool win = _ctrl.AtWindowForCapture;
                    if (win && !prevWindow)
                    {
                        pa++;
                        if (_ctrl.ManagerOnOffenseForCapture)
                        {
                            // 盗塁が先（成功・失敗の両方を撮る）。揃ったらバントへ切り替える。
                            if (!_done.Contains("steal-safe") || !_done.Contains("steal-out"))
                                _ctrl.SelectSignForCapture("run-steal");
                            else if (!_done.Contains("bunt"))
                                _ctrl.SelectPitchBattingForCapture(PitchBattingOverride.Bunt);
                        }
                    }
                    prevWindow = win;

                    // ── 1コマ読む ─────────────────────────────────────
                    var prevActive = RenderTexture.active;
                    RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                    tex.Apply();
                    RenderTexture.active = prevActive;
                    var bytes = tex.EncodeToJPG(88);

                    Step(bytes, midEpoch, paEpoch);
                }
            }
            finally
            {
                CloseClip();
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); DestroyImmediate(rt); DestroyImmediate(tex);
            }

            var missing = new List<string>();
            foreach (var (slug, label) in Wanted) if (!_done.Contains(slug)) missing.Add(slug + "(" + label + ")");
            File.AppendAllText(Path.Combine(OutDir, "index.tsv"), _index.ToString());
            Debug.Log("[reel] 撮り終わり: 打席=" + pa + " 試合終了=" + _ctrl.IsGameOver
                      + " / 足りない=" + (missing.Count == 0 ? "なし" : string.Join(",", missing)));
        }

        /// <summary>1コマぶんの状態遷移。輪へ入れる／本編へ書く／閉じるのどれかを行う。</summary>
        private void Step(byte[] bytes, int midEpoch, int paEpoch)
        {
            var (key, slug, label) = CurrentScene(midEpoch, paEpoch);

            if (_slug == null)
            {
                if (slug != null && !_done.Contains(slug))
                {
                    _slug = slug; _sceneKey = key; _label = label;
                    _clipFrames = 0; _tailLeft = TailFrames;
                    var dir = Path.Combine(OutDir, slug);
                    Directory.CreateDirectory(dir);
                    // 前録りを古い順に吐く（輪は循環しているので head から数える）
                    for (int i = 0; i < _ringCount; i++)
                    {
                        var b = _ring[(_ringHead - _ringCount + i + PreRollFrames * 2) % PreRollFrames];
                        if (b != null) WriteFrame(dir, b);
                    }
                    WriteFrame(dir, bytes);
                    Debug.Log("[reel] " + slug + " 録画開始（前録り" + _ringCount + "コマ）result=" + label);
                }
            }
            else
            {
                WriteFrame(Path.Combine(OutDir, _slug), bytes);
                if (key == _sceneKey) _tailLeft = TailFrames;
                else _tailLeft--;
                if (_tailLeft <= 0 || _clipFrames >= MaxClipFrames) CloseClip();
            }

            // 次の1本の前録りのため、録画中も輪は回し続ける
            _ring[_ringHead] = bytes;
            _ringHead = (_ringHead + 1) % PreRollFrames;
            if (_ringCount < PreRollFrames) _ringCount++;
        }

        private void WriteFrame(string dir, byte[] bytes)
        {
            File.WriteAllBytes(Path.Combine(dir, "f" + _clipFrames.ToString("D4") + ".jpg"), bytes);
            _clipFrames++;
        }

        private void CloseClip()
        {
            if (_slug == null) return;
            _done.Add(_slug);
            _index.Append(_slug).Append('\t').Append(_clipFrames).Append('\t').Append(Fps).Append('\t')
                  .Append(_label ?? "-").Append('\n');
            Debug.Log("[reel] " + _slug + " 録画終了 " + _clipFrames + "コマ");
            _slug = _sceneKey = _label = null;
            _clipFrames = 0;
            // ⚠ ここで輪を捨てない。捨てると「1本の直後に始まる場面」の前録りが空になり、
            // 見せたいスイングが丸ごと落ちる（実測: 振り逃げの直後の三振が本編1.25秒だけの30コマ）。
        }

        /// <summary>いま画面に出ている場面の (同一性キー, 種類, 結果テキスト)。撮る対象でなければ全部 null。</summary>
        private (string Key, string Slug, string Label) CurrentScene(int midEpoch, int paEpoch)
        {
            var mid = _ctrl.MidPaPlayForCapture;
            if (mid != null)
            {
                var res = _ctrl.MidPaPlayResultForCapture ?? "";
                var slug = mid switch
                {
                    "steal" => res == "盗塁死" ? "steal-out" : "steal-safe",
                    "pickoff" => res == "牽制死" ? "pickoff-out" : "pickoff",
                    "battery-miss" => "wild-pitch",
                    "drop-third" => "drop-third",
                    _ => null,
                };
                return ("mid" + midEpoch, slug, res);
            }

            // 打席の決着（三振・打球）。判定・結果テロップまで見せたいので Play の段の間ずっと同じキー。
            if (_ctrl.StageNameForCapture != "Play") return (null, null, null);
            var pa = _ctrl.CurrentPa;
            if (pa == null) return (null, null, null);
            var txt = pa.Play?.Result ?? "";
            var key = "pa" + paEpoch;

            if (_ctrl.BuntAttemptForCapture) return (key, "bunt", txt.Length == 0 ? "バント" : txt);
            // ⚠ 併殺は結果テキストに出ない（GIDP は集計値で、文言は「ショートゴロ」等のまま）。
            // 実測: 文字列で拾う実装は 200打席×2試合を流して一度も当たらなかった。判定イベントで拾う。
            if (OutJudgements(pa.Play) >= 2) return (key, "double-play", txt.Length == 0 ? "併殺" : txt);
            return pa.Result switch
            {
                PlateAppearanceResult.HomeRun => (key, "homerun", txt),
                PlateAppearanceResult.Strikeout => (key, "strikeout", "三振"),
                PlateAppearanceResult.ReachedOnError => (key, "error", txt),
                PlateAppearanceResult.HitByPitch => (key, "hit-by-pitch", "死球"),
                PlateAppearanceResult.Single or PlateAppearanceResult.Double
                    or PlateAppearanceResult.Triple => (key, "hit", txt),
                PlateAppearanceResult.InPlayOut =>
                    (key, txt.Contains("飛") || txt.Contains("フライ") || txt.Contains("ライナー")
                        ? "fly-out" : "ground-out", txt),
                _ => (null, null, null),   // 四球は絵が無い
            };
        }

        /// <summary>このプレーで確定したアウトの数（判定イベント。2件以上＝併殺）。</summary>
        private static int OutJudgements(KokoSim.Engine.Match.Timeline.Playback.PlaybackPlay play)
        {
            if (play == null) return 0;
            var n = 0;
            foreach (var j in play.Judgements)
                if (j.Kind == KokoSim.Engine.Match.Timeline.JudgementKind.Out) n++;
            return n;
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
