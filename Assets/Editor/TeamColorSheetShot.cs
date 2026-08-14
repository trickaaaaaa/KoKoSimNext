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

        // 頭の寄り（つばの塗り分けの判定用）。実寸[m]で指定する＝欄の縦横比が変わっても大きさが動かない。
        private const float HeadHeightM = 1.62f;
        private const float CloseUpDistM = 0.85f;
        private const float CloseUpFrameHeightM = 0.42f;

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

            // ③ 軸の掛け算（issue #621）。同じ表の1件が、つば・白アンダー・縦縞の有無で
            //    どこまで別の校に見えるかを確かめる（8通り＝軸3本の全組み合わせ）。
            var axes = new List<Cell>();
            var baseEntry = palette.FindById("white_navy");
            if (baseEntry != null)
            {
                for (var mask = 0; mask < 8; mask++)
                {
                    var c = TeamColors.FromEntry(baseEntry);
                    var name = "白＋紺";
                    if ((mask & 1) != 0) { c = c with { CapVisor = baseEntry.Base }; name += "・白つば"; }
                    if ((mask & 2) != 0)
                    {
                        c = c with { Undershirt = baseEntry.Base, Socks = baseEntry.Base };
                        name += "・白アンダー";
                    }
                    if ((mask & 4) != 0)
                    {
                        var s = baseEntry.Stripe ?? TeamStripeStyle.DefaultFor(baseEntry.Team);
                        c = c with { Stripe = new TeamStripe(true, s.Color, s.FreqPerMeter) };
                        name += "・縦縞";
                    }
                    axes.Add(new Cell(name, c));
                }
            }
            Shoot(axes, cols: 4, cellW: 520, cellH: 390, file: "sheet-axes");

            // ③b つばの寄り（issue #621）。帽子メッシュを幾何で二分した境界は**寄りでしか判定できない**
            //     ＝全身の絵ではつばが数画素しかなく「ずれている」のか「そう見えるだけ」か分からない。
            var visor = new List<Cell>();
            foreach (var id in new[] { "white_navy", "white_navy_red", "white_black_gold", "white_royal_navy" })
            {
                var e = palette.FindById(id);
                if (e == null) continue;
                var c = TeamColors.FromEntry(e);
                if (id == "white_navy") c = c with { CapVisor = e.Base };   // 軸で白つばにした型
                visor.Add(new Cell(e.Label, c));
            }
            Shoot(visor, cols: 4, cellW: 520, cellH: 390, file: "sheet-visor", closeUp: true);
            Shoot(visor, cols: 4, cellW: 520, cellH: 390, file: "sheet-visor-side",
                closeUp: true, closeUpYawDeg: 75f);

            // ③c 同じ寄りをU2素体でも撮る。購入素体は gitignore 配下で手元に無い環境があり、
            //     そちらは帽子が別メッシュ（生成メッシュ）＝つばの塗り分けが**別実装**になる。
            try
            {
                DuelStageRig.ForceU2Body = true;
                Shoot(visor, cols: 4, cellW: 520, cellH: 390, file: "sheet-visor-u2", closeUp: true);
                Shoot(visor, cols: 4, cellW: 520, cellH: 390, file: "sheet-visor-u2-side",
                    closeUp: true, closeUpYawDeg: 75f);
            }
            finally
            {
                DuelStageRig.ForceU2Body = false;
            }

            // ④ 実際の校IDで引いた見た目（軸込み）。同一県規模で並べて被りの体感を確かめる。
            var pref = new List<Cell>();
            for (var i = 0; i < 40; i++)
                pref.Add(new Cell(palette.EntryFor(i).Label + " #" + i, palette.ColorsFor(i)));
            Shoot(pref, cols: 10, cellW: 240, cellH: 180, file: "sheet-prefecture");

            Debug.Log("[ColorSheet] DONE -> " + Dir);
        }

        /// <summary>
        /// 立っている投手の帽子の中心を実測して、ステージ根ローカルで返す（<see cref="DuelStageRig.FramePreview"/>
        /// はステージ根ローカルで受ける）。見つからなければ全身の狙点へ落ちる。
        /// </summary>
        private static Vector3 FindCapCenter(DuelStageRig rig)
        {
            var root = rig.StageRoot;
            foreach (var r in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (!r.gameObject.activeInHierarchy) continue;
                // 購入素体は "A_Players_0001_Cap_0001"、U2素体は "cap-mesh"＝**大小文字を無視して探す**
                // （揃っていると思い込むと、狙点が全身の既定へ落ちて胸の寄りが撮れる。2026-08-14 実害）。
                if (r.name.IndexOf("cap", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (root != null && !r.transform.IsChildOf(root)) continue;
                return root != null ? root.InverseTransformPoint(r.bounds.center) : r.bounds.center;
            }
            Debug.LogWarning("[ColorSheet] 帽子メッシュが見つからないので全身の狙点で撮る");
            return LookAt;
        }

        private readonly struct Cell
        {
            public readonly string Label;
            public readonly TeamColors Colors;
            public Cell(string label, TeamColors colors) { Label = label; Colors = colors; }
        }

        private static void Shoot(List<Cell> cells, int cols, int cellW, int cellH, string file,
            bool closeUp = false, float closeUpYawDeg = 0f)
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
                if (closeUp)
                {
                    // 帽子の寄り。狙点は**目測でなく帽子メッシュの実測**で採る（目で置いた狙点だと
                    // 「カメラの狙い外し」と「塗り分けの失敗」が絵で区別できない）。
                    // 向きは全身と同じまま距離だけ詰める（横へ回るとつばの前後が分からなくなる）。
                    var cap = FindCapCenter(rig);
                    // 横へ回した絵も撮れるようにする＝つばが正面を向いていると板が顔に被さって見え、
                    // 「つばなのか塗り間違いなのか」が1枚では分からない（横顔なら形で判る）。
                    var dir = Quaternion.Euler(0f, closeUpYawDeg, 0f) * (CamPos - LookAt).normalized;
                    rig.FramePreview(cap + dir * CloseUpDistM, cap, CloseUpFrameHeightM);
                }
                else
                {
                    rig.FramePreview(CamPos, LookAt, FrameHeightM);
                }
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
