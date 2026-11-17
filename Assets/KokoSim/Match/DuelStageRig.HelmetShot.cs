#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    public sealed partial class DuelStageRig
    {
        /// <summary>打者ヘルメット中心と向き（正面・右）を stage-local で返す（寄りの絵の狙い用）。</summary>
        internal bool BatterHelmetFrameForCapture(out Vector3 center, out Vector3 fwd, out Vector3 right)
            => HelmetFrame(_helmetMount, out center, out fwd, out right);

        /// <summary>走者枠0を stage-local の位置へ立てて有効化し、そのヘルメットの中心と向きを返す。</summary>
        internal bool RunnerHelmetFrameForCapture(Vector3 localPos, out Vector3 center, out Vector3 fwd, out Vector3 right)
        {
            center = fwd = right = Vector3.zero;
            EnsureFieldCast();
            if (_runnerCast.Count == 0 || _runnerHelmetMounts.Count == 0) return false;
            var a = _runnerCast[0];
            a.Go.SetActive(true);
            a.Enabled = true;
            a.Go.transform.position = _root.transform.TransformPoint(localPos);
            StepActor(a, 0.001f);
            return HelmetFrame(_runnerHelmetMounts[0], out center, out fwd, out right);
        }

        /// <summary>
        /// 役者の Animator（EditMode テストの実測用）。0=打者 / 1=投手 / 2=捕手 / 4=球審。組めていなければ null。
        /// </summary>
        internal Animator ActorAnimatorForCapture(int who)
        {
            var a = who == 0 ? _batter : who == 1 ? _pitcher : who == 2 ? _catcher : who == 4 ? _umpire : null;
            return a?.Animator;
        }

        /// <summary>
        /// 顔の寄りの狙い用（のっぺらぼうの確認）。0=打者 / 1=投手 / 2=捕手 / 3=走者枠0（RunnerHelmetFrameForCapture で立てた後）/ 4=球審。
        /// 中心は頭ボーンから頭蓋の上へ 0.09m、向きは素立ちで実測した顔の前（Actor.HeadFwdLocal）。
        /// </summary>
        internal bool HeadFrameForCapture(int who, out Vector3 center, out Vector3 fwd, out Vector3 up)
        {
            center = fwd = up = Vector3.zero;
            var a = who == 0 ? _batter : who == 1 ? _pitcher : who == 2 ? _catcher : who == 4 ? _umpire
                : (_runnerCast.Count > 0 ? _runnerCast[0] : null);
            if (a == null || a.Animator == null) return false;
            var head = a.Animator.GetBoneTransform(HumanBodyBones.Head);
            if (head == null) return false;
            up = _root.transform.InverseTransformDirection(head.TransformDirection(a.HeadUpLocal)).normalized;
            fwd = _root.transform.InverseTransformDirection(head.TransformDirection(a.HeadFwdLocal)).normalized;
            center = _root.transform.InverseTransformPoint(head.position) + up * 0.09f;
            return true;
        }

        /// <summary>
        /// 【実測】ヘルメットのめり込み。who=0 打者 / 3 走者枠0。現ポーズのスキンを焼き、頭・体の頂点のうち
        /// **ヘルメットの殻の肉の中**にあるもの（＝肌・襟が殻を突き抜けて見える側）をメッシュ別に数える。
        /// 内外は4方向の光線の交差数の偶奇の多数決（殻の中の空洞から上へ抜ける光線は内面＋外面で偶数＝外扱い）。
        /// 最寄り面の符号で判定すると、辺・頂点が最寄りになる所で符号が暴れて使えなかった（2026-11-15 実測）。
        /// 深さは最寄りの殻の面までの距離。位置は頭ボーン基準の「上[m]」「前[m]」「横[m]」で添える。
        /// </summary>
        internal string HelmetClipDiagForCapture(int who)
        {
            var a = who == 0 ? _batter : (_runnerCast.Count > 0 ? _runnerCast[0] : null);
            if (a == null) return "no actor";
            Transform helm = null;
            foreach (var t in a.Go.GetComponentsInChildren<Transform>(true))
                if (t.name == "helmet-mesh") { helm = t; break; }
            if (helm == null) return "no helmet-mesh";
            var hm = helm.GetComponent<MeshFilter>().sharedMesh;
            var hv = hm.vertices; var ht = hm.triangles;
            var hw = new Vector3[hv.Length];
            for (int i = 0; i < hv.Length; i++) hw[i] = helm.TransformPoint(hv[i]);
            var head = a.Animator.GetBoneTransform(HumanBodyBones.Head);
            var up = head.TransformDirection(a.HeadUpLocal).normalized;
            var fwd = head.TransformDirection(a.HeadFwdLocal).normalized;
            var hb = new Bounds(hw[0], Vector3.zero);
            foreach (var p in hw) hb.Encapsulate(p);
            var dirs = new[]
            {
                new Vector3(0.13f, 1f, 0.07f).normalized, new Vector3(1f, 0.11f, 0.17f).normalized,
                new Vector3(-1f, 0.19f, -0.05f).normalized, new Vector3(0.09f, 0.23f, 1f).normalized,
                new Vector3(-0.15f, 0.21f, -1f).normalized,
            };

            var sb = new System.Text.StringBuilder();
            var baked = new Mesh();
            foreach (var smr in a.Go.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (!smr.gameObject.activeInHierarchy || smr.sharedMesh == null) continue;
                smr.BakeMesh(baked, true);
                var m = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
                var bv = baked.vertices;
                int n = 0; float deep = 0f; Vector3 worst = Vector3.zero;
                var seen = new HashSet<Vector3Int>();
                for (int i = 0; i < bv.Length; i++)
                {
                    var p = m.MultiplyPoint3x4(bv[i]);
                    if (!hb.Contains(p)) continue;
                    if (!seen.Add(Vector3Int.RoundToInt(p * 1e4f))) continue;   // UV継ぎ目で割れた同位置の頂点は1つ
                    int odd = 0;
                    foreach (var d in dirs) if ((RayHits(p, d, hw, ht) & 1) == 1) odd++;
                    if (odd * 2 <= dirs.Length) continue;
                    n++;
                    float dist = NearestDistance(p, hw, ht);
                    if (dist > deep) { deep = dist; worst = p; }
                }
                if (n == 0) continue;
                var w = worst - head.position;
                sb.Append(smr.name).Append(": 殻の肉の中の頂点=").Append(n)
                  .Append(" 最深").Append((deep * 1000f).ToString("F1")).Append("mm @(上")
                  .Append(Vector3.Dot(w, up).ToString("F3")).Append(" 前").Append(Vector3.Dot(w, fwd).ToString("F3"))
                  .Append(" 横").Append(Vector3.Dot(w, Vector3.Cross(up, fwd).normalized).ToString("F3")).Append(")").AppendLine();
            }
            Object.Destroy(baked);
            return sb.Length == 0 ? "めり込みなし" : sb.ToString();
        }

        private static int RayHits(Vector3 o, Vector3 d, Vector3[] v, int[] tris)
        {
            int hits = 0;
            for (int t = 0; t < tris.Length; t += 3)
            {
                var a = v[tris[t]]; var e1 = v[tris[t + 1]] - a; var e2 = v[tris[t + 2]] - a;
                var pv = Vector3.Cross(d, e2);
                float det = Vector3.Dot(e1, pv);
                if (Mathf.Abs(det) < 1e-12f) continue;
                float inv = 1f / det;
                var tv = o - a;
                float u = Vector3.Dot(tv, pv) * inv;
                if (u < 0f || u > 1f) continue;
                var qv = Vector3.Cross(tv, e1);
                float vv = Vector3.Dot(d, qv) * inv;
                if (vv < 0f || u + vv > 1f) continue;
                if (Vector3.Dot(e2, qv) * inv > 0f) hits++;
            }
            return hits;
        }

        private static float NearestDistance(Vector3 p, Vector3[] v, int[] tris)
        {
            float best = float.MaxValue;
            for (int t = 0; t < tris.Length; t += 3)
                best = Mathf.Min(best, (p - ClosestOnTriangle(p, v[tris[t]], v[tris[t + 1]], v[tris[t + 2]])).sqrMagnitude);
            return Mathf.Sqrt(best);
        }

        private static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            var ab = b - a; var ac = c - a; var ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return a;
            var bp = p - b; float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return b;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));
            var cp = p - c; float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return c;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
            float denom = 1f / (va + vb + vc);
            return a + ab * (vb * denom) + ac * (vc * denom);
        }

        private bool HelmetFrame(Transform mount, out Vector3 center, out Vector3 fwd, out Vector3 right)
        {
            center = fwd = right = Vector3.zero;
            if (mount == null) return false;
            var c = mount.parent;
            center = _root.transform.InverseTransformPoint(c.TransformPoint(HelmetPivot));
            fwd = _root.transform.InverseTransformDirection(c.forward).normalized;
            right = _root.transform.InverseTransformDirection(c.right).normalized;
            return true;
        }
    }
}
#endif
