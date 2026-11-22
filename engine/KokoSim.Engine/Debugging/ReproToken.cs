using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Debugging;

/// <summary>
/// 再現トークン（設計書17 §3.3, F0）。「この試合のこの場面」を1本の文字列で指す。
///
/// <code>k1:&lt;rngStateHex&gt;:&lt;pa&gt;:&lt;pitch&gt;:&lt;fixtureFp(8桁)&gt;</code>
///
/// <para>トークンだけでは選手データまでは復元できない。よって対戦カード指紋（<see cref="FixtureFingerprint"/>）を
/// 併記し、貼り付け時に照合する。不一致なら「別のロスターです」と<b>警告する</b>のが本設計の要点で、
/// 黙って別の試合を再生してはならない（<see cref="Verify"/>）。</para>
/// </summary>
public readonly record struct ReproToken(
    ulong[] RngState, int PlateAppearance, int Pitch, string FixtureFingerprint)
{
    /// <summary>トークンのバージョン接頭辞。書式を変えたら上げる（旧トークンは解釈を拒否する）。</summary>
    public const string Version = "k1";

    /// <summary>対戦カード指紋の桁数（SHA256 先頭）。</summary>
    public const int FingerprintLength = 8;

    public override string ToString()
    {
        var sb = new StringBuilder(Version).Append(':');
        foreach (var w in RngState) sb.Append(w.ToString("x16", CultureInfo.InvariantCulture));
        sb.Append(':').Append(PlateAppearance.ToString(CultureInfo.InvariantCulture));
        sb.Append(':').Append(Pitch.ToString(CultureInfo.InvariantCulture));
        sb.Append(':').Append(FixtureFingerprint);
        return sb.ToString();
    }

    /// <summary>文字列からトークンを解釈する。書式違反・バージョン違いは false（例外を投げない）。</summary>
    public static bool TryParse(string? text, out ReproToken token)
    {
        token = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text!.Trim().Split(':');
        if (parts.Length != 5) return false;
        if (parts[0] != Version) return false;

        var hex = parts[1];
        if (hex.Length == 0 || hex.Length % 16 != 0) return false;
        var words = new ulong[hex.Length / 16];
        for (var i = 0; i < words.Length; i++)
        {
            if (!ulong.TryParse(hex.Substring(i * 16, 16), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out words[i])) return false;
        }
        if (words.Length != 4 && words.Length != Core.Xoshiro256Random.StateWords) return false;

        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pa) || pa < 0) return false;
        if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pitch) || pitch < 0) return false;
        if (parts[4].Length != FingerprintLength) return false;

        token = new ReproToken(words, pa, pitch, parts[4]);
        return true;
    }

    /// <summary>このトークンから中断保存状態を組む（<see cref="GameReplay.Restore"/> にそのまま渡せる）。</summary>
    public GameSaveState ToSaveState(IReadOnlyList<GameDecision>? decisions = null) => new(0UL, PlateAppearance)
    {
        RngState = RngState,
        ConfirmedPitchesInCurrentPa = Pitch,
        Decisions = decisions ?? Array.Empty<GameDecision>(),
    };

    /// <summary>
    /// 現在の対戦カードとトークンの指紋が一致するか。false のとき呼び出し側は<b>必ず警告を出す</b>
    /// （設計書17 §3.3「黙って違う試合を再生しない」）。
    /// </summary>
    public bool Verify(Team away, Team home, GameContext ctx, ScenarioStart? scenario = null)
        => FixtureFingerprint == Fingerprint(away, home, ctx, scenario);

    /// <summary>
    /// 対戦カード指紋: 両校名・全選手の同一性・主要ルール・球場寸法の SHA256 先頭
    /// <see cref="FingerprintLength"/> 桁。<b>係数の細部までは含めない</b>（含めると係数調整のたびに
    /// 過去トークンが全滅して再現の役に立たなくなる）。「別のロスター／別の球場を掴んでいないか」の検出が目的。
    ///
    /// <para>場面ジャンプで始めた試合は開始局面（<paramref name="scenario"/>）も含める（issue #1285）。含めないと
    /// 「1回表から始まる試合」のトークンと同じ指紋になり、照合が通ったまま別の場面を再生してしまう。
    /// 通常の試合（null）は従来と1バイトも変わらない＝既存トークンは無効にならない。</para>
    /// </summary>
    public static string Fingerprint(Team away, Team home, GameContext ctx, ScenarioStart? scenario = null)
    {
        var sb = new StringBuilder();
        AppendTeam(sb, away);
        AppendTeam(sb, home);
        var c = CultureInfo.InvariantCulture;
        sb.Append("rules|").Append(ctx.RegulationInnings.ToString(c)).Append('|')
          .Append(ctx.MaxInnings.ToString(c)).Append('|')
          .Append(ctx.MercyRuleEnabled ? '1' : '0').Append('|')
          .Append(ctx.TieBreakEnabled ? '1' : '0').Append('|')
          .Append(ctx.TieBreakStartInning.ToString(c)).Append('|')
          .Append(ctx.WeeklyPitchLimit.ToString(c)).Append('\n');
        sb.Append("field|").Append(ctx.Field.LeftFenceM.ToString("F2", c)).Append('|')
          .Append(ctx.Field.CenterFenceM.ToString("F2", c)).Append('|')
          .Append(ctx.Field.RightFenceM.ToString("F2", c)).Append('|')
          .Append(ctx.Field.FenceHeightM.ToString("F2", c)).Append('\n');
        if (scenario is not null) sb.Append("scenario|").Append(scenario).Append('\n');

        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        var hex = new StringBuilder(FingerprintLength);
        for (var i = 0; i < FingerprintLength / 2; i++) hex.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
        return hex.ToString();
    }

    private static void AppendTeam(StringBuilder sb, Team t)
    {
        sb.Append("team|").Append(t.Name).Append('|').Append(t.PitcherSlot).Append('|').Append(t.DhSlot).Append('\n');
        foreach (var p in t.BattingOrder) AppendPlayer(sb, "o", p);
        if (t.StartingPitcher is { } sp) AppendPlayer(sb, "sp", sp);
        // 控えは1本の名簿（issue #381）。旧 "bp"/"bn" の2タグを "rs" へ統合したので、
        // 本変更をまたぐ再現トークンは指紋不一致になる＝警告して再生しない（設計書17 §3.3 の想定どおり）。
        foreach (var p in t.Reserves) AppendPlayer(sb, "rs", p);
    }

    private static void AppendPlayer(StringBuilder sb, string tag, Player p)
    {
        var c = CultureInfo.InvariantCulture;
        sb.Append(tag).Append('|').Append(p.Name).Append('|')
          .Append(p.SourceId?.ToString(c) ?? "-").Append('|').Append(p.UniformNumber.ToString(c)).Append('|')
          .Append(p.Position).Append('|')
          .Append(p.Contact.ToString(c)).Append(',').Append(p.Power.ToString(c)).Append(',')
          .Append(p.LaunchTendency.ToString(c)).Append(',').Append(p.Discipline.ToString(c)).Append(',')
          .Append(p.Speed.ToString(c)).Append(',').Append(p.ArmStrength.ToString(c)).Append(',')
          .Append(p.Fielding.ToString(c)).Append(',').Append(p.Catching.ToString(c)).Append(',')
          .Append(p.Mental.ToString(c)).Append('|');
        // 送球精度・捕手リード・走塁系3能力・利き手・特殊能力も試合結果を動かす＝指紋に入れる。
        // 入れないと、これらだけ育った後のトークンが照合を通って黙って別の試合を再生する（設計書17 §3.3）。
        // 本追加をまたぐ旧トークンは指紋不一致＝警告して再生しない。
        sb.Append(p.ThrowAccuracy.ToString(c)).Append(',').Append(p.Lead.ToString(c)).Append(',')
          .Append(p.Bunt.ToString(c)).Append(',').Append(p.Steal.ToString(c)).Append(',')
          .Append(p.Baserunning.ToString(c)).Append(',')
          .Append(p.Throws).Append(',').Append(p.Bats).Append('|');
        AppendSkills(sb, p.Skills);
        // 投手能力は全選手が持つ（#381）＝有無で分岐しない。旧トークンは野手の欄が空だったので指紋は変わる。
        var pit = p.Pitching;
        sb.Append(pit.MaxVelocityKmh.ToString("F1", c)).Append(',')
          .Append(pit.Control.ToString(c)).Append(',')
          .Append(pit.StaminaPitches.ToString("F0", c)).Append(',')
          .Append(pit.PitchRank.ToString(c)).Append(',').Append(pit.Style).Append('|');
        foreach (var slot in pit.EffectiveRepertoire)
            sb.Append(slot.Type).Append(':').Append(slot.Power.ToString(c)).Append(':').Append(slot.Sharpness.ToString(c)).Append(';');
        sb.Append('\n');
    }

    // 可視/隠しは効果判定で区別されない（SkillSet.Has）ので、和集合を名前順に並べる＝挿入順に依存しない。
    private static void AppendSkills(StringBuilder sb, SkillSet skills)
    {
        var names = new List<string>();
        foreach (var s in skills.Visible) names.Add(s.ToString());
        foreach (var s in skills.Hidden) if (!skills.Visible.Contains(s)) names.Add(s.ToString());
        names.Sort(StringComparer.Ordinal);
        sb.Append(string.Join(",", names)).Append('|');
    }
}
