using System.Collections.Generic;
using KokoSim.Engine.Match.Broadcast;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Broadcast;

/// <summary>
/// 場内放送（issue #919）の中身の組み立て。UIは組んだ部品を鳴らすだけ。
/// </summary>
public sealed class PaAnnouncementTests
{
    // ── 場内放送 ──

    [Fact]
    public void Announcement_ReadsOrderPositionAndSurname()
    {
        var clips = PaAnnouncement.Clips(4, FieldPosition.Catcher, "田中　一郎", pinchHitter: false);
        Assert.Equal(new[] { "order_4", "pos_c", PaAnnouncement.SurnameClip("田中") }, clips);
        Assert.Equal("4番、キャッチャー、田中くん。", PaAnnouncement.Text(4, FieldPosition.Catcher, "田中　一郎", false));
    }

    [Fact]
    public void Announcement_PinchHitterIsIntroducedAsBatter()
    {
        var clips = PaAnnouncement.Clips(4, FieldPosition.Catcher, "鈴木　次郎", pinchHitter: true);
        Assert.Equal(new[] { PaAnnouncement.BatterClip, PaAnnouncement.SurnameClip("鈴木") }, clips);
        Assert.Equal("バッター、鈴木くん。", PaAnnouncement.Text(4, FieldPosition.Catcher, "鈴木　次郎", true));
    }

    [Fact]
    public void Announcement_DesignatedHitterSlotSaysShimeiDasha()
    {
        Assert.Equal("pos_dh", PaAnnouncement.PositionClip(FieldPosition.DesignatedHitter));
        Assert.Equal("9番、指名打者、佐藤くん。", PaAnnouncement.Text(9, FieldPosition.DesignatedHitter, "佐藤　翔", false));
    }

    [Fact]
    public void Announcement_EveryPositionHasItsOwnClip()
    {
        var seen = new HashSet<string>();
        foreach (FieldPosition p in System.Enum.GetValues(typeof(FieldPosition)))
            Assert.True(seen.Add(PaAnnouncement.PositionClip(p)), p.ToString());
    }

    [Fact]
    public void Announcement_EmptyNameOrBadOrderSaysNothing()
    {
        Assert.Empty(PaAnnouncement.Clips(1, FieldPosition.Pitcher, "", false));
        Assert.Empty(PaAnnouncement.Clips(0, FieldPosition.Pitcher, "田中　一郎", false));
    }

    [Fact]
    public void SurnameClip_UsesFnv1aOfUtf8_SameAsTheGeneratorScript()
    {
        // 既知値（FNV-1a 32bit の公開テストベクタ）。tools/voicevox-announcements.py と同じ規則。
        Assert.Equal(0x811c9dc5u, PaAnnouncement.Fnv1a32(""));
        Assert.Equal(0xe40c292cu, PaAnnouncement.Fnv1a32("a"));
        Assert.Equal("sei_" + PaAnnouncement.Fnv1a32("田中").ToString("x8"), PaAnnouncement.SurnameClip("田中"));
        Assert.NotEqual(PaAnnouncement.SurnameClip("田中"), PaAnnouncement.SurnameClip("中田"));
    }

    [Fact]
    public void Key_ChangesWhenTheBatterIsReplaced()
    {
        var a = PaAnnouncement.Key(3, true, 4, 20, 101, "田中　一郎");
        var b = PaAnnouncement.Key(3, true, 4, 20, 202, "鈴木　次郎");
        Assert.NotEqual(a, b);
        Assert.Equal(a, PaAnnouncement.Key(3, true, 4, 20, 101, "田中　一郎"));
    }

    [Theory]
    [InlineData("田中　一郎", "田中")]
    [InlineData("代打 一郎", "代打")]
    [InlineData("佐々木", "佐々木")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Surname_SplitsAtEitherSpace(string? full, string expected)
        => Assert.Equal(expected, PlayerNameGenerator.Surname(full));
}
