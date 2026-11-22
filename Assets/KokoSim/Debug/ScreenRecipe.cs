#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace KokoSim.Unity.Debugging
{
    /// <summary>
    /// データ画面の撮影レシピ1件（<c>data/debug/screens.yaml</c>）。試合の撮影レシピ（<see cref="CaptureRecipe"/>）の
    /// データ画面版で、「状態を作る→画面を開く→押す→撮る」を手順（<see cref="Steps"/>）として上から順に書く。
    /// 画面ごとに使い捨ての *Shooter を書いていたのをやめるための入れ物（夜間の実機確認でもこれを使う）。
    /// 実行は <c>ScreenCaptureRunner.Run("id")</c>（Editor）＝Play に入って撮り、終わったら Play を抜ける。
    /// </summary>
    public sealed class ScreenRecipe
    {
        public string Id;
        public string Name;
        public int Width = 1600, Height = 900;
        public readonly List<ScreenStep> Steps = new List<ScreenStep>();
    }

    /// <summary>手順1つ。<see cref="Kind"/> が種類、<see cref="Arg"/> が値（そのままの文字列）、<see cref="Map"/> がフロー・マップの値。</summary>
    public sealed class ScreenStep
    {
        public int Line;
        public string Kind;
        public string Arg;
        public Dictionary<string, string> Map;

        public override string ToString() => Kind + ": " + Arg;
    }

    /// <summary>
    /// <c>data/debug/screens.yaml</c> の読み込み。書式は captures.yaml と同じ部分集合に、手順の並び（<c>steps:</c> の下の
    /// <c>- 種類: 値</c>）を足したもの。未知のキー・手順の種類は**黙って捨てずエラー**にする（綴り違いの手順が
    /// 効かないまま「撮れた」ことになるのを防ぐ）。
    /// </summary>
    public static class ScreenRecipes
    {
        /// <summary>手順の種類（<see cref="ScreenRecipeShooter"/> が実装しているもの）。</summary>
        public static readonly HashSet<string> StepKinds = new HashSet<string>
        {
            "new_game", "advance_weeks", "advance_days", "advance_to_match_day", "play_tournament", "advance_to_week", "boost_roster", "show", "click", "submit", "hover", "leave", "call", "set", "wait", "shot", "read",
        };

        public static string FindPath()
        {
            var dir = new DirectoryInfo(Application.dataPath);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "data/debug/screens.yaml");
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }

        public static List<ScreenRecipe> Load(List<string> errors)
        {
            var path = FindPath();
            if (path == null) { errors.Add("data/debug/screens.yaml が見つからない"); return new List<ScreenRecipe>(); }
            return Parse(File.ReadAllText(path), errors);
        }

        public static bool TryGet(string id, out ScreenRecipe recipe, List<string> errors)
        {
            foreach (var r in Load(errors))
                if (r.Id == id) { recipe = r; return true; }
            recipe = null;
            errors.Add("レシピが無い: " + id);
            return false;
        }

        public static List<ScreenRecipe> Parse(string yaml, List<string> errors)
        {
            var list = new List<ScreenRecipe>();
            ScreenRecipe cur = null;
            bool inScreens = false, inSteps = false;
            int recipeIndent = -1, lineNo = 0;
            foreach (var raw in yaml.Replace("\r\n", "\n").Split('\n'))
            {
                lineNo++;
                var line = StripComment(raw);
                if (line.Trim().Length == 0) continue;
                int indent = line.Length - line.TrimStart().Length;
                var body = line.Trim();
                if (indent == 0) { inScreens = body == "screens:"; continue; }
                if (!inScreens) continue;

                // レシピの頭（- id: …）。最初に出た字下げをレシピの段とする
                if (body.StartsWith("- ") && (recipeIndent < 0 || indent == recipeIndent))
                {
                    recipeIndent = indent;
                    cur = new ScreenRecipe();
                    list.Add(cur);
                    inSteps = false;
                    body = body.Substring(2).Trim();
                }
                else if (cur == null) { errors.Add($"{lineNo}行: レシピの外にキーがある"); continue; }
                else if (inSteps && body.StartsWith("- "))
                {
                    var step = ParseStep(body.Substring(2).Trim(), lineNo, errors);
                    if (step != null) cur.Steps.Add(step);
                    continue;
                }

                int colon = body.IndexOf(':');
                if (colon <= 0) { errors.Add($"{lineNo}行: key: value の形でない"); continue; }
                var key = body.Substring(0, colon).Trim();
                var value = body.Substring(colon + 1).Trim();
                if (key == "steps")
                {
                    if (value.Length > 0) errors.Add($"{lineNo}行: steps: の後は改行して - 種類: 値 を並べる");
                    inSteps = true;
                    continue;
                }
                inSteps = false;
                if (!Apply(cur, key, value)) errors.Add($"{lineNo}行: 読めないキーか値 '{key}: {value}'");
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (string.IsNullOrEmpty(list[i].Id)) errors.Add($"{i + 1}件目: id が無い");
                else if (list[i].Steps.Count == 0) errors.Add(list[i].Id + ": steps が無い");
                else if (!list[i].Steps.Exists(s => s.Kind == "shot")) errors.Add(list[i].Id + ": shot が1つも無い");
            }
            return list;
        }

        private static ScreenStep ParseStep(string body, int lineNo, List<string> errors)
        {
            int colon = body.IndexOf(':');
            string kind = colon <= 0 ? body : body.Substring(0, colon).Trim();
            string value = colon <= 0 ? "" : body.Substring(colon + 1).Trim();
            if (!StepKinds.Contains(kind))
            {
                errors.Add($"{lineNo}行: 知らない手順 '{kind}'（使えるのは {string.Join(" / ", StepKinds)}）");
                return null;
            }
            var step = new ScreenStep { Line = lineNo, Kind = kind, Arg = Unquote(value), Map = FlowMap(value) };
            if (value.StartsWith("{") && step.Map == null)
            {
                errors.Add($"{lineNo}行: {{…}} を読めない '{value}'");
                return null;
            }
            return step;
        }

        private static bool Apply(ScreenRecipe r, string key, string v)
        {
            switch (key)
            {
                case "id": r.Id = Unquote(v); return r.Id.Length > 0;
                case "name": r.Name = Unquote(v); return true;
                case "size":
                    var m = FlowMap(v);
                    if (m == null) return false;
                    if (m.TryGetValue("w", out var w) && !int.TryParse(w, out r.Width)) return false;
                    if (m.TryGetValue("h", out var h) && !int.TryParse(h, out r.Height)) return false;
                    return r.Width > 0 && r.Height > 0;
                default: return false;
            }
        }

        public static bool TryFloat(string v, out float f)
            => float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out f);

        public static string Unquote(string v)
        {
            v = v.Trim();
            if (v.Length >= 2 && (v[0] == '"' && v[v.Length - 1] == '"' || v[0] == '\'' && v[v.Length - 1] == '\''))
                return v.Substring(1, v.Length - 2);
            return v;
        }

        private static Dictionary<string, string> FlowMap(string v)
        {
            v = v.Trim();
            if (!v.StartsWith("{") || !v.EndsWith("}")) return null;
            var map = new Dictionary<string, string>();
            foreach (var part in v.Substring(1, v.Length - 2).Split(','))
            {
                if (part.Trim().Length == 0) continue;
                int c = part.IndexOf(':');
                if (c <= 0) return null;
                map[Unquote(part.Substring(0, c))] = Unquote(part.Substring(c + 1));
            }
            return map;
        }

        private static string StripComment(string line)
        {
            bool inQuote = false;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '"') inQuote = !inQuote;
                if (line[i] == '#' && !inQuote && (i == 0 || line[i - 1] == ' ')) return line.Substring(0, i);
            }
            return line;
        }
    }
}
#endif
