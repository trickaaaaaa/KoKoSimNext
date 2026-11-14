using System.Collections.Generic;
using KokoSim.Engine.Match.Broadcast;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Broadcast;

/// <summary>
/// 交代テロップ（issue #920）の中身の組み立て。UIは組んだものを並べるだけ。
/// </summary>
public sealed class SubstitutionTelopTests
{
    private static SubstitutionPlayer Pl(string name, int number = 0, int? id = null) => new(id, number, name);

    private static SubstitutionEvent Ev(bool away, params SubstitutionEntry[] entries)
        => new(5, true, away, entries);

    // ── 交代テロップ ──

    [Fact]
    public void Telop_PinchHitShowsOrderButNoPosition()
    {
        var card = Assert.Single(SubstitutionTelop.Build(new[]
        {
            Ev(true, new SubstitutionEntry(SubstitutionEntryKind.PinchHit, 4, Pl("代打　太郎", 14), Pl("山田　一郎"), FieldPosition.Catcher)),
        }));
        Assert.True(card.TeamIsAway);
        var row = Assert.Single(card.Rows);
        Assert.Equal("代打", row.Kind);
        Assert.Equal("4番", row.Order);
        Assert.Equal(14, row.Number);
        Assert.Equal("代打　太郎", row.Name);
        Assert.Equal("", row.Position);
        Assert.Equal("山田　一郎", row.OutName);
    }

    [Fact]
    public void Telop_SameMomentIsOneCardPerTeam_InOrderOfAppearance()
    {
        var cards = SubstitutionTelop.Build(new[]
        {
            Ev(false, new SubstitutionEntry(SubstitutionEntryKind.Relief, 0, Pl("二番手"), Pl("先発"), FieldPosition.Pitcher)),
            Ev(true, new SubstitutionEntry(SubstitutionEntryKind.PinchHit, 1, Pl("代打"), Pl("一番"), FieldPosition.Shortstop)),
            Ev(false, new SubstitutionEntry(SubstitutionEntryKind.DefensiveSub, 3, Pl("守備固め"), Pl("三番"), FieldPosition.LeftField)),
        });
        Assert.Equal(2, cards.Count);
        Assert.False(cards[0].TeamIsAway);
        Assert.Equal(new[] { "二番手", "守備固め" }, new[] { cards[0].Rows[0].Name, cards[0].Rows[1].Name });
        Assert.Equal("", cards[0].Rows[0].Order);   // DH制の投手は打順外
        Assert.Equal("ピッチャー", cards[0].Rows[0].Position);
        Assert.Equal("レフト", cards[0].Rows[1].Position);
        Assert.True(cards[1].TeamIsAway);
    }

    [Fact]
    public void Telop_PlayerWhoMovedAgainKeepsHowHeCameIn()
    {
        var card = Assert.Single(SubstitutionTelop.Build(new[]
        {
            Ev(false, new SubstitutionEntry(SubstitutionEntryKind.DefensiveSub, 6, Pl("控え", 13), Pl("遊撃手"), FieldPosition.Shortstop)),
            Ev(false, new SubstitutionEntry(SubstitutionEntryKind.PositionChange, 6, Pl("控え", 13), null, FieldPosition.SecondBase)),
        }));
        var row = Assert.Single(card.Rows);
        Assert.Equal("守備交代", row.Kind);
        Assert.Equal("セカンド", row.Position);
        Assert.Equal("遊撃手", row.OutName);
    }

    [Fact]
    public void HistoryLine_JoinsEntries()
    {
        var e = Ev(true,
            new SubstitutionEntry(SubstitutionEntryKind.PinchRun, 2, Pl("代走"), Pl("二番"), FieldPosition.SecondBase),
            new SubstitutionEntry(SubstitutionEntryKind.PositionChange, 3, Pl("三番"), null, FieldPosition.FirstBase));
        Assert.Equal("5回表 代走 代走 ← 二番／守備位置変更 三番（ファースト）", SubstitutionTelop.HistoryLine(e));
    }
}
