using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Career.Draft;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using KokoSim.Engine.Stats;
using Xunit;

namespace KokoSim.Engine.Tests.Career;

/// <summary>
/// 教え子名鑑（issue #586・設計書20 §6「指名実績」）。指名された者だけが勲章として残ること、
/// 当時の背番号が引けること、台帳を積んでも既存の乱数列を乱さないこと（帯不変）を固定する。
/// </summary>
public sealed class DraftHistoryTests
{
    private static readonly DraftCoefficients C = new();

    // 足切り（設計書20 §3.1a）は打撃力チップ（弾道/選球眼込み）で見るので、それも同じ値で埋める。
    private static readonly AbilityKind[] BatterKinds =
    {
        AbilityKind.Contact, AbilityKind.Power, AbilityKind.Speed,
        AbilityKind.Fielding, AbilityKind.ArmStrength, AbilityKind.Catching,
        AbilityKind.LaunchTendency, AbilityKind.Discipline,
    };
    private static readonly TeamStrengthCoefficients S = new();

    private static DevelopingPlayer Batter(int id, int grade, int level, int number, string name)
    {
        var p = new DevelopingPlayer { Id = id, Name = name, Grade = grade, UniformNumber = number };
        PitcherRoleResolver.SeedAptitudeForRole(p, false);
        foreach (var k in BatterKinds) { p.SetLevel(k, level); p.SetCap(k, level); }
        return p;
    }

    /// <summary>能力を振り切った3年だけを並べたロスター（確実に候補入りする）。</summary>
    private static List<DevelopingPlayer> StarSeniors() => new()
    {
        Batter(1, 3, 99, 1, "中田　大祐"),
        Batter(2, 3, 99, 18, "岩崎　幹太"),
    };

    private static DraftResult Nominate(IReadOnlyList<DevelopingPlayer> roster, ulong seed)
        => DraftEngine.RunNomination(3, roster, _ => (PlayerStats?)null, C, S, new Xoshiro256Random(seed));

    /// <summary>ロスターから背番号を引く関数（見つからなければ0）。</summary>
    private static Func<int, int> NumbersOf(IReadOnlyList<DevelopingPlayer> roster) => id =>
    {
        foreach (var p in roster) if (p.Id == id) return p.UniformNumber;
        return 0;
    };

    // ── 積み方 ──────────────────────────────────────────────

    /// <summary>指名された者だけが台帳に残る（落選は勲章ではない）。</summary>
    [Fact]
    public void AddFrom_KeepsNominatedOnly()
    {
        var roster = StarSeniors();
        // 圏外の3年を混ぜても台帳には出ない（候補にすらならない）。
        roster.Add(Batter(3, 3, 10, 25, "控え　三郎"));

        var result = Nominate(roster, 4242);
        var history = new DraftHistory();
        var added = history.AddFrom(result, calendarYear: 2028, NumbersOf(roster));

        Assert.Equal(result.Nominated.Count, added.Count);
        Assert.Equal(added.Count, history.Count);
        Assert.All(history.Records, r => Assert.True(r.Round >= 1, "指名順位が入っていない"));
        Assert.DoesNotContain(history.Records, r => r.Name == "控え　三郎");
    }

    /// <summary>当時の背番号がロスターから引かれる（卒業後は引けなくなるので確定時に焼く）。</summary>
    [Fact]
    public void AddFrom_ResolvesUniformNumberAtTheTime()
    {
        var roster = StarSeniors();
        var history = new DraftHistory();
        history.AddFrom(Nominate(roster, 777), calendarYear: 2028, NumbersOf(roster));
        Assert.NotEmpty(history.Records);

        foreach (var r in history.Records)
        {
            var player = roster.First(p => p.Id == r.PlayerId);
            Assert.Equal(player.UniformNumber, r.UniformNumber);
            Assert.Equal(player.Name, r.Name);
        }
    }

    /// <summary>ロスターに居ない選手（既に卒業した等）は背番号なし（0）で積む＝落とさない。</summary>
    [Fact]
    public void AddFrom_UnknownPlayer_GetsNoNumber()
    {
        var roster = StarSeniors();
        var result = Nominate(roster, 777);
        var history = new DraftHistory();
        history.AddFrom(result, calendarYear: 2028, NumbersOf(new List<DevelopingPlayer>()));

        Assert.Equal(result.Nominated.Count, history.Count);
        Assert.All(history.Records, r => Assert.Equal(0, r.UniformNumber));
    }

    /// <summary>年を跨いで積み上がる（過去の勲章が消えない）。</summary>
    [Fact]
    public void History_AccumulatesAcrossYears()
    {
        var history = new DraftHistory();
        var roster = StarSeniors();
        history.AddFrom(Nominate(roster, 100), calendarYear: 2027, NumbersOf(roster));
        var afterFirst = history.Count;
        history.AddFrom(Nominate(roster, 200), calendarYear: 2028, NumbersOf(roster));

        Assert.True(history.Count > afterFirst, "2年目の指名が積まれていない");
        Assert.Contains(history.Records, r => r.Year == 2027);
        Assert.Contains(history.Records, r => r.Year == 2028);
    }

    /// <summary>暦年は導出でなく確定時の値をそのまま持つ（起点を動かしても過去の年がずれない）。</summary>
    [Fact]
    public void Record_KeepsCalendarYearAsGiven()
    {
        var roster = StarSeniors();
        var history = new DraftHistory();
        history.AddFrom(Nominate(roster, 31337), calendarYear: 1999, NumbersOf(roster));
        Assert.All(history.Records, r =>
        {
            Assert.Equal(1999, r.Year);
            Assert.Equal(3, r.YearIndex);   // 年度は DraftResult 由来（暦年と独立）
        });
    }

    // ── 帯不変（不変条件#2） ──────────────────────────────────

    /// <summary>
    /// 指名は独立Forkなので、何回引いても呼び出し元の乱数列が1ビットも進まない。
    /// これが崩れると「キャリアタブを実装したら帯が動いた」になる。
    /// </summary>
    [Fact]
    public void Nomination_DoesNotConsumeCallerStream()
    {
        var roster = StarSeniors();
        var rng = new Xoshiro256Random(2026);
        var before = rng.CaptureState();

        for (var i = 0; i < 5; i++)
        {
            var result = DraftEngine.RunNomination(3, roster, _ => (PlayerStats?)null, C, S, rng);
            new DraftHistory().AddFrom(result, 2028, NumbersOf(roster));
        }

        Assert.Equal(before, rng.CaptureState());
    }
}
