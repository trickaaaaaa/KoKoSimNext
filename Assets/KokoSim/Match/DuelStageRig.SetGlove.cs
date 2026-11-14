using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// セットポジションで投げる手（握り）をグラブで隠す（issue #927）。
    /// </summary>
    public sealed partial class DuelStageRig
    {
        /// <summary>
        /// セット姿勢でグラブ側の手首を掌側へ曲げる角度[度]（掌屈）。グラブの指先側がポケットの向き＝握りの側へ倒れ、
        /// 握った手を外からかぶせて覆う。
        /// </summary>
        public static float SetGloveFlexDeg = 58f;

        /// <summary>
        /// 前腕の軸まわりのひねり[度]。掌屈だけ（58°）でも握りは大きく隠れるが、ひねりを足すと
        /// グラブの縁が握りの残りを覆う。値は掃引の実測（2026-11-14・右投げ）:
        /// 見えている握りの頂点（中継カメラ/二塁走者）＝ 収録のまま 344/318 → 掌屈60° 43/32 → 58°+30° 10/12。
        /// 左投げ（鏡映）は 292/295 → 47/13。掌屈は 65° を超えると指先側が握りを通り過ぎてまた見え始める（75°で277）。
        /// </summary>
        public static float SetGloveRollDeg = 30f;

        /// <summary>セット補正の効き（0〜1）。セットで静止している間は1へ、投球へ入ったら0へ、<see cref="CrossfadeSeconds"/>で寄せる。</summary>
        private float _setGloveW;

        private Transform _setGlove;   // 投手の prop-glove（初回に探して持つ）

        /// <summary>
        /// セット補正の効きをプレー時間で進める（Tick の頭で毎フレーム）。駆動はクリップ内時刻でなく状態＝
        /// 「セットで静止しているか」（<see cref="PitcherAwaitsWindup"/>）。クリップ差し替えのコマで飛ばないため。
        /// </summary>
        private void AdvanceSetGloveWeight(float dt)
        {
            float target = PitcherAwaitsWindup ? 1f : 0f;
            _setGloveW = Mathf.MoveTowards(_setGloveW, target, dt / CrossfadeSeconds);
        }

        /// <summary>
        /// セット姿勢でグラブ側の手首を回し、投げる手（握り）をグラブで外から覆う。
        /// ポーズを評価した直後だけ呼ぶ（相対回転なので二度当てると寄りすぎる）。
        ///
        /// 収録のセットは、グラブを盆のように寝かせてポケットを二塁側（中継カメラ）へ向け、握った手をその上に
        /// 載せている＝握りが丸見え。手首を掌側へ曲げる（掌屈）とグラブの指先側が握りの上へ倒れ、外からかぶさる。
        /// グラブを狙いの位置へ動かす解き方（手首まわりの回転だけで中心を握りの外へ）は、手首からグラブ中心まで
        /// 数cmしか無く1cmも動かなかった（2026-11-14 実測）＝向きを直接決める。
        /// </summary>
        private void FixPitcherSetGlove()
        {
            if (_setGloveW <= 0f || _pitcher == null || _pitcher.Animator == null) return;
            var anim = _pitcher.Animator;
            var hand = anim.GetBoneTransform(HumanBodyBones.LeftHand);
            var fore = anim.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            if (hand == null || hand.parent == null) return;
            if (_setGlove == null)
                foreach (var t in _pitcher.Go.GetComponentsInChildren<Transform>(true))
                    if (t.name == "prop-glove") { _setGlove = t; break; }
            if (_setGlove == null) return;

            // 軸はグラブ自身の向き（+Y＝指先・+Z＝ポケット）から取り、手首の親（前腕）のローカルで回す＝
            // 鏡映親（左投手）の下でも同じ式で効く（ワールド回転を負スケールの下で直接書くと手系が裏返る）
            var par = hand.parent;
            var fingers = par.InverseTransformDirection(_setGlove.up);
            var pocket = par.InverseTransformDirection(_setGlove.forward);
            var flexAxis = Vector3.Cross(fingers, pocket);   // 指先をポケットの側へ倒す軸
            var q = Quaternion.AngleAxis(SetGloveFlexDeg * _setGloveW, flexAxis.normalized);
            if (fore != null && SetGloveRollDeg != 0f)
            {
                var armAxis = par.InverseTransformDirection(hand.position - fore.position).normalized;
                q = Quaternion.AngleAxis(SetGloveRollDeg * _setGloveW, armAxis) * q;
            }
            hand.localRotation = q * hand.localRotation;
        }

        /// <summary>【診断用】セット補正を掛け直して測る（掃引用）。ポーズは今のコマを評価し直してから当てる。</summary>
        public string SweepSetGloveForCapture(float flexDeg, float rollDeg)
        {
            SetGloveFlexDeg = flexDeg;
            SetGloveRollDeg = rollDeg;
            if (_pitcher == null) return "no pitcher";
            EvaluateGraph(_pitcher, 0f);
            _setGloveW = 1f;
            FixPitcherSetGlove();
            return SetGloveDiagForCapture();
        }

        /// <summary>二塁走者の目の位置（ステージローカル・本塁原点・+z=マウンド）。二塁ベース 38.8m の少し三塁寄り。</summary>
        private static readonly Vector3 SecondRunnerEye = new(-1.0f, 1.6f, 38.0f);

        /// <summary>
        /// 【診断用】投手の投げる手の皮膚頂点のうち、対決ビューのカメラ・二塁走者の目から見えている数を返す。
        /// 遮るものは投手自身の体（BakeMesh）とグラブ。手の骨（と子孫）に重み0.5以上の頂点だけを数える
        /// （半径で拾うと前腕まで入る＝design-18 §3.3.2 の 4）。
        /// </summary>
        public string SetGloveDiagForCapture()
        {
            if (_pitcher == null || _pitcher.Animator == null || _camera == null) return "no pitcher";
            var anim = _pitcher.Animator;
            bool mirrored = _pitcher.Mirrored;
            var throwHand = anim.GetBoneTransform(HumanBodyBones.RightHand);
            var gloveHand = anim.GetBoneTransform(HumanBodyBones.LeftHand);
            if (throwHand == null || gloveHand == null) return "no hand bones";

            var occluders = new List<Vector3[]>();   // 三角形の頂点（ワールド）を3つずつ並べた配列
            var handVerts = new List<Vector3>();
            foreach (var smr in _pitcher.Go.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (!smr.gameObject.activeInHierarchy || smr.sharedMesh == null) continue;
                var baked = new Mesh();
                smr.BakeMesh(baked, false);   // 縮尺なし＋TRS(位置,回転)で世界へ（実測: 手の頂点重心と手の骨が3mmで一致）
                var m = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
                var vs = baked.vertices;
                for (int i = 0; i < vs.Length; i++) vs[i] = m.MultiplyPoint3x4(vs[i]);
                occluders.Add(Triangles(vs, baked.triangles));
                if (smr.sharedMesh.isReadable)
                {
                    var bones = smr.bones;
                    var isHand = new bool[bones.Length];
                    for (int b = 0; b < bones.Length; b++)
                        isHand[b] = bones[b] != null && (bones[b] == throwHand || bones[b].IsChildOf(throwHand));
                    var w = smr.sharedMesh.boneWeights;
                    for (int v = 0; v < w.Length && v < vs.Length; v++)
                    {
                        float s = 0f;
                        if (w[v].boneIndex0 < isHand.Length && isHand[w[v].boneIndex0]) s += w[v].weight0;
                        if (w[v].boneIndex1 < isHand.Length && isHand[w[v].boneIndex1]) s += w[v].weight1;
                        if (w[v].boneIndex2 < isHand.Length && isHand[w[v].boneIndex2]) s += w[v].weight2;
                        if (w[v].boneIndex3 < isHand.Length && isHand[w[v].boneIndex3]) s += w[v].weight3;
                        if (s >= 0.5f) handVerts.Add(vs[v]);
                    }
                }
                Object.Destroy(baked);
            }
            Transform glove = null;
            foreach (var t in _pitcher.Go.GetComponentsInChildren<Transform>(true))
                if (t.name == "prop-glove") { glove = t; break; }
            if (glove != null)
            {
                var mf = glove.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null && mf.sharedMesh.isReadable)
                {
                    var vs = mf.sharedMesh.vertices;
                    var m = glove.localToWorldMatrix;
                    for (int i = 0; i < vs.Length; i++) vs[i] = m.MultiplyPoint3x4(vs[i]);
                    occluders.Add(Triangles(vs, mf.sharedMesh.triangles));
                }
            }

            var sb = new StringBuilder();
            var centroid = Vector3.zero;
            foreach (var v in handVerts) centroid += v;
            if (handVerts.Count > 0) centroid /= handVerts.Count;
            sb.Append("clipT=").Append(_pitcher.Time.ToString("F2"))
              .Append(" mirrored=").Append(mirrored)
              .Append(" handVerts=").Append(handVerts.Count)
              .Append(" centroid-bone=").Append(Vector3.Distance(centroid, throwHand.position).ToString("F3")).Append("m")
              .Append(" glove=").Append(glove != null);
            foreach (var (name, eye) in new[]
                     {
                         ("camera", _camera.transform.position),
                         ("2B", _root.transform.TransformPoint(SecondRunnerEye)),
                     })
            {
                int visible = 0;
                foreach (var p in handVerts)
                    if (!Blocked(eye, p, occluders)) visible++;
                sb.Append(" | ").Append(name).Append(" visible=").Append(visible);
            }
            sb.Append("\n throwHand=").Append(_root.transform.InverseTransformPoint(throwHand.position).ToString("F3"))
              .Append(" gloveHand=").Append(_root.transform.InverseTransformPoint(gloveHand.position).ToString("F3"));
            if (glove != null)
                sb.Append(" glove=").Append(_root.transform.InverseTransformPoint(glove.position).ToString("F3"))
                  .Append(" pocket(+Z)=").Append(_root.transform.InverseTransformDirection(glove.forward).ToString("F2"))
                  .Append(" fingers(+Y)=").Append(_root.transform.InverseTransformDirection(glove.up).ToString("F2"));
            return sb.ToString();
        }

        private static Vector3[] Triangles(Vector3[] vs, int[] tris)
        {
            var o = new Vector3[tris.Length];
            for (int i = 0; i < tris.Length; i++) o[i] = vs[tris[i]];
            return o;
        }

        /// <summary>目から点までの線分が、点のごく手前（3mm）より前で何かの三角形に当たるか。</summary>
        private static bool Blocked(Vector3 eye, Vector3 p, List<Vector3[]> occluders)
        {
            var d = p - eye;
            float len = d.magnitude;
            if (len < 1e-4f) return false;
            d /= len;
            float limit = len - 0.003f;
            foreach (var tri in occluders)
            {
                for (int i = 0; i + 2 < tri.Length; i += 3)
                {
                    if (RayTri(eye, d, tri[i], tri[i + 1], tri[i + 2], out float t) && t > 1e-4f && t < limit)
                        return true;
                }
            }
            return false;
        }

        private static bool RayTri(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
        {
            t = 0f;
            var e1 = b - a;
            var e2 = c - a;
            var p = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, p);
            if (det > -1e-9f && det < 1e-9f) return false;
            float inv = 1f / det;
            var s = o - a;
            float u = Vector3.Dot(s, p) * inv;
            if (u < 0f || u > 1f) return false;
            var q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(d, q) * inv;
            if (v < 0f || u + v > 1f) return false;
            t = Vector3.Dot(e2, q) * inv;
            return true;
        }
    }
}
