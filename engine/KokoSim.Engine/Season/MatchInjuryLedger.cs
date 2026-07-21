using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Season;

/// <summary>試合後の怪我処理1件ぶんの結果（通知フィード表示用）。</summary>
public sealed record MatchInjuryOutcome(
    int PlayerId,
    string PlayerName,
    MatchInjuryOutcomeKind Kind,
    InjuryType Type,
    InjurySite Site,
    InjurySeverity Severity,
    int WeeksRemaining);

/// <summary>試合後の怪我処理の種別。</summary>
public enum MatchInjuryOutcomeKind
{
    /// <summary>試合中に新しく負傷した。</summary>
    Occurred,
    /// <summary>怪我を押して出場し、悪化した。</summary>
    Worsened,
    /// <summary>怪我を押して出場し、悪化はしなかったが全治が延びた。</summary>
    Delayed,
}

/// <summary>
/// 試合中に起きた怪我（<see cref="GameResult.Injuries"/>）と「押して出場」（設計書03 §3.5）を
/// 試合後に自校ロスターへ反映する（issue #29 B/C）。
/// <para>
/// MatchGrowthModel と同じ合流点・同じ SourceId 突き合わせで使う。試合中の判定は観測のみで
/// 選手状態を書き換えないため、実際の状態遷移はすべてここへ集約される。
/// AI校は <see cref="DevelopingPlayer"/> を持たない（毎年生成される Player のみ）ため反映先が無く、
/// 怪我状態の持ち越し自体が存在しない。
/// </para>
/// </summary>
public static class MatchInjuryLedger
{
    /// <summary>
    /// 1試合ぶんを適用する。<paramref name="managerIsAway"/>=true なら detail の Away 側が自校。
    /// 出場の定義は MatchGrowthModel と同じ（打席1以上 または 対戦打者1人以上）。
    /// 乱数は「押して出場」の悪化判定だけが使う（注入された <paramref name="rng"/>。不変条件#2）。
    /// </summary>
    public static IReadOnlyList<MatchInjuryOutcome> Apply(
        GameResult detail, bool managerIsAway, IReadOnlyList<DevelopingPlayer> roster,
        IRandomSource rng, InjuryCoefficients c, InjuryCatalog? catalog = null)
    {
        catalog ??= InjuryCatalog.Default;
        var outcomes = new List<MatchInjuryOutcome>();

        var byId = new Dictionary<int, DevelopingPlayer>(roster.Count);
        foreach (var dp in roster) byId[dp.Id] = dp;

        // ① 出場者の「押して出場」（設計書03 §3.5）。既に怪我している選手だけが対象。
        //    先に処理する＝この試合で新たに負傷した分に対して二重に悪化判定を掛けない。
        foreach (var id in AppearedIds(detail, managerIsAway))
        {
            if (!byId.TryGetValue(id, out var dp) || dp.Injury == InjurySeverity.None) continue;
            var worsened = InjuryModel.PlayThrough(dp, rng, c, catalog);
            outcomes.Add(new MatchInjuryOutcome(dp.Id, dp.Name,
                worsened ? MatchInjuryOutcomeKind.Worsened : MatchInjuryOutcomeKind.Delayed,
                dp.InjuryType, dp.InjurySite, dp.Injury, dp.InjuryWeeksRemaining));
        }

        // ② 試合中の受傷。自校選手（SourceId あり）のみ。同じ選手に複数回起きたら重い方を残す。
        foreach (var e in detail.Injuries)
        {
            if (e.PlayerSourceId is not { } id || !byId.TryGetValue(id, out var dp)) continue;
            var diagnosis = InjuryModel.ToDiagnosis(e.Draw, c);
            // 既に同等以上の怪我をしているなら上書きしない（軽い新規で重傷が消えるのを防ぐ）。
            if (dp.Injury >= diagnosis.Severity && dp.Injury != InjurySeverity.None) continue;
            InjuryModel.Apply(dp, diagnosis);
            outcomes.Add(new MatchInjuryOutcome(dp.Id, dp.Name, MatchInjuryOutcomeKind.Occurred,
                dp.InjuryType, dp.InjurySite, dp.Injury, dp.InjuryWeeksRemaining));
        }

        return outcomes;
    }

    /// <summary>この試合に出場した自校選手のID（打順・登板の順、重複なし）。</summary>
    private static IEnumerable<int> AppearedIds(GameResult detail, bool managerIsAway)
    {
        var seen = new HashSet<int>();
        var batting = managerIsAway ? detail.AwayBatting : detail.HomeBatting;
        var pitching = managerIsAway ? detail.AwayPitching : detail.HomePitching;

        foreach (var line in batting)
        {
            if (line.SourceId is { } id && line.PlateAppearances > 0 && seen.Add(id)) yield return id;
        }
        foreach (var line in pitching)
        {
            if (line.SourceId is { } id && line.BattersFaced > 0 && seen.Add(id)) yield return id;
        }
    }
}
