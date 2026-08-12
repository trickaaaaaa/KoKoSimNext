using System;
using System.Collections.Generic;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;      // AbilityKind（能力ラベル単一ソースの引数, issue #94）
using KokoSim.Unity.Shell;        // AbilityLabels

namespace KokoSim.Unity.Match
{
    /// <summary>能力列の見せ方（issue #602）。ランクチップにしてよい軸かどうかで分ける。</summary>
    public enum AbilityColumnKind
    {
        /// <summary>0〜100の優劣軸。ランクチップ＋数値で出してよい。</summary>
        Rank,
        /// <summary>タイプ軸（弾道）。優劣が無いのでランク色を付けない（issue #219）。</summary>
        Type,
        /// <summary>単位つきの物理量（球速 km/h）。ランク化せず実寸で出す（issue #94）。</summary>
        Unit,
    }

    /// <summary>能力表の1列（表示名・一覧の見出し・値の取り方）。</summary>
    public sealed class AbilityColumn
    {
        /// <summary>表示名（「ミート」）。<see cref="AbilityLabels"/> を単一ソースにする。</summary>
        public string Label = "";

        /// <summary>一覧の列見出し（1〜2字の略。「ミ」）。幅の狭い表で桁を揃えるため。</summary>
        public string Head = "";

        public AbilityColumnKind Kind = AbilityColumnKind.Rank;

        /// <summary>値の取り方（値を持たないときは -1）。</summary>
        public Func<Player, int> Get = _ => -1;
    }

    /// <summary>
    /// 試合中の <see cref="Player"/> から能力を読むときの<b>列の定義と並び順の単一ソース</b>（issue #602）。
    ///
    /// <para>試合中の実体は育成選手（<c>DevelopingPlayer</c>）ではなく engine の <see cref="Player"/> で、
    /// 相手校の選手は育成選手を<b>持たない</b>（<c>StrengthTeamFactory</c> が Player を直接組む）。
    /// そのため名簿画面の <c>PlayerCompareTabs</c>（DevelopingPlayer 前提）は試合中には使えない。
    /// ここが試合中の唯一の入口で、選手交代モーダル（設計書09 §6）と能力ビューア（issue #602）が
    /// <b>同じ表・同じ並び</b>を引く＝画面をまたいでも項目の順が変わらない。</para>
    ///
    /// <para>ラベルは <see cref="AbilityLabels"/>、ランクは <c>Tiers.FromStrength</c>、
    /// スタミナ・球威・球速の逆変換は engine の <see cref="PitcherAttributes"/> に委ねる
    /// ＝ここで式を作り直さない（不変条件#1）。</para>
    ///
    /// <para><b>読むだけ</b>＝engine を一切変更しない。乱数も引かないので決定論・統計帯に影響しない。</para>
    /// </summary>
    public static class MatchAbilityColumns
    {
        /// <summary>
        /// 野手能力の列（打撃・走塁・守備＋精神力）。
        /// 並びはメンバー設定・スタメン設定（<c>PlayerCompareTabs</c>）と同じ相対順にする
        /// ＝画面をまたいでも目が滑らない。ここに無いのは バント／送球／リード の3つ（向こうにだけ有る）。
        /// </summary>
        public static readonly IReadOnlyList<AbilityColumn> Fielder = new[]
        {
            Col(AbilityKind.LaunchTendency, "弾", AbilityColumnKind.Type, p => p?.LaunchTendency ?? -1),
            Col(AbilityKind.Contact, "ミ", p => p?.Contact ?? -1),
            Col(AbilityKind.Power, "パ", p => p?.Power ?? -1),
            Col(AbilityKind.Discipline, "選", p => p?.Discipline ?? -1),
            Col(AbilityKind.Speed, "走", p => p?.Speed ?? -1),
            Col(AbilityKind.Steal, "盗", p => p?.Steal ?? -1),
            Col(AbilityKind.Baserunning, "塁", p => p?.Baserunning ?? -1),
            Col(AbilityKind.Fielding, "守", p => p?.Fielding ?? -1),
            Col(AbilityKind.Catching, "捕", p => p?.Catching ?? -1),
            Col(AbilityKind.ArmStrength, "肩", p => p?.ArmStrength ?? -1),
            Raw("精神力", "精", p => p?.Mental ?? -1),
        };

        /// <summary>
        /// 投手能力の列。並び・項目はエンジンの単一ソース <c>AbilityKinds.Pitching</c>
        /// （球速/制球/スタミナ/キレ/球威）に合わせ、精神力（AbilityKind 外）を末尾に足す。
        /// <b>球速だけは <see cref="AbilityColumnKind.Unit"/></b>＝交代モーダルの比較表には出さず
        /// （持ち球チャートのストレートに添える・issue #560）、一覧にはチャートが無いので km/h で出す。
        /// </summary>
        public static readonly IReadOnlyList<AbilityColumn> Pitcher = new[]
        {
            Col(AbilityKind.Velocity, "球速", AbilityColumnKind.Unit, VelocityKmh),
            Col(AbilityKind.Control, "制", p => p?.Pitching?.Control ?? -1),
            Col(AbilityKind.Stamina, "ス", Stamina),
            Col(AbilityKind.PitchRank, "キ", p => p?.Pitching?.PitchRank ?? -1),
            Col(AbilityKind.Stuff, "威", Stuff),
            Raw("精神力", "精", p => p?.Mental ?? -1),
        };

        private static AbilityColumn Col(AbilityKind kind, string head, Func<Player, int> get)
            => new AbilityColumn { Label = AbilityLabels.Jp(kind), Head = head, Get = get };

        private static AbilityColumn Col(AbilityKind kind, string head, AbilityColumnKind columnKind,
            Func<Player, int> get)
            => new AbilityColumn { Label = AbilityLabels.Jp(kind), Head = head, Kind = columnKind, Get = get };

        /// <summary>AbilityKind に無い項目（精神力）。</summary>
        private static AbilityColumn Raw(string label, string head, Func<Player, int> get)
            => new AbilityColumn { Label = label, Head = head, Get = get };

        // ── 逆変換（試合用 Player は育成レベルを持たない） ────────────────

        /// <summary>
        /// スタミナは Level（0〜100）で出す＝メンバー設定・スタメン設定と同じもの。
        /// 試合用の Player が持つのは目安投球数（issue #356）だけなので、順変換と同じ定数から出す
        /// エンジンの逆変換で戻す（UI側で式を作り直さない・不変条件#1）。上限なしなので Lv100超はそのまま。
        /// </summary>
        public static int Stamina(Player p)
            => p?.Pitching == null ? -1 : PitcherAttributes.LevelFromStaminaPitches(p.Pitching.StaminaPitches);

        /// <summary>
        /// 球威＝ストレートの有効軸（issue #274）。試合用の Player は育成能力を持たないので、
        /// レパートリーのストレート枠の Power から引く（RosterTeamBuilder が Stuff レベルを入れている値）。
        /// </summary>
        public static int Stuff(Player p)
        {
            if (p?.Pitching == null) return -1;
            foreach (var slot in p.Pitching.EffectiveRepertoire)
                if (slot.Type == PitchType.Fastball) return slot.Power;
            return -1;
        }

        /// <summary>球速[km/h]（四捨五入）。Level→km/h の式は <see cref="PitcherAttributes"/> にしか置かない。</summary>
        public static int VelocityKmh(Player p)
            => p?.Pitching == null ? -1 : (int)Math.Round(p.Pitching.MaxVelocityKmh);

        // ── 比較表（交代モーダル用） ──────────────────────────────

        /// <summary>
        /// 2人ぶんの比較行（<c>msub-cmp-rows</c> が引く）。<see cref="AbilityColumnKind.Unit"/> の列は
        /// 落とす＝球速はカードの持ち球チャートが担当し、表に二度出さない（issue #560）。
        /// </summary>
        public static IEnumerable<(string Label, int A, int B, bool TypeAxis)> CompareRows(
            Player a, Player b, bool pitcherView)
        {
            foreach (var c in pitcherView ? Pitcher : Fielder)
            {
                if (c.Kind == AbilityColumnKind.Unit) continue;
                yield return (c.Label, c.Get(a), c.Get(b), c.Kind == AbilityColumnKind.Type);
            }
        }
    }
}
