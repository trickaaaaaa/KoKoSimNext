namespace KokoSim.Engine.Stats;

/// <summary>1選手ぶんの累積成績（打撃＋投手）。帰属キーは育成選手ID（SourceId）。</summary>
public sealed class PlayerStats
{
    public int SourceId { get; }
    public BattingStatLine Batting { get; } = new();
    public PitchingStatLine Pitching { get; } = new();

    public PlayerStats(int sourceId) => SourceId = sourceId;
}
