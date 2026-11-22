using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 守備伝令の場面（issue #1174）。伝令を送ったら、次の球の前に「マウンドに集まって話す」絵を差し込む。
    ///
    /// <b>表示だけ</b>の芝居で、engine（効き目・乱数・決定論）には一切触れない。位置も時刻もここで決める
    /// （engine のタイムラインを持たない唯一の3D場面）。乱数は引かない＝位相・遅れは役ごとの固定値。
    ///
    /// 流れ: 球審がタイム → 捕手と内野4人がマウンドへ・伝令がベンチから走ってくる → 投手を囲んで話す
    /// （投手がうなずく）→ 球審がタイムを解く → 散る。散り始めてしばらくしたら畳む（定位置へ置き直す）。
    /// 終わった後は投手・捕手を控えた状態へ戻し、内野4人は定位置へ戻して伏せる（守備ビューの
    /// <see cref="EndFieldPlay"/> と同じ戻し方。基準位置の汚染を残さない）。
    ///
    /// クリップの素の向きはすべて +Z（実測 2026-11-21・<c>DuelMotionSpike.RunFielderFacingProbe</c>。
    /// 投手の待機・うなずきも投球クリップと違って +Z）＝野手と同じ yaw の式で向ける。
    /// </summary>
    public sealed partial class DuelStageRig
    {
        // ---- 収録（監督決定: 購入パックの組み合わせ。issue #1174） ----------------------------------
        private const string TimeoutRunClipName = "AA_Baseball_Defender_Movement_Run_Loop";
        private const string TimeoutWalkClipName = "AA_Baseball_Defender_Movement_Walk_F_Loop";
        private const string TimeoutPitcherIdleClipName = "AA_Baseball_Pitcher_Standing_Idle_Loop";
        private const string TimeoutNod1ClipName = "AA_Baseball_Pitcher_Head_Nodd_Type01";
        private const string TimeoutNod2ClipName = "AA_Baseball_Pitcher_Head_Nodd_Type02";
        private const string TimeoutUmpireClipName = "AA_Baseball_Umpire_Standing_Time";
        private const string TimeoutCrouchToStandClipName = "AA_Baseball_Defender_Crouch_To_Standing_Idle";

        /// <summary>走りの段の実測（<see cref="LocoLadder"/> と同じ出典）。前進量[m/s]と進行方向のずれ[度]。</summary>
        private const float TimeoutRunVClip = 4.28f, TimeoutRunMoveDir = -9.6f;
        private const float TimeoutWalkVClip = 0.73f, TimeoutWalkMoveDir = -4.5f;

        /// <summary>集まる・散るときの走る速さ[m/s]（収録4.28を0.93倍＝軽いジョグ）。</summary>
        public static float TimeoutRunSpeed = 4.0f;
        /// <summary>輪に入る直前・散り始めの歩く速さ[m/s]（収録0.73の1.37倍。歩きの上限1.45倍の内側）。</summary>
        public static float TimeoutWalkSpeed = 1.0f;
        /// <summary>輪に入る前に歩く距離[m]。</summary>
        public static float TimeoutWalkInM = 1.6f;
        /// <summary>輪の半径[m]（投手の位置から各人の立ち位置まで）。</summary>
        public static float TimeoutRingRadiusM = 1.2f;
        /// <summary>集まる人が走り始める位置の、輪からの最大距離[m]。定位置がこれより遠い人は画面外のこの距離から走ってくる。</summary>
        public static float TimeoutStartRadiusM = 9f;
        /// <summary>全員が輪に入ってから話している秒数[s]。</summary>
        public static float TimeoutTalkSeconds = 4.0f;
        /// <summary>散り始めてから場面を畳むまで[s]。</summary>
        public static float TimeoutLeaveSeconds = 2.6f;
        /// <summary>散るとき、歩いてから走りへ替えるまで[s]。</summary>
        private const float TimeoutLeaveWalkSeconds = 0.7f;
        /// <summary>クリップの繋ぎ[s]。</summary>
        private const float TimeoutFadeSeconds = 0.3f;
        /// <summary>待機で向き直る速さ[度/秒]。</summary>
        private const float TimeoutYawRateDegPerSec = 220f;
        /// <summary>
        /// 伝令の減速（走り→歩き）に使う距離[m]。収録 <see cref="OverrunClipName"/> の減速はほぼ全編
        /// （<see cref="OverrunDecelEndsAt"/>＝2.58s）で、走り→歩きの平均の速さで割った距離を進む。
        /// </summary>
        private static float TimeoutDecelM => (TimeoutRunSpeed + TimeoutWalkSpeed) * 0.5f * OverrunDecelEndsAt;

        /// <summary>
        /// カメラの位置（ステージ座標: +x=一塁側・+z=センター方向・マウンド z=18.44）。本塁側の斜め上から輪を見下ろす
        /// （2026-11-21 監督決定。一塁側・二塁側の案と録画で見比べて選んだ）。
        /// </summary>
        public static Vector3 TimeoutCameraPos = new Vector3(5.5f, 7.0f, 5.0f);
        /// <summary>縦に収めたい実寸[m]（輪の全身と足元のマウンド。構図は画角でなく実寸で決める＝3d-character.md）。</summary>
        public static float TimeoutFrameHeightM = 7.5f;
        /// <summary>
        /// カメラの注視点の高さ[m]。輪の胸（約0.9m）より下を見る＝輪を画面の上寄せに置き、下に出る伝令役の
        /// テロップ（交代テロップと同じ器）と重ねない（2026-11-21 録画で輪の下半分がテロップに隠れていた）。
        /// </summary>
        public static float TimeoutLookY = -0.8f;

        /// <summary>
        /// 自校のベンチが一塁側か（伝令が走ってくる側）。日本の高校野球は試合ごとに抽選で決まり、
        /// engine はベンチの側を持っていない＝ここで先攻・後攻から決め打ちする（後攻＝一塁側）。
        /// 攻撃伝令の場面（#1175）も同じ決め方に揃える。
        /// </summary>
        public static bool BenchIsFirstBaseSide(bool teamIsAway) => !teamIsAway;

        /// <summary>ベンチ前（伝令が出てくる所）[ステージ座標 m]。一塁側の値。三塁側は x を反転する。</summary>
        private static readonly Vector2 TimeoutBenchFront1B = new Vector2(22f, 5f);

        private enum TimeoutRole { Messenger, Catcher, First, Second, Third, Short }

        private enum TimeoutLeg { Wait, Run, Decel, WalkIn, Talk, LeaveWalk, LeaveRun }

        private sealed class TimeoutMember
        {
            public Actor A;
            public TimeoutRole Role;
            public Vector2 Start, Slot, Pos;
            public float Delay;          // 走り出すまでの遅れ[s]（固定値＝全員が揃って動かない）
            public AnimationClip Idle;   // 輪で立つ待機
            public float Phase;          // 待機の頭出し（収録の長さに対する比）
            public TimeoutLeg Leg;
            public float LegT;
            public float Yaw;
        }

        private readonly List<TimeoutMember> _toMembers = new();
        private bool _toActive;
        private float _toT;
        private float _toTalkStartT = -1f, _toLeaveStartT = -1f;
        private bool _toNod1, _toNod2, _toUmpireOut;
        private Vector2 _toCenter;
        private float _toPitcherYaw;
        private Actor _messenger;
        private DuelActorSnapshot _toPitcherSnap, _toCatcherSnap;
        private bool _toPitcherRootMotion, _toCatcherRootMotion;
        private bool _toMaskWasOn;

        /// <summary>伝令の場面（守備＝#1174・攻撃＝#1175 のどちらか）を流しているか。</summary>
        public bool TimeoutSceneActive => _toActive || _otActive;

        /// <summary>場面が終わった（散り終えた）か。呼び出し側はこれを見て <see cref="EndTimeoutScene"/> を呼ぶ。</summary>
        public bool TimeoutSceneDone => (_toActive && _toLeaveStartT >= 0f && _toT - _toLeaveStartT >= TimeoutLeaveSeconds)
            || OffenseTimeoutSceneDone;

        /// <summary>
        /// 伝令の場面を始める。対決ビューの投球・打球の途中では呼ばない（呼び出し側が球の合間に限る）。
        /// <paramref name="benchFirstBaseSide"/>＝伝令が一塁側のベンチから出てくるか。
        /// 組めない（素体・収録が無い）ときは false＝呼び出し側はテロップだけで済ませる。
        /// </summary>
        public bool BeginTimeoutScene(bool benchFirstBaseSide)
        {
            if (!Available || _toActive || _otActive || FieldPlayActive || _pitcher == null || _catcher == null) return false;
            EnsureFieldCast();
            if (_fieldCast.Count == 0) return false;
            var run = LoadClip(TimeoutRunClipName);
            var walk = LoadClip(TimeoutWalkClipName);
            var pIdle = LoadClip(TimeoutPitcherIdleClipName);
            if (run == null || walk == null || pIdle == null) return false;
            if (!EnsureMessenger(run)) return false;

            // 投手・捕手は対決の状態を控える（終わったら丸ごと戻す。EndFieldPlay と同じ作法）
            _toPitcherSnap = Snapshot(_pitcher);
            _toCatcherSnap = Snapshot(_catcher);
            _toPitcherRootMotion = RootMotionOf(_pitcher);
            _toCatcherRootMotion = RootMotionOf(_catcher);

            var pp = StagePos(_pitcher);
            _toCenter = new Vector2(pp.x, pp.z);
            _toMembers.Clear();

            // 輪の立ち位置（投手から見た方位[度]。0=センター方向・90=一塁側・180=本塁側）。
            // 来る方向の順に並べる＝走ってくる線が交差しない。伝令はベンチ側、捕手は本塁側。
            float s = benchFirstBaseSide ? 1f : -1f;
            var bench = new Vector2(TimeoutBenchFront1B.x * s, TimeoutBenchFront1B.y);
            AddMember(_messenger, TimeoutRole.Messenger, bench, 180f - 60f * s, 0.25f,
                LoadClip(PostHomeRunIdleClipNames[0]), 0.35f);
            AddMember(_catcher, TimeoutRole.Catcher, new Vector2(_toCatcherSnap.BasePos.x * MirrorSign(_catcher), _toCatcherSnap.BasePos.z),
                180f, 0.0f, LoadClip(PostHomeRunIdleClipNames[1]), 0.10f);
            AddFielder(FieldPosition.FirstBase, TimeoutRole.First, benchFirstBaseSide ? 60f : 120f, 0.15f, 2, 0.55f);
            AddFielder(FieldPosition.SecondBase, TimeoutRole.Second, benchFirstBaseSide ? 0f : 60f, 0.40f, 3, 0.20f);
            AddFielder(FieldPosition.Shortstop, TimeoutRole.Short, benchFirstBaseSide ? 300f : 0f, 0.30f, 1, 0.75f);
            AddFielder(FieldPosition.ThirdBase, TimeoutRole.Third, benchFirstBaseSide ? 240f : 300f, 0.05f, 0, 0.0f);

            foreach (var m in _toMembers)
            {
                var a = m.A;
                a.Go.SetActive(true);
                a.Enabled = true;
                a.HoldAtEnd = false;
                a.Holding = false;
                a.HoldRemaining = 0f;
                a.DefaultEndAt = 0f;
                a.IdleEndAt = 0f;
                SetRootMotion(a, false);   // 位置はここで置く（クリップに動かさせない）
                m.Pos = m.Start;
                m.Leg = TimeoutLeg.Wait;
                m.LegT = 0f;
                m.Yaw = YawToward(m.Start, m.Slot) - TimeoutRunMoveDir;
                // 最初のコマから走りの姿で置く（構えのまま滑らない）。頭出しは役ごとにずらす
                SetActorClip(a, run, m.Phase * run.length, 0f);
                a.IdleClip = run;
                a.Speed = 0f;   // 走り出すまでは止めておく
                PlaceFielder(a, m.Pos.x, m.Pos.y, m.Yaw);
            }

            // 投手はその場で立つ（輪の中心）。伝令の来る方を向く
            _pitcher.HoldAtEnd = false;
            _pitcher.Holding = false;
            _pitcher.HoldRemaining = 0f;
            _pitcher.DefaultEndAt = 0f;
            _pitcher.IdleEndAt = 0f;
            SetRootMotion(_pitcher, false);
            CrossfadeActorToClip(_pitcher, pIdle, 0f, 0f, 0.4f);
            _pitcher.IdleClip = pIdle;
            _pitcher.Speed = 1f;
            _toPitcherYaw = YawToward(_toCenter, _toMembers[0].Slot);
            PlaceFielder(_pitcher, _toCenter.x, _toCenter.y, _toPitcherYaw);

            // 捕手はマスクを外した姿（監督決定A案＝隠すだけ。ヘルメットは被ったまま）
            _toMaskWasOn = _cMaskMount != null && _cMaskMount.gameObject.activeSelf;
            if (_cMaskMount != null) _cMaskMount.gameObject.SetActive(false);

            UmpireCallsTime();

            _toActive = true;
            _toT = 0f;
            _toTalkStartT = _toLeaveStartT = -1f;
            _toNod1 = _toNod2 = _toUmpireOut = false;
            ApplyTimeoutCamera();
            return true;
        }

        private void AddFielder(FieldPosition pos, TimeoutRole role, float slotDeg, float delay, int idleIdx, float phase)
        {
            if (!_fieldCast.TryGetValue(pos, out var a) || a == null || a.Go == null) return;
            var home = FieldHomeSpot(pos);
            AddMember(a, role, new Vector2((float)home.X, (float)home.Y), slotDeg, delay,
                LoadClip(PostHomeRunIdleClipNames[idleIdx]), phase);
        }

        private void AddMember(Actor a, TimeoutRole role, Vector2 home, float slotDeg, float delay,
            AnimationClip idle, float phase)
        {
            if (a == null || a.Go == null) return;
            float r = slotDeg * Mathf.Deg2Rad;
            var slot = _toCenter + new Vector2(Mathf.Sin(r), Mathf.Cos(r)) * TimeoutRingRadiusM;
            var from = home - slot;
            float d = from.magnitude;
            // 定位置が遠い人は、輪から TimeoutStartRadiusM の所（画面の外）から走ってくる
            var start = d > TimeoutStartRadiusM ? slot + from / d * TimeoutStartRadiusM : home;
            _toMembers.Add(new TimeoutMember
            {
                A = a, Role = role, Start = start, Slot = slot, Delay = delay, Idle = idle, Phase = phase,
            });
        }

        /// <summary>伝令役の素体（守備側の色・グラブなし）。初回だけ作り、以降は使い回す。</summary>
        private bool EnsureMessenger(AnimationClip clip)
        {
            if (_messenger != null && _messenger.Go != null)
            {
                Repaint(_messenger, _defenseColors);   // 試合・攻守で色が変わる
                return true;
            }
            var vendor = LoadVendorBody();
            bool useVendor = vendor != null;
            var bodyPrefab = useVendor ? vendor : LoadBody();
            int layer = LayerMask.NameToLayer(LayerName);
            if (bodyPrefab == null || layer < 0) return false;
            var a = SpawnActor(bodyPrefab, clip, Vector3.zero, 0f, _defenseColors, 0f, layer, useVendor,
                rootMotion: false, tightBounds: true);
            a.Go.name = "timeout-messenger";
            a.IdleClip = clip;
            StepActor(a, 0.001f);
            GroundFix(a);
            if (!useVendor)
                AttachGear(a, GearKind.Cap, Vector3.zero, _defenseColors.Cap, layer, _defenseColors.CapVisor);
            CollectTeamMats(a, _defenseColors.Cap);
            a.Go.SetActive(false);
            a.Enabled = false;
            _messenger = a;
            return true;
        }

        private void UmpireCallsTime()
        {
            var clip = LoadClip(TimeoutUmpireClipName);
            if (_umpire == null || clip == null) return;
            CrossfadeActorToClip(_umpire, clip, 0f, 0f, 0.25f);   // 終わったら待機（IdleClip）へ戻る
            _umpire.Speed = 1f;
        }

        private void ApplyTimeoutCamera()
        {
            FramePreview(TimeoutCameraPos, new Vector3(_toCenter.x, TimeoutLookY, _toCenter.y), TimeoutFrameHeightM);
        }

        /// <summary>stage-local の a から b を向く yaw[度]（素の向き +Z のクリップ用）。</summary>
        private static float YawToward(Vector2 a, Vector2 b) => Mathf.Atan2(b.x - a.x, b.y - a.y) * Mathf.Rad2Deg;

        /// <summary>
        /// 場面中の <see cref="Tick"/>。投球の予約（カットバック・コール・捕球）と投球の補正（セットの握り・首・
        /// 捕手のミット寄せ）は進めない＝場面の間に発火して待機の姿勢を崩さない。ポーズは12fps量子化のまま。
        /// </summary>
        private void TickTimeoutScene(float deltaTime)
        {
            AdvanceRattledFx(deltaTime);   // 伝令で動揺が解けた渦巻きは場面の中で消えていく（issue #1178）
            bool posed = StepPoseQuantum(deltaTime);
            StepTimeoutScene(deltaTime);
            StepOffenseTimeoutScene(deltaTime, posed);   // 攻撃伝令の場面（issue #1175）
        }

        /// <summary>
        /// 1コマ進める（ポーズを進めた後）＝置いた位置・向きがこのコマの絵になる。
        /// </summary>
        private void StepTimeoutScene(float dt)
        {
            if (!_toActive) return;
            _toT += dt;
            var run = LoadClip(TimeoutRunClipName);
            var walk = LoadClip(TimeoutWalkClipName);

            bool allIn = true;
            foreach (var m in _toMembers)
            {
                StepTimeoutMember(m, dt, run, walk);
                if (m.Leg != TimeoutLeg.Talk) allIn = false;
            }

            // 全員が輪に入ったら話し始める（投手は伝令の話に2回うなずく）
            if (allIn && _toTalkStartT < 0f) _toTalkStartT = _toT;
            if (_toTalkStartT >= 0f && _toLeaveStartT < 0f)
            {
                float tt = _toT - _toTalkStartT;
                if (!_toNod1 && tt >= 0.7f) { _toNod1 = true; PitcherNods(TimeoutNod1ClipName); }
                if (!_toNod2 && tt >= 2.5f) { _toNod2 = true; PitcherNods(TimeoutNod2ClipName); }
                if (tt >= TimeoutTalkSeconds) BeginTimeoutLeave(walk);
            }

            // 投手は輪の中心で向きだけ合わせる（伝令へ。散ったら本塁へ）
            float want = _toLeaveStartT >= 0f ? YawFacingHome(_toCenter.x, _toCenter.y) : _toPitcherYaw;
            float cur = StageYaw(_pitcher);
            PlaceFielder(_pitcher, _toCenter.x, _toCenter.y,
                Mathf.MoveTowardsAngle(cur, want, TimeoutYawRateDegPerSec * dt));
        }

        private void PitcherNods(string clipName)
        {
            var clip = LoadClip(clipName);
            if (clip == null) return;
            CrossfadeActorToClip(_pitcher, clip, 0f, 0f, 0.2f);   // 一発物＝終わると待機（IdleClip）へ戻る
            _pitcher.Speed = 1f;
        }

        private void BeginTimeoutLeave(AnimationClip walk)
        {
            _toLeaveStartT = _toT;
            UmpireCallsTime();   // タイムを解く（同じ収録で「プレー再開」の合図に見せる）
            foreach (var m in _toMembers)
            {
                m.Leg = TimeoutLeg.LeaveWalk;
                m.LegT = 0f;
                CrossfadeActorToClip(m.A, walk, m.Phase * walk.length, 0f, TimeoutFadeSeconds);
                m.A.IdleClip = walk;
            }
        }

        private void StepTimeoutMember(TimeoutMember m, float dt, AnimationClip run, AnimationClip walk)
        {
            var a = m.A;
            if (a == null || a.Go == null) return;
            m.LegT += dt;
            float speed = 0f;
            float moveDir = TimeoutRunMoveDir;
            Vector2 target = m.Slot;
            switch (m.Leg)
            {
                case TimeoutLeg.Wait:
                    if (_toT >= m.Delay) { m.Leg = TimeoutLeg.Run; m.LegT = 0f; }
                    break;
                case TimeoutLeg.Run:
                {
                    speed = TimeoutRunSpeed;
                    float left = Vector2.Distance(m.Pos, m.Slot);
                    float brake = m.Role == TimeoutRole.Messenger ? TimeoutDecelM + TimeoutWalkInM * 0.5f : TimeoutWalkInM;
                    if (left <= brake)
                    {
                        m.LegT = 0f;
                        if (m.Role == TimeoutRole.Messenger)
                        {
                            // 伝令は走り→歩きへ落ちる収録で減速する（issue #1174 の割り当て）
                            m.Leg = TimeoutLeg.Decel;
                            CrossfadeActorToClip(a, LoadClip(OverrunClipName), 0f, 0f, TimeoutFadeSeconds);
                            a.IdleClip = walk;
                        }
                        else
                        {
                            m.Leg = TimeoutLeg.WalkIn;
                            CrossfadeActorToClip(a, walk, m.Phase * walk.length, 0f, TimeoutFadeSeconds);
                            a.IdleClip = walk;
                        }
                    }
                    break;
                }
                case TimeoutLeg.Decel:
                {
                    // 収録の減速に合わせて地面の速さも落とす（走り→歩き。OverrunDecelEndsAt で歩きの速さ）
                    float u = Mathf.Clamp01(m.LegT / OverrunDecelEndsAt);
                    speed = Mathf.Lerp(TimeoutRunSpeed, TimeoutWalkSpeed, u);
                    moveDir = OverrunMoveDirDeg;
                    if (u >= 1f)
                    {
                        m.Leg = TimeoutLeg.WalkIn;
                        m.LegT = 0f;
                        CrossfadeActorToClip(a, walk, 0f, 0f, TimeoutFadeSeconds);
                        a.IdleClip = walk;
                    }
                    break;
                }
                case TimeoutLeg.WalkIn:
                    speed = TimeoutWalkSpeed;
                    moveDir = TimeoutWalkMoveDir;
                    break;
                case TimeoutLeg.LeaveWalk:
                    speed = TimeoutWalkSpeed;
                    moveDir = TimeoutWalkMoveDir;
                    target = m.Start;
                    if (m.LegT >= TimeoutLeaveWalkSeconds)
                    {
                        m.Leg = TimeoutLeg.LeaveRun;
                        m.LegT = 0f;
                        CrossfadeActorToClip(a, run, m.Phase * run.length, 0f, TimeoutFadeSeconds);
                        a.IdleClip = run;
                    }
                    break;
                case TimeoutLeg.LeaveRun:
                    speed = TimeoutRunSpeed;
                    target = m.Start;
                    break;
            }

            if (m.Leg == TimeoutLeg.Talk)
            {
                // 輪の中心（投手）へ向き直る
                m.Yaw = Mathf.MoveTowardsAngle(m.Yaw, YawToward(m.Pos, _toCenter), TimeoutYawRateDegPerSec * dt);
                a.Speed = 1f;
                PlaceFielder(a, m.Pos.x, m.Pos.y, m.Yaw);
                return;
            }

            if (speed > 0f)
            {
                var to = target - m.Pos;
                float dist = to.magnitude;
                float stepM = speed * dt;
                if (dist <= stepM || dist < 1e-4f)
                {
                    m.Pos = target;
                    if (m.Leg == TimeoutLeg.WalkIn || m.Leg == TimeoutLeg.Decel) Arrive(m);
                }
                else
                {
                    m.Pos += to / dist * stepM;
                    // 体は進行方向から収録のずれぶん戻す（カニ歩き防止。LocoClip.MoveDirDeg の項）
                    float wantYaw = YawToward(Vector2.zero, to) - moveDir;
                    m.Yaw = Mathf.MoveTowardsAngle(m.Yaw, wantYaw, 540f * dt);
                }
                // 足の回転を地面の速さへ合わせる（収録の前進量で割る）
                float vClip = m.Leg == TimeoutLeg.Run || m.Leg == TimeoutLeg.LeaveRun ? TimeoutRunVClip
                    : m.Leg == TimeoutLeg.Decel ? 0f : TimeoutWalkVClip;
                if (m.Leg != TimeoutLeg.Talk)
                    a.Speed = vClip > 0f ? Mathf.Clamp(speed / vClip, LocoSpeedMin, LocoSpeedMax) : 1f;
            }
            else if (m.Leg == TimeoutLeg.Wait)
            {
                a.Speed = 0f;
            }
            PlaceFielder(a, m.Pos.x, m.Pos.y, m.Yaw);
        }

        private void Arrive(TimeoutMember m)
        {
            m.Leg = TimeoutLeg.Talk;
            m.LegT = 0f;
            var a = m.A;
            if (m.Role == TimeoutRole.Third)
            {
                // 三塁手は「しゃがみ→立つ」を挟む（監督の割り当て。終わると待機 Type01 へ戻る）
                var cts = LoadClip(TimeoutCrouchToStandClipName);
                if (cts != null)
                {
                    CrossfadeActorToClip(a, cts, 0f, 0f, TimeoutFadeSeconds);
                    a.IdleClip = m.Idle;
                    a.Speed = 1f;
                    return;
                }
            }
            if (m.Idle != null)
            {
                CrossfadeActorToClip(a, m.Idle, m.Phase * m.Idle.length, 0f, 0.35f);
                a.IdleClip = m.Idle;
            }
            a.Speed = 1f;
        }

        /// <summary>
        /// 場面を畳んで対決ビューへ戻す（散り終えた・クリックで飛ばした・高速試合モードへ入った）。
        /// 走っていないときに呼んでも何もしない。
        /// </summary>
        public void EndTimeoutScene()
        {
            EndOffenseTimeoutScene();   // 攻撃伝令の場面（issue #1175）も同じ口で畳む
            if (!_toActive) return;
            _toActive = false;
            _toMembers.Clear();

            if (_messenger != null && _messenger.Go != null)
            {
                _messenger.Go.SetActive(false);
                _messenger.Enabled = false;
            }
            // 内野4人は守備ビューの後始末と同じ戻し方（待機クリップ・定位置・伏せる）
            foreach (var kv in _fieldCast)
            {
                var idle = IdleClipFor(kv.Key);
                if (idle == null || kv.Value == null || kv.Value.Go == null) continue;
                SetActorClip(kv.Value, idle, 0f, 0f);
                kv.Value.IdleClip = idle;
                kv.Value.Speed = 1f;
                SetRootMotion(kv.Value, false);
            }
            PlaceFieldCastAtDefault();
            SetFieldCastVisible(_fieldCastVisible);

            Restore(_pitcher, _toPitcherSnap);
            Restore(_catcher, _toCatcherSnap);
            SetRootMotion(_pitcher, _toPitcherRootMotion);
            SetRootMotion(_catcher, _toCatcherRootMotion);
            if (_cMaskMount != null && _toMaskWasOn) _cMaskMount.gameObject.SetActive(true);
            if (_umpire != null && _umpire.IdleClip != null) SetActorClip(_umpire, _umpire.IdleClip, 0f, 0f);

            ClearPreviewFrame();
            ApplyCalibration(_cal);   // ⚠ 画角だけでなくカメラの位置・向きも戻す
        }

        /// <summary>
        /// 撮影・検査用: 場面の後に投手・捕手の基準位置と向きが場面の前と一致しているか
        /// （PlaceFielder 系での恒久汚染の見張り。3d-character.md）。最大のずれ [m, 度] を返す。
        /// </summary>
        internal (float PosM, float YawDeg) TimeoutRestoreErrorForCapture()
        {
            float pm = 0f, yd = 0f;
            void One(Actor a, DuelActorSnapshot s)
            {
                if (a == null || !s.Valid) return;
                pm = Mathf.Max(pm, Vector3.Distance(a.BasePos, s.BasePos));
                yd = Mathf.Max(yd, Quaternion.Angle(a.BaseRot, s.BaseRot));
            }
            One(_pitcher, _toPitcherSnap);
            One(_catcher, _toCatcherSnap);
            return (pm, yd);
        }
    }
}
