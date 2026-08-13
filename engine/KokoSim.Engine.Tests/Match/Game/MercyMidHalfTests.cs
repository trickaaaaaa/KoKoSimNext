using System;
using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// issue #633: コールドは「点差がついた時点で」打ち切る（設計書05 §1.3）。
///
/// <para>従来は判定が半回の境目にしか無く、裏の攻撃中に点差が開いてもその半回を3アウトまで
/// 消化していた。サヨナラは打席ごとの述語で正しく効いていたので、コールドを同じ器へ相乗りさせる。</para>
///
/// <para><b>表には入れない</b>: 先攻がコールド差をつけても、その回の裏は行ってから終わるのが
/// 実際のルール（後攻の攻撃機会を奪わない）。issue #537 の挙動を壊さないことも併せて固定する。</para>
/// </summary>
public sealed class MercyMidHalfTests
{
    private static Player Pos(FieldPosition pos) => new()
    {
        Position = pos, Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

    private static Team Team(string name)
    {
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase), Pos(FieldPosition.SecondBase),
            Pos(FieldPosition.ThirdBase), Pos(FieldPosition.Shortstop), Pos(FieldPosition.LeftField),
            Pos(FieldPosition.CenterField), Pos(FieldPosition.RightField),
            Pos(FieldPosition.Pitcher) with { Name = name + "P", Pitching = PitcherAttributes.LeagueAverage },
        };
        return new Team
        {
            Name = name, BattingOrder = order, PitcherSlot = 8,
            Reserves = TeamReserves.Merge(
                new[] { Pos(FieldPosition.Pitcher) with { Name = name + "R", Pitching = PitcherAttributes.LeagueAverage } },
                System.Array.Empty<Player>()),
        };
    }

    private static GameContext Mercy() => new() { MercyRuleEnabled = true };

    private static List<LivePlateAppearance> PlayOut(
        ulong seed, KokoSim.Engine.Debugging.ScenarioStart scenario, out GameResult result)
    {
        var prog = new MatchProgression(Team("A"), Team("H"), Mercy(), seed, scenario);
        var pas = new List<LivePlateAppearance>();
        while (prog.Advance()) pas.Add(prog.Current!);
        result = prog.BuildResult();
        return pas;
    }

    /// <summary>
    /// 後攻が裏の攻撃中にコールド点差へ到達したら、<b>その打席が試合の最後の打席</b>になる。
    /// 修正前はここで終わらず、その半回を3アウトまで消化していた。
    ///
    /// <para>コールドは「5回10点差」と「7回7点差」の2条件（<c>GameEngine.IsMercy</c>）なので両方を踏む。
    /// 片方だけだと、もう一方の閾値を通る経路が未検証のまま残る。</para>
    /// </summary>
    [Theory]
    [InlineData(5, 9, 10)]   // 5回裏・9-0＝あと1点で10点差
    [InlineData(7, 6, 7)]    // 7回裏・6-0＝あと1点で7点差
    public void BottomHalf_EndsOnThePlateAppearanceThatReachesMercy(int inning, int homeScore, int diff)
    {
        var hits = 0;
        for (ulong seed = 1; seed <= 200 && hits < 8; seed++)
        {
            // 満塁無死＝この回の得点でコールド差へ届く。
            var pas = PlayOut(seed, new KokoSim.Engine.Debugging.ScenarioStart
            {
                Inning = inning, IsTop = false, Outs = 0,
                OnFirst = true, OnSecond = true, OnThird = true,
                AwayScore = 0, HomeScore = homeScore,
            }, out var result);

            var idx = pas.FindIndex(pa =>
                !pa.IsTop && pa.HomeScore - pa.AwayScore >= diff);
            if (idx < 0) continue;   // この回でコールド差に届かなかったシードは対象外
            hits++;

            Assert.True(result.MercyEnded, $"seed {seed}: MercyEnded が立っていない");
            Assert.Equal(pas.Count - 1, idx);   // 到達打席＝最後の打席
        }
        Assert.True(hits >= 8, $"コールド差へ到達するシードが足りない（{hits} 件）");
    }

    /// <summary>
    /// 先攻が表でコールド点差へ到達しても、その回の裏は行われる（issue #537 の挙動を壊さない）。
    /// 表に打席ごとの打ち切りを入れると後攻の攻撃機会を奪うため、入れていないことの回帰。
    /// </summary>
    [Fact]
    public void TopHalf_StillGivesTheHomeTeamItsAtBat()
    {
        var hits = 0;
        for (ulong seed = 1; seed <= 200 && hits < 8; seed++)
        {
            // 5回表・先攻が9-0でリード・満塁無死。
            var pas = PlayOut(seed, new KokoSim.Engine.Debugging.ScenarioStart
            {
                Inning = 5, IsTop = true, Outs = 0,
                OnFirst = true, OnSecond = true, OnThird = true,
                AwayScore = 9, HomeScore = 0,
            }, out var result);

            var reached = pas.FindIndex(pa => pa.IsTop && pa.AwayScore - pa.HomeScore >= 10);
            if (reached < 0) continue;
            hits++;

            Assert.True(result.MercyEnded, $"seed {seed}: MercyEnded が立っていない");
            Assert.Contains(pas, pa => pa.Inning == 5 && !pa.IsTop);   // 5回裏が行われている
        }
        Assert.True(hits >= 8, $"コールド差へ到達するシードが足りない（{hits} 件）");
    }

    /// <summary>
    /// 打ち切った半回の得点がイニング別スコアへ記録される（サヨナラ経路と同じ扱い＝呼び漏れが無い）。
    /// 後攻は5回まで攻撃しているので、イニング別得点は5要素ぶん記録されていること。
    /// </summary>
    [Fact]
    public void MercyCutHalf_RecordsItsInningRuns()
    {
        var hits = 0;
        for (ulong seed = 1; seed <= 200 && hits < 5; seed++)
        {
            var prog = new MatchProgression(Team("A"), Team("H"), Mercy(), seed,
                new KokoSim.Engine.Debugging.ScenarioStart
                {
                    Inning = 5, IsTop = false, Outs = 0,
                    OnFirst = true, OnSecond = true, OnThird = true,
                    AwayScore = 0, HomeScore = 9,
                });
            var last = (LivePlateAppearance?)null;
            while (prog.Advance()) last = prog.Current;
            var result = prog.BuildResult();
            if (last is null || !result.MercyEnded) continue;
            if (last.HomeScore - last.AwayScore < 10) continue;
            hits++;

            var homeInnings = result.HomeLineScore;
            Assert.Equal(5, homeInnings.Count);
            // 打ち切った5回裏ぶんの得点が最後の要素に入っている（0点で終わることはない＝到達した回）。
            Assert.True(homeInnings[4] > 0, $"seed {seed}: 打ち切った半回の得点が記録されていない");
        }
        Assert.True(hits >= 5, $"検証できたシードが足りない（{hits} 件）");
    }
}
