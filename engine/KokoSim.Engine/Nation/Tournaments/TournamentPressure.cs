using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 大会の試合が背負うプレッシャーの加点（設計書02 §3.1・issue #1507）。
/// 段階＝予選系（県大会・地区予選・秋季地区大会）+1 / 全国大会（甲子園・センバツ・神宮）+2 / 決勝（大会を問わず最終戦）+3。
/// 「負けたら引退」＝3年生の最後の夏（夏の県大会・夏の甲子園）。どちらも試合の両校に同じ値（GameContext は両校共通）。
/// 練習試合（大会に紐付かない一戦）は加点なし＝<see cref="GameContext"/> の既定のまま。
/// </summary>
public static class TournamentPressure
{
    public const int QualifierBonus = 1;
    public const int NationalBonus = 2;
    public const int FinalBonus = 3;

    /// <param name="roundsRemaining">この試合を含む残りラウンド数（決勝=1）。1以外は決勝ではない。</param>
    public static int StageBonus(TournamentKind kind, int roundsRemaining)
    {
        if (roundsRemaining == 1) return FinalBonus;
        return kind switch
        {
            TournamentKind.SummerKoshien or TournamentKind.SenbatsuKoshien or TournamentKind.Jingu => NationalBonus,
            _ => QualifierBonus,
        };
    }

    public static bool RetirementOnLine(TournamentKind kind)
        => kind is TournamentKind.Summer or TournamentKind.SummerKoshien;

    /// <param name="match">大会の1試合ぶんの文脈。null＝練習試合（大会に紐付かない一戦）で、加点せず
    /// <paramref name="ctx"/> をそのまま返す。<paramref name="kind"/> は大会外でも直前の大会の値が残るので、
    /// 大会の試合かどうかは必ずこちらで判定する（PR #1555 のレビュー指摘）。</param>
    public static GameContext Apply(GameContext ctx, TournamentKind kind, TournamentMatchContext? match)
        => match is null
            ? ctx
            : ctx with
            {
                PressureStageBonus = StageBonus(kind, match.RoundsRemaining),
                RetirementOnLine = RetirementOnLine(kind),
            };
}
