using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Stats;

/// <summary>
/// 自校選手の成績ストア（4スコープ）。通算（Career, 練習試合を含む全試合・永続）、
/// 公式戦通算（Official, 大会の試合だけ・永続）、今大会（CurrentTournament, 大会ごとにリセット）、
/// 県予選（PrefQualifier, 夏の県予選の終了時に今大会から焼き取る）を
/// 並行して積む。純データ・決定論・UnityEngine 非依存。
/// インスタンスは横断状態として Shell（GameSession）が保持する。
///
/// <para><b>TODO（転任・設計書04 §1.2 が入ったとき）</b>: このストアは「監督の手持ち」であって
/// <b>学校に紐づいていない</b>。転任すると前任校の選手成績がそのまま残り、記録タブ（issue #600・
/// 主語＝学校）の校歴代記録へ他校の成績が混ざる。帰属の分離は転任 issue で行う（記録タブ側では直せない
/// ＝どの成績がどの学校のものかを、このストアが区別していないため）。</para>
/// </summary>
public sealed class PlayerStatStore
{
    /// <summary>通算成績（永続。練習試合も含む全試合。夏/秋・年を跨いでも消さない）。</summary>
    public StatBook Career { get; } = new();

    /// <summary>公式戦通算成績（永続。練習試合は含まない）。</summary>
    public StatBook Official { get; } = new();

    /// <summary>今大会成績（大会開始時にクリア）。</summary>
    public StatBook CurrentTournament { get; } = new();

    /// <summary>
    /// 直前の夏の県予選成績（設計書05 §1.4b）。甲子園の大会展望が「選手一人一人の県予選成績」を
    /// 出すための焼き取り。<see cref="SnapshotPrefQualifier"/> が県予選の終了時に一度だけ写す。
    ///
    /// <para>今大会スコープ（<see cref="CurrentTournament"/>）は甲子園の開始で消えるので、
    /// 消える前にここへ移しておく必要がある。相手校側の対は
    /// <see cref="NationTournamentStats.PrefQualifierForSchool"/>。</para>
    /// </summary>
    public StatBook PrefQualifier { get; } = new();

    /// <summary>
    /// 直前の地区大会成績（設計書05 §1.5b）。センバツの大会展望が「選手一人一人の地区大会成績」を
    /// 出すための焼き取り。<see cref="SnapshotRegionalQualifier"/> が自校の地区大会終了時に一度だけ写す
    /// （自校が出場枠外で地区大会に出ていない年は空のまま＝センバツにも選ばれない年なので実害なし）。
    /// </summary>
    public StatBook RegionalQualifier { get; } = new();

    /// <summary>大会別アーカイブ（学年×大会枠＋当時の背番号, issue #77）。永続・セーブ横断。</summary>
    public TournamentArchive Archive { get; } = new();

    /// <summary>
    /// どのスコープ（アーカイブ含む）にでも現れる選手IDの最大値（無ければ 0）。セーブ復元時の採番の
    /// 再開値に使う（issue #644・<see cref="Season.PlayerIdCounter.Resume"/>）。
    /// </summary>
    public int MaxPlayerId()
    {
        var max = 0;
        foreach (var book in new[] { Career, Official, CurrentTournament, PrefQualifier, RegionalQualifier })
            foreach (var id in book.Players.Keys) if (id > max) max = id;
        foreach (var key in Archive.Keys)
            if (Archive.Slot(key) is { } slot)
                foreach (var id in slot.Keys) if (id > max) max = id;
        return max;
    }

    /// <summary>大会開始時に今大会スコープをリセット（通算・公式戦通算・県予選・アーカイブは保持）。</summary>
    public void StartTournament() => CurrentTournament.Clear();

    /// <summary>
    /// 今大会スコープを県予選スコープへ焼き取る（夏の県予選の終了時に一度だけ呼ぶ）。
    /// 上書き＝去年の県予選成績は残さない（展望が見せるのは「今年の夏の県予選」だけ）。
    /// </summary>
    public void SnapshotPrefQualifier() => PrefQualifier.ReplaceWith(CurrentTournament);

    /// <summary>
    /// 今大会スコープを地区大会スコープへ焼き取る（自校の地区大会終了時に一度だけ呼ぶ）。
    /// 上書き＝去年の地区大会成績は残さない（展望が見せるのは「今年の地区大会」だけ）。
    /// </summary>
    public void SnapshotRegionalQualifier() => RegionalQualifier.ReplaceWith(CurrentTournament);

    /// <summary>
    /// 大会終了時に今大会成績を大会別アーカイブへ確定する（issue #77）。次の StartTournament が
    /// CurrentTournament をクリアする前に呼ぶ。playerInfo は sourceId→(当時の学年, 当時の背番号)。
    /// year はその大会の年度（YearIndex。記録タブのシーズン記録が年を出すのに使う, issue #600）。
    /// </summary>
    public void ArchiveCurrentTournament(TournamentSlot slot,
        IReadOnlyDictionary<int, (int Grade, int UniformNumber)> playerInfo, int year = 0)
        => Archive.Archive(slot, CurrentTournament, playerInfo, year);

    /// <summary>
    /// 1試合ぶんの詳細結果を各スコープへ畳み込む。
    /// <paramref name="isOfficial"/>=false（練習試合）のときは通算スコープにだけ積み、
    /// 公式戦通算・今大会には積まない（設計: 練習試合は公式記録に残さない）。
    /// </summary>
    public void FoldGame(GameResult r, bool managerIsAway, bool isOfficial = true)
    {
        Career.FoldGame(r, managerIsAway);
        if (!isOfficial) return;
        Official.FoldGame(r, managerIsAway);
        CurrentTournament.FoldGame(r, managerIsAway);
    }
}
