using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 1球ぶんの観測（<see cref="LivePitch"/>・設計書15 Phase D-1 のUI採用）の受け入れ。
///
/// <para>試合画面を打席単位から1球単位へ移すための供給口。守るべき性質は2つ:
/// ①<b>観測でしかない</b>——ゲートの ON/OFF で試合結果が1ビットも変わらない
/// ②<b>同じ球を2通りに描かない</b>——1球ずつ受け取った列が、打席確定時の
/// <see cref="LivePlateAppearance.PitchSeq"/> と一致する。</para>
/// </summary>
public sealed class LivePitchStreamTests
{
    /// <summary>ゲートを開けても試合は変わらない（観測は乱数を1回も消費しない）。</summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    [InlineData(42UL)]
    public void CapturePitches_DoesNotChangeTheGame(ulong seed)
    {
        var off = new MatchProgression(Team("A"), Team("H"), new GameContext(), seed);
        while (off.Advance()) { }
        var on = new MatchProgression(Team("A"), Team("H"), new GameContext { CapturePitches = true }, seed);
        while (on.Advance()) { }

        Assert.Equal(GameResultDigest.Sha256Of(off.BuildResult()), GameResultDigest.Sha256Of(on.BuildResult()));
    }

    /// <summary>ゲートが閉じているあいだは1球も載らない（統計シム・裏試合のゼロコストの担保）。</summary>
    [Fact]
    public void CapturePitches_Off_LeavesLastPitchNull()
    {
        var prog = new MatchProgression(Team("A"), Team("H"), new GameContext(), 42UL);
        var seen = 0;
        while (true)
        {
            var r = prog.AdvancePitch();
            if (r == AdvancePitchResult.Finished) break;
            if (prog.LastPitch != null) seen++;
        }
        Assert.Equal(0, seen);
    }

    /// <summary>
    /// 1球ずつ受け取った列＝打席確定時の投球列。<b>ファウルの打球再生（FoulFlight）だけは別扱い</b>——
    /// 走者は打席途中に盗塁で動くので、1球ごとの版はその時点の塁、打席確定時の版は打球解決直前の塁を載せる。
    /// 判定に使う値ではなく絵の走者位置なので、ここでは「有無が一致すること」だけを見る。
    /// </summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    [InlineData(42UL)]
    public void PitchByPitchStream_MatchesConfirmedPitchSequence(ulong seed)
    {
        var prog = new MatchProgression(Team("A"), Team("H"),
            new GameContext { CapturePitches = true }, seed);

        var streamed = new List<PitchToken>();
        var comparedPa = 0;
        var comparedPitches = 0;
        while (true)
        {
            var before = prog.LastPitch;
            var r = prog.AdvancePitch();
            if (r == AdvancePitchResult.Finished) break;

            // 采配窓を1つ抜けた＝1つ前の球が投げ終わっている。同じ球を二度積まないよう参照で見る。
            var last = prog.LastPitch;
            if (last != null && !ReferenceEquals(last, before)) streamed.Add(last.Pitch);

            if (r != AdvancePitchResult.PlateAppearance) continue;

            var confirmed = prog.Current!.PitchSeq.Pitches;
            // 敬遠など1球も投げない打席は投球列が空。合成列（PitchSequenceSynthesizer）へ落ちた打席も
            // 実記録が無いので比較対象から外す（実データのある打席だけを突き合わせる）。
            if (confirmed.Count == streamed.Count && confirmed.Count > 0)
            {
                for (var i = 0; i < confirmed.Count; i++)
                {
                    var a = confirmed[i];
                    var b = streamed[i];
                    Assert.Equal(a.Kind, b.Kind);
                    Assert.Equal(a.BallsAfter, b.BallsAfter);
                    Assert.Equal(a.StrikesAfter, b.StrikesAfter);
                    Assert.Equal(a.PitchType, b.PitchType);
                    Assert.Equal(a.VelocityKmh, b.VelocityKmh);
                    Assert.Equal(a.LocationX, b.LocationX);
                    Assert.Equal(a.LocationY, b.LocationY);
                    Assert.Equal(a.FoulFlight is null, b.FoulFlight is null);
                    comparedPitches++;
                }
                comparedPa++;
            }
            streamed.Clear();
        }

        Assert.True(comparedPa > 20, $"突き合わせた打席が少なすぎる（{comparedPa}）");
        Assert.True(comparedPitches > 60, $"突き合わせた球数が少なすぎる（{comparedPitches}）");
    }

    /// <summary>1球ごとの観測は、その球の時点の局面（回・表裏・アウト・カウント）を正しく載せている。</summary>
    [Fact]
    public void LivePitch_CarriesTheSituationOfThatPitch()
    {
        var prog = new MatchProgression(Team("A"), Team("H"),
            new GameContext { CapturePitches = true }, 42UL);

        LivePitch? prev = null;
        var checkedPitches = 0;
        while (true)
        {
            var r = prog.AdvancePitch();
            if (r == AdvancePitchResult.Finished) break;
            var last = prog.LastPitch;
            if (last == null || ReferenceEquals(last, prev)) continue;
            prev = last;

            Assert.InRange(last.Outs, 0, 2);
            Assert.InRange(last.Pitch.BallsAfter, 0, 4);
            Assert.InRange(last.Pitch.StrikesAfter, 0, 3);
            Assert.InRange(last.BatterOrder, 1, 9);
            Assert.False(string.IsNullOrEmpty(last.BatterName));
            Assert.False(string.IsNullOrEmpty(last.PitcherName));
            Assert.True(last.PitchNumberInPa >= 1);
            checkedPitches++;
        }
        Assert.True(checkedPitches > 100, $"検査した球が少なすぎる（{checkedPitches}）");
    }

    private static Team Team(string name)
    {
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase), Pos(FieldPosition.SecondBase),
            Pos(FieldPosition.ThirdBase), Pos(FieldPosition.Shortstop), Pos(FieldPosition.LeftField),
            Pos(FieldPosition.CenterField), Pos(FieldPosition.RightField),
            Pos(FieldPosition.Pitcher) with { Name = name + "P", Pitching = PitcherAttributes.LeagueAverage },
        };
        return new Team { Name = name, BattingOrder = order, PitcherSlot = 8 };
    }

    private static Player Pos(FieldPosition p) => new()
    {
        Name = p.ToString(),
        Position = p,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50, Speed = 50,
    };
}
