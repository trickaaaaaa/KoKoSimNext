using System;
using System.Collections.Generic;

namespace KokoSim.Engine.Season;

/// <summary>育成式の係数（設計書02 §5.1, YAML駆動）。</summary>
public sealed record TrainingCoefficients
{
    /// <summary>
    /// 主効果の練習基礎値[exp/週]。2026-08-15 に 85→130（案D。中核能力の3年間の伸び +4〜5 → +7）。
    /// 2026-11-12（issue #865・監督決定）: 130→290。施設なし・お任せ練習の3年の伸びを +7 → +16.4 にする改定。
    /// <see cref="LevelUpGrowth"/> は意図的に変更していない（Mental/Lead/Baserunning の実戦成長や個別指導の
    /// 必要exp曲線を共有しており、そこを寝かせると関係のない系統まで一緒に加速してしまう）。
    /// 入学時の下げ（<see cref="RosterCoefficients.IntakeDrop"/>：練習で伸びる能力だけ）とセットで、定常のチーム総合は
    /// 改定前と ±0.8 以内（名声0/30/60/90 実測）。
    /// </summary>
    public double BaseMainExp { get; init; } = 290.0;
    /// <summary>副効果の倍率（メニュー×能力の個別指定が無いときの既定）。</summary>
    public double SubFactor { get; init; } = 0.4;

    /// <summary>
    /// 副効果の倍率の個別指定（メニュー×能力）。載っていない組は <see cref="SubFactor"/>。
    /// 既定は守備練習→捕球 0.7（issue #1409・オーナー決定 2026-11-22。捕球は失策の起きやすさを決めるのに
    /// 伸ばす手段が守備練習の副効果しか無く、0.4 では守備+10に対し捕球+4程度だった。守備の主効果は変えない）。
    /// data/coefficients.yaml training.sub_factor_overrides と一致させる。
    /// </summary>
    public IReadOnlyList<SubFactorOverride> SubFactorOverrides { get; init; } = DefaultSubFactorOverrides();

    private static IReadOnlyList<SubFactorOverride> DefaultSubFactorOverrides() => new SubFactorOverride[]
    {
        new() { Menu = TrainingMenu.Defense, Ability = AbilityKind.Catching, Factor = 0.7 },
    };

    /// <summary>メニュー×能力の副効果倍率。個別指定があればそれ、無ければ <see cref="SubFactor"/>。</summary>
    public double SubFactorFor(TrainingMenu menu, AbilityKind ability)
    {
        foreach (var o in SubFactorOverrides)
            if (o.Menu == menu && o.Ability == ability) return o.Factor;
        return SubFactor;
    }

    /// <summary>施設係数（施設レベル0＝現状の基準値）。<see cref="FacilityTiers"/> 未指定時の既定施設係数。</summary>
    public double FacilityCoef { get; init; } = 1.0;
    /// <summary>
    /// 基準指導力（Issue #115）。監督メタ（<see cref="CoachingProfile"/>）を注入しない場合に全分野へ適用する既定値、
    /// かつ分野別指導力の「中立点」＝この指導力なら従来のシムと1ビット一致する（不変条件#2）。
    /// 監督指導力を注入すると各能力分野が Manager.Coaching.* で駆動される。
    /// </summary>
    public double CoachingLevel { get; init; } = 20.0;
    /// <summary>指導力1あたりの効率係数（設計書02: 1 + 指導力×0.005）。</summary>
    public double CoachingSlope { get; init; } = 0.005;

    /// <summary>
    /// 個別指導3枠のスロット数上限（Issue #126・設計書06 §3.3）。エンジン側では強制しない
    /// （UI側の <c>TrainingPlanState.ToggleNominate</c> がこの値を上限として使う）。
    /// </summary>
    public int IndividualCoachingSlots { get; init; } = 3;

    /// <summary>
    /// 個別指導3枠の基礎倍率（issue #693）。指名選手の主効果expに、監督の指導力に関係なく掛かる分。
    /// 旧式（#126）は「指導力の比−1」だけだったので、新任監督（指導力20＝中立点）ではちょうど1.0＝
    /// 指名しても何も起きなかった。新任でも効果が体感できるよう、ここを床にする。
    /// 追加倍率 = この値 + (coachingFactor比 − 1) × <see cref="IndividualCoachingBonusScale"/>（下限1.0）。
    /// </summary>
    public double IndividualCoachingBaseMultiplier { get; init; } = 1.5;

    /// <summary>
    /// 個別指導3枠の指導力スケール（Issue #126・#693）。分野別指導力由来のcoachingFactor比
    /// （Issue #115・<see cref="DevelopmentModel"/> の中立点比）が1を超えた分に掛ける。
    /// 既定3.0＝名将（指導力90・素直さ1.0）で比1.32 → 追加倍率 ≈ 2.45（2026-10-28 監督決定「名将で×2.5程度」）。
    /// 基礎倍率1.0・スケール0で個別指導の効果を無効化できる。
    /// </summary>
    public double IndividualCoachingBonusScale { get; init; } = 3.0;

    /// <summary>
    /// 個別指導の効果3「信頼関係」（issue #702・設計書03 §3.3）。指名中の週ごとに精神力へ足すexp。
    /// 成長段階・合宿・大会の倍率は掛けない（体でなく関係が育つ）。怪我中も入る（練習に出なくても目はかけている）。
    /// 必要expは能力と同じ曲線（精神力55で約865）＝1年指名し続けて +5 前後（2026-10-31 監督決定）。0で無効。
    /// </summary>
    public double IndividualCoachingMentalExp { get; init; } = 90.0;

    /// <summary>
    /// 施設レベル→(係数, 週練習時間) 対応表（Issue #115・設計書03 §4）。index=施設レベル。
    /// 既定は data/coefficients.yaml training.facility_tiers と一致（Lv0-4, issue #402）。Lv0＝現状値(1.0/300)で
    /// SeasonContext.FacilityLevel=0（既定）のとき従来と1ビット一致する。多軸施設は <see cref="FacilityCatalog"/> が担い、
    /// この単一軸表は <c>SeasonEngine.FacilityLevel &gt; 0</c> かつ多軸未注入のときのフォールバックとして効く。
    /// </summary>
    public IReadOnlyList<FacilityTier> FacilityTiers { get; init; } = DefaultFacilityTiers();

    /// <summary>data/coefficients.yaml training.facility_tiers と一致する既定表（Lv0-4, issue #402）。</summary>
    private static IReadOnlyList<FacilityTier> DefaultFacilityTiers() => new FacilityTier[]
    {
        new() { Coef = 1.00, BudgetMinutes = 300 }, // Lv0 施設なし（現状・従来一致）
        new() { Coef = 1.12, BudgetMinutes = 335 }, // Lv1
        new() { Coef = 1.25, BudgetMinutes = 370 }, // Lv2
        new() { Coef = 1.37, BudgetMinutes = 405 }, // Lv3
        new() { Coef = 1.49, BudgetMinutes = 440 }, // Lv4 フル施設
    };

    /// <summary>
    /// レベルアップ必要exp = LevelUpBase × LevelUpGrowth^v（設計書02 §5.1）。
    /// Growth は 2026-08-15 に 1.05→1.04（案D）。1.05 だと Lv74 の必要expが Lv32 の7.8倍で高レベル帯の成長が
    /// ほぼ止まり、名声で入る B/A の新入生が「入学時が完成形」になる。1.04 で Lv74 でも3年 +2〜3（施設フル +10）。
    /// 2026-11-10（issue #865）: 検討したが変更していない。この曲線は <see cref="MatchGrowthModel"/>（精神力/
    /// 捕手リード/走塁判断の実戦成長）・個別指導の信頼関係exp・守備位置適性にも共有されており、ここを
    /// 寝かせると「育成式の伸びを強める」という本issueの範囲を越えてそれら全部が一緒に速くなる
    /// （実測: 1.017へ緩めただけで3年のリード成長が旧期待値レンジの2倍超に膨らんだ）。<see cref="BaseMainExp"/>
    /// 側だけで +15〜20 を作る（6倍弱の引き上げで足りる・才能上限には遠い。out/overnight/issue-865 実測）。
    /// </summary>
    public double LevelUpBase { get; init; } = 100.0;
    public double LevelUpGrowth { get; init; } = 1.04;

    /// <summary>
    /// 能力別 trainability（伸ばしやすさ）係数（Issue #114）。exp加算時に乗算する内在特性。
    /// 既定（全1.0）で従来と完全一致。
    /// </summary>
    public TrainabilityCoefficients Trainability { get; init; } = new();

    /// <summary>
    /// 守備位置適性の必要exp倍率（&lt;1.0で速く伸びる, 守備適性 未決1・2026-07-22）。
    /// 能力レベルの必要exp（LevelUpBase×LevelUpGrowth^v）にこれを乗じて適性専用に緩める。
    /// 既定は data/coefficients.yaml training.aptitude_required_exp_factor と一致（issue #402）。
    /// </summary>
    public double AptitudeRequiredExpFactor { get; init; } = 0.6;

    /// <summary>合宿の経験値倍率（設計書04 §4: 夏×2.0 / 冬×2.5）。</summary>
    public double SummerCampMult { get; init; } = 2.0;
    public double WinterCampMult { get; init; } = 2.5;

    /// <summary>
    /// 大会モード中の練習効果倍率（&lt;1.0で低下）。大会期間は試合中心で練習量が落ちることを表す。
    /// 呼び出し側が合宿倍率と同様に common へ乗算する（大会モード時のみ適用, 通常週は1.0）。
    /// </summary>
    public double TournamentPracticeMult { get; init; } = 0.5;

    /// <summary>
    /// 基準週の練習時間[分]（設計書03 §3.1）。1メニューにこの分を配分すると BaseMainExp と一致する
    /// （時間配分育成の後方互換の基準）。成長は「配分分 ÷ この値」で線形スケールする（疲労は廃止済み）。
    /// </summary>
    public int ReferenceWeekMinutes { get; init; } = 300;

    /// <summary>
    /// 施設0（デフォルト校）の週あたり練習時間[分]。照明・寮などの施設導入でこれを増やす（設計書03 §4）。
    /// 現状は基準週と同値。将来 School/施設Lv から算出して SeasonContext.BudgetMinutes に注入する。
    /// </summary>
    public int DefaultBudgetMinutes { get; init; } = 300;

    // === 実戦成長（設計書02 §5.3a, Q8・2026-07-20）: 精神力・走塁判断・捕手リードは試合出場でのみ伸びる ===
    /// <summary>1試合出場あたりの精神力exp（成長段階係数を乗算）。</summary>
    public double MatchMentalExp { get; init; } = 260.0;
    /// <summary>1試合の捕手出場あたりのリードexp（成長段階係数を乗算）。</summary>
    public double MatchLeadExp { get; init; } = 400.0;
    /// <summary>1試合出場あたりの走塁判断exp（成長段階係数を乗算, AbilityKind.Baserunning へ）。</summary>
    public double MatchBaserunningExp { get; init; } = 300.0;
    /// <summary>主将に選ばれた本人の実戦成長（精神力・走塁判断・捕手リード）のexp倍率（設計書09 §8.1「重責が人を育てる」・やや促進）。部員全体には掛からない。1.0＝効果なし。</summary>
    public double CaptainMatchGrowthFactor { get; init; } = 1.25;

    // === 成功体験ボーナス（設計書02 §5.3b・issue #1487）: 好打・好投・好守の試合で該当能力に少量のexp ===
    // 量は練習の主効果（BaseMainExp=290/週）の1割前後＝練習を上回らない。大会の格で倍率が上がる（練習試合は1.0）。
    /// <summary>好打の試合で ミート/パワー それぞれに入るexp（成長段階係数・大会の格を乗算）。</summary>
    public double MatchSuccessBatExp { get; init; } = 40.0;
    /// <summary>好投の試合で 制球/キレ/スタミナ それぞれに入るexp。</summary>
    public double MatchSuccessPitchExp { get; init; } = 30.0;
    /// <summary>好守の試合で 守備/捕球/送球精度 それぞれに入るexp。</summary>
    public double MatchSuccessFieldExp { get; init; } = 30.0;
    /// <summary>好打の目安: 安打数・打点（本塁打1本以上も好打）。</summary>
    public int MatchSuccessMinHits { get; init; } = 2;
    public int MatchSuccessMinRbi { get; init; } = 2;
    /// <summary>好投の目安: アウト数以上かつ失点以下、または奪三振以上。</summary>
    public int MatchSuccessMinOuts { get; init; } = 15;
    public int MatchSuccessMaxRuns { get; init; } = 3;
    public int MatchSuccessMinStrikeOuts { get; init; } = 7;
    /// <summary>好守の目安: 守備に就いた野手が無失策で、チーム全体の失策もこの数以下（既定0＝無失策試合）。</summary>
    public int MatchSuccessMaxTeamErrors { get; init; } = 0;
    /// <summary>大会の格による倍率（大舞台ほど大きい）。練習試合は1.0。</summary>
    public double MatchSuccessMultPractice { get; init; } = 1.0;
    public double MatchSuccessMultPrefectural { get; init; } = 1.5;
    public double MatchSuccessMultRegional { get; init; } = 2.0;
    public double MatchSuccessMultKoshien { get; init; } = 3.0;

    // === 今日のMVP（設計書02 §5.3c・issue #1745）: 公式戦に勝った試合の自校でいちばん活躍した1人 ===
    // 活躍の点数＝勝利への貢献を同じ物差しにした簡単な式。打者と投手の両方をした選手は合算。
    /// <summary>安打1本の点。</summary>
    public double MvpHit { get; init; } = 1.0;
    /// <summary>長打（二塁打・三塁打・本塁打）1本に足す点。</summary>
    public double MvpExtraBaseHit { get; init; } = 1.0;
    /// <summary>本塁打1本に足す点（長打の点に上乗せ）。</summary>
    public double MvpHomeRun { get; init; } = 2.0;
    public double MvpRbi { get; init; } = 1.0;
    public double MvpRun { get; init; } = 0.5;
    public double MvpStolenBase { get; init; } = 0.5;
    /// <summary>投球1回（3アウト）の点。1/3回は1/3。</summary>
    public double MvpInning { get; init; } = 1.0;
    public double MvpStrikeOut { get; init; } = 0.3;
    /// <summary>失点1点の減点。</summary>
    public double MvpRunAllowed { get; init; } = 1.0;
    /// <summary>完投（自校の投手が1人だけ）の上乗せ。</summary>
    public double MvpCompleteGame { get; init; } = 1.0;
    /// <summary>完封（完投で相手の得点0）の上乗せ（完投の上乗せに足す）。</summary>
    public double MvpShutout { get; init; } = 1.0;
    /// <summary>MVPのご褒美の大きさ＝成功体験ボーナスの何倍の経験値を活躍した分野へ足すか（同じ大会の格の倍率・同じ経路）。</summary>
    public double MvpRewardMult { get; init; } = 3.0;

    /// <summary>成功体験ボーナスの大会の格倍率（null＝練習試合）。</summary>
    public double MatchSuccessStageMult(TournamentKind? kind) => kind switch
    {
        null => MatchSuccessMultPractice,
        TournamentKind.SummerKoshien or TournamentKind.SenbatsuKoshien => MatchSuccessMultKoshien,
        TournamentKind.Regional or TournamentKind.SpringRegional or TournamentKind.Jingu => MatchSuccessMultRegional,
        _ => MatchSuccessMultPrefectural,
    };

    // === 投手センス→投手系能力の伸びやすさ（設計書01 §1.3・issue #1472） ===
    // 投手系（球速/制球/スタミナ/キレ/球威）の経験値にだけ掛ける。打撃の才能（伸びしろ・総合力）とは独立に
    // 投手センスで決まる＝野手として平凡でも投手センスが高い選手は、投手の練習を振ると大きく伸びる（投手転向で覚醒）。
    /// <summary>倍率1.0になる投手センス（物理Lv＝表示Lv＋Δ）。既定の新入生（talent_center_default 32）の投手役の平均（実測30.8）。</summary>
    public double PitcherSenseGrowthNeutral { get; init; } = 30.0;
    /// <summary>投手センス1あたりの倍率の傾き（オーナー判断 2026-11-23：0.012→0.03・倍率0.5〜1.0＝上乗せをやめ、センスが低い選手だけ伸びにくくする）。</summary>
    public double PitcherSenseGrowthSlope { get; init; } = 0.03;
    /// <summary>倍率の下限（投手センスが極端に低くても投手系がまったく伸びなくはしない）。</summary>
    public double PitcherSenseGrowthMin { get; init; } = 0.5;
    /// <summary>倍率の上限（オーナー判断 2026-11-23：2.0→1.2→1.0＝上乗せしない。2.0 だと豪腕＋名将＋施設フルの3年で +31.9km/h、1.2 でも +29.6km/h）。</summary>
    public double PitcherSenseGrowthMax { get; init; } = 1.0;

    /// <summary>
    /// 見出された選手（外部コーチ・<see cref="PitcherTalentStage.Discovered"/>）の投手系の経験値倍率（issue #1523）。
    /// 通常の倍率（上限 <see cref="PitcherSenseGrowthMax"/>）の上限が外れ、これに置き換わる。見出されるのは見える投手能力が
    /// 投手センスに追いついていない選手だけ＝すでに強い投手には起きず、強い投手の伸びの目標（3年 +15〜20km/h・テスト上限 +28）は動かない。
    /// </summary>
    public double PitcherAwakenedGrowthFactor { get; init; } = 2.0;

    /// <summary>
    /// 投手センス（表示Lv）→投手系能力の経験値倍率。物差しは今の物差し（表示Lv＋Δ）で読む（issue #1080）。
    /// <paramref name="discovered"/>＝外部コーチに見出された選手（issue #1523）は <see cref="PitcherAwakenedGrowthFactor"/> を下限に持つ。
    /// </summary>
    public double PitcherSenseGrowthFactor(int pitcherSenseDisplay, bool discovered = false)
    {
        var f = 1.0 + (Players.AbilityScale.Phys(pitcherSenseDisplay) - PitcherSenseGrowthNeutral) * PitcherSenseGrowthSlope;
        var normal = Math.Max(PitcherSenseGrowthMin, Math.Min(PitcherSenseGrowthMax, f));
        return discovered ? Math.Max(normal, PitcherAwakenedGrowthFactor) : normal;
    }

    public double RequiredExp(int level) => LevelUpBase * Math.Pow(LevelUpGrowth, level);
}

/// <summary>副効果の倍率の個別指定1件（issue #1409）。</summary>
public sealed record SubFactorOverride
{
    public TrainingMenu Menu { get; init; }
    public AbilityKind Ability { get; init; }
    public double Factor { get; init; }
}
