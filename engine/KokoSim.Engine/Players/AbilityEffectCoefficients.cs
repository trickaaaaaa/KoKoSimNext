using System;

namespace KokoSim.Engine.Players;

/// <summary>
/// 表示能力（1〜100）が物理層へ入るときの「効き」（issue prefecture-dominance-physics・OPEN-QUESTIONS Q24-5・設計書21）。
///
/// <para>実測（2026-08-18）でエンジンは「チーム全体が10Lv上＝勝率90%」と急で、AI校だけを生成で寝かせる圧縮
/// （<c>tier_compression_*</c>）は表示ランクと中身を乖離させた。ここでは<b>全員に対称</b>に、表示→物理の境界で
/// <c>effective = pivot + scale×(Lv − pivot)</c> を掛ける。表示Lv・最速km/h・目安球数・打球の3D表現は変えず、
/// 動くのは「Lv1あたりの結果の差」だけ。</para>
///
/// <para><b>入口は4つだけ</b>（不変条件#1: 表示層→物理層の変換は一箇所）:
/// 打者 <see cref="Player.ToBatter(AbilityEffectCoefficients)"/>／野手 <see cref="Player.ToFielder(AbilityEffectCoefficients)"/>／
/// 投手 <see cref="Pitcher(PitcherAttributes)"/>（制球・キレ・球威ランク）＋球速の反応 <see cref="PitchingKmh(double)"/>
/// （<see cref="Match.Pitching.PitchPlan.ReactionVelocityKmh"/>）。解決式で <see cref="Player"/> の能力を直接読む場所は
/// <see cref="Batting(int)"/> 等を通す。</para>
///
/// <para><b>投手の扱い</b>（オーナー決定 2026-08-19・案A「全員一律・エースはスターで突出」）: 制球・キレ・球威ランクは
/// Lv で寝かせる。<b>球速は km/h が画面に出る</b>（最速・毎球の球速表示）ので実球速は変えず、打者の反応
/// （球威・タイミング・打球質・バント難度）に使う <see cref="Match.Pitching.PitchPlan.ReactionVelocityKmh"/> だけを
/// Lv50 の 132km/h を軸に寝かせる。スタミナ（目安球数）も画面に出る単位なので寝かせない。</para>
/// </summary>
public sealed record AbilityEffectCoefficients
{
    /// <summary>不動点（リーグ平均）。ここの選手は効きの変更で1ビットも変わらない。</summary>
    public double Pivot { get; init; } = 50.0;

    // 実測（2026-08-19・永続ロスター同士＝実ゲーム経路・スター lift 0.25・300試合×2方向）:
    //   投手1.0/打撃1.0/守備1.0/走力1.0: 80vs75 74.5%／80vs70 86%／80vs65 96%／60vs50 94%（全能力+10 ≈ 勝率90%＝急すぎ）
    //   投手1.0/打撃0.5/守備0.4/走力0.5: 80vs75 65%／80vs70 78%／80vs65 89%／60vs50 85%、ただし 80vs80 が 1.3−1.3 の投手戦
    //   投手0.55/打撃0.55/守備0.45/走力0.55: 80vs75 65%／80vs70 82%／80vs65 88%／80vs60 94%／60vs50 81%、80vs80 2.4−2.4
    // → 案A（全員一律≈0.55）を採用。守備だけ 0.45 なのは守備+10 の得点価値（+1.43点）が打撃+10（+1.1点）より大きいため。
    /// <summary>打撃3能力（ミート/パワー/選球眼/バント）の効き。1.0＝従来どおり。</summary>
    public double BattingScale { get; init; } = 0.55;

    /// <summary>守備4能力（守備/捕球/肩/送球）の効き。1.0＝従来どおり。</summary>
    public double FieldingScale { get; init; } = 0.45;

    /// <summary>走力系（走力/盗塁/走塁）の効き。1.0＝従来どおり。</summary>
    public double RunningScale { get; init; } = 0.55;

    /// <summary>
    /// 投手系（制球/キレ/球威ランク/捕手リード）と球速の反応の効き。1.0＝従来どおり。
    /// 球速は km/h を変えず <see cref="PitchingKmh(double)"/> で打者側の反応だけを寝かせる。
    /// </summary>
    public double PitchingScale { get; init; } = 0.55;

    /// <summary>恒等（従来と1ビット一致）。テストと非物理経路の既定。</summary>
    public static readonly AbilityEffectCoefficients Identity = new() { BattingScale = 1.0, FieldingScale = 1.0, RunningScale = 1.0, PitchingScale = 1.0 };

    public int Batting(int lv) => Scale(lv, BattingScale);
    public int Fielding(int lv) => Scale(lv, FieldingScale);
    public int Running(int lv) => Scale(lv, RunningScale);
    public int Pitching(int lv) => Scale(lv, PitchingScale);

    /// <summary>
    /// 球速の反応[km/h]: 最速 v を Lv50 の最速（<see cref="PitcherAttributes.VelocityKmhFromLevel"/>(pivot)＝132km/h）を軸に
    /// <c>pivotKmh + scale×(v − pivotKmh)</c>。scale 1.0 で恒等。<see cref="Pitcher"/> がこれと実最速の差を
    /// <see cref="PitcherAttributes.ReactionVelocityOffsetKmh"/> に焼き、PitchSelection が毎球の実球速に足す＝
    /// 「その投手のストレートの速さの差」だけを寝かせ、消耗・調子・ギア・ドロップ・変化球の緩急はそのまま残す。
    /// </summary>
    public double PitchingKmh(double kmh)
    {
        if (PitchingScale == 1.0) return kmh;
        var pivotKmh = PitcherAttributes.VelocityKmhFromLevel((int)Math.Round(Pivot));
        return pivotKmh + PitchingScale * (kmh - pivotKmh);
    }

    /// <summary>
    /// 投手能力の入口: 制球・キレ（PitchRank／各球種の Power・Sharpness）を寝かせ、球速の反応オフセットを焼いた写しを返す。
    /// 最速km/h・目安球数・投法・球質タイプは据え置き（画面に出る単位・型）。scale 1.0 なら同一インスタンス。
    /// </summary>
    public PitcherAttributes Pitcher(PitcherAttributes p)
    {
        if (PitchingScale == 1.0) return p;
        PitchSlot[]? rep = null;
        if (p.Repertoire is { Count: > 0 } r)
        {
            rep = new PitchSlot[r.Count];
            for (var i = 0; i < r.Count; i++)
                rep[i] = r[i] with { Power = Pitching(r[i].Power), Sharpness = Pitching(r[i].Sharpness) };
        }
        return p with
        {
            Control = Pitching(p.Control),
            PitchRank = Pitching(p.PitchRank),
            Repertoire = rep ?? p.Repertoire,
            ReactionVelocityOffsetKmh = PitchingKmh(p.MaxVelocityKmh) - p.MaxVelocityKmh,
        };
    }

    private int Scale(int lv, double s)
    {
        if (s == 1.0) return lv;   // 恒等は丸めも通さない（従来と1ビット一致）
        return (int)Math.Clamp(Math.Round(Pivot + s * (lv - Pivot)), 1, 100);
    }
}
