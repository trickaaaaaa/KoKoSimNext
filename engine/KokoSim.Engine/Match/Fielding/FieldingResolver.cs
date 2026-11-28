using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Fielding;

/// <summary>打球の守備解決結果。</summary>
public enum BattedBallResult
{
    Foul,
    Out,
    Error,
    Single,
    Double,
    Triple,
    HomeRun,
}

/// <summary>
/// 失策の種類（issue #439 / #632）。Catch=捕球エラー（お手玉・落球）／Throw=悪送球／
/// Tunnel=トンネル（股・脇を抜けて球が外野へ行く）。
///
/// <para>Catch/Throw は「絵の選び方」だけの観測データだが、<b>Tunnel は球の行き先が違う</b>
/// ＝失策後の走者の進塁が幾何で決まる（不変条件#1）。issue #629 の失策連鎖（悪送球だけに乗る
/// 一律確率）と役割が分かれていて、こちらは<b>外野からの返球との時間勝負</b>になる。</para>
/// </summary>
/// <summary>
/// 失策の種別。Catch=捕球で弾いて前へこぼす／Throw=悪送球／Tunnel=股を抜かれる（issue #632）／
/// Bobble=お手玉（握り損ねて拾い直すあいだに打者が一塁へ着く。送球は正しく一塁へ届くが遅い。2026-11-03 監督報告）／
/// Receive=一塁手の捕球ミス（送球は正確なのに一塁手が落とす／ショートバウンドを捕れない。失策は一塁手に付く。issue #810）／
/// Fumble=外野手のファンブル（安打の回収時に弾く・後逸して余分に進塁される。安打の記録は変えず、失策は外野手に付く。issue #811）。
/// </summary>
public enum FieldingErrorKind { Catch, Throw, Tunnel, Bobble, Receive, Fumble }

/// <summary>
/// 守備解決の詳細（結果＋幾何・時間情報, CHANGELOG 32）。
/// 判定に使った着地点・滞空・捕球/送球時刻を捨てずに保持し、タイムライン出力の素材にする。
/// 時刻はすべて「接触（打った瞬間）＝0秒」基準。
/// </summary>
public sealed record FieldingPlay
{
    public required BattedBallResult Result { get; init; }
    /// <summary>着地点（X=+一塁側, Z=センター方向）[m]。</summary>
    public required double LandingX { get; init; }
    public required double LandingZ { get; init; }
    public required double HangTimeSeconds { get; init; }
    public required double ApexHeightM { get; init; }
    /// <summary>
    /// 打ち出し（接触）点の高さ[m]（表示専用・issue duel-view-fly-leaves-bat-flat-then-rises）。
    /// 弾道積分は <see cref="Batting.BattedBall.ContactHeightM"/> から始まるのに、表示の滞空プロファイルは
    /// h(0)=0（地面）から始まっていて、対決ビューのフライが「打点まで平らに走ってから急上昇＝弾んだ」絵になっていた。
    /// 生成側が既に持っている値の素通し。0＝未設定（ファウル判定前の経路等）。
    /// </summary>
    public double LaunchHeightM { get; init; }
    public required double RangeM { get; init; }
    public required double BearingDeg { get; init; }
    /// <summary>
    /// 打球の回収点（着地後の転がりの終端。X=+一塁側, Z=センター方向）[m]。
    /// <c>null</c> = 回収を解いていない経路（ファウル・本塁打・空中捕球）＝**着地点と同値**。
    /// 読む側は必ず <c>?? LandingX/Z</c> でフォールバックすること。
    ///
    /// nullable なのは「未設定」を表現可能にするため（Issue #315）。非nullable の既定 0 は
    /// 本塁という正当な座標なので、埋め忘れが型でも実行時でも検出されず、テストの手書き
    /// フィクスチャが誤った挙動を「正しい」として固定してしまう事故が実際に起きた（Issue #301）。
    /// </summary>
    public double? FieldedX { get; init; }
    public double? FieldedZ { get; init; }
    /// <summary>
    /// 転がりがフェンスまで届いたか（issue fence-carom-and-over-the-head-cues）。
    /// <see cref="ExtraBaseResolver.RollPath.ReachedFence"/> の写し＝新規計算も乱数消費もゼロの観測値。
    /// 実測（400試合）: 二塁打の 92.9% ／ 三塁打の 100% が該当＝長打のほぼ全部で跳ね返りの出番がある。
    /// </summary>
    public bool ReachedFence { get; init; }

    /// <summary>
    /// フェンス直撃の跳ね返り（<see cref="ExtraBaseResolver.ComputeFenceCarom"/> の結果）。
    /// **表示専用**＝タイムラインが絵を描くためだけに運ぶ。判定（回収時刻・塁打数・送球）は
    /// 一切これを読まない。<see cref="ReachedFence"/> でも速度が0なら null。
    /// </summary>
    public ExtraBaseResolver.FenceCarom? Carom { get; init; }

    /// <summary>処理した野手（フライ捕球/ゴロ処理）。ファウル・本塁打では null。</summary>
    public FieldPosition? FielderRole { get; init; }
    /// <summary>野手が打球に到達（捕球/処理）した時刻[s]。</summary>
    public double? FieldedAtSeconds { get; init; }
    /// <summary>一塁送球が到達した時刻[s]（内野ゴロのみ）。<see cref="UnassistedPutout"/> のときは
    /// 送球ではなく「一塁手がベースを踏んで刺殺が成立した時刻」を表す。</summary>
    public double? ThrowArriveSeconds { get; init; }
    /// <summary>
    /// アンアシスト刺殺（3U, Issue #400）。処理野手が一塁手で、送球するより自分でベースを踏むほうが
    /// 速いと物理層の時間比較（握り替え0.70sを払う送球 vs 捕球点→ベースを走り込む自重）で決まったとき true。
    /// このとき <see cref="ThrowArriveSeconds"/> は走り込みの到達時刻＝刺殺成立時刻を表し、表示層は送球
    /// セグメントも投手カバーも生成しない。処理野手が一塁手の内野ゴロ以外では常に false（不変条件#1: 判定は
    /// 表示能力値でなく物理層の時間比較で決める）。
    /// </summary>
    public bool UnassistedPutout { get; init; }
    /// <summary>打者走者の一塁到達時刻[s]。</summary>
    public required double BatterToFirstSeconds { get; init; }
    public bool IsFly { get; init; }
    /// <summary>処理野手の送球速度[m/s]（本塁クロスプレーの中継起点, 設計書12 §3 F2）。処理野手なしでは null。</summary>
    public double? FielderThrowSpeedMps { get; init; }
    /// <summary>処理野手の守備(Fielding)。返球・中継の握り替えの能力伸縮に使う（Issue #36）。既定50＝恒等。</summary>
    public int FielderFielding { get; init; } = 50;
    /// <summary>処理野手の送球精度(ThrowAccuracy)。失策連鎖の能力駆動に使う（Issue #37）。既定50＝恒等。</summary>
    public int FielderThrowAccuracy { get; init; } = 50;
    /// <summary>
    /// 一塁での判定margin[s]（判定オーバーレイ, Issue #59）。守備所要−走者所要＋ForceOutMarginSeconds
    /// （0=判定境界、正=セーフ寄り、負=アウト寄り）。内野ゴロで一塁送球が絡んだ判定のみ非null。
    /// </summary>
    public double? JudgementMarginSeconds { get; init; }

    /// <summary>
    /// 空中捕球の余裕[s]（判定オーバーレイ「フェア」, Issue #328②）。CatchTimeBudget−到達所要
    /// （正=捕球に余裕あり、負=間に合わず落球）。フライ/ライナーのみ非null（ゴロ・本塁打・ファウルはnull）。
    /// </summary>
    public double? CatchMarginSeconds { get; init; }

    /// <summary>
    /// 本塁打の当たりの種類（issue #704・表示専用）。フェンス上の余裕と打球角度から
    /// <see cref="Timeline.HomeRunStyles.Classify"/> で分けた値。本塁打以外は null。
    /// 既に決まった弾道の写し＝新規の乱数消費・判定への影響はゼロ。
    /// </summary>
    public Timeline.HomeRunStyle? HomeRunStyle { get; init; }

    /// <summary>
    /// 本塁打が柵を越えた点の本塁からの水平距離[m]と、その点での高さ[m]（判定に使った
    /// <c>FenceCrossPosition</c> の写し・issue #743）。本塁打以外は null。弧の絵が柵の上を必ず通るように
    /// タイムラインへ渡すだけ＝判定・乱数には触れない。
    /// </summary>
    public double? FenceCrossRangeM { get; init; }
    public double? FenceCrossHeightM { get; init; }

    /// <summary>
    /// 失策の種類（issue #439）。<see cref="Result"/>==Error のときだけ非null。
    /// 引いたロールの結果を記録するだけ＝新規RNGなし（不変条件#2）。
    /// </summary>
    public FieldingErrorKind? ErrorKind { get; init; }

    /// <summary>
    /// 失策を犯した野手（issue #632）。トンネルでは<b>処理野手（回収する外野手）と別人</b>になるので、
    /// 失策の個人帰属はこちらから引く。null＝<see cref="FielderRole"/> と同じ（捕球エラー・悪送球）。
    /// </summary>
    public FieldPosition? ErrorFielderRole { get; init; }

    /// <summary>
    /// 失策が確定した瞬間[s]（表示専用, issue #632）。トンネルでは<b>抜かれた瞬間</b>＝
    /// 外野手が球を回収する <see cref="FieldedAtSeconds"/> よりずっと前。
    /// null＝従来どおり（捕球エラー＝処理点で弾いた時刻／悪送球＝一塁へ届くはずだった時刻）。
    /// </summary>
    public double? ErrorAtSeconds { get; init; }

    // --- 悪送球を拾い直してアウト（issue #815・表示専用） ---

    /// <summary>
    /// 一塁への送球が逸れたが、回収レース（design-12 §3.8）で拾い直しが間に合ってアウトになった打席の
    /// 逸れ幅[m]（塁を通り過ぎて転がり着いた点までの距離）。それ以外は null。回収レースで引いた値の写し
    /// ＝新しい乱数は引かない。判定・帯は読まない（絵に「逸れた送球→拾い直し→アウト」を出す入力）。
    /// </summary>
    public double? RecoveredThrowScatterM { get; init; }
    /// <summary>拾い直したのがバックアップ（一塁なら右翼手。<see cref="ThrowRecovery.BackupRole"/>）か。false＝一塁手自身。</summary>
    public bool RecoveredThrowByBackup { get; init; }
    /// <summary>拾い直しでアウトが成立した時刻[s]（接触基準＝<see cref="ThrowArriveSeconds"/> と同じ時計）。</summary>
    public double? RecoveredThrowOutSeconds { get; init; }

    /// <summary>
    /// 悪送球（<see cref="ErrorKind"/>==Throw）で、回収レースでも拾い直しが間に合わなかった打席の逸れ幅[m]
    /// （issue #812・表示専用）。回収レースで引いた値の写し＝新しい乱数は引かない。逸れ方の絵
    /// （<see cref="Timeline.WildThrowStyles.Classify"/>）の入力。それ以外は null。
    /// </summary>
    public double? WildThrowScatterM { get; init; }

    // --- 捕球ミスを拾い直してアウト（issue #817・表示専用） ---

    /// <summary>
    /// 捕球ミス（お手玉。<see cref="FieldingErrorKind.Catch"/>）が確定した後、回収レース
    /// （design-14 P1-6 追記）で処理野手自身の拾い直しが間に合ってアウトに戻った打席の、こぼれ幅[m]。
    /// それ以外（失策のまま／別の種別）は null。回収レースで引いた値の写し＝新しい乱数は引かない。
    /// 判定・帯は読まない（絵に「弾いた→拾い直し→一塁送球→アウト」を出す入力）。
    /// </summary>
    public double? RecoveredCatchScatterM { get; init; }
    /// <summary>拾い直しでアウトが成立した時刻[s]（接触基準＝<see cref="ThrowArriveSeconds"/> と同じ時計）。</summary>
    public double? RecoveredCatchOutSeconds { get; init; }

    // --- こぼれ球と塁上の走者の勝負（issue #816） ---

    /// <summary>
    /// 捕球ミス（<see cref="FieldingErrorKind.Catch"/>）が確定し、回収レース（issue #817）でも
    /// 拾い直しが間に合わず失策のまま確定した打席の、こぼれ幅[m]（<see cref="WildThrowScatterM"/> の
    /// 捕球ミス版・表示専用）。それ以外（回収に成功／別の種別）は null。回収レースで引いた値の写し
    /// ＝新しい乱数は引かない。
    /// </summary>
    public double? CatchErrorScatterM { get; init; }

    /// <summary>
    /// 内野ゴロ→一塁送球の逸れ（issue #859）。送球の秒（遅れ）と悪送球の判定に使った値そのもの＝絵は受け手を
    /// この逸れへ寄せて描く。一塁送球を判定しない打球（外野・フライ・悪送球ロール無効）は null。
    /// </summary>
    public ThrowScatterModel.Scatter? FirstThrowScatter { get; init; }

    /// <summary>
    /// 内野ゴロ→一塁送球の「見かけの球速」[m/s]（issue #1685）。判定が使った送球の飛行時間
    /// （<see cref="FieldingResolver.FirstBaseThrowFlightSeconds"/>＝体勢による球の弱まり＋距離による失速）で
    /// 捕球点→一塁の距離を割った値。絵はこの速さで送球を飛ばす＝判定の送球の秒が球の飛行に出て、
    /// 捕ってから投げるまで（保持）には紛れ込まない。一塁送球を判定しない打球は null（肩どおり）。
    /// </summary>
    public double? FirstThrowSpeedMps { get; init; }

    /// <summary>
    /// 捕球ミスが拾い直せず確定した打席で、処理野手がこぼれ球を拾い直して次の送球に移れる時刻[s]
    /// （接触基準。issue #816）。<see cref="FieldedAtSeconds"/> は最初に球へ触れた時刻（=こぼれた瞬間）の
    /// ままなので、塁上の走者が次塁を狙うかどうかの判断（<see cref="KokoSim.Engine.Match.Game.HomePlayResolver"/>）
    /// にそちらを使うと「弾いた直後に別の塁へ送球できる」という誤った前提になる。回収レース（issue #817）で引いた
    /// 反応遅延・こぼれ幅・脚力から出た<see cref="FieldedAtSeconds"/>+回収ロスの写し＝新しい乱数は引かない。
    /// それ以外（回収に成功／別の種別）は null。
    /// </summary>
    public double? CatchErrorReadyAtSeconds { get; init; }

    /// <summary>
    /// 拾い直せず確定した捕球ミスで、拾い直したあと球を離すまでの手間[s]（issue #1733。表示専用）。
    /// 判定は回収レースで「拾い直し＋<b>捕ってから離すまで（守備の握り替え・もたつき）</b>＋送球の飛翔」と一塁到達を比べて
    /// 間に合わないと決めている＝絵の拾い直し後の送球も同じ手間を払わないと、判定が「間に合わない」と言った一塁へ
    /// 投げれば間に合うように見える。<see cref="CatchErrorReadyAtSeconds"/> と対（それ以外は null）。
    /// 値は判定の守備所要時間から捕球時刻と送球の移動を引いた残り＝新しい乱数は引かない。
    /// </summary>
    public double? CatchErrorHandlingSeconds { get; init; }

    /// <summary>ファウルフライのアウト（issue #830）。落下点はファウルグラウンド。</summary>
    public bool FoulFly { get; init; }

    // --- 打球のバウンド（Issue #63 / OPEN-QUESTIONS Q14） ---

    /// <summary>
    /// 打球の4分類（ゴロ/バウンド/ライナー/フライ）。物理層の積分結果から導出する。
    /// <c>null</c> = 未判定（分類が存在しないファウル。弾道積分を回す前に返す経路）。
    /// 既定を <c>Grounder</c> にすると分類を持たない打球まで「ゴロ」と名乗るので nullable（Issue #315）。
    /// </summary>
    public BattedBallClass? Class { get; init; }
    /// <summary>最大バウンド頂点[m]（接地後の弾みの最高到達点。フライ/ライナー捕球では0）。</summary>
    public double BounceApexHeightM { get; init; }
    /// <summary>接地後の弾みの回数（転がりへ移行するまで）。</summary>
    public int BounceCount { get; init; }
    /// <summary>イレギュラーバウンドが発生したか（エラー確率へ加算される）。</summary>
    public bool IrregularBounce { get; init; }
    /// <summary>バウンド/転がりで内野を抜けた打球か（外野へ二次展開した）。</summary>
    public bool ThroughInfield { get; init; }
    /// <summary>
    /// 表示専用（issue #759）: 内野手が処理したゴロ系の、着地後の弾み列（<see cref="BouncePath.Hops"/> の写し）。
    /// 3D の打球の高さを「決め打ちの3山」でなく実際の弾み方で描くための入力。判定・帯は一切読まない。
    /// 内野を抜けた打球・フライには入らない（null）。
    /// </summary>
    public IReadOnlyList<BounceHop>? GroundHops { get; init; }
    /// <summary>
    /// 表示専用（issue #759）: 弾み列の時計で、球が捕球点に着いた時刻[s]（チョッパー待ちを含まない）。
    /// <see cref="FieldedAtSeconds"/> はチョッパー待ちを足した処理時刻なので、両者の比で弾み列を再生尺へ伸ばす。
    /// </summary>
    public double? GroundPathCatchSeconds { get; init; }

    // --- 抜けた打球の「寄る/飛びつく」表示専用データ（Issue #336②） ---

    /// <summary>
    /// 内野を抜けた打球（<see cref="ThroughInfield"/>=true）で最も惜しく打球ラインに迫った内野手。
    /// タイムラインの「寄る／飛びつく」演出の入力（表示専用・判定不使用）。抜けていない打球や
    /// 該当が無い経路（想定外）では null。
    /// </summary>
    public FieldPosition? ClosestInfielderRole { get; init; }
    /// <summary>その内野手が打球ラインに最も近づいた点[m]（X=一塁側, Z=センター方向）。表示専用。</summary>
    public double? ClosestApproachX { get; init; }
    public double? ClosestApproachZ { get; init; }

    /// <summary>
    /// 表示専用（issue #1591）: 打球ラインを挟んで <see cref="ClosestInfielderRole"/> と反対側にいる内野手のうち、
    /// 最も惜しく迫った1人（二遊間・三遊間・一二塁間を抜ける打球は実物では両側の2人が追う）。
    /// 該当者がいない（打球が端を抜けた等）／抜けていない打球では null。判定・帯・指紋は読まない。
    /// </summary>
    public FieldPosition? SecondInfielderRole { get; init; }
    public double? SecondApproachX { get; init; }
    public double? SecondApproachZ { get; init; }
}

/// <summary>
/// 守備解決（設計書01 §2⑥）。打球を弾道積分し、着地点・滞空時間と守備位置の幾何から
/// 捕球可否・送球 vs 走者を判定して安打/凡打/本塁打を導く。
/// </summary>
public static class FieldingResolver
{
    /// <summary>互換API（結果のみ）。</summary>
    public static BattedBallResult Resolve(
        BattedBall ball,
        FieldGeometry field,
        Aerodynamics aero,
        BatterAttributes batter,
        IReadOnlyList<Fielder> fielders,
        FieldingCoefficients coeff,
        IRandomSource rng)
        => ResolveDetailed(ball, field, aero, batter, fielders, coeff, rng).Result;

    /// <summary>詳細解決。判定ロジック・乱数消費順は Resolve と完全に同一（決定論の後方互換）。</summary>
    public static FieldingPlay ResolveDetailed(
        BattedBall ball,
        FieldGeometry field,
        Aerodynamics aero,
        BatterAttributes batter,
        IReadOnlyList<Fielder> fielders,
        FieldingCoefficients coeff,
        IRandomSource rng)
    {
        var runnerTime = BatterToFirstSeconds(field, batter, coeff);

        // フェア/ファウル: 方位角が±45°を越えたらファウル。
        if (Math.Abs(ball.BearingDeg) > 45.0)
        {
            // ファウルフライ（issue #830）: ファウルグラウンドに落ちる前に野手が届けばアウト（乱数不使用）
            if (coeff.FoulFlyShare > 0.0 && ResolveFoulFly(ball, field, aero, fielders, coeff, runnerTime) is { } foulOut)
                return foulOut;
            return new FieldingPlay
            {
                Result = BattedBallResult.Foul,
                LandingX = 0, LandingZ = 0, HangTimeSeconds = 0, ApexHeightM = 0,
                RangeM = 0, BearingDeg = ball.BearingDeg, BatterToFirstSeconds = runnerTime,
            };
        }

        var bearingRad = ball.BearingDeg * Math.PI / 180.0;
        var fenceDist = field.FenceDistance(bearingRad);
        // 飛行経路の標本（issue #1135）。LineDriveReachHeightM<=0 なら記録しない＝従来と完全に同じ積分コスト。
        var trackFlightPath = coeff.LineDriveReachHeightM > 0.0;
        var flightSamples = trackFlightPath ? new List<BallisticIntegrator.FlightSample>() : null;
        var ground = BallisticIntegrator.IntegrateToGround(
            ball.ContactPoint(), ball.InitialVelocity(), ball.SpinVector(), aero, fenceDistanceM: fenceDist,
            sampleStepSeconds: coeff.LineDriveScanStepSeconds, samples: flightSamples, wind: field.Wind);

        // 通り道のライナー捕球（issue #1135）: 着地点（本塁打／フェンス直撃の判定より先）を待たず、
        // 飛行経路の途中で野手が正面にいればノーバウンド捕球でアウトにする。遊撃手の胸の高さを通って
        // 外野に落ちるライナー（頂点は高いが着地点基準では遊撃手が候補に出てこない）と、低いライナー
        // （頂点<FlyApexThresholdMでゴロ経路に入り、着地前の空中が一切判定されていなかった）の両方に効く。
        // 本塁打コースの打球も対象＝野手は結果を知らずに追う（物理的に正面で捕れるなら捕る）。
        // フェンス際の捕りにくさ（issue #976）は「この打球が柵の近くへ向かっているか」で決まる1つの値
        // （着地点基準の既存判定と同じ）。標本ごとの位置で引き直すと、同じ打球の捕球機会なのに
        // 「柵ゾーンの外縁をたまたま通る標本」だけペナルティが0になり、着地点では正しく際どい落球と
        // 判定される打球が経路の途中で楽に捕れてしまう（実害: フェンス際の「あと一歩届かない」シナリオが
        // 壊れた）。着地点（柵で打ち切る前の自然な到達点）までの距離で1回だけ決める。
        var naturalLanding = ground.LandingPosition;
        var naturalRange = Math.Sqrt(naturalLanding.X * naturalLanding.X + naturalLanding.Z * naturalLanding.Z);
        var lineDriveWallPenalty = coeff.WallCatchPenalty(fenceDist - naturalRange);
        if (flightSamples is { Count: > 0 }
            && TryLineDriveCatch(fielders, flightSamples, ground.HangTimeSeconds, ground.ApexHeightM, lineDriveWallPenalty, coeff) is { } ldc)
        {
            var catchMargin = ldc.BudgetSeconds - ldc.ReachSeconds;
            var result = MaybeFlyError(ldc.Fielder, catchMargin, coeff, rng);
            return new FieldingPlay
            {
                Result = result,
                LandingX = ldc.Point.X,
                LandingZ = ldc.Point.Z,
                HangTimeSeconds = ldc.TimeSeconds,
                ApexHeightM = TruncatedApexHeightM(flightSamples, ldc.TimeSeconds, ball.ContactHeightM),
                LaunchHeightM = ball.ContactHeightM,
                RangeM = Math.Sqrt(ldc.Point.X * ldc.Point.X + ldc.Point.Z * ldc.Point.Z),
                BearingDeg = ball.BearingDeg,
                BatterToFirstSeconds = runnerTime,
                // 分類は着地点基準の飛球と同じ式（頂点÷自然な到達距離）で決める。経路の途中で捕っても、
                // 打球そのものがフライならフライ（内野フライ・外野フライの集計を崩さない）。一律 Liner にすると
                // 落ちてくる途中で捕った普通のフライがライナーに化け、内野フライ率が帯 3.5〜6.5% を 2.5% へ割った。
                Class = ground.ApexHeightM / Math.Max(naturalRange, 1e-6) <= coeff.LinerApexRangeRatio
                    ? BattedBallClass.Liner : BattedBallClass.Fly,
                IsFly = true,
                FielderRole = ldc.Fielder.Position,
                FieldedAtSeconds = ldc.TimeSeconds,
                FielderThrowSpeedMps = ldc.Fielder.Attributes.ThrowSpeedMps,
                FielderFielding = ldc.Fielder.Attributes.Fielding,
                FielderThrowAccuracy = ldc.Fielder.Attributes.ThrowAccuracy,
                CatchMarginSeconds = catchMargin,
                ErrorKind = result == BattedBallResult.Error ? FieldingErrorKind.Catch : null,
            };
        }

        // 本塁打: **フェンス上での高さ**がフェンス高以上（issue homerun-rate-explodes-at-high-power）。
        // 旧判定「着地距離≥フェンス距離 かつ 頂点≥フェンス高」は、柵の0〜6m先に落ちる低い当たり
        // （フェンス上では 4m に届かない）まで本塁打にしていた（実測: 本塁打の41%）。
        var clearsFence = ground.CrossedFence && ground.FenceCrossPosition.Y >= field.FenceHeightM;
        // 柵に当たった高さ（issue #1243）。グラブの届かない高さで当たった打球は、柵に跳ね返る前に空中で捕れない
        // （柵より手前の飛行区間は上の通り道のライナー捕球が見ている）。
        var fenceHitAboveReach = ground.CrossedFence && !clearsFence
            && coeff.LineDriveReachHeightM > 0.0
            && ground.FenceCrossPosition.Y > coeff.LineDriveReachHeightM;
        // フェンス直撃（空中で柵に届いたが越えない）: 打球はそこで落ちる。着地＝フェンス弧上の点・
        // 着地時刻＝到達時刻・着地速度＝到達時の速度（転がりモデルはフェンス到達＝距離0で跳ね返り待ちに入る）。
        // 積分結果を柵で打ち切るだけ＝乱数不使用。
        if (ground.CrossedFence && !clearsFence)
        {
            var hit = ground.FenceCrossPosition;
            ground = new BallisticIntegrator.GroundResult(
                ground.FenceCrossSeconds, new Vector3D(hit.X, 0.0, hit.Z), ground.FenceCrossVelocity, ground.ApexHeightM);
        }

        var landing = ground.LandingPosition;
        var range = Math.Sqrt(landing.X * landing.X + landing.Z * landing.Z);

        // 4分類のうち「空を飛ぶ側」（Issue #63 (b)）。頂点÷水平到達距離が小さい＝低い弾道＝ライナー。
        // 地上側（ゴロ/バウンド）は内野ゴロ経路でバウンド積分の結果から決める。
        var airClass = ground.ApexHeightM / Math.Max(range, 1e-6) <= coeff.LinerApexRangeRatio
            ? BattedBallClass.Liner : BattedBallClass.Fly;

        // 回収点(FieldedX/Z)はここでは埋めない。転がりを解く経路だけが上書きし、それ以外は
        // null＝着地点フォールバックで同値になる（Issue #315）。本番でもフォールバックを通す
        // ことで、テストだけが踏む死んだ経路にならないようにしている。
        FieldingPlay Base(BattedBallResult result) => new()
        {
            Result = result,
            LandingX = landing.X,
            LandingZ = landing.Z,
            HangTimeSeconds = ground.HangTimeSeconds,
            ApexHeightM = ground.ApexHeightM,
            LaunchHeightM = ball.ContactHeightM,
            RangeM = range,
            BearingDeg = ball.BearingDeg,
            BatterToFirstSeconds = runnerTime,
            Class = airClass,
        };

        // 本塁打（上の clearsFence）。着地点は柵の外＝本塁打らしく飛んでいく絵のまま。
        if (clearsFence)
        {
            return Base(BattedBallResult.HomeRun) with
            {
                HomeRunStyle = Timeline.HomeRunStyles.Classify(
                    ground.FenceCrossPosition.Y - field.FenceHeightM, ball.LaunchAngleDeg),
                FenceCrossRangeM = Math.Sqrt(ground.FenceCrossPosition.X * ground.FenceCrossPosition.X
                                             + ground.FenceCrossPosition.Z * ground.FenceCrossPosition.Z),
                FenceCrossHeightM = ground.FenceCrossPosition.Y,
            };
        }

        var isFly = ground.ApexHeightM >= coeff.FlyApexThresholdM;

        // 空中捕球判定（フライ／ライナー）。
        if (isFly)
        {
            // フェンス際の捕りにくさ（issue #976）: 壁を探して減速するぶん、捕球に使える時間が減る（乱数不使用）。
            // 捕るのは「予算内に届く野手の中で最速」（通り道のライナー捕球と同じ基準・issue #1243）。
            var (catcher, reach, catchBudget) = BestAirCatcher(
                fielders, landing, ground.HangTimeSeconds, coeff.WallCatchPenalty(fenceDist - range), coeff);
            // 捕球余裕（正=余裕あり、負=間に合わず）。判定オーバーレイ「フェア」（Issue #328②）の表示だけでなく、
            // フライ落球の難度連動（issue #532）にも使う＝平凡な飛球ほど落球確率が下限へ落ちる物理量。
            var catchMargin = catchBudget - reach;
            if (!fenceHitAboveReach && reach <= catchBudget)
            {
                // フライ落球は「実際に捕る野手（BestAirCatcher）」の Catching で引く（issue #532 C: 距離で選ぶ
                // NearestFielder との食い違いを解消）。難度は捕球余裕 catchMargin で伸縮（issue #532 A/B）。
                var result = MaybeFlyError(catcher, catchMargin, coeff, rng);
                return Base(result) with
                {
                    IsFly = true,
                    FielderRole = catcher.Position,
                    FieldedAtSeconds = ground.HangTimeSeconds,
                    FielderThrowSpeedMps = catcher.Attributes.ThrowSpeedMps,
                    FielderFielding = catcher.Attributes.Fielding,
                    FielderThrowAccuracy = catcher.Attributes.ThrowAccuracy,
                    CatchMarginSeconds = catchMargin,
                    // 空中捕球のエラーは常に捕球エラー（落球）。ロール結果の記録だけ＝新規RNGなし（issue #439）
                    ErrorKind = result == BattedBallResult.Error ? FieldingErrorKind.Catch : null,
                };
            }
            // 捕れないフライ → 安打。着地後の転がりと幾何・走力で塁打数を決める。
            return Hit(Base, ground, landing, fenceDist, field, batter, fielders, coeff, runnerTime, rng)
                with { IsFly = true, CatchMarginSeconds = catchMargin };
        }

        // ゴロ/低いライナー。着地後のバウンド積分（Issue #63 (a)）をここで解く。
        if (range <= coeff.InfieldDepthM)
        {
            // イレギュラーはここで一律確率で抽選する（Issue #63 (c)）。
            var irregular = DrawIrregular(coeff, rng);
            var path = BouncePath.Compute(landing, ground.LandingVelocity, ground.HangTimeSeconds, coeff, irregular);
            var groundClass = path.MaxBounceApexM >= coeff.BouncerApexThresholdM
                ? BattedBallClass.Bouncer : BattedBallClass.Grounder;

            FieldingPlay GroundBase(BattedBallResult result) => Base(result) with
            {
                Class = groundClass,
                BounceApexHeightM = path.MaxBounceApexM,
                BounceCount = path.Hops.Count,
                IrregularBounce = path.Irregular,
            };

            // Issue #63 (b) やること2: バウンド/転がりで内野手を抜けて外野へ達する打球。
            // どの内野手も打球を（守備範囲内・グラブの届く高さで）内野の外縁より手前で捕えられず、
            // かつ外野まで運ぶ強い打球のときだけ「内野を抜けた」＝外野への二次展開。
            // この頭上抜け判定（crossBoundary かつ !InfielderCanCutOff）は Issue #261 でも一切変えない
            // ＝ThroughInfield 件数・#63 の帯を保存する。内野で収まる打球の「処理野手」だけを実捕球点へ移す。
            var crossBoundary = path.TimeCrossingRange(coeff.InfieldDepthM, coeff.InfieldInterceptStepSeconds);
            if (crossBoundary is { } oct && !InfielderCanCutOff(fielders, path, coeff))
            {
                // 抜けた先は Issue #24 で校正済みの集約転がりモデルへ引き継ぐ。
                var td = path.TouchdownAtOrAfter(oct);
                var through = td.Airborne
                    ? ExtraBaseResolver.ComputeRoll(td.Position, td.Velocity, td.Seconds, fenceDist, coeff, field)
                    : ExtraBaseResolver.RollFrom(
                        td.Position, path.DirectionAt(td.Seconds), path.HorizontalSpeedAt(td.Seconds),
                        td.Seconds, fenceDist, coeff, field);
                var result = HitFromRoll(GroundBase, through, field, batter, fielders, coeff, runnerTime, rng)
                    with { ThroughInfield = true };
                // 表示専用（Issue #336②）: 最も惜しく迫った内野手と最接近点。判定・帯には無関係。
                if (FindClosestInfielderMiss(fielders, path, coeff) is { } miss)
                {
                    result = result with
                    {
                        ClosestInfielderRole = miss.Fielder.Position,
                        ClosestApproachX = miss.Point.X,
                        ClosestApproachZ = miss.Point.Z,
                    };
                    result = WithSecondInfielder(result, fielders, path, coeff, miss.Fielder, miss.Point);
                }
                return result;
            }

            // 内野が処理 → 一塁送球 vs 打者走者。処理野手・処理時刻・捕球点はバウンド経路を辿って
            // 「打球が実際に転がり込む内野手」を実捕球点で決める（Issue #261。着地点決め打ちを廃止）。
            var (infielder, fieldTimeRaw, catchPoint, advanceM) = FieldInfieldGrounder(fielders, path, coeff);
            var fieldTime = fieldTimeRaw;
            // Issue #63 (a) やること1: 高く弾むバウンド（チョッパー）は落ちてくるまで野手が待たされる。
            // 待ち時間＝最大バウンド頂点からの自由落下時間×係数（バウンド分類のときだけ効かせる）。
            if (groundClass == BattedBallClass.Bouncer)
            {
                fieldTime += coeff.ChopperWaitFactor
                             * Math.Sqrt(2.0 * path.MaxBounceApexM / BouncePath.GravityMps2);
            }
            var throwDist = (field.FirstBase - catchPoint).Length;
            // 前進捕球のぶんだけ体勢が悪い（issue #630）。前進0＝従来と恒等。捕球点が本塁寄りへ動いて
            // 送球距離が縮むのを、握り替えの追加時間と送球精度の低下で食い戻す＝一方的な守備強化にしない。
            var chargeFrac = MaxChargeAdvanceM(infielder.Position, coeff) is var maxAdvance and > 0.0
                ? MathUtil.Clamp(advanceM / maxAdvance, 0.0, 1.0)
                : 0.0;
            var throwAccuracy = (int)Math.Round(
                infielder.Attributes.ThrowAccuracy - coeff.ChargeThrowAccuracyPenalty * chargeFrac);
            // 握り替え（トランスファー）を守備力で伸縮（Issue #36）。守備50で×1.0＝現行と恒等。
            // 内野は外野返球より強く効かせる（issue #765: 上手い内野手ほど捕ってから離すまでが速い）。
            var handFactor = infielder.Attributes.TransferFactor(coeff.InfieldTransferFieldingSlope, coeff.InfieldTransferFactorMin);
            // 前進捕球（issue #630）の体勢の悪さは握り替えへ乗せる（チャージ量に比例）。
            var transfer = coeff.ThrowTransferSeconds * handFactor
                           + coeff.ChargeThrowPenaltySeconds * chargeFrac;
            // もたつき（判定境界の校正項を物理層へ移した項）。**引いた値がそのまま絵の秒**になる
            // ＝送球開始が遅れる形で見える。守備力で伸縮（issue #765: 名手はほぼもたつかず、下手ほど握り損ねる）。
            // 乱数は守備に関係なく必ず1つ引く（不変条件#2）。
            var jitterFactor = infielder.Attributes.TransferFactor(coeff.InfieldHandleJitterFieldingSlope, coeff.InfieldHandleJitterFactorMin);
            var handleJitter = DrawHandleJitter(coeff, rng, jitterFactor);
            // 送球の逸れ（issue #858）: 送球精度から決まる期待遅れの、リーグ平均（精度50）からの差分だけを
            // 送球時間へ乗せる（ThrowScatterCenteredBiasSeconds）。精度50では常に0（定義により厳密に0.0。
            // 両項が同じ式に同じσ・同じ受け手Catching=50を渡すため浮動小数も完全一致）＝精度50の既存テストは
            // 1件も結果が変わらない。受け手は league average 固定（この送球では受け手を特定しない。帯への
            // 影響をゼロに保つための意図的な簡略化, issue #858）。ThrowErrorBaseProb<=0（悪送球ロール自体が
            // 無効化された呼び出し）では0固定＝「送球精度がこの送球に一切効かない」という従来の恒等を保つ
            // （ThrowAccuracyErrorTests.ThrowErrorDisabled_IsIdenticalToCatchOnly が精度だけ振って完全一致を要求する）。
            var throwScatterBias = coeff.ThrowErrorBaseProb > 0.0 ? ThrowScatterCenteredBiasSeconds(throwAccuracy, coeff) : 0.0;
            // 一塁送球1本の逸れ（issue #859）: 送球は1本＝逸れも1つ。ここで引いた値を ①その本の遅れ（期待値との差＝
            // ゼロ平均のゆらぎ）として送球の秒へ足し ②悪送球かどうかの判定（MaybeInfieldError）にもそのまま使い
            // ③絵（受け手の寄せ）にも渡す。悪送球ロールが無効（ThrowErrorBaseProb<=0）のときは引かない＝上と同じ恒等。
            ThrowScatterModel.Scatter? firstThrowScatter = null;
            if (coeff.ThrowErrorBaseProb > 0.0)
            {
                var sigmaCm = ThrowScatterModel.SigmaCmForAccuracy(throwAccuracy);
                var sampled = ThrowScatterModel.Sample(sigmaCm, rng);
                firstThrowScatter = sampled;
                throwScatterBias += ThrowScatterModel.RecoverySeconds(sampled, receiverCatching: 50,
                                        coeff.ThrowScatterLateralSecPerCm, coeff.ThrowScatterHighSecPerCm, coeff.ThrowScatterShortHopSecPerCm,
                                        coeff.ThrowScatterCatchingReliefSlope, coeff.ThrowScatterCatchingReliefMinFactor)
                                    - ThrowScatterModel.ExpectedRecoverySeconds(sigmaCm, receiverCatching: 50,
                                        coeff.ThrowScatterLateralSecPerCm, coeff.ThrowScatterHighSecPerCm, coeff.ThrowScatterShortHopSecPerCm,
                                        coeff.ThrowScatterCatchingReliefSlope, coeff.ThrowScatterCatchingReliefMinFactor);
            }
            // 一塁送球の飛行（issue #1685）: 前へ突っ込みながら・一塁と逆へ流れながらの送球は球が弱く、
            // 長い送球は失速する。内野安打の校正を「捕ってから長く持つ」から送球の物理へ移した項。
            var throwFlight = FirstBaseThrowFlightSeconds(
                throwDist, infielder, chargeFrac, AwayMomentumFraction(infielder, catchPoint, fieldTimeRaw, field.FirstBase), coeff, handFactor);
            var throwTime = fieldTime + coeff.InfieldPlayOverheadSeconds
                            + transfer + handleJitter + throwFlight
                            + throwScatterBias;
            var firstThrowSpeed = throwFlight > 1e-9 ? throwDist / throwFlight : infielder.Attributes.ThrowSpeedMps;
            // アンアシスト刺殺（3U, Issue #400）: 処理野手が一塁手なら、投手カバーへ送球する以外に「自分で
            // ベースを踏む」選択肢を持つ。捕球点がベース至近なら握り替え0.70sを払わずに済むぶん自重で踏むほうが
            // 速い＝実際の 3U が発生する。所要は握り替え・送球を除いた「捕球点→一塁ベースを走り込む」時間で、
            // 送球所要と物理層の秒だけで比較して速いほうを採る（不変条件#1: 表示能力値から確率を引かない）。
            var unassistedPutout = false;
            var defenseTime = throwTime;
            if (infielder.Position == FieldPosition.FirstBase)
            {
                // もたつきは「捕ってから次の動作に移るまで」なので、自分で踏みにいく側も同じだけ払う
                var carryTime = fieldTime + coeff.InfieldPlayOverheadSeconds + handleJitter
                                + throwDist / infielder.Attributes.SprintSpeedMps;
                if (carryTime < throwTime)
                {
                    unassistedPutout = true;
                    defenseTime = carryTime;
                }
            }
            // 判定margin（Issue #59）: 0=判定境界（=defenseTime+ForceOutMargin と runnerTime が拮抗）、
            // 正=セーフ寄り、負=アウト寄り。表示専用（判定・帯には影響しない）。
            var judgementMargin = defenseTime - runnerTime + coeff.ForceOutMarginSeconds;

            // イレギュラーバウンドは捕球ロールへ加算＝「アウトになる打球でも弾いて失策」になりうる（Issue #63 (c)）。
            var errorBonus = irregular.Active ? coeff.ErrorIrregularBonus : 0.0;
            if (defenseTime + coeff.ForceOutMarginSeconds <= runnerTime)
            {
                // 送球が間に合う → アウト。捕球ロール(Catching＋イレギュラー加算)→送球ロール(ThrowAccuracy)の
                // 2段階失策（Issue #37/#63, design-14 P1-6b）。失策ロールは実捕球点の野手で引く（Issue #261）。
                var (result, errorKind) = MaybeInfieldError(
                    fielders, infielder, coeff, rng, errorBonus, throwAccuracy, firstThrowScatter, unassistedPutout);
                double? recoveredScatterM = null, recoveredOutSeconds = null, wildScatterM = null;
                double? recoveredCatchScatterM = null, recoveredCatchOutSeconds = null;
                double? catchErrorScatterM = null, catchErrorReadyAtSeconds = null, catchErrorHandlingSeconds = null;
                var recoveredByBackup = false;
                if (errorKind == FieldingErrorKind.Catch)
                {
                    // 捕球ミスの回収レース（design-14 P1-6 追記, issue #817）: 送球逸れの回収レースと同じ形。
                    // こぼれた球は捕球点のすぐそば＝バックアップ野手という概念は無く、回収役は常に処理野手自身。
                    // こぼれ幅→拾い直しのロス（反応遅延+こぼれ幅÷脚力）を守備所要時間へ加算してもう一度
                    // 同じ判定基準で再評価する。間に合えば「弾いたのに一塁アウト」が構造的に成立する。
                    // 転がった球を地面から拾い上げる間（CatchFumblePickupSeconds, issue #1685）も払う＝
                    // お手玉の失策は「捕ってから長く持つ」でなく、ここ（弾いて拾い直す）で起きる。
                    var scatterM = Math.Abs(rng.NextGaussian(0.0, coeff.CatchFumbleScatterSigmaM));
                    var recovery = coeff.RecoveryReactionSeconds + coeff.CatchFumblePickupSeconds
                                   + scatterM / infielder.Attributes.SprintSpeedMps;
                    if (defenseTime + recovery + coeff.ForceOutMarginSeconds <= runnerTime)
                    {
                        result = BattedBallResult.Out;
                        errorKind = null;
                        // 表示専用（issue #817）: 弾いた球を拾い直してアウト、を絵に出すために回収レースの値を残す
                        recoveredCatchScatterM = scatterM;
                        recoveredCatchOutSeconds = defenseTime + recovery;
                    }
                    else
                    {
                        // 拾い直しが間に合わず失策のまま確定（issue #816）: 球は捕球点のすぐそばに留まる
                        // （位置は変えない）が、野手が次の送球に動けるのは拾い直しを終えた後＝fieldTime+recovery。
                        // この時刻を呼び出し側（GameEngine の本塁/三塁クロスプレー文脈）へ渡すことで、
                        // 塁上の走者の次塁判断・守備との時間勝負が「弾いた直後に送球できる」誤った前提を使わずに済む。
                        catchErrorScatterM = scatterM;
                        catchErrorReadyAtSeconds = fieldTime + recovery;
                        // 拾い直したあと離すまでの手間（表示専用・issue #1733）。一塁手の自力で踏む場合は握り替えを払わず走る。
                        var travelSeconds = throwDist / (unassistedPutout ? infielder.Attributes.SprintSpeedMps : infielder.Attributes.ThrowSpeedMps);
                        catchErrorHandlingSeconds = Math.Max(0.0, defenseTime - fieldTime - travelSeconds);
                    }
                }
                if (errorKind == FieldingErrorKind.Throw)
                {
                    // 送球逸れの回収レース（design-12 §3.8, 2026-08-30）: 悪送球確定後、逸れ幅→回収ロスを
                    // 守備所要時間へ加算してもう一度同じ判定基準で再評価する。拾い直しが間に合えば
                    // 従来どおりアウトのまま（「毎回逸れたら即エラー」にはしない）。回収役は一塁手自身
                    // （返球不要）とバックアップ（右翼手, ThrowRecovery.BackupRole）の近い方。
                    var scatterM = Math.Abs(rng.NextGaussian(0.0, coeff.WildThrowScatterSigmaM));
                    var throwLanding = ThrowRecovery.LandingPoint(catchPoint, field.FirstBase, scatterM);
                    var receiver = FirstBaseReceiver(fielders, infielder);
                    var backup = fielders.First(f => f.Position == ThrowRecovery.BackupRole(1));
                    var recovery = ThrowRecovery.RecoveryPenaltySeconds(
                        scatterM, throwLanding, backup.Location,
                        receiver.Attributes.SprintSpeedMps, backup.Attributes.SprintSpeedMps,
                        backup.Attributes.ThrowSpeedMps, coeff.RecoveryReactionSeconds, out var byBackup);
                    if (defenseTime + recovery + coeff.ForceOutMarginSeconds <= runnerTime)
                    {
                        result = BattedBallResult.Out;
                        errorKind = null;
                        // 表示専用（issue #815）: 逸れた送球を拾い直してアウト、を絵に出すために回収レースの値を残す
                        recoveredScatterM = scatterM;
                        recoveredByBackup = byBackup;
                        recoveredOutSeconds = defenseTime + recovery;
                    }
                    else
                    {
                        wildScatterM = scatterM;   // 表示専用（issue #812）: 逸れ方の絵の入力
                    }
                }
                if (errorKind == FieldingErrorKind.Tunnel)
                {
                    // トンネル（issue #632）: 野手は触れず、球は捕球点から<b>その瞬間の速度のまま</b>
                    // 転がり続ける。以降は内野を抜けた打球とまったく同じ経路＝外野手が回収して返球する
                    // （回収野手・回収時刻・回収点・肩は HitFromRoll が埋める）ので、二塁走者の生還は
                    // 一律確率ではなく外野からの返球との時間勝負（HomePlayResolver）で決まる。
                    // 時刻は fieldTimeRaw（経路上の実時刻）で採る。fieldTime のチョッパー待ちは
                    // 「野手が落ちてくるのを待つ」時間なので、触らずに抜けた球には乗らない。
                    var through = ExtraBaseResolver.RollFrom(
                        catchPoint, path.DirectionAt(fieldTimeRaw), path.HorizontalSpeedAt(fieldTimeRaw),
                        fieldTimeRaw, fenceDist, coeff, field);
                    return WithSecondInfielder(HitFromRoll(GroundBase, through, field, batter, fielders, coeff, runnerTime, rng) with
                    {
                        Result = BattedBallResult.Error,
                        ThroughInfield = true,
                        ErrorKind = FieldingErrorKind.Tunnel,
                        ErrorFielderRole = infielder.Position,     // 失策は抜かれた内野手に付く
                        ErrorAtSeconds = fieldTimeRaw,             // 「エラー」が出るのは抜かれた瞬間
                        // 表示専用（Issue #336②）: 抜かれた本人が打球ラインへ飛びつく絵の入力。
                        ClosestInfielderRole = infielder.Position,
                        ClosestApproachX = catchPoint.X,
                        ClosestApproachZ = catchPoint.Z,
                    }, fielders, path, coeff, infielder, catchPoint);
                }
                return GroundBase(result) with
                {
                    FieldedX = catchPoint.X,
                    FieldedZ = catchPoint.Z,
                    FielderRole = infielder.Position,
                    FieldedAtSeconds = fieldTime,
                    ThrowArriveSeconds = defenseTime,
                    UnassistedPutout = unassistedPutout,
                    FielderThrowSpeedMps = infielder.Attributes.ThrowSpeedMps,
                    FielderFielding = infielder.Attributes.Fielding,
                    FielderThrowAccuracy = throwAccuracy,
                    JudgementMarginSeconds = judgementMargin,
                    ErrorKind = errorKind,
                    // 受け手の捕球ミス（issue #810）: 処理野手（FielderRole）は球を処理した野手（遊撃等）の
                    // ままだが、失策を犯したのは受け手の一塁手（一塁手処理なら一塁カバーの投手）＝ErrorFielderRole で帰属を分ける（トンネルと同じ作法）。
                    ErrorFielderRole = errorKind == FieldingErrorKind.Receive ? FirstBaseReceiver(fielders, infielder).Position : null,
                    // 「エラー」は送球が一塁へ届いて弾かれた瞬間（＝送球到達時刻）に出す。
                    ErrorAtSeconds = errorKind == FieldingErrorKind.Receive ? defenseTime : null,
                    GroundHops = path.Hops,
                    GroundPathCatchSeconds = fieldTimeRaw,
                    RecoveredThrowScatterM = recoveredScatterM,
                    RecoveredThrowByBackup = recoveredByBackup,
                    RecoveredThrowOutSeconds = recoveredOutSeconds,
                    WildThrowScatterM = wildScatterM,
                    RecoveredCatchScatterM = recoveredCatchScatterM,
                    RecoveredCatchOutSeconds = recoveredCatchOutSeconds,
                    CatchErrorScatterM = catchErrorScatterM,
                    CatchErrorReadyAtSeconds = catchErrorReadyAtSeconds,
                    CatchErrorHandlingSeconds = catchErrorHandlingSeconds,
                    FirstThrowScatter = firstThrowScatter,
                    FirstThrowSpeedMps = firstThrowSpeed,
                };
            }
            // お手玉の失策（2026-11-03 監督報告「あれだけファンブルしたら内野安打じゃなくて普通にエラーでは？」）:
            // もたつきが長く（お手玉級）、しかも**もたつきが無ければアウトだった**セーフは、記録上は失策。
            // 新しい乱数は引かない（もたつきは上で引き済み）＝内野安打の一部が失策へ振り替わるだけ。
            if (coeff.BobbleErrorMinJitterSeconds > 0.0
                && handleJitter >= coeff.BobbleErrorMinJitterSeconds
                && defenseTime - handleJitter + coeff.ForceOutMarginSeconds <= runnerTime)
            {
                return GroundBase(BattedBallResult.Error) with
                {
                    GroundHops = path.Hops,
                    GroundPathCatchSeconds = fieldTimeRaw,
                    FieldedX = catchPoint.X,
                    FieldedZ = catchPoint.Z,
                    FielderRole = infielder.Position,
                    FieldedAtSeconds = fieldTime,
                    ThrowArriveSeconds = defenseTime,
                    UnassistedPutout = unassistedPutout,
                    FielderThrowSpeedMps = infielder.Attributes.ThrowSpeedMps,
                    FielderFielding = infielder.Attributes.Fielding,
                    FielderThrowAccuracy = throwAccuracy,
                    JudgementMarginSeconds = judgementMargin,
                    ErrorKind = FieldingErrorKind.Bobble,
                    ErrorFielderRole = infielder.Position,
                    ErrorAtSeconds = fieldTime,   // 「エラー」は弾いた瞬間（捕球直後）に出す
                    FirstThrowScatter = firstThrowScatter,
                    FirstThrowSpeedMps = firstThrowSpeed,
                };
            }
            // 間に合わず内野安打（失策ロールは引かない＝main の校正モデルを保つ）。
            return GroundBase(BattedBallResult.Single) with
            {
                GroundHops = path.Hops,
                GroundPathCatchSeconds = fieldTimeRaw,
                FieldedX = catchPoint.X,
                FieldedZ = catchPoint.Z,
                FielderRole = infielder.Position,
                FieldedAtSeconds = fieldTime,
                ThrowArriveSeconds = defenseTime,
                UnassistedPutout = unassistedPutout,
                FielderThrowSpeedMps = infielder.Attributes.ThrowSpeedMps,
                FielderFielding = infielder.Attributes.Fielding,
                FielderThrowAccuracy = throwAccuracy,
                JudgementMarginSeconds = judgementMargin,
                FirstThrowScatter = firstThrowScatter,
                FirstThrowSpeedMps = firstThrowSpeed,
            };
        }

        // 内野を抜けた（外野への低い打球）→ 安打。転がりと幾何・走力で塁打数を決める。
        return Hit(Base, ground, landing, fenceDist, field, batter, fielders, coeff, runnerTime, rng);
    }

    /// <summary>
    /// イレギュラーバウンドの抽選（Issue #63 (c)。一律確率＝球場の土質との接続は将来の別Issue）。
    /// 乱数消費: 発生判定1回＋発生時に横ぶれ・反発の2回。
    /// </summary>
    /// <summary>
    /// 内野の打球処理のもたつき[s]を引く（<see cref="FieldingCoefficients.InfieldHandleJitterMeanSeconds"/>）。
    ///
    /// <para>指数分布の逆関数法。下限0・右に裾＝「たいていは澱みなく、たまに大きくもたつく」。
    /// 上限は平均の <see cref="FieldingCoefficients.InfieldHandleJitterMaxFactor"/> 倍で切る
    /// （指数の裾は無限に伸びるので、切らないと「捕ってから4秒動かない内野手」が極稀に出る＝
    /// これは<b>絵に出る量</b>なので実在しない値を残さない）。</para>
    ///
    /// <para>⚠ 平均が 0 でも<b>必ず1つ引く</b>。分岐で消費数を変えると同シード同結果が壊れる
    /// （不変条件#2。係数を 0 にして帯を比べる、という運用ができなくなる）。</para>
    /// </summary>
    /// <summary>
    /// 送球精度のリーグ平均（50）からの差分だけの期待遅れ[s]（issue #858）。内野ゴロ→一塁送球・外野からの
    /// 返球（<see cref="ExtraBaseResolver.ThrowArriveSeconds"/>）が共有する。受け手は league average 固定
    /// （Catching=50＝個別の受け手を特定しない。帯への影響をゼロに保つための意図的な簡略化）。
    /// 精度50では厳密に0.0（<see cref="ThrowScatterModel.CenteredExpectedRecoverySeconds"/> の定義により、
    /// 既存の精度50の呼び出しは浮動小数まで完全に不変）。
    /// </summary>
    internal static double ThrowScatterCenteredBiasSeconds(int throwAccuracy, FieldingCoefficients coeff)
        => ThrowScatterModel.CenteredExpectedRecoverySeconds(
            ThrowScatterModel.SigmaCmForAccuracy(throwAccuracy), receiverCatching: 50,
            coeff.ThrowScatterLateralSecPerCm, coeff.ThrowScatterHighSecPerCm, coeff.ThrowScatterShortHopSecPerCm,
            coeff.ThrowScatterCatchingReliefSlope, coeff.ThrowScatterCatchingReliefMinFactor);

    /// <summary>
    /// 内野ゴロ→一塁送球の飛行時間[s]（issue #1685）。乱数不使用。
    ///
    /// <para>球速 = 肩 ×(1 − <see cref="FieldingCoefficients.ChargeThrowSpeedPenalty"/>×前進率
    /// − <see cref="FieldingCoefficients.AwayMomentumThrowSpeedPenalty"/>×流れ率)（下限
    /// <see cref="FieldingCoefficients.InfieldThrowSpeedFactorMin"/>。短いトスほど弱まりを薄める
    /// <see cref="FieldingCoefficients.InfieldThrowPostureFullDistanceM"/>。弱まりは守備の手さばき倍率
    /// <paramref name="handFactor"/>（握り替えと同じ <c>TransferFactor</c>・守備50で×1.0）で伸縮）。前へ突っ込みながら（ボテボテ）・
    /// 一塁と逆へ流れながら（三遊間・二遊間の深い所）の送球は、足を踏ん張れず球が弱い。
    /// 飛行時間 = 距離÷球速×(1＋距離×<see cref="FieldingCoefficients.ThrowDistanceDragPerM"/>)
    /// ＝長い送球ほど失速して山なりになる（外野の返球 <see cref="ExtraBaseResolver.ThrowArriveSeconds"/> と同じ失速の式）。</para>
    /// </summary>
    public static double FirstBaseThrowFlightSeconds(
        double throwDistM, Fielder thrower, double chargeFrac, double awayFrac, FieldingCoefficients coeff,
        double handFactor = 1.0)
    {
        // 短いトス（一塁手→ベースカバーの投手など）は下半身を使わないので体勢の影響を受けにくい＝
        // 距離が InfieldThrowPostureFullDistanceM に届くまで弱まりを比例で薄める。
        var posture = coeff.InfieldThrowPostureFullDistanceM > 0.0
            ? Math.Min(1.0, throwDistM / coeff.InfieldThrowPostureFullDistanceM) : 1.0;
        // 体勢の悪さの弱まりは手さばき（握り替えと同じ守備の倍率 handFactor）で伸縮する＝上手い内野手ほど
        // 足を運び直して強い球を投げられる（issue #765 の「上手い内野手ほど機敏」を、保持でなく送球の側にも持たせる）。
        var factor = Math.Max(coeff.InfieldThrowSpeedFactorMin,
            1.0 - posture * handFactor
                * (coeff.ChargeThrowSpeedPenalty * chargeFrac + coeff.AwayMomentumThrowSpeedPenalty * awayFrac));
        var speed = thrower.Attributes.ThrowSpeedMps * factor;
        return throwDistM / speed * (1.0 + throwDistM * coeff.ThrowDistanceDragPerM);
    }

    /// <summary>
    /// 捕球した瞬間に、体が一塁と<b>逆向きへ</b>どれだけ流れているか（0〜1, issue #1685）。乱数不使用。
    /// 守備位置→捕球点を走った向きと捕球点→一塁の向きのなす角の余弦の負の部分（真逆へ走っていれば1）に、
    /// 走った速さ÷全力（守備位置から捕球点までの距離÷（捕球時刻−反応遅延）を全力で頭打ち）を掛ける。
    /// 正面の打球（ほぼ動かない）・一塁側へ動いて捕る打球は0。
    /// </summary>
    public static double AwayMomentumFraction(Fielder f, Vector3D catchPoint, double catchSeconds, Vector3D firstBase)
    {
        var rx = catchPoint.X - f.Location.X;
        var rz = catchPoint.Z - f.Location.Z;
        var run = Math.Sqrt(rx * rx + rz * rz);
        var tx = firstBase.X - catchPoint.X;
        var tz = firstBase.Z - catchPoint.Z;
        var toFirst = Math.Sqrt(tx * tx + tz * tz);
        if (run < 1e-6 || toFirst < 1e-6) return 0.0;
        var away = Math.Max(0.0, -(rx * tx + rz * tz) / (run * toFirst));
        var sprint = f.Attributes.SprintSpeedMps;
        var runSeconds = Math.Max(0.1, catchSeconds - f.Attributes.ReactionDelaySeconds);
        var speed = Math.Min(sprint, run / runSeconds);
        return away * speed / sprint;
    }

    internal static double DrawHandleJitter(FieldingCoefficients coeff, IRandomSource rng, double jitterFactor = 1.0)
    {
        var u = rng.NextDouble();
        if (coeff.InfieldHandleJitterMeanSeconds <= 0.0) return 0.0;
        // u∈[0,1) なので 1−u∈(0,1] ＝ log の引数は必ず正
        var jitter = -coeff.InfieldHandleJitterMeanSeconds * Math.Log(1.0 - u);
        var cap = coeff.InfieldHandleJitterMeanSeconds * coeff.InfieldHandleJitterMaxFactor;
        // 守備倍率を掛けた後で頭打ちにする。先に切ると倍率<1（守備51以上）の最大値が cap×倍率 に縮み、
        // お手玉のしきい値（<see cref="FieldingCoefficients.BobbleErrorMinJitterSeconds"/>）へ届かず
        // 失策が0になる（issue #1259: 能力が逆向きに効く崖）。倍率≥1（守備50以下）は cap×倍率 で従来と同一。
        return Math.Min(jitter * jitterFactor, cap * Math.Max(1.0, jitterFactor));
    }

    private static IrregularBounce DrawIrregular(FieldingCoefficients coeff, IRandomSource rng)
    {
        if (!MathUtil.Chance(coeff.IrregularBounceProb, rng)) return IrregularBounce.None;
        var lateral = (rng.NextDouble() * 2.0 - 1.0) * coeff.IrregularBounceLateralDeg;
        var scale = 1.0 + (rng.NextDouble() * 2.0 - 1.0) * coeff.IrregularBounceRestitutionSwing;
        return new IrregularBounce(true, lateral, scale);
    }

    /// <summary>
    /// 内野の通過走査（<see cref="InfielderCanCutOff"/>／<see cref="FindClosestInfielderMiss"/>）の反復上限。
    /// 打球が内野に止まる・NaN になる等で「内野を出た」に届かない経路でも必ず終わらせる安全弁
    /// （過去の CameraPlan 無限ループと同型）。刻み0.005s でも100秒ぶん＝実在の打球は届かない値。
    /// </summary>
    private const int MaxInterceptScanSteps = 20_000;

    /// <summary>
    /// 野手がある点へ詰めて打球に触れるまでの時間[s]（反応遅延込み。issue #1789 やること4②）。
    /// 投手は投げ終わりで体勢が崩れているぶん、<see cref="FieldingCoefficients.PitcherFieldingReactionPenaltySeconds"/>
    /// だけ余分にかかる（issue #1789）。<see cref="InfielderCanCutOff"/>・<see cref="FieldInfieldGrounder"/> の
    /// cutTime・bodyTime・gloveTime・<see cref="ChargeCatch"/>・表示専用の最接近探索
    /// （<see cref="FindClosestInfielderMiss"/>・<see cref="FindOppositeSideInfielderMiss"/>）
    /// 全てがこの1つの式を通ることで、「内野を抜けるか」の判定と「誰がどこで触れるか」が食い違わない。
    ///
    /// <para>走行時間は加速（止まった状態からトップスピードに乗るまで）込みで求める。トップスピードは
    /// 守備位置→<paramref name="to"/> の変位の横（X）成分の割合ぶん <see cref="FieldingCoefficients.InfielderLateralSpeedFactor"/>
    /// で割り引く（クロスオーバーステップを要る横の動きは前後より遅い）。反応遅延は、<paramref name="ballSpeedMps"/>が
    /// <see cref="FieldingCoefficients.FastGrounderReactionThresholdMps"/> を超えるぶんだけ一歩目が遅れる
    /// （強い打球ほど読みに手間取る）。<paramref name="radius"/> は呼び出し側が渡す守備範囲・飛びつきリーチ（0=実距離）。</para>
    /// </summary>
    private static double FielderReachSeconds(
        Fielder f, Vector3D to, double radius, double ballSpeedMps, FieldingCoefficients coeff)
    {
        var dx = to.X - f.Location.X;
        var dz = to.Z - f.Location.Z;
        var dist = Math.Sqrt(dx * dx + dz * dz);
        var travel = Math.Max(0.0, dist - radius);
        var lateralFrac = dist > 1e-9 ? Math.Abs(dx) / (Math.Abs(dx) + Math.Abs(dz) + 1e-9) : 0.0;
        var vmax = f.Attributes.SprintSpeedMps
                   * (1.0 - lateralFrac * (1.0 - coeff.InfielderLateralSpeedFactor));
        var reach = RunUpSeconds(travel, vmax, coeff.InfielderAccelMps2) + f.Attributes.ReactionDelaySeconds;
        if (f.Position == FieldPosition.Pitcher) reach += coeff.PitcherFieldingReactionPenaltySeconds;
        var excessMps = ballSpeedMps - coeff.FastGrounderReactionThresholdMps;
        if (excessMps > 0.0)
        {
            reach += Math.Min(
                coeff.FastGrounderReactionExtraMaxSeconds, excessMps * coeff.FastGrounderReactionExtraSecPerMps);
        }
        return reach;
    }

    /// <summary>
    /// 距離 <paramref name="travel"/> を、止まった状態から加速度 <paramref name="accelMps2"/> で
    /// トップスピード <paramref name="vmax"/> まで立ち上がりつつ走る所要時間[s]（issue #1789 やること4②）。
    /// 立ち上がり距離（<c>vmax²/(2×accel)</c>）内なら等加速度の式 √(2d/accel)、それを超えたらトップスピード
    /// に乗ってからは等速。<paramref name="accelMps2"/>→∞（または0以下＝無効値扱い）で旧式（travel/vmax）と一致する。
    /// </summary>
    private static double RunUpSeconds(double travel, double vmax, double accelMps2)
    {
        if (travel <= 0.0) return 0.0;
        if (accelMps2 <= 0.0 || vmax <= 0.0) return travel / Math.Max(vmax, 1e-6);
        var rampDist = vmax * vmax / (2.0 * accelMps2);
        if (travel <= rampDist) return Math.Sqrt(2.0 * travel / accelMps2);
        var rampTime = vmax / accelMps2;
        return rampTime + (travel - rampDist) / vmax;
    }

    /// <summary>
    /// いずれかの内野手が、打球が内野の外縁を越える前に守備範囲（横の半径＋グラブの届く高さ）で
    /// 打球のライン上を捕えられるか（Issue #63 やること2の判定）。乱数は消費しない＝決定論。
    /// 捕えられない＝ホールを抜く／頭上を越すほど強い打球のときだけ内野を抜けたと判定する。
    /// </summary>
    internal static bool InfielderCanCutOff(
        IReadOnlyList<Fielder> fielders, BouncePath path, FieldingCoefficients coeff)
    {
        var step = Math.Max(0.005, coeff.InfieldInterceptStepSeconds);
        var t = path.HangTimeSeconds;
        for (var i = 0; i < MaxInterceptScanSteps; i++, t += step)
        {
            var p = path.PositionAt(t);
            if (Math.Sqrt(p.X * p.X + p.Z * p.Z) > coeff.InfieldDepthM) return false; // 内野を出た
            if (path.HeightAt(t) <= coeff.InfielderReachHeightM)                        // グラブの届く高さ
            {
                var ballSpeed = path.HorizontalSpeedAt(t);
                foreach (var f in fielders)
                {
                    if (!IsInfield(f)) continue;
                    var radius = Math.Max(0.0, InfielderRadius(f, coeff));
                    if (FielderReachSeconds(f, p, radius, ballSpeed, coeff) <= t) return true; // この時刻・位置で捕えられる
                }
            }
        }
        return false; // 走査上限まで内野に止まったまま誰も届かない（想定外）＝捕えられない扱い
    }

    /// <summary>
    /// 内野を抜けた打球（<see cref="InfielderCanCutOff"/>=false）について、最も打球ラインに迫った
    /// 内野手とその最接近点を求める（Issue #336②・表示専用）。<see cref="InfielderCanCutOff"/> と
    /// 同じ経路・同じ式（守備範囲半径込みの到達可否）を辿るだけの副産物で、判定・帯には一切使わない。
    /// 乱数は消費しない（決定論）。誰も打球の高さ帯を通らない（想定外）場合のみ null。
    /// </summary>
    internal static (Fielder Fielder, Vector3D Point)? FindClosestInfielderMiss(
        IReadOnlyList<Fielder> fielders, BouncePath path, FieldingCoefficients coeff)
    {
        var step = Math.Max(0.005, coeff.InfieldInterceptStepSeconds);
        Fielder? best = null;
        var bestPoint = default(Vector3D);
        var bestShortfall = double.PositiveInfinity;
        var t = path.HangTimeSeconds;
        for (var i = 0; i < MaxInterceptScanSteps; i++, t += step)
        {
            var p = path.PositionAt(t);
            if (Math.Sqrt(p.X * p.X + p.Z * p.Z) > coeff.InfieldDepthM) break; // 内野を出た
            if (path.HeightAt(t) > coeff.InfielderReachHeightM) continue;     // グラブが届かない高さ
            var ballSpeed = path.HorizontalSpeedAt(t);
            foreach (var f in fielders)
            {
                if (!IsInfield(f)) continue;
                var radius = Math.Max(0.0, InfielderRadius(f, coeff));
                var shortfall = FielderReachSeconds(f, p, radius, ballSpeed, coeff) - t; // 正=間に合わない。最小=最も惜しかった。
                if (shortfall < bestShortfall)
                {
                    bestShortfall = shortfall;
                    best = f;
                    bestPoint = p;
                }
            }
        }
        return best is null ? null : (best, bestPoint);
    }

    /// <summary>
    /// 表示専用（issue #1591）: <paramref name="first"/>（最も惜しかった内野手）と打球ラインを挟んで反対側の、
    /// 次に惜しかった内野手（投手を除く）とその最接近点を求める。打球ラインは本塁から
    /// <paramref name="firstPoint"/> へ引いた直線。<paramref name="first"/> がほぼライン上（横ずれ1m未満）なら
    /// どちら側でもよい。判定・乱数には一切触れない。
    /// </summary>
    internal static (Fielder Fielder, Vector3D Point)? FindOppositeSideInfielderMiss(
        IReadOnlyList<Fielder> fielders, BouncePath path, FieldingCoefficients coeff,
        Fielder first, Vector3D firstPoint)
    {
        var len = Math.Sqrt(firstPoint.X * firstPoint.X + firstPoint.Z * firstPoint.Z);
        if (len < 1e-6) return null;
        var dx = firstPoint.X / len;
        var dz = firstPoint.Z / len;
        double SideOf(Fielder f) => dx * f.Location.Z - dz * f.Location.X;
        var firstSide = SideOf(first);
        var anySide = Math.Abs(firstSide) < 1.0;

        var step = Math.Max(0.005, coeff.InfieldInterceptStepSeconds);
        Fielder? best = null;
        var bestPoint = default(Vector3D);
        var bestShortfall = double.PositiveInfinity;
        var t = path.HangTimeSeconds;
        for (var i = 0; i < MaxInterceptScanSteps; i++, t += step)
        {
            var p = path.PositionAt(t);
            if (Math.Sqrt(p.X * p.X + p.Z * p.Z) > coeff.InfieldDepthM) break;
            if (path.HeightAt(t) > coeff.InfielderReachHeightM) continue;
            var ballSpeed = path.HorizontalSpeedAt(t);
            foreach (var f in fielders)
            {
                if (!IsInfield(f) || f.Position == FieldPosition.Pitcher || f.Position == first.Position) continue;
                if (!anySide && SideOf(f) * firstSide >= 0.0) continue;
                var radius = Math.Max(0.0, InfielderRadius(f, coeff));
                var shortfall = FielderReachSeconds(f, p, radius, ballSpeed, coeff) - t;
                if (shortfall < bestShortfall)
                {
                    bestShortfall = shortfall;
                    best = f;
                    bestPoint = p;
                }
            }
        }
        return best is null ? null : (best, bestPoint);
    }

    private static FieldingPlay WithSecondInfielder(
        FieldingPlay play, IReadOnlyList<Fielder> fielders, BouncePath path, FieldingCoefficients coeff,
        Fielder first, Vector3D firstPoint)
        => FindOppositeSideInfielderMiss(fielders, path, coeff, first, firstPoint) is { } second
            ? play with
            {
                SecondInfielderRole = second.Fielder.Position,
                SecondApproachX = second.Point.X,
                SecondApproachZ = second.Point.Z,
            }
            : play;

    /// <summary>
    /// 内野ゴロの処理野手・処理時刻・捕球点を、バウンド経路（Issue #63 の <see cref="BouncePath"/>）を辿って決める
    /// （Issue #261）。着地点（本塁前でワンバウンドした一点）ではなく「打球が実際に転がり込む内野手」が
    /// 自分の守備範囲で処理する。乱数は消費しない＝決定論（不変条件#2）。
    ///
    /// 経路を時間刻みで走査し、内野手ごとに次の2つを集める:
    /// <list type="bullet">
    /// <item><b>処理野手（誰が）</b>: グラブの届く高さ・内野内を通る点で「その時刻までにその点へ実際に届く」
    ///   最初の内野手。到達判定には遊びの半径を与えず実距離で見る（<see cref="InfielderCanCutOff"/> の頭上抜け判定
    ///   ＝帯校正ノブ <see cref="FieldingCoefficients.InfielderFieldingRadiusM"/> はダイビングを含む広い半径で、
    ///   これを流用すると通り道の近くを通っただけの打球までマウンドの投手が吸い込む＝本Issueの不具合になる）。
    ///   投手はフォロースルー直後で守備体勢が崩れているぶん追加の反応遅延を負う
    ///   （<see cref="FieldingCoefficients.PitcherFieldingReactionPenaltySeconds"/>）。</item>
    /// <item><b>捕球点・処理時刻（どこで・いつ）</b>: 基準は処理野手の守備位置に最も近く打球が通る点
    ///   （＝最短の守備範囲）で、強い打球は<b>自分の間合いで</b>捕る（処理時刻は打球がその点へ来る時刻＝打球到達に
    ///   間に合えば待ち、遅れれば追いつく時刻）。緩い打球だけ前へ出て早い時刻で捕る
    ///   （<see cref="ChargeCatch"/>＝issue #630。従来はここが常に「待ち」で、ボテボテのゴロほど長く突っ立って
    ///   待つ絵と内野安打を作っていた）。</item>
    /// </list>
    /// 誰も間に合わないまま内野内で失速して死ぬ緩いゴロは、打球が最も近くを通った内野手が回収する（内野安打寄り）。
    /// </summary>
    private static (Fielder Fielder, double Time, Vector3D Point, double AdvanceM) FieldInfieldGrounder(
        IReadOnlyList<Fielder> fielders, BouncePath path, FieldingCoefficients coeff)
    {
        var step = Math.Max(0.005, coeff.InfieldInterceptStepSeconds);
        var end = path.SettleAtSeconds;

        var infield = new List<Fielder>(5);
        foreach (var f in fielders)
        {
            if (IsInfield(f)) infield.Add(f);
        }
        var n = infield.Count;
        var gloveTime = new double[n];      // 実捕球できる最初のボール時刻（∞=最後まで間に合わない）
        var cutTime = new double[n];        // 守備範囲(半径)込みで触れる最初の時刻＝InfielderCanCutOff と同式
        var cutPoint = new Vector3D[n];     // その触れる点
        var bodyTime = new double[n];       // 飛びつきの実効リーチ（体から届く距離）で処理できる最初の時刻
        var bodyPoint = new Vector3D[n];    // その処理点
        var zoneDist = new double[n];       // 守備位置への最接近距離
        var zoneBallTime = new double[n];   // その最接近点を打球が通る時刻
        var zonePoint = new Vector3D[n];    // その最接近点
        var zoneAtGloveHeight = new bool[n]; // zone* がグラブの届く高さの点で更新済みか
        // 守備範囲・飛びつきリーチは野手ごと（issue #1661: 守備力で半径が変わる）。
        var cutRadius = new double[n];
        var diveReach = new double[n];
        for (var k = 0; k < n; k++)
        {
            gloveTime[k] = double.PositiveInfinity;
            cutTime[k] = double.PositiveInfinity;
            cutPoint[k] = path.Landing;
            bodyTime[k] = double.PositiveInfinity;
            bodyPoint[k] = path.Landing;
            zoneDist[k] = double.MaxValue;
            zoneBallTime[k] = path.HangTimeSeconds;
            zonePoint[k] = path.Landing;
            cutRadius[k] = Math.Max(0.0, InfielderRadius(infield[k], coeff));
            diveReach[k] = Math.Max(0.0, Math.Min(cutRadius[k], coeff.InfielderDiveReachM));
        }

        for (var t = path.HangTimeSeconds; t <= end + step; t += step)
        {
            var tt = Math.Min(t, end);
            var p = path.PositionAt(tt);
            if (Math.Sqrt(p.X * p.X + p.Z * p.Z) > coeff.InfieldDepthM) break; // 内野を出た
            var aboveGlove = path.HeightAt(tt) > coeff.InfielderReachHeightM;  // グラブより高い（またぐ）
            var ballSpeed = path.HorizontalSpeedAt(tt);
            for (var k = 0; k < n; k++)
            {
                var f = infield[k];
                var dist = (p - f.Location).Length;
                // 処理点の基準はグラブの届く高さの点だけ（issue #1260）。グラブより高い点しか通らない野手だけは
                // 従来どおり最接近点（zoneAtGloveHeight=false の間の暫定値）を持つ。
                if (!aboveGlove)
                {
                    if (!zoneAtGloveHeight[k] || dist < zoneDist[k])
                    { zoneDist[k] = dist; zoneBallTime[k] = tt; zonePoint[k] = p; zoneAtGloveHeight[k] = true; }
                }
                else if (!zoneAtGloveHeight[k] && dist < zoneDist[k])
                { zoneDist[k] = dist; zoneBallTime[k] = tt; zonePoint[k] = p; }
                if (aboveGlove) continue;
                if (double.IsPositiveInfinity(cutTime[k]))
                {
                    // 頭上抜け判定（InfielderCanCutOff）と**同じ式**（投手の追加反応遅延込み）。あちらが
                    // 「抜けていない」と言った打球は、必ずここで誰かが触れる時刻・点を持つ＝2つの判定が食い違わない。
                    if (FielderReachSeconds(f, p, cutRadius[k], ballSpeed, coeff) <= tt)
                    {
                        cutTime[k] = tt;
                        cutPoint[k] = p;
                    }
                }
                if (double.IsPositiveInfinity(bodyTime[k]))
                {
                    // 処理点・処理時刻の候補（issue fielder-body-far-from-ball-at-catch）: 体が「飛びつきの
                    // 実効リーチ」まで球に寄れた最初の時刻。守備範囲の半径は「内野を抜けるか」の判定にだけ残す。
                    if (FielderReachSeconds(f, p, diveReach[k], ballSpeed, coeff) <= tt)
                    {
                        bodyTime[k] = tt;
                        bodyPoint[k] = p;
                    }
                }
                if (!double.IsPositiveInfinity(gloveTime[k])) continue;
                if (FielderReachSeconds(f, p, 0.0, ballSpeed, coeff) <= tt) gloveTime[k] = tt; // この点・時刻で実捕球できる
            }
        }

        // 処理野手＝最初に実捕球できる内野手。誰も間に合わなければ最接近が最小の内野手（ゾーン最寄り）。
        var best = -1;
        var bestGlove = double.PositiveInfinity;
        for (var k = 0; k < n; k++)
        {
            if (gloveTime[k] < bestGlove) { bestGlove = gloveTime[k]; best = k; }
        }
        if (best < 0)
        {
            // 実距離では誰も捕れない。**まず頭上抜け判定と同じ式（守備範囲の半径込み＝突っ込む/飛びつく）
            // で触れる野手を採る。** ここを飛ばすと、「抜けていない」と判定された打球なのに
            // 処理野手が居ないことになり、下のゾーン最寄りが打球を内野の外まで追いかける
            // ——BouncePath は内野専用モデル（自由転がりは v²/2a で最大180m）なので、
            // 長い時刻で引くと適用範囲の外へ外挿する（実測: 捕球点が本塁から193m）。
            var bestCut = double.PositiveInfinity;
            for (var k = 0; k < n; k++)
            {
                if (cutTime[k] < bestCut) { bestCut = cutTime[k]; best = k; }
            }
            if (best >= 0)
            {
                // 処理点・処理時刻は「体（グラブ）が球に届く時刻」で採る（issue fielder-body-far-from-ball-at-catch）。
                // 従来は半径 4.2m で触れた点・時刻＝内野ゴロアウトの 22% で体が球から 3〜4m 離れたまま
                // 「捕球→送球」していた（半径は内野を抜けるかの帯校正ノブで、飛びつきの実効リーチより大きい）。
                // 実効リーチ内まで体が寄れる打球はその時刻・点で処理する。寄れないまま内野を出る（走査終端）
                // 打球は飛びついて止めた（触れた点で球が死ぬ）扱い＝体がその点へ着く時刻に拾う。
                var f = infield[best];
                if (!double.IsPositiveInfinity(bodyTime[best]))
                    return (f, bodyTime[best], bodyPoint[best], 0.0);
                var knockedDownAt = Math.Max(cutTime[best], ReachTime(f, cutPoint[best]));
                return (f, knockedDownAt, cutPoint[best], 0.0);
            }

            // 誰も触れない＝内野内で失速して死ぬ緩いゴロ（打球は内野の中で止まっている）。
            // 最も近くを通った内野手が拾いに行く（内野安打寄り）。
            var bestZone = double.MaxValue;
            for (var k = 0; k < n; k++)
            {
                if (zoneDist[k] < bestZone) { bestZone = zoneDist[k]; best = k; }
            }
        }
        if (best < 0)
        {
            // 内野内をグラブの届く高さで通る点が皆無（想定外）→ 従来どおり着地点の最寄り内野手。
            var fallback = NearestInfielder(fielders, path.Landing);
            return (fallback, ReachTime(fallback, path.Landing), path.Landing, 0.0);
        }

        var chosen = infield[best];
        var reachToZone = ReachTime(chosen, zonePoint[best]);
        // 打球到達に間に合う＝待つ余裕がある。緩い打球はその余裕で前へ出る（issue #630）。
        if (reachToZone <= zoneBallTime[best])
        {
            var charge = ChargeCatch(
                chosen, path, zoneDist[best], zoneBallTime[best], zonePoint[best], coeff);
            return (chosen, charge.Time, charge.Point, charge.AdvanceM);
        }

        // 間に合わない＝追いかける。**1回の代入では解けない**: 「自分が最接近点へ着く時刻の打球位置」は
        // そのぶん先へ転がっているので、そこへはまだ届いていない。以前はここで1回だけ代入しており、
        // 野手が到底走れない距離を「走ったこと」にしていた（実測: マウンドの投手が 31m を 0.89秒＝
        // 35 m/s。処理時刻 FieldedAtSeconds はアウト/セーフ判定に入るので、守備に甘い側の誤差だった）。
        // ここへ来るのは緩いゴロだけ（強い打球は上の cutTime で内野内に決着している）ので、
        // 打球は内野の中で止まる＝この反復は単調増加で必ず収束する（実測の遅れは最大 0.023秒）。
        var time = reachToZone;
        for (var i = 0; i < CatchUpIterations; i++)
        {
            var next = ReachTime(chosen, path.PositionAt(time));
            if (next <= time + CatchUpToleranceSeconds) break;
            time = next;
        }
        return (chosen, time, path.PositionAt(time), 0.0);
    }

    /// <summary>
    /// 前進捕球（チャージ, issue #630）。待って捕るなら <paramref name="zoneBallTime"/> に
    /// <paramref name="zonePoint"/> で捕る打球について、<b>それより早く捕れる本塁寄りの点</b>を返す。
    ///
    /// <para>前へ出られる距離は「打球の緩さ」で決める:
    /// 上限 ×(1 − 待って捕るときの打球速度 / <see cref="FieldingCoefficients.ChargeSlowBallSpeedMps"/>)。
    /// <b>強い打球は上限0＝走査に入らず従来と完全に同じ捕球点・処理時刻を返す</b>（帯・絵とも不変）。
    /// しきいをまたぐ瞬間に前進量が飛ばないよう、比例で0へ落とす。</para>
    ///
    /// <para>「間に合うか」は処理野手の割り当て（<c>gloveTime</c>）と<b>同じ式</b>
    /// （<see cref="FielderReachSeconds"/>、半径0＝守備範囲の半径を足さない実距離）で見る。
    /// 乱数は引かない（幾何と時間だけ）。</para>
    /// </summary>
    private static (double Time, Vector3D Point, double AdvanceM) ChargeCatch(
        Fielder f, BouncePath path, double zoneDist, double zoneBallTime, Vector3D zonePoint,
        FieldingCoefficients coeff)
    {
        var slowLimit = coeff.ChargeSlowBallSpeedMps;
        if (slowLimit <= 0.0) return (zoneBallTime, zonePoint, 0.0);
        var slowness = MathUtil.Clamp(
            (slowLimit - path.HorizontalSpeedAt(zoneBallTime)) / slowLimit, 0.0, 1.0);
        var cap = slowness * MaxChargeAdvanceM(f.Position, coeff);
        if (cap <= 0.0) return (zoneBallTime, zonePoint, 0.0);

        var step = Math.Max(0.005, coeff.InfieldInterceptStepSeconds);
        for (var t = path.HangTimeSeconds; t < zoneBallTime; t += step)
        {
            if (path.HeightAt(t) > coeff.InfielderReachHeightM) continue;  // グラブが届かない高さ
            var p = path.PositionAt(t);
            var dist = (p - f.Location).Length;
            if (dist - zoneDist > cap) continue;                           // ここまでは出られない
            if (FielderReachSeconds(f, p, 0.0, path.HorizontalSpeedAt(t), coeff) > t) continue; // その時刻までに届かない
            return (t, p, dist - zoneDist);                                // 最も早い＝いちばん前で捕る点
        }
        return (zoneBallTime, zonePoint, 0.0);
    }

    /// <summary>前へ出られる距離の上限[m]。コーナー（投手・一塁手・三塁手）は一塁が近く思い切って
    /// 突っ込める／ミドル（二塁手・遊撃手）は送球が長く体勢を崩せない（issue #630）。</summary>
    private static double MaxChargeAdvanceM(FieldPosition position, FieldingCoefficients coeff)
        => position is FieldPosition.Pitcher or FieldPosition.FirstBase or FieldPosition.ThirdBase
            ? coeff.ChargeMaxAdvanceCornerM
            : coeff.ChargeMaxAdvanceMiddleM;


    /// <summary>追いかけ捕球の反復上限（打球が止まれば数回で収まる。無限ループの保険）。</summary>
    private const int CatchUpIterations = 24;

    /// <summary>追いかけ捕球の収束判定[s]。</summary>
    private const double CatchUpToleranceSeconds = 0.005;

    internal static bool IsInfield(Fielder f)
        => f.Position is FieldPosition.FirstBase or FieldPosition.SecondBase
            or FieldPosition.ThirdBase or FieldPosition.Shortstop or FieldPosition.Pitcher;

    /// <summary>
    /// 内野手1人分の横の守備範囲[m]（issue #1661）。守備(Fielding)50で <see cref="FieldingCoefficients.InfielderFieldingRadiusM"/>
    /// と恒等、守備が高いほど広がり低いほど狭まる（下限・上限つき）。<paramref name="f"/> の Attributes は
    /// 呼び出し元で既に <c>ToFielder(effect)</c> 済みの実効値であること（不変条件#1）。
    /// </summary>
    internal static double InfielderRadius(Fielder f, FieldingCoefficients coeff)
        => f.Attributes.InfielderFieldingRadiusM(
            coeff.InfielderFieldingRadiusM, coeff.InfielderFieldingRadiusSlopeMPerPoint,
            coeff.InfielderFieldingRadiusFloorM, coeff.InfielderFieldingRadiusCapM);

    /// <summary>
    /// 空中で捕れなかった打球（＝安打）の解決。着地後の転がり → 回収 → 各塁への送球 vs 打者走者。
    /// 乱数は外野手のファンブル判定（issue #811・<see cref="FieldingCoefficients.OutfieldFumbleBaseProb"/>>0 のときのみ）
    /// だけが消費する。進塁判断そのものは幾何と時間のみで決まる。
    /// </summary>
    private static FieldingPlay Hit(
        Func<BattedBallResult, FieldingPlay> baseOf,
        BallisticIntegrator.GroundResult ground,
        Vector3D landing,
        double fenceDistanceM,
        FieldGeometry field,
        BatterAttributes batter,
        IReadOnlyList<Fielder> fielders,
        FieldingCoefficients coeff,
        double runnerTime,
        IRandomSource rng)
    {
        var roll = ExtraBaseResolver.ComputeRoll(
            landing, ground.LandingVelocity, ground.HangTimeSeconds, fenceDistanceM, coeff, field);
        return HitFromRoll(baseOf, roll, field, batter, fielders, coeff, runnerTime, rng);
    }

    /// <summary>
    /// 転がり経路から回収・塁打数を決めて FieldingPlay を組む。外野手のファンブル（issue #811）を
    /// ここで一括判定する＝安打の回収経路（フライが落ちて安打になった球／内野を抜けたゴロ／
    /// トンネルで抜けた球）すべてに同じ規則が掛かる。
    /// </summary>
    private static FieldingPlay HitFromRoll(
        Func<BattedBallResult, FieldingPlay> baseOf,
        ExtraBaseResolver.RollPath roll,
        FieldGeometry field,
        BatterAttributes batter,
        IReadOnlyList<Fielder> fielders,
        FieldingCoefficients coeff,
        double runnerTime,
        IRandomSource rng)
    {
        var (retriever, retrieveAt) = ExtraBaseResolver.Retrieve(fielders, roll, coeff);

        // 外野手のファンブル（issue #811 / design-14 P1-6 追記）: 回収点でのボール速度（速いほど難度が
        // 上がる・止まった球ではほぼ発生しない）と外野手の Catching から判定する。既定0では一切ロールを
        // 引かない＝乱数消費順・結果とも完全に従来どおり（ThrowErrorBaseProb と同じ恒等の作法）。
        // 発生時は一律の追加進塁ではなく、遅れた回収時刻で ResolveBases を解き直す（不変条件#1）。
        var fumbled = false;
        var effectiveRetrieveAt = retrieveAt;
        if (coeff.OutfieldFumbleBaseProb > 0.0 && IsOutfield(retriever.Position))
        {
            var ballSpeed = roll.SpeedAt(retrieveAt);
            if (MaybeOutfieldFumble(retriever, ballSpeed, coeff, rng))
            {
                fumbled = true;
                effectiveRetrieveAt = retrieveAt + DrawOutfieldFumbleDelay(coeff, rng);
            }
        }

        var result = ExtraBaseResolver.ResolveBases(
            roll, retriever, effectiveRetrieveAt, field, batter, runnerTime, coeff);
        var fieldedPoint = roll.PositionAt(effectiveRetrieveAt);
        return baseOf(result) with
        {
            FieldedX = fieldedPoint.X,
            FieldedZ = fieldedPoint.Z,
            // 絵のための観測（issue fence-carom-and-over-the-head-cues）。既に決まった値の写しと
            // その純関数変換だけ＝乱数を1発も引かず、判定にも使わない。
            ReachedFence = roll.ReachedFence,
            // ⚠ `ReachedFence` は「**放っておけば**フェンスまで転がる」＝野手が先に追いつけば球は
            // フェンスに当たらない（実測: 単打の3分の1が ReachedFence だが、その多くは外野手が
            // 手前で回収している）。跳ね返りを描くのは**実際に当たったプレーだけ**なので、
            // 回収時刻が衝突時刻より後のときに限って観測を載せる。
            // ファンブルで遅らせた時刻ではなく、実際に球へ届いた時刻（retrieveAt）で見る＝壁に当たる前に
            // 触った球は、弾いたあとに遅れても跳ね返りの絵にならない（issue #1243）。
            Carom = ExtraBaseResolver.ComputeFenceCarom(roll, coeff) is { } k && retrieveAt > k.HitAtSeconds + 1e-9
                ? k
                : null,
            FielderRole = retriever.Position,
            FieldedAtSeconds = effectiveRetrieveAt,
            FielderThrowSpeedMps = retriever.Attributes.ThrowSpeedMps,
            FielderFielding = retriever.Attributes.Fielding,
            FielderThrowAccuracy = retriever.Attributes.ThrowAccuracy,
            // 安打＋失策（issue #811）: Result はそのまま安打（Single/Double/Triple）のまま変えず、
            // 失策だけ外野手に付ける。ErrorAtSeconds は弾いた瞬間（＝遅延前の回収時刻）の写し。
            ErrorKind = fumbled ? FieldingErrorKind.Fumble : null,
            ErrorFielderRole = fumbled ? retriever.Position : null,
            ErrorAtSeconds = fumbled ? retrieveAt : null,
        };
    }

    private static bool IsOutfield(FieldPosition pos)
        => pos is FieldPosition.LeftField or FieldPosition.CenterField or FieldPosition.RightField;

    /// <summary>
    /// 外野手のファンブルの確率（issue #811）。回収点でのボール速度から難度を出し（<see cref="FieldingCoefficients.OutfieldFumbleSpeedRefMps"/>
    /// 以上で難度1・速度0で難度0）、外野手の Catching から専用の非対称スロープ（OutfieldFumbleCatchingSlope*）で
    /// 伸縮させる。FlyCatchErrorProb（issue #532）と同じ「難度×基準確率」の形。
    /// </summary>
    private static double OutfieldFumbleProb(Fielder retriever, double ballSpeedMps, FieldingCoefficients coeff)
    {
        var difficulty = MathUtil.Clamp(ballSpeedMps / Math.Max(1e-6, coeff.OutfieldFumbleSpeedRefMps), 0.0, 1.0);
        var catchingDelta = retriever.Attributes.Catching - 50;
        var slope = catchingDelta < 0 ? coeff.OutfieldFumbleCatchingSlope : coeff.OutfieldFumbleCatchingSlopeStrong;
        // 捕球の効きは基準確率に掛ける倍率（足し算だと基準と無関係な上乗せになる）。内野の error_base_prob には連動しない。
        var catchingFactor = Math.Max(0.0, 1.0 - catchingDelta * slope);
        var errProb = coeff.OutfieldFumbleBaseProb * catchingFactor * difficulty;
        return MathUtil.Clamp(errProb, 0.0, coeff.ErrorMaxProb);
    }

    /// <summary>外野手のファンブル判定（rng を1回消費）。</summary>
    private static bool MaybeOutfieldFumble(Fielder retriever, double ballSpeedMps, FieldingCoefficients coeff, IRandomSource rng)
        => MathUtil.Chance(OutfieldFumbleProb(retriever, ballSpeedMps, coeff), rng);

    /// <summary>
    /// 外野手のファンブルで球が余分に逸れる時間[s]（issue #811）。指数分布の逆関数法（下限0・右に裾）＝
    /// たいていは小さく弾くだけ、たまに大きく後逸する。内野のもたつき（<see cref="DrawHandleJitter"/>）と同じ形。
    /// 平均が0でも必ず1つ rng を引く（不変条件#2）。
    /// </summary>
    private static double DrawOutfieldFumbleDelay(FieldingCoefficients coeff, IRandomSource rng)
    {
        var u = rng.NextDouble();
        if (coeff.OutfieldFumbleDelayMeanSeconds <= 0.0) return 0.0;
        var delay = -coeff.OutfieldFumbleDelayMeanSeconds * Math.Log(1.0 - u);
        var cap = coeff.OutfieldFumbleDelayMeanSeconds * coeff.OutfieldFumbleDelayMaxFactor;
        return Math.Min(delay, cap);
    }

    /// <summary>
    /// 空中捕球で走れる時間の上限[s]。滞空時間比例（＝深い飛球ほど守備範囲が広がる）に
    /// 絶対上限を掛け、走路取りの巧拙（守備力）で伸縮させる。守備50・上限未満では従来と同一。
    /// </summary>
    private static double CatchTimeBudget(double hangTime, Fielder fielder, FieldingCoefficients coeff)
    {
        // 守備の巧さは上乗せだけ（issue #740）。守備50未満で予算を滞空時間より短くすると、反応＋脚力で走る絵では
        // 落下点に先着しているのに安打になる。下手さは反応遅延（ReactionDelaySeconds）で判定にも絵にも既に出ている。
        var routeFactor = Math.Max(1.0, 1.0 + (fielder.Attributes.Fielding - 50) * coeff.CatchReachFieldingSlope);
        return Math.Min(hangTime * coeff.CatchReachFactor, coeff.CatchReachCapSeconds) * routeFactor;
    }

    /// <summary>捕球エラー（Catching）ロール。実際に捕った野手 <paramref name="f"/> の Catching で引く（issue #985）。rng を1回消費（守備力連動, issue #123）。
    /// extraProb はイレギュラーバウンド等の失策確率への加算（Issue #63・既定0で恒等）。</summary>
    private static bool CatchErrorOccurs(
        Fielder f, FieldingCoefficients coeff, IRandomSource rng, double extraProb = 0.0)
    {
        // 守備力連動は非対称: 平均(50)より下（弱小）は急傾斜で大量失策へ、上（精鋭）は緩傾斜で
        // 「守備を上げるほど失策が減る」勾配を残す（下限へ潰さない, issue #123）。
        var catchingDelta = f.Attributes.Catching - 50;
        var slope = catchingDelta < 0 ? coeff.ErrorCatchingSlope : coeff.ErrorCatchingSlopeStrong;
        // 守備（手さばき）の倍率（issue #1685）: 下手な内野手ほど握り損ね・お手玉が増える。守備50で×1.0。
        // 以前はもたつき（指数分布）の守備伸縮とお手玉のしきい値がこの差を作っていた。
        var handling = f.Attributes.TransferFactor(coeff.CatchErrorFieldingSlope, coeff.CatchErrorFieldingFactorMin);
        var errProb = (coeff.ErrorBaseProb - catchingDelta * slope) * handling + extraProb;
        return MathUtil.Chance(MathUtil.Clamp(errProb, coeff.ErrorMinProb, coeff.ErrorMaxProb), rng);
    }

    /// <summary>
    /// フライ落球の失策確率（issue #532）。捕球余裕 <paramref name="catchMargin"/>[s] から難度を出し、
    /// フライ専用の基準確率 <see cref="FieldingCoefficients.FlyErrorBaseProb"/> を伸縮させる。
    /// 難度 = clamp(1 − catchMargin/RoutineMargin, 0, 1)＝余裕が大きい平凡な飛球ほど 0（落球確率が下限へ）、
    /// 余裕≈0 の際どい飛球で 1（基準確率フル）。守備力連動は内野ゴロと同じ非対称スロープ（弱小側は急・精鋭側は緩）を
    /// 難度と同時に掛け、平凡な飛球はどの守備力でもほぼ落とさない一方、際どい飛球で守備力差が出るようにする。
    /// </summary>
    private static double FlyCatchErrorProb(Fielder catcher, double catchMargin, FieldingCoefficients coeff)
    {
        var routine = Math.Max(1e-9, coeff.FlyErrorRoutineMarginSeconds);
        var difficulty = MathUtil.Clamp(1.0 - catchMargin / routine, 0.0, 1.0);
        var catchingDelta = catcher.Attributes.Catching - 50;
        var slope = catchingDelta < 0 ? coeff.ErrorCatchingSlope : coeff.ErrorCatchingSlopeStrong;
        var errProb = (coeff.FlyErrorBaseProb - catchingDelta * slope) * difficulty;
        return MathUtil.Clamp(errProb, coeff.ErrorMinProb, coeff.ErrorMaxProb);
    }

    /// <summary>フライ落球の判定（rng を1回消費＝従来のフライ捕球エラー判定と乱数消費順は同一, issue #532）。
    /// 落球確率だけが難度・実捕球者連動へ変わる（結果は変わる＝決定論 baseline 再生成を伴う。不変条件#2 は保たれる）。</summary>
    private static BattedBallResult MaybeFlyError(
        Fielder catcher, double catchMargin, FieldingCoefficients coeff, IRandomSource rng)
        => MathUtil.Chance(FlyCatchErrorProb(catcher, catchMargin, coeff), rng)
            ? BattedBallResult.Error : BattedBallResult.Out;

    /// <summary>
    /// 内野ゴロ→一塁送球のエラー判定（Issue #37, design-14 P1-6b）。捕球ロール(Catching)→送球ロール(ThrowAccuracy)→
    /// 受け手の捕球ロール(Catching, issue #810)の3段階。送球ロールは <see cref="FieldingCoefficients.ThrowErrorBaseProb"/>、
    /// 受け手ロールは <see cref="FieldingCoefficients.ReceiveErrorBaseProb"/> が &gt; 0 のときのみ引く＝既定0では
    /// 両方スキップ（現行と乱数消費順・結果とも完全一致, 決定論不変）。extraProb はイレギュラーバウンドを
    /// 捕球ロールへ加算する（Issue #63・既定0で恒等）。
    /// </summary>
    private static (BattedBallResult Result, FieldingErrorKind? ErrorKind) MaybeInfieldError(
        IReadOnlyList<Fielder> fielders, Fielder infielder,
        FieldingCoefficients coeff, IRandomSource rng, double extraProb = 0.0, int? throwAccuracy = null,
        ThrowScatterModel.Scatter? throwScatter = null, bool unassistedPutout = false)
    {
        if (CatchErrorOccurs(infielder, coeff, rng, extraProb))
        {
            // 捕球エラーの内訳（issue #632）: 一定割合は股・脇を抜けて外野へ行く＝トンネル。
            // 失策の総数は変えない（お手玉が減ってトンネルが増える）＝#123/#532 の失策率校正を保つ。
            // TunnelShareProb=0 のときはロール自体を引かない＝乱数消費順・結果とも完全に従来どおり
            // （ThrowErrorBaseProb と同じ恒等の作法）。
            if (coeff.TunnelShareProb > 0.0 && MathUtil.Chance(coeff.TunnelShareProb, rng))
                return (BattedBallResult.Error, FieldingErrorKind.Tunnel);
            return (BattedBallResult.Error, FieldingErrorKind.Catch);
        }
        // アンアシスト刺殺（3U）は送球が無い＝悪送球も受け手の捕球ミスも起こらない（issue #1260）。
        if (unassistedPutout) return (BattedBallResult.Out, null);
        if (coeff.ThrowErrorBaseProb > 0.0)
        {
            // 送球精度が高いほど悪送球が減る。クランプは捕球エラーと共通の min/max を流用。
            // throwAccuracy は前進捕球の低下を織り込んだ実効精度（issue #630。null＝素の精度）。
            var accuracy = throwAccuracy ?? infielder.Attributes.ThrowAccuracy;
            var errProb = coeff.ThrowErrorBaseProb - (accuracy - 50) * coeff.ThrowErrorAccuracySlope;
            var clampedProb = MathUtil.Clamp(errProb, coeff.ErrorMinProb, coeff.ErrorMaxProb);
            // 送球の逸れ（issue #858）: 従来の二値ロール（MathUtil.Chance(clampedProb)）を、2次元の逸れを
            // サンプルして閾値と比べる形へ統合する。閾値は ThrowScatterModel.WildThresholdCmForProbability で
            // clampedProb から逆算＝確率カーブは完全に不変（rng消費だけが1→2系統に増える。決定論baseline再生成）。
            var sigmaCm = ThrowScatterModel.SigmaCmForAccuracy(accuracy);
            var thresholdCm = ThrowScatterModel.WildThresholdCmForProbability(sigmaCm, clampedProb);
            // 逸れは呼び出し側で引き済み（issue #859: 送球の秒にも絵にも同じ1本を使う）。無ければここで引く。
            if (ThrowScatterModel.IsWild(throwScatter ?? ThrowScatterModel.Sample(sigmaCm, rng), thresholdCm))
            {
                return (BattedBallResult.Error, FieldingErrorKind.Throw);
            }
        }
        // 受け手の捕球ミス（issue #810）: 送球自体は正確（上の2段を通過済み）でも、受け手が
        // 正確な送球を落とす／ショートバウンドを捕れない失策を三段目として引く。守備力連動は捕球エラーと
        // 同じ非対称スロープ（弱小側 ErrorCatchingSlope・精鋭側 ErrorCatchingSlopeStrong）を受け手自身の
        // Catching へ適用する。受け手は通常は一塁手、一塁手が処理して投手のカバーへ投げる場合は投手（issue #1260）。
        if (coeff.ReceiveErrorBaseProb > 0.0)
        {
            var receiver = FirstBaseReceiver(fielders, infielder);
            var catchingDelta = receiver.Attributes.Catching - 50;
            var slope = catchingDelta < 0 ? coeff.ErrorCatchingSlope : coeff.ErrorCatchingSlopeStrong;
            var errProb = coeff.ReceiveErrorBaseProb - catchingDelta * slope;
            if (MathUtil.Chance(MathUtil.Clamp(errProb, coeff.ErrorMinProb, coeff.ErrorMaxProb), rng))
            {
                return (BattedBallResult.Error, FieldingErrorKind.Receive);
            }
        }
        return (BattedBallResult.Out, null);
    }

    /// <summary>一塁送球の受け手。一塁手が処理した打球は投手が一塁カバーに入って受ける（TimelineBuilder の firstBaseCoverer と同じ）。</summary>
    private static Fielder FirstBaseReceiver(IReadOnlyList<Fielder> fielders, Fielder infielder)
        => fielders.First(f => f.Position == (infielder.Position == FieldPosition.FirstBase
            ? FieldPosition.Pitcher : FieldPosition.FirstBase));

    /// <summary>本塁→一塁の実戦タイム（設計書02 §4.1b）: 天井速度での距離÷速度＋実戦オフセット−左打者短縮。</summary>
    public static double BatterToFirstSeconds(FieldGeometry field, BatterAttributes batter, FieldingCoefficients coeff)
    {
        var handednessBonus = batter.Bats.IsLeftSide() ? coeff.LeftBatterFirstStepBonusSeconds : 0.0;
        return field.BaseDistanceM / batter.SpeedToFirstMps() + coeff.RunnerReactionSeconds - handednessBonus;
    }

    /// <summary>
    /// ファウルフライの捕球（issue #830）。打球を積分して落下点を求め、ファウルグラウンド（スタンドより内側）に
    /// 落ちる飛球へ捕手を含む全野手のうち最も早く届く者が間に合えばアウトの <see cref="FieldingPlay"/> を返す。
    /// 届かない・スタンドに入る・低い打球は null（＝ただのファウル）。乱数は使わない（落球は扱わない）。
    /// </summary>
    public static FieldingPlay? ResolveFoulFly(
        BattedBall ball, FieldGeometry field, Aerodynamics aero,
        IReadOnlyList<Fielder> fielders, FieldingCoefficients coeff, double runnerTime)
    {
        var ground = BallisticIntegrator.IntegrateToGround(
            ball.ContactPoint(), ball.InitialVelocity(), ball.SpinVector(), aero, wind: field.Wind);
        if (ground.ApexHeightM < coeff.FlyApexThresholdM) return null;
        var landing = ground.LandingPosition;
        if (!field.InFoulTerritory(landing)) return null;

        if (fielders.Count == 0) return null;
        var (best, bestReach, budget) = BestAirCatcher(fielders, landing, ground.HangTimeSeconds, 0.0, coeff);
        if (bestReach > budget) return null;

        var range = Math.Sqrt(landing.X * landing.X + landing.Z * landing.Z);
        return new FieldingPlay
        {
            Result = BattedBallResult.Out,
            LandingX = landing.X,
            LandingZ = landing.Z,
            HangTimeSeconds = ground.HangTimeSeconds,
            ApexHeightM = ground.ApexHeightM,
            LaunchHeightM = ball.ContactHeightM,
            RangeM = range,
            BearingDeg = ball.BearingDeg,
            BatterToFirstSeconds = runnerTime,
            Class = BattedBallClass.Fly,
            IsFly = true,
            FoulFly = true,
            FielderRole = best.Position,
            FieldedAtSeconds = ground.HangTimeSeconds,
            FielderThrowSpeedMps = best.Attributes.ThrowSpeedMps,
            FielderFielding = best.Attributes.Fielding,
            FielderThrowAccuracy = best.Attributes.ThrowAccuracy,
            CatchMarginSeconds = budget - bestReach,
        };
    }

    /// <summary>
    /// 打ち上がったファウルの打球（issue #830）。接触判定の「ファウル」は弾道を持たないので、
    /// <see cref="FieldingCoefficients.FoulFlyShare"/> の割合でこの打球を与える。<paramref name="rng"/> は
    /// 打席の本流から Fork した専用ストリーム（本流の乱数順を変えない）。打ち上がらない回は null。
    /// </summary>
    public static BattedBall? FoulFlyBall(FieldingCoefficients coeff, IRandomSource rng)
    {
        if (!MathUtil.Chance(coeff.FoulFlyShare, rng)) return null;
        var la = MathUtil.Clamp(rng.NextGaussian(coeff.FoulFlyLaunchMeanDeg, coeff.FoulFlyLaunchSigmaDeg), 25.0, 85.0);
        var kmh = Math.Max(40.0, rng.NextGaussian(coeff.FoulFlyExitMeanKmh, coeff.FoulFlyExitSigmaKmh));
        var abs = coeff.FoulFlyBearingMinDeg + rng.NextDouble() * (coeff.FoulFlyBearingMaxDeg - coeff.FoulFlyBearingMinDeg);
        var sign = rng.NextDouble() < 0.5 ? -1.0 : 1.0;
        return new BattedBall { ExitVelocityMps = kmh / 3.6, LaunchAngleDeg = la, BearingDeg = sign * abs };
    }

    private static double ReachTime(Fielder f, Vector3D target)
    {
        var dist = (target - f.Location).Length;
        return dist / f.Attributes.SprintSpeedMps + f.Attributes.ReactionDelaySeconds;
    }

    /// <summary>
    /// 通り道のライナー捕球（issue #1135）の捕球者・時刻・点・その到達所要[s]・捕球予算[s]。
    /// <see cref="BudgetSeconds"/> − <see cref="ReachSeconds"/> が着地点基準の捕球余裕
    /// （<c>catchMargin</c>）と同じ意味＝<b>標本時刻そのもの（滞空時間）ではない</b>
    /// （実害: 一度 <c>TimeSeconds − ReachSeconds</c> を margin として使い、際どい捕球が
    /// すべて「余裕たっぷり」に化けるバグを作った）。
    /// </summary>
    private readonly record struct LineDriveCatch(
        Fielder Fielder, double TimeSeconds, Vector3D Point, double ReachSeconds, double BudgetSeconds);

    /// <summary>
    /// 打球の飛行経路を時間で走査し、着地点を待たずに野手が正面でノーバウンド捕球できる最初の
    /// (時刻・野手)を返す（issue #1135）。到達可否は<b>着地点の空中捕球判定（<see cref="CatchTimeBudget"/>＋
    /// <see cref="FieldingCoefficients.WallCatchPenalty"/>）と同じ式</b>を経路上の各標本時刻へ適用する＝素の
    /// 「距離÷脚力＋反応遅延 ≤ 標本時刻」ではなく「同じ ≤ CatchTimeBudget(標本時刻,…) − フェンス際の減算」で見る。
    /// 素の式のままだと着地点判定より緩く（<see cref="FieldingCoefficients.CatchReachFactor"/> の間合い・走路の
    /// 巧拙・上限秒・フェンス際ペナルティを一切効かせない）なり、着地点では正しく安打／フェンス際の際どい落球と
    /// 判定される打球まで経路の途中で捕れてしまう（実害: 標準守備位置の正面へ強く抜けていく打球／フェンス際で
    /// あと一歩届かないシナリオのテスト群が全滅した。ウォールペナルティ抜けが原因）。標本時刻＝その点までの
    /// 滞空時間とみなして同じ式へ通すことで、ちょうど着地点（標本時刻=全体の滞空時間）に一致したとき既存の
    /// 判定と完全に同じ基準になる。<paramref name="wallPenaltySeconds"/> は着地点1つから決めた値をそのまま渡す
    /// （標本ごとに引き直すと、柵ゾーンの外縁を通る標本だけペナルティが消えてしまう＝この打球は柵の近くへ
    /// 向かっているか否かという1つの性質であるべき）。投手はフォロースルー直後の追加反応遅延
    /// （<see cref="FieldingCoefficients.PitcherFieldingReactionPenaltySeconds"/>）を他の判定と同様に負う。
    /// 乱数は消費しない（決定論）。見つからなければ null。
    /// </summary>
    private static LineDriveCatch? TryLineDriveCatch(
        IReadOnlyList<Fielder> fielders,
        IReadOnlyList<BallisticIntegrator.FlightSample> samples,
        double hangTimeSeconds,
        double apexHeightM,
        double wallPenaltySeconds,
        FieldingCoefficients coeff)
    {
        // 飛球（頂点 ≥ FlyApexThresholdM）は**上昇区間だけ**を走査する。頂点を過ぎて落ちてくる区間で
        // グラブの届く高さを通る点は着地点のすぐ手前＝着地点の空中捕球判定が既に担っている（その「着地の
        // 手前で高く捕れる分」は CatchReachFactor 等で校正済み）。ここでも拾うと同じ分を二重に数え、
        // 背走して頭の高さで捕る打球まで先取りして打率が帯（.235〜）を .230 へ割った。低いライナーは全区間。
        var flyOnlyAscending = apexHeightM >= coeff.FlyApexThresholdM;
        var prevY = double.NegativeInfinity;
        foreach (var s in samples)
        {
            if (flyOnlyAscending && s.Position.Y < prevY) break;   // 頂点を過ぎた
            prevY = s.Position.Y;
            // t=0（接触点そのもの）と最終着地点は既存の判定（本塁打／着地点基準の空中捕球／ゴロ経路）に任せる。
            if (s.TimeSeconds <= 0.0 || s.TimeSeconds >= hangTimeSeconds - 1e-9) continue;
            if (s.Position.Y > coeff.LineDriveReachHeightM) continue; // グラブの届く高さを越える

            Fielder? best = null;
            var bestReach = double.PositiveInfinity;
            var bestBudget = 0.0;
            foreach (var f in fielders)
            {
                // 地上に立つ野手の移動は水平距離だけで決まる（グラブを高さ方向へ伸ばす分は走らない）。
                // 3D距離（.Length）で引くと、球の高さぶんまで「走って詰める」距離に混ざり込み、
                // 正面に立っているだけの野手まで届かなくなる（実害: 標準守備位置の正面を通る打球が
                // 捕れないままだった）。<see cref="FieldingCoefficients.LineDriveReachHeightM"/> の
                // 高さ制限が「グラブで届くか」を既に担っているので、ここは水平距離のみで良い。
                var dx = s.Position.X - f.Location.X;
                var dz = s.Position.Z - f.Location.Z;
                var dist = Math.Sqrt(dx * dx + dz * dz);
                var reach = dist / f.Attributes.SprintSpeedMps + f.Attributes.ReactionDelaySeconds;
                if (f.Position == FieldPosition.Pitcher) reach += coeff.PitcherFieldingReactionPenaltySeconds;
                var budget = CatchTimeBudget(s.TimeSeconds, f, coeff) - wallPenaltySeconds;
                if (reach <= budget && reach < bestReach)
                {
                    bestReach = reach;
                    bestBudget = budget;
                    best = f;
                }
            }
            if (best is not null) return new LineDriveCatch(best, s.TimeSeconds, s.Position, bestReach, bestBudget);
        }
        return null;
    }

    /// <summary>
    /// 捕球時刻までに打球が達した最高到達点[m]（issue #1135。タイムライン／3D描画が「捕球時刻までの弧」を
    /// 元の滞空時間ではなく捕球時刻を基準に再構成するため、頂点も同じ基準で切り詰める。元の
    /// ApexHeightM（全飛行の頂点）をそのまま使うと、短く切り詰めた経路の中点へ過大な頂点を押し込み、
    /// 平たいライナーが途中で急上昇する絵になる）。
    /// </summary>
    private static double TruncatedApexHeightM(
        IReadOnlyList<BallisticIntegrator.FlightSample> samples, double catchTimeSeconds, double launchHeightM)
    {
        var apex = launchHeightM;
        foreach (var s in samples)
        {
            if (s.TimeSeconds > catchTimeSeconds) break;
            if (s.Position.Y > apex) apex = s.Position.Y;
        }
        return apex;
    }

    /// <summary>
    /// 着地点の空中捕球の捕り手（飛球・ファウルフライ共通）。捕球予算（<see cref="CatchTimeBudget"/>−<paramref name="wallPenaltySeconds"/>）
    /// の内側に届く野手のうち到達が最も早い者。誰も届かなければ余裕（予算−到達）が最大の者（余裕の符号が表示とフライ落球の難度に使われる）。
    /// 予算は守備力で野手ごとに違うので、最速の1人だけを見ると、その人が予算外のとき予算内に届く別の野手を見落とす（issue #1243）。
    /// 捕手も飛球を捕る（issue #830）。投手はフォロースルー直後の追加反応遅延を負う。乱数不使用。
    /// </summary>
    private static (Fielder Fielder, double Reach, double Budget) BestAirCatcher(
        IReadOnlyList<Fielder> fielders, Vector3D target, double hangTimeSeconds, double wallPenaltySeconds,
        FieldingCoefficients coeff)
    {
        var best = fielders[0];
        var bestReach = double.MaxValue;
        var bestBudget = 0.0;
        var bestMargin = double.NegativeInfinity;
        var found = false;
        foreach (var f in fielders)
        {
            var reach = ReachTime(f, target)
                        + (f.Position == FieldPosition.Pitcher ? coeff.PitcherFieldingReactionPenaltySeconds : 0.0);
            var budget = CatchTimeBudget(hangTimeSeconds, f, coeff) - wallPenaltySeconds;
            var inBudget = reach <= budget;
            if (inBudget)
            {
                if (!found || reach < bestReach)
                {
                    found = true; best = f; bestReach = reach; bestBudget = budget; bestMargin = budget - reach;
                }
            }
            else if (!found && budget - reach > bestMargin)
            {
                best = f; bestReach = reach; bestBudget = budget; bestMargin = budget - reach;
            }
        }
        return (best, bestReach, bestBudget);
    }

    private static Fielder NearestFielder(IReadOnlyList<Fielder> fielders, Vector3D target)
    {
        Fielder best = fielders[0];
        var bestD = double.MaxValue;
        foreach (var f in fielders)
        {
            var d = (target - f.Location).LengthSquared;
            if (d < bestD) { bestD = d; best = f; }
        }
        return best;
    }

    private static Fielder NearestInfielder(IReadOnlyList<Fielder> fielders, Vector3D target)
    {
        Fielder? best = null;
        var bestD = double.MaxValue;
        foreach (var f in fielders)
        {
            if (f.Position is not (FieldPosition.FirstBase or FieldPosition.SecondBase
                or FieldPosition.ThirdBase or FieldPosition.Shortstop or FieldPosition.Pitcher))
            {
                continue;
            }
            var d = (target - f.Location).LengthSquared;
            if (d < bestD) { bestD = d; best = f; }
        }
        return best ?? fielders[0];
    }
}
