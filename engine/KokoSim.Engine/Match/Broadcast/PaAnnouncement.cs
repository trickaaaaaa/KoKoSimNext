using System.Collections.Generic;
using System.Text;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Match.Broadcast;

/// <summary>
/// 打席に入る打者の場内放送（ウグイス嬢・issue #919）を、鳴らす音声部品の並びに組む。
/// 文面は「4番、キャッチャー、田中くん。」、代打でまだ打席を終えていない打者は「バッター、田中くん。」。
///
/// <para>音声部品は VOICEVOX:No.7（アナウンス）で <c>tools/voicevox-announcements.py</c> が生成する。
/// 部品の名前はここが正で、スクリプトは同じ規則で作る（苗字の部品名は苗字の UTF-8 の FNV-1a 32bit）。
/// 苗字ごとに「◯◯くん。」を1本で録る＝つなぎ目が名前の中に来ない。</para>
/// </summary>
public static class PaAnnouncement
{
    /// <summary>「バッター、」の部品名。</summary>
    public const string BatterClip = "batter";

    /// <summary>打順「N番、」の部品名（1〜9）。</summary>
    public static string OrderClip(int order) => "order_" + order;

    /// <summary>守備位置「キャッチャー、」の部品名。</summary>
    public static string PositionClip(FieldPosition p) => "pos_" + p switch
    {
        FieldPosition.Pitcher => "p",
        FieldPosition.Catcher => "c",
        FieldPosition.FirstBase => "1b",
        FieldPosition.SecondBase => "2b",
        FieldPosition.ThirdBase => "3b",
        FieldPosition.Shortstop => "ss",
        FieldPosition.LeftField => "lf",
        FieldPosition.CenterField => "cf",
        FieldPosition.RightField => "rf",
        _ => "dh",
    };

    /// <summary>苗字「田中くん。」の部品名（苗字の UTF-8 の FNV-1a 32bit を16進8桁で）。</summary>
    public static string SurnameClip(string surname) => "sei_" + Fnv1a32(surname).ToString("x8");

    /// <summary>
    /// 鳴らす部品の並び。<paramref name="position"/> は打順表のいまの位置（DHの枠は DH。
    /// <c>LiveLineup()[order-1].Position</c>）。名前が空なら何も言わない（空の列）。
    /// </summary>
    public static IReadOnlyList<string> Clips(int order, FieldPosition position, string batterName, bool pinchHitter)
    {
        var surname = PlayerNameGenerator.Surname(batterName);
        if (surname.Length == 0) return System.Array.Empty<string>();
        if (pinchHitter) return new[] { BatterClip, SurnameClip(surname) };
        if (order < 1 || order > 9) return System.Array.Empty<string>();
        return new[] { OrderClip(order), PositionClip(position), SurnameClip(surname) };
    }

    /// <summary>読み上げる文面（確認・ログ用。部品の並びと同じ規則）。</summary>
    public static string Text(int order, FieldPosition position, string batterName, bool pinchHitter)
    {
        var surname = PlayerNameGenerator.Surname(batterName);
        if (surname.Length == 0) return "";
        if (pinchHitter) return "バッター、" + surname + "くん。";
        if (order < 1 || order > 9) return "";
        return order + "番、" + SubstitutionTelop.PositionText(position) + "、" + surname + "くん。";
    }

    /// <summary>
    /// 同じ打席で二度言わないための鍵（回・表裏・打順・打席数＋打者）。打者を含めるのは、打席の頭で
    /// 代打を送ったら新しい打者を言い直すため。<paramref name="plateAppearances"/> は試合で確定した打席数。
    /// </summary>
    public static string Key(int inning, bool isTop, int order, int plateAppearances, int? batterSourceId, string batterName)
        => inning + (isTop ? "T" : "B") + "/" + order + "/" + plateAppearances + "/" + (batterSourceId?.ToString() ?? "-") + "/" + batterName;

    /// <summary>FNV-1a 32bit（UTF-8）。スクリプト側（Python）と同じ値になる。</summary>
    public static uint Fnv1a32(string s)
    {
        var h = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            h ^= b;
            h *= 16777619u;
        }
        return h;
    }
}
