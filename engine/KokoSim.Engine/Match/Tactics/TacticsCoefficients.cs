using System;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Tactics;

/// <summary>
/// 采配まわりの係数（設計書09, YAML駆動）。判断閾値（StandardTacticsBrain）と
/// サイン・指示の効果量（試合エンジン側）の両方をここに集約する。
/// </summary>
public sealed record TacticsCoefficients
{
    // ===== 攻撃サイン判断（StandardTacticsBrain） =====
    /// <summary>送りバントを考え始めるイニング。</summary>
    public int SacBuntFromInning { get; init; } = 7;
    /// <summary>送りバント実行確率（好条件時）。タイブレークでは跳ね上がる（設計書09 §7）。</summary>
    public double SacBuntProb { get; init; } = 0.55;
    public double SacBuntTieBreakProb { get; init; } = 0.90;
    /// <summary>この打者パワー以上なら打たせる。</summary>
    public int SacBuntMaxPower { get; init; } = 58;
    public int SacBuntMinSkill { get; init; } = 40;
    /// <summary>ビハインド許容（−2点まで）とリード許容（+1点まで）。大差ではバントしない。</summary>
    public int SacBuntMaxBehind { get; init; } = 2;
    public int SacBuntMaxAhead { get; init; } = 1;

    public int SqueezeFromInning { get; init; } = 8;
    public double SqueezeProb { get; init; } = 0.28;
    public int SqueezeMaxDiffAbs { get; init; } = 1;
    public int SqueezeMinBunt { get; init; } = 55;

    /// <summary>盗塁: 成功見込みがこの値以上のときだけ、確率的に仕掛ける。</summary>
    public double StealMinSuccess { get; init; } = 0.72;
    public double StealProb { get; init; } = 0.45;

    // ===== 三盗・本盗（issue #67, design-14 未決A）: 二塁のみ在塁／三塁のみ在塁でのみ判定。 =====
    // 一・三塁の重盗（DoubleStealThirdBreakProb）とは塁状況が排他（重盗は一塁+三塁）のため優先順位の
    // 競合はない。三塁が絡む唯一の競合はスクイズ（本盗も三塁走者が起点）で、GameEngine 側で
    // 「この球スクイズが確定していれば本盗は試みない」という単純上書きで解消する（Q12-3と同型）。
    /// <summary>三盗: 二盗より選別的な成功見込み閾値（実測ベースの初期値）。三盗はキャッチャーの三塁送球が
    /// 二塁送球より短い一方、StealThirdSuccessBiasの下方補正が上回るため、解決式が返す実現可能な見込みの
    /// 天井自体が二盗（≈0.95）よりずっと低い（俊足×弱肩でも≈0.4〜0.65）。よって0.30は「二盗と同じ絶対値」
    /// ではなく「三盗の実現可能レンジの中で上位のみ」という相対的な選別線。</summary>
    public double StealThirdMinSuccess { get; init; } = 0.30;
    public double StealThirdProb { get; init; } = 0.25;
    /// <summary>三盗を考えるアウトカウント上限（0=無死のみ）と、大差では狙わない点差上限。</summary>
    public int StealThirdMaxOuts { get; init; } = 0;
    public int StealThirdMaxDiffAbs { get; init; } = 3;

    /// <summary>本盗: 超高閾値のギャンブル枠。解決式（StealResolver）は投手クイック＋タッチのみで守備側を
    /// 評価し、走者側の塁間走破（3秒超）に追いつけないため、どの選手の組み合わせでも成功見込みは解決式の
    /// 下限（0.01）に張り付く＝「成功見込みで選別する」こと自体が意味を持たない。よって閾値は0（常に通過）
    /// とし、発生頻度は StealHomeProb と状況条件（アウトカウント・点差）だけで絞る。</summary>
    public double StealHomeMinSuccess { get; init; } = 0.0;
    public double StealHomeProb { get; init; } = 0.10;
    public int StealHomeMaxOuts { get; init; } = 1;
    public int StealHomeMaxDiffAbs { get; init; } = 1;

    public double HitAndRunProb { get; init; } = 0.07;
    public int HitAndRunMinContact { get; init; } = 55;
    public int HitAndRunMaxPower { get; init; } = 65;

    /// <summary>ギャンブル始動（設計書12 §5, G3b）: 盗塁の成功見込みがこの値未満（＝際どく通常始動では
    /// 心もとない）のとき、好ジャンプ・意表に賭ける。見込みが十分高い盗塁は通常始動で堅実に決める
    /// （＝ギャンブルの無防備リスクを負わない）。</summary>
    public double GambleStartMaxSuccess { get; init; } = 0.82;
    /// <summary>際どい盗塁でギャンブル始動に賭ける確率。</summary>
    public double GambleStartProb { get; init; } = 0.40;

    /// <summary>待て: 制球難投手（Control≦閾値）相手に確率で。</summary>
    public int TakeMaxControl { get; init; } = 40;
    public double TakeProb { get; init; } = 0.30;

    // ===== 本塁への送り判定（設計書12 §3/§4, F2）=====
    // 盗塁の StealMinSuccess と同型: 生還推定確率がこの閾値以上なら三塁コーチが還す。
    /// <summary>中立采配の送り閾値（生還見込みがこの値以上で還す）。Slice C校正: 0.50 で憤死を目標帯へ。</summary>
    public double SendHomeMinSuccess { get; init; } = 0.50;
    /// <summary>2アウトは大きく緩和（還すか自重かで失うものが少ない＝積極的に還す）。</summary>
    public double SendHomeTwoOutRelax { get; init; } = 0.30;
    /// <summary>aggression(0=超慎重, 0.5=中立, 1=超積極)で閾値を動かす幅。機動力校・攻めの監督で下がる。</summary>
    public double SendHomeAggressionSpan { get; init; } = 0.50;

    // ===== 三塁への送り判定（単打の一塁→三塁, Issue #89, 設計書12 §3.5）=====
    // 本塁と同型だが、三塁は失うものが小さく積極的に回す＝閾値は本塁より低め。
    /// <summary>中立采配で三塁へ回す閾値（到達見込みがこの値以上で回す）。</summary>
    public double SendThirdMinSuccess { get; init; } = 0.60;
    /// <summary>2アウトは緩和（三塁で止めても得点に直結しない＝積極的に回す）。</summary>
    public double SendThirdTwoOutRelax { get; init; } = 0.25;
    /// <summary>aggression で閾値を動かす幅。</summary>
    public double SendThirdAggressionSpan { get; init; } = 0.45;

    // ===== 守備指示判断 =====
    public double BuntShiftProb { get; init; } = 0.50;
    public int InfieldInFromInning { get; init; } = 8;
    /// <summary>この点差以内で守っている（0=同点/負け含む）とき前進守備を考える。</summary>
    public int InfieldInMaxLead { get; init; } = 1;
    public int OutfieldDeepMinPower { get; init; } = 72;
    public int ControlFirstMaxControl { get; init; } = 38;
    public int KeepLowMinPower { get; init; } = 75;
    /// <summary>ギア「飛ばせ」: 残りイニング数がこの値以下＋接戦＋得点圏。</summary>
    public int GearPushInningsLeft { get; init; } = 1;
    public int GearPushMaxDiffAbs { get; init; } = 1;
    /// <summary>ギア「流せ」: この点差以上リードで温存。</summary>
    public int GearCoastMinLead { get; init; } = 5;

    // ===== 敬遠（design-14 P1-3） =====
    /// <summary>一塁空き・得点圏でこの打者パワー以上なら敬遠を考え始める。</summary>
    public int IntentionalWalkMinPower { get; init; } = 78;
    /// <summary>敬遠を考え始めるイニング（終盤限定）。</summary>
    public int IntentionalWalkFromInning { get; init; } = 7;
    /// <summary>この点差以内（僅差）でのみ敬遠を考える。</summary>
    public int IntentionalWalkMaxDiffAbs { get; init; } = 2;
    /// <summary>好条件成立時に実際に敬遠へ踏み切る確率。既定0＝機能オフ（rng.NextDouble()自体を呼ばない
    /// ガード付きなので、Brainつきの試合でも従来の乱数消費順・結果と完全一致する）。</summary>
    public double IntentionalWalkProb { get; init; } = 0.0;

    // ===== 伝令判断（設計書09 §3） =====
    public int DefenseTimeoutMinPressure { get; init; } = 3;
    public int OffenseTimeoutMinPressure { get; init; } = 5;
    public int OffenseTimeoutMaxMental { get; init; } = 42;

    // ===== 選手交代判断（設計書09 §6, C-2。StandardTacticsBrain, 決定論） =====
    /// <summary>代打を考え始めるイニング／代打候補にする打者ミート上限／控えがこれだけミート上なら送る。</summary>
    public int PinchHitFromInning { get; init; } = 7;
    public int PinchHitContactCeiling { get; init; } = 46;
    public int PinchHitImprovement { get; init; } = 12;
    /// <summary>代打を出す得失点差レンジ（僅差〜小ビハインドで打線を厚くする。大差では出さない）。</summary>
    public int PinchHitMinDiff { get; init; } = -3;
    public int PinchHitMaxDiff { get; init; } = 2;

    /// <summary>代走を考え始めるイニング／代走候補にする走者の走力上限／控えがこれだけ走力上なら送る。</summary>
    public int PinchRunFromInning { get; init; } = 8;
    public int PinchRunSpeedCeiling { get; init; } = 44;
    public int PinchRunImprovement { get; init; } = 22;
    /// <summary>代走を出す得失点差の下限（このビハインド以内。1点を取りにいく場面）。上限は+1（僅差リードまで）。</summary>
    public int PinchRunMinDiff { get; init; } = -2;

    /// <summary>守備固めを考え始めるイニング／守るリードの下限／守備交代候補の守備上限／控えがこれだけ守備上なら固める。</summary>
    public int DefensiveSubFromInning { get; init; } = 8;
    public int DefensiveSubMinLead { get; init; } = 1;
    public int DefensiveSubFieldingCeiling { get; init; } = 44;
    public int DefensiveSubImprovement { get; init; } = 12;

    // ===== サイン効果（試合エンジン側の補正係数） =====
    /// <summary>エンドラン: 当てにいく（ミート↑・パワー↓・ゴロ性向↑, 設計書09 §1）。</summary>
    public double HitAndRunContactBoost { get; init; } = 0.10;
    public double HitAndRunPowerPenalty { get; init; } = 0.15;
    public double HitAndRunLaunchPenalty { get; init; } = 0.20;
    /// <summary>エンドラン空振り時: 走者憤死判定に載せる成功率ペナルティ（打者と同時スタートの分の減額）。</summary>
    public double HitAndRunCaughtPenalty { get; init; } = 0.15;

    /// <summary>バスター: シフト時は穴が空く（補正↑）、通常時は準備不足（補正↓）。</summary>
    public double BusterVsShiftBonus { get; init; } = 0.08;
    public double BusterPenalty { get; init; } = 0.05;

    /// <summary>スクイズのウエスト（バッテリー察知, 設計書09 §1）: 基礎＋捕手の判断（Fielding）勾配＋状況の露骨さ。</summary>
    public double SqueezeReadBase { get; init; } = 0.08;
    public double SqueezeReadPerLead { get; init; } = 0.002;
    public double SqueezeReadObviousBonus { get; init; } = 0.05;

    // ===== 陣形の初期守備位置（設計書09 §2.1: 効果は物理から出る） =====
    public double InfieldInFactor { get; init; } = 0.82;
    public double InfieldDeepFactor { get; init; } = 1.12;
    public double OutfieldInFactor { get; init; } = 0.88;
    public double OutfieldDeepFactor { get; init; } = 1.10;
    /// <summary>バントシフト: 一・三塁手が本塁方向へチャージする距離倍率。</summary>
    public double BuntShiftCornerChargeFactor { get; init; } = 0.60;

    // ===== 1球采配（設計書15 §2.3, Phase C。C-1のno-opゲートで従来と完全一致を確認済み、C-2で実装） =====
    /// <summary>追い込まれ矯正: 2ストライクで、見逃さない判断力（Contact）がある打者ほど一定確率でForceSwingへ。</summary>
    public double PitchTacticsTwoStrikeForceSwingProb { get; init; } = 0.35;
    public int PitchTacticsTwoStrikeMinContact { get; init; } = 50;
    /// <summary>3ボール0ストライク（3-0）: 高確率でForceTake（classic 3-0待て）。</summary>
    public double PitchTacticsThreeZeroTakeProb { get; init; } = 0.85;
    /// <summary>3-0でも打たせる例外: 強打者×終盤×僅差（一発を狙う価値がTakeを上回る）。</summary>
    public int PitchTacticsThreeZeroSwingAwayMinPower { get; init; } = 80;
    public int PitchTacticsThreeZeroSwingAwayFromInning { get; init; } = 7;
    public int PitchTacticsThreeZeroSwingAwayMaxDiffAbs { get; init; } = 1;
    /// <summary>守備側: 2ストライク後、決め球（変化球中心）へ切り替える確率。</summary>
    public double PitchTacticsPutAwayProb { get; init; } = 0.50;
    /// <summary>守備側: 3ボール以上でゾーンに集める（ControlFirst）ボール数の下限。</summary>
    public int PitchTacticsControlFirstMinBalls { get; init; } = 3;

    // ===== 配球方針の重み（設計書09 §2.2） =====
    public double FastballHeavyShareDelta { get; init; } = 0.25;
    public double BreakingHeavyShareDelta { get; init; } = -0.25;
    /// <summary>コントロール重視: 狙いの散らしを絞る（ゾーン内比率↑・四球回避が物理で出る）。</summary>
    public double ControlFirstAimSigmaFactor { get; init; } = 0.72;
    public double KeepLowAimYOffsetM { get; init; } = -0.18;
    public double InsideAimXOffsetM { get; init; } = 0.15;

    // ===== 伝令効果・動揺・主将（設計書09 §3/§8） =====
    /// <summary>伝令1回の負補正緩和量（0〜1）とその継続打席数。</summary>
    public double TimeoutMitigation { get; init; } = 0.60;
    public int TimeoutDurationPa { get; init; } = 3;
    /// <summary>動揺: 連続出塁がこの数に達すると投手が動揺し、負補正が増幅される。</summary>
    public int RattledConsecutiveBaserunners { get; init; } = 3;
    public double RattledNegativeAmplify { get; init; } = 1.4;
    /// <summary>動揺の発生耐性（精神力50で従来と同値, 100で+offset・0で-offset）。</summary>
    public int RattledThresholdMentalOffset { get; init; } = 1;
    /// <summary>動揺後、無失点でこの数だけアウトを重ねると自然回復（伝令・継投を使わず解除）。</summary>
    public int RattledRecoveryOuts { get; init; } = 2;
    /// <summary>主将: 統率力（統率傾向×精神力/100, 0〜100）1あたりの負補正緩和。ベンチ時は大きく減衰。</summary>
    public double CaptainMitigationPerPower { get; init; } = 0.004;
    public double CaptainBenchFactor { get; init; } = 0.30;

    /// <summary>配球方針→自動配球への重み（おまかせは恒等）。内角は打者の利きで符号が変わる。</summary>
    public PitchDirective DirectiveFor(PitchPolicy policy, Handedness batterBats) => policy switch
    {
        PitchPolicy.FastballHeavy => PitchDirective.Identity with { StraightShareDelta = FastballHeavyShareDelta },
        PitchPolicy.BreakingHeavy => PitchDirective.Identity with { StraightShareDelta = BreakingHeavyShareDelta },
        PitchPolicy.ControlFirst => PitchDirective.Identity with { AimSigmaFactor = ControlFirstAimSigmaFactor },
        PitchPolicy.KeepLow => PitchDirective.Identity with { AimYOffsetM = KeepLowAimYOffsetM },
        // 右打者の内角は三塁側(−X)、左打者は一塁側(+X)。スイッチは左打席想定。
        PitchPolicy.InsideAttack => PitchDirective.Identity with
        {
            AimXOffsetM = batterBats == Handedness.Right ? -InsideAimXOffsetM : InsideAimXOffsetM,
        },
        _ => PitchDirective.Identity,
    };

    /// <summary>
    /// 動揺の発生閾値（連続出塁数）。精神力50なら <see cref="RattledConsecutiveBaserunners"/> のまま（従来と同値）、
    /// 高いほど閾値が上がって動揺しにくく、低いほど下がって動揺しやすい（PressureModel と同じ(mental-50)/50の式）。
    /// </summary>
    public int RattledThresholdFor(int mental)
    {
        var offset = (int)Math.Round((mental - 50) / 50.0 * RattledThresholdMentalOffset);
        return Math.Max(1, RattledConsecutiveBaserunners + offset);
    }
}
