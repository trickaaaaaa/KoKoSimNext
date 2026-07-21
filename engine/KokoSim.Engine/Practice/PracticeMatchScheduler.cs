using KokoSim.Engine.Career;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;

namespace KokoSim.Engine.Practice;

/// <summary>申込が通らなかった理由。<see cref="PracticeMatchRejection.None"/> は成立を表す。</summary>
public enum PracticeMatchRejection
{
    None,
    /// <summary>今週はすでに練習試合を消化済み（週末アクション枠は週1）。</summary>
    AlreadyPlayedThisWeek,
    /// <summary>資金不足（費用に足りない）。</summary>
    InsufficientFunds,
    /// <summary>相手校に断られた（名声/ティア差）。</summary>
    Declined,
}

/// <summary>
/// 練習試合1件の結果。<paramref name="Detail"/> は成立時のみ非 null。
/// </summary>
public sealed record PracticeMatchOutcome(
    bool Played, PracticeMatchRejection Rejection, double AcceptChance, PlayerMatchDetail? Detail);

/// <summary>
/// 練習試合の実行経路（設計書03 §週ターン③ 週末アクション）。
/// 週1回制約・資金・受諾判定をエンジン側で表現し（UIガードに頼らない）、成立時だけ
/// <see cref="IPlayerMatchResolver"/> で詳細シムを回す。成績の畳み込みと実戦成長の適用は
/// 呼び出し側（Shell）が <see cref="PracticeMatchOutcome.Detail"/> を使って行う。
/// 乱数はシード付き <see cref="IRandomSource"/> のみ（不変条件#2）。
/// </summary>
public sealed class PracticeMatchScheduler
{
    private readonly PracticeMatchCoefficients _c;

    public PracticeMatchScheduler(PracticeMatchCoefficients coefficients) => _c = coefficients;

    /// <summary>最後に練習試合を消化した週（絶対週番号。未消化は null）。</summary>
    public int? LastPlayedWeek { get; private set; }

    /// <summary>費用[万円]。</summary>
    public double Cost => _c.Cost;

    /// <summary>
    /// 申込可否の事前判定（相手校に依存しない条件だけ）。UI のボタン活性はこれを引く。
    /// <paramref name="week"/> は年を跨いでも単調増加する絶対週番号を渡すこと。
    /// </summary>
    public PracticeMatchRejection CanRequest(Manager manager, int week)
    {
        if (LastPlayedWeek == week) return PracticeMatchRejection.AlreadyPlayedThisWeek;
        if (manager.Funds < _c.Cost) return PracticeMatchRejection.InsufficientFunds;
        return PracticeMatchRejection.None;
    }

    /// <summary>受諾確率（表示用）。</summary>
    public double AcceptChance(Manager manager, School managerSchool, School opponent)
        => PracticeMatchModel.AcceptChance(managerSchool.Tier, opponent.Tier, manager.Fame, _c);

    /// <summary>
    /// 練習試合を申し込む。断られた場合は費用も週枠も消費しない。成立した場合のみ
    /// 資金を <see cref="PracticeMatchCoefficients.Cost"/> だけ減らし、今週の枠を消費する。
    /// </summary>
    public PracticeMatchOutcome Request(
        Manager manager, School managerSchool, School opponent, int week,
        IPlayerMatchResolver resolver, IRandomSource rng)
    {
        var blocked = CanRequest(manager, week);
        var chance = AcceptChance(manager, managerSchool, opponent);
        if (blocked != PracticeMatchRejection.None)
            return new PracticeMatchOutcome(false, blocked, chance, null);

        if (rng.NextDouble() >= chance)
            return new PracticeMatchOutcome(false, PracticeMatchRejection.Declined, chance, null);

        var detail = resolver.Resolve(managerSchool, opponent, rng.Fork(0x9159_2AC5UL));
        manager.Funds -= _c.Cost;
        LastPlayedWeek = week;
        return new PracticeMatchOutcome(true, PracticeMatchRejection.None, chance, detail);
    }
}
