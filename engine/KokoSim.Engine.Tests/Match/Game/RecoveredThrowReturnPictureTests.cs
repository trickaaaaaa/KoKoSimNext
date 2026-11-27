using System;
using System.Linq;
using System.Threading.Tasks;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Nation;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 悪送球を一塁手自身が拾い直してアウトになる打席（issue #1735）の絵が、判定と食い違わないこと。
/// 拾った球を持って一塁へ戻って踏んだ瞬間がアウトで、拾う時刻はアウトの時刻より塁へ戻る時間だけ前。
/// 走者なし・走者ありの両方（走者ありで最初の送球を差し替える経路は従来の絵＝この検査の対象外）。
/// 一塁カバーの投手が拾う打席は対象外（判定が投手の走り込み時間を払っていないため、判定を変えずには揃えられない）。
/// </summary>
public sealed class RecoveredThrowReturnPictureTests
{
    [Fact]
    public void SelfRecovery_FirstBasemanIsBackOnTheBagAtTheOutTime_WithAndWithoutRunners()
    {
        const int games = 160;
        var noRunners = new int[games];
        var withRunners = new int[games];
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, games, i =>
        {
            var rng = new Xoshiro256Random(18_350UL + (ulong)i);
            var away = StrengthTeamFactory.Create(50, "A", rng);
            var home = StrengthTeamFactory.Create(50, "B", rng);
            var r = GameEngine.Play(away, home, new GameContext { CaptureTimelines = true }, rng);
            foreach (var e in r.Log)
            {
                if (!e.ThrowRecovered || e.Timeline is not { } tl) continue;
                // 一塁手（または一塁カバーの投手）自身が拾った打席＝同じ野手が持ち運ぶ区間がある
                var carry = tl.Ball.FirstOrDefault(b => b.Kind == BallSegmentKind.Carry && b.ToBase == "first" && b.FromRole == b.ToRole);
                if (carry is null) continue;
                var picker = carry.FromRole!.Value;
                // 一塁カバーの投手（一塁手が捕って投手へ送る打球）は、判定の往復式が「塁にいる前提」で投手の走り込み時間を
                // 払っていない＝判定を変えない表示専用の範囲では、走者到達までに戻れない打席がある。一塁手本人だけを厳密に見る。
                if (picker != FieldPosition.FirstBase) continue;
                var chase = tl.Moves.LastOrDefault(m => m.Role == picker && m.Task == FielderTask.Chase && m.T1 <= carry.T0 + 1e-6);
                var back = tl.Moves.FirstOrDefault(m => m.Role == picker && m.Task == FielderTask.CoverBase && m.T0 >= carry.T0 - 1e-6);
                var outJudge = tl.Judgements.FirstOrDefault(j => j.Label == "打" && j.Kind == JudgementKind.Out);
                if (chase is null || back is null || outJudge is null)
                {
                    failures.Add($"game{i}: 拾う/戻る/アウト判定のどれかが無い");
                    continue;
                }
                var first = new FieldGeometry().FirstBase;
                if (Math.Abs(back.To.X - first.X) > 1e-6 || Math.Abs(back.To.Z - first.Z) > 1e-6)
                    failures.Add($"game{i}: 戻り先が一塁でない");
                if (back.T1 < outJudge.T - 1e-6 || back.T1 > outJudge.T + 0.6)
                    failures.Add($"game{i}: 一塁へ戻る時刻 {back.T1:F2} とアウトの時刻 {outJudge.T:F2} が離れている");
                if (carry.T0 >= outJudge.T)
                    failures.Add($"game{i}: 拾う時刻がアウトの時刻以降");
                if (e.BaseFirstBefore || e.BaseSecondBefore || e.BaseThirdBefore) withRunners[i]++; else noRunners[i]++;
            }
        });
        Assert.Empty(failures);
        Assert.True(noRunners.Sum() > 0, "走者なしで一塁手が拾い直してアウトの打席が1件も出ない（前提が崩れている）");
        Assert.True(withRunners.Sum() > 0, "走者ありで一塁手が拾い直してアウトの打席が1件も出ない（前提が崩れている）");
    }
}
