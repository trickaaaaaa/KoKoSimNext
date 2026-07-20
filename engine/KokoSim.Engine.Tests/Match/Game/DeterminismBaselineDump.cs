using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 決定論ベースラインのダンプ（一時ツール）。現行エンジンの全カード×全シードのダイジェストを出力する。
/// これを凍結して EngineDeterminismGateTests のベースラインにする。ゲート整備後は削除してよい。
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
