using KokoSim.Engine.Core;

namespace KokoSim.Engine.Players;

/// <summary>
/// 打者の型（issue #1066・オーナー承認 2026-11-17）。<b>打者総合力は変えず、能力の伸び方の重みだけを変える</b>
/// ための型。守備位置・適性とは無関係（プレイヤーが決める・CLAUDE.md 守備位置ルール）。
/// <see cref="PitcherArchetype"/> と同じ考え方で、現状は<b>AI校の永続ロスターだけ</b>が持つ（自校の
/// <c>ProspectGenerator</c> は未配線＝issue #1066 本文で別issue可とされた範囲）。
/// </summary>
public enum BatterArchetype
{
    /// <summary>バランス型（型なし）。全部そこそこ・目立った穴なし。</summary>
    Balanced,
    /// <summary>長距離砲: パワー・弾道が長所、走力・守備が短所。足の遅い4番。</summary>
    PowerHitter,
    /// <summary>粗削りの強打者: パワーが長所、ミート・選球眼が短所。三振か本塁打か。</summary>
    RawSlugger,
    /// <summary>中距離ヒッター: ミート・パワー(中)・低い弾道が長所、走力が短所。ライナーで二塁打を量産する3番。</summary>
    GapHitter,
    /// <summary>俊足巧打: 走力・ミートが長所、パワーが短所。1番センター。</summary>
    SpeedyContact,
    /// <summary>選球眼の鬼: 選球眼・ミートが長所、パワーが短所。四球で出塁する1・2番。</summary>
    EyeHitter,
    /// <summary>小技職人: バント・走塁が長所、パワーが短所。送りバントが確実な2番。</summary>
    SmallBall,
    /// <summary>盗塁職人: 走力・盗塁が長所、打撃全般が短所。代走の切り札。</summary>
    StealSpecialist,
    /// <summary>守備職人: 守備・捕球が長所、打撃が短所。打てないが外せない内野手。</summary>
    DefenseSpecialist,
    /// <summary>強肩: 肩・送球精度が長所、ミートが短所。強肩の外野手・盗塁を刺す捕手。</summary>
    StrongArm,
    /// <summary>頭脳派捕手: リード・捕球が長所、走力が短所。下位打線でも投手を引っ張る。</summary>
    CerebralCatcher,
    /// <summary>一芸（素材型）: どれか1つが突出、ほかは低い。「パワーだけはチーム一」の1年生。</summary>
    OneTool,
}

/// <summary>
/// <see cref="BatterArchetype.OneTool"/> が突出させる1能力の対象（issue #1066）。
/// <see cref="Nation.Roster.AiRosterCycle.BumpTo"/> が伸ばす範囲と同じ12能力（弾道・精神・性格は含まない）。
/// </summary>
public enum BatterAttribute
{
    Contact, Power, Discipline, Speed, ArmStrength, ThrowAccuracy,
    Fielding, Catching, Bunt, Steal, Baserunning, Lead,
}

/// <summary>
/// 打者の型の出現割合と、型ごとの能力オフセット（data/coefficients.yaml 駆動・不変条件#4）。
/// オフセットは<see cref="Nation.Roster.PersistentRosterCoefficients"/> 側のやわらかい天井（<c>BatterSoftCap</c>）と
/// 組み合わせて使う＝<see cref="PitcherArchetype"/> の制球/キレ/スタミナ（<c>PitcherArchetypes.Offsets</c>＋
/// <see cref="Nation.Roster.AiRosterCycle.BumpTo"/> の <c>UpKeep</c>）と同じ「基準値をオフセットだけ上下にずらす」作法。
/// 長所は正・短所は負。合計は型によって揃えていない（打撃総合は逆算配分の二分探索＝
/// <see cref="Nation.Roster.AiRosterCycle"/> の <c>SolveGrowthG</c> が毎年吸収するので、型の合計が揃っていなくても
/// 学校の強さとチーム総合の対応は変わらない）。
/// </summary>
public sealed record BatterArchetypeCoefficients
{
    // --- 出現割合（残りが Balanced） ---
    // 2026-11-17 実測で判明: スタメン選抜（AssignFieldPositions）は打撃（ミート・パワー）の重みで8枠を解くため、
    // ミート・パワーが短所の型（粗削りの強打者/守備職人/強肩/盗塁職人 等）は生成母集団より強くスタメンから
    // 弾かれる（生き残るのは短所があまり響かなかった個体＝実質サバイバーシップバイアス）。バランス型（型なしの
    // 代わり）は逆に一度も弾かれないので、残り（Balanced）の出現割合を絞るほどスタメンの「オールA」が減る。
    public double PowerHitterShare { get; init; } = 0.10;
    public double RawSluggerShare { get; init; } = 0.08;
    public double GapHitterShare { get; init; } = 0.10;
    public double SpeedyContactShare { get; init; } = 0.10;
    public double EyeHitterShare { get; init; } = 0.09;
    public double SmallBallShare { get; init; } = 0.08;
    public double StealSpecialistShare { get; init; } = 0.07;
    public double DefenseSpecialistShare { get; init; } = 0.10;
    public double StrongArmShare { get; init; } = 0.08;
    public double CerebralCatcherShare { get; init; } = 0.07;
    public double OneToolShare { get; init; } = 0.08;

    // --- 長距離砲: パワー↑ 走力・守備↓ ---
    // 長所と短所のオフセットは非対称（長所は控えめ・短所は大きめ）。issue #1066 の実測で判明した事情:
    // AI校の逆算配分は強豪校ほど1人あたり年間+80相当（3年生の追い上げぶん含む）という巨大な成長量を積むため、
    // 控えめな長所オフセットでも（やわらかい天井の近くまで育つので）十分に高く着地する。一方、短所側は
    // 新入生の入学時点の中心がすでに学校の強さに連動して高い（issue #1066 原因2）ので、巨大な成長量を
    // 弱める重み（GrowthWeight）だけでは「C以下まで低く保つ」には足りず、オフセット自体を大きく下げて
    // 入学時点から低くする必要がある（実測: strength90 で短所-18 では C以下に届かず-35で届いた）。
    public double PowerHitterPower { get; init; } = 11;
    public double PowerHitterSpeed { get; init; } = -30;
    public double PowerHitterFielding { get; init; } = -13;

    // --- 粗削りの強打者: パワー↑ ミート・選球眼↓ ---
    public double RawSluggerPower { get; init; } = 11;
    public double RawSluggerContact { get; init; } = -26;
    public double RawSluggerDiscipline { get; init; } = -16;

    // --- 中距離ヒッター: ミート・パワー(中)↑ 走力↓ ---
    public double GapHitterContact { get; init; } = 10;
    public double GapHitterPower { get; init; } = 6;
    public double GapHitterSpeed { get; init; } = -19;

    // --- 俊足巧打: 走力・ミート↑ パワー↓ ---
    public double SpeedyContactSpeed { get; init; } = 13;
    public double SpeedyContactContact { get; init; } = 8;
    public double SpeedyContactPower { get; init; } = -26;

    // --- 選球眼の鬼: 選球眼・ミート↑ パワー↓ ---
    public double EyeHitterDiscipline { get; init; } = 13;
    public double EyeHitterContact { get; init; } = 6;
    public double EyeHitterPower { get; init; } = -24;

    // --- 小技職人: バント・走塁↑ パワー↓ ---
    public double SmallBallBunt { get; init; } = 16;
    public double SmallBallBaserunning { get; init; } = 10;
    public double SmallBallPower { get; init; } = -26;

    // --- 盗塁職人: 走力・盗塁↑↑ 打撃全般↓ ---
    public double StealSpecialistSpeed { get; init; } = 11;
    public double StealSpecialistSteal { get; init; } = 16;
    public double StealSpecialistContact { get; init; } = -16;
    public double StealSpecialistPower { get; init; } = -19;
    public double StealSpecialistDiscipline { get; init; } = -11;

    // --- 守備職人: 守備・捕球↑ 打撃↓ ---
    public double DefenseSpecialistFielding { get; init; } = 11;
    public double DefenseSpecialistCatching { get; init; } = 8;
    public double DefenseSpecialistContact { get; init; } = -19;
    public double DefenseSpecialistPower { get; init; } = -22;
    public double DefenseSpecialistDiscipline { get; init; } = -8;

    // --- 強肩: 肩・送球精度↑ ミート↓ ---
    public double StrongArmArmStrength { get; init; } = 13;
    public double StrongArmThrowAccuracy { get; init; } = 8;
    public double StrongArmContact { get; init; } = -24;

    // --- 頭脳派捕手: リード・捕球↑ 走力↓ ---
    public double CerebralCatcherLead { get; init; } = 13;
    public double CerebralCatcherCatching { get; init; } = 8;
    public double CerebralCatcherSpeed { get; init; } = -24;

    // --- 一芸（素材型）: 抽選で選んだ1能力だけ突出、残りはやや低い ---
    public double OneToolFocusBonus { get; init; } = 14;
    public double OneToolOtherPenalty { get; init; } = -18;

    /// <summary>
    /// オフセット1ポイントあたり毎年の成長重み（<see cref="BatterArchetypes.GrowthWeight"/>）に足す量（issue #1066）。
    /// 生成時のオフセットだけでは、AI校の逆算配分（<c>AiRosterCycle.SolveGrowthG</c>）が強豪校ほど積む
    /// 巨大な成長量（1人あたり年間+80相当まで＝3年生の追い上げ分含む）に押し流されて長所・短所が消える
    /// （issue #1066 原因1そのもの）。長所は多く・短所はあまり育たない重みを毎年の成長そのものに掛けることで、
    /// 成長量が大きいほど差が<b>広がる</b>（旧実装は差が縮んでいた）。
    /// </summary>
    public double GrowthWeightPerOffsetPoint { get; init; } = 0.035;
    /// <summary>成長重みの下限（短所でも成長が完全に止まらない保険）。</summary>
    public double GrowthWeightMin { get; init; } = 0.15;
    /// <summary>成長重みの上限（長所の暴走防止）。</summary>
    public double GrowthWeightMax { get; init; } = 1.9;

    // --- 尖り方の強さ（学校の強さで決まる・issue #1066「強豪校ほど長所の尖り方を大きく・弱い学校はバランス型が多め」） ---
    // 弱い学校の新入生は、上下いずれにせよ入学時点の中心がすでに低い（原因2とは逆方向）ので、型のオフセットを
    // そのまま乗せると「短所が底(10)に張り付く」「長所だけ平均より目立って不自然」になりやすい。弱い学校では
    // 型の効きをほぼ0（＝実質バランス型）にし、強豪校では逆算配分の巨大な成長量に負けないよう1を超えて強める。
    /// <summary>尖り方が0になる学校の強さ（これ以下は型を持っていても効果なし）。</summary>
    public double IntensityPivot { get; init; } = 55.0;
    /// <summary>尖り方が1.0になるまでの強さの幅（Pivot+これ で1.0）。</summary>
    public double IntensitySpan { get; init; } = 30.0;
    /// <summary>尖り方の上限（強豪校ほど1を超えて強くなるが、ここで頭打ち）。</summary>
    public double IntensityMax { get; init; } = 2.2;
    /// <summary>
    /// Pivot+Span（尖り方1.0）を超えた先の傾き（issue #1066）。スタメン選抜（打撃重みで8枠を解く）が
    /// 打撃系の短所をスタメンから弾くぶん（サバイバーシップバイアス）、最上位の学校ほど素の係数以上に
    /// 尖りを強めないと「オールA」が減らない。1.0未満は不可（Pivot+Span以下の挙動は変えない）。
    /// </summary>
    public double IntensityAboveOneSlope { get; init; } = 3.0;
}

/// <summary>打者の型の抽選・オフセット適用・表示名（純関数）。</summary>
public static class BatterArchetypes
{
    private static readonly int AttributeCount = System.Enum.GetValues(typeof(BatterAttribute)).Length;

    /// <summary>
    /// 打者の型を抽選する。呼び出し側は Fork した独立ストリームを渡すこと
    /// （既存の能力ロール列を乱さないため・不変条件#2）。
    /// </summary>
    public static BatterArchetype Sample(IRandomSource rng, BatterArchetypeCoefficients c)
    {
        var r = rng.NextDouble();
        if (r < c.PowerHitterShare) return BatterArchetype.PowerHitter;
        r -= c.PowerHitterShare;
        if (r < c.RawSluggerShare) return BatterArchetype.RawSlugger;
        r -= c.RawSluggerShare;
        if (r < c.GapHitterShare) return BatterArchetype.GapHitter;
        r -= c.GapHitterShare;
        if (r < c.SpeedyContactShare) return BatterArchetype.SpeedyContact;
        r -= c.SpeedyContactShare;
        if (r < c.EyeHitterShare) return BatterArchetype.EyeHitter;
        r -= c.EyeHitterShare;
        if (r < c.SmallBallShare) return BatterArchetype.SmallBall;
        r -= c.SmallBallShare;
        if (r < c.StealSpecialistShare) return BatterArchetype.StealSpecialist;
        r -= c.StealSpecialistShare;
        if (r < c.DefenseSpecialistShare) return BatterArchetype.DefenseSpecialist;
        r -= c.DefenseSpecialistShare;
        if (r < c.StrongArmShare) return BatterArchetype.StrongArm;
        r -= c.StrongArmShare;
        if (r < c.CerebralCatcherShare) return BatterArchetype.CerebralCatcher;
        r -= c.CerebralCatcherShare;
        return r < c.OneToolShare ? BatterArchetype.OneTool : BatterArchetype.Balanced;
    }

    /// <summary><see cref="BatterArchetype.OneTool"/> が突出させる1能力を一様抽選する（他の型では未使用）。</summary>
    public static BatterAttribute SampleFocus(IRandomSource rng) => (BatterAttribute)rng.NextInt(0, AttributeCount);

    /// <summary>
    /// 型・能力ごとのオフセット。<paramref name="focus"/> は <see cref="BatterArchetype.OneTool"/> のときだけ使う
    /// （どの能力が突出するか）。Balanced は常に0。
    /// </summary>
    public static double Offset(BatterArchetype a, BatterAttribute attr, BatterAttribute? focus, BatterArchetypeCoefficients c) => a switch
    {
        BatterArchetype.PowerHitter => attr switch
        {
            BatterAttribute.Power => c.PowerHitterPower,
            BatterAttribute.Speed => c.PowerHitterSpeed,
            BatterAttribute.Fielding => c.PowerHitterFielding,
            _ => 0.0,
        },
        BatterArchetype.RawSlugger => attr switch
        {
            BatterAttribute.Power => c.RawSluggerPower,
            BatterAttribute.Contact => c.RawSluggerContact,
            BatterAttribute.Discipline => c.RawSluggerDiscipline,
            _ => 0.0,
        },
        BatterArchetype.GapHitter => attr switch
        {
            BatterAttribute.Contact => c.GapHitterContact,
            BatterAttribute.Power => c.GapHitterPower,
            BatterAttribute.Speed => c.GapHitterSpeed,
            _ => 0.0,
        },
        BatterArchetype.SpeedyContact => attr switch
        {
            BatterAttribute.Speed => c.SpeedyContactSpeed,
            BatterAttribute.Contact => c.SpeedyContactContact,
            BatterAttribute.Power => c.SpeedyContactPower,
            _ => 0.0,
        },
        BatterArchetype.EyeHitter => attr switch
        {
            BatterAttribute.Discipline => c.EyeHitterDiscipline,
            BatterAttribute.Contact => c.EyeHitterContact,
            BatterAttribute.Power => c.EyeHitterPower,
            _ => 0.0,
        },
        BatterArchetype.SmallBall => attr switch
        {
            BatterAttribute.Bunt => c.SmallBallBunt,
            BatterAttribute.Baserunning => c.SmallBallBaserunning,
            BatterAttribute.Power => c.SmallBallPower,
            _ => 0.0,
        },
        BatterArchetype.StealSpecialist => attr switch
        {
            BatterAttribute.Speed => c.StealSpecialistSpeed,
            BatterAttribute.Steal => c.StealSpecialistSteal,
            BatterAttribute.Contact => c.StealSpecialistContact,
            BatterAttribute.Power => c.StealSpecialistPower,
            BatterAttribute.Discipline => c.StealSpecialistDiscipline,
            _ => 0.0,
        },
        BatterArchetype.DefenseSpecialist => attr switch
        {
            BatterAttribute.Fielding => c.DefenseSpecialistFielding,
            BatterAttribute.Catching => c.DefenseSpecialistCatching,
            BatterAttribute.Contact => c.DefenseSpecialistContact,
            BatterAttribute.Power => c.DefenseSpecialistPower,
            BatterAttribute.Discipline => c.DefenseSpecialistDiscipline,
            _ => 0.0,
        },
        BatterArchetype.StrongArm => attr switch
        {
            BatterAttribute.ArmStrength => c.StrongArmArmStrength,
            BatterAttribute.ThrowAccuracy => c.StrongArmThrowAccuracy,
            BatterAttribute.Contact => c.StrongArmContact,
            _ => 0.0,
        },
        BatterArchetype.CerebralCatcher => attr switch
        {
            BatterAttribute.Lead => c.CerebralCatcherLead,
            BatterAttribute.Catching => c.CerebralCatcherCatching,
            BatterAttribute.Speed => c.CerebralCatcherSpeed,
            _ => 0.0,
        },
        BatterArchetype.OneTool => attr == focus ? c.OneToolFocusBonus : c.OneToolOtherPenalty,
        _ => 0.0,   // Balanced
    };

    private static readonly BatterAttribute[] AllAttributes = (BatterAttribute[])System.Enum.GetValues(typeof(BatterAttribute));

    /// <summary>
    /// 生成時に1回だけ適用するオフセット（<see cref="Offset"/>）を、選手1人の12能力の平均が0になるよう
    /// 正規化して返す（issue #1066）。<see cref="Offset"/> の生の値は型によって12能力の合計が0でない
    /// （例: 長距離砲は+11−30−13=−32）。正規化しないと型を持つ選手ほど生成時点の打撃中心が平均より
    /// 低くなり、AI校の逆算配分（<c>AiRosterCycle.SolveGrowthG</c>）がチーム総合を目標へ届かせるため g を
    /// 大きく解き直し、その g が<b>投手の球速にもそのまま乗って</b>無関係の球速分布を乱す
    /// （<see cref="GrowthWeight"/> の解説と同じ事故の生成時バージョン）。平均0にすることで「誰のどこが
    /// 高いか低いか」という個性の形だけを残し、選手1人の総量（チームの強さが決まる軸）は型の有無で変わらない。
    /// </summary>
    public static double NormalizedOffset(BatterArchetype a, BatterAttribute attr, BatterAttribute? focus, BatterArchetypeCoefficients c)
    {
        var sum = 0.0;
        foreach (var x in AllAttributes) sum += Offset(a, x, focus, c);
        var mean = sum / AllAttributes.Length;
        return Offset(a, attr, focus, c) - mean;
    }

    /// <summary>
    /// 毎年の成長に掛ける重み（issue #1066）。<see cref="Offset"/> から
    /// <c>1.0 + offset × GrowthWeightPerOffsetPoint</c> を [GrowthWeightMin, GrowthWeightMax] にクランプして
    /// 素の重みを作り、<b>選手1人の12能力の平均が常に1.0になるよう正規化</b>して返す（型なし・オフセット0は
    /// 常に1.0＝旧実装と1ビットも変えない）。
    ///
    /// <para>正規化が無いと、短所側のクランプ（下限0.15）が長所側の上振れより強く効くため、1人あたりの
    /// 12能力の重みの合計が型ありの選手ほど旧実装（全員重み1.0＝合計12）より小さくなる。AI校の逆算配分
    /// （<c>AiRosterCycle.SolveGrowthG</c>）はチーム総合が目標に届くよう二分探索で g を決めるので、打撃側の
    /// 重み合計が目標に届きにくくなった分だけ g を大きく解き直し、その大きな g が<b>投手の球速にも同じ式で
    /// そのまま乗る</b>＝打者の型を入れただけで無関係のはずの投手球速分布が暴れる事故を生んだ
    /// （実測: 3年生の150km/h以上が 11人→38人に増加）。正規化で1人あたりの吸収量を型の有無で揃えることで
    /// g の解が変わらなくなり、この漏れを断つ。</para>
    /// </summary>
    public static double GrowthWeight(BatterArchetype a, BatterAttribute attr, BatterAttribute? focus, double intensity, BatterArchetypeCoefficients c)
    {
        double Raw(BatterAttribute x)
        {
            var off = Offset(a, x, focus, c) * intensity;
            var w = 1.0 + off * c.GrowthWeightPerOffsetPoint;
            return w < c.GrowthWeightMin ? c.GrowthWeightMin : w > c.GrowthWeightMax ? c.GrowthWeightMax : w;
        }

        var sum = 0.0;
        foreach (var x in AllAttributes) sum += Raw(x);
        if (sum <= 0.0) return 1.0;
        var scale = AllAttributes.Length / sum;
        return Raw(attr) * scale;
    }

    /// <summary>
    /// 学校の強さ（圧縮前・raw）から型の尖り方の強さを決める（issue #1066）。Pivot以下は0（効果なし＝
    /// バランス型に見える）、Pivot+Spanで1.0、その先は IntensityMax まで線形に強まる。
    /// </summary>
    public static double IntensityForStrength(double strength, BatterArchetypeCoefficients c)
    {
        var span = c.IntensitySpan;
        if (span <= 0.0) return strength > c.IntensityPivot ? 1.0 : 0.0;
        var x = (strength - c.IntensityPivot) / span;
        if (x <= 0.0) return 0.0;
        var intensity = x <= 1.0 ? x : 1.0 + (x - 1.0) * c.IntensityAboveOneSlope;
        return intensity > c.IntensityMax ? c.IntensityMax : intensity;
    }

    /// <summary>表示名（展望の寸評・選手詳細で使う想定。issue #1066 の守備位置非開示ルールに注意して使うこと）。</summary>
    public static string Label(BatterArchetype a) => a switch
    {
        BatterArchetype.PowerHitter => "長距離砲",
        BatterArchetype.RawSlugger => "粗削りの強打者",
        BatterArchetype.GapHitter => "中距離ヒッター",
        BatterArchetype.SpeedyContact => "俊足巧打",
        BatterArchetype.EyeHitter => "選球眼の鬼",
        BatterArchetype.SmallBall => "小技職人",
        BatterArchetype.StealSpecialist => "盗塁職人",
        BatterArchetype.DefenseSpecialist => "守備職人",
        BatterArchetype.StrongArm => "強肩",
        BatterArchetype.CerebralCatcher => "頭脳派捕手",
        BatterArchetype.OneTool => "一芸",
        _ => "バランス型",
    };
}
