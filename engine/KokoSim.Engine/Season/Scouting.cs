using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Season;

/// <summary>
/// 探す分野（設計書04 §2・issue #867・オーナー決定2026-11-09の4）。選ぶと、スカウトが探してくる候補の
/// <b>能力の傾き</b>だけが変わる。候補に「投手」「野手」のラベルは一切付けない（CLAUDE.md
/// 「守備位置・適性はゲームが提示しない」）＝<see cref="PitcherRoleResolver.LeansPitcher(DevelopingPlayer)"/>
/// 等はここでは一切参照しない。
/// </summary>
public enum ScoutField
{
    /// <summary>おまかせ（傾きなし）。</summary>
    Omakase,
    Pitching,
    Batting,
    /// <summary>守備走塁（守備＋機動の複合軸）。</summary>
    Defense,
}

/// <summary>
/// 進路希望（設計書04 §2・オーナー決定2026-11-09の6）。候補1人に1つだけ持たせる。
/// 入部の成否はこの志向と自校の状況との相性で変わる（<see cref="ScoutModel.SuccessProbability"/>）。
/// </summary>
public enum CareerPathPreference
{
    /// <summary>名門志向（名声が高いほど来る）。</summary>
    Prestige,
    /// <summary>出場機会志向（その子より強い部員が少ないほど来る）。</summary>
    PlayingTime,
    /// <summary>地元志向（近いほど来る）。</summary>
    Local,
}

/// <summary>
/// スカウトの係数（設計書04 §2・issue #867）。枠の数自体は年契約「スカウト網」の段が持つ
/// （<see cref="Career.ContractTier.ScoutSlots"/>＝未契約0/Lv1=2/Lv2=3/Lv3=4）。ここは
/// 候補リストの作り方・入部成否・訪問費用・見立ての当たりやすさの係数だけを持つ。
///
/// <para><b>進路希望の重み・見立てノイズの具体値は issue 本文・オーナー決定に数値指定が無い</b>ため、
/// 無難な初期値を置いた（要バランス調整）。</para>
/// </summary>
public sealed record ScoutCoefficients
{
    /// <summary>候補リストの人数＝枠×これ（オーナー決定「候補リストは枠の約3倍」）。</summary>
    public int CandidatesPerSlot { get; init; } = 3;

    /// <summary>
    /// 候補の才能中心値への上乗せ（受動枠より上振れさせる＝わざわざ名前が挙がる子）。
    /// 旧 <c>RecruitCoefficients.TalentCenterAdd</c> と同じ位置づけ。契約段の質加算
    /// （<see cref="Career.ContractTier.ScoutTalentAdd"/>）とは別に、常に乗る固定の上振れ。
    /// </summary>
    public double CandidateTalentCenterAdd { get; init; } = 10.0;

    /// <summary>
    /// 分野を指定したときに生成するプールの倍率（候補リストの人数に対して）。大きいほど「その分野に強い子」
    /// に絞り込める。おまかせ（<see cref="ScoutField.Omakase"/>）はプールを作らず傾きも付けない。
    /// </summary>
    public double FieldBiasOversample { get; init; } = 3.0;

    /// <summary>選抜の順位付けに乗せるジッタのσ（0〜100スケール）。無いと毎回同じ上位が機械的に選ばれる。</summary>
    public double FieldBiasJitterSd { get; init; } = 10.0;

    /// <summary>他県から来る割合（残りは自県＝距離0）。</summary>
    public double OutOfPrefectureRatio { get; init; } = 0.65;

    // --- 入部成否（設計書04 §2・オーナー決定6） ---
    /// <summary>
    /// 基礎成功率（進路希望の相性項が0・競合なし・訪問0のとき）。遠方（<see cref="ScoutModel.Closeness"/>=0）
    /// でも相性項が付かない志向が床（<see cref="ProbabilityMin"/>）に落ちきらない水準に置く
    /// （<see cref="PenaltyFar"/>=0.24 を上回らせる）。
    /// </summary>
    public double BaseProbability { get; init; } = 0.30;
    /// <summary>名門志向の子に効く名声の傾き（名声100で+0.35）。</summary>
    public double ProbPerFame { get; init; } = 0.0035;
    /// <summary>出場機会志向の子に効く「空き」の傾き（<see cref="ScoutModel.OpportunityScore"/> 0〜1）。</summary>
    public double ProbPerOpportunity { get; init; } = 0.35;
    /// <summary>地元志向の子に効く「近さ」の傾き（<see cref="ScoutModel.Closeness"/> 0〜1。同県=1.0〜遠方=0.0）。</summary>
    public double ProbPerLocal { get; init; } = 0.35;
    /// <summary>訪問1回あたりの上昇。</summary>
    public double ProbPerVisit { get; init; } = 0.09;
    /// <summary>訪問による上昇の上限。</summary>
    public double VisitGainMax { get; init; } = 0.36;
    /// <summary>競合校のティア1段あたりの低下（自校より上の段だけ効く）。</summary>
    public double ProbPerRivalTier { get; init; } = 0.08;
    public double ProbabilityMin { get; init; } = 0.02;
    public double ProbabilityMax { get; init; } = 0.92;

    public double PenaltySameRegion { get; init; } = 0.06;
    public double PenaltyNeighborRegion { get; init; } = 0.14;
    public double PenaltyFar { get; init; } = 0.24;
    /// <summary>地元志向の相性項に使う「近さ」（0〜1）: 同地方。同県=1.0・遠方=0.0。</summary>
    public double ClosenessSameRegion { get; init; } = 0.67;
    /// <summary>同・隣接地方。</summary>
    public double ClosenessNeighborRegion { get; init; } = 0.33;
    /// <summary>競合校のティア抽選: 一様乱数がこの値未満なら自校と同ティア（差0）。</summary>
    public double RivalTierSameCutoff { get; init; } = 0.35;
    /// <summary>同・この値未満なら自校の1つ上。</summary>
    public double RivalTierPlus1Cutoff { get; init; } = 0.65;
    /// <summary>同・この値未満なら自校の2つ上。</summary>
    public double RivalTierPlus2Cutoff { get; init; } = 0.85;
    /// <summary>同・この値未満なら自校の3つ上（それ以上は1つ下）。</summary>
    public double RivalTierPlus3Cutoff { get; init; } = 0.95;
    /// <summary>寸評の水準バケツ: 最大軸（ノイズ入り）がこの値以上なら「高」。</summary>
    public double BlurbHighMin { get; init; } = 60.0;
    /// <summary>同・この値以上なら「中」、未満は「低」。</summary>
    public double BlurbMidMin { get; init; } = 40.0;
    /// <summary>寮が距離の減点を打ち消す割合（オーナー決定8「寮は距離の減点を打ち消す効果として残る」）。</summary>
    public double DormDistanceRelief { get; init; } = 0.6;

    // --- 訪問費用[万円]（部費から。距離依存は今どおり） ---
    public double VisitCostSamePrefecture { get; init; } = 5.0;
    public double VisitCostSameRegion { get; init; } = 10.0;
    public double VisitCostNeighborRegion { get; init; } = 20.0;
    public double VisitCostFar { get; init; } = 30.0;

    // --- 争奪戦（今どおり） ---
    public double RivalEventProb { get; init; } = 0.22;
    public int RivalEventTierStep { get; init; } = 1;

    // --- 訪問の手応え（成功率を4段の言葉へ丸める境目。数値そのものは監督に見せない） ---
    /// <summary>成功率がこの値以上なら「反応は薄い」、未満は「心ここにあらず」。</summary>
    public double ResponseLukewarmMin { get; init; } = 0.25;
    /// <summary>同・この値以上なら「悪くない反応」。</summary>
    public double ResponseWarmMin { get; init; } = 0.45;
    /// <summary>同・この値以上なら「目を輝かせる」。</summary>
    public double ResponseEagerMin { get; init; } = 0.65;

    // --- 入部の条件（約束）。設計書04 §2.4 ---
    /// <summary>訪問1回あたりに条件を出してくる確率（候補1人につき一度きり＝たまに）。</summary>
    public double PromiseAskProb { get; init; } = 0.15;
    /// <summary>約束したときの成功率の上乗せ（訪問2回ぶん）。</summary>
    public double PromiseBonus { get; init; } = 0.18;
    /// <summary>進路希望に合う条件の重み。</summary>
    public double PromiseWeightFit { get; init; } = 3.0;
    /// <summary>進路希望と関係ない条件の重み。</summary>
    public double PromiseWeightNeutral { get; init; } = 1.0;
    /// <summary>「主将にしてほしい」を言い出す統率傾向の下限。</summary>
    public int PromiseCaptainLeadershipMin { get; init; } = 70;
    /// <summary>約束を破ったときの信頼度の下げ幅（1回だけ）。</summary>
    public double PromiseBrokenTrust { get; init; } = 5.0;
    /// <summary>約束を破ったときの名声の下げ幅（1回だけ）。</summary>
    public double PromiseBrokenFame { get; init; } = 5.0;
    /// <summary>約束を守り切ったときの信頼度の上げ幅。</summary>
    public double PromiseKeptTrust { get; init; } = 2.0;

    // --- 進路希望の分布（重みの比。指定なし＝等分が初期値・要調整） ---
    public double PreferencePrestigeWeight { get; init; } = 1.0;
    public double PreferencePlayingTimeWeight { get; init; } = 1.0;
    public double PreferenceLocalWeight { get; init; } = 1.0;

    // --- 段ごとの見立てノイズσ（寸評の軸選び・ランク表示の両方に使う。段を上げるほど小さい＝よく当たる） ---
    public double ReportNoiseSdTier1 { get; init; } = 18.0;
    public double ReportNoiseSdTier2 { get; init; } = 10.0;
    public double ReportNoiseSdTier3 { get; init; } = 4.0;
}

/// <summary>
/// 候補1人ぶんの見立て（<see cref="ScoutModel.BuildCandidates"/> の時点で凍結＝契約段を後から上げても
/// 「もう見ているリスト」の見え方は変わらない。既存の受動枠と同じ約束・設計書04 §2.3 と同じ流儀）。
///
/// <para>隠し素質（成長タイプ・伸びしろ・怪物）はここに<b>一切含めない</b>（オーナー決定2「入部後の
/// 育成眼・気づきの楽しみに残す」）。</para>
/// </summary>
public sealed class ScoutReport
{
    /// <summary>生成時のスカウト網の段（1〜3）。0=無し（スカウト自体が起きないので実際には現れない）。</summary>
    public int Tier { get; set; }

    /// <summary>寸評の軸（"batting"/"mobility"/"defense"/"pitching"）。文言は data/scout-blurbs.yaml 側。</summary>
    public string BlurbAxis { get; set; } = "";
    /// <summary>寸評の水準バケツ（"high"/"mid"/"low"）。段が低いほど実際の水準と食い違いうる。</summary>
    public string BlurbBucket { get; set; } = "";

    // Lv2以上で意味を持つ4分野（0〜100・見立てノイズ入り）。
    public double Batting { get; set; }
    public double Mobility { get; set; }
    public double Defense { get; set; }
    public double Pitching { get; set; }

    // Lv3で意味を持つ個別能力（見立てノイズ入り・1〜99スケール）。
    public double Contact { get; set; }
    public double Power { get; set; }
    public double Discipline { get; set; }
    public double Speed { get; set; }
    public double Fielding { get; set; }
    public double Catching { get; set; }
    public double ThrowAccuracy { get; set; }
    public double Baserunning { get; set; }
    public double Velocity { get; set; }
    public double Control { get; set; }
    public double PitchRank { get; set; }
    public double Stamina { get; set; }
}

/// <summary>
/// スカウト候補1人（本人＝合流すればそのまま入部する <see cref="DevelopingPlayer"/>）。
/// </summary>
public sealed class ScoutCandidate
{
    /// <summary>リスト内の安定した並び順（0始まり）。セーブと画面の対応キー。</summary>
    public int Index { get; set; }

    /// <summary>本人（合流時にそのまま入部予定者へ入る）。</summary>
    public DevelopingPlayer Player { get; set; } = new();

    /// <summary>出身県（JIS順0基点）。</summary>
    public int PrefectureId { get; set; }

    /// <summary>自校から見た遠さ。</summary>
    public RecruitDistance Distance { get; set; }

    /// <summary>競合校の格。訪問で争奪戦が起きると上がる。</summary>
    public Tier RivalTier { get; set; } = Tier.G;

    /// <summary>これまでの訪問回数。</summary>
    public int Visits { get; set; }

    /// <summary>勧誘対象に指定しているか（枠を消費する）。</summary>
    public bool Targeted { get; set; }

    /// <summary>3月の判定を済ませたか。</summary>
    public bool Decided { get; set; }

    /// <summary>判定の結果（<see cref="Decided"/> が true のときだけ意味を持つ）。</summary>
    public bool Succeeded { get; set; }

    /// <summary>進路希望（設計書04 §2・オーナー決定6）。生成時に1つだけ割り当てる。</summary>
    public CareerPathPreference Preference { get; set; }

    /// <summary>見立て（生成時に凍結）。</summary>
    public ScoutReport Report { get; set; } = new();

    /// <summary>最後に訪問したときの手応え（未訪問は <see cref="VisitResponse.None"/>）。</summary>
    public VisitResponse Response { get; set; }

    /// <summary>訪問のとき出してきた入部の条件（出していなければ None。一度きり）。</summary>
    public ScoutPromiseKind Promise { get; set; }

    /// <summary>その条件を約束したか（断った・答えていないは false）。</summary>
    public bool Promised { get; set; }
}

/// <summary>
/// 訪問の手応え（設計書04 §2.4）。訪問した時点の成功率を4段に丸めたもので、入部の見込み（%）の代わりに
/// 監督へ返す唯一の感触。並びは弱い→強い（比較できる）。
/// </summary>
public enum VisitResponse
{
    /// <summary>まだ訪問していない。</summary>
    None,
    /// <summary>心ここにあらず。</summary>
    Cold,
    /// <summary>反応は薄い。</summary>
    Lukewarm,
    /// <summary>悪くない反応。</summary>
    Warm,
    /// <summary>目を輝かせている。</summary>
    Eager,
}

/// <summary>訪問1回の結果（<see cref="ScoutModel.TryVisit"/>）。</summary>
public enum ScoutVisitOutcome
{
    /// <summary>訪問できた（争奪戦は起きなかった）。</summary>
    Visited,
    /// <summary>訪問できたが、競合校が動いた。</summary>
    VisitedWithRivalMove,
    /// <summary>訪問できない時期、または決着済み・候補なし。</summary>
    OutOfSeason,
    /// <summary>枠が埋まっている。</summary>
    SlotsFull,
    /// <summary>部費が足りない。</summary>
    NotEnoughFunds,
}

/// <summary>
/// スカウトの進行状態（1年ぶん）。新チーム発足後に探す分野を決め、秋の大会後（<see cref="SeasonCalendar.RecruitListWeek"/>）
/// にこの状態を作り、3月の確定週（<see cref="SeasonCalendar.ProspectConfirmWeek"/>）の判定で使い切る。
/// </summary>
public sealed class ScoutingState
{
    /// <summary>このリストが対象にしている「合流する年度」。</summary>
    public int YearIndex { get; set; }

    /// <summary>探す分野（生成時に焼く＝後から変えてもこのリストの傾きは変わらない）。</summary>
    public ScoutField Field { get; set; }

    /// <summary>解禁されている枠数（スカウト網の契約段から。0＝候補が1人も居ない）。</summary>
    public int Slots { get; set; }

    /// <summary>候補リスト（生成順＝安定順）。</summary>
    public List<ScoutCandidate> Candidates { get; set; } = new();

    /// <summary>3月の判定を済ませたか（冪等ガード）。</summary>
    public bool Resolved { get; set; }

    /// <summary>いま対象に指定している人数。</summary>
    public int TargetedCount
    {
        get
        {
            var n = 0;
            foreach (var c in Candidates) if (c.Targeted) n++;
            return n;
        }
    }
}

/// <summary>
/// スカウト（設計書04 §2・issue #867）。候補生成・寸評・入部成否の純ロジック。
///
/// <para>乱数はすべて注入（不変条件#2）。呼び出し側は<b>受動枠の生成列とは別の Fork ストリーム</b>を渡すこと＝
/// スカウトを何回試しても受動枠の新入生は1ビットも動かない（旧 <c>Recruiting_DoesNotDisturbPassiveIntake</c> と
/// 同じ守り）。候補は受動枠と<b>同じ <see cref="ProspectGenerator.Intake"/></b> で作るので、合流しても
/// 「スカウト組だけ別物」にならない。</para>
/// </summary>
public static class ScoutModel
{
    /// <summary>契約段→解禁される枠数（<see cref="Career.ContractTier.ScoutSlots"/> と同じ対応・冗長参照用）。</summary>
    public static int SlotsForLevel(int level, Career.ContractCatalog catalog)
    {
        var def = catalog.Find("scouting");
        if (def == null) return 0;
        var lv = Math.Clamp(level, 0, def.MaxLevel);
        return lv <= 0 ? 0 : def.Tiers[lv].ScoutSlots;
    }

    /// <summary>
    /// 候補リストを作る。<paramref name="level"/> はスカウト網の契約段（0〜3）＝枠数と見立ての段を兼ねる。
    /// <paramref name="talentCenter"/> は受動枠と同じ中心値で、ここに候補の上振れ分（固定＋契約段の質加算）を足す。
    /// </summary>
    public static ScoutingState BuildCandidates(
        int yearIndex, ScoutField field, int level, int slots, int homePrefectureId, Tier homeTier,
        double talentCenter, double contractQualityAdd, RosterCoefficients roster, ScoutCoefficients c,
        TeamStrengthCoefficients strengthCoeff, IRandomSource rng,
        SkillCoefficients? skills = null, PersonalityCoefficients? personalities = null, FormCoefficients? form = null)
    {
        var state = new ScoutingState { YearIndex = yearIndex, Field = field, Slots = Math.Max(0, slots) };
        if (state.Slots <= 0) return state;

        var listSize = Math.Max(1, state.Slots * Math.Max(1, c.CandidatesPerSlot));
        var oversample = field == ScoutField.Omakase ? 1.0 : Math.Max(1.0, c.FieldBiasOversample);
        var poolSize = (int)Math.Ceiling(listSize * oversample);
        var center = talentCenter + c.CandidateTalentCenterAdd + contractQualityAdd;

        var pool = ProspectGenerator.Intake(
            yearIndex, roster, rng, skills: skills, personalities: personalities, form: form,
            talentCenter: center, fixedCount: poolSize);

        List<DevelopingPlayer> chosen;
        if (field == ScoutField.Omakase || pool.Count <= listSize)
        {
            chosen = pool.Take(listSize).ToList();
        }
        else
        {
            // 分野に強い候補ほど選ばれやすくする（ラベルは付けない＝能力の傾きだけ）。ジッタを乗せて
            // 「毎回機械的に同じ上位が選ばれる」を避ける。
            var jitterRng = rng.Fork(SelectStreamId(yearIndex));
            chosen = pool
                .Select((p, i) => (Player: p, Index: i,
                    Score: AxisValue(field, PlayerStrengthProfile.Compute(p, strengthCoeff))
                           + jitterRng.NextGaussian(0, c.FieldBiasJitterSd)))
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Index)   // 同点は生成順で決定論
                .Take(listSize)
                .Select(x => x.Player)
                .ToList();
        }

        for (var i = 0; i < chosen.Count; i++)
        {
            var p = chosen[i];
            // 怪物（issue #1468・「スカウトで見つける」）: 候補1人ずつ専用Forkで引く＝ほかの抽選（県・競合・進路・見立て）は
            // 1ビットも動かない。名声と関係ない。見立て（寸評・ランク）は怪物の能力をそのまま（ノイズ越しに）映すが、
            // 怪物という隠し素質そのものはどの段でも見せない（§2.4「隠し素質は見せない」）。
            {
                var phenomRng = rng.Fork(PhenomStreamId(yearIndex, i));
                if (phenomRng.NextDouble() < roster.Phenom.ScoutCandidateRate)
                {
                    // 怪物の帯（PhenomCoefficients）は今の物差しの値＝物理Lvへ戻して当て、表示Lvへ戻す（issue #1080）。
                    DevelopingScale.ToPhysical(p);
                    ProspectPhenom.Apply(p, ProspectPhenom.RollTypeFor(p, roster, phenomRng), roster.Phenom, phenomRng);
                    DevelopingScale.ToDisplay(p);
                }
            }
            var prefectureId = RollPrefecture(homePrefectureId, c, rng);
            var cand = new ScoutCandidate
            {
                Index = i,
                Player = p,
                PrefectureId = prefectureId,
                Distance = PrefectureRegions.DistanceBetween(homePrefectureId, prefectureId),
                RivalTier = RollRivalTier(homeTier, c, rng),
                Preference = RollPreference(c, rng),
            };
            cand.Report = BuildReport(p, level, strengthCoeff, c, rng.Fork(ReportStreamId(yearIndex, i)));
            state.Candidates.Add(cand);
        }
        return state;
    }

    /// <summary>
    /// 出場機会志向の「空き」0〜1（設計書04 §2・オーナー決定6「その子より強い部員が少ないほど来る」）。
    /// 候補の最良4軸と、現ロスターの各部員の最良4軸を比べ、候補より弱い部員の割合を返す
    /// （1.0＝自分が今のロスターの誰よりも強い＝出場機会が大きい）。乱数を使わない純関数。
    /// </summary>
    public static double OpportunityScore(
        DevelopingPlayer candidate, IReadOnlyList<DevelopingPlayer> roster, TeamStrengthCoefficients tc)
    {
        if (roster == null || roster.Count == 0) return 1.0;
        var candidateBest = Best(PlayerStrengthProfile.Compute(candidate, tc));
        var weaker = 0;
        foreach (var r in roster)
            if (Best(PlayerStrengthProfile.Compute(r, tc)) < candidateBest) weaker++;
        return (double)weaker / roster.Count;
    }

    private static double Best(PlayerStrength s) => Math.Max(Math.Max(s.Batting, s.Mobility), Math.Max(s.Defense, s.Pitching));

    /// <summary>
    /// いまの成功率（設計書04 §2・オーナー決定6）。進路希望の種類だけが自分の相性項を持ち、
    /// 距離の減点（寮で緩和）・競合校の格・訪問は全員に共通で効く。
    /// </summary>
    public static double SuccessProbability(
        ScoutCandidate p, double fame, Tier homeTier, bool hasDorm, double opportunity, ScoutCoefficients c)
    {
        var prob = c.BaseProbability;
        switch (p.Preference)
        {
            case CareerPathPreference.Prestige:
                prob += c.ProbPerFame * Math.Max(0.0, fame);
                break;
            case CareerPathPreference.PlayingTime:
                prob += c.ProbPerOpportunity * MathUtil.Clamp(opportunity, 0.0, 1.0);
                break;
            case CareerPathPreference.Local:
                prob += c.ProbPerLocal * Closeness(p.Distance, c);
                break;
        }

        var penalty = DistancePenalty(p.Distance, c);
        if (hasDorm) penalty *= 1.0 - MathUtil.Clamp(c.DormDistanceRelief, 0.0, 1.0);
        prob -= penalty;

        var gap = TierRank(p.RivalTier) - TierRank(homeTier);
        if (gap > 0) prob -= c.ProbPerRivalTier * gap;

        prob += Math.Min(c.VisitGainMax, c.ProbPerVisit * Math.Max(0, p.Visits));

        // 入部の条件を約束した子は来やすい（断っても下げない）。
        if (p.Promised) prob += c.PromiseBonus;

        return MathUtil.Clamp(prob, c.ProbabilityMin, c.ProbabilityMax);
    }

    /// <summary>成功率を手応えの4段へ丸める（境目は <see cref="ScoutCoefficients"/>）。</summary>
    public static VisitResponse ResponseOf(double probability, ScoutCoefficients c)
        => probability >= c.ResponseEagerMin ? VisitResponse.Eager
            : probability >= c.ResponseWarmMin ? VisitResponse.Warm
            : probability >= c.ResponseLukewarmMin ? VisitResponse.Lukewarm
            : VisitResponse.Cold;

    /// <summary>
    /// 訪問が成立した直後に呼び、その時点の成功率（3月の判定と同じ式）から手応えを候補へ焼く。
    /// 乱数は使わない＝スカウト網の段によるノイズも乗らない（監督が自分で会った感触）。
    /// 3月までに名声・部員・競合校が動けば、最終の成否とはずれうる。
    /// </summary>
    public static VisitResponse UpdateResponse(
        ScoutCandidate p, double fame, Tier homeTier, bool hasDorm,
        IReadOnlyList<DevelopingPlayer> roster, TeamStrengthCoefficients tc, ScoutCoefficients c)
    {
        var opportunity = OpportunityScore(p.Player, roster, tc);
        p.Response = ResponseOf(SuccessProbability(p, fame, homeTier, hasDorm, opportunity, c), c);
        return p.Response;
    }

    /// <summary>訪問1回の費用[万円]（距離で決まる。部費から出る）。</summary>
    public static double VisitCost(RecruitDistance d, ScoutCoefficients c) => d switch
    {
        RecruitDistance.SamePrefecture => c.VisitCostSamePrefecture,
        RecruitDistance.SameRegion => c.VisitCostSameRegion,
        RecruitDistance.NeighborRegion => c.VisitCostNeighborRegion,
        _ => c.VisitCostFar,
    };

    /// <summary>1回訪問する。訪問回数を進め、確率で争奪戦（競合校の格が上がる）を起こす。</summary>
    /// <returns>争奪戦が起きたら true。</returns>
    public static bool Visit(ScoutCandidate p, ScoutCoefficients c, IRandomSource rng)
    {
        p.Visits++;
        if (!MathUtil.Chance(c.RivalEventProb, rng)) return false;
        var rank = TierRank(p.RivalTier) + Math.Max(0, c.RivalEventTierStep);
        p.RivalTier = TierOfRank(Math.Min(rank, TierRank(Tier.S)));
        return true;
    }

    /// <summary>
    /// 訪問の乱数ストリームの鍵。合流年度・候補の並び順・その候補への訪問済み回数から決まる＝
    /// 連番カウンタ（セーブに載らない・訪問の順番で結果が変わる）を持たず、年度ごとに別の列になり、
    /// 保存/読み直しでも同じ訪問は同じ結果になる（不変条件#2）。呼び出し側が母種と混ぜる。
    /// </summary>
    public static ulong VisitStreamKey(int joinYearIndex, int candidateIndex, int visitsSoFar)
        => ((ulong)(uint)joinYearIndex << 40) ^ ((ulong)(uint)candidateIndex << 20) ^ (ulong)(uint)visitsSoFar;

    /// <summary>
    /// 1回訪問を試みる。<b>払えない・枠が無い・時期でないときは状態を一切変えない</b>（対象の印も付けない）。
    /// まだ対象でなければ、枠に空きがある限り自動で対象にする。費用は返すだけで、引き落としは呼び出し側が持つ
    /// （engine は金庫を持たない）。
    /// </summary>
    /// <param name="seedBase">訪問ストリームの母種（呼び出し側が全体シードと混ぜて渡す）。</param>
    /// <param name="cost">訪問が成立したときの費用[万円]。成立しなければ 0。</param>
    public static ScoutVisitOutcome TryVisit(
        ScoutingState? state, int index, bool inSeason, double funds, ScoutCoefficients c, ulong seedBase, out double cost)
    {
        cost = 0.0;
        if (state == null || state.Resolved || !inSeason || index < 0 || index >= state.Candidates.Count)
            return ScoutVisitOutcome.OutOfSeason;

        var p = state.Candidates[index];
        if (!p.Targeted && state.TargetedCount >= state.Slots) return ScoutVisitOutcome.SlotsFull;

        var price = VisitCost(p.Distance, c);
        if (funds < price) return ScoutVisitOutcome.NotEnoughFunds;

        p.Targeted = true;
        cost = price;
        var rng = new Xoshiro256Random(seedBase ^ VisitStreamKey(state.YearIndex, index, p.Visits));
        return Visit(p, c, rng) ? ScoutVisitOutcome.VisitedWithRivalMove : ScoutVisitOutcome.Visited;
    }

    /// <summary>
    /// 3月の判定。対象にしている候補だけ成否を引く。冪等（<see cref="ScoutingState.Resolved"/> が立っていれば何もしない）。
    /// </summary>
    /// <returns>入部に成功した候補（リスト順）。</returns>
    public static IReadOnlyList<ScoutCandidate> Resolve(
        ScoutingState state, double fame, Tier homeTier, bool hasDorm,
        IReadOnlyList<DevelopingPlayer> roster, TeamStrengthCoefficients tc, ScoutCoefficients c, IRandomSource rng)
    {
        var won = new List<ScoutCandidate>();
        if (state.Resolved) return won;

        foreach (var p in state.Candidates)
        {
            if (!p.Targeted) continue;
            var opportunity = OpportunityScore(p.Player, roster, tc);
            var prob = SuccessProbability(p, fame, homeTier, hasDorm, opportunity, c);
            p.Decided = true;
            p.Succeeded = MathUtil.Chance(prob, rng);
            if (!p.Succeeded) continue;
            won.Add(p);
            // 交わした約束は本人に持たせる＝入部後もずっと本人について回る。
            if (p.Promised && p.Promise != ScoutPromiseKind.None)
            {
                p.Player.Promise = p.Promise;
                p.Player.PromiseStatus = ScoutPromiseStatus.Active;
            }
        }
        state.Resolved = true;
        return won;
    }

    // --- 見立て（寸評・ランク） ---

    private static ScoutReport BuildReport(
        DevelopingPlayer p, int level, TeamStrengthCoefficients strengthCoeff, ScoutCoefficients c, IRandomSource rng)
    {
        var tier = Math.Clamp(level, 1, 3);
        var sd = tier switch { 3 => c.ReportNoiseSdTier3, 2 => c.ReportNoiseSdTier2, _ => c.ReportNoiseSdTier1 };
        var s = PlayerStrengthProfile.Compute(p, strengthCoeff);

        double N(double v) => MathUtil.Clamp(v + rng.NextGaussian(0, sd), 0.0, 100.0);
        var batting = N(s.Batting);
        var mobility = N(s.Mobility);
        var defense = N(s.Defense);
        var pitching = N(s.Pitching);
        var (axis, bucket) = PickBlurb(batting, mobility, defense, pitching, c);

        var report = new ScoutReport { Tier = tier, BlurbAxis = axis, BlurbBucket = bucket };
        if (tier < 2) return report;

        report.Batting = batting;
        report.Mobility = mobility;
        report.Defense = defense;
        report.Pitching = pitching;
        if (tier < 3) return report;

        double NL(AbilityKind k) => MathUtil.Clamp(p.Level(k) + rng.NextGaussian(0, sd), 1.0, 99.0);
        report.Contact = NL(AbilityKind.Contact);
        report.Power = NL(AbilityKind.Power);
        report.Discipline = NL(AbilityKind.Discipline);
        report.Speed = NL(AbilityKind.Speed);
        report.Fielding = NL(AbilityKind.Fielding);
        report.Catching = NL(AbilityKind.Catching);
        report.ThrowAccuracy = NL(AbilityKind.ThrowAccuracy);
        report.Baserunning = NL(AbilityKind.Baserunning);
        report.Velocity = NL(AbilityKind.Velocity);
        report.Control = NL(AbilityKind.Control);
        report.PitchRank = NL(AbilityKind.PitchRank);
        report.Stamina = NL(AbilityKind.Stamina);
        return report;
    }

    /// <summary>寸評の軸＝ノイズ入り4軸の最大値。水準バケツは高/中/低の3段（文言は data/scout-blurbs.yaml）。</summary>
    internal static (string Axis, string Bucket) PickBlurb(
        double batting, double mobility, double defense, double pitching, ScoutCoefficients c)
    {
        var items = new[] { ("batting", batting), ("mobility", mobility), ("defense", defense), ("pitching", pitching) };
        var top = items.OrderByDescending(x => x.Item2).First();
        var bucket = top.Item2 >= c.BlurbHighMin ? "high" : top.Item2 >= c.BlurbMidMin ? "mid" : "low";
        return (top.Item1, bucket);
    }

    /// <summary>
    /// 候補の寸評の文言を <paramref name="phrases"/>（data/scout-blurbs.yaml）から引く。置き場
    /// （<c>scout.blurb.&lt;軸&gt;.&lt;水準&gt;</c>）内の複数候補から、候補ごとに決定論で1文を選ぶ
    /// （<c>string.GetHashCode</c> は実行ごとに変わるので使わない＝不変条件#2）。
    /// </summary>
    public static string BlurbText(ScoutCandidate candidate, ScoutBlurbPhrases phrases)
    {
        var key = "scout.blurb." + candidate.Report.BlurbAxis + "." + candidate.Report.BlurbBucket;
        var pool = phrases.Pool(key);
        if (pool.Count == 0) return "";
        var salt = Fnv1a(candidate.Player.Name + ":" + candidate.Index);
        return pool[(int)(salt % (ulong)pool.Count)];
    }

    private static ulong Fnv1a(string s)
    {
        var h = 14695981039346656037UL;
        foreach (var ch in s) { h ^= ch; h *= 1099511628211UL; }
        return h;
    }

    private static double AxisValue(ScoutField field, PlayerStrength s) => field switch
    {
        ScoutField.Pitching => s.Pitching,
        ScoutField.Batting => s.Batting,
        ScoutField.Defense => (s.Defense + s.Mobility) / 2.0,
        _ => 0.0,
    };

    // --- 補助 ---

    private static double DistancePenalty(RecruitDistance d, ScoutCoefficients c) => d switch
    {
        RecruitDistance.SamePrefecture => 0.0,
        RecruitDistance.SameRegion => c.PenaltySameRegion,
        RecruitDistance.NeighborRegion => c.PenaltyNeighborRegion,
        _ => c.PenaltyFar,
    };

    /// <summary>地元志向の相性項に使う「近さ」0〜1（同県=1.0・遠方=0.0）。</summary>
    public static double Closeness(RecruitDistance d, ScoutCoefficients c) => d switch
    {
        RecruitDistance.SamePrefecture => 1.0,
        RecruitDistance.SameRegion => c.ClosenessSameRegion,
        RecruitDistance.NeighborRegion => c.ClosenessNeighborRegion,
        _ => 0.0,
    };

    internal static int RollPrefecture(int homePrefectureId, ScoutCoefficients c, IRandomSource rng)
    {
        if (!MathUtil.Chance(MathUtil.Clamp(c.OutOfPrefectureRatio, 0.0, 1.0), rng)) return homePrefectureId;
        var count = PrefectureNames.Japanese.Count;
        if (homePrefectureId < 0 || homePrefectureId >= count) return rng.NextInt(0, count);
        // 自県を除く count-1 県から一様に引く（自県の次の県だけ確率2倍になるのを避ける）。
        var id = rng.NextInt(0, count - 1);
        return id >= homePrefectureId ? id + 1 : id;
    }

    private static CareerPathPreference RollPreference(ScoutCoefficients c, IRandomSource rng)
    {
        var wp = Math.Max(0.0, c.PreferencePrestigeWeight);
        var wt = Math.Max(0.0, c.PreferencePlayingTimeWeight);
        var wl = Math.Max(0.0, c.PreferenceLocalWeight);
        var total = wp + wt + wl;
        if (total <= 0.0) return CareerPathPreference.Prestige;
        var roll = rng.NextDouble() * total;
        if (roll < wp) return CareerPathPreference.Prestige;
        return roll < wp + wt ? CareerPathPreference.PlayingTime : CareerPathPreference.Local;
    }

    private static Tier RollRivalTier(Tier homeTier, ScoutCoefficients c, IRandomSource rng)
    {
        var roll = rng.NextDouble();
        var delta = roll < c.RivalTierSameCutoff ? 0
            : roll < c.RivalTierPlus1Cutoff ? 1
            : roll < c.RivalTierPlus2Cutoff ? 2
            : roll < c.RivalTierPlus3Cutoff ? 3
            : -1;
        var rank = MathUtil.Clamp(TierRank(homeTier) + delta, 0, TierRank(Tier.S));
        return TierOfRank((int)rank);
    }

    private static readonly Tier[] Ladder = { Tier.G, Tier.F, Tier.E, Tier.D, Tier.C, Tier.B, Tier.A, Tier.S };

    public static int TierRank(Tier t)
    {
        for (var i = 0; i < Ladder.Length; i++) if (Ladder[i] == t) return i;
        return 0;
    }

    private static Tier TierOfRank(int rank) => Ladder[(int)MathUtil.Clamp(rank, 0, Ladder.Length - 1)];

    /// <summary>候補の怪物ロール（issue #1468）の Fork ソルト（合流年度×候補の並び順・他ストリームと別の上位ビット）。</summary>
    private static ulong PhenomStreamId(int yearIndex, int index)
        => 0x9E5C_0000_0000UL ^ ((ulong)(uint)yearIndex << 16) ^ (ulong)(uint)index;

    private static ulong SelectStreamId(int year) => 0xA1C0_0000UL ^ (ulong)year;
    private static ulong ReportStreamId(int year, int index) => 0xB2D0_0000UL ^ (ulong)(year * 1000 + index + 1);
}
