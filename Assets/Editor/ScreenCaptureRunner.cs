using System.Collections.Generic;
using System.IO;
using KokoSim.Unity.Debugging;
using UnityEditor;

namespace KokoSim.UnityEditorTools
{
    /// <summary>
    /// データ画面の撮影レシピ（data/debug/screens.yaml）の実行口。1回の呼び出しで「Play に入る→状態を作る→画面を開く・押す→撮る→
    /// Play を抜ける」まで進む。試合の撮影レシピ（<see cref="CaptureRunner"/>）のデータ画面版。
    ///
    /// 使い方（MCP execute_code・C#6 でも呼べる）:
    /// <code>return KokoSim.UnityEditorTools.ScreenCaptureRunner.Run("management-contracts");</code>
    /// 呼び出しはすぐ返る（Play はこのあと始まる）。終わったかは screenshots/&lt;id&gt;/DONE か FAILED の有無、
    /// まとめは screenshots/_screen-run.txt で見る。中身は summary.json（撮った絵・書体監査・手順のメモ・エラー）。
    /// </summary>
    public static class ScreenCaptureRunner
    {
        /// <summary>レシピを検査して、通れば Play に入る。返り値は「始めた」か、始められなかった理由。</summary>
        public static string Run(params string[] ids)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return "Play 中は始めない（止めてから呼ぶ。遊んでいる最中かもしれない）";
            if (EditorApplication.isCompiling)
                return "コンパイル中。終わってから呼ぶ";
            if (EditorUtility.scriptCompilationFailed)
                return "コンパイルエラーがあり Play に入れない。直してから呼ぶ";
            if (ids == null || ids.Length == 0) return "レシピの id を渡す";
            var errors = new List<string>();
            var all = ScreenRecipes.Load(errors);
            if (errors.Count > 0) return "screens.yaml を読めない:\n" + string.Join("\n", errors);
            foreach (var id in ids)
                if (!all.Exists(r => r.Id == id)) return "レシピが無い: " + id;

            foreach (var id in ids)
            {
                var dir = Path.Combine(ScreenRecipeShooter.OutRoot, id);
                foreach (var f in new[] { "DONE", "FAILED" })
                    if (File.Exists(Path.Combine(dir, f))) File.Delete(Path.Combine(dir, f));
            }
            if (File.Exists(ScreenRecipeShooter.RunMarkerPath)) File.Delete(ScreenRecipeShooter.RunMarkerPath);

            ScreenRecipeShooter.Arm(ids);
            EditorApplication.EnterPlaymode();
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                ScreenRecipeShooter.Disarm();   // 予約を残すと、後で手で Play したときに撮影が勝手に始まり Play を抜ける
                return "Play に入れなかった（撮影の予約は取り消した）";
            }
            return "開始: " + string.Join(", ", ids) + "（終わると " + ScreenRecipeShooter.RunMarkerPath + " ができる）";
        }

        /// <summary>レシピの一覧（id と名前）。</summary>
        public static string List()
        {
            var errors = new List<string>();
            var all = ScreenRecipes.Load(errors);
            var lines = new List<string>(errors);
            foreach (var r in all) lines.Add(r.Id + "\t" + r.Name);
            return string.Join("\n", lines);
        }
    }
}
