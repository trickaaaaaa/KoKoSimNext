using System.Collections.Generic;
using KokoSim.Engine.Match.Field;

namespace KokoSim.Engine.Players;

/// <summary>
/// 試合に出場する1選手。打撃・走塁・守備の能力を一体で持ち、必要に応じて投手能力(Pitching)を伴う。
/// 既存の解決器へは ToBatter()/ToFielder() で投影して再利用する。
/// </summary>
public sealed record Player
{
    public string Name { get; init; } = "選手";
    public FieldPosition Position { get; init; } = FieldPosition.CenterField;

    /// <summary>
    /// 投影元の育成選手ID（成績集計の帰属キー）。相手校の生成選手（DevelopingPlayer 不在）は null。
    /// 乱数を含まない純データ（不変条件#2）。ボックススコアの SourceId 経由で通算/今大会成績へ紐づく。
    /// </summary>
    public int? SourceId { get; init; }

    /// <summary>
    /// 背番号（ベンチ入りメンバの識別・表示用）。1〜=ベンチ入り、0=番号なし（ベンチ外/未割当）。
    /// 自校は DevelopingPlayer.UniformNumber を焼き込み、相手校は生成時に採番する。乱数を含まない純データ。
    /// </summary>
    public int UniformNumber { get; init; }

    /// <summary>
    /// 学年（1〜3）。表示用（大会展望の登録メンバー・試合盤面のプロフィール）。0=未設定。
    /// 自校は DevelopingPlayer.Grade を焼き込み、相手校は生成時に採番する。乱数を含まない純データ。
    /// </summary>
    public int Grade { get; init; }

    /// <summary>投げ手・打ち手の利き（設計書01 §1.1c）。</summary>
    public Handedness Throws { get; init; } = Handedness.Right;
    public Handedness Bats { get; init; } = Handedness.Right;

    /// <summary>
    /// 対左投手の経験（加重打席数。設計書02 §2.3「左のプレミア」・issue #1072）。自校は
    /// <see cref="Season.DevelopingPlayer.LeftExperiencePa"/> を投影、AI校は学年から近似（<see cref="Nation.Roster.AiTeamBuilder"/>）。
    /// 既定 <see cref="double.PositiveInfinity"/>＝「経験は十分（恒等）」で、この値を明示的に投影しない
    /// 既存のテスト素体・統計シムの生成はすべて従来どおり対左の不利が無い（<see cref="Match.Batting.ContactModel.LeftExperienceDeficit"/>
    /// が常に0を返す）。実際に効くのは試合エンジンが対戦投手が左投げのときだけ読む値（右投手には使わない）。
    /// </summary>
    public double LeftExperiencePa { get; init; } = double.PositiveInfinity;

    public int Contact { get; init; } = AbilityScale.AverageDisplay;
    public int Power { get; init; } = AbilityScale.AverageDisplay;
    public int LaunchTendency { get; init; } = 50;
    public int Discipline { get; init; } = AbilityScale.AverageDisplay;
    public int Speed { get; init; } = AbilityScale.AverageDisplay;
    public int ArmStrength { get; init; } = AbilityScale.AverageDisplay;

    /// <summary>送球精度（肩＝ArmStrengthから分離, 設計書01 §1.2）。</summary>
    public int ThrowAccuracy { get; init; } = AbilityScale.AverageDisplay;
    public int Fielding { get; init; } = AbilityScale.AverageDisplay;

    /// <summary>
    /// 守備位置ごとの実効守備力（添字＝<see cref="FieldPosition"/> の整数値）。null＝持たない（<see cref="Fielding"/> が全位置の値）。
    /// 投影時の守備位置が試合中に変わりうる選手（DH打者＝投手で投影され、DH解除で別の位置に就く）だけが持ち、
    /// <see cref="AtPosition"/> が就く位置の適性で <see cref="Fielding"/> を作り直す（issue #1245）。
    /// </summary>
    public IReadOnlyList<int>? FieldingByPosition { get; init; }

    /// <summary>守備位置を <paramref name="pos"/> へ変えた選手。<see cref="FieldingByPosition"/> があれば守備力もその位置の値へ差し替える。</summary>
    public Player AtPosition(FieldPosition pos)
        => FieldingByPosition is { } f && (int)pos < f.Count
            ? this with { Position = pos, Fielding = f[(int)pos] }
            : this with { Position = pos };
    public int Catching { get; init; } = AbilityScale.AverageDisplay;

    /// <summary>捕手リード（配球の質, 設計書01 §2①）。高いほど良い配球。捕手のみ意味を持つ。50=平均で恒等。</summary>
    public int Lead { get; init; } = AbilityScale.AverageDisplay;

    /// <summary>
    /// 投手センス（隠し能力・設計書01 §1.3・issue #1472）。配球の質（球種の選び方）と、盗塁の読み・牽制の鋭さに効く。
    /// 全選手が持つ（投手は配置であって属性ではない）。表示Lv（物差し −Δ）で持ち、解決式は
    /// <see cref="AbilityEffectCoefficients.Pitching(int)"/> を通して読む（不変条件#1）。既定は平均＝恒等。
    /// 選手画面の能力表には出さない（見極めで推定だけが分かる）。
    /// </summary>
    public int PitcherSense { get; init; } = AbilityScale.AverageDisplay;

    /// <summary>走塁系連続パラメータ（設計書02 §4）。旧走塁系スキルの後継。</summary>
    public int Bunt { get; init; } = AbilityScale.AverageDisplay;
    public int Steal { get; init; } = AbilityScale.AverageDisplay;
    public int Baserunning { get; init; } = AbilityScale.AverageDisplay;

    /// <summary>精神力（設計書02 §3）。プレッシャー指数との積で場面補正になる。50=補正なし。</summary>
    public int Mental { get; init; } = AbilityScale.AverageDisplay;

    /// <summary>統率傾向（性格の一部, CHANGELOG 22b）。主将の統率力 = 統率傾向×精神力（設計書09 §8）。</summary>
    public int Leadership { get; init; } = 50;

    /// <summary>性格②素直さ（設計書23 §1.1。1〜99・50が中立）。指導の入りやすさ・伝令の落ち着きに効く。</summary>
    public int Receptiveness { get; init; } = 50;

    /// <summary>性格③勤勉さ（設計書23 §1.1）。試合層では効かない（自主成長＝育成層だけ）。表示と導出ラベル用に持つ。</summary>
    public int Diligence { get; init; } = 50;

    /// <summary>性格④試合での姿勢（設計書23 §1.1。高い＝献身／低い＝目立ちたがり）。伝令役の得意場面・相性・結束に効く。</summary>
    public int Selflessness { get; init; } = 50;

    /// <summary>
    /// 自主成長の隠れた個人差（隠しパラメータ・OPEN-QUESTIONS Q47）。試合層では効かない。AI校の選手が転任で部員になるとき
    /// そのまま引き継ぐために持つ（<see cref="PersonalityDraw.SelfGrowthJitter"/>）。既定0。
    /// </summary>
    public double SelfGrowthJitter { get; init; }

    /// <summary>
    /// 性格タイプ（設計書23 §1.3）。4軸から導出するラベルで、別には保存しない。②③④がすべて50なら Normal。
    /// 犠打・チャンスの効きは下の解決済みスカラーに投影時に焼き込む（ホットパスでの係数引き回し回避）。
    /// </summary>
    public Personality Personality => Personalities.Classify(Leadership, Receptiveness, Diligence, Selflessness);

    /// <summary>性格④の犠打/進塁打 成功率加算（自己犠牲+/目立ち−）。投影時に性格表から解決。既定0＝無補正。</summary>
    public double BuntSuccessBonus { get; init; }

    /// <summary>性格④の得点圏強攻での長打質(Power)倍率（目立ちたがり>1/自己犠牲<1）。既定1.0＝無補正。</summary>
    public double ChanceHitFactor { get; init; } = 1.0;


    /// <summary>特殊能力（設計書10）。有無フラグ制。可視/隠しを内包。既定は空（従来挙動と一致）。</summary>
    public SkillSet Skills { get; init; } = SkillSet.Empty;

    /// <summary>
    /// 怪物（隠しフラグ, 設計書 OPEN-QUESTIONS Q20）。既定 None＝通常選手。AI校の永続ロスター生成
    /// （新入生コホート）で稀にロールされ、新聞・大会展望・テストから参照する。物理層の解決には使わない
    /// （能力は生成時に既に引き上げ済み＝帯は能力分布経由でのみ動く）。
    /// </summary>
    public PhenomType Phenom { get; init; } = PhenomType.None;

    /// <summary>
    /// 打者の型（隠しフラグ, issue #1066）。能力の伸び方の重み（長所は伸び短所は伸びない）だけを決める。
    /// 守備位置・適性とは無関係＝UIに出すときは CLAUDE.md の守備位置ルールに注意すること。
    /// 既定 null＝型なし（自校の<c>ProspectGenerator</c>・旧セーブは未配線のため常に null）。
    /// AI校の永続ロスター生成（<see cref="Nation.Roster.AiRosterFactory.NewcomerCohort"/>）だけが付与する。
    /// </summary>
    public BatterArchetype? BatterArchetype { get; init; }

    /// <summary>
    /// <see cref="BatterArchetype.OneTool"/>（一芸・素材型）が突出させる1能力。他の型では null で無視する。
    /// </summary>
    public BatterAttribute? BatterArchetypeFocus { get; init; }

    /// <summary>
    /// 打者の型の尖り方の強さ（生成時の学校の強さから決まる・<see cref="Players.BatterArchetypes.IntensityForStrength"/>）。
    /// 弱い学校ほど0に近づき型の効果が目立たない（バランス型に見える＝issue #1066「弱い学校はバランス型・素材型が多め」）。
    /// 強豪校ほど1を超えて大きくなる（「強豪校ほど長所の尖り方を大きく」）。学年成長時（<c>AiRosterCycle.BumpTo</c>）は
    /// 学校を参照できないので、生成時にこの値へ焼き込んでおく。既定1.0＝型なしの選手には無関係。
    /// </summary>
    public double BatterArchetypeIntensity { get; init; } = 1.0;

    /// <summary>調子（設計書02 §3.3）。週次の波を Season 層が更新し、投影時に載せる。試合中は補正係数として作用。</summary>
    public Condition Condition { get; init; } = Condition.Normal;

    /// <summary>
    /// 調子の内部連続値（-1〜+1）。<see cref="Condition"/>（5段階量子化済み）と同じ源だが、
    /// 相手校の調子を監督の育成眼で観測する誤認モデル（<see cref="FormModel.Observe"/>）が
    /// 量子化前の真値を必要とするため、投影時にそのまま保持する（設計書02 §3.3, issue #47）。
    /// </summary>
    public double ConditionValue { get; init; }

    /// <summary>怪我の段階（設計書03 §3.5, 常に可視）。能力ダウンは投影時に一律係数で反映済み。采配判断用の表示。</summary>
    public InjurySeverity Injury { get; init; } = InjurySeverity.None;

    /// <summary>
    /// 怪我耐性（隠し, 1〜100。設計書01 L34）。試合中の場面駆動の受傷判定（設計書03 §3.5）で
    /// 週次判定と同じ掛け方をするために投影する。物理層の解決には一切使わない＝帯不変。
    /// </summary>
    public double InjuryResistance { get; init; } = 50.0;

    /// <summary>
    /// 投手能力。**全選手が必ず持つ**（issue #381 / #366）。投手として置かれていない選手は
    /// 「投げ慣れていないぶん低い」だけで、能力そのものは欠けていない＝<c>Pitching is null</c> で
    /// 役割を判定しない。誰がマウンドに立つかは配置（<see cref="Position"/>／<c>Team.PitcherSlot</c>）が決める。
    ///
    /// 旧実装は投手だけ本値・野手だけ <c>LatentPitching</c> という二重管理で、緊急登板のたびに
    /// 実体化（旧 <c>TeamState.Materialize</c>）で詰め替えていた。詰め替えを忘れた経路は
    /// 黙ってリーグ平均で投げる（＝本人の能力が消える）ので、持たせ方を1本にして構造的に断つ。
    ///
    /// 既定値は <see cref="PitcherAttributes.LeagueAverage"/>（＝旧コードの <c>?? LeagueAverage</c>
    /// フォールバックと同値）。実際の生成経路（<c>RosterTeamBuilder</c>／<c>StrengthTeamFactory</c>）は
    /// 必ず本人の能力を詰めるので、既定値が試合に出るのはテスト素体だけ。
    /// </summary>
    public PitcherAttributes Pitching { get; init; } = new();

    /// <summary>表示能力そのまま（効き＝恒等）。物理解決へ渡す値は <see cref="ToBatter(AbilityEffectCoefficients)"/> を使う。</summary>
    public BatterAttributes ToBatter() => ToBatter(AbilityEffectCoefficients.Identity);

    /// <summary>
    /// 表示層→物理層の変換（不変条件#1・設計書21）。打撃3能力は <see cref="AbilityEffectCoefficients.Batting"/>、走力は
    /// <see cref="AbilityEffectCoefficients.Running"/> の効きを通す。弾道は「型」なので通さない。
    /// </summary>
    public BatterAttributes ToBatter(AbilityEffectCoefficients e) => new()
    {
        Contact = e.Batting(Contact),
        Power = e.Batting(Power),
        LaunchTendency = LaunchTendency,
        Discipline = e.Batting(Discipline),
        Speed = e.Running(Speed),
        Bats = Bats,
    };

    /// <summary>表示能力そのまま（効き＝恒等）。物理解決へ渡す値は <see cref="ToFielder(AbilityEffectCoefficients)"/> を使う。</summary>
    public FielderAttributes ToFielder() => ToFielder(AbilityEffectCoefficients.Identity);

    /// <summary>
    /// 表示層→物理層の変換（不変条件#1・設計書21）。守備/捕球/送球精度は <see cref="AbilityEffectCoefficients.Fielding"/>、
    /// 肩は <see cref="AbilityEffectCoefficients.Arm"/>（送球速度＝km/h で語る量・issue #764）、走力は
    /// <see cref="AbilityEffectCoefficients.Running"/> の効きを通す。捕手リードは投手側の能力なので
    /// <see cref="AbilityEffectCoefficients.Pitching"/> の効きを通す（配球の球威上乗せ・盗塁の読み）。
    /// </summary>
    public FielderAttributes ToFielder(AbilityEffectCoefficients e)
    {
        // 外野手の守備3能力は上位の頭打ちを通す（issue #976 追補・AbilityEffectCoefficients.OutfieldDefenseTopKnee）。
        var outfield = Position is FieldPosition.LeftField or FieldPosition.CenterField or FieldPosition.RightField;
        int Def(int lv) => outfield ? e.OutfieldFielding(lv) : e.Fielding(lv);
        return new()
        {
            Speed = e.Running(Speed),
            ArmStrength = e.Arm(ArmStrength),
            ThrowAccuracy = Def(ThrowAccuracy),
            Fielding = Def(Fielding),
            Catching = Def(Catching),
            Lead = e.Pitching(Lead),
        };
    }
}
