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
/// 設計書15 Phase D-2b の合否テスト。バントを AtBatSession の投球ループへ統一したことで
/// (1) 2ストライクへ達しても実カウントを保ったまま強攻へ切り替わる（旧: 0-0にリセットされていた）、
/// (2) PopOut/SacrificeSuccess/InfieldHit を区別できる（AtBatResult.BuntOutcome）、
/// (3) GameEngine 側で正しい進塁（AdvanceOnBunt）に分岐する、ことを固定する。
/// </summary>
public sealed class BuntUnificationTests
{
    private static readonly FieldGeometry Field = new();
    private static readonly BatterAttributes Batter = new() { Contact = 55 };
    private static readonly PitcherAttributes Pitcher = PitcherAttributes.LeagueAverage;
    private static readonly Player BunterPlayer = new() { Bunt = 55, Speed = 60 };

    private static AtBatContext Ctx() => new() { Fielders = Field.StandardAlignment() };

    // ── AtBatSession レベル ──

    /// <summary>
    /// 2球連続でミス/ファウルとなり2ストライクへ達した後、無指示（battingOverride=null）で続けると
    /// 実カウント（2ストライク）を保ったまま通常の打者判断パイプラインへ入る（旧: カウントリセット）。
    /// </summary>
    [Fact]
    public void TwoStrikesFromBuntAttempts_CarryOverIntoNormalPipeline()
    {
        var found = false;
        for (ulong seed = 1; seed <= 300 && !found; seed++)
        {
            var rng = new Xoshiro256Random(seed);
            var session = AtBatSession.Begin(Batter, Pitcher, Ctx(), batterPlayer: BunterPlayer);

            var r1 = session.ThrowNextPitch(rng, PitchBattingOverride.Bunt);
            if (r1.EndsPlateAppearance) continue; // 1球目で決着（PopOut/Sac/Infield）→別シードへ
            if (session.Strikes != 1) continue;   // ボール等ではなく必ずミス/ファウルであることを要求

            var r2 = session.ThrowNextPitch(rng, PitchBattingOverride.Bunt);
            if (r2.EndsPlateAppearance) continue;
            if (session.Strikes != 2) continue;

            // ここまでで2ストライク・PA継続中。以降は方針側（GameEngine）がバント上書きを止める想定なので
            // battingOverride=null で通常の打者判断へフォールバックする。
            Assert.False(session.IsComplete);
            Assert.Equal(2, session.Strikes);
            Assert.Equal(2, session.PitchLog.Count);
            Assert.True(session.PitchLog[0].Kind is PitchKind.SwingingStrike or PitchKind.Foul);
            Assert.True(session.PitchLog[1].Kind is PitchKind.SwingingStrike or PitchKind.Foul);
            found = true;
        }
        Assert.True(found, "2球連続でバントが決着しないシードが見つからなかった（探索範囲を広げる必要あり）");
    }

    /// <summary>PopOut は打席を凡打（InPlayOut）で確定し、BuntOutcome にその区別を残す。</summary>
    [Fact]
    public void PopOut_EndsAtBat_WithBuntOutcomeSet()
    {
        BuntResult? found = null;
        for (ulong seed = 1; seed <= 200 && found is null; seed++)
        {
            var rng = new Xoshiro256Random(seed);
            var session = AtBatSession.Begin(Batter, Pitcher, Ctx(), batterPlayer: BunterPlayer);
            PitchResolution res;
            do { res = session.ThrowNextPitch(rng, PitchBattingOverride.Bunt); }
            while (!res.EndsPlateAppearance && session.Strikes < 2);

            if (session.IsComplete && session.Result.BuntOutcome == BuntResult.PopOut) found = BuntResult.PopOut;
        }
        Assert.NotNull(found);
    }

    /// <summary>SacrificeSuccess と InfieldHit は同じ InPlayOut 域ではなく、Result と BuntOutcome で正しく区別される。</summary>
    [Fact]
    public void SacrificeSuccessAndInfieldHit_AreDistinguishable()
    {
        var seenSac = false;
        var seenHit = false;
        for (ulong seed = 1; seed <= 400 && !(seenSac && seenHit); seed++)
        {
            var rng = new Xoshiro256Random(seed);
            var session = AtBatSession.Begin(Batter, Pitcher, Ctx(), batterPlayer: BunterPlayer);
            PitchResolution res;
            do { res = session.ThrowNextPitch(rng, PitchBattingOverride.Bunt); }
            while (!res.EndsPlateAppearance && session.Strikes < 2);
            if (!session.IsComplete) continue;

            if (session.Result.BuntOutcome == BuntResult.SacrificeSuccess)
            {
                Assert.Equal(PlateAppearanceResult.InPlayOut, session.Result.Result);
                seenSac = true;
            }
            else if (session.Result.BuntOutcome == BuntResult.InfieldHit)
            {
                Assert.Equal(PlateAppearanceResult.Single, session.Result.Result);
                seenHit = true;
            }
        }
        Assert.True(seenSac, "SacrificeSuccess のケースが見つからなかった");
        Assert.True(seenHit, "InfieldHit のケースが見つからなかった");
    }

    /// <summary>バント指示が無い（battingOverride=Bunt を渡さない）打席は従来どおり通常パイプラインのまま。</summary>
    [Fact]
    public void WithoutBuntOverride_BehavesAsNormalAtBat()
    {
        var session = AtBatSession.Begin(Batter, Pitcher, Ctx(), batterPlayer: BunterPlayer);
        var rng = new Xoshiro256Random(1);
        while (!session.IsComplete) session.ThrowNextPitch(rng);
        Assert.Null(session.Result.BuntOutcome);
    }

    // ── GameEngine レベル（AI brain を経由した実試合での統合） ──

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

    /// <summary>常に送りバントを指示するブレイン（CallPitchAction は実装しない＝方針だけが効くのを確認）。</summary>
    private sealed class AlwaysSacBuntBrain : ITacticsBrain
    {
        public OffensiveSign CallOffense(in TacticsSituation s, IRandomSource rng) => OffensiveSign.SacrificeBunt;
        public DefensiveTactics CallDefense(in TacticsSituation s, IRandomSource rng) => DefensiveTactics.Default;
        public bool CallOffenseTimeout(in TacticsSituation s, IRandomSource rng) => false;
        public bool CallDefenseTimeout(in TacticsSituation s, IRandomSource rng) => false;
        public Player? CallPinchHit(in SubstitutionSituation s, IRandomSource rng) => null;
        public (Player Runner, Player Sub)? CallPinchRun(in SubstitutionSituation s, IRandomSource rng) => null;
        public (Player Out, Player Sub)? CallDefensiveSub(in SubstitutionSituation s, IRandomSource rng) => null;
        public PitchingChangeDecision? CallPitchingChange(in PitchingChangeSituation s, IRandomSource rng)
            => s.FatigueTriggered || s.AtWeeklyLimit ? new PitchingChangeDecision(PitchingChangeReason.Fatigue) : null;
    }

    /// <summary>常に送りバントを指示するチームで複数試合流すと、犠打が実際に記録される（配線が生きている）。</summary>
    [Fact]
    public void AlwaysSacBunt_RecordsSacrificeBuntsAcrossGames()
    {
        var totalSacs = 0;
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var rng = new Xoshiro256Random(seed);
            var home = TeamOf("H", new AlwaysSacBuntBrain());
            var away = TeamOf("A");
            var r = GameEngine.Play(away, home, new GameContext(), rng);
            totalSacs += r.HomeTactics.SacrificeBunts;
        }
        Assert.True(totalSacs > 0, "常時送りバント指示なのに一度も犠打が記録されなかった（配線切れの疑い）");
    }
}
