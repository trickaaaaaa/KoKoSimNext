using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.AtBat;

/// <summary>
/// 設計書15 Phase D-2c の合否テスト。スクイズを AtBatSession の投球ループへ統一したことで
/// (1) 三塁走者が挟殺される（RunnerOut=true）場合は理由（ウエストを読まれた／送りバント自体が
///     Foul・MissedBuntで不成立）を問わず必ず打席を確定させず打者続行させる（<see cref="PitchResolution.SqueezeRunnerCaughtAtThird"/>）、
/// (2) SacrificeSuccess/InfieldHit/PopOut は <see cref="AtBatResult.Squeeze"/> で打席を確定・区別できる、
/// (3) 実球（PitchSelection→ControlScatter）を経て実PitchLogが残る、
/// (4) GameEngine 側で bases.Third の消去（生還時のみ）と進塁が正しく分岐する、ことを固定する。
/// </summary>
public sealed class SqueezeUnificationTests
{
    private static readonly FieldGeometry Field = new();
    private static readonly BatterAttributes Batter = new() { Contact = 55 };
    private static readonly PitcherAttributes Pitcher = PitcherAttributes.LeagueAverage;
    private static readonly Player Bunter = new() { Bunt = 55, Speed = 60 };
    private static readonly Player ThirdRunner = new() { Speed = 55 };

    private static AtBatContext Ctx() => new() { Fielders = Field.StandardAlignment() };

    // ── AtBatSession レベル ──

    /// <summary>
    /// ウエスト確率1.0（必ず読まれる）なら、打席は確定せず（打者は打席続行）、三塁走者だけが挟殺されたことが
    /// <see cref="PitchResolution.SqueezeRunnerCaughtAtThird"/> で通知される。この球はBallとして実記録される。
    /// </summary>
    [Fact]
    public void WasteAlwaysRead_RunnerCaughtAtThird_ButAtBatContinues()
    {
        var rng = new Xoshiro256Random(1);
        var session = AtBatSession.Begin(Batter, Pitcher, Ctx(), batterPlayer: Bunter,
            thirdBaseRunner: ThirdRunner, squeezeWasteProbability: 1.0);

        var res = session.ThrowNextPitch(rng, PitchBattingOverride.Squeeze);

        Assert.False(res.EndsPlateAppearance);
        Assert.True(res.SqueezeRunnerCaughtAtThird);
        Assert.False(session.IsComplete);
        Assert.Equal(1, session.Balls);
        Assert.Single(session.PitchLog);
        Assert.Equal(PitchKind.Ball, session.PitchLog[0].Kind);

        // 以降は方針（Squeeze）を渡さない＝通常の打者判断パイプラインへ復帰する。
        while (!session.IsComplete) session.ThrowNextPitch(rng);
        Assert.Null(session.Result.Squeeze);
    }

    /// <summary>
    /// ウエスト確率0でも、送りバント自体が Foul/MissedBunt で不成立なら三塁走者は挟殺され（RunnerOut=true）、
    /// これもウエストを読まれた場合と全く同じ「打席は確定せず打者続行」の経路（<see cref="PitchResolution.SqueezeRunnerCaughtAtThird"/>）
    /// を通る（設計書02 §4.4: 挟殺の原因を問わず打席は続く）。
    /// </summary>
    [Fact]
    public void NoWasteButBuntFails_RunnerCaughtAtThird_SameContinuePathAsWaste()
    {
        var found = false;
        for (ulong seed = 1; seed <= 400 && !found; seed++)
        {
            var rng = new Xoshiro256Random(seed);
            var session = AtBatSession.Begin(Batter, Pitcher, Ctx(), batterPlayer: Bunter,
                thirdBaseRunner: ThirdRunner, squeezeWasteProbability: 0.0);
            var res = session.ThrowNextPitch(rng, PitchBattingOverride.Squeeze);
            if (!res.SqueezeRunnerCaughtAtThird) continue;

            Assert.False(res.EndsPlateAppearance);
            Assert.False(session.IsComplete);
            Assert.Equal(1, session.Balls);
            Assert.Equal(PitchKind.Ball, session.PitchLog[0].Kind);
            found = true;
        }
        Assert.True(found, "ウエストなしでバントが不成立（挟殺）になるケースが見つからなかった");
    }

    /// <summary>
    /// 打席が確定する（<see cref="PitchResolution.SqueezeRunnerCaughtAtThird"/> ではない）場合は
    /// SacrificeSuccess/InfieldHit/PopOut のいずれかで、Result（InPlayOut/Single）と Squeeze.Bunt で正しく区別される。
    /// </summary>
    [Fact]
    public void DecidedOutcomes_SacrificeSuccessInfieldHitPopOut_AreDistinguishable()
    {
        var seenSac = false;
        var seenHit = false;
        var seenPop = false;
        for (ulong seed = 1; seed <= 400 && !(seenSac && seenHit && seenPop); seed++)
        {
            var rng = new Xoshiro256Random(seed);
            var session = AtBatSession.Begin(Batter, Pitcher, Ctx(), batterPlayer: Bunter,
                thirdBaseRunner: ThirdRunner, squeezeWasteProbability: 0.0);
            var res = session.ThrowNextPitch(rng, PitchBattingOverride.Squeeze);
            if (res.SqueezeRunnerCaughtAtThird) continue; // 打席未確定（別テストでカバー）

            Assert.True(res.EndsPlateAppearance);
            Assert.True(session.IsComplete);
            Assert.Single(session.PitchLog);
            var sq = session.Result.Squeeze!.Value;
            Assert.False(sq.RunnerOut);

            switch (sq.Bunt)
            {
                case BuntResult.SacrificeSuccess:
                    Assert.Equal(PlateAppearanceResult.InPlayOut, session.Result.Result);
                    Assert.True(sq.BatterOut);
                    Assert.Equal(1, sq.Runs);
                    seenSac = true;
                    break;
                case BuntResult.InfieldHit:
                    Assert.Equal(PlateAppearanceResult.Single, session.Result.Result);
                    Assert.False(sq.BatterOut);
                    Assert.Equal(1, sq.Runs);
                    seenHit = true;
                    break;
                case BuntResult.PopOut:
                    Assert.Equal(PlateAppearanceResult.InPlayOut, session.Result.Result);
                    Assert.True(sq.BatterOut);
                    Assert.Equal(0, sq.Runs);
                    seenPop = true;
                    break;
            }
        }
        Assert.True(seenSac, "SacrificeSuccess のケースが見つからなかった");
        Assert.True(seenHit, "InfieldHit のケースが見つからなかった");
        Assert.True(seenPop, "PopOut のケースが見つからなかった");
    }

    /// <summary>スクイズ指示が無い（battingOverride=Squeeze を渡さない）打席は従来どおり通常パイプラインのまま。</summary>
    [Fact]
    public void WithoutSqueezeOverride_BehavesAsNormalAtBat()
    {
        var session = AtBatSession.Begin(Batter, Pitcher, Ctx(), batterPlayer: Bunter, thirdBaseRunner: ThirdRunner);
        var rng = new Xoshiro256Random(1);
        while (!session.IsComplete) session.ThrowNextPitch(rng);
        Assert.Null(session.Result.Squeeze);
    }

    // ── GameEngine レベル（実試合での統合。bases.Third の消去バグ修正込みの回帰確認） ──

    private static Player Pos(FieldPosition pos, int bunt = 50, int speed = 50) => new()
        { Position = pos, Contact = 50, Power = 50, Bunt = bunt, Speed = speed };

    private static Team TeamOf(string name, ITacticsBrain? tactics = null)
    {
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase), Pos(FieldPosition.SecondBase),
            Pos(FieldPosition.ThirdBase), Pos(FieldPosition.Shortstop), Pos(FieldPosition.LeftField),
            Pos(FieldPosition.CenterField, bunt: 70, speed: 70), Pos(FieldPosition.RightField),
            new() { Name = name + "P", Position = FieldPosition.Pitcher, Pitching = PitcherAttributes.LeagueAverage },
        };
        return new Team { Name = name, BattingOrder = order, PitcherSlot = 8, Tactics = tactics };
    }

    /// <summary>常にスクイズを指示するブレイン（三塁走者がいない場面では GameEngine 側のガードで素通りする）。</summary>
    private sealed class AlwaysSqueezeBrain : ITacticsBrain
    {
        public OffensiveSign CallOffense(in TacticsSituation s, IRandomSource rng) => OffensiveSign.Squeeze;
        public DefensiveTactics CallDefense(in TacticsSituation s, IRandomSource rng) => DefensiveTactics.Default;
        public bool CallOffenseTimeout(in TacticsSituation s, IRandomSource rng) => false;
        public bool CallDefenseTimeout(in TacticsSituation s, IRandomSource rng) => false;
        public Player? CallPinchHit(in SubstitutionSituation s, IRandomSource rng) => null;
        public (Player Runner, Player Sub)? CallPinchRun(in SubstitutionSituation s, IRandomSource rng) => null;
        public (Player Out, Player Sub)? CallDefensiveSub(in SubstitutionSituation s, IRandomSource rng) => null;
    }

    /// <summary>
    /// 常にスクイズを指示するチームで複数試合流すと、犠打企図が実際に記録され（配線が生きている）、かつ試合が
    /// 例外なく最後まで進む（三塁走者アウト時の bases.Third クリア漏れがあれば塁状況が壊れて破綻するはず）。
    /// </summary>
    [Fact]
    public void AlwaysSqueeze_RecordsSqueezesAndCompletesGamesAcrossSeeds()
    {
        var totalSqueezes = 0;
        for (ulong seed = 1; seed <= 30; seed++)
        {
            var rng = new Xoshiro256Random(seed);
            var home = TeamOf("H", new AlwaysSqueezeBrain());
            var away = TeamOf("A");
            var r = GameEngine.Play(away, home, new GameContext(), rng);
            totalSqueezes += r.HomeTactics.Squeezes;
        }
        Assert.True(totalSqueezes > 0, "常時スクイズ指示なのに一度も企図が記録されなかった（配線切れの疑い）");
    }
}
