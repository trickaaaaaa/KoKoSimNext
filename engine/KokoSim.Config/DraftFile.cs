using KokoSim.Engine.Career.Draft;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KokoSim.Config;

/// <summary>
/// data/draft.yaml（ドラフト係数）のローダ（設計書20, 不変条件#3=IOはこの層に隔離）。
/// C# 既定（<see cref="DraftCoefficients"/>）が Unity 実プレイの真値、YAML は sim/テスト調整用。
/// 未指定フィールドは既定値のまま（IgnoreUnmatchedProperties＋null合体）。
/// </summary>
public static class DraftLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static DraftCoefficients LoadFromFile(string path) => Parse(File.ReadAllText(path));

    public static DraftCoefficients Parse(string yaml)
    {
        var dto = Deserializer.Deserialize<FileDto>(yaml)
            ?? throw new InvalidDataException("draft.yaml が空です。");
        var d = dto.Draft ?? new DraftDto();
        var def = new DraftCoefficients();
        return new DraftCoefficients
        {
            AbilityWeight = d.AbilityWeight ?? def.AbilityWeight,
            PerformanceWeight = d.PerformanceWeight ?? def.PerformanceWeight,
            CeilingBlend = d.CeilingBlend ?? def.CeilingBlend,
            BatterOpsBase = d.BatterOpsBase ?? def.BatterOpsBase,
            BatterOpsScale = d.BatterOpsScale ?? def.BatterOpsScale,
            BatterHrPerGameScale = d.BatterHrPerGameScale ?? def.BatterHrPerGameScale,
            BatterMinPlateAppearances = d.BatterMinPlateAppearances ?? def.BatterMinPlateAppearances,
            PitcherEraBase = d.PitcherEraBase ?? def.PitcherEraBase,
            PitcherEraScale = d.PitcherEraScale ?? def.PitcherEraScale,
            PitcherK9Base = d.PitcherK9Base ?? def.PitcherK9Base,
            PitcherK9Scale = d.PitcherK9Scale ?? def.PitcherK9Scale,
            PitcherMinBattersFaced = d.PitcherMinBattersFaced ?? def.PitcherMinBattersFaced,
            FirstRoundThreshold = d.FirstRoundThreshold ?? def.FirstRoundThreshold,
            UpperRoundThreshold = d.UpperRoundThreshold ?? def.UpperRoundThreshold,
            MiddleRoundThreshold = d.MiddleRoundThreshold ?? def.MiddleRoundThreshold,
            CandidateThreshold = d.CandidateThreshold ?? def.CandidateThreshold,
            NominationMidpoint = d.NominationMidpoint ?? def.NominationMidpoint,
            NominationSpread = d.NominationSpread ?? def.NominationSpread,
        };
    }

    private sealed class FileDto
    {
        public DraftDto? Draft { get; set; }
    }

    private sealed class DraftDto
    {
        public double? AbilityWeight { get; set; }
        public double? PerformanceWeight { get; set; }
        public double? CeilingBlend { get; set; }
        public double? BatterOpsBase { get; set; }
        public double? BatterOpsScale { get; set; }
        public double? BatterHrPerGameScale { get; set; }
        public int? BatterMinPlateAppearances { get; set; }
        public double? PitcherEraBase { get; set; }
        public double? PitcherEraScale { get; set; }
        public double? PitcherK9Base { get; set; }
        public double? PitcherK9Scale { get; set; }
        public int? PitcherMinBattersFaced { get; set; }
        public double? FirstRoundThreshold { get; set; }
        public double? UpperRoundThreshold { get; set; }
        public double? MiddleRoundThreshold { get; set; }
        public double? CandidateThreshold { get; set; }
        public double? NominationMidpoint { get; set; }
        public double? NominationSpread { get; set; }
    }
}
