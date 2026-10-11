using System.Linq;
using KokoSim.Engine.Nation.Tournaments;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 新聞型トーナメント表（issue #645）。校名は1回戦の端にだけ置き、勝ち上がりは線の太さで表す。
/// 6校×8枠（シード2校＝不戦勝）の手組みブラケットで、端・線・スコア・優勝欄の座標規則を固定する。
/// </summary>
public sealed class NewspaperBracketLayoutTests
{
    private static readonly NewspaperBracketMetrics M = new(NameWidth: 100, RoundWidth: 40, RowPitch: 20, HeaderHeight: 30, ChampionGap: 10);

    private static BracketSlot S(string? name, bool win = false, int? score = null, bool mgr = false)
        => new(name, mgr, win, score);

    private static BracketCard C(int r, int i, BracketSlot top, BracketSlot bottom, bool played = false, bool bye = false, bool mercy = false)
        => new(r, i, "R" + r, top, bottom, bye, played, mercy);

    /// <summary>
    /// 1回戦: [A(自校) 5-2 B] [C 不戦勝] [D 未消化 E] [F 不戦勝]
    /// 準決勝: [A 3-1 C] [未定 vs F]  決勝: [A vs 未定]
    /// </summary>
    private static TournamentBracketView MidTournament(string? champion = null) => new("テスト大会", [], champion, false, false,
    [
        new BracketRound(0, "1回戦",
        [
            C(0, 0, S("A", true, 5, mgr: true), S("B", false, 2), played: true),
            C(0, 1, S("C"), S(null), bye: true),
            C(0, 2, S("D"), S("E")),
            C(0, 3, S(null), S("F"), bye: true),
        ]),
        new BracketRound(1, "準決勝",
        [
            C(1, 0, S("A", true, 3, mgr: true), S("C", false, 1), played: true, mercy: true),
            C(1, 1, S(null), S("F")),
        ]),
        new BracketRound(2, "決勝", [C(2, 0, S("A", mgr: true), S(null))]),
    ]);

    [Fact]
    public void Leaves_AreOnlyFirstRoundTeams_SplitLeftRight_WithoutEmptySlots()
    {
        var b = NewspaperBracketLayout.Build(MidTournament(), M);

        Assert.Equal(new[] { "A", "B", "C", "D", "E", "F" }, b.Leaves.Select(l => l.Name));
        Assert.Equal(new[] { "A", "B", "C" }, b.Leaves.Where(l => l.Side == BracketSide.Left).Select(l => l.Name));
        // 空き枠は行を取らない＝右ブロックは D,E,F の3行で詰まる。
        Assert.Equal(new[] { 40.0, 60.0, 80.0 }, b.Leaves.Where(l => l.Side == BracketSide.Right).Select(l => l.LineStart.Y));
        // 左は校名欄の右端から、右は左右反転して線が始まる。
        Assert.All(b.Leaves.Where(l => l.Side == BracketSide.Left), l => Assert.Equal(100, l.LineStart.X));
        Assert.All(b.Leaves.Where(l => l.Side == BracketSide.Right), l => Assert.Equal(b.Width - 100, l.LineStart.X));
        Assert.True(b.Leaves.Single(l => l.Name == "B").IsEliminated);
        Assert.True(b.Leaves.Single(l => l.Name == "C").IsEliminated);
        Assert.False(b.Leaves.Single(l => l.Name == "A").IsEliminated);
    }

    [Fact]
    public void Paths_WinnerThick_LoserThin_ByeFollowsNextResult_ManagerFlagged()
    {
        var b = NewspaperBracketLayout.Build(MidTournament(), M);

        // 1回戦 A-B: A の線は勝ち（太）、B は負け（細）。合流点は2行の中点。
        var aFirst = b.Paths.First(p => p.Points[0] == new BracketPoint(100, 40));
        Assert.Equal(BracketLineState.Won, aFirst.State);
        Assert.True(aFirst.IsManager);
        Assert.Equal(new BracketPoint(140, 50), aFirst.Points[^1]);
        Assert.Equal(BracketLineState.Lost, b.Paths.First(p => p.Points[0] == new BracketPoint(100, 60)).State);

        // シード C は不戦勝の線を次段まで伸ばす。準決勝で負けたので太線にしない。
        var cBye = b.Paths.First(p => p.Points[0] == new BracketPoint(100, 80));
        Assert.Equal(2, cBye.Points.Count);
        Assert.Equal(BracketLineState.Pending, cBye.State);

        // 未消化の D-E は両方とも細線（未消化）。
        Assert.Equal(BracketLineState.Pending, b.Paths.First(p => p.Points[0] == new BracketPoint(b.Width - 100, 40)).State);
    }

    [Fact]
    public void Scores_SitBesideJoint_OnInputLine_MercyOnWinnerOnly()
    {
        var b = NewspaperBracketLayout.Build(MidTournament(), M);

        var five = b.Scores.Single(s => s.Text == "5");
        Assert.Equal(new BracketPoint(140 + NewspaperBracketLayout.ScoreGap, 40), five.Anchor);   // 合流点(140)の決勝側・A の線の高さ
        Assert.True(five.IsWinner && five.IsManager && !five.Mercy);
        Assert.False(b.Scores.Single(s => s.Text == "2").IsWinner);

        var three = b.Scores.Single(s => s.Text == "3");
        Assert.True(three.Mercy);
        Assert.False(b.Scores.Single(s => s.Text == "1").Mercy);
        Assert.Equal(4, b.Scores.Count);   // 未消化カードはスコアを持たない
    }

    [Fact]
    public void Champion_HiddenUntilDecided_FocusOnManagersLatestCard()
    {
        var mid = NewspaperBracketLayout.Build(MidTournament(), M);
        Assert.Null(mid.ChampionName);
        Assert.Equal(BracketLineState.Pending, mid.Paths[^1].State);
        // 自校の最新カード＝決勝（中央の合流点）。
        Assert.Equal(mid.Width / 2, mid.ManagerFocus!.Value.X);

        var done = NewspaperBracketLayout.Build(MidTournament(champion: "A"), M);
        Assert.Equal("A", done.ChampionName);
        Assert.Equal(BracketLineState.Won, done.Paths[^1].State);
        Assert.Equal(done.Width / 2, done.ChampionAnchor.X);
    }

    [Fact]
    public void Heads_MirrorEachRound_FinalCentered()
    {
        var b = NewspaperBracketLayout.Build(MidTournament(), M);
        Assert.Equal(new[] { "1回戦", "1回戦", "準決勝", "準決勝", "決勝" }, b.Heads.Select(h => h.Name));
        Assert.Equal(b.Width - b.Heads[0].X, b.Heads[1].X, 9);
        Assert.Equal(b.Width / 2, b.Heads[^1].X);
    }

    [Fact]
    public void ChampionLabel_NeverOverlapsHeader_InTinyBracket()
    {
        var view = new TournamentBracketView("2校", [], null, false, false,
            [new BracketRound(0, "決勝", [C(0, 0, S("X"), S("Y"))])]);
        var b = NewspaperBracketLayout.Build(view, M);
        Assert.Equal(2, b.Leaves.Count);
        Assert.True(b.ChampionAnchor.Y >= M.HeaderHeight + M.RowPitch);
    }

    [Fact]
    public void EmptyView_ReturnsEmpty()
    {
        Assert.Same(NewspaperBracket.Empty, NewspaperBracketLayout.Build(null));
    }
}
