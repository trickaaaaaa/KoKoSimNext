using System.Collections.Generic;
using KokoSim.Engine.Core;

namespace KokoSim.Engine.Career;

/// <summary>
/// 1シーズンぶんの監督メタ更新（設計書04 §1.1 自然増＋§1.1b 節目イベント）を1つのシームに束ねる。
/// <see cref="CareerEngine.Run"/> の年内処理（<see cref="CareerEngine.ApplyResults"/>＋通算年数/甲子園回数の加算＋
/// <see cref="ManagerGrowthEvents.Yearly"/>）と<b>同じ順序・同じ効果</b>で1年ぶんだけ適用する純関数。
/// これにより「バランスCLI/テストの年次ループ」と「Unity実プレイの年度替わり」が同じ成長曲線を共有する
/// （成長ロジックの二重実装を避ける・不変条件#3 エンジン純度＝Unity非依存）。
/// </summary>
public static class ManagerSeasonUpdate
{
    /// <summary>
    /// 監督にこのシーズンの結果を適用する。<paramref name="milestoneRng"/> は本編の乱数列を乱さないよう
    /// 必ず Fork した独立ストリームを渡すこと（不変条件#2 決定論・設計書04 §1.1b）。戻り値は発火した節目イベント。
    /// </summary>
    /// <param name="reachedKoshien">このシーズンに甲子園（夏＝県予選優勝）へ到達したか。</param>
    /// <param name="nationalChampion">このシーズンに全国優勝したか（甲子園本戦が実プレイ接続するまでは false）。</param>
    /// <param name="wins">このシーズンの公式戦勝数。</param>
    /// <param name="upsetFameDelta">番狂わせ連動の名声デルタ（issue #170）。未算出時は 0（順当扱い＝二重計上なし）。</param>
    public static IReadOnlyList<ManagerGrowthNotice> Apply(
        Manager m, bool reachedKoshien, bool nationalChampion, int wins,
        IRandomSource milestoneRng,
        CareerCoefficients? careerCoeff = null,
        ManagerGrowthCoefficients? growthCoeff = null,
        double upsetFameDelta = 0)
    {
        var c = careerCoeff ?? new CareerCoefficients();
        var mg = growthCoeff ?? new ManagerGrowthCoefficients();

        // 自然増（指導力・采配・育成眼・名声・信頼度・資金）。
        CareerEngine.ApplyResults(m, reachedKoshien, nationalChampion, wins, upsetFameDelta, c);
        m.CareerYears++;
        if (reachedKoshien) m.KoshienAppearances++;

        // 節目イベント（甲子園経験・大敗の学び・講習会・OB訪問等）。独立ストリームで既存の乱数列を乱さない。
        return ManagerGrowthEvents.Yearly(m, reachedKoshien, wins, milestoneRng, mg);
    }
}
