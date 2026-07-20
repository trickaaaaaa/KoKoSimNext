using System;
using System.Collections.Generic;
using System.Diagnostics;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// タイムライン記録（CaptureTimelines）のコスト計測と「観戦する試合だけON」の設計保証。
///
/// 設計の要（設計者Claude指示）: 全国4000校の裏試合（AI校同士）は <see cref="Nation.AggregateMatch"/> の
/// 強さベース高速抽象シムで決着し、<see cref="GameEngine.Play"/>・<see cref="GameContext"/> を**一切通らない**。
/// よってタイムライン記録は構造的にゼロ。GameEngine を通るのは監督自身の一戦だけで、そこでのみ ON にする。
/// 本テストは (1) 既定 OFF の保証、(2) 大量シム経路（既定コンテキスト）が非記録であることの保証、
/// (3) ON/OFF のコスト差の実測（ITestOutputHelper に報告）を行う。
/// </summary>
public sealed class CaptureTimelinesCostTests
{
    private readonly ITestOutputHelper _out;
    public CaptureTimelinesCostTests(ITestOutputHelper output) => _out = output;

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

    [Fact]
    public void CaptureTimelines_DefaultsOff()
    {
        // 既定コンテキスト（＝Balance の大量シム・その他バルク経路が使うもの）は必ず OFF。
        Assert.False(new GameContext().CaptureTimelines);
    }

    [Fact]
    public void DefaultContext_ProducesNoTimelines()
    {
        // 大量シムと同じ既定コンテキストで回した試合は、全プレーのタイムラインが null（記録なし）。
        var r = GameEngine.Play(BuildTeam("A"), BuildTeam("H"), new GameContext(), new Xoshiro256Random(1));
        Assert.All(r.Log, e => Assert.Null(e.Timeline));
    }

    [Fact]
    [Trait("Category", "Heavy")]
    public void Cost_OnVsOff_250Games()
    {
        // 250 試合。コスト「比」を見るのが目的なので試合数は必要最小限にする
        // （1000 試合だと CI で単体6分かかり、PRのCI時間を独占していた。issue #17）。
        const int games = 250;

        var (offMs, offBytes, offTimelines) = Measure(games, capture: false);
        var (onMs, onBytes, onTimelines) = Measure(games, capture: true);

        _out.WriteLine($"CaptureTimelines コスト計測（{games} 試合・league-average）:");
        _out.WriteLine($"  OFF: {offMs,7:N0} ms / {offBytes / 1024.0 / 1024.0,8:N1} MB alloc / timelines={offTimelines}");
        _out.WriteLine($"  ON : {onMs,7:N0} ms / {onBytes / 1024.0 / 1024.0,8:N1} MB alloc / timelines={onTimelines}");
        _out.WriteLine($"  差 : ×{(double)onMs / Math.Max(1, offMs):N2} 時間 / +{(onBytes - offBytes) / 1024.0 / 1024.0:N1} MB");

        // OFF は必ずタイムライン0、ON は多数生成される（記録が効いている証拠）。
        Assert.Equal(0, offTimelines);
        Assert.True(onTimelines > games, "ON では試合数を超えるタイムラインが生成される");

        // ガードレール（回帰検知）は**アロケーション**で見る。
        // 経過時間の比は実行環境に強く依存し（ローカル ×4.9 に対し GitHub の共有ランナーでは ×9.4）、
        // 他テストとの CPU 競合でも動くため、閾値付きの合否判定には使えない。
        // 一方アロケーション量は環境に依らずほぼ一定で、記録処理が太った回帰を素直に捉える。
        Assert.True(onBytes < offBytes * 2, $"ON の割り当てが想定外に多い（on={onBytes}B off={offBytes}B）");

        // 時間は「桁が変わっていないか」だけを見る極めて緩い上限（測定値自体は上のログで確認する）。
        Assert.True(onMs < offMs * 25 + 2000, $"ON が想定外に遅い（on={onMs}ms off={offMs}ms）");
    }

    private static (long Ms, long Bytes, long Timelines) Measure(int games, bool capture)
    {
        // JIT ウォームアップ。
        GameEngine.Play(BuildTeam("A"), BuildTeam("H"),
            new GameContext { CaptureTimelines = capture }, new Xoshiro256Random(999));

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // プロセス全体ではなく**現在のスレッド**の割り当てを見る。
        // GetTotalAllocatedBytes はプロセス全体の値なので、xUnit が他テストを並列実行していると
        // そちらの割り当てまで混入し、計測が数倍にぶれる（単独 119MB → 並列 742MB を実測）。
        var bytes0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        long timelines = 0;
        for (var s = 1; s <= games; s++)
        {
            var r = GameEngine.Play(BuildTeam("A"), BuildTeam("H"),
                new GameContext { CaptureTimelines = capture }, new Xoshiro256Random((ulong)s));
            foreach (var e in r.Log)
                if (e.Timeline is not null) timelines++;
        }
        sw.Stop();
        var bytes1 = GC.GetAllocatedBytesForCurrentThread();
        return (sw.ElapsedMilliseconds, bytes1 - bytes0, timelines);
    }
}
