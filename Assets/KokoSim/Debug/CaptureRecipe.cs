#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace KokoSim.Unity.Debugging
{
    /// <summary>
    /// 撮影レシピ1件（<c>data/debug/captures.yaml</c>）。「場面→撮る→測る」を1項目で書く。
    /// 1件の確認ごとに専用の撮影スクリプト（*Shooter）を書いていたのをやめるための入れ物。
    /// 実行は <c>CaptureRunner.Run("id")</c>（Editor）＝Play に入って撮り、終わったら Play を抜ける。
    /// </summary>
    public sealed class CaptureRecipe
    {
        public string Id;
        public string Name;
        /// <summary>場面（data/debug/scenarios.yaml の id）。必須。</summary>
        public string Scenario;
        public ulong Seed = 1;
        /// <summary>撮り直しの回数（シードを1ずつ進める）。強制発動で場面が確実に出るなら1で足りる。</summary>
        public int Attempts = 3;
        /// <summary>1回あたり、守備ビューの再生が始まるまで待つ上限[s]。</summary>
        public float TimeoutSeconds = 90f;
        /// <summary>シナリオの force の代わりに最初の打席へ予約する強制発動（ForcedOutcome の enum 名）。</summary>
        public string Force;
        /// <summary>監督の攻撃中、毎球の采配窓で押す打撃の指示（PitchBattingOverride の enum 名。例: Bunt）。</summary>
        public string Sign;
        /// <summary>監督が先攻か（null＝シナリオどおり）。</summary>
        public bool? ManagerAway;
        public bool DuelView = true;
        public float Speed = 1f;
        /// <summary>board＝盤面だけ切り抜く / panel＝画面全体 / rig＝3Dリグの描画先そのまま。</summary>
        public string View = "board";
        public int Width = 1600, Height = 900;
        /// <summary>撮る瞬間（例: release-0.2 / flight_end / 1.5）。</summary>
        public List<string> Shots = new List<string>();
        /// <summary>連番で撮る区間（例: from catch-0.8 to release+0.9 fps 30）。From が空なら撮らない。</summary>
        public string VideoFrom, VideoTo;
        public int VideoFps = 30;
        /// <summary>対決ビューのまま撮る秒数（0＝使わない）。最初の球が投げられた瞬間から video の fps で連番を撮る
        /// （守備ビューへ移らない場面＝ファウルの打球など。issue #1074）。</summary>
        public float DuelSeconds;
        /// <summary>duel_seconds の撮影の途中（開始から何秒後）で監督が守備伝令を送る（負＝送らない。issue #1178）。
        /// 動揺が解けて渦巻きが消えるところを撮る。</summary>
        public float DefenseTimeoutAt = -1f;
        /// <summary>duel_seconds の撮り始め（duel_from: launch＝試合の画面が出た瞬間から。プレイボールのテロップ等
        /// 最初の球より前を撮るとき。issue #1179）。既定 false＝最初の球が投げられた瞬間から。</summary>
        public bool DuelFromLaunch;
        /// <summary>timeout_at の伝令を、画面の操作と同じ流れ（中断→伝令役を選ぶ→再開）で送るか（issue #1174）。
        /// true のとき守備伝令の場面が流れる。送れる瞬間（球の合間）まで毎コマ試す。</summary>
        public bool TimeoutViaMenu;
        /// <summary>TimeoutViaMenu の伝令役（控えの添字。負＝記録員）。</summary>
        public int TimeoutMessengerIndex = -1;
        /// <summary>毎コマ記録する実測（DuelStageRig の *ForCapture のメンバー名）。</summary>
        public List<string> Probes = new List<string>();
        /// <summary>
        /// 采配窓で押す采配カードの id（issue #1473。null＝使わない）。指定すると守備ビューを待たず、最初の采配窓で
        /// 止めて「01-window」（押す前の手札）を撮り、カードを順に押して「02-pressed」を撮り、続けて
        /// <see cref="CardsAfterSeconds"/> 秒流して「03-after」（その球の結果）を撮る。view: panel で手札まで写る。
        /// </summary>
        public List<string> Cards;
        /// <summary>cards の押下後、03-after を撮るまで流す秒数（ゲームの時計。既定 4）。</summary>
        public float CardsAfterSeconds = 4f;
        /// <summary>見比べ用の切り替え（"DuelStageRig.Xxx": 値）。終わったら元へ戻す。</summary>
        public Dictionary<string, string> Overrides = new Dictionary<string, string>();
    }

    /// <summary>
    /// <c>data/debug/captures.yaml</c> の読み込み。書式は scenarios.yaml と同じ部分集合
    /// （<c>captures:</c> 直下の <c>- key: value</c> 連なり／<c>{a: 1}</c> のフロー・マップ／<c>[a, b]</c> のフロー・シーケンス）。
    /// 未知のキー・読めない値は**黙って捨てず**エラーとして返す（綴り違いの設定が効かないまま撮れてしまうのを防ぐ）。
    /// </summary>
    public static class CaptureRecipes
    {
        public static string FindPath()
        {
            var dir = new DirectoryInfo(Application.dataPath);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "data/debug/captures.yaml");
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }

        public static List<CaptureRecipe> Load(List<string> errors)
        {
            var path = FindPath();
            if (path == null) { errors.Add("data/debug/captures.yaml が見つからない"); return new List<CaptureRecipe>(); }
            return Parse(File.ReadAllText(path), errors);
        }

        public static bool TryGet(string id, out CaptureRecipe recipe, List<string> errors)
        {
            foreach (var r in Load(errors))
                if (r.Id == id) { recipe = r; return true; }
            recipe = null;
            errors.Add("レシピが無い: " + id);
            return false;
        }

        public static List<CaptureRecipe> Parse(string yaml, List<string> errors)
        {
            var list = new List<CaptureRecipe>();
            CaptureRecipe cur = null;
            bool inCaptures = false;
            int lineNo = 0;
            foreach (var raw in yaml.Replace("\r\n", "\n").Split('\n'))
            {
                lineNo++;
                var line = StripComment(raw);
                if (line.Trim().Length == 0) continue;
                int indent = line.Length - line.TrimStart().Length;
                var body = line.Trim();
                if (indent == 0)
                {
                    inCaptures = body == "captures:";
                    continue;
                }
                if (!inCaptures) continue;
                if (body.StartsWith("- "))
                {
                    cur = new CaptureRecipe();
                    list.Add(cur);
                    body = body.Substring(2).Trim();
                }
                if (cur == null) { errors.Add($"{lineNo}行: レシピの外にキーがある"); continue; }
                int colon = body.IndexOf(':');
                if (colon <= 0) { errors.Add($"{lineNo}行: key: value の形でない"); continue; }
                var key = body.Substring(0, colon).Trim();
                var value = body.Substring(colon + 1).Trim();
                if (!Apply(cur, key, value)) errors.Add($"{lineNo}行: 読めないキーか値 '{key}: {value}'");
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (string.IsNullOrEmpty(list[i].Id)) errors.Add($"{i + 1}件目: id が無い");
                else if (string.IsNullOrEmpty(list[i].Scenario)) errors.Add(list[i].Id + ": scenario が無い");
            }
            return list;
        }

        private static bool Apply(CaptureRecipe r, string key, string v)
        {
            switch (key)
            {
                case "id": r.Id = Unquote(v); return r.Id.Length > 0;
                case "name": r.Name = Unquote(v); return true;
                case "scenario": r.Scenario = Unquote(v); return r.Scenario.Length > 0;
                case "seed": return ulong.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r.Seed);
                case "attempts": return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r.Attempts) && r.Attempts > 0;
                case "timeout": return TryFloat(v, out r.TimeoutSeconds) && r.TimeoutSeconds > 0;
                case "force": r.Force = Unquote(v); return true;
                case "sign": r.Sign = Unquote(v); return true;
                case "manager":
                    var m = Unquote(v);
                    if (m == "away") { r.ManagerAway = true; return true; }
                    if (m == "home") { r.ManagerAway = false; return true; }
                    return false;
                case "duel_view": return bool.TryParse(v, out r.DuelView);
                case "speed": return TryFloat(v, out r.Speed) && r.Speed > 0;
                case "duel_seconds": return TryFloat(v, out r.DuelSeconds) && r.DuelSeconds > 0;
                case "timeout_at": return TryFloat(v, out r.DefenseTimeoutAt) && r.DefenseTimeoutAt >= 0f;
                case "timeout_messenger":
                {
                    // 守備伝令の場面（issue #1174）: 控えの添字か recorder（記録員）
                    var mv = Unquote(v);
                    r.TimeoutViaMenu = true;
                    if (mv == "recorder") { r.TimeoutMessengerIndex = -1; return true; }
                    return int.TryParse(mv, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out r.TimeoutMessengerIndex) && r.TimeoutMessengerIndex >= 0;
                }
                case "duel_from":
                {
                    var f = Unquote(v);
                    if (f == "launch") { r.DuelFromLaunch = true; return true; }
                    if (f == "pitch") { r.DuelFromLaunch = false; return true; }
                    return false;
                }
                case "view":
                    r.View = Unquote(v);
                    return r.View == "board" || r.View == "panel" || r.View == "rig";
                case "size":
                {
                    var map = FlowMap(v);
                    return map != null && map.TryGetValue("w", out var w) && map.TryGetValue("h", out var h)
                           && int.TryParse(w, out r.Width) && int.TryParse(h, out r.Height);
                }
                case "shots": r.Shots = FlowSeq(v); return r.Shots != null;
                case "probes": r.Probes = FlowSeq(v); return r.Probes != null;
                case "video":
                {
                    var map = FlowMap(v);
                    if (map == null || !map.TryGetValue("from", out r.VideoFrom) || !map.TryGetValue("to", out r.VideoTo)) return false;
                    return !map.TryGetValue("fps", out var fps) || (int.TryParse(fps, out r.VideoFps) && r.VideoFps > 0);
                }
                case "overrides": r.Overrides = FlowMap(v); return r.Overrides != null;
                case "cards": r.Cards = FlowSeq(v); return r.Cards != null && r.Cards.Count > 0;
                case "cards_after": return TryFloat(v, out r.CardsAfterSeconds) && r.CardsAfterSeconds >= 0f;
                default: return false;
            }
        }

        private static bool TryFloat(string v, out float f)
            => float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out f);

        private static string Unquote(string v)
        {
            v = v.Trim();
            if (v.Length >= 2 && (v[0] == '"' && v[v.Length - 1] == '"' || v[0] == '\'' && v[v.Length - 1] == '\''))
                return v.Substring(1, v.Length - 2);
            return v;
        }

        private static List<string> FlowSeq(string v)
        {
            v = v.Trim();
            if (!v.StartsWith("[") || !v.EndsWith("]")) return null;
            var list = new List<string>();
            foreach (var part in v.Substring(1, v.Length - 2).Split(','))
                if (part.Trim().Length > 0) list.Add(Unquote(part));
            return list;
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
