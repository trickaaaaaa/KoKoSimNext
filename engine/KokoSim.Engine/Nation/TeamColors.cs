using System;
using System.Collections.Generic;
using System.Globalization;

namespace KokoSim.Engine.Nation;

/// <summary>
/// 8bit RGB（不変条件#3: エンジンは UnityEngine.Color を知らない）。Unity 側で Color へ変換する。
/// </summary>
public readonly struct RgbColor : IEquatable<RgbColor>
{
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }

    public RgbColor(byte r, byte g, byte b) { R = r; G = g; B = b; }

    /// <summary>"#RRGGBB" / "RRGGBB" を解釈する。桁数・文字が不正なら false（既定色へ落とさない＝誤りを隠さない）。</summary>
    public static bool TryParseHex(string? hex, out RgbColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex!.Trim().Trim('"');
        if (s.Length > 0 && s[0] == '#') s = s.Substring(1);
        if (s.Length != 6) return false;
        if (!byte.TryParse(s.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ||
            !byte.TryParse(s.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) ||
            !byte.TryParse(s.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            return false;
        color = new RgbColor(r, g, b);
        return true;
    }

    public string ToHex() => "#" + R.ToString("X2", CultureInfo.InvariantCulture)
                                 + G.ToString("X2", CultureInfo.InvariantCulture)
                                 + B.ToString("X2", CultureInfo.InvariantCulture);

    public bool Equals(RgbColor other) => R == other.R && G == other.G && B == other.B;
    public override bool Equals(object? obj) => obj is RgbColor o && Equals(o);
    public override int GetHashCode() => (R << 16) | (G << 8) | B;
    public override string ToString() => ToHex();
}

/// <summary>
/// 縦縞（設計書18 §3.5.3）。テクスチャに焼かずシェーダで手続き生成するため、有無・色・密度だけを持つ。
/// </summary>
/// <param name="On">縞を出すか。false のとき Color/Freq は無視する。</param>
/// <param name="Color">縞の色。</param>
/// <param name="FreqPerMeter">横1mあたりの縞の本数（実物のピンストライプは間隔2.5cm前後＝40本/m）。</param>
public sealed record TeamStripe(bool On, RgbColor Color, double FreqPerMeter)
{
    public static readonly TeamStripe None = new(false, default, 0.0);
}

/// <summary>
/// パレット表（data/team-colors.yaml）の1件。実物の高校野球は「白系の地色＋チーム色1」で
/// ほぼ説明がつくので、6領域を全部書かせず 2〜3色から導出する（<see cref="TeamColors.FromEntry"/>）。
/// </summary>
/// <param name="Id">安定キー（セーブ・上書き判定用）。</param>
/// <param name="Label">画面に出す和名。</param>
/// <param name="Base">地色（ユニ上・ズボン）。</param>
/// <param name="Team">チーム色（アンダーシャツ・ソックス・帽子クラウン）。</param>
/// <param name="Visor">つばの色（省略時は Team）。</param>
/// <param name="Stripe">縦縞。</param>
public sealed record TeamColorEntry(
    string Id, string Label, RgbColor Base, RgbColor Team, RgbColor Visor, TeamStripe Stripe);

/// <summary>
/// 描画へ渡す確定配色。パレット表から導出するほか、プレイヤーが領域ごとに上書きした結果もこの形で持つ
/// （＝表に無い配色も表現できる。自校の色はプレイヤーが決める・2026-07-29 監督決定）。
/// 肌・シューズは配色対象外（固定色）なので持たない。
/// </summary>
public sealed record TeamColors(
    RgbColor Shirt, RgbColor Pants, RgbColor Undershirt, RgbColor Socks,
    RgbColor CapCrown, RgbColor CapVisor, TeamStripe Stripe)
{
    /// <summary>パレット表1件 → 6領域。導出規則は data/team-colors.yaml の先頭コメントと対。</summary>
    public static TeamColors FromEntry(TeamColorEntry e) => new(
        Shirt: e.Base, Pants: e.Base,
        Undershirt: e.Team, Socks: e.Team,
        CapCrown: e.Team, CapVisor: e.Visor,
        Stripe: e.Stripe);
}

/// <summary>
/// チームカラーのパレット表と、校ID→配色の決定論割り当て（設計書18 §3.5.1）。
/// 全校を列挙せず「実在校らしい配色の型」を並べ、同じ配色の学校が複数出ることを許容する。
/// </summary>
public sealed class TeamColorPalette
{
    public IReadOnlyList<TeamColorEntry> Entries { get; }

    /// <summary>
    /// 領域ごとの色を選ぶときの見本一覧（表に出てくる色を重複なしで集めたもの）。
    /// <b>地色 → チーム色 → 縞色</b> の順に、表の並びを保って集める＝送り操作が
    /// 「明るい方から暗い方へ」に近い並びになる。表の外の色は出さない＝破綻した配色を作れない。
    /// </summary>
    public IReadOnlyList<RgbColor> Swatches { get; }

    private readonly IReadOnlyDictionary<RgbColor, string> _names;

    /// <summary>色の和名（表の color_names 由来）。未定義なら16進を返す＝画面に空欄が出ない。</summary>
    public string NameOf(RgbColor color)
        => _names.TryGetValue(color, out var n) ? n : color.ToHex();

    public TeamColorPalette(IReadOnlyList<TeamColorEntry> entries,
        IReadOnlyDictionary<RgbColor, string>? names = null)
    {
        if (entries == null || entries.Count == 0)
            throw new ArgumentException("パレット表が空", nameof(entries));
        Entries = entries;
        _names = names ?? new Dictionary<RgbColor, string>();

        var swatches = new List<RgbColor>();
        var seen = new HashSet<RgbColor>();
        void Add(RgbColor c) { if (seen.Add(c)) swatches.Add(c); }
        foreach (var e in entries) Add(e.Base);
        foreach (var e in entries) { Add(e.Team); Add(e.Visor); }
        foreach (var e in entries) if (e.Stripe.On) Add(e.Stripe.Color);
        Swatches = swatches;
    }

    /// <summary>
    /// 校IDから配色を決める。<b>同シード同結果でなく「同IDなら常に同じ」</b>＝乱数源を使わない純関数。
    /// Nation は決定論生成（保存せず毎回再生成）なので、これだけで年度・セーブを跨いで色が固定される。
    ///
    /// 素の <c>id % Count</c> でも分布は一様だが、連番IDだと「隣の学校が必ず隣の配色」になり、
    /// 大会表で規則性が見えてしまう。整数ミックス（splitmix32 の finalizer）を挟んで散らす。
    /// string.GetHashCode は .NET Core でプロセスごとにランダム化されるため<b>使ってはいけない</b>。
    /// </summary>
    public TeamColorEntry EntryFor(int schoolId)
    {
        unchecked
        {
            uint x = (uint)schoolId + 0x9E3779B9u;
            x = (x ^ (x >> 16)) * 0x85EBCA6Bu;
            x = (x ^ (x >> 13)) * 0xC2B2AE35u;
            x ^= x >> 16;
            return Entries[(int)(x % (uint)Entries.Count)];
        }
    }

    /// <summary>校IDから6領域の確定配色を得る（<see cref="EntryFor"/> の結果を展開しただけ）。</summary>
    public TeamColors ColorsFor(int schoolId) => TeamColors.FromEntry(EntryFor(schoolId));

    public TeamColorEntry? FindById(string id)
    {
        foreach (var e in Entries) if (e.Id == id) return e;
        return null;
    }

    /// <summary>
    /// YAML が読めない環境用の最小フォールバック。<b>本番の正は data/team-colors.yaml</b> で、
    /// ここに来た時点で配色の種類が激減する（呼び元は必ず警告を出すこと）。
    /// </summary>
    public static TeamColorPalette Fallback { get; } = new(new[]
    {
        Entry("white_navy", "白＋紺", "#EDEBE0", "#293356"),
        Entry("white_crimson", "白＋えんじ", "#EDEBE0", "#853032"),
        Entry("white_darkgreen", "白＋深緑", "#EDEBE0", "#1F4A34"),
        Entry("cream_black", "クリーム＋黒", "#E8E2CE", "#23242A"),
    });

    private static TeamColorEntry Entry(string id, string label, string bas, string team)
    {
        RgbColor.TryParseHex(bas, out var b);
        RgbColor.TryParseHex(team, out var t);
        return new TeamColorEntry(id, label, b, t, t, TeamStripe.None);
    }
}
