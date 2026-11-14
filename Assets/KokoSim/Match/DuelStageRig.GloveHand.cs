using System.Collections.Generic;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// グラブをはめた手を描かない（issue #913）。
    ///
    /// 購入グラブは殻が薄く（中央4.8cm）ポケットが深い（約10cm）ので、手をどこに置いても
    /// 振りかぶりでポケットがカメラを向いた瞬間に中の指が覗く。実物の指は指袋の中だが、
    /// メッシュには指袋の中身が無い＝手がポケットの空洞に浮いて見える。
    ///
    /// **骨は触らない**: 手の骨を縮めると、手の骨の子に付けているグラブと捕球した球まで縮む。
    /// 代わりに体のメッシュから「手（手の骨とその子孫）に重み0.5以上の頂点を含む三角形」を抜いた
    /// 複製へ差し替える。骨・アニメ・装着・捕球の付け先は一切変わらない。
    /// 指だけたたむ案（指の骨を0.05倍）は手のひら〜手首が縁から残ったので不採用。
    /// </summary>
    public sealed partial class DuelStageRig
    {
        /// <summary>手の頂点とみなす重みの下限（手の骨とその子孫への重みの合計）。</summary>
        private const float GloveHandWeightMin = 0.5f;

        /// <summary>元メッシュ＋手の骨名ごとの「手抜き」複製。同じ素体の役者同士で共有する。</summary>
        private static readonly Dictionary<(Mesh, string), Mesh> HandlessMeshCache = new();

        private static readonly HashSet<Mesh> WarnedUnreadable = new();

        /// <summary>
        /// <paramref name="actor"/> の全スキンメッシュから <paramref name="hand"/> 側の手を抜く。
        /// 手の頂点を持たないメッシュ（帽子・ズボン等）は差し替えない。
        /// </summary>
        private static void HideGloveHand(Actor actor, Transform hand)
        {
            if (actor?.Go == null || hand == null) return;
            foreach (var smr in actor.Go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var src = smr.sharedMesh;
                if (src == null) continue;
                if (!src.isReadable)
                {
                    // 読めないと手を抜けず、エラーも出ずに手が残る（2026-11-14 実害: 領域メッシュが非読み取りだった）
                    if (WarnedUnreadable.Add(src))
                        Debug.LogWarning("[DuelStageRig] glove hand not hidden: mesh not readable - " + src.name);
                    continue;
                }
                var key = (src, hand.name);
                if (!HandlessMeshCache.TryGetValue(key, out var handless))
                {
                    handless = BuildHandlessMesh(src, smr.bones, hand);
                    HandlessMeshCache[key] = handless;   // null＝手を含まないメッシュ（次回も素通し）
                }
                if (handless != null) smr.sharedMesh = handless;
            }
        }

        private static Mesh BuildHandlessMesh(Mesh src, Transform[] bones, Transform hand)
        {
            if (bones == null || bones.Length == 0) return null;
            var isHandBone = new bool[bones.Length];
            bool any = false;
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] != null && (bones[i] == hand || bones[i].IsChildOf(hand)))
                {
                    isHandBone[i] = true;
                    any = true;
                }
            }
            if (!any) return null;

            var weights = src.boneWeights;
            if (weights == null || weights.Length != src.vertexCount) return null;
            var handVert = new bool[weights.Length];
            int handCount = 0;
            for (int v = 0; v < weights.Length; v++)
            {
                var w = weights[v];
                float sum = 0f;
                if (w.boneIndex0 < isHandBone.Length && isHandBone[w.boneIndex0]) sum += w.weight0;
                if (w.boneIndex1 < isHandBone.Length && isHandBone[w.boneIndex1]) sum += w.weight1;
                if (w.boneIndex2 < isHandBone.Length && isHandBone[w.boneIndex2]) sum += w.weight2;
                if (w.boneIndex3 < isHandBone.Length && isHandBone[w.boneIndex3]) sum += w.weight3;
                if (sum >= GloveHandWeightMin) { handVert[v] = true; handCount++; }
            }
            if (handCount == 0) return null;

            var dst = Object.Instantiate(src);
            dst.name = src.name + " (glove hand hidden)";
            var tris = new List<int>();
            var kept = new List<int>();
            for (int sub = 0; sub < src.subMeshCount; sub++)
            {
                src.GetTriangles(tris, sub);
                kept.Clear();
                for (int t = 0; t + 2 < tris.Count; t += 3)
                {
                    // 1頂点でも手なら抜く＝手首の縁の帯まで消す（残すとグラブの手首口から肌の縁が覗く）
                    if (handVert[tris[t]] || handVert[tris[t + 1]] || handVert[tris[t + 2]]) continue;
                    kept.Add(tris[t]); kept.Add(tris[t + 1]); kept.Add(tris[t + 2]);
                }
                dst.SetTriangles(kept, sub, false);
            }
            return dst;
        }
    }
}
