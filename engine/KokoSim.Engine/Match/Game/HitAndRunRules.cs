using KokoSim.Engine.Match.Tactics;

namespace KokoSim.Engine.Match.Game;

/// <summary>
/// エンドランの塁状況まわりの約束（設計書09 §1・issue #1234・2026-11-24 オーナー判断）。
///
/// <para><b>判定式はここにしか置かない</b>。<see cref="GameEngine"/> の解決も、采配カードの可否もこの1か所を引く
/// ＝「押せたのに engine が効かせないエンドラン」が構造的に作れない（<see cref="StealTargets"/> と同じ作り）。</para>
/// </summary>
public static class HitAndRunRules
{
    /// <summary>
    /// エンドランを掛けられる塁状況か。一塁に走者が居て、満塁でないこと。
    /// 満塁は押し出しで全員が進む場面＝走者を走らせる意味が無いので選ばせない。
    /// </summary>
    public static bool IsAvailable(bool onFirst, bool onSecond, bool onThird)
        => onFirst && !(onSecond && onThird);

    /// <summary>
    /// 打者が空振りしたとき捕手が投げる塁。二塁が空なら二塁（一塁走者と二塁で勝負）、
    /// 一・二塁なら三塁（先頭の二塁走者と三塁で勝負・一塁走者は送球されず二塁へ進む）。
    /// </summary>
    public static StealTarget CaughtThrowTarget(bool onSecond)
        => onSecond ? StealTarget.Third : StealTarget.Second;
}
