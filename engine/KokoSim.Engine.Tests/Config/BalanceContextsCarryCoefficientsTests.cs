using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using KokoSim.Balance;
using KokoSim.Config;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Config;

/// <summary>
/// issue #1212: Balance CLI の <c>BuildContext</c> は <c>--coefficients</c> で読んだ YAML を試合・打席の文脈へ写す。
/// 写し漏れがあると「YAML を変えても CLI の結果が動かない」（実害: Mound / PitchRecovery / Momentum / Baserunning）。
/// 文脈（<see cref="GameContext"/>・<see cref="AtBatContext"/>）が持つ係数のうち、bundle に同じ型のものは
/// YAML の値がそのまま入っていなければならない。型の突き合わせでやるので、文脈に係数を足しても手書き不要。
/// </summary>
public sealed class BalanceContextsCarryCoefficientsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"coef-sentinel-{Guid.NewGuid():N}.yaml");

    public BalanceContextsCarryCoefficientsTests()
        => File.WriteAllText(_path, YamlSentinelSupport.Build(YamlSentinelSupport.FindDataFile("coefficients.yaml")).Text);

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void GameSimulationContext_CarriesBundleCoefficients()
        => AssertCarries(GameSimulation.BuildContext(_path), typeof(GameContext));

    [Fact]
    public void AtBatSimulationContext_CarriesBundleCoefficients()
        => AssertCarries(AtBatSimulation.BuildContext(_path), typeof(AtBatContext));

    private void AssertCarries(object ctx, Type ctxType)
    {
        var bundle = CoefficientsLoader.LoadFromFile(_path);
        var bundleProps = typeof(CoefficientsBundle).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != "Version" && IsCoefficientType(p.PropertyType))
            .ToList();

        var checkedCount = 0;
        var missing = new List<string>();
        foreach (var cp in ctxType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var match = bundleProps.Where(b => b.PropertyType == cp.PropertyType).ToList();
            if (match.Count != 1) continue;
            checkedCount++;
            var expected = YamlSentinelSupport.NumbersOf(match[0].GetValue(bundle));
            var actual = YamlSentinelSupport.NumbersOf(cp.GetValue(ctx));
            if (!expected.SequenceEqual(actual)) missing.Add($"{ctxType.Name}.{cp.Name}（bundle.{match[0].Name}）");
        }

        Assert.True(checkedCount >= 5, $"突き合わせた係数が少なすぎる（{checkedCount}）。型の対応が壊れている");
        Assert.True(missing.Count == 0,
            "Balance CLI の BuildContext が YAML の値を文脈へ写していない（既定値のまま）:\n" + string.Join("\n", missing));
    }

    private static bool IsCoefficientType(Type t)
        => t.IsClass && t != typeof(string) && (t.Namespace?.StartsWith("KokoSim", StringComparison.Ordinal) ?? false);
}
