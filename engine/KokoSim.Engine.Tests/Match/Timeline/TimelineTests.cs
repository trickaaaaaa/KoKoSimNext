using System.Collections.Generic;
using System.Linq;
using KokoSim.Config;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Balance;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// タイムライン出力契約（CHANGELOG 32-33）。
/// 「エンジンが座標＋時刻付きイベント列を出力し、UIは再生するだけ」の検証:
/// 時刻単調性・ボール連続性・9人の役割付与・結果への非干渉（オン/オフ同結果）・決定論。
/// </summary>
public sealed class TimelineTests
{
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
            Bullpen = new[] { Pos(FieldPosition.Pitcher) with { Name = name + "R", Pitching = PitcherAttributes.LeagueAverage } },
        };
    }

    // --- 契約の核: タイムライン捕捉は試合結果に一切影響しない（オン/オフ同シード同結果） ---

    [Fact]
    public void Capture_DoesNotAffectGameOutcome()
    {
        var off = new GameContext();
        var on = new GameContext { CaptureTimelines = true };
        for (ulong s = 0; s < 10; s++)
        {
            var a = GameEngine.Play(BuildTeam("A"), BuildTeam("H"), off, new Xoshiro256Random(s));
            var b = GameEngine.Play(BuildTeam("A"), BuildTeam("H"), on, new Xoshiro256Random(s));
            Assert.Equal(a.AwayRuns, b.AwayRuns);
            Assert.Equal(a.HomeRuns, b.HomeRuns);
            Assert.Equal(a.TotalPitches, b.TotalPitches);
        }
    }

    [Fact]
    public void CaptureOff_ProducesNoTimelines()
    {
        var r = GameEngine.Play(BuildTeam("A"), BuildTeam("H"), new GameContext(), new Xoshiro256Random(3));
        Assert.All(r.Log, e => Assert.Null(e.Timeline));
    }

    // --- 捕捉オン: インプレー打席にタイムラインが付き、構造が正しい ---

    private static GameResult CapturedGame(ulong seed)
        => GameEngine.Play(BuildTeam("A"), BuildTeam("H"),
            new GameContext { CaptureTimelines = true }, new Xoshiro256Random(seed));

    [Fact]
    public void CaptureOn_InPlayEntriesHaveTimelines()
    {
        var r = CapturedGame(7);
        var inPlay = r.Log.Where(e => e.Result is not (PlateAppearanceResult.Strikeout or PlateAppearanceResult.Walk
            or PlateAppearanceResult.HitByPitch)).ToList();
        Assert.NotEmpty(inPlay);
        Assert.All(inPlay, e => Assert.NotNull(e.Timeline));
        // 三振・四球・死球は打球プレーが無いのでタイムラインなし。
        Assert.All(r.Log.Where(e => e.Result is PlateAppearanceResult.Strikeout or PlateAppearanceResult.Walk
                or PlateAppearanceResult.HitByPitch),
            e => Assert.Null(e.Timeline));
    }

    [Fact]
    public void Timeline_TimesAreMonotonic_AndWithinDuration()
    {
        var r = CapturedGame(11);
        foreach (var tl in r.Log.Select(e => e.Timeline).Where(t => t is not null)!)
        {
            Assert.True(tl!.Duration > 0);
            Assert.InRange(tl.ResolvedAt, 0, tl.Duration);
            foreach (var b in tl.Ball)
            {
                Assert.True(b.T1 >= b.T0, $"ボール区間が逆行: {b.Kind} {b.T0}→{b.T1}");
                Assert.True(b.T1 <= tl.Duration + 1e-6);
            }
            foreach (var m in tl.Moves) Assert.True(m.T1 >= m.T0);
            foreach (var leg in tl.Runners) Assert.True(leg.T1 >= leg.T0);
            // 先頭は必ず投球セグメント。
            Assert.Equal(BallSegmentKind.Pitch, tl.Ball[0].Kind);
            // 打者走者レッグが必ず存在。
            Assert.Contains(tl.Runners, leg => leg.Label == "打");
        }
    }

    [Fact]
    public void Timeline_FormationAssignsRoles_IncludingFieldBall()
    {
        var r = CapturedGame(13);
        var withMoves = r.Log.Select(e => e.Timeline).Where(t => t is not null && t!.Moves.Count > 0).ToList();
        Assert.NotEmpty(withMoves);
        foreach (var tl in withMoves)
        {
            // 陣形ルール表から複数野手が動く（打球処理＋カバー/中継/バックアップ）。
            Assert.True(tl!.Moves.Count >= 3, $"陣形の役割が少なすぎる: {tl.Moves.Count}");
        }
        // 実捕球者の移動距離が0.5m未満（定位置直撃のコンバッカー等）だと FieldBall の move 自体を出さない
        // （TimelineBuilder.BuildBattedBall の意図的な仕様、CoverBase/Backupだけの陣形になり得る）。
        // このテストが検証したいのは「陣形の複数野手ムーブ機構自体が機能している」ことなので、
        // 1試合の中で少なくとも1回 FieldBall が付与されていれば十分とする（全打球への要求は過剰）。
        Assert.Contains(withMoves, tl => tl!.Moves.Any(m => m.Task == FielderTask.FieldBall));
    }

    [Fact]
    public void Timeline_IsDeterministic()
    {
        var a = CapturedGame(17);
        var b = CapturedGame(17);
        Assert.Equal(a.Log.Count, b.Log.Count);
        for (var i = 0; i < a.Log.Count; i++)
        {
            var ta = a.Log[i].Timeline;
            var tb = b.Log[i].Timeline;
            Assert.Equal(ta is null, tb is null);
            if (ta is null) continue;
            Assert.Equal(ta.Duration, tb!.Duration, 9);
            Assert.Equal(ta.Ball.Count, tb.Ball.Count);
            Assert.Equal(ta.Moves.Count, tb.Moves.Count);
            Assert.Equal(ta.Runners.Count, tb.Runners.Count);
        }
    }

    // --- 陣形ルール表 ---

    [Theory]
    [InlineData(-30.0, 80.0, BallZone.OutfieldLeft)]
    [InlineData(0.0, 95.0, BallZone.OutfieldCenter)]
    [InlineData(30.0, 80.0, BallZone.OutfieldRight)]
    [InlineData(-20.0, 30.0, BallZone.InfieldLeft)]
    [InlineData(20.0, 30.0, BallZone.InfieldRight)]
    [InlineData(0.0, 8.0, BallZone.Bunt)]
    public void FormationTable_ClassifiesZones(double bearing, double range, BallZone expected)
    {
        Assert.Equal(expected, FormationTable.Classify(bearing, range));
    }

    [Fact]
    public void FormationTable_DefaultCoversAllZones()
    {
        foreach (BallZone zone in System.Enum.GetValues(typeof(BallZone)))
        {
            var rule = FormationTable.Default.Lookup(zone, runnersOn: false);
            Assert.NotNull(rule);
            Assert.Equal(9, rule!.Assignments.Count); // 9人全員に役割
        }
    }

    [Fact]
    public void FormationsYaml_LoadsAndMatchesDefaultShape()
    {
        var path = BalanceRegressionTests.FindDataFile("defensive-formations.yaml");
        var table = FormationsLoader.LoadFromFile(path);
        foreach (BallZone zone in System.Enum.GetValues(typeof(BallZone)))
        {
            var rule = table.Lookup(zone, runnersOn: true);
            Assert.NotNull(rule);
            Assert.Equal(9, rule!.Assignments.Count);
        }
    }

    // --- 磨き込み①: ゴロは地を這うロール／実況の具体化（表示専用・判定非干渉） ---

    private static FieldingPlay GrounderOut() => new()
    {
        Result = BattedBallResult.Out,
        LandingX = -9.0, LandingZ = 30.0, HangTimeSeconds = 0.3, ApexHeightM = 0.4,
        RangeM = 31.3, BearingDeg = -16.0, FielderRole = FieldPosition.Shortstop,
        FieldedAtSeconds = 1.4, ThrowArriveSeconds = 3.0, BatterToFirstSeconds = 4.2, IsFly = false,
    };

    [Fact]
    public void Grounder_BallIsRoll_NotFly_AndThrowStartsWhereItWasFielded()
    {
        var field = new FieldGeometry();
        var play = GrounderOut();
        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

        Assert.Equal(BallSegmentKind.Pitch, tl.Ball[0].Kind);
        // ゴロは放物線(Flight)を出さない。地を這うロールで表現する。
        Assert.DoesNotContain(tl.Ball, b => b.Kind == BallSegmentKind.Flight);
        var roll = Assert.Single(tl.Ball, b => b.Kind == BallSegmentKind.Roll);
        // 本塁から野手処理点(landing)まで転がる。
        Assert.Equal(0.0, roll.From.X, 6);
        Assert.Equal(0.0, roll.From.Z, 6);
        Assert.Equal(play.LandingX, roll.To.X, 6);
        Assert.Equal(play.LandingZ, roll.To.Z, 6);
        // 一塁送球はロール終端（＝捕球点）から連続して始まる。
        var throwSeg = Assert.Single(tl.Ball, b => b.Kind == BallSegmentKind.Throw);
        Assert.Equal(roll.To.X, throwSeg.From.X, 6);
        Assert.Equal(roll.To.Z, throwSeg.From.Z, 6);
        Assert.True(throwSeg.T0 >= roll.T1 - 1e-6, "送球がロール終端より前に始まっている");
    }

    [Fact]
    public void FlyBall_StillUsesParabolaFlight()
    {
        var field = new FieldGeometry();
        var play = new FieldingPlay
        {
            Result = BattedBallResult.Out, LandingX = 0, LandingZ = 88, HangTimeSeconds = 3.5, ApexHeightM = 25,
            RangeM = 88, BearingDeg = 0, FielderRole = FieldPosition.CenterField,
            FieldedAtSeconds = 3.5, BatterToFirstSeconds = 4.2, IsFly = true,
        };
        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

        Assert.Contains(tl.Ball, b => b.Kind == BallSegmentKind.Flight);
        Assert.DoesNotContain(tl.Ball, b => b.Kind == BallSegmentKind.Roll);
    }

    [Theory]
    [InlineData(BattedBallResult.Out, false, FieldPosition.Shortstop, false, "ショートゴロ")]
    [InlineData(BattedBallResult.Out, true, FieldPosition.CenterField, false, "センターフライ")]
    [InlineData(BattedBallResult.Single, false, FieldPosition.LeftField, false, "レフト前ヒット")]
    [InlineData(BattedBallResult.Single, false, FieldPosition.Shortstop, true, "内野安打")]
    [InlineData(BattedBallResult.Double, false, FieldPosition.RightField, false, "ライトへの二塁打")]
    [InlineData(BattedBallResult.Error, false, FieldPosition.Shortstop, false, "ショートのエラー")]
    [InlineData(BattedBallResult.HomeRun, true, null, false, "ホームラン")]
    public void DescribeResult_IsSpecific(
        BattedBallResult result, bool isFly, FieldPosition? role, bool infieldThrow, string expected)
    {
        var play = new FieldingPlay
        {
            Result = result, LandingX = 0, LandingZ = 40, HangTimeSeconds = 1, ApexHeightM = 1,
            RangeM = 40, BearingDeg = 0, FielderRole = role, IsFly = isFly,
            ThrowArriveSeconds = infieldThrow ? 3.0 : (double?)null, BatterToFirstSeconds = 4.2,
        };
        Assert.Equal(expected, TimelineBuilder.DescribeResult(play));
    }

    // --- F1 Slice 2: 併殺(6-4-3)の送球連鎖（野手→二塁→一塁）。表示専用・結果不変・乱数不使用。 ---

    [Fact]
    public void DoublePlayThrows_ReplacesSingleThrowWithTwo_FielderToSecondToFirst()
    {
        var field = new FieldGeometry();
        var play = GrounderOut();
        var baseTl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: true, TimelineBuilder.DescribeResult(play));
        // 差し替え前は「野手→一塁」1本だけ。
        Assert.Single(baseTl.Ball, b => b.Kind == BallSegmentKind.Throw);
        var batterArrive = baseTl.Runners.Single(r => r.Label == "打").T1;

        var tl = TimelineBuilder.AppendDoublePlayThrows(baseTl, play, field, batterArrive);

        var throws = tl.Ball.Where(b => b.Kind == BallSegmentKind.Throw).ToList();
        Assert.Equal(2, throws.Count);
        var second = new TimelinePoint(field.SecondBase.X, field.SecondBase.Z);
        var first = new TimelinePoint(field.FirstBase.X, field.FirstBase.Z);
        // 1本目: 野手処理点(=捕球点)→二塁。
        Assert.Equal(play.LandingX, throws[0].From.X, 6);
        Assert.Equal(play.LandingZ, throws[0].From.Z, 6);
        Assert.Equal(second.X, throws[0].To.X, 6);
        Assert.Equal(second.Z, throws[0].To.Z, 6);
        // 2本目: 二塁→一塁。
        Assert.Equal(second.X, throws[1].From.X, 6);
        Assert.Equal(second.Z, throws[1].From.Z, 6);
        Assert.Equal(first.X, throws[1].To.X, 6);
        Assert.Equal(first.Z, throws[1].To.Z, 6);
    }

    [Fact]
    public void DoublePlayThrows_TimesAreContinuousAndMonotonic()
    {
        var field = new FieldGeometry();
        var play = GrounderOut();
        var baseTl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: true, TimelineBuilder.DescribeResult(play));
        var batterArrive = baseTl.Runners.Single(r => r.Label == "打").T1;

        var tl = TimelineBuilder.AppendDoublePlayThrows(baseTl, play, field, batterArrive);

        var roll = tl.Ball.Single(b => b.Kind == BallSegmentKind.Roll);
        var throws = tl.Ball.Where(b => b.Kind == BallSegmentKind.Throw).ToList();
        // ロール終端(=捕球点)が1本目の始点。時刻もロール終端以降。
        Assert.Equal(roll.To.X, throws[0].From.X, 6);
        Assert.Equal(roll.To.Z, throws[0].From.Z, 6);
        Assert.True(throws[0].T0 >= roll.T1 - 1e-6, "1本目がロール終端より前に始まっている");
        // 1本目終端≈2本目始点（握り替え分だけ後・連続）。
        Assert.True(throws[1].T0 >= throws[0].T1 - 1e-6, "2本目が1本目到達より前に始まっている");
        Assert.True(throws[1].T0 - throws[0].T1 <= 0.25, "握り替えが長すぎる（連続でない）");
        // 二塁到達 < 一塁到達。
        Assert.True(throws[0].T1 < throws[1].T1, "二塁より先に一塁へ到達している");
        // 全ボール区間が単調かつ Duration 内。
        foreach (var b in tl.Ball)
        {
            Assert.True(b.T1 >= b.T0, $"区間が逆行: {b.Kind}");
            Assert.True(b.T1 <= tl.Duration + 1e-6, $"区間が Duration を超過: {b.Kind}");
        }
    }

    [Fact]
    public void DoublePlayThrows_FlyBall_LeavesTimelineUnchanged()
    {
        var field = new FieldGeometry();
        var play = new FieldingPlay
        {
            Result = BattedBallResult.Out, LandingX = 0, LandingZ = 88, HangTimeSeconds = 3.5, ApexHeightM = 25,
            RangeM = 88, BearingDeg = 0, FielderRole = FieldPosition.CenterField,
            FieldedAtSeconds = 3.5, BatterToFirstSeconds = 4.2, IsFly = true,
        };
        var baseTl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: true, TimelineBuilder.DescribeResult(play));

        var tl = TimelineBuilder.AppendDoublePlayThrows(baseTl, play, field, batterArriveAnchor: 5.0);

        // フライは併殺の送球連鎖対象外＝差し替えせず同一タイムラインを返す。
        Assert.Same(baseTl, tl);
    }

    // --- F1 Slice 3: GameEngine 結線（併殺で二塁→一塁の2連続送球が描かれる） ---

    private static bool Near(TimelinePoint a, TimelinePoint b) =>
        System.Math.Abs(a.X - b.X) < 0.5 && System.Math.Abs(a.Z - b.Z) < 0.5;

    [Fact]
    public void DoublePlay_InGame_DrawsSecondThenFirstThrowChain()
    {
        var field = new FieldGeometry();
        var second = new TimelinePoint(field.SecondBase.X, field.SecondBase.Z);
        var first = new TimelinePoint(field.FirstBase.X, field.FirstBase.Z);

        var found = 0;
        for (ulong s = 0; s < 300 && found == 0; s++)
        {
            var r = CapturedGame(s);
            foreach (var tl in r.Log.Select(e => e.Timeline).Where(t => t is not null)!)
            {
                // ゴロ(Roll)かつ二塁封殺レッグ（走者・二塁到達・アウト）を持つ＝内野ゴロ併殺。
                var hasRoll = tl!.Ball.Any(b => b.Kind == BallSegmentKind.Roll);
                var forceAtSecond = tl.Runners.Any(
                    l => l.Label.StartsWith("走") && l.OutAtEnd && Near(l.To, second));
                if (!hasRoll || !forceAtSecond) continue;

                var throws = tl.Ball.Where(b => b.Kind == BallSegmentKind.Throw).ToList();
                Assert.Equal(2, throws.Count); // 併殺は 6-4-3 の2本送球に差し替わる
                Assert.True(Near(throws[0].To, second), "1本目が二塁到達でない");
                Assert.True(Near(throws[1].From, second) && Near(throws[1].To, first), "2本目が二塁→一塁でない");
                Assert.True(throws[0].T1 < throws[1].T1, "二塁より先に一塁へ到達している");
                Assert.True(throws[1].T0 >= throws[0].T1 - 1e-6, "送球連鎖が不連続");
                foreach (var b in tl.Ball) Assert.True(b.T1 <= tl.Duration + 1e-6);
                found++;
                break;
            }
        }
        Assert.True(found > 0, "300シードで内野ゴロ併殺タイムラインが1件も見つからない");
    }

    [Fact]
    public void NonDoublePlayGrounder_KeepsSingleThrow()
    {
        var field = new FieldGeometry();
        var second = new TimelinePoint(field.SecondBase.X, field.SecondBase.Z);
        var first = new TimelinePoint(field.FirstBase.X, field.FirstBase.Z);

        var checkedAny = 0;
        for (ulong s = 0; s < 60; s++)
        {
            var r = CapturedGame(s);
            foreach (var tl in r.Log.Select(e => e.Timeline).Where(t => t is not null)!)
            {
                // 二塁封殺レッグを持たない＝非DP。内野ゴロの一塁送球は従来通り1本のまま。
                var forceAtSecond = tl!.Runners.Any(
                    l => l.Label.StartsWith("走") && l.OutAtEnd && Near(l.To, second));
                if (forceAtSecond) continue;
                // 一塁への送球（内野ゴロ）だけを検査対象にする。外野安打の返球（二塁/三塁へ, #6）や
                // バックホーム送球（本塁, Slice D）は別機構なので、一塁到達送球の本数で回帰を見る。
                var throwsToFirst = tl.Ball.Count(b => b.Kind == BallSegmentKind.Throw && Near(b.To, first));
                if (throwsToFirst == 0) continue;
                Assert.Equal(1, throwsToFirst); // 非DPの内野ゴロは一塁送球1本（回帰）
                checkedAny++;
            }
        }
        Assert.True(checkedAny > 0, "非DPの内野ゴロ送球タイムラインが検査対象に無い");
    }

    // --- F2 Slice D: バックホーム憤死の送球連鎖描画（外野→中継→本塁）＋実況 ---

    private static FieldingPlay OutfieldHit(double landingZ) => new()
    {
        Result = BattedBallResult.Single,
        LandingX = 0, LandingZ = landingZ, HangTimeSeconds = 1.2, ApexHeightM = 3.0,
        RangeM = landingZ, BearingDeg = 0, FielderRole = FieldPosition.CenterField,
        FieldedAtSeconds = 3.2, BatterToFirstSeconds = 4.2, IsFly = false,
        FielderThrowSpeedMps = 31.9,
    };

    [Fact]
    public void BackHomeThrows_DeepBall_DrawsRelayChainToHome_WithOutCaption()
    {
        var field = new FieldGeometry();
        var play = OutfieldHit(75); // 深い当たり（>60m）＝中継が入る
        var baseTl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: true, TimelineBuilder.DescribeResult(play));
        Assert.DoesNotContain(baseTl.Ball, b => b.Kind == BallSegmentKind.Throw); // 外野安打は元々送球なし

        var tl = TimelineBuilder.AppendBackHomeThrows(baseTl, play, field, homeArriveAnchor: 7.5, runnerOut: true);

        var throws = tl.Ball.Where(b => b.Kind == BallSegmentKind.Throw).ToList();
        Assert.Equal(2, throws.Count); // 外野→カット→本塁
        Assert.Equal(play.LandingX, throws[0].From.X, 6);
        Assert.Equal(play.LandingZ, throws[0].From.Z, 6);
        Assert.True(System.Math.Abs(throws[1].To.X) < 1e-6 && System.Math.Abs(throws[1].To.Z) < 1e-6, "最終送球が本塁でない");
        Assert.True(throws[0].T1 <= throws[1].T0 + 1e-6, "中継の時刻が不連続");
        foreach (var b in tl.Ball) Assert.True(b.T1 <= tl.Duration + 1e-6);
        Assert.Contains(tl.Captions, c => c.Text.Contains("タッチアウト"));
    }

    [Fact]
    public void BackHomeThrows_ShallowBall_DrawsDirectThrowToHome()
    {
        var field = new FieldGeometry();
        var play = OutfieldHit(40); // 浅い当たり（≤60m）＝直接返球
        var baseTl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: true, TimelineBuilder.DescribeResult(play));

        var tl = TimelineBuilder.AppendBackHomeThrows(baseTl, play, field, homeArriveAnchor: 6.5, runnerOut: false);

        var throws = tl.Ball.Where(b => b.Kind == BallSegmentKind.Throw).ToList();
        Assert.Single(throws); // 直接返球1本
        Assert.Equal(play.LandingZ, throws[0].From.Z, 6);
        Assert.True(System.Math.Abs(throws[0].To.X) < 1e-6 && System.Math.Abs(throws[0].To.Z) < 1e-6);
        Assert.Contains(tl.Captions, c => c.Text.Contains("セーフ"));
    }

    [Fact]
    public void BackHomeOut_InGame_ProducesRunnerLegAndBallThrowToHome()
    {
        var found = 0;
        for (ulong s = 0; s < 200 && found == 0; s++)
        {
            var r = CapturedGame(s);
            foreach (var tl in r.Log.Select(e => e.Timeline).Where(t => t is not null)!)
            {
                var homeOutLeg = tl!.Runners.Any(
                    leg => leg.OutAtEnd && System.Math.Abs(leg.To.X) < 0.5 && System.Math.Abs(leg.To.Z) < 0.5);
                if (!homeOutLeg) continue;

                // 憤死には本塁で終わる送球と「タッチアウト」実況が伴う（Slice D）。
                Assert.Contains(tl.Ball, b => b.Kind == BallSegmentKind.Throw
                    && System.Math.Abs(b.To.X) < 0.5 && System.Math.Abs(b.To.Z) < 0.5);
                Assert.Contains(tl.Captions, c => c.Text.Contains("タッチアウト"));
                foreach (var b in tl.Ball) Assert.True(b.T1 <= tl.Duration + 1e-6);
                found++;
                break;
            }
        }
        Assert.True(found > 0, "200シードで本塁憤死のタイムラインが1件も見つからない");
    }

    // --- 盗塁タイムライン（mock-steal-2d-view.html 相当） ---

    [Fact]
    public void StealTimeline_HasRunnerThrowAndVerdict()
    {
        var c = new BaserunningCoefficients();
        var field = new FieldGeometry();
        var runner = new Player { Speed = 80, Steal = 75 };
        var catcher = new Player { Position = FieldPosition.Catcher, ArmStrength = 60 };

        foreach (var result in new[] { StealResult.Safe, StealResult.CaughtStealing })
        {
            var tl = TimelineBuilder.BuildSteal(runner, catcher, result, c, field);
            Assert.Equal(2, tl.Ball.Count); // 投球（クイック）＋二塁送球
            Assert.Equal(BallSegmentKind.Pitch, tl.Ball[0].Kind);
            Assert.Equal(BallSegmentKind.Throw, tl.Ball[1].Kind);
            Assert.True(tl.Ball[1].T0 >= tl.Ball[0].T1, "送球が捕球前に始まっている");
            var leg = Assert.Single(tl.Runners);
            Assert.Equal(result == StealResult.CaughtStealing, leg.OutAtEnd);
            Assert.True(tl.ResolvedAt <= tl.Duration);
            // 遊撃手の二塁ベースカバー（陣形の最小適用）。
            Assert.Contains(tl.Moves, m => m.Task == FielderTask.CoverBase);
        }
    }

    // --- #6 外野安打の返球: 送球が走者を追い越さない（三塁打で送球が先着しない） ---
    [Fact]
    public void OutfieldReturnThrow_ArrivesJustAfterRunner_NotBefore()
    {
        var field = new FieldGeometry();
        var third = new TimelinePoint(field.ThirdBase.X, field.ThirdBase.Z);
        // 深いセンター三塁打（外野へ抜けた低い当たり）。
        var play = OutfieldHit(100) with { Result = BattedBallResult.Triple };
        var baseTl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));
        Assert.DoesNotContain(baseTl.Ball, b => b.Kind == BallSegmentKind.Throw); // 外野安打は元々送球なし

        const double runnerArrive = 13.0; // 打者走者が三塁へ着く時刻
        var tl = TimelineBuilder.AppendOutfieldReturnThrow(baseTl, play, field, "third", runnerArrive);

        double baseArrive = 0;
        foreach (var b in tl.Ball)
            if (b.Kind == BallSegmentKind.Throw && Near(b.To, third)) baseArrive = System.Math.Max(baseArrive, b.T1);

        Assert.True(baseArrive > 0, "三塁への返球が描かれていない（ボールが外野で死ぬ）");
        Assert.True(baseArrive >= runnerArrive, $"返球が走者より先に三塁到達({baseArrive:F2} < {runnerArrive})");
        Assert.True(baseArrive <= runnerArrive + 0.6, $"返球が走者から遅れすぎ({baseArrive:F2})");
        Assert.True(tl.Duration >= baseArrive, "Duration が返球到達より短い");
        foreach (var b in tl.Ball) Assert.True(b.T1 <= tl.Duration + 1e-6);
    }

    // --- #136: 中継のボール経由点と中継野手(カットオフ)の立ち位置が一致すること ---
    // （不一致だと「野手のいない無人のフィールドにボールが落ちる」表示になる回帰バグ）

    [Fact]
    public void OutfieldReturnThrow_CutoffWaypoint_MatchesCutoffFielderPosition()
    {
        var field = new FieldGeometry();
        var play = OutfieldHit(100) with { Result = BattedBallResult.Triple }; // 深い当たり＝中継が入る
        var baseTl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

        // カットオフ役は「中継点へ出る」動きと「決着後に定位置へ戻る」動きの2つを持ちうる（#2）。
        // 中継点への往路（T0が早い方）を中継野手の立ち位置として検証する。
        var cutoffMove = baseTl.Moves.Where(m => m.Task == FielderTask.Cutoff).OrderBy(m => m.T0).First();

        var tl = TimelineBuilder.AppendOutfieldReturnThrow(baseTl, play, field, "third", runnerArriveAnchor: 13.0);

        var throws = tl.Ball.Where(b => b.Kind == BallSegmentKind.Throw).ToList();
        Assert.Equal(2, throws.Count); // 外野→カット→三塁
        Assert.True(Near(throws[0].To, cutoffMove.To), "ボールの中継点が中継野手の立ち位置と一致しない");
        Assert.True(Near(throws[1].From, cutoffMove.To), "2本目送球の起点が中継野手の立ち位置と一致しない");
    }

    [Fact]
    public void BackHomeThrows_CutoffWaypoint_MatchesCutoffFielderPosition()
    {
        var field = new FieldGeometry();
        var play = OutfieldHit(75); // 深い当たり（>60m）＝中継が入る
        var baseTl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: true, TimelineBuilder.DescribeResult(play));

        // カットオフ役は「中継点へ出る」動きと「決着後に定位置へ戻る」動きの2つを持ちうる（#2）。
        // 中継点への往路（T0が早い方）を中継野手の立ち位置として検証する。
        var cutoffMove = baseTl.Moves.Where(m => m.Task == FielderTask.Cutoff).OrderBy(m => m.T0).First();

        var tl = TimelineBuilder.AppendBackHomeThrows(baseTl, play, field, homeArriveAnchor: 7.5, runnerOut: true);

        var throws = tl.Ball.Where(b => b.Kind == BallSegmentKind.Throw).ToList();
        Assert.Equal(2, throws.Count); // 外野→カット→本塁
        Assert.True(Near(throws[0].To, cutoffMove.To), "ボールの中継点が中継野手の立ち位置と一致しない");
        Assert.True(Near(throws[1].From, cutoffMove.To), "2本目送球の起点が中継野手の立ち位置と一致しない");
    }
}
