namespace KokoSim.Engine.Match.Timeline;

/// <summary>
/// 悪送球の逸れ方（issue #812・表示専用）。3D の守備ビューが送球の軌道と一塁手の芝居を選ぶのに使う。
/// 判定・帯には一切使わない＝判定で引いた逸れ幅（<see cref="Fielding.FieldingPlay.WildThrowScatterM"/>）を
/// 言葉に分けるだけ。どの型でも球は一塁手の後ろへ逃げる（悪送球の約9割は失策連鎖で全走者が追加進塁する
/// ＝球が手元に残る絵にすると走者の進塁と食い違う）。
/// </summary>
public enum WildThrowStyle
{
    /// <summary>手前で弾む（ショートバウンドを捕り損ね、グラブに当たって後ろへ転がる）。</summary>
    ShortHop,
    /// <summary>横へ逸れる（一塁手が塁を離れて手を伸ばすが届かず、ファウルグラウンドへ抜ける）。</summary>
    Wide,
    /// <summary>高く抜ける（一塁手がジャンプしても届かず、頭上を越えて遠くへ転がる）。</summary>
    Sail,
}

/// <summary>悪送球の逸れ方の分類（issue #812）。境目はここにしか置かない。</summary>
public static class WildThrowStyles
{
    /// <summary>逸れ幅[m]がこれ未満なら「手前で弾む」（弾いた球は近くに転がる）。</summary>
    public const double ShortHopMaxScatterM = 5.0;
    /// <summary>逸れ幅[m]がこれ以上なら「高く抜ける」（頭上を越えた球は遠くまで転がる）。間は「横へ逸れる」。</summary>
    public const double SailMinScatterM = 9.0;

    /// <summary>逸れ幅[m]から逸れ方を決める（決定論・乱数不使用）。</summary>
    public static WildThrowStyle Classify(double scatterM)
        => scatterM < ShortHopMaxScatterM ? WildThrowStyle.ShortHop
            : scatterM >= SailMinScatterM ? WildThrowStyle.Sail
            : WildThrowStyle.Wide;
}
