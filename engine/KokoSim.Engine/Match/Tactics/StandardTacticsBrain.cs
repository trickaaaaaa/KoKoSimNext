using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Tactics;

/// <summary>
/// 基準采配（設計書09）。セオリーに沿った判断の実装で、委任采配と敵AI（設計書11）の土台。
/// 敵AIは本クラスの判断に「監督能力値ミス率×AIティア×校風」の三層を被せる＝裏ロジックなし。
/// 閾値はすべて TacticsCoefficients（YAML）駆動。
/// </summary>
public sealed class StandardTacticsBrain : ITacticsBrain, IPitchTacticsBrain
{
    private readonly TacticsCoefficients _c;
    private readonly BaserunningCoefficients _br;
    private readonly FormCoefficients _form;

    public StandardTacticsBrain(TacticsCoefficients? coefficients = null, BaserunningCoefficients? baserunning = null,
        FormCoefficients? form = null)
    {
        _c = coefficients ?? new TacticsCoefficients();
        _br = baserunning ?? new BaserunningCoefficients();
        _form = form ?? new FormCoefficients();
    }

    public OffensiveSign CallOffense(in TacticsSituation s, IRandomSource rng)
    {
        // スクイズ: 三塁走者・1アウト以下・終盤の接戦・バントできる打者（設計書09 §1 / 02 §4.4）。
        if (s.OnThird is not null && s.Outs <= 1
            && System.Math.Abs(s.ScoreDiff) <= _c.SqueezeMaxDiffAbs
            && (s.Inning >= _c.SqueezeFromInning || s.TieBreak)
            && s.Batter.Bunt >= _c.SqueezeMinBunt
            && rng.NextDouble() < _c.SqueezeProb)
        {
            return OffensiveSign.Squeeze;
        }

        // 送りバント: 無死で走者一塁 or 二塁・接戦・終盤（タイブレークでは価値が跳ね上がる §7）。
        var buntBase = s.OnThird is null && s.Outs == 0
                       && (s.OnFirst is not null || s.OnSecond is not null)
                       && s.ScoreDiff >= -_c.SacBuntMaxBehind && s.ScoreDiff <= _c.SacBuntMaxAhead
                       && s.Batter.Power <= _c.SacBuntMaxPower && s.Batter.Bunt >= _c.SacBuntMinSkill;
        if (buntBase && (s.Inning >= _c.SacBuntFromInning || s.TieBreak))
        {
            var prob = s.TieBreak ? _c.SacBuntTieBreakProb : _c.SacBuntProb;
            if (rng.NextDouble() < prob) return OffensiveSign.SacrificeBunt;
        }

        // エンドラン: 一塁走者・当てられる打者（ミート高・パワー控えめ）。
        if (s.OnFirst is not null && s.OnSecond is null && s.Outs <= 1
            && s.Batter.Contact >= _c.HitAndRunMinContact && s.Batter.Power <= _c.HitAndRunMaxPower
            && rng.NextDouble() < _c.HitAndRunProb)
        {
            return OffensiveSign.HitAndRun;
        }

        // 待て: 制球難の投手から四球・球数を狙う。
        if ((s.Pitcher.Pitching?.Control ?? 50) <= _c.TakeMaxControl && rng.NextDouble() < _c.TakeProb)
        {
            return OffensiveSign.Take;
        }

        return OffensiveSign.Swing;
    }

    public DefensiveTactics CallDefense(in TacticsSituation s, IRandomSource rng)
    {
        // バントシフト: 相手の送りバント好機（自分の攻撃判断の鏡写し）を読む。
        var buntThreat = s.OnThird is null && s.Outs == 0
                         && (s.OnFirst is not null || s.OnSecond is not null)
                         && (s.Inning >= _c.SacBuntFromInning || s.TieBreak)
                         && System.Math.Abs(s.ScoreDiff) <= _c.SacBuntMaxBehind
                         && s.Batter.Power <= _c.SacBuntMaxPower;
        var buntShift = buntThreat && rng.NextDouble() < _c.BuntShiftProb;

        // 内野前進: 三塁走者・2アウト未満・終盤の僅差（守備側リードは最大 InfieldInMaxLead）。
        var defenseLead = -s.ScoreDiff; // 守備側から見たリード
        var infield = s.OnThird is not null && s.Outs < 2
                      && s.Inning >= _c.InfieldInFromInning && defenseLead <= _c.InfieldInMaxLead
            ? DefenseDepth.In
            : DefenseDepth.Normal;

        // 外野: 強打者に後退、最終回同点で決勝走者が二塁なら前進（本塁バックアップ）。
        var outfield = s.Batter.Power >= _c.OutfieldDeepMinPower ? DefenseDepth.Deep
            : s.Inning >= s.RegulationInnings && s.ScoreDiff == 0 && s.OnSecond is not null ? DefenseDepth.In
            : DefenseDepth.Normal;

        // 配球方針: 低め徹底（強打者/三塁走者）＞コントロール重視（制球難）＞おまかせ。
        var control = s.Pitcher.Pitching?.Control ?? 50;
        var policy = s.Batter.Power >= _c.KeepLowMinPower || (s.OnThird is not null && s.Outs < 2)
            ? PitchPolicy.KeepLow
            : control <= _c.ControlFirstMaxControl ? PitchPolicy.ControlFirst
            : PitchPolicy.Auto;

        // ギア: 勝負どころは飛ばし、大量リードは流す（設計書02 §1.1f）。
        var gear = PitcherGear.Normal;
        var inningsLeft = s.RegulationInnings - s.Inning;
        if (inningsLeft <= _c.GearPushInningsLeft
            && System.Math.Abs(s.ScoreDiff) <= _c.GearPushMaxDiffAbs
            && (s.OnSecond is not null || s.OnThird is not null))
        {
            gear = PitcherGear.Push;
        }
        else if (defenseLead >= _c.GearCoastMinLead)
        {
            gear = PitcherGear.Coast;
        }

        // 敬遠（design-14 P1-3）: 一塁空き・得点圏・強打者・僅差終盤で次打者と勝負。既定(IntentionalWalkProb=0)
        // では rng.NextDouble() 自体を呼ばずガードでスキップ＝Brainつきの試合でも従来の乱数消費順・結果と完全一致。
        var intentionalWalkBase = s.OnFirst is null
            && (s.OnSecond is not null || s.OnThird is not null)
            && s.Batter.Power >= _c.IntentionalWalkMinPower
            && (s.Inning >= _c.IntentionalWalkFromInning || s.TieBreak)
            && System.Math.Abs(s.ScoreDiff) <= _c.IntentionalWalkMaxDiffAbs;
        var intentionalWalk = intentionalWalkBase && _c.IntentionalWalkProb > 0.0
            && rng.NextDouble() < _c.IntentionalWalkProb;

        return new DefensiveTactics
        {
            Infield = infield,
            Outfield = outfield,
            BuntShift = buntShift,
            Policy = policy,
            Gear = gear,
            IntentionalWalk = intentionalWalk,
        };
    }

    /// <summary>
    /// 1球采配（設計書15 §2.3, C-2）。打席頭の方針（<see cref="CallOffense"/>/<see cref="CallDefense"/>）は
    /// このカウントに関係なく決まっているため、ここではカウント依存の上書きだけを判断する。
    /// 対象は強攻/待て（<see cref="PitchBattingOverride"/>）と配球方針のみ（設計書15 Phase C スコープ）。
    /// </summary>
    public PitchTacticsDirective? CallPitchAction(in PitchTacticsSituation s, IRandomSource rng)
    {
        PitchBattingOverride? batting = null;
        if (s.Strikes >= 2 && s.Base.Batter.Contact >= _c.PitchTacticsTwoStrikeMinContact
            && rng.NextDouble() < _c.PitchTacticsTwoStrikeForceSwingProb)
        {
            // 追い込まれ矯正: 見逃し三振を避けるため、ゾーンの際どい球にも手を出す。
            batting = PitchBattingOverride.ForceSwing;
        }
        else if (s.Balls >= 3 && s.Strikes == 0)
        {
            // 3-0待て。ただし強打者×終盤×僅差は一発を狙う価値がTakeを上回るため打たせる。
            var swingAway = s.Base.Batter.Power >= _c.PitchTacticsThreeZeroSwingAwayMinPower
                && System.Math.Abs(s.Base.ScoreDiff) <= _c.PitchTacticsThreeZeroSwingAwayMaxDiffAbs
                && s.Base.Inning >= _c.PitchTacticsThreeZeroSwingAwayFromInning;
            if (!swingAway && rng.NextDouble() < _c.PitchTacticsThreeZeroTakeProb)
            {
                batting = PitchBattingOverride.ForceTake;
            }
        }

        PitchPolicy? policy = null;
        if (s.Strikes >= 2 && rng.NextDouble() < _c.PitchTacticsPutAwayProb)
        {
            // 決め球: 変化球中心へ切り替える。
            policy = PitchPolicy.BreakingHeavy;
        }
        else if (s.Balls >= _c.PitchTacticsControlFirstMinBalls)
        {
            // 不利カウント: ゾーンに集めて四球を避ける。
            policy = PitchPolicy.ControlFirst;
        }

        var steal = CallStealAttempt(s.Base, rng);

        return batting is null && policy is null && steal is null
            ? null
            : new PitchTacticsDirective(batting, policy, StealAttempt: steal?.Start, StealTarget: steal?.Target ?? StealTarget.Second);
    }

    /// <summary>
    /// 盗塁を試みるか＋始動種別＋狙う塁（設計書12 §5, G3b／設計書15 Phase D-2d／issue #67で三盗・本盗へ拡張）。
    /// 旧来は打席頭で一度だけ（<c>ITacticsBrain.CallOffense</c>/<c>CallStartType</c>）判定していたが、
    /// 毎球の独立試行へ置き換えた（＝任意の球の前で発動しうる。解決式・係数はそのまま、試行回数が増える分だけ
    /// 発動タイミングが多様化）。塁状況は排他（一塁のみ在塁＝二盗候補／二塁のみ在塁＝三盗候補／三塁のみ在塁＝
    /// 本盗候補）なので同時に複数の狙いが競合することはない。一・三塁の重盗（DoubleStealThirdBreakProb）は
    /// 一塁+三塁在塁が前提でこの分岐に一切入らないため、優先順位の整理は不要（塁状況で自然に排他）。
    /// </summary>
    private (StartType Start, StealTarget Target)? CallStealAttempt(in TacticsSituation s, IRandomSource rng)
    {
        if (s.OnFirst is not null && s.OnSecond is null && s.Outs < 2)
        {
            return AttemptSteal(s.OnFirst, s.Catcher, StealTarget.Second, _c.StealMinSuccess, _c.StealProb, rng);
        }
        if (s.OnFirst is null && s.OnSecond is not null && s.OnThird is null
            && s.Outs <= _c.StealThirdMaxOuts && System.Math.Abs(s.ScoreDiff) <= _c.StealThirdMaxDiffAbs)
        {
            return AttemptSteal(s.OnSecond, s.Catcher, StealTarget.Third, _c.StealThirdMinSuccess, _c.StealThirdProb, rng);
        }
        if (s.OnFirst is null && s.OnSecond is null && s.OnThird is not null
            && s.Outs <= _c.StealHomeMaxOuts && System.Math.Abs(s.ScoreDiff) <= _c.StealHomeMaxDiffAbs)
        {
            // 本盗は超高閾値のギャンブル枠（design-14）: 仕掛けると決めたら常に好ジャンプの一発勝負。
            var estimate = StealResolver.SuccessProbability(s.OnThird, s.Catcher, _br, target: StealTarget.Home);
            if (estimate < _c.StealHomeMinSuccess || rng.NextDouble() >= _c.StealHomeProb) return null;
            return (StartType.Gamble, StealTarget.Home);
        }
        return null;
    }

    /// <summary>成功見込みがこの値以上のときだけ確率的に仕掛け、際どいほど好ジャンプ・意表のギャンブルに賭ける
    /// （見込みが十分高い盗塁は通常始動で堅実に決める＝無防備リスクを負わない）。二盗・三盗で共通の型。</summary>
    private (StartType Start, StealTarget Target)? AttemptSteal(
        Player runner, Player catcher, StealTarget target, double minSuccess, double attemptProb, IRandomSource rng)
    {
        var estimate = StealResolver.SuccessProbability(runner, catcher, _br, target: target);
        if (estimate < minSuccess || rng.NextDouble() >= attemptProb) return null;
        var start = estimate < _c.GambleStartMaxSuccess && rng.NextDouble() < _c.GambleStartProb
            ? StartType.Gamble
            : StartType.Normal;
        return (start, target);
    }

    public bool CallOffenseTimeout(in TacticsSituation s, IRandomSource rng)
        => s.OffenseTimeoutsLeft > 0
           && s.PressureIndex >= _c.OffenseTimeoutMinPressure
           && s.Batter.Mental <= _c.OffenseTimeoutMaxMental;

    public bool CallDefenseTimeout(in TacticsSituation s, IRandomSource rng)
        => s.DefenseTimeoutsLeft > 0
           && s.PitcherRattled
           && s.PressureIndex >= _c.DefenseTimeoutMinPressure;

    // ===== 選手交代（設計書09 §6）。判断は決定論（能力の極値を選ぶ）。RNGは使わない。 =====

    /// <summary>
    /// 代打: 終盤の僅差〜小ビハインドで、非力な打者（投手除く）を明確に上回る控えへ替える。
    /// 評価は生のミートではなく調子込みの実効値（設計書11 §4「代打」, issue #48）。
    /// 絶不調の好打者はケイリングを割り込んで対象になり得るし、絶好調の控えは僅差の比較を覆し得る。
    /// </summary>
    public Player? CallPinchHit(in SubstitutionSituation s, IRandomSource rng)
    {
        if (s.UpcomingBatterIsPitcher) return null;                       // 投手枠代打は継投結合のため後続（C-2対象外）
        if (s.Inning < _c.PinchHitFromInning) return null;
        if (s.ScoreDiff < _c.PinchHitMinDiff || s.ScoreDiff > _c.PinchHitMaxDiff) return null;
        var upcomingContact = EffectiveContact(s.UpcomingBatter);
        if (upcomingContact > _c.PinchHitContactCeiling) return null;

        var best = BestBy(s.Bench, EffectiveContact);
        if (best is null || EffectiveContact(best) < upcomingContact + _c.PinchHitImprovement) return null;
        return best;
    }

    /// <summary>調子込みのミート実効値（設計書11 §4, issue #48）。当日の出来（dayForm）は含まない。</summary>
    private int EffectiveContact(Player p) => FormModel.EffectiveAbility(p.Contact, p.Condition, _form.ContactPerStep);

    /// <summary>
    /// 打順の並べ替え（設計書11 §4「オーダー編成」, issue #48）。守備位置は変えず、調子（のみ）で
    /// 並び順を安定ソートする＝同条件内は元の並びを保ち、絶不調の打者だけが下がる。能力ベースの
    /// オーダー最適化（校風の好み・左右のバランス等）は本Issueの対象外（設計書11 §4の未実装分）。
    /// 選手交代と同型で RNG は使わない（決定論）。
    /// </summary>
    public IReadOnlyList<Player> ComposeBattingOrder(IReadOnlyList<Player> order)
        => order.OrderByDescending(p => FormModel.Step(p.Condition)).ToList();

    /// <summary>代走: 終盤に1点を取りにいく場面、鈍足の走者を明確に速い控えへ替える（得点に近い走者を優先）。</summary>
    public (Player Runner, Player Sub)? CallPinchRun(in SubstitutionSituation s, IRandomSource rng)
    {
        if (s.Inning < _c.PinchRunFromInning) return null;
        if (s.ScoreDiff < _c.PinchRunMinDiff || s.ScoreDiff > 1) return null;

        // 得点に近い塁の走者から見て、鈍足ならば候補。
        var runner = new[] { s.OnThird, s.OnSecond, s.OnFirst }
            .FirstOrDefault(r => r is not null && r.Speed <= _c.PinchRunSpeedCeiling);
        if (runner is null) return null;

        var fastest = BestBy(s.Bench, p => p.Speed);
        if (fastest is null || fastest.Speed < runner.Speed + _c.PinchRunImprovement) return null;
        return (runner, fastest);
    }

    /// <summary>守備固め: 終盤にリードを守る場面、守備の穴（投手除く）を明確に上回る控えへ替える。</summary>
    public (Player Out, Player Sub)? CallDefensiveSub(in SubstitutionSituation s, IRandomSource rng)
    {
        if (s.Inning < _c.DefensiveSubFromInning) return null;
        if (s.ScoreDiff < _c.DefensiveSubMinLead) return null;            // リードを守る場面のみ

        var weakest = s.Lineup
            .Where(p => p.Position != Field.FieldPosition.Pitcher && p.Fielding <= _c.DefensiveSubFieldingCeiling)
            .OrderBy(p => p.Fielding).FirstOrDefault();
        if (weakest is null) return null;

        var best = BestBy(s.Bench, p => p.Fielding);
        if (best is null || best.Fielding < weakest.Fielding + _c.DefensiveSubImprovement) return null;
        return (weakest, best);
    }

    /// <summary>控えから score が最大の選手を決定論で選ぶ（同値は控え順で先勝ち）。</summary>
    private static Player? BestBy(IReadOnlyList<Player> bench, System.Func<Player, int> score)
    {
        Player? best = null;
        var bestScore = int.MinValue;
        foreach (var p in bench)
        {
            var v = score(p);
            if (v > bestScore) { bestScore = v; best = p; }
        }
        return best;
    }
}
