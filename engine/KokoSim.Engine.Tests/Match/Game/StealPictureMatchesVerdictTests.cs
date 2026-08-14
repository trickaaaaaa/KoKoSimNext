using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 盗塁の絵と判定が一致する（issue: 判定境界の校正項を物理層へ移す）。
///
/// <para>従来は判定が <c>logistic((守備 − 走者 + StealSuccessBias(0.38)) / 0.22)</c> の抽選で、
/// バイアス0.38は<b>判定の秒だけを動かして絵の秒には出ない</b>ため、成功50%の場面でも
/// 「捕手の送球が走者到達の0.48秒前（0.38＋タッチ0.10）に塁へ届く」絵になっていた。
/// バイアスを物理層の秒（走者のジャンプ0.19／捕手の立ち上がり0.19）へ移し、運は
/// <b>ゆらぎ[s]</b>として引いて走者・守備の秒へ振り分ける＝<b>絵の先着＝判定</b>になる。</para>
/// </summary>
public sealed class StealPictureMatchesVerdictTests
{
    private readonly ITestOutputHelper _out;
    public StealPictureMatchesVerdictTests(ITestOutputHelper output) => _out = output;

    private static readonly BaserunningCoefficients C = new();

    /// <summary>
    /// 送球のある盗塁（二盗・三盗）で、絵の先着（送球到達＋タッチ vs 走者到達）が判定と1件も食い違わない。
    /// </summary>
    [Fact]
    public void ThrowArrivalOrder_NeverContradictsTheCall()
    {
        int safes = 0, outs = 0, contradictions = 0;
        var worst = "";

        foreach (var (play, safe) in StealPlays())
        {
            var leg = play.Runners.FirstOrDefault(r => r.Label == "走");
            var thrown = play.Ball.OfType<EndpointBallSegment>().FirstOrDefault(b => b.Kind == BallSegmentKind.Throw);
            if (leg is null || thrown is null) continue;   // 本盗は送球区間そのものが無い
            var runnerArrive = leg.Segs[^1].T1;
            var defenseArrive = thrown.T1 + C.TagSeconds;

            if (safe) safes++; else outs++;
            if (safe == runnerArrive < defenseArrive) continue;
            contradictions++;
            if (worst.Length == 0)
                worst = $"{(safe ? "セーフ" : "アウト")}なのに 走者{runnerArrive:F2}s / 守備{defenseArrive:F2}s";
        }

        _out.WriteLine($"送球のある盗塁 {safes + outs} 件（成功 {safes} / 盗塁死 {outs}）＝食い違い {contradictions} 件 {worst}");
        Assert.True(safes >= 50 && outs >= 20, $"母集団が薄い（成功{safes}/盗塁死{outs}）＝テストが空回り");
        Assert.Equal(0, contradictions);
    }

    /// <summary>
    /// 際どさが一様でない（＝表示側ガードで「走者到達＋0.20秒」へ潰していた頃の症状が戻っていない）。
    /// ゆらぎが実際の秒として払われるので、勝ち負けの量はプレーごとに散る。
    /// </summary>
    [Fact]
    public void Margins_AreNotAllTheSame()
    {
        var margins = new List<double>();
        foreach (var (play, safe) in StealPlays())
        {
            if (!safe) continue;
            var leg = play.Runners.FirstOrDefault(r => r.Label == "走");
            var thrown = play.Ball.OfType<EndpointBallSegment>().FirstOrDefault(b => b.Kind == BallSegmentKind.Throw);
            if (leg is null || thrown is null) continue;
            margins.Add(thrown.T1 + C.TagSeconds - leg.Segs[^1].T1);   // 正＝走者が勝った量
        }

        Assert.True(margins.Count >= 50, $"盗塁成功が {margins.Count} 件しか採れていない");
        margins.Sort();
        var p10 = margins[margins.Count / 10];
        var p90 = margins[margins.Count * 9 / 10];
        _out.WriteLine($"走者の勝ち幅[s]: p10 {p10:F2} / p50 {margins[margins.Count / 2]:F2} / p90 {p90:F2}");

        Assert.True(p10 > 0, "セーフなのに走者が勝っていない（判定と絵が食い違っている）");
        Assert.True(p90 - p10 > 0.15, $"勝ち幅が {p90 - p10:F2}s に潰れている＝際どさの濃淡が失われている");
    }

    /// <summary>
    /// ゆらぎの振り分けは物理の上限を破らない: 走者は<b>自分の脚より速く走らない</b>し、
    /// 捕手は<b>自分の肩より速く投げない</b>（どちらも取り分に上限があり、超えた分は相手側の失敗として出る）。
    /// </summary>
    [Fact]
    public void Luck_NeverMakesAnyoneFasterThanTheirBody()
    {
        var runner = new Player { Speed = 60, Steal = 60 };
        var catcher = new Player { Position = FieldPosition.Catcher, ArmStrength = 55, Fielding = 55 };
        var runnerTime = StealResolver.RunnerTimeSeconds(runner, C);
        var defenseTime = StealResolver.DefenseTimeSeconds(catcher, C);
        var rng = new Xoshiro256Random(4242);

        double worstRunnerGain = 0, worstDefenseGain = 0;
        for (var i = 0; i < 5000; i++)
        {
            var v = StealResolver.ResolveVerdict(runner, catcher, C, rng);
            // 判定はこの2つの秒の比較そのもの
            Assert.Equal(v.Safe, v.RunnerSeconds < v.DefenseSeconds);
            worstRunnerGain = System.Math.Max(worstRunnerGain, runnerTime - v.RunnerSeconds);
            worstDefenseGain = System.Math.Max(worstDefenseGain, defenseTime - v.DefenseSeconds);
        }

        _out.WriteLine($"取り分の最大: 走者 {worstRunnerGain:F2}s / 守備 {worstDefenseGain:F2}s（上限 0.20）");
        Assert.True(worstRunnerGain <= 0.20 + 1e-9, $"走者が {worstRunnerGain:F2}s も速くなっている");
        Assert.True(worstDefenseGain <= 0.20 + 1e-9, $"守備が {worstDefenseGain:F2}s も速くなっている");
        // 上限まで使う場面が実際に出る（＝上限が形骸化していない）
        Assert.True(worstRunnerGain > 0.19 && worstDefenseGain > 0.19, "取り分の上限に一度も届いていない");
    }

    /// <summary>
    /// 校正項は<b>移しただけ</b>で成功率を変えていない: 旧係数（バイアス0.38・ジャンプ0・立ち上がり0）と
    /// 新係数（バイアス0・ジャンプ0.19・立ち上がり0.19）で <see cref="StealResolver.SuccessProbability"/> が
    /// 完全一致する（二盗・三盗）。＝帯も決定論ダイジェストも動かないことの根拠。
    ///
    /// <para>本盗だけは送球区間が無い（＝立ち上がりが乗らない）ので 0.19 だけ走者有利へ動くが、
    /// 元々クランプ下限 1% に張り付いている場面なので確率は動かない。それも併せて固定する。</para>
    /// </summary>
    [Fact]
    public void MovingTheBias_DoesNotChangeAnySuccessProbability()
    {
        var old = C with { StealSuccessBias = 0.38, StealJumpCreditSeconds = 0.0, CatcherPopRiseSeconds = 0.0 };
        foreach (var legs in new[] { 20, 40, 55, 70, 90, 100 })
            foreach (var arm in new[] { 20, 50, 75, 95 })
                foreach (var fielding in new[] { 20, 50, 90 })
                    foreach (var pitchout in new[] { false, true })
                        foreach (var start in new[] { StartType.Normal, StartType.Gamble })
                        {
                            var runner = new Player { Speed = legs, Steal = legs };
                            var catcher = new Player
                                { Position = FieldPosition.Catcher, ArmStrength = arm, Fielding = fielding };
                            foreach (var target in new[] { StealTarget.Second, StealTarget.Third })
                                Assert.Equal(
                                    StealResolver.SuccessProbability(runner, catcher, old, pitchout, start, target),
                                    StealResolver.SuccessProbability(runner, catcher, C, pitchout, start, target), 12);
                            // 本盗は両方ともクランプ下限（＝結果は不変）
                            Assert.Equal(0.01,
                                StealResolver.SuccessProbability(runner, catcher, C, pitchout, start, StealTarget.Home), 12);
                        }
    }

    /// <summary>
    /// 物理層へ移した先の絶対値が<b>高校生の実測帯</b>に入る（校正項に隠れていた頃はどちらも外れていた）。
    /// ポップタイム＝捕球から二塁到達まで。走者＝一塁の一次リードから二塁到達まで。
    /// </summary>
    [Fact]
    public void PopTimeAndRunnerTime_AreRealisticNow()
    {
        var catcher = new Player { Position = FieldPosition.Catcher, ArmStrength = 50, Fielding = 50 };
        var pop = StealResolver.DefenseTimeSeconds(catcher, C) - C.PitcherQuickSeconds - C.TagSeconds;
        var runner = StealResolver.RunnerTimeSeconds(new Player { Speed = 50, Steal = 50 }, C);
        _out.WriteLine($"ポップタイム {pop:F2}s ／ 一塁→二塁 {runner:F2}s");

        Assert.InRange(pop, 2.00, 2.30);      // 高校生の実測 2.0〜2.3（旧 1.91 はプロの好捕手の値）
        Assert.InRange(runner, 3.60, 3.90);   // 高校生の実測 3.6〜3.8（旧 3.94 は遅すぎ）
    }

    // ---- 補助 ----

    /// <summary>盗塁が起きた球の再生データ（<see cref="LivePitch.StealPlay"/>）を集める。</summary>
    private static IEnumerable<(PlaybackPlay Play, bool Safe)> StealPlays()
    {
        var ctx = new GameContext { CaptureTimelines = true, CapturePitches = true };
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var prog = new MatchProgression(Team("A"), Team("H", new AlwaysStealBrain()), ctx, seed);
            LivePitch? prev = null;
            while (prog.AdvancePitch() != AdvancePitchResult.Finished)
            {
                var last = prog.LastPitch;
                if (last is null || ReferenceEquals(last, prev)) continue;
                prev = last;
                if (last.StealPlay is not { } play) continue;
                // 盗塁死は走者レッグの終端がアウト＝結果はここから読む（判定側の写し）。
                var leg = play.Runners.FirstOrDefault(r => r.Label == "走");
                if (leg is null || leg.Segs.Count == 0) continue;
                yield return (play, !leg.Segs[^1].OutAtEnd);
            }
        }
    }

    private static Player Pos(FieldPosition pos, int legs = 55) => new()
    {
        Position = pos,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = legs, ArmStrength = 50, Fielding = 50, Catching = 50, Steal = legs,
    };

    /// <summary>走力をばらけさせた打線（盗塁成功も盗塁死も母集団に入るようにする）。</summary>
    private static Team Team(string name, ITacticsBrain? tactics = null)
    {
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher, 40), Pos(FieldPosition.FirstBase, 85),
            Pos(FieldPosition.SecondBase, 70), Pos(FieldPosition.ThirdBase, 30),
            Pos(FieldPosition.Shortstop, 80), Pos(FieldPosition.LeftField, 50),
            Pos(FieldPosition.CenterField, 90), Pos(FieldPosition.RightField, 60),
            Pos(FieldPosition.Pitcher, 45) with { Name = name + "P", Pitching = PitcherAttributes.LeagueAverage },
        };
        return new Team
        {
            Name = name, BattingOrder = order, PitcherSlot = 8, Tactics = tactics,
            Reserves = new[]
            {
                Pos(FieldPosition.Pitcher, 45) with { Name = name + "R", Pitching = PitcherAttributes.LeagueAverage },
            },
        };
    }

    /// <summary>一塁走者・二塁空きなら必ず走るブレイン（母集団を作るためだけの采配）。</summary>
    private sealed class AlwaysStealBrain : ITacticsBrain, IPitchTacticsBrain
    {
        public OffensiveSign CallOffense(in TacticsSituation s, IRandomSource rng) => OffensiveSign.Swing;
        public DefensiveTactics CallDefense(in TacticsSituation s, IRandomSource rng) => DefensiveTactics.Default;
        public bool CallOffenseTimeout(in TacticsSituation s, IRandomSource rng) => false;
        public bool CallDefenseTimeout(in TacticsSituation s, IRandomSource rng) => false;
        public Player? CallPinchHit(in SubstitutionSituation s, IRandomSource rng) => null;
        public (Player Runner, Player Sub)? CallPinchRun(in SubstitutionSituation s, IRandomSource rng) => null;
        public (Player Out, Player Sub)? CallDefensiveSub(in SubstitutionSituation s, IRandomSource rng) => null;
        public PitchingChangeDecision? CallPitchingChange(in PitchingChangeSituation s, IRandomSource rng)
            => s.FatigueTriggered || s.AtWeeklyLimit ? new PitchingChangeDecision(PitchingChangeReason.Fatigue) : null;

        public PitchTacticsDirective? CallPitchAction(in PitchTacticsSituation s, IRandomSource rng)
            => s.Base.OnFirst is not null && s.Base.OnSecond is null
                ? new PitchTacticsDirective(StealAttempt: StartType.Normal)
                : null;
    }
}
