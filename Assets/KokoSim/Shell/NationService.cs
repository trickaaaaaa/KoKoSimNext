using System.Collections.Generic;
using KokoSim.Engine.Career;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Season;
using KokoSim.Engine.Stats;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 架空全国（4000校）の単一ソース。従来は HomeState が private static で抱えていたため、
    /// 練習試合の申込先（県内校）を引く画面が同じ母集団を参照できなかった。ここに集約し、
    /// 大会の出場校も練習試合の相手も同じ Nation・同じ School インスタンスを見るようにする。
    /// 生成は決定論（seed=2026 固定・不変条件#2）で初回アクセス時に1回だけ走る。
    /// </summary>
    public static class NationService
    {
        /// <summary>自校ID（生成校と衝突しない専用ID）。</summary>
        public const int ManagerSchoolId = -1;

        /// <summary>本拠県・校名の既定（新規ゲーム設定画面が無かった頃の固定値。旧セーブの補完値でもある）。</summary>
        public const int DefaultPrefectureId = 13;   // 神奈川（Prefecture.Id は0基点＝JIS番号-1）
        public const string DefaultSchoolName = "桜丘";

        /// <summary>
        /// 本拠県（issue #585 でセッション状態へ）。新規ゲーム設定画面が確定し、セーブにも載る。
        /// 全国生成（seed=2026）はこの値に依存しない＝どの県を選んでも全国4000校の顔ぶれは同じ。
        /// </summary>
        public static int PrefectureId { get; private set; } = DefaultPrefectureId;

        /// <summary>自校の校名（「高校」を含まない幹。表示側で付ける）。</summary>
        public static string ManagerSchoolName { get; private set; } = DefaultSchoolName;

        /// <summary>県内地区（0基点。地区を持たない県は null。設計書05 §2.2）。</summary>
        public static int? DistrictId { get; private set; }

        /// <summary>
        /// 自校の同定を据える（新規ゲーム設定の確定／セーブ復元の1経路）。県が変わると
        /// 「本拠県の全高校」「県外の遠征先」のキャッシュが意味を失うので必ず捨てる。
        /// </summary>
        public static void SetSchoolIdentity(string name, int prefectureId, int? districtId)
        {
            ManagerSchoolName = string.IsNullOrWhiteSpace(name) ? DefaultSchoolName : name.Trim();
            PrefectureId = prefectureId;
            DistrictId = districtId;
            _prefecture = null;
            _away = null;
        }

        private static readonly NationCoefficients Coeff = new NationCoefficients();
        private static Nation _generated;
        private static Nation _nation;
        // 転任で全国に残った学校・監督が率いる生成校（issue #641）。決定論で再生成した全国に重ねる差分。
        private static NationOverlay _overlay = new NationOverlay();
        private static List<School> _prefecture;
        private static List<School> _away;

        // ===== AI校の年次進化（issue wire-ai-school-evolve-into-game・設計書05 §2.3）=====
        // 学校の Strength/Fame は生成値のまま固定されていた（Evolve を呼ぶ箇所が無かった）＝県内一強が
        // 23大会続く。年間の校別勝数を台帳へ積み、年度替わりに1回 AiSchoolEvolution.ApplyYear で進化させ、
        // 進化後の値はセーブへ載せる（学校・県の名前は決定論再生成で戻るので変化する2値だけ保存する）。

        /// <summary>今年度の校別勝数（自県ブラケット＋他県裏試合）。年度末の進化で消費して空にする。</summary>
        public static AiSchoolYearLedger YearLedger { get; } = new AiSchoolYearLedger();

        /// <summary>一度でも進化した（or セーブから進化後の値を受けた）か。真なら Capture で全校の値を保存する。</summary>
        private static bool _hasEvolution;
        /// <summary>セーブから受けた進化後の値。<see cref="Nation"/> を再生成した直後に上書きして消費する。</summary>
        private static List<SchoolEvolutionState> _pendingSchoolStates;

        /// <summary>
        /// 全国4000校。<b>県内地区（DistrictId）の割当を渡して生成する</b>（issue #585）。地区の付与は
        /// 独立Fork（NationGenerator 側）なので強さ・名声・校風の生成列は乱れない＝全国生成は不変。
        /// セーブ復元直後は生成した実体へ進化後の Strength/Fame を上書きする（同一インスタンスなので
        /// 校IDで引く側＝ロスターの成長ターゲット・出場校は自動で追従する）。
        /// </summary>
        public static Nation Nation
        {
            get
            {
                if (_nation != null) return _nation;
                _nation = _overlay.Apply(GeneratedNation);
                if (_pendingSchoolStates != null)
                {
                    AiSchoolEvolution.Restore(_nation, _pendingSchoolStates);
                    _pendingSchoolStates = null;
                }
                return _nation;
            }
        }

        /// <summary>
        /// 決定論で再生成した素の全国（seed=2026）。転任の差分（<see cref="_overlay"/>）を当てる前。
        /// 大会・練習試合などはすべて差分込みの <see cref="Nation"/> を見る。
        /// </summary>
        private static Nation GeneratedNation => _generated ??= NationGenerator.Generate(
            SchoolNameVocabProvider.Default, Coeff, new Xoshiro256Random(2026),
            districtPlan: DistrictPlanProvider.Plan);

        /// <summary>
        /// 監督の転任（設計書04 §1.3・issue #641）。旧校を AI校として全国に残し（旧部員はそのロスターへ）、
        /// 転任先 AI校の部員を新しい自校の部員として返す。転任先は全国から外れる（自校は専用ID で出場）。
        /// 自校の同定（校名・県）は呼び出し側が <see cref="SetSchoolIdentity"/> で据え直す。
        /// </summary>
        public static SchoolTransferResult TransferManager(
            DepartingSchool departing, IReadOnlyList<DevelopingPlayer> departingActive, School target,
            int yearIndex, int nextPlayerId)
        {
            _ = Nation;   // 保留中の年次進化を当ててから差分を動かす
            var result = SchoolTransfer.Execute(_overlay, GeneratedNation, Rosters, departing, departingActive,
                target, yearIndex, nextPlayerId, GameSeed.Master);
            _nation = null;
            _prefecture = null;
            _away = null;
            _schoolById = null;
            return result;
        }

        /// <summary>
        /// 年度替わり（4月）に1回、終了した年度 <paramref name="endedYearIndex"/> の勝数で全AI校を進化させる
        /// （<see cref="GameClock"/> が <see cref="AdvanceAiYear"/> の<b>前</b>に呼ぶ＝進化後の Strength を
        /// 逆算配分の目標にする）。乱数は母種×年度の専用ストリーム＝他の抽選列は不変（不変条件#2）。
        /// 甲子園王者は台帳が持ち回る（8月に決まり進化は翌4月＝年をまたぐ）。
        /// 自校は <see cref="Nation"/> に含まれない（専用ID）ので、自校が優勝した年は対象校なし＝無しと同じ。
        /// </summary>
        public static void EvolveAiSchools(int endedYearIndex)
        {
            AiSchoolEvolution.ApplyYear(Nation, YearLedger.Snapshot(), YearLedger.ChampionId, Coeff,
                GameSeed.Master, endedYearIndex);
            YearLedger.Clear();
            _hasEvolution = true;
            // 遠征先は強さ順の等間隔抽出＝勢力図が動いたら並べ直す（県内校リストは生成順なので不変）。
            _away = null;
        }

        /// <summary>本拠県の全高校（自校は含まない）。順序は生成順で安定。</summary>
        public static IReadOnlyList<School> PrefectureSchools
        {
            get
            {
                if (_prefecture != null) return _prefecture;
                _prefecture = new List<School>();
                foreach (var s in Nation.InPrefecture(PrefectureId))
                {
                    if (s.Id == ManagerSchoolId) continue;
                    _prefecture.Add(s);
                }
                return _prefecture;
            }
        }

        /// <summary>
        /// 県外の練習試合相手（年契約「遠征」・design-04 §1.4a）。全国4000校をそのまま並べても選べないので
        /// <see cref="AwaySchoolCount"/> 校へ絞るが、<b>絞り方は「強い順の上位」ではなく「強さ順の等間隔」</b>。
        ///
        /// <para>各県の最強校だけを並べると総合66〜95に固まり、赴任直後（総合33前後）の監督には
        /// 受諾率2%＝一生組めない相手しか並ばない＝20万/年を払う意味が無くなる（実測で確認した）。
        /// 等間隔に取れば全国最強から自校と同格まで散るので、名声が上がるほど上の相手に手が届く段階ができる。</para>
        ///
        /// <para>乱数は使わず、同点は校IDの昇順で割る＝同じ全国生成なら常に同じ顔ぶれ（不変条件#2）。
        /// 自校の強さには依存しないので、年をまたいでも作り直しが要らない。</para>
        /// </summary>
        public static IReadOnlyList<School> AwaySchools
        {
            get
            {
                if (_away != null) return _away;

                var all = new List<School>();
                foreach (var s in Nation.Schools)
                    if (s.PrefectureId != PrefectureId) all.Add(s);

                all.Sort((a, b) =>
                {
                    var c = b.Strength.CompareTo(a.Strength);
                    return c != 0 ? c : a.Id.CompareTo(b.Id);
                });

                var list = new List<School>();
                if (all.Count <= AwaySchoolCount)
                {
                    list.AddRange(all);
                }
                else
                {
                    // 先頭（全国最強）を必ず含め、そこから末尾まで等間隔に拾う。
                    for (var i = 0; i < AwaySchoolCount; i++)
                        list.Add(all[(int)((long)i * (all.Count - 1) / (AwaySchoolCount - 1))]);
                }

                _away = list;
                return _away;
            }
        }

        /// <summary>遠征先として並べる県外校の数（1画面でスキャンできる長さに抑える）。</summary>
        private const int AwaySchoolCount = 24;

        /// <summary>県ID→県名（遠征先の行に添える）。見つからなければ空文字。</summary>
        public static string PrefectureName(int prefectureId)
        {
            foreach (var p in Nation.Prefectures)
                if (p.Id == prefectureId) return p.Name;
            return "";
        }

        /// <summary>
        /// 自校（School 表現）。強さは共有ロスター由来の総合力（TeamOverall＝6指標の標準化総合）で、
        /// チーム総合力パネルの表示値と同じ尺度になる。
        /// </summary>
        public static School ManagerSchool() => new School
        {
            Id = ManagerSchoolId,
            Name = ManagerSchoolName,
            PrefectureId = PrefectureId,
            DistrictId = DistrictId,
            Strength = TeamOverall.Of(RosterService.Active),
        };

        // ===== AI校の永続ロスター（#80）＋全国通算成績（#43）=====
        // 使い捨て生成（StrengthTeamFactory.ForSchool）を廃し、全4000校の選手個人を年をまたいで継続させる。
        // 生成はロスター参照時に (校ID, 年度) から遅延ブートストラップ（決定論）。GameContext と同様、係数は既定
        // （Unity側は coefficients.yaml を読まない従来挙動に合わせる。調整はテスト/Balance CLI 経由）。

        private static readonly AiRosterDeps RosterDeps = new AiRosterDeps();
        private static NationRosters _rosters;
        private static NationTournamentStats _tournamentStats;
        private static Dictionary<int, School> _schoolById;

        /// <summary>全国の永続ロスター保持庫（遅延生成の単一ソース）。</summary>
        public static NationRosters Rosters => _rosters ?? (_rosters = new NationRosters(RosterDeps));

        /// <summary>全国の学校別成績（裏試合フルシムが積む単一ソース。今大会＋通算の2スコープ）。</summary>
        public static NationTournamentStats TournamentStats
            => _tournamentStats ?? (_tournamentStats = new NationTournamentStats());

        /// <summary>
        /// 成績を (校ID, 選手ID) で引く単一の入口（issue #634）。自校ストア（<see cref="GameSession.Stats"/>）と
        /// 全国ストア（<see cref="TournamentStats"/>）の振り分けは engine の <see cref="SchoolStatsLookup"/> に
        /// 集約されている＝画面ごとに「SourceId があれば自校」と書かない。
        /// <para>参照するストア実体はセーブ復元で差し替わる（<see cref="GameSession.Current"/> ごと入れ替わる）ので
        /// 都度組む＝軽い値オブジェクト。</para>
        /// </summary>
        public static SchoolStatsLookup StatsLookup
            => new SchoolStatsLookup(ManagerSchoolId, GameSession.Current.Stats, TournamentStats);

        /// <summary>学校ID→School 解決（成長ターゲット＝進化後Strength を引く）。自校IDは対象外＝null。</summary>
        public static School SchoolById(int id)
        {
            if (_schoolById == null)
            {
                _schoolById = new Dictionary<int, School>();
                foreach (var s in Nation.Schools) _schoolById[s.Id] = s;
            }
            return _schoolById.TryGetValue(id, out var found) ? found : null;
        }

        /// <summary>セーブ保存: materialize 済み全国ロスターと今大会成績を DTO へ写す（issue #507）。
        /// 学校・県（<see cref="Nation"/>）は決定論再生成なので保存しない（School.cs 設計注記）。</summary>
        public static void Capture(KokoSim.Engine.Saves.SaveState s)
        {
            s.NationRosters = new List<AiSchoolRoster>(Rosters.Snapshot());
            s.TournamentStats = TournamentStats;
            // AI校の進化状態（v13）。一度も進化していなければ空＝生成値のまま（旧セーブと同じ意味）。
            s.NationSchools = _hasEvolution ? AiSchoolEvolution.Snapshot(Nation) : new List<SchoolEvolutionState>();
            s.NationYearWins = new Dictionary<int, int>(YearLedger.Snapshot());
            s.NationKoshienChampionId = YearLedger.ChampionId;
            s.NationOverlay = _overlay;   // 転任の差分（v21・issue #641）
            // 自校の同定（issue #585・v4）。学校・県と違い再生成では出てこないので保存する。
            s.SchoolName = ManagerSchoolName;
            s.PrefectureId = PrefectureId;
            s.DistrictId = DistrictId;
        }

        /// <summary>セーブ復元: 全国ロスター・今大会成績を据え、学校/県キャッシュを落として決定論再生成させる。</summary>
        public static void Restore(KokoSim.Engine.Saves.SaveState s)
        {
            _rosters = NationRosters.FromSaved(RosterDeps, s.NationRosters);
            _tournamentStats = s.TournamentStats;
            _overlay = s.NationOverlay ?? new NationOverlay();
            _generated = null;
            _nation = null;
            _schoolById = null;
            _away = null;
            // AI校の進化状態（v13）: 再生成の直後に上書きするため保留に置く。空＝生成値のまま。
            _pendingSchoolStates = s.NationSchools != null && s.NationSchools.Count > 0 ? s.NationSchools : null;
            _hasEvolution = _pendingSchoolStates != null;
            YearLedger.Clear();
            if (s.NationYearWins != null) YearLedger.AddWins(s.NationYearWins);
            YearLedger.SetChampion(s.NationKoshienChampionId);
            // 自校の同定（旧セーブは Migrate が桜丘・神奈川で補ってくる）。県別キャッシュもここで落ちる。
            SetSchoolIdentity(s.SchoolName, s.PrefectureId, s.DistrictId);
        }

        /// <summary>
        /// 新規ゲームの完全リセット: 全国・ロスター・今大会成績のキャッシュを落とす（次アクセスで再生成）。
        /// 自校の同定は<b>ここでは戻さない</b>＝新規ゲーム設定画面が確定した直後に呼ばれても
        /// 選んだ県・校名が消えないようにする（既定へ戻したいときは <see cref="SetSchoolIdentity"/> を明示で呼ぶ）。
        /// </summary>
        public static void Reset()
        {
            _overlay = new NationOverlay();
            _generated = null;
            _nation = null;
            _prefecture = null;
            _away = null;
            _schoolById = null;
            _rosters = null;
            _tournamentStats = null;
            _hasEvolution = false;
            _pendingSchoolStates = null;
            YearLedger.Clear();
        }

        /// <summary>夏大会後の代替わり: 生成済み全校の3年生を引退させる（秋の新チーム＝下級生のみ）。</summary>
        public static void RetireAiGraduates() => Rosters.RetireGraduatesAll();

        /// <summary>年度替わり（4月）: 生成済み全校で進級・新入生加入・逆算配分成長を適用する。</summary>
        public static void AdvanceAiYear(int newYearIndex) => Rosters.AdvanceExistingYear(SchoolById, newYearIndex);
    }
}
