using System.IO;
using KokoSim.Config;
using KokoSim.Engine.Core;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Nation;
using Xunit;

namespace KokoSim.Engine.Tests.Debugging;

/// <summary>
/// シナリオYAMLの書き間違いは読み込み時にエラーになる（issue #1291）。
/// 黙って既定値で別の局面になると、3つの入口（CLI・Unity デバッグ試合・DebugBridge）で何を撮ったか分からなくなる。
/// </summary>
public sealed class ScenarioStrictnessTests
{
    private static ScenarioCatalog Load(string entryBody)
        => DebugScenarioLoader.Parse("scenarios:\n  - id: t\n" + entryBody);

    private static void AssertRejected(string entryBody, string expectedFragment)
    {
        var e = Assert.Throws<InvalidDataException>(() => Load(entryBody));
        Assert.Contains(expectedFragment, e.Message);
    }

    [Theory]
    [InlineData("    pitcher_fatige: 100\n", "pitcher_fatige")]                  // キーの綴り違い
    [InlineData("    count: { ball: 3 }\n", "ball")]                           // ネスト側のキーの綴り違い
    [InlineData("    away: { schol: \"player\" }\n", "schol")]
    [InlineData("    modern_rules: { dh: true, tie_break: true }\n", "tie_break")]
    [InlineData("    inning: 9回\n", "inning")]                                // 数値でない
    [InlineData("    outs: two\n", "outs")]
    [InlineData("    batter: 4.5\n", "batter")]
    [InlineData("    seed: abc\n", "seed")]
    [InlineData("    count: { balls: x }\n", "count.balls")]
    [InlineData("    away: { score: many }\n", "away.score")]
    [InlineData("    top: maybe\n", "top")]
    [InlineData("    pitcher_rattled: 1\n", "pitcher_rattled")]
    [InlineData("    modern_rules: { dh: 2 }\n", "modern_rules.dh")]
    [InlineData("    bases: [1, 4]\n", "bases")]                               // 範囲外
    [InlineData("    bases: [0]\n", "bases")]
    [InlineData("    bases: [1, 1]\n", "重複")]
    [InlineData("    bases: [1, x]\n", "bases")]                               // 数字でない要素
    [InlineData("    bases: [1, ]\n", "bases")]
    [InlineData("    bases: 1, 2\n", "bases")]                                 // 角括弧なし
    [InlineData("    count: balls=3\n", "count")]                              // 中括弧なし
    [InlineData("    away: { school }\n", "away")]                             // 「キー: 値」でない要素
    [InlineData("    away: { school: \"player\", school: \"player\" }\n", "重複")]
    [InlineData("    inning: 3\n    inning: 4\n", "重複")]
    [InlineData("    inning: 0\n", "inning")]                                  // 範囲外（Validate）
    [InlineData("    outs: 3\n", "outs")]
    [InlineData("    away: { school: \"AI:A\" }\n", "AI:A")]                   // = の無い AI:
    [InlineData("    away: { school: \"AI:\" }\n", "AI:")]
    [InlineData("    away: { school: \"AI:tier=Z\" }\n", "Z")]
    [InlineData("    home: { school: \"team\" }\n", "team")]
    [InlineData("    home: { school: \"player;bench\" }\n", "bench")]
    [InlineData("    force: wild_pitch\n", "wild_pitch")]                      // 設計書の旧綴り
    [InlineData("    force: 3\n", "3")]
    [InlineData("    force: Balk\n", "Balk")]
    public void Misspelling_IsAnErrorAtLoadTime(string entryBody, string expectedFragment)
        => AssertRejected(entryBody, expectedFragment);

    /// <summary>ブロック形式のネストは未対応。黙って既定の相手で始めず、読み込み時に止める。</summary>
    [Fact]
    public void BlockStyleNesting_IsRejected()
    {
        var e = Assert.Throws<InvalidDataException>(() =>
            DebugScenarioLoader.Parse("scenarios:\n  - id: t\n    away:\n      school: \"AI:tier=S\"\n"));
        Assert.Contains("away", e.Message);
    }

    [Fact]
    public void UnknownTopLevelSection_IsRejected()
        => Assert.Throws<InvalidDataException>(() => DebugScenarioLoader.Parse("scenario:\n  - id: t\n"));

    [Fact]
    public void LineOutsideAnEntry_IsRejected()
        => Assert.Throws<InvalidDataException>(() => DebugScenarioLoader.Parse("scenarios:\n  inning: 3\n"));

    /// <summary>YAML として正しい「scenarios: 直下に字下げなしの - id:」を0件にしない。</summary>
    [Fact]
    public void UnindentedSequenceUnderScenarios_IsRead()
    {
        var catalog = DebugScenarioLoader.Parse(
            "scenarios:\n- id: a\n  inning: 9\n  force: WildPitch\n- id: b\n  outs: 2\n");
        Assert.Equal(2, catalog.Count);
        Assert.True(catalog.TryGet("a", out var a));
        Assert.Equal(9, a.Inning);
        Assert.Equal(ForcedOutcome.WildPitch, a.ResolveForce());
        Assert.True(catalog.TryGet("b", out var b));
        Assert.Equal(2, b.Outs);
    }

    /// <summary>引用符の中の「:」「;」で要素が割れない（school にチーム指定の「AI:tier=C;bench=N」が入る）。</summary>
    [Fact]
    public void QuotedValues_AreNotSplit()
    {
        var catalog = Load("    home: { school: \"AI:tier=C;bench=12\", score: 3 }\n    away: { score: 1, school: \"player\" }\n");
        Assert.True(catalog.TryGet("t", out var t));
        Assert.Equal("AI:tier=C;bench=12", t.Home);
        Assert.Equal(3, t.HomeScore);
        Assert.Equal(1, t.AwayScore);
    }

    /// <summary>force は enum 名の1つの綴り（大文字小文字は無視）。経路ごとに解釈がズレない。</summary>
    [Theory]
    [InlineData("WildPitch", ForcedOutcome.WildPitch)]
    [InlineData("wildpitch", ForcedOutcome.WildPitch)]
    [InlineData("DroppedThirdStrike", ForcedOutcome.DroppedThirdStrike)]
    public void ForceSpelling_IsTheEnumName(string spelling, ForcedOutcome expected)
    {
        Assert.True(Load("    force: " + spelling + "\n").TryGet("t", out var def));
        Assert.Equal(expected, def.ResolveForce());
    }

    [Fact]
    public void ScenarioWithoutForce_ResolvesToNone()
    {
        Assert.True(Load("    inning: 3\n").TryGet("t", out var def));
        Assert.Equal(ForcedOutcome.None, def.ResolveForce());
    }

    /// <summary>コードで組んだ定義も Build が同じ検査を通す（YAML を経由しない入口でも黙って通さない）。</summary>
    [Fact]
    public void Build_ValidatesDefinitionsBuiltInCode()
    {
        var bad = new ScenarioDefinition { Id = "x", Bases = new[] { 5 } };
        Assert.Throws<System.ArgumentException>(() => ScenarioBuilder.Build(bad, new KokoSim.Engine.Match.Game.GameContext(), 1UL));
        var badForce = new ScenarioDefinition { Id = "y", Force = "wild_pitch" };
        Assert.Throws<System.ArgumentException>(() => ScenarioBuilder.Build(badForce, new KokoSim.Engine.Match.Game.GameContext(), 1UL));
    }

    /// <summary>
    /// Unity のデバッグ試合経路（DebugMatchFactory）も、"player" を渡された実部員のチームで解く
    /// （DebugBridge と同じ顔ぶれ）。渡さなければ従来どおり生成校。
    /// </summary>
    [Fact]
    public void DebugMatchFactory_ResolvesPlayerToTheGivenTeam()
    {
        var real = StrengthTeamFactory.Create(70.0, "実部員校", new Xoshiro256Random(5UL));
        var def = new ScenarioDefinition { Id = "p", Home = "player", Away = "AI:tier=C" };

        var withTeam = DebugMatchFactory.Build(Stadium.Koshien, new DebugMatchOptions { Scenario = def, PlayerTeam = real });
        Assert.Equal("実部員校", withTeam.Home.Name);

        var without = DebugMatchFactory.Build(Stadium.Koshien, new DebugMatchOptions { Scenario = def });
        Assert.NotEqual("実部員校", without.Home.Name);
    }
}
