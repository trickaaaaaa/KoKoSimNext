using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 捕手の防具（胸プロテクター・レガース）が体に沿っているかの実測（issue #1791）。
    /// 立った姿勢・歩く姿勢・しゃがんだ姿勢（対決ビュー）で同じ式を当てて並べる＝姿勢で崩れるかを数字で見る。
    /// 表示専用。決定論・engine には触れない。
    /// </summary>
    public sealed partial class DuelStageRig
    {
        /// <summary>胸プロテクターの裏面が上衣の胸の面より前へ離れている量[m]。測れなければ null。</summary>
        internal readonly struct ChestGearGap
        {
            public readonly float GapM;          // 防具の裏面（最後方）−上衣の前面（最前方）。正＝浮いている
            public readonly float LeanDeg;       // 背骨の前傾角
            public ChestGearGap(float gap, float lean) { GapM = gap; LeanDeg = lean; }
        }

        /// <summary>レガースの1本ぶんの実測。</summary>
        internal readonly struct LegGuardMeasure
        {
            public readonly bool Present;          // メッシュがあり、描かれる状態か
            public readonly float FrontGapM;       // 防具の前面−ズボンの前面（脛の前向きに沿って）。正＝ズボンより前へ出て見える
            public readonly float BackGapM;        // 防具の背面−ズボンの前面。負が深い＝ズボンの中に埋まっている
            public readonly float OffsetDeg;       // 防具の置かれた向きと体の前との開き（脛の軸まわり・度）
            public readonly float TMin, TMax;      // 防具が脛の軸（膝=0・足首=1）のどこにあるか
            public LegGuardMeasure(bool present, float front, float back, float offsetDeg, float tMin, float tMax)
            { Present = present; FrontGapM = front; BackGapM = back; OffsetDeg = offsetDeg; TMin = tMin; TMax = tMax; }
        }

        private static readonly List<Vector3> DiagVerts = new();

        private static bool BakeWorld(SkinnedMeshRenderer smr, Mesh scratch, List<Vector3> world)
        {
            world.Clear();
            if (smr == null || !smr.enabled || smr.sharedMesh == null || !smr.gameObject.activeInHierarchy) return false;
            smr.BakeMesh(scratch, true);
            var t = smr.transform;
            var m = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
            scratch.GetVertices(DiagVerts);
            foreach (var v in DiagVerts) world.Add(m.MultiplyPoint3x4(v));
            return true;
        }

        /// <summary>体の枠（上・右・前）を肩と背骨から作る。立っても倒れていても同じ式で前が決まる。</summary>
        private static bool BodyFrame(Animator an, out Vector3 up, out Vector3 right, out Vector3 front)
        {
            up = right = front = Vector3.zero;
            var hips = an.GetBoneTransform(HumanBodyBones.Hips);
            var neck = an.GetBoneTransform(HumanBodyBones.Neck) ?? an.GetBoneTransform(HumanBodyBones.Head);
            var ls = an.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            var rs = an.GetBoneTransform(HumanBodyBones.RightUpperArm);
            if (hips == null || neck == null || ls == null || rs == null) return false;
            up = (neck.position - hips.position).normalized;
            right = rs.position - ls.position;
            right = (right - Vector3.Dot(right, up) * up).normalized;
            front = Vector3.Cross(right, up).normalized;
            return up.sqrMagnitude > 0.5f && right.sqrMagnitude > 0.5f;
        }

        /// <summary>胸プロテクターと上衣の胸の面との前後の距離。</summary>
        internal ChestGearGap? MeasureChestGear()
        {
            if (_catcher == null || _catcher.Go == null || _cChestMount == null) return null;
            var mf = _cChestMount.GetComponentInChildren<MeshFilter>();
            var mr = mf != null ? mf.GetComponent<MeshRenderer>() : null;
            if (mf == null || mr == null || !mr.enabled || !mr.gameObject.activeInHierarchy) return null;
            if (!BodyFrame(_catcher.Animator, out var up, out var right, out var front)) return null;

            var center = mr.bounds.center;
            bool InWindow(Vector3 w, float halfX, float halfY)
            {
                var p = w - center;
                return Mathf.Abs(Vector3.Dot(p, right)) < halfX && Mathf.Abs(Vector3.Dot(p, up)) < halfY;
            }

            float torsoFront = float.NegativeInfinity;
            var scratch = new Mesh();
            var world = new List<Vector3>();
            foreach (var smr in _catcher.Go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!BakeWorld(smr, scratch, world)) continue;
                foreach (var w in world)
                    if (InWindow(w, 0.10f, 0.10f)) torsoFront = Mathf.Max(torsoFront, Vector3.Dot(w, front));
            }
            Object.Destroy(scratch);
            if (float.IsNegativeInfinity(torsoFront)) return null;

            float gearBack = float.PositiveInfinity;
            var gm = mf.sharedMesh;
            var l2w = mf.transform.localToWorldMatrix;
            foreach (var v in gm.vertices)
            {
                var w = l2w.MultiplyPoint3x4(v);
                if (InWindow(w, 0.10f, 0.10f)) gearBack = Mathf.Min(gearBack, Vector3.Dot(w, front));
            }
            if (float.IsPositiveInfinity(gearBack)) return null;

            var spine = up;
            float lean = Mathf.Atan2(Vector3.Dot(spine, front), spine.y) * Mathf.Rad2Deg;
            return new ChestGearGap(gearBack - torsoFront, lean);
        }

        /// <summary>レガース（左=0・右=1）のズボンとの位置関係。</summary>
        internal LegGuardMeasure MeasureLegGuard(int side)
        {
            var none = new LegGuardMeasure(false, 0f, 0f, 0f, 0f, 0f);
            if (_catcher == null || _catcher.Go == null) return none;
            var an = _catcher.Animator;
            var calf = an.GetBoneTransform(side == 0 ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg);
            var foot = an.GetBoneTransform(side == 0 ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot);
            if (calf == null || foot == null || !BodyFrame(an, out var up, out var right, out var front)) return none;
            var holder = calf.Find("gear-leg");
            var lm = holder != null ? holder.GetComponentInChildren<MeshFilter>() : null;
            var lr = lm != null ? lm.GetComponent<MeshRenderer>() : null;
            if (lm == null || lr == null || !lr.enabled || !lr.gameObject.activeInHierarchy) return none;

            var knee = calf.position;
            var shin = foot.position - knee;
            float len = shin.magnitude;
            if (len < 0.05f) return none;
            var a = shin / len;

            // 脛の軸に直交する面での「体の前」。ここを基準にズボンの前面と防具の前面を比べる
            var u = front - Vector3.Dot(front, a) * a;
            if (u.sqrMagnitude < 1e-6f) return none;
            u.Normalize();

            float pantsFront = float.NegativeInfinity;
            var scratch = new Mesh();
            var world = new List<Vector3>();
            foreach (var smr in _catcher.Go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!BakeWorld(smr, scratch, world)) continue;
                foreach (var w in world)
                {
                    var p = w - knee;
                    float t = Vector3.Dot(p, a) / len;
                    if (t < 0.2f || t > 0.85f) continue;
                    var radial = p - Vector3.Dot(p, a) * a;
                    if (radial.magnitude > 0.16f) continue;
                    pantsFront = Mathf.Max(pantsFront, Vector3.Dot(radial, u));
                }
            }
            Object.Destroy(scratch);
            if (float.IsNegativeInfinity(pantsFront)) return none;

            float gFront = float.NegativeInfinity, gBack = float.PositiveInfinity, tMin = float.PositiveInfinity, tMax = float.NegativeInfinity;
            var l2w = lm.transform.localToWorldMatrix;
            var sum = Vector3.zero;
            int n = 0;
            foreach (var v in lm.sharedMesh.vertices)
            {
                var w = l2w.MultiplyPoint3x4(v);
                var p = w - knee;
                float t = Vector3.Dot(p, a) / len;
                var radial = p - Vector3.Dot(p, a) * a;
                float d = Vector3.Dot(radial, u);
                gFront = Mathf.Max(gFront, d);
                gBack = Mathf.Min(gBack, d);
                tMin = Mathf.Min(tMin, t);
                tMax = Mathf.Max(tMax, t);
                sum += radial;
                n++;
            }
            if (n == 0) return none;
            var mean = sum / n;
            float off = mean.magnitude < 1e-4f ? 0f : Vector3.SignedAngle(u, mean.normalized, a);
            return new LegGuardMeasure(true, gFront - pantsFront, gBack - pantsFront, off, tMin, tMax);
        }

        /// <summary>撮影の実測（recipe の probes: CatcherGearDiag）。立つ・歩く・しゃがむの同じ式の数字。</summary>
        internal string CatcherGearDiagForCapture()
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            var chest = MeasureChestGear();
            sb.Append("chest=");
            if (chest.HasValue) sb.Append(chest.Value.GapM.ToString("F3", ci)).Append("(lean ").Append(chest.Value.LeanDeg.ToString("F0", ci)).Append(')');
            else sb.Append("none");
            for (int side = 0; side < 2; side++)
            {
                var l = MeasureLegGuard(side);
                sb.Append(side == 0 ? " legL=" : " legR=");
                if (!l.Present) { sb.Append("none"); continue; }
                sb.Append("front").Append(l.FrontGapM.ToString("F3", ci))
                  .Append("/back").Append(l.BackGapM.ToString("F3", ci))
                  .Append("/off").Append(l.OffsetDeg.ToString("F0", ci))
                  .Append("/t").Append(l.TMin.ToString("F2", ci)).Append('-').Append(l.TMax.ToString("F2", ci));
            }
            return sb.ToString();
        }
    }
}
