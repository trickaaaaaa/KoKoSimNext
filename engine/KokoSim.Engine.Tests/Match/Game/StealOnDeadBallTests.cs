using System.Collections.Generic;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// issue #626: 盗塁が成立しない球（ファウル＝ボールデッド／打球＝打球優先）では盗塁を無かったことにする。
///
/// <para>engine は盗塁を実球より前に完全解決してから投げるため、その球が無効だったと判るのは後になる。
/// 乱数消費順を動かさないよう「解決はそのまま走らせて適用結果だけ巻き戻す」実装なので、
/// ここでは<b>巻き戻しが漏れなく効いているか</b>を球単位の観測（<see cref="LivePitch"/>）で固定する。</para>
///
/// <para>強制発動にファウルを作る手段が無いので、狙い撃ちではなく「全球で盗塁を試みる試合」を回して
/// 不変条件で殺す。牽制（<see cref="LivePitch.PickoffPlay"/> が載る球）は盗塁と独立に塁・アウトを動かせる
/// 正当な事象（issue #402 で PickoffBaseProb が 0→0.05 に正値化済み）なので、その球自体は比較から除く。</para>
/// </summary>
public sealed class StealOnDeadBallTests
{
    private static Player Pos(FieldPosition pos) => new()
    {
        Position = pos, Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 70, ArmStrength = 50, Fielding = 50, Catching = 50,   // 走力高め＝盗塁企図を増やす
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

    /// <summary>全球で盗塁を試みながら1試合を回し、球ごとの観測を集める。</summary>
    private static (GameResult Result, List<LivePitch> Pitches) PlayStealingEveryPitch(
        ulong seed, StealTarget target)
    {
        var prog = new MatchProgression(Team("A"), Team("H"),
            new GameContext { CaptureTimelines = true, CapturePitches = true }, seed);
        var pitches = new List<LivePitch>();
        LivePitch? seen = null;
        while (true)
        {
            var r = prog.AdvancePitch();
            var lp = prog.LastPitch;
            if (lp is not null && !ReferenceEquals(lp, seen)) { seen = lp; pitches.Add(lp); }
            if (r == AdvancePitchResult.Finished) break;
            if (r == AdvancePitchResult.Pitch)
            {
                // 攻守どちらの窓かを問わず両軍へ予約する（守備側の予約はその回では発火しない）。
                prog.SetStealAttempt(true, StartType.Gamble, target);
                prog.SetStealAttempt(false, StartType.Gamble, target);
            }
        }
        return (prog.BuildResult(), pitches);
    }

    /// <summary>
    /// issue #661: 牽制は既定でオフ。牽制球が映ると「この後、盗塁だ」と分かってしまうので、
    /// 全球で盗塁を試みる試合でも牽制の絵（<see cref="LivePitch.PickoffPlay"/>）も牽制死も出ない。
    /// </summary>
    [Theory]
    [InlineData(1UL, StealTarget.Second)]
    [InlineData(7UL, StealTarget.Third)]
    [InlineData(42UL, StealTarget.Second)]
    public void PickoffIsOffByDefault_NoPickoffPlayNorPickoffOut(ulong seed, StealTarget target)
    {
        Assert.Equal(0.0, new BaserunningCoefficients().PickoffBaseProb);

        var (result, pitches) = PlayStealingEveryPitch(seed, target);

        Assert.All(pitches, p => Assert.Null(p.PickoffPlay));
        Assert.Equal(0, result.PickoffCount);
        Assert.Contains(pitches, p => p.StealPlay is not null);   // 前提: 盗塁自体は起きている
    }

    /// <summary>ファウル・打球の球には盗塁の再生データが載らない（＝絵にも出ない）。</summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    [InlineData(42UL)]
    [InlineData(2026UL)]
    public void FoulOrInPlayPitch_NeverCarriesStealPlay(ulong seed)
    {
        var (_, pitches) = PlayStealingEveryPitch(seed, StealTarget.Second);

        foreach (var p in pitches)
        {
            if (p.Pitch.Kind is PitchKind.Foul or PitchKind.InPlay)
                Assert.Null(p.StealPlay);
        }
        // 前提の確認: そもそもファウル/打球の球が試合に含まれていること（空振り検証を防ぐ）。
        Assert.Contains(pitches, p => p.Pitch.Kind == PitchKind.Foul);
        Assert.Contains(pitches, p => p.Pitch.Kind == PitchKind.InPlay);
    }

    /// <summary>
    /// ファウルはボールデッド＝盗塁企図だけではその球で塁・アウトが動かない。
    /// 打席の2球目以降だけを見る（初球は打席頭との比較先が無い）。牽制
    /// （<see cref="LivePitch.PickoffPlay"/> が載る球）は投球そのものと独立に塁・アウトを動かせる
    /// 正当な事象なので比較から除く（ファウルかどうかとは無関係に牽制刺殺が起きうる）。
    /// </summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    [InlineData(42UL)]
    [InlineData(2026UL)]
    public void FoulPitch_LeavesBasesAndOutsUntouched(ulong seed)
    {
        var (_, pitches) = PlayStealingEveryPitch(seed, StealTarget.Second);

        for (var i = 1; i < pitches.Count; i++)
        {
            var cur = pitches[i];
            if (cur.Pitch.Kind != PitchKind.Foul) continue;
            if (cur.PitchNumberInPa <= 1) continue;      // 同じ打席の前の球が無い
            if (cur.PickoffPlay is not null) continue;   // 牽制は投球と独立に塁・アウトを動かせる正当な事象
            var prev = pitches[i - 1];
            Assert.Equal(prev.Outs, cur.Outs);
            Assert.Equal(prev.BaseFirst, cur.BaseFirst);
            Assert.Equal(prev.BaseSecond, cur.BaseSecond);
            Assert.Equal(prev.BaseThird, cur.BaseThird);
        }
    }

    /// <summary>
    /// 絵が載った球は必ず記録された盗塁である（記録を消したのに絵だけ残る、が無い）。
    ///
    /// <para>逆向きの等号は成立しない: 盗塁が3アウト目になった球は<b>実球そのものが投げられない</b>ため、
    /// その盗塁には載せる <see cref="LivePitch"/> が存在しない（本 issue とは無関係の既存の性質）。</para>
    /// </summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    [InlineData(42UL)]
    [InlineData(2026UL)]
    public void StealPlayCount_NeverExceedsRecordedSteals(ulong seed)
    {
        var (result, pitches) = PlayStealingEveryPitch(seed, StealTarget.Second);

        var withPlay = 0;
        foreach (var p in pitches) if (p.StealPlay is not null) withPlay++;
        var recorded = result.AwayTactics.StealAttempts + result.HomeTactics.StealAttempts;

        Assert.True(withPlay <= recorded, $"絵 {withPlay} 件 > 記録 {recorded} 件＝記録だけ巻き戻された");
        Assert.True(recorded > 0, "盗塁が一度も起きない試合では検証にならない");
    }

    /// <summary>
    /// ファウルの球で解決した盗塁は SB/CS に残らない（記録の巻き戻し）。
    ///
    /// <para>強制発動にファウルが無いので、一塁走者だけの場面から初球に盗塁を予約し、
    /// <b>初球がファウルになるシード</b>を探して狙い撃ちする（決定論なので毎回同じシードが当たる）。</para>
    /// </summary>
    [Fact]
    public void StealRecord_IsErasedWhenPitchIsFoul()
    {
        var hits = 0;
        for (ulong seed = 1; seed <= 400 && hits < 5; seed++)
        {
            var prog = new MatchProgression(Team("A"), Team("H"),
                new GameContext { CaptureTimelines = true, CapturePitches = true }, seed,
                new KokoSim.Engine.Debugging.ScenarioStart
                {
                    Inning = 1, IsTop = true, Outs = 0, OnFirst = true,
                });
            if (prog.AdvancePitch() != AdvancePitchResult.Pitch) continue;
            prog.SetStealAttempt(offenseIsAway: true, StartType.Gamble, StealTarget.Second);
            prog.AdvancePitch();

            var lp = prog.LastPitch;
            if (lp is null || lp.Pitch.Kind != PitchKind.Foul) continue;
            hits++;

            // ファウル＝ボールデッド。走者は一塁のまま、アウトも増えず、SB/CS も付かない。
            Assert.True(lp.BaseFirst, $"seed {seed}: 走者が一塁へ戻っていない");
            Assert.False(lp.BaseSecond, $"seed {seed}: 盗塁が成立したままになっている");
            Assert.Equal(0, lp.Outs);

            while (prog.AdvancePitch() != AdvancePitchResult.Finished) { }
            var r = prog.BuildResult();
            Assert.Equal(0, r.AwayTactics.StealAttempts);
            Assert.Equal(0, r.AwayTactics.StealSuccesses);
        }
        Assert.True(hits >= 5, $"初球がファウルになるシードが足りない（{hits} 件）");
    }

    /// <summary>
    /// 本盗がファウルの球と重なっても得点は入らない（三塁走者は三塁のまま・アウトも増えない）。
    ///
    /// <para>本盗は「三塁のみ在塁」でしか走らず自然発生を待てないので、場面ジャンプで三塁走者を置き、
    /// 初球がファウルになるシードを探して狙い撃ちする。</para>
    /// </summary>
    [Fact]
    public void StealHome_NeverScoresOnFoul()
    {
        var hits = 0;
        for (ulong seed = 1; seed <= 400 && hits < 5; seed++)
        {
            var prog = new MatchProgression(Team("A"), Team("H"),
                new GameContext { CaptureTimelines = true, CapturePitches = true }, seed,
                new KokoSim.Engine.Debugging.ScenarioStart
                {
                    Inning = 1, IsTop = true, Outs = 0, OnThird = true,
                });
            if (prog.AdvancePitch() != AdvancePitchResult.Pitch) continue;
            prog.SetStealAttempt(offenseIsAway: true, StartType.Gamble, StealTarget.Home);
            prog.AdvancePitch();

            var lp = prog.LastPitch;
            if (lp is null || lp.Pitch.Kind != PitchKind.Foul) continue;
            hits++;

            Assert.True(lp.BaseThird, $"seed {seed}: 三塁走者が戻っていない");
            Assert.Equal(0, lp.Outs);
            Assert.Null(lp.StealPlay);

            while (prog.AdvancePitch() != AdvancePitchResult.Finished) { }
            var r = prog.BuildResult();
            // 得点は三塁走者が本塁へ到達したときにしか入らないので、
            // 「走者が三塁のまま」＝この球で得点が入っていない、と同値。
            Assert.Equal(0, r.AwayTactics.StealAttempts);
        }
        Assert.True(hits >= 5, $"初球がファウルになるシードが足りない（{hits} 件）");
    }
}
