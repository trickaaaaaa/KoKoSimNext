using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Debugging;

/// <summary>
/// 強制発動（設計書17 §6.1, F4）。<b>唯一「結果を変える」層</b>で、デバッグ経路からしか触れない。
///
/// <para><b>実装方針</b>: 該当の抽選をスキップして結果を固定する。<b>打球の物理層は偽装しない</b>（不変条件#1）。
/// 「本塁打を強制」は「本塁打になる BattedBall を注入する」ではなく「打席結果を HomeRun として確定させる」。
/// つまりこれは<b>下流（表現・記録・UI）の検証</b>に使う道具で、物理モデルの検証には使えない。</para>
///
/// <para>強制した試合は <see cref="GameResult.HasForcedOutcomes"/> が真になり、
/// 決定論ゲート（digest）と統計集計から自動的に外れる。</para>
///
/// <para><b>ここに無いもの</b>（設計書17 §6.1 のスケッチから外した理由）:
/// TriplePlay / Balk / Squeeze / EnterTieBreak / WalkOff は、単独の抽選ゲートを持たず
/// 「守備の共同解決」や「試合状況の帰結」として現れる。無理に固定すると物理層の偽装か第二の解決経路が要り、
/// どちらも不変条件#1に反する。EnterTieBreak は <c>GameContext.TieBreakEnabled</c>＋シナリオの
/// 開始イニング指定で作れるため、強制発動としては持たない。</para>
/// </summary>
public enum ForcedOutcome
{
    None = 0,

    // ── 打席結果を固定する（AtBatSession が投球ループごとスキップして確定させる） ──
    HomeRun,
    Triple,
    Double,
    Single,
    Strikeout,
    Walk,
    HitByPitch,
    /// <summary>ゴロ凡打（打席結果は InPlayOut。進塁は通常どおり下流が解く）。</summary>
    GroundOut,
    /// <summary>フライ凡打（打席結果は InPlayOut）。</summary>
    FlyOut,
    /// <summary>失策出塁（打席結果は ReachedOnError）。</summary>
    ReachedOnError,
    IntentionalWalk,

    // ── 稀プレーの発動を固定する（該当ゲートの確率を1.0にする＝抽選そのものは残す） ──
    WildPitch,
    DroppedThirdStrike,
    FieldersChoice,
    DoublePlay,
    PickoffOut,
    DoubleSteal,
    /// <summary>
    /// 送りバントを必ず転がす（空振り・ファウル・小フライ・ボール球の見送りを0にし、転がしの成功率を上限へ）。
    /// バントの指示そのもの（<c>PitchBattingOverride.Bunt</c>）は別に出す＝これは「指示したバントが転がる」だけを固定する。
    /// 転がしの成功率は式の上限 0.98 で頭打ちなので、約2%は転がしそこねの凡打になる（撮影側で撮り直す）。
    /// 守備ビューのバント処理の絵（issue #1089 等）を、シードを回さずに撮るためのもの。
    /// </summary>
    BuntInPlay,
    /// <summary>
    /// 打席の初球を「野手が追ったがあと一歩届かなかったファウルフライ」にする（issue #1074 の撮影用）。
    /// 初球は必ず振り、実際の守備位置で届かない打球を決まった順で探して当てる（乱数を引かない）。打席は続く。
    /// </summary>
    ChasedFoulFly,
    /// <summary>打席の初球を「ポール際の大ファウル」にする（issue #1074 の撮影用。<see cref="ChasedFoulFly"/> と同じ作り）。</summary>
    BigFoul,
    /// <summary>打席の初球を「打者が叩きつけて足元に落ちるファウル」にする（issue #1074「足元で跳ねる」の撮影用）。</summary>
    GroundFoul,
    /// <summary>打席の初球を「バックネット方向へ高く上がるファウル」にする（issue #1074「画面端で跳ね返る」の撮影用）。</summary>
    HighFoul,
    /// <summary>
    /// 打席の初球を「三遊間（三塁手と遊撃手の間）を抜けるゴロの安打」にする（issue #1591 の撮影用）。
    /// 実際の守備位置で内野を抜け、両側の内野手が追う球を決まった順で探して当てる（乱数を引かない）。
    /// </summary>
    ThroughInfieldLeft,
    /// <summary>打席の初球を「二遊間（二塁手と遊撃手の間）を抜けるゴロの安打」にする（<see cref="ThroughInfieldLeft"/> と同じ作り）。</summary>
    ThroughInfieldMiddle,
    /// <summary>打席の初球を「一二塁間を抜けるゴロの安打」にする（<see cref="ThroughInfieldLeft"/> と同じ作り）。</summary>
    ThroughInfieldRight,
    /// <summary>
    /// 打席の初球を「二塁手が捕って一塁へ投げるゴロ」にし、捕ってから投げるまでのもたつきを
    /// <see cref="ForcedOutcomes.SlowHandleJitterSeconds"/> に固定する（2026-11-26 送球へのステップの撮影用）。
    /// 打球は <see cref="ThroughInfieldLeft"/> と同じく決まった順で探す（乱数を引かない）。失策は起こさない。
    /// </summary>
    SlowHandleGrounder,
    /// <summary>
    /// <see cref="SlowHandleGrounder"/> のさらに長い版（もたつき <see cref="ForcedOutcomes.VerySlowHandleJitterSeconds"/>＝
    /// 守備の低い野手の上限に近い保持）。捕球の収録を流し切っても余る時間を横歩きで寄る絵の撮影用。
    /// </summary>
    VerySlowHandleGrounder,
    /// <summary>
    /// 打席の初球を「二塁手が捕って一塁へ投げたが逸れ、一塁手が拾い直して塁へ戻ってアウト」のゴロにする
    /// （issue #1735 の撮影用）。悪送球を必ず引き、逸れ幅を小さくして拾い直しが間に合う形に寄せる（打球は
    /// <see cref="SlowHandleGrounder"/> と同じく決まった順で探す・乱数を引かない）。間に合わなかった打席は失策になる＝撮り直す。
    /// </summary>
    RecoveredWildThrowGrounder,
    /// <summary>
    /// 打席の初球を「内野手が通り道でノーバウンド捕球するライナーのアウト」にする（issue #1724 の撮影用）。
    /// 実際の守備位置でライナー捕球が成立する打球を決まった順で探して当てる（乱数を引かない）。
    /// </summary>
    InfieldLineOut,

    // ── 走塁（盗塁企図を強制したうえで成否を固定する） ──
    StealSuccess,
    StealCaught,

    // ── 守備（打球の物理はそのまま・捕球の失策だけを確定させる） ──
    /// <summary>
    /// 前に飛んだ打球の最初の捕球を失策（お手玉・落球）にする（issue #1247 の撮影用）。<see cref="ReachedOnError"/> は
    /// 打席結果だけを固定して打球を作らない＝守備ビューに場面が出ないので、こちらは打球の物理を通したうえで
    /// 捕球の失策確率だけを 1 に寄せる。失策が起きなかった打席（三振・四球・外野へ抜けた安打・本塁打等）では
    /// 次の打席へ予約を持ち越す。
    /// </summary>
    FieldingError,
}

/// <summary>強制発動の分類と、打席結果への写像。</summary>
public static class ForcedOutcomes
{
    /// <summary>打席結果そのものを固定する種別か（投球ループをスキップする側）。</summary>
    public static bool FixesPlateAppearance(this ForcedOutcome f) => ToPlateAppearance(f) is not null;

    /// <summary>打席結果への写像（打席結果を固定しない種別は null）。</summary>
    public static PlateAppearanceResult? ToPlateAppearance(ForcedOutcome f) => f switch
    {
        ForcedOutcome.HomeRun => PlateAppearanceResult.HomeRun,
        ForcedOutcome.Triple => PlateAppearanceResult.Triple,
        ForcedOutcome.Double => PlateAppearanceResult.Double,
        ForcedOutcome.Single => PlateAppearanceResult.Single,
        ForcedOutcome.Strikeout => PlateAppearanceResult.Strikeout,
        ForcedOutcome.Walk => PlateAppearanceResult.Walk,
        ForcedOutcome.IntentionalWalk => PlateAppearanceResult.Walk,
        ForcedOutcome.HitByPitch => PlateAppearanceResult.HitByPitch,
        ForcedOutcome.GroundOut => PlateAppearanceResult.InPlayOut,
        ForcedOutcome.FlyOut => PlateAppearanceResult.InPlayOut,
        ForcedOutcome.ReachedOnError => PlateAppearanceResult.ReachedOnError,
        _ => null,
    };

    /// <summary>
    /// 1打席ぶんの守備係数（<see cref="FieldingError"/> だけが変える。他は同一参照＝乱数順・結果は不変）。
    /// 捕球の失策確率を下限・上限とも 1 にし、トンネルの分岐を止め（お手玉に揃える）、こぼれ幅を広げる。抽選自体は残るので乱数の消費順は変わらない。
    /// </summary>
    public static Match.Fielding.FieldingCoefficients ApplyFielding(
        Match.Fielding.FieldingCoefficients c, ForcedOutcome f) => f == ForcedOutcome.FieldingError
        // こぼれ幅を広げる＝拾い直して一塁に間に合う（記録はアウト・issue #817）ことを減らす。間に合ってしまった打席は次へ持ち越す
        ? c with { ErrorBaseProb = 1.0, ErrorMinProb = 1.0, ErrorMaxProb = 1.0, FlyErrorBaseProb = 1.0, TunnelShareProb = 0.0, CatchFumbleScatterSigmaM = 4.0 }
        : c;

    /// <summary><see cref="ForcedOutcome.SlowHandleGrounder"/> のもたつき[s]（守備50の握り替えと合わせて捕ってから約2.1秒で投げる）。</summary>
    public const double SlowHandleJitterSeconds = 1.3;
    /// <summary><see cref="ForcedOutcome.VerySlowHandleGrounder"/> のもたつき[s]（捕ってから約2.9秒で投げる）。</summary>
    public const double VerySlowHandleJitterSeconds = 2.1;

    /// <summary>
    /// <see cref="ForcedOutcome.SlowHandleGrounder"/> の1打席ぶんの守備係数。もたつきの平均を非常に大きくし、上限で
    /// <see cref="SlowHandleJitterSeconds"/> に頭打ちさせる（抽選は残る＝乱数の消費順は変わらない）。守備力の伸縮・
    /// お手玉の失策・捕球と送球の失策は切る（もたつきの長さだけを固定して、絵の確認に余計な分岐を入れない）。
    /// </summary>
    public static Match.Fielding.FieldingCoefficients ApplySlowHandle(
        Match.Fielding.FieldingCoefficients c, double jitterSeconds = SlowHandleJitterSeconds)
    {
        const double mean = 1000.0;
        return c with
        {
            InfieldHandleJitterMeanSeconds = mean,
            InfieldHandleJitterMaxFactor = jitterSeconds / mean,
            InfieldHandleJitterFieldingSlope = 0.0,
            BobbleErrorMinJitterSeconds = 0.0,
            ErrorBaseProb = 0.0, ErrorMinProb = 0.0, ErrorMaxProb = 0.0,
            ThrowErrorBaseProb = 0.0,
            IrregularBounceProb = 0.0,
        };
    }

    /// <summary>
    /// <see cref="ForcedOutcome.RecoveredWildThrowGrounder"/> の1打席ぶんの守備係数。悪送球の確率を1へ寄せ（捕球の失策・
    /// イレギュラーは切る）、逸れ幅の標準偏差を小さくして一塁手の拾い直しが間に合う形にする。
    /// </summary>
    public static Match.Fielding.FieldingCoefficients ApplyRecoveredWildThrow(Match.Fielding.FieldingCoefficients c)
        => c with
        {
            ErrorBaseProb = 0.0, ErrorMinProb = 0.0, ErrorMaxProb = 1.0,
            ThrowErrorBaseProb = 50.0,
            IrregularBounceProb = 0.0,
            WildThrowScatterSigmaM = 2.0,
        };

    /// <summary>盗塁企図そのものを起こす種別か（走者一塁・二塁空きが前提）。</summary>
    public static bool ForcesStealAttempt(this ForcedOutcome f)
        => f is ForcedOutcome.StealSuccess or ForcedOutcome.StealCaught
            or ForcedOutcome.PickoffOut or ForcedOutcome.DoubleSteal;

    /// <summary>
    /// 稀プレーのゲート確率を1.0へ寄せた1打席ぶんの走塁係数を作る。抽選自体は残るので
    /// 乱数の消費順は「既定オンの状態」と同じになる（既定オフのゲートだけ消費が増える）。
    /// </summary>
    public static Match.Game.BaserunningCoefficients Apply(
        Match.Game.BaserunningCoefficients c, ForcedOutcome f) => f switch
    {
        // 暴投は着弾コースのゲート（CanBatteryMiss）と投手の制球の傾きでも絞られる＝確率1.0だけでは強制にならず
        // 最初の打席に暴投が出ない（撮影が空振りする）。コースのゲートを全開・制球の傾きを0にして最初の対象球で必ず出す。
        ForcedOutcome.WildPitch => c with
        {
            WildPitchProb = 1.0, WildPitchControlSlope = 0.0, PassedBallProb = 0.0, BatteryMissLowY = 99.0,
        },
        ForcedOutcome.DroppedThirdStrike => c with { DropThirdStrikeReachProb = 1.0, DropThirdStrikeCatchingSlope = 0.0 },
        ForcedOutcome.FieldersChoice => c with { FieldersChoiceProb = 1.0, DoublePlayProb = 0.0 },
        ForcedOutcome.DoublePlay => c with { DoublePlayProb = 1.0, SpeedSlope = 0.0 },
        // ⚠ 牽制だけは確率が「上限係数でクランプされる」形（PickoffResolver.Probability は
        //    Clamp(Base + 走者リード − 投手センス, 0, PickoffMaxProb)）なので、Base を 1.0 に
        //    しても上限 0.08 で頭打ちになり**強制にならなかった**（実測: 8%。200打席×2試合を
        //    流しても牽制死が撮れなかったのはこれが原因）。上限と傾きも一緒に潰す
        //    （DoublePlay が SpeedSlope、DroppedThirdStrike が CatchingSlope を潰しているのと同じ作法）。
        ForcedOutcome.PickoffOut => c with
        {
            PickoffBaseProb = 1.0,
            PickoffMaxProb = 1.0,
            PickoffRunnerLeadSlope = 0.0,
            PickoffPitcherSenseSlope = 0.0,
        },
        ForcedOutcome.DoubleSteal => c with { DoubleStealThirdBreakProb = 1.0 },
        ForcedOutcome.BuntInPlay => c with
        {
            BuntTakeBase = 0.0, BuntTakeCap = 0.0,
            BuntMissShare = 0.0, BuntMissFloor = 0.0, BuntMissSkillSlope = 0.0,
            BuntMissVelocitySlope = 0.0, BuntMissStuffSlope = 0.0,
            BuntFoulShare = 0.0, BuntFoulFloor = 0.0, BuntFoulSkillSlope = 0.0,
            BuntPopShare = 0.0, BuntPopFloor = 0.0, BuntPopSkillSlope = 0.0,
            BuntPopVelocitySlope = 0.0, BuntPopStuffSlope = 0.0,
            // 転がしそこねをファウルに振り分けない（issue #1092）＝残る2%の転がしそこねは小フライのまま
            BuntMisrollFoulShare = 0.0,
            // 転がしの成功率（SuccessRate）は Clamp(…, 0.05, 0.98)。基準を上げて上限へ張り付かせる
            BuntBase = 1.0,
        },
        _ => c,
    };

    /// <summary>
    /// enum 名（大文字小文字を無視）から解釈する。未知なら false（例外を投げない）。
    /// 綴りは enum 名の1つだけ（<c>WildPitch</c>。<c>wild_pitch</c> や数値は通さない）＝CLI・Unity・DebugBridge・シナリオYAMLで同じ。
    /// </summary>
    public static bool TryParse(string? name, out ForcedOutcome outcome)
    {
        outcome = ForcedOutcome.None;
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var n in System.Enum.GetNames(typeof(ForcedOutcome)))
        {
            if (!string.Equals(n, name, System.StringComparison.OrdinalIgnoreCase)) continue;
            outcome = (ForcedOutcome)System.Enum.Parse(typeof(ForcedOutcome), n);
            return true;
        }
        return false;
    }
}
