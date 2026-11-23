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
    private readonly Nation.LineupCoefficients _lineup;
    private readonly SkillCoefficients _skills;

    /// <param name="skills">特殊能力の係数（クイック○×を盗塁の見込みへ読む・issue #907）。既定＝C#既定（YAML と一致）。</param>
    public StandardTacticsBrain(TacticsCoefficients? coefficients = null, BaserunningCoefficients? baserunning = null,
        FormCoefficients? form = null, Nation.LineupCoefficients? lineup = null, SkillCoefficients? skills = null)
    {
        _c = coefficients ?? new TacticsCoefficients();
        _br = baserunning ?? new BaserunningCoefficients();
        _form = form ?? new FormCoefficients();
        _lineup = lineup ?? new Nation.LineupCoefficients();
        _skills = skills ?? new SkillCoefficients();
    }

    /// <summary>この采配が読む特殊能力の係数（継投の人選で火消しを読む AiTacticsBrain と共有）。</summary>
    internal SkillCoefficients Skills => _skills;

    public OffensiveSign CallOffense(in TacticsSituation s, IRandomSource rng)
    {
        // スクイズ: 三塁走者・1アウト以下・終盤の接戦・バントできる打者（設計書09 §1 / 02 §4.4）。
        if (s.OnThird is not null && s.Outs <= 1
            && System.Math.Abs(s.ScoreDiff) <= _c.SqueezeMaxDiffAbs
            && (s.Inning >= _c.SqueezeFromInning || s.TieBreak)
            && P(s.Batter.Bunt) >= _c.SqueezeMinBunt
            && rng.NextDouble() < _c.SqueezeProb)
        {
            return OffensiveSign.Squeeze;
        }

        // 送りバント: 無死で走者一塁 or 二塁・接戦・終盤（タイブレークでは価値が跳ね上がる §7）。
        var buntBase = s.OnThird is null && s.Outs == 0
                       && (s.OnFirst is not null || s.OnSecond is not null)
                       && s.ScoreDiff >= -_c.SacBuntMaxBehind && s.ScoreDiff <= _c.SacBuntMaxAhead
                       && P(s.Batter.Power) <= _c.SacBuntMaxPower && P(s.Batter.Bunt) >= _c.SacBuntMinSkill;
        if (buntBase && (s.Inning >= _c.SacBuntFromInning || s.TieBreak))
        {
            var prob = s.TieBreak ? _c.SacBuntTieBreakProb : _c.SacBuntProb;
            if (rng.NextDouble() < prob) return OffensiveSign.SacrificeBunt;
        }

        // エンドラン: 一塁走者・当てられる打者（ミート高・パワー控えめ）。
        if (s.OnFirst is not null && s.OnSecond is null && s.Outs <= 1
            && P(s.Batter.Contact) >= _c.HitAndRunMinContact && P(s.Batter.Power) <= _c.HitAndRunMaxPower
            && rng.NextDouble() < _c.HitAndRunProb)
        {
            return OffensiveSign.HitAndRun;
        }

        // 待て: 制球難の投手から四球・球数を狙う。
        if (P(s.Pitcher.Pitching.Control) <= _c.TakeMaxControl && rng.NextDouble() < _c.TakeProb)
        {
            return OffensiveSign.Take;
        }

        return OffensiveSign.Swing;
    }

    public DefensiveTactics CallDefense(in TacticsSituation s, IRandomSource rng)
    {
        // バントシフト: 相手の送りバント好機（CallOffense の送りバント判定の鏡写し＝点差の窓・打者のバント能力も同じ）を読む。
        var buntThreat = s.OnThird is null && s.Outs == 0
                         && (s.OnFirst is not null || s.OnSecond is not null)
                         && (s.Inning >= _c.SacBuntFromInning || s.TieBreak)
                         && s.ScoreDiff >= -_c.SacBuntMaxBehind && s.ScoreDiff <= _c.SacBuntMaxAhead
                         && P(s.Batter.Power) <= _c.SacBuntMaxPower && P(s.Batter.Bunt) >= _c.SacBuntMinSkill;
        var buntShift = buntThreat && rng.NextDouble() < _c.BuntShiftProb;

        // 内野前進: 三塁走者・2アウト未満・終盤の僅差（守備側リードは最大 InfieldInMaxLead）。
        var defenseLead = -s.ScoreDiff; // 守備側から見たリード
        var infield = s.OnThird is not null && s.Outs < 2
                      && s.Inning >= _c.InfieldInFromInning && defenseLead <= _c.InfieldInMaxLead
            ? DefenseDepth.In
            : DefenseDepth.Normal;

        // 外野: 強打者に後退、最終回同点で決勝走者が二塁なら前進（本塁バックアップ）。
        // 強打者は絶対基準（OutfieldDeepMinPower）と、打線の中で抜けているか（相対基準・issue #976 追補）の両方で見る。
        var slugger = P(s.Batter.Power) >= _c.OutfieldDeepMinPower
                      && (_c.OutfieldDeepAboveLineupMean <= 0.0 || s.OffenseLineupMeanPower <= 0.0
                          || s.Batter.Power >= s.OffenseLineupMeanPower + _c.OutfieldDeepAboveLineupMean);
        var outfield = slugger ? DefenseDepth.Deep
            : s.Inning >= s.RegulationInnings && s.ScoreDiff == 0 && s.OnSecond is not null ? DefenseDepth.In
            : DefenseDepth.Normal;

        // 配球方針: 低め徹底（強打者/三塁走者）＞コントロール重視（制球難）＞おまかせ。
        var control = P(s.Pitcher.Pitching.Control);
        var policy = P(s.Batter.Power) >= _c.KeepLowMinPower || (s.OnThird is not null && s.Outs < 2)
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

        // 敬遠（design-14 P1-3）: 一塁空き・得点圏・強打者・僅差終盤で次打者と勝負。確率（IntentionalWalkProb）が0なら
        // rng.NextDouble() を呼ばずガードでスキップする（既定は #402 で0.6＝通常は引く）。
        var intentionalWalkBase = s.OnFirst is null
            && (s.OnSecond is not null || s.OnThird is not null)
            && P(s.Batter.Power) >= _c.IntentionalWalkMinPower
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
        if (s.Strikes >= 2 && P(s.Base.Batter.Contact) >= _c.PitchTacticsTwoStrikeMinContact
            && rng.NextDouble() < _c.PitchTacticsTwoStrikeForceSwingProb)
        {
            // 追い込まれ矯正: 見逃し三振を避けるため、ゾーンの際どい球にも手を出す。
            batting = PitchBattingOverride.ForceSwing;
        }
        else if (s.Balls >= 3 && s.Strikes == 0)
        {
            // 3-0待て。ただし強打者×終盤×僅差は一発を狙う価値がTakeを上回るため打たせる。
            var swingAway = P(s.Base.Batter.Power) >= _c.PitchTacticsThreeZeroSwingAwayMinPower
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
        else if (_c.PitchTacticsInsideAttackProb > 0.0
                 && s.Strikes > s.Balls
                 && P(s.Base.Batter.Power) < _c.KeepLowMinPower
                 && !(s.Base.OnFirst is not null && s.Base.OnSecond is not null && s.Base.OnThird is not null)
                 && rng.NextDouble() < _c.PitchTacticsInsideAttackProb)
        {
            // 釣り球・内角攻め: 有利カウントで内を突いて見せ球にする（強打者・満塁では使わない）。
            policy = PitchPolicy.InsideAttack;
        }

        var steal = CallStealAttempt(s.Base, rng);

        return batting is null && policy is null && steal is null
            ? null
            : new PitchTacticsDirective(batting, policy, StealAttempt: steal?.Start, StealTarget: steal?.Target ?? StealTarget.Second);
    }

    /// <summary>
    /// 盗塁を試みるか＋始動種別＋狙う塁（設計書12 §5, G3b／設計書15 Phase D-2d／issue #67で三盗へ拡張）。
    /// 旧来は打席頭で一度だけ（<c>ITacticsBrain.CallOffense</c>/<c>CallStartType</c>）判定していたが、
    /// 毎球の独立試行へ置き換えた（＝任意の球の前で発動しうる。解決式・係数はそのまま、試行回数が増える分だけ
    /// 発動タイミングが多様化）。塁状況は排他（一塁のみ在塁＝二盗候補／二塁のみ在塁＝三盗候補）なので同時に複数の狙いが競合することはない。一・三塁の重盗（DoubleStealThirdBreakProb）は
    /// 一塁+三塁在塁が前提でこの分岐に一切入らないため、優先順位の整理は不要（塁状況で自然に排他）。
    /// </summary>
    private (StartType Start, StealTarget Target)? CallStealAttempt(in TacticsSituation s, IRandomSource rng)
    {
        if (s.OnFirst is not null && s.OnSecond is null && s.Outs < 2)
        {
            return AttemptSteal(s.OnFirst, s.Catcher, s.Pitcher, StealTarget.Second, _c.StealMinSuccessFor(s.Outs), _c.StealProb, rng);
        }
        if (s.OnFirst is null && s.OnSecond is not null && s.OnThird is null
            && s.Outs <= _c.StealThirdMaxOuts && System.Math.Abs(s.ScoreDiff) <= _c.StealThirdMaxDiffAbs)
        {
            return AttemptSteal(s.OnSecond, s.Catcher, s.Pitcher, StealTarget.Third, _c.StealThirdMinSuccess, _c.StealThirdProb, rng);
        }
        // 単独の本盗（三塁のみ在塁）は採用しない（issue #788・2026-11-08 監督決定）。
        return null;
    }

    /// <summary>損益分岐からの余裕 <paramref name="margin"/> に対する仕掛ける確率（issue #838）。余裕0未満は0、
    /// 0ちょうども0で、余裕が大きいほど <paramref name="attemptProb"/> へなだらかに近づく（<c>2*logistic(kx)-1</c>）。</summary>
    public static double StealAttemptProbability(double margin, double attemptProb, double steepness)
        => margin < 0.0 ? 0.0 : attemptProb * (2.0 * MathUtil.Logistic(margin * steepness) - 1.0);

    /// <summary>損益分岐（<paramref name="minSuccess"/>）未満は仕掛けない（損をする盗塁を選ばない不変条件は維持）。
    /// 以上のときは、余裕（差分）が大きいほど高頻度・際どいほど低頻度で仕掛け、仕掛けた中でも際どいほど
    /// 好ジャンプ・意表のギャンブルに賭ける（見込みが十分高い盗塁は通常始動で堅実に決める＝無防備リスクを
    /// 負わない）。二盗・三盗で共通の型。
    ///
    /// <para>issue #838: 旧来は閾値以上で一律 <paramref name="attemptProb"/> という二値だった（能力がわずかに
    /// 動くだけで企図数が0↔上限へ跳ぶ「崖」の原因）。差分を0でゼロに固定したロジスティック
    /// （<c>2*logistic(kx)-1</c>, x&gt;=0のみ）に通し、損益分岐ちょうどは0・余裕があるほど
    /// <paramref name="attemptProb"/> へなだらかに近づける＝閾値未満ゼロの不変条件はそのまま、
    /// 閾値直上での「即満額」だけを崖からなだらかな立ち上がりへ変える。</para>
    ///
    /// <para><see cref="TacticsSituation"/> はベースカバー役（遊撃手/三塁手）を持たないため、送球の逸れ
    /// （issue #841）の受け手はCatching=50（恒等）で見込む＝送球精度（捕手）の効きは正しく読むが、
    /// 受け手の捕球能力ぶんの読みは近似（実際の判定は<see cref="KokoSim.Engine.Match.Game.GameEngine"/>
    /// が実際のカバー役で解決する）。</para></summary>
    private (StartType Start, StealTarget Target)? AttemptSteal(
        Player runner, Player catcher, Player pitcher, StealTarget target, double minSuccess, double attemptProb, IRandomSource rng)
    {
        // 読まれる（ピッチアウト）確率まで込みの見込み（issue #838）。読まれない場合だけの見込みで判断すると、
        // 盗塁のうまい走者ほど読まれて実成功が見込みを7ポイントほど下回り、損益分岐を守っても損をしていた。
        // クイック○×（issue #907）: 投手のクイックの差も見込みへ入れる＝判定（GameEngine）と同じ秒で読む。
        var estimate = StealResolver.ExpectedSuccessProbability(runner, catcher, pitcher, _br, target: target,
            pitcherQuickOffsetSeconds: SkillModel.QuickDeliveryOffsetSeconds(pitcher.Skills, _skills));
        var margin = estimate - minSuccess;
        if (margin < 0.0) return null;
        var p = StealAttemptProbability(margin, attemptProb, _c.StealAttemptLogisticSteepness);
        if (rng.NextDouble() >= p) return null;
        var start = estimate < _c.GambleStartMaxSuccess && rng.NextDouble() < _c.GambleStartProb
            ? StartType.Gamble
            : StartType.Normal;
        return (start, target);
    }

    public bool CallOffenseTimeout(in TacticsSituation s, IRandomSource rng)
        => s.OffenseTimeoutsLeft > 0
           && s.PressureIndex >= _c.OffenseTimeoutMinPressure
           && P(s.Batter.Mental) <= _c.OffenseTimeoutMaxMental;

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

        // 代打候補にする打者ミート上限は、得点圏（場面）ではこの分だけ緩める（設計書11 §4）。
        var ceiling = _c.PinchHitContactCeiling + (s.RunnerInScoringPosition ? _c.PinchHitRispCeilingBonus : 0);
        if (EffectiveContact(s.UpcomingBatter) > ceiling) return null;

        // 評価は調子込みミートを土台に、打力の総合（パワー）と左右の相性（対戦投手）を混ぜる（設計書11 §4）。
        // 既定（重み0）では EffectiveContact のみ＝従来と一致。in 引数は捕捉できないのでローカルへ退避する。
        var pitcherThrows = s.PitcherThrows;
        double Value(Player p) => PinchHitValue(p, pitcherThrows);
        var upcoming = Value(s.UpcomingBatter);

        var outgoing = s.UpcomingBatter;
        var best = BestBy(s.Reserves, p => (int)System.Math.Round(Value(p) * 100),
            allow: p => CanFillSlotOf(p, outgoing));
        if (best is null || Value(best) < upcoming + _c.PinchHitImprovement) return null;
        return best;
    }

    /// <summary>
    /// 代打評価値（設計書11 §4）: 調子込みミート＋パワー重み×(実効パワー−50)＋左右の相性ボーナス。
    /// 既定の重み0では調子込みミートそのもの（＝従来の比較と恒等）。
    ///
    /// <para>対左投手経験（issue #1072）との整合レビュー: このボーナスは打席側(Bats)と対戦投手(Throws)の
    /// 一致/不一致だけを見る<b>静的</b>な相性で、経験（<see cref="Players.Player.LeftExperiencePa"/>）という
    /// <b>時間蓄積</b>の概念は見ていない。左投手に対しては元から機能する（右打者を出す＝同じ理由で正しい）ため、
    /// このまま変更なしで整合する。「左腕には特に左腕慣れした打者を」まで踏み込む（経験量で代打の評価を強める）
    /// 拡張は、AI采配の新しい判断軸を増やす大きめの変更になるため今回は対象外（issue #1072「見直す」の結論・
    /// オーナー要望があれば別issue）。</para>
    /// </summary>
    private double PinchHitValue(Player p, Players.Handedness? pitcherThrows)
    {
        var value = (double)EffectiveContact(p);
        if (_c.PinchHitPowerWeight != 0.0)
            value += _c.PinchHitPowerWeight * (EffectivePower(p) - 50);
        if (_c.PinchHitPlatoonBonus != 0.0 && pitcherThrows is { } throws)
            value += _c.PinchHitPlatoonBonus * PlatoonSign(p.Bats, throws);
        return value;
    }

    /// <summary>左右の相性の符号: 対戦投手と逆の打席（スイッチは常に有利）なら+1、同じ側なら−1。</summary>
    private static int PlatoonSign(Players.Handedness bats, Players.Handedness pitcherThrows)
        => bats == Players.Handedness.Switch ? 1 : bats != pitcherThrows ? 1 : -1;

    /// <summary>調子込みのミート実効値（設計書11 §4, issue #48）。当日の出来（dayForm）は含まない。</summary>
    private int EffectiveContact(Player p) => FormModel.EffectiveAbility(P(p.Contact), p.Condition, _form.ContactPerStep);

    /// <summary>調子込みのパワー実効値（打力の総合評価用, 設計書11 §4）。</summary>
    private int EffectivePower(Player p) => FormModel.EffectiveAbility(P(p.Power), p.Condition, _form.PowerPerStep);

    /// <summary>
    /// 表示Lv→今の物差しのLv（issue #1080）。閾値（<c>coefficients.yaml</c> の <c>sac_bunt_min_skill</c> 等）は今の物差しの
    /// 数字のまま置いてあるので、能力の絶対値を閾値に当てる所は必ずこれを通す（差・並びだけを見る所は通さなくても同じ）。
    /// </summary>
    private int P(int lv) => _br.AbilityEffect.Phys(lv);

    /// <summary>この采配が能力を読む物差し（AiTacticsBrain が同じ Δ で読むため）。</summary>
    internal Players.AbilityEffectCoefficients AbilityEffect => _br.AbilityEffect;

    /// <summary>
    /// 打順の並べ替え（設計書11 §4「オーダー編成」, issue #48）。守備位置は変えず、調子（のみ）で
    /// 並び順を安定ソートする＝同条件内は元の並びを保ち、絶不調の打者だけが下がる。能力ベースの
    /// オーダー最適化（校風の好み・左右のバランス等）は本Issueの対象外（設計書11 §4の未実装分）。
    /// 選手交代と同型で RNG は使わない（決定論）。
    /// 投手の枠は打力で決める（issue #360）: 打撃総合が野手平均に届かない投手は席（通常9番）を固定し、
    /// 調子ソートの対象から外す＝非力な投手が絶好調だけで1番を打たない。打撃型エース
    /// （<see cref="Nation.LineupOrderer.BatsWithFielders"/>＝裏試合の編成と同じ単一規則）は野手と
    /// 同じ土俵で調子の微調整を受ける。DH使用時は投手が打順に居ないので全員が従来どおり対象。
    /// </summary>
    public IReadOnlyList<Player> ComposeBattingOrder(IReadOnlyList<Player> order)
    {
        var pitcherIdx = -1;
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i].Position == Field.FieldPosition.Pitcher) { pitcherIdx = i; break; }
        }

        if (pitcherIdx >= 0)
        {
            var pitcher = order[pitcherIdx];
            var fielders = order.Where((_, i) => i != pitcherIdx).ToList();
            if (!Nation.LineupOrderer.BatsWithFielders(pitcher, fielders, _lineup))
            {
                var sorted = fielders.OrderByDescending(p => FormModel.Step(p.Condition)).ToList();
                sorted.Insert(pitcherIdx, pitcher);
                return sorted;
            }
        }

        return order.OrderByDescending(p => FormModel.Step(p.Condition)).ToList();
    }

    /// <summary>
    /// 代走: 終盤に1点を取りにいく場面、鈍足の走者を明確に速い控えへ替える。
    /// 全走者のうち「速さの上積みが最大」の1人を狙う（同値は得点に近い塁が先）。
    /// 投手の走者には出さない（代走は投手の守備位置を引き継ぎ、次の回からそのまま登板してしまう・issue #1294）。
    /// </summary>
    public (Player Runner, Player Sub)? CallPinchRun(in SubstitutionSituation s, IRandomSource rng)
    {
        if (s.Inning < _c.PinchRunFromInning) return null;
        if (s.ScoreDiff < _c.PinchRunMinDiff || s.ScoreDiff > _c.PinchRunMaxDiff) return null;

        (Player Runner, Player Sub)? pick = null;
        var bestGain = int.MinValue;
        foreach (var runner in new[] { s.OnThird, s.OnSecond, s.OnFirst })
        {
            if (runner is null || runner.Position == Field.FieldPosition.Pitcher
                || P(runner.Speed) > _c.PinchRunSpeedCeiling) continue;
            var fastest = BestBy(s.Reserves, p => p.Speed, allow: p => CanFillSlotOf(p, runner));
            if (fastest is null) continue;
            var gain = fastest.Speed - runner.Speed;
            if (gain < _c.PinchRunImprovement || gain <= bestGain) continue;
            bestGain = gain;
            pick = (runner, fastest);
        }
        return pick;
    }

    /// <summary>
    /// 代打・代走で入る控えは退く選手の守備位置を引き継ぐ（<c>TeamState.SubstituteAtSlot</c>）ので、
    /// 次の回にその位置を守れない控えと、ブルペンの投手（投手として置かれた控え）は使わない（issue #1294）。
    /// 左投げを捕手・二塁・三塁・遊撃へ入れない規則は守備固めと同じ単一ソース。
    /// </summary>
    private static bool CanFillSlotOf(Player candidate, Player outgoing)
        => candidate.Position != Field.FieldPosition.Pitcher
           && (candidate.Throws != Players.Handedness.Left || !Field.FieldPositions.IsRightThrowOnly(outgoing.Position));

    /// <summary>守備固め: 終盤にリードを守る場面、守備の穴（投手除く）を明確に上回る控えへ替える。</summary>
    public (Player Out, Player Sub)? CallDefensiveSub(in SubstitutionSituation s, IRandomSource rng)
    {
        if (s.Inning < _c.DefensiveSubFromInning) return null;
        if (s.ScoreDiff < _c.DefensiveSubMinLead) return null;            // リードを守る場面のみ

        // 守備に就いている野手枠だけが対象（投手枠・DH枠は守備固めにならない）。
        var swappable = s.DefenseSlotSwappable;
        var weakest = s.Lineup
            .Where((p, i) => (swappable is null || (i < swappable.Count && swappable[i]))
                             && p.Position != Field.FieldPosition.Pitcher && P(p.Fielding) <= _c.DefensiveSubFieldingCeiling)
            .OrderBy(p => p.Fielding).FirstOrDefault();
        if (weakest is null) return null;

        // 交代で入る選手は**退く選手の守備位置を引き継ぐ**（TeamState.SubstituteAtSlot）。
        // 左投げを捕手・二塁・三塁・遊撃へ入れない（issue ai-lefty-not-at-c-2b-3b-ss）＝
        // 生成側だけ直すと「生成では出ないのに守備固めで出る」形で漏れる。
        var best = BestBy(s.Reserves, p => p.Fielding,
            allow: p => p.Throws != Players.Handedness.Left
                        || !Field.FieldPositions.IsRightThrowOnly(weakest.Position));
        if (best is null || best.Fielding < weakest.Fielding + _c.DefensiveSubImprovement) return null;
        return (weakest, best);
    }

    /// <summary>
    /// 守備位置の入替（issue #255, 設計書09 §2.3）。基準采配は<b>動かさない</b>＝従来の試合結果と
    /// 1ビットも変わらない（恒等・RNG不使用）。守備の組み替えは AI 側の引き出し
    /// （<see cref="AiTacticsBrain.CallDefenseSwap"/>）に閉じる。
    /// </summary>
    public (int SlotA, int SlotB)? CallDefenseSwap(in DefenseSwapSituation s, IRandomSource rng) => null;

    /// <summary>
    /// 継投（設計書11 §4, issue #209）。基準采配は従来の疲労球数トリガーそのまま＝engine が事前計算した
    /// <see cref="PitchingChangeSituation.FatigueTriggered"/>（目安投球数＋疲労margin。ハードキャップ撤廃, issue #356）または週間球数制限で
    /// 替える。RNG は使わない（決定論・恒等）。人選は engine 側の bullpen 先頭のまま（横断ランキングは Phase B）。
    /// </summary>
    public PitchingChangeDecision? CallPitchingChange(in PitchingChangeSituation s, IRandomSource rng)
    {
        if (s.FatigueTriggered) return new PitchingChangeDecision(PitchingChangeReason.Fatigue);
        if (s.AtWeeklyLimit) return new PitchingChangeDecision(PitchingChangeReason.WeeklyLimit);
        return null;
    }

    /// <summary>控えから score が最大の選手を決定論で選ぶ（同値は控え順で先勝ち）。</summary>
    /// <param name="allow">起用してよいか（null＝全員可）。守備位置の制約を掛けるのに使う。</param>
    private static Player? BestBy(
        IReadOnlyList<Player> bench, System.Func<Player, int> score, System.Func<Player, bool>? allow = null)
    {
        Player? best = null;
        var bestScore = int.MinValue;
        foreach (var p in bench)
        {
            if (allow != null && !allow(p)) continue;
            var v = score(p);
            if (v > bestScore) { bestScore = v; best = p; }
        }
        return best;
    }
}
