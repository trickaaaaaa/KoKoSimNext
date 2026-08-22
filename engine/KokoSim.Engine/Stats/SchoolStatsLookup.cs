namespace KokoSim.Engine.Stats;

/// <summary>
/// 成績を <b>(校ID, 校内選手ID)</b> で引く単一の入口（issue #634）。自校ストア
/// （<see cref="PlayerStatStore"/>）と全国ストア（<see cref="NationTournamentStats"/>）を<b>校IDで</b>
/// 振り分ける。UI はこの seam だけを見て、成績を自前で集計し直さない。
///
/// <para><b>なぜ要るか</b>: 永続ロスター（#80）以降、相手校の生成選手も <see cref="Players.Player.SourceId"/>
/// を持つ。ところが選手IDは<b>校ごとに1から</b>振るため自校の選手IDと衝突する（#634）。
/// 「SourceId が非 null＝自校」という #80 以前の前提で自校ストアを引くと、<b>同じ番号の自校選手の
/// 成績が相手校の選手に出る</b>（その自校選手が未出場なら「—」になり成績が無いように見える）。</para>
/// </summary>
public sealed class SchoolStatsLookup
{
    private readonly int _managerSchoolId;
    private readonly PlayerStatStore _manager;
    private readonly NationTournamentStats _nation;

    /// <param name="managerSchoolId">自校の学校ID（Shell の ManagerSchoolId＝生成校と衝突しない専用ID）。</param>
    /// <param name="manager">自校の成績ストア（通算・公式戦通算・今大会）。</param>
    /// <param name="nation">全国の学校別成績（裏試合フルシムが積む）。</param>
    public SchoolStatsLookup(int managerSchoolId, PlayerStatStore manager, NationTournamentStats nation)
    {
        _managerSchoolId = managerSchoolId;
        _manager = manager;
        _nation = nation;
    }

    /// <summary>その学校が自校か（＝自校ストア側で引くか）。</summary>
    public bool IsManagerSchool(int schoolId) => schoolId == _managerSchoolId;

    /// <summary>通算成績（年・大会をまたぐ永続スコープ）。未登録なら null。</summary>
    public PlayerStats? Career(int schoolId, int? playerId)
        => Get(CareerBook(schoolId), playerId);

    /// <summary>今大会成績（大会開始時にリセットされるスコープ）。未登録なら null。</summary>
    public PlayerStats? CurrentTournament(int schoolId, int? playerId)
        => Get(CurrentTournamentBook(schoolId), playerId);

    /// <summary>学校の通算帳簿（未登録なら null）。</summary>
    public StatBook? CareerBook(int schoolId)
        => IsManagerSchool(schoolId) ? _manager.Career : _nation.CareerForSchool(schoolId);

    /// <summary>学校の今大会帳簿（未登録なら null）。一覧表はこれを1回引いて全員ぶんを描く。</summary>
    public StatBook? CurrentTournamentBook(int schoolId)
        => IsManagerSchool(schoolId) ? _manager.CurrentTournament : _nation.ForSchool(schoolId);

    /// <summary>直前の夏の県予選成績（甲子園の大会展望が引く。設計書05 §1.4b）。未登録なら null。</summary>
    public PlayerStats? PrefQualifier(int schoolId, int? playerId)
        => Get(PrefQualifierBook(schoolId), playerId);

    /// <summary>学校の県予選帳簿（未登録なら null）。</summary>
    public StatBook? PrefQualifierBook(int schoolId)
        => IsManagerSchool(schoolId) ? _manager.PrefQualifier : _nation.PrefQualifierForSchool(schoolId);

    private static PlayerStats? Get(StatBook? book, int? playerId)
        => book is not null && playerId is int id ? book.Get(id) : null;
}
