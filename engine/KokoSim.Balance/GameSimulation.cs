using System.Globalization;
using System.Text;
using KokoSim.Config;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;

namespace KokoSim.Balance;

/// <summary>1万試合シミュレーションと得点分布・試合時間の集計（Phase 2 DoD）。</summary>
public static class GameSimulation
{
    // 試合時間の推定係数（1球あたり秒 ＋ ハーフイニングの合間）。
    private const double SecondsPerPitch = 22.0;
    private const double SecondsPerHalfInningBreak = 75.0;

    // 球種別集計（設計書15 Phase E-4）の配列長。PitchType enum の要素数。
    private static readonly int PitchTypeCount = System.Enum.GetValues(typeof(PitchType)).Length;

    private static readonly FieldPosition[] FieldSlots =
    {
        FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
        FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField,
        FieldPosition.CenterField, FieldPosition.RightField,
    };

    public sealed record Stats
    {
        public int Games { get; init; }
        public int[] RunsPerTeamHistogram { get; init; } = new int[30];
        public long TotalTeamRuns { get; init; }
        public long TotalPitches { get; init; }
        public long TotalInnings { get; init; }
        public int HomeWins { get; init; }
        public int Ties { get; init; }
        public int ExtraInningGames { get; init; }
        public int Shutouts { get; init; }
        public long TotalPitcherChanges { get; init; }
        public long TotalHomePlayOuts { get; init; }
        public long TotalThirdPlayOuts { get; init; }
        /// <summary>失策総数（両軍計・全試合計。issue #123: 試合単位の失策率の校正用）。</summary>
        public long TotalErrors { get; init; }

        // ===== design-14 第1段（P1）新プレー発生数（両軍計・全試合計） =====
        public long TotalFieldersChoice { get; init; }
        public long TotalDroppedThirdStrike { get; init; }
        public long TotalErrorExtraAdvance { get; init; }
        public long TotalPickoffs { get; init; }
        public long TotalIntentionalWalks { get; init; }
        public long TotalDoubleStealThirdBreaks { get; init; }
        public long TotalWildPitches { get; init; }
        public long TotalStealAttempts { get; init; }
        public long TotalStealSuccesses { get; init; }
        public long TotalSacrificeBunts { get; init; }
        public long TotalSacrificeBuntSuccesses { get; init; }
        /// <summary>1球采配（設計書15 Phase C）の帯校正用: 三振/四球/本塁打は1球判断の影響を最も受ける。</summary>
        public long TotalStrikeOuts { get; init; }
        public long TotalWalks { get; init; }
        public long TotalHitBatters { get; init; }
        public long TotalHomeRuns { get; init; }
        public long TotalPlateAppearances { get; init; }

        // ===== 球種別の空振り率・チェイス率（設計書15 Phase E-4）=====
        // PitchType の enum 添字（長さ = PitchTypeCount）。すべて整数集計＝並列マージ順に依らず決定論。
        // 空振り率 = 空振り(SwingingStrike) / スイング(SwingingStrike+Foul+InPlay)。
        // チェイス率 = ゾーン外でスイングした球 / ゾーン外の球（見え方と実軌道のズレの効きを球種別に見る）。
        public long[] SwingsByPitchType { get; init; } = new long[PitchTypeCount];
        public long[] WhiffsByPitchType { get; init; } = new long[PitchTypeCount];
        public long[] OutOfZoneByPitchType { get; init; } = new long[PitchTypeCount];
        public long[] ChasesByPitchType { get; init; } = new long[PitchTypeCount];

        public double WhiffRateOf(PitchType type) =>
            SwingsByPitchType[(int)type] > 0 ? (double)WhiffsByPitchType[(int)type] / SwingsByPitchType[(int)type] : 0;
        public double ChaseRateOf(PitchType type) =>
            OutOfZoneByPitchType[(int)type] > 0 ? (double)ChasesByPitchType[(int)type] / OutOfZoneByPitchType[(int)type] : 0;

        public double AverageRunsPerTeam => Games > 0 ? (double)TotalTeamRuns / (Games * 2) : 0;
        /// <summary>本塁クロスプレー憤死/試合（両軍計。設計書12 §3 F2の参考指標）。</summary>
        public double AverageHomePlayOutsPerGame => Games > 0 ? (double)TotalHomePlayOuts / Games : 0;
        /// <summary>三塁憤死/試合（両軍計。単打の一塁→三塁レース, Issue #89の参考指標）。</summary>
        public double AverageThirdPlayOutsPerGame => Games > 0 ? (double)TotalThirdPlayOuts / Games : 0;
        /// <summary>失策数/試合（両軍計。issue #123: 甲子園実測≈2.1〜2.7）。</summary>
        public double ErrorsPerGame => Games > 0 ? (double)TotalErrors / Games : 0;
        public double AveragePitchesPerGame => Games > 0 ? (double)TotalPitches / Games : 0;
        public double AverageInnings => Games > 0 ? (double)TotalInnings / Games : 0;
        public double HomeWinRate => Games > 0 ? (double)HomeWins / Games : 0;
        public double ExtraInningRate => Games > 0 ? (double)ExtraInningGames / Games : 0;
        public double AverageMinutes =>
            (AveragePitchesPerGame * SecondsPerPitch + AverageInnings * 2 * SecondsPerHalfInningBreak) / 60.0;

        // ===== design-14 P1 新プレー発生率/試合（両軍計） =====
        public double FieldersChoicePerGame => Games > 0 ? (double)TotalFieldersChoice / Games : 0;
        public double DroppedThirdStrikePerGame => Games > 0 ? (double)TotalDroppedThirdStrike / Games : 0;
        public double ErrorExtraAdvancePerGame => Games > 0 ? (double)TotalErrorExtraAdvance / Games : 0;
        public double PickoffsPerGame => Games > 0 ? (double)TotalPickoffs / Games : 0;
        public double IntentionalWalksPerGame => Games > 0 ? (double)TotalIntentionalWalks / Games : 0;
        public double DoubleStealThirdBreaksPerGame => Games > 0 ? (double)TotalDoubleStealThirdBreaks / Games : 0;
        public double WildPitchesPerGame => Games > 0 ? (double)TotalWildPitches / Games : 0;
        public double StealSuccessRate => TotalStealAttempts > 0 ? (double)TotalStealSuccesses / TotalStealAttempts : 0;
        public double SacrificeBuntsPerGame => Games > 0 ? (double)TotalSacrificeBunts / Games : 0;
        public double SacrificeBuntSuccessRate =>
            TotalSacrificeBunts > 0 ? (double)TotalSacrificeBuntSuccesses / TotalSacrificeBunts : 0;
        public double StrikeoutRate => TotalPlateAppearances > 0 ? (double)TotalStrikeOuts / TotalPlateAppearances : 0;
        public double WalkRate => TotalPlateAppearances > 0 ? (double)TotalWalks / TotalPlateAppearances : 0;
        public double HitByPitchRate => TotalPlateAppearances > 0 ? (double)TotalHitBatters / TotalPlateAppearances : 0;
        public double HomeRunRate => TotalPlateAppearances > 0 ? (double)TotalHomeRuns / TotalPlateAppearances : 0;
    }

    /// <summary>
    /// <paramref name="useTacticsBrain"/>=true で両チームに <see cref="StandardTacticsBrain"/>（YAML係数駆動）を
    /// 付与する（design-14 第1段の敬遠・重盗・牽制は采配Brainが盗塁/敬遠を選ばない限り発火しないため）。
    /// 既定 false は従来通り無指示＝Brainなしの試合（K/9・BB/9等の既存帯はこちらで校正済み、非破壊）。
    /// </summary>
    public static Stats Run(int games, ulong seed, string? coefficientsPath = null, FieldGeometry? field = null,
        bool useTacticsBrain = false)
    {
        var ctx = BuildContext(coefficientsPath, field);
        var brain = useTacticsBrain ? new StandardTacticsBrain(ctx.Tactics, ctx.Baserunning) : null;
        var root = new Xoshiro256Random(seed);

        // 決定論を保った並列化（統計テスト高速化）:
        // 1) 各試合の乱数ストリームは逐次 Fork で事前確定（i→シード対応は逐次実行と同一）
        // 2) 集計は全て整数加算（可換・結合的）→ 実行順序・スレッド数に依らず同シード同結果
        // 3) ctx は不変 record、チーム生成・試合状態は試合ローカル → データ競合なし
        var forks = new IRandomSource[games];
        for (var i = 0; i < games; i++)
        {
            forks[i] = root.Fork((ulong)i);
        }

        var total = new Accumulator();
        Parallel.For(0, games,
            () => new Accumulator(),
            (i, _, local) =>
            {
                var g = forks[i];
                var away = GenerateTeam(g, "遠征校") with { Tactics = brain };
                var home = GenerateTeam(g, "地元校") with { Tactics = brain };
                var r = GameEngine.Play(away, home, ctx, g);
                local.Add(r, ctx.RegulationInnings, ctx.StrikeZone);
                return local;
            },
            local => { lock (total) total.Merge(local); });

        return total.ToStats(games);
    }

    /// <summary>並列集計用の部分和（整数のみ＝マージ順に依らず決定論）。</summary>
    private sealed class Accumulator
    {
        public readonly int[] Hist = new int[30];
        public long TotalRuns, TotalPitches, TotalInnings, TotalChanges, TotalHomePlayOuts, TotalThirdPlayOuts, TotalErrors;
        public long TotalFc, TotalDropThird, TotalErrorExtra, TotalPickoffs, TotalIntentionalWalks, TotalDoubleSteal;
        public long TotalWildPitches;
        public long TotalStealAttempts, TotalStealSuccesses;
        public long TotalSacrificeBunts, TotalSacrificeBuntSuccesses;
        public long TotalStrikeOuts, TotalWalks, TotalHitBatters, TotalHomeRuns, TotalPlateAppearances;
        public int HomeWins, Ties, Extra, Shutouts;
        // 球種別（設計書15 Phase E-4）: enum 添字。
        public readonly long[] Swings = new long[PitchTypeCount];
        public readonly long[] Whiffs = new long[PitchTypeCount];
        public readonly long[] OutOfZone = new long[PitchTypeCount];
        public readonly long[] Chases = new long[PitchTypeCount];

        public void Add(GameResult r, int regulationInnings, StrikeZone zone)
        {
            TotalRuns += r.AwayRuns + r.HomeRuns;
            TotalPitches += r.TotalPitches;
            TotalInnings += r.InningsPlayed;
            TotalChanges += r.PitcherChanges;
            TotalHomePlayOuts += r.HomePlayOuts;
            TotalThirdPlayOuts += r.ThirdPlayOuts;
            TotalErrors += r.AwayErrors + r.HomeErrors;
            TotalFc += r.FieldersChoiceCount;
            TotalDropThird += r.DroppedThirdStrikeCount;
            TotalErrorExtra += r.ErrorExtraAdvanceCount;
            TotalPickoffs += r.PickoffCount;
            TotalIntentionalWalks += r.IntentionalWalkCount;
            TotalDoubleSteal += r.DoubleStealThirdBreakCount;
            TotalWildPitches += r.WildPitchCount;
            TotalStealAttempts += r.AwayTactics.StealAttempts + r.HomeTactics.StealAttempts;
            TotalStealSuccesses += r.AwayTactics.StealSuccesses + r.HomeTactics.StealSuccesses;
            TotalSacrificeBunts += r.AwayTactics.SacrificeBunts + r.HomeTactics.SacrificeBunts;
            TotalSacrificeBuntSuccesses += r.AwayTactics.SacrificeBuntSuccesses + r.HomeTactics.SacrificeBuntSuccesses;
            foreach (var p in r.AwayPitching) { TotalStrikeOuts += p.StrikeOuts; TotalWalks += p.Walks; TotalHitBatters += p.HitBatters; }
            foreach (var p in r.HomePitching) { TotalStrikeOuts += p.StrikeOuts; TotalWalks += p.Walks; TotalHitBatters += p.HitBatters; }
            TotalPlateAppearances += r.Log.Count;
            foreach (var e in r.Log)
            {
                if (e.Result == KokoSim.Engine.Match.AtBat.PlateAppearanceResult.HomeRun) TotalHomeRuns++;

                // 球種別の空振り率・チェイス率（設計書15 Phase E-4）。PitchLog は AtBatSession 経由の打席にのみ載る。
                if (e.PitchLog is null) continue;
                foreach (var p in e.PitchLog)
                {
                    var t = (int)p.PitchType;
                    var swung = p.Kind is KokoSim.Engine.Match.AtBat.PitchKind.SwingingStrike
                        or KokoSim.Engine.Match.AtBat.PitchKind.Foul
                        or KokoSim.Engine.Match.AtBat.PitchKind.InPlay;
                    if (swung) Swings[t]++;
                    if (p.Kind == KokoSim.Engine.Match.AtBat.PitchKind.SwingingStrike) Whiffs[t]++;

                    if (!zone.Contains(p.LocationX, p.LocationY))
                    {
                        OutOfZone[t]++;
                        if (swung) Chases[t]++;
                    }
                }
            }
            if (r.HomeWon) HomeWins++;
            if (r.Tied) Ties++;
            if (r.InningsPlayed > regulationInnings) Extra++;
            if (r.AwayRuns == 0 || r.HomeRuns == 0) Shutouts++;
            Hist[Math.Min(r.AwayRuns, Hist.Length - 1)]++;
            Hist[Math.Min(r.HomeRuns, Hist.Length - 1)]++;
        }

        public void Merge(Accumulator o)
        {
            for (var i = 0; i < Hist.Length; i++) Hist[i] += o.Hist[i];
            TotalRuns += o.TotalRuns;
            TotalPitches += o.TotalPitches;
            TotalInnings += o.TotalInnings;
            TotalChanges += o.TotalChanges;
            TotalHomePlayOuts += o.TotalHomePlayOuts;
            TotalThirdPlayOuts += o.TotalThirdPlayOuts;
            TotalErrors += o.TotalErrors;
            TotalFc += o.TotalFc;
            TotalDropThird += o.TotalDropThird;
            TotalErrorExtra += o.TotalErrorExtra;
            TotalPickoffs += o.TotalPickoffs;
            TotalIntentionalWalks += o.TotalIntentionalWalks;
            TotalDoubleSteal += o.TotalDoubleSteal;
            TotalWildPitches += o.TotalWildPitches;
            TotalStealAttempts += o.TotalStealAttempts;
            TotalStealSuccesses += o.TotalStealSuccesses;
            TotalSacrificeBunts += o.TotalSacrificeBunts;
            TotalSacrificeBuntSuccesses += o.TotalSacrificeBuntSuccesses;
            TotalStrikeOuts += o.TotalStrikeOuts;
            TotalWalks += o.TotalWalks;
            TotalHitBatters += o.TotalHitBatters;
            TotalHomeRuns += o.TotalHomeRuns;
            TotalPlateAppearances += o.TotalPlateAppearances;
            for (var i = 0; i < PitchTypeCount; i++)
            {
                Swings[i] += o.Swings[i];
                Whiffs[i] += o.Whiffs[i];
                OutOfZone[i] += o.OutOfZone[i];
                Chases[i] += o.Chases[i];
            }
            HomeWins += o.HomeWins;
            Ties += o.Ties;
            Extra += o.Extra;
            Shutouts += o.Shutouts;
        }

        public Stats ToStats(int games) => new()
        {
            Games = games,
            RunsPerTeamHistogram = Hist,
            TotalTeamRuns = TotalRuns,
            TotalPitches = TotalPitches,
            TotalInnings = TotalInnings,
            HomeWins = HomeWins,
            Ties = Ties,
            ExtraInningGames = Extra,
            Shutouts = Shutouts,
            TotalPitcherChanges = TotalChanges,
            TotalHomePlayOuts = TotalHomePlayOuts,
            TotalThirdPlayOuts = TotalThirdPlayOuts,
            TotalErrors = TotalErrors,
            TotalFieldersChoice = TotalFc,
            TotalDroppedThirdStrike = TotalDropThird,
            TotalErrorExtraAdvance = TotalErrorExtra,
            TotalPickoffs = TotalPickoffs,
            TotalIntentionalWalks = TotalIntentionalWalks,
            TotalDoubleStealThirdBreaks = TotalDoubleSteal,
            TotalWildPitches = TotalWildPitches,
            TotalStealAttempts = TotalStealAttempts,
            TotalStealSuccesses = TotalStealSuccesses,
            TotalSacrificeBunts = TotalSacrificeBunts,
            TotalSacrificeBuntSuccesses = TotalSacrificeBuntSuccesses,
            TotalStrikeOuts = TotalStrikeOuts,
            TotalWalks = TotalWalks,
            TotalHitBatters = TotalHitBatters,
            TotalHomeRuns = TotalHomeRuns,
            TotalPlateAppearances = TotalPlateAppearances,
            SwingsByPitchType = Swings,
            WhiffsByPitchType = Whiffs,
            OutOfZoneByPitchType = OutOfZone,
            ChasesByPitchType = Chases,
        };
    }

    internal static GameContext BuildContext(string? coefficientsPath, FieldGeometry? field = null)
    {
        var geom = field ?? new FieldGeometry();
        if (coefficientsPath is null) return new GameContext { Field = geom };
        var b = CoefficientsLoader.LoadFromFile(coefficientsPath);
        return new GameContext
        {
            Aerodynamics = b.Aerodynamics,
            Pitching = b.Pitching,
            Batting = b.Batting,
            Fielding = b.Fielding,
            Baserunning = b.Baserunning,
            Fatigue = b.Fatigue,
            Form = b.Form,
            Skills = b.Skills,
            Pressure = b.Pressure,
            Tactics = b.Tactics,
            MatchInjury = b.MatchInjury,   // 試合中の受傷（Fork隔離＝結果・帯には影響しない観測データ）
            Field = geom,
        };
    }

    private static int Ability(IRandomSource rng)
        => (int)MathUtil.Clamp(Math.Round(rng.NextGaussian(50, 12)), 20, 88);

    private static Player GeneratePositionPlayer(IRandomSource rng, FieldPosition pos) => new()
    {
        Name = pos.ToString(),
        Position = pos,
        Contact = Ability(rng),
        Power = Ability(rng),
        LaunchTendency = Ability(rng),
        Discipline = Ability(rng),
        Speed = Ability(rng),
        ArmStrength = Ability(rng),
        Fielding = Ability(rng),
        Catching = Ability(rng),
    };

    private static Player GeneratePitcher(IRandomSource rng, string name)
    {
        var velo = MathUtil.Clamp(rng.NextGaussian(132, 6), 118, 150);
        var control = Ability(rng);
        var staminaLevel = Ability(rng);
        var pitchRank = Ability(rng);
        return new Player
        {
            Name = name,
            Position = FieldPosition.Pitcher,
            Contact = Ability(rng) / 2, // 投手は打撃控えめ
            Power = Ability(rng) / 2,
            LaunchTendency = Ability(rng),
            Discipline = Ability(rng),
            Speed = Ability(rng),
            ArmStrength = Ability(rng),
            Fielding = Ability(rng),
            Catching = Ability(rng),
            Pitching = new PitcherAttributes
            {
                MaxVelocityKmh = velo,
                Control = control,
                StaminaPitches = PitcherAttributes.StaminaPitchesFromLevel(staminaLevel),
                PitchRank = pitchRank,
                Repertoire = new[]
                {
                    PitchSlot.FastballOf(pitchRank),
                    new PitchSlot { Type = PitchType.Slider, Power = pitchRank, Sharpness = pitchRank },
                    new PitchSlot { Type = PitchType.Fork, Power = pitchRank, Sharpness = pitchRank },
                },
            },
        };
    }

    internal static Team GenerateTeam(IRandomSource rng, string name)
    {
        var order = new List<Player>(9);
        foreach (var pos in FieldSlots)
        {
            order.Add(GeneratePositionPlayer(rng, pos));
        }
        order.Add(GeneratePitcher(rng, name + "先発")); // スロット8＝投手

        var bullpen = new List<Player>
        {
            GeneratePitcher(rng, name + "中継ぎ"),
            GeneratePitcher(rng, name + "抑え"),
        };

        return new Team { Name = name, BattingOrder = order, PitcherSlot = 8, Bullpen = bullpen };
    }

    public static string Report(Stats s, ulong seed)
    {
        var sb = new StringBuilder();
        var c = CultureInfo.InvariantCulture;
        sb.AppendLine("# KokoSim 試合エンジン 統計レポート（Phase 2）");
        sb.AppendLine();
        sb.AppendLine(c, $"- シード: {seed} / 試合数: {s.Games}");
        sb.AppendLine();
        sb.AppendLine("| 指標 | 値 | 目標帯 |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine(c, $"| 平均得点/チーム | {s.AverageRunsPerTeam:F2} | 3.5–6.0 |");
        sb.AppendLine(c, $"| 平均球数/試合 | {s.AveragePitchesPerGame:F0} | 210–330 |");
        sb.AppendLine(c, $"| 推定試合時間(分) | {s.AverageMinutes:F0} | 90–160 |");
        sb.AppendLine(c, $"| 平均イニング | {s.AverageInnings:F2} | 9.0–9.8 |");
        sb.AppendLine(c, $"| 後攻勝率 | {s.HomeWinRate:P1} | 48–60% |");
        sb.AppendLine(c, $"| 延長試合率 | {s.ExtraInningRate:P1} | 参考 |");
        sb.AppendLine(c, $"| 完封試合率 | {(double)s.Shutouts / s.Games:P1} | 参考 |");
        sb.AppendLine(c, $"| 平均継投数/試合 | {(double)s.TotalPitcherChanges / s.Games:F2} | 参考 |");
        sb.AppendLine(c, $"| 本塁憤死/試合 | {s.AverageHomePlayOutsPerGame:F3} | 0.12–0.42 |");
        sb.AppendLine(c, $"| 三塁憤死/試合 | {s.AverageThirdPlayOutsPerGame:F3} | 0.02–0.40 |");
        sb.AppendLine(c, $"| 失策数/試合（両軍計） | {s.ErrorsPerGame:F2} | 1.8–3.6 |");
        sb.AppendLine(c, $"| 三振率（打席あたり） | {s.StrikeoutRate:P2} | 参考 |");
        sb.AppendLine(c, $"| 四球率（打席あたり） | {s.WalkRate:P2} | 参考 |");
        sb.AppendLine(c, $"| 死球率（打席あたり） | {s.HitByPitchRate:P2} | 参考 0.5–2.0% |");
        sb.AppendLine(c, $"| 本塁打率（打席あたり） | {s.HomeRunRate:P2} | 参考 |");
        sb.AppendLine();
        sb.AppendLine("## design-14 第1段（P1）新プレー発生率（両軍計/試合）");
        sb.AppendLine();
        sb.AppendLine("| プレー | 値/試合 |");
        sb.AppendLine("|---|---|");
        sb.AppendLine(c, $"| 野選（FC） | {s.FieldersChoicePerGame:F3} |");
        sb.AppendLine(c, $"| 振り逃げ | {s.DroppedThirdStrikePerGame:F3} |");
        sb.AppendLine(c, $"| 失策連鎖 | {s.ErrorExtraAdvancePerGame:F3} |");
        sb.AppendLine(c, $"| 牽制アウト | {s.PickoffsPerGame:F3} |");
        sb.AppendLine(c, $"| 敬遠 | {s.IntentionalWalksPerGame:F3} |");
        sb.AppendLine(c, $"| 一・三塁重盗 | {s.DoubleStealThirdBreaksPerGame:F3} |");
        sb.AppendLine(c, $"| 暴投・パスボール | {s.WildPitchesPerGame:F3} |");
        sb.AppendLine(c, $"| 盗塁成功率（参考） | {s.StealSuccessRate:P1}（試行 {s.TotalStealAttempts}） |");
        sb.AppendLine(c, $"| 犠打成功率（参考） | {s.SacrificeBuntSuccessRate:P1}（試行 {s.TotalSacrificeBunts}） |");
        sb.AppendLine();
        sb.AppendLine("## 球種別の空振り率・チェイス率（設計書15 Phase E-4）");
        sb.AppendLine();
        sb.AppendLine("- 空振り率 = 空振り / スイング（当てにいって空を切った割合）");
        sb.AppendLine("- チェイス率 = ゾーン外でのスイング / ゾーン外の球（釣られ率）");
        sb.AppendLine();
        sb.AppendLine("| 球種 | 空振り率 | チェイス率 | スイング数 | ゾーン外球数 |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (PitchType t in System.Enum.GetValues(typeof(PitchType)))
        {
            var swings = s.SwingsByPitchType[(int)t];
            if (swings == 0) continue; // 生成レパートリーに無い球種は出さない
            sb.AppendLine(c,
                $"| {PitchTypeName(t)} | {s.WhiffRateOf(t):P1} | {s.ChaseRateOf(t):P1} | {swings} | {s.OutOfZoneByPitchType[(int)t]} |");
        }
        sb.AppendLine();
        sb.AppendLine("## チーム得点分布（各試合の両チーム得点）");
        var total = s.Games * 2.0;
        for (var r = 0; r < 15; r++)
        {
            var pct = s.RunsPerTeamHistogram[r] / total;
            var bar = new string('#', (int)Math.Round(pct * 100));
            sb.AppendLine(c, $"- {r,2}点: {pct,6:P1} {bar}");
        }
        return sb.ToString();
    }

    /// <summary>球種の日本語表示名（用語集準拠）。</summary>
    private static string PitchTypeName(PitchType t) => t switch
    {
        PitchType.Fastball => "ストレート",
        PitchType.TwoSeam => "ツーシーム",
        PitchType.Cutter => "カットボール",
        PitchType.Slider => "スライダー",
        PitchType.Curve => "カーブ",
        PitchType.Fork => "フォーク",
        PitchType.Changeup => "チェンジアップ",
        PitchType.Shuuto => "シュート",
        PitchType.Sinker => "シンカー",
        _ => t.ToString(),
    };
}
