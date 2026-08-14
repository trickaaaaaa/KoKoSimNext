using System.Collections.Generic;
using System.IO;
using System.Linq;
using KokoSim.Engine.Nation;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// チームカラー（設計書18 §3.5.1）のパレット表・純パーサ・校ID割り当ての検証。
/// 表示専用のデータなので試合結果には触れない＝統計帯には影響しない。
/// </summary>
public sealed class TeamColorTests
{
    private static TeamColorPalette LoadReal()
    {
        var path = Engine.Tests.Balance.BalanceRegressionTests.FindDataFile("team-colors.yaml");
        var palette = TeamColorParser.Parse(File.ReadAllText(path));
        Assert.NotNull(palette);
        return palette!;
    }

    // ---- 実ファイル ----

    [Fact]
    public void RealFile_ParsesEveryEntry_WithUniqueIds()
    {
        var palette = LoadReal();

        // 表の規模: 4000校へ配るので、同じ配色が並びすぎない程度の件数を保つ。
        Assert.True(palette.Entries.Count >= 30, $"配色が {palette.Entries.Count} 件（30未満）");
        Assert.Equal(palette.Entries.Count, palette.Entries.Select(e => e.Id).Distinct().Count());

        foreach (var e in palette.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Label), $"{e.Id} に和名が無い");
            // 地色とチーム色が同じ＝全身1色は配色として成立しない（表の書き間違いを弾く）。
            Assert.NotEqual(e.Base, e.Team);
        }
    }

    [Fact]
    public void RealFile_KeepsWhiteBasesInTheMajority()
    {
        // 実物準拠（表の冒頭コメント）。地色を足しても「白＋チーム色1」が過半でないと
        // 大会表が色とりどりになって嘘くさくなる。
        var entries = LoadReal().Entries;
        var white = entries.Count(e => e.Base.ToHex() == "#EDEBE0");
        Assert.True(white * 2 >= entries.Count,
            $"白の地色が {white}/{entries.Count} 件（過半を割った）");

        // 地色は3階調では足りない（issue #620）。砂色・銀鼠を足して5階調にしてある。
        // ただし**身体の上で見分けが付く階調だけ**を数える意味の下限（実測で潰した経緯は表のコメント）。
        Assert.True(entries.Select(e => e.Base).Distinct().Count() >= 5, "地色の階調が5種未満");
    }

    [Fact]
    public void RealFile_KeepsCollisionsLow_AtPrefectureScale()
    {
        // issue #620: 表が薄いと同一県（約85校）の大会表で同じ配色が並ぶ。校IDは県ごとに連番なので、
        // 47県ぶんの窓（85校）で「何種類の配色が出るか」を数えて件数拡充の効果を固定する。
        // 実測: 36件のとき 最低28種・平均32.5／60件で 最低39種・平均45.9。
        var palette = LoadReal();
        const int schoolsInPrefecture = 85;
        var distinctPerPrefecture = new List<int>();
        for (var pref = 0; pref < 47; pref++)
        {
            var used = new HashSet<string>();
            for (var i = 0; i < schoolsInPrefecture; i++)
                used.Add(palette.EntryFor(pref * schoolsInPrefecture + i).Id);
            distinctPerPrefecture.Add(used.Count);
        }

        var worstPref = distinctPerPrefecture.Min();
        var average = distinctPerPrefecture.Average();
        Assert.True(worstPref >= 38, $"一番薄い県で85校が {worstPref} 種類しか着ていない");
        Assert.True(average >= 43.0, $"85校あたり平均 {average:0.0} 種類（表が薄い）");
    }

    [Fact]
    public void RealFile_HasStripedEntries_AndTheyCarryColorAndFreq()
    {
        var striped = LoadReal().Entries.Where(e => e.Stripe.On).ToList();
        Assert.NotEmpty(striped);
        foreach (var e in striped)
        {
            // 縞は「間隔2.5cm前後＝40本/m」が実物。桁を間違えると縞が消える/潰れるので範囲で守る。
            Assert.InRange(e.Stripe.FreqPerMeter, 10.0, 120.0);
            Assert.NotEqual(e.Base, e.Stripe.Color);
        }
    }

    // ---- パーサ ----

    [Fact]
    public void Parse_ReadsFlowMap_IncludingNestedStripe()
    {
        var palette = TeamColorParser.Parse(@"
palettes:
  - { id: a, label: テスト白紺, base: ""#FFFFFF"", team: ""#001133"" }
  - { id: b, label: テスト縞, base: ""#EEEEEE"", team: ""#220000"", visor: ""#123456"",
      stripe: { on: true, color: ""#220000"", freq: 36 } }
");
        Assert.NotNull(palette);
        Assert.Equal(2, palette!.Entries.Count);

        var a = palette.FindById("a")!;
        Assert.Equal(new RgbColor(0xFF, 0xFF, 0xFF), a.Base);
        Assert.Equal(new RgbColor(0x00, 0x11, 0x33), a.Team);
        Assert.Equal(a.Team, a.Visor);        // visor 省略時は team
        Assert.False(a.Stripe.On);

        var b = palette.FindById("b")!;
        Assert.Equal(new RgbColor(0x12, 0x34, 0x56), b.Visor);
        Assert.True(b.Stripe.On);             // 2行に跨ったフロー・マップが連結できている
        Assert.Equal(36.0, b.Stripe.FreqPerMeter);
    }

    [Fact]
    public void Parse_KeepsHexInsideQuotes_WhenStrippingComments()
    {
        // 行末コメントを落とす処理が引用符を見ないと、"#EDEBE0" が丸ごと消えて全件落ちる。
        var palette = TeamColorParser.Parse(@"
# 全行コメント
palettes:
  - { id: a, label: 白紺, base: ""#EDEBE0"", team: ""#293356"" }   # 行末コメント
");
        Assert.NotNull(palette);
        Assert.Equal(new RgbColor(0xED, 0xEB, 0xE0), palette!.FindById("a")!.Base);
    }

    [Fact]
    public void Parse_ReturnsNull_WhenNothingUsable()
    {
        Assert.Null(TeamColorParser.Parse("palettes:\n"));
        Assert.Null(TeamColorParser.Parse(""));
        // 色が壊れている行は捨てる（＝既定色で握りつぶさない）。
        Assert.Null(TeamColorParser.Parse(@"palettes:
  - { id: a, base: ""#ZZZZZZ"", team: ""#000000"" }"));
    }

    [Fact]
    public void TryParseHex_RejectsMalformed()
    {
        Assert.True(RgbColor.TryParseHex("#A1B2C3", out var c) && c.ToHex() == "#A1B2C3");
        Assert.True(RgbColor.TryParseHex("A1B2C3", out _));      // # は省略可
        Assert.False(RgbColor.TryParseHex("#ABC", out _));       // 3桁短縮は非対応
        Assert.False(RgbColor.TryParseHex("#GGGGGG", out _));
        Assert.False(RgbColor.TryParseHex(null, out _));
    }

    // ---- 校ID割り当て ----

    [Fact]
    public void EntryFor_IsStable_ForSameSchoolId()
    {
        var palette = LoadReal();
        // 「同シード同結果」ではなく「同IDなら常に同じ」＝乱数源を使わない純関数であること。
        for (var id = 0; id < 200; id++)
            Assert.Equal(palette.EntryFor(id).Id, palette.EntryFor(id).Id);
    }

    [Fact]
    public void EntryFor_SpreadsAcrossPalette_ForSequentialIds()
    {
        var palette = LoadReal();
        var used = new HashSet<string>();
        for (var id = 0; id < 4000; id++) used.Add(palette.EntryFor(id).Id);

        // 4000校を配って表の全件が使われる（＝一部の配色が死んでいない）。
        Assert.Equal(palette.Entries.Count, used.Count);

        // 連番IDで「隣の学校が必ず隣の配色」にならない（素の id % Count 回避の意図を守る）。
        var consecutiveShifts = new HashSet<int>();
        for (var id = 0; id < 200; id++)
        {
            var a = IndexOf(palette, palette.EntryFor(id).Id);
            var b = IndexOf(palette, palette.EntryFor(id + 1).Id);
            consecutiveShifts.Add(b - a);
        }
        Assert.True(consecutiveShifts.Count > 1, "連番IDの配色差が一定＝規則性が見えている");
    }

    [Fact]
    public void EntryFor_HandlesNegativeAndLargeIds()
    {
        var palette = LoadReal();
        // 添字計算が符号で壊れないこと（IndexOutOfRange を出さない）。
        Assert.NotNull(palette.EntryFor(-1));
        Assert.NotNull(palette.EntryFor(int.MinValue));
        Assert.NotNull(palette.EntryFor(int.MaxValue));
    }

    [Fact]
    public void Swatches_AreDistinct_AndCoverEveryColorInTheTable()
    {
        var palette = LoadReal();
        var sw = palette.Swatches;

        Assert.Equal(sw.Count, sw.Distinct().Count());
        // 表に出てくる色はすべて選べる（＝設定画面で「今の色が一覧に無い」が起きない）。
        foreach (var e in palette.Entries)
        {
            Assert.Contains(e.Base, sw);
            Assert.Contains(e.Team, sw);
            Assert.Contains(e.Visor, sw);
            if (e.Stripe.On) Assert.Contains(e.Stripe.Color, sw);
        }
        // 先頭は地色（明るい側）＝送り操作が明→暗の並びに近くなる意図を守る。
        Assert.Equal(palette.Entries[0].Base, sw[0]);
    }

    [Fact]
    public void RealFile_NamesEveryColorUsedInTheTable()
    {
        var palette = LoadReal();
        // 設定画面は色名で選ばせるので、表に出る色に和名が無いと16進が露出する。
        foreach (var c in palette.Swatches)
            Assert.False(palette.NameOf(c).StartsWith("#"),
                $"{c.ToHex()} に color_names の和名が無い");
    }

    [Fact]
    public void NameOf_FallsBackToHex_WhenUndefined()
        => Assert.Equal("#010203", LoadReal().NameOf(new RgbColor(1, 2, 3)));

    [Fact]
    public void FromEntry_DerivesSixRegions()
    {
        var palette = LoadReal();
        var e = palette.Entries[0];
        var c = TeamColors.FromEntry(e);

        Assert.Equal(e.Base, c.Shirt);
        Assert.Equal(e.Base, c.Pants);
        Assert.Equal(e.Team, c.Undershirt);
        Assert.Equal(e.Team, c.Socks);
        Assert.Equal(e.Team, c.CapCrown);
        Assert.Equal(e.Visor, c.CapVisor);
        Assert.Equal(e.Stripe, c.Stripe);
    }

    private static int IndexOf(TeamColorPalette palette, string id)
    {
        for (var i = 0; i < palette.Entries.Count; i++)
            if (palette.Entries[i].Id == id) return i;
        return -1;
    }
}
