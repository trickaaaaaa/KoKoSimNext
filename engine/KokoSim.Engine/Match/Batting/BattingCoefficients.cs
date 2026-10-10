namespace KokoSim.Engine.Match.Batting;

/// <summary>
/// 表示層→物理層の打撃系変換・確率係数（不変条件#1の集約点、YAML駆動）。
/// すべて初期値であり、ヘッドレス1万打席の統計回帰で調整する（不変条件#5）。
/// </summary>
public sealed record BattingCoefficients
{
    /// <summary>
    /// 表示能力→物理層の効き（設計書21・<see cref="KokoSim.Engine.Players.AbilityEffectCoefficients"/>）。正典は YAML の <c>ability_effect:</c> 1ブロックで、
    /// ローダが batting/fielding/baserunning の3ブロックへ<b>同じ値</b>を配る（解決式が手元の係数束から引けるようにするため）。
    /// 3つが食い違わないことは <c>AbilityEffectConsistencyTests</c> が固定する。
    /// </summary>
    public KokoSim.Engine.Players.AbilityEffectCoefficients AbilityEffect { get; init; } = new();

    // --- 配球（狙いの散らばり） ---
    /// <summary>投手が狙う位置のゾーン中心からの散布σ（横）[m]。</summary>
    public double AimSigmaXMeters { get; init; } = 0.10;
    /// <summary>投手が狙う位置のゾーン中心からの散布σ（縦）[m]。</summary>
    public double AimSigmaYMeters { get; init; } = 0.155;
    /// <summary>
    /// 制球の高い投手ほど狙いをゾーンの隅へ散らす（issue walk-rate-floor-vs-elite-pitcher）: 狙い散布σの倍率
    /// ＝ max(0.5, 1 + この値 × (Control−50))。従来は全員がゾーン中心を狙い、そこへ制球σだけが乗っていたので、
    /// 制球が上がるほどゾーン率が一方的に上がり（C80 で 68%・実物の好投手は 50% 前後）、A投手 vs F打者の四球率が
    /// 1% まで消えていた。好投手ほどコーナーを突く＝ゾーン際に散り、見逃しボールが増える。制球の低い投手は逆に
    /// 真ん中を狙う（隅を狙う余裕が無い）＝母集団の四球率（atbat_10k 帯 ≤10%）を動かさない対称形。C50 で恒等。
    /// </summary>
    ///
    /// <para>0.008＝実測（matchup grid・6,000打席×seed42/2024）: P80 vs B30 の BB% 1→3%・P90 vs B75 1→7%・P50 vs B45 は 8% のまま。
    /// K% は P80/B30 44→43%・P90/B30 46→45%（帯内）。母集団（atbat_10k）の BB% は 9.7/9.1→9.8/9.2 で帯（≤10%）内。
    /// 0.010 だと seed42 が 10.1% で天井を超え、0.017 だと P90 vs B75 が 17% まで膨らむ。</para>
    public double AimSpreadControlSlopePerPoint { get; init; } = 0.008;

    // --- 打者判断（スイング/見逃し） ---
    /// <summary>ゾーン内球へのスイング率の基準。</summary>
    public double ZoneSwingBase { get; init; } = 0.68;
    /// <summary>ゾーン外球への釣られ率（追いかけ）の基準。</summary>
    public double ChaseBase { get; init; } = 0.28;
    /// <summary>選球眼が追いかけ率を下げる強さ（Discipline−50 あたり）。</summary>
    public double ChaseDisciplineSlope { get; init; } = 0.0045;
    /// <summary>選球眼がゾーン内スイング率を上げる強さ。</summary>
    public double ZoneSwingDisciplineSlope { get; init; } = 0.0015;
    /// <summary>
    /// 誘発変化合成量[m]（設計書15 Phase E-3）あたりのスイング確率補正。単一係数を符号だけ反転して対称に効く
    /// （ゾーン外+＝釣られる、ゾーン内−＝見誤って見送る）。「見え方と実軌道のズレ」の代理指標。
    /// </summary>
    public double ChaseBreakSlope { get; init; } = 0.02;
    /// <summary>
    /// ゾーン外距離[m]（設計書15 Phase E-3, <see cref="KokoSim.Engine.Match.Field.StrikeZone.DistanceOutsideM"/>）
    /// あたりのスイング確率減衰。ChaseBreakSlope の一定加算が「大外れでも変化量だけで釣れる」不自然を作らないための
    /// ガード（ゾーン内は距離=0で常に無効）。
    /// </summary>
    public double ChaseDistanceSlope { get; init; } = 0.15;
    /// <summary>
    /// ゾーン内の縁への近さ（<see cref="KokoSim.Engine.Match.Field.StrikeZone.EdgeProximity"/>、無次元 [0,1]）
    /// あたりのスイング確率減衰（issue #403 案B）。中心=恒等・縁上でこの値ぶん見送る＝「隅のいい球は見逃されやすい」。
    /// 投手は隅を狙うため小さな値でも見逃しKが強く増える（0.02 で atbat_10k の K% 帯上限を 0.230 へ再校正済み）。
    /// </summary>
    public double ZoneEdgeSwingPenalty { get; init; } = 0.02;
    /// <summary>
    /// 2ストライク時（追い込まれ）の「プレートを守る」加点（設計書01 §2③「カウントで判定」・
    /// 見逃し三振率の調査）。<see cref="BatterDecision.SwingProbability"/> はカウントを一切見ない構造だったため、
    /// 見逃し三振が三振の6割超を占めていた（実測 issue looking-strikeout-rate）。ゾーン内外どちらにも
    /// 同一値を加点する対称係数（<see cref="ChaseBreakSlope"/> と同じ1変数原則）。Brain依存の外付けパッチ
    /// （<c>PitchTacticsTwoStrikeForceSwingProb</c>）ではなく判断そのものに組み込み、既定0.0は恒等
    /// （strikes未指定の既存呼び出しに影響しない）。
    /// </summary>
    public double TwoStrikeSwingBoost { get; init; } = 0.28;
    /// <summary>
    /// 3ボール0ストライク時のスイング確率への倍率 [0,1]（設計書01 §2③「カウントで判定」・見逃し三振率の調査）。
    /// カウント非依存の <see cref="ZoneSwingBase"/>/<see cref="ChaseBase"/> がそのまま3-0にも適用され、
    /// 実質的な「待て」がゼロだった（3-0でのスイング率が実測45%＝実野球のおよそ5〜7%を大きく上回る）。
    /// Brain依存の外付けパッチ（<c>PitchTacticsThreeZeroTakeProb</c>）ではなく判断そのものに組み込む。
    /// 既定1.0は恒等（strikes/balls未指定の既存呼び出しに影響しない）。
    /// </summary>
    public double ThreeZeroSwingFactor { get; init; } = 0.15;

    // --- コンタクト（空振り/ファウル/フェア） ---
    /// <summary>空振り率のロジスティック基準（球威=打者互角時の空振り対数オッズ）。</summary>
    public double WhiffIntercept { get; init; } = -2.52;
    /// <summary>ミートが空振りを下げる強さ。</summary>
    public double WhiffContactSlope { get; init; } = 0.030;
    /// <summary>ゾーン外スイング時の空振り加算（対数オッズ）。</summary>
    public double WhiffOutOfZonePenalty { get; init; } = 0.85;
    /// <summary>
    /// 誘発変化量の合成量[m]（設計書15 Phase E-2, <see cref="Pitching.PitchTrajectoryFeatures.BreakMagnitudeM"/>）
    /// あたりの空振り加算（対数オッズ）。球種ランク(PitchRank)は Sharpness→rpm→変化量の経路で既に弾道へ
    /// 織り込み済みのため、旧 StuffPerPitchRank（球種ランク直参照）を本項へ完全置換した（二重計上を避ける）。
    /// </summary>
    public double WhiffBreakSlope { get; init; } = 1.5;
    /// <summary>
    /// 投打差（球威 − ミート項）の対数オッズ寄与を頭打ちにする soft-cap（issue matchup-gap-saturation-k-rate）。
    /// z 寄与 = cap × tanh(d / cap)。互角近傍（|d| ≪ cap）は従来の線形と一致し、大差では ±cap で飽和する
    /// ＝A投手が格下打線から平均24三振・四球ゼロを取る「線形で飽和しない効き」を止める。0 以下＝無効（従来の線形）。
    /// 既定 1.2: P80(146km/h) vs B30 の d≈2.4 → 1.15、P50 vs B45（d≈0.15）は不変。平均同士（atbat_10k）は帯内のまま。
    /// </summary>
    public double WhiffMatchupCap { get; init; } = 1.5;

    // --- 打者の「慣れ」（設計書02 §2.3b・issue times-through-order-familiarity） ---
    // 同一投手との試合内の対戦回数（打席の頭で数える・継投でその投手に対して0から）が増えるほど
    // コンタクト（空振り対数オッズ）と選球（追いかけ率）が控えめに良くなる。個人スキル SecondTimeThrough
    // （打者一巡で投手側が落ちる）は一般則の上の個人差として残す。
    /// <summary>1打席あたりの空振り対数オッズ低下。既定 0.08 ＝ 3打席目（蓄積2）で −0.16 ≒ ミート+5相当。</summary>
    public double FamiliarityWhiffPerPa { get; init; } = 0.08;
    /// <summary>1打席あたりの追いかけ率（ゾーン外スイング確率）低下。</summary>
    public double FamiliarityChasePerPa { get; init; } = 0.008;
    /// <summary>蓄積の上限打席数（これ以上は慣れが進まない）。</summary>
    public int FamiliarityMaxPa { get; init; } = 3;
    /// <summary>
    /// 慣れを万能にしない（§2.3b）: 誘発変化合成量[m]が大きい球ほど慣れの効きを割り引く（1 − これ×変化量、下限0）。
    /// 既定 1.2 ＝ 変化量 0.4m の球で効きが 52%、0.8m 超では 0。制球で散らす投手ほど進みにくい表現は係数の器のみ（未接続）。
    /// </summary>
    public double FamiliarityBreakDiscountPerM { get; init; } = 1.2;
    /// <summary>
    /// コンタクトのうちファウルになる割合。方位角が±45°を越えて幾何的にファウルになる分は別枠で、
    /// issue #262 で打球方向が広がった結果その別枠が増えたため、ここを 0.40→0.35 に下げて総ファウル率を保つ。
    /// </summary>
    public double FoulShare { get; init; } = 0.35;

    // --- 球威（stuff）の算出 ---
    /// <summary>
    /// 基準球速[km/h]（この球速で stuff=0 寄与）。リーグ平均投手の平均リリース球速
    /// （最速 <see cref="Players.PitcherAttributes.LeagueAverage"/> 132km/h − 平均ドロップ4km/h）に置く。
    /// ここを母平均に合わせておくことで <see cref="StuffPerKmh"/> を振っても平均球威が動かない＝帯中立。
    /// </summary>
    public double StuffBaseVelocityKmh { get; init; } = 128.0;
    /// <summary>
    /// 球速1km/hあたりの stuff 寄与（対数オッズ）。球速Level→km/h の傾き 0.47 と掛けて
    /// <b>球速1レベル ≈ ミート1レベル</b>（<see cref="WhiffContactSlope"/>=0.030）になるよう定める
    /// （0.064×0.47≈0.030）。旧値0.020では球速1レベルがミート0.31レベル相当しかなかった。
    /// ⚠ 0.47 は Lv≦50 の傾き。Lv&gt;50 は <see cref="Players.PitcherAttributes.VelocitySlopeAboveKneeKmhPerLevel"/>=0.36
    /// （issue velocity-kmh-mapping-flatten-top）＝上位では球速Lv1 の空振り価値が 0.36/0.47 に下がる（物理は km/h のまま）。
    /// </summary>
    public double StuffPerKmh { get; init; } = 0.128;

    // --- 打球生成 ---
    /// <summary>
    /// 打球初速上限[km/h] の切片（パワー0相当）。設計書02は 100 + P×0.6 だが伸び不足のため係数化。
    /// issue homerun-rate-explodes-at-high-power（2026-08-18）: 旧 125+0.82P は P90 で 199 km/h（MLB全史最速級）・
    /// P70 で 182 と上位が急峻で、B/A校同士の試合が 5〜10 本塁打/試合になっていた。P50 の 166 を固定したまま
    /// 傾きを 0.50 へ寝かせる（141+0.50P: P30 156／P70 176／P90 186／P99 190.5）。P&lt;50 は +6 km/h 以内の差
    /// （P30 150→156）で弱小校の本塁打はほぼ動かない。傾き 0.40 以下は強豪同士の打率が .20 に落ちる（安打も
    /// 初速に依る）ので採らない。帯は本塁打率 1.4%／二塁打÷本塁打 3.5 へ再校正。
    /// </summary>
    public double ExitVeloInterceptKmh { get; init; } = 141.0;
    /// <summary>打球初速上限[km/h] のパワー1あたり係数。</summary>
    public double ExitVeloPerPower { get; init; } = 0.50;

    /// <summary>
    /// コンタクト品質(0〜1)の平均（互角時）。issue #262 で打球が野手の間を抜けやすくなった分を 0.62→0.60 で吸収していたが、
    /// issue homerun-rate-explodes-at-high-power（2026-08-18）で本塁打判定の正し＋初速上限の上位圧縮により本塁打が
    /// 安打から抜けた分（AVG .253→.235〜.243＝帯の床）を 0.62 へ戻して補う（AVG .242〜.253＝改定前と同水準）。
    /// 同日、投手の打球品質寄与 ×3（issue pitcher-barely-affects-contact-quality）で母集団の打率が −.009 動く分を
    /// 0.63 で戻す（AVG .240〜.250。0.64 だと夏場帯 4.90–5.80 を 5.90 で越える）。
    /// </summary>
    public double QualityMean { get; init; } = 0.63;
    /// <summary>コンタクト品質へのミート寄与((Contact−50)あたり)。</summary>
    public double QualityContactSlope { get; init; } = 0.0020;
    /// <summary>コンタクト品質の標準偏差。</summary>
    public double QualitySigma { get; init; } = 0.17;
    /// <summary>打球角度: 弾道1のときの平均角[deg]。</summary>
    public double LaunchAngleAtLt1 { get; init; } = -5.0;
    /// <summary>打球角度: 弾道100のときの平均角[deg]。</summary>
    public double LaunchAngleAtLt100 { get; init; } = 26.0;
    /// <summary>打球角度の標準偏差[deg]。</summary>
    public double LaunchAngleSigma { get; init; } = 12.0;
    /// <summary>
    /// ミートによる打球角度σの締まり（issue ai-roster-power-top-tier-all-sluggers, 2026-08-18）。
    /// σ × (1 − これ×(Contact−50))（下限 <see cref="LaunchAngleSigmaContactFloor"/>）。ミートが良いほど角度のばらつきが小さく
    /// ＝ゴロ・ポップが減ってライナー帯（安打）に集まる。本塁打帯（25〜35°）は平均角（弾道）から遠いので増えない
    /// ＝ミートは「安打を生む」「パワーは本塁打を生む」の分業。0 で無効。
    /// </summary>
    /// 0.010: C90 で σ×0.6（下限）＝AVG .353→.380／HR 4.2→1.9%（P60・D投手）、C30 で σ×1.2。母集団は AVG .249→.252・HR 1.67→1.60%。
    public double LaunchAngleSigmaPerContact { get; init; } = 0.010;
    /// <summary>ミートによる角度σ倍率の下限。</summary>
    public double LaunchAngleSigmaContactFloor { get; init; } = 0.6;
    /// <summary>方位角の残差（引っ張り/流しを引いた後のばらつき）の標準偏差[deg]。</summary>
    public double BearingSigma { get; init; } = 19.0;
    /// <summary>芯を外すと初速が落ちる度合い（品質1で満初速, 0で係数分だけ残る）。</summary>
    public double MinQualityVeloFactor { get; init; } = 0.55;

    // --- コンタクト品質への投手側寄与（「打たせて取る」の土台） ---
    // 打球質が打者能力だけで決まると、技巧派の優位は与四球の少なさからしか生まれず
    // 「弱い打球を打たせる＝結果が守備力に左右される」という型の個性が成立しない。
    // 3項とも基準値で寄与0＝リーグ平均投手では恒等（帯中立）に組む。
    // 2026-08-18（issue pitcher-barely-affects-contact-quality・監督承認）: 3項を ×3 へ。旧値は初速換算で ±2km/h しか動かず
    // 「エースが本塁打を減らせるのは三振の分だけ＝当てられればF投手と同じ本塁打率」（インプレー打球あたり P90 打者で
    // A投手 12.7% vs F投手 16.1%）だった。×3 で 9.6% vs 17.4%（2〜4倍差）。母集団の打率 −.009 は quality_mean で戻す。
    /// <summary>コンタクト品質への球速寄与の基準[km/h]（平均的な配球の実投球速度＝この球速で寄与0）。</summary>
    public double ContactQualityVelocityRefKmh { get; init; } = 121.5;
    /// <summary>実投球速度1km/hあたりのコンタクト品質寄与。正＝速い球は当たれば強い打球になる（本格派の代償）。</summary>
    /// <para>2026-08-19（設計書21・球速の価値）: +0.0030（×3）では球速Lv 50→90 で打球あたりの安打率 .323→.376・本塁打率 1.8→3.45% と
    /// 「速い投手ほど打たれる」になり、三振増と相殺して球速の価値がゼロだった（球速+10＝得点差 ±0）。速い球は差し込まれる
    /// （打球の質が落ちる）側へ −0.0050 に置き、打球あたりの質を球速に対して平坦〜微減にする＝球速+10 ≈ 制球+10（AVG −.017）。</para>
    public double ContactQualityPerKmh { get; init; } = -0.0050;
    /// <summary>投手コントロール(−50)あたりのコンタクト品質寄与。負＝制球が良いほど芯を外させる。</summary>
    public double ContactQualityPerControl { get; init; } = -0.0030;
    /// <summary>コンタクト品質への誘発変化合成量の基準[m]（リーグ平均球速×平均キレ＝この変化量で寄与0）。</summary>
    public double ContactQualityBreakRefM { get; init; } = 0.47;
    /// <summary>誘発変化合成量[m]あたりのコンタクト品質寄与。負＝変化が大きいほど詰まる。</summary>
    public double ContactQualityPerBreakM { get; init; } = -0.90;

    // --- ④ タイミングのズレ → ⑤ 打球方向（設計書01 §2④⑤, issue #262） ---
    // 打者はスイング開始を投球に合わせる。速い球・変化の大きい球ほど遅れ（差し込まれ）、ミートが良いほどズレない。
    // ズレは無次元（正＝差し込まれ／負＝早い）。⑤でこれが引っ張り/流しへ変換され、打者の左右が符号を与える。
    // 座標規約: −＝左翼（三塁側）／＋＝右翼（一塁側）。右打者の引っ張り＝負、左打者の引っ張り＝正。

    /// <summary>タイミングのズレの標準偏差（無次元）。1.0＝ここを単位に <see cref="BearingPullPerTiming"/> を測る。</summary>
    public double TimingSigma { get; init; } = 1.0;
    /// <summary>ズレ寄与0となる実投球速度[km/h]（平均的な配球の実投球速度）。</summary>
    public double TimingRefKmh { get; init; } = 121.5;
    /// <summary>実投球速度1km/hあたりの平均ズレ。正＝速い球ほど差し込まれる。</summary>
    public double TimingPerKmh { get; init; } = 0.010;
    /// <summary>ズレ寄与0となる誘発変化合成量[m]（リーグ平均球速×平均キレ）。</summary>
    public double TimingBreakRefM { get; init; } = 0.47;
    /// <summary>誘発変化合成量[m]あたりの平均ズレ。正＝変化が大きいほど差し込まれる。</summary>
    public double TimingPerBreakM { get; init; } = 0.60;
    /// <summary>ミート(−50)あたりのズレ縮小。正＝ミートが良いほど差し込まれない。</summary>
    public double TimingPerContact { get; init; } = 0.004;

    /// <summary>ズレ1あたりの引っ張り角[deg]。早い（ズレが負）ほど引っ張る。</summary>
    public double BearingPullPerTiming { get; init; } = 14.0;
    /// <summary>純ゴロ（打球角 <see cref="BearingGroundAngleDeg"/> 以下）での引っ張り角[deg]。</summary>
    public double BearingPullGroundDeg { get; init; } = 28.0;
    /// <summary>純フライ（打球角 <see cref="BearingFlyAngleDeg"/> 以上）での引っ張り角[deg]。負＝流し。</summary>
    public double BearingPullFlyDeg { get; init; } = -6.0;
    /// <summary>「純ゴロ」とみなす打球角[deg]。これ以下は <see cref="BearingPullGroundDeg"/> で頭打ち。</summary>
    public double BearingGroundAngleDeg { get; init; } = 0.0;
    /// <summary>「純フライ」とみなす打球角[deg]。これ以上は <see cref="BearingPullFlyDeg"/> で頭打ち。</summary>
    public double BearingFlyAngleDeg { get; init; } = 25.0;

    /// <summary>打球の平均角[deg]を弾道値から線形補間。</summary>
    public double MeanLaunchAngle(int launchTendency)
        => LaunchAngleAtLt1 + (LaunchAngleAtLt100 - LaunchAngleAtLt1) * (launchTendency - 1) / 99.0;

    /// <summary>
    /// 打球種別（打球角）に応じた引っ張り角[deg]。ゴロは引っ張り寄り・フライは流し寄りという実野球の傾向。
    /// 両端で頭打ちの線形補間。
    /// </summary>
    public double LaunchAnglePullDeg(double launchAngleDeg)
    {
        var span = BearingFlyAngleDeg - BearingGroundAngleDeg;
        if (span <= 0.0) return BearingPullGroundDeg;
        var t = (launchAngleDeg - BearingGroundAngleDeg) / span;
        t = t < 0.0 ? 0.0 : t > 1.0 ? 1.0 : t;
        return BearingPullGroundDeg + (BearingPullFlyDeg - BearingPullGroundDeg) * t;
    }
}
