using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 絵とセーフ/アウトのコールが食い違わない（issue match-picture-vs-safe-out-verdict）。
///
/// <para>「明らかに送球が先に届いているのにアウトにならない」の3つの出どころを塞ぐ:</para>
/// <list type="number">
/// <item>本塁: 送球のアンカーが<b>先頭の走者</b>に付いていた（複数生還プレーで後続が悠々生還。実測 189/2,045）</item>
/// <item>一塁: 矛盾ガードが掛かっていなかった（内野安打の 83.5% で送球が先着して見える）</item>
/// <item>盗塁: 同上（成功50%の場面で送球が 0.48 秒先着する絵）</item>
/// </list>
///
/// <para>いずれも<b>表示専用</b>＝判定・統計帯・決定論ダイジェストは1ビットも動かさない
/// （ガードは既に確定した結果を読んで絵をずらすだけ・乱数不使用）。</para>
/// </summary>
public sealed class PictureVsVerdictTests
{
    private const int Games = 60;
    private static readonly BaserunningCoefficients C = new();
    private static readonly FieldGeometry Field = new();

    // ---- ① 本塁: 送球は「最後に還る走者」に合わせる ----

    /// <summary>
    /// 生還だけのプレー（本塁憤死なし）では、送球は<b>最後に還る走者</b>より後に着く。
    ///
    /// <para>⚠ 本塁憤死を含むプレーは対象外。送球は刺した走者に紐付くのが正しく、その球が
    /// 後続の生還より先に着くのは絵として正しい（＝別の問題。<c>OPEN-QUESTIONS.md</c> Q27 に
    /// 「走塁死の直後に後続が生還する場面をどう扱うか」として記録した。実測 368件中1件）。</para>
    /// </summary>
    [Fact]
    public void HomeThrow_NeverArrivesBeforeAScoringRunner()
    {
        var bad = new List<string>();
        var checkedRunners = 0;
        var skippedPlays = 0;

        foreach (var tl in Timelines())
        {
            var throwAt = ThrowArrival(tl, "home");
            if (throwAt is not { } ta) continue;
            if (tl.Runners.Any(l => l.OutAtEnd && IsHome(l.To))) { skippedPlays++; continue; }

            foreach (var leg in tl.Runners)
            {
                if (leg.OutAtEnd || !IsHome(leg.To) || Dist(leg.From, leg.To) < 0.5) continue;
                checkedRunners++;
                if (ta < leg.T1 - 1e-6)
                    bad.Add($"{tl.Result}: 球が {leg.T1 - ta:F2}s 先に本塁へ着いている"
                            + $"（{leg.Label} 到達{leg.T1:F2} 球{ta:F2}）");
            }
        }

        Assert.True(checkedRunners >= 100, $"本塁送球つきの生還が {checkedRunners} 件しかない＝テストが空回り");
        Assert.True(bad.Count == 0,
            $"送球が先着した生還が {bad.Count}/{checkedRunners} 件（憤死ありで除外 {skippedPlays} プレー）: "
            + string.Join(" / ", bad.Take(5)));
    }

    /// <summary>
    /// 複数生還のプレーで、送球が<b>先頭の走者</b>ではなく最後の走者に合っている（この issue の①そのもの）。
    /// 走者レッグは三塁走者→二塁走者→一塁走者の順に積まれるため、直す前は「最初に見つけたレッグ」＝
    /// いちばん早く還る走者に固定されていた。
    /// </summary>
    [Fact]
    public void HomeThrow_AnchorsToTheLastRunner_OnMultiRunPlays()
    {
        var multi = 0;
        foreach (var tl in Timelines())
        {
            var throwAt = ThrowArrival(tl, "home");
            if (throwAt is not { } ta) continue;
            if (tl.Runners.Any(l => l.OutAtEnd && IsHome(l.To))) continue;

            var arrivals = tl.Runners
                .Where(l => !l.OutAtEnd && IsHome(l.To) && Dist(l.From, l.To) >= 0.5)
                .Select(l => l.T1).ToList();
            if (arrivals.Count < 2) continue;

            multi++;
            Assert.True(ta >= arrivals.Max() - 1e-6,
                $"{tl.Result}: 送球({ta:F2}) が最後の生還({arrivals.Max():F2}) より前＝先頭走者に付いている");
        }
        Assert.True(multi >= 10, $"複数生還のプレーが {multi} 件しかない＝テストが空回り");
    }

    // ---- ② 一塁: 内野安打で送球が先着して見えない ----

    [Fact]
    public void FirstBaseThrow_NeverArrivesBeforeASafeBatter()
    {
        var bad = new List<string>();
        var infieldHits = 0;

        foreach (var tl in Timelines())
        {
            var throwAt = ThrowArrival(tl, "first");
            if (throwAt is not { } ta) continue;
            var batter = tl.Runners.FirstOrDefault(l => l.Label == "打");
            if (batter is null || batter.OutAtEnd) continue;

            infieldHits++;
            if (ta < batter.T1 - 1e-6)
                bad.Add($"{tl.Result}: 球が {batter.T1 - ta:F2}s 先に一塁へ着いている");
        }

        Assert.True(infieldHits >= 30, $"一塁送球つきのセーフが {infieldHits} 件しかない＝テストが空回り");
        Assert.True(bad.Count == 0,
            $"送球が先着した内野安打が {bad.Count}/{infieldHits} 件: {string.Join(" / ", bad.Take(5))}");
    }

    // ---- ③ 盗塁: セーフなら送球は走者より後 ----

    [Theory]
    [InlineData(StealTarget.Second)]
    [InlineData(StealTarget.Third)]
    public void StealThrow_ArrivesAfterASafeRunner(StealTarget target)
    {
        // 走力・捕手の肩を振っても「セーフなのに（タッチが）先着」が出ないこと。
        // ⚠ 見るのは**タッチ**（送球到達＋タッチ時間）。盗塁はフォースでなくタッグプレーなので、
        // 球が走者より先に着くこと自体は正しい絵（着いた球でタッチする間に走者が滑り込む）。
        // 校正項を物理層へ移す前は「送球の到達」で見ていたが、それは 0.20秒のガードで
        // 到達を後ろへ倒していたから成り立っていた条件（issue verdict-margin-into-physical-layer）。
        for (var speed = 30; speed <= 90; speed += 15)
            for (var arm = 30; arm <= 90; arm += 15)
            {
                var tl = TimelineBuilder.BuildSteal(
                    Runner(speed), Catcher(arm), StealResult.Safe, C, Field, target);
                var throwAt = ThrowArrival(tl, target == StealTarget.Third ? "third" : "second");
                var runner = tl.Runners.Single(r => r.Label == "走");
                Assert.NotNull(throwAt);
                var tagAt = throwAt!.Value + C.TagSeconds;
                Assert.True(tagAt > runner.T1,
                    $"走力{speed}・肩{arm}: セーフなのにタッチが {runner.T1 - tagAt:F2}s 先着している");
            }
    }

    [Fact]
    public void StealThrow_StaysAheadOfACaughtRunner()
    {
        var tl = TimelineBuilder.BuildSteal(Runner(40), Catcher(80), StealResult.CaughtStealing, C, Field);
        var throwAt = ThrowArrival(tl, "second");
        var runner = tl.Runners.Single(r => r.Label == "走");
        Assert.NotNull(throwAt);
        Assert.True(throwAt!.Value <= runner.T1 + 1e-9, "アウトなのに送球が走者より後に着いている");
    }

    /// <summary>
    /// セーフ/アウトでずれるのは<b>投げ始め</b>だけで、送球区間の長さ（＝距離÷肩）は動かない
    /// （Issue #334 の回帰なし）。同じ走者・同じ捕手で引き比べ、区間長が一致することで固定する。
    /// </summary>
    [Fact]
    public void StealThrow_KeepsItsFlightLength_WhenTheGuardFires()
    {
        var safe = TimelineBuilder.BuildSteal(Runner(70), Catcher(60), StealResult.Safe, C, Field);
        var caught = TimelineBuilder.BuildSteal(Runner(70), Catcher(60), StealResult.CaughtStealing, C, Field);

        var s = safe.Ball.Single(b => b.Kind == BallSegmentKind.Throw);
        var c = caught.Ball.Single(b => b.Kind == BallSegmentKind.Throw);

        Assert.True(s.T1 > c.T1, "セーフ側でガードが効いていない（前提が崩れている）");
        Assert.Equal(c.T1 - c.T0, s.T1 - s.T0, 9);
    }

    // ---- 補助 ----

    /// <summary>実試合のタイムライン（打球プレーのみ）。</summary>
    private static IEnumerable<PlayTimeline> Timelines()
    {
        for (ulong seed = 1; seed <= Games; seed++)
        {
            var r = GameEngine.Play(BuildTeam("A"), BuildTeam("H"),
                new GameContext { CaptureTimelines = true }, new Xoshiro256Random(seed));
            foreach (var e in r.Log)
            {
                if (e.Timeline is not null) yield return e.Timeline;
            }
        }
    }

    /// <summary>指定の塁へ着く球（送球・保持運び）の到達時刻。無ければ null。</summary>
    private static double? ThrowArrival(PlayTimeline tl, string toBase)
    {
        double? last = null;
        foreach (var b in tl.Ball)
        {
            if (b.ToBase != toBase) continue;
            if (b.Kind != BallSegmentKind.Throw && b.Kind != BallSegmentKind.Carry) continue;
            if (last is null || b.T1 < last) last = b.T1;   // 最初に着いた球で見る
        }
        return last;
    }

    private static bool IsHome(TimelinePoint p) => Math.Abs(p.X) < 0.5 && Math.Abs(p.Z) < 0.5;

    private static double Dist(TimelinePoint a, TimelinePoint b)
        => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));

    private static Player Runner(int speed) => new()
    {
        Position = FieldPosition.CenterField,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = speed, Baserunning = speed, Steal = speed, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

    private static Player Catcher(int arm) => new()
    {
        Position = FieldPosition.Catcher,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 50, ArmStrength = arm, Fielding = 50, Catching = 50,
    };

    private static Player Pos(FieldPosition pos) => new()
    {
        Position = pos,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

    private static Team BuildTeam(string name)
    {
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase), Pos(FieldPosition.SecondBase),
            Pos(FieldPosition.ThirdBase), Pos(FieldPosition.Shortstop), Pos(FieldPosition.LeftField),
            Pos(FieldPosition.CenterField), Pos(FieldPosition.RightField),
            Pos(FieldPosition.Pitcher) with { Name = name + "P", Pitching = PitcherAttributes.LeagueAverage },
        };
        return new Team
        {
            Name = name, BattingOrder = order, PitcherSlot = 8,
            Reserves = new[]
            {
                Pos(FieldPosition.Pitcher) with { Name = name + "R", Pitching = PitcherAttributes.LeagueAverage },
            },
        };
    }
}
