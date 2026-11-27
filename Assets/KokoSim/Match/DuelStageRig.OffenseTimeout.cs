using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 攻撃伝令の場面（issue #1175）。自校の攻撃伝令を送ったら、次の球の前に「伝令が打者に耳打ちする」絵を差し込む。
    /// 守備伝令の場面（<see cref="BeginTimeoutScene"/>・issue #1174）の攻撃版で、入口・テロップ・クリックで飛ばす・
    /// 高速試合モードの扱いは呼び出し側（MatchLiveController）で共通。
    ///
    /// <b>表示だけ</b>の芝居で、engine（効き目・乱数・決定論）には一切触れない。位置・時刻・位相はここの固定値。
    ///
    /// 流れ: 球審がタイム → **いきなり耳打ちの場面から**（伝令が打者の横に立ち、顔を寄せて片手を口もとへ・
    /// 打者は首を向けてうなずく）→ 球審がタイムを解く → 打者は打席へ歩いて戻る（伝令はその場に立ったまま）→ 畳む。
    /// 伝令がベンチから入ってくるところと、ベンチへ戻るところは映さない（issue #1741。ベンチが打者と反対側だと、
    /// ベンチから一直線の道が本塁の上を通っていた）。
    ///
    /// 耳打ちの形は収録に無いので、評価した後の骨に後から足す（design-18 §3.1「クリップ内部の緩急は改変しない」）。
    /// 回す量はすべて骨の親のローカル空間で「向き→向き」の写像として作る＝左打者の鏡映親ノードの下でも同じ式で効く
    /// （負スケールの下でワールドの回転を直接書くと手系が裏返る。<see cref="FixPitcherSetHead"/> と同じ作法）。
    ///
    /// 座標はすべてステージ座標（+x=一塁側・+z=センター方向・本塁が原点）。左打者は x を反転した鏡の絵になる。
    /// クリップの素の向きは全部 +Z（実測 2026-11-21・<c>DuelMotionSpike.RunBatterTimeoutProbe</c>）。
    /// </summary>
    public sealed partial class DuelStageRig
    {
        // ---- 収録（issue #1175 の割り当て） ----------------------------------------------------
        private const string OtBatterWalkClipName = "AA_Baseball_Batter_Movement_Walk_F_Loop";
        private const string OtBatterIdleClipName = "AA_Baseball_Batter_Idle_Loop";
        /// <summary>
        /// 耳打ちの間の打者の待機。打者の待機（<see cref="OtBatterIdleClipName"/>）はバットを肩に担ぐ構えなので、
        /// 手を下ろして立つ野手の待機を使い、バットは片手で下げて持たせる（2026-11-21 監督指摘）。
        /// 伝令の待機（Type01）と位相が揃って同じ揺れにならないよう別の型にする。
        /// </summary>
        private const string OtBatterStandClipName = "AA_Baseball_Defender_Standing_Idle_Loop_Type02";
        private const string OtMessengerIdleClipName = "AA_Baseball_Defender_Standing_Idle_Loop_Type01";

        /// <summary>打者の歩きの収録の前進量[m/s]（1.33秒で0.95m）。</summary>
        private const float OtBatterWalkVClip = 0.71f;


        private const int OtNodCount = 3;
        /// <summary>打者が首を伝令へ向ける上限[度]（胴の正面から）。</summary>
        private const float OtHeadTurnMaxDeg = 65f;


        private enum OtLeg { Talk, Leave, Done }

        private bool _otActive;
        private float _otT;
        private float _otTalkStartT = -1f, _otLeaveStartT = -1f;
        private OtLeg _otBatterLeg;
        private Vector2 _otB0, _otB1, _otM, _otBatterPos;
        private float _otBatterYaw, _otBatterTalkYaw, _otMsgTalkYaw;
        private float _otSide;            // 打者の側（右打者＝−1＝三塁側・左打者＝+1）
        private float _otWhisperW;        // 耳打ちの後補正の効き（0..1）
        private DuelActorSnapshot _otBatterSnap;
        private bool _otBatterRootMotion;
        private bool _otBatWasActive;
        private float _otMouthEarM = -1f; // 撮影・検査用: 耳打ち中の伝令の口と打者の耳の距離の最小[m]

        /// <summary>
        /// 攻撃伝令の場面を始める。<paramref name="benchFirstBaseSide"/>＝伝令が一塁側のベンチから出てくるか
        /// （<see cref="BenchIsFirstBaseSide"/> で決める）。組めないときは false＝呼び出し側はテロップだけで済ませる。
        /// </summary>
        public bool BeginOffenseTimeoutScene(bool benchFirstBaseSide)
        {
            if (!Available || _toActive || _otActive || FieldPlayActive || _batter == null || _batter.Animator == null) return false;
            var run = LoadClip(TimeoutRunClipName);
            var bIdle = LoadClip(OtBatterIdleClipName);
            var bStand = LoadClip(OtBatterStandClipName);
            var mIdle = LoadClip(OtMessengerIdleClipName);
            if (run == null || bIdle == null || bStand == null || mIdle == null || LoadClip(OtBatterWalkClipName) == null) return false;
            if (!EnsureMessenger(run)) return false;
            Repaint(_messenger, _offenseColors);   // 伝令は攻撃側の選手

            _otBatterSnap = Snapshot(_batter);
            _otBatterRootMotion = RootMotionOf(_batter);
            _otBatWasActive = _bat != null && _bat.activeSelf;

            _otSide = _batterLeft ? 1f : -1f;
            var bp = StagePos(_batter);
            _otB0 = new Vector2(bp.x, bp.z);
            _otB1 = _otB0 + new Vector2(_otSide * DuelTuning.OtBatterBackM, 0f);
            // 伝令は打者の**ベンチ側**に立つ（ベンチが打者と反対側＝後攻の右打者などなら本塁側）。
            // 耳打ちの手・首の向きは立った側から決まる（ApplyWhisperPose）
            float s = benchFirstBaseSide ? 1f : -1f;
            _otM = _otB1 + new Vector2(s * DuelTuning.OtPairGapM, 0f);
            _otBatterTalkYaw = s * DuelTuning.OtFaceInDeg;     // センター方向から伝令の側へ
            _otMsgTalkYaw = -s * DuelTuning.OtFaceInDeg;       // センター方向から打者の側へ

            // 2人とも最初のコマから耳打ちの立ち位置・向きにいる（入ってくる足取りと打者が下がる足取りは映さない）
            _batter.HoldAtEnd = false;
            _batter.Holding = false;
            _batter.HoldRemaining = 0f;
            _batter.EndAt = 0f;
            _batter.DefaultEndAt = 0f;
            _batter.IdleEndAt = 0f;
            _batter.SpeedSwitchAt = -1f;
            SetRootMotion(_batter, false);
            _otBatterPos = _otB1;
            _otBatterYaw = _otBatterTalkYaw;
            CrossfadeActorToClip(_batter, bStand, 0.5f * bStand.length, 0f, 0f);   // 手を下ろして立つ（バットは片手で下げる）
            _batter.IdleClip = bStand;
            _batter.Speed = 1f;
            PlaceFielder(_batter, _otBatterPos.x, _otBatterPos.y, _otBatterYaw);
            _otBatterLeg = OtLeg.Talk;

            var m = _messenger;
            m.Go.SetActive(true);
            m.Enabled = true;
            m.HoldAtEnd = false;
            m.Holding = false;
            m.HoldRemaining = 0f;
            m.DefaultEndAt = 0f;
            m.IdleEndAt = 0f;
            SetRootMotion(m, false);
            SetActorClip(m, mIdle, 0f, 0f);
            m.IdleClip = mIdle;
            m.Speed = 1f;
            PlaceFielder(m, _otM.x, _otM.y, _otMsgTalkYaw);

            UmpireCallsTime();

            _otActive = true;
            _otT = 0f;
            _otTalkStartT = 0f;
            _otLeaveStartT = -1f;
            _otWhisperW = 1f;   // 最初のコマから耳打ちの形
            _otMouthEarM = -1f;
            ApplyOffenseTimeoutCamera();
            return true;
        }

        private void ApplyOffenseTimeoutCamera()
        {
            var mid = (_otB1 + _otM) * 0.5f;
            var off = DuelTuning.OtCameraOffset;
            // x は「本塁から遠い側＝+」の相対値。打者の側（_otSide＝本塁から遠い向き）を掛ける
            var pos = new Vector3(mid.x + off.x * _otSide, off.y, mid.y + off.z);
            FramePreview(pos, new Vector3(mid.x, DuelTuning.OtLookY, mid.y), DuelTuning.OtFrameHeightM);
        }

        /// <summary>場面が終わった（伝令が去り、打者が打席へ戻り着いた）か。</summary>
        private bool OffenseTimeoutSceneDone => _otActive && _otLeaveStartT >= 0f
            && _otT - _otLeaveStartT >= DuelTuning.OtLeaveMinSeconds && _otBatterLeg == OtLeg.Done;

        /// <summary>1コマ進める（ポーズを進めた後）。<paramref name="posed"/>＝このコマでポーズを評価し直したか。</summary>
        private void StepOffenseTimeoutScene(float dt, bool posed)
        {
            if (!_otActive) return;
            _otT += dt;
            StepOtBatter(dt);
            if (_messenger != null && _messenger.Go != null) PlaceFielder(_messenger, _otM.x, _otM.y, _otMsgTalkYaw);
            if (_otLeaveStartT < 0f && _otT - _otTalkStartT >= DuelTuning.OtTalkSeconds) BeginOtLeave();

            // 耳打ちの効き: 話し始めで寄せ、散り始めで戻す
            float wantW = _otLeaveStartT < 0f ? 1f : 0f;
            _otWhisperW = Mathf.MoveTowards(_otWhisperW, wantW, dt / 0.45f);

            // 後補正はポーズを評価し直したコマにだけ当てる（評価の間は骨がそのまま残る＝二度当てしない）
            if (posed)
            {
                ApplyWhisperPose();
                OtAlignBat();
            }
        }

        private void StepOtBatter(float dt)
        {
            switch (_otBatterLeg)
            {
                case OtLeg.Talk:
                    break;
                case OtLeg.Leave:
                {
                    // 本塁の方を向いて歩いて戻る（収録の前進量で足を合わせる）
                    var to = _otB0 - _otBatterPos;
                    float want = YawToward(_otBatterPos, _otB0);
                    _otBatterYaw = Mathf.MoveTowardsAngle(_otBatterYaw, want, 260f * dt);
                    if (Mathf.Abs(Mathf.DeltaAngle(_otBatterYaw, want)) > 25f) break;   // 向き直ってから歩き出す
                    if (_batter.Clip != LoadClip(OtBatterWalkClipName))
                    {
                        var walk = LoadClip(OtBatterWalkClipName);
                        CrossfadeActorToClip(_batter, walk, 0f, 0f, 0.3f);
                        _batter.IdleClip = walk;
                        _batter.Speed = 1f;
                    }
                    float stepM = OtBatterWalkVClip * dt;
                    if (to.magnitude <= stepM)
                    {
                        _otBatterPos = _otB0;
                        _otBatterLeg = OtLeg.Done;
                        var idle = LoadClip(OtBatterIdleClipName);
                        CrossfadeActorToClip(_batter, idle, 0f, 0f, 0.3f);
                        _batter.IdleClip = idle;
                    }
                    else _otBatterPos += to.normalized * stepM;
                    break;
                }
                case OtLeg.Done:
                    break;
            }
            PlaceFielder(_batter, _otBatterPos.x, _otBatterPos.y, _otBatterYaw);
        }

        private void BeginOtLeave()
        {
            _otLeaveStartT = _otT;
            UmpireCallsTime();   // タイムを解く（守備伝令の場面と同じ収録で「プレー再開」の合図に見せる）
            _otBatterLeg = OtLeg.Leave;   // 伝令はその場に立ったまま（耳打ちの形だけ解ける）
        }

        // ---- 耳打ちの後補正 ------------------------------------------------------------------

        /// <summary>
        /// 耳打ちの形を骨へ足す（ポーズを評価した直後だけ）。伝令＝上体を打者へ傾け、首を打者の耳へ向け、
        /// 打者に近い側の手を口もとへ。打者＝首を伝令へ向け、ゆっくりうなずく。
        /// </summary>
        private void ApplyWhisperPose()
        {
            if (_otWhisperW <= 0f || _messenger?.Animator == null || _batter?.Animator == null) return;
            float w = _otWhisperW * _otWhisperW * (3f - 2f * _otWhisperW);
            var ma = _messenger.Animator;
            var ba = _batter.Animator;
            var bHead = ba.GetBoneTransform(HumanBodyBones.Head);
            var mHead = ma.GetBoneTransform(HumanBodyBones.Head);
            if (bHead == null || mHead == null) return;

            // 口と耳は「補正の前の頭」から見た点として控え、補正の後は頭の骨に付いて動かす
            // （TransformPoint は鏡映の負スケールも含めて往復する＝左打者でも同じ点を指す）
            var mSide = Vector3.ProjectOnPlane(bHead.position - mHead.position, Vector3.up).normalized;
            var mMouthL = mHead.InverseTransformPoint(MouthPoint(ma, _messenger, mHead));
            var bEarL = bHead.InverseTransformPoint(bHead.position + Vector3.up * 0.06f - mSide * 0.08f);

            // ① 伝令の上体を打者の方へ傾ける（胸の骨で。上方向を打者側へ倒す）
            var spine = ma.GetBoneTransform(HumanBodyBones.Chest) ?? ma.GetBoneTransform(HumanBodyBones.Spine);
            if (spine != null && mSide.sqrMagnitude > 1e-6f)
            {
                var axis = Vector3.Cross(Vector3.up, mSide);
                RotateBoneWorld(spine, Vector3.up, Quaternion.AngleAxis(DuelTuning.OtLeanDeg * w, axis) * Vector3.up);
            }

            // ② 伝令の首を打者の耳へ向ける
            TurnHeadToward(ma, _messenger, bHead.TransformPoint(bEarL), w, 0f);

            // ③ 打者に近い側の手を口もとへ（手の中心を運ぶ＝手首でなく。3d-character.md）
            var mouth = mHead.TransformPoint(mMouthL);
            var face = FaceForward(ma, _messenger);
            bool rightHand = Vector3.Dot(mSide, ChestRight(ma, _messenger)) > 0f;
            var up = ma.GetBoneTransform(rightHand ? HumanBodyBones.RightUpperArm : HumanBodyBones.LeftUpperArm);
            var lo = ma.GetBoneTransform(rightHand ? HumanBodyBones.RightLowerArm : HumanBodyBones.LeftLowerArm);
            var hand = ma.GetBoneTransform(rightHand ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
            if (up != null && lo != null && hand != null)
            {
                var knuckle = ma.GetBoneTransform(rightHand ? HumanBodyBones.RightMiddleProximal : HumanBodyBones.LeftMiddleProximal);
                // 手の中心＝手首→中指付け根の中点（design-18 §3.3.1）。Fist は打者の骨を見るので使わない
                var center = HandCenter(hand, knuckle);
                // 口の打者側の脇へ、少し前に（掌で口を覆う位置）
                var want = mouth + mSide * 0.07f + face * 0.05f;
                var wristTarget = hand.position + (want - center);
                DuelIk.TwoBoneIk(up, lo, hand, Vector3.Lerp(hand.position, wristTarget, w), 0f);
            }

            // ④ 打者は首を伝令の口へ向け、うなずく
            float tt = _otTalkStartT >= 0f ? _otT - _otTalkStartT : 0f;
            float nod = 0f;
            for (int i = 0; i < OtNodCount; i++)
            {
                float u = (tt - (0.9f + i * 0.85f)) / 0.6f;
                if (u > 0f && u < 1f) nod = Mathf.Sin(u * Mathf.PI) * DuelTuning.OtNodDeg;
            }
            TurnHeadToward(ba, _batter, mHead.TransformPoint(mMouthL), w, nod * w);

            // 撮影・検査用: 伝令の口と打者の耳の距離（どちらも補正の後の頭に付いた点）
            if (_otWhisperW >= 0.99f)
            {
                float dm = Vector3.Distance(mHead.TransformPoint(mMouthL), bHead.TransformPoint(bEarL));
                _otMouthEarM = _otMouthEarM < 0f ? dm : Mathf.Min(_otMouthEarM, dm);
            }
        }

        /// <summary>骨を、親のローカル空間で「向き a を向き b へ」写す回転だけ回す（鏡映の下でも同じ式で効く）。</summary>
        private static void RotateBoneWorld(Transform bone, Vector3 aWorld, Vector3 bWorld)
        {
            var p = bone.parent;
            if (p == null) return;
            var aL = p.InverseTransformDirection(aWorld).normalized;
            var bL = p.InverseTransformDirection(bWorld).normalized;
            bone.localRotation = Quaternion.FromToRotation(aL, bL) * bone.localRotation;
        }

        /// <summary>胸の正面（水平）。肩の並びから取る。鏡映中は右と左が入れ替わるので符号を戻す。</summary>
        private static Vector3 FaceForward(Animator an, Actor a)
        {
            var ra = an.GetBoneTransform(HumanBodyBones.RightUpperArm).position;
            var la = an.GetBoneTransform(HumanBodyBones.LeftUpperArm).position;
            var f = Vector3.Cross(ra - la, Vector3.up);
            f.y = 0f;
            if (a.Mirrored) f = -f;
            return f.sqrMagnitude > 1e-8f ? f.normalized : Vector3.forward;
        }

        /// <summary>体の右（見た目の右手の側・水平）。</summary>
        private static Vector3 ChestRight(Animator an, Actor a)
        {
            var ra = an.GetBoneTransform(HumanBodyBones.RightUpperArm).position;
            var la = an.GetBoneTransform(HumanBodyBones.LeftUpperArm).position;
            var r = ra - la;
            r.y = 0f;
            return r.sqrMagnitude > 1e-8f ? r.normalized : Vector3.right;
        }

        /// <summary>口のおおよその位置（頭の骨＝首の付け根から少し上・顔の前）。</summary>
        private static Vector3 MouthPoint(Animator an, Actor a, Transform head)
        {
            var neck = an.GetBoneTransform(HumanBodyBones.Neck);
            var upDir = neck != null ? (head.position - neck.position).normalized : Vector3.up;
            // 顔の正面＝頭の前方向。頭の骨の向きは素体ごとに違うので、首→頭の上方向と胸の正面から作る
            var f = Vector3.ProjectOnPlane(FaceForward(an, a), upDir);
            if (f.sqrMagnitude < 1e-8f) f = FaceForward(an, a);
            return head.position - upDir * 0.02f + f.normalized * 0.09f;
        }

        /// <summary>
        /// 首（と頭）を target の方へ向ける。胴の正面からの水平の角度を <see cref="OtHeadTurnMaxDeg"/> までに抑え、
        /// <paramref name="nodDeg"/> だけうつむかせる。首と頭に半分ずつ配る。
        /// </summary>
        private static void TurnHeadToward(Animator an, Actor a, Vector3 target, float w, float nodDeg)
        {
            var neck = an.GetBoneTransform(HumanBodyBones.Neck);
            var head = an.GetBoneTransform(HumanBodyBones.Head);
            if (neck == null || head == null) return;
            var face = FaceForward(an, a);
            var to = target - head.position;
            var toH = new Vector3(to.x, 0f, to.z);
            if (toH.sqrMagnitude < 1e-6f) return;
            float yaw = Mathf.Clamp(Vector3.SignedAngle(face, toH.normalized, Vector3.up), -OtHeadTurnMaxDeg, OtHeadTurnMaxDeg) * w;
            var turned = Quaternion.AngleAxis(yaw, Vector3.up) * face;
            // 水平の向き → 半分を首、残りを頭で回す
            var half = Quaternion.AngleAxis(yaw * 0.5f, Vector3.up) * face;
            RotateBoneWorld(neck, face, half);
            RotateBoneWorld(head, half, turned);
            if (Mathf.Abs(nodDeg) > 0.01f)
            {
                var axis = Vector3.Cross(Vector3.up, turned);   // 右向きの軸まわりに前へ倒す
                RotateBoneWorld(head, turned, Quaternion.AngleAxis(nodDeg, axis) * turned);
            }
        }

        /// <summary>
        /// 場面中のバット。両手が揃う収録（打席へ戻った後の構え待ち）は手の中点から立て、片手の収録
        /// （歩き・耳打ちの間の立ち）は右手に持たせて先を下へ下ろす。両者は手の離れで混ぜる（クリップの替わり目で飛ばない）。
        /// 打撃の握りの補正（<see cref="AlignBat"/>）は場面の間は通さない（構えの区間判定が歩きの収録に合わない）。
        /// </summary>
        private void OtAlignBat()
        {
            if (_bat == null || _batter?.Animator == null) return;
            if (!_otBatWasActive) return;
            var lhT = _batter.Animator.GetBoneTransform(HumanBodyBones.LeftHand);
            var rhT = _batter.Animator.GetBoneTransform(HumanBodyBones.RightHand);
            if (lhT == null || rhT == null) return;
            _bat.SetActive(true);
            var lh = Fist(lhT, HumanBodyBones.LeftMiddleProximal);
            var rh = Fist(rhT, HumanBodyBones.RightMiddleProximal);
            float apart = Vector3.Distance(lh, rh);
            float one = Mathf.InverseLerp(0.30f, 0.50f, apart);
            var face = FaceForward(_batter.Animator, _batter);
            // 両手: 握りの中点から、両手の並びの水平の向きへ仰角を付けて立てる
            var d = rh - lh;
            var dh = new Vector3(d.x, 0f, d.z);
            var twoAxis = dh.sqrMagnitude > 1e-6f
                ? (dh.normalized * Mathf.Cos(_cal.BatStanceElevDeg * Mathf.Deg2Rad) + Vector3.up * Mathf.Sin(_cal.BatStanceElevDeg * Mathf.Deg2Rad)).normalized
                : Vector3.up;
            // 片手: 右手（左打者は鏡映で見た目の左手）で握り、先を下へ下ろす（体の少し前・外へ）。
            // 握る手は固定する＝両手の高さが揃う立ちの収録で、低い方の手を選ぶと持ち手が入れ替わってバットが飛ぶ
            var grip1 = rh;
            var side = ChestRight(_batter.Animator, _batter);
            var oneAxis = (Vector3.down * 0.9f + face * 0.3f + side * 0.15f).normalized;
            var axis = Vector3.Slerp(twoAxis, oneAxis, one).normalized;
            var grip = Vector3.Lerp((lh + rh) * 0.5f, grip1, one);
            float halfLen = Mathf.Max(0.1f, _cal.BatLengthM) * 0.5f;
            _bat.transform.localScale = new Vector3(1f, halfLen / BatHalfLength, 1f);
            _bat.transform.rotation = Quaternion.FromToRotation(Vector3.up, axis);
            _bat.transform.position = grip + axis * (halfLen - _cal.BatGripBelowM);
        }

        /// <summary>攻撃伝令の場面を畳む（打者を構えへ戻し、伝令役を伏せる）。走っていなければ何もしない。</summary>
        private void EndOffenseTimeoutScene()
        {
            if (!_otActive) return;
            _otActive = false;
            if (_messenger != null && _messenger.Go != null)
            {
                _messenger.Go.SetActive(false);
                _messenger.Enabled = false;
                Repaint(_messenger, _defenseColors);   // 守備伝令で使うときの色へ戻しておく
            }
            Restore(_batter, _otBatterSnap);
            SetRootMotion(_batter, _otBatterRootMotion);
            ResetBatterToStance();
            if (_bat != null) _bat.SetActive(_otBatWasActive);
            if (_umpire != null && _umpire.IdleClip != null) SetActorClip(_umpire, _umpire.IdleClip, 0f, 0f);
            ClearPreviewFrame();
            ApplyCalibration(_cal);   // ⚠ 画角だけでなくカメラの位置・向きも戻す
        }

        /// <summary>
        /// 撮影・検査用: 場面の後の打者のずれ [m, 度]（PlaceFielder 系での汚染の見張り）。位置は場面の前の基準と、
        /// 向きは実際の transform と基準（BaseRot）の食い違いを見る。基準の向きそのものは場面の前と比べない:
        /// 場面の後は構えの待機から始め直し、向きは較正（<see cref="SyncBatterYaw"/>）が決め直す
        /// （見逃しの後に構えへ戻る途中で控えた向きは補間の途中の値。2026-11-21 録画で前後の構えが一致するのを確認）。
        /// </summary>
        internal (float PosM, float YawDeg) OffenseTimeoutRestoreErrorForCapture()
        {
            if (_batter == null || _batter.Go == null || !_otBatterSnap.Valid) return (0f, 0f);
            var bp = _batter.BasePos;
            var sp = _otBatterSnap.BasePos;
            var lp = _batter.Go.transform.localPosition;
            float pos = Mathf.Max(Vector2.Distance(new Vector2(bp.x, bp.z), new Vector2(sp.x, sp.z)),
                Vector2.Distance(new Vector2(lp.x, lp.z), new Vector2(bp.x, bp.z)));
            return (pos, Quaternion.Angle(_batter.Go.transform.localRotation, _batter.BaseRot));
        }

        /// <summary>撮影・検査用: 耳打ち中の伝令の口と打者の耳の距離の最小[m]（未計測は負）。</summary>
        internal float OffenseTimeoutMouthEarForCapture => _otMouthEarM;

        /// <summary>撮影・検査用: いまの打者が左打席か。</summary>
        internal bool BatterLeftForCapture => _batterLeft;
    }
}
