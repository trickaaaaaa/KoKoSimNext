using KokoSim.Engine.Core;

namespace KokoSim.Engine.Match.Field;

/// <summary>
/// 地方大会（夏・秋の県大会）の球場ティア判定（設計書13 §1・「地方大会の球場割当」issue）。
/// 47県固有の実在球場は追わず（決勝会場の例外は <see cref="FinalStadiumFor"/>）、汎用ティア方式で寸法差を実際の判定へ反映する:
/// 序盤ラウンド＝市営級（<see cref="Stadium.MunicipalSmall01"/>/<see cref="Stadium.MunicipalSmall02"/>
/// から決定論抽選）、準決勝・決勝＝県営級（<see cref="Stadium.PrefLarge"/>）。純関数・決定論。
/// </summary>
public static class RegionalStadiumTier
{
    /// <summary>
    /// ラウンドから使用球場を返す。<paramref name="roundsRemaining"/> は決勝までの残りラウンド数
    /// （決勝=1・準決勝=2・…浅いほど大。<see cref="Nation.Tournaments.TournamentRecorder.RoundsRemaining"/> と同じ数え方）。
    /// null（大会外・ラウンド不明）は序盤扱い＝市営級。
    /// </summary>
    /// <param name="rng">
    /// 序盤2種のどちらを使うかの決定論抽選に使う専用Forkストリーム。null なら固定で
    /// <see cref="Stadium.MunicipalSmall01"/>（展望など、試合ごとの乱数を持たない呼び出し向け）。
    /// </param>
    /// <param name="prefectureId">
    /// 大会を戦う県（<see cref="FinalStadiumFor"/>）。null なら県に依らない汎用（県営級）。
    /// </param>
    /// <param name="summer">
    /// 夏の県大会か（issue #956・#959）。神奈川の横浜スタジアム・北海道の北海道ボールパーク・宮城の宮城球場・広島の広島市民球場は夏の準決勝・決勝だけ。
    /// 秋は <see cref="Stadium.AutumnFinals"/> の会場（issue #1020）。東京の神宮は夏秋とも。乱数の引き方には関わらない。
    /// </param>
    public static FieldGeometry For(int? roundsRemaining, IRandomSource? rng, int? prefectureId = null, bool summer = false)
        => ForStadium(roundsRemaining, rng, prefectureId, summer).ToFieldGeometry();

    /// <summary>
    /// <see cref="For"/> と同じ判定で、寸法だけでなく球場そのもの（名称を含む）を返す
    /// （issue #803・電光掲示板の球場名表示。rng の引き方は一字一句同じ＝乱数消費は変わらない）。
    /// </summary>
    public static Stadium ForStadium(int? roundsRemaining, IRandomSource? rng, int? prefectureId = null, bool summer = false)
    {
        // 準決勝・決勝は乱数を引かない（県ごとの球場に差し替えても乱数列は1ビットも変わらない）
        if (roundsRemaining is >= 1 and <= 2) return FinalStadiumFor(prefectureId, summer);
        return rng is null ? Stadium.MunicipalSmall01 : Pick(rng);
    }

    /// <summary>
    /// その県の準決勝・決勝の球場（issue #904）。東京（東東京・西東京の夏、秋の都大会）は明治神宮野球場。
    /// 夏は神奈川＝横浜スタジアム（issue #956）、北海道（北北海道・南北海道とも）＝北海道ボールパーク（issue #959）、
    /// 宮城＝宮城球場（issue #961）、広島＝広島市民球場（issue #963）。
    /// 秋は夏と会場が違う県（<see cref="Stadium.AutumnFinals"/>・issue #1020）が実在の会場（保土ケ谷・札幌ドーム・
    /// 仙台市民・みよし など17か所）。それ以外は夏・秋とも汎用の県営級（47県すべての配線はしない＝設計書13 §1）。
    /// 秋季地区予選は県を渡さない（呼び出し側。決定27）＝ここへは来ない。
    /// </summary>
    public static Stadium FinalStadiumFor(int? prefectureId, bool summer = false)
    {
        if (prefectureId == Nation.SummerRegions.TokyoId) return Stadium.Jingu;
        if (!summer)
            return prefectureId is { } id && Stadium.AutumnFinals.TryGetValue(id, out var autumn) ? autumn : Stadium.PrefLarge;
        return prefectureId switch
        {
            Nation.SummerRegions.KanagawaId => Stadium.Yokohama,
            Nation.SummerRegions.HokkaidoId => Stadium.HokkaidoBallpark,
            Nation.SummerRegions.MiyagiId => Stadium.Miyagi,
            Nation.SummerRegions.HiroshimaId => Stadium.Hiroshima,
            _ => Stadium.PrefLarge,
        };
    }

    private static Stadium Pick(IRandomSource rng)
        => rng.NextInt(0, 2) == 0 ? Stadium.MunicipalSmall01 : Stadium.MunicipalSmall02;
}
