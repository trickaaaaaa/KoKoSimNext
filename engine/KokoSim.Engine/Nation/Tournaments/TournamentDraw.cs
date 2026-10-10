using KokoSim.Engine.Core;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 組み合わせ抽選（設計書05 §1.2, issue #631）。トーナメント表の初期配置を<b>ここにしか置かない</b>
/// ＝ <see cref="TournamentRunner"/>（自県の対話進行）・<see cref="Knockout"/>（順位付き集計）・
/// <see cref="TournamentEngine"/>（集計のみ）の3経路で同じ組み方になる。
///
/// <para>従来は「出場校を強さ順に並べて標準シード順へ機械的に置く」だけで乱数を一切引いていなかった。
/// 帰結として ①入力（出場校と強さ）が変わらなければ同じ表＝<b>夏と秋で初戦の相手が同じ</b>になる
/// ②1位は必ず最下位と当たる＝<b>強豪同士が初戦で当たることが原理的にありえない</b>（オーナー報告 2026-08-12）。</para>
///
/// <para>実装は実際の県大会と同じ二段構え:
/// <list type="number">
/// <item>上位K校（<see cref="SeedCountFor"/>）は<b>シード</b>として標準シード位置＝ブラケット対角へ置く</item>
/// <item>残りは<b>抽選</b>で空きスロットへ配る（Fisher-Yates。乱数は呼び出し側が Fork した専用ストリーム）</item>
/// </list>
/// 不戦勝（出場校数が2の冪でないときの空きスロット）の総数と位置は従来と同じ＝
/// 上位シード側から埋まる（実際の県大会でもシードと抽選の当たり校が1回戦免除になる）。</para>
///
/// <para><b>決定論</b>（不変条件#2）: 抽選は必ず <see cref="IRandomSource.Fork"/> した専用ストリームで引く。
/// 本編ストリームを進めると試合の乱数順が動いて統計帯まで動く（Heavy が赤くなったら Fork し損ね）。</para>
/// </summary>
public static class TournamentDraw
{
    /// <summary>抽選ストリームの分岐ID（'DRAW'）。呼び出し側は <c>rng.Fork(DrawStreamId)</c> を渡す。</summary>
    public const ulong DrawStreamId = 0x44_52_41_57UL;

    /// <summary>シード校数の上限（実際の県大会のシードは多くても16校規模）。</summary>
    public const int MaxSeeds = 16;

    /// <summary>
    /// 出場校数からシード校数を決める（設計書05 §1.2「シードはブラケット対角へ分散配置」）。
    /// <b>上位1/8をシード</b>とし、2の冪へ丸めて 2〜<see cref="MaxSeeds"/> に収める
    /// （2の冪なら各ブロックにシードがちょうど1校ずつ入り、対角分散が崩れない）。
    /// 実測: 16校→2 ／ 32校→4 ／ 64校→8 ／ 128校以上→16。
    /// 実際の県大会（神奈川190校で16シード等）と同オーダー。
    /// </summary>
    public static int SeedCountFor(int entrantCount)
    {
        if (entrantCount <= 2) return entrantCount;      // 2校以下は組み方が一意（抽選の余地がない）
        var k = 2;                                       // 最低2＝上位2校が必ず反対の山
                                                         // （3〜4校でも最強が不戦勝を得る＝従来と同じ挙動）
        while (k * 2 <= entrantCount / 8 && k * 2 <= MaxSeeds) k *= 2;
        return System.Math.Min(k, entrantCount);
    }

    /// <summary>
    /// 出場校を初期スロットへ並べる（長さ＝出場校数以上で最小の2の冪。不足分は不戦勝の null）。
    ///
    /// <para><paramref name="drawRng"/> は<b>Fork 済みの抽選専用ストリーム</b>を渡すこと（本編を進めない）。
    /// null を渡すと抽選せず従来どおりの純シード配置になる（回帰確認・移行用）。</para>
    ///
    /// <para>シードの根拠は <c>School.Strength</c>。設計書05 §1.2 は「春季大会実績から算出」と規定するが
    /// <b>春季大会が未実装</b>のため当面は総合力で代用する（春季大会の実装時にここを差し替える）。</para>
    /// </summary>
    public static School?[] Arrange(
        IReadOnlyList<School> entrants, IRandomSource? drawRng, int? seedCount = null)
    {
        if (entrants is null) throw new System.ArgumentNullException(nameof(entrants));
        if (entrants.Count == 0) throw new System.ArgumentException("参加校が空です。", nameof(entrants));

        // 序列＝強さ順（同値は Id で決定化）。抽選をしてもこの序列自体は乱数に依存しない。
        var ranked = entrants.OrderByDescending(s => s.Strength).ThenBy(s => s.Id).ToList();
        var size = TournamentEngine.NextPowerOfTwo(ranked.Count);
        var order = TournamentEngine.SeedOrder(size);   // slot → 1-based シード番号

        var seeds = System.Math.Clamp(seedCount ?? SeedCountFor(ranked.Count), 0, ranked.Count);
        if (drawRng is null) seeds = ranked.Count;      // 抽選なし＝全校が序列どおりの純シード配置

        // シード以外を抽選（Fisher-Yates）。ranked[seeds..] を並べ替えて空きシード位置へ順に流し込む。
        var drawn = new List<School>(ranked.Count - seeds);
        for (var i = seeds; i < ranked.Count; i++) drawn.Add(ranked[i]);
        if (drawRng is not null)
        {
            for (var i = drawn.Count - 1; i > 0; i--)
            {
                var j = drawRng.NextInt(0, i + 1);
                (drawn[i], drawn[j]) = (drawn[j], drawn[i]);
            }
        }

        var bySeedPosition = new School?[ranked.Count];
        for (var i = 0; i < seeds; i++) bySeedPosition[i] = ranked[i];
        for (var i = 0; i < drawn.Count; i++) bySeedPosition[seeds + i] = drawn[i];

        var slots = new School?[size];
        for (var slot = 0; slot < size; slot++)
        {
            var position = order[slot] - 1;             // 0-based シード位置
            slots[slot] = position < bySeedPosition.Length ? bySeedPosition[position] : null; // 不足分は不戦勝
        }
        return slots;
    }
}
