using System.Globalization;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Players;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// トンネル＝股・脇を抜けて球が外野へ行く失策（issue #632）。
///
/// <para>回帰の実害: 失策は「お手玉（打球方向へ3m）」と「悪送球（一塁の脇へ抜ける）」の2種しかなく、
/// <b>どんな失策でも球は内野から出なかった</b>。実際に二塁走者が生還する失策の典型（内野手を抜けて外野へ）を
/// 表現できず、そのぶんの進塁を失策連鎖（一律確率）が球の位置も見ずに配っていた＝
/// 絵（3mのお手玉）と結果（二塁走者が生還）が食い違う根本原因。</para>
///
/// <para>トンネルは球の行き先が物理層で変わるので、走者の生還は<b>外野からの返球との時間勝負</b>になる
/// （不変条件#1）。失策の総数は変えない＝捕球エラーの内訳として按分する（オーナー決定 2026-08-13）。</para>
///
/// <para><b>恒等の実測</b>: <c>TunnelShareProb=0</c> にした状態で決定論ゲート
/// （<c>EngineDeterminismGateTests</c> 200行）と観測 digest（<c>DebugTraceTests</c>）が
/// <b>本 issue 以前の凍結ベースラインに対して緑</b>であることを確認済み（2026-08-13）。
/// ＝0 のときは内訳ロールを1本も引かない。</para>
/// </summary>
public sealed class ErrorTunnelTests
{
    private readonly ITestOutputHelper _out;
    public ErrorTunnelTests(ITestOutputHelper output) => _out = output;

    private static readonly Aerodynamics Aero = new();
    private static readonly FieldGeometry Field = new();

    /// <summary>捕球エラーを必ず起こす係数（クランプ上限も開ける）。内訳ロールだけを見たいので送球ロールは止める。</summary>
    private static FieldingCoefficients AlwaysCatchError(double tunnelShare) => new()
    {
        ErrorBaseProb = 1.0,
        ErrorMaxProb = 1.0,
        ThrowErrorBaseProb = 0.0,
        TunnelShareProb = tunnelShare,
    };

    private static FieldingPlay Grounder(FieldingCoefficients coeff, IRandomSource? rng = null,
        double exitVelocityMps = 30.0, double bearingDeg = -18.0)
        => FieldingResolver.ResolveDetailed(
            new BattedBall { ExitVelocityMps = exitVelocityMps, LaunchAngleDeg = 3.0, BearingDeg = bearingDeg },
            Field, Aero, BatterAttributes.LeagueAverage,
            Field.StandardAlignment(new FielderAttributes { Fielding = 50 }),
            coeff, rng ?? new Xoshiro256Random(777));

    private static double DistanceFromHome(FieldingPlay p)
    {
        var x = p.FieldedX ?? 0.0;
        var z = p.FieldedZ ?? 0.0;
        return System.Math.Sqrt(x * x + z * z);
    }

    private static bool IsOutfield(FieldPosition p)
        => p is FieldPosition.LeftField or FieldPosition.CenterField or FieldPosition.RightField;

    // ===== ① 既定0で恒等・正値で内訳ロール1本 =====

    [Fact]
    public void ZeroShare_KeepsCatchError_AndDrawsNoExtraRandom()
    {
        var zero = new CountingRandomSource(new Xoshiro256Random(777));
        var full = new CountingRandomSource(new Xoshiro256Random(777));

        var noTunnel = Grounder(AlwaysCatchError(0.0), zero);
        var tunnel = Grounder(AlwaysCatchError(1.0), full);

        _out.WriteLine($"消費: 0%={zero.Stats.Draws} / 100%={full.Stats.Draws}"
                       + $" 種別: {noTunnel.ErrorKind} → {tunnel.ErrorKind}");

        Assert.Equal(BattedBallResult.Error, noTunnel.Result);
        Assert.Equal(FieldingErrorKind.Catch, noTunnel.ErrorKind);
        Assert.Equal(FieldingErrorKind.Tunnel, tunnel.ErrorKind);
        // 内訳ロールはちょうど1本＝0のときは引いていない（乱数消費順が従来と一致する根拠）。
        Assert.Equal(zero.Stats.Draws + 1, full.Stats.Draws);
    }

    /// <summary>トンネルは捕球エラーの内訳＝どちらの設定でも「失策になること」自体は変わらない。</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.3)]
    [InlineData(1.0)]
    public void AnyShare_StillProducesAnError(double share)
        => Assert.Equal(BattedBallResult.Error, Grounder(AlwaysCatchError(share)).Result);

    // ===== ② 球は外野へ行き、外野手が回収する =====

    [Fact]
    public void Tunnel_SendsTheBallToTheOutfield_AndAnOutfielderRetrievesIt()
    {
        var catchError = Grounder(AlwaysCatchError(0.0));
        var tunnel = Grounder(AlwaysCatchError(1.0));

        _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "お手玉: {0} が {1:F1}m で処理（{2:F2}s）／トンネル: {3} が {4:F1}m で回収（{5:F2}s）",
            catchError.FielderRole, DistanceFromHome(catchError), catchError.FieldedAtSeconds,
            tunnel.FielderRole, DistanceFromHome(tunnel), tunnel.FieldedAtSeconds));

        // 修正前（お手玉のみ）: 球は内野の中で止まる。
        Assert.True(DistanceFromHome(catchError) <= 46.0);
        Assert.False(catchError.ThroughInfield);

        // トンネル: 内野を抜けた打球とまったく同じ経路＝外野手が内野の外で回収する。
        Assert.True(tunnel.ThroughInfield, "トンネルなのに内野を抜けた扱いになっていない");
        Assert.True(IsOutfield(tunnel.FielderRole!.Value), $"回収したのが外野手でない（{tunnel.FielderRole}）");
        Assert.True(DistanceFromHome(tunnel) > 46.0,
            $"回収点が内野の中（{DistanceFromHome(tunnel):F1}m）＝球が外野へ行っていない");
        Assert.True(tunnel.FieldedAtSeconds > catchError.FieldedAtSeconds, "回収がお手玉より早い");
    }

    /// <summary>失策は抜かれた内野手に付く（処理野手＝回収した外野手と混ざらない）。</summary>
    [Fact]
    public void Tunnel_AttributesTheErrorToTheBeatenInfielder()
    {
        var tunnel = Grounder(AlwaysCatchError(1.0));

        Assert.NotNull(tunnel.ErrorFielderRole);
        Assert.False(IsOutfield(tunnel.ErrorFielderRole!.Value), "失策が外野手に付いている");
        Assert.NotEqual(tunnel.FielderRole, tunnel.ErrorFielderRole);
        // 他の種別では null＝FielderRole と同じ（既存の帰属は変えない）。
        Assert.Null(Grounder(AlwaysCatchError(0.0)).ErrorFielderRole);
    }

    /// <summary>
    /// 実況文も失策を犯した野手で名乗る。実試合の初回キャプチャで<b>「レフトのエラー」</b>と出ていた
    /// （回収した外野手の名前）＝記録・実況・Balance CLI の3箇所すべてが処理野手を見ていたので、
    /// 帰属側（ErrorFielderRole）へ寄せた。
    /// </summary>
    [Fact]
    public void Tunnel_IsDescribedAsTheInfieldersError()
    {
        var tunnel = Grounder(AlwaysCatchError(1.0));
        var text = TimelineBuilder.DescribeResult(tunnel);
        _out.WriteLine($"{tunnel.ErrorFielderRole} が抜かれ {tunnel.FielderRole} が回収 → 「{text}」");

        Assert.DoesNotContain("レフト", text);
        Assert.DoesNotContain("センター", text);
        Assert.DoesNotContain("ライト", text);
        Assert.EndsWith("のエラー", text);
    }

    // ===== ③ 絵: テロップは抜かれた瞬間・お手玉の絵は出さない =====

    [Fact]
    public void Tunnel_ErrorTelopFiresWhenTheInfielderIsBeaten_NotWhenTheBallIsRetrieved()
    {
        var tunnel = Grounder(AlwaysCatchError(1.0));
        Assert.NotNull(tunnel.ErrorAtSeconds);
        Assert.True(tunnel.ErrorAtSeconds < tunnel.FieldedAtSeconds - 0.5,
            "抜かれた瞬間と回収時刻が同じ＝テロップが数秒遅れて出る");

        var tl = TimelineBuilder.BuildBattedBall(
            tunnel, Field.StandardAlignment(), Field, null, runnersOn: true,
            TimelineBuilder.DescribeResult(tunnel));

        const double ContactAt = 0.85;   // TimelineBuilder の接触時刻（private const と同値・既存テストと同じ書き方）
        var err = Assert.Single(tl.Judgements, j => j.Kind == JudgementKind.Error);
        _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "エラー表示 {0:F2}s（抜かれた {1:F2}s / 回収 {2:F2}s）",
            err.T, ContactAt + tunnel.ErrorAtSeconds!.Value, ContactAt + tunnel.FieldedAtSeconds!.Value));
        Assert.Equal(ContactAt + tunnel.ErrorAtSeconds!.Value, err.T, 3);
    }

    /// <summary>
    /// トンネルに「3m弾いて拾い直す」の絵（issue #439/#629 のお手玉）は出さない。球は止まらず外野へ転がり、
    /// 抜かれた内野手だけが打球ラインへ飛びつく（Issue #336② の寄るレッグ）。
    /// </summary>
    [Fact]
    public void Tunnel_DoesNotDrawTheBobblePicture()
    {
        var tunnel = Grounder(AlwaysCatchError(1.0));
        var tl = TimelineBuilder.BuildBattedBall(
            tunnel, Field.StandardAlignment(), Field, null, runnersOn: true,
            TimelineBuilder.DescribeResult(tunnel));

        Assert.Null(tl.ErrorPickupAt);   // こぼした球の拾い直し（issue #629）は無い

        // 球が内野の中で止まる区間が無い＝「弾いてその場に転がる」絵になっていない。
        foreach (var b in tl.Ball)
        {
            var rest = System.Math.Sqrt(b.To.X * b.To.X + b.To.Z * b.To.Z);
            Assert.True(b.Kind != BallSegmentKind.Roll || rest > 46.0,
                $"内野の中（{rest:F1}m）で止まる転がりがある＝お手玉の絵が出ている");
        }

        // 追いかけレッグは「抜かれた内野手が飛びつく」1本だけ（外野手の回収は FieldBall 役）。
        var chases = tl.Moves.Where(m => m.Task == FielderTask.Chase).ToList();
        Assert.All(chases, m => Assert.Equal(tunnel.ErrorFielderRole, m.Role));
    }

    // ===== ④ 二塁走者の生還は「返球との時間勝負」 =====

    /// <summary>
    /// 二塁走者の生還が一律確率ではなく<b>回収点からの返球</b>で決まる。同じ「失策で出塁」でも、
    /// 球が内野で止まるお手玉なら還れず、外野へ抜けたトンネルなら還れる＝
    /// 「球が外野へ行ったから還った」という因果（不変条件#1）。
    /// </summary>
    [Fact]
    public void SecondRunner_ScoringDependsOnWhereTheBallWent()
    {
        var bothScored = Score(Grounder(AlwaysCatchError(1.0)), out var tunnelOuts);
        var bobbleScored = Score(Grounder(AlwaysCatchError(0.0)), out var bobbleOuts);

        _out.WriteLine($"二塁走者の生還/1000: トンネル {bothScored}（本塁憤死 {tunnelOuts}）"
                       + $" ／ お手玉 {bobbleScored}（本塁憤死 {bobbleOuts}）");

        Assert.True(bothScored > bobbleScored * 2,
            $"外野へ抜けても生還が増えない（トンネル {bothScored} vs お手玉 {bobbleScored}）");
        Assert.True(bothScored < 1000, "トンネルなら無条件生還になっている＝時間勝負になっていない");
    }

    private static int Score(FieldingPlay play, out int homeOuts)
    {
        var coeff = new BaserunningCoefficients();
        var rng = new Xoshiro256Random(20260813);
        var situation = new HomePlaySituation(
            new Vector3D(play.FieldedX ?? 0, 0, play.FieldedZ ?? 0),
            play.FieldedAtSeconds ?? 0,
            play.FielderThrowSpeedMps ?? 30.0,
            play.FielderFielding);
        var home = new HomePlayContext(
            Field, situation, new TacticsCoefficients(), 0.5, DefenseDepth.Normal, IsFly: false);

        var scored = 0;
        homeOuts = 0;
        for (var i = 0; i < 1000; i++)
        {
            var bases = new BaseState { Second = new Player { Speed = 50, Baserunning = 50 } };
            var (runs, _, baseOuts, _, _, _) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.ReachedOnError, new Player(), 0, coeff, rng,
                collectMoves: true, home, errorKind: play.ErrorKind);
            scored += runs;
            homeOuts += baseOuts.Home;
        }
        return scored;
    }
}
