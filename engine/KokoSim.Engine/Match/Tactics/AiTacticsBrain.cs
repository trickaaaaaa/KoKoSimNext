using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;

namespace KokoSim.Engine.Match.Tactics;

/// <summary>
/// 敵AI・委任采配（設計書11）。<see cref="StandardTacticsBrain"/> の判断に三層を被せる:
/// ③校風で係数を偏らせ、②ティアで使える戦術を絞り、①能力値で正着からブレさせる。
/// 「AIは同じ采配システムの選択肢を選ぶだけ」＝AI専用の裏ルールを作らない（不変条件）。
/// 委任采配は本クラスをプレイヤー自身の采配能力＋Standard校風で使う（§7）。
/// </summary>
public sealed class AiTacticsBrain : ITacticsBrain, IPitchTacticsBrain
{
    private readonly StandardTacticsBrain _inner;
    private readonly AiProfile _profile;
    private readonly EnemyAiCoefficients _ai;
    private readonly double _optimalProb;

    public AiTacticsBrain(
        AiProfile profile,
        TacticsCoefficients? baseCoeff = null,
        BaserunningCoefficients? baserunning = null,
        EnemyAiCoefficients? aiCoeff = null)
    {
        _profile = profile;
        _ai = aiCoeff ?? new EnemyAiCoefficients();
        // ③ 校風で采配係数を偏らせ、その係数で共通の基準采配を構成する。
        var styled = ApplyStyle(baseCoeff ?? new TacticsCoefficients(), profile.Style, _ai);
        _inner = new StandardTacticsBrain(styled, baserunning);
        // ① 采配能力→最適解選択率。
        _optimalProb = MathUtil.Clamp(
            _ai.OptimalBase + profile.TacticalSense * _ai.OptimalPerSense, _ai.OptimalFloor, _ai.OptimalCap);
    }

    /// <summary>この AI が最適解を選ぶ確率（テスト・寸評用）。</summary>
    public double OptimalProbability => _optimalProb;

    public OffensiveSign CallOffense(in TacticsSituation s, IRandomSource rng)
    {
        // ③校風込みの基準采配 → ②ティアで実行可能な範囲へ丸め → ①能力値を外すと強攻に落ちる
        // （盗塁の無謀ブレはPhase D-2dで毎球采配側＝CallPitchActionへ移設済み）。
        var sign = TierGateOffense(_inner.CallOffense(s, rng));
        return MathUtil.Chance(_optimalProb, rng) ? sign : OffensiveSign.Swing;
    }

    public DefensiveTactics CallDefense(in TacticsSituation s, IRandomSource rng)
    {
        var d = TierGateDefense(_inner.CallDefense(s, rng));
        // 能力値ミス: 守備陣形・配球・ギアの設定を最適から外す（素の配置に戻す）。
        if (!MathUtil.Chance(_optimalProb, rng))
        {
            d = DefensiveTactics.Default;
        }
        return d;
    }

    /// <summary>
    /// 1球采配（設計書15 §2.3, C-2／盗塁は Phase D-2d）。強攻/待て・配球方針・ギアの本体は
    /// ②引き出し（PitchTacticsMinTier）で運用可否、①能力値で好機を逃す（PA頭のCallDefenseと同じ丸め方）。
    /// 盗塁は本体と独立のティア下限（StealMinTier）を持つ別軸（旧 TierGateOffense/Misfire の毎球版）。
    /// </summary>
    public PitchTacticsDirective? CallPitchAction(in PitchTacticsSituation s, IRandomSource rng)
    {
        var d = _inner.CallPitchAction(s, rng);
        var optimal = MathUtil.Chance(_optimalProb, rng);

        var coreOk = optimal && _profile.TierRank >= _ai.PitchTacticsMinTier;
        var batting = coreOk ? d?.Batting : null;
        var policy = coreOk ? d?.Policy : null;
        var gear = coreOk ? d?.Gear : null;

        // 盗塁（設計書12 §5「セオリー vs 意表」）: 正着を外すと（①）、無謀な盗塁に走ることがある
        // （旧 Misfire、ティア不問）。正着どおりなら②ティア（StealMinTier）を満たした時だけ_innerの
        // 判断（試みるか＋始動種別）を使う。ギャンブル始動は別途、さらに上級の判断（GambleStartMinTier）。
        StartType? steal;
        if (!optimal)
        {
            steal = s.Base.OnFirst is not null && s.Base.OnSecond is null && s.Base.Outs < 2
                    && MathUtil.Chance(_ai.RecklessOnMissProb, rng)
                ? StartType.Normal
                : null;
        }
        else
        {
            steal = _profile.TierRank >= _ai.StealMinTier ? d?.StealAttempt : null;
        }
        if (steal == StartType.Gamble && _profile.TierRank < _ai.GambleStartMinTier) steal = StartType.Normal;

        return batting is null && policy is null && gear is null && steal is null
            ? null
            : new PitchTacticsDirective(batting, policy, gear, steal);
    }

    public bool CallOffenseTimeout(in TacticsSituation s, IRandomSource rng)
    {
        if (_profile.TierRank < _ai.TimeoutMinTier) return false;       // ②引き出しになければ使えない
        if (!_inner.CallOffenseTimeout(s, rng)) return false;
        return MathUtil.Chance(_optimalProb, rng);                       // ①使いどころを外すことがある
    }

    public bool CallDefenseTimeout(in TacticsSituation s, IRandomSource rng)
    {
        if (_profile.TierRank < _ai.TimeoutMinTier) return false;
        if (!_inner.CallDefenseTimeout(s, rng)) return false;
        // ③豪腕依存はマウンドへ行かず任せがち。
        if (_profile.Style == SchoolStyle.AceDependent
            && !MathUtil.Chance(_ai.AceDependentDefenseTimeoutKeepProb, rng))
        {
            return false;
        }
        return MathUtil.Chance(_optimalProb, rng);
    }

    // --- 選手交代（設計書09 §6, C-2）: ②ティアで運用可否、①能力値で好機を逃す ---

    public Players.Player? CallPinchHit(in SubstitutionSituation s, IRandomSource rng)
    {
        if (_profile.TierRank < _ai.PinchHitMinTier) return null;         // ②引き出しになければ運用しない
        var pick = _inner.CallPinchHit(s, rng);
        if (pick is null) return null;
        return MathUtil.Chance(_optimalProb, rng) ? pick : null;          // ①使いどころを外して好機を逃す
    }

    public (Players.Player Runner, Players.Player Sub)? CallPinchRun(in SubstitutionSituation s, IRandomSource rng)
    {
        if (_profile.TierRank < _ai.PinchRunMinTier) return null;
        var pick = _inner.CallPinchRun(s, rng);
        if (pick is null) return null;
        return MathUtil.Chance(_optimalProb, rng) ? pick : null;
    }

    public (Players.Player Out, Players.Player Sub)? CallDefensiveSub(in SubstitutionSituation s, IRandomSource rng)
    {
        if (_profile.TierRank < _ai.DefensiveSubMinTier) return null;
        var pick = _inner.CallDefensiveSub(s, rng);
        if (pick is null) return null;
        return MathUtil.Chance(_optimalProb, rng) ? pick : null;
    }

    // --- ② ティア・ゲート: 引き出しにない高度な戦術は素の手へ落とす ---

    private OffensiveSign TierGateOffense(OffensiveSign sign)
    {
        var rank = _profile.TierRank;
        return sign switch
        {
            OffensiveSign.SafetyBunt when rank < _ai.SafetyBuntMinTier => OffensiveSign.SacrificeBunt,
            OffensiveSign.Squeeze when rank < _ai.SqueezeMinTier => OffensiveSign.SacrificeBunt,
            OffensiveSign.HitAndRun when rank < _ai.HitAndRunMinTier => OffensiveSign.Swing,
            OffensiveSign.Buster when rank < _ai.BusterMinTier => OffensiveSign.Swing,
            _ => sign,
        };
    }

    private DefensiveTactics TierGateDefense(DefensiveTactics d)
    {
        var rank = _profile.TierRank;
        var infield = rank < _ai.DepthMinTier ? DefenseDepth.Normal : d.Infield;
        var outfield = rank < _ai.DepthMinTier ? DefenseDepth.Normal : d.Outfield;
        var shift = d.BuntShift && rank >= _ai.BuntShiftMinTier;
        var gear = rank < _ai.GearMinTier ? PitcherGear.Normal : d.Gear;
        var policy = d.Policy;
        if (policy == PitchPolicy.InsideAttack && rank < _ai.InsidePolicyMinTier) policy = PitchPolicy.Auto;
        else if (policy != PitchPolicy.Auto && rank < _ai.AdvancedPolicyMinTier) policy = PitchPolicy.Auto;
        return d with { Infield = infield, Outfield = outfield, BuntShift = shift, Gear = gear, Policy = policy };
    }

    // --- ③ 校風: 采配係数の重み付け（同じ局面でも手が変わる） ---

    private static TacticsCoefficients ApplyStyle(TacticsCoefficients b, SchoolStyle style, EnemyAiCoefficients ai)
        => style switch
        {
            SchoolStyle.SmallBall => b with
            {
                StealProb = Clamp01(b.StealProb * ai.SmallBallStealFactor),
                StealMinSuccess = MathUtil.Clamp(b.StealMinSuccess - ai.SmallBallStealMinSuccessRelax, 0.4, 0.99),
                GambleStartProb = Clamp01(b.GambleStartProb * ai.SmallBallGambleStartFactor),
                SacBuntProb = Clamp01(b.SacBuntProb * ai.SmallBallBuntFactor),
                SacBuntFromInning = System.Math.Max(1, b.SacBuntFromInning - ai.SmallBallBuntInningEarlier),
                HitAndRunProb = Clamp01(b.HitAndRunProb * ai.SmallBallHitAndRunFactor),
                PinchRunFromInning = System.Math.Max(1, b.PinchRunFromInning - ai.SmallBallPinchRunInningEarlier),
            },
            SchoolStyle.PowerHitting => b with
            {
                SacBuntProb = Clamp01(b.SacBuntProb * ai.PowerBuntFactor),
                TakeProb = Clamp01(b.TakeProb * ai.PowerTakeFactor),
                SqueezeProb = Clamp01(b.SqueezeProb * ai.PowerSqueezeFactor),
            },
            SchoolStyle.DefensiveMinded => b with
            {
                SacBuntProb = Clamp01(b.SacBuntProb * ai.DefensiveBuntFactor),
                BuntShiftProb = Clamp01(b.BuntShiftProb * ai.DefensiveShiftFactor),
                GearCoastMinLead = System.Math.Max(1, b.GearCoastMinLead - ai.DefensiveCoastLeadEarlier),
                DefensiveSubFromInning = System.Math.Max(1, b.DefensiveSubFromInning - ai.DefensiveSubInningEarlier),
            },
            // 全員野球・豪腕依存・型なしは采配係数を素のまま使う（差は継投/伝令運用や層で出る）。
            _ => b,
        };

    private static double Clamp01(double v) => MathUtil.Clamp(v, 0.0, 1.0);
}
