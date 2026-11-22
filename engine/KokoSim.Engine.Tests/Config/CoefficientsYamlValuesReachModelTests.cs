using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Config;
using Xunit;

namespace KokoSim.Engine.Tests.Config;

/// <summary>
/// issue #1212: data/coefficients.yaml の数値が<b>Model まで届く</b>こと。
/// <c>CoefficientsYamlKeysAreBoundTests</c> は「YAML の鍵が DTO にある」までで、DTO→<c>ToModel</c> の写し漏れ
/// （実害: <c>roster.dual_talent_*</c>）や、シーケンス内 mapping の取りこぼしを見逃す。
/// また <c>CoefficientsDefaultsConsistencyTests</c> は「YAML＝既定」なので、値が捨てられても緑のままになる。
/// ここでは YAML の数値の葉を1つずつ別の目印の値へ書き換えて読み、その目印が Model のどこかに現れることを確かめる。
/// </summary>
public sealed class CoefficientsYamlValuesReachModelTests
{
    [Fact]
    public void EveryNumericYamlLeaf_ReachesTheModel()
    {
        var sentinel = YamlSentinelSupport.Build(YamlSentinelSupport.FindDataFile("coefficients.yaml"));
        Assert.True(sentinel.Markers.Count > 1000, $"数値の葉が少なすぎる（{sentinel.Markers.Count}）。走査が壊れている");

        var bundle = CoefficientsLoader.Parse(sentinel.Text);
        var seen = YamlSentinelSupport.NumbersOf(bundle).Select(YamlSentinelSupport.Key).ToHashSet();

        var dropped = sentinel.Markers
            .Where(m => !seen.Contains(YamlSentinelSupport.Key(m.Value)))
            .Select(m => m.Key)
            .ToList();

        Assert.True(dropped.Count == 0,
            $"data/coefficients.yaml の数値 {dropped.Count} 件が Model に届かない（ToModel の写し漏れ・DTO のミスの疑い。"
            + "ここで落ちたら ToModel に1行足す）:\n" + string.Join("\n", dropped));
    }
}
