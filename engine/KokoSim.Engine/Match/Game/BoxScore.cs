using KokoSim.Engine.Match.Field;

namespace KokoSim.Engine.Match.Game;

/// <summary>
/// 個人打撃成績（1試合）。プレー記録から集計。HitByPitches は SourceId より後（互換のため末尾追加）。
/// Position は表示専用＝DHスロットは <see cref="FieldPosition.DesignatedHitter"/>（issue #70）。
/// </summary>
public sealed record BattingLine(
    int Order, FieldPosition Position, string Name,
    int PlateAppearances, int AtBats, int Hits, int Doubles, int Triples, int HomeRuns,
    int Rbi, int Walks, int StrikeOuts, int? SourceId = null, int HitByPitches = 0)
{
    /// <summary>打率（打数0なら0）。</summary>
    public double Average => AtBats > 0 ? (double)Hits / AtBats : 0.0;
}

/// <summary>個人投手成績（1試合）。SourceId＝育成選手ID（相手校の生成投手は null）。HitBatters＝与死球。</summary>
public sealed record PitchingLine(
    string Name, int Outs, int BattersFaced, int Hits, int Runs, int StrikeOuts, int Walks, int Pitches,
    int? SourceId = null, int HitBatters = 0)
{
    /// <summary>投球回（アウト数/3）。例: 7回1/3 → "7 1/3"。</summary>
    public string InningsText => (Outs / 3) + (Outs % 3 == 0 ? "" : " " + (Outs % 3) + "/3");
}
