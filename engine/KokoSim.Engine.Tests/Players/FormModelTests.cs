using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Players;

/// <summary>
/// 調子システム（設計書02 §3.3）＋試合限りの好不調（§3.3b）。
/// 補正の向き・恒等性・当日揺らぎの頻度（大半微小/スパイクは十数試合に一度）・週次の波の性質を検証。
/// </summary>
public sealed class FormModelTests
{
    private static readonly FormCoefficients F = new();

    // --- 補正の向きと恒等性 ---

    [Fact]
    public void NormalConditionAndZeroDayForm_IsIdentity()
    {
        var b = new BatterAttributes { Contact = 63, Power = 47 };
        var p = new PitcherAttributes { Control = 58, MaxVelocityKmh = 141.0 };
        Assert.Equal(b, FormModel.ApplyBatter(b, Condition.Normal, 0.0, F));
        Assert.Equal(p, FormModel.ApplyPitcher(p, Condition.Normal, 0.0, F));
    }

    [Fact]
    public void ExcellentRaises_TerribleLowers()
    {
        var b = new BatterAttributes { Contact = 50, Power = 50 };
        var hot = FormModel.ApplyBatter(b, Condition.Excellent, 0.0, F);
        var cold = FormModel.ApplyBatter(b, Condition.Terrible, 0.0, F);
        Assert.True(hot.Contact > 50 && hot.Power > 50);
        Assert.True(cold.Contact < 50 && cold.Power < 50);

        var p = new PitcherAttributes { Control = 50, MaxVelocityKmh = 140.0 };
        var hotP = FormModel.ApplyPitcher(p, Condition.Excellent, 0.0, F);
        var coldP = FormModel.ApplyPitcher(p, Condition.Terrible, 0.0, F);
        Assert.True(hotP.Control > 50 && hotP.MaxVelocityKmh > 140.0);
        Assert.True(coldP.Control < 50 && coldP.MaxVelocityKmh < 140.0);
    }

    // --- キレ（PitchRank / 球種Sharpness）への補正（issue #49） ---

    [Fact]
    public void ApplyPitcher_Sharpness_IdentityAtNormal()
    {
        var p = new PitcherAttributes
        {
            Control = 58,
            MaxVelocityKmh = 141.0,
            PitchRank = 55,
            Repertoire = new[]
            {
                new PitchSlot { Type = PitchType.Fastball, Power = 55, Sharpness = 55 },
                new PitchSlot { Type = PitchType.Slider, Power = 48, Sharpness = 52 },
            },
        };
        Assert.Equal(p, FormModel.ApplyPitcher(p, Condition.Normal, 0.0, F));
    }

    [Fact]
    public void ApplyPitcher_Sharpness_ExcellentRaises_TerribleLowers()
    {
        var p = new PitcherAttributes
        {
            Control = 50,
            MaxVelocityKmh = 140.0,
            PitchRank = 50,
            Repertoire = new[]
            {
                new PitchSlot { Type = PitchType.Fastball, Power = 50, Sharpness = 50 },
                new PitchSlot { Type = PitchType.Slider, Power = 50, Sharpness = 50 },
            },
        };
        var hot = FormModel.ApplyPitcher(p, Condition.Excellent, 0.0, F);
        var cold = FormModel.ApplyPitcher(p, Condition.Terrible, 0.0, F);

        Assert.True(hot.PitchRank > 50);
        Assert.True(cold.PitchRank < 50);
        Assert.All(hot.Repertoire!, slot => Assert.True(slot.Sharpness > 50));
        Assert.All(cold.Repertoire!, slot => Assert.True(slot.Sharpness < 50));
        // 球威(Power)は本Issueの対象外＝据え置き（設計上キレのみを補正する）。
        Assert.All(hot.Repertoire!, slot => Assert.Equal(50, slot.Power));
    }

    [Fact]
    public void ApplyPitcher_Sharpness_NoRepertoire_FallsBackToPitchRankOnly()
    {
        var p = new PitcherAttributes { Control = 50, MaxVelocityKmh = 140.0, PitchRank = 50 };
        var hot = FormModel.ApplyPitcher(p, Condition.Excellent, 0.0, F);
        Assert.True(hot.PitchRank > 50);
        Assert.Null(hot.Repertoire);
    }

    [Fact]
    public void ApplyPitcher_Sharpness_IsDeterministic()
    {
        var p = new PitcherAttributes
        {
            Control = 50,
            MaxVelocityKmh = 140.0,
            PitchRank = 50,
            Repertoire = new[] { new PitchSlot { Type = PitchType.Fastball, Power = 50, Sharpness = 50 } },
        };
        var a = FormModel.ApplyPitcher(p, Condition.Good, dayForm: 0.3, F);
        var b = FormModel.ApplyPitcher(p, Condition.Good, dayForm: 0.3, F);
        // Repertoire は補正のたびに新しい配列を割り当てる（IReadOnlyList はレコードの既定Equalsが
        // 参照比較になるため、要素の値で比較する）。
        Assert.Equal(a.PitchRank, b.PitchRank);
        Assert.Equal(a.Repertoire!.Single().Sharpness, b.Repertoire!.Single().Sharpness);
    }

    [Fact]
    public void Quantize_MapsThresholds()
    {
        Assert.Equal(Condition.Excellent, FormModel.Quantize(0.8));
        Assert.Equal(Condition.Good, FormModel.Quantize(0.3));
        Assert.Equal(Condition.Normal, FormModel.Quantize(0.0));
        Assert.Equal(Condition.Poor, FormModel.Quantize(-0.3));
        Assert.Equal(Condition.Terrible, FormModel.Quantize(-0.8));
    }

    // --- 相手校の調子観測（§3.3「育成眼が高いほど正確、低いと誤認」, issue #47） ---

    [Fact]
    public void Observe_TalentEyeMax_MatchesTrueValue_AlmostAlways()
    {
        var rng = new Xoshiro256Random(1);
        const double actual = 0.7; // Excellent 域
        var matches = 0;
        const int trials = 500;
        for (var i = 0; i < trials; i++)
        {
            var observed = FormModel.Observe(actual, talentEye: 100, rng, F);
            if (observed == FormModel.Quantize(actual)) matches++;
        }
        // σ≈下限(0.02)なので、ほぼ常に真値と同じ段階になる。
        Assert.True(matches > trials * 0.95, $"育成眼MAXでも真値と一致しない: {matches}/{trials}");
    }

    [Fact]
    public void Observe_TalentEyeMin_HasLargeVariance()
    {
        var rng = new Xoshiro256Random(2);
        const double actual = 0.0; // Normal 域
        var seen = new HashSet<Condition>();
        for (var i = 0; i < 500; i++)
        {
            seen.Add(FormModel.Observe(actual, talentEye: 0, rng, F));
        }
        // σが大きいので、真値(Normal)以外の段階も相当出る（分散が大きい）。
        Assert.True(seen.Count >= 3, $"育成眼MINでも誤認の分散が小さすぎる: {seen.Count}種のみ");
    }

    [Fact]
    public void Observe_SameSeed_SameResult()
    {
        var a = FormModel.Observe(0.4, talentEye: 30, new Xoshiro256Random(99), F);
        var b = FormModel.Observe(0.4, talentEye: 30, new Xoshiro256Random(99), F);
        Assert.Equal(a, b);
    }

    // --- 当日の出来の分布（§3.3b: 大半は微小・大崩れは十数試合に一度） ---

    [Fact]
    public void DayForm_MostlySmall_SpikesRare()
    {
        var rng = new Xoshiro256Random(42);
        var samples = Enumerable.Range(0, 20000).Select(_ => FormModel.SampleDayForm(rng, F)).ToList();

        Assert.All(samples, v => Assert.InRange(v, -1.0, 1.0));

        // スパイク（|v| >= 0.6）は spike_prob≈7% ＝ 十数試合に一度。頻発しない・ゼロでもない。
        var spikeRate = samples.Count(v => System.Math.Abs(v) >= F.DayFormSpikeMin) / (double)samples.Count;
        Assert.InRange(spikeRate, 0.04, 0.11);

        // 大半（通常日）は微小: |v| <= clamp(0.45)。
        var smallRate = samples.Count(v => System.Math.Abs(v) <= F.DayFormClamp) / (double)samples.Count;
        Assert.True(smallRate > 0.85, $"通常日の比率が低すぎる: {smallRate:P0}");

        // 対称（平均≈0）: 能力の積み上げを崩さない。
        Assert.InRange(samples.Average(), -0.02, 0.02);
    }

    // --- 週次の波（§3.3: 数週間続く・毎試合振り直さない） ---

    [Fact]
    public void WeeklyCondition_StaysInRange_AndPersists()
    {
        var rng = new Xoshiro256Random(7);
        // 高い状態から始めると、持ち越し(persistence)により数週間は正側に留まりやすい。
        var positiveWeeks = 0;
        for (var trial = 0; trial < 200; trial++)
        {
            var v = 0.9;
            v = FormModel.NextWeeklyCondition(v, rng, F);
            Assert.InRange(v, -1.0, 1.0);
            if (v > 0) positiveWeeks++;
        }
        Assert.True(positiveWeeks > 150, $"絶好調が翌週に持ち越されない: {positiveWeeks}/200");
    }

    // --- 初期化（issue #50）: 週次AR(1)の定常分布からサンプリングする ---

    [Fact]
    public void StationaryConditionSigma_MatchesAr1StationaryVariance()
    {
        // 定常分布のσ = WeeklySigma / √(1 − persistence²)。現行係数(0.75, 0.28)なら ≈0.42。
        Assert.InRange(F.StationaryConditionSigma, 0.40, 0.44);
    }

    [Fact]
    public void SampleInitialCondition_MatchesStationarySigma_AndIsDeterministic()
    {
        var rng = new Xoshiro256Random(5);
        var samples = Enumerable.Range(0, 20000).Select(_ => FormModel.SampleInitialCondition(rng, F)).ToList();

        Assert.All(samples, v => Assert.InRange(v, -1.0, 1.0));
        Assert.InRange(samples.Average(), -0.02, 0.02);

        var mean = samples.Average();
        var sd = System.Math.Sqrt(samples.Average(v => (v - mean) * (v - mean)));
        Assert.InRange(sd, F.StationaryConditionSigma - 0.03, F.StationaryConditionSigma + 0.03);

        var a = FormModel.SampleInitialCondition(new Xoshiro256Random(9), F);
        var b = FormModel.SampleInitialCondition(new Xoshiro256Random(9), F);
        Assert.Equal(a, b);
    }

    // --- Season 接続: 週次更新で調子が分布し、5段階すべてが出現する ---

    [Fact]
    public void SeasonRoster_DevelopsConditionSpread()
    {
        var c = new KokoSim.Engine.Season.RosterCoefficients();
        var roster = KokoSim.Engine.Season.ProspectGenerator.Intake(1, c, new Xoshiro256Random(11)).ToList();
        var rng = new Xoshiro256Random(12);
        var seen = new HashSet<Condition>();
        for (var week = 0; week < 200; week++)
        {
            var formRng = rng.Fork((ulong)(9000 + week));
            foreach (var p in roster)
                p.ConditionValue = FormModel.NextWeeklyCondition(p.ConditionValue, formRng, F);
            foreach (var p in roster) seen.Add(FormModel.Quantize(p.ConditionValue));
        }
        // 長期では全5段階が観測される（波が動いている）。
        Assert.Equal(5, seen.Count);
    }

    // --- 試合統合: 絶好調チーム vs 絶不調チームで勝率が傾く（調子が試合に効く） ---

    [Fact]
    public void Game_HotTeamBeatsColdTeam_Majority()
    {
        var ctx = new GameContext();
        var wins = 0;
        const int games = 30;
        for (ulong s = 0; s < games; s++)
        {
            var hot = BuildTeam("H", Condition.Excellent);
            var cold = BuildTeam("C", Condition.Terrible);
            var r = GameEngine.Play(hot, cold, ctx, new Xoshiro256Random(1000 + s));
            if (r.AwayRuns > r.HomeRuns) wins++; // hot=先攻
        }
        Assert.True(wins > games / 2, $"絶好調チームが勝ち越さない: {wins}/{games}");
    }

    [Fact]
    public void Game_WithDayForm_IsDeterministic()
    {
        var ctx = new GameContext();
        var a = GameEngine.Play(BuildTeam("A", Condition.Normal), BuildTeam("B", Condition.Normal), ctx, new Xoshiro256Random(77));
        var b = GameEngine.Play(BuildTeam("A", Condition.Normal), BuildTeam("B", Condition.Normal), ctx, new Xoshiro256Random(77));
        Assert.Equal(a.AwayRuns, b.AwayRuns);
        Assert.Equal(a.TotalPitches, b.TotalPitches);
    }

    private static Player Pos(FieldPosition pos, Condition c) => new()
    {
        Position = pos, Condition = c,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

    private static Team BuildTeam(string name, Condition c)
    {
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher, c), Pos(FieldPosition.FirstBase, c), Pos(FieldPosition.SecondBase, c),
            Pos(FieldPosition.ThirdBase, c), Pos(FieldPosition.Shortstop, c), Pos(FieldPosition.LeftField, c),
            Pos(FieldPosition.CenterField, c), Pos(FieldPosition.RightField, c),
            Pos(FieldPosition.Pitcher, c) with { Name = name + "P", Pitching = PitcherAttributes.LeagueAverage },
        };
        return new Team
        {
            Name = name, BattingOrder = order, PitcherSlot = 8,
            Bullpen = new[] { Pos(FieldPosition.Pitcher, c) with { Name = name + "R", Pitching = PitcherAttributes.LeagueAverage } },
        };
    }
}
