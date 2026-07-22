using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Players;
using KokoSim.Engine.Stats;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>1県の大会結果（全国ダイジェスト・新聞用）。</summary>
public sealed record PrefectureResult(int PrefectureId, string PrefectureName, string? ChampionName);

/// <summary>
/// 全国の裏試合を一括フルシムするオーケストレータ（設計書05 §1.4 / #43・全国47県）。各県の地方大会を
/// 永続ロスターの GameEngine.Play で最後まで解決し、全4000校のボックススコアを全国通算成績へ積む。
/// 自校が対話的に消化する県は <paramref name="excludePrefectureId"/> で除外する（Shell が別 TournamentRunner
/// で回して同一 <see cref="NationTournamentStats"/> へ積む＝重複しない）。県ごとに Fork した独立乱数で決定論。
/// エンジンは Unity 非依存なので、呼び出し側（Shell）はこれをバックグラウンドスレッドで回してよい。
/// </summary>
public static class NationalTournamentEngine
{
    public static IReadOnlyList<PrefectureResult> RunSummer(
        Nation nation, NationRosters rosters, GameContext ctx, NationCoefficients coeff,
        TournamentSchedule schedule, int yearIndex, NationTournamentStats stats, IRandomSource rng,
        int? excludePrefectureId = null, ModernRules? modernRules = null, int? calendarYear = null,
        IEnemyBrainFactory? brains = null)
    {
        var results = new List<PrefectureResult>(nation.Prefectures.Count);
        foreach (var pref in nation.Prefectures)
        {
            if (excludePrefectureId is int ex && pref.Id == ex) continue;

            var field = nation.InPrefecture(pref.Id).ToList();
            if (field.Count < 2)
            {
                results.Add(new PrefectureResult(pref.Id, pref.Name, field.FirstOrDefault()?.Name));
                continue;
            }

            var bg = new BackgroundMatchResolver(rosters, ctx, yearIndex, stats, modernRules, calendarYear, brains);
            var prefRng = rng.Fork(0xF00D_0000UL ^ (uint)pref.Id);
            // 自校非関与の県なので nominal manager（field[0]）に playerResolver は付けない＝全カード裏試合フルシム。
            var runner = new TournamentRunner(
                field, field[0], coeff, prefRng, schedule, $"{pref.Name}大会",
                playerResolver: null, backgroundResolver: bg);

            while (!runner.Finished) runner.PlayNextPlayerMatch();
            results.Add(new PrefectureResult(pref.Id, pref.Name, runner.ChampionName));
        }
        return results;
    }
}
