#if UNITY_EDITOR
using System.IO;
using System.Text;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】球審の見た目の確認撮影（issue #445 の候補出しで作成→確定後は
    /// 既定の見た目を3アングルで撮る再撮影ハーネスとして残す）。
    /// Edit mode で実リグ（DuelStageRig）を組み、既定の球審（恰幅Stout＋薄青シャツ＋黒スラックス）を
    /// ポスタライズ後の実際の絵で確かめる。
    ///
    /// 呼び出しは execute_code から KokoSim.Unity.Match.DuelUmpireLookSheet.RunFinal()。
    /// 出力: out/duel-stage/umpire/final_{game,close,side}.png
    /// </summary>
    public static class DuelUmpireLookSheet
    {
        private const string Dir = "out/duel-stage/umpire";
        private const int W = 960, H = 540;

        public static string RunFinal()
        {
            Directory.CreateDirectory(Dir);
            var sb = new StringBuilder();
            DuelStageRig rig = null;
            try
            {
                rig = new DuelStageRig();
                if (!rig.Available) return "rig unavailable";
                rig.EnsureSize(W, H);
                // アイドルのポーズを安定させる（12fps数コマぶん）
                for (int i = 0; i < 12; i++) rig.Tick(1f / DuelStageRig.PoseFps);

                // ① 試合の構図（既定カメラのまま）
                rig.RenderNow();
                Save(rig, "final_game");

                // ② 寄り: 一塁側前方・やや上から球審を縦1.7mで収める（構図は画角でなく実寸で指定する掟）
                rig.FramePreview(new Vector3(2.3f, 1.3f, -0.4f), new Vector3(0.18f, 0.7f, -1.88f), 1.7f);
                rig.RenderNow();
                Save(rig, "final_close");

                // ③ 真横（一塁側）: 恰幅＝胴の前後の張り出しと捕手との間隔はプロファイルが一番分離できる
                rig.FramePreview(new Vector3(2.8f, 1.0f, -1.88f), new Vector3(0.18f, 0.75f, -1.88f), 1.8f);
                rig.RenderNow();
                Save(rig, "final_side");

                sb.AppendLine("final: OK");
            }
            finally
            {
                if (rig != null) rig.Dispose();
            }
            return sb.ToString();
        }

        private static void Save(DuelStageRig rig, string file)
        {
            var rt = rig.Target;
            var prev = RenderTexture.active;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                File.WriteAllBytes(Path.Combine(Dir, file + ".png"), tex.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = prev;
                Object.DestroyImmediate(tex);
            }
        }
    }
}
#endif
