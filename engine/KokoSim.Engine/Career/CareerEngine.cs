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

        // 秋の大会フロー（設計書05 §1.5/§4）。prefTable と regionals を渡した時のみ併走（既定オフ＝挙動不変）。
        var autumnEnabled = prefTable is not null && regionals is not null;
        var senbatsuB = senbatsuBerths ?? new SenbatsuBerths();
        var districtPlan = prefTable is not null && prefFormats is not null
            ? DistrictPlan.Build(prefTable, prefFormats)
            : null;

        var nation = NationGenerator.Generate(vocab, nationCoeff, rng, districtPlan);
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
            var (reachedKoshien, nationalChampion, wins, upsetMatches) =
                SimulateSeason(nation, managerSchool, prefId, nationCoeff, rng);

            // 番狂わせ連動の名声デルタ（issue #170）。自校 vs 相手の Tier 格差で金星/取りこぼしを算出。
            var upsetFameDelta = FameUpsetModel.SeasonDelta(upsetMatches, managerSchool.Strength, c);

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

            // メタ更新。
            ApplyResults(manager, reachedKoshien, nationalChampion, wins, upsetFameDelta, c);
            manager.CareerYears++;
            manager.YearsAtSchool++;
            if (reachedKoshien) manager.KoshienAppearances++;

            // 監督成長イベント（設計書04 §1.1b: 節目の跳ね）。独立ストリームで既存の乱数列を乱さない。
            ManagerGrowthEvents.Yearly(manager, reachedKoshien, wins, rng.Fork(0xA6A6_0000UL ^ (ulong)year), mg);

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
                    prefId = rng.NextInt(0, nation.Prefectures.Count);
                    baseStrength = rng.NextGaussian(c.NewSchoolStrengthMean, c.NewSchoolStrengthSd);
                    manager.Trust = c.TrustReset; // 信頼度は転任でリセット
                    manager.YearsAtSchool = 0;
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

    private static (bool ReachedKoshien, bool NationalChampion, int Wins, IReadOnlyList<TrackedMatch> UpsetMatches) SimulateSeason(
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
        if (reachedKoshien)
        {
            managerWins += koshien.WinsBySchool.TryGetValue(managerSchool.Id, out var kw) ? kw : 0;
            nationalChampion = koshien.Champion.Id == managerSchool.Id;
            upsetMatches.AddRange(koshien.TrackedMatches);
        }

        return (reachedKoshien, nationalChampion, managerWins, upsetMatches);
    }

    /// <param name="contracts">
    /// 年契約の実効値（設計書04 §1.4a）。null なら従来どおり <see cref="CareerCoefficients.ScoutCost"/> を
    /// 固定で引く＝年契約を持たないキャリアシムは1ビットも変わらない（不変条件#2）。
    /// 非nullならスカウト費は年契約側（スカウト網 Lv1 が同額）に含まれるので二重には引かない。
    /// </param>
    internal static void ApplyResults(
        Manager m, bool reachedKoshien, bool nationalChampion, int wins, double upsetFameDelta, CareerCoefficients c,
        ContractEffects? contracts = null)
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

        // 名声（持ち越し）。FamePerWin は勝利一律の加算、upsetFameDelta は Tier 格差ぶんの金星/取りこぼし
        // （順当な結果には効かない＝二重計上でない, issue #170）。
        m.Fame = NextFame(m.Fame, reachedKoshien, nationalChampion, wins, upsetFameDelta, c);

        // 信頼度（校内）。
        var trust = m.Trust + c.TrustPerWin * wins + (reachedKoshien ? c.TrustKoshien : 0);
        if (wins == 0) trust -= c.TrustPoorSeasonPenalty;
        m.Trust = MathUtil.Clamp(trust, 0, 100);

        // 部費（運営費）。年間予算を加算し、合宿費と年契約の年額を差し引く（Issue #128・設計書04 §1.4a）。
        // 年契約を持たない経路（キャリアシム）は従来どおりスカウト費だけを固定で引く。
        // 資金は下限0で止める（借金しない）。
        m.Funds += c.AnnualBudgetBase + c.BudgetPerTrust * m.Trust;
        m.Funds -= c.SummerCampCost + c.WinterCampCost + (contracts?.AnnualCost ?? c.ScoutCost);
        if (m.Funds < 0) m.Funds = 0;

        // 施設基金（設計書04 §1.4）。学校の設備投資＋名声に比例する後援会・OB寄付が積み上がり、
        // 甲子園に出た年は一時金が乗る。支出は施設の購入だけ（FacilityPurchase）なのでここでは引かない。
        // 名声は上の更新後の値を使う＝「勝った年の寄付はその年から増える」。
        m.FacilityFund += c.FacilityFundSchoolBase
                          + c.FacilityFundDonationPerFame * m.Fame
                          + (reachedKoshien ? c.FacilityFundKoshienBonus : 0)
                          + (nationalChampion ? c.FacilityFundNationalTitleBonus : 0);
    }

    /// <summary>
    /// 名声の1年ぶんの更新（設計書04 §1.2・2026-08-26 案C+D）。純関数＝乱数を引かない。
    ///
    /// <para>形は「減衰＋上昇」だが、上昇側にだけ2段の抑えが入る:
    /// <list type="number">
    /// <item>逓減（案C）: 上昇量に <c>(1 − 名声/FameGainCeiling)</c> を掛ける。定常値が
    /// <c>100g/(20+g)</c> になり100へ張り付かない（強さ75の常勝校でも C 帯で落ち着く）。</item>
    /// <item>年間上限（案D）: <b>正味の</b>上昇量を <see cref="CareerCoefficients.FameSeasonRiseCap"/> で
    /// 頭打ちにする。県大会を制した年でも等級が1段ぶんずつしか動かない。</item>
    /// </list>
    /// どちらも下降側には効かない＝勝てなくなった王朝は従来どおり一世代で崩れる。</para>
    /// </summary>
    public static double NextFame(
        double fame, bool reachedKoshien, bool nationalChampion, int wins, double upsetFameDelta,
        CareerCoefficients c)
    {
        var gain = c.FamePerWin * wins
                   + (reachedKoshien ? c.FameKoshienAppearance : 0)
                   + (nationalChampion ? c.FameNationalChampion : 0)
                   + upsetFameDelta;

        // 逓減は上昇側だけ（負のデルタに掛けると「名声が高いほど取りこぼしの罰が軽い」になって逆）。
        if (gain > 0 && c.FameGainCeiling > 0)
            gain *= Math.Max(0, 1.0 - fame / c.FameGainCeiling);

        var next = fame * c.FameDecay + gain;
        // 正味の上昇の頭打ち。減衰で下がる年（next < fame）には効かない。
        if (next > fame + c.FameSeasonRiseCap) next = fame + c.FameSeasonRiseCap;
        return MathUtil.Clamp(next, 0, 100);
    }
}
