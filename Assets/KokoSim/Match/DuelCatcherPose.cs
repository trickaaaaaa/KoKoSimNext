using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 捕手のミットをボールの実着弾点へ合わせる実行時補正（design-18 §4.5「捕球・ミートの端数合わせは
    /// Event直前の短時間IKで吸収する」）。
    ///
    /// なぜ要るか: 購入パックの捕球クリップは5方向（中央・高め・低め・左・右）しか無く、コースに一番
    /// 近い1本を選んでも残差が最大0.3m出る（実測 2026-07-30。高め×外角のような複合コーナーは
    /// どの1本も持っていない）。ボールは実データの弾道なので**動かせない**＝ミットを寄せる。
    ///
    /// 掟: 効かせるのは捕球の瞬間の前後だけ（台形の重み）。収録の吸収動作（ミットが引かれる）は
    /// 捕球後に重みを抜いて素のまま見せる＝球はその時点で手ボーンの子なので一緒に引かれる。
    /// </summary>
    internal static class DuelCatcherPose
    {
        /// <summary>捕球の何秒前からミットを寄せ始めるか（腕が動いて見える最短。12fpsで2コマ強）。</summary>
        internal const float LeadSeconds = 0.18f;

        /// <summary>
        /// 捕球の何秒前に寄せ切るか。**1ポーズコマ（12fps=83ms）以上**にすること。
        /// ポーズは12fpsなので、捕球の瞬間ちょうどで重み1になる台形だと、実際に描かれる直前のコマは
        /// 重み0.7前後＝ミットが寄り切っておらず、球がミットの外（実測0.19m）で止まる（2026-07-30 実害）。
        /// 現実の捕手もミットは球より先に来ているので、絵としてもこちらが正しい。
        /// </summary>
        internal const float SettleSeconds = 0.10f;

        /// <summary>捕球後、何秒かけて収録のポーズへ戻すか（吸収動作を殺さないため短く）。</summary>
        internal const float ReleaseSeconds = 0.12f;

        /// <summary>肩→手首をこの割合までに抑える（肘のロック防止。打者の握りと同じ流儀）。</summary>
        private const float ReachGuard = 0.98f;

        /// <summary>
        /// 腕が届かないときに胴を倒して肩を寄せる上限[度]。ゾーンの外（高め・大きく外れた球）は
        /// 腕だけでは 0.2m 届かない（実測: y=1.15 の高めで残差0.199m。しゃがんだ捕手の肩から
        /// 腕長0.55mしかない）。実際の捕手も体を伸ばして捕るので、足りない分だけ胴で稼ぐ。
        /// 大きくすると座ったまま上体だけ反る不自然な絵になるので、伸び上がりは控えめに止める。
        /// </summary>
        private const float TorsoAssistMaxDeg = 14f;

        /// <summary>
        /// 台形の重み。<paramref name="catchAt"/> は**発射からの捕球時刻**（球の時計）。
        /// クリップ内時刻で駆動しないのは、捕球クリップの差し替えコマと12fpsの刻みに引っかかると
        /// 重みが飛ぶため（球の時計はフルレートで単調）。
        /// </summary>
        internal static float Weight(float ballTime, float catchAt)
        {
            float settled = catchAt - SettleSeconds;
            if (ballTime <= settled - LeadSeconds) return 0f;
            if (ballTime < settled) return Mathf.InverseLerp(settled - LeadSeconds, settled, ballTime);
            if (ballTime <= catchAt) return 1f;
            return 1f - Mathf.InverseLerp(catchAt, catchAt + ReleaseSeconds, ballTime);
        }

        /// <summary>
        /// ミットの中心（手首→中指付け根の中点。手ボーン原点は手首なので、そのまま運ぶと
        /// ボールが手首に刺さる。design-18 §3.3.1 の掟）を <paramref name="target"/> へ寄せる。
        /// </summary>
        internal static void ReachMitt(Animator anim, Vector3 target, float weight)
        {
            if (anim == null || weight <= 0f) return;
            var upper = anim.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            var lower = anim.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            var hand = anim.GetBoneTransform(HumanBodyBones.LeftHand);
            if (upper == null || lower == null || hand == null) return;

            float w = Mathf.Clamp01(weight);
            var wrist = target - MittCenterOffset(anim, hand);
            LeanTorso(anim, upper, lower, hand, wrist, w);
            var goal = Vector3.Lerp(hand.position, wrist, w);
            goal = DuelIk.ClampReach(upper, lower, hand, hand.position, goal, ReachGuard);
            DuelIk.TwoBoneIk(upper, lower, hand, goal, 0f);
        }

        /// <summary>
        /// 腕の届く範囲を超えた分だけ胴を倒して肩を寄せる（届く球では何もしない）。
        /// 回すのは胸＝肩・首・両腕がまとまって付いてくる＝「体を伸ばして捕る」形になる。
        /// 腰から回すと足が浮くので触らない。
        /// </summary>
        private static void LeanTorso(Animator anim, Transform upper, Transform lower, Transform hand,
            Vector3 wristGoal, float weight)
        {
            var chest = anim.GetBoneTransform(HumanBodyBones.UpperChest)
                        ?? anim.GetBoneTransform(HumanBodyBones.Chest)
                        ?? anim.GetBoneTransform(HumanBodyBones.Spine);
            if (chest == null) return;
            float armLength = Vector3.Distance(upper.position, lower.position)
                              + Vector3.Distance(lower.position, hand.position);
            var shoulder = upper.position;
            var toGoal = wristGoal - shoulder;
            float shortfall = toGoal.magnitude - armLength * ReachGuard;
            if (shortfall <= 0f) return;

            var pivot = chest.position;
            var arm = shoulder - pivot;
            if (arm.sqrMagnitude < 1e-6f) return;
            // 肩を「足りない分だけ」目標へ寄せた位置を作り、そこへ胸を回す（長さは回転で保たれる）
            var wanted = shoulder + toGoal.normalized * shortfall * weight;
            var q = Quaternion.FromToRotation(arm, wanted - pivot);
            q = Quaternion.RotateTowards(Quaternion.identity, q, TorsoAssistMaxDeg * weight);
            DuelIk.RotateAround(chest, pivot, q);
        }

        /// <summary>ミット中心の現在位置（実測・診断と受け入れ判定の単一ソース）。</summary>
        internal static Vector3 MittCenter(Animator anim)
        {
            if (anim == null) return Vector3.zero;
            var hand = anim.GetBoneTransform(HumanBodyBones.LeftHand);
            if (hand == null) return Vector3.zero;
            return hand.position + MittCenterOffset(anim, hand);
        }

        /// <summary>手首からミット中心までのずれ（現在の姿勢での実測）。</summary>
        private static Vector3 MittCenterOffset(Animator anim, Transform hand)
        {
            var knuckle = anim.GetBoneTransform(HumanBodyBones.LeftMiddleProximal);
            return knuckle != null ? (knuckle.position - hand.position) * 0.5f : Vector3.zero;
        }
    }
}
