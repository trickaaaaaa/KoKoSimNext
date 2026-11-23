using System;
using System.Collections.Generic;

namespace KokoSim.Engine.Players;

/// <summary>
/// 表示能力の物差し（issue #1080・設計書21 §物差しの下げ幅）。保存・表示する能力値（表示Lv）は「今の物差し」より
/// 一律 <see cref="Delta"/> 低い。能力の絶対値を読む所（物理の入口・采配AIの閾値・チーム総合・ドラフトの注目度・
/// 練習の必要exp など）は <see cref="Phys(int)"/>（表示Lv＋Δ）で読む＝試合の物理・帯・km/h の分布・学校どうしの勝率は
/// Δ によらず不変。生成は今の物差し（物理Lv）のまま作り、最後に <see cref="ToDisplay(Player)"/> で一律 −Δ する。
///
/// <para>天井と下限の扱い（オーナー決定 2026-11-21）: 物理Lvの<b>天井は +Δ</b>（表示99＝物理106まで出せる＝強豪の個性）、
/// <b>下限は物理のまま</b>（表示では Δ 下がる: AI校 10→3・専門外 8→1）。表示の最小は1（物理 1+Δ）。</para>
///
/// <para>対象: 1〜99 の表示能力（打撃・走塁・守備・投手の各能力・精神力・捕手リード・才能上限）。
/// 対象外: 弾道（ゴロ型〜フライ型の型の軸）・守備位置適性（地力に掛ける補正）・性格の4軸・km/h・目安投球数（画面の単位）。</para>
///
/// <para>試合の中は係数束の <see cref="AbilityEffectCoefficients.Delta"/>（YAML <c>ability_effect.delta</c>）を読む
/// （漏れ検出テストが Δ=0 と Δ=7 の試合を比べるため）。試合の外（生成・育成・総合・ドラフト）はこの定数を読む。
/// 両者が食い違うと物理がずれるので、YAML の読み込みは <see cref="Delta"/> 以外の値を拒む。</para>
/// </summary>
public static class AbilityScale
{
    /// <summary>物差しの下げ幅（表示Lv＝物理Lv−Δ）。7が上限（専門外の下限 8−7=1 が表示の最小にちょうど収まる）。</summary>
    public const int Delta = 7;

    /// <summary>
    /// 平均的な能力の表示Lv（今の物差しの50＝補正なしの中立点）。試合用の選手・投手能力の既定値（テスト素体・
    /// 能力を詰めない投影）はこれ＝物理は従来の既定50と同じ。
    /// </summary>
    public const int AverageDisplay = 50 - Delta;

    /// <summary>表示の最小値（物理 1+Δ）。</summary>
    public const int DisplayMin = 1;

    /// <summary>表示Lv→物理Lv（今の物差し）。</summary>
    public static int Phys(int display) => display + Delta;

    /// <summary>表示Lv→物理Lv（端数つき）。</summary>
    public static double Phys(double display) => display + Delta;

    /// <summary>物理Lv→表示Lv（下限 <see cref="DisplayMin"/>。天井は物理側の +Δ 天井で既に収まっている）。</summary>
    public static int Disp(int physical) => Math.Max(DisplayMin, physical - Delta);

    /// <summary>物理側の天井（今の天井＋Δ）。生成・成長の clamp の上端に使う（表示ではもとの天井になる）。</summary>
    public static int Ceiling(int currentCeiling) => currentCeiling + Delta;

    /// <summary>物理側の天井 99+Δ（表示99）。</summary>
    public const int PhysMax99 = 99 + Delta;

    /// <summary>物理側の天井 100+Δ（表示100）。</summary>
    public const int PhysMax100 = 100 + Delta;

    /// <summary>試合用の選手の表示Lvを物理Lvへ（全対象能力 +Δ）。</summary>
    public static Player ToPhysical(Player p) => Shift(p, +Delta);

    /// <summary>試合用の選手の物理Lvを表示Lvへ（全対象能力 −Δ・下限1）。</summary>
    public static Player ToDisplay(Player p) => Shift(p, -Delta);

    /// <summary>一律 <paramref name="by"/> だけずらす（負なら下限1で止める）。セーブの移行用。</summary>
    public static Player ShiftBy(Player p, int by) => by == 0 ? p : Shift(p, by);

    /// <summary>物理Lvの空間で <paramref name="f"/> を当てて表示Lvへ戻す（生成・成長の式を今の物差しのまま使うための包み）。</summary>
    public static Player InPhysical(Player p, Func<Player, Player> f) => ToDisplay(f(ToPhysical(p)));

    /// <summary>
    /// チームの全選手（打順・控え・DHの先発投手・主将）を表示Lvへ。主将は在場判定が参照一致なので、同じ選手の写しを指す。
    /// </summary>
    public static Match.Game.Team ToDisplay(Match.Game.Team t) => ShiftTeam(t, ToDisplay);

    /// <summary>チームの全選手を物理Lvへ（<see cref="ToDisplay(Match.Game.Team)"/> の逆）。</summary>
    public static Match.Game.Team ToPhysical(Match.Game.Team t) => ShiftTeam(t, ToPhysical);

    private static Match.Game.Team ShiftTeam(Match.Game.Team t, Func<Player, Player> f)
    {
        var map = new Dictionary<Player, Player>(ByReference.Instance);
        Player M(Player p)
        {
            if (!map.TryGetValue(p, out var q)) { q = f(p); map[p] = q; }
            return q;
        }
        var order = new List<Player>(t.BattingOrder.Count);
        foreach (var p in t.BattingOrder) order.Add(M(p));
        var reserves = new List<Player>(t.Reserves.Count);
        foreach (var p in t.Reserves) reserves.Add(M(p));
        return t with
        {
            BattingOrder = order,
            Reserves = reserves,
            StartingPitcher = t.StartingPitcher is { } sp ? M(sp) : null,
            Captain = t.Captain is { } c ? M(c) : null,
        };
    }

    /// <summary>投手能力の表示Lv⇔物理Lv（制球・キレ・各球種の球威/キレ。km/h・目安投球数・型はそのまま）。</summary>
    public static PitcherAttributes Shift(PitcherAttributes a, int by)
    {
        IReadOnlyList<PitchSlot>? rep = a.Repertoire;
        if (rep is { Count: > 0 } r)
        {
            var copy = new PitchSlot[r.Count];
            for (var i = 0; i < r.Count; i++)
                copy[i] = r[i] with { Power = Move(r[i].Power, by), Sharpness = Move(r[i].Sharpness, by) };
            rep = copy;
        }
        return a with { Control = Move(a.Control, by), PitchRank = Move(a.PitchRank, by), Repertoire = rep };
    }

    private static Player Shift(Player p, int by)
    {
        IReadOnlyList<int>? byPos = p.FieldingByPosition;
        if (byPos is { Count: > 0 } f)
        {
            var copy = new int[f.Count];
            for (var i = 0; i < f.Count; i++) copy[i] = Move(f[i], by);
            byPos = copy;
        }
        return p with
        {
            Contact = Move(p.Contact, by),
            Power = Move(p.Power, by),
            Discipline = Move(p.Discipline, by),
            Speed = Move(p.Speed, by),
            ArmStrength = Move(p.ArmStrength, by),
            ThrowAccuracy = Move(p.ThrowAccuracy, by),
            Fielding = Move(p.Fielding, by),
            FieldingByPosition = byPos,
            Catching = Move(p.Catching, by),
            Lead = Move(p.Lead, by),
            Bunt = Move(p.Bunt, by),
            Steal = Move(p.Steal, by),
            Baserunning = Move(p.Baserunning, by),
            Mental = Move(p.Mental, by),
            Pitching = Shift(p.Pitching, by),
        };
    }

    /// <summary>−Δ は表示の下限1で止める（物理で 1〜Δ の値は表示1＝物理 1+Δ に丸まる）。+Δ は素直に足す。</summary>
    private static int Move(int v, int by) => by < 0 ? Math.Max(DisplayMin, v + by) : v + by;

    /// <summary>実体の同一性で比べる（netstandard2.1 には ReferenceEqualityComparer が無い）。</summary>
    private sealed class ByReference : IEqualityComparer<Player>
    {
        public static readonly ByReference Instance = new();
        public bool Equals(Player? x, Player? y) => ReferenceEquals(x, y);
        public int GetHashCode(Player obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
