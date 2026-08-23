using System;
using System.Threading.Tasks;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 1死以下・二塁走者のみ・内野ゴロ（打者アウト）で二塁走者が三塁へ進む割合の実ゲーム回帰
/// （issue productive-out-second-to-third-not-advancing）。旧実装は「捕球点→三塁への直接送球」に
/// 対する走者の生存確率だけで自重を判定しており、この場面に至るために必要な速い捕球時刻では常に
/// 自重域へ落ちて進塁が実質0%になっていた（300試合実測で270件中0件）。修正後は一塁送球到達を起点に
/// した転送レース（P_relay）で判定する＝実際に発生する送球だけを見る。
/// </summary>
[Trait("Category", "Heavy")]
public sealed class ProductiveOutAdvanceRegressionTests
{
    private readonly ITestOutputHelper _out;
    public ProductiveOutAdvanceRegressionTests(ITestOutputHelper o) => _out = o;

    private static bool Infield(FieldPosition p) => p is FieldPosition.Pitcher or FieldPosition.Catcher
        or FieldPosition.FirstBase or FieldPosition.SecondBase or FieldPosition.ThirdBase or FieldPosition.Shortstop;

    [Fact]
    public void R2Only_InfieldGrounder_RunnerUsuallyAdvancesToThird()
    {
        const int games = 600;
        var advanced = new int[games];
        var held = new int[games];
        var outAtThird = new int[games];

        Parallel.For(0, games, i =>
        {
            var rng = new Xoshiro256Random(9100UL + (ulong)i);
            var away = StrengthTeamFactory.Create(50, "A", rng);
            var home = StrengthTeamFactory.Create(50, "B", rng);
            var r = GameEngine.Play(away, home, new GameContext(), rng);
            foreach (var e in r.Log)
            {
                if (!(e.BaseSecondBefore && !e.BaseFirstBefore && !e.BaseThirdBefore)) continue;
                if (e.OutsBefore >= 2) continue;
                if (e.BattedIsFly || e.FieldedBy is not { } fb || !Infield(fb)) continue;
                if (e.Result is not PlateAppearanceResult.InPlayOut) continue;

                if (e.BaseThirdAfter) advanced[i]++;
                else if (e.BaseSecondAfter) held[i]++;
                else outAtThird[i]++;
            }
        });

        var totalAdv = 0; var totalHeld = 0; var totalOut = 0;
        for (var i = 0; i < games; i++) { totalAdv += advanced[i]; totalHeld += held[i]; totalOut += outAtThird[i]; }
        var total = totalAdv + totalHeld + totalOut;
        _out.WriteLine($"n={total} advanced(2->3)={totalAdv} ({100.0 * totalAdv / total:F1}%) " +
                        $"held={totalHeld} ({100.0 * totalHeld / total:F1}%) outAtThird={totalOut} ({100.0 * totalOut / total:F1}%)");

        Assert.True(total > 400, $"標本不足 n={total}");
        // 旧実装はこの場面で進塁0%（300試合270件中0件）。修正後の実測は約77%（600試合）。
        // 自重・三塁憤死（1塁手処理の短い中継など）も一定は残るはずなので上限も置く。
        var rate = (double)totalAdv / total;
        Assert.InRange(rate, 0.55, 0.95);
    }
}
