namespace KokoSim.Engine.Season;

/// <summary>自校の夏の終わり方（3年生の引退の場面を分ける。issue #954）。</summary>
public enum SummerExit
{
    /// <summary>夏の県予選（地方大会）で敗退した。</summary>
    PrefectureLoss,
    /// <summary>夏の甲子園で敗退した。</summary>
    KoshienLoss,
    /// <summary>夏の甲子園で優勝した（負けずに引退する）。</summary>
    KoshienChampion,
}

/// <summary>
/// 夏の敗退直後の3年引退（issue #954・設計書03 §2「8月: 敗退チームは3年生引退→新チーム移行」）。
/// 自校の夏が終わった大会と結果から、引退の場面（<see cref="SummerExit"/>）と会話のイベントIDを決める。
///
/// <para>引退そのもの（フラグ・背番号の返上・暫定主将）は <see cref="RosterLifecycle.BeginNewTeam"/> が行う。
/// ここは「いつ・どの場面で」を決めるだけの純関数（乱数不使用・不変条件#2）。統計シム（<see cref="SeasonEngine"/>）
/// の暦（第18週の固定フック）は変えない＝帯・決定論の指紋は不変。</para>
/// </summary>
public static class SummerRetirement
{
    /// <summary>県予選で敗退した年の引退の会話（data/events.yaml）。</summary>
    public const string PrefectureLossEventId = "summer_retirement_prefecture";
    /// <summary>甲子園で敗退した年の引退の会話。</summary>
    public const string KoshienLossEventId = "summer_retirement_koshien";
    /// <summary>甲子園で優勝した年の引退の会話（敗戦の台詞を出さない）。</summary>
    public const string KoshienChampionEventId = "summer_retirement_champion";

    /// <summary>
    /// 終わった大会と結果から、自校の夏がここで終わったかを判定する。県予選で優勝した年は甲子園へ進むので
    /// null（甲子園の後で判定する）。夏以外の大会も null。
    /// </summary>
    public static SummerExit? Classify(TournamentKind ended, bool isChampion) => ended switch
    {
        TournamentKind.Summer => isChampion ? null : SummerExit.PrefectureLoss,
        TournamentKind.SummerKoshien => isChampion ? SummerExit.KoshienChampion : SummerExit.KoshienLoss,
        _ => null,
    };

    /// <summary>引退の場面に対応する会話のイベントID。</summary>
    public static string EventId(SummerExit exit) => exit switch
    {
        SummerExit.PrefectureLoss => PrefectureLossEventId,
        SummerExit.KoshienLoss => KoshienLossEventId,
        _ => KoshienChampionEventId,
    };

    /// <summary>
    /// 新チーム発足（<see cref="RosterLifecycle.BeginNewTeam"/>）の結果を見て、出す引退の会話のイベントIDを返す。
    /// この夏に引退した3年生が0人（新規開始の1年目＝全員1年生など）なら、返しに来る背番号も見送る先輩も居ないので
    /// null＝会話を出さない（issue #1531）。新チームの発足・主将指名はそのまま続ける。
    /// </summary>
    public static string? EventIdFor(SummerExit exit, NewTeamTransition transition)
        => transition.Retired.Count > 0 ? EventId(exit) : null;

    /// <summary>引退の会話のイベントか（主将指名より先に読ませる順序の判定に使う）。</summary>
    public static bool IsRetirementEvent(string? eventId)
        => eventId == PrefectureLossEventId || eventId == KoshienLossEventId || eventId == KoshienChampionEventId;
}
