using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Timeline.Playback;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// サヨナラの場面（issue #1176）。勝ち越しの走者が本塁を踏んだ瞬間、攻撃側（勝った側）の選手が一斉に
    /// 本塁付近へ駆け寄って喜ぶ。守備側はその場でうなだれる。
    ///
    /// <b>表示だけ</b>の芝居で、engine（試合結果・乱数・決定論）には一切触れない。人数は engine の
    /// ベンチ入り（<c>MatchProgression.BenchEntryCount</c>）、位置・時刻・位相はここの固定値。
    ///
    /// 入り方は2つ:
    /// ・守備ビューの再生中（打球のあるサヨナラ）… <see cref="ArmWalkoff"/> で予約し、再生の時計が
    ///   集まり始める時刻に達したら場面へ切り替わる（<see cref="SetFieldTime"/> の頭で見る）。以降は場面の時計で進む。
    ///   本塁打は球が死んだらベンチが飛び出して輪で待ち、打者が本塁を踏んだ瞬間に一斉に喜ぶ（監督決定）。
    ///   それ以外は勝ち越しの走者の本塁到達で飛び出し、着いた者から喜ぶ。
    /// ・再生の無いサヨナラ（押し出し・死球など）… <see cref="BeginWalkoffWithoutPlay"/> で守備の定位置から始める。
    ///
    /// 喜ぶのは「塁上・打席の走者」＋「ベンチから出てくる選手（役者の溜め・最大20人）」で、合計がベンチ入りの人数。
    /// クリップの素の向きはすべて +Z（実測 2026-11-21・<c>DuelMotionSpike.RunBatterTimeoutProbe</c>）。
    /// </summary>
    public sealed partial class DuelStageRig
    {
        // ---- 収録（issue #1176 の割り当て） ----------------------------------------------------
        private const string WoSprintClipName = "AA_Baseball_Defender_Movement_Sprint_Loop";
        /// <summary>喜びの出し方。収録をそのまま流す／立ち姿勢の上でバンザイを手で作る／収録の上半身だけ借りる（下半身は立ち姿勢で止める）。</summary>
        private enum WoCheerKind { Clip, Banzai, UpperBody }

        /// <summary>
        /// 喜ぶ5種類（issue #1195）。勝利の2本はその場で喜ぶ収録そのまま。バンザイは立ち姿勢の収録の上で両腕を骨の後補正で
        /// 上げ下げする。本塁打の喜び（Batter_Home_Run_Celebrating_Type01/02）は収録の中で約9m走り回る動きなので
        /// （実測 2026-11-21: 腰が dx −6.0 / dz +7.0 m 動く）、下半身を立ち姿勢で止めて腕と体幹の動きだけ借りる。
        /// </summary>
        private static readonly (string Clip, WoCheerKind Kind)[] WoCheers =
        {
            ("AA_Baseball_Pitcher_Emotion_Victory_Type01", WoCheerKind.Clip),
            ("AA_Baseball_Pitcher_Emotion_Victory_Type02", WoCheerKind.Clip),
            ("AA_Baseball_Defender_Standing_Idle_Loop_Type01", WoCheerKind.Banzai),
            ("AA_Baseball_Batter_Home_Run_Celebrating_Type01", WoCheerKind.UpperBody),
            ("AA_Baseball_Batter_Home_Run_Celebrating_Type02", WoCheerKind.UpperBody),
        };
        /// <summary>立ち姿勢の控え（下半身を止める基準）に使う収録。</summary>
        private const string WoStandClipName = "AA_Baseball_Defender_Standing_Idle_Loop_Type01";

        /// <summary>バンザイ: 1回の上げ下げ[s]・続けて行う回数・その後の間[s]。腕を上げる角度（真下から。180＝真上）。上げる間に跳ぶ高さ[m]。</summary>
        private const float WoBanzaiCycleSeconds = 0.9f, WoBanzaiRestSeconds = 0.6f, WoBanzaiArmDeg = 165f, WoBanzaiHopM = 0.14f;
        private const int WoBanzaiCount = 2;

        private const string WoPitcherDefeatedClipName = "AA_Baseball_Pitcher_Emotion_Defeated_Type01";
        private const string WoPitcherDefeated2ClipName = "AA_Baseball_Pitcher_Emotion_Defeated_Type02";
        private const string WoCatcherDefeatedClipName = "AA_Baseball_Catcher_Emotion_Defeated";

        /// <summary>全力疾走の収録の前進量[m/s]と進行方向のずれ[度]（<see cref="RunnerLadder"/> と同じ実測）。</summary>
        private const float WoSprintVClip = 5.11f, WoSprintMoveDir = -12.9f;

        /// <summary>ベンチから出てくる役者の上限（ベンチ入りの上限＝AiUniformNumbers.BenchSize）。</summary>
        public const int WalkoffPoolMax = 20;

        /// <summary>人だかりの中心（本塁の少し前。ステージ座標 m）。勝ち越しの走者（本塁打なら打者）がここに立つ。</summary>
        public static Vector2 WalkoffCenter = new Vector2(0.0f, 3.0f);
        /// <summary>輪の半径[m]と、その輪に並べる人数（内から）。0.9m以上空くように決めてある。</summary>
        private static readonly float[] WoRingRadius = { 1.15f, 2.0f, 2.85f, 3.7f };
        private static readonly int[] WoRingCapacity = { 6, 11, 16, 21 };

        /// <summary>ベンチ前の出発点（一塁側。三塁側は x を反転）。ここから列に並んで散らばる。</summary>
        private static readonly Vector2 WoBenchFront1B = new Vector2(21.0f, 3.0f);

        /// <summary>走る速さ[m/s]。遠い者ほど速い（近い 4.8 〜 遠い 6.4）。</summary>
        private const float WoRunSpeedNear = 4.8f, WoRunSpeedFar = 6.4f;
        /// <summary>止まる前に落とす距離[m]と速さ[m/s]（走りの収録で減速する）。</summary>
        private const float WoBrakeM = 1.6f, WoBrakeSpeed = 3.0f;
        /// <summary>本塁打で輪に着いて待っている者が、打者の生還から喜び出すまでのずらし[s]の上限。</summary>
        private const float WoCheerStaggerMax = 0.35f;
        /// <summary>球が死んでからベンチが飛び出すまで[s]（本塁打）。</summary>
        private const float WoHrBenchDelay = 0.4f;

        /// <summary>
        /// カメラ（ステージ座標の位置と注視点・縦に収めたい実寸[m]）。本塁の後ろの高い所から、内野と人だかりを一枚に収める
        /// 引きの絵（寄りすぎない＝3d-character.md）。構図は監督が録画の3案から選んだ（issue #1176）。
        /// </summary>
        private static readonly Vector3 WoCamPos = new Vector3(-3.0f, 9.0f, -12.0f);
        private static readonly Vector3 WoCamLook = new Vector3(0.0f, -0.5f, 4.0f);
        private const float WoCamFrameH = 13.0f;

        private enum WoLeg { Playback, Wait, Run, Brake, Stand, Cheer }

        private sealed class WoMember
        {
            public Actor A;
            public int RunnerIdx = -1;      // 守備ビューの走者（-1＝ベンチから出る役者）
            public bool Winner;             // 人だかりの中心（勝ち越しの走者／本塁打の打者）
            public float TakeoverT = float.PositiveInfinity;   // 走者を再生から引き取る時刻（場面の時計）
            public Vector2 Pos, Slot;
            public float Delay, Phase, Speed, Yaw, CheerAt = -1f;
            public int CheerClip;           // WoCheers の添字
            public float CheerStartT;       // 喜び始めた時刻（場面の時計）
            public float HopM;              // いまの跳ね上がり[m]（ポーズを書き直したコマで決め、毎コマ足す）
            public bool PoseDirty;          // 喜びの後補正をまだ当てていない（収録を替えた直後）
            public WoLeg Leg;
        }

        /// <summary>立ち姿勢の下半身（腰・両脚）。喜びの上半身だけ借りる収録で下半身をここへ戻す。</summary>
        private sealed class WoStandPose
        {
            public Transform[] Bones;
            public Quaternion[] Rots;
            public Vector3 HipsPos;
        }

        private static readonly HumanBodyBones[] WoLowerBones =
        {
            HumanBodyBones.Hips,
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes,
            HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, HumanBodyBones.RightToes,
        };

        private readonly Dictionary<Actor, WoStandPose> _woStand = new();
        private bool _woPosedThisTick;

        private readonly List<Actor> _woPool = new();
        private readonly List<WoMember> _woMembers = new();
        private bool _woArmed, _woActive, _woNoPlay;
        private bool _woHomeRun;
        private float _woT, _woTGather, _woTCelebrate;
        private float _woAllCheerT = -1f;   // 人だかりができた（喜ぶ人が WoCrowdFormedShare に達した）時刻（場面の時計）
        private const float WoCrowdFormedShare = 0.8f;
        private int _woCount;
        private bool _woBench1B;
        private DuelActorSnapshot _woPitcherSnap, _woCatcherSnap;
        private bool _woBatterWasActive, _woBatWasActive;

        /// <summary>サヨナラの場面を流しているか（予約中は含まない）。</summary>
        public bool WalkoffSceneActive => _woActive;
        /// <summary>サヨナラの場面を予約しているか、流しているか。</summary>
        public bool WalkoffSceneArmedOrActive => _woArmed || _woActive;

        /// <summary>
        /// 人だかりができてから（8割が輪に着いて喜び始めてから）の秒数（場面の時計）。まだなら負。「ゲームセット」はこれで待つ
        /// （勝ち越しの生還から数えると、ベンチから走ってくる途中で掲がる。全員を待つと最後の数人の分だけ喜びを長く映し、
        /// 動きが2種類しかないのが見えてしまう＝残りはゲームセットの文字の裏で合流させる。2026-11-21 録画）。
        /// </summary>
        public float WalkoffCelebrateSeconds => _woActive && _woAllCheerT >= 0f ? _woT - _woAllCheerT : -1f;

        /// <summary>
        /// 守備ビューの再生中のサヨナラを予約する（<see cref="BeginFieldPlay"/> の後に呼ぶ）。
        /// <paramref name="tCelebrate"/>＝一斉に喜び始める時刻（プレーの時計。本塁打は打者の生還、それ以外は勝ち越しの走者の生還）。
        /// <paramref name="benchCount"/>＝攻撃側のベンチ入りの人数。<paramref name="bench1B"/>＝攻撃側のベンチが一塁側か。
        /// 組めない（素体が無い）ときは false＝呼び出し側は従来どおり終局へ進む。
        /// </summary>
        public bool ArmWalkoff(float tCelebrate, int benchCount, bool bench1B)
        {
            if (!Available || _fieldPlay == null || _woActive || benchCount <= 0) return false;
            if (!EnsureWalkoffPool()) return false;
            _woHomeRun = IsHomeRun(_fieldPlay);
            _woTCelebrate = tCelebrate;
            // 本塁打は球が死んだら飛び出す（打者の一周の間に輪を作って待つ）。それ以外は生還の瞬間
            _woTGather = _woHomeRun && !float.IsPositiveInfinity(_hrDeadT)
                ? Mathf.Min(_hrDeadT + WoHrBenchDelay, tCelebrate)
                : tCelebrate;
            _woCount = Mathf.Min(benchCount, WalkoffPoolMax + MaxRunners);
            _woBench1B = bench1B;
            _woNoPlay = false;
            _woArmed = true;
            return true;
        }

        /// <summary>
        /// 再生の無いサヨナラ（押し出し・死球など）を、守備の定位置から始める。<paramref name="onBase"/>＝
        /// 決着の前に塁にいた走者（一・二・三塁）。打者も打席から加わる。
        /// </summary>
        public bool BeginWalkoffWithoutPlay(int benchCount, bool[] onBase, bool bench1B)
        {
            if (!Available || _woActive || _fieldPlay != null || benchCount <= 0) return false;
            if (!EnsureWalkoffPool()) return false;
            EnsureFieldCast();
            if (_fieldCast.Count == 0) return false;
            _woPitcherSnap = Snapshot(_pitcher);
            _woCatcherSnap = Snapshot(_catcher);
            PlaceFieldCastAtDefault();
            SetFieldCastVisible(true);
            _woBatterWasActive = _batter?.Go != null && _batter.Go.activeSelf;
            if (_batter?.Go != null) _batter.Go.SetActive(false);   // 打者はベンチ入りの1人として役者の溜めから出す
            _woBatWasActive = _bat != null && _bat.activeSelf;
            if (_bat != null) _bat.SetActive(false);

            _woHomeRun = false;
            _woCount = Mathf.Min(benchCount, WalkoffPoolMax);
            _woBench1B = bench1B;
            _woNoPlay = true;
            _woTGather = _woTCelebrate = 0f;

            // 塁上の走者と打者は、いる場所から走り出す（押し出しで三塁から生還した走者が中心）
            var starts = new List<(Vector2 Pos, bool Winner)>();
            if (onBase != null && onBase.Length >= 3 && onBase[2]) starts.Add((ToV2(FieldDiagramGeometry.Third), true));
            if (onBase != null && onBase.Length >= 2 && onBase[1]) starts.Add((ToV2(FieldDiagramGeometry.Second), false));
            if (onBase != null && onBase.Length >= 1 && onBase[0]) starts.Add((ToV2(FieldDiagramGeometry.First), false));
            starts.Add((new Vector2(_cal.BatterX * BatterSideSign, _cal.BatterZ), starts.Count == 0));
            BeginWalkoffScene(0f, starts);
            return true;
        }

        private static Vector2 ToV2((double X, double Y) p) => new Vector2((float)p.X, (float)p.Y);

        /// <summary>ベンチから出てくる役者（攻撃側の色・帽子）。初回だけ作り、以降は塗り直して使い回す。</summary>
        private bool EnsureWalkoffPool()
        {
            var sprint = LoadClip(WoSprintClipName);
            if (sprint == null || LoadClip(WoCheers[0].Clip) == null) return false;
            if (_woPool.Count == 0)
            {
                var vendor = LoadVendorBody();
                bool useVendor = vendor != null;
                var bodyPrefab = useVendor ? vendor : LoadBody();
                int layer = LayerMask.NameToLayer(LayerName);
                if (bodyPrefab == null || layer < 0) return false;
                for (int i = 0; i < WalkoffPoolMax; i++)
                {
                    var a = SpawnActor(bodyPrefab, sprint, Vector3.zero, 0f, _offenseColors, 0f, layer, useVendor,
                        rootMotion: false, tightBounds: true);
                    a.Go.name = "walkoff-" + i;
                    a.IdleClip = sprint;
                    CaptureWoStand(a, sprint);
                    StepActor(a, 0.001f);
                    GroundFix(a);
                    if (!useVendor) AttachGear(a, GearKind.Cap, Vector3.zero, _offenseColors.Cap, layer, _offenseColors.CapVisor);
                    CollectTeamMats(a, _offenseColors.Cap);
                    a.Go.SetActive(false);
                    a.Enabled = false;
                    _woPool.Add(a);
                }
            }
            foreach (var a in _woPool) Repaint(a, _offenseColors);   // 試合・攻守で色が変わる（喜ぶのは攻撃側）
            return true;
        }

        /// <summary>立ち姿勢の下半身を控える（収録の頭の1コマ）。控えたあとは走りの収録へ戻す。</summary>
        private void CaptureWoStand(Actor a, AnimationClip back)
        {
            var stand = LoadClip(WoStandClipName);
            if (stand == null || a.Animator == null) return;
            SetActorClip(a, stand, 0f, 0f);
            StepActor(a, 0.001f);
            var bones = new List<Transform>();
            foreach (var hb in WoLowerBones)
            {
                var t = a.Animator.GetBoneTransform(hb);
                if (t != null) bones.Add(t);
            }
            var hips = a.Animator.GetBoneTransform(HumanBodyBones.Hips);
            if (hips != null && bones.Count > 0)
            {
                var pose = new WoStandPose { Bones = bones.ToArray(), Rots = new Quaternion[bones.Count], HipsPos = hips.localPosition };
                for (int i = 0; i < bones.Count; i++) pose.Rots[i] = bones[i].localRotation;
                _woStand[a] = pose;
            }
            SetActorClip(a, back, 0f, 0f);
        }

        /// <summary>予約した場面へ入る（守備ビューの再生の時計が集まり始める時刻に達した）。</summary>
        private void BeginArmedWalkoff(float t)
        {
            _woArmed = false;
            var starts = new List<(Vector2 Pos, bool Winner)>();
            BeginWalkoffScene(t, starts);
        }

        /// <summary>予約中の場面を今すぐ始める（再生を最後まで待たずに終局したとき）。</summary>
        public void StartArmedWalkoffNow()
        {
            if (_woArmed && !_woActive && _fieldPlay != null) BeginArmedWalkoff(_fieldT);
        }

        /// <summary>
        /// 場面を組む。<paramref name="extraStarts"/>＝再生の無いときの走者（塁・打席から出る役者）。
        /// 再生のあるときは守備ビューの走者をそのまま使う。
        /// </summary>
        private void BeginWalkoffScene(float t, List<(Vector2 Pos, bool Winner)> extraStarts)
        {
            _woActive = true;
            _woT = t;
            _woAllCheerT = -1f;
            _woMembers.Clear();
            int pool = 0;

            // ① 守備ビューの走者（本塁打は生還するまで再生のまま走らせる）
            int winnerRunner = -1;
            if (!_woNoPlay && _fieldPlay != null)
            {
                winnerRunner = WinnerRunnerIndex(_fieldPlay, _woTCelebrate, _woHomeRun);
                for (int i = 0; i < _fieldPlay.Runners.Count && i < _runnerCast.Count; i++)
                {
                    var a = _runnerCast[i];
                    if (a?.Go == null || !a.Go.activeSelf) continue;
                    if (_woMembers.Count >= _woCount) break;
                    var r = _fieldPlay.Runners[i];
                    float take = _woTCelebrate;
                    // 本塁打の一周で先に生還した走者は、本塁を駆け抜けたら輪へ向かう
                    if (RunnerFinal(r, out var t1, out var outAtEnd, out _, out _, out _) && !outAtEnd && EndsAtHome(r))
                        take = Mathf.Min(take, t1 + ArriveFollowSeconds * 0.5f);
                    _woMembers.Add(new WoMember
                    {
                        A = a, RunnerIdx = i, Winner = i == winnerRunner, TakeoverT = Mathf.Max(t, take),
                        Leg = WoLeg.Playback,
                    });
                }
            }
            // ② 再生の無いときの走者・打者（役者の溜めから）
            foreach (var s in extraStarts)
            {
                if (_woMembers.Count >= _woCount || pool >= _woPool.Count) break;
                var a = _woPool[pool++];
                _woMembers.Add(new WoMember { A = a, Pos = s.Pos, Winner = s.Winner, Leg = WoLeg.Wait, Delay = 0.1f * _woMembers.Count });
            }
            // ③ ベンチから出てくる選手（残りの人数ぶん）。列に並んだ位置から、少しずつずらして飛び出す
            float sx = _woBench1B ? 1f : -1f;
            int k = 0;
            while (_woMembers.Count < _woCount && pool < _woPool.Count)
            {
                var a = _woPool[pool++];
                var start = new Vector2((WoBenchFront1B.x + (k % 5) * 1.1f) * sx, WoBenchFront1B.y + (k / 5) * 1.3f + (k % 2) * 0.4f);
                _woMembers.Add(new WoMember { A = a, Pos = start, Leg = WoLeg.Wait, Delay = WoBenchDelay(k) });
                k++;
            }
            // 使わない役者は伏せる
            for (int i = pool; i < _woPool.Count; i++)
            {
                _woPool[i].Go.SetActive(false);
                _woPool[i].Enabled = false;
            }
            // 中心に立つ人がいなければ（走者の特定に失敗）、最初の走者・最初の人を中心にする
            if (!_woMembers.Exists(m => m.Winner) && _woMembers.Count > 0) _woMembers[0].Winner = true;

            AssignWalkoffSlots();

            // 役者の溜めから出す人を立たせる（走り出すまでは走りの頭の姿で止めておく）
            var sprint = LoadClip(WoSprintClipName);
            for (int i = 0; i < _woMembers.Count; i++)
            {
                var m = _woMembers[i];
                m.Phase = Mathf.Repeat(i * 0.37f, 1f);
                if (m.RunnerIdx >= 0) continue;
                var a = m.A;
                a.Go.SetActive(true);
                a.Enabled = true;
                a.HoldAtEnd = false;
                a.Holding = false;
                a.HoldRemaining = 0f;
                a.DefaultEndAt = 0f;
                a.IdleEndAt = 0f;
                SetRootMotion(a, false);
                SetActorClip(a, sprint, m.Phase * sprint.length, 0f);
                a.IdleClip = sprint;
                a.Speed = 0f;
                m.Yaw = YawToward(m.Pos, m.Slot) - WoSprintMoveDir;
                PlaceFielder(a, m.Pos.x, m.Pos.y, m.Yaw);
            }

            DejectDefense();
            HideBall();
            if (_errorMark != null) _errorMark.SetActive(false);
            FramePreview(WoCamPos, WoCamLook, WoCamFrameH);
        }

        /// <summary>ベンチから飛び出す遅れ[s]（列の前ほど早い。固定のずらし＝乱数を引かない）。</summary>
        private static float WoBenchDelay(int k) => 0.15f * (k % 5) + 0.25f * (k / 5) + (k % 3) * 0.05f;

        private static bool EndsAtHome(PlaybackRunner r)
        {
            if (r?.Segs == null || r.Segs.Count == 0) return false;
            var to = r.Segs[r.Segs.Count - 1].To;
            return to.X * to.X + to.Y * to.Y < 4.0;
        }

        /// <summary>人だかりの中心に立つ走者（本塁打は打者、それ以外は勝ち越しの時刻に本塁へ着いた走者）。</summary>
        private static int WinnerRunnerIndex(PlaybackPlay play, float tCelebrate, bool homeRun)
        {
            int best = -1;
            float bestD = float.PositiveInfinity;
            for (int i = 0; i < play.Runners.Count; i++)
            {
                var r = play.Runners[i];
                if (homeRun) { if (r.Label == "打") return i; continue; }
                if (!RunnerFinal(r, out var t1, out var outAtEnd, out _, out _, out _) || outAtEnd || !EndsAtHome(r)) continue;
                float d = Mathf.Abs(t1 - tCelebrate);
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        /// <summary>
        /// 立ち位置を決める。中心に勝ち越しの走者、その周りに輪（内から埋める）。各輪の場所は、その人が来る方向に
        /// 近い場所を割り当てる＝走ってくる線が交差しにくい。人数が変わっても同じ規則で決まる。
        /// </summary>
        private void AssignWalkoffSlots()
        {
            var slots = new List<Vector2>();
            var slotCheer = new List<int>();
            int need = _woMembers.Count - 1;
            for (int ring = 0; ring < WoRingRadius.Length && slots.Count < need; ring++)
            {
                int n = Mathf.Min(WoRingCapacity[ring], need - slots.Count);
                float off = ring * 17f;   // 輪ごとに角度をずらす（前後の人が重ならない）
                for (int j = 0; j < n; j++)
                {
                    float deg = off + 360f * j / n;
                    float r = deg * Mathf.Deg2Rad;
                    slots.Add(WalkoffCenter + new Vector2(Mathf.Sin(r), Mathf.Cos(r)) * WoRingRadius[ring]);
                    slotCheer.Add(WoCheerForSlot(ring, j, n));
                }
            }
            var free = new List<WoMember>();
            foreach (var m in _woMembers)
            {
                if (m.Winner) m.Slot = WalkoffCenter;
                else free.Add(m);
            }
            // 来る方向（いまの位置。走者は再生の位置）で近い場所を貪欲に割り当てる
            for (int si = 0; si < slots.Count; si++)
            {
                if (free.Count == 0) break;
                var slot = slots[si];
                float sa = Mathf.Atan2(slot.x - WalkoffCenter.x, slot.y - WalkoffCenter.y);
                int bi = 0;
                float bd = float.PositiveInfinity;
                for (int i = 0; i < free.Count; i++)
                {
                    var p = MemberPos(free[i]);
                    float ma = Mathf.Atan2(p.x - WalkoffCenter.x, p.y - WalkoffCenter.y);
                    float d = Mathf.Abs(Mathf.DeltaAngle(sa * Mathf.Rad2Deg, ma * Mathf.Rad2Deg));
                    if (d < bd) { bd = d; bi = i; }
                }
                free[bi].Slot = slot;
                free[bi].CheerClip = slotCheer[si];
                free.RemoveAt(bi);
            }
        }

        /// <summary>
        /// 輪の j 番目（全 n 人）が喜ぶ種類。固定のずらし（乱数なし）で、輪の隣どうしは必ず別の種類にする
        /// （3つ飛びで巡る。輪を一周して戻る最後の人だけ最初の人と重なるので1つ送る）。輪ごとに出だしをずらす。
        /// </summary>
        private static int WoCheerForSlot(int ring, int j, int n)
        {
            int k = WoCheers.Length;
            int kind = (ring * 2 + j * 3) % k;
            if (n > 1 && j == n - 1 && kind == (ring * 2) % k) kind = (kind + 1) % k;
            return kind;
        }

        private Vector2 MemberPos(WoMember m)
        {
            if (m.RunnerIdx >= 0 && m.A?.Go != null)
            {
                var p = StagePos(m.A);
                return new Vector2(p.x, p.z);
            }
            return m.Pos;
        }

        /// <summary>守備側をその場でうなだれさせる（投手・捕手は落胆の収録、野手は持ち場で立ち尽くす）。</summary>
        private void DejectDefense()
        {
            for (int i = 0; i < AllFieldPositions.Length; i++)
            {
                var pos = AllFieldPositions[i];
                var a = FielderActor(pos);
                if (a == null || a.Go == null || !a.Go.activeInHierarchy) continue;
                var stand = LoadClip(PostHomeRunIdleClipNames[PostHrClipIndex[i]]);
                AnimationClip clip = stand;
                float phase = PostHrPhase[i];
                if (pos == FieldPosition.Pitcher) { clip = LoadClip(WoPitcherDefeatedClipName) ?? stand; phase = 0f; }
                else if (pos == FieldPosition.Catcher) { clip = LoadClip(WoCatcherDefeatedClipName) ?? stand; phase = 0f; }
                if (clip == null) continue;
                a.HoldAtEnd = false;
                a.Holding = false;
                a.HoldRemaining = 0f;
                a.DefaultEndAt = 0f;
                a.IdleEndAt = 0f;
                a.SpeedSwitchAt = -1f;
                SetRootMotion(a, false);
                CrossfadeActorToClip(a, clip, phase * clip.length, 0f, 0.4f);
                a.IdleClip = stand ?? clip;   // 落胆の収録が終わったら、その場で立ち尽くす
                a.Speed = 1f;
                var p = StagePos(a);
                PlaceFielder(a, p.x, p.z, StageYaw(a));
            }
        }

        /// <summary>場面中の <see cref="Tick"/>。ポーズだけ進め、置き場所と向きは場面が決める（伝令の場面と同じ作り）。</summary>
        private void TickWalkoffScene(float deltaTime)
        {
            AdvanceRattledFx(deltaTime);   // 再生の無いサヨナラ（押し出し等）で動揺の渦巻きが凍らない
            _acc += deltaTime;
            float step = 1f / PoseFps;
            _woPosedThisTick = false;
            while (_acc >= step)
            {
                _acc -= step;
                foreach (var a in _actors) StepActor(a, step);
                _poseGen++;
                _woPosedThisTick = true;
            }
            _woT += deltaTime;
            StepWalkoffScene(deltaTime);
        }

        private void StepWalkoffScene(float dt)
        {
            var sprint = LoadClip(WoSprintClipName);
            var brake = LoadClip(TimeoutRunClipName);
            if (_woAllCheerT < 0f && _woMembers.Count > 0)
            {
                int cheering = 0;
                for (int i = 0; i < _woMembers.Count; i++) if (_woMembers[i].Leg == WoLeg.Cheer) cheering++;
                if (cheering >= Mathf.CeilToInt(_woMembers.Count * WoCrowdFormedShare)) _woAllCheerT = _woT;
            }
            foreach (var m in _woMembers)
            {
                var a = m.A;
                if (a == null || a.Go == null) continue;
                switch (m.Leg)
                {
                    case WoLeg.Playback:
                        // 生還するまで（本塁打の一周）は守備ビューの再生のまま走らせる
                        if (_woT < m.TakeoverT && _fieldPlay != null && m.RunnerIdx < _runnerMotion.Count)
                        {
                            StepRunnerMotion(m.RunnerIdx, _woT, dt);
                            continue;
                        }
                        // 引き取る: いる場所から輪へ走り出す
                        var p = StagePos(a);
                        m.Pos = new Vector2(p.x, p.z);
                        m.Yaw = StageYaw(a);
                        a.HoldAtEnd = false;
                        a.Holding = false;
                        a.HoldRemaining = 0f;
                        SetRootMotion(a, false);
                        m.Leg = WoLeg.Wait;
                        m.Delay = 0f;
                        StartWoRun(m, sprint);
                        break;
                    case WoLeg.Wait:
                        a.Speed = 0f;
                        if (_woT - _woTGather >= m.Delay) StartWoRun(m, sprint);
                        break;
                    case WoLeg.Run:
                    case WoLeg.Brake:
                        StepWoMove(m, dt, brake);
                        break;
                    case WoLeg.Stand:
                        // 本塁打で輪に着いて待っている: 打者の生還で一斉に（少しずつずれて）喜ぶ
                        FaceWo(m, dt);
                        if (_woT >= _woTCelebrate)
                        {
                            if (m.CheerAt < 0f) m.CheerAt = _woTCelebrate + Mathf.Repeat(m.Phase * 3.1f, 1f) * WoCheerStaggerMax;
                            if (_woT >= m.CheerAt) StartWoCheer(m);
                        }
                        break;
                    case WoLeg.Cheer:
                        // 後補正はポーズを書き直したコマにだけ当てる（評価の間は骨がそのまま残る＝二度当てしない）
                        if (m.PoseDirty || (_woPosedThisTick && a.PoseAdvanced)) ApplyWoCheerPose(m);
                        FaceWo(m, dt);
                        break;
                }
            }
        }

        private void StartWoRun(WoMember m, AnimationClip sprint)
        {
            var a = m.A;
            float dist = Vector2.Distance(m.Pos, m.Slot);
            m.Speed = Mathf.Lerp(WoRunSpeedNear, WoRunSpeedFar, Mathf.Clamp01(dist / 28f));
            if (dist <= WoBrakeM)
            {
                m.Leg = WoLeg.Brake;
                var brake = LoadClip(TimeoutRunClipName);
                CrossfadeActorToClip(a, brake, m.Phase * brake.length, 0f, 0.25f);
                a.IdleClip = brake;
            }
            else
            {
                m.Leg = WoLeg.Run;
                CrossfadeActorToClip(a, sprint, m.Phase * sprint.length, 0f, 0.2f);
                a.IdleClip = sprint;
            }
            a.Speed = 1f;
        }

        private void StepWoMove(WoMember m, float dt, AnimationClip brake)
        {
            var a = m.A;
            bool braking = m.Leg == WoLeg.Brake;
            float speed = braking ? WoBrakeSpeed : m.Speed;
            var to = m.Slot - m.Pos;
            float dist = to.magnitude;
            float stepM = speed * dt;
            if (!braking && dist <= WoBrakeM)
            {
                m.Leg = WoLeg.Brake;
                CrossfadeActorToClip(a, brake, m.Phase * brake.length, 0f, 0.25f);
                a.IdleClip = brake;
                braking = true;
            }
            if (dist <= stepM || dist < 1e-4f)
            {
                m.Pos = m.Slot;
                PlaceFielder(a, m.Pos.x, m.Pos.y, m.Yaw);
                // 着いた: 喜び始めていれば喜ぶ、本塁打の一周の途中なら輪で待つ
                if (_woT >= _woTCelebrate) StartWoCheer(m);
                else
                {
                    m.Leg = WoLeg.Stand;
                    var stand = LoadClip(PostHomeRunIdleClipNames[(m.CheerClip + 1) % PostHomeRunIdleClipNames.Length]);
                    if (stand != null)
                    {
                        CrossfadeActorToClip(a, stand, m.Phase * stand.length, 0f, 0.3f);
                        a.IdleClip = stand;
                    }
                    a.Speed = 1f;
                }
                return;
            }
            m.Pos += to / dist * stepM;
            float moveDir = braking ? TimeoutRunMoveDir : WoSprintMoveDir;
            float vClip = braking ? TimeoutRunVClip : WoSprintVClip;
            m.Yaw = Mathf.MoveTowardsAngle(m.Yaw, YawToward(Vector2.zero, to) - moveDir, 540f * dt);
            a.Speed = Mathf.Clamp(speed / vClip, LocoSpeedMin, LocoSpeedMax);
            PlaceFielder(a, m.Pos.x, m.Pos.y, m.Yaw);
        }

        private void StartWoCheer(WoMember m)
        {
            m.Leg = WoLeg.Cheer;
            var clip = LoadClip(WoCheers[m.CheerClip].Clip);
            if (clip == null) return;
            m.CheerStartT = _woT;
            m.HopM = 0f;
            m.PoseDirty = WoCheers[m.CheerClip].Kind != WoCheerKind.Clip;
            CrossfadeActorToClip(m.A, clip, m.Phase * clip.length, 0f, 0.25f);
            m.A.IdleClip = clip;   // 終わったら頭からもう一度（喜び続ける）
            m.A.Speed = 1f;
        }

        // ---- 喜びの後補正（バンザイ・本塁打の喜びの上半身） ---------------------------------------

        /// <summary>喜びの種類に応じた後補正を骨へ足す（ポーズを評価した直後だけ）。</summary>
        private void ApplyWoCheerPose(WoMember m)
        {
            m.PoseDirty = false;
            if (m.A?.Animator == null) return;
            switch (WoCheers[m.CheerClip].Kind)
            {
                case WoCheerKind.Banzai: ApplyWoBanzai(m); break;
                case WoCheerKind.UpperBody: ApplyWoUpperBody(m); break;
            }
        }

        /// <summary>
        /// バンザイ。立ち姿勢の収録の上で、両腕を体の横から真上へ上げ下げする（正面の面内で振る＝腕が体を突き抜けない）。
        /// 上げるのに合わせて小さく跳ねる。骨は親ローカルの FromTo（<see cref="RotateBoneWorld"/>）で回すのでミラー下でも効く。
        /// 長い間（<see cref="WoBanzaiRestSeconds"/>）をはさんで繰り返し、人ごとに位相をずらす。
        /// </summary>
        private void ApplyWoBanzai(WoMember m)
        {
            float cycle = WoBanzaiCycleSeconds * WoBanzaiCount + WoBanzaiRestSeconds;
            float t = Mathf.Repeat(_woT - m.CheerStartT + m.Phase * cycle, cycle);
            float w = 0f, hop = 0f;
            if (t < WoBanzaiCycleSeconds * WoBanzaiCount)
            {
                float c = Mathf.Repeat(t, WoBanzaiCycleSeconds) / WoBanzaiCycleSeconds;   // 0..1（前半で上げ、後半で下げる）
                float u = c < 0.5f ? c * 2f : (1f - c) * 2f;
                w = u * u * (3f - 2f * u);
                if (c < 0.5f) hop = Mathf.Sin(c * 2f * Mathf.PI) * WoBanzaiHopM;
            }
            m.HopM = hop;
            if (w <= 1e-3f) return;

            var an = m.A.Animator;
            var right = ChestRight(an, m.A);
            float blend = Mathf.Clamp01(w / 0.12f);
            float theta = w * WoBanzaiArmDeg * Mathf.Deg2Rad;
            for (int side = 0; side < 2; side++)
            {
                var up = an.GetBoneTransform(side == 0 ? HumanBodyBones.RightUpperArm : HumanBodyBones.LeftUpperArm);
                var lo = an.GetBoneTransform(side == 0 ? HumanBodyBones.RightLowerArm : HumanBodyBones.LeftLowerArm);
                var hand = an.GetBoneTransform(side == 0 ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
                if (up == null || lo == null || hand == null) continue;
                var outward = side == 0 ? right : -right;
                var target = outward * Mathf.Sin(theta) - Vector3.up * Mathf.Cos(theta);
                var cur = (lo.position - up.position).normalized;
                var want = Vector3.Slerp(cur, target, blend);
                RotateBoneWorld(up, cur, want);
                // 肘は伸ばす（腕が回った後の前腕の向きを、上腕と同じ向きへ寄せる）
                var cur2 = (hand.position - lo.position).normalized;
                RotateBoneWorld(lo, cur2, Vector3.Slerp(cur2, want, blend * 0.85f));
            }
        }

        /// <summary>
        /// 収録の上半身だけ借りる。腰と両脚は立ち姿勢のまま止め、背骨は収録のときの世界の向きを保つ
        /// （腰の向きを立ち姿勢へ戻したぶん、背骨のローカルを逆に回す）＝腕と体幹の動きだけが残る。
        /// </summary>
        private void ApplyWoUpperBody(WoMember m)
        {
            if (!_woStand.TryGetValue(m.A, out var st)) return;
            var an = m.A.Animator;
            var hips = an.GetBoneTransform(HumanBodyBones.Hips);
            var spine = an.GetBoneTransform(HumanBodyBones.Spine);
            if (hips == null) return;
            if (spine != null && spine.parent == hips)
                spine.localRotation = Quaternion.Inverse(st.Rots[0]) * hips.localRotation * spine.localRotation;
            for (int i = 0; i < st.Bones.Length; i++) st.Bones[i].localRotation = st.Rots[i];
            hips.localPosition = st.HipsPos;
        }

        /// <summary>輪の人は中心へ、中心の人はベンチの側（駆け寄ってくる仲間）へ向き直る。</summary>
        private void FaceWo(WoMember m, float dt)
        {
            var target = m.Winner
                ? WalkoffCenter + new Vector2(_woBench1B ? 5f : -5f, 2f)
                : WalkoffCenter;
            float want = YawToward(m.Pos, target);
            m.Yaw = Mathf.MoveTowardsAngle(m.Yaw, want, 260f * dt);
            PlaceFielder(m.A, m.Pos.x, m.Pos.y, m.Yaw);
            if (m.HopM > 0f)   // 接地の補正（PlaceFielder）のあとに足す＝跳ねが接地に吸われない
            {
                var lp = m.A.Go.transform.localPosition;
                m.A.Go.transform.localPosition = new Vector3(lp.x, lp.y + m.HopM, lp.z);
            }
        }

        /// <summary>
        /// 場面を畳む（新しい試合・画面を離れる）。役者の溜めを伏せ、守備ビューの再生も畳む。
        /// 走っていなければ何もしない。
        /// </summary>
        public void EndWalkoffScene()
        {
            if (!_woArmed && !_woActive) return;
            bool wasActive = _woActive;
            _woArmed = false;
            _woActive = false;
            _woMembers.Clear();
            foreach (var a in _woPool)
            {
                if (a?.Go == null) continue;
                a.Go.SetActive(false);
                a.Enabled = false;
            }
            if (!wasActive) return;
            if (_fieldPlay != null)
            {
                EndFieldPlay();
                return;
            }
            if (_woNoPlay)
            {
                Restore(_pitcher, _woPitcherSnap);
                Restore(_catcher, _woCatcherSnap);
                foreach (var kv in _fieldCast)
                {
                    var idle = IdleClipFor(kv.Key);
                    if (idle == null || kv.Value == null) continue;
                    SetActorClip(kv.Value, idle, 0f, 0f);
                    kv.Value.IdleClip = idle;
                    kv.Value.Speed = 1f;
                }
                PlaceFieldCastAtDefault();
                SetFieldCastVisible(_fieldCastVisible);
                if (_batter?.Go != null) _batter.Go.SetActive(_woBatterWasActive);
                if (_bat != null) _bat.SetActive(_woBatWasActive);
                ClearPreviewFrame();
                ApplyCalibration(_cal);
            }
        }

        /// <summary>撮影・検査用: 場面に出ている（見えている）喜ぶ側の人数。</summary>
        internal int WalkoffCelebrantsForCapture
        {
            get
            {
                int n = 0;
                foreach (var m in _woMembers) if (m.A?.Go != null && m.A.Go.activeInHierarchy) n++;
                return n;
            }
        }

        /// <summary>撮影・検査用: 予約・開始時に渡されたベンチ入りの人数。</summary>
        internal int WalkoffExpectedForCapture => _woCount;
    }
}
