using KokoSim.Engine.Core;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 地区の中を少数校のブロックへ分ける組み合わせ（神奈川の秋季地区予選＝4校ブロックのリーグ戦・issue #772）。
///
/// <para><b>シード分散</b>（監督決定 2026-11-03）: 夏の県大会の順位が付いている学校を順位順に、残りを抽選順に
/// 並べ、ブロックへ蛇行（1→N→N→1…）で配る。夏の上位校は別々のブロックに散り、ブロックの数を超えた分も
/// 2周目・3周目で偏らない。順位が無い年（初年度・ロード直後で控えが無い等）は全校が抽選になる。</para>
///
/// <para>ブロック数は「校数 ÷ ブロックの定員」の切り上げ＝ブロックの大きさは定員かその1つ下に揃う
/// （27校・定員4なら 4校×6＋3校×1）。抽選は Fork した専用ストリーム＝本流の乱数列を進めない。決定論。</para>
/// </summary>
public static class BlockDraw
{
    /// <summary>ブロック抽選の Fork ストリームID（本流・試合の Fork と衝突しない固定値）。</summary>
    public const ulong DrawStreamId = 0xB10C_D2A3UL;

    /// <param name="seedRank">夏の順位（0＝優勝。小さいほど上位）。null を返す学校は抽選側。関数自体が null＝全校抽選。</param>
    public static IReadOnlyList<IReadOnlyList<School>> Split(
        IReadOnlyList<School> entrants, int teamsPerBlock, System.Func<School, int?>? seedRank, IRandomSource rng)
    {
        if (teamsPerBlock < 2) throw new System.ArgumentOutOfRangeException(nameof(teamsPerBlock));
        var n = entrants.Count;
        var blockCount = System.Math.Max(1, (n + teamsPerBlock - 1) / teamsPerBlock);

        // 並び: 順位付き（順位→Id）＋抽選（決定論シャッフル）。Id の昇順を起点にして入力順に依存させない
        var byId = entrants.OrderBy(s => s.Id).ToList();
        var seeded = seedRank is null
            ? new List<School>()
            : byId.Where(s => seedRank(s) is not null).OrderBy(s => seedRank(s)!.Value).ThenBy(s => s.Id).ToList();
        var rest = byId.Where(s => !seeded.Contains(s)).ToList();
        for (var i = rest.Count - 1; i > 0; i--)
        {
            var j = rng.NextInt(0, i + 1);
            (rest[i], rest[j]) = (rest[j], rest[i]);
        }
        var order = seeded.Concat(rest).ToList();

        var blocks = new List<List<School>>(blockCount);
        for (var b = 0; b < blockCount; b++) blocks.Add(new List<School>(teamsPerBlock));
        for (var i = 0; i < order.Count; i++)
        {
            var cycle = i / blockCount;
            var pos = i % blockCount;
            blocks[cycle % 2 == 0 ? pos : blockCount - 1 - pos].Add(order[i]);
        }
        return blocks;
    }

    /// <summary>
    /// ブロック内の総当たりの組み方（円順列法）。<paramref name="teamCount"/> 校の総当たりを「節」に割り、
    /// 各節の対戦を (i, j)＝ブロック内の添字の組で返す。奇数校は毎節1校が休み（その節の対戦に出ない）。
    /// 4校なら3節×2試合、3校なら3節×1試合。先に書いた方（i）を先攻にする。
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<(int A, int B)>> RoundRobinRounds(int teamCount)
    {
        if (teamCount < 2) return System.Array.Empty<IReadOnlyList<(int, int)>>();
        var m = teamCount % 2 == 0 ? teamCount : teamCount + 1;   // 奇数は休み枠（添字 m-1 = 不在）を足す
        var idx = Enumerable.Range(0, m).ToList();
        var rounds = new List<IReadOnlyList<(int, int)>>(m - 1);
        for (var r = 0; r < m - 1; r++)
        {
            var games = new List<(int, int)>(m / 2);
            for (var k = 0; k < m / 2; k++)
            {
                var a = idx[k];
                var b = idx[m - 1 - k];
                if (a >= teamCount || b >= teamCount) continue;   // 休み
                // 先攻・後攻を節ごとに入れ替えて偏らせない
                games.Add(r % 2 == 0 ? (System.Math.Min(a, b), System.Math.Max(a, b)) : (System.Math.Max(a, b), System.Math.Min(a, b)));
            }
            rounds.Add(games);
            // 先頭を固定して残りを1つ回す
            var last = idx[m - 1];
            idx.RemoveAt(m - 1);
            idx.Insert(1, last);
        }
        return rounds;
    }
}
