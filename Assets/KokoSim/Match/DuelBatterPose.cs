using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 打者ポーズの実行時補正（design-18 §7-2）。収録モーキャップの「絵として気に食わない所」を、
    /// **リターゲット後の骨**へ毎コマ当て直す。
    ///
    /// なぜ実行時か（2026-07-29 に方式変更）:
    ///  - Blenderで焼くと 1往復3分（再焼き→FBX差し替え→再インポート→撮影）かかり、絵を見ながら追い込めない。
    ///    実行時なら <see cref="DuelCalibrationWindow"/> のスライダで即座に見える
    ///  - Humanoidの筋肉可動域（Head/Neck Turn は ±1.0 で頭打ち）を回避できる。骨を直接触るので
    ///    「頭が回りきらない」が起きない
    ///  - FBXは収録のまま保てる（補正値は DuelCalibration＝JSON・コード既定の1箇所）
    ///
    /// 掟: クリップ内部の緩急は触らない（姿勢だけ差し替える）。バットの整列（AlignBat）は
    /// **この補正の後**に行う＝握りを詰めた後の手からバット軸を採る。
    ///
    /// 収録は右打者（踏み込み＝左足・上の手＝右手）。左打者ミラー時は Side を反転させる想定で
    /// 左右を定数でなく引数に寄せてある。
    /// </summary>
    public static class DuelBatterPose
    {
        /// <summary>補正の入口。clipTime はクリップ内秒（構え0〜スイング〜走り出し）。</summary>
        public static void Apply(Animator anim, in DuelCalibration cal, float clipTime, Vector3 aimPoint)
        {
            if (anim == null) return;
            FixKnee(anim, cal, clipTime);
            FixHead(anim, cal, clipTime, aimPoint);
            FixGrip(anim, cal, clipTime);
        }

        // ---------------------------------------------------------------- 部品

        // IKの実体は捕手（DuelCatcherPose）と共用の DuelIk にある。呼び名はここに残して差分を小さく保つ
        private static void RotateAround(Transform t, Vector3 pivot, Quaternion q)
            => DuelIk.RotateAround(t, pivot, q);

        private static float Ramp(float t, float t0, float t1)
        {
            if (t1 <= t0) return t >= t1 ? 1f : 0f;
            return Mathf.Clamp01((t - t0) / (t1 - t0));
        }

        private static void TwoBoneIk(Transform root, Transform mid, Transform tip, Vector3 target,
            float poleDeg)
            => DuelIk.TwoBoneIk(root, mid, tip, target, poleDeg);

        private static Vector3 ClampReach(Transform root, Transform mid, Transform tip,
            Vector3 origin, Vector3 target, float guard)
            => DuelIk.ClampReach(root, mid, tip, origin, target, guard);

        private static Transform B(Animator a, HumanBodyBones b) => a.GetBoneTransform(b);

        // ---------------------------------------------------------------- 握り

        private static void FixGrip(Animator anim, in DuelCalibration cal, float t)
        {
            float w = 1f - Ramp(t, cal.GripEndSec - 0.15f, cal.GripEndSec);
            if (w <= 0f) return;
            var lu = B(anim, HumanBodyBones.LeftUpperArm);
            var ll = B(anim, HumanBodyBones.LeftLowerArm);
            var lh = B(anim, HumanBodyBones.LeftHand);
            var ru = B(anim, HumanBodyBones.RightUpperArm);
            var rl = B(anim, HumanBodyBones.RightLowerArm);
            var rh = B(anim, HumanBodyBones.RightHand);
            if (lu == null || ll == null || lh == null || ru == null || rl == null || rh == null) return;

            Vector3 a = lh.position, b = rh.position;
            Vector3 d = a - b;
            if (d.magnitude < 1e-4f) return;
            // 片手だけ寄せると、その腕が届かず伸び切って棒になる。既定は中点を保って半分ずつ寄せ、
            // 配分を変えたい時だけ GripPullBias で「動かさない側」の手を軸にする
            Vector3 u = d.normalized;
            Vector3 mid = (a + b) * 0.5f;
            float half = cal.GripGapM * 0.5f;
            float bias = Mathf.Clamp(cal.GripPullBias, -1f, 1f);
            Vector3 anchor = mid;   // 寄せた後の両手の中点
            if (bias < 0f) anchor = Vector3.Lerp(mid, a - u * half, -bias);   // 左手が動かない
            else if (bias > 0f) anchor = Vector3.Lerp(mid, b + u * half, bias);
            // スイング中は許容を上げる（実物も接触では腕が伸びる。収録もこの辺りで手が飛ぶ）
            float sw = Mathf.Min(Ramp(t, cal.GripSwingFromSec - 0.10f, cal.GripSwingFromSec),
                                 1f - Ramp(t, cal.GripSwingToSec, cal.GripSwingToSec + 0.10f));
            float gl = Mathf.Lerp(cal.GripReachGuard, cal.GripReachGuardSwing, sw);
            float gr = Mathf.Lerp(cal.GripReachGuardTop, cal.GripReachGuardSwingTop, sw);
            Vector3 tl = ClampReach(lu, ll, lh, a, Vector3.Lerp(a, anchor + u * half, w), gl);
            Vector3 tr = ClampReach(ru, rl, rh, b, Vector3.Lerp(b, anchor - u * half, w), gr);
            TwoBoneIk(lu, ll, lh, tl, cal.GripElbowPoleDeg * w);
            TwoBoneIk(ru, rl, rh, tr, cal.GripElbowPoleDegTop * w);
            // 前腕のひねりはIKの後・手首の前。肘→手首軸まわりなので手首の位置＝握り＝バット軸は動かない
            RollForearm(ll, lh, cal.GripForearmRollDeg * w);
            RollForearm(rl, rh, cal.GripForearmRollDegTop * w);
            FixWrist(anim, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
                HumanBodyBones.LeftMiddleProximal, cal.GripWristFlexDeg, cal.GripWristFlexStrength,
                cal.GripWristTwistDeg, cal.GripWristSideDeg, w);
            FixWrist(anim, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
                HumanBodyBones.RightMiddleProximal, cal.GripWristFlexDegTop, cal.GripWristFlexStrengthTop,
                cal.GripWristTwistDegTop, cal.GripWristSideDegTop, w);
            FixFingers(anim, true, cal.GripFingerCurl * w);
            FixFingers(anim, false, cal.GripFingerCurlTop * w);
        }

        // 握り込みの角度[度]（付け根→第2関節→先）。指を撮っていない収録なので静的に閉じる
        private static readonly float[] CurlDeg = { 55f, 70f, 45f };
        private const float ThumbScale = 0.45f;   // 親指は他の指に覆いかぶさるので浅く

        /// <summary>
        /// 指を握り込む。収録は指を撮っていない＝手が開いたままバットが通り抜けるため
        /// （2026-07-29 絵で確認）、残す価値のある指の動きが無い。
        /// 回す軸は**指の付け根を貫く線**（人差し指→小指）＝バットが通る向きそのもの。
        /// 内側の符号は骨のローカル軸でなく実測で決める（素体依存。design-18 §3.3）。
        /// </summary>
        private static void FixFingers(Animator anim, bool left, float curl)
        {
            if (curl <= 0.001f) return;
            var hd = B(anim, left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            var mp = B(anim, left ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal);
            var md = B(anim, left ? HumanBodyBones.LeftMiddleDistal : HumanBodyBones.RightMiddleDistal);
            var ip = B(anim, left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal);
            var lp = B(anim, left ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal);
            if (hd == null || mp == null || md == null || ip == null || lp == null) return;
            Vector3 axis = (ip.position - lp.position).normalized;
            if (axis.sqrMagnitude < 0.5f) return;
            // 掌側＝中指の先が手首へ近づく向き。骨を触らずに両符号を試して選ぶ
            Vector3 d = md.position - mp.position;
            float dp = (mp.position + Quaternion.AngleAxis(90f, axis) * d - hd.position).sqrMagnitude;
            float dm = (mp.position + Quaternion.AngleAxis(-90f, axis) * d - hd.position).sqrMagnitude;
            float sign = dp <= dm ? 1f : -1f;

            var table = left ? LeftFingers : RightFingers;
            for (int f = 0; f < table.Length; f++)
            {
                float scale = f == 0 ? ThumbScale : 1f;
                for (int g = 0; g < 3; g++)
                {
                    var t = B(anim, table[f][g]);
                    if (t == null) continue;
                    // 軸まわりの回転は軸自身を動かさない＝親を回しても子の蝶番はこの軸のまま
                    RotateAround(t, t.position, Quaternion.AngleAxis(sign * CurlDeg[g] * scale * curl, axis));
                }
            }
        }

        private static readonly HumanBodyBones[][] LeftFingers =
        {
            new[] { HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.LeftThumbDistal },
            new[] { HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal },
            new[] { HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal },
            new[] { HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal },
            new[] { HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal },
        };

        private static readonly HumanBodyBones[][] RightFingers =
        {
            new[] { HumanBodyBones.RightThumbProximal, HumanBodyBones.RightThumbIntermediate, HumanBodyBones.RightThumbDistal },
            new[] { HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal },
            new[] { HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal },
            new[] { HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal },
            new[] { HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal },
        };

        /// <summary>
        /// 前腕を肘→手首の軸まわりに回す（回内・回外）。手首の位置は軸上なので動かない＝
        /// IKで運んだ握りもバット軸も保たれ、掌の向きだけが変わる。
        /// 掌をバットへ向けるのはここ（手首だけで90°回すと関節が折れて見える）。
        /// </summary>
        private static void RollForearm(Transform foreArm, Transform hand, float deg)
        {
            if (Mathf.Abs(deg) < 0.01f || foreArm == null || hand == null) return;
            Vector3 axis = (hand.position - foreArm.position).normalized;
            if (axis.sqrMagnitude < 0.5f) return;
            RotateAround(foreArm, foreArm.position, Quaternion.AngleAxis(deg, axis));
        }

        /// <summary>
        /// 手首を3軸で直す。軸は前腕・手の実測から組む（骨のローカル軸は素体依存で信用できない）。
        ///  - 曲げ: 前腕方向 u と手方向 h の成す角。目標へ**曲げ面の中だけ**で戻す
        ///  - ひねり: 手の長軸 h まわり＝拳の位置を動かさず掌の向きだけ回る
        ///  - 横振り: 前腕軸 u まわり＝曲げ面の外へ拳が振れる
        /// いずれも手の位置（手首）は動かさないので、握り位置とバット軸（AlignBat）に影響しない。
        ///
        /// 収録の左手首は全編95°（180=まっすぐ）＝人体の限界まで折れており、そのまま詰めると
        /// 「握れていない手」に見える。ただし**手ごと向きを反対の手へ寄せてはいけない**＝
        /// ねじれが±200°振れて腕が一回転して見える（2026-07-29 実害）。左右差は手で詰める。
        /// </summary>
        private static void FixWrist(Animator anim, HumanBodyBones foreArm, HumanBodyBones hand,
            HumanBodyBones knuckle, float targetDeg, float strength, float twistDeg, float sideDeg, float w)
        {
            var fa = B(anim, foreArm);
            var hd = B(anim, hand);
            var kn = B(anim, knuckle);
            if (fa == null || hd == null || kn == null) return;

            if (strength > 0.001f)
            {
                Vector3 u = (hd.position - fa.position).normalized;
                Vector3 h = (kn.position - hd.position).normalized;
                if (u.sqrMagnitude > 0.5f && h.sqrMagnitude > 0.5f)
                {
                    float cur = 180f - Vector3.Angle(u, h);      // 180=まっすぐ
                    float slack = 180f - cur;                    // まっすぐまでの残り
                    if (slack >= 0.5f)
                    {
                        float delta = (targetDeg - cur) * strength * w;
                        var full = Quaternion.FromToRotation(h, u);  // h を u に載せる＝まっすぐ
                        RotateAround(hd, hd.position,
                            Quaternion.SlerpUnclamped(Quaternion.identity, full, delta / slack));
                    }
                }
            }
            if (Mathf.Abs(twistDeg) > 0.01f)
            {
                Vector3 h = (kn.position - hd.position).normalized;   // 曲げで動くので採り直す
                if (h.sqrMagnitude > 0.5f)
                    RotateAround(hd, hd.position, Quaternion.AngleAxis(twistDeg * w, h));
            }
            if (Mathf.Abs(sideDeg) > 0.01f)
            {
                Vector3 u = (hd.position - fa.position).normalized;
                if (u.sqrMagnitude > 0.5f)
                    RotateAround(hd, hd.position, Quaternion.AngleAxis(sideDeg * w, u));
            }
        }

        // ---------------------------------------------------------------- 頭

        private static void FixHead(Animator anim, in DuelCalibration cal, float t, Vector3 aimPoint)
        {
            float w = cal.HeadStrength * (1f - Ramp(t, cal.HeadUntilSec - 0.20f, cal.HeadUntilSec));
            if (w <= 0f) return;
            var neck = B(anim, HumanBodyBones.Neck);
            var head = B(anim, HumanBodyBones.Head);
            if (head == null) return;
            // 顔の正面はバインドポーズ実測（素体依存＝骨のローカル軸は信用できない。design-18 §3.3）
            Vector3 face = head.TransformDirection(HeadForwardLocal);
            Vector3 to = aimPoint - head.position;
            to.y = 0f;
            if (to.sqrMagnitude < 1e-6f) return;
            float yaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg + cal.HeadYawOffsetDeg;
            Vector3 want = Quaternion.Euler(-cal.HeadElevDeg, yaw, 0f) * Vector3.forward;
            var full = Quaternion.FromToRotation(face, want);
            var target = Quaternion.SlerpUnclamped(Quaternion.identity, full, w);
            if (neck != null && cal.HeadNeckShare > 0f)
            {
                RotateAround(neck, neck.position,
                    Quaternion.SlerpUnclamped(Quaternion.identity, full, w * cal.HeadNeckShare));
                // 首で回したぶん顔も動いているので、頭は残りだけ回す
                Vector3 face2 = head.TransformDirection(HeadForwardLocal);
                Vector3 goal = target * face;
                RotateAround(head, head.position, Quaternion.FromToRotation(face2, goal));
            }
            else
            {
                RotateAround(head, head.position, target);
            }
        }

        /// <summary>
        /// 頭ボーンローカルでの顔の正面。<see cref="DuelStageRig"/> がスポーン時に実測した値を入れる
        /// （素体で符号が逆になるため。購入素体=+Z / UBC=-Z）。
        /// </summary>
        public static Vector3 HeadForwardLocal { get; set; } = Vector3.forward;

        // ---------------------------------------------------------------- 脚

        private static void FixKnee(Animator anim, in DuelCalibration cal, float t)
        {
            float w = Ramp(t, cal.KneeFromSec, cal.KneeFullSec)
                * (1f - Ramp(t, cal.KneeFullSec, cal.KneeToSec));
            if (w <= 0.001f || Mathf.Abs(cal.KneeDeg) < 0.01f) return;
            var up = B(anim, HumanBodyBones.LeftUpperLeg);
            var foot = B(anim, HumanBodyBones.LeftFoot);
            if (up == null || foot == null) return;
            Vector3 hip = up.position, ankle = foot.position;
            Vector3 axis = ankle - hip;
            if (axis.sqrMagnitude < 1e-6f) return;
            // 股関節→足首の軸まわりに脚ごと回す＝足首は軸上にあるので**接地は動かない**
            var footRot = foot.rotation;
            RotateAround(up, hip, Quaternion.AngleAxis(cal.KneeDeg * w, axis.normalized));
            foot.rotation = footRot;   // 足の向きは元のまま（つま先が回らない）
        }
    }
}
