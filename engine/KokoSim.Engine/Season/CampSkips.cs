namespace KokoSim.Engine.Season;

/// <summary>
/// 自校が今年度に見送った合宿（issue #1482）。夏冬の合宿週の頭にマネージャーが「合宿をしますか？」と聞き、
/// 「しない」を選ぶと倍率・結束・費用の三つがすべてなくなる。<b>default＝どちらも行う</b>＝従来どおり
/// （AI校・ヘッドレスシム・キャリアシムは常にこれ。見送れるのは自校のプレイヤーだけ）。
/// </summary>
public readonly record struct CampSkips(bool Summer, bool Winter)
{
    /// <summary>どちらも行う（従来）。</summary>
    public static CampSkips None => default;

    /// <summary>夏（false）／冬（true）の合宿を見送ったか。</summary>
    public bool IsSkipped(bool winter) => winter ? Winter : Summer;

    /// <summary>夏／冬の合宿を見送るかどうかを書き換えたものを返す。</summary>
    public CampSkips With(bool winter, bool skipped)
        => winter ? this with { Winter = skipped } : this with { Summer = skipped };
}
