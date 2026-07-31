#if UNITY_EDITOR
using System.IO;
using KokoSim.Unity.Match;
using UnityEngine;

namespace KokoSim.UnityEditorTools
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】球場の最小追加（S1・打球処理3D化 F1）の確認撮影。
    ///
    /// 2枚だけ撮る:
    ///   duel.png … **対決構図のまま**（＝F1の受け入れ「画素差分 0」を突き合わせる相手）
    ///   wide.png … 本塁後方高所の全景（＝塁とファウルラインが乗ったかを見る）
    ///
    /// Edit mode の単発描画はスキンが前ポーズのまま焼かれる既知現象があるが（design-18 §7-3）、
    /// ここで見たいのは**プリミティブの地面・塁・線**（MeshRenderer）なので影響しない。
    /// 選手のポーズが変に見えてもそれは本件の判断材料ではない。
    /// </summary>
    public static class DuelFieldShot
    {
        private const string Dir = "out/duel-stage/field";

        // 全景の station（本塁後方の高所）。design-18 の掟どおり画角[度]でなく
        // 「注視点の距離で縦に収めたい実寸[m]」で構図を決める。
        // ⚠ z は **バックネット書き割り（z=-16・34×8m）より手前** に置くこと。奥に引くと壁が
        //    画面下半分を黒く塞ぐ（2026-08-01 実害）。F3 の守備カメラ station を決めるときも同じ制約がかかる。
        private static readonly Vector3 WideCam = new(0f, 24f, -14f);
        private static readonly Vector3 WideAim = new(0f, 0f, 52f);
        private const float WideFrameHeightM = 105f;

        // 見やすさのための撮影解像度（実ゲームは 480×270 のカラム適応）。
        private const int W = 1280;
        private const int H = 720;

        [UnityEditor.MenuItem("KokoSim/Duel/F1 球場の最小追加を撮る")]
        public static void Run()
        {
            var rig = new DuelStageRig();
            try
            {
                if (!rig.Available)
                {
                    Debug.LogError("[F1Shot] DuelStageRig が組めない（素体/クリップ未導入）。撮影を中止する");
                    return;
                }
                Directory.CreateDirectory(Dir);
                rig.EnsureSize(W, H);

                // ⚠ 撮影順は「対決構図 → 全景」の一方通行にする。
                //   ClearPreviewFrame() が戻すのは**画角だけ**で、FramePreview が動かした
                //   カメラの位置・向きは戻らない（全景の位置から望遠で撮る絵になり、
                //   duel-defense が画面の56%も違った。2026-08-01 実害）。
                //   対決構図へ戻す公開APIが無いので、対決の絵は全部先に撮り切る。

                rig.RenderNow();
                Write(rig.Target, "duel");

                // ---- F2: 守備9人 ----
                var sw = System.Diagnostics.Stopwatch.StartNew();
                rig.EnsureFieldCast();
                sw.Stop();
                rig.SetFieldCastVisible(true);
                rig.PlaceFieldCastAtDefault();

                // 対決構図のまま撮る＝望遠2.9°では野手はフレーム外のはず（打席中に伏せる根拠の裏取り）
                rig.RenderNow();
                Write(rig.Target, "duel-defense");

                // ---- 全景（ここから先はカメラが戻らない） ----
                rig.FramePreview(WideCam, WideAim, WideFrameHeightM);
                rig.RenderNow();
                Write(rig.Target, "wide-defense");

                // 実測は全景カメラで採る（px列は「この構図で人が何pxか」＝絵の判断に直結する数）
                var diag = rig.FielderDiagForCapture();
                File.WriteAllText(Path.Combine(Dir, "fielder-diag.tsv"), diag);
                Debug.Log("[F1Shot] 守備7人のスポーン " + sw.ElapsedMilliseconds + "ms / px は全景("
                    + WideFrameHeightM + "m)での実測\n" + diag);

                rig.SetFieldCastVisible(false);
                rig.RenderNow();
                Write(rig.Target, "wide");

                Debug.Log("[F1Shot] DONE -> " + Dir);
            }
            finally
            {
                rig.Dispose();
            }
        }

        private static void Write(RenderTexture rt, string name)
        {
            if (rt == null) { Debug.LogError("[F1Shot] RT が無い: " + name); return; }
            var prev = RenderTexture.active;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                File.WriteAllBytes(Path.Combine(Dir, name + ".png"), tex.EncodeToPNG());
                Debug.Log("[F1Shot] wrote " + name + " (" + rt.width + "x" + rt.height + ")");
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
