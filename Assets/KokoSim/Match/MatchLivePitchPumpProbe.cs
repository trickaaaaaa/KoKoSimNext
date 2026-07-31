#if UNITY_EDITOR
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】1球進行のポンプ（設計書15 §8）が実進行で回っているかの計測。
    ///
    /// 見たいのは絵では分からない4点:
    /// ①<b>打席の頭の窓が飛んでいない</b>——1球も投げていない時点で打者が分かり、指示を受け付けられる
    /// ②カード列の攻守が映している半回と一致し続ける（半回の変わり目で裏返らない）
    /// ③イニング間画面が出て、ちゃんと明ける（進行が止まらない）
    /// ④<b>エンジン1歩のコスト</b>——1球進行はメインスレッド同期なので、1歩が1フレーム（16ms）に収まるか。
    ///   設計書15 §8.3-⑤「背景スレッドは要らないはずだが移行時に実測する」の実測。
    /// 使い捨て。
    /// </summary>
    public sealed class MatchLivePitchPumpProbe : MonoBehaviour
    {
        [SerializeField] private float watchSeconds = 120f;
        [SerializeField] private float speed = 12f;
        [SerializeField] private ulong timingSeed = 20260718UL;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MeasureEngineStepCost();

            MatchLiveController.CaptureMode = false;   // 実進行（1球ポンプ）で回す
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            yield return null;

            MatchLiveController ctrl = null;
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid() || d.gameObject.name != "MatchLive") continue;
                ctrl = d.GetComponent<MatchLiveController>();
            }
            if (ctrl == null) { Debug.LogError("[PumpProbe] MatchLive が見つからない"); yield break; }

            const BindingFlags Bf = BindingFlags.NonPublic | BindingFlags.Instance;
            var t = typeof(MatchLiveController);
            var fStage = t.GetField("_stage", Bf);
            var fAtBat = t.GetField("_atBat", Bf);
            var fQueue = t.GetField("_paQueue", Bf);
            var fBreak = t.GetField("_inningBreakOn", Bf);
            var fSpeed = t.GetField("_speed", Bf);
            var fDeck = t.GetField("_signPad", Bf);
            var fAway = t.GetField("_managerIsAway", Bf);
            var fDuelOn = t.GetField("_duelOn", Bf);
            var fRig = t.GetField("_duelRig", Bf);
            var rt = typeof(DuelStageRig);
            var fBatter = rt.GetField("_batter", Bf);
            var at2 = rt.GetNestedType("Actor", BindingFlags.NonPublic);
            var fActorClip = at2.GetField("Clip");
            var fActorIdle = at2.GetField("IdleClip");
            var fOffense = typeof(KokoSim.Unity.Components.TacticsCardDeck)
                .GetField("_offense", BindingFlags.NonPublic | BindingFlags.Instance);
            var managerIsAway = (bool)fAway.GetValue(ctrl);
            fSpeed.SetValue(ctrl, speed);

            var windows = 0;         // 開いた采配窓の数
            var heads = 0;           // うち打席の頭（まだ1球も投げていない）
            var pitches = 0;         // 描いた球
            var breaks = 0;          // イニング間画面
            var phaseMismatch = 0;   // カード列と映している半回の食い違い
            var headWithoutBatter = 0;
            var duelOffAtHead = 0;
            var ghostSwingAtHead = 0;   // 打席の頭なのに打者が待機以外のクリップ（＝前打席の振りの残り）
            LiveAtBat prevAtBat = null;
            var prevQueue = 0;
            var prevBreak = false;
            var clock = 0f;
            var lastProgressAt = 0f;
            var stallReported = false;

            while (clock < watchSeconds && !ctrl.IsGameOver)
            {
                clock += Time.deltaTime;
                var stage = fStage.GetValue(ctrl).ToString();
                var at = fAtBat.GetValue(ctrl) as LiveAtBat;
                var queue = ((System.Collections.ICollection)fQueue.GetValue(ctrl)).Count;
                var onBreak = (bool)fBreak.GetValue(ctrl);

                if (!ReferenceEquals(at, prevAtBat) && at != null)
                {
                    windows++;
                    lastProgressAt = clock;
                    if (at.PitchNumberInPa == 1)
                    {
                        heads++;
                        // 打席の頭では対決ビューへ戻っていること（打球のあいだだけ俯瞰へ逃がす契約）。
                        if (!(bool)fDuelOn.GetValue(ctrl))
                        {
                            duelOffAtHead++;
                            Debug.LogError($"[PumpProbe] 打席の頭なのに俯瞰のまま {at.Inning}回" +
                                           $"{(at.IsTop ? "表" : "裏")} {at.BatterOrder}番 {at.BatterName}");
                        }
                        // 打席の頭は打者が待機ポーズであること。前打席の振りが凍ったまま残っていると、
                        // 対決ビューへ戻した瞬間に**球も無いのに振り抜く**（2026-08-01「幽霊空振り」）。
                        var rig = fRig.GetValue(ctrl);
                        var batter = rig == null ? null : fBatter.GetValue(rig);
                        if (batter != null)
                        {
                            var cur = fActorClip.GetValue(batter) as AnimationClip;
                            var idle = fActorIdle.GetValue(batter) as AnimationClip;
                            if (cur != null && idle != null && cur != idle)
                            {
                                ghostSwingAtHead++;
                                Debug.LogError($"[PumpProbe] 打席の頭で打者が待機でない clip='{cur.name}' " +
                                               $"{at.Inning}回{(at.IsTop ? "表" : "裏")} {at.BatterName}");
                            }
                        }
                        // 打席の頭＝この時点で打者が分かっていなければならない（サインを出せない）。
                        if (string.IsNullOrEmpty(at.BatterName) || at.Balls != 0 || at.Strikes != 0)
                        {
                            headWithoutBatter++;
                            Debug.LogError($"[PumpProbe] 打席の頭の窓が壊れている {at.Inning}回" +
                                           $"{(at.IsTop ? "表" : "裏")} 打者='{at.BatterName}' {at.Balls}-{at.Strikes}");
                        }
                    }
                    // カード列の攻守は「映している半回」と一致し続けること。
                    var deck = fDeck.GetValue(ctrl);
                    if (deck != null && stage == "Window")
                    {
                        var deckOffense = (bool)fOffense.GetValue(deck);
                        var shownOffense = at.IsTop == managerIsAway;
                        if (deckOffense != shownOffense)
                        {
                            phaseMismatch++;
                            Debug.LogError($"[PumpProbe] カードの攻守が食い違い {at.Inning}回" +
                                           $"{(at.IsTop ? "表" : "裏")} 映像={(shownOffense ? "攻撃" : "守備")} " +
                                           $"カード={(deckOffense ? "攻撃" : "守備")}");
                        }
                    }
                    prevAtBat = at;
                }

                if (queue > prevQueue) { pitches += queue - prevQueue; lastProgressAt = clock; }
                prevQueue = queue;

                if (onBreak && !prevBreak) { breaks++; lastProgressAt = clock; }
                if (!onBreak && prevBreak) lastProgressAt = clock;
                prevBreak = onBreak;

                // 15秒（×4速なら実時間60秒相当）何も動かなければ進行が止まっている。
                if (!stallReported && clock - lastProgressAt > 15f)
                {
                    stallReported = true;
                    Debug.LogError($"[PumpProbe] 進行が止まった t={clock:F1}s stage={stage} " +
                                   $"break={onBreak} 窓={windows} 球={pitches}");
                }
                yield return null;
            }

            Debug.Log($"[PumpProbe] 結果 t={clock:F1}s 終局={ctrl.IsGameOver} " +
                      $"采配窓={windows}（うち打席の頭={heads}） 描いた球={pitches} イニング間={breaks} " +
                      $"カード食い違い={phaseMismatch} 頭の窓の破損={headWithoutBatter} " +
                      $"頭で俯瞰のまま={duelOffAtHead} 頭で幽霊スイング={ghostSwingAtHead} 停止={stallReported}");
        }

        /// <summary>
        /// エンジン1歩（AdvancePitch）のコストを実測する（UI とは別の進行体で測る＝画面に影響しない）。
        /// 打席を確定する歩だけ打球解決＋タイムライン生成を含むので、最大値がフレーム予算に収まるかを見る。
        /// </summary>
        private void MeasureEngineStepCost()
        {
            var awayRng = new KokoSim.Engine.Core.Xoshiro256Random(timingSeed ^ 0x1234ABCDUL);
            var away = KokoSim.Engine.Nation.StrengthTeamFactory.Create(58, "計測相手", awayRng);
            var homeRng = new KokoSim.Engine.Core.Xoshiro256Random(timingSeed ^ 0x77777777UL);
            var home = KokoSim.Engine.Nation.StrengthTeamFactory.Create(58, "計測自校", homeRng);
            var prog = new MatchProgression(away, home,
                new GameContext { CaptureTimelines = true, CapturePitches = true }, timingSeed);

            // 初回の数歩は JIT・係数YAMLの遅延ロードを含む「冷えた」コストなので分けて出す
            //（実ゲームでは MatchLive を開く前に裏試合が大量に走っており、そこは既に払い終えている）。
            const int Warm = 20;
            var sw = new Stopwatch();
            var steps = 0;
            var paSteps = 0;
            double total = 0, worstWarm = 0, worstPaWarm = 0, totalPa = 0, cold = 0, worstIdx = -1;
            while (true)
            {
                sw.Restart();
                var r = prog.AdvancePitch();
                sw.Stop();
                if (r == AdvancePitchResult.Finished) break;
                var ms = sw.Elapsed.TotalMilliseconds;
                steps++; total += ms;
                if (steps <= Warm) { cold += ms; continue; }
                if (ms > worstWarm) { worstWarm = ms; worstIdx = steps; }
                if (r == AdvancePitchResult.PlateAppearance)
                {
                    paSteps++; totalPa += ms;
                    if (ms > worstPaWarm) worstPaWarm = ms;
                }
            }
            Debug.Log($"[PumpProbe] エンジン1歩の実測: 全{steps}歩 合計{total:F0}ms／" +
                      $"最初の{Warm}歩（冷えた分）{cold:F0}ms／" +
                      $"以降 平均{(total - cold) / System.Math.Max(1, steps - Warm):F3}ms " +
                      $"最大{worstWarm:F2}ms(#{worstIdx})／" +
                      $"うち打席確定 {paSteps}歩 平均{totalPa / System.Math.Max(1, paSteps):F3}ms 最大{worstPaWarm:F2}ms " +
                      $"（1フレーム=16.7ms）");
        }

        private static void EnsureOnlyMatchLive()
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                var want = d.gameObject.name == "MatchLive";
                if (d.gameObject.activeSelf != want) d.gameObject.SetActive(want);
            }
        }
    }
}
#endif
