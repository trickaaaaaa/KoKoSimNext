using System.Collections.Generic;
using System.IO;
using KokoSim.Unity.Debugging;
using UnityEditor;

namespace KokoSim.UnityEditorTools
{
    /// <summary>
    /// 撮影レシピ（data/debug/captures.yaml）の実行口。1回の呼び出しで「Play に入る→場面を作る→撮る・測る→Play を抜ける」まで進む。
    ///
    /// 使い方（MCP execute_code・C#6 でも呼べる）:
    /// <code>return KokoSim.UnityEditorTools.CaptureRunner.Run("bunt-throw-1089");</code>
    /// 呼び出しはすぐ返る（Play はこのあと始まる）。終わったかは screenshots/&lt;id&gt;/DONE か FAILED の有無、
    /// まとめは screenshots/_capture-run.txt で見る。中身は summary.json（節目の時刻・撮った絵・エラー）と probes.jsonl。
    /// </summary>
    public static class CaptureRunner
    {
        /// <summary>レシピを検査して、通れば Play に入る。返り値は「始めた」か、始められなかった理由。</summary>
        public static string Run(params string[] ids)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return "Play 中は始めない（止めてから呼ぶ。遊んでいる最中かもしれない）";
            if (EditorApplication.isCompiling)
                return "コンパイル中。終わってから呼ぶ";
            if (ids == null || ids.Length == 0) return "レシピの id を渡す";
            var errors = new List<string>();
            var all = CaptureRecipes.Load(errors);
            if (errors.Count > 0) return "captures.yaml を読めない:\n" + string.Join("\n", errors);
            foreach (var id in ids)
                if (!all.Exists(r => r.Id == id)) return "レシピが無い: " + id;

            foreach (var id in ids)
            {
                var dir = Path.Combine(RecipeShooter.OutRoot, id);
                foreach (var f in new[] { "DONE", "FAILED" })
                    if (File.Exists(Path.Combine(dir, f))) File.Delete(Path.Combine(dir, f));
            }
            if (File.Exists(RecipeShooter.RunMarkerPath)) File.Delete(RecipeShooter.RunMarkerPath);

            RecipeShooter.Arm(ids);
            EditorApplication.EnterPlaymode();
            return "開始: " + string.Join(", ", ids) + "（終わると " + RecipeShooter.RunMarkerPath + " ができる）";
        }

        /// <summary>レシピの一覧（id と名前）。</summary>
        public static string List()
        {
            var errors = new List<string>();
            var all = CaptureRecipes.Load(errors);
            var lines = new List<string>(errors);
            foreach (var r in all) lines.Add(r.Id + "\t" + r.Name);
            return string.Join("\n", lines);
        }
    }
}
