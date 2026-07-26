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
/// 申込の可否＋受諾判定だけの結果（<see cref="PracticeMatchScheduler.TryAccept"/>, issue #269）。
/// <paramref name="Accepted"/> が真なら「この申込は成立する」＝スタメン設定へ進んでよい。副作用は無い。
/// </summary>
public sealed record PracticeMatchAcceptance(
    bool Accepted, PracticeMatchRejection Rejection, double AcceptChance);

/// <summary>
/// ライブ観戦で始めた練習試合1件（issue #269）。<paramref name="Live"/> は成立時のみ非 null。
/// </summary>
public sealed record PracticeMatchLiveOutcome(
    bool Started, PracticeMatchRejection Rejection, double AcceptChance, PlayerMatchLive? Live);

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

        // 練習試合は大会ラウンドの概念がない＝コールドゲームは常にOFF（設計書05 §1.3, Q18は大会限定）。
        var detail = resolver.Resolve(managerSchool, opponent, rng.Fork(0x9159_2AC5UL), mercyRuleEnabled: false);
        manager.Funds -= _c.Cost;
        LastPlayedWeek = week;
        return new PracticeMatchOutcome(true, PracticeMatchRejection.None, chance, detail);
    }

    /// <summary>
    /// 申込の可否＋受諾判定だけを行う（issue #269）。<b>副作用なし</b>＝費用も週枠も動かさず試合も作らない。
    /// 乱数の消費は <see cref="Request"/>／<see cref="BeginRequest"/> と同じ「受諾判定で1回」なので、
    /// 同じ種から作った <paramref name="rng"/> を渡せば必ず同じ受諾結果になる。
    /// UI はこれで「申し込む」を判定し、成立したときだけスタメン設定画面へ進む。
    /// </summary>
    public PracticeMatchAcceptance TryAccept(
        Manager manager, School managerSchool, School opponent, int week, IRandomSource rng)
    {
        var blocked = CanRequest(manager, week);
        var chance = AcceptChance(manager, managerSchool, opponent);
        if (blocked != PracticeMatchRejection.None)
            return new PracticeMatchAcceptance(false, blocked, chance);

        return rng.NextDouble() >= chance
            ? new PracticeMatchAcceptance(false, PracticeMatchRejection.Declined, chance)
            : new PracticeMatchAcceptance(true, PracticeMatchRejection.None, chance);
    }

    /// <summary>
    /// 練習試合をライブ観戦で始める（issue #269）。<see cref="Request"/> の一括消化版と<b>乱数の消費順が同一</b>
    /// （受諾判定で1回 → 試合は <c>rng.Fork(0x9159_2AC5UL)</c>）なので、返した進行体を最後まで進めた
    /// ボックススコアは同じシードの <see cref="Request"/> の結果と一致する＝観戦しても一括消化しても同じ試合になる。
    /// 費用と今週の枠は<b>ここで確定</b>する（申込成立＝費用発生。途中で閉じても消費済み扱い）。
    /// 断られた場合・週枠消化済み・資金不足では何も消費しない。
    /// 練習試合は大会ラウンドの概念がない＝コールドゲームは常にOFF（設計書05 §1.3）。
    /// <para>
    /// スタメン設定を挟むフローでは <see cref="TryAccept"/> で成立を確かめてから、
    /// <b>同じ種で作り直した</b> <paramref name="rng"/> を渡して呼ぶこと（受諾判定を同じ順で再消費して Fork を揃える）。
    /// </para>
    /// </summary>
    public PracticeMatchLiveOutcome BeginRequest(
        Manager manager, School managerSchool, School opponent, int week,
        IPlayerMatchResolver resolver, IRandomSource rng)
    {
        var blocked = CanRequest(manager, week);
        var chance = AcceptChance(manager, managerSchool, opponent);
        if (blocked != PracticeMatchRejection.None)
            return new PracticeMatchLiveOutcome(false, blocked, chance, null);

        if (rng.NextDouble() >= chance)
            return new PracticeMatchLiveOutcome(false, PracticeMatchRejection.Declined, chance, null);

        var live = resolver.BeginLive(managerSchool, opponent, rng.Fork(0x9159_2AC5UL), mercyRuleEnabled: false);
        manager.Funds -= _c.Cost;
        LastPlayedWeek = week;
        return new PracticeMatchLiveOutcome(true, PracticeMatchRejection.None, chance, live);
    }
}
