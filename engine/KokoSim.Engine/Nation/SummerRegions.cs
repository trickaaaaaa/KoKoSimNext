using System.Collections.Generic;
using System.Linq;

namespace KokoSim.Engine.Nation;

/// <summary>
/// 夏の地方大会の1区画（設計書05 §1.1: 49地方=北海道2・東京2, OPEN-QUESTIONS Q5 / issue #65 決定A-b）。
/// 都道府県モデル（47, <see cref="Prefecture"/>）自体は変えず、夏の予選単位だけ北海道・東京を2つに割る
/// 仮想区分。秋季・センバツ選考など他の大会フローは従来どおり47都道府県のまま
/// （<c>single_prefecture</c>, data/pref-formats/regional-tournaments.yaml）で、この型とは無関係。
/// </summary>
public sealed record SummerRegion(int PrefectureId, string Name, int? Split)
{
    /// <summary>学校がこの区画に属するか。Split未設定の区画は県内全校。分割県は、北海道で支部（DistrictId）を
    /// 持つ学校なら支部から導いた南北（自校の <see cref="SummerRegions.RegionFor"/> と同じ規則・issue #1217）、
    /// それ以外は Id 偶奇で2分割（GroupSplit.cs の地理未設定フォールバックと同じ規約。地理的な意味は持たない機械的な等分）。</summary>
    public bool Contains(School school)
        => school.PrefectureId == PrefectureId
           && (Split is not { } s || SummerRegions.SplitOf(school) == s);
}

/// <summary>49の夏の地方大会区画を組み立てる（設計書05 §1.1）。</summary>
public static class SummerRegions
{
    // JIS順Id（data/prefectures.yaml）。
    /// <summary>北海道の県ID（夏の準決勝/決勝の北海道ボールパーク＝<see cref="Match.Field.RegionalStadiumTier"/>。北北海道・南北海道とも同じ県ID）。</summary>
    internal const int HokkaidoId = 0;
    /// <summary>宮城県の県ID（夏の準決勝/決勝の宮城球場＝<see cref="Match.Field.RegionalStadiumTier"/>）。</summary>
    internal const int MiyagiId = 3;
    /// <summary>広島県の県ID（夏の準決勝/決勝の広島市民球場＝<see cref="Match.Field.RegionalStadiumTier"/>）。</summary>
    internal const int HiroshimaId = 33;
    /// <summary>東京都の県ID（夏の東西分割・準決勝/決勝の神宮＝<see cref="Match.Field.RegionalStadiumTier"/>）。</summary>
    internal const int TokyoId = 12;
    /// <summary>神奈川県の県ID（夏の準決勝/決勝の横浜スタジアム＝<see cref="Match.Field.RegionalStadiumTier"/>）。</summary>
    internal const int KanagawaId = 13;

    // Split順(0,1)の区画名。唯一のソース＝Build()とSplitDistrictNames()の両方がこれを見る。
    private static readonly string[] HokkaidoSplitNames = { "北北海道", "南北海道" };
    private static readonly string[] TokyoSplitNames = { "東東京", "西東京" };

    /// <summary>
    /// 北海道の県内地区（DistrictId 0-9）＝秋季支部予選の10支部。夏の区画（Split・北北海道=0/南北海道=1）は
    /// この支部からの多対1写像で求める（支部が正・南北はそこから導出、design-05 §2.2）。
    /// <c>data/pref-formats/hokkaido.yaml</c> の <c>districts:</c> と<b>同じ並び順</b>（0=札幌..9=釧根）に
    /// 保つこと＝ズレるとゲーム内の支部名と夏の南北判定が食い違う。
    /// 出典: 秋季北海道高等学校野球大会・全国高等学校野球選手権北北海道大会(Wikipedia)。
    /// </summary>
    private static readonly (string Name, int Split)[] HokkaidoBranches =
    {
        ("札幌", 1), ("旭川", 0), ("函館", 1), ("室蘭", 1), ("小樽", 1),
        ("空知", 0), ("名寄", 0), ("北見", 0), ("十勝", 0), ("釧根", 0),
    };

    public static IReadOnlyList<SummerRegion> Build(IReadOnlyList<Prefecture> prefectures)
    {
        var regions = new List<SummerRegion>(49);
        foreach (var pref in prefectures)
        {
            if (pref.Id == HokkaidoId)
            {
                regions.Add(new SummerRegion(pref.Id, HokkaidoSplitNames[0], 0));
                regions.Add(new SummerRegion(pref.Id, HokkaidoSplitNames[1], 1));
            }
            else if (pref.Id == TokyoId)
            {
                regions.Add(new SummerRegion(pref.Id, TokyoSplitNames[0], 0));
                regions.Add(new SummerRegion(pref.Id, TokyoSplitNames[1], 1));
            }
            else
            {
                regions.Add(new SummerRegion(pref.Id, pref.Name, null));
            }
        }
        return regions;
    }

    /// <summary>
    /// 新設校スタート（issue #585系）の県内地区選択向け：夏だけ2分割され、かつ秋季大会側に地区データを
    /// 持たない県の、Split順(0,1)に対応する地区名。東京だけが該当する（分割されない45県・北海道は空）。
    /// <c>data/pref-formats/</c>（<see cref="Tournaments.PrefFormatDistrictParser"/>）とは別経路＝
    /// あちらは秋季大会フォーマットの実行にも使われるため、夏だけ分割される東京をそちらに混ぜると
    /// 秋季（分割されない・単一都大会）の挙動を壊してしまう（design-05 §1.5, §1.1a）。
    /// 北海道は逆に秋季側（<c>data/pref-formats/hokkaido.yaml</c>・10支部）が正で、新規開始画面の
    /// 地区選択もそちらから出る（<see cref="HokkaidoBranches"/> 参照）。
    /// </summary>
    public static IReadOnlyList<string> SplitDistrictNames(int prefectureId) => prefectureId switch
    {
        TokyoId => TokyoSplitNames,
        _ => System.Array.Empty<string>(),
    };

    // 新規開始画面で南北を選んだときに自校へ付ける支部（DistrictId）。南北それぞれで最大の支部＝
    // 北北海道は旭川(1)・南北海道は札幌(0)。支部が正で南北は導出なので、選んだ南北の区画（RegionFor）と必ず一致する。
    private static readonly int[] HokkaidoNewGameBranch = { 1, 0 };

    /// <summary>
    /// 新規開始画面の地区選択欄に出す名前（夏の区画名）。夏に2分割される東京（東東京/西東京）と北海道
    /// （北北海道/南北海道）だけが2択を返し、他の県は空。<see cref="SplitDistrictNames"/> と違い北海道も含む＝
    /// 北海道の10支部は秋季の概念で、最初の選択では見せない（issue #1334）。
    /// </summary>
    public static IReadOnlyList<string> NewGameSplitNames(int prefectureId) => prefectureId switch
    {
        HokkaidoId => HokkaidoSplitNames,
        TokyoId => TokyoSplitNames,
        _ => System.Array.Empty<string>(),
    };

    /// <summary>
    /// 新規開始画面で選んだ夏の区画（<see cref="NewGameSplitNames"/> の添字）→自校の DistrictId。
    /// 東京は添字そのもの（<see cref="RegionFor"/> が偶奇で読む）。北海道は選んだ南北の中の代表支部
    /// （北北海道=旭川・南北海道=札幌）＝秋季の支部予選は <c>data/pref-formats/hokkaido.yaml</c> の支部番号のまま動く。
    /// </summary>
    public static int NewGameDistrictId(int prefectureId, int split)
        => prefectureId == HokkaidoId ? HokkaidoNewGameBranch[split] : split;

    /// <summary>この区画の参加校（Nation.InPrefecture を区画の Contains でさらに絞る）。</summary>
    public static IEnumerable<School> Entrants(Nation nation, SummerRegion region)
        => nation.InPrefecture(region.PrefectureId).Where(region.Contains);

    /// <summary>
    /// 学校 Id の偶奇に頼れない主体（自校＝専用ID。<see cref="SummerRegion.Contains"/> がどの区画にも
    /// 一致しない）が属する区画を、県内地区 <c>DistrictId</c> から決める（issue 北海道・東京分割）。
    /// 地区は新チーム発足時に地理固定割で付与済み（設計書05 §2.2）＝この選び方も学校のIdとは無関係に
    /// セーブをまたいで安定する。地区未設定（null）は0区画目（北北海道/東東京）に倒す。
    /// 非分割県は該当する1区画をそのまま返す。
    ///
    /// <para>北海道は <see cref="HokkaidoBranches"/>（10支部）のルックアップで南北を求める
    /// （支部が正・南北は多対1写像。範囲外の DistrictId も0番目＝北北海道側に倒す）。東京は
    /// 秋季側に地区データを持たないため、従来どおり DistrictId の偶奇で決める。</para>
    /// </summary>
    public static SummerRegion RegionFor(IReadOnlyList<Prefecture> prefectures, int prefectureId, int? districtId)
    {
        var matches = Build(prefectures).Where(r => r.PrefectureId == prefectureId).ToList();
        if (matches.Count == 1) return matches[0];

        var split = prefectureId == HokkaidoId
            ? HokkaidoSplitFor(districtId)
            : ((districtId ?? 0) % 2 + 2) % 2;
        return matches.First(r => r.Split == split);
    }

    /// <summary>分割県の学校の Split（0/1）。北海道で支部が範囲内なら支部から、それ以外は Id 偶奇
    /// （自校の負Idは偶奇のどちらにも一致しない＝-1を返す）。</summary>
    internal static int SplitOf(School school)
    {
        if (school.PrefectureId == HokkaidoId && school.DistrictId is { } d && d >= 0 && d < HokkaidoBranches.Length)
            return HokkaidoBranches[d].Split;
        return school.Id % 2 is var m && (m == 0 || m == 1) ? m : -1;
    }

    /// <summary>北海道の DistrictId（支部番号）→夏の区画（Split）。範囲外・未設定は0（北北海道）。</summary>
    private static int HokkaidoSplitFor(int? districtId)
        => districtId is { } d && d >= 0 && d < HokkaidoBranches.Length ? HokkaidoBranches[d].Split : 0;

    /// <summary>
    /// 北海道の支部名（DistrictId順、"札幌".."釧根"の10件）。新規開始画面の地区選択表示用
    /// （Unity側の DistrictPlanProvider がこれを北海道のラベルに使う）。
    /// <c>data/pref-formats/hokkaido.yaml</c> の <c>districts:</c> と1:1で対応するので、
    /// 神奈川・兵庫のような「YAMLのdistricts名は運用ブロック数と食い違う」問題が無い＝実名を出してよい。
    /// </summary>
    public static IReadOnlyList<string> HokkaidoBranchNames { get; } = HokkaidoBranches.Select(b => b.Name).ToList();

    /// <summary>
    /// その学校が属する夏の区画名（"北北海道" / "神奈川" 等）。甲子園の出場校一覧が
    /// 「どこの代表か」を出すために引く（設計書05 §1.1）。
    ///
    /// <para>どの区画にも属さない学校（自校＝専用IDで <see cref="Nation"/> に居ない、
    /// 分割県で Id の偶奇が合わない等）は県名へ落とす＝呼び出し側で分岐させない。
    /// 乱数を使わない純関数。</para>
    /// </summary>
    public static string NameOf(IReadOnlyList<Prefecture> prefectures, School school)
    {
        foreach (var region in Build(prefectures))
            if (region.Contains(school)) return region.Name;

        foreach (var pref in prefectures)
            if (pref.Id == school.PrefectureId) return pref.Name;
        return "";
    }
}
