using KokoSim.Engine.Core;

namespace KokoSim.Engine.Match.Field;

/// <summary>
/// 地方大会（夏・秋の県大会）の球場ティア判定（設計書13 §1・「地方大会の球場割当」issue）。
/// 47県固有の実在球場は追わず、汎用ティア方式で寸法差を実際の判定へ反映する:
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
    public static FieldGeometry For(int? roundsRemaining, IRandomSource? rng, int? prefectureId = null)
        => ForStadium(roundsRemaining, rng, prefectureId).ToFieldGeometry();

    /// <summary>
    /// <see cref="For"/> と同じ判定で、寸法だけでなく球場そのもの（名称を含む）を返す
    /// （issue #803・電光掲示板の球場名表示。rng の引き方は一字一句同じ＝乱数消費は変わらない）。
    /// </summary>
    public static Stadium ForStadium(int? roundsRemaining, IRandomSource? rng, int? prefectureId = null)
    {
        // 準決勝・決勝は乱数を引かない（県ごとの球場に差し替えても乱数列は1ビットも変わらない）
        if (roundsRemaining is >= 1 and <= 2) return FinalStadiumFor(prefectureId);
        return rng is null ? Stadium.MunicipalSmall01 : Pick(rng);
    }

    /// <summary>
    /// その県の準決勝・決勝の球場（issue #904）。東京（東東京・西東京の夏、秋の都大会）は明治神宮野球場、
    /// それ以外は汎用の県営級。47県の決勝球場（data/stadiums.yaml の <c>prefecture_finals</c>）へ広げるときはここに足す。
    /// </summary>
    public static Stadium FinalStadiumFor(int? prefectureId)
        => prefectureId == Nation.SummerRegions.TokyoId ? Stadium.Jingu : Stadium.PrefLarge;

    private static Stadium Pick(IRandomSource rng)
        => rng.NextInt(0, 2) == 0 ? Stadium.MunicipalSmall01 : Stadium.MunicipalSmall02;
}
