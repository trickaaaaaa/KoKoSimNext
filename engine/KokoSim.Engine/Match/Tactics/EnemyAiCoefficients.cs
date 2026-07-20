namespace KokoSim.Engine.Match.Tactics;

/// <summary>
/// 敵AIの三層（能力値ミス率・ティア引き出し・校風重み）の係数（設計書11, YAML駆動）。
/// 別途の難易度補正は持たない（§6: 対戦相手のティア分布で難易度が自然に決まる）。
/// </summary>
public sealed record EnemyAiCoefficients
{
    // === ① 能力値層: 正着を選ぶ確率（設計書11 §1） ===
    /// <summary>最適解選択率 = Base + 采配能力 × PerSense（floor〜cap にクランプ）。</summary>
    public double OptimalBase { get; init; } = 0.55;
    public double OptimalPerSense { get; init; } = 0.004;
    public double OptimalFloor { get; init; } = 0.50;
    public double OptimalCap { get; init; } = 0.98;
    /// <summary>正着を外したとき、無謀な盗塁に走る確率（残りは「見送って強攻」＝機会損失）。</summary>
    public double RecklessOnMissProb { get; init; } = 0.18;

    // === ② ティア層: 戦術の引き出し（設計書11 §2, 0=G〜7=S） ===
    public int SafetyBuntMinTier { get; init; } = 2;   // E: 状況に応じたセーフティ
    public int StealMinTier { get; init; } = 2;        // E: 状況に応じた盗塁
    public int GambleStartMinTier { get; init; } = 4;  // C: ギャンブル始動（好ジャンプ・意表）は上級の判断（G3b）
    public int SqueezeMinTier { get; init; } = 5;      // B: 上級
    public int HitAndRunMinTier { get; init; } = 5;    // B: 上級
    public int BusterMinTier { get; init; } = 5;       // B: 上級（シフト読みの裏）
    public int DepthMinTier { get; init; } = 2;        // E: 守備位置調整
    public int BuntShiftMinTier { get; init; } = 5;    // B: 守備シフト
    public int AdvancedPolicyMinTier { get; init; } = 2; // E: カウント別配球（低め徹底/制球重視）
    public int InsidePolicyMinTier { get; init; } = 5; // B: 内角・釣り球
    public int GearMinTier { get; init; } = 4;         // C: ギアの先読み運用
    public int TimeoutMinTier { get; init; } = 2;      // E: 伝令の運用
    // 選手交代（設計書09 §6, C-2）: ベンチ運用の巧拙もティアで出る。
    public int PinchHitMinTier { get; init; } = 2;     // E: 代打の基本運用
    public int PinchRunMinTier { get; init; } = 4;     // C: 代走の使いどころ
    public int DefensiveSubMinTier { get; init; } = 3; // D: 守備固め
    // 1球采配（設計書15 §2.3, Phase C）: カウントを読んだ球単位の判断は上級の引き出し。
    public int PitchTacticsMinTier { get; init; } = 2; // E: カウント別の基本判断（追い込まれ矯正/3-0待て等）

    // === ③ 校風層: 采配傾向の重み（設計書11 §3） ===
    // 機動力野球
    public double SmallBallStealFactor { get; init; } = 1.7;
    public double SmallBallBuntFactor { get; init; } = 1.4;
    public int SmallBallBuntInningEarlier { get; init; } = 2;   // 送りバント開始回を早める
    public double SmallBallHitAndRunFactor { get; init; } = 1.8;
    public double SmallBallStealMinSuccessRelax { get; init; } = 0.08; // 多少無理でも走る
    public double SmallBallGambleStartFactor { get; init; } = 1.8;     // 機動力校はギャンブル始動を多用（G3b）
    // 強打・待球
    public double PowerBuntFactor { get; init; } = 0.30;
    public double PowerTakeFactor { get; init; } = 1.5;
    public double PowerSqueezeFactor { get; init; } = 0.40;
    // 守り勝つ野球
    public double DefensiveBuntFactor { get; init; } = 1.4;
    public double DefensiveShiftFactor { get; init; } = 1.4;
    public int DefensiveCoastLeadEarlier { get; init; } = 1;   // 早めに流す（温存）
    public int DefensiveSubInningEarlier { get; init; } = 1;   // 守備固めを早める（守り勝つ）
    // 機動力野球: 代走を早めに使う
    public int SmallBallPinchRunInningEarlier { get; init; } = 1;
    // 豪腕依存: マウンドへ行かず任せがち（守備伝令を絞る）
    public double AceDependentDefenseTimeoutKeepProb { get; init; } = 0.45;
}
