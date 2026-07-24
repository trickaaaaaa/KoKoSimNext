using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 設計書15 Phase D-2e（旧 OPEN-QUESTIONS.md Q13）の合否テスト。打席途中の3アウト目（スクイズ挟殺・
/// 盗塁死・牽制死）で打席が未決着のまま終わる場合、<c>offense.NextBatter()</c> を「打席が実際に確定した
/// 時点」まで遅らせたことで、中断された打者自身が次にこの打者が打席へ来る時（＝次イニングの先頭）に
/// 必ず立つ（カウントは新規 AtBatSession のため自動的に0-0リセット）。
/// 副産物として見つかった旧実装のバグ（犠打/スクイズが決着する打席でも offense.NextBatter() を二重に
/// 呼んでおり、決着のたびに打者を1人分余計に飛ばしていた）も同じ変更で解消しており、ここで検出できる。
/// </summary>
public sealed class BattingOrderContinuationTests
{
    private static Player Pos(FieldPosition pos, int bunt = 50, int speed = 50, int steal = 50) => new()
        { Position = pos, Contact = 50, Power = 50, Bunt = bunt, Speed = speed, Steal = steal };

    private static Team TeamOf(string name, ITacticsBrain? tactics = null)
    {
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase, speed: 85, steal: 85),
            Pos(FieldPosition.SecondBase, speed: 85, steal: 85), Pos(FieldPosition.ThirdBase),
            Pos(FieldPosition.Shortstop), Pos(FieldPosition.LeftField),
            Pos(FieldPosition.CenterField, bunt: 70, speed: 85, steal: 85), Pos(FieldPosition.RightField),
            new() { Name = name + "P", Position = FieldPosition.Pitcher, Pitching = PitcherAttributes.LeagueAverage },
        };
        return new Team { Name = name, BattingOrder = order, PitcherSlot = 8, Tactics = tactics };
    }

    private sealed class AlwaysSqueezeBrain : ITacticsBrain
    {
        public OffensiveSign CallOffense(in TacticsSituation s, IRandomSource rng) => OffensiveSign.Squeeze;
        public DefensiveTactics CallDefense(in TacticsSituation s, IRandomSource rng) => DefensiveTactics.Default;
        public bool CallOffenseTimeout(in TacticsSituation s, IRandomSource rng) => false;
        public bool CallDefenseTimeout(in TacticsSituation s, IRandomSource rng) => false;
        public Player? CallPinchHit(in SubstitutionSituation s, IRandomSource rng) => null;
        public (Player Runner, Player Sub)? CallPinchRun(in SubstitutionSituation s, IRandomSource rng) => null;
        public (Player Out, Player Sub)? CallDefensiveSub(in SubstitutionSituation s, IRandomSource rng) => null;
        public PitchingChangeDecision? CallPitchingChange(in PitchingChangeSituation s, IRandomSource rng)
            => s.FatigueTriggered || s.AtWeeklyLimit ? new PitchingChangeDecision(PitchingChangeReason.Fatigue) : null;
    }

    /// <summary>毎球、一塁に走者がいれば無条件で通常始動の盗塁を試みるブレイン。</summary>
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
    /// 常時スクイズ/盗塁指示のチームで複数試合流し、指示側チームの打順が試合を通して完全な周期列
    /// （1→2→…→9→1→…）になっていることを固定する。挟殺・盗塁死・牽制死で打席が未決着のまま3アウト目を
    /// 迎えた半イニングでも、次にこの打者が来る時（次イニング先頭）に打順が飛ばないことをここで検出する
    /// （修正前は中断時に1人飛ばし、犠打/スクイズが決着した打席でも別経路で1人飛ばしていた＝いずれも
    /// prev→gotのギャップとして現れるためここで捕捉できる）。
    /// </summary>
    [Theory]
    [InlineData(true)]  // スクイズ
    [InlineData(false)] // 盗塁
    public void BattingOrder_IsPerfectlyCyclic_AcrossWholeGame(bool squeeze)
    {
        var totalTransitions = 0;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var rng = new Xoshiro256Random(seed);
            ITacticsBrain brain = squeeze ? new AlwaysSqueezeBrain() : new AlwaysStealBrain();
            var home = TeamOf("H", brain);
            var away = TeamOf("A");
            var r = GameEngine.Play(away, home, new GameContext(), rng);

            var last = -1;
            foreach (var e in r.Log)
            {
                if (!e.IsTop) // 後攻=指示側チーム（home）の打席だけを追う
                {
                    if (last != -1)
                    {
                        var expected = last % 9 + 1;
                        Assert.True(e.BatterOrder == expected,
                            $"seed={seed}: 打順が飛んだ（prev={last}, got={e.BatterOrder}, expected={expected}）");
                        totalTransitions++;
                    }
                    last = e.BatterOrder;
                }
            }
        }
        Assert.True(totalTransitions > 0, "打順の連続性を検証できるログが1件も無かった");
    }

    /// <summary>
    /// 打席はすべて新規 AtBatSession から始まるため、中断された打者が次に打席へ立つ時（次イニング先頭）も
    /// 必ず0-0から始まる（保存されたカウントを引き継がない）ことを、実際にログへ残った各打席の初球記録で確認する。
    /// </summary>
    [Fact]
    public void EveryLoggedPlateAppearance_StartsFromFreshCount()
    {
        var rng = new Xoshiro256Random(11);
        var home = TeamOf("H", new AlwaysSqueezeBrain());
        var away = TeamOf("A");
        var r = GameEngine.Play(away, home, new GameContext(), rng);

        var checkedAny = false;
        foreach (var e in r.Log)
        {
            if (e.PitchLog is not { Count: > 0 } pitches) continue;
            var first = pitches[0];
            Assert.True(first.BallsAfter <= 1 && first.StrikesAfter <= 1,
                $"初球のはずがカウントが持ち越されている（BallsAfter={first.BallsAfter}, StrikesAfter={first.StrikesAfter}）");
            checkedAny = true;
        }
        Assert.True(checkedAny, "検証対象の打席ログが1件も無かった");
    }

    /// <summary>
    /// スクイズ指示チームでも、保存（シード＋確定打席数）→復元→続行の結果が中断なし実行と完全一致する
    /// （打席途中の中断＝squeezeAbandoned を跨いだ保存点を含む複数の confirmedPa で確認）。
    /// </summary>
    [Theory]
    [InlineData(1UL, 3)]
    [InlineData(5UL, 10)]
    [InlineData(9UL, 20)]
    public void SaveRestoreContinue_WithSqueezeBrain_MatchesUninterrupted(ulong seed, int confirmedPa)
    {
        GameResult Play(ulong s) => GameEngine.Play(
            TeamOf("A"), TeamOf("H", new AlwaysSqueezeBrain()), new GameContext(), new Xoshiro256Random(s));

        var full = GameResultDigest.Sha256Of(Play(seed));
        var save = new GameSaveState(seed, confirmedPa);
        var resumed = GameReplay.Restore(
            TeamOf("A"), TeamOf("H", new AlwaysSqueezeBrain()), new GameContext(), save);
        while (resumed.Steps.MoveNext()) { }
        var continued = GameResultDigest.Sha256Of(GameEngine.BuildResult(resumed.Progress));

        Assert.Equal(full, continued);
    }
}
