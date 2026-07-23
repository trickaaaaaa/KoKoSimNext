using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Nation.Tournaments;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// エース温存（issue #42）が大会進行（TournamentRunner）＋裏試合フルシム（#43 BackgroundMatchResolver）へ
/// 実際に配線されていることの統合検証。校ID＋校風にかかわらず全国AI校（scope B, オーナー2026-07-23確定）に
/// 適用されること、決勝ではもう温存する先がなく常時エースへ戻ることを確認する。
/// </summary>
public sealed class TournamentRunnerAceRestTests
{
    [Fact]
    public void ForcedAceRest_AppliesInEarlierRounds_ButNeverInTheFinal()
    {
        var schools = new List<School>();
        for (var i = 0; i < 8; i++)
        {
            schools.Add(new School
            {
                Id = 900 + i, Name = $"校{900 + i}", PrefectureId = 13, Strength = 50 + i,
            });
        }

        var deps = new AiRosterDeps { EnemyAi = new EnemyAiCoefficients { AceRestFloor = 1.0, AceRestCap = 1.0 } };
        var rosters = new NationRosters(deps);

        // 校ID＋PlayNextPlayerMatch の呼び出し粒度（自校敗退後は残り全ラウンドが1回で背景消化される）に
        // 依らないよう、onMatch は解決順のフラットな列で捕捉し、既知のブラケット構造（8校・不戦勝なし＝
        // 4→2→1試合）で後からラウンドに切り分ける。ResolveRound はラウンドを跨いで呼び出し順が保たれる。
        var startersInOrder = new List<int>();
        void OnMatch(int awayId, int homeId, GameResult r)
        {
            startersInOrder.Add(r.AwayPitching[0].UniformNumber);
            startersInOrder.Add(r.HomePitching[0].UniformNumber);
        }

        var bg = new BackgroundMatchResolver(rosters, new GameContext(), yearIndex: 1, onMatch: OnMatch);
        var schedule = new TournamentSchedule { FirstRoundDay = 1, RoundGapDays = 3 };
        var runner = new TournamentRunner(
            schools, schools[0], new NationCoefficients(), new Xoshiro256Random(123),
            schedule, "テスト大会", playerResolver: null, backgroundResolver: bg);

        // playerResolver 無し＝自校カードも背景フルシムで解決される＝全国AI校と同じ経路（scope B）。
        while (!runner.Finished) runner.PlayNextPlayerMatch();

        Assert.Equal(14, startersInOrder.Count);   // 7試合(4+2+1)×2校分の先発背番号
        var round1 = startersInOrder.GetRange(0, 8);
        var round2 = startersInOrder.GetRange(8, 4);
        var final = startersInOrder.GetRange(12, 2);
        Assert.All(round1, j => Assert.NotEqual(AceRestSelector.AceUniformNumber, j));
        Assert.All(round2, j => Assert.NotEqual(AceRestSelector.AceUniformNumber, j));
        // 決勝はこの先温存する意味がない（roundsAfter=0）＝常時エース(背番号1)へ戻る。
        Assert.All(final, j => Assert.Equal(AceRestSelector.AceUniformNumber, j));
    }
}
