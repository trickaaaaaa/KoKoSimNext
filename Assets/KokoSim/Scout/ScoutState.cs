using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Career;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using KokoSim.Unity.Shell;

namespace KokoSim.Unity.Scout
{
    /// <summary>個別能力1項目ぶんの表示行（スカウト候補の段3詳細・issue #867）。</summary>
    public readonly struct AbilityRankRow
    {
        public readonly string Label;
        public readonly Tier Rank;
        public AbilityRankRow(string label, Tier rank) { Label = label; Rank = rank; }
    }

    /// <summary>入部予定者／スカウト候補1人ぶんの表示行（設計書04 §2.1・§2.4）。</summary>
    public sealed class ProspectRow
    {
        public string Name = "";
        /// <summary>投打（「右投左打」）。役割（投手/野手）は出さない＝ポジションはプレイヤーが決める（Issue #93）。</summary>
        public string Handedness = "";
        /// <summary>事前の見立て（推定カテゴリ強さ）。ランクチップはこの値から起こす。</summary>
        public PlayerStrength Estimate = new PlayerStrength(0, 0, 0, 0);
        /// <summary>見立てが有効か。スカウト網が未契約なら false＝<b>推定そのものを出さない</b>
        /// （開示0の推定は誤差σ20でほぼ雑音なので、色付きランクとして出すと嘘をつくことになる）。</summary>
        public bool HasEstimate;
        /// <summary>入部後にだけ埋まる実測（null＝まだ入部していない＝答え合わせは出さない）。</summary>
        public PlayerStrength Actual;
        public int UnknownCount;

        // 隠しパラメータ（判明していなければ null＝画面は「？」）。
        public string GrowthType;
        public string Potential;
        public string Personality;
        public string InjuryResistance;

        /// <summary>スカウト評（見立てのいちばん高い軸を一言にしたもの。入部予定者の一覧で使う）。</summary>
        public string Comment = "";

        // ── スカウト候補のときだけ埋まる（issue #867・design-04 §2.4） ──
        /// <summary>スカウト候補の行か（false＝入部予定者）。</summary>
        public bool IsScout;
        /// <summary>出身県（「静岡県」）。</summary>
        public string Hometown = "";
        /// <summary>自校から見た遠さ（「遠方」）。</summary>
        public string DistanceWord = "";
        /// <summary>競合校の格（「A」）。</summary>
        public string RivalTier = "";
        /// <summary>競合校が自校より上か（不利に効いている＝淡く出す）。</summary>
        public bool RivalIsStronger;
        /// <summary>勧誘対象に指定しているか（枠を1つ使っている。訪問すると自動で立つ）。</summary>
        public bool Targeted;
        /// <summary>スカウトで獲った子か（入部予定者の一覧で印を付ける）。</summary>
        public bool Recruited;
        /// <summary>訪問回数。</summary>
        public int Visits;
        /// <summary>訪問1回の費用[万円]。</summary>
        public double VisitCost;
        /// <summary>3月の判定が済んでいるか。</summary>
        public bool Decided;
        /// <summary>判定の結果（<see cref="Decided"/> が true のときだけ意味を持つ）。</summary>
        public bool Succeeded;
        /// <summary>段（1〜3）。見立ての段ゲートに使う。</summary>
        public int Tier;
        /// <summary>寸評（data/scout-blurbs.yaml 駆動）。</summary>
        public string Blurb = "";
        /// <summary>進路希望の短い語（一覧用。「名門」「出場機会」「地元」）。</summary>
        public string CareerPathWord = "";
        /// <summary>進路希望の一文（詳細用。「名門校に行きたいようだ」）。</summary>
        public string CareerPathSentence = "";
        /// <summary>個別能力のランク（段3だけ埋まる）。</summary>
        public List<AbilityRankRow> Abilities = new List<AbilityRankRow>();
    }

    /// <summary>左の名簿のタブ（設計書06 §3.6・issue #867）。</summary>
    public enum ScoutTab
    {
        /// <summary>入部予定者（受動枠・issue #587）。</summary>
        Incoming = 0,
        /// <summary>スカウト候補（issue #867・旧「中学生リスト」を置き換え）。</summary>
        Candidates = 1,
    }

    /// <summary>スカウト画面の ViewModel（設計書06 §3.6）。</summary>
    public sealed class ScoutView
    {
        /// <summary>一覧の見出し（「入部予定者」／「今年の入部者」）。</summary>
        public string ListTitle = "入部予定者";
        /// <summary>一覧の副題（合流年度・人数）。</summary>
        public string ListSub = "";
        /// <summary>詳細の副題に添える状態（「2027年4月入部予定」／「入部済み」）。</summary>
        public string StateWord = "";
        /// <summary>一覧が空のときに出す一文（空＝行がある）。</summary>
        public string EmptyText = "";
        /// <summary>入部済みの学年を見ているか（＝答え合わせの列を出す）。</summary>
        public bool Joined;
        /// <summary>見立てを出せるか（スカウト網が未契約なら false）。</summary>
        public bool HasEstimate;
        /// <summary>見立てカードの注記（開示の程度）。</summary>
        public string EstimateNote = "";
        /// <summary>いま見ているタブ。</summary>
        public ScoutTab Tab;
        /// <summary>スカウト候補のタブを出せるか（スカウト網を契約して枠が解禁されている）。</summary>
        public bool HasScouting;
        /// <summary>いま訪問できる時期か（秋〜冬）。</summary>
        public bool InScoutSeason;

        // ── 共通の上部帯（オーナー決定2026-11-09・ワイヤー案A） ──
        /// <summary>「スカウト網 Lv2」。</summary>
        public string NetworkWord = "";
        /// <summary>「スカウト枠 3人（内定1）」。</summary>
        public string SlotWord = "";
        /// <summary>「次の節目: 3月 入部確定（あと8週）」。</summary>
        public string MilestoneWord = "";
        /// <summary>探す分野（選択肢の表示名）。</summary>
        public string[] FieldOptions = { "おまかせ", "投球", "打撃", "守備走塁" };
        /// <summary>いま選んでいる分野（<see cref="FieldOptions"/> の添字）。</summary>
        public int FieldIndex;
        /// <summary>分野を選べるか（候補リストが生成済みなら凍結されており選べない）。</summary>
        public bool CanChooseField;

        public List<ProspectRow> Rows = new List<ProspectRow>();
    }

    /// <summary>
    /// スカウト画面の状態（設計書04 §2.1 受動枠 ／ §2.4 スカウト・issue #867）。
    /// エンジンが確定した状態を読むだけで、ここでは何も抽選しない（UI-BUILD-METHOD 大原則④）。
    /// </summary>
    public sealed class ScoutState
    {
        private static readonly TeamStrengthCoefficients Strength = new TeamStrengthCoefficients();
        private readonly SeasonCalendar _calendar = new SeasonCalendar();

        private static readonly ScoutField[] FieldValues =
            { ScoutField.Omakase, ScoutField.Pitching, ScoutField.Batting, ScoutField.Defense };

        /// <summary>いま見ているタブ（画面から切り替える）。</summary>
        public ScoutTab Tab { get; set; }

        public ScoutView BuildView()
        {
            var v = new ScoutView();
            BuildTopBand(v);
            v.HasScouting = ScoutService.Slots > 0;
            v.InScoutSeason = ScoutService.InSeason;
            v.Tab = v.HasScouting ? Tab : ScoutTab.Incoming;

            if (v.Tab == ScoutTab.Candidates) return BuildScoutView(v);

            var cls = ProspectService.Current;
            if (cls == null)
            {
                v.ListSub = "未確定";
                v.EmptyText = "入部予定者は3月に確定する（あと " + WeeksUntilConfirm() + " 週）。\n"
                              + "確定すると、スカウトの見立てと「？」がここに並ぶ。";
                return v;
            }

            v.Joined = cls.Joined;
            v.HasEstimate = cls.Disclosure > 0;
            v.ListTitle = cls.Joined ? "今年の入部者" : "入部予定者";
            v.EstimateNote = !v.HasEstimate ? "スカウト網 未契約"
                : cls.Joined ? "見立てと実際"
                : DisclosureWord(cls.Disclosure);
            v.StateWord = cls.Joined ? "入部済み" : YearLabel(cls.YearIndex) + "4月入部予定";
            v.ListSub = cls.Count + "名 ・ " + v.StateWord;

            for (var i = 0; i < cls.Reports.Count; i++)
                v.Rows.Add(BuildRow(cls, i));

            if (v.Rows.Count == 0)
                v.EmptyText = "今年は入部予定者が居ない。";
            return v;
        }

        /// <summary>探す分野を切り替える（候補リストが生成済みなら無視＝凍結）。</summary>
        public void SelectField(int fieldIndex)
        {
            if (ScoutService.Current != null) return;   // 既に今年ぶんのリストが出ていれば変えられない
            if (fieldIndex < 0 || fieldIndex >= FieldValues.Length) return;
            ScoutService.SelectedField = FieldValues[fieldIndex];
        }

        // ── 上部帯（オーナー決定・ワイヤー案A） ─────────────────────
        private void BuildTopBand(ScoutView v)
        {
            var level = ScoutService.Level;
            v.NetworkWord = level <= 0 ? "スカウト網 未契約" : "スカウト網 Lv" + level;

            var slots = ScoutService.Slots;
            if (slots <= 0)
            {
                v.SlotWord = "スカウト枠 なし（経営でスカウト網を契約）";
            }
            else
            {
                var used = ScoutService.Current?.TargetedCount ?? 0;
                v.SlotWord = "スカウト枠 " + slots + "人（内定" + used + "）";
            }

            v.MilestoneWord = "次の節目: " + ConfirmMonthWord() + " 入部確定（あと " + WeeksUntilConfirm() + " 週）";

            v.CanChooseField = ScoutService.Current == null;
            var idx = System.Array.IndexOf(FieldValues, ScoutService.SelectedField);
            v.FieldIndex = idx < 0 ? 0 : idx;
        }

        private string ConfirmMonthWord()
        {
            var (month, _, _) = _calendar.DateOf(_calendar.ProspectConfirmWeek);
            return month + "月";
        }

        // ── スカウト候補一覧（issue #867） ─────────────────────────
        private ScoutView BuildScoutView(ScoutView v)
        {
            var state = ScoutService.Current;
            v.ListTitle = "スカウト候補";

            if (state == null || state.Candidates.Count == 0)
            {
                v.ListSub = "未着";
                v.EmptyText = "スカウト候補は秋（10月）に届く（あと " + WeeksUntilCandidates() + " 週）。\n"
                              + "届いたら訪問を重ね、気に入った候補を口説く。";
                return v;
            }

            v.ListSub = state.Candidates.Count + "名 ・ " + (state.Resolved ? "決着済み" : "スカウト期間中");
            foreach (var c in state.Candidates) v.Rows.Add(BuildScoutRow(c));
            return v;
        }

        private ProspectRow BuildScoutRow(ScoutCandidate c)
        {
            var r = c.Report;
            var row = new ProspectRow
            {
                IsScout = true,
                Name = c.Player.Name,
                Handedness = HandednessJp(c.Player),
                Estimate = new PlayerStrength(r.Batting, r.Mobility, r.Defense, r.Pitching),
                HasEstimate = r.Tier >= 2,
                Hometown = PrefectureNames.JapaneseOf(c.PrefectureId),
                DistanceWord = PrefectureRegions.DisplayName(c.Distance),
                RivalTier = c.RivalTier.ToString(),
                RivalIsStronger = ScoutModel.TierRank(c.RivalTier) > ScoutModel.TierRank(HomeTier()),
                Targeted = c.Targeted,
                Visits = c.Visits,
                VisitCost = ScoutModel.VisitCost(c.Distance, ScoutService.Coefficients),
                Decided = c.Decided,
                Succeeded = c.Succeeded,
                Tier = r.Tier,
                Blurb = ScoutModel.BlurbText(c, ScoutBlurbPhrasesProvider.Default),
                CareerPathWord = CareerPathShort(c.Preference),
                CareerPathSentence = CareerPathLong(c.Preference),
            };

            if (r.Tier >= 3)
            {
                row.Abilities.Add(new AbilityRankRow("ミート", Tiers.FromStrength(r.Contact)));
                row.Abilities.Add(new AbilityRankRow("パワー", Tiers.FromStrength(r.Power)));
                row.Abilities.Add(new AbilityRankRow("選球眼", Tiers.FromStrength(r.Discipline)));
                row.Abilities.Add(new AbilityRankRow("走力", Tiers.FromStrength(r.Speed)));
                row.Abilities.Add(new AbilityRankRow("守備", Tiers.FromStrength(r.Fielding)));
                row.Abilities.Add(new AbilityRankRow("捕球", Tiers.FromStrength(r.Catching)));
                row.Abilities.Add(new AbilityRankRow("送球", Tiers.FromStrength(r.ThrowAccuracy)));
                row.Abilities.Add(new AbilityRankRow("走塁", Tiers.FromStrength(r.Baserunning)));
                row.Abilities.Add(new AbilityRankRow("球速", Tiers.FromStrength(r.Velocity)));
                row.Abilities.Add(new AbilityRankRow("制球", Tiers.FromStrength(r.Control)));
                row.Abilities.Add(new AbilityRankRow("キレ", Tiers.FromStrength(r.PitchRank)));
                row.Abilities.Add(new AbilityRankRow("スタミナ", Tiers.FromStrength(r.Stamina)));
            }

            row.Comment = row.Blurb;
            return row;
        }

        private static string CareerPathShort(CareerPathPreference p) => p switch
        {
            CareerPathPreference.Prestige => "名門",
            CareerPathPreference.PlayingTime => "出場機会",
            _ => "地元",
        };

        private static string CareerPathLong(CareerPathPreference p) => p switch
        {
            CareerPathPreference.Prestige => "名門校に行きたいようだ",
            CareerPathPreference.PlayingTime => "試合に出られる場所を探しているようだ",
            _ => "地元でプレーしたいようだ",
        };

        private static Tier HomeTier() => Tiers.FromStrength(TeamOverall.Of(RosterService.Active));

        private int WeeksUntilCandidates()
        {
            var left = _calendar.RecruitListWeek - GameClock.Week;
            return left > 0 ? left : left + _calendar.WeeksPerYear;
        }

        // ── 1行ぶん（入部予定者） ────────────────────────────────
        private ProspectRow BuildRow(IncomingClass cls, int index)
        {
            var r = cls.Reports[index];
            // 未合流なら予定者の実体、合流済みならロスターから引く（実体はロスター側だけが持つ）。
            var player = cls.Joined ? FindInRoster(r.PlayerId) : PlayerAt(cls, index);

            var row = new ProspectRow
            {
                Name = string.IsNullOrEmpty(r.Name) ? "？" : r.Name,
                Estimate = new PlayerStrength(r.Batting, r.Mobility, r.Defense, r.Pitching),
                HasEstimate = cls.Disclosure > 0,
                // 入部したら隠しは全部開くので「？」は残さない（詳細が値を出しているのに一覧だけ？だと矛盾する）。
                UnknownCount = cls.Joined ? 0 : r.UnknownCount,
                Recruited = r.Recruited,
                // スカウト評は見立てから起こすので、見立てが無ければ出さない。
                Comment = "",
            };

            if (player != null)
            {
                row.Handedness = HandednessJp(player);
                if (cls.Joined) row.Actual = PlayerStrengthProfile.Compute(player, Strength);
                // 隠しパラメータは「事前に判明した項目」だけ出す。入部後は全部見せる
                // （選手詳細と同じ扱い＝ここだけ隠し続けると二重の真実になる）。
                if (cls.Joined || r.KnowsGrowthType) row.GrowthType = GrowthTypeJp(player.GrowthType);
                if (cls.Joined || r.KnowsPotential) row.Potential = PotentialJp(player);
                if (cls.Joined || r.KnowsPersonality) row.Personality = Personalities.DisplayName(player.Personality);
                if (cls.Joined || r.KnowsInjuryResistance) row.InjuryResistance = InjuryResistanceJp(player.InjuryResistance);
            }

            row.Comment = row.HasEstimate
                ? Comment(row)
                : "スカウト網を契約していないので、入ってくるまで何も分からない。";
            return row;
        }

        private static DevelopingPlayer PlayerAt(IncomingClass cls, int index)
            => index >= 0 && index < cls.Players.Count ? cls.Players[index] : null;

        private static DevelopingPlayer FindInRoster(int id)
            => id <= 0 ? null : RosterService.Roster.FirstOrDefault(p => p.Id == id);

        private int WeeksUntilConfirm()
        {
            var week = GameClock.Week;
            var left = _calendar.ProspectConfirmWeek - week;
            return left > 0 ? left : left + _calendar.WeeksPerYear;
        }

        // ── 表示語彙 ──────────────────────────────────────────

        /// <summary>開示精度を言葉にする。数値そのものは監督に見せない（＝見えている度合いの実感だけ渡す）。</summary>
        public static string DisclosureWord(double disclosure)
        {
            if (disclosure >= 0.65) return "かなり見える";
            if (disclosure >= 0.35) return "一部見える";
            if (disclosure > 0.0) return "ほとんど？";
            return "なし（未契約）";
        }

        private static string HandednessJp(DevelopingPlayer p)
            => ThrowJp(p.Throws) + "投" + BatJp(p.Bats) + "打";

        private static string ThrowJp(Handedness h) => h == Handedness.Left ? "左" : "右";

        private static string BatJp(Handedness h)
            => h == Handedness.Left ? "左" : h == Handedness.Switch ? "両" : "右";

        // 成長タイプの対訳（GrowthStageTable のコメントと同じ語）。他画面は「？」固定なので
        // 対訳が要るのは今のところこの画面だけ＝ここに置く（使う画面が増えたら部品辞書へ移す）。
        private static string GrowthTypeJp(GrowthType t)
            => t == GrowthType.Early ? "早熟" : t == GrowthType.Late ? "晩成" : "標準";

        /// <summary>才能上限は能力ごとに違うので、平均をティアに畳んで1文字で出す（数値は見せない）。</summary>
        private static string PotentialJp(DevelopingPlayer p)
        {
            double sum = 0;
            var n = 0;
            foreach (var k in AbilityKinds.All) { sum += p.Cap(k); n++; }
            return n == 0 ? "？" : Tiers.FromStrength(sum / n).ToString();
        }

        private static string InjuryResistanceJp(double v)
            => v >= 65 ? "強い" : v >= 40 ? "普通" : "弱い";

        /// <summary>
        /// スカウト評。見立ての最も高い軸を一言にする（＝推定から機械的に起こすだけで、
        /// 新しい抽選はしない）。分からないことが多いほど言葉を濁す。
        /// </summary>
        private static string Comment(ProspectRow row)
        {
            var s = row.Estimate;
            var best = "打撃";
            var v = s.Batting;
            if (s.Pitching > v) { best = "投球"; v = s.Pitching; }
            if (s.Defense > v) { best = "守備"; v = s.Defense; }
            if (s.Mobility > v) { best = "走り"; v = s.Mobility; }

            var head = v >= 70 ? best + "は県内でも指折り"
                : v >= 55 ? best + "に見どころがある"
                : v >= 40 ? best + "は年相応"
                : "いまは何もかも粗い";
            var tail = row.UnknownCount >= 3 ? "。ただ、見えているものはまだ少ない。"
                : row.UnknownCount >= 1 ? "。伸びしろは入ってからだろう。"
                : "。ひととおり見え切った。";
            return head + tail;
        }

        /// <summary>「合流する年度の4月」の暦年（週0＝その年度の4月1週目で引く）。</summary>
        private static string YearLabel(int joinYearIndex) => SeasonClock.YearLabel(joinYearIndex, 0);
    }
}
