namespace KokoSim.Engine.Season;

/// <summary>
/// 能力別 trainability（伸ばしやすさ）係数（Issue #114, 設計書02 §5.1）。
/// 能力の「素質の形（内在特性）」を表し、経験値加算時に <see cref="DevelopingPlayer.GrowthMultiplier"/>
/// （分野別の伸びしろ・個体差）とは別に乗算する。どの学校に行っても変わらない＝環境倍率（監督指導力・
/// 施設, #115）とは直交する。既定は全て1.0で従来と1ビットも変わらない（不変条件#2）。
///
/// 方針（会話 2026-07-22 で確定）:
/// ・足(Speed) のみ素質固定＝フラット（&lt;1.0でほぼ伸びない）。
/// ・技術で決まる能力（守備・捕球・選球眼・制球・キレ・バント・送球精度・ミート）は相対的に伸びやすい（&gt;1.0）。
/// ・球速(Velocity) は 1.0 固定。「入学時130→3年で150」の伸びは環境倍率が担う（#115）。素質と混ぜない。
/// ・弾道(LaunchTendency) は型属性のため触らない（1.0）。
/// </summary>
public sealed record TrainabilityCoefficients
{
    public double Contact { get; init; } = 1.0;
    public double Power { get; init; } = 1.0;
    public double LaunchTendency { get; init; } = 1.0;
    public double Discipline { get; init; } = 1.0;
    public double Speed { get; init; } = 1.0;
    public double ArmStrength { get; init; } = 1.0;
    public double Fielding { get; init; } = 1.0;
    public double Catching { get; init; } = 1.0;
    public double Velocity { get; init; } = 1.0;
    public double Control { get; init; } = 1.0;
    public double Stamina { get; init; } = 1.0;
    public double PitchRank { get; init; } = 1.0;
    public double Bunt { get; init; } = 1.0;
    public double Steal { get; init; } = 1.0;
    public double Baserunning { get; init; } = 1.0;
    public double ThrowAccuracy { get; init; } = 1.0;

    /// <summary>能力に対応する trainability 係数（未定義の能力は1.0）。</summary>
    public double For(AbilityKind k) => k switch
    {
        AbilityKind.Contact => Contact,
        AbilityKind.Power => Power,
        AbilityKind.LaunchTendency => LaunchTendency,
        AbilityKind.Discipline => Discipline,
        AbilityKind.Speed => Speed,
        AbilityKind.ArmStrength => ArmStrength,
        AbilityKind.Fielding => Fielding,
        AbilityKind.Catching => Catching,
        AbilityKind.Velocity => Velocity,
        AbilityKind.Control => Control,
        AbilityKind.Stamina => Stamina,
        AbilityKind.PitchRank => PitchRank,
        AbilityKind.Bunt => Bunt,
        AbilityKind.Steal => Steal,
        AbilityKind.Baserunning => Baserunning,
        AbilityKind.ThrowAccuracy => ThrowAccuracy,
        _ => 1.0,
    };
}
