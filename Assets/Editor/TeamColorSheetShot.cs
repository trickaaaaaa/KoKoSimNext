#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using KokoSim.Engine.Nation;
using KokoSim.Unity.Match;
using UnityEngine;

namespace KokoSim.UnityEditorTools
{
    /// <summary>
    /// 【開発用・エディタ限定】パレット表（data/team-colors.yaml）の配色を<b>実際に着せて</b>一覧シートにする
    /// （issue #620。同型の先行例は Assets/KokoSim/Match/DuelTeamColorShooter.cs）。
    ///
    /// 16進を眺めても分からないことを潰すのが目的:
    ///   ① 地色の階調違い（白／生成り／クリーム／砂色／薄グレー／銀鼠）が身体の上で区別できるか
    ///   ② 足したチーム色が既存の色と見分けられるか（帽子・アンダー・ソックスに出る）
    ///   ③ リニア変換とポスタライズを通したあとの実際の見え方（design-18 §3.5.1 の罠）
    ///
    /// Play を通さず Edit mode で組む（<see cref="DuelCatalogOnlyShot"/> と同じ流儀）。
    /// 立たせるのは投手だけ＝配色対象の6領域が全部見えるのは投手（<see cref="DuelStageRig.PosePreviewStanding"/>）。
    /// </summary>
    public static class TeamColorSheetShot
    {
        private const string Dir = "out/team-colors";

        // チーム設定画面のプレビューと同じ構図（Assets/KokoSim/Team/TeamSettingController.cs）。
        private static readonly Vector3 CamPos = new Vector3(-2.35f, 1.30f, 16.85f);
        private static readonly Vector3 LookAt = new Vector3(0f, 1.10f, 18.44f);
        private const float FrameHeightM = 1.95f;

        [UnityEditor.MenuItem("KokoSim/Duel/チームカラー一覧を撮る")]
        public static void Run()
        {
            var palette = KokoSim.Unity.Shell.TeamColorProvider.Palette;
            Directory.CreateDirectory(Dir);

            // ① 全件シート（被りの体感と全体の色幅）
            var all = new List<Cell>();
            foreach (var e in palette.Entries) all.Add(new Cell(e.Label, TeamColors.FromEntry(e)));
            Shoot(all, cols: 10, cellW: 240, cellH: 180, file: "sheet-all");

            // ② 今回足した色だけを大きく（issue #620 の受け入れ）
            var added = new List<Cell>();
            foreach (var id in new[]
                     {
                         "white_navy", "cream_navy", "gray_navy",       // 見比べの基準（従来の3階調）
                         "sand_navy", "silver_navy",                    // 足した地色2種
                         "white_fuji", "white_yamabuki", "white_moss", "white_turquoise",  // 足したチーム色4種
                     })
            {
                var e = palette.FindById(id);
                if (e != null) added.Add(new Cell(e.Label, TeamColors.FromEntry(e)));
                else Debug.LogWarning("[ColorSheet] 表に無いID: " + id);
            }
            Shoot(added, cols: 4, cellW: 520, cellH: 390, file: "sheet-added");

            Debug.Log("[ColorSheet] DONE -> " + Dir);
        }

        private readonly struct Cell
        {
            public readonly string Label;
            public readonly TeamColors Colors;
            public Cell(string label, TeamColors colors) { Label = label; Colors = colors; }
        }

        private static void Shoot(List<Cell> cells, int cols, int cellW, int cellH, string file)
        {
            if (cells.Count == 0) return;
            var rows = (cells.Count + cols - 1) / cols;
            Texture2D sheet = null;
            var index = new StringBuilder();

            var rig = new DuelStageRig();
            try
            {
                if (!rig.Available) { Debug.LogError("[ColorSheet] 素体が使えない（リグが組めない）"); return; }
                if (!rig.PosePreviewStanding()) Debug.LogWarning("[ColorSheet] 直立クリップが無い＝投球姿勢のまま撮る");
                rig.FramePreview(CamPos, LookAt, FrameHeightM);
                rig.EnsureSize(cellW, cellH);

                // EnsureSize は縦横比を丸める（4:3 より縦長にしない）ので、実寸は必ず RT から採る。
                // 指定どおりに読むと「Reading pixels out of bounds」で全面が空白のシートになる。
                cellW = rig.Target.width;
                cellH = rig.Target.height;
                sheet = new Texture2D(cols * cellW, rows * cellH, TextureFormat.RGBA32, false);

                for (var i = 0; i < cells.Count; i++)
                {
                    rig.SetTeamColors(cells[i].Colors, cells[i].Colors);
                    rig.RenderNow();   // Edit mode の単発描画は RenderNow を通さないと真っ黒に焼ける

                    var rt = rig.Target;
                    var prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    var shot = new Texture2D(cellW, cellH, TextureFormat.RGBA32, false);
                    shot.ReadPixels(new Rect(0, 0, cellW, cellH), 0, 0);
                    shot.Apply();
                    RenderTexture.active = prev;

                    var col = i % cols;
                    var row = i / cols;
                    // ReadPixels は左下原点なので、1行目が上に来るよう y を反転して置く。
                    sheet.SetPixels(col * cellW, (rows - 1 - row) * cellH, cellW, cellH, shot.GetPixels());
                    Object.DestroyImmediate(shot);

                    index.AppendLine((row + 1) + "行" + (col + 1) + "列: " + cells[i].Label);
                }
                sheet.Apply();
                File.WriteAllBytes(Path.Combine(Dir, file + ".png"), sheet.EncodeToPNG());
                index.AppendLine("セル寸法: " + cellW + "x" + cellH);
                File.WriteAllText(Path.Combine(Dir, file + ".txt"), index.ToString());
                Debug.Log("[ColorSheet] wrote " + file + " (" + cells.Count + "件)");
            }
            finally
            {
                rig.Dispose();
                if (sheet != null) Object.DestroyImmediate(sheet);
            }
        }
    }
}
#endif
