using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Career;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using KokoSim.Unity.Shell;

namespace KokoSim.Unity.Scout
{
    /// <summary>入部予定者1人ぶんの表示行（設計書04 §2.1・issue #587）。</summary>
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

        /// <summary>スカウト評（見立てのいちばん高い軸を一言にしたもの）。</summary>
        public string Comment = "";

        // ── 中学生リストのときだけ埋まる（能動勧誘・issue #588） ──
        /// <summary>中学生リストの行か（false＝入部予定者）。</summary>
        public bool IsRecruit;
        /// <summary>出身県（「静岡県」）。</summary>
        public string Hometown = "";
        /// <summary>自校から見た遠さ（「遠方」）。</summary>
        public string DistanceWord = "";
        /// <summary>競合校の格（「A」）。</summary>
        public string RivalTier = "";
        /// <summary>競合校が自校より上か（不利に効いている＝淡く出す）。</summary>
        public bool RivalIsStronger;
        /// <summary>勧誘対象に指定しているか（枠を1つ使っている）。中学生リストで使う。</summary>
        public bool Targeted;
        /// <summary>能動勧誘で獲った子か（入部予定者の一覧で印を付ける）。</summary>
        public bool Recruited;
        /// <summary>訪問回数。</summary>
        public int Visits;
        /// <summary>いまの成功率（0〜1）。</summary>
        public double SuccessProbability;
        /// <summary>訪問1回の費用[万円]。</summary>
        public double VisitCost;
        /// <summary>3月の判定が済んでいるか。</summary>
        public bool Decided;
        /// <summary>判定の結果（<see cref="Decided"/> が true のときだけ意味を持つ）。</summary>
        public bool Succeeded;
    }

    /// <summary>左の名簿のタブ（設計書06 §3.6・issue #588 A案）。</summary>
    public enum ScoutTab
    {
        /// <summary>入部予定者（受動枠・issue #587）。</summary>
        Incoming = 0,
        /// <summary>中学生リスト（能動勧誘・issue #588）。</summary>
        Recruits = 1,
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
        /// <summary>下段のスカウト網の状態（段・年額・開示）。</summary>
        public string NetworkLine = "";
        /// <summary>下段の勧誘枠の状態（「勧誘枠 1/2 ・ 寮 Lv1」）。空＝寮が無い。</summary>
        public string SlotLine = "";
        /// <summary>いま見ているタブ。</summary>
        public ScoutTab Tab;
        /// <summary>中学生リストのタブを出せるか（寮を建てて枠が解禁されている）。</summary>
        public bool HasRecruiting;
        /// <summary>いま訪問できる時期か（秋〜冬）。</summary>
        public bool InRecruitSeason;
        public List<ProspectRow> Rows = new List<ProspectRow>();
    }

    /// <summary>
    /// スカウト画面の状態（設計書04 §2.1 受動枠 ／ 設計書06 §3.6・issue #587）。
    /// エンジンが確定した <see cref="IncomingClass"/> を読むだけで、ここでは何も抽選しない
    /// （UI-BUILD-METHOD 大原則④＝UIはロジックを持たない）。
    /// </summary>
    public sealed class ScoutState
    {
        private static readonly TeamStrengthCoefficients Strength = new TeamStrengthCoefficients();
        private readonly SeasonCalendar _calendar = new SeasonCalendar();

        /// <summary>いま見ているタブ（画面から切り替える）。</summary>
        public ScoutTab Tab { get; set; }

        public ScoutView BuildView()
        {
            var v = new ScoutView();
            BuildNetworkLine(v);
            BuildSlotLine(v);
            v.HasRecruiting = RecruitService.Slots > 0;
            v.InRecruitSeason = RecruitService.InSeason;
            v.Tab = v.HasRecruiting ? Tab : ScoutTab.Incoming;

            if (v.Tab == ScoutTab.Recruits) return BuildRecruitView(v);

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
            // 半幅カードの見出しに収まる長さにする（長いと右端で黙って切れる）。
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

        // ── 中学生リスト（能動勧誘・issue #588） ─────────────────────
        private ScoutView BuildRecruitView(ScoutView v)
        {
            var state = RecruitService.Current;
            v.ListTitle = "中学生リスト";
            v.EstimateNote = state == null || state.Prospects.Count == 0
                ? ""
                : DisclosureWord(ManagerService.ContractEffects.ProspectDisclosure);

            if (state == null || state.Prospects.Count == 0)
            {
                v.ListSub = "未着";
                v.EmptyText = "中学生リストは秋（10月）に届く（あと " + WeeksUntilRecruitList() + " 週）。\n"
                              + "届いたら枠のぶんだけ勧誘対象を選び、秋〜冬に訪問を重ねる。";
                return v;
            }

            v.ListSub = state.Prospects.Count + "名 ・ " + (state.Resolved ? "決着済み" : "勧誘期間中");
            foreach (var p in state.Prospects) v.Rows.Add(BuildRecruitRow(p));
            return v;
        }

        private ProspectRow BuildRecruitRow(RecruitProspect p)
        {
            var r = p.Report;
            var hasEstimate = ManagerService.ContractEffects.ProspectDisclosure > 0;
            var row = new ProspectRow
            {
                IsRecruit = true,
                Name = string.IsNullOrEmpty(r.Name) ? p.Player.Name : r.Name,
                Handedness = HandednessJp(p.Player),
                Estimate = new PlayerStrength(r.Batting, r.Mobility, r.Defense, r.Pitching),
                HasEstimate = hasEstimate,
                UnknownCount = r.UnknownCount,
                Hometown = PrefectureNames.JapaneseOf(p.PrefectureId),
                DistanceWord = PrefectureRegions.DisplayName(p.Distance),
                RivalTier = p.RivalTier.ToString(),
                RivalIsStronger = RecruitModel.TierRank(p.RivalTier) > RecruitModel.TierRank(HomeTier()),
                Targeted = p.Targeted,
                Visits = p.Visits,
                SuccessProbability = RecruitService.SuccessProbability(p.Index),
                VisitCost = RecruitService.VisitCost(p.Index),
                Decided = p.Decided,
                Succeeded = p.Succeeded,
            };

            // 隠しパラメータは受動枠と同じ扱い（判明した項目だけ出す）。
            if (r.KnowsGrowthType) row.GrowthType = GrowthTypeJp(p.Player.GrowthType);
            if (r.KnowsPotential) row.Potential = PotentialJp(p.Player);
            if (r.KnowsPersonality) row.Personality = Personalities.DisplayName(p.Player.Personality);
            if (r.KnowsInjuryResistance) row.InjuryResistance = InjuryResistanceJp(p.Player.InjuryResistance);

            row.Comment = hasEstimate
                ? Comment(row)
                : "スカウト網を契約していないので、名前しか分からない。";
            return row;
        }

        private static Tier HomeTier() => Tiers.FromStrength(TeamOverall.Of(RosterService.Active));

        private static void BuildSlotLine(ScoutView v)
        {
            var slots = RecruitService.Slots;
            if (slots <= 0)
            {
                v.SlotLine = "勧誘枠 なし（寮を建てると解禁）";
                return;
            }
            var used = RecruitService.Current?.TargetedCount ?? 0;
            v.SlotLine = "勧誘枠 " + used + "/" + slots
                         + " ・ 寮 Lv" + ManagerService.Facilities.LevelOf("dorm");
        }

        private int WeeksUntilRecruitList()
        {
            var left = _calendar.RecruitListWeek - GameClock.Week;
            return left > 0 ? left : left + _calendar.WeeksPerYear;
        }

        // ── 1行ぶん ────────────────────────────────────────────
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

        // ── 下段: スカウト網の状態 ─────────────────────────────
        private static void BuildNetworkLine(ScoutView v)
        {
            var def = ManagerService.ContractList.Find("scouting");
            var level = ManagerService.Contracts.LevelOf("scouting");
            if (def == null || level <= 0 || level >= def.Tiers.Count)
            {
                v.NetworkLine = "スカウト網 未契約 ／ 事前開示 なし";
                return;
            }
            var tier = def.Tiers[level];
            v.NetworkLine = "スカウト網 Lv" + level
                            + " ／ " + tier.AnnualCost.ToString("0") + "万/年"
                            + " ／ 事前開示 " + DisclosureWord(tier.ProspectDisclosure);
        }

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
