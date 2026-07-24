using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Tactics;

/// <summary>
/// 在フィールドのレギュラー野手を継投候補に含め、投野交代で守備穴を backfill する（issue #209 Phase C）。
/// 二刀流の主力を緊急登板させ、退く投手をその守備位置へ回す（両者残留・ベンチ非消費・リエントリー禁止不抵触）。
/// Standard／無指示は不変（帯・決定論baseline は EngineDeterminismGateTests が別途担保）。
/// </summary>
public sealed class ReliefBackfillTests
{
    private static PitcherAttributes Attr(int velLevel, int control, int staminaLevel, int pitchRank)
        => new()
        {
            MaxVelocityKmh = PitcherAttributes.VelocityKmhFromLevel(velLevel),
            Control = control,
            StaminaPitches = PitcherAttributes.StaminaPitchesFromLevel(staminaLevel),
            PitchRank = pitchRank,
        };

    private static Player Field(string name, FieldPosition pos, PitcherAttributes? latent = null)
        => new() { Name = name, Position = pos, LatentPitching = latent };

    private static Player Arm(string name, PitcherAttributes a)
        => new() { Name = name, Position = FieldPosition.Pitcher, Pitching = a };

    /// <summary>非DHチーム。遊撃手(遊)に潜在投手能力を持たせ、ブルペンは1枚。</summary>
    private static Team NonDhTeam(string name, PitcherAttributes? ssLatent, PitcherAttributes bullpenAttr)
    {
        var order = new List<Player>
        {
            Field("捕", FieldPosition.Catcher), Field("一", FieldPosition.FirstBase),
            Field("二", FieldPosition.SecondBase), Field("三", FieldPosition.ThirdBase),
            Field("遊", FieldPosition.Shortstop, ssLatent), Field("左", FieldPosition.LeftField),
            Field("中", FieldPosition.CenterField), Field("右", FieldPosition.RightField),
            Arm(name + "先発", PitcherAttributes.LeagueAverage),
        };
        return new Team
        {
            Name = name, BattingOrder = order, PitcherSlot = 8,
            Bullpen = new[] { Arm(name + "中継ぎ", bullpenAttr) },
            Bench = new[] { Field(name + "控", FieldPosition.FirstBase) },
        };
    }

    // ===== 候補プール: 在フィールドのレギュラー野手を含める（非DHのみ） =====

    [Fact]
    public void ReliefCandidates_IncludeOnFieldRegulars_NonDh()
    {
        var st = new TeamState(NonDhTeam("T", Attr(80, 60, 45, 55), Attr(30, 30, 30, 30)));
        var cands = st.ReliefRankingCandidates;

        // ブルペン1 + ベンチ1 + 在フィールド野手8（現投手を除く打順9-1）= 10。
        Assert.Equal(10, cands.Count);
        Assert.Contains(cands, p => p.Name == "遊");
        // 現投手（先発）は候補に含めない。
        Assert.DoesNotContain(cands, p => p.Name == "T先発");
    }

    [Fact]
    public void ReliefCandidates_ExcludeOnFieldRegulars_InDh()
    {
        var order = new List<Player>
        {
            Field("捕", FieldPosition.Catcher), Field("一", FieldPosition.FirstBase),
            Field("二", FieldPosition.SecondBase), Field("三", FieldPosition.ThirdBase),
            Field("遊", FieldPosition.Shortstop, Attr(80, 60, 45, 55)), Field("左", FieldPosition.LeftField),
            Field("中", FieldPosition.CenterField), Field("右", FieldPosition.RightField),
            Field("DH", FieldPosition.Catcher),
        };
        var team = new Team
        {
            Name = "D", BattingOrder = order, DhSlot = 8,
            StartingPitcher = Arm("D先発", PitcherAttributes.LeagueAverage),
            Bullpen = new[] { Arm("D中継ぎ", Attr(30, 30, 30, 30)) },
            Bench = new[] { Field("D控", FieldPosition.FirstBase) },
        };
        var st = new TeamState(team);

        // DH制では在フィールド野手を含めない＝ブルペン1 + ベンチ1 = 2（Phase B 相当）。
        Assert.Equal(2, st.ReliefRankingCandidates.Count);
    }

    // ===== 投野交代（backfill path ①） =====

    [Fact]
    public void RelieveWith_OnFieldFielder_SwapsWithPitcher_NoDefensiveHole()
    {
        var strong = Attr(velLevel: 90, control: 60, staminaLevel: 45, pitchRank: 55);
        var st = new TeamState(NonDhTeam("T", strong, Attr(30, 30, 30, 30)));
        var ss = st.CurrentLineup.First(p => p.Name == "遊");
        var oldPitcher = st.CurrentPitcher;

        Assert.True(st.RelieveWith(ss));

        // 遊撃手がマウンドへ＝本人の潜在能力（リーグ平均でない）で投げる。
        Assert.Equal("遊", st.CurrentPitcher.Name);
        Assert.NotNull(st.CurrentPitcher.Pitching);
        Assert.Equal(strong.MaxVelocityKmh, st.CurrentPitcher.Pitching!.MaxVelocityKmh, 3);
        Assert.Equal(FieldPosition.Pitcher, st.CurrentPitcher.Position);

        // 旧投手は退場せず、空いた遊撃へ回る。
        var atShort = st.PlayerAtPosition(FieldPosition.Shortstop);
        Assert.NotNull(atShort);
        Assert.Equal(oldPitcher.Name, atShort!.Name);
        Assert.False(st.IsRetired(oldPitcher));

        // 守備に穴が開かない（9ポジション全て埋まる・各ポジションに実選手）。
        Assert.Equal(9, st.DefensiveAlignment(new FieldGeometry()).Count);
        foreach (var pos in new[]
        {
            FieldPosition.Pitcher, FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
            FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField,
            FieldPosition.CenterField, FieldPosition.RightField,
        })
        {
            Assert.NotNull(st.PlayerAtPosition(pos));
        }

        // ベンチ非消費・選手交代カウンタは増えない・投手交代として1回計上。
        Assert.Single(st.Bench);
        Assert.Equal(0, st.Substitutions);
        Assert.Equal(1, st.PitcherChanges);
    }

    [Fact]
    public void RelieveWith_BullpenPitcher_UsesNormalChange_RetiresOldPitcher()
    {
        var st = new TeamState(NonDhTeam("T", Attr(80, 60, 45, 55), Attr(70, 70, 70, 70)));
        var oldPitcher = st.CurrentPitcher;
        var reliever = st.AvailableBullpen[0];

        Assert.True(st.RelieveWith(reliever));

        // ブルペン投手は従来交代＝旧投手は退場（リエントリー禁止）、投野交代ではない。
        Assert.Equal(reliever.Name, st.CurrentPitcher.Name);
        Assert.True(st.IsRetired(oldPitcher));
        Assert.Equal(1, st.PitcherChanges);
    }

    // ===== AI が在フィールドの二刀流を選び、GameEngine 結線で決定論を保つ =====

    [Fact]
    public void Game_AiPromotesOnFieldAce_IsDeterministic()
    {
        var coeff = new EnemyAiCoefficients
        {
            BlowupReliefMinTier = 0, RattledReliefMinTier = 0, CloseLateReliefMinTier = 0, BlowupRunsInInning = 1,
        };
        // 遊撃に強力な潜在投手能力、ブルペンは弱い1枚＝AI は緊急時に遊撃をマウンドへ上げうる。
        Team Make(string n) => NonDhTeam(n, Attr(92, 80, 60, 82), Attr(25, 25, 25, 25)) with
        {
            Tactics = new AiTacticsBrain(new AiProfile(90, 6, SchoolStyle.DefensiveMinded), aiCoeff: coeff),
        };

        for (ulong seed = 1; seed <= 20; seed++)
        {
            var r1 = GameEngine.Play(Make("A"), Make("H"), new GameContext(), new Xoshiro256Random(seed));
            var r2 = GameEngine.Play(Make("A"), Make("H"), new GameContext(), new Xoshiro256Random(seed));
            Assert.Equal(GameResultDigest.Sha256Of(r1), GameResultDigest.Sha256Of(r2));
        }
    }
}
