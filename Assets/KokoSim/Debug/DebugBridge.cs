#if KOKOSIM_DEBUG || UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Text;
using KokoSim.Engine.Core;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Unity.Shell;
using UnityEngine;

namespace KokoSim.Unity.Debugging
{
    /// <summary>
    /// MCP デバッグAPI（設計書17 §7, F2）。<b>全メソッド static・引数と戻り値はプリミティブと JSON 文字列のみ</b>。
    ///
    /// <para>この制約は <c>mcp__UnityMCP__execute_code</c> が C#6 相当のコンパイラで動き、
    /// <c>required</c> メンバーを持つ型を <c>new</c> できないことから来ている。プリミティブと文字列だけなら
    /// 1行で確実に叩ける。</para>
    ///
    /// <para><b>例外を投げない</b>（設計書17 §7）。失敗はすべて <c>{"ok":false,"err":"..."}</c> を返す。
    /// execute_code からスタックトレースを読むのは高コストなため。</para>
    ///
    /// <para>使い方（MCP から）:</para>
    /// <code>
    /// KokoSim.Unity.Debugging.DebugBridge.StartMatch("bases-loaded-9th", "0x9f3a5511");
    /// KokoSim.Unity.Debugging.DebugBridge.AdvancePitch(3);
    /// KokoSim.Unity.Debugging.DebugBridge.Force("WildPitch");
    /// return KokoSim.Unity.Debugging.DebugBridge.DumpState();
    /// </code>
    ///
    /// <para>人間は <c>KokoSim/Debug/...</c> のEditorメニューから<b>同じ関数</b>を叩く（経路を二重化しない）。</para>
    /// </summary>
    public static class DebugBridge
    {
        // ヘッドレスに回す試合。画面（MatchLive）とは独立で、盤面を出さずに1球ずつ検算できる。
        private static MatchProgression _prog;
        private static KokoSim.Engine.Match.Game.Team _away, _home;
        private static GameContext _ctx;
        private static string _scenarioId;
        private static ulong _seed;
        // "player" 指定を実部員で解決したか（設計書17 §6, #96）。"roster"=自校ロスター／"generated"=生成校。
        private static string _playerTeamSource = "generated";

        // ===== 一覧・起動 =====

        /// <summary>シナリオ一覧。<c>data/debug/</c> が無ければ 0件で ok（リリース想定の正常系）。</summary>
        public static string ListScenarios()
        {
            // ResolvedPath は Catalog の遅延ロードで初めて埋まるので、先に触ってから読む。
            var catalog = DebugScenarios.Catalog;
            var sb = new StringBuilder("{\"ok\":true,\"path\":");
            sb.Append(TraceJson.Str(DebugScenarios.ResolvedPath ?? ""));
            sb.Append(",\"count\":").Append(TraceJson.Int(catalog.Count));
            sb.Append(",\"scenarios\":[");
            var first = true;
            foreach (var s in catalog.All)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"id\":").Append(TraceJson.Str(s.Id))
                  .Append(",\"name\":").Append(TraceJson.Str(s.Name))
                  .Append(",\"inning\":").Append(TraceJson.Int(s.Inning))
                  .Append(",\"top\":").Append(TraceJson.Bool(s.Top))
                  .Append(",\"force\":").Append(s.Force == null ? "null" : TraceJson.Str(s.Force))
                  .Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>シナリオから試合を起こす。scenarioId が空なら1回表頭の平均的な対戦。</summary>
        public static string StartMatch(string scenarioId, string seedHex)
        {
            try
            {
                var seed = ParseSeed(seedHex);
                ScenarioStart start = null;
                var ctx = DebugTraceHub.AttachTo(new GameContext { CaptureTimelines = true });

                if (!string.IsNullOrEmpty(scenarioId))
                {
                    if (!DebugScenarios.Catalog.TryGet(scenarioId, out var def))
                        return Err("unknown-scenario:" + scenarioId);
                    // "player" 指定を実部員で解決する（設計書17 §6, #96）。ロスター未ロード時は null を渡し、
                    // engine 側の既定（平均55の生成校）へフォールバックする＝CLI/engineテストと同じ契約。
                    var playerTeam = TryBuildPlayerTeam(out _playerTeamSource);
                    var built = ScenarioBuilder.Build(def, ctx, seed, playerTeam);
                    _away = built.Away;
                    _home = built.Home;
                    ctx = built.Ctx;
                    start = built.Start;
                    seed = built.Seed;
                    _scenarioId = def.Id;
                }
                else
                {
                    var g = new Xoshiro256Random(seed);
                    _away = KokoSim.Engine.Nation.StrengthTeamFactory.Create(60, "遠征校", g);
                    _home = KokoSim.Engine.Nation.StrengthTeamFactory.Create(60, "自校", g);
                    _scenarioId = null;
                    _playerTeamSource = "generated"; // シナリオ無し＝"player"の概念なし。両校とも生成校。
                }

                _ctx = ctx;
                _seed = seed;
                _prog = new MatchProgression(_away, _home, _ctx, seed, start);
                return DumpState();
            }
            catch (System.Exception e) { return Err(e.Message); }
        }

        /// <summary>再現トークンから場面を復元する。指紋が合わなければ<b>再生せずに</b>警告を返す。</summary>
        public static string Restore(string reproToken)
        {
            try
            {
                if (_prog == null) return Err("no-match（先に StartMatch を呼ぶこと）");
                if (!ReproToken.TryParse(reproToken, out var token)) return Err("bad-token");
                if (!token.Verify(_away, _home, _ctx))
                    return Err("fixture-mismatch（別のロスター/ルールです。黙って別の試合を再生しません）");

                _prog = new MatchProgression(_away, _home, _ctx, _seed);
                _prog.SeekTo(token.PlateAppearance, token.Pitch);
                return DumpState();
            }
            catch (System.Exception e) { return Err(e.Message); }
        }

        // ===== 進行 =====

        public static string AdvancePitch(int n)
        {
            if (_prog == null) return Err("no-match");
            try
            {
                for (var i = 0; i < n; i++)
                {
                    if (_prog.AdvancePitch() == AdvancePitchResult.Finished) break;
                }
                return DumpTrace(n);
            }
            catch (System.Exception e) { return Err(e.Message); }
        }

        public static string AdvancePa(int n)
        {
            if (_prog == null) return Err("no-match");
            try
            {
                var moved = _prog.AdvancePa(n);
                return "{\"ok\":true,\"advanced\":" + TraceJson.Int(moved) + ",\"state\":" + StateBody() + "}";
            }
            catch (System.Exception e) { return Err(e.Message); }
        }

        /// <summary>"inning-end" | "game-end" | "pa" のいずれかまで進める。</summary>
        public static string AdvanceUntil(string what)
        {
            if (_prog == null) return Err("no-match");
            try
            {
                switch ((what ?? "").ToLowerInvariant())
                {
                    case "inning-end":
                        _prog.AdvanceUntilInningEnd();
                        break;
                    case "game-end":
                        _prog.FinishRemaining();
                        break;
                    case "pa":
                        _prog.Advance();
                        break;
                    default:
                        return Err("unknown-target:" + what + "（inning-end|game-end|pa）");
                }
                return DumpState();
            }
            catch (System.Exception e) { return Err(e.Message); }
        }

        public static string SeekTo(int pa, int pitch)
        {
            if (_prog == null) return Err("no-match");
            try
            {
                _prog.SeekTo(pa, pitch);
                return DumpState();
            }
            catch (System.Exception e) { return Err(e.Message); }
        }

        // ===== 注入 =====

        /// <summary>次の1打席に効く強制発動を予約する（設計書17 §6.1）。</summary>
        public static string Force(string forcedOutcome)
        {
            if (_prog == null) return Err("no-match");
            if (!ForcedOutcomes.TryParse(forcedOutcome, out var f))
                return Err("unknown-outcome:" + forcedOutcome + "（" + string.Join("|", System.Enum.GetNames(typeof(ForcedOutcome))) + "）");
            _prog.ForceNext(f);
            return Ok("forced", f.ToString());
        }

        /// <summary>次の1球の打撃指示（ForceSwing/ForceTake/Bunt/SafetyBunt/Squeeze）。空文字で解除。</summary>
        public static string SetBattingOverride(string sign)
        {
            if (_prog == null) return Err("no-match");
            if (string.IsNullOrEmpty(sign))
            {
                _prog.SetPitchBattingOverride(false, null);
                return Ok("batting", "-");
            }
            if (!System.Enum.TryParse(sign, true, out PitchBattingOverride v))
                return Err("unknown-sign:" + sign);
            _prog.SetPitchBattingOverride(false, v);   // 自校＝後攻(home) を既定の対象にする
            return Ok("batting", v.ToString());
        }

        /// <summary>次の1球の配球方針・ギア。空文字はその項目を触らない。</summary>
        public static string SetDefenseOverride(string policy, string gear)
        {
            if (_prog == null) return Err("no-match");
            PitchPolicy? p = null;
            KokoSim.Engine.Match.Pitching.PitcherGear? g = null;
            if (!string.IsNullOrEmpty(policy))
            {
                if (!System.Enum.TryParse(policy, true, out PitchPolicy pv)) return Err("unknown-policy:" + policy);
                p = pv;
            }
            if (!string.IsNullOrEmpty(gear))
            {
                if (!System.Enum.TryParse(gear, true, out KokoSim.Engine.Match.Pitching.PitcherGear gv))
                    return Err("unknown-gear:" + gear);
                g = gv;
            }
            _prog.SetPitchDefenseOverride(false, p, g);
            return Ok("defense", (p?.ToString() ?? "-") + "/" + (g?.ToString() ?? "-"));
        }

        // ===== 観測 =====

        /// <summary>スコア・イニング・進行位置・再現トークン。</summary>
        public static string DumpState()
        {
            if (_prog == null) return Err("no-match");
            return "{\"ok\":true,\"state\":" + StateBody() + "}";
        }

        /// <summary>直近 lastN 球の観測（JSONL 文字列。設計書17 §4.3 と同じキー）。</summary>
        public static string DumpTrace(int lastN)
        {
            var ring = DebugTraceHub.Ring;
            if (ring == null) return Err("no-trace（DebugTraceHub.Enabled を立ててから StartMatch）");
            var sb = new StringBuilder();
            foreach (var t in ring.RecentPitches(lastN <= 0 ? 8 : lastN))
            {
                if (sb.Length > 0) sb.Append('\n');
                TraceJson.Pitch(sb, t);
            }
            return "{\"ok\":true,\"jsonl\":" + TraceJson.Str(sb.ToString()) + "}";
        }

        /// <summary>ライブ観戦スナップショット（両軍の現ラインナップと今日の成績）の要約。</summary>
        public static string DumpSnapshot()
        {
            if (_prog == null) return Err("no-match");
            try
            {
                var s = _prog.Snapshot();
                var sb = new StringBuilder("{\"ok\":true,\"isTop\":");
                sb.Append(TraceJson.Bool(s.OffenseIsTop))
                  .Append(",\"batterOrder\":").Append(TraceJson.Int(s.CurrentBatterOrder))
                  .Append(",\"playerTeam\":").Append(TraceJson.Str(_playerTeamSource ?? "generated"))
                  .Append(",\"away\":").Append(TeamLineupJson(s.Away))
                  .Append(",\"home\":").Append(TeamLineupJson(s.Home))
                  .Append('}');
                return sb.ToString();
            }
            catch (System.Exception e) { return Err(e.Message); }
        }

        // ===== 画面 =====

        /// <summary>既存の <see cref="ScreenshotCapture"/> 経由でスクショを撮る（Play 中のみ）。</summary>
        public static string Screenshot(string path)
        {
            if (!Application.isPlaying) return Err("not-playing");
            var screen = ScreenRouter.Instance != null ? ScreenRouter.Instance.CurrentScreen : null;
            if (string.IsNullOrEmpty(screen)) return Err("no-active-screen");
            ScreenshotCapture.Capture(screen, 1600, 900, path);
            return Ok("screenshot", path);
        }

        /// <summary>デバッグHUDの表示を切り替える（F1キーと同じ経路）。</summary>
        public static string ToggleHud(bool on)
        {
            DebugTraceHub.Enabled = on || DebugTraceHub.Enabled;
            DebugTraceHub.HudVisible = on;
            return Ok("hud", on ? "on" : "off");
        }

        /// <summary>画面遷移（<see cref="ScreenRouter"/> 経由。Play 中のみ）。</summary>
        public static string Goto(string screenName)
        {
            if (!Application.isPlaying) return Err("not-playing");
            if (ScreenRouter.Instance == null) return Err("no-router");
            ScreenRouter.Instance.ShowDeferred(screenName);
            return Ok("goto", screenName);
        }

        // ===== 内部 =====

        /// <summary>
        /// 自校ラインナップをロスターから組む（設計書17 §6, #96）。表示スタメンと出場スタメンが一致する
        /// 契約を二重化しないため <see cref="PlayerMatchResolver.BuildManagerTeam"/> を唯一の入口として再利用する。
        /// タイトル直後などロスター未ロード時は null＝engine 既定（生成校）へフォールバックし、
        /// どちらを使ったかを <paramref name="source"/>（"roster"/"generated"）で返す。
        /// </summary>
        private static KokoSim.Engine.Match.Game.Team TryBuildPlayerTeam(out string source)
        {
            try
            {
                var roster = RosterService.Active;
                if (roster == null || roster.Count == 0) { source = "generated"; return null; }
                var team = PlayerMatchResolver.BuildManagerTeam("自校");
                source = "roster";
                return team;
            }
            catch
            {
                // BuildManagerTeam は GameSession.Current を参照するため、未初期化なら生成校へ退避する。
                source = "generated";
                return null;
            }
        }

        /// <summary>
        /// スナップショット1チームのスタメン列を JSON へ（設計書17 §6, #96 受け入れ: 実部員の氏名・背番号が出ること）。
        /// 自校を実部員で解決していれば <c>num</c>（背番号）と <c>id</c>（選手ID）が実値、生成校は 0/null。
        /// </summary>
        private static string TeamLineupJson(KokoSim.Engine.Match.Game.LiveTeamSnapshot team)
        {
            var sb = new StringBuilder("{\"lineup\":[");
            var first = true;
            foreach (var slot in team.Lineup)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"order\":").Append(TraceJson.Int(slot.Order))
                  .Append(",\"num\":").Append(TraceJson.Int(slot.Number))
                  .Append(",\"name\":").Append(TraceJson.Str(slot.Name))
                  .Append(",\"pos\":").Append(TraceJson.Str(slot.Position.ToString()))
                  .Append(",\"id\":").Append(slot.SourceId.HasValue ? TraceJson.Int(slot.SourceId.Value) : "null")
                  .Append('}');
            }
            sb.Append("],\"pitcher\":{\"num\":").Append(TraceJson.Int(team.Pitcher.Number))
              .Append(",\"name\":").Append(TraceJson.Str(team.Pitcher.Name))
              .Append(",\"id\":").Append(team.Pitcher.SourceId.HasValue ? TraceJson.Int(team.Pitcher.SourceId.Value) : "null")
              .Append("}}");
            return sb.ToString();
        }

        private static string StateBody()
        {
            var sb = new StringBuilder("{\"scenario\":");
            sb.Append(_scenarioId == null ? "null" : TraceJson.Str(_scenarioId))
              .Append(",\"seed\":").Append(TraceJson.Str("0x" + _seed.ToString("x16")))
              .Append(",\"playerTeam\":").Append(TraceJson.Str(_playerTeamSource ?? "generated"))
              .Append(",\"pa\":").Append(TraceJson.Int(_prog.ConfirmedPlateAppearances))
              .Append(",\"away\":").Append(TraceJson.Int(_prog.AwayScore))
              .Append(",\"home\":").Append(TraceJson.Int(_prog.HomeScore))
              .Append(",\"finished\":").Append(TraceJson.Bool(_prog.IsFinished));

            var cur = _prog.Current;
            if (cur != null)
            {
                sb.Append(",\"last\":{\"i\":").Append(TraceJson.Int(cur.Inning))
                  .Append(",\"top\":").Append(TraceJson.Bool(cur.IsTop))
                  .Append(",\"outs\":").Append(TraceJson.Int(cur.OutsBefore))
                  .Append(",\"batter\":").Append(TraceJson.Str(cur.BatterName))
                  .Append(",\"res\":").Append(TraceJson.Str(cur.Result.ToString()))
                  .Append(",\"rbi\":").Append(TraceJson.Int(cur.RunsScored))
                  .Append(",\"bases\":").Append(TraceJson.Str(
                      (cur.BaseFirstAfter ? "1" : "-") + (cur.BaseSecondAfter ? "2" : "-") + (cur.BaseThirdAfter ? "3" : "-")))
                  .Append('}');
            }

            var token = _prog.ReproToken();
            sb.Append(",\"repro\":").Append(token.HasValue ? TraceJson.Str(token.Value.ToString()) : "null");
            sb.Append('}');
            return sb.ToString();
        }

        private static ulong ParseSeed(string seedHex)
        {
            if (string.IsNullOrEmpty(seedHex)) return GameSeed.Master;
            var s = seedHex.Trim();
            if (s.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            return ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var v)
                ? v
                : ulong.Parse(seedHex, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string Ok(string key, string value)
            => "{\"ok\":true,\"" + key + "\":" + TraceJson.Str(value) + "}";

        private static string Err(string message)
            => "{\"ok\":false,\"err\":" + TraceJson.Str(message) + "}";
    }
}
#endif
