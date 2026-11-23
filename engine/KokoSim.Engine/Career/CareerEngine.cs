using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using NationState = KokoSim.Engine.Nation.Nation;

namespace KokoSim.Engine.Career;

/// <summary>
/// 1年のキャリア記録。ReachedJingu/ReachedSenbatsu は秋の大会フロー有効時のみ意味を持つ
/// （監督校が地区優勝で明治神宮に出たか・翌春センバツに選ばれたか, 設計書05 §1.5/§4）。
/// </summary>
public sealed record CareerYear(
    int Year,
    int Prefecture,
    ManagerStatus Status,
    double AverageCoaching,
    double Fame,
    double Trust,
    bool ReachedKoshien,
    bool NationalChampion,
    int Wins,
    bool Transferred,
    bool ReachedJingu = false,
    bool ReachedSenbatsu = false);

/// <summary>キャリア全体の記録。</summary>
public sealed record CareerTimeline(IReadOnlyList<CareerYear> Years)
{
    public int SchoolsServed { get; init; }
    public int? YearBecameFree { get; init; }
    public int KoshienAppearances { get; init; }
    public int NationalTitles { get; init; }
    /// <summary>翌春センバツ出場回数（秋の大会フロー有効時のみ）。</summary>
    public int SenbatsuAppearances { get; init; }
}

/// <summary>
/// 監督キャリアエンジン（設計書04 §1.3）。教員監督として弱小校を渡り歩き（修行編）、
/// 甲子園出場/名声でフリー資格を得て本拠地を定める、というキャリア曲線を自動再現する。決定論。
/// </summary>
public static class CareerEngine
{
    private const int ManagerSchoolId = int.MaxValue - 1;

    public static CareerTimeline Run(
        int years, Manager manager, SchoolNameVocab vocab,
        NationCoefficients nationCoeff, CareerCoefficients c, IRandomSource rng,
        ManagerGrowthCoefficients? growthCoeff = null,
        PrefectureTable? prefTable = null,
        RegionalFormatSet? regionals = null,
        SenbatsuBerths? senbatsuBerths = null,
        IReadOnlyDictionary<string, PrefFormat>? prefFormats = null,
        int startYear = 2025)
    {
        var mg = growthCoeff ?? new ManagerGrowthCoefficients();
        var newGameContracts = ContractCatalog.Default.Aggregate(ContractSet.Initial());

        // 秋の大会フロー（設計書05 §1.5/§4）。prefTable と regionals を渡した時のみ併走（既定オフ＝挙動不変）。
        var autumnEnabled = prefTable is not null && regionals is not null;
        var senbatsuB = senbatsuBerths ?? new SenbatsuBerths();
        var districtPlan = prefTable is not null && prefFormats is not null
            ? DistrictPlan.Build(prefTable, prefFormats)
            : null;
        var districtWeights = prefTable is not null && prefFormats is not null
            ? DistrictPlan.BuildWeights(prefTable, prefFormats)
            : null;

        var nation = NationGenerator.Generate(vocab, nationCoeff, rng, districtPlan, districtWeights);
        var timeline = new List<CareerYear>(years);

        var prefId = rng.NextInt(0, nation.Prefectures.Count);
        var baseStrength = rng.NextGaussian(c.NewSchoolStrengthMean, c.NewSchoolStrengthSd);
        var schoolsServed = 1;
        int? yearBecameFree = null;

        for (var year = 1; year <= years; year++)
        {
            // 監督の指導力で赴任校を強化（Id は固定, 県は当年の赴任先）。
            var managerSchool = new School
            {
                Id = ManagerSchoolId,
                Name = "監督赴任校",
                PrefectureId = prefId,
                Strength = MathUtil.Clamp(
                    baseStrength + (manager.AverageCoaching - 20) * c.CoachingToStrength,
                    nationCoeff.StrengthMin, nationCoeff.StrengthMax),
            };

            // 全国大会（47県予選→甲子園）。監督校を自県に加える。
            var (reachedKoshien, nationalChampion, wins, koshienWins, upsetMatches) =
                SimulateSeason(nation, managerSchool, prefId, nationCoeff, rng);

            // 番狂わせ連動の名声デルタ（issue #170）。自校 vs 相手の Tier 格差で金星/取りこぼしを算出。
            var (upsetFameGain, upsetFameLoss) = FameUpsetModel.SeasonSplit(upsetMatches, managerSchool.Strength, c);

            // 秋の大会フロー（設計書05 §1.5/§4）。監督校を全校に加えた一時全国で回す。
            // 独立ストリーム(Fork)＝夏の予選・監督成長・AI進化の乱数列を乱さない（既存キャリアの決定論を保存）。
            var reachedJingu = false;
            var reachedSenbatsu = false;
            if (autumnEnabled)
            {
                var autumnNation = new NationState(
                    nation.Prefectures, nation.Schools.Append(managerSchool).ToList());
                var calYear = startYear + (year - 1);
                var af = AutumnFlowEngine.Run(
                    autumnNation, prefTable!, regionals!, senbatsuB, nationCoeff, calYear,
                    rng.Fork(0xAB77_0000UL ^ (ulong)year), prefFormats);
                reachedJingu = af.JinguField.Any(s => s.Id == managerSchool.Id);
                reachedSenbatsu = af.SenbatsuField.Any(s => s.Id == managerSchool.Id);
            }

            // メタ更新＋監督成長イベント（設計書04 §1.1/§1.1b）。実ゲームの年度替わりと同じ ManagerSeasonUpdate を通す
            // （手書きの重複を持たない・issue #1222）。節目イベントは独立ストリームで既存の乱数列を乱さない。
            // 新規ゲームは年契約を何も持たない＝スカウト費は引かない（実ゲームの ContractSet.Initial と同じ）。
            ManagerSeasonUpdate.Apply(manager, reachedKoshien, nationalChampion, wins,
                rng.Fork(0xA6A6_0000UL ^ (ulong)year), c, mg, upsetFameGain, upsetFameLoss, newGameContracts,
                nationalStageWins: koshienWins);

            var transferred = false;

            // フリー化資格（設計書04 §1.3）。
            if (manager.Status == ManagerStatus.Teacher &&
                (manager.KoshienAppearances >= c.FreeKoshienThreshold || manager.Fame >= c.FreeFameThreshold))
            {
                manager.Status = ManagerStatus.Free;
                yearBecameFree = year;
                // 本拠地を定める（好条件校へ）。
                baseStrength = c.FreeChoiceSchoolStrength;
                schoolsServed++;
                transferred = true;
                manager.YearsAtSchool = 0;
                manager.Funds *= c.TransferFundsCarryOverRatio;   // 部費は一部だけ持ち越す（issue #1082）
            }
            // 最低在任年数（設計書04 §1.3追記）。赴任した年に採った1年生が卒業する（3年）までは
            // 強制転任の抽選そのものを行わない＝必ず残留（ManagerTransferModel.Evaluate と同じ判定式）。
            else if (manager.Status == ManagerStatus.Teacher && manager.YearsAtSchool >= c.MinTenureYears)
            {
                // 残留判定（設計書04 §1.3）。信頼度が高いほど校が引き留める＝腰を据えて育てられる。
                // 信頼が積み上がるまでは配置転換で渡り歩く「修行編」、信頼を得れば数年腰を据える、という手触り。
                // RetainTrustThreshold を残留確率の中点、RetainProbability を高信頼時の上限確率として使う
                // （ロジスティック）。信頼度は転任時のみリセットするので、残留する限り年々積み上がる。
                var z = (manager.Trust - c.RetainTrustThreshold) / c.RetainTrustSpread;
                var retainChance = c.RetainProbability / (1.0 + Math.Exp(-z));
                var retained = rng.NextDouble() < retainChance;
                if (!retained)
                {
                    // 教員の強制転任は同一県内の異動（実ゲームの ManagerTransferModel と同じ・issue #1222）。
                    // prefId は据え置き＝赴任先の県は変えず、新しい学校の強さだけ引き直す。
                    baseStrength = rng.NextGaussian(c.NewSchoolStrengthMean, c.NewSchoolStrengthSd);
                    manager.Trust = c.TrustReset; // 信頼度は転任でリセット
                    manager.YearsAtSchool = 0;
                    manager.Funds *= c.TransferFundsCarryOverRatio;   // 部費は一部だけ持ち越す（issue #1082）
                    schoolsServed++;
                    transferred = true;
                }
            }
            // フリー監督は残留（本拠地で王朝を築く）。

            timeline.Add(new CareerYear(
                year, prefId, manager.Status, manager.AverageCoaching,
                manager.Fame, manager.Trust, reachedKoshien, nationalChampion, wins, transferred,
                reachedJingu, reachedSenbatsu));

            // 周囲のAI校も進化させ勢力図を生かす。
            AiSchoolModel.Evolve(nation, new Dictionary<int, int>(), -1, nationCoeff, rng);
        }

        return new CareerTimeline(timeline)
        {
            SchoolsServed = schoolsServed,
            YearBecameFree = yearBecameFree,
            KoshienAppearances = manager.KoshienAppearances,
            NationalTitles = timeline.Count(y => y.NationalChampion),
            SenbatsuAppearances = timeline.Count(y => y.ReachedSenbatsu),
        };
    }

    private static (bool ReachedKoshien, bool NationalChampion, int Wins, int KoshienWins, IReadOnlyList<TrackedMatch> UpsetMatches) SimulateSeason(
        NationState nation, School managerSchool, int prefId, NationCoefficients coeff, IRandomSource rng)
    {
        var reps = new List<School>(49);
        var managerWins = 0;
        var reachedKoshien = false;
        // 監督校が戦った試合の格差記録（金星/取りこぼしの名声算出用, issue #170）。
        var upsetMatches = new List<TrackedMatch>();

        // 夏の地方大会（49地方=北海道・東京だけ2分割, 設計書05 §1.1 / issue #65）。監督校は所属区画にのみ加える。
        foreach (var region in SummerRegions.Build(nation.Prefectures))
        {
            var entrants = SummerRegions.Entrants(nation, region).ToList();
            var managerInRegion = region.Contains(managerSchool);
            if (managerInRegion) entrants.Add(managerSchool);
            if (entrants.Count == 0) continue;

            var result = TournamentEngine.Run(
                entrants, coeff, rng, trackSchoolId: managerInRegion ? managerSchool.Id : null);
            if (managerInRegion)
            {
                managerWins += result.WinsBySchool.TryGetValue(managerSchool.Id, out var w) ? w : 0;
                reachedKoshien = result.Champion.Id == managerSchool.Id;
                upsetMatches.AddRange(result.TrackedMatches);
            }
            reps.Add(result.Champion);
        }

        // 甲子園。
        var koshien = TournamentEngine.Run(
            reps, coeff, rng, trackSchoolId: reachedKoshien ? managerSchool.Id : null);
        var nationalChampion = false;
        var koshienWins = 0;
        if (reachedKoshien)
        {
            koshienWins = koshien.WinsBySchool.TryGetValue(managerSchool.Id, out var kw) ? kw : 0;
            managerWins += koshienWins;
            nationalChampion = koshien.Champion.Id == managerSchool.Id;
            upsetMatches.AddRange(koshien.TrackedMatches);
        }

        return (reachedKoshien, nationalChampion, managerWins, koshienWins, upsetMatches);
    }

    /// <summary>部費の年間収入[万円]＝学校予算＋信頼度ぶん。経営タブ・年契約の更新の見込みと総括が同じ式を使う。</summary>
    public static double AnnualBudgetIncome(double trust, CareerCoefficients c)
        => c.AnnualBudgetBase + c.BudgetPerTrust * trust;

    /// <summary>部費の年間固定支出[万円]＝合宿（夏冬）＋年契約の年額。</summary>
    public static double AnnualFixedExpense(double contractCost, CareerCoefficients c)
        => c.SummerCampCost + c.WinterCampCost + contractCost;

    /// <summary>
    /// 年度末に部費へ入る寄付・一時金[万円]＝学校の設備投資＋後援会寄付（名声比例）＋甲子園出場・全国優勝の一時金
    /// （設計書04 §1.4・issue #1082 で施設基金から部費へ統合）。総括と経営タブの見込みが同じ式を使う。
    /// </summary>
    public static double AnnualDonationIncome(double fame, bool reachedKoshien, bool nationalChampion, CareerCoefficients c)
        => c.SchoolInvestment
           + c.BoosterDonationPerFame * fame
           + (reachedKoshien ? c.KoshienYearBonus : 0)
           + (nationalChampion ? c.NationalTitleBonus : 0);

    /// <summary>
    /// 大会報奨[万円]（設計書04 §1.4・issue #1469 T1）＝公式戦1勝ごと <see cref="CareerCoefficients.PrizePerOfficialWin"/>、
    /// そのうち甲子園・センバツの勝ちは <see cref="CareerCoefficients.PrizePerNationalStageWin"/>。年度末の総括で入る。
    /// 勝ちは実ゲームの数え方（<c>SchoolRecord.OfficialWins</c>／<c>KoshienWins</c> の年差分）をそのまま使う。
    /// </summary>
    /// <param name="officialWins">この年度の公式戦の勝ち（甲子園・センバツの勝ちを含む）。</param>
    /// <param name="nationalStageWins">そのうち甲子園・センバツの勝ち。</param>
    public static double TournamentPrizeIncome(int officialWins, int nationalStageWins, CareerCoefficients c)
    {
        var national = Math.Clamp(nationalStageWins, 0, Math.Max(0, officialWins));
        return c.PrizePerOfficialWin * (Math.Max(0, officialWins) - national) + c.PrizePerNationalStageWin * national;
    }

    /// <summary>
    /// プロ入りしたOBからの一時金[万円]（設計書04 §1.4・issue #1469 O2）。教え子が指名された年にだけ入る
    /// （積み上がらない＝名門の収入が雪だるまにならない）。順位 1／2〜3／4〜7／8（下位・育成）。指名なし（0）は0。
    /// </summary>
    public static double ProDraftDonation(int round, CareerCoefficients c) => round switch
    {
        <= 0 => 0,
        1 => c.ProDonationFirstRound,
        <= 3 => c.ProDonationUpperRound,
        <= 7 => c.ProDonationMiddleRound,
        _ => c.ProDonationLowerRound,
    };

    /// <summary>
    /// 教え子のプロ入りで上がる名声（逓減の前・設計書04 §1.2・issue #1270 D1）。1人 <see cref="CareerCoefficients.FamePerDraftPick"/>
    /// （1位は <see cref="CareerCoefficients.FameFirstRoundPick"/>）、合計は年 <see cref="CareerCoefficients.FameDraftSeasonCap"/> まで。
    /// 年度末の総括（<see cref="NextFame"/> の上昇側）に入れる＝逓減と年上限を通す（名声→新入生→指名→名声の正のループを強めない）。
    /// </summary>
    /// <param name="rounds">この年度に指名された教え子の順位（1始まり。0以下は数えない）。</param>
    public static double DraftFameGain(IEnumerable<int> rounds, CareerCoefficients c)
    {
        var sum = 0.0;
        foreach (var r in rounds)
            if (r >= 1) sum += r == 1 ? c.FameFirstRoundPick : c.FamePerDraftPick;
        return Math.Min(sum, Math.Max(0, c.FameDraftSeasonCap));
    }

    /// <summary>
    /// 部費の年間差引[万円]（予算＋寄付−固定支出）。経営タブの「年間差引」。甲子園・全国優勝の一時金と
    /// 大会報奨（<see cref="TournamentPrizeIncome"/>）は結果しだいなので見込みに入れない。信頼度・名声は総括前の値＝見込み。
    /// </summary>
    public static double AnnualFundsNet(double trust, double fame, double contractCost, CareerCoefficients c)
        => AnnualBudgetIncome(trust, c) + AnnualDonationIncome(fame, false, false, c) - AnnualFixedExpense(contractCost, c);

    /// <summary>総括のあとの部費の見込み[万円]（総括と同じく0で止める）。信頼度・名声は総括前の値＝見込み。</summary>
    public static double ProjectedFunds(double funds, double trust, double fame, double contractCost, CareerCoefficients c)
        => Math.Max(0, funds + AnnualFundsNet(trust, fame, contractCost, c));

    /// <param name="contracts">
    /// 年契約の実効値（設計書04 §1.4a）。null なら従来どおり <see cref="CareerCoefficients.ScoutCost"/> を
    /// 固定で引く＝年契約を持たないキャリアシムは1ビットも変わらない（不変条件#2）。
    /// 非nullならスカウト費は年契約側（スカウト網 Lv1 が同額）に含まれるので二重には引かない。
    /// </param>
    internal static void ApplyResults(
        Manager m, bool reachedKoshien, bool nationalChampion, int wins, double upsetFameGain, double upsetFameLoss,
        CareerCoefficients c, ContractEffects? contracts = null, int nationalStageWins = 0, double draftFameGain = 0)
    {
        // 指導力成長（采配経験, 設計書04 §1.1）。上限近傍で減衰する漸近成長にする
        // （「気づいたら練れていた」の意図に沿った緩やかなカーブ。線形だと十数年で99へ急上昇し
        //  終盤の伸びしろが無くなる＝エンドゲームの単調化を招くため、残り余地に比例させる）。
        var baseGrowth = c.CoachingGrowthPerYear + c.CoachingGrowthPerWin * wins;
        double Grow(double current, double factor)
        {
            var headroom = (c.CoachingCap - current) / c.CoachingCap; // 1(未熟)→0(上限)
            var amount = baseGrowth * factor * Math.Max(0, headroom);
            return Math.Min(c.CoachingCap, current + amount);
        }
        m.CoachingBatting = Grow(m.CoachingBatting, 1.0);
        m.CoachingPitching = Grow(m.CoachingPitching, 1.0);
        m.CoachingDefense = Grow(m.CoachingDefense, 1.0);
        m.TacticalSense = Grow(m.TacticalSense, 0.7);
        m.TalentEye = Grow(m.TalentEye, 0.5);

        // 名声（持ち越し）。FamePerWin は勝利一律の加算、upsetFameGain/Loss は Tier 格差ぶんの金星/取りこぼし
        // （順当な結果には効かない＝二重計上でない, issue #170）。
        m.Fame = NextFame(m.Fame, reachedKoshien, nationalChampion, wins, upsetFameGain, upsetFameLoss, c, draftFameGain);

        // 信頼度（校内）。
        var trust = m.Trust + c.TrustPerWin * wins + (reachedKoshien ? c.TrustKoshien : 0);
        if (wins == 0) trust -= c.TrustPoorSeasonPenalty;
        m.Trust = MathUtil.Clamp(trust, 0, 100);

        // 部費（設計書04 §1.4・issue #1082 で財布は1つ）。年間予算と寄付・一時金を加算し、
        // 合宿費と年契約の年額を差し引く（Issue #128・設計書04 §1.4a）。
        // 年契約を持たない経路（キャリアシム）は従来どおりスカウト費だけを固定で引く。
        // 寄付の名声は上の更新後の値を使う＝「勝った年の寄付はその年から増える」。
        // 資金は下限0で止める（借金しない）。
        m.Funds += AnnualBudgetIncome(m.Trust, c);
        m.Funds += AnnualDonationIncome(m.Fame, reachedKoshien, nationalChampion, c);
        m.Funds += TournamentPrizeIncome(wins, nationalStageWins, c);   // 大会報奨（issue #1469）
        m.Funds -= AnnualFixedExpense(contracts?.AnnualCost ?? c.ScoutCost, c);
        if (m.Funds < 0) m.Funds = 0;
    }

    /// <summary>
    /// 名声の1年ぶんの更新（設計書04 §1.2・2026-08-26 案C+D、issue #868 で維持を難しく）。純関数＝乱数を引かない。
    ///
    /// <para>形は「減衰＋上昇−取りこぼし」で、上昇側と取りこぼし側を<b>別枠</b>で扱う:
    /// <list type="number">
    /// <item>逓減（案C）: 上昇量（勝ち・甲子園・金星）にだけ <c>(1 − 名声/FameGainCeiling)</c> を掛ける。</item>
    /// <item>減衰: 名声 <see cref="CareerCoefficients.FameDecayHighThreshold"/> までは <see cref="CareerCoefficients.FameDecay"/>、
    /// それを超えた分には <see cref="CareerCoefficients.FameDecayHigh"/>＝名門ほど保つのに勝ち続けなければならない。</item>
    /// <item>年間上限（案D）: 上昇後の<b>正味の</b>上昇量を <see cref="CareerCoefficients.FameSeasonRiseCap"/> で頭打ち。</item>
    /// <item>取りこぼし: 上のあとに別枠で引く。<c>× (1 + 名声/FameUpsetLossFameScale)</c>＝名声が高いほど重い。
    /// 勝ちの加点と先に足し合わせると、合計が正のとき逓減で丸ごと縮んで罰が消えてしまう。</item>
    /// </list></para>
    /// </summary>
    /// <param name="upsetFameGain">金星の名声上昇（0以上・季上限適用済み）。上昇側として逓減を受ける。</param>
    /// <param name="upsetFameLoss">取りこぼしの名声低下（0以下）。逓減を受けず、名声に応じた倍率で引く。</param>
    /// <param name="draftFameGain">
    /// 教え子のプロ入りの名声（0以上・<see cref="DraftFameGain"/> で年上限を掛けた値。issue #1270 D1）。上昇側として逓減と年上限を受ける。
    /// </param>
    public static double NextFame(
        double fame, bool reachedKoshien, bool nationalChampion, int wins,
        double upsetFameGain, double upsetFameLoss, CareerCoefficients c, double draftFameGain = 0)
    {
        var gain = c.FamePerWin * wins
                   + (reachedKoshien ? c.FameKoshienAppearance : 0)
                   + (nationalChampion ? c.FameNationalChampion : 0)
                   + upsetFameGain
                   + Math.Max(0, draftFameGain);

        if (gain > 0 && c.FameGainCeiling > 0)
            gain *= Math.Max(0, 1.0 - fame / c.FameGainCeiling);

        var low = Math.Min(fame, c.FameDecayHighThreshold);
        var high = Math.Max(0, fame - c.FameDecayHighThreshold);
        var kept = low * c.FameDecay + high * c.FameDecayHigh;

        var next = kept + gain;
        // 正味の上昇の頭打ち。減衰で下がる年（next < fame）には効かない。
        if (next > fame + c.FameSeasonRiseCap) next = fame + c.FameSeasonRiseCap;

        if (upsetFameLoss < 0)
        {
            var weight = c.FameUpsetLossFameScale > 0 ? 1.0 + fame / c.FameUpsetLossFameScale : 1.0;
            next += upsetFameLoss * weight;
        }
        return MathUtil.Clamp(next, 0, 100);
    }
}
