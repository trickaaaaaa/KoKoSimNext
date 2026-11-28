using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 捕手の胸プロテクターを、いまの肩の位置と背骨の前傾に毎コマ合わせて置く。
    /// 胸当ての土台（gear-chest）は胸ボーンの子だが、しゃがんで上体を倒す動きは背骨の鎖へ配分されるため、
    /// 胸ボーン自体はほとんど傾かず、肩も起きた姿勢の高さから大きく外れる（実測: 背骨は鉛直から39°倒れ、
    /// 土台は0.8°。取り付け時に肩の高さで置いた胸当ては、しゃがむと腹〜ももの上まで下がっていた）。
    /// 起きたままの位置では倒れた上衣の中へ埋まるか、腹の前に浮き、正面から胸に見えなかった。
    /// 前傾角は腰→首の向きから測る（立てば0°＝取り付け時の位置と同じ）。置き方は
    /// 「メッシュの上端を肩の稜線（両上腕の中点）の少し上に合わせ、その点を回転の中心にして倒す」。
    /// ポーズは別の更新で書かれるので、評価の後（LateUpdate・遅い順）に当てる。
    /// </summary>
    [DefaultExecutionOrder(10000)]
    public sealed class DuelChestTilt : MonoBehaviour
    {
        public Transform Hips;
        public Transform Neck;
        public Transform LeftShoulder;
        public Transform RightShoulder;
        /// <summary>土台（gear-chest）。体前=+Z・右=+X・実寸[m]の空間。</summary>
        public Transform Container;
        /// <summary>胸当てメッシュの上端の高さ[m]（この部品の局所座標）。</summary>
        public float MeshTopLocalY;
        /// <summary>肩の稜線より上へ出す量[m]（首元の切り欠きの高さ）。</summary>
        public float ShoulderLift = 0.045f;
        /// <summary>調整用のずらし量[m]（DuelCalibration.CatcherChestOffset。傾けた後の向きで効く）。</summary>
        public Vector3 Offset;
        /// <summary>前傾角の上限[度]。素体の姿勢が崩れた瞬間に胸当てが暴れないようにする。</summary>
        public float MaxDeg = 70f;

        /// <summary>撮影の実測用: いまの前傾角と、土台の前向きの水平方位[度]。</summary>
        public float LastLeanDeg, LastFwdYawDeg;

        private void LateUpdate() => Apply();

        /// <summary>
        /// 置き直す。向きは土台（胸ボーン）の傾きに**依らず**、水平の前方位＋前傾角だけで決める。
        /// 胸ボーンはクリップによって大きく前へ倒れる（実測: 守備伝令で立って話す待機は胸ボーンが約49°前傾）。
        /// 前傾角を土台の局所へ掛けるだけだと、その倒れがそのまま胸当てに乗り、起きた背筋の前で
        /// 首のまわりへ「つばのように」浮いた（issue #1791）。
        /// </summary>
        public void Apply()
        {
            if (Hips == null || Neck == null || Container == null
                || LeftShoulder == null || RightShoulder == null) return;
            var spine = Neck.position - Hips.position;
            if (spine.sqrMagnitude < 1e-6f) return;
            spine.Normalize();
            var fwd = Container.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) return;
            fwd.Normalize();

            var lean = Mathf.Clamp(Mathf.Atan2(Vector3.Dot(spine, fwd), spine.y) * Mathf.Rad2Deg, 0f, MaxDeg);
            LastLeanDeg = lean;
            LastFwdYawDeg = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
            var worldRot = Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.Euler(lean, 0f, 0f);
            var rot = Quaternion.Inverse(Container.rotation) * worldRot;
            var shoulderWorld = (LeftShoulder.position + RightShoulder.position) * 0.5f + spine * ShoulderLift;
            var shoulder = Container.InverseTransformPoint(shoulderWorld);

            transform.localRotation = rot;
            transform.localPosition = shoulder - rot * new Vector3(0f, MeshTopLocalY, 0f) + rot * Offset;
        }
    }
}
