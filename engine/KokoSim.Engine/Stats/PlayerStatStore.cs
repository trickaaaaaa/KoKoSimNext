using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Stats;

/// <summary>
/// 自校選手の成績ストア（4スコープ）。通算（Career, 練習試合を含む全試合・<b>今の赴任先に居る間は永続</b>）、
/// 公式戦通算（Official, 大会の試合だけ・同様）、今大会（CurrentTournament, 大会ごとにリセット）、
/// 県予選（PrefQualifier, 夏の県予選の終了時に今大会から焼き取る）を
/// 並行して積む。純データ・決定論・UnityEngine 非依存。
/// インスタンスは横断状態として Shell（GameSession）が保持する。
///
/// <para><b>転任での帰属分離（issue #1112）</b>: このストアは「監督の今の赴任先」の選手成績であって
/// 学校IDそのものは持たない。転任のたびに <see cref="CloseForTransfer"/> が今の Career/Archive を
/// <see cref="ClosedSchools"/> へ複写してから現本体を空にするので、新しい赴任先の記録タブ
/// （issue #600・主語＝学校）には新しい赴任先の成績だけが積まれる。前任校の成績は捨てずに
/// <see cref="ClosedSchools"/> に残る。</para>
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

    private readonly List<ClosedSchoolStats> _closedSchools = new();

    /// <summary>
    /// 転任で退任した赴任先ぶんの個人成績（issue #1112）。<see cref="CloseForTransfer"/> が退任のたびに
    /// 1件積む。発生順（古い順）。
    /// </summary>
    public IReadOnlyList<ClosedSchoolStats> ClosedSchools => _closedSchools;

    /// <summary>
    /// どのスコープ（アーカイブ・退任済みの赴任ぶん含む）にでも現れる選手IDの最大値（無ければ 0）。
    /// セーブ復元時の採番の再開値に使う（issue #644・<see cref="Season.PlayerIdCounter.Resume"/>）。
    /// </summary>
    public int MaxPlayerId()
    {
        var max = 0;
        foreach (var book in new[] { Career, Official, CurrentTournament, PrefQualifier, RegionalQualifier })
            foreach (var id in book.Players.Keys) if (id > max) max = id;
        foreach (var key in Archive.Keys)
            if (Archive.Slot(key) is { } slot)
                foreach (var id in slot.Keys) if (id > max) max = id;
        foreach (var closed in _closedSchools)
        {
            foreach (var id in closed.Career.Players.Keys) if (id > max) max = id;
            foreach (var key in closed.Archive.Keys)
                if (closed.Archive.Slot(key) is { } slot)
                    foreach (var id in slot.Keys) if (id > max) max = id;
        }
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

    /// <summary>
    /// 転任の確定時に呼ぶ（issue #1112）。今の赴任先ぶんの Career/Archive を <see cref="ClosedSchools"/> へ
    /// 複写してから、全スコープを空にする（新しい赴任先の記録タブに前任校の選手が混ざらないようにする）。
    /// Official/CurrentTournament/PrefQualifier/RegionalQualifier は焼き取り元のスクラッチ置き場でしかなく
    /// 記録タブは読まないので、複写はせずクリアだけする。
    /// </summary>
    public void CloseForTransfer(string schoolName, int startYearIndex, int endYearIndex)
    {
        var career = new StatBook();
        career.ReplaceWith(Career);
        var archive = new TournamentArchive();
        archive.ReplaceWith(Archive);
        _closedSchools.Add(new ClosedSchoolStats
        {
            SchoolName = schoolName,
            StartYearIndex = startYearIndex,
            EndYearIndex = endYearIndex,
            Career = career,
            Archive = archive,
        });

        Career.Clear();
        Official.Clear();
        CurrentTournament.Clear();
        PrefQualifier.Clear();
        RegionalQualifier.Clear();
        Archive.Clear();
    }

    /// <summary>セーブ復元専用（issue #507・<see cref="Saves.StatsSaves"/> だけが使う）。全消去して据え直す。</summary>
    internal void RestoreClosedSchools(IEnumerable<ClosedSchoolStats> closed)
    {
        _closedSchools.Clear();
        _closedSchools.AddRange(closed);
    }
}

/// <summary>
/// 転任で退任した赴任先1回ぶんの個人成績（issue #1112）。<see cref="PlayerStatStore.CloseForTransfer"/> が積む。
/// 在任年数・校名は <see cref="Career.ManagerPosting"/>（issue #980）と同じ情報を重複して持つ（あちらは
/// 集計値の差分、こちらは選手単位の複写で、どちらも単独で読めるようにするため）。
/// </summary>
public sealed class ClosedSchoolStats
{
    public string SchoolName { get; init; } = "";
    public int StartYearIndex { get; init; }
    public int EndYearIndex { get; init; }
    public StatBook Career { get; init; } = new();
    public TournamentArchive Archive { get; init; } = new();
}
