using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Saves.Json;
using KokoSim.Engine.Season;
using KokoSim.Engine.Stats;

namespace KokoSim.Balance;

/// <summary>
/// 長年プレイしたときのセーブの大きさ・保存/読込の時間の測定（issue #1414）。
/// 自校の年度送り（卒業・新入生・週ごとの練習・月ごとの能力推移の記録・成績の積み上げ）を N 年回し、
/// 節目の年ごとに「卒業生を選手データ丸ごと持つ（旧）」と「名前の表で持つ（新）」の2通りでセーブを組んで測る。
/// 全国の学校（約4000校）は年数に依らずほぼ一定の固まりなので含めない（<c>SaveSizeTests</c> が別に測る）。
/// 成績は合成のボックススコア（出場選手ごとの行の数は実プレイ並み）。試合結果そのものは再現しない。
/// </summary>
public static class SaveGrowthSimulation
{
    public sealed record Row(
        int Year, string Mode, int Active, int Graduates, long JsonBytes, long GzipBytes,
        double SerializeMs, double GzipMs, double DeserializeMs, IReadOnlyList<(string Key, long Bytes)> Sections);

    private const int SummerGames = 5;
    private const int AutumnGames = 4;
    private const int PracticeGames = 15;

    public static IReadOnlyList<Row> Run(IReadOnlyList<int> checkpoints, ulong seed)
    {
        var maxYear = checkpoints.Max();
        var ctx = new SeasonContext();
        var rng = new Xoshiro256Random(seed);
        var roster = new List<DevelopingPlayer>();
        var graduateBodies = new List<DevelopingPlayer>();
        var roll = new GraduateRoll();
        var stats = new PlayerStatStore();
        var nextId = 1;
        var rows = new List<Row>();

        for (var year = 1; year <= maxYear; year++)
        {
            var rollover = RosterLifecycle.AdvanceYear(roster, year, ctx.Roster, rng,
                skills: ctx.Skills, personalities: ctx.Personalities, form: ctx.Form, nextId: nextId);
            nextId = rollover.NextId;
            roll.AddGraduated(rollover.Graduated, year);
            graduateBodies.AddRange(rollover.Graduated);

            var (facilityCoef, budget) = ctx.ResolveFacility();
            var training = ctx.Training with { FacilityCoef = facilityCoef };
            for (var week = 0; week < ctx.Calendar.WeeksPerYear; week++)
            {
                RosterLifecycle.OnWeekEntered(roster, week, ctx.Calendar);
                AbilityHistory.OnWeekEntered(roster, week, ctx.Calendar);
                var campMult = ctx.Calendar.CampMultiplier(week, ctx.Training);
                foreach (var p in roster)
                {
                    if (!ctx.Calendar.CanTrain(p.Grade, week)) continue;
                    var alloc = TrainingPresets.Resolve(p.Plan ?? TrainingPlan.Default, budget);
                    DevelopmentModel.TrainWeekPlan(p, alloc, training.ReferenceWeekMinutes,
                        ctx.Calendar.StageIndex(p.Grade, week), campMult, ctx.Stages, training, ctx.Skills,
                        ctx.Personalities, ctx.Coaching, ctx.Equipment, roster);
                }
            }

            FoldYearOfGames(stats, roster, year);

            if (!checkpoints.Contains(year)) continue;
            rows.Add(Measure(year, "旧（卒業生を丸ごと）", roster, graduateBodies, new GraduateRoll(), stats));
            rows.Add(Measure(year, "新（名前の表）", roster, new List<DevelopingPlayer>(), roll, stats));
        }
        return rows;
    }

    private static void FoldYearOfGames(PlayerStatStore stats, List<DevelopingPlayer> roster, int year)
    {
        var info = new Dictionary<int, (int Grade, int UniformNumber)>();
        for (var i = 0; i < roster.Count; i++) info[roster[i].Id] = (roster[i].Grade, i + 1);

        foreach (var (slot, games) in new[] { (TournamentSlot.SummerPref, SummerGames), (TournamentSlot.Autumn, AutumnGames) })
        {
            stats.StartTournament();
            for (var g = 0; g < games; g++) stats.FoldGame(SyntheticGame(roster, g), managerIsAway: true, isOfficial: true);
            stats.ArchiveCurrentTournament(slot, info, year);
        }
        for (var g = 0; g < PracticeGames; g++) stats.FoldGame(SyntheticGame(roster, g), managerIsAway: true, isOfficial: false);
    }

    private static GameResult SyntheticGame(List<DevelopingPlayer> roster, int g)
    {
        var batters = roster.Where(p => !p.IsRetired).Skip(g % 3).Take(9).ToList();
        var line = batters.Select((p, i) => new BattingLine(i + 1, FieldPosition.LeftField, p.Name,
            PlateAppearances: 5, AtBats: 4, Hits: 1 + (p.Id + g) % 2, Doubles: 0, Triples: 0, HomeRuns: (p.Id + g) % 7 == 0 ? 1 : 0,
            Rbi: 1, Walks: 1, StrikeOuts: 1, SourceId: p.Id)).ToArray();
        var pitcher = batters.Count > 0 ? batters[0] : null;
        return new GameResult
        {
            AwayName = "自校", HomeName = "相手校", AwayRuns = 4, HomeRuns = 2,
            InningsPlayed = 9, TotalPitches = 240, PitcherChanges = 0,
            AwayBatting = line,
            AwayPitching = pitcher is null ? System.Array.Empty<PitchingLine>() : new[]
            {
                new PitchingLine(pitcher.Name, Outs: 27, BattersFaced: 34, Hits: 5, Runs: 2, StrikeOuts: 8, Walks: 2,
                    Pitches: 120, SourceId: pitcher.Id, HomeRunsAllowed: 1, EarnedRuns: 2),
            },
        };
    }

    private static Row Measure(int year, string mode, List<DevelopingPlayer> roster, List<DevelopingPlayer> graduateBodies,
        GraduateRoll roll, PlayerStatStore stats)
    {
        var all = new List<DevelopingPlayer>(roster);
        all.AddRange(graduateBodies);
        var state = new SaveState { Roster = all, Graduates = roll, Stats = stats, YearIndex = year, NextPlayerId = 1 };

        var sw = Stopwatch.StartNew();
        var json = SaveCodec.Serialize(state);
        var serializeMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var gz = GzipLength(json);
        var gzipMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var ok = SaveCodec.TryDeserialize(json, out _, out var err);
        var deserializeMs = sw.Elapsed.TotalMilliseconds;
        if (!ok) throw new InvalidOperationException($"往復に失敗: {err}");

        var sections = new List<(string, long)>();
        var root = JsonValue.Parse(json).AsObject();
        foreach (var key in root.Keys)
            sections.Add((key, Encoding.UTF8.GetByteCount(root[key].ToString())));
        sections.Sort((a, b) => b.Item2.CompareTo(a.Item2));

        return new Row(year, mode, roster.Count, graduateBodies.Count > 0 ? graduateBodies.Count : roll.Count,
            Encoding.UTF8.GetByteCount(json), gz, serializeMs, gzipMs, deserializeMs, sections);
    }

    private static long GzipLength(string text)
    {
        var raw = Encoding.UTF8.GetBytes(text);
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(raw, 0, raw.Length);
        return ms.Length;
    }

    public static string Report(IReadOnlyList<Row> rows, ulong seed)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("# 長年プレイのセーブ測定（issue #1414）");
        sb.AppendLine();
        sb.AppendLine(c, $"- シード: {seed}。全国の学校（約4000校ぶん・年数に依らずほぼ一定）は含まない。成績は合成（毎年 夏{SummerGames}試合＋秋{AutumnGames}試合＋練習{PracticeGames}試合）");
        sb.AppendLine("- 載せていないもの: 全国の学校・歴代大会結果・戦績台帳（#1413）・記録帳・教え子名鑑。年ごとに増えるものは別途測る");
        sb.AppendLine("- 保存＝JSON 組み立て＋gzip（ファイル書き出しは含まない）、読込＝JSON パース＋復元（gzip 展開は含まない）。時間は1回測りの参考値");
        sb.AppendLine();
        sb.AppendLine("| 年 | 方式 | 在籍 | 卒業生 | JSON | gzip | 組立 ms | gzip ms | 読込 ms |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var r in rows)
            sb.AppendLine(c, $"| {r.Year} | {r.Mode} | {r.Active} | {r.Graduates} | {Mib(r.JsonBytes)} | {Mib(r.GzipBytes)} | {r.SerializeMs:F0} | {r.GzipMs:F0} | {r.DeserializeMs:F0} |");
        sb.AppendLine();
        sb.AppendLine("## 内訳（新方式・JSON のキーごとの大きさ。上位8）");
        foreach (var r in rows.Where(r => r.Mode.StartsWith("新")))
        {
            sb.AppendLine();
            sb.AppendLine(c, $"### {r.Year}年目");
            sb.AppendLine("| キー | JSON | 割合 |");
            sb.AppendLine("|---|---|---|");
            foreach (var (key, bytes) in r.Sections.Take(8))
                sb.AppendLine(c, $"| {key} | {Mib(bytes)} | {(double)bytes / r.JsonBytes:P0} |");
        }
        return sb.ToString();
    }

    private static string Mib(long bytes) => (bytes / 1024.0 / 1024.0).ToString("F2", CultureInfo.InvariantCulture) + " MiB";
}
