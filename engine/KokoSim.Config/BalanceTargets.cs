using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KokoSim.Config;

/// <summary>統計回帰の許容帯（1指標）。</summary>
public sealed record Band(double Min, double Max)
{
    public bool Contains(double v) => v >= Min && v <= Max;
}

/// <summary>data/balance-targets.yaml の Phase 1（1万打席）許容帯。</summary>
public sealed record BalanceTargets
{
    public required Band BattingAverage { get; init; }
    public required Band StrikeoutRate { get; init; }
    public required Band WalkRate { get; init; }
    public required Band HomeRunRate { get; init; }
}

/// <summary>Phase 2（試合）の許容帯。</summary>
public sealed record GameTargets
{
    public required Band RunsPerTeam { get; init; }
    public required Band PitchesPerGame { get; init; }
    public required Band MinutesPerGame { get; init; }
    public required Band InningsPerGame { get; init; }
    /// <summary>本塁クロスプレー憤死/試合の参考帯（設計書12 §3 F2, Q9）。広め＝得点帯と競合させない warn 相当。</summary>
    public Band HomePlayOutsPerGame { get; init; } = new(0.12, 0.42);

    // ===== design-14 第1段（P1）新プレー発生率/試合（両軍計）。采配Brain不要＝無指示でも発生する常時系 =====
    public Band FieldersChoicePerGame { get; init; } = new(0.10, 1.20);
    public Band DroppedThirdStrikePerGame { get; init; } = new(0.02, 0.40);
    public Band ErrorExtraAdvancePerGame { get; init; } = new(0.02, 0.40);
    /// <summary>暴投・パスボール/試合の参考帯（design-14 P2-8, 設計書15 Phase D-3）。</summary>
    public Band WildPitchPerGame { get; init; } = new(0.15, 1.00);
}

/// <summary>design-14 第1段（P1）のうち采配Brain（KokoSim.Balance の GameSimulation --tactics）が
/// 盗塁/敬遠を選ばない限り発生しないプレーの許容帯。games_10k_tactics セクション。</summary>
public sealed record GameTacticsTargets
{
    public required Band RunsPerTeam { get; init; }
    public Band PickoffPerGame { get; init; } = new(0.0, 0.05);
    public Band IntentionalWalkPerGame { get; init; } = new(0.0, 0.30);
    public Band DoubleStealThirdBreakPerGame { get; init; } = new(0.0, 0.05);
    // 1球采配（設計書15 Phase C-2）: 追い込まれ矯正/3-0待て/決め球切替の影響を最も受ける指標。
    public Band StrikeoutRate { get; init; } = new(0.16, 0.22);
    public Band WalkRate { get; init; } = new(0.06, 0.10);
    public Band HomeRunRate { get; init; } = new(0.020, 0.036);
}

public static class BalanceTargetsLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static BalanceTargets LoadFromFile(string path) => Parse(File.ReadAllText(path));

    public static BalanceTargets Parse(string yaml)
    {
        var a = Root(yaml).Atbat10k ?? throw new InvalidDataException("atbat_10k セクションがありません。");
        return new BalanceTargets
        {
            BattingAverage = a.BattingAverage!.ToBand(),
            StrikeoutRate = a.StrikeoutRate!.ToBand(),
            WalkRate = a.WalkRate!.ToBand(),
            HomeRunRate = a.HomeRunRate!.ToBand(),
        };
    }

    public static GameTargets LoadGameFromFile(string path) => ParseGame(File.ReadAllText(path));

    public static GameTargets ParseGame(string yaml)
    {
        var g = Root(yaml).Games10k ?? throw new InvalidDataException("games_10k セクションがありません。");
        return new GameTargets
        {
            RunsPerTeam = g.RunsPerTeam!.ToBand(),
            PitchesPerGame = g.PitchesPerGame!.ToBand(),
            MinutesPerGame = g.MinutesPerGame!.ToBand(),
            InningsPerGame = g.InningsPerGame!.ToBand(),
            HomePlayOutsPerGame = g.HomePlayOutsPerGame?.ToBand() ?? new Band(0.12, 0.42),
            FieldersChoicePerGame = g.FieldersChoicePerGame?.ToBand() ?? new Band(0.10, 1.20),
            DroppedThirdStrikePerGame = g.DroppedThirdStrikePerGame?.ToBand() ?? new Band(0.02, 0.40),
            ErrorExtraAdvancePerGame = g.ErrorExtraAdvancePerGame?.ToBand() ?? new Band(0.02, 0.40),
            WildPitchPerGame = g.WildPitchPerGame?.ToBand() ?? new Band(0.15, 1.00),
        };
    }

    public static GameTacticsTargets LoadGameTacticsFromFile(string path) => ParseGameTactics(File.ReadAllText(path));

    public static GameTacticsTargets ParseGameTactics(string yaml)
    {
        var g = Root(yaml).Games10kTactics
            ?? throw new InvalidDataException("games_10k_tactics セクションがありません。");
        return new GameTacticsTargets
        {
            RunsPerTeam = g.RunsPerTeam!.ToBand(),
            PickoffPerGame = g.PickoffPerGame?.ToBand() ?? new Band(0.0, 0.05),
            IntentionalWalkPerGame = g.IntentionalWalkPerGame?.ToBand() ?? new Band(0.0, 0.30),
            DoubleStealThirdBreakPerGame = g.DoubleStealThirdBreakPerGame?.ToBand() ?? new Band(0.0, 0.05),
            StrikeoutRate = g.StrikeoutRate?.ToBand() ?? new Band(0.16, 0.22),
            WalkRate = g.WalkRate?.ToBand() ?? new Band(0.06, 0.10),
            HomeRunRate = g.HomeRunRate?.ToBand() ?? new Band(0.020, 0.036),
        };
    }

    private static RootDto Root(string yaml)
        => Deserializer.Deserialize<RootDto>(yaml)
            ?? throw new InvalidDataException("balance-targets.yaml が空です。");

    private sealed class RootDto
    {
        [YamlMember(Alias = "atbat_10k")]
        public AtBatDto? Atbat10k { get; set; }

        [YamlMember(Alias = "games_10k")]
        public GamesDto? Games10k { get; set; }

        [YamlMember(Alias = "games_10k_tactics")]
        public GamesTacticsDto? Games10kTactics { get; set; }
    }

    private sealed class AtBatDto
    {
        public BandDto? BattingAverage { get; set; }
        public BandDto? StrikeoutRate { get; set; }
        public BandDto? WalkRate { get; set; }
        public BandDto? HomeRunRate { get; set; }
    }

    private sealed class GamesDto
    {
        public BandDto? RunsPerTeam { get; set; }
        public BandDto? PitchesPerGame { get; set; }
        public BandDto? MinutesPerGame { get; set; }
        public BandDto? InningsPerGame { get; set; }
        public BandDto? HomePlayOutsPerGame { get; set; }
        public BandDto? FieldersChoicePerGame { get; set; }
        public BandDto? DroppedThirdStrikePerGame { get; set; }
        public BandDto? ErrorExtraAdvancePerGame { get; set; }
        public BandDto? WildPitchPerGame { get; set; }
    }

    private sealed class GamesTacticsDto
    {
        public BandDto? RunsPerTeam { get; set; }
        public BandDto? PickoffPerGame { get; set; }
        public BandDto? IntentionalWalkPerGame { get; set; }
        public BandDto? DoubleStealThirdBreakPerGame { get; set; }
        public BandDto? StrikeoutRate { get; set; }
        public BandDto? WalkRate { get; set; }
        public BandDto? HomeRunRate { get; set; }
    }

    private sealed class BandDto
    {
        public double Min { get; set; }
        public double Max { get; set; }
        public Band ToBand() => new(Min, Max);
    }
}
