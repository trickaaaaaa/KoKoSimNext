using KokoSim.Engine.Core;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 自校の先攻/後攻を対戦の組み合わせから決定論的に決める（design-05, OPEN-QUESTIONS 未決I, issue #70）。
/// 校ID同士のペア＋年度＋週から合成した専用シードで決める＝大会本流の乱数（TournamentRunner._rng）には
/// 依存しない。これにより①対戦カード画面（試合開始前）でも実試合と同じ結果を先読みでき、②観戦の有無や
/// 同ラウンドの他カードの解決順で結果が変わらない（不変条件#2）。
/// </summary>
public static class HomeAwayAssignment
{
    private const ulong Salt = 0x0D7A_0000UL;

    /// <summary>true＝自校が先攻(away)。同じ組み合わせ（校ID対＋年度＋週）なら常に同じ結果を返す。</summary>
    public static bool ManagerIsAway(int managerSchoolId, int opponentSchoolId, int yearIndex, int week)
    {
        var mixed = Salt
            ^ (ulong)(long)managerSchoolId
            ^ ((ulong)(long)opponentSchoolId * 0x9E37_79B97F4A7C15UL)
            ^ ((ulong)(long)yearIndex * 0xBF58_476D1CE4E5B9UL)
            ^ ((ulong)(long)week * 0x94D0_49BB133111EBUL);
        return new Xoshiro256Random(mixed).NextDouble() < 0.5;
    }
}
