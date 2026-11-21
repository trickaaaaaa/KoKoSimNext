using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 対決ビューのポーズ補正の共通部品（design-18 §7-2）。打者の握り（<see cref="DuelBatterPose"/>）と
    /// 捕手のミット（<see cref="DuelCatcherPose"/>）が同じ解き方を使うので実装を1箇所に置く。
    ///
    /// 掟: リターゲット後の骨を直接触る＝クリップ内部の緩急は改変しない。
    /// </summary>
    internal static class DuelIk
    {
        /// <summary>pivot を中心に回す。子はUnityの階層でついてくる。</summary>
        internal static void RotateAround(Transform t, Vector3 pivot, Quaternion q)
        {
            t.rotation = q * t.rotation;
            t.position = pivot + q * (t.position - pivot);
        }

        /// <summary>
        /// 2ボーンIK。tip（手首・足首）を target へ運ぶ。
        /// 肘（膝）は**今向いている側にそのまま置く**＝解の分岐を選ばないので裏返らない
        /// （角度で組むと符号を1つ間違えるだけで肘が180°裏返り、脇が開いた絵になる。2026-07-29 実害）。
        /// </summary>
        internal static void TwoBoneIk(Transform root, Transform mid, Transform tip, Vector3 target,
            float poleDeg)
        {
            Vector3 s = root.position, e = mid.position, w = tip.position;
            float l1 = Vector3.Distance(s, e), l2 = Vector3.Distance(e, w);
            if (l1 < 1e-5f || l2 < 1e-5f) return;
            Vector3 t = target;
            float d = Vector3.Distance(s, t);
            float dmax = (l1 + l2) * 0.999f;
            float dmin = Mathf.Abs(l1 - l2) * 1.001f + 1e-4f;
            if (d > dmax) { t = s + (t - s).normalized * dmax; d = dmax; }
            else if (d < dmin) { t = s + (t - s).normalized * dmin; d = dmin; }
            Vector3 axis = (t - s).normalized;
            // 今の肘の「横へのはみ出し」方向＝ポール。これを保つ限り解は一意に決まる
            Vector3 pole = Vector3.ProjectOnPlane(e - s, axis);
            if (pole.sqrMagnitude < 1e-8f)
                pole = Vector3.ProjectOnPlane(Vector3.up, axis);
            if (pole.sqrMagnitude < 1e-8f) return;
            pole.Normalize();
            if (Mathf.Abs(poleDeg) > 0.01f)
                pole = Quaternion.AngleAxis(poleDeg, axis) * pole;
            float cos = Mathf.Clamp((l1 * l1 + d * d - l2 * l2) / (2f * l1 * d), -1f, 1f);
            float sin = Mathf.Sqrt(Mathf.Max(0f, 1f - cos * cos));
            Vector3 eNew = s + axis * (l1 * cos) + pole * (l1 * sin);
            RotateAround(root, s, Quaternion.FromToRotation(e - s, eNew - s));
            Vector3 e2 = mid.position, w2 = tip.position;
            RotateAround(mid, e2, Quaternion.FromToRotation(w2 - e2, t - e2));
        }

        /// <summary>肩から届く範囲まで target を戻す（肘のロック防止）。</summary>
        internal static Vector3 ClampReach(Transform root, Transform mid, Transform tip,
            Vector3 origin, Vector3 target, float guard)
        {
            float limit = (Vector3.Distance(root.position, mid.position)
                + Vector3.Distance(mid.position, tip.position)) * guard;
            Vector3 s = root.position;
            if ((target - s).magnitude <= limit) return target;
            float lo = 0f, hi = 1f;
            for (int i = 0; i < 14; i++)
            {
                float m = (lo + hi) * 0.5f;
                if ((Vector3.Lerp(origin, target, m) - s).magnitude <= limit) lo = m; else hi = m;
            }
            return Vector3.Lerp(origin, target, lo);
        }
    }
}
