using System.Collections.Generic;

namespace KokoSim.Engine.Season;

/// <summary>個別指導の指名を押したときの結果（issue #707）。</summary>
public enum NominationResult
{
    /// <summary>指名した。</summary>
    Nominated,
    /// <summary>指名中だったので外した。</summary>
    Released,
    /// <summary>枠が埋まっていて指名できなかった（何も変えていない）。</summary>
    Full,
    /// <summary>添字が範囲外（何も変えていない）。</summary>
    Invalid,
}

/// <summary>
/// 個別指導3枠の指名・解除（設計書03 §3.3・issue #126/#707）。
/// 練習計画の部員表の★・上の枠の×・選手モーダルのボタンは、すべてここを通す（上限の判定を画面ごとに持たない）。
/// 書き込み先は共有ロスターの <see cref="DevelopingPlayer.IndividualCoaching"/> だけ。
/// </summary>
public static class IndividualCoachingNomination
{
    /// <summary>指名中の人数。</summary>
    public static int CountNominated(IReadOnlyList<DevelopingPlayer> roster)
    {
        var n = 0;
        foreach (var p in roster) if (p.IndividualCoaching) n++;
        return n;
    }

    /// <summary>指名中なら外し、そうでなければ枠に空きがあるときだけ指名する。</summary>
    public static NominationResult Toggle(IReadOnlyList<DevelopingPlayer> roster, int index, int slots)
    {
        if (index < 0 || index >= roster.Count) return NominationResult.Invalid;
        var p = roster[index];
        if (p.IndividualCoaching)
        {
            p.IndividualCoaching = false;
            return NominationResult.Released;
        }
        if (CountNominated(roster) >= slots) return NominationResult.Full;
        p.IndividualCoaching = true;
        return NominationResult.Nominated;
    }
}
