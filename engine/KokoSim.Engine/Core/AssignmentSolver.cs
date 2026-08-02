using System;

namespace KokoSim.Engine.Core;

/// <summary>
/// 最大重み割当（ハンガリアン法・O(rows²×cols)。issue #384）。「行＝埋めたい枠」「列＝候補」の重み行列から、
/// 各行に別々の列を割り当てて<b>重みの総和が最大</b>になる組み合わせを返す。
///
/// AI校の守備配置（8枠×在校生）を毎回解くために置いた。自校（<see cref="Season.RosterTeamBuilder"/>）は
/// 8!＝40320通りの全探索で同じ問題を解いているが、AI校は4000校×逆算配分の二分探索26回で回るため
/// 全探索だと 34.8G 評価になって成立しない。本ソルバは同じ最適解を 8³ 程度で返す
/// （<c>AssignmentSolverTests</c> が総当たりとの一致を固定している）。
///
/// 決定論（不変条件#2）: 乱数を使わない純関数。同点は入力の並び順で決まり、同じ入力なら常に同じ割当を返す。
/// </summary>
public static class AssignmentSolver
{
    /// <summary>
    /// 重み最大の割当を解く。戻り値は行→列の対応（添字＝行、値＝割り当てた列）。
    /// <paramref name="weight"/> は [行, 列]。列は行以上必要（足りなければ例外＝黙って未割当を返さない）。
    /// </summary>
    public static int[] MaxWeight(double[,] weight)
    {
        if (weight is null) throw new ArgumentNullException(nameof(weight));
        var rows = weight.GetLength(0);
        var cols = weight.GetLength(1);
        if (cols < rows)
            throw new ArgumentException(
                $"候補（列={cols}）が枠（行={rows}）より少ないので全枠を埋められません。", nameof(weight));
        if (rows == 0) return Array.Empty<int>();

        // 最小費用の定式で解き、費用＝−重み とすることで最大重みにする。
        // 実装は「ポテンシャル付き最短増加路」（JV法）。1始まりの補助配列を使う教科書型で、
        // p[j] = 列 j に割り当てられた行（0＝未割当）。
        const double Inf = double.PositiveInfinity;
        var u = new double[rows + 1];      // 行のポテンシャル
        var v = new double[cols + 1];      // 列のポテンシャル
        var p = new int[cols + 1];         // 列→行
        var way = new int[cols + 1];       // 増加路の復元用

        for (var i = 1; i <= rows; i++)
        {
            p[0] = i;
            var j0 = 0;
            var minv = new double[cols + 1];
            var used = new bool[cols + 1];
            for (var j = 0; j <= cols; j++) { minv[j] = Inf; used[j] = false; }

            do
            {
                used[j0] = true;
                var i0 = p[j0];
                var delta = Inf;
                var j1 = -1;
                for (var j = 1; j <= cols; j++)
                {
                    if (used[j]) continue;
                    var cur = -weight[i0 - 1, j - 1] - u[i0] - v[j];
                    // 厳密な「より小さい」だけで更新する＝同点は先に見た列を保つ（決定論のタイブレーク）。
                    if (cur < minv[j]) { minv[j] = cur; way[j] = j0; }
                    if (minv[j] < delta) { delta = minv[j]; j1 = j; }
                }
                // 候補が尽きることは cols >= rows の下では起きない（保険）。
                if (j1 < 0) throw new InvalidOperationException("割当の増加路が見つかりませんでした。");

                for (var j = 0; j <= cols; j++)
                {
                    if (used[j]) { u[p[j]] += delta; v[j] -= delta; }
                    else minv[j] -= delta;
                }
                j0 = j1;
            }
            while (p[j0] != 0);

            do
            {
                var j1 = way[j0];
                p[j0] = p[j1];
                j0 = j1;
            }
            while (j0 != 0);
        }

        var result = new int[rows];
        for (var i = 0; i < rows; i++) result[i] = -1;
        for (var j = 1; j <= cols; j++)
            if (p[j] != 0) result[p[j] - 1] = j - 1;
        return result;
    }
}
