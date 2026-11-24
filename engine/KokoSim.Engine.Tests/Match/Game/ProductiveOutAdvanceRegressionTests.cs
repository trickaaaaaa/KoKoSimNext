using System;
using System.Linq;
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
        // 600→800（issue #797）: 一塁走者ありのゴロで先の走者を二塁で封殺するようになり（打者が一塁に残る）、
        // 「二塁走者だけ」の場面が減った（600試合で 400件弱）。標本の下限 400 を保つために試合を増やす。
        // 800→1000（issue #1549）: 二塁の自己フォース併殺（一塁走者ありの場面でだけ選ばれる）の追加で
        // 固定シードのRNG消費順が下流まで変わり、無関係なこの場面（二塁走者のみ）の出現数も偶然動く
        // （二塁ゴロの標本が800試合でn=96まで下がり下限100を割った）。800試合で#797が保っていた余裕を
        // 取り戻すため試合数を増やす（このテストが数える場面自体には一塁走者が居ないので二塁の自己フォース
        // 自体は一切関与しない＝RNGストリームが先の打席で変わった副作用）。
        const int games = 1000;
        var advanced = new int[games];
        var held = new int[games];
        var outAtThird = new int[games];
        // 打球の方向別（issue #1418）: 二塁ゴロ（走者の後ろ＝進む側）と遊撃・三塁ゴロ（走者の前＝原則自重）。
        var secondBaseN = new int[games];
        var secondBaseAdv = new int[games];
        var frontN = new int[games];
        var firstBaseN = new int[games];
        var firstBaseRan = new int[games];
        var firstBaseOut = new int[games];
        var frontRan = new int[games];

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
                if (fb == FieldPosition.SecondBase)
                {
                    secondBaseN[i]++;
                    if (e.BaseThirdAfter) secondBaseAdv[i]++;
                }
                else if (fb == FieldPosition.FirstBase)
                {
                    firstBaseN[i]++;
                    if (!e.BaseSecondAfter) firstBaseRan[i]++;
                    if (e.OutsThisPa >= 2) firstBaseOut[i]++;
                }
                else if (fb is FieldPosition.Shortstop or FieldPosition.ThirdBase)
                {
                    frontN[i]++;
                    if (!e.BaseSecondAfter) frontRan[i]++;
                }
            }
        });

        var totalAdv = 0; var totalHeld = 0; var totalOut = 0;
        for (var i = 0; i < games; i++) { totalAdv += advanced[i]; totalHeld += held[i]; totalOut += outAtThird[i]; }
        var total = totalAdv + totalHeld + totalOut;
        var sbN = secondBaseN.Sum(); var sbAdv = secondBaseAdv.Sum(); var fN = frontN.Sum(); var fRan = frontRan.Sum();
        var b1N = firstBaseN.Sum(); var b1Ran = firstBaseRan.Sum(); var b1Out = firstBaseOut.Sum();
        _out.WriteLine($"n={total} advanced(2->3)={totalAdv} ({100.0 * totalAdv / total:F1}%) " +
                        $"held={totalHeld} ({100.0 * totalHeld / total:F1}%) outAtThird={totalOut} ({100.0 * totalOut / total:F1}%)");
        _out.WriteLine($"二塁ゴロ n={sbN} 進塁={sbAdv} ／ 一塁ゴロ n={b1N} 走った={b1Ran} 三塁憤死={b1Out} ／ 遊撃・三塁ゴロ n={fN} 走った={fRan}");

        Assert.True(total > 400, $"標本不足 n={total}");
        // 旧実装はこの場面で進塁0%（300試合270件中0件）。issue #1418 で遊撃・三塁ゴロは原則自重にしたので、
        // 全体の進塁率は約77%→約4割へ下がった（800試合）。走者の後ろ（一塁・二塁方向）の一塁手・二塁手のゴロは
        // 必ず進み、守備は三塁へ転送しない（オーナー決定 2026-11-23）＝ここが0%へ戻っていないことを見張る。
        // 二塁ゴロは二塁ベースの左（走者の前）で捕ったものが自重に回るので100%にはならない。
        Assert.True(sbN > 100, $"二塁ゴロの標本不足 n={sbN}");
        Assert.InRange((double)sbAdv / sbN, 0.60, 1.0);
        // 一塁ゴロ（投ゴロは除く）は二塁走者が100%進み、一塁手は三塁へ転送しない＝三塁憤死0（issue #1418）。
        Assert.True(b1N > 50, $"一塁ゴロの標本不足 n={b1N}");
        Assert.Equal(b1N, b1Ran);
        Assert.Equal(0, b1Out);
        // 走者の前（遊撃・三塁）のゴロでは走らない（例外は三盗で投球と同時に走っていた走者だけ）。
        Assert.True(fN > 100, $"遊撃・三塁ゴロの標本不足 n={fN}");
        Assert.True((double)fRan / fN < 0.03, $"遊撃・三塁ゴロで走った割合が高すぎる（{fRan}/{fN}）");
        var rate = (double)totalAdv / total;
        Assert.InRange(rate, 0.15, 0.95);
    }
}
