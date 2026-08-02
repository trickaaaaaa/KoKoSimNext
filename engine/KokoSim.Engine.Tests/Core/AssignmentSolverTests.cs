using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using Xunit;

namespace KokoSim.Engine.Tests.Core;

/// <summary>
/// 最大重み割当（<see cref="AssignmentSolver"/>, issue #384）。AI校の守備配置を毎回解くので、
/// <b>自校の 8! 全探索と同じ最適解</b>を返すことを総当たりとの突き合わせで固定する
/// （自校＝<see cref="Season.RosterTeamBuilder"/> は 40320 通りの全探索。AI校は4000校×二分探索26回で
/// 回すため全探索は使えないが、答えが違うと「同じ能力なのにAI校だけ守備が弱い」非対称が生まれる）。
/// </summary>
public sealed class AssignmentSolverTests
{
    /// <summary>行数ぶんの総当たりで最大重み和を求める（正解の定義）。</summary>
    private static double BruteForceBest(double[,] w)
    {
        var rows = w.GetLength(0);
        var cols = w.GetLength(1);
        var best = double.NegativeInfinity;
        var used = new bool[cols];
        var pick = new int[rows];

        void Recurse(int r, double acc)
        {
            if (r == rows) { if (acc > best) best = acc; return; }
            for (var c = 0; c < cols; c++)
            {
                if (used[c]) continue;
                used[c] = true; pick[r] = c;
                Recurse(r + 1, acc + w[r, c]);
                used[c] = false;
            }
        }
        Recurse(0, 0);
        return best;
    }

    private static double TotalOf(double[,] w, int[] assign)
    {
        var sum = 0.0;
        for (var r = 0; r < assign.Length; r++) if (assign[r] >= 0) sum += w[r, assign[r]];
        return sum;
    }

    [Fact]
    public void SmallKnownMatrix_PicksTheOptimum()
    {
        // 対角を選ぶと 3+3+3=9、反対角を選ぶと 1+2+1=4。最適は対角。
        var w = new double[,]
        {
            { 3, 1, 1 },
            { 1, 3, 2 },
            { 1, 1, 3 },
        };
        var assign = AssignmentSolver.MaxWeight(w);
        Assert.Equal(new[] { 0, 1, 2 }, assign);
        Assert.Equal(9.0, TotalOf(w, assign), 9);
    }

    [Fact]
    public void GreedyTrap_BeatsTakeTheBestFirst()
    {
        // 貪欲（行0から最良を取る）だと 0→列0(10) を掴んで 1→列1(1) の合計11。
        // 最適は 0→列1(9) と 1→列0(9) の合計18。準最適解との差が出る形。
        var w = new double[,]
        {
            { 10, 9 },
            {  9, 1 },
        };
        var assign = AssignmentSolver.MaxWeight(w);
        Assert.Equal(18.0, TotalOf(w, assign), 9);
    }

    [Theory]
    [InlineData(8, 8)]
    [InlineData(8, 20)]   // AI校の実形（守備8枠 × 在校生20人超）
    [InlineData(5, 9)]
    [InlineData(1, 1)]
    public void MatchesBruteForce_OverManyRandomMatrices(int rows, int cols)
    {
        // 総当たりは cols! に近く伸びるので、行数の多い形は本数を絞る（8×20 は 20P8 ＝ 5,079,110,400
        // なので総当たり不可）。ここでは総当たり可能な形だけ全比較し、大きい形は下の別テストで担保する。
        if (rows == 8 && cols == 20) return;

        var rng = new Xoshiro256Random(20260802);
        for (var trial = 0; trial < 200; trial++)
        {
            var w = new double[rows, cols];
            for (var r = 0; r < rows; r++)
                for (var c = 0; c < cols; c++)
                    w[r, c] = System.Math.Round(rng.NextDouble() * 100, 3);

            var assign = AssignmentSolver.MaxWeight(w);

            // 割当が正当（全行に列が付き、列は重複しない）。
            Assert.Equal(rows, assign.Length);
            Assert.All(assign, c => Assert.InRange(c, 0, cols - 1));
            Assert.Equal(rows, assign.Distinct().Count());

            Assert.Equal(BruteForceBest(w), TotalOf(w, assign), 6);
        }
    }

    [Fact]
    public void Rectangular_AssignsEveryRow_AndIsOptimalAgainstSampledSwaps()
    {
        // 8×20（AI校の実形）。総当たりは不可なので「どの1手交換でも改善しない」＝局所最適性で確かめる。
        // ハンガリアン法の解は大域最適なので、任意の交換で悪化する（等しくなることはある）。
        var rng = new Xoshiro256Random(4242);
        for (var trial = 0; trial < 50; trial++)
        {
            var w = new double[8, 20];
            for (var r = 0; r < 8; r++)
                for (var c = 0; c < 20; c++)
                    w[r, c] = System.Math.Round(rng.NextDouble() * 100, 3);

            var assign = AssignmentSolver.MaxWeight(w);
            Assert.Equal(8, assign.Distinct().Count());
            var best = TotalOf(w, assign);

            // ① 割当済み2行の入れ替え。
            for (var a = 0; a < 8; a++)
                for (var b = a + 1; b < 8; b++)
                {
                    var swapped = w[a, assign[b]] + w[b, assign[a]];
                    var current = w[a, assign[a]] + w[b, assign[b]];
                    Assert.True(swapped <= current + 1e-9, $"交換で改善した（最適でない）: trial={trial}");
                }

            // ② 未使用の列への差し替え。
            var used = new HashSet<int>(assign);
            for (var r = 0; r < 8; r++)
                for (var c = 0; c < 20; c++)
                {
                    if (used.Contains(c)) continue;
                    Assert.True(w[r, c] <= w[r, assign[r]] + 1e-9,
                        $"未使用列へ差し替えると改善した（最適でない）: trial={trial}");
                }
            Assert.True(best > 0);
        }
    }

    [Fact]
    public void IsDeterministic_SameInputSameOutput()
    {
        var rng = new Xoshiro256Random(7);
        var w = new double[8, 12];
        for (var r = 0; r < 8; r++)
            for (var c = 0; c < 12; c++)
                w[r, c] = rng.NextInt(1, 5);   // 同点を大量に含む行列＝タイブレークの決定性を見る

        var a = AssignmentSolver.MaxWeight(w);
        for (var i = 0; i < 20; i++) Assert.Equal(a, AssignmentSolver.MaxWeight(w));
    }

    [Fact]
    public void MoreRowsThanColumns_Throws()
    {
        // 守備8枠に対し候補が8人未満＝守備網羅が崩れる状況。黙って -1 を返さず弾く
        // （呼び出し側＝AiTeamBuilder は候補が足りているかを先に保証する責任がある）。
        var w = new double[3, 2];
        Assert.Throws<ArgumentException>(() => AssignmentSolver.MaxWeight(w));
    }
}
