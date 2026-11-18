using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// タッチプレーのタッチ秒がフォースプレーと同じ0.10秒になっていた問題（issue #1139）。
/// タッチ（捕球→グラブを走者の足元へ下ろす）はフォース（捕球して塁へ触れるだけ）より一拍遅いはずなのに、
/// 解決式では同じ0.10秒を使っていた＝際どいタッチプレーが送球到達と同時にアウトが決まるフォースのタイミングに
/// 見えていた。ここでは「タッチ系の秒数がフォース系より長い」ことと、「同じ送球・同じ走者でタッチの塁は
/// フォースの塁よりセーフ寄りになる」ことを固定する（issue #1139 完了条件）。
/// </summary>
public sealed class TagVsForceSecondsTests
{
    private static readonly BaserunningCoefficients C = new();
    private static readonly FieldGeometry Field = new();

    // --- 係数そのもの: タッチはフォースより長く、目安0.2〜0.3秒に収まる ---

    [Fact]
    public void AllTagSeconds_AreLongerThanForceBaseline_AndWithinRealisticRange()
    {
        // タッチ系の秒数（issue #1139 で分離）: いずれもフォースの基準0.10秒より長く、目安0.2〜0.3秒に収まる。
        // 盗塁の二塁タッチ(tag_seconds)だけは+0.03（目安より控えめ）に留めている＝盗塁は steal_success_bias で
        // 増分を打ち消せない（打ち消すと「送球が先着した絵なのにセーフ」が戻る、XMLコメント参照）ため、
        // 増分がそのまま帯・感度テストへ効く。+0.10だとSteal_LeagueAverage_IsPlausible（0.30-0.60）を0.608で
        // 超え、+0.06でもHeavyのdouble_steal_third_break_per_gameとAbilitySensitivityTestsのmental/defenseを
        // 割るため、「タッチ>フォース」を満たす最小限の+0.03に留めた（TagSeconds のXMLコメント参照）。
        foreach (var (name, tagSeconds, min) in new[]
                 {
                     ("tag_seconds（盗塁の二塁タッチ）", C.TagSeconds, 0.11),
                     ("third_tag_seconds（一塁→三塁）", C.ThirdTagSeconds, 0.18),
                     ("advance_tag_seconds（ゴロ進塁打の二塁→三塁）", C.AdvanceTagSeconds, 0.18),
                     ("second_tag_seconds（3-6のタッチ併殺）", C.SecondTagSeconds, 0.18),
                     ("doubled_off_tag_seconds（帰塁）", C.DoubledOffTagSeconds, 0.18),
                     ("home_tag_seconds（本塁クロスプレー）", C.HomeTagSeconds, 0.18),
                 })
        {
            Assert.True(tagSeconds > 0.10, $"{name} がフォース基準(0.10秒)以下: {tagSeconds}");
            Assert.InRange(tagSeconds, min, 0.32);
        }
    }

    [Fact]
    public void AllForceSeconds_StayAtBagTouchBaseline()
    {
        // フォース（捕球して塁に触れるだけ）は現行どおり0.10秒のまま＝タッチ側だけを分離する（issue #1139）。
        Assert.Equal(0.10, C.SecondForceTagSeconds);
        Assert.Equal(0.10, C.ThirdForceTagSeconds);
        Assert.Equal(0.10, C.HomeForceTagSeconds);
        Assert.Equal(0.10, C.FirstForceTagSeconds);
    }

    // --- 統合: 同じ送球・同じ走者でタッチの塁はフォースの塁よりセーフ寄りになる ---
    // バイアス(SuccessBias)を0に固定し、タッチ/フォースの秒の差だけを取り出す（既存の校正値は
    // 他の簡略化を吸収する別物の補正であり、ここでの比較には混ぜない）。

    [Fact]
    public void ThirdTag_SameThrowAndRunner_IsSaferThanThirdForce_ByExactlyTheSecondsDelta()
    {
        var runner = new Player { Speed = 50, Baserunning = 50 };
        var situation = new HomePlaySituation(new Vector3D(0, 0, 70), 3.0, new Player { ArmStrength = 50 }.ToFielder().ThrowSpeedMps);

        var tagParams = new HomePlayResolver.BasePlayParams(3, Field.ThirdBase, C.ThirdTagSeconds, 0.0, C.ThirdMarginScale);
        var forceParams = new HomePlayResolver.BasePlayParams(3, Field.ThirdBase, C.ThirdForceTagSeconds, 0.0, C.ThirdMarginScale);

        var tagMargin = HomePlayResolver.Margin(runner, fromBase: 1, situation, Field, C, tagParams);
        var forceMargin = HomePlayResolver.Margin(runner, fromBase: 1, situation, Field, C, forceParams);

        // タッチは守備所要が長いぶんmarginが大きい＝セーフ(到達)寄り。差はちょうどタッチ秒とフォース秒の差。
        Assert.True(tagMargin > forceMargin, $"タッチのmarginがフォース以下: タッチ{tagMargin} フォース{forceMargin}");
        Assert.Equal(C.ThirdTagSeconds - C.ThirdForceTagSeconds, tagMargin - forceMargin, precision: 9);
    }

    [Fact]
    public void HomeTag_SameThrowAndRunner_IsSaferThanHomeForce_ByExactlyTheSecondsDelta()
    {
        var runner = new Player { Speed = 50, Baserunning = 50 };
        var situation = new HomePlaySituation(new Vector3D(0, 0, 90), 3.2, new Player { ArmStrength = 50 }.ToFielder().ThrowSpeedMps);

        var tagParams = new HomePlayResolver.BasePlayParams(4, new Vector3D(0, 0, 0), C.HomeTagSeconds, 0.0, C.HomeMarginScale);
        var forceParams = new HomePlayResolver.BasePlayParams(4, new Vector3D(0, 0, 0), C.HomeForceTagSeconds, 0.0, C.HomeMarginScale);

        var tagMargin = HomePlayResolver.Margin(runner, fromBase: 2, situation, Field, C, tagParams);
        var forceMargin = HomePlayResolver.Margin(runner, fromBase: 2, situation, Field, C, forceParams);

        Assert.True(tagMargin > forceMargin, $"タッチのmarginがフォース以下: タッチ{tagMargin} フォース{forceMargin}");
        Assert.Equal(C.HomeTagSeconds - C.HomeForceTagSeconds, tagMargin - forceMargin, precision: 9);
    }

    [Fact]
    public void Steal_DefenseTimeSeconds_IsLongerThanTheOldForceLikeBaseline()
    {
        // タッチ秒を0.10（旧値＝フォースと同じ）に差し替えると守備所要が短くなる＝修正前は際どい盗塁が
        // 送球到達と同時にアウトへ見えていたことを固定する（issue #1139）。
        var catcher = new Player { ArmStrength = 50, Fielding = 50 };
        var withNewTag = StealResolver.DefenseTimeSeconds(catcher, C);
        var withOldForceLikeTag = StealResolver.DefenseTimeSeconds(catcher, C with { TagSeconds = 0.10 });

        Assert.True(withNewTag > withOldForceLikeTag);
        Assert.Equal(C.TagSeconds - 0.10, withNewTag - withOldForceLikeTag, precision: 9);
    }
}
