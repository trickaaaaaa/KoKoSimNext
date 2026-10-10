using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 組み合わせ抽選（設計書05 §1.2, issue #631）。
///
/// <para>回帰の実害: 山の組み方が「出場校を強さ順に並べて標準シード順へ置く」だけの純関数で、乱数を
/// 一切引いていなかった。①入力（出場校と強さ）が夏と秋で変わらない＝<b>初戦の相手が同じ</b>
/// ②1位は必ず最下位と当たる＝<b>強豪同士が初戦で当たることが原理的にありえない</b>
/// （オーナー報告 2026-08-12）。</para>
/// </summary>
public sealed class TournamentDrawTests
{
    private readonly ITestOutputHelper _out;
    public TournamentDrawTests(ITestOutputHelper output) => _out = output;

    private static readonly NationCoefficients Coeff = new();
    private static readonly TournamentSchedule Schedule = new();

    private static School Sch(int id, double strength) => new()
    {
        Id = id, Name = $"校{id}", PrefectureId = 0, Strength = strength,
    };

    /// <summary>自校(id=1)＋others 校。強さは全校バラバラ（序列が一意に決まる）。</summary>
    private static List<School> Field(int others, double managerStrength = 60)
    {
        var list = new List<School> { Sch(1, managerStrength) };
        for (var i = 0; i < others; i++) list.Add(Sch(100 + i, 20.0 + (i * 7) % 61 + i * 0.01));
        return list;
    }

    private static IRandomSource Draw(ulong seed)
        => new Xoshiro256Random(seed).Fork(TournamentDraw.DrawStreamId);

    private static string Bracket(School?[] slots)
        => string.Join(",", slots.Select(s => s?.Name ?? "-"));

    // ===== シード数 =====

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]     // 3〜4校でも上位2校は反対の山＝最強が不戦勝（従来と同じ）
    [InlineData(8, 2)]
    [InlineData(16, 2)]
    [InlineData(32, 4)]
    [InlineData(64, 8)]
    [InlineData(128, 16)]
    [InlineData(190, 16)]  // 実際の県（神奈川190校で16シード）と同オーダー。上限で頭打ち
    public void SeedCountFor_ScalesWithTheField(int entrants, int expected)
        => Assert.Equal(expected, TournamentDraw.SeedCountFor(entrants));

    // ===== ① 決定論 =====

    /// <summary>同じ母種・同じ年・同じ大会種別なら常に同じ組み合わせ（不変条件#2）。</summary>
    [Fact]
    public void SameSeed_ProducesTheSameDraw()
    {
        var field = Field(31);
        var a = TournamentDraw.Arrange(field, Draw(12345));
        var b = TournamentDraw.Arrange(field, Draw(12345));
        Assert.Equal(Bracket(a), Bracket(b));
    }

    /// <summary>
    /// 抽選は Fork した専用ストリームで引く＝<b>本編の乱数列を1つも進めない</b>。
    /// ここが崩れると試合の乱数順が動いて統計帯まで動く（issue #631 の制約）。
    /// </summary>
    [Fact]
    public void Draw_DoesNotConsumeTheParentStream()
    {
        var field = Field(31);
        var used = new Xoshiro256Random(777);
        var untouched = new Xoshiro256Random(777);

        TournamentDraw.Arrange(field, used.Fork(TournamentDraw.DrawStreamId));

        for (var i = 0; i < 8; i++) Assert.Equal(untouched.NextDouble(), used.NextDouble());
    }

    // ===== ② 夏と秋で組み合わせが変わる（本 issue の発端） =====

    /// <summary>
    /// 夏と秋で初戦の相手が変わる。大会シードは Unity 側（TournamentEntry）が
    /// <c>母種 ^ (9000 + 年*10 + 種別)</c> で作るので、ここでも同じ形の種で確かめる。
    /// 修正前は山の組み方が乱数に一切依存せず、入力（出場校と強さ）が夏秋で同じ＝同じ表になっていた。
    /// </summary>
    [Fact]
    public void SummerAndAutumn_DrawDifferentBrackets()
    {
        const ulong master = 0xC0FFEE;
        const int yearIndex = 1;
        var field = Field(31);

        var summer = new TournamentRunner(
            field, field[0], Coeff, new Xoshiro256Random(master ^ (ulong)(9000 + yearIndex * 10 + 0)),
            Schedule, "夏大会");
        var autumn = new TournamentRunner(
            field, field[0], Coeff, new Xoshiro256Random(master ^ (ulong)(9000 + yearIndex * 10 + 1)),
            Schedule, "秋大会");

        _out.WriteLine($"夏の初戦: {summer.NextOpponent?.Name} ／ 秋の初戦: {autumn.NextOpponent?.Name}");

        Assert.NotNull(summer.NextOpponent);
        Assert.NotNull(autumn.NextOpponent);
        Assert.NotEqual(summer.NextOpponent!.Name, autumn.NextOpponent!.Name);
    }

    /// <summary>非シード校の配置は抽選で変わる（母種を変えると別の表になる）。</summary>
    [Fact]
    public void NonSeeds_AreDrawn_SeedsStayOnTheirSlots()
    {
        var field = Field(63);                                  // 64校＝8シード
        var a = TournamentDraw.Arrange(field, Draw(1));
        var b = TournamentDraw.Arrange(field, Draw(2));

        Assert.NotEqual(Bracket(a), Bracket(b));

        // シード校（上位8）のスロットは母種によらず同じ＝「シードは配置・残りは抽選」。
        var seeded = field.OrderByDescending(s => s.Strength).ThenBy(s => s.Id).Take(8).Select(s => s.Id).ToHashSet();
        for (var slot = 0; slot < a.Length; slot++)
        {
            var isSeedSlot = a[slot] is { } s && seeded.Contains(s.Id);
            if (!isSeedSlot) continue;
            Assert.Equal(a[slot]!.Id, b[slot]!.Id);
        }
    }

    // ===== ③ シードは対角へ分散 =====

    /// <summary>
    /// 上位K校が同じ山に固まらない（設計書05 §1.2「ブラケット対角に分散配置」）。
    /// K は2の冪なので、ブラケットを K 等分した各ブロックにシードがちょうど1校ずつ入る。
    /// </summary>
    [Theory]
    [InlineData(32, 4)]
    [InlineData(64, 8)]
    [InlineData(128, 16)]
    public void TopSeeds_AreSpreadAcrossTheBracket(int entrants, int seeds)
    {
        var field = Field(entrants - 1);
        var slots = TournamentDraw.Arrange(field, Draw(4649));
        var top = field.OrderByDescending(s => s.Strength).ThenBy(s => s.Id).Take(seeds).Select(s => s.Id).ToHashSet();

        var block = slots.Length / seeds;
        for (var b = 0; b < seeds; b++)
        {
            var inBlock = 0;
            for (var i = b * block; i < (b + 1) * block; i++)
            {
                if (slots[i] is { } s && top.Contains(s.Id)) inBlock++;
            }
            Assert.True(inBlock == 1, $"ブロック{b} のシード校が {inBlock} 校（1校であるべき＝対角分散が崩れている）");
        }
    }

    /// <summary>強豪同士が初戦で当たりうる（＝抽選が効いている）。純シード配置では原理的に起きない。</summary>
    [Fact]
    public void StrongSchools_CanMeetInTheFirstRound()
    {
        var field = Field(31);                                   // 32校＝4シード・28校は抽選
        var ranked = field.OrderByDescending(s => s.Strength).ThenBy(s => s.Id).ToList();
        var rankOf = ranked.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i);

        var upsetPairings = 0;
        for (var seed = 0UL; seed < 40; seed++)
        {
            var slots = TournamentDraw.Arrange(field, Draw(seed));
            for (var i = 0; i < slots.Length; i += 2)
            {
                if (slots[i] is not { } a || slots[i + 1] is not { } b) continue;
                // 上位1/4同士の初戦（純シード配置では絶対に起きない組み合わせ）。
                if (rankOf[a.Id] < 8 && rankOf[b.Id] < 8) upsetPairings++;
            }
        }

        _out.WriteLine($"40回の抽選で上位8校同士の初戦が {upsetPairings} 組");
        Assert.True(upsetPairings > 0, "何度抽選しても強豪同士が初戦で当たらない＝抽選が効いていない");
    }

    // ===== ④ 不戦勝は従来どおり =====

    [Theory]
    [InlineData(3)]
    [InlineData(20)]
    [InlineData(33)]
    [InlineData(48)]
    public void Byes_KeepTheOldShape(int entrants)
    {
        var field = Field(entrants - 1);
        var slots = TournamentDraw.Arrange(field, Draw(31));

        var size = 1;
        while (size < entrants) size *= 2;
        Assert.Equal(size, slots.Length);
        Assert.Equal(size - entrants, slots.Count(s => s is null));                 // 不戦勝の数は従来と同じ
        Assert.Equal(entrants, slots.Where(s => s is not null).Select(s => s!.Id).Distinct().Count()); // 全校が1回だけ

        // 空きスロットの相手＝1回戦不戦勝。最強シードは常にその側（実際の県大会のシード1回戦免除）。
        if (size == entrants) return;
        var strongest = field.OrderByDescending(s => s.Strength).ThenBy(s => s.Id).First();
        var at = System.Array.FindIndex(slots, s => s is not null && s.Id == strongest.Id);
        Assert.Null(slots[at % 2 == 0 ? at + 1 : at - 1]);
    }

    // ===== ⑤ セーブ往復 =====

    /// <summary>抽選結果はセーブに載って復元される（ロード後に別の表にならない）。</summary>
    [Fact]
    public void Draw_SurvivesSaveRoundTrip()
    {
        var field = Field(31);
        var runner = new TournamentRunner(
            field, field[0], Coeff, new Xoshiro256Random(20260813), Schedule, "テスト大会");
        var before = runner.BuildBracketView().Rounds[0];

        var reloaded = TournamentRunner.FromSaved(runner.CaptureState(), field, field[0], Coeff, Schedule);
        var after = reloaded.BuildBracketView().Rounds[0];

        Assert.Equal(
            string.Join(",", before.Cards.Select(c => $"{c.Top.TeamName ?? "-"}/{c.Bottom.TeamName ?? "-"}")),
            string.Join(",", after.Cards.Select(c => $"{c.Top.TeamName ?? "-"}/{c.Bottom.TeamName ?? "-"}")));
    }
}
