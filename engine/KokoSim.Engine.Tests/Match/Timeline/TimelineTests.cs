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
using KokoSim.Engine.Tests.Match.Fielding;
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

    // --- Issue #257: 一塁カバーは実捕球者から動的に決める。静的表のゾーン別担当（bunt=セカンド固定,
    // infield_right=ピッチャー固定）だけでは、ファースト以外が捕っても押し出されたファーストが役割を失い
    // 棒立ちになる／実捕球者自身が唯一のカバー担当だった場合は誰も一塁に入らない、という回帰があった。 ---

    private static FieldingPlay GroundOutAt(FieldPosition fielderRole, double bearingDeg, double rangeM)
    {
        var bearingRad = bearingDeg * System.Math.PI / 180.0;
        return FieldingPlayFixtures.InfieldGrounder(
            fielderRole,
            fieldedX: rangeM * System.Math.Sin(bearingRad),
            fieldedZ: rangeM * System.Math.Cos(bearingRad),
            fieldedAtSeconds: 1.0, throwArriveSeconds: 2.0);
    }

    /// <summary>一塁ベース座標へ向かう移動の担当（複数居たら回帰）。誰も居なければ null。</summary>
    private static FieldPosition? FirstBaseCoverer(PlayTimeline tl, FieldGeometry field)
    {
        var first = new TimelinePoint(field.FirstBase.X, field.FirstBase.Z);
        var coverers = tl.Moves.Where(m => Near(m.To, first)).Select(m => m.Role).ToList();
        Assert.True(coverers.Count <= 1, $"一塁への移動が複数（{string.Join(",", coverers)}）");
        return coverers.Count == 1 ? coverers[0] : (FieldPosition?)null;
    }

    [Theory]
    // Bunt: ファースト以外が捕ったら、押し出されずにファーストが一塁カバーへ入る。
    [InlineData(FieldPosition.Pitcher, 0.0, 8.0, FieldPosition.FirstBase)]
    [InlineData(FieldPosition.Catcher, 0.0, 8.0, FieldPosition.FirstBase)]
    [InlineData(FieldPosition.ThirdBase, 0.0, 8.0, FieldPosition.FirstBase)]
    // Bunt: ファーストが捕ったときだけ、静的表どおりセカンドが一塁カバーへ入る（既存の正常系）。
    [InlineData(FieldPosition.FirstBase, 0.0, 8.0, FieldPosition.SecondBase)]
    // InfieldRight: ファースト以外が捕ったらファーストが一塁カバーへ入る（ピッチャーが捕って
    // 一塁カバーが1人も居なくなる回帰＋セカンドが捕ってピッチャー固定担当が残る回帰の両方）。
    [InlineData(FieldPosition.SecondBase, 20.0, 30.0, FieldPosition.FirstBase)]
    [InlineData(FieldPosition.Pitcher, 20.0, 30.0, FieldPosition.FirstBase)]
    // InfieldRight: ファーストが捕ったときだけ、静的表どおりピッチャーが一塁カバーへ入る（既存の正常系）。
    [InlineData(FieldPosition.FirstBase, 20.0, 30.0, FieldPosition.Pitcher)]
    // InfieldLeft: 元々正常だった系の回帰確認（ショート/サードが捕ってもファーストが一塁カバーへ入る）。
    [InlineData(FieldPosition.Shortstop, -20.0, 30.0, FieldPosition.FirstBase)]
    [InlineData(FieldPosition.ThirdBase, -20.0, 30.0, FieldPosition.FirstBase)]
    public void FirstBaseCover_FollowsActualFielder_NotStaticZoneTable(
        FieldPosition fielderRole, double bearingDeg, double rangeM, FieldPosition expectedCoverer)
    {
        var field = new FieldGeometry();
        var play = GroundOutAt(fielderRole, bearingDeg, rangeM);
        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

        var coverer = FirstBaseCoverer(tl, field);
        Assert.Equal(expectedCoverer, coverer); // 一塁カバーが必ず1人、かつ表の通りの担当
        Assert.NotEqual(fielderRole, coverer); // 実捕球者はカバー担当にならない
    }

    // --- 磨き込み①: ゴロは地を這うロール／実況の具体化（表示専用・判定非干渉） ---

    /// <summary>
    /// 三遊間のゴロアウト。着地点(-6,20)と実捕球点(-9,30)を**わざとズラす**（Issue #261 で実際に
    /// そうなった形）。両者が同値だと「着地点を見ている」バグが緑のまま通ってしまう（Issue #315）。
    /// </summary>
    private static FieldingPlay GrounderOut()
        => FieldingPlayFixtures.InfieldGrounder(
            FieldPosition.Shortstop, landingX: -6.0, landingZ: 20.0);

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
        // 本塁から**実捕球点**まで転がる（着地点で止めない, Issue #261/#301）。
        Assert.Equal(0.0, roll.From.X, 6);
        Assert.Equal(0.0, roll.From.Z, 6);
        Assert.Equal(play.FieldedX!.Value, roll.To.X, 6);
        Assert.Equal(play.FieldedZ!.Value, roll.To.Z, 6);
        Assert.NotEqual(play.LandingZ, roll.To.Z, 6); // 着地点と取り違えていない
        // 一塁送球はロール終端（＝捕球点）から連続して始まる。
        var throwSeg = Assert.Single(tl.Ball, b => b.Kind == BallSegmentKind.Throw);
        Assert.Equal(roll.To.X, throwSeg.From.X, 6);
        Assert.Equal(roll.To.Z, throwSeg.From.Z, 6);
        Assert.True(throwSeg.T0 >= roll.T1 - 1e-6, "送球がロール終端より前に始まっている");
    }

    // --- Issue #334: 一塁送球の画面上の速度が肩どおりになること（距離に依らない下駄が乗らないこと） ---

    [Theory]
    [InlineData(FieldPosition.Shortstop, -9.0, 30.0)]  // 三遊間の通常距離
    [InlineData(FieldPosition.Pitcher, 0.0, 18.0)]     // 投手からのごく短い送球
    [InlineData(FieldPosition.FirstBase, 15.0, 20.0)]  // 一塁手からの最短送球
    public void Throw_DisplaySpeed_MatchesFielderArm_RegardlessOfDistance(
        FieldPosition role, double fieldedX, double fieldedZ)
    {
        var field = new FieldGeometry();
        var play = FieldingPlayFixtures.InfieldGrounder(role, fieldedX: fieldedX, fieldedZ: fieldedZ);
        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

        var throwSeg = Assert.Single(tl.Ball, b => b.Kind == BallSegmentKind.Throw);
        var dist = System.Math.Sqrt(
            System.Math.Pow(throwSeg.To.X - throwSeg.From.X, 2) + System.Math.Pow(throwSeg.To.Z - throwSeg.From.Z, 2));
        var displaySpeed = dist / (throwSeg.T1 - throwSeg.T0);
        // 画面上の速度＝処理野手の肩（従来は捕球オーバーヘッド＋握り替えの1.58秒が下駄として乗り、
        // 距離が短いほど極端に遅く見えていた＝Issue #334）。
        Assert.Equal(play.FielderThrowSpeedMps!.Value, displaySpeed, 3);

        // 到達時刻(T1)は判定を決めたエンジンの値のまま（表示専用の修正で判定・帯には触れない）。
        Assert.Equal(0.85 + play.ThrowArriveSeconds!.Value, throwSeg.T1, 6);
        // 投げ始め(T0)は捕球より前には遡らない。
        Assert.True(throwSeg.T0 >= 0.85 + play.FieldedAtSeconds!.Value - 1e-6, "捕球より前に投げ始めている");
    }

    [Fact]
    public void FlyBall_StillUsesParabolaFlight()
    {
        var field = new FieldGeometry();
        var play = FieldingPlayFixtures.CaughtFly(FieldPosition.CenterField);
        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

        Assert.Contains(tl.Ball, b => b.Kind == BallSegmentKind.Flight);
        Assert.DoesNotContain(tl.Ball, b => b.Kind == BallSegmentKind.Roll);
    }

    // --- Issue #300: 本塁打（捕球者なし=FielderRole null）で外野手がフェンス外まで打球を追わない ---

    /// <summary>本塁打の FieldingPlay（FielderRole=null・IsFly=false のまま）。
    /// 着地点はセンター方向フェンス(118m)の外。</summary>
    private static FieldingPlay HomeRunPlay() => FieldingPlayFixtures.HomeRun();

    [Fact]
    public void HomeRun_BallDrawnAsFlight_NotRoll()
    {
        var field = new FieldGeometry();
        var play = HomeRunPlay();
        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

        // IsFly は本塁打では false のまま（FieldingResolver 側は変えない）だが、表示は放物線を選ぶ。
        Assert.False(play.IsFly);
        Assert.Contains(tl.Ball, b => b.Kind == BallSegmentKind.Flight);
        Assert.DoesNotContain(tl.Ball, b => b.Kind is BallSegmentKind.Roll or BallSegmentKind.Bounce);
    }

    [Fact]
    public void HomeRun_FieldBallMove_StaysInsideFence_DoesNotChaseBeyondWall()
    {
        var field = new FieldGeometry();
        var play = HomeRunPlay();
        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

        var fenceDist = field.FenceDistance(play.BearingDeg * System.Math.PI / 180.0);
        var fieldBallMoves = tl.Moves.Where(m => m.Task == FielderTask.FieldBall).ToList();
        Assert.NotEmpty(fieldBallMoves); // センターが FieldBall 役として動く（陣形表 OutfieldCenter）
        foreach (var m in fieldBallMoves)
        {
            var range = System.Math.Sqrt(m.To.X * m.To.X + m.To.Z * m.To.Z);
            Assert.True(range <= fenceDist + 1e-6, $"外野手がフェンス({fenceDist}m)の外({range}m)まで打球を追っている");
        }
        // ボールそのものは着地点（柵の外）まで飛ぶ＝本塁打らしい絵は保つ。野手だけが止まる。
        var flight = Assert.Single(tl.Ball, b => b.Kind == BallSegmentKind.Flight);
        Assert.Equal(play.LandingX, flight.To.X, 6);
        Assert.Equal(play.LandingZ, flight.To.Z, 6);
    }

    [Fact]
    public void NullFielderRole_AnyResult_FieldBallMove_StaysInsideFence()
    {
        // #300 の抜け道は Result==HomeRun 限定ではなく「FielderRole が null」全般（ファウル等）で
        // 起きる。将来 FielderRole=null のまま着地点が柵外になる結果が増えても抜けないことを、
        // Result を HomeRun 以外にした構成でも確認する（クランプの条件が Result ではなく
        // FielderRole==null に懸かっていることの回帰）。
        var field = new FieldGeometry();
        var play = HomeRunPlay() with { Result = BattedBallResult.Out };
        Assert.Null(play.FielderRole);
        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

        var fenceDist = field.FenceDistance(play.BearingDeg * System.Math.PI / 180.0);
        var fieldBallMoves = tl.Moves.Where(m => m.Task == FielderTask.FieldBall).ToList();
        Assert.NotEmpty(fieldBallMoves);
        foreach (var m in fieldBallMoves)
        {
            var range = System.Math.Sqrt(m.To.X * m.To.X + m.To.Z * m.To.Z);
            Assert.True(range <= fenceDist + 1e-6, $"外野手がフェンス({fenceDist}m)の外({range}m)まで打球を追っている");
        }
    }

    [Fact]
    public void HomeRun_FieldBallMove_DoesNotExceedLandingPoint()
    {
        // クランプが逆方向に壊れて（例: 距離0に潰れる等）野手が動かなくなる回帰も検出する。
        // フェンス手前までは実際に近づく（着地点そのものよりは手前、かつ野手の元位置よりは大きく前進）。
        var field = new FieldGeometry();
        var play = HomeRunPlay();
        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

        var centerMove = Assert.Single(tl.Moves, m => m.Role == FieldPosition.CenterField && m.Task == FielderTask.FieldBall);
        var range = System.Math.Sqrt(centerMove.To.X * centerMove.To.X + centerMove.To.Z * centerMove.To.Z);
        Assert.True(range < play.RangeM, "野手の移動先が着地点まで届いている（クランプが効いていない）");
        Assert.True(range > 70.0, "野手がフェンスへ向かって前進していない（クランプが過剰に効いている）");
    }

    // infieldGrounder は「内野ゴロ経路で解決したか」。内野ゴロは結果がアウトでも失策でも一塁送球の
    // 勝負をするので ThrowArriveSeconds を必ず持つ（＝リゾルバが作れない組み合わせを書かない, Issue #315）。
    [Theory]
    [InlineData(BattedBallResult.Out, true, FieldPosition.Shortstop, "ショートゴロ")]
    [InlineData(BattedBallResult.Single, true, FieldPosition.Shortstop, "内野安打")]
    [InlineData(BattedBallResult.Error, true, FieldPosition.Shortstop, "ショートのエラー")]
    [InlineData(BattedBallResult.Out, false, FieldPosition.CenterField, "センターフライ")]
    [InlineData(BattedBallResult.Single, false, FieldPosition.LeftField, "レフト前ヒット")]
    [InlineData(BattedBallResult.Double, false, FieldPosition.RightField, "ライトへの二塁打")]
    [InlineData(BattedBallResult.HomeRun, false, null, "ホームラン")]
    public void DescribeResult_IsSpecific(
        BattedBallResult result, bool infieldGrounder, FieldPosition? role, string expected)
    {
        // 外野手の担当方向へ実際に飛ばす（役割と方位が食い違ったフィクスチャを書かない）。
        var landingX = role switch
        {
            FieldPosition.LeftField => -30.0,
            FieldPosition.RightField => 30.0,
            _ => 0.0,
        };
        var play =
            role is null ? FieldingPlayFixtures.HomeRun()
            : infieldGrounder ? FieldingPlayFixtures.InfieldGrounder(role.Value) with { Result = result }
            : result == BattedBallResult.Out
                ? FieldingPlayFixtures.CaughtFly(role.Value)
                : FieldingPlayFixtures.OutfieldHit(
                    role.Value, landingX, landingZ: 60.0, fieldedAtSeconds: 2.5) with { Result = result };

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
        // 1本目: 実捕球点→二塁（着地点ではない, Issue #315）。
        Assert.Equal(play.FieldedX!.Value, throws[0].From.X, 6);
        Assert.Equal(play.FieldedZ!.Value, throws[0].From.Z, 6);
        Assert.Equal(second.X, throws[0].To.X, 6);
        Assert.Equal(second.Z, throws[0].To.Z, 6);
        // 2本目: 二塁→一塁。
        Assert.Equal(second.X, throws[1].From.X, 6);
        Assert.Equal(second.Z, throws[1].From.Z, 6);
        Assert.Equal(first.X, throws[1].To.X, 6);
        Assert.Equal(first.Z, throws[1].To.Z, 6);
    }

    [Fact]
    public void DoublePlayThrows_CarryThrowerAndReceiverRoles()
    {
        // Issue E1: 3D守備ビューは「誰が投げるモーションをするか」を決める必要がある。座標だけの
        // 最近傍マッチは、二塁の周りに遊撃手・二塁手・走者が同時に居る併殺で取り違える。
        var field = new FieldGeometry();
        var play = GrounderOut();   // 遊撃手が処理＝6-4-3
        var baseTl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: true, TimelineBuilder.DescribeResult(play));
        var batterArrive = baseTl.Runners.Single(r => r.Label == "打").T1;

        var tl = TimelineBuilder.AppendDoublePlayThrows(baseTl, play, field, batterArrive);

        var throws = tl.Ball.Where(b => b.Kind == BallSegmentKind.Throw).ToList();
        Assert.Equal(2, throws.Count);
        // 1本目: 遊撃手 → 二塁カバー（陣形が実際に二塁へ動かした野手と同一人物）。
        Assert.Equal(FieldPosition.Shortstop, throws[0].FromRole);
        Assert.Equal("second", throws[0].ToBase);
        var pivot = throws[0].ToRole;
        Assert.NotNull(pivot);
        Assert.Contains(tl.Moves, m => m.Role == pivot && m.Task == FielderTask.CoverBase);
        // 2本目: その中継役 → 一塁。**投げ手が1本目の受け手と同一**であることが要（併殺の肝）。
        Assert.Equal(pivot, throws[1].FromRole);
        Assert.Equal(FieldPosition.FirstBase, throws[1].ToRole);
        Assert.Equal("first", throws[1].ToBase);
    }

    [Fact]
    public void InfieldGrounderThrow_CarriesFielderAndFirstBaseCoverer()
    {
        // 一塁送球（併殺でない通常のゴロ）。受け手は「一塁カバーに実際に入る野手」＝
        // 遊撃ゴロなら一塁手そのもの。ファーストが捕った場合は投手が入る（Issue #257 の動的割当）。
        var field = new FieldGeometry();
        var play = GrounderOut();
        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

        var throw1 = tl.Ball.Single(b => b.Kind == BallSegmentKind.Throw);
        Assert.Equal(FieldPosition.Shortstop, throw1.FromRole);
        Assert.Equal(FieldPosition.FirstBase, throw1.ToRole);
        Assert.Equal("first", throw1.ToBase);

        var byFirst = FieldingPlayFixtures.InfieldGrounder(
            FieldPosition.FirstBase, landingX: 12.0, landingZ: 20.0);
        var tl2 = TimelineBuilder.BuildBattedBall(
            byFirst, field.StandardAlignment(), field, null, runnersOn: false,
            TimelineBuilder.DescribeResult(byFirst));
        var throw2 = tl2.Ball.Single(b => b.Kind == BallSegmentKind.Throw);
        Assert.Equal(FieldPosition.FirstBase, throw2.FromRole);
        Assert.Equal(FieldPosition.Pitcher, throw2.ToRole);
    }

    [Fact]
    public void Roles_ArePresentOnlyOnThrows()
    {
        // 表示専用の付加情報なので、送球以外（投球・滞空・転がり）には付けない＝
        // Unity 側が「役が入っている＝送球」と読めるようにしておく。
        var field = new FieldGeometry();
        var play = GrounderOut();
        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

        foreach (var seg in tl.Ball.Where(b => b.Kind != BallSegmentKind.Throw))
        {
            Assert.Null(seg.FromRole);
            Assert.Null(seg.ToRole);
            Assert.Null(seg.ToBase);
        }
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
        var play = FieldingPlayFixtures.CaughtFly(FieldPosition.CenterField);
        var baseTl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: true, TimelineBuilder.DescribeResult(play));

        var tl = TimelineBuilder.AppendDoublePlayThrows(baseTl, play, field, batterArriveAnchor: 5.0);

        // フライは併殺の送球連鎖対象外＝差し替えせず同一タイムラインを返す。
        Assert.Same(baseTl, tl);
    }

    [Fact]
    public void DoublePlayThrows_FirstLeg_HoldsBeforeRelease_AndMatchesArm()
    {
        // Issue #334: 1本目（野手→二塁）が捕球と同時（握り替え0秒）にリリースされていた回帰の防止。
        var field = new FieldGeometry();
        var play = GrounderOut();
        var baseTl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: true, TimelineBuilder.DescribeResult(play));
        var batterArrive = baseTl.Runners.Single(r => r.Label == "打").T1;

        var tl = TimelineBuilder.AppendDoublePlayThrows(baseTl, play, field, batterArrive);

        var throws = tl.Ball.Where(b => b.Kind == BallSegmentKind.Throw).ToList();
        var fieldedAt = 0.85 + play.FieldedAtSeconds!.Value;
        Assert.True(throws[0].T0 > fieldedAt + 1e-6, "処理と同時に投げてしまっている（握り替えが無い）");

        var dist = System.Math.Sqrt(
            System.Math.Pow(throws[0].To.X - throws[0].From.X, 2) + System.Math.Pow(throws[0].To.Z - throws[0].From.Z, 2));
        var speed = dist / (throws[0].T1 - throws[0].T0);
        Assert.Equal(play.FielderThrowSpeedMps!.Value, speed, 3); // 握り替えを挟んでも速度は肩どおり
    }

    // --- F1 Slice 3: GameEngine 結線（併殺で二塁→一塁の2連続送球が描かれる） ---

    private static bool Near(TimelinePoint a, TimelinePoint b) =>
        System.Math.Abs(a.X - b.X) < 0.5 && System.Math.Abs(a.Z - b.Z) < 0.5;

    /// <summary>
    /// 一塁走者の封殺レッグ（＝二塁でのフォースアウト。あれば）。走者は投球の間に二次リードを取るので
    /// **レッグの From は一塁ちょうどではない**（リード距離ぶん次塁側）。一塁からリードの範囲内で
    /// 始まっているかで見分ける。
    /// </summary>
    private static RunnerLeg? ForceOutFromFirst(PlayTimeline tl, TimelinePoint first)
    {
        var reach = new BaserunningCoefficients().HomeLeadDistanceM + 0.5;
        foreach (var leg in tl.Runners)
        {
            if (!leg.Label.StartsWith("走") || !leg.OutAtEnd) continue;
            var dx = leg.From.X - first.X;
            var dz = leg.From.Z - first.Z;
            if (System.Math.Sqrt(dx * dx + dz * dz) <= reach) return leg;
        }
        return null;
    }

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
                // 判定時刻の差し替え（Issue #328①）でレッグが送球到達点で止まる（Toが二塁ちょうどで
                // なくなりうる）ため、消えない From(=一塁) で検出する。
                var forceLeg = ForceOutFromFirst(tl, first);
                if (!hasRoll || forceLeg is null) continue;

                var throws = tl.Ball.Where(b => b.Kind == BallSegmentKind.Throw).ToList();
                Assert.Equal(2, throws.Count); // 併殺は 6-4-3 の2本送球に差し替わる
                Assert.True(Near(throws[0].To, second), "1本目が二塁到達でない");
                Assert.True(Near(throws[1].From, second) && Near(throws[1].To, first), "2本目が二塁→一塁でない");
                Assert.True(throws[0].T1 < throws[1].T1, "二塁より先に一塁へ到達している");
                Assert.True(throws[1].T0 >= throws[0].T1 - 1e-6, "送球連鎖が不連続");
                foreach (var b in tl.Ball) Assert.True(b.T1 <= tl.Duration + 1e-6);

                // Issue #328①: 二塁封殺の判定時刻は走者到達ではなく1本目送球の到達（throws[0].T1）と一致する。
                var forceJudgement = Assert.Single(tl.Judgements, j => j.Label == forceLeg.Label);
                Assert.Equal(JudgementKind.Out, forceJudgement.Kind);
                Assert.Equal(throws[0].T1, forceJudgement.T, 6);
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
                if (ForceOutFromFirst(tl!, first) is not null) continue;
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

    /// <summary>センター前へ抜けたゴロの安打。着地点＝回収点（転がりが短く止まった形）。</summary>
    private static FieldingPlay OutfieldHit(double landingZ)
        => FieldingPlayFixtures.OutfieldHit(
            FieldPosition.CenterField, landingX: 0, landingZ: landingZ, fieldedAtSeconds: 3.2);

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

    // ═══ Issue #301: 打球の回収点(FieldedX/Z・FieldedAtSeconds)を表示の単一ソースにする ═══

    /// <summary>外野へ抜けた飛球の安打。着地点(90m)から回収点(102m)まで転がってから回収される。</summary>
    private static FieldingPlay FlyHitWithRoll()
        => FieldingPlayFixtures.OutfieldHit(
            FieldPosition.CenterField, landingX: 0, landingZ: 90.0,
            fieldedAtSeconds: 5.1,               // 着地(3.4s)より後＝転がっている区間がある
            fieldedZ: 102.0,                     // 着地後に12m転がった先で回収
            result: BattedBallResult.Double,
            hangTimeSeconds: 3.4, apexHeightM: 18.0, isFly: true,
            throwSpeedMps: 33.0, fielding: 55);

    /// <summary>#301②: 外野飛球の安打は着地点で静止せず、回収点まで転がる区間を持つ。</summary>
    [Fact]
    public void OutfieldFlyHit_BallRollsFromLandingToFieldedPoint()
    {
        var field = new FieldGeometry();
        var play = FlyHitWithRoll();
        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

        var flight = Assert.Single(tl.Ball, b => b.Kind == BallSegmentKind.Flight);
        var roll = Assert.Single(tl.Ball, b => b.Kind == BallSegmentKind.Roll);

        // 転がりは着地点から始まり、エンジンの回収点で終わる（表示側で独自の転がりを発明しない）。
        Assert.Equal(play.LandingX, roll.From.X, 6);
        Assert.Equal(play.LandingZ, roll.From.Z, 6);
        Assert.Equal(play.FieldedX!.Value, roll.To.X, 6);
        Assert.Equal(play.FieldedZ!.Value, roll.To.Z, 6);
        // 時刻も着地→回収でエンジンの値どおり、かつ放物線と連続。
        Assert.Equal(flight.T1, roll.T0, 6);
        Assert.Equal(0.85 + play.HangTimeSeconds, roll.T0, 6);
        Assert.Equal(0.85 + play.FieldedAtSeconds!.Value, roll.T1, 6);
    }

    /// <summary>#301②: 空中捕球（回収点＝着地点）では転がり区間を足さない（従来の絵のまま）。</summary>
    [Fact]
    public void CaughtFly_HasNoRollSegment()
    {
        var field = new FieldGeometry();
        var play = FieldingPlayFixtures.CaughtFly(FieldPosition.CenterField);
        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

        Assert.Contains(tl.Ball, b => b.Kind == BallSegmentKind.Flight);
        Assert.DoesNotContain(tl.Ball, b => b.Kind == BallSegmentKind.Roll);
    }

    /// <summary>
    /// #315: 回収点を持たない打球（空中捕球・本塁打＝リゾルバが FieldedX/Z を書かない経路）でも、
    /// 処理点は**着地点**へフォールバックする。
    ///
    /// 回収点が非nullable だった頃は未設定＝(0,0)＝本塁で、この打球は「センターが本塁へ突っ込み、
    /// ボールも本塁で終わる」絵になっていた。既定値がドメイン上ありうる座標だったせいで、
    /// 型でも実行時でも検出されずテストだけが緑で通っていた形（Issue #301 が生き残った原因）。
    /// </summary>
    [Fact]
    public void PlayWithoutFieldPoint_FallsBackToLanding_NotHomePlate()
    {
        var field = new FieldGeometry();
        var play = FieldingPlayFixtures.CaughtFly(FieldPosition.CenterField, landingZ: 88.0);
        Assert.Null(play.FieldedX); // 前提: この経路は回収点を書かない（書いてしまうと検査にならない）
        Assert.Null(play.FieldedZ);

        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

        // ボールの放物線は着地点で終わる（本塁 Z=0 へ戻らない）。
        var flight = Assert.Single(tl.Ball, b => b.Kind == BallSegmentKind.Flight);
        Assert.Equal(play.LandingX, flight.To.X, 6);
        Assert.Equal(play.LandingZ, flight.To.Z, 6);

        // 捕球者も着地点へ向かう（本塁へ突っ込まない）。
        var leg = Assert.Single(
            tl.Moves, m => m.Role == FieldPosition.CenterField && m.Task == FielderTask.FieldBall);
        Assert.Equal(play.LandingX, leg.To.X, 6);
        Assert.Equal(play.LandingZ, leg.To.Z, 6);
    }

    /// <summary>時刻 t のボール位置（再生器と同じ「セグメント間は最後の位置で保持」規則）。</summary>
    private static TimelinePoint BallAt(PlayTimeline tl, double t)
    {
        TimelinePoint held = new(0, 0);
        foreach (var s in tl.Ball)
        {
            if (t < s.T0) return held;
            var u = System.Math.Min(1.0, (t - s.T0) / System.Math.Max(1e-9, s.T1 - s.T0));
            var at = new TimelinePoint(s.From.X + (s.To.X - s.From.X) * u, s.From.Z + (s.To.Z - s.From.Z) * u);
            if (t <= s.T1) return at;
            held = new TimelinePoint(s.To.X, s.To.Z);
        }
        return held;
    }

    /// <summary>時刻 t の野手位置（再生器 PlaybackEvaluator.FielderAt と同じ線形累積・T1以降は保持）。</summary>
    private static TimelinePoint FielderAt(PlayTimeline tl, FieldPosition role, TimelinePoint start, double t)
    {
        var pos = start;
        foreach (var m in tl.Moves)
        {
            if (m.Role != role) continue;
            if (t <= m.T0) break;
            var u = System.Math.Min(1.0, (t - m.T0) / System.Math.Max(1e-9, m.T1 - m.T0));
            pos = new TimelinePoint(pos.X + (m.To.X - pos.X) * u, pos.Z + (m.To.Z - pos.Z) * u);
        }
        return pos;
    }

    /// <summary>
    /// #301①: 回収した野手は回収時刻までにボールへ到達している（＝ボールを持っているはずの時刻に
    /// 野手とボールの座標が一致する）。修正前は野手の移動が「反応0.35s＋距離÷走力」の別計算で、
    /// 回収時刻に間に合わず「誰も触っていないのにボールが動き出す」絵になっていた。
    /// </summary>
    [Fact]
    public void RetrievingFielder_ReachesBallByFieldedTime_AndSharesCoordinates()
    {
        var field = new FieldGeometry();
        var play = FlyHitWithRoll();
        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

        var fieldedAt = 0.85 + play.FieldedAtSeconds!.Value;
        var role = play.FielderRole!.Value;

        // 実捕球者の移動レッグは回収点で終わり、回収時刻までに着く。
        var legs = tl.Moves.Where(m => m.Role == role && m.Task == FielderTask.FieldBall).ToList();
        var leg = Assert.Single(legs);
        Assert.Equal(play.FieldedX!.Value, leg.To.X, 6);
        Assert.Equal(play.FieldedZ!.Value, leg.To.Z, 6);
        Assert.True(leg.T1 <= fieldedAt + 1e-6, $"回収時刻({fieldedAt:F2})に野手が間に合っていない(到達{leg.T1:F2})");

        // 回収時刻のボールと野手が同じ場所にいる。
        var startPos = field.StandardAlignment().Single(f => f.Position == role) is { } f0
            ? new TimelinePoint(f0.Location.X, f0.Location.Z) : new TimelinePoint(0, 0);
        var ball = BallAt(tl, fieldedAt);
        var fielder = FielderAt(tl, role, startPos, fieldedAt);
        Assert.True(System.Math.Abs(ball.X - fielder.X) < 0.5 && System.Math.Abs(ball.Z - fielder.Z) < 0.5,
            $"回収時刻にボール({ball.X:F1},{ball.Z:F1})と野手({fielder.X:F1},{fielder.Z:F1})の座標が一致しない");
    }

    /// <summary>
    /// #301①: 返球の開始時刻 ≧ その野手のボール到達時刻（＝誰も触っていないのに返球が始まらない）。
    /// 全シードの全打球タイムラインで成り立つ不変条件として検証する。
    /// </summary>
    [Fact]
    public void ThrowNeverStartsBeforeRetrievingFielderReachesBall()
    {
        var field = new FieldGeometry();
        var checkedAny = 0;
        for (ulong s = 0; s < 12; s++)   // 1試合に多数の送球があるので少数シードで十分
        {
            var r = CapturedGame(s);
            foreach (var e in r.Log)
            {
                var tl = e.Timeline;
                if (tl is null) continue;
                var firstThrow = tl.Ball.Where(b => b.Kind == BallSegmentKind.Throw).OrderBy(b => b.T0).FirstOrDefault();
                if (firstThrow is null) continue;

                // 実捕球者の「ボールへ向かう」レッグ（あれば）の到達時刻。無ければ定位置直撃で0距離。
                var ballLeg = tl.Moves.Where(m => m.Task == FielderTask.FieldBall).OrderBy(m => m.T1).LastOrDefault();
                if (ballLeg is null) continue;

                Assert.True(firstThrow.T0 >= ballLeg.T1 - 1e-6,
                    $"seed={s}: 野手到達({ballLeg.T1:F3})より前に返球が開始({firstThrow.T0:F3})");
                checkedAny++;
            }
        }
        Assert.True(checkedAny > 0, "送球を含むタイムラインが検査対象に無い");
    }

    /// <summary>
    /// #298-2/#301③: バックホーム送球が距離に応じた時間をかける（0.30秒固定下限で200〜370m/sにならない）。
    /// 描かれた各送球レッグの速度が、処理野手の肩から見て妥当な上限を超えないことを検証する。
    /// </summary>
    [Theory]
    [InlineData(45.0)]   // 浅い＝直接返球
    [InlineData(75.0)]   // 深い＝中継
    [InlineData(110.0)]  // 最深部＝中継（従来ここが 0.30 秒で本塁到達＝370m/s になっていた）
    public void BackHomeThrow_SpeedStaysPhysical_RegardlessOfRunnerAnchor(double landingZ)
    {
        var field = new FieldGeometry();
        var play = OutfieldHit(landingZ) with { FieldedX = 0, FieldedZ = landingZ };
        var baseTl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: true, TimelineBuilder.DescribeResult(play));

        // 走者アンカーを極端に早く（＝処理直後）置いても、送球が光速にならないこと。
        var tl = TimelineBuilder.AppendBackHomeThrows(
            baseTl, play, field, homeArriveAnchor: 0.85 + play.FieldedAtSeconds!.Value + 0.05, runnerOut: true);

        var throws = tl.Ball.Where(b => b.Kind == BallSegmentKind.Throw).ToList();
        Assert.NotEmpty(throws);
        // 1本目は処理野手の肩、2本目（中継）は中継送球速度（BaserunningCoefficients.RelayThrowSpeedMps=32）。
        var cap = System.Math.Max(play.FielderThrowSpeedMps!.Value, 32.0);
        foreach (var t in throws)
        {
            var dist = System.Math.Sqrt(
                System.Math.Pow(t.To.X - t.From.X, 2) + System.Math.Pow(t.To.Z - t.From.Z, 2));
            var dur = t.T1 - t.T0;
            Assert.True(dur > 0, "送球区間の所要が0以下");
            var speed = dist / dur;
            Assert.True(speed <= cap + 1e-6,
                $"送球が上限({cap}m/s)を超えている: {speed:F1}m/s（{dist:F1}m を {dur:F2}s）");
        }
    }

    /// <summary>
    /// #298-2 完了条件: 実試合の全打球タイムラインで、どの送球セグメントも
    /// <c>Distance / (T1 - T0)</c> が上限を超えない。修正前はバックホームだけ距離項が無く、
    /// 外野深部（80〜110m）から 0.30 秒で本塁到達＝200〜370 m/s の絵になっていた。
    /// </summary>
    [Fact]
    public void AllThrowSegments_StayUnderPhysicalSpeedCap_AcrossGames()
    {
        const double capMps = 40.0;  // 高校生の肩（〜35m/s）＋中継の余裕。バグ時は 200〜370m/s
        var worst = 0.0;
        var checkedAny = 0;
        for (ulong s = 0; s < 20; s++)
        {
            foreach (var tl in CapturedGame(s).Log.Select(e => e.Timeline).Where(t => t is not null)!)
            {
                foreach (var t in tl!.Ball.Where(b => b.Kind == BallSegmentKind.Throw))
                {
                    var dist = System.Math.Sqrt(
                        System.Math.Pow(t.To.X - t.From.X, 2) + System.Math.Pow(t.To.Z - t.From.Z, 2));
                    var dur = t.T1 - t.T0;
                    Assert.True(dur > 0, $"seed={s}: 送球区間の所要が0以下");
                    var speed = dist / dur;
                    if (speed > worst) worst = speed;
                    Assert.True(speed <= capMps,
                        $"seed={s}: 送球が上限({capMps}m/s)を超えた {speed:F1}m/s（{dist:F1}m を {dur:F2}s）");
                    checkedAny++;
                }
            }
        }
        Assert.True(checkedAny > 0, "送球セグメントが検査対象に無い");
    }

    /// <summary>#301③: 送球速度が処理野手の肩に連動する（肩が強いほど早く着く）。</summary>
    [Fact]
    public void BackHomeThrow_StrongerArmArrivesEarlier()
    {
        var field = new FieldGeometry();
        double ArriveWithArm(double arm)
        {
            var play = OutfieldHit(85.0) with { FieldedX = 0, FieldedZ = 85.0, FielderThrowSpeedMps = arm };
            var baseTl = TimelineBuilder.BuildBattedBall(
                play, field.StandardAlignment(), field, null, runnersOn: true, TimelineBuilder.DescribeResult(play));
            // アンカーは遅く（＝ガードが効かない）置いて、物理だけを比較する。
            var tl = TimelineBuilder.AppendBackHomeThrows(baseTl, play, field, homeArriveAnchor: 0.0, runnerOut: true);
            return tl.Ball.Where(b => b.Kind == BallSegmentKind.Throw).Max(b => b.T1);
        }

        var weak = ArriveWithArm(26.0);
        var strong = ArriveWithArm(40.0);
        Assert.True(strong < weak - 0.1, $"肩が絵に反映されていない（弱肩{weak:F2}s / 強肩{strong:F2}s）");
    }

    /// <summary>この打球のバックホーム送球が物理的に本塁へ着ける最速時刻[s]（「間」ゼロで飛ばしきった値）。</summary>
    private static double EarliestBackHomeArrive(PlayTimeline baseTl, FieldingPlay play, FieldGeometry field)
        => TimelineBuilder
            .AppendBackHomeThrows(baseTl, play, field, homeArriveAnchor: 0.0, runnerOut: true)
            .Ball.Where(b => b.Kind == BallSegmentKind.Throw).Max(b => b.T1);

    /// <summary>
    /// #301③ 絶対条件: 絵が判定と矛盾しない。セーフ確定なら送球は走者より後に着き、
    /// アウト確定なら走者より先（同時まで）に着く。物理的に到達可能なアンカー範囲で検証する
    /// （到達不能なケースは <see cref="BackHomeThrow_PhysicallyImpossibleOut_KeepsPhysicalSpeed"/>）。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BackHomeThrow_NeverContradictsVerdict(bool runnerOut)
    {
        var field = new FieldGeometry();
        var play = OutfieldHit(70.0) with { FieldedX = 0, FieldedZ = 70.0 };
        var baseTl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: true, TimelineBuilder.DescribeResult(play));

        // 物理到達がアンカーの前後どちらに来ても矛盾しないことを、広い範囲のアンカーで確認する。
        var floor = EarliestBackHomeArrive(baseTl, play, field);
        for (var anchor = floor; anchor <= floor + 8.0; anchor += 0.5)
        {
            var tl = TimelineBuilder.AppendBackHomeThrows(baseTl, play, field, anchor, runnerOut);
            var arrive = tl.Ball.Where(b => b.Kind == BallSegmentKind.Throw).Max(b => b.T1);
            if (runnerOut)
                Assert.True(arrive <= anchor + 1e-6, $"アウト確定なのに送球が走者({anchor:F2})より遅い({arrive:F2})");
            else
                Assert.True(arrive >= anchor - 1e-6, $"セーフ確定なのに送球が走者({anchor:F2})より先に着く({arrive:F2})");
        }
    }

    /// <summary>
    /// #301③ の残余ケース: 走者の表示到達が送球の物理最速より早いとき（本塁レースはロジスティック抽選
    /// なので物理と逆に出ることがある）、矛盾は原理的に消せない。そのとき**送球を瞬間移動させない**
    /// ＝物理最速のまま描くことを固定する（ボールを光速で飛ばして絵を合わせる方が悪い）。
    /// </summary>
    [Fact]
    public void BackHomeThrow_PhysicallyImpossibleOut_KeepsPhysicalSpeed()
    {
        var field = new FieldGeometry();
        var play = OutfieldHit(70.0) with { FieldedX = 0, FieldedZ = 70.0 };
        var baseTl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: true, TimelineBuilder.DescribeResult(play));
        var floor = EarliestBackHomeArrive(baseTl, play, field);

        // 到達不能なアンカー（物理最速より前）でも、送球は物理最速で止まる＝それより前へ詰めない。
        var tl = TimelineBuilder.AppendBackHomeThrows(baseTl, play, field, floor - 2.0, runnerOut: true);
        var arrive = tl.Ball.Where(b => b.Kind == BallSegmentKind.Throw).Max(b => b.T1);
        Assert.Equal(floor, arrive, 6);

        // 送球速度も肩の範囲に収まっている（ガードのために光速化していない）。
        var arm = play.FielderThrowSpeedMps!.Value;
        foreach (var t in tl.Ball.Where(b => b.Kind == BallSegmentKind.Throw))
        {
            var dist = System.Math.Sqrt(
                System.Math.Pow(t.To.X - t.From.X, 2) + System.Math.Pow(t.To.Z - t.From.Z, 2));
            Assert.True(dist / (t.T1 - t.T0) <= arm * 1.2 + 1e-6, "ガードのために送球が光速化している");
        }
    }

    // ═══ Issue #298-1: 走者なしでは投手が本塁バックアップへ走らない ═══

    [Theory]
    [InlineData(BallZone.InfieldLeft)]
    [InlineData(BallZone.OutfieldLeft)]
    [InlineData(BallZone.OutfieldCenter)]
    [InlineData(BallZone.OutfieldRight)]
    public void FormationTable_PitcherHoldsWhenNoRunners_BacksUpHomeWhenRunnersOn(BallZone zone)
    {
        FormationAssignment Pitcher(bool runnersOn)
            => FormationTable.Default.Lookup(zone, runnersOn)!.Assignments
                .Single(a => a.Position == FieldPosition.Pitcher);

        // 走者なし: その場待機（本塁への送球がありえないので動かない）。
        Assert.Equal(FielderTask.Hold, Pitcher(false).Task);
        // 走者あり: 従来どおり本塁バックアップ。
        Assert.Equal(FielderTask.Backup, Pitcher(true).Task);
        Assert.Equal("home", Pitcher(true).Target);
    }

    [Fact]
    public void FormationTable_NoRunnersVariant_DiffersOnlyInPitcherRow()
    {
        foreach (BallZone zone in System.Enum.GetValues(typeof(BallZone)))
        {
            var on = FormationTable.Default.Lookup(zone, runnersOn: true)!;
            var off = FormationTable.Default.Lookup(zone, runnersOn: false)!;
            Assert.Equal(9, off.Assignments.Count); // 走者なしでも9人全員に役割
            foreach (var a in on.Assignments.Where(a => a.Position != FieldPosition.Pitcher))
            {
                Assert.Contains(a, off.Assignments); // 投手以外は完全一致
            }
        }
    }

    [Fact]
    public void NoRunners_PitcherDoesNotLeaveMoundForHomeBackup()
    {
        var field = new FieldGeometry();
        // 外野への飛球（従来は投手が本塁後方へ走っていたゾーン）。
        var play = FlyHitWithRoll();
        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));
        Assert.DoesNotContain(tl.Moves, m => m.Role == FieldPosition.Pitcher);

        // 走者ありなら従来どおりバックアップへ動く（機構を殺していないことの確認）。
        var withRunners = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: true, TimelineBuilder.DescribeResult(play));
        Assert.Contains(withRunners.Moves, m => m.Role == FieldPosition.Pitcher && m.Task == FielderTask.Backup);
    }

    /// <summary>
    /// 既定表と YAML の2面同期（Issue #298-1）。Unity 実プレーは YAML を読まず FormationTable.Default で
    /// 動くため、片方だけ直すと実ゲームに反映されない。内容が完全一致していることを機械的に検証する。
    /// </summary>
    [Fact]
    public void FormationsYaml_MatchesDefaultTableExactly()
    {
        var path = BalanceRegressionTests.FindDataFile("defensive-formations.yaml");
        var yaml = FormationsLoader.LoadFromFile(path);
        foreach (BallZone zone in System.Enum.GetValues(typeof(BallZone)))
        {
            foreach (var runnersOn in new[] { true, false })
            {
                var expected = FormationTable.Default.Lookup(zone, runnersOn);
                var actual = yaml.Lookup(zone, runnersOn);
                Assert.NotNull(actual);
                Assert.Equal(expected!.RunnersOn, actual!.RunnersOn);
                Assert.Equal(expected.Assignments.Count, actual.Assignments.Count);
                foreach (var a in expected.Assignments)
                {
                    Assert.Contains(a, actual.Assignments);
                }
            }
        }
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

    // ═══ Issue #336: 三遊間/一二塁間を抜けるヒットで SS/3B(2B) が棒立ちにならないこと ═══

    /// <summary>
    /// 三遊間を抜けた単打。着地点(-6,20, RangeM≈21m)は旧ロジックが InfieldLeft と誤分類していた距離
    /// （issue本文の実測と同型）。回収点(-33.6,53.8)は実際にレフトが処理する外野の深さ。
    /// closestRole=null にすると「最も惜しかった内野手」情報なしの構成になる（対照実験用）。
    /// </summary>
    private static FieldingPlay ThroughInfieldLeftHit(FieldPosition? closestRole = FieldPosition.Shortstop)
        => FieldingPlayFixtures.OutfieldHit(
                FieldPosition.LeftField, landingX: -6.0, landingZ: 20.0, fieldedAtSeconds: 2.0,
                fieldedX: -33.6, fieldedZ: 53.8, result: BattedBallResult.Single, throughInfield: true)
            with
            {
                ClosestInfielderRole = closestRole,
                ClosestApproachX = closestRole is null ? null : -18.0,
                ClosestApproachZ = closestRole is null ? null : 38.0,
            };

    /// <summary>一二塁間を抜けた単打（左右対称、issue本文の「対称の症状」）。</summary>
    private static FieldingPlay ThroughInfieldRightHit(FieldPosition? closestRole = FieldPosition.SecondBase)
        => FieldingPlayFixtures.OutfieldHit(
                FieldPosition.RightField, landingX: 6.0, landingZ: 20.0, fieldedAtSeconds: 2.0,
                fieldedX: 33.6, fieldedZ: 53.8, result: BattedBallResult.Single, throughInfield: true)
            with
            {
                ClosestInfielderRole = closestRole,
                ClosestApproachX = closestRole is null ? null : 18.0,
                ClosestApproachZ = closestRole is null ? null : 38.0,
            };

    /// <summary>深い三遊間の二塁打（中継しきい値60mを超える）。カットオフ検証専用。</summary>
    private static FieldingPlay ThroughInfieldLeftHit_Deep()
        => FieldingPlayFixtures.OutfieldHit(
                FieldPosition.LeftField, landingX: -6.0, landingZ: 20.0, fieldedAtSeconds: 3.0,
                fieldedX: -45.0, fieldedZ: 95.0, result: BattedBallResult.Double, throughInfield: true)
            with
            {
                ClosestInfielderRole = FieldPosition.Shortstop,
                ClosestApproachX = -18.0,
                ClosestApproachZ = 38.0,
            };

    [Fact]
    public void ThroughInfieldLeft_OldRangeBasedClassification_WouldHaveBeenInfield()
    {
        // 回帰の前提確認: 着地点距離(RangeM)だけで分類すると内野ゾーンに落ちる
        // （＝これが直したい誤分類そのものであることをテスト自身が保証する）。
        var play = ThroughInfieldLeftHit();
        var oldZone = FormationTable.Classify(play.BearingDeg, play.RangeM);
        Assert.Equal(BallZone.InfieldLeft, oldZone);
    }

    [Fact]
    public void ThroughInfieldLeft_ClassifiesAsOutfieldZone_SsAndThirdBaseMove()
    {
        var field = new FieldGeometry();
        var play = ThroughInfieldLeftHit();
        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

        // 修正前は着地点距離で InfieldLeft に誤分類され、SS/3B が一歩も動かなかった（Issue #336①）。
        // 回収点(fieldPoint)基準なら OutfieldLeft になり、両者に移動が生成される。
        Assert.Contains(tl.Moves, m => m.Role == FieldPosition.Shortstop);
        Assert.Contains(tl.Moves, m => m.Role == FieldPosition.ThirdBase);
        Assert.Contains(tl.Moves, m => m.Task == FielderTask.Cutoff); // 中継役が実在する（#336③の前提）
    }

    [Fact]
    public void ThroughInfieldRight_ClassifiesAsOutfieldZone_SecondBaseMoves()
    {
        var field = new FieldGeometry();
        var play = ThroughInfieldRightHit();
        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

        // 一二塁間の対称ケース（issue本文）。2B が棒立ちにならない。
        Assert.Contains(tl.Moves, m => m.Role == FieldPosition.SecondBase);
    }

    [Fact]
    public void ThroughInfieldLeft_CutoffFielder_RelayPointMatchesOutfieldReturnThrow()
    {
        var field = new FieldGeometry();
        var play = ThroughInfieldLeftHit_Deep();
        var baseTl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

        var cutoffMove = baseTl.Moves.Where(m => m.Task == FielderTask.Cutoff).OrderBy(m => m.T0).First();
        Assert.Equal(FieldPosition.Shortstop, cutoffMove.Role);

        var tl = TimelineBuilder.AppendOutfieldReturnThrow(baseTl, play, field, "second", runnerArriveAnchor: 9.0);
        var throws = tl.Ball.Where(b => b.Kind == BallSegmentKind.Throw).ToList();
        Assert.Equal(2, throws.Count); // 外野→中継(SS)→二塁（#336③、#136の再発防止）
        Assert.True(Near(throws[0].To, cutoffMove.To), "中継点が中継野手(SS)の立ち位置と一致しない");
        Assert.True(Near(throws[1].From, cutoffMove.To), "2本目送球の起点が中継野手の立ち位置と一致しない");
    }

    [Fact]
    public void ThroughInfieldLeft_ClosestInfielder_DivesTowardBall_ThenReturnsToDefaultPosition()
    {
        var field = new FieldGeometry();
        var play = ThroughInfieldLeftHit(); // ClosestInfielderRole = Shortstop
        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

        var ssMoves = tl.Moves.Where(m => m.Role == FieldPosition.Shortstop).OrderBy(m => m.T0).ToList();
        Assert.True(ssMoves.Count >= 2, "SSに「寄る→戻る」の複数レッグが生成されていない");
        Assert.Equal(FielderTask.Chase, ssMoves[0].Task); // 最初のレッグ=打球ラインへ寄る/飛びつく

        // 時系列は単調増加（レッグの逆転はFielderAtの補間バグに直結する, Issue #336②）。
        for (var i = 1; i < ssMoves.Count; i++)
        {
            Assert.True(ssMoves[i].T0 >= ssMoves[i - 1].T0, "SSのレッグが時系列で逆転している");
        }

        // 決着後は必ず**出発した場所**（＝判定が距離を測った FieldGeometry の守備位置）へ戻り切る。
        // 戻り先を表示用の別表にすると、出発と帰着が別座標＝戻りレッグの長さが嘘になる。
        var (hx, hz) = FieldGeometry.StandardPosition(FieldPosition.Shortstop);
        Assert.True(Near(ssMoves[^1].To, new TimelinePoint(hx, hz)), "決着後に定位置へ戻り切っていない");
    }

    [Fact]
    public void ThroughInfieldRight_ClosestInfielder_DivesTowardBall_ThenReturnsToDefaultPosition()
    {
        var field = new FieldGeometry();
        var play = ThroughInfieldRightHit(); // ClosestInfielderRole = SecondBase
        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

        var moves = tl.Moves.Where(m => m.Role == FieldPosition.SecondBase).OrderBy(m => m.T0).ToList();
        Assert.True(moves.Count >= 2, "2Bに「寄る→戻る」の複数レッグが生成されていない");
        Assert.Equal(FielderTask.Chase, moves[0].Task);
        var (hx, hz) = FieldGeometry.StandardPosition(FieldPosition.SecondBase);
        Assert.True(Near(moves[^1].To, new TimelinePoint(hx, hz)), "決着後に定位置へ戻り切っていない");
    }

    [Fact]
    public void ClosestInfielderDive_OnlyAffectsMoves_NotResultOrBallOrRunners()
    {
        var field = new FieldGeometry();
        var withDive = ThroughInfieldLeftHit(closestRole: FieldPosition.Shortstop);
        var withoutDive = ThroughInfieldLeftHit(closestRole: null);

        var tlWith = TimelineBuilder.BuildBattedBall(
            withDive, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(withDive));
        var tlWithout = TimelineBuilder.BuildBattedBall(
            withoutDive, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(withoutDive));

        // 寄る/飛びつく演出の有無は Moves 以外へ波及しない（表示専用・判定/帯不変, Issue #336②）。
        Assert.Equal(tlWith.Result, tlWithout.Result);
        Assert.Equal(tlWith.ResolvedAt, tlWithout.ResolvedAt, 9);
        Assert.Equal(tlWith.Ball.Count, tlWithout.Ball.Count);
        for (var i = 0; i < tlWith.Ball.Count; i++)
        {
            Assert.Equal(tlWith.Ball[i].From, tlWithout.Ball[i].From);
            Assert.Equal(tlWith.Ball[i].To, tlWithout.Ball[i].To);
            Assert.Equal(tlWith.Ball[i].T0, tlWithout.Ball[i].T0, 9);
            Assert.Equal(tlWith.Ball[i].T1, tlWithout.Ball[i].T1, 9);
        }
        Assert.Equal(tlWith.Runners.Count, tlWithout.Runners.Count);
        for (var i = 0; i < tlWith.Runners.Count; i++)
        {
            Assert.Equal(tlWith.Runners[i].OutAtEnd, tlWithout.Runners[i].OutAtEnd);
            Assert.Equal(tlWith.Runners[i].T1, tlWithout.Runners[i].T1, 9);
        }
        Assert.Equal(tlWith.Judgements.Count, tlWithout.Judgements.Count);

        // Moves は実際に変わる（この演出が効いていることの確認）。
        var ssCountWith = tlWith.Moves.Count(m => m.Role == FieldPosition.Shortstop);
        var ssCountWithout = tlWithout.Moves.Count(m => m.Role == FieldPosition.Shortstop);
        Assert.True(ssCountWith > ssCountWithout, "寄る演出を付けても SS の move 数が増えていない");
    }

    /// <summary>
    /// 実際のエンジン出力（弾道積分→守備解決）で「旧ロジックなら内野ゾーンに誤分類されていたはずの
    /// 抜けた打球」を探し、タイムラインが外野ゾーンの陣形（＝最も惜しかった内野手の移動込み）になる
    /// ことを確認する（issue本文の実測と同じ手法）。
    /// </summary>
    [Fact]
    public void ThroughInfieldHit_FromRealPhysics_GetsOutfieldFormation_AndClosestInfielderMoves()
    {
        var aero = new KokoSim.Engine.Match.Pitching.Aerodynamics();
        var field = new FieldGeometry();
        var coeff = new FieldingCoefficients { ErrorBaseProb = 0, IrregularBounceProb = 0 };
        var found = false;
        for (var exit = 30.0; exit <= 48.0 && !found; exit += 2.0)
        for (var launch = -8.0; launch <= 8.0 && !found; launch += 2.0)
        foreach (var bearing in new[] { -44.0, -32.0, -20.0, -12.0 })
        {
            if (found) break;
            var play = FieldingResolver.ResolveDetailed(
                new KokoSim.Engine.Match.Batting.BattedBall
                {
                    ExitVelocityMps = exit, LaunchAngleDeg = launch, BearingDeg = bearing,
                },
                field, aero, KokoSim.Engine.Players.BatterAttributes.LeagueAverage,
                field.StandardAlignment(), coeff, new Xoshiro256Random(42));
            if (!play.ThroughInfield || play.Result is not (BattedBallResult.Single or BattedBallResult.Double))
                continue;

            // 修正前の分類（着地点距離）が「内野扱い」だったケースだけを検証対象にする
            // （元々外野扱いだったケースを拾って検証にならないことを防ぐ）。
            var oldZone = FormationTable.Classify(play.BearingDeg, play.RangeM);
            if (oldZone is not (BallZone.InfieldLeft or BallZone.InfieldRight or BallZone.Bunt)) continue;

            var tl = TimelineBuilder.BuildBattedBall(
                play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

            Assert.NotNull(play.ClosestInfielderRole);
            Assert.Contains(tl.Moves, m => m.Role == play.ClosestInfielderRole);
            found = true;
        }
        Assert.True(found, "内野ゾーンに誤分類されていた実データが見つからない（探索条件を見直すこと）");
    }
}
