#if UNITY_EDITOR
using System.Text;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace KokoSim.EditorTools
{
    /// <summary>
    /// 捕手の送球クリップ（AA_Baseball_Catcher_Throwing）の「投げる向き」の実測（issue #662）。
    /// 体をヨー0（+z＝二塁の向き・+x＝一塁側）に置き、ルートモーションを切ってクリップを 120fps で
    /// 再生し、右手・両肩・左足を記録する。球が出ていく向き＝リリース前後の右手の水平速度の向き、
    /// 踏み出しの向き＝左足の水平移動の向き。ヨーは Atan2(x,z)[度]（負＝三塁側）。
    /// 試合中の記録は 12fps のコマ打ちで粗く、手の速度の向きが読めないためクリップ単体で測る。
    /// 使い方: KokoSim.EditorTools.CatcherThrowClipProbe.Run() の戻り値を読む。
    /// </summary>
    public static class CatcherThrowClipProbe
    {
        private const string VendorBodyFbx =
            "Assets/DuelSpike/Vendor/SchoolBaseballPlayer0001/School_Baseball_Player_0001.fbx";
        private const string BodyFbx = "Assets/DuelSpike/UBC/SuperheroMale.fbx";
        private const string ClipName = "AA_Baseball_Catcher_Throwing";
        private const float TargetHeight = 1.75f;
        private const float Release = 1.16f;   // DuelStageRig.CatcherThrowAnchor と同じ

        public static string Run(float from = 0f, float to = 1.6f, float step = 1f / 30f)
        {
            var sb = new StringBuilder();
            GameObject go = null;
            PlayableGraph graph = default;
            try
            {
                var src = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(VendorBodyFbx)
                       ?? UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(BodyFbx);
                var clip = KokoSim.Unity.Match.DuelStageRig.LoadClip(ClipName);
                if (src == null || clip == null) return "素体かクリップが無い";
                go = Object.Instantiate(src);
                go.hideFlags = HideFlags.HideAndDontSave;
                var rs = go.GetComponentsInChildren<Renderer>();
                var b = rs[0].bounds;
                foreach (var r in rs) b.Encapsulate(r.bounds);
                go.transform.localScale = Vector3.one * (TargetHeight / Mathf.Max(0.01f, b.size.y));
                go.transform.position = Vector3.zero;
                go.transform.rotation = Quaternion.identity;

                var anim = go.GetComponentInChildren<Animator>();
                anim.applyRootMotion = false;
                anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                graph = PlayableGraph.Create("catcherthrowprobe");
                var output = AnimationPlayableOutput.Create(graph, "out", anim);
                var pl = AnimationClipPlayable.Create(graph, clip);
                output.SetSourcePlayable(pl);

                // 素体のバインド正面がどちらか（購入素体＝+Z）を確かめるため、頭→鼻ではなく肩線から出す
                Vector3 B(HumanBodyBones h) { var t = anim.GetBoneTransform(h); return t != null ? t.position : Vector3.zero; }
                float Yaw(Vector3 v) => Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;

                sb.Append("clip=").Append(clip.name).Append(" len=").Append(clip.length.ToString("F3")).Append('\n');
                sb.Append("t\tchestYaw\thipsYaw\thand(x,y,z)\thandVelYaw\thandSpd\tlFoot(x,z)\trFoot(x,z)\n");
                Vector3? prevHand = null;
                Vector3 lFoot0 = Vector3.zero;
                float bestSpd = 0f, bestT = 0f, bestYaw = 0f;
                for (float t = from; t <= to + 1e-4f; t += step)
                {
                    pl.SetTime(t);
                    graph.Evaluate(0f);
                    var ls = B(HumanBodyBones.LeftUpperArm); var rsh = B(HumanBodyBones.RightUpperArm);
                    var lh = B(HumanBodyBones.LeftUpperLeg); var rh = B(HumanBodyBones.RightUpperLeg);
                    var hand = B(HumanBodyBones.RightHand);
                    var lf = B(HumanBodyBones.LeftFoot); var rf = B(HumanBodyBones.RightFoot);
                    if (t <= from + 1e-4f) lFoot0 = lf;
                    float chest = Yaw(Vector3.Cross(rsh - ls, Vector3.up));
                    float hips = Yaw(Vector3.Cross(rh - lh, Vector3.up));
                    string vel = "", spd = "";
                    if (prevHand.HasValue)
                    {
                        var d = (hand - prevHand.Value) / step;
                        var h = new Vector2(d.x, d.z).magnitude;
                        spd = h.ToString("F2");
                        if (h > 0.3f) vel = Yaw(d).ToString("F1");
                        if (h > bestSpd && t >= Release - 0.25f && t <= Release + 0.1f) { bestSpd = h; bestT = t; bestYaw = Yaw(d); }
                    }
                    prevHand = hand;
                    sb.Append(t.ToString("F3")).Append('\t').Append(chest.ToString("F1")).Append('\t').Append(hips.ToString("F1"))
                      .Append('\t').Append(hand.x.ToString("F2")).Append(',').Append(hand.y.ToString("F2")).Append(',').Append(hand.z.ToString("F2"))
                      .Append('\t').Append(vel).Append('\t').Append(spd)
                      .Append('\t').Append(lf.x.ToString("F2")).Append(',').Append(lf.z.ToString("F2"))
                      .Append('\t').Append(rf.x.ToString("F2")).Append(',').Append(rf.z.ToString("F2")).Append('\n');
                }
                pl.SetTime(to); graph.Evaluate(0f);
                var stride = B(HumanBodyBones.LeftFoot) - lFoot0;
                sb.Append("# リリース前後で手が最速の瞬間 t=").Append(bestT.ToString("F3"))
                  .Append(" 水平速度=").Append(bestSpd.ToString("F2")).Append("m/s 向き=").Append(bestYaw.ToString("F1")).Append("度\n");
                sb.Append("# 左足の踏み出し（").Append(from.ToString("F2")).Append("→").Append(to.ToString("F2"))
                  .Append("） 向き=").Append(Yaw(stride).ToString("F1")).Append("度 距離=")
                  .Append(new Vector2(stride.x, stride.z).magnitude.ToString("F2")).Append("m\n");
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy();
                if (go != null) Object.DestroyImmediate(go);
            }
            return sb.ToString();
        }
    }
}
#endif
