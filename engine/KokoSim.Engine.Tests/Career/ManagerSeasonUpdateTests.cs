using KokoSim.Engine.Career;
using KokoSim.Engine.Core;
using Xunit;

namespace KokoSim.Engine.Tests.Career;

/// <summary>
/// 1シーズンぶんの監督メタ更新シーム（issue #171: Unity実プレイの年度替わり配線が呼ぶ純関数）。
/// 自然増＋節目イベントが1回で乗ること・同シード同結果（不変条件#2）を担保する。
/// </summary>
public sealed class ManagerSeasonUpdateTests
{
    [Fact]
    public void Apply_GrowsManagerMeta_ForAWinningKoshienSeason()
    {
        var m = new Manager();
        var before = (m.Fame, m.Trust, m.AverageCoaching, m.TacticalSense, m.CareerYears, m.KoshienAppearances);

        ManagerSeasonUpdate.Apply(m, reachedKoshien: true, nationalChampion: false, wins: 6,
            milestoneRng: new Xoshiro256Random(0xF00DUL));

        Assert.Equal(before.CareerYears + 1, m.CareerYears);
        Assert.Equal(before.KoshienAppearances + 1, m.KoshienAppearances);
        Assert.True(m.Fame > before.Fame, "甲子園到達＋勝利で名声が上がる");
        Assert.True(m.Trust > before.Trust, "勝利＋甲子園で校内信頼が上がる");
        Assert.True(m.AverageCoaching > before.AverageCoaching, "采配経験で指導力が伸びる");
        Assert.True(m.TacticalSense > before.TacticalSense, "甲子園経験で采配が伸びる");
    }

    [Fact]
    public void Apply_PenalizesTrust_OnWinlessSeason()
    {
        var m = new Manager { Trust = 50 };

        ManagerSeasonUpdate.Apply(m, reachedKoshien: false, nationalChampion: false, wins: 0,
            milestoneRng: new Xoshiro256Random(1));

        Assert.True(m.Trust < 50, "未勝利シーズンは信頼度が下がる");
    }

    [Fact]
    public void Apply_IsDeterministic_ForSameSeed()
    {
        Manager Run()
        {
            var m = new Manager();
            for (var year = 0; year < 4; year++)
                ManagerSeasonUpdate.Apply(m, reachedKoshien: year % 2 == 0, nationalChampion: false, wins: 3 + year,
                    milestoneRng: new Xoshiro256Random(0xABCDUL ^ (ulong)year));
            return m;
        }

        var a = Run();
        var b = Run();
        Assert.Equal(a.Fame, b.Fame);
        Assert.Equal(a.Trust, b.Trust);
        Assert.Equal(a.TacticalSense, b.TacticalSense);
        Assert.Equal(a.CoachingBatting, b.CoachingBatting);
        Assert.Equal(a.CoachingPitching, b.CoachingPitching);
        Assert.Equal(a.CoachingDefense, b.CoachingDefense);
        Assert.Equal(a.TalentEye, b.TalentEye);
        Assert.Equal(a.Funds, b.Funds);
    }
}
