using System.Collections.Generic;

namespace KokoSim.Engine.Players;

/// <summary>投法（元祖踏襲の3種）。データの箱のみ保持し、補正ロジックは現フェーズ実装しない（設計書02 §2.2 / CHANGELOG 31b）。</summary>
public enum ThrowingStyle
{
    Overhand,
    Sidearm,
    Underhand,
}

/// <summary>
/// 投手の表示層能力。球速のみ km/h 直接表示（設計書02 §1.1）。
/// スタミナは等級でなく「目安投球数」で直接管理（§1.1e）。球種はストレート含む全てが2軸ランク（§2.2）。
/// </summary>
public sealed record PitcherAttributes
{
    /// <summary>最高球速[km/h]（範囲110〜165）。カタログ上限であり、試合中は毎球これ以下をサンプリング（§1.1d）。</summary>
    public double MaxVelocityKmh { get; init; } = 132.0;

    /// <summary>
    /// 球速の反応オフセット[km/h]（設計書21）。打者の反応（球威・タイミング・打球質・バント難度）に使う球速は
    /// 実球速＋これ。表示→物理の効き <see cref="AbilityEffectCoefficients.Pitcher"/> が
    /// <c>PitchingKmh(MaxVelocityKmh) − MaxVelocityKmh</c> を焼く（速い投手ほど負・遅い投手ほど正・Lv50=132km/h で0）。
    /// 差分で持つので、その後の消耗・調子・ギア・毎球のドロップ（いずれも実測 km/h の単位）は寝かせずそのまま効く。
    /// 育成レイヤの投手（<see cref="Player.Pitching"/>）では常に0＝保存もしない。
    /// </summary>
    public double ReactionVelocityOffsetKmh { get; init; } = 0.0;

    /// <summary>コントロール: 狙いからの散布σ（1〜100）。</summary>
    public int Control { get; init; } = AbilityScale.AverageDisplay;

    /// <summary>
    /// 疲れ・重圧などで下がる前の本来の制球（issue #1589）。null＝<see cref="Control"/> と同じ。
    /// 試合中の消耗（<c>PitchingFatigue.Effective</c>）が一時的に下げる前の値を残し、
    /// 「制球の低い投手は四球で崩れる」非対称項（散布の上乗せ・狙いの下側の傾き）はこの値で判定する
    /// ＝ばてた好投手が急に四球を連発しない。
    /// </summary>
    public int? NativeControl { get; init; }

    /// <summary>本来の制球（<see cref="NativeControl"/> が無ければ <see cref="Control"/>）。</summary>
    public int BaseControl => NativeControl ?? Control;

    /// <summary>
    /// スタミナ＝目安投球数（§1.1e）。本来の力を保てる球数。他能力と独立に生成・管理。
    /// 内部の消耗カーブ（残量減→球速天井・制球低下）は PitchingFatigue が駆動する。
    /// 既定は「各50＝D帯」の目安投球数（<see cref="StaminaPitchesFromLevel"/>(50)＝100.5球, issue #408）。
    /// </summary>
    public double StaminaPitches { get; init; } = 100.5;

    /// <summary>
    /// 球種総合の素質（1〜100, 育成レイヤ）。レパートリー各球のランクの土台。
    /// 表示は球種ごとのランク（Repertoire）で行い、本値は成長・投影の中間表現。
    /// </summary>
    public int PitchRank { get; init; } = AbilityScale.AverageDisplay;

    /// <summary>
    /// 保有球種（設計書02 §2.2）。全投手ストレート必修。野手はデフォルト0〜1変化球（＝ストレート＋最大1）。
    /// 未指定時はストレートのみ（ランク=PitchRank）。
    /// </summary>
    public IReadOnlyList<PitchSlot>? Repertoire { get; init; }

    public ThrowingStyle Style { get; init; } = ThrowingStyle.Overhand;

    /// <summary>
    /// 球質タイプ（本格派/技巧派/軟投派/バランス）。生成時に確定した型をそのまま保持する純データ。
    /// null＝型を持たずに作られた投手（育成選手からの投影など）。表示側は null なら能力から推定する。
    /// 試合結果には影響しない（型の効果は既に球速・制球・キレのレベルへ焼き込み済み）。
    /// </summary>
    public PitcherArchetype? Archetype { get; init; }

    /// <summary>実効レパートリー（未指定ならストレートのみ）。</summary>
    public IReadOnlyList<PitchSlot> EffectiveRepertoire
        => Repertoire is { Count: > 0 } r ? r : new[] { PitchSlot.FastballOf(PitchRank) };

    // --- スタミナLevel → 目安投球数（表示層→物理層の一箇所集約・不変条件#1 / issue #408） ---
    // 旧式 45.0 + Lv*0.9 は生成分布（Clamp(N(45,12),10,92)）の下限が Lv10→54球・約7.6%が70球未満で、
    // #356 の上限撤廃後もオーナー要望「低くても70〜80球・平均95〜100球」を満たしていなかった。
    // 下の式は Lv10→70.5 / Lv45→96.75（生成平均）/ Lv50→100.5（D帯）/ Lv92→132（完投上限域）。
    // 傾きを 0.9→0.75 に寝かせて下限を持ち上げつつ上限が高校野球の完投域(120〜140)を外れないよう解いた。
    // 定数は data/coefficients.yaml roster.stamina_pitches_intercept / _per_level と一致（issue #402/#408）。
    // <see cref="Season.RosterCoefficients.StaminaPitchesIntercept"/> が既定として同じ定数を参照し、
    // 一致テスト（CoefficientsDefaultsConsistencyTests）が YAML との乖離を固定する。
    public const double StaminaPitchesIntercept = 63.0;
    public const double StaminaPitchesPerLevel = 0.75;

    /// <summary>
    /// 育成レイヤのスタミナLevel（<b>表示Lv</b>）→目安投球数の変換（表示層→内部の一箇所集約）。
    /// 表示Lvは今の物差しより Δ 低い（issue #1080・<see cref="AbilityScale"/>）ので、+Δ して今の式に通す＝目安投球数は Δ によらず不変。
    /// </summary>
    public static double StaminaPitchesFromLevel(int level) => StaminaPitchesFromPhysicalLevel(AbilityScale.Phys(level));

    /// <summary>今の物差しのスタミナLv（物理Lv）→目安投球数。生成の内部（表示へ下げる前）だけが使う。</summary>
    public static double StaminaPitchesFromPhysicalLevel(int level)
        => StaminaPitchesIntercept + level * StaminaPitchesPerLevel;

    /// <summary>
    /// 目安投球数→スタミナLevel の逆変換（投手総合の合成で他能力と土俵を揃える用）。
    /// 順変換と同じ定数から出す（別々に持つと必ずずれる, issue #408）。
    /// スタミナは上限なし（issue #356）＝上側をクランプしない。目安投球数の大きい投手は
    /// Lv100で頭打ちにならず素直に外挿する（Lv100=138球 / 逆変換 (138-63)/0.75=100）。下限のみ1で丸める。
    /// </summary>
    public static int LevelFromStaminaPitches(double pitches) => AbilityScale.Disp(PhysicalLevelFromStaminaPitches(pitches));

    /// <summary>目安投球数→今の物差しのスタミナLv（物理Lv）。生成・成長の内部（物理Lvの空間）だけが使う。</summary>
    public static int PhysicalLevelFromStaminaPitches(double pitches)
        => (int)Math.Round(Math.Max(1.0, (pitches - StaminaPitchesIntercept) / StaminaPitchesPerLevel));

    // --- 球速Level → 最高球速[km/h]（表示層→物理層の一箇所集約・不変条件#1） ---
    // 旧式 120 + Lv*0.45 は Lv50→142.5km/h と高校野球として速すぎ、かつ Lv80以上が上限155に張り付いて
    // 最上位帯の差が消えていた（本格派が際立たない）。108.5 + 0.47×Lv は LeagueAverage の記述（Lv50=132km/h）と一致する。
    //
    // Lv50 を境に上位を寝かせる区分線形（issue velocity-kmh-mapping-flatten-top, 2026-08-18・案A）:
    // 一本の傾き 0.47 では C校のエースに 140 後半が複数居り、A/S は Lv99 のクランプで全員 155 に張り付いていた
    // （実測 seed2026・600校: エース最速 p50 C 142／B 148／A 154／S 155＝現実の高校野球より B以上で +5〜10、C で +3〜5）。
    // Lv>50 は 132 + 0.36×(Lv−50) ＝ Lv60→135.6 / 70→139.2 / 80→142.8 / 90→146.4 / 99→149.6。
    // 同じ Lv 分布で C 140／B 144／A 148／S 149.6 に来る（現実の目安: 甲子園常連 A/S 140〜148・150+ は年に数人）。
    // Lv≦50 は従来どおり（E以下の一般母集団は妥当だった）。逆変換は同じ区分の逆関数（往復一致をテストで固定）。
    // 球速の物理効果（stuff_per_kmh 等）は km/h で効くので、上位では「Lv1 の価値」が 0.47→0.36 km/h ぶんに下がる。
    //
    // Lv90 から上を急にする（issue #908・2026-11-14）: 現実の高校生は最速150km/h以上が年10〜15人・最速158前後
    // （ドラフト・レポート 2021〜2025）。旧式は Lv99＝149.6 が上限で150km/h超が作れなかった。Lv90（146.4）から
    // 1.16 km/h/Lv で Lv100＝158.0 に届かせる（Lv93≒149.9・Lv95≒152.2）。人数は生成側（AI校のやわらかい天井
    // RosterCoefficients.VelocitySoftCap）で合わせる＝この式は「上位の1Lvの重み」だけを決める。
    //
    // この区分線形の定数は「Lv→km/h の変換式の定義」であって調整ノブではない（issue #1213・オーナー判断 2026-11-24）。
    // YAML には出さず、ここの const を正典にする（#1080 の Δ と同じ扱い）。球速の価値を動かしたいときは
    // data/coefficients.yaml の ability_effect（cq_per_kmh 等）と生成側のやわらかい天井で動かす。
    // 値は PitcherVelocityMappingTests が固定する＝変えるなら生成分布・帯への影響を見たうえでテストごと直す。
    private const double VelocityInterceptKmh = 108.5;
    private const double VelocitySlopeKmhPerLevel = 0.47;
    /// <summary>区分の境（この Lv までは従来の傾き）。</summary>
    public const int VelocityKneeLevel = 50;
    /// <summary>境から上の傾き[km/h per Lv]。</summary>
    public const double VelocitySlopeAboveKneeKmhPerLevel = 0.36;
    private const double VelocityKneeKmh = VelocityInterceptKmh + VelocityKneeLevel * VelocitySlopeKmhPerLevel;   // 132.0
    /// <summary>上位の境（ここから上は150km/h超へ伸ばす傾き）。</summary>
    public const int VelocityTopKneeLevel = 90;
    /// <summary>上位の境から上の傾き[km/h per Lv]（Lv100＝158.0km/h）。</summary>
    public const double VelocitySlopeAboveTopKneeKmhPerLevel = 1.16;
    private const double VelocityTopKneeKmh =
        VelocityKneeKmh + (VelocityTopKneeLevel - VelocityKneeLevel) * VelocitySlopeAboveKneeKmhPerLevel;   // 146.4

    /// <summary>
    /// 育成レイヤの球速Level（<b>表示Lv</b>）→最高球速[km/h]の変換。表示Lvは今の物差しより Δ 低い（issue #1080・
    /// <see cref="AbilityScale"/>）ので +Δ して今の区分線形（<see cref="VelocityKmhFromPhysicalLevel(double)"/>）に通す＝km/h は Δ によらず不変。
    /// </summary>
    public static double VelocityKmhFromLevel(int level) => VelocityKmhFromPhysicalLevel(AbilityScale.Phys(level));

    /// <summary>
    /// 今の物差しの球速Lv（物理Lv・Lv50・Lv90 を境にした区分線形）→最高球速[km/h]。端数可（AI校のやわらかい天井が
    /// 端数の球速Lvを作るため）。生成・成長の内部（表示へ下げる前）と効きの軸だけが使う。
    /// </summary>
    public static double VelocityKmhFromPhysicalLevel(double level)
        => level <= VelocityKneeLevel
            ? VelocityInterceptKmh + level * VelocitySlopeKmhPerLevel
            : level <= VelocityTopKneeLevel
                ? VelocityKneeKmh + (level - VelocityKneeLevel) * VelocitySlopeAboveKneeKmhPerLevel
                : VelocityTopKneeKmh + (level - VelocityTopKneeLevel) * VelocitySlopeAboveTopKneeKmhPerLevel;

    /// <summary>
    /// 最高球速[km/h]→今の物差しの球速Lv（物理Lv）の逆変換（端数つき・下限1でだけ丸める）。AI校の毎年の底上げが
    /// 「天井の手前の素の値」へ戻すのに使う（物理Lvの空間）。
    /// </summary>
    public static double PhysicalLevelFromVelocityKmhExact(double kmh)
    {
        var lv = kmh <= VelocityKneeKmh
            ? (kmh - VelocityInterceptKmh) / VelocitySlopeKmhPerLevel
            : kmh <= VelocityTopKneeKmh
                ? VelocityKneeLevel + (kmh - VelocityKneeKmh) / VelocitySlopeAboveKneeKmhPerLevel
                : VelocityTopKneeLevel + (kmh - VelocityTopKneeKmh) / VelocitySlopeAboveTopKneeKmhPerLevel;
        // 往復の浮動小数の誤差（整数Lv→km/h→Lv が 89.99999999 になる等）で別の値を作らないよう丸める。
        return Math.Max(1.0, Math.Round(lv, 9));
    }

    /// <summary>
    /// 最高球速[km/h]→球速Level（<b>表示Lv</b>）の逆変換（表示・選出で「球速の等級」を復元する用。順変換と同じ区分の逆関数）。
    /// 今の物差しの整数Lv（<see cref="PhysicalLevelFromVelocityKmh"/>）から Δ を引く。
    /// </summary>
    public static int LevelFromVelocityKmh(double kmh) => AbilityScale.Disp(PhysicalLevelFromVelocityKmh(kmh));

    /// <summary>最高球速[km/h]→今の物差しの球速Lv（物理Lv・整数）。天井は 100+Δ（issue #1080）。</summary>
    public static int PhysicalLevelFromVelocityKmh(double kmh)
        => (int)Math.Round(Math.Clamp(PhysicalLevelFromVelocityKmhExact(kmh), 1, AbilityScale.PhysMax100));

    /// <summary>リーグ平均的な投手（球速132km/h, 各50 = D帯, 目安100.5球 = Lv50, issue #408）。</summary>
    public static PitcherAttributes LeagueAverage => new();

    /// <summary>
    /// 継投人選のランキングスコア（issue #209 Phase B）。球速・制球・キレ・スタミナを等級（Level）に
    /// 揃えた素点和から、<b>球質タイプの上乗せの合計を差し引いた</b>地力（issue #908）。AiTeamBuilder.PitcherScore と
    /// 同一の合成で、控え全員の投手能力を同じ土俵で比較する（球速単独で決まらない＝用語集 PitcherComposite の趣旨）。
    /// 上乗せは試合の失点が型によらず揃うよう校正してあり合計は揃っていない（本格 +6・技巧 −2・軟投 −5）ので、
    /// 差し引かないと同じ実力でも本格派ばかりが継投に選ばれる。
    /// </summary>
    public double ReliefScore(PitcherArchetypeCoefficients? archetypes = null, int delta = AbilityScale.Delta)
        => PhysicalStrengthSum(archetypes ?? DefaultArchetypes, delta);

    /// <summary>
    /// 型の上乗せを差し引いた地力（<see cref="PitcherArchetypes.StrengthSum"/>）を今の物差し（物理Lv）で測る（issue #1080）。
    /// 剛腕の制球は割合で戻す（非線形）ので、表示Lvのまま足すと Δ で並びが変わる＝必ず物理Lvで合成する。
    /// </summary>
    public double PhysicalStrengthSum(PitcherArchetypeCoefficients archetypes, int delta = AbilityScale.Delta)
        => PitcherArchetypes.StrengthSum(Archetype, PhysicalLevelFromVelocityKmh(MaxVelocityKmh), Control + delta,
            PhysicalLevelFromStaminaPitches(StaminaPitches), PitchRank + delta, archetypes);

    private static readonly PitcherArchetypeCoefficients DefaultArchetypes = new();
}
