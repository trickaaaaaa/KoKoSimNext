using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// ライブ観戦のスナップショット露出（3カラムのスタメン列＋マッチアップHUD用の観測データ）の受け入れ。
/// すべて観測データ＝試合結果に影響しない（決定論は <see cref="Game.MatchLiveSnapshotDeterminismTests"/> 相当を
/// 既存の EngineDeterminismGate が担保）。ここでは「UIが引く数値がエンジン集計と一致すること」を検証する。
/// </summary>
public sealed class MatchLiveSnapshotTests
{
    private const string PinchHitterName = "代打ヒーロー";

    private static Player Pos(FieldPosition pos, string name, int? sourceId = null) =>
        new()
        {
            Position = pos, Name = name, SourceId = sourceId,
            Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
            Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50,
        };

    // 打順1〜9に識別しやすい名前を付ける。home 側は SourceId を焼き込む（自校相当）。away は null（相手校相当）。
    private static Team Team(string name, bool withSourceIds, bool withBench)
    {
        int? Sid(int i) => withSourceIds ? name.GetHashCode() % 1000 * 10 + i : null;
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher,     name + "1", Sid(1)),
            Pos(FieldPosition.FirstBase,   name + "2", Sid(2)),
            Pos(FieldPosition.SecondBase,  name + "3", Sid(3)),
            Pos(FieldPosition.ThirdBase,   name + "4", Sid(4)),
            Pos(FieldPosition.Shortstop,   name + "5", Sid(5)),
            Pos(FieldPosition.LeftField,   name + "6", Sid(6)),
            Pos(FieldPosition.CenterField, name + "7", Sid(7)),
            Pos(FieldPosition.RightField,  name + "8", Sid(8)),
            Pos(FieldPosition.Pitcher,     name + "P", Sid(9)) with { Pitching = PitcherAttributes.LeagueAverage },
        };
        return new Team
        {
            Name = name, BattingOrder = order, PitcherSlot = 8,
            Bullpen = new[] { Pos(FieldPosition.Pitcher, name + "R", Sid(10)) with { Pitching = PitcherAttributes.LeagueAverage } },
            Bench = withBench
                ? new[] { Pos(FieldPosition.FirstBase, PinchHitterName, withSourceIds ? 777 : null) with { Contact = 60 } }
                : System.Array.Empty<Player>(),
        };
    }

    // シードは「7回に到達し、その後に代打の打順が回ってくる」局面を選んだもの
    // （Issue #24 で打球の塁打数決定が変わり、42 では代打の打順が回る前に試合が終わるようになった）。
    private static MatchProgression NewProg() =>
        new(Team("A", withSourceIds: false, withBench: true),
            Team("H", withSourceIds: true, withBench: true),
            new GameContext { CaptureTimelines = true }, 43UL);

    // 代打シナリオ専用の進行（「7回到達後に代打の打順が回る」局面のシード）。
    // Issue #24 で 42→43、Issue #169 の送球エラー2段階化で rng 消費が変わり 43→1 に更新。
    private static MatchProgression NewPinchHitProg() =>
        new(Team("A", withSourceIds: false, withBench: true),
            Team("H", withSourceIds: true, withBench: true),
            new GameContext { CaptureTimelines = true }, 1UL);

    // スタメン列は打順1〜9で、名前・守備位置・SourceId が現ラインナップと一致する。
    [Fact]
    public void Snapshot_LineupIsBattingOrder_WithIdentity()
    {
        var prog = NewProg();
        for (var i = 0; i < 20 && prog.Advance(); i++) { }

        var snap = prog.Snapshot();
        Assert.Equal(9, snap.Home.Lineup.Count);
        Assert.Equal(9, snap.Away.Lineup.Count);
        Assert.Equal(Enumerable.Range(1, 9), snap.Home.Lineup.Select(s => s.Order));

        // home は自校相当＝SourceId を持つ。away は相手校相当＝null。
        Assert.All(snap.Home.Lineup, s => Assert.NotNull(s.SourceId));
        Assert.All(snap.Away.Lineup, s => Assert.Null(s.SourceId));

        // 名前・守備位置が打順どおり（無交代なら初期打順）。
        Assert.Equal("H1", snap.Home.Lineup[0].Name);
        Assert.Equal(FieldPosition.Catcher, snap.Home.Lineup[0].Position);

        // 控え（野手＝代打候補・投手＝ブルペン）も露出する。
        Assert.Contains(snap.Home.Bench, s => s.Name == PinchHitterName);
        Assert.NotEmpty(snap.Home.Bullpen);
    }

    // 今日の打数・安打がエンジン集計から引ける（UI再計算なし）。現ラインナップ9人＝ボックススコアの部分集合で、
    // 継投も途中出場も無ければ完全一致する。
    [Fact]
    public void Snapshot_TodaysBattingMatchesBoxScore()
    {
        var prog = NewProg();
        while (prog.Advance()) { }          // 完走
        var result = prog.BuildResult();
        var snap = prog.Snapshot();

        var snapAb = snap.Home.Lineup.Sum(s => s.AtBats);
        var snapHits = snap.Home.Lineup.Sum(s => s.Hits);
        var boxAb = result.HomeBatting.Sum(b => b.AtBats);
        var boxHits = result.HomeBatting.Sum(b => b.Hits);

        Assert.True(snapAb > 0, "打数が計上されていない");
        // 現ラインナップは box の部分集合（退いた選手＝継投された先発投手などは含まない）。
        Assert.True(snapAb <= boxAb, $"snapAb={snapAb} > boxAb={boxAb}");
        Assert.True(snapHits <= boxHits, $"snapHits={snapHits} > boxHits={boxHits}");

        // 継投も途中出場も無ければ現ラインナップ＝スタメン9人で完全一致。
        if (result.PitcherChanges == 0 && result.HomeBatting.Count == 9)
        {
            Assert.Equal(boxAb, snapAb);
            Assert.Equal(boxHits, snapHits);
        }
    }

    // 現投手の今日の成績が打席消化で計上され、球数・投球回が最終ボックススコアの先発投手と一致する。
    [Fact]
    public void Snapshot_PitcherTodayMatchesBoxScore()
    {
        var prog = NewProg();
        var sawPitches = false;
        for (var i = 0; i < 30 && prog.Advance(); i++)
        {
            var snap = prog.Snapshot();
            var pitcher = snap.OffenseIsTop ? snap.Home.Pitcher : snap.Away.Pitcher; // 守備側投手
            if (pitcher.Pitches > 0) sawPitches = true;
        }
        Assert.True(sawPitches, "投球数が計上されていない");

        // 完走後、home 先発投手の今日の球数がボックススコアと一致（無交代なら先発1人）。
        while (prog.Advance()) { }
        var result = prog.BuildResult();
        var final = prog.Snapshot();
        var boxStarterPitches = result.HomePitching.Sum(p => p.Pitches);
        // スナップショットは「現投手」1人分。継投が無ければ先発＝現投手で全球数一致。
        if (result.PitcherChanges == 0 && result.HomePitching.Count == 1)
            Assert.Equal(boxStarterPitches, final.Home.Pitcher.Pitches);
    }

    // Current の対戦アイデンティティ（打順・投手名・左右）が埋まり、スナップショットの攻撃側スロットと整合する。
    [Fact]
    public void Current_CarriesMatchupIdentity_AndAlignsWithSnapshot()
    {
        var prog = NewProg();
        Assert.True(prog.Advance());
        var cur = prog.Current!;

        Assert.InRange(cur.BatterOrder, 1, 9);
        Assert.False(string.IsNullOrEmpty(cur.PitcherName));

        var snap = prog.Snapshot();
        Assert.Equal(cur.IsTop, snap.OffenseIsTop);
        Assert.Equal(cur.BatterOrder, snap.CurrentBatterOrder);

        // 攻撃側列の該当打順スロットが、まさに今打った打者。
        var offense = cur.IsTop ? snap.Away.Lineup : snap.Home.Lineup;
        Assert.Equal(cur.BatterName, offense[cur.BatterOrder - 1].Name);
    }

    // 代打を送ると、スナップショットの該当スロットが代打選手へ入れ替わり、退いた選手名が ReplacedName に載る。
    [Fact]
    public void PinchHit_SwapsSlot_AndRecordsReplacedName()
    {
        var prog = NewPinchHitProg();
        while (prog.Advance() && prog.Current!.Inning < 7) { }
        Assert.False(prog.IsFinished, "7回到達前に試合終了（別シードを使う）");

        // 代打前: 該当打順スロットの元選手名を控えておく。
        var before = prog.Snapshot();

        Assert.True(prog.PinchHitUpcoming(offenseIsAway: false, benchIndex: 0), "代打を送れなかった");

        // 代打が実際に打席に立つまで進める。
        var appeared = false;
        while (prog.Advance())
        {
            if (prog.Current!.BatterName == PinchHitterName) { appeared = true; break; }
        }
        Assert.True(appeared, "代打が打席に現れない");

        var after = prog.Snapshot();
        var slot = after.Home.Lineup.FirstOrDefault(s => s.Name == PinchHitterName);
        Assert.NotNull(slot);
        Assert.False(string.IsNullOrEmpty(slot!.ReplacedName)); // 退いた選手名が載っている
        // 元の打順に元選手はもういない。
        Assert.DoesNotContain(before.Home.Lineup.Select(s => s.Name),
            n => n == PinchHitterName); // 代打前には居なかった
    }

    // 設計書16 §4-2 LineScorePanel の観測データ。完走後のラインスコアがボックススコアと一致すること
    // （UI側で回別得点を組み立てさせない＝数値はエンジン集計から引く、の担保）。
    [Fact]
    public void Snapshot_LineScoreMatchesResult()
    {
        var prog = NewProg();
        while (prog.Advance()) { }
        var result = prog.BuildResult();
        var snap = prog.Snapshot();

        Assert.Equal(result.AwayName, snap.AwayLine.Name);
        Assert.Equal(result.HomeName, snap.HomeLine.Name);
        Assert.Equal(result.AwayRuns, snap.AwayLine.Runs);
        Assert.Equal(result.HomeRuns, snap.HomeLine.Runs);
        Assert.Equal(result.AwayHits, snap.AwayLine.Hits);
        Assert.Equal(result.HomeHits, snap.HomeLine.Hits);
        Assert.Equal(result.AwayErrors, snap.AwayLine.Errors);
        Assert.Equal(result.HomeErrors, snap.HomeLine.Errors);
        Assert.Equal(result.AwayLineScore, snap.AwayLine.InningRuns);
        Assert.Equal(result.HomeLineScore, snap.HomeLine.InningRuns);

        // 試合終了後は進行中の半回が無いので、回別得点の合計＝総得点（PendingRuns は 0）。
        Assert.Equal(0, snap.AwayLine.PendingRuns);
        Assert.Equal(0, snap.HomeLine.PendingRuns);
        Assert.Equal(result.AwayRuns, snap.AwayLine.InningRuns.Sum());
    }

    // 試合中は「確定した半回の得点（InningRuns）＋進行中の半回の得点（PendingRuns）＝総得点 R」が常に成り立つ。
    // 進行中の半回はまだ InningRuns に載らないので、この不変式が破れると掲示板の合計が狂う。
    [Fact]
    public void Snapshot_LineScoreSplitsPendingHalfInning()
    {
        var prog = NewProg();
        var checkedMidGame = 0;

        while (prog.Advance())
        {
            var snap = prog.Snapshot();
            Assert.Equal(snap.AwayLine.Runs, snap.AwayLine.InningRuns.Sum() + snap.AwayLine.PendingRuns);
            Assert.Equal(snap.HomeLine.Runs, snap.HomeLine.InningRuns.Sum() + snap.HomeLine.PendingRuns);

            // 守備側に進行中の半回は無い（＝PendingRuns は必ず0）。
            if (snap.OffenseIsTop) Assert.Equal(0, snap.HomeLine.PendingRuns);
            else Assert.Equal(0, snap.AwayLine.PendingRuns);

            checkedMidGame++;
        }

        Assert.True(checkedMidGame > 20, $"試合中の検証回数が少なすぎる: {checkedMidGame}");
    }
}
