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
    public static FieldGeometry For(int? roundsRemaining, IRandomSource? rng)
    {
        if (roundsRemaining is >= 1 and <= 2) return Stadium.PrefLarge.ToFieldGeometry();
        var municipal = rng is null ? Stadium.MunicipalSmall01 : Pick(rng);
        return municipal.ToFieldGeometry();
    }

    private static Stadium Pick(IRandomSource rng)
        => rng.NextInt(0, 2) == 0 ? Stadium.MunicipalSmall01 : Stadium.MunicipalSmall02;
}
