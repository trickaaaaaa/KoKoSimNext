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
        // issue #620/#621: 表が薄いと同一県（約85校）の大会表で同じ配色が並ぶ。校IDは県ごとに連番なので、
        // 47県ぶんの窓（85校）で「何種類の<b>見た目</b>が出るか」を数える。数えるのは表の1件ではなく
        // 軸を重ねた確定配色＝プレイヤーが実際に見分けるもの。
        // 実測: 表36件・軸なしで 最低28種・平均32.5／表60件・軸なしで 最低39種・平均45.9。
        var palette = LoadReal();
        const int schoolsInPrefecture = 85;
        var distinctPerPrefecture = new List<int>();
        for (var pref = 0; pref < 47; pref++)
        {
            var used = new HashSet<TeamColors>();
            for (var i = 0; i < schoolsInPrefecture; i++)
                used.Add(palette.ColorsFor(pref * schoolsInPrefecture + i));
            distinctPerPrefecture.Add(used.Count);
        }

        var worstPref = distinctPerPrefecture.Min();
        var average = distinctPerPrefecture.Average();
        Assert.True(worstPref >= 55, $"一番薄い県で85校が {worstPref} 種類しか着ていない");
        Assert.True(average >= 60.0, $"85校あたり平均 {average:0.0} 種類（見た目の幅が足りない）");
    }

    [Fact]
    public void RealFile_StripeStylesCarrySaneColorAndFreq()
    {
        // 縞の型を明示している件だけの検査（型を持たない件は「チーム色・40本/m」で出る・issue #621）。
        var styled = LoadReal().Entries.Where(e => e.Stripe != null).ToList();
        Assert.NotEmpty(styled);
        foreach (var e in styled)
        {
            // 縞は「間隔2.5cm前後＝40本/m」が実物。桁を間違えると縞が消える/潰れるので範囲で守る。
            Assert.InRange(e.Stripe!.FreqPerMeter, 10.0, 120.0);
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
      stripe: { color: ""#220000"", freq: 36 } }
");
        Assert.NotNull(palette);
        Assert.Equal(2, palette!.Entries.Count);

        var a = palette.FindById("a")!;
        Assert.Equal(new RgbColor(0xFF, 0xFF, 0xFF), a.Base);
        Assert.Equal(new RgbColor(0x00, 0x11, 0x33), a.Team);
        Assert.Equal(a.Team, a.Visor);        // visor 省略時は team
        Assert.Null(a.Stripe);                // 型を書かない件は既定（チーム色・40本/m）

        var b = palette.FindById("b")!;
        Assert.Equal(new RgbColor(0x12, 0x34, 0x56), b.Visor);
        Assert.NotNull(b.Stripe);             // 2行に跨ったフロー・マップが連結できている
        Assert.Equal(36.0, b.Stripe!.FreqPerMeter);
    }

    [Fact]
    public void Parse_ReadsAxes_AndFallsBackToDefaults()
    {
        var palette = TeamColorParser.Parse(@"
axes:
  - { stripe: 0.10, visor: 0.40, white_under: 0.05 }
palettes:
  - { id: a, label: テスト, base: ""#FFFFFF"", team: ""#001133"" }
");
        Assert.NotNull(palette);
        Assert.Equal(0.10, palette!.Axes.StripeRate);
        Assert.Equal(0.40, palette.Axes.VisorRate);
        Assert.Equal(0.05, palette.Axes.WhiteUnderRate);

        // 軸を書かない表でも壊れない（既定で走る）。
        var noAxes = TeamColorParser.Parse(@"
palettes:
  - { id: a, label: テスト, base: ""#FFFFFF"", team: ""#001133"" }
");
        Assert.Equal(TeamColorAxes.Default, noAxes!.Axes);
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

    // ---- 配色の軸（issue #621） ----

    /// <summary>
    /// 軸の検査用の合成表。<b>つばを明示しない</b>件だけを並べる＝つばの軸がそのまま観測できる。
    /// </summary>
    private static TeamColorPalette Synthetic(int count, TeamColorAxes? axes = null)
    {
        var entries = new List<TeamColorEntry>();
        for (var i = 0; i < count; i++)
        {
            var team = new RgbColor((byte)(0x20 + i * 3), (byte)(0x30 + i), (byte)(0x50 + i * 2));
            entries.Add(new TeamColorEntry("e" + i, "配色" + i, new RgbColor(0xED, 0xEB, 0xE0), team, team));
        }
        return new TeamColorPalette(entries, null, axes);
    }

    [Fact]
    public void ColorsFor_IsStable_ForSameSchoolId()
    {
        var palette = LoadReal();
        // 軸も乱数源を使わない純関数＝同IDなら何度引いても同じ（セーブに持たない前提の根拠）。
        for (var id = 0; id < 300; id++)
            Assert.Equal(palette.ColorsFor(id), palette.ColorsFor(id));
    }

    [Fact]
    public void Axes_AppearAtTheIntendedRates()
    {
        // 実物準拠で偏らせた出現率（縦縞2割・白つば3割・白アンダー2割）を数で守る。
        // 等分にすると派手な型が並んで嘘くさくなる＝この比率が仕様。
        var axes = new TeamColorAxes(StripeRate: 0.20, VisorRate: 0.30, WhiteUnderRate: 0.20);
        var palette = Synthetic(37, axes);   // 件数は軸と無関係＝素数でも同じ結果になること
        const int schools = 4000;
        int stripe = 0, visor = 0, under = 0;
        for (var id = 0; id < schools; id++)
        {
            var c = palette.ColorsFor(id);
            if (c.Stripe.On) stripe++;
            if (c.CapVisor.Equals(c.Shirt)) visor++;
            if (c.Undershirt.Equals(c.Shirt)) under++;
        }

        Assert.InRange(stripe / (double)schools, 0.17, 0.23);
        Assert.InRange(visor / (double)schools, 0.27, 0.33);
        Assert.InRange(under / (double)schools, 0.17, 0.23);
    }

    [Fact]
    public void Axes_AreIndependentOfEachOther()
    {
        // 軸ごとに別の種を混ぜる意図（同じ校で全部いっぺんに立つ／立たないが起きない）。
        var palette = Synthetic(37);
        int both = 0, stripeOnly = 0, underOnly = 0;
        for (var id = 0; id < 4000; id++)
        {
            var c = palette.ColorsFor(id);
            var s = c.Stripe.On;
            var u = c.Undershirt.Equals(c.Shirt);
            if (s && u) both++;
            else if (s) stripeOnly++;
            else if (u) underOnly++;
        }
        Assert.True(both > 0 && stripeOnly > 0 && underOnly > 0, "軸が連動している");
        // 独立なら同時に立つのは 0.20×0.20＝4% 前後。
        Assert.InRange(both / 4000.0, 0.02, 0.06);
    }

    [Fact]
    public void Axes_DoNotShift_WhenTableSizeChanges()
    {
        // #620 のように表の件数を増減しても軸の出方は変わらない（表と軸を別の混合から引く根拠）。
        var small = Synthetic(7);
        var large = Synthetic(61);
        for (var id = 0; id < 1000; id++)
        {
            var a = small.ColorsFor(id);
            var b = large.ColorsFor(id);
            Assert.Equal(a.Stripe.On, b.Stripe.On);
            Assert.Equal(a.CapVisor.Equals(a.Shirt), b.CapVisor.Equals(b.Shirt));
            Assert.Equal(a.Undershirt.Equals(a.Shirt), b.Undershirt.Equals(b.Shirt));
        }
    }

    [Fact]
    public void VisorAxis_DoesNotOverrideAnExplicitVisor()
    {
        // 表がつばの色を書いている件（つば赤・つば金）は、その型が消えないこと＝二重に振らない。
        var palette = LoadReal();
        for (var id = 0; id < 4000; id++)
        {
            var e = palette.EntryFor(id);
            if (e.Visor.Equals(e.Team)) continue;
            Assert.Equal(e.Visor, palette.ColorsFor(id).CapVisor);
        }
    }

    [Fact]
    public void StripeAxis_UsesTheEntryStyle_OrTheTeamColor()
    {
        var palette = LoadReal();
        var seenStyled = false;
        var seenDefault = false;
        for (var id = 0; id < 4000; id++)
        {
            var c = palette.ColorsFor(id);
            if (!c.Stripe.On) continue;
            var e = palette.EntryFor(id);
            if (e.Stripe is { } style)
            {
                Assert.Equal(style.Color, c.Stripe.Color);
                Assert.Equal(style.FreqPerMeter, c.Stripe.FreqPerMeter);
                seenStyled = true;
            }
            else
            {
                Assert.Equal(e.Team, c.Stripe.Color);
                Assert.Equal(TeamStripeStyle.DefaultFreqPerMeter, c.Stripe.FreqPerMeter);
                seenDefault = true;
            }
        }
        Assert.True(seenStyled && seenDefault, "縞の型あり・なしの両方が出ていない");
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
            if (e.Stripe != null) Assert.Contains(e.Stripe.Color, sw);
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
        // 縞は表の1件では決まらない（軸が決める・issue #621）。
        Assert.Equal(TeamStripe.None, c.Stripe);
    }

    private static int IndexOf(TeamColorPalette palette, string id)
    {
        for (var i = 0; i < palette.Entries.Count; i++)
            if (palette.Entries[i].Id == id) return i;
        return -1;
    }
}
