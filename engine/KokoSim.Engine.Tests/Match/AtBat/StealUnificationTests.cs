using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.AtBat;

/// <summary>
/// 設計書15 Phase D-2d の合否テスト。盗塁/牽制/重盗を GameEngine の毎球ループへ統一したことで、
/// (1) 盗塁の「試みるか」判定が打席頭一度きり（旧 CallOffense/CallStartType）ではなく毎球の
///     独立試行（IPitchTacticsBrain.CallPitchAction）になり、最初の1球で試みない場合でも後続の球で
///     発動しうる（＝旧アーキテクチャでは不可能だったタイミング）、
/// (2) 解決式（PickoffResolver/StealReadModel/StealResolver/重盗ロール）は変更なしのまま機能し、
///     試合が例外なく最後まで進む（塁状況が壊れて破綻しないことの回帰確認）。
/// </summary>
public sealed class StealUnificationTests
{
    private static Player Pos(FieldPosition pos, int speed = 50, int steal = 50) => new()
        { Position = pos, Contact = 50, Power = 50, Speed = speed, Steal = steal };

    private static Team TeamOf(string name, ITacticsBrain? tactics = null)
    {
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase, speed: 85, steal: 85),
            Pos(FieldPosition.SecondBase, speed: 85, steal: 85), Pos(FieldPosition.ThirdBase),
            Pos(FieldPosition.Shortstop), Pos(FieldPosition.LeftField),
            Pos(FieldPosition.CenterField, speed: 85, steal: 85), Pos(FieldPosition.RightField),
            new() { Name = name + "P", Position = FieldPosition.Pitcher, Pitching = PitcherAttributes.LeagueAverage },
        };
        return new Team { Name = name, BattingOrder = order, PitcherSlot = 8, Tactics = tactics };
    }

    /// <summary>
    /// 一塁走者・二塁空きの間、常に盗塁を試みるブレイン（打席頭以外の球でも毎回問い合わせられる）。
    /// </summary>
    private sealed class AlwaysStealBrain : ITacticsBrain, IPitchTacticsBrain
    {
        public OffensiveSign CallOffense(in TacticsSituation s, IRandomSource rng) => OffensiveSign.Swing;
        public DefensiveTactics CallDefense(in TacticsSituation s, IRandomSource rng) => DefensiveTactics.Default;
        public bool CallOffenseTimeout(in TacticsSituation s, IRandomSource rng) => false;
        public bool CallDefenseTimeout(in TacticsSituation s, IRandomSource rng) => false;
        public Player? CallPinchHit(in SubstitutionSituation s, IRandomSource rng) => null;
        public (Player Runner, Player Sub)? CallPinchRun(in SubstitutionSituation s, IRandomSource rng) => null;
        public (Player Out, Player Sub)? CallDefensiveSub(in SubstitutionSituation s, IRandomSource rng) => null;
        public PitchingChangeDecision? CallPitchingChange(in PitchingChangeSituation s, IRandomSource rng)
            => s.FatigueTriggered || s.AtWeeklyLimit ? new PitchingChangeDecision(PitchingChangeReason.Fatigue) : null;

        public PitchTacticsDirective? CallPitchAction(in PitchTacticsSituation s, IRandomSource rng)
            => s.Base.OnFirst is not null && s.Base.OnSecond is null
                ? new PitchTacticsDirective(StealAttempt: StartType.Normal)
                : null;
    }

    /// <summary>
    /// 一塁走者・二塁空きの間、打席3球目以降（PitchNumber&gt;=2）でしか盗塁を試みないブレイン。
    /// 旧アーキテクチャ（打席頭一度きりの判定）では絶対に発動しなかったタイミングでの発動を固定する。
    /// </summary>
    private sealed class DelayedStealBrain : ITacticsBrain, IPitchTacticsBrain
    {
        public OffensiveSign CallOffense(in TacticsSituation s, IRandomSource rng) => OffensiveSign.Swing;
        public DefensiveTactics CallDefense(in TacticsSituation s, IRandomSource rng) => DefensiveTactics.Default;
        public bool CallOffenseTimeout(in TacticsSituation s, IRandomSource rng) => false;
        public bool CallDefenseTimeout(in TacticsSituation s, IRandomSource rng) => false;
        public Player? CallPinchHit(in SubstitutionSituation s, IRandomSource rng) => null;
        public (Player Runner, Player Sub)? CallPinchRun(in SubstitutionSituation s, IRandomSource rng) => null;
        public (Player Out, Player Sub)? CallDefensiveSub(in SubstitutionSituation s, IRandomSource rng) => null;
        public PitchingChangeDecision? CallPitchingChange(in PitchingChangeSituation s, IRandomSource rng)
            => s.FatigueTriggered || s.AtWeeklyLimit ? new PitchingChangeDecision(PitchingChangeReason.Fatigue) : null;

        public PitchTacticsDirective? CallPitchAction(in PitchTacticsSituation s, IRandomSource rng)
            => s.PitchNumber >= 2 && s.Base.OnFirst is not null && s.Base.OnSecond is null
                ? new PitchTacticsDirective(StealAttempt: StartType.Normal)
                : null;
    }

    /// <summary>
    /// 常時盗塁指示のチームで複数試合流すと、企図が実際に記録され（配線が生きている）、かつ試合が
    /// 例外なく最後まで進む（毎球ループへの移設で塁状況が壊れないことの回帰確認）。
    /// </summary>
    [Fact]
    public void AlwaysSteal_RecordsStealsAndCompletesGamesAcrossSeeds()
    {
        var totalSteals = 0;
        for (ulong seed = 1; seed <= 30; seed++)
        {
            var rng = new Xoshiro256Random(seed);
            var home = TeamOf("H", new AlwaysStealBrain());
            var away = TeamOf("A");
            var r = GameEngine.Play(away, home, new GameContext(), rng);
            totalSteals += r.HomeTactics.StealAttempts;
        }
        Assert.True(totalSteals > 0, "常時盗塁指示なのに一度も企図が記録されなかった（配線切れの疑い）");
    }

    /// <summary>
    /// 打席3球目以降でしか試みないブレインでも盗塁企図が記録される＝設計書15 Phase D-2d の狙いどおり
    /// 「任意の球の前」で発動しうる（旧: 打席頭一度きりの判定では到達不能だったタイミング）。
    /// </summary>
    [Fact]
    public void DelayedSteal_StillFiresOnLaterPitches_AndGamesComplete()
    {
        var totalSteals = 0;
        for (ulong seed = 1; seed <= 60; seed++)
        {
            var rng = new Xoshiro256Random(seed);
            var home = TeamOf("H", new DelayedStealBrain());
            var away = TeamOf("A");
            var r = GameEngine.Play(away, home, new GameContext(), rng);
            totalSteals += r.HomeTactics.StealAttempts;
        }
        Assert.True(totalSteals > 0, "3球目以降限定の盗塁指示が一度も発動しなかった（毎球再判定が機能していない疑い）");
    }
}
