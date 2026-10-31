using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>観客の入り（issue #705・表示専用）。</summary>
public class CrowdTurnoutTests
{
    [Theory]
    [InlineData(null, null, CrowdTurnout.Sparse)]                          // 練習試合
    [InlineData(TournamentKind.Summer, 6, CrowdTurnout.Sparse)]            // 夏の県大会・序盤
    [InlineData(TournamentKind.Summer, 4, CrowdTurnout.Sparse)]
    [InlineData(TournamentKind.Summer, 3, CrowdTurnout.Half)]              // 準々決勝
    [InlineData(TournamentKind.Summer, 2, CrowdTurnout.Half)]              // 準決勝
    [InlineData(TournamentKind.Summer, 1, CrowdTurnout.Full)]              // 決勝
    [InlineData(TournamentKind.Autumn, 3, CrowdTurnout.Sparse)]
    [InlineData(TournamentKind.Autumn, 2, CrowdTurnout.Half)]
    [InlineData(TournamentKind.Autumn, 1, CrowdTurnout.Half)]
    [InlineData(TournamentKind.AutumnDistrict, 1, CrowdTurnout.Sparse)]
    [InlineData(TournamentKind.Regional, 4, CrowdTurnout.Half)]
    [InlineData(TournamentKind.Jingu, 1, CrowdTurnout.Half)]
    [InlineData(TournamentKind.SummerKoshien, 6, CrowdTurnout.Full)]       // 甲子園は初戦から満員
    [InlineData(TournamentKind.SenbatsuKoshien, 5, CrowdTurnout.Full)]
    [InlineData(TournamentKind.Summer, null, CrowdTurnout.Sparse)]         // 回戦不明は序盤扱い
    public void For_FollowsTable(TournamentKind? kind, int? roundsRemaining, CrowdTurnout expected)
        => Assert.Equal(expected, CrowdTurnouts.For(kind, roundsRemaining));
}
