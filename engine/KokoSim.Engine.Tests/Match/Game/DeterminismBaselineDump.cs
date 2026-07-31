using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 決定論ベースラインの再生成ツール（恒久保持）。現行エンジンの全カード×全シードのダイジェストを出力する。
/// エンジン挙動を意図して変えたときに Skip を外して実行し、出力を determinism-baseline.txt へ貼り直して凍結する
/// （EngineDeterminismGateTests と DebugTraceTests の照合先）。
/// 実行: dotnet test --filter "FullyQualifiedName~DeterminismBaselineDump" --logger "console;verbosity=detailed"
/// </summary>
public sealed class DeterminismBaselineDump
{
    private readonly ITestOutputHelper _out;
    public DeterminismBaselineDump(ITestOutputHelper output) => _out = output;

    [Fact(Skip = "ベースライン再生成用。determinism-baseline.txt を作り直すときだけ Skip を外す。")]
    public void Dump()
    {
        foreach (var card in DeterminismCards.CardNames)
            foreach (var seed in DeterminismCards.Seeds())
            {
                var r = DeterminismCards.Run(card, seed);
                _out.WriteLine($"BL {card} {seed} {GameResultDigest.Sha256Of(r)}");
            }
    }
}
