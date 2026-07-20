using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Tactics;

/// <summary>
/// A-2: 敵AI采配（AiTacticsBrain）をフル采配の実試合へ配線（設計書11 §7）。
/// Team.Tactics に敵AIブレインを差した試合が回り、校風どおりの采配活動（盗塁等）を行うこと、
/// 無指示なら従来どおり采配ゼロ、決定論、集計モデル（StrengthTeamFactory）は Tactics=null 維持、を検証する。
/// </summary>
public sealed class AiGameWiringTests
{
    private static readonly NationCoefficients NationCoeff = new();

    private static School StyledSchool(SchoolStyle style, double strength = 70, int sense = 92)
        => new() { Id = 1, Name = "AI校", PrefectureId = 0, Strength = strength, Style = style, TacticalSense = sense };

    // away=無指示の対戦相手, home=校風付き敵AI。N試合の home 側采配集計を返す。
    private static (int Steals, int Sacs, int Squeezes, int HomeWins) PlaySeries(SchoolStyle style, int games, ulong seed0)
    {
        var ctx = new GameContext();
        int steals = 0, sacs = 0, squeezes = 0, wins = 0;
        for (ulong g = 0; g < (ulong)games; g++)
        {
            var rng = new Xoshiro256Random(seed0 + g);
            var away = StrengthTeamFactory.Create(52, "相手", rng);
            var home = StrengthTeamFactory.Create(52, "AI校", rng) with
            {
                Tactics = EnemyAiFactory.BrainFor(StyledSchool(style)),
            };
            var r = GameEngine.Play(away, home, ctx, rng);
            steals += r.HomeTactics.StealAttempts;
            sacs += r.HomeTactics.SacrificeBunts;
            squeezes += r.HomeTactics.Squeezes;
            if (r.HomeWon) wins++;
        }
        return (steals, sacs, squeezes, wins);
    }

    [Fact]
    public void AiBrain_DrivesTacticsInRealGames()
    {
        // 機動力校はフル采配の試合で実際に小技（盗塁・犠打）を仕掛ける＝配線が効いている。
        var m = PlaySeries(SchoolStyle.SmallBall, games: 40, seed0: 100);
        var total = m.Steals + m.Sacs + m.Squeezes;
        Assert.True(total >= 6, $"機動力AIの小技が少なすぎる（配線未達の疑い）: 盗塁{m.Steals}/犠打{m.Sacs}/スクイズ{m.Squeezes}");
    }

    [Fact]
    public void SchoolStyle_ShapesTacticalActivity()
    {
        // 校風で采配の質が変わる: 機動力は強打待球より明確に小技を多用する（設計書11 §3）。
        var mob = PlaySeries(SchoolStyle.SmallBall, games: 60, seed0: 100);
        var pow = PlaySeries(SchoolStyle.PowerHitting, games: 60, seed0: 100);
        var mobTotal = mob.Steals + mob.Sacs + mob.Squeezes;
        var powTotal = pow.Steals + pow.Sacs + pow.Squeezes;
        Assert.True(mobTotal > powTotal, $"機動力({mobTotal}) が強打待球({powTotal}) より小技が多いはず");
        Assert.True(mob.Sacs > pow.Sacs, $"機動力の犠打({mob.Sacs}) が強打待球({pow.Sacs}) を上回るはず");
    }

    [Fact]
    public void NoTactics_RecordsZeroTacticEvents_AndBandUnchanged()
    {
        // 無指示（Tactics=null, EnableSmallBall 既定オフ）＝采配集計は全ゼロ＝従来の試合と完全一致。
        var ctx = new GameContext();
        var rng = new Xoshiro256Random(7);
        var away = StrengthTeamFactory.Create(52, "A", rng);
        var home = StrengthTeamFactory.Create(52, "B", rng);
        Assert.Null(away.Tactics);   // 集計モデルは Tactics=null 維持（Heavy帯保護）
        Assert.Null(home.Tactics);
        var r = GameEngine.Play(away, home, ctx, rng);
        Assert.Equal(0, r.HomeTactics.StealAttempts + r.HomeTactics.SacrificeBunts + r.HomeTactics.Squeezes);
        Assert.Equal(0, r.AwayTactics.StealAttempts + r.AwayTactics.SacrificeBunts + r.AwayTactics.Squeezes);
    }

    [Fact]
    public void AiGame_IsDeterministic()
    {
        var a = PlaySeries(SchoolStyle.SmallBall, games: 12, seed0: 55);
        var b = PlaySeries(SchoolStyle.SmallBall, games: 12, seed0: 55);
        Assert.Equal(a, b);
    }

    [Fact]
    public void RosterTeamBuilder_AttachesTacticsBrain()
    {
        // 配線の入口: ロスター編成に采配ブレインを差せる（自校のフル采配試合の材料）。
        var roster = ProspectGenerator.Intake(1, new RosterCoefficients(), new Xoshiro256Random(3)).ToList();
        var brain = EnemyAiFactory.BrainFor(StyledSchool(SchoolStyle.SmallBall));
        var withTactics = RosterTeamBuilder.Build(roster, "自校", brain);
        var without = RosterTeamBuilder.Build(roster, "自校");
        Assert.Same(brain, withTactics.Tactics);
        Assert.Null(without.Tactics);
    }
}
