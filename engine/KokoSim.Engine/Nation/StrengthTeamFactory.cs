using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Nation;

/// <summary>
/// チーム力(0〜100)から試合可能な Team を構成する。集計モデルとフルエンジン(GameEngine)の
/// キャリブレーション、および注目試合の中解像度処理に使う（設計書05 §1.4）。
/// </summary>
public static class StrengthTeamFactory
{
    private static readonly FieldPosition[] FieldSlots =
    {
        FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
        FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField,
        FieldPosition.CenterField, FieldPosition.RightField,
    };

    // 控えの布陣（代打/代走/守備固め用）。守備固めが全ポジション成立するよう各守備位置に1人ずつ。
    private static readonly FieldPosition[] BenchSlots =
    {
        FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
        FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField,
        FieldPosition.CenterField, FieldPosition.RightField,
    };

    /// <summary>
    /// 学校から決定論的にチームを組む（校ID＋年度シード）。大会展望の表示と実際の対戦相手ラインナップの
    /// **単一ソース**。同じ校・同じ年度なら常に同一チーム＝展望で見た選手が実戦でそのまま出てくる。
    /// 年度を混ぜるのは3年生が抜けて代替わりするため（年が変わればメンバーも変わる）。
    /// 渡す rng に依存しないので、大会の進行状況や観戦の有無に関係なく再現できる。
    /// <paramref name="modernRules"/>/<paramref name="calendarYear"/> は DH使用判断（issue #54）専用の
    /// 任意入力。両方揃ったときだけ検討する（既定 null＝DH不使用＝従来挙動と完全一致）。
    /// </summary>
    public static Team ForSchool(School school, int yearIndex,
        PersonalityCoefficients? personalityCoeff = null, PlayerNameVocab? nameVocab = null,
        RosterCoefficients? rosterCoeff = null, ModernRules? modernRules = null, int? calendarYear = null,
        EnemyAiCoefficients? aiCoeff = null, FormCoefficients? form = null)
        => Create(school.Strength, school.Name, SeedFor(school.Id, yearIndex),
            personalityCoeff, nameVocab, rosterCoeff, modernRules, calendarYear,
            school.ManagerTraits, aiCoeff, form, school.Style);

    /// <summary>校ID＋年度から生成シードを導出する（決定論・不変条件#2）。</summary>
    public static IRandomSource SeedFor(int schoolId, int yearIndex)
        => new Xoshiro256Random(0x5C40_0000UL ^ (ulong)(long)schoolId ^ ((ulong)(long)yearIndex * 0x9E37_79B9UL));

    public static Team Create(double strength, string name, IRandomSource rng,
        PersonalityCoefficients? personalityCoeff = null, PlayerNameVocab? nameVocab = null,
        RosterCoefficients? rosterCoeff = null, ModernRules? modernRules = null, int? calendarYear = null,
        IReadOnlyList<ManagerTrait>? traits = null, EnemyAiCoefficients? aiCoeff = null,
        FormCoefficients? form = null, SchoolStyle? style = null)
    {
        var pc = personalityCoeff ?? new PersonalityCoefficients();
        var vocab = nameVocab ?? new PlayerNameVocab();
        var rc = rosterCoeff ?? new RosterCoefficients();
        // 精神力の中心は圧縮前の強度と校風から（issue ai-school-mental-fixed-50。AiMentalModel が単一ソース）。
        // 学年アークは Profile で引いた学年に応じて後段で載せる。
        var mental = new AiMentalDraw(AiMentalModel.MentalCenter(strength, style, rc), AiMentalModel.StyleBonus(style, rc));
        // ティア→能力の圧縮（issue #578・Q24 確定2）。ここで1度だけ写す＝以降の控え・中継ぎの
        // 差分（−4/−2/−6/−8）は圧縮後の空間で絶対値のまま効く（チーム内の厚みは縮めない）。
        // 表示ランクは元の strength のまま（確定4＝表示は直さない）。
        var rawStrength = strength;
        strength = rc.CompressStrength(strength);
        // 上位集中＝スター型（issue prefecture-dominance-star）: 圧縮で削った分をエース＋主軸へ戻す。脇役は
        // lift×cast_drop だけ下げてチーム総合を圧縮後の尺度に留める。主軸の守備位置は Fork（主RNG非消費）で選ぶ。
        var lift = rc.StarLift(rawStrength);
        var castCenter = strength - rc.AiStarCastDrop * lift;
        var starLift = lift * (1.0 + rc.AiStarCastDrop);   // 脇役の中心から見た上乗せ＝圧縮後 + lift
        var starSlots = PickStarSlots(rng.Fork(StarSalt), FieldSlots.Length, rc.AiStarHitters);
        var ac = rc.Archetypes;   // 球質タイプ係数は自校生成と同じ RosterCoefficients に同居
        var archSalt = 0x7B15_0000UL;   // 球質タイプ用の Fork ソルト（主RNG非消費）
        var order = new List<Player>(9);
        // 性格・氏名・投打・学年は Fork（親状態非消費）で付与するため、既存の能力ロール列＝乱数消費列を
        // 1ビットも変えない（裏試合の決定論保存）。
        var salt = 0x50E1_0000UL;
        var nameSalt = 0x4E17_0000UL;      // 氏名用の Fork ソルト（主RNG非消費）
        // 同一チーム内で下の名前が被らないよう、採用済みの名前を持ち回る（苗字の重複はOK＝現実にもある）。
        var usedGiven = new HashSet<string>();
        var profileSalt = 0x6A17_0000UL;   // 投打・学年用の Fork ソルト（主RNG非消費）
        // 背番号は高校野球の慣例に沿って採番: 先発の野手＝守備位置番号(2〜9)、エース＝1、控え野手＝10〜17、控え投手＝18〜。
        for (var slot = 0; slot < FieldSlots.Length; slot++)
        {
            var pos = FieldSlots[slot];
            var pr = Profile(rc, rng, ref profileSalt, starter: true, pos);
            order.Add(AiMentalModel.WithGradeArc(
                PositionPlayer(pos, castCenter, rng, pc, salt++, GenName(vocab, rng, ref nameSalt, usedGiven), mental, rc,
                        battingLift: starSlots.Contains(slot) ? starLift : 0.0)
                    with { UniformNumber = PositionNumber(pos), Throws = pr.Throws, Bats = pr.Bats, Grade = pr.Grade },
                pr.Grade, rc));
        }
        // 投手枠に置く4人の「投手寄せ重み」をエース格から順に引く（#377・Fork＝主RNG非消費）。
        // 打撃はこの w の連続量で決まる＝役割ごとの −20 の崖は無い。
        var pitcherW = PitcherWeights(rc, rng, 0x7C77_0000UL);

        var acePr = Profile(rc, rng, ref profileSalt, starter: true);
        order.Add(AiMentalModel.WithGradeArc(
            Pitcher(GenName(vocab, rng, ref nameSalt, usedGiven), castCenter, pitcherW[0], rng, pc, salt++, ac, archSalt++, rc, mental,
                    pitchingLift: starLift)
                with { UniformNumber = 1, Throws = acePr.Throws, Bats = acePr.Bats, Grade = acePr.Grade },
            acePr.Grade, rc));

        var bullpen = new List<Player>();
        var wIndex = 1;
        foreach (var (number, drop) in new[] { (18, 4.0), (19, 2.0) })
        {
            var pr = Profile(rc, rng, ref profileSalt, starter: false);
            bullpen.Add(AiMentalModel.WithGradeArc(
                Pitcher(GenName(vocab, rng, ref nameSalt, usedGiven), castCenter - drop, pitcherW[wIndex++], rng, pc, salt++, ac, archSalt++, rc, mental)
                    with { UniformNumber = number, Throws = pr.Throws, Bats = pr.Bats, Grade = pr.Grade },
                pr.Grade, rc));
        }
        // 背番号20（現代のベンチ入り20人制）。能力ロールは Fork＝主RNG非消費で決定論を保つ。
        var relief20 = rng.Fork(0x9A20_0000UL);
        var pr20 = Profile(rc, rng, ref profileSalt, starter: false);
        bullpen.Add(AiMentalModel.WithGradeArc(
            Pitcher(GenName(vocab, rng, ref nameSalt, usedGiven), castCenter - 6, pitcherW[wIndex], relief20, pc, salt++, ac, archSalt++, rc, mental)
                with { UniformNumber = 20, Throws = pr20.Throws, Bats = pr20.Bats, Grade = pr20.Grade },
            pr20.Grade, rc));

        // 控え（全員生成・ベンチ入りメンバとして背番号10〜を採番）。能力・氏名とも Fork ストリーム由来＝
        // 主RNG消費を1ビットも変えない。Team.Tactics 未設定の相手校では MaybeOffenseSubs が Bench 前に早期 return するため、
        // 控えを持たせても現行の試合進行・裏試合の決定論には影響しない（采配ブレイン注入時のみ交代候補として機能）。
        var bench = new List<Player>(BenchSlots.Length);
        var benchSalt = 0x8E27_0000UL;
        for (var i = 0; i < BenchSlots.Length; i++)
        {
            var ability = rng.Fork(benchSalt++);   // 控えの能力ロールは Fork（主RNG非消費）
            var pr = Profile(rc, rng, ref profileSalt, starter: false, BenchSlots[i]);
            bench.Add(AiMentalModel.WithGradeArc(
                PositionPlayer(BenchSlots[i], castCenter - 8, ability, pc, salt++, GenName(vocab, rng, ref nameSalt, usedGiven), mental, rc)
                    with { UniformNumber = 10 + i, Throws = pr.Throws, Bats = pr.Bats, Grade = pr.Grade },
                pr.Grade, rc));
        }

        // 監督傾向・抜擢型（issue #55）: Compose の前に控えを先発へ抜擢する（オーダー編成に効く傾向）。
        // 調子ロールは主RNGを乱さない Fork ストリーム＝既存の能力生成列を1ビットも変えない（帯・決定論保護）。
        var ai = aiCoeff ?? new EnemyAiCoefficients();
        if (ManagerTraitEffects.HasPromoter(traits))
        {
            ApplyPromoter(order, bench, rng.Fork(0x9C55_0000UL), rc.Lineup, ai, form ?? new FormCoefficients());
        }

        var team = new Team
        {
            Name = name, BattingOrder = order, PitcherSlot = 8,
            Reserves = TeamReserves.Merge(bullpen, bench),
        };
        // 打順編成＋DH使用判断（issue #54, 設計書11 §4）。既に確定した能力ロールだけを材料にする決定論変換
        // （rng は使わない）＝展望（TournamentPreview）と実戦（Shell）が同じここを通り自動で一致する。
        var composed = LineupOrderer.Compose(team, rc.Lineup, modernRules, calendarYear);
        // 主将（設計書09 §8）: 自校と同じ規則（最上級生の統率力最大）で AI 校にも置く（issue ai-school-mental-fixed-50）。
        // 参照は Compose 後の打順・控えの実体から選ぶ（Team.Captain の在場判定は参照一致）。
        var captain = CaptainSelector.SelectFromPlayers(AllPlayers(composed));
        // 監督傾向・継投系（issue #55, 決定4: B-1）: エース酷使/継投早めのチームだけ継投しきい値を差し替える。
        // 傾向なしは null＝GameContext.Fatigue に落ちる（従来と完全一致・帯不変）。
        return composed with
        {
            Captain = captain,
            Fatigue = ManagerTraitEffects.FatigueOverride(new FatigueCoefficients(), traits, ai),
        };
    }

    /// <summary>主将候補＝打順＋控え＋（DH制の）先発投手の全員（Compose 後の参照）。</summary>
    internal static IEnumerable<Player> AllPlayers(Team t)
    {
        foreach (var p in t.BattingOrder) yield return p;
        foreach (var p in t.Reserves) yield return p;
        if (t.StartingPitcher is { } sp) yield return sp;
    }

    /// <summary>
    /// 抜擢型（issue #55, 監督傾向 <see cref="ManagerTrait.Promoter"/>）。控え（背番号10〜）に決定論の調子を振り、
    /// 好調（Good以上）の控えを、同じ守備位置の先発より「調子込みの起用価値」が上回るなら先発へ抜擢する
    /// （押し出された正位置選手はベンチへ＝守備固め/代打候補として #40 の交代判断に乗る）。調子ロールは渡された
    /// 隔離ストリーム（校ID＋年度から Fork）で順に引くので、大会展望（ForSchool 経由）と実戦が同一結果になる。
    /// order/bench を破壊的に更新する（Compose の前に呼ぶ）。
    /// </summary>
    private static void ApplyPromoter(
        List<Player> order, List<Player> bench, IRandomSource rng,
        LineupCoefficients lineup, EnemyAiCoefficients ai, FormCoefficients form)
    {
        if (bench.Count == 0) return;
        var sigma = form.StationaryConditionSigma;

        var bestIdx = -1;
        var bestStep = 0;
        var bestScore = double.NegativeInfinity;
        var bestCv = 0.0;
        var bestCond = Condition.Normal;
        for (var i = 0; i < bench.Count; i++)
        {
            // 全控えを1回ずつロール（分岐に関係なく消費数を固定＝決定論）。
            var cv = rng.NextGaussian(0, sigma);
            var cond = FormModel.Quantize(cv);
            var step = FormModel.Step(cond);
            if (step < ai.PromoterMinConditionStep) continue;   // Good未満は抜擢対象外
            var score = LineupOrderer.BattingScore(bench[i], lineup) + ai.PromoterConditionWeight * step;
            if (step > bestStep || (step == bestStep && score > bestScore))
            {
                bestIdx = i;
                bestStep = step;
                bestScore = score;
                bestCv = cv;
                bestCond = cond;
            }
        }
        if (bestIdx < 0) return;   // 今年は好調の控えがいない＝抜擢なし

        var benchPlayer = bench[bestIdx];
        // 同じ守備位置の先発（末尾＝投手枠は除外）を探す。
        var slot = -1;
        for (var i = 0; i < order.Count - 1; i++)
        {
            if (order[i].Position == benchPlayer.Position) { slot = i; break; }
        }
        if (slot < 0) return;

        var starter = order[slot];
        // 抜擢の価値があるとき（調子込みで先発の素の打撃を上回る）だけ入れ替える＝諸刃を無闇に負わない。
        if (bestScore <= LineupOrderer.BattingScore(starter, lineup)) return;

        order[slot] = benchPlayer with { Condition = bestCond, ConditionValue = bestCv };
        bench[bestIdx] = starter;   // 押し出された先発はベンチへ（守備位置は同一）
    }

    /// <summary>
    /// 投打の利き・学年を Fork（親状態非消費）で抽選する。主RNGの消費列を変えないため決定論を保つ。
    /// 投打の分布は ProspectGenerator.Create（設計書01 §1.1c）と同じ条件付き抽選＝自校と同一の傾向にする。
    /// 学年は先発を上級生寄り・控えを下級生寄りに配分する（高校野球の実感に沿う）。
    /// </summary>
    /// <param name="position">
    /// この選手が就く守備位置（分かっているとき）。<see cref="FieldPositions.IsRightThrowOnly"/> の位置なら
    /// 左投げを引いても右投げに固定する（issue ai-lefty-not-at-c-2b-3b-ss）。
    /// null＝位置が決まっていない経路（永続AIロスターの生成。配置は <c>AiTeamBuilder</c> が後で決める）。
    /// </param>
    internal static (Handedness Throws, Handedness Bats, int Grade) Profile(
        RosterCoefficients c, IRandomSource rng, ref ulong profileSalt, bool starter,
        FieldPosition? position = null)
    {
        var r = rng.Fork(profileSalt++);
        var throws = r.NextDouble() < c.ThrowLeftProb ? Handedness.Left : Handedness.Right;
        // ⚠ 乱数は**引いてから捨てる**（issue #525/#629 と同じ流儀）。引き直すと Fork の消費列が
        // ずれて学年・以降の生成まで動く＝裏試合の決定論と帯が動く。下の Bats も右投げ側の
        // 条件分布へ落ちるだけで**引く回数は左右どちらでも1回**なので消費不変。
        if (throws == Handedness.Left && position is { } p && FieldPositions.IsRightThrowOnly(p)) throws = Handedness.Right;
        Handedness bats;
        if (r.NextDouble() < c.SwitchProb) bats = Handedness.Switch;
        else if (throws == Handedness.Left) bats = r.NextDouble() < c.BatLeftGivenLeftThrow ? Handedness.Left : Handedness.Right;
        else bats = r.NextDouble() < c.BatLeftGivenRightThrow ? Handedness.Left : Handedness.Right;

        var g = r.NextDouble();
        var grade = starter
            ? (g < 0.50 ? 3 : g < 0.85 ? 2 : 1)    // 先発: 3年5割・2年35%・1年15%
            : (g < 0.20 ? 3 : g < 0.55 ? 2 : 1);   // 控え: 3年2割・2年35%・1年45%
        return (throws, bats, grade);
    }

    /// <summary>守備位置→先発背番号（投1・捕2・一3・二4・三5・遊6・左7・中8・右9）。</summary>
    private static int PositionNumber(FieldPosition pos) => pos switch
    {
        FieldPosition.Pitcher => 1,
        FieldPosition.Catcher => 2,
        FieldPosition.FirstBase => 3,
        FieldPosition.SecondBase => 4,
        FieldPosition.ThirdBase => 5,
        FieldPosition.Shortstop => 6,
        FieldPosition.LeftField => 7,
        FieldPosition.CenterField => 8,
        FieldPosition.RightField => 9,
        _ => 0,
    };

    /// <summary>
    /// 氏名を Fork（親状態非消費）で生成する。主RNGの消費列を変えないため決定論を保つ。
    /// 下の名前の重複回避リロールも Fork ストリーム内で完結する。
    /// </summary>
    private static string GenName(PlayerNameVocab vocab, IRandomSource rng, ref ulong nameSalt,
        ISet<string> usedGiven)
        => PlayerNameGenerator.Generate(vocab, rng.Fork(nameSalt++), usedGiven);

    internal static int Ability(double center, IRandomSource rng)
        => (int)MathUtil.Clamp(Math.Round(rng.NextGaussian(center, 6)), 10, 99);

    /// <summary>球質タイプのオフセットを載せたレベルを能力域へ丸める。</summary>
    private static int Lv(double level) => (int)MathUtil.Clamp(Math.Round(level), 10, 99);

    /// <summary>球種ランク（PitchRank 近傍の個体差）。</summary>
    private static int Grade(int pitchRank, IRandomSource rng)
        => (int)MathUtil.Clamp(Math.Round(rng.NextGaussian(pitchRank, 8)), 1, 100);

    /// <summary>潜在投手能力の Fork salt（性格 salt と衝突しない別ストリーム。issue #209 Phase B）。</summary>
    // 旧 LatentPitchingSalt（#381 で改称）。値を変えると全AI校の非マウンド選手の投手能力が変わるので触らない。
    private const ulong UntrainedPitchingSalt = 0x7A17_0000_0000_0000UL;

    /// <summary>
    /// 投手寄せ重み w を引くときの候補プール人数。使い捨て生成（<see cref="Create"/>）専用で、
    /// 永続ロスター側は #384 で「コホート全員が適性を持ち選抜で配置が決まる」形になったため対応物は無い
    /// （旧 <c>PersistentRosterCoefficients.PitcherPoolSize</c> は同issueで削除）。
    /// </summary>
    private const int PitcherWeightPoolSize = 6;

    /// <summary>主軸の守備位置を選ぶ Fork ソルト（主RNG非消費・他ストリームと別の上位ビット）。</summary>
    private const ulong StarSalt = 0x57A2_0000UL;

    /// <summary>
    /// 先発野手 <paramref name="slotCount"/> 枠から主軸 <paramref name="count"/> 枠を重複なしで選ぶ（Fisher–Yates の先頭 count）。
    /// 全枠を1回ずつ引く（消費数固定）。<paramref name="count"/> ≤ 0 なら空。
    /// </summary>
    internal static HashSet<int> PickStarSlots(IRandomSource rng, int slotCount, int count)
    {
        var slots = new int[slotCount];
        for (var i = 0; i < slotCount; i++) slots[i] = i;
        for (var i = 0; i < slotCount - 1; i++)
        {
            var j = rng.NextInt(i, slotCount);
            (slots[i], slots[j]) = (slots[j], slots[i]);
        }
        var chosen = new HashSet<int>();
        for (var i = 0; i < Math.Min(Math.Max(0, count), slotCount); i++) chosen.Add(slots[i]);
        return chosen;
    }

    /// <summary>
    /// 投手枠に置く選手たちの「投手寄せ重み」をエース格から順に返す（#377）。
    /// 適性プールを独立Fork（主RNG非消費）でロールし、判定スコア（投手適性＋バイアス−最良守備適性）の
    /// 高い順に並べる。
    /// 使い捨て生成（<see cref="Create"/>）は投手枠の人数が固定なので、選抜でなく順位で重みだけを取る。
    /// </summary>
    internal static double[] PitcherWeights(RosterCoefficients rc, IRandomSource rng, ulong salt)
    {
        var scored = new (double Score, double Weight)[PitcherWeightPoolSize];
        for (var i = 0; i < PitcherWeightPoolSize; i++)
        {
            var apt = AptitudeRoller.Roll(rc, rng.Fork(salt + (ulong)i));
            var pitcherApt = apt[(int)FieldPosition.Pitcher];
            var bestField = AptitudeRoller.BestField(apt);
            scored[i] = (pitcherApt + rc.PitcherAptitudeBias - bestField,
                         PitcherRoleResolver.PitcherWeight(pitcherApt, bestField, rc));
        }
        return scored.OrderByDescending(s => s.Score).Select(s => s.Weight).ToArray();
    }

    /// <summary>
    /// 単一生成器（#377）。役割で生成器を分けない＝**打撃は投手寄せ重み w の連続量**で決まり、
    /// 「投手はミート/パワーが strength−20 固定」という崖は無い。自校
    /// <see cref="Season.ProspectGenerator"/> の「total＋適性 w の連続補間」と同型。
    ///
    /// <paramref name="pitcherWeight"/>=0 は「明確な野手」＝打撃中心が strength そのもの（従来の
    /// <c>PositionPlayer</c> と完全一致）。投手枠に置く選手は <see cref="PitcherWeights"/> の値を渡す。
    /// w が中間の投手ほど打撃を保つ＝「エースで4番」が構造的に出る。
    ///
    /// 投手能力（制球/スタミナ/キレ/球速）を引くかは**配置**で決める（#366 §④: 配置は属性ではない）。
    /// 全選手が PitcherAttributes を持つ形にするのは #366b（#378）の担当。ここで分けているのは
    /// 乱数消費列を野手側で1ビットも変えないため＝この issue の帯の動きを投手側だけに閉じ込める。
    /// </summary>
    /// <param name="battingLift">主軸（スター型）の打撃3能力（ミート/パワー/選球眼）の中心上乗せ。他の能力は脇役と同じ中心。</param>
    /// <param name="pitchingLift">エース格（スター型）の投手4能力（球速/制球/スタミナ/キレ）の中心上乗せ。マウンドに置く選手だけ効く。</param>
    internal static Player CreatePlayer(string playerName, FieldPosition pos, double strength, double pitcherWeight,
        IRandomSource rng, PersonalityCoefficients pc, ulong salt,
        PitcherArchetypeCoefficients ac, ulong archetypeSalt, RosterCoefficients rc,
        AiMentalDraw? mental = null, double battingLift = 0.0, double pitchingLift = 0.0)
    {
        var onMound = pos == FieldPosition.Pitcher;
        // 打撃中心: 本業(strength) ↔ 専門外(strength×OffSpecialtyFactor) を w で補間。主軸の上乗せは補間の後に足す
        // （投手寄せの主軸でも上乗せ量は同じ）。
        var battingCenter = strength + (strength * rc.OffSpecialtyFactor - strength) * pitcherWeight + battingLift;

        // 打撃プロフィール（issue ai-roster-power-top-tier-all-sluggers）: パワーを希少側にしてミートへ振り替え（打撃指標不変）
        // ＋5能力の凸凹配分（重みは専用 Fork から＝主RNG非消費）。Ability() の呼び出し順・回数は従来どおり保つ。
        var prof = AiBattingProfile.Draw(rng.Fork(salt ^ AiBattingProfile.ProfileSalt), rc);
        var contact = Ability((battingCenter + rc.AiPowerSkew) * prof.Contact, rng);
        var power = Ability(Math.Max(0.0, battingCenter - rc.AiPowerSkew) * prof.Power, rng);
        var launch = Ability(50, rng);
        var discipline = Ability((strength + battingLift) * prof.Discipline, rng);
        var speed = Ability(strength, rng);
        var arm = Ability(strength, rng);
        var fielding = Ability(strength * prof.Fielding, rng);
        var catching = Ability(strength * prof.Catching, rng);

        PitcherAttributes? pitching = null;
        if (onMound)
        {
            var pitchingCenter = strength + pitchingLift;
            var control = Ability(pitchingCenter, rng);
            var staminaLevel = Ability(pitchingCenter, rng);
            var pitchRank = Ability(pitchingCenter, rng);
            // 球速も他能力と同じく個体差を持つ（旧実装は強さの決定関数で、同じ強さの学校のエースが全員同じ球速＝
            // AI校に技巧派が存在できなかった）。自校（RosterTeamBuilder）と同じ「球速Level→km/h」の形に揃える。
            var velocityLevel = Ability(pitchingCenter, rng);

            // 球質タイプで配分を振り替える（総合はほぼ不変・Fork＝主RNG非消費）。
            var archetype = PitcherArchetypes.Sample(rng.Fork(archetypeSalt), ac);
            var (dv, dc, ds, dr) = PitcherArchetypes.Offsets(archetype, ac);
            velocityLevel = Lv(velocityLevel + dv);
            control = Lv(control + dc);
            staminaLevel = Lv(staminaLevel + ds);
            pitchRank = Lv(pitchRank + dr);

            pitching = new PitcherAttributes
            {
                MaxVelocityKmh = PitcherAttributes.VelocityKmhFromLevel(velocityLevel),
                Archetype = archetype,
                Control = control,
                StaminaPitches = PitcherAttributes.StaminaPitchesFromLevel(staminaLevel),
                PitchRank = pitchRank,
                // 集計用の簡易レパートリー: ストレート＋変化球2種（ランクは PitchRank 近傍）。
                Repertoire = new[]
                {
                    PitchSlot.FastballOf(pitchRank),
                    new PitchSlot { Type = PitchType.Slider, Power = Grade(pitchRank, rng), Sharpness = Grade(pitchRank, rng) },
                    new PitchSlot { Type = PitchType.Fork, Power = Grade(pitchRank, rng), Sharpness = Grade(pitchRank, rng) },
                },
            };
        }

        // 性格（Fork＝親状態非消費）: 上の能力ロール列に影響しない。
        var personality = pc.Sample(rng.Fork(salt));
        var pp = pc.Profile(personality);
        // 投手能力は全選手が持つ（issue #209 Phase B → #381）。マウンドに置かない選手は主RNGから
        // 投手4能力を引かず、肩から推定して埋める。専用 Fork（性格の salt と別ストリーム）＝主RNG非消費。
        // 呼ぶ位置も条件も旧 LatentPitching と同じなので、生成される値・乱数消費列ともに完全に同一。
        pitching ??= UntrainedPitchingEstimator.Estimate(arm, rng.Fork(salt ^ UntrainedPitchingSalt));

        // 精神力・統率・捕手リード（issue ai-school-mental-fixed-50）: 専用 Fork（性格 salt・潜在投手 salt と別ストリーム）
        // で引く＝主RNGの消費列と上の能力ロールは1ビットも変わらない。中心は呼び出し側が学校の強さ・校風から
        // 決める（AiMentalModel.MentalCenter）。null＝中心 = mental_mean + (strength−50)×slope（学校を知らない呼び出し）。
        var draw = mental ?? new AiMentalDraw(AiMentalModel.MentalCenter(strength, null, rc), 0.0);
        var (mentalLv, leadershipLv, leadLv) = AiMentalModel.Roll(
            draw.Center, draw.StyleBonus, pp, rc, rng.Fork(salt ^ AiMentalModel.MentalSalt));

        return new Player
        {
            Name = playerName,
            Position = pos,
            Contact = contact,
            Power = power,
            LaunchTendency = launch,
            Discipline = discipline,
            Speed = speed,
            ArmStrength = arm,
            // 新パラメータは既存の抽選値から派生（新規RNG非消費）。正式生成は 2B。
            ThrowAccuracy = arm,
            Fielding = fielding,
            Catching = catching,
            Bunt = (int)MathUtil.Clamp(strength, 10, 99),
            Steal = speed,
            Baserunning = speed,
            Pitching = pitching,
            Personality = personality,
            BuntSuccessBonus = pp.BuntSuccessBonus,
            ChanceHitFactor = pp.ChanceHitFactor,
            Mental = mentalLv,
            Leadership = leadershipLv,
            Lead = leadLv,
        };
    }

    /// <summary>守備位置の選手（単一生成器の w=0 呼び出し＝明確な野手）。</summary>
    internal static Player PositionPlayer(FieldPosition pos, double strength, IRandomSource rng,
        PersonalityCoefficients pc, ulong salt, string playerName, AiMentalDraw? mental = null,
        RosterCoefficients? rc = null, double battingLift = 0.0)
        => CreatePlayer(playerName, pos, strength, pitcherWeight: 0.0, rng, pc, salt,
            new PitcherArchetypeCoefficients(), 0UL, rc ?? new RosterCoefficients(), mental, battingLift: battingLift);

    /// <summary>投手枠の選手（単一生成器へ投手寄せ重みを渡す）。</summary>
    internal static Player Pitcher(string name, double strength, double pitcherWeight, IRandomSource rng,
        PersonalityCoefficients pc, ulong salt, PitcherArchetypeCoefficients ac, ulong archetypeSalt,
        RosterCoefficients rc, AiMentalDraw? mental = null, double pitchingLift = 0.0)
        => CreatePlayer(name, FieldPosition.Pitcher, strength, pitcherWeight, rng, pc, salt, ac, archetypeSalt, rc, mental,
            pitchingLift: pitchingLift);
}

/// <summary>
/// AI校の精神力を引くときの中心（学校の強さ・校風から <see cref="Roster.AiMentalModel.MentalCenter"/> で決めた値）と
/// 校風の統率上乗せ。生成器へ「学校」を渡さずに済ませるための小さな束（issue ai-school-mental-fixed-50）。
/// </summary>
public readonly record struct AiMentalDraw(double Center, double StyleBonus);
