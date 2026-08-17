using System.Linq;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 育成レイヤ→試合用レパートリー（<see cref="RosterTeamBuilder.BuildRepertoire"/>, design-02 §2.2）。
/// 球種チャート（Unity）もこの公開APIをそのまま読むので、ここが「球種ごとのランク」の唯一の定義になる。
/// 旧UIは「50＋オフセット×2」で独自に出しておりキレEの投手にSのチェンジアップが付いた（2026-08-17）。
/// </summary>
public sealed class RosterRepertoireTests
{
    private static DevelopingPlayer PlayerWith(int pitchRank, int stuff, params LearnedPitch[] pitches)
    {
        var p = new DevelopingPlayer { Name = "投手" };
        p.SetLevel(AbilityKind.PitchRank, pitchRank);
        p.SetLevel(AbilityKind.Stuff, stuff);
        p.LearnedPitches.AddRange(pitches);
        return p;
    }

    [Fact]
    public void BuildRepertoire_BreakingBallSharpness_IsPitchRankPlusOffset()
    {
        // キレE級（30）＋チェンジアップのオフセット +15 → 実効45（Cの下）。決して S にはならない。
        var p = PlayerWith(30, 70, new LearnedPitch(PitchType.Changeup, 0, 15), new LearnedPitch(PitchType.Curve, 0, -10));

        var rep = RosterTeamBuilder.BuildRepertoire(p, p.Level(AbilityKind.PitchRank), p.Level(AbilityKind.Stuff));

        var change = rep.Single(s => s.Type == PitchType.Changeup);
        var curve = rep.Single(s => s.Type == PitchType.Curve);
        Assert.Equal(45, change.Sharpness);
        Assert.Equal(45, change.Rank);      // 変化球の表示ランク＝Sharpness
        Assert.Equal(20, curve.Sharpness);
    }

    [Fact]
    public void BuildRepertoire_FastballFirst_UsesStuffAsRank()
    {
        var p = PlayerWith(30, 70, new LearnedPitch(PitchType.Slider, 3, 3));

        var rep = RosterTeamBuilder.BuildRepertoire(p, p.Level(AbilityKind.PitchRank), p.Level(AbilityKind.Stuff));

        Assert.Equal(PitchType.Fastball, rep[0].Type);
        Assert.Equal(70, rep[0].Rank);       // ストレートは球威(Stuff)が有効軸（Issue #274）
        Assert.Equal(2, rep.Count);
    }

    [Fact]
    public void BuildRepertoire_ClampsTo1To100()
    {
        var p = PlayerWith(95, 50, new LearnedPitch(PitchType.Fork, 0, 20), new LearnedPitch(PitchType.Sinker, 0, -20));
        p.SetLevel(AbilityKind.PitchRank, 95);

        var rep = RosterTeamBuilder.BuildRepertoire(p, 95, 50);

        Assert.Equal(100, rep.Single(s => s.Type == PitchType.Fork).Sharpness);
        Assert.Equal(75, rep.Single(s => s.Type == PitchType.Sinker).Sharpness);
        var low = RosterTeamBuilder.BuildRepertoire(p, 5, 50);
        Assert.Equal(1, low.Single(s => s.Type == PitchType.Sinker).Sharpness);
    }
}
