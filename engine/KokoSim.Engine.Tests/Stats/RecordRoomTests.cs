using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Stats;
using Xunit;

namespace KokoSim.Engine.Tests.Stats;

/// <summary>
/// 記録室の集計（issue #600・設計書06 §3.9）。表示専用の純関数＝乱数も物理層も通らないので帯は不変。
/// 主眼は「規定打席未満の選手が首位に出ない」こと（高校野球は試合数が少なく、閾値が無いと
/// 1打数1安打＝打率1.000 が校歴代1位に出る）。
/// </summary>
public sealed class RecordRoomTests
{
    // ── 候補の組み立て（テスト用の最小ヘルパ） ──────────────────

    private static BattingLine Bat(int pa, int ab, int h, int hr = 0, int rbi = 0, int sb = 0, int id = 1)
        => new(1, FieldPosition.CenterField, "打者", pa, ab, h, 0, 0, hr, rbi, 0, 0, id,
            StolenBases: sb);

    private static PitchingLine Pit(int outs, int hits, int er, int k = 0, int bb = 0, int id = 1)
        => new("投手", outs, 0, hits, er, k, bb, 0, id, EarnedRuns: er);

    /// <summary>
    /// 出場 <paramref name="games"/> 試合ぶん・累計は引数どおりの打者。
    /// 出場試合数は「打席が立った試合」でしか増えない（<c>BattingStatLine.Add</c>）ので、
    /// 1試合目に累計を全部載せ、残りは1打席0打数の試合として積む（<paramref name="pa"/> ≧ games が前提）。
    /// </summary>
    private static RecordCandidate Batter(int id, int year, int games, int pa, int ab, int h,
        int hr = 0, int rbi = 0, int sb = 0)
    {
        var c = new RecordCandidate { PlayerId = id, Year = year };
        c.Batting.Add(Bat(pa - (games - 1), ab, h, hr, rbi, sb, id));
        for (var i = 1; i < games; i++) c.Batting.Add(Bat(1, 0, 0, id: id));
        return c;
    }

    private static RecordCandidate Pitcher(int id, int year, int games, int outs, int hits, int er, int k = 0)
    {
        var c = new RecordCandidate { PlayerId = id, Year = year };
        c.Pitching.Add(Pit(outs, hits, er, k, id: id), started: true, win: false, loss: false);
        for (var i = 1; i < games; i++) c.Pitching.Add(Pit(0, 0, 0, id: id), started: false, win: false, loss: false);
        return c;
    }

    // ── 規定打席・規定投球回（本 issue の完了条件） ─────────────

    /// <summary>
    /// 規定打席に届かない選手は率系（打率・OPS・出塁率）の首位に出ない。
    /// 1打数1安打の .1000 が10試合ぶんの .350 を押しのけない。
    /// </summary>
    [Fact]
    public void Average_ExcludesPlayersBelowThePlateAppearanceThreshold()
    {
        var regular = Batter(id: 1, year: 1, games: 10, pa: 40, ab: 40, h: 14);   // .350
        var cameo = Batter(id: 2, year: 1, games: 1, pa: 1, ab: 1, h: 1);         // 1.000（規定未満）
        var top = RecordRoom.Top(new[] { regular, cameo }, RecordCategory.Average, 3);

        Assert.Single(top);
        Assert.Equal(1, top[0].PlayerId);
        Assert.Equal(".350", top[0].Display);
    }

    /// <summary>規定投球回に届かない投手は防御率・WHIP の首位に出ない。</summary>
    [Fact]
    public void Era_ExcludesPitchersBelowTheInningsThreshold()
    {
        var ace = Pitcher(id: 1, year: 1, games: 10, outs: 120, hits: 30, er: 8);   // 40回・防御率1.80
        var mopUp = Pitcher(id: 2, year: 1, games: 1, outs: 3, hits: 0, er: 0);     // 1回・0.00（規定未満）
        var era = RecordRoom.Top(new[] { ace, mopUp }, RecordCategory.Era, 3);
        var whip = RecordRoom.Top(new[] { ace, mopUp }, RecordCategory.Whip, 3);

        Assert.Single(era);
        Assert.Equal(1, era[0].PlayerId);
        Assert.Single(whip);
        Assert.Equal(1, whip[0].PlayerId);
    }

    /// <summary>数系（本塁打・打点・盗塁・奪三振）は規定を課さない＝1本でも載る。</summary>
    [Fact]
    public void CountingCategories_DoNotRequireQualification()
    {
        var regular = Batter(id: 1, year: 1, games: 10, pa: 40, ab: 40, h: 14, hr: 2);
        var cameo = Batter(id: 2, year: 1, games: 1, pa: 1, ab: 1, h: 1, hr: 1);
        var top = RecordRoom.Top(new[] { regular, cameo }, RecordCategory.HomeRuns, 3);

        Assert.Equal(2, top.Count);
        Assert.Equal(new[] { 1, 2 }, top.Select(e => e.PlayerId));
    }

    /// <summary>0本の選手は数系の順位表に載らない（「本塁打0で3位」を出さない）。</summary>
    [Fact]
    public void CountingCategories_SkipZeroes()
    {
        var slugger = Batter(id: 1, year: 1, games: 10, pa: 40, ab: 40, h: 14, hr: 3);
        var none = Batter(id: 2, year: 1, games: 10, pa: 40, ab: 40, h: 12);
        Assert.Single(RecordRoom.Top(new[] { slugger, none }, RecordCategory.HomeRuns, 3));
    }

    /// <summary>規定はチーム試合数（＝最大出場試合数）に比例して動く。</summary>
    [Fact]
    public void Qualification_ScalesWithTeamGames()
    {
        var few = new[] { Batter(1, 1, games: 3, pa: 12, ab: 12, h: 4) };
        var many = new[] { Batter(1, 1, games: 20, pa: 80, ab: 80, h: 28) };

        Assert.Equal((6, 9), RecordRoom.QualificationOf(few));       // 3試合×2打席 / ×1回
        Assert.Equal((40, 60), RecordRoom.QualificationOf(many));    // 20試合ぶん
    }

    /// <summary>係数（<c>data/coefficients.yaml</c> の records:）を上げると閾値も上がる。</summary>
    [Fact]
    public void Qualification_FollowsTheCoefficients()
    {
        var scope = new[] { Batter(1, 1, games: 10, pa: 40, ab: 40, h: 14) };
        var strict = new RecordsCoefficients { QualifiedPlateAppearancesPerGame = 3.1, QualifiedOutsPerGame = 3.0 };

        Assert.Equal(20, RecordRoom.QualificationOf(scope).PlateAppearances);
        Assert.Equal(31, RecordRoom.QualificationOf(scope, strict).PlateAppearances);
    }

    /// <summary>成績が空でも閾値は1以上＝0除算も「全員規定達成」も起こさない。</summary>
    [Fact]
    public void Qualification_OnEmptyScope_IsAtLeastOne()
        => Assert.Equal((1, 1), RecordRoom.QualificationOf(new List<RecordCandidate>()));

    // ── 並べ方 ──────────────────────────────────────────

    /// <summary>防御率・WHIP は小さいほど上位（他は大きいほど上位）。</summary>
    [Fact]
    public void Era_IsRankedAscending()
    {
        var good = Pitcher(id: 1, year: 1, games: 10, outs: 120, hits: 25, er: 5);
        var bad = Pitcher(id: 2, year: 1, games: 10, outs: 120, hits: 50, er: 20);
        var top = RecordRoom.Top(new[] { bad, good }, RecordCategory.Era, 2);

        Assert.Equal(new[] { 1, 2 }, top.Select(e => e.PlayerId));
        Assert.True(RecordRoom.LowerIsBetter(RecordCategory.Era));
        Assert.False(RecordRoom.LowerIsBetter(RecordCategory.Average));
    }

    /// <summary>同値は選手ID→年度の昇順で確定する（決定論・順位が揺れない）。</summary>
    [Fact]
    public void Ties_AreBrokenDeterministically()
    {
        var a = Batter(id: 7, year: 2, games: 10, pa: 40, ab: 40, h: 10, hr: 3);
        var b = Batter(id: 3, year: 1, games: 10, pa: 40, ab: 40, h: 10, hr: 3);
        var c = Batter(id: 3, year: 3, games: 10, pa: 40, ab: 40, h: 10, hr: 3);

        var top = RecordRoom.Top(new[] { a, b, c }, RecordCategory.HomeRuns, 3);
        Assert.Equal(new[] { (3, 1), (3, 3), (7, 2) }, top.Select(e => (e.PlayerId, e.Year)));
    }

    /// <summary>該当者が居ない部門は空で返る（呼び出し側が空枠を埋める）。</summary>
    [Fact]
    public void Top_WithNoQualifier_IsEmpty()
        => Assert.Empty(RecordRoom.Top(new List<RecordCandidate>(), RecordCategory.Average, 3));

    // ── 表示の整形（数字と和文を混ぜない＝Label 分割の前提） ────────

    [Fact]
    public void Display_UsesBaseballConventions()
    {
        var b = Batter(id: 1, year: 1, games: 10, pa: 40, ab: 40, h: 14, hr: 12, rbi: 41, sb: 22);
        var p = Pitcher(id: 1, year: 1, games: 10, outs: 253, hits: 60, er: 40, k: 98);

        Assert.Equal(".350", RecordRoom.DisplayOf(RecordCategory.Average, b));
        Assert.Equal("12", RecordRoom.DisplayOf(RecordCategory.HomeRuns, b));
        Assert.Equal("41", RecordRoom.DisplayOf(RecordCategory.Rbi, b));
        Assert.Equal("22", RecordRoom.DisplayOf(RecordCategory.StolenBases, b));
        Assert.Equal("84 1/3", RecordRoom.DisplayOf(RecordCategory.InningsPitched, p));
        Assert.Equal("98", RecordRoom.DisplayOf(RecordCategory.StrikeOuts, p));
        // OPS は1を超えるので先頭の0を落とさない。
        Assert.Equal("1.104", RecordRoom.Rate3(1.104));
        Assert.Equal(".384", RecordRoom.Rate3(0.384));
    }

    /// <summary>部門の表示名・打撃/投手の別は engine が単一ソースで持つ。</summary>
    [Fact]
    public void Categories_CarryTheirOwnLabels()
    {
        Assert.Equal("打率", RecordRoom.LabelOf(RecordCategory.Average));
        Assert.Equal("防御率", RecordRoom.LabelOf(RecordCategory.Era));
        Assert.All(RecordRoom.BattingCategories, c => Assert.False(RecordRoom.IsPitching(c)));
        Assert.All(RecordRoom.PitchingCategories, c => Assert.True(RecordRoom.IsPitching(c)));
    }

    // ── スコープ2種 ──────────────────────────────────────

    /// <summary>通算スコープは帳簿1冊＝選手1人1件で、年を持たない。</summary>
    [Fact]
    public void CareerCandidates_AreOnePerPlayer()
    {
        var book = new StatBook();
        book.GetOrCreate(5).Batting.Add(Bat(10, 9, 3, id: 5));
        book.GetOrCreate(5).Batting.Add(Bat(10, 9, 4, id: 5));
        book.GetOrCreate(2).Batting.Add(Bat(10, 9, 2, id: 2));

        var cands = RecordRoom.CareerCandidates(book);
        Assert.Equal(new[] { 2, 5 }, cands.Select(c => c.PlayerId));   // ID昇順＝辞書順に依存しない
        Assert.All(cands, c => Assert.Equal(0, c.Year));
        Assert.Equal(7, cands[1].Batting.Hits);                        // 2試合ぶんが合算されている
    }

    /// <summary>
    /// シーズンスコープは大会別アーカイブを「選手×学年」へ畳む。1つの学年の夏と秋は同じ年度なので
    /// 合算して1件になり、年度はアーカイブに焼いた値から出る。
    /// </summary>
    [Fact]
    public void SeasonCandidates_MergeSummerAndAutumnOfTheSameGrade()
    {
        var archive = new TournamentArchive();
        var info = new Dictionary<int, (int Grade, int UniformNumber)> { [1] = (3, 7) };

        var summer = new StatBook();
        summer.GetOrCreate(1).Batting.Add(Bat(20, 18, 6, hr: 2, id: 1));
        archive.Archive(TournamentSlot.SummerPref, summer, info, year: 4);

        var autumn = new StatBook();
        autumn.GetOrCreate(1).Batting.Add(Bat(15, 13, 5, hr: 1, id: 1));
        archive.Archive(TournamentSlot.Autumn, autumn, info, year: 4);

        var cands = RecordRoom.SeasonCandidates(archive);
        Assert.Single(cands);
        Assert.Equal(3, cands[0].Grade);
        Assert.Equal(4, cands[0].Year);
        Assert.Equal(11, cands[0].Batting.Hits);
        Assert.Equal(3, cands[0].Batting.HomeRuns);
    }

    /// <summary>学年が違えば別シーズン＝同じ選手でも2件に分かれる（校歴代の「シーズン1位」の単位）。</summary>
    [Fact]
    public void SeasonCandidates_SplitByGrade()
    {
        var archive = new TournamentArchive();
        var second = new StatBook();
        second.GetOrCreate(1).Batting.Add(Bat(20, 18, 4, id: 1));
        archive.Archive(TournamentSlot.SummerPref, second,
            new Dictionary<int, (int, int)> { [1] = (2, 7) }, year: 3);

        var third = new StatBook();
        third.GetOrCreate(1).Batting.Add(Bat(20, 18, 9, id: 1));
        archive.Archive(TournamentSlot.SummerPref, third,
            new Dictionary<int, (int, int)> { [1] = (3, 1) }, year: 4);

        var cands = RecordRoom.SeasonCandidates(archive);
        Assert.Equal(2, cands.Count);
        Assert.Equal(new[] { 2, 3 }, cands.Select(c => c.Grade));
        Assert.Equal(new[] { 3, 4 }, cands.Select(c => c.Year));

        // 3年時の .500 が首位（同一選手でもシーズンごとに競う）。
        var top = RecordRoom.Top(cands, RecordCategory.Average, 2);
        Assert.Equal(3, top[0].Grade);
        Assert.Equal(4, top[0].Year);
    }

    /// <summary>年度が無いアーカイブ（issue #600 より前のセーブ）でも 0 のまま壊れず読める。</summary>
    [Fact]
    public void SeasonCandidates_WithoutYear_StayAtZero()
    {
        var archive = new TournamentArchive();
        var book = new StatBook();
        book.GetOrCreate(1).Batting.Add(Bat(20, 18, 6, id: 1));
        archive.Archive(TournamentSlot.SummerPref, book,
            new Dictionary<int, (int, int)> { [1] = (3, 7) });   // year 未指定＝旧呼び出し

        var cands = RecordRoom.SeasonCandidates(archive);
        Assert.Single(cands);
        Assert.Equal(0, cands[0].Year);
    }

    /// <summary>規定は年度ごとに出す＝短い年と長い年を同じ閾値で測らない。</summary>
    [Fact]
    public void Qualification_IsComputedPerSeason()
    {
        // 1年目はチーム1試合（規定打席2）、2年目はチーム10試合（規定打席20）。
        var shortYear = Batter(id: 1, year: 1, games: 1, pa: 8, ab: 8, h: 4);      // .500・規定達成
        var longYear = Batter(id: 2, year: 2, games: 10, pa: 40, ab: 40, h: 14);   // .350・規定達成
        var longYearCameo = Batter(id: 3, year: 2, games: 1, pa: 4, ab: 4, h: 3);  // .750・2年目では規定未満

        var top = RecordRoom.Top(new[] { shortYear, longYear, longYearCameo }, RecordCategory.Average, 3);
        Assert.Equal(new[] { 1, 2 }, top.Select(e => e.PlayerId));
    }
}
