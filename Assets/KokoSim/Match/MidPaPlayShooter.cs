#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】夜間ランで撮れなかった「打席途中プレーの絵」の受け入れ（issue #485〜#490）。
    ///
    /// <b>なぜ専用ハーネスが要るか</b>: 牽制 0.05・暴投 0.0012・捕逸 0.0008・振り逃げ 0.001 は、
    /// 素の試合を何試合流しても検証に足る回数が出ない。<see cref="MatchLiveController.CaptureContextOverride"/>
    /// で確率だけを上げた <see cref="GameContext"/> を挿し、盗塁・バントは采配カードを押して起こす。
    /// 係数の差し替えは撮影中のこのプロセスだけ＝本番の乱数順・統計帯には触れない。
    ///
    /// <b>なぜ帯（等間隔の連写）で撮るか</b>: 節目だけ1枚ずつ撮ると「カメラが寄らない」のか
    /// 「寄る前の瞬間をたまたま撮った」のかが絵から区別できず、defect の判定ができない。
    /// 1プレーを頭から <see cref="StripStepSeconds"/> 間隔で撮り切る。
    ///
    /// <b>なぜ実試合を流すか</b>: 壊れ得るのは engine の出力ではなく画面の配線（再生経路の入口・
    /// ビュー切替・打ち切りの後始末）で、モックのプレーを rig へ直接流すと何も確かめられない
    /// （<see cref="MatchLiveFieldPlayShooter"/> と同じ理由）。自動連続進行（issue #173）にそのまま乗る
    /// ＝<c>CaptureMode</c> は立てない（撮影の間だけ <c>FreezeLiveForCapture</c> で凍らせる）。
    ///
    /// 出力:
    ///   screenshots/midpa/&lt;種類&gt;-s&lt;通番&gt;.png   各プレーの等間隔の帯
    ///   out/duel-stage/midpa/plays.tsv            1プレー＝1行（種類・尺・判定の出方・3Dへ入ったか）
    /// </summary>
    public sealed class MidPaPlayShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string ShotDir = "screenshots/midpa";
        private const string OutDir = "out/duel-stage/midpa";
        private const string SpawnFlag = "KokoSim.MidPaPlayShooter.Spawn";

        /// <summary>打ち切り（この打席数を消化しても揃わなければ諦めて出力する）。</summary>
        private const int MaxPa = 140;

        private const float StripStepSeconds = 0.25f;
        private const float StanceStepSeconds = 0.15f;   // バントの構えは1球ぶんしか窓が無いので細かく
        private const int StripMaxFrames = 14;

        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            // ⚠ 係数の差し替えは MatchProgression が組まれる前でないと効かない（試合開始時に1度だけ読む）。
            MatchLiveController.CaptureContextOverride = Boost;
            new GameObject("~MidPaAuto").AddComponent<MidPaPlayShooter>();
        }

        private MatchLiveController _ctrl;

        /// <summary>稀プレーの確率を「必ず起きる」寄りへ持ち上げた係数。撮影中だけ効く。</summary>
        private static GameContext Boost(GameContext ctx) => ctx with
        {
            Baserunning = ctx.Baserunning with
            {
                PickoffBaseProb = 0.55,           // 既定 0.05
                PickoffMaxProb = 0.90,            // 既定 0.08（上限で頭打ちにしない）
                WildPitchProb = 0.10,             // 既定 0.0012
                PassedBallProb = 0.10,            // 既定 0.0008
                DropThirdStrikeReachProb = 0.60,  // 既定 0.001
            },
        };

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
            if (_ctrl == null) { Debug.LogError("[midpa] MatchLive/Controller が見つからない"); yield break; }
            yield return null;

            var rig = _ctrl.DuelRigForCapture;
            Debug.Log("[midpa] rig=" + (rig == null ? "null" : rig.Available ? "使える" : "組めない"));

            var log = new StringBuilder("種類\t尺[s]\t判定\t文言\t3Dへ\t枚数\n");
            // 1コマごとの枠の実測。「カメラが寄らない」のか「寄っているのに被写体が小さい」のかは
            // 絵では切り分けられない（design-18 §3.3 と同じ理由）＝枠の高さ[m]と注視点で判定する。
            var frames = new StringBuilder("種類\tコマ\tt\t球x\t球y\t球h\t枠[m]\t注視y\t人px\t球px\n");
            var done = new HashSet<string>();
            bool prevWindow = false;
            bool buntArmed = false;
            int pa = 0;
            int guard = 0;

            while (done.Count < 5 && pa < MaxPa && !_ctrl.IsGameOver && guard++ < 400000)
            {
                yield return null;

                // ── 采配（窓の頭で1回だけ）───────────────────────────────
                bool win = _ctrl.AtWindowForCapture;
                if (win && !prevWindow)
                {
                    pa++;
                    if (_ctrl.ManagerOnOffenseForCapture)
                    {
                        // 盗塁が先。撮り終えたらバントへ切り替える（同時に出すと絵の切り分けができない）。
                        if (!done.Contains("steal")) _ctrl.SelectSignForCapture("run-steal");
                        else if (!done.Contains("bunt"))
                        {
                            _ctrl.SelectPitchBattingForCapture(PitchBattingOverride.Bunt);
                            buntArmed = true;
                        }
                    }
                }
                prevWindow = win;

                // ── バント（#490）: 構え（対決ビュー）→ 打球（守備ビュー）────
                if (buntArmed && !done.Contains("bunt") && _ctrl.BuntAttemptForCapture)
                {
                    int n1 = 0, n2 = 0;
                    yield return ShootStrip("bunt-stance", StanceStepSeconds,
                        () => _ctrl.BuntAttemptForCapture && !_ctrl.FieldPlayActiveForCapture, x => n1 = x);
                    if (_ctrl.FieldPlayActiveForCapture)
                        yield return ShootStrip("bunt-play", StripStepSeconds,
                            () => _ctrl.FieldPlayActiveForCapture, x => n2 = x, frames);
                    log.Append("bunt\t-\t-\t-\t").Append(n2 > 0 ? "入った" : "入らない")
                       .Append('\t').Append(n1 + n2).Append('\n');
                    done.Add("bunt");
                    buntArmed = false;
                    continue;
                }

                // ── 打席途中プレー（#485〜#488）─────────────────────────
                var kind = _ctrl.MidPaPlayForCapture;
                if (kind == null || done.Contains(kind)) continue;

                double headT = _ctrl.PlayClockForCapture;
                bool sawJudge = false, saw3d = false;
                string judgeText = "";
                int shotCount = 0;
                Debug.Log("[midpa] " + kind + " 開始");
                yield return ShootStrip(kind, StripStepSeconds, () =>
                {
                    if (_ctrl.MidPaPlayForCapture != kind) return false;
                    if (_ctrl.FieldPlayActiveForCapture) saw3d = true;
                    if (_ctrl.JudgementVisibleForCapture && !sawJudge)
                    {
                        sawJudge = true;
                        judgeText = _ctrl.JudgementTextForCapture;
                    }
                    return true;
                }, x => shotCount = x, frames);

                log.Append(kind).Append('\t')
                   .Append((_ctrl.PlayClockForCapture - headT).ToString("F2")).Append('\t')
                   .Append(sawJudge ? "出た" : "出ない").Append('\t')
                   .Append(judgeText.Length == 0 ? "-" : judgeText).Append('\t')
                   .Append(saw3d ? "入った" : "入らない").Append('\t').Append(shotCount).Append('\n');
                done.Add(kind);
            }

            File.WriteAllText(Path.Combine(OutDir, "plays.tsv"), log.ToString());
            File.WriteAllText(Path.Combine(OutDir, "frames.tsv"), frames.ToString());
            Debug.Log("[midpa] 撮り終わり: 揃った=" + string.Join(",", new List<string>(done))
                      + " / 打席=" + pa + " / 試合終了=" + _ctrl.IsGameOver + "\n" + log);
        }

        /// <summary>プレーが続いている間、等間隔で撮り続ける。<paramref name="alive"/> は毎フレーム呼ばれる
        /// （観測の副作用＝判定を見たか等もここで拾う）。</summary>
        private IEnumerator ShootStrip(string prefix, float step, Func<bool> alive, Action<int> count,
                                       StringBuilder frames = null)
        {
            int i = 0;
            while (i < StripMaxFrames && alive())
            {
                if (frames != null)
                {
                    var rig = _ctrl.DuelRigForCapture;
                    var t = (float)_ctrl.PlayClockForCapture;
                    // 列: name, maxZoom, margin, t, x, y, h, frameH, aimY, aimPx, ballPx, bu, bv, stationY
                    var c = rig != null ? rig.FieldFrameDiagForCapture(t).Split('\t') : null;
                    frames.Append(prefix).Append('\t').Append(i).Append('\t').Append(t.ToString("F2")).Append('\t');
                    if (c != null && c.Length >= 11)
                        frames.Append(c[4]).Append('\t').Append(c[5]).Append('\t').Append(c[6]).Append('\t')
                              .Append(c[7]).Append('\t').Append(c[8]).Append('\t').Append(c[9]).Append('\t')
                              .Append(c[10]);
                    else frames.Append("-\t-\t-\t-\t-\t-\t-");
                    frames.Append('\n');
                }
                yield return Shoot(prefix + "-s" + i.ToString("D2"));
                i++;
                float waited = 0f;
                while (waited < step && alive()) { yield return null; waited += Time.captureDeltaTime; }
            }
            count(i);
        }

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
                if (tex != null) DestroyImmediate(tex);
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); DestroyImmediate(rt);
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
