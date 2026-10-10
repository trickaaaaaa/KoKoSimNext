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
/// 縞の型（issue #621）。<b>出すかどうかは持たない</b>＝縞の有無は校IDの軸
/// （<see cref="TeamColorPalette.ColorsFor"/>）が決める。表に書くのは「その校が縞を出すときの
/// 色と密度」だけで、書かなければ既定（チーム色・40本/m）になる。
/// </summary>
/// <param name="Color">縞の色。</param>
/// <param name="FreqPerMeter">横1mあたりの縞の本数。</param>
public sealed record TeamStripeStyle(RgbColor Color, double FreqPerMeter)
{
    /// <summary>既定の密度（実物のピンストライプは間隔2.5cm前後＝40本/m）。</summary>
    public const double DefaultFreqPerMeter = 40.0;

    /// <summary>表に指定が無いときの型＝チーム色の縞。</summary>
    public static TeamStripeStyle DefaultFor(RgbColor team) => new(team, DefaultFreqPerMeter);
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
/// <param name="Stripe">縞の型（null＝既定。<b>縞を出すかどうかではない</b>・issue #621）。</param>
public sealed record TeamColorEntry(
    string Id, string Label, RgbColor Base, RgbColor Team, RgbColor Visor, TeamStripeStyle? Stripe = null);

/// <summary>
/// 配色の軸（issue #621）。パレット表の1件とは<b>独立に校IDから</b>決まる型で、表を厚くする
/// （＝件数を増やす #620）のとは別に掛け算で効く。出現率は実物準拠で偏らせる＝等分にしない。
/// </summary>
/// <param name="StripeRate">縦縞を出す割合。</param>
/// <param name="VisorRate">つばを地色にする割合（表がつばの色を明示している件には効かない）。</param>
/// <param name="WhiteUnderRate">アンダーシャツ・ソックスを地色にする割合。</param>
public sealed record TeamColorAxes(
    double StripeRate = 0.20,
    double VisorRate = 0.30,
    double WhiteUnderRate = 0.20)
{
    public static readonly TeamColorAxes Default = new();
}

/// <summary>
/// 描画へ渡す確定配色。パレット表から導出するほか、プレイヤーが領域ごとに上書きした結果もこの形で持つ
/// （＝表に無い配色も表現できる。自校の色はプレイヤーが決める・2026-07-29 監督決定）。
/// 肌・ベルトは配色対象外（固定色）なので持たない。
/// </summary>
public sealed record TeamColors(
    RgbColor Shirt, RgbColor Pants, RgbColor Undershirt, RgbColor Socks,
    RgbColor CapCrown, RgbColor CapVisor, RgbColor Shoes, TeamStripe Stripe)
{
    /// <summary>
    /// スパイクの選択肢＝白・黒の二択（実物の高校野球はこの2色。2026-07-29 監督決定）。
    /// 学校ごとの型ではないのでパレット表（team-colors.yaml の palettes）には持たせず、
    /// 先頭を既定にしてプレイヤーの選択だけで上書きする。値は color_names に載っている
    /// 「黒」「白」と**同じ16進**にしてある＝画面に色名がそのまま出る。
    /// </summary>
    public static readonly IReadOnlyList<RgbColor> ShoeChoices = new[]
    {
        new RgbColor(0x23, 0x24, 0x2A),   // 黒
        new RgbColor(0xED, 0xEB, 0xE0),   // 白
    };

    /// <summary>スパイクの既定色（黒）。</summary>
    public static RgbColor DefaultShoes => ShoeChoices[0];

    /// <summary>
    /// パレット表1件 → 7領域。導出規則は data/team-colors.yaml の先頭コメントと対。
    /// <b>縞は付かない</b>（issue #621）＝縞・つば・白アンダーは校IDの軸で決まるので、
    /// 校の見た目が要るときは <see cref="TeamColorPalette.ColorsFor"/> を使う。
    /// </summary>
    public static TeamColors FromEntry(TeamColorEntry e) => new(
        Shirt: e.Base, Pants: e.Base,
        Undershirt: e.Team, Socks: e.Team,
        CapCrown: e.Team, CapVisor: e.Visor,
        Shoes: DefaultShoes,
        Stripe: TeamStripe.None);
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

    /// <summary>配色の軸（issue #621）。表とは独立に校IDから決まる型の出現率。</summary>
    public TeamColorAxes Axes { get; }

    /// <summary>色の和名（表の color_names 由来）。未定義なら16進を返す＝画面に空欄が出ない。</summary>
    public string NameOf(RgbColor color)
        => _names.TryGetValue(color, out var n) ? n : color.ToHex();

    public TeamColorPalette(IReadOnlyList<TeamColorEntry> entries,
        IReadOnlyDictionary<RgbColor, string>? names = null,
        TeamColorAxes? axes = null)
    {
        if (entries == null || entries.Count == 0)
            throw new ArgumentException("パレット表が空", nameof(entries));
        Entries = entries;
        _names = names ?? new Dictionary<RgbColor, string>();
        Axes = axes ?? TeamColorAxes.Default;

        var swatches = new List<RgbColor>();
        var seen = new HashSet<RgbColor>();
        void Add(RgbColor c) { if (seen.Add(c)) swatches.Add(c); }
        foreach (var e in entries) Add(e.Base);
        foreach (var e in entries) { Add(e.Team); Add(e.Visor); }
        foreach (var e in entries) if (e.Stripe is { } s) Add(s.Color);
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

    /// <summary>
    /// 校IDから確定配色を得る。<see cref="EntryFor"/> で表の1件を選び、そこへ
    /// <b>表とは独立の軸</b>（縦縞・つば・白アンダー）を重ねる（issue #621）。
    ///
    /// 軸は表の件数に依らない混合から引く＝#620 のように表を増減しても軸の出方は変わらない。
    /// 36件×2×2×2 のように<b>掛け算</b>で効くので、表を厚くするより安く見た目の幅が増える。
    /// </summary>
    public TeamColors ColorsFor(int schoolId)
    {
        var e = EntryFor(schoolId);
        var c = TeamColors.FromEntry(e);

        // つば: 表がつばの色を明示している件（＝すでにつば別色の型）には重ねない。
        if (e.Visor.Equals(e.Team) && Roll(schoolId, VisorSalt) < Axes.VisorRate)
            c = c with { CapVisor = e.Base };

        // 白アンダー: アンダーシャツとソックスを地色にする（チーム色が帽子だけに残る型）。
        if (Roll(schoolId, WhiteUnderSalt) < Axes.WhiteUnderRate)
            c = c with { Undershirt = e.Base, Socks = e.Base };

        // 縦縞: 出すかどうかは軸、色と密度は表の型（無ければチーム色・40本/m）。
        if (Roll(schoolId, StripeSalt) < Axes.StripeRate)
        {
            var s = e.Stripe ?? TeamStripeStyle.DefaultFor(e.Team);
            c = c with { Stripe = new TeamStripe(true, s.Color, s.FreqPerMeter) };
        }

        return c;
    }

    // 軸ごとに別の種を混ぜる（同じ校でも軸ごとに独立に出る）。EntryFor とも別の混合定数を使う
    // ＝「表で選ばれた配色」と「軸」が相関しない（例: 紺の校だけ縞になる、が起きない）。
    private const uint VisorSalt = 0x5652_1D01u;
    private const uint WhiteUnderSalt = 0x5755_1D02u;
    private const uint StripeSalt = 0x5354_1D03u;

    /// <summary>校ID＋軸の種 → [0,1) の決定論値（乱数源を使わない・string.GetHashCode 禁止）。</summary>
    private static double Roll(int schoolId, uint salt)
    {
        unchecked
        {
            // splitmix32 の finalizer（EntryFor とは別の定数列＝独立に散る）。
            uint x = (uint)schoolId + salt;
            x = (x ^ (x >> 16)) * 0x7FEB352Du;
            x = (x ^ (x >> 15)) * 0x846CA68Bu;
            x ^= x >> 16;
            return x / 4294967296.0;
        }
    }

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
        return new TeamColorEntry(id, label, b, t, t);
    }
}
