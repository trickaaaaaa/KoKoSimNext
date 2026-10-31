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
public sealed class AiTacticsBrain : ITacticsBrain, IPitchTacticsBrain, IExplainTactics
{
    private readonly StandardTacticsBrain _inner;
    private readonly AiProfile _profile;
    private readonly EnemyAiCoefficients _ai;
    private readonly double _optimalProb;

    public AiTacticsBrain(
        AiProfile profile,
        TacticsCoefficients? baseCoeff = null,
        BaserunningCoefficients? baserunning = null,
        EnemyAiCoefficients? aiCoeff = null,
        Players.FormCoefficients? form = null,
        Nation.LineupCoefficients? lineup = null)
    {
        _profile = profile;
        _ai = aiCoeff ?? new EnemyAiCoefficients();
        // ③ 校風で采配係数を偏らせ、その係数で共通の基準采配を構成する。
        var styled = ApplyStyle(baseCoeff ?? new TacticsCoefficients(), profile.Style, _ai);
        _inner = new StandardTacticsBrain(styled, baserunning, form, lineup);
        // ① 采配能力→最適解選択率。
        _optimalProb = MathUtil.Clamp(
            _ai.OptimalBase + profile.TacticalSense * _ai.OptimalPerSense, _ai.OptimalFloor, _ai.OptimalCap);
    }

    /// <summary>三層プロファイル（采配能力・ティア・校風）。伝令の効き目が采配能力を読む（issue #714）。</summary>
    public AiProfile Profile => _profile;

    /// <summary>この AI が最適解を選ぶ確率（テスト・寸評用）。</summary>
    public double OptimalProbability => _optimalProb;

    /// <summary>
    /// 観測用の判断内訳を吐くか（設計書17 §5 P4, F3）。観測が有効なときだけ GameEngine が立てる。
    /// 判断そのものには一切影響しない（文字列を1本組むだけ）。
    /// </summary>
    public bool ExplainDecisions { get; set; }

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
        // （旧 Misfire、ティア不問。二盗のみ＝無謀を働くのは従来からの型で、三盗・本盗はセオリー判断時のみ）。
        // 正着どおりなら②ティア（狙う塁ごとに独立の下限。issue #67: 三盗・本盗は二盗より高いティアを要求）
        // を満たした時だけ_innerの判断（試みるか＋始動種別＋狙う塁）を使う。ギャンブル始動は別途、
        // さらに上級の判断（GambleStartMinTier）。
        StartType? steal;
        var stealTarget = d?.StealTarget ?? StealTarget.Second;
        if (!optimal)
        {
            steal = s.Base.OnFirst is not null && s.Base.OnSecond is null && s.Base.Outs < 2
                    && MathUtil.Chance(_ai.RecklessOnMissProb, rng)
                ? StartType.Normal
                : null;
            stealTarget = StealTarget.Second;
        }
        else
        {
            var stealTierOk = stealTarget switch
            {
                StealTarget.Third => _profile.TierRank >= _ai.StealThirdMinTier,
                StealTarget.Home => _profile.TierRank >= _ai.StealHomeMinTier,
                _ => _profile.TierRank >= _ai.StealMinTier,
            };
            steal = stealTierOk ? d?.StealAttempt : null;
        }
        if (steal == StartType.Gamble && _profile.TierRank < _ai.GambleStartMinTier) steal = StartType.Normal;

        // 観測（設計書17 §5 P4）: この AI は「スコア比較」ではなく「①能力値の当たり外れ×②ティア関門」で
        // 手を絞る構造なので、候補スコアを捏造せず<b>関門の通過状況をそのまま</b>出す。
        // 素の判断（_inner）とティア下限を並べれば「なぜその手になったか」が1行で読める。
        var explanation = ExplainDecisions
            ? string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "inner={0}/{1}/{2}/{3},optimal={4}(p={5:F2}),tier={6},minPitch={7},minSteal={8}",
                d?.Batting?.ToString() ?? "-", d?.Policy?.ToString() ?? "-",
                d?.Gear?.ToString() ?? "-", d?.StealAttempt?.ToString() ?? "-",
                optimal ? 1 : 0, _optimalProb, _profile.TierRank, _ai.PitchTacticsMinTier, _ai.StealMinTier)
            : null;

        return batting is null && policy is null && gear is null && steal is null && explanation is null
            ? null
            : new PitchTacticsDirective(batting, policy, gear, steal, stealTarget, explanation);
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

    /// <summary>
    /// 守備位置の入替＝コンバート（issue #255 A-2, 設計書09 §2.3）。オーナー決定 A-2＝<b>僅差終盤限定</b>で、
    /// リードを守る場面だけ守備を組み替える（無料の手なので毎回やらせると帯が大きく動くため範囲を絞る）。
    /// ③校風で実効ティアを補正し、②ティア関門（既定 <c>DefenseSwapMinTier</c>=99＝誰も通らない＝恒等）で
    /// 運用可否を決め、①能力値で好機を掴めるかを引く。関門を通らない限り RNG は一切引かない。
    /// </summary>
    public (int SlotA, int SlotB)? CallDefenseSwap(in DefenseSwapSituation s, IRandomSource rng)
    {
        // ③ 校風による実効ティア補正（守り勝つ＝組み替えに積極的／豪腕依存＝投手任せで動かさない）。
        var effTier = _profile.TierRank + _profile.Style switch
        {
            SchoolStyle.DefensiveMinded => _ai.DefensiveMindedSwapTierBonus,
            SchoolStyle.AceDependent => -_ai.AceDependentSwapTierPenalty,
            _ => 0,
        };
        if (effTier < _ai.DefenseSwapMinTier) return null;   // ② 引き出しになければ運用しない（RNG不使用）

        // A-2: 終盤かつ僅差のときだけ。ここまでも RNG は引かない。
        if (s.Inning < _ai.DefenseSwapFromInning) return null;
        if (System.Math.Abs(s.ScoreDiff) > _ai.DefenseSwapMaxScoreDiff) return null;

        var best = BestSwap(s);
        if (best is null) return null;

        // ① 能力値: 改善に気づけるか。外すとそのまま＝好機を逃す（他の判断と同じ丸め方）。
        return MathUtil.Chance(_optimalProb, rng) ? best : null;
    }

    /// <summary>
    /// 守備スコア（Σ 守備位置の負担重み × Fielding）を最も改善する1組を返す（RNG不使用＝決定論）。
    /// 入替(a,b)の改善量 = (w[posB] - w[posA]) × (fieldA - fieldB)＝「上手い方を重い位置へ」。
    /// 捕手は専門職（リード・捕球）なので動かさない＝重み表に載せず候補から外す。
    /// 同値は (slotA, slotB) の小さい方を採る（決定論の担保）。
    /// </summary>
    private (int SlotA, int SlotB)? BestSwap(in DefenseSwapSituation s)
    {
        (int A, int B)? best = null;
        var bestGain = _ai.DefenseSwapMinGain;   // これ未満の改善では動かさない（> で比較＝閾値ちょうどは不採用）
        var n = System.Math.Min(s.Lineup.Count, s.Swappable.Count);
        for (var a = 0; a < n; a++)
        {
            if (!s.Swappable[a] || DefenseWeight(s.Lineup[a].Position) is not { } wa) continue;
            for (var b = a + 1; b < n; b++)
            {
                if (!s.Swappable[b] || DefenseWeight(s.Lineup[b].Position) is not { } wb) continue;
                var gain = (wb - wa) * (s.Lineup[a].Fielding - s.Lineup[b].Fielding);
                if (gain > bestGain) { bestGain = gain; best = (a, b); }
            }
        }
        return best;
    }

    /// <summary>守備位置の負担重み。投手・捕手・DHは入替対象外なので null。</summary>
    private double? DefenseWeight(Field.FieldPosition pos) => pos switch
    {
        Field.FieldPosition.Shortstop => _ai.DefenseWeightShortstop,
        Field.FieldPosition.CenterField => _ai.DefenseWeightCenterField,
        Field.FieldPosition.SecondBase => _ai.DefenseWeightSecondBase,
        Field.FieldPosition.ThirdBase => _ai.DefenseWeightThirdBase,
        Field.FieldPosition.RightField => _ai.DefenseWeightRightField,
        Field.FieldPosition.LeftField => _ai.DefenseWeightLeftField,
        Field.FieldPosition.FirstBase => _ai.DefenseWeightFirstBase,
        _ => null,
    };

    /// <summary>
    /// 継投（設計書11 §4, issue #209）。基本トリガー（疲労・球数上限）は必ず尊重（Standardと同一・RNG不使用）。
    /// その上に高度トリガー（崩れ・僅差終盤・動揺）を②ティア関門・③校風・①能力値ミスで被せる。
    /// 高度トリガーの各 MinTier は既定で到達不能(99)＝発火せず、基本トリガーの時しか RNG を引かない
    /// （＝games_10k/tactics 帯・決定論baseline 不変, D1=(A)）。人選は Phase A では bullpen 先頭のまま。
    /// </summary>
    public PitchingChangeDecision? CallPitchingChange(in PitchingChangeSituation s, IRandomSource rng)
    {
        // 基本トリガー（疲労margin/週間球数）は無条件に尊重（決定論・RNG不使用。ハードキャップは撤廃, issue #356）。
        // 人選だけ横断ランキングに載せ替える（issue #209 Phase B。RNG非依存＝決定論）。
        if (_inner.CallPitchingChange(s, rng) is { } basic)
        {
            var basicReliever = PickReliever(s);
            return basic with { Reliever = basicReliever, ConvertTo = ConvertTargetFor(s, basicReliever) };
        }

        // 交代先がいなければ高度トリガーも意味を持たない（＝RNGを引かず素通り）。
        if (!s.ReliefAvailable) return null;

        // ②ティア関門×③校風で高度トリガーの発火条件だけを判定（ここまで RNG は一切引かない）。
        var reason = EvaluateAdvancedRelief(s);
        if (reason is null) return null;

        // ①能力値: 最適な継投の好機を掴む確率（専用Fork由来の rng を消費）。外すと続投＝好機を逃す。
        if (!MathUtil.Chance(_optimalProb, rng)) return null;
        var reliever = PickReliever(s);
        return new PitchingChangeDecision(reason.Value, reliever, ConvertTargetFor(s, reliever));
    }

    /// <summary>
    /// コンバート継投の行き先（issue #255 B-1, 設計書09 §6.2）。継投がすでに決まった上で、
    /// 「元投手の打力が高いので打順に残したい」ときだけ通常継投の代わりに守備へ回す。
    /// 打順に残す判断なので<b>非DHのみ</b>（DH制では元投手はもともと打席に立たない）。
    /// ②ティア関門（既定 <c>ConvertReliefMinTier</c>=99＝誰も通らない＝恒等）。RNG不使用＝決定論。
    /// 行き先は<b>負担の最も軽い守備位置</b>（＝押し出す野手の損失が最小）。同重みなら Fielding が低い方、
    /// それも同値なら打順スロットの小さい方を採る。
    /// </summary>
    private Field.FieldPosition? ConvertTargetFor(in PitchingChangeSituation s, Players.Player? reliever)
    {
        if (_profile.TierRank < _ai.ConvertReliefMinTier) return null;
        if (reliever is null) return null;            // 人選が無いと行き先を決められない＝通常継投に落とす
        if (s.UsesDh) return null;                    // 「打順に残す」判断は非DHでのみ意味を持つ
        if (s.CurrentPitcher is not { } pitcher) return null;
        if (s.DefenseLineup is not { } lineup || s.SwappableSlots is not { } swappable) return null;

        // 元投手の打力（ミート・パワーの平均）が閾値未満なら普通に替える＝打線に残す価値がない。
        if ((pitcher.Contact + pitcher.Power) / 2.0 < _ai.ConvertReliefMinBatting) return null;

        Field.FieldPosition? target = null;
        var bestWeight = double.PositiveInfinity;
        var bestFielding = int.MaxValue;
        var n = System.Math.Min(lineup.Count, swappable.Count);
        for (var i = 0; i < n; i++)
        {
            if (!swappable[i] || DefenseWeight(lineup[i].Position) is not { } w) continue;
            var f = lineup[i].Fielding;
            if (w > bestWeight || (w == bestWeight && f >= bestFielding)) continue;
            bestWeight = w; bestFielding = f; target = lineup[i].Position;
        }
        return target;
    }

    /// <summary>
    /// 横断ランキングによる継投人選（issue #209 Phase B, 設計書11 §4）。控え全員を実能力値
    /// （<see cref="Players.PitcherAttributes.ReliefScore"/>）で比較して最良を返す（RNG不使用＝決定論）。
    ///
    /// 同点のときだけ「投手として置かれている控え」を優先する（オーナー判断 2026-08-02: 現状維持）。
    /// 判定の材料は選手の属性ではなく<b>配置</b>（<see cref="FieldPosition.Pitcher"/>）＝#366 §④ が
    /// 抹殺対象外とした軸で、旧 <c>c.Pitching is not null</c> と生成経路上つねに同値
    /// （<c>StrengthTeamFactory.onMound</c> も <c>RosterTeamBuilder.asPitcher</c> も守備位置と連動していた）。
    /// <see cref="Players.PitcherAttributes.ReliefScore"/> は整数和なので同点は実際に起きる
    /// （実測: 校力35で最大3.63%・校力50以上では0%）。
    /// 候補が無ければ null（engine 既定＝控えの先頭にフォールバック）。
    /// </summary>
    private static Players.Player? PickReliever(in PitchingChangeSituation s)
    {
        Players.Player? bestPlaced = null; var bestPlacedScore = double.NegativeInfinity;
        Players.Player? bestOther = null; var bestOtherScore = double.NegativeInfinity;
        foreach (var c in s.ReliefCandidates)
        {
            // 全選手が投手能力を持つ（#381）＝ランク不能で除外される候補はもう無い。
            var score = c.Pitching.ReliefScore();
            if (c.Position == Field.FieldPosition.Pitcher)
            {
                if (score > bestPlacedScore) { bestPlacedScore = score; bestPlaced = c; }
            }
            else
            {
                if (score > bestOtherScore) { bestOtherScore = score; bestOther = c; }
            }
        }
        if (bestPlaced is null) return bestOther;                        // 投手枠の控えゼロ→守備の誰かが上がる
        if (bestOther is not null && bestOtherScore > bestPlacedScore) return bestOther;  // 上回れば配置外から
        return bestPlaced;
    }

    /// <summary>
    /// 高度な継投トリガーの条件判定（RNG不使用）。③校風で実効ティアを補正し（豪腕依存＝引っ張る／
    /// 守り勝つ＝早め）、②各トリガーの引き出し（MinTier）を満たしたものから理由を返す。既定は MinTier=99
    /// のため誰も通らず null＝恒等。
    /// </summary>
    private PitchingChangeReason? EvaluateAdvancedRelief(in PitchingChangeSituation s)
    {
        // ③ 校風による実効ティア補正。
        var effTier = _profile.TierRank + _profile.Style switch
        {
            SchoolStyle.AceDependent => -_ai.AceDependentReliefTierPenalty,   // 引っ張る＝関門を通りにくく
            SchoolStyle.DefensiveMinded => _ai.DefensiveMindedReliefTierBonus, // 厚め・早め
            _ => 0,
        };

        // 崩れ（連打・大量失点の火消し）: G-F でも持つ基本的な引き出し（設計書11 §4）。
        if (effTier >= _ai.BlowupReliefMinTier && s.RunsAllowedThisInning >= _ai.BlowupRunsInInning)
        {
            return PitchingChangeReason.Blowup;
        }

        // 動揺（連続出塁で動じた投手を切替。現状は伝令・イニング跨ぎでしか解けない, 設計書09/§4）。
        if (effTier >= _ai.RattledReliefMinTier && s.PitcherRattled)
        {
            return PitchingChangeReason.Rattled;
        }

        // 僅差終盤（点差×イニング）: 疲労margin を前倒しした早め継投で締める。
        if (effTier >= _ai.CloseLateReliefMinTier
            && s.Inning >= _ai.CloseLateFromInning
            && System.Math.Abs(s.DefenseScoreDiff) <= _ai.CloseLateMaxScoreDiff
            && s.FatiguePitches >= s.StaminaTarget + s.RelieveMargin * _ai.CloseLateReliefMarginFactor)
        {
            return PitchingChangeReason.CloseLate;
        }

        return null;
    }

    /// <summary>
    /// 打順の並べ替え（設計書11 §4「オーダー編成」, issue #48）。③豪腕依存は「打線は水物」で
    /// 調子によるいじりをしない。①戦術眼が足りないと気づかず見送る（他の判断と同じ丸め方）。
    /// </summary>
    public IReadOnlyList<Players.Player> ComposeBattingOrder(IReadOnlyList<Players.Player> order, IRandomSource rng)
    {
        if (_profile.Style == SchoolStyle.AceDependent) return order;
        if (!MathUtil.Chance(_optimalProb, rng)) return order;
        return _inner.ComposeBattingOrder(order);
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
                // issue #67: 機動力校は三盗・本盗も同じ重みで多用する（狙う塁ごとに専用係数は設けない）。
                // 三盗・本盗は解決式の実現可能レンジ自体が二盗よりずっと低い（StealThirdMinSuccess/
                // StealHomeMinSuccessのコメント参照）ため、下限クランプは二盗と同じ0.4を流用しない
                // （0.4だと素の既定値より緩和後の値が高くなり、校風で"渋くなる"逆転が起きる）。
                // issue steal-thresholds-unreachable（2026-08-27）で素の既定値を0.30→0.045へ下げたのに合わせ、
                // 下限も0.05→0.01へ下げる（0.05のままだと 0.045-0.08 の緩和後がまた既定値を上回って同じ逆転が起きる）。
                StealThirdProb = Clamp01(b.StealThirdProb * ai.SmallBallStealFactor),
                StealThirdMinSuccess = MathUtil.Clamp(b.StealThirdMinSuccess - ai.SmallBallStealMinSuccessRelax, 0.01, 0.99),
                StealHomeProb = Clamp01(b.StealHomeProb * ai.SmallBallStealFactor),
                StealHomeMinSuccess = MathUtil.Clamp(b.StealHomeMinSuccess - ai.SmallBallStealMinSuccessRelax, 0.0, 0.99),
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
