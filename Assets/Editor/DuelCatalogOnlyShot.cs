#if UNITY_EDITOR
using System.Globalization;
using System.IO;
using System.Text;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Unity.Match;
using UnityEngine;

namespace KokoSim.UnityEditorTools
{
    /// <summary>
    /// 【開発用・エディタ限定】索引だけで組めるかを確かめる（打球処理の3D化 F8 の受け入れ）。
    ///
    /// <b>なぜ要るか</b>: エディタのロードは <c>AssetDatabase</c> を先に見るので、
    /// ビルド同梱の索引が壊れていても**手元では絵が出る**。索引の不備は Player ビルドでだけ
    /// 現れる＝作らないと気づけない類の壊れ方なので、
    /// <see cref="DuelStageRig.ForceCatalogOnly"/> でビルドと同じ経路に切り替えて撮る。
    ///
    /// 出すのは2つ:
    ///   ① <c>Available</c>（＝リグが組めたか。ビルドでの受け入れ条件そのもの）
    ///   ② 通常経路と索引経路の**画素差分**（0 なら同じ資産を指している）
    /// </summary>
    public static class DuelCatalogOnlyShot
    {
        private const string Dir = "out/duel-stage/catalog";
        private const int W = 960;
        private const int H = 540;

        [UnityEditor.MenuItem("KokoSim/Duel/F8 索引だけで組めるか試す")]
        public static void Run()
        {
            Directory.CreateDirectory(Dir);
            var sb = new StringBuilder();

            var a = Shoot(false, "normal", sb);
            var b = Shoot(true, "catalog", sb);

            if (a != null && b != null)
            {
                int diff = 0;
                var pa = a.GetPixels32();
                var pb = b.GetPixels32();
                for (int i = 0; i < pa.Length && i < pb.Length; i++)
                    if (!pa[i].Equals(pb[i])) diff++;
                sb.AppendLine("画素差分: " + diff + " / " + pa.Length);
            }
            if (a != null) Object.DestroyImmediate(a);
            if (b != null) Object.DestroyImmediate(b);

            File.WriteAllText(Path.Combine(Dir, "catalog-only.txt"), sb.ToString());
            Debug.Log("[F8Shot]\n" + sb);
        }

        private static Texture2D Shoot(bool catalogOnly, string tag, StringBuilder sb)
        {
            DuelStageRig.ForceCatalogOnly = catalogOnly;
            var rig = new DuelStageRig();
            try
            {
                sb.AppendLine(tag + ": Available=" + rig.Available);
                if (!rig.Available) return null;
                rig.EnsureSize(W, H);
                var play = PlaybackSamples.All[2];   // ③ ショートゴロ 6-3
                float contact = 0.85f;
                rig.BeginFieldPlay(play, contact);
                float step = 1f / DuelStageRig.PoseFps;
                for (float t = contact; t < 2.6f; t += step)
                {
                    if (t > contact) rig.Tick(step);
                    rig.SetFieldTime(t);
                }
                rig.RenderNow();

                var rt = rig.Target;
                var prev = RenderTexture.active;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                File.WriteAllBytes(Path.Combine(Dir, tag + ".png"), tex.EncodeToPNG());

                sb.AppendLine(tag + ": 捕球" + rig.FieldCatchDiagForCapture().Split('\t')[0]
                    .Replace("捕球予定=", "予定=") + " / 送球予定"
                    + rig.FieldThrowDiagForCapture().Split('\t')[0].Replace("送球予定=", "="));
                return tex;
            }
            finally
            {
                rig.Dispose();
                DuelStageRig.ForceCatalogOnly = false;
            }
        }
    }
}
#endif
