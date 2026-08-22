using System.Collections.Generic;
using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Stats;

/// <summary>
/// 全国の学校別成績（設計書05 §1.4 / #43）。裏試合フルシム化で全4000校の全試合のボックススコアを
/// 学校ごとの <see cref="StatBook"/> へ畳み込み、対戦前に相手校の成績を参照できるようにする。
/// キーは (校ID, 校内選手ID)＝校ごとに StatBook を持ち、その中は選手ID（<see cref="Player.SourceId"/>）。
/// 純データ・決定論・UnityEngine 非依存。Shell（横断状態）が保持する。
///
/// <para>スコープは自校の <see cref="PlayerStatStore"/> と対（issue #634 / 設計書05 §1.4b）。
/// <b>今大会</b>（<see cref="ForSchool"/>）は大会開始時に <see cref="StartTournament"/> で全消去し、
/// <b>通算</b>（<see cref="CareerForSchool"/>）は年・大会をまたいで消さない。相手校の選手にも自校と
/// 同じ意味の「通算」を出すために通算スコープを持つ（自校 <see cref="PlayerStatStore.Career"/> と対）。
/// <b>県予選</b>（<see cref="PrefQualifierForSchool"/>）は甲子園の大会展望が「選手一人一人の県予選成績」を
/// 出すための焼き取りで、今大会が甲子園で消える前に <see cref="SnapshotPrefQualifier"/> が写す。</para>
/// </summary>
public sealed class NationTournamentStats
{
    private readonly Dictionary<int, StatBook> _bySchool = new();
    private readonly Dictionary<int, StatBook> _careerBySchool = new();
    private readonly Dictionary<int, StatBook> _prefQualifierBySchool = new();
    // 全国裏試合はバックグラウンドスレッドで回し得る（設計書05 §1.4）。自校県（メインスレッド）と
    // 他県（背景スレッド）が同時に別校を畳み込むため、辞書構造の競合をロックで防ぐ（校キーは disjoint）。
    private readonly object _gate = new();

    /// <summary>学校IDの今大会成績（未登録なら null）。UIの相手校成績欄・新聞が引く。</summary>
    public StatBook? ForSchool(int schoolId)
    {
        lock (_gate) return _bySchool.TryGetValue(schoolId, out var b) ? b : null;
    }

    /// <summary>学校IDの通算成績（未登録なら null。大会をまたいで積む, issue #634）。</summary>
    public StatBook? CareerForSchool(int schoolId)
    {
        lock (_gate) return _careerBySchool.TryGetValue(schoolId, out var b) ? b : null;
    }

    /// <summary>成績を持つ学校ID一覧（新聞・注目選手の走査用・スナップショット）。</summary>
    public IReadOnlyCollection<int> Schools
    {
        get { lock (_gate) return new List<int>(_bySchool.Keys); }
    }

    /// <summary>通算成績を持つ学校ID一覧（セーブ用・スナップショット, issue #634）。</summary>
    public IReadOnlyCollection<int> CareerSchools
    {
        get { lock (_gate) return new List<int>(_careerBySchool.Keys); }
    }

    /// <summary>
    /// 学校IDの県予選成績（未登録なら null。設計書05 §1.4b）。甲子園の大会展望が引く。
    /// 自県の校は <see cref="SnapshotPrefQualifier"/> が今大会から写し、他県の代表校は
    /// 勝ち上がりの再シム（Shell の <c>KoshienQualifierStatsSim</c>）が
    /// <see cref="FoldPrefQualifier"/> で直接積む。
    /// </summary>
    public StatBook? PrefQualifierForSchool(int schoolId)
    {
        lock (_gate) return _prefQualifierBySchool.TryGetValue(schoolId, out var b) ? b : null;
    }

    /// <summary>県予選成績を持つ学校ID一覧（セーブ用・スナップショット）。</summary>
    public IReadOnlyCollection<int> PrefQualifierSchools
    {
        get { lock (_gate) return new List<int>(_prefQualifierBySchool.Keys); }
    }

    /// <summary>大会切替時に<b>今大会スコープだけ</b>を全消去する（通算・県予選は保持）。</summary>
    public void StartTournament() { lock (_gate) _bySchool.Clear(); }

    /// <summary>
    /// 今大会スコープを県予選スコープへ焼き取る（夏の県予選の終了時に一度だけ呼ぶ）。
    /// <b>全消去してから写す</b>＝去年の県予選成績は残さない（展望が見せるのは「今年の夏の県予選」だけ）。
    /// 他県の代表校ぶんはこの後に再シム（<see cref="FoldPrefQualifier"/>）が足す。
    /// </summary>
    public void SnapshotPrefQualifier()
    {
        lock (_gate)
        {
            _prefQualifierBySchool.Clear();
            foreach (var kv in _bySchool) BookFor(_prefQualifierBySchool, kv.Key).ReplaceWith(kv.Value);
        }
    }

    /// <summary>
    /// 1試合の<b>片側だけ</b>を県予選スコープへ積む（他県代表の勝ち上がり再シム専用）。
    /// 今大会・通算には積まない＝再シムは「県予選の読み物を作るためだけ」の計算で、
    /// 甲子園の成績にも通算にも混ぜない（設計書05 §1.4b）。
    /// </summary>
    public void FoldPrefQualifier(int schoolId, GameResult result, bool schoolIsAway)
    {
        lock (_gate)
        {
            var (winPid, losePid) = DecisionOfRecord.Resolve(result, schoolIsAway);
            BookFor(_prefQualifierBySchool, schoolId).FoldGame(result, schoolIsAway, winPid, losePid);
        }
    }

    private static StatBook BookFor(Dictionary<int, StatBook> books, int schoolId)
    {
        if (!books.TryGetValue(schoolId, out var b)) { b = new StatBook(); books[schoolId] = b; }
        return b;
    }

    /// <summary>セーブ復元専用（issue #507・<see cref="Saves.StatsSaves"/> だけが使う）。校の帳簿を作って返す。</summary>
    internal StatBook GetOrCreateBook(int schoolId)
    {
        lock (_gate) return BookFor(_bySchool, schoolId);
    }

    /// <summary>セーブ復元専用（issue #634）。校の通算帳簿を作って返す。</summary>
    internal StatBook GetOrCreateCareerBook(int schoolId)
    {
        lock (_gate) return BookFor(_careerBySchool, schoolId);
    }

    /// <summary>セーブ復元専用（issue #634）。通算スコープだけを全消去する。</summary>
    internal void ClearCareer() { lock (_gate) _careerBySchool.Clear(); }

    /// <summary>セーブ復元専用。校の県予選帳簿を作って返す。</summary>
    internal StatBook GetOrCreatePrefQualifierBook(int schoolId)
    {
        lock (_gate) return BookFor(_prefQualifierBySchool, schoolId);
    }

    /// <summary>セーブ復元専用。県予選スコープだけを全消去する。</summary>
    internal void ClearPrefQualifier() { lock (_gate) _prefQualifierBySchool.Clear(); }

    /// <summary>
    /// 1試合ぶんを両校へ畳み込む。away=先攻校ID・home=後攻校ID。勝敗投手は各サイドで自動判定
    /// （<see cref="DecisionOfRecord"/>）。相手校の生成選手も SourceId（校内ID）を持つので集計に乗る。
    /// </summary>
    public void FoldMatch(int awaySchoolId, int homeSchoolId, GameResult result)
    {
        lock (_gate)
        {
            Fold(awaySchoolId, result, schoolIsAway: true);
            Fold(homeSchoolId, result, schoolIsAway: false);
        }
    }

    /// <summary>
    /// 1試合の<b>片側だけ</b>を畳み込む（issue #634）。自校戦・練習試合の相手校側が入口で、
    /// 自校のボックススコアは自校ストア（<see cref="PlayerStatStore"/>）が持つため全国側へは積まない。
    /// </summary>
    public void FoldSchool(int schoolId, GameResult result, bool schoolIsAway)
    {
        lock (_gate) Fold(schoolId, result, schoolIsAway);
    }

    private void Fold(int schoolId, GameResult result, bool schoolIsAway)
    {
        var (winPid, losePid) = DecisionOfRecord.Resolve(result, schoolIsAway);
        BookFor(_bySchool, schoolId).FoldGame(result, schoolIsAway, winPid, losePid);
        BookFor(_careerBySchool, schoolId).FoldGame(result, schoolIsAway, winPid, losePid);
    }
}
