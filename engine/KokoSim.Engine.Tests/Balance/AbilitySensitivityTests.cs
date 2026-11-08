using KokoSim.Balance;
using KokoSim.Config;
using Xunit;

namespace KokoSim.Engine.Tests.Balance;

/// <summary>
/// 能力感度の回帰（issue #840・不変条件#5）。**能力を1つだけ振って、結果が期待どおり動くか**を常に見張る。
///
/// <para>既存の統計帯は平均的なチーム同士の平均値しか測らないので、「効くはずの能力が効いていない／逆向きに効いている」は
/// 帯が全部緑のまま残った（#835 変化球が効かない／#837 遅い投手ほど打たれない／#838 盗塁AIが損をする）。
/// 測り方と読み方は <see cref="AbilitySweep"/>、期待値は data/balance-targets.yaml の <c>ability_sensitivity</c>。</para>
///
/// <para><b>除外リスト（known_defects）</b>は既知の不具合で今は満たせない項目。直って通るようになったら
/// 「もう通っている」と赤くなる＝除外を外し忘れない。新しく赤くなった項目は新しい穴。</para>
/// </summary>
public sealed class AbilitySensitivityTests
{
    private const int Games = 1000;
    private const ulong Seed = 42UL;

    private static readonly Lazy<(AbilitySensitivityTargets Targets, AbilitySweep.Result Result)> Sweep = new(() =>
    {
        var targets = BalanceTargetsLoader.LoadAbilitySensitivityFromFile(
            BalanceRegressionTests.FindDataFile("balance-targets.yaml"));
        // 実ゲームと同じ C#既定の係数で測る。期待のある能力だけ測る（残りはレポートでだけ見る）。
        var only = new HashSet<string>(targets.Abilities.Keys);
        return (targets, AbilitySweep.Run(Games, Seed, coefficientsPath: null, only));
    });

    [Trait("Category", "Heavy")]   // 約40条件 × 1000試合のフルエンジン実測
    [Fact]
    public void EveryAbility_MovesTheResult_InTheExpectedDirection()
    {
        var (targets, result) = Sweep.Value;

        var verdicts = AbilitySweep.Evaluate(result, targets);
        Assert.NotEmpty(verdicts);

        var newHoles = verdicts.Where(v => !v.Pass && v.KnownIssue is null)
            .Select(v => $"{v.Ability}/{v.Side}: 期待する向きに {v.Effect:+0.00;-0.00} 点（最低 {v.MinRuns:0.00}）")
            .ToList();
        Assert.True(newHoles.Count == 0,
            "期待どおりに効いていない能力（新しい穴）:\n" + string.Join("\n", newHoles)
            + "\n既知の不具合なら known_defects に issue 番号付きで載せる。");

        var stale = verdicts.Where(v => v.Pass && v.KnownIssue is not null)
            .Select(v => $"{v.Ability}/{v.Side}（#{v.KnownIssue}）")
            .ToList();
        Assert.True(stale.Count == 0,
            "除外リストの項目がもう通っている。balance-targets.yaml の known_defects から外す:\n" + string.Join("\n", stale));
    }

    [Trait("Category", "Heavy")]
    [Fact]
    public void VelocityLadder_FallsMonotonically()
    {
        var (targets, result) = Sweep.Value;
        var failures = AbilitySweep.VelocityLadderFailures(result.VelocityLadder, targets);
        AssertSeries("最速の梯子（100〜150km/h）", "velocity_ladder", failures, targets);
    }

    [Trait("Category", "Heavy")]
    [Fact]
    public void BreakingBalls_ReduceRunsAgainst()
    {
        var (targets, result) = Sweep.Value;
        var failures = AbilitySweep.RepertoireFailures(result.Repertoire, targets);
        AssertSeries("球種構成", "repertoire", failures, targets);
    }

    private static void AssertSeries(string title, string key, IReadOnlyList<string> failures, AbilitySensitivityTargets targets)
    {
        var known = targets.KnownDefects.FirstOrDefault(d => d.Ability == key);
        if (known is null)
        {
            Assert.True(failures.Count == 0, $"{title}（新しい穴）: " + string.Join(" / ", failures));
        }
        else
        {
            Assert.True(failures.Count > 0,
                $"{title}: 除外リスト（#{known.Issue}）の項目がもう通っている。known_defects から外す");
        }
    }

    // ── 以下は日常ループ（fast）で回す ──

    [Fact]
    public void Measure_IsDeterministic_AndAlternatesSides()
    {
        var focus = AbilitySweep.UniformTeam("自校");
        var opp = AbilitySweep.UniformTeam("相手校");
        var a = AbilitySweep.Measure("x", "x", 50, focus, opp, games: 12, seed: 7UL);
        var b = AbilitySweep.Measure("x", "x", 50, focus, opp, games: 12, seed: 7UL);

        Assert.Equal(a.Focus.Runs, b.Focus.Runs);
        Assert.Equal(a.Opponent.Runs, b.Opponent.Runs);
        Assert.Equal(a.Focus.PlateAppearances, b.Focus.PlateAppearances);
        Assert.Equal(12, a.Games);
        // 同一チーム同士でも、2チームの打席数合計は全試合の打席数（先攻・後攻の取り違えで片方に偏らない）。
        Assert.True(a.Focus.PlateAppearances > 0 && a.Opponent.PlateAppearances > 0);
    }

    [Fact]
    public void Mutate_ChangesOnlyTheScopedPlayers()
    {
        var team = AbilitySweep.UniformTeam("自校");

        var catcherOnly = AbilitySweep.Mutate(team, AbilitySweep.Scope.Catcher, p => p with { ArmStrength = 80 });
        Assert.Equal(1, catcherOnly.BattingOrder.Count(p => p.ArmStrength == 80));
        Assert.Equal(80, catcherOnly.BattingOrder.Single(p => p.Position == Engine.Match.Field.FieldPosition.Catcher).ArmStrength);

        var pitchers = AbilitySweep.Mutate(team, AbilitySweep.Scope.Pitchers, p => p with { Fielding = 80 });
        // 先発1＋控え投手2（野手は変えない）。
        Assert.Equal(3, pitchers.BattingOrder.Concat(pitchers.Reserves).Count(p => p.Fielding == 80));
    }

    [Fact]
    public void AbilitySensitivityTargets_AreBoundToKnownConditions()
    {
        var targets = BalanceTargetsLoader.LoadAbilitySensitivityFromFile(
            BalanceRegressionTests.FindDataFile("balance-targets.yaml"));
        var ids = AbilitySweep.Conditions.Select(c => c.Id).ToHashSet();

        Assert.NotEmpty(targets.Abilities);
        foreach (var id in targets.Abilities.Keys)
        {
            Assert.True(ids.Contains(id), $"ability_sensitivity.abilities.{id} に対応する掃引条件が無い");
        }
        foreach (var d in targets.KnownDefects)
        {
            Assert.True(d.Issue > 0, $"{d.Ability}: 除外には issue 番号が要る");
            var known = ids.Contains(d.Ability) || d.Ability is "velocity_ladder" or "repertoire";
            Assert.True(known, $"known_defects の {d.Ability} が掃引条件に無い");
        }
    }
}
