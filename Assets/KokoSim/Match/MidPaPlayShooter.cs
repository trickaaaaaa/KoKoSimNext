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

        /// <summary>打ち切り（この<b>打席</b>数を消化しても揃わなければ諦めて出力する）。
        /// ⚠ 数える口は <see cref="MatchLiveController.AtPaHeadWindowForCapture"/>（打席の頭）であって
        /// <see cref="MatchLiveController.AtWindowForCapture"/>（球ごとの窓・設計書15）ではない。
        /// 球の窓で数えると 140 は「140球＝4イニング弱」を意味し、試合が終わる前に打ち切られる
        /// （実測: 試合終了=False のまま steal と bunt が一度も撮れずに終了。issue #489）。</summary>
        private const int MaxPa = 140;

        /// <summary>盗塁を追うのをやめてバントへ譲る打席数（盗塁は一塁走者が要る＝機会が来ない試合がある）。</summary>
        private const int StealGiveUpPa = 60;

        private const float StripStepSeconds = 0.25f;
        private const float StanceStepSeconds = 0.15f;   // バントの構えは1球ぶんしか窓が無いので細かく
        private const int StripMaxFrames = 14;

        /// <summary>
        /// 撮る組。<b>1試合で5種類は撮れない</b>ので2回に分ける（issue #489 実測）。
        /// 牽制・盗塁・バントはどれも「一塁走者が居る球」を取り合うが、その窓は1試合で 11 回しか無く、
        /// 牽制を <c>PickoffBaseProb=0.55 / MaxProb=0.90</c> まで持ち上げると走者がその場で消えて
        /// 盗塁の番が回ってこない（実測: 一塁走者あり11窓・盗塁カード押下8回で盗塁 0 件）。
        /// </summary>
        public enum Set
        {
            /// <summary>暴投・捕逸・振り逃げ・牽制（電池まわりの稀プレー）。牽制を強く持ち上げる。</summary>
            Battery,
            /// <summary>盗塁死・バント（走者を使うプレー）。牽制と暴投・捕逸は既定へ戻して走者を残す。</summary>
            Runners,
        }

        private const string SetKey = "KokoSim.MidPaPlayShooter.Set";

        public static void Arm(Set set = Set.Battery)
        {
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);
            UnityEditor.EditorPrefs.SetInt(SetKey, (int)set);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            _set = (Set)UnityEditor.EditorPrefs.GetInt(SetKey, (int)Set.Battery);
            // ⚠ 係数の差し替えは MatchProgression が組まれる前でないと効かない（試合開始時に1度だけ読む）
            //    ＝撮り終えた種類のぶんだけ確率を戻す、という動的な調整はできない。だから組で分ける。
            MatchLiveController.CaptureContextOverride = Boost;
            new GameObject("~MidPaAuto").AddComponent<MidPaPlayShooter>();
        }

        private static Set _set = Set.Battery;

        private MatchLiveController _ctrl;

        /// <summary>稀プレーの確率を「必ず起きる」寄りへ持ち上げた係数。撮影中だけ効く。</summary>
        private static GameContext Boost(GameContext ctx) => ctx with
        {
            Baserunning = ctx.Baserunning with
            {
                // 一塁走者を取り合う牽制は Battery 組でだけ持ち上げる（Runners 組では既定＝走者が残る）。
                PickoffBaseProb = _set == Set.Battery ? 0.55 : ctx.Baserunning.PickoffBaseProb,  // 既定 0.05
                PickoffMaxProb = _set == Set.Battery ? 0.90 : ctx.Baserunning.PickoffMaxProb,    // 既定 0.08
                // 暴投・捕逸は一塁走者を二塁へ進めてしまう＝これも Battery 組でだけ持ち上げる。
                WildPitchProb = _set == Set.Battery ? 0.10 : ctx.Baserunning.WildPitchProb,      // 既定 0.0012
                PassedBallProb = _set == Set.Battery ? 0.10 : ctx.Baserunning.PassedBallProb,    // 既定 0.0008
                // 振り逃げは打者を一塁へ置く＝どちらの組でも走者を増やす方向なので常に持ち上げてよい。
                DropThirdStrikeReachProb = 0.60,  // 既定 0.001
                // issue #489: 盗塁死（タッチアウト）の絵を必ず出す。既定 0.38 を大きく下回らせて
                // 必ず刺される側へ倒す（margin→logisticのバイアス項なので符号反転で確実に振り切る）。
                StealSuccessBias = -3.0,
            },
        };

        /// <summary>この組で撮り切るべき種類。</summary>
        private static string[] Wanted =>
            _set == Set.Battery
                ? new[] { "pickoff", "battery-miss", "drop-third" }
                : new[] { "steal", "bunt" };

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
            bool prevWindow = false, prevPaHead = false;
            bool buntArmed = false, buntPressed = false;
            int pa = 0;
            int guard = 0;
            // 撮れなかった時に原因を残すための計数。カードは無効なら押しても黙って何も起きないので、
            // 「機会が来なかった」のか「押せていたのに出なかった」のかはこれが無いと分からない。
            int windows = 0, winOnOffense = 0, winRunnerOnFirst = 0, stealPressed = 0;

            var wanted = new HashSet<string>(Wanted);
            Debug.Log("[midpa] 組=" + _set + " 撮る種類=" + string.Join(",", Wanted));

            while (!done.IsSupersetOf(wanted) && pa < MaxPa && !_ctrl.IsGameOver && guard++ < 400000)
            {
                yield return null;

                // 打席数は「打席の頭の窓」で数える（球ごとの窓で数えると MaxPa が球数になる）。
                bool paHead = _ctrl.AtPaHeadWindowForCapture;
                if (paHead && !prevPaHead) pa++;
                prevPaHead = paHead;

                // ── 采配（窓の頭で1回だけ）───────────────────────────────
                bool win = _ctrl.AtWindowForCapture;
                if (win && !prevWindow)
                {
                    windows++;
                    if (_ctrl.ManagerOnOffenseForCapture)
                    {
                        winOnOffense++;
                        if (_ctrl.RunnerOnFirstForCapture) winRunnerOnFirst++;
                        // 盗塁が先。撮り終えたらバントへ切り替える（同時に出すと絵の切り分けができない）。
                        // ただし盗塁が来ないまま試合を使い切るとバントまで道連れで撮れない（issue #489 の実害）。
                        // StealGiveUpPa 打席で見切ってバントへ譲る＝どちらか片方は必ず撮れるようにする。
                        bool chaseSteal = wanted.Contains("steal") && !done.Contains("steal") && pa < StealGiveUpPa;
                        if (chaseSteal)
                        {
                            if (_ctrl.RunnerOnFirstForCapture) stealPressed++;
                            _ctrl.SelectSignForCapture("run-steal");
                        }
                        else if (wanted.Contains("bunt") && !done.Contains("bunt") && !buntPressed)
                        {
                            // ⚠ 1回だけ押す。カードはトグルなので毎窓押すと**次の窓で消灯**し、
                            // 構えの前倒しが台無しになる（実測: リリース時点で移行クリップが毎回 t=0.00）。
                            _ctrl.SelectPitchBattingForCapture(PitchBattingOverride.Bunt);
                            buntPressed = true;
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
                        () => _ctrl.BuntAttemptForCapture && !_ctrl.FieldPlayActiveForCapture, x => n1 = x,
                        null, "打者");
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
                if (kind == null || done.Contains(kind) || !wanted.Contains(kind)) continue;

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

                // タッチアウトの受け入れ実測（issue #489）。走者が滑って倒れる横で野手が
                // 届いているかは絵では狙い外しと区別できないので、数を残す。
                if (kind == "steal")
                {
                    var rig2 = _ctrl.DuelRigForCapture;
                    if (rig2 != null)
                    {
                        var diag = rig2.TagOutDiagForCapture();
                        File.WriteAllText(Path.Combine(OutDir, "tagout.tsv"), diag);
                        Debug.Log("[midpa] タッチ実測\n" + diag);
                    }
                }

                log.Append(kind).Append('\t')
                   .Append((_ctrl.PlayClockForCapture - headT).ToString("F2")).Append('\t')
                   .Append(sawJudge ? "出た" : "出ない").Append('\t')
                   .Append(judgeText.Length == 0 ? "-" : judgeText).Append('\t')
                   .Append(saw3d ? "入った" : "入らない").Append('\t').Append(shotCount).Append('\n');
                done.Add(kind);
            }

            File.WriteAllText(Path.Combine(OutDir, "plays.tsv"), log.ToString());
            File.WriteAllText(Path.Combine(OutDir, "frames.tsv"), frames.ToString());
            // 撮れなかった種類がある時に「機会が無かった」のか「機会はあったのに絵にならなかった」のかを
            // ここだけ見れば切り分けられるようにする（無いと次の人がまた同じ調査からやり直す）。
            Debug.Log("[midpa] 撮り終わり: 揃った=" + string.Join(",", new List<string>(done))
                      + " / 打席=" + pa + "(上限" + MaxPa + ") / 試合終了=" + _ctrl.IsGameOver
                      + " / 窓=" + windows + " 攻撃側=" + winOnOffense
                      + " 一塁走者あり=" + winRunnerOnFirst + " 盗塁カード押下=" + stealPressed
                      + "\n" + log);
        }

        /// <summary>プレーが続いている間、等間隔で撮り続ける。<paramref name="alive"/> は毎フレーム呼ばれる
        /// （観測の副作用＝判定を見たか等もここで拾う）。</summary>
        private IEnumerator ShootStrip(string prefix, float step, Func<bool> alive, Action<int> count,
                                       StringBuilder frames = null, string clipLog = null)
        {
            int i = 0;
            while (i < StripMaxFrames && alive())
            {
                if (clipLog != null)
                {
                    var r = _ctrl.DuelRigForCapture;
                    Debug.Log("[midpa] " + clipLog + " " + i + " "
                              + (r != null ? r.BatterClipDiagForCapture() : "-"));
                }
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
