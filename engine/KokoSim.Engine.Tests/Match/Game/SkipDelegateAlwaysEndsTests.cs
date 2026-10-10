using System;
using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 「スキップ（委任）」は<b>いつ押しても</b>試合を終わらせる（オーナー報告 2026-08-14）。
///
/// <para>MatchLiveController.OnSkip は <c>_paused=false</c>・ポップアップ閉じを**先に**やってから
/// <see cref="MatchProgression.SkipDelegateToAi"/> を呼ぶ。ここが例外で落ちると1球ポンプを起こす者が
/// 誰も居なくなり「UIは押せるが投手が投げず試合も終わらない」という止まり方になる
/// （フリーズでも例外ダイアログでもないので原因が絵に出ない）。</para>
///
/// <para>守るべき不変条件は「<b>打席内のどの球で押しても</b>例外なく終局する」。打席境界だけを試すと
/// 打席途中の窓（設計書15 の Pitch 窓）で押した場合が抜ける＝実際そこだけが壊れていた。</para>
/// </summary>
public sealed class SkipDelegateAlwaysEndsTests
{
    private static Team TeamOf(double strength, string name, ulong seed, bool withBrain)
    {
        var t = StrengthTeamFactory.Create(strength, name, new Xoshiro256Random(seed),
            rosterCoeff: new RosterCoefficients());
        return withBrain ? t with { Tactics = new AiTacticsBrain(AiProfile.Delegated(60, 5)) } : t;
    }

    /// <summary>
    /// 1球ごとの窓すべてを押下点として総当たりする。コールド有効（県大会と同じ）。
    /// <paramref name="managerHasBrain"/>=false が実プレイの自校（人間が采配＝Tactics は null）。
    /// 相手側の brain の有無も振る＝練習試合・デモ観戦のように両方 null の配置も通す。
    /// </summary>
    [Theory]
    [InlineData(false, true)]    // 実プレイ（自校=人間 / 相手=AI）
    [InlineData(false, false)]   // 両方 brain 無し
    [InlineData(true, true)]     // 両方 AI（観戦）
    public void SkipAtEveryPitchWindow_AlwaysFinishes(bool managerHasBrain, bool opponentHasBrain)
    {
        var ctx = new GameContext { MercyRuleEnabled = true, CaptureTimelines = true, CapturePitches = true };
        var failures = new List<string>();

        for (var seed = 0; seed < 4; seed++)
        {
            var s = seed;
            MatchProgression New() => new(
                TeamOf(35.0, "自校", (ulong)s, managerHasBrain),
                TeamOf(45.0, "相手", (ulong)s + 7777, opponentHasBrain),
                ctx, 0x54EEDUL + (ulong)s);

            var count = 0;
            var probe = New();
            while (probe.AdvancePitch() != AdvancePitchResult.Finished) count++;

            for (var stop = 0; stop <= count; stop++)
            {
                var prog = New();
                var finished = false;
                for (var i = 0; i < stop; i++)
                    if (prog.AdvancePitch() == AdvancePitchResult.Finished) { finished = true; break; }
                if (finished) continue;

                try
                {
                    Assert.NotNull(prog.SkipDelegateToAi(managerIsAway: true, 60));
                }
                catch (Exception e)
                {
                    failures.Add($"seed={seed} 窓={stop}/{count}: {e.GetType().Name} {e.Message}");
                }
            }
        }

        Assert.True(failures.Count == 0,
            $"スキップで例外が出た（{failures.Count}件）:\n"
            + string.Join("\n", failures.GetRange(0, Math.Min(5, failures.Count))));
    }
}
