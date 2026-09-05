using KokoSim.Engine.Core;

namespace KokoSim.Engine.Nation;

/// <summary>全国生成の係数（設計書05 §2, YAML駆動）。</summary>
public sealed record NationCoefficients
{
    /// <summary>チーム力の母集団平均・分散（大半はD〜F, 強豪は稀）。</summary>
    public double StrengthMean { get; init; } = 44.0;
    /// <summary>
    /// 母集団のσ（issue prefecture-high-rank-distribution・2026-09-05）: 13→15。
    /// 13 だと 80 が +2.8σ で全国4000校のAランク以上が 13校（0.33%）しか出ず、校数最多の神奈川でも
    /// 期待 0.66 校・「Aが1校もない年」が 49% だった＝校数と高ランク数の相関 0.99 が
    /// ランク表示（<see cref="Tiers.FromStrength"/>）に出てこない。15 で全国A 37校・神奈川 期待 2.0校。
    /// 県1位vs2位の勝率は 0.649→0.666 とほぼ動かない（σは1位も2位も一緒に持ち上げる）。
    /// 17 は全国S 15.7 で NationTests の S≤15 に触るため 15〜16 が上限。
    /// <b><see cref="ChurnSd"/> と必ずセットで動かすこと</b>（定常σは churn 側が決める）。
    /// </summary>
    public double StrengthSd { get; init; } = 15.0;
    public double StrengthMin { get; init; } = 8.0;
    public double StrengthMax { get; init; } = 99.0;

    /// <summary>古豪/新興の名声初期補正。</summary>
    public double StoriedFameBonus { get; init; } = 20.0;
    /// <summary>古豪の強さボーナス。8 だと県1位−2位の差が最大22ポイントに開き生成時点で一強が確定した（2026-08-18 に 4 へ）。</summary>
    public double StoriedStrengthBonus { get; init; } = 4.0;
    public double EmergingFamePenalty { get; init; } = -5.0;

    /// <summary>集計マッチのロジスティック尺度（強さ差→勝率）。フルシム（実ゲーム経路）へキャリブレーション済み
    /// （2026-08-19・設計書21: 差5→63%／10→≈80%／15→88%／20→95%）。旧 4.0 は圧縮0.55後の差で引いていた＝raw 差なら ≈7.3。</summary>
    public double AggregateScale { get; init; } = 7.2;

    // --- AI校の年次進化（設計書05 §2.3） ---
    /// <summary>
    /// 平均回帰の強さ（暴走防止＝全国統計の安定化）。旧 0.10 では強さ82の校の期待ドリフトが −2/年で覇権が10年落ちず、
    /// 集計20年で最多優勝校の中央値が 9/20 だった（issue prefecture-dominance・2026-08-18 に 0.15 へ）。
    /// </summary>
    public double MeanReversion { get; init; } = 0.15;
    /// <summary>
    /// 世代交代の揺らぎσ（古豪凋落・新興台頭の源泉）。定常sd＝σ/√(1−(1−rev)²)＝σ/0.527 なので、
    /// <b><see cref="StrengthSd"/> と必ずセットで動かす</b>。片方だけ変えると進化が初期σを打ち消し、
    /// 「新規ゲームは正しいが数年で元へ戻る」という気づきにくい壊れ方をする
    /// （実測: sd15・churn6.5 据え置きで 10年後に sd 13.0・全国A 13.5 へ回帰。2026-09-05）。
    /// sd 15 に対して 7.9（=15×0.527）。実測: 10年後 sd 15.3・全国A 44.9。
    /// </summary>
    public double ChurnSd { get; init; } = 7.9;
    /// <summary>大会成績1勝あたりの強さボーナス（成功が補強を呼ぶ）。旧 0.12 は覇者の正帰還が強すぎた。</summary>
    public double StrengthPerWin { get; init; } = 0.06;
    /// <summary>名声→強さの寄与（強豪に人が集まる）。</summary>
    public double FameToStrength { get; init; } = 0.015;
    /// <summary>名声の年次減衰。</summary>
    public double FameDecay { get; init; } = 0.90;
    /// <summary>甲子園優勝の名声獲得。</summary>
    public double FameChampion { get; init; } = 40.0;
    /// <summary>大会1勝あたりの名声。</summary>
    public double FamePerWin { get; init; } = 2.5;
}

/// <summary>都道府県（設計書05: 校数に地域差）。</summary>
public sealed record Prefecture(int Id, string Name, int SchoolCount);

/// <summary>全国（都道府県＋全校）。</summary>
public sealed record Nation(IReadOnlyList<Prefecture> Prefectures, IReadOnlyList<School> Schools)
{
    public IEnumerable<School> InPrefecture(int prefectureId)
    {
        foreach (var s in Schools)
        {
            if (s.PrefectureId == prefectureId) yield return s;
        }
    }
}

/// <summary>架空4000校の生成（設計書05 §2）。</summary>
public static class NationGenerator
{
    /// <summary>
    /// 47都道府県の校数の相対重み＝日本高等学校野球連盟の硬式加盟校数（2025年度・合計3,768校）。
    /// JIS順で <see cref="PrefectureNames.Japanese"/> と添字が一致する。
    /// 出典: https://www.jhbf.or.jp/data/statistical/koushiki/2025.html
    /// （2023年5月版の別集計とも整合: 東京272/神奈川188/千葉171/茨城102/広島91/鳥取24）。
    /// 旧テーブルは出典のない手置きで、千葉95（実際173）・埼玉110（実際157）と激戦区を大きく取りこぼす一方、
    /// 宮城90（実際65）・熊本90（実際59）を過大に見ていた（issue prefecture-high-rank-distribution・2026-09-05）。
    /// </summary>
    private static readonly int[] PrefectureWeights =
    {
        195,  53,  63,  65,  42,  43,  68,             // 北海道 青森 岩手 宮城 秋田 山形 福島
        102,  61,  68, 157, 173, 270, 188,             // 茨城 栃木 群馬 埼玉 千葉 東京 神奈川
         77,  43,  50,  28,  34,  85,                  // 新潟 富山 石川 福井 山梨 長野
         68, 109, 183,  64,                            // 岐阜 静岡 愛知 三重
         53,  76, 173, 164,  39,  39,                  // 滋賀 京都 大阪 兵庫 奈良 和歌山
         24,  39,  58,  93,  56,                       // 鳥取 島根 岡山 広島 山口
         30,  37,  55,  28,                            // 徳島 香川 愛媛 高知
        136,  36,  55,  59,  44,  47,  74,  64,        // 福岡 佐賀 長崎 熊本 大分 宮崎 鹿児島 沖縄
    };

    /// <summary>
    /// 実数（3,768校）から設計書05 §2 の「架空4000校」規模への相似拡大率。
    /// 県間の比は加盟校数そのままで、総数だけを設計の想定規模に合わせる。
    /// </summary>
    private const double SchoolCountScale = 1.0616;

    /// <summary>
    /// 架空全国を生成する。districtPlan（県Id→県内地区数）を渡すと、地理固定割（district_assignment=geographic）の
    /// 県の学校に県内地区 DistrictId を付与する（設計書05 §2.2 / CHANGELOG 28）。
    /// 付与は独立ストリーム(Fork)＝強さ・名声・校風の生成列を乱さない（既存の全国統計テスト非破壊）。
    /// null/該当なしの県は DistrictId=null のまま（group_split は Id 均等割へフォールバック）。
    /// </summary>
    public static Nation Generate(
        SchoolNameVocab vocab, NationCoefficients coeff, IRandomSource rng,
        IReadOnlyDictionary<int, int>? districtPlan = null)
    {
        var prefectures = new List<Prefecture>(47);
        for (var i = 0; i < 47; i++)
        {
            // 加盟校数（実数・合計3,768）を約4000校へ相似拡大する。
            var count = (int)Math.Round(PrefectureWeights[i] * SchoolCountScale);
            // 県名は実名（JIS順の不変表）。夏の地区名（SummerRegions）は北海道・東京だけ日本語で
            // 分割名を持っていて、それ以外がここの名前をそのまま使う＝「第N県」だと片方だけ実名になる。
            prefectures.Add(new Prefecture(i, PrefectureNames.JapaneseOf(i), count));
        }

        var schools = new List<School>(4000);
        var id = 0;
        foreach (var pref in prefectures)
        {
            var districtCount = districtPlan is not null && districtPlan.TryGetValue(pref.Id, out var dc) ? dc : 0;
            // 校名は県内でユニーク（同一県の大会・プレビューで同名校が並ばない）。県をまたぐ重複は許容（実在同様）。
            var usedNames = new HashSet<string>();
            for (var j = 0; j < pref.SchoolCount; j++)
            {
                schools.Add(CreateSchool(id++, pref.Id, vocab, coeff, rng, usedNames, districtCount));
            }
        }

        return new Nation(prefectures, schools);
    }

    // 実在の慣例に沿う地名接尾（語彙が尽きたときの一意化に使う。〇〇東・〇〇西…）。
    private static readonly string[] NameSuffixes = { "東", "西", "南", "北", "第二", "中央", "工科", "学園" };

    /// <summary>
    /// 県内で一意な校名を返す（乱数不使用＝決定論・帯保護）。重複時は「高校」の前に地名接尾を挿入し、
    /// それでも尽きたら連番で確実に一意化する。used に確定名を登録する。
    /// </summary>
    private static string UniqueName(string baseName, HashSet<string> used)
    {
        if (used.Add(baseName)) return baseName;
        var (stem, tail) = baseName.EndsWith("高校")
            ? (baseName.Substring(0, baseName.Length - 2), "高校")
            : (baseName, "");
        foreach (var sf in NameSuffixes)
        {
            // 語尾重複を避ける（〇〇東＋東=東東 / 私立の学園・学院＋学園 等の不自然な二重接尾）。
            if (stem.EndsWith(sf)) continue;
            if (sf == "学園" && (stem.Contains("学園") || stem.Contains("学院"))) continue;
            var alt = stem + sf + tail;
            if (used.Add(alt)) return alt;
        }
        for (var n = 2; ; n++)
        {
            var alt = stem + "第" + n + tail;
            if (used.Add(alt)) return alt;
        }
    }

    private static School CreateSchool(
        int id, int prefId, SchoolNameVocab vocab, NationCoefficients coeff, IRandomSource rng,
        HashSet<string> usedNames, int districtCount = 0)
    {
        var (baseName, establishment) = SchoolNameGenerator.Generate(vocab, rng, prefId);
        // 県内でユニーク化。名前生成の乱数消費は変えず（帯保護）、重複時のみ決定論的に接尾辞を挿入する。
        var name = UniqueName(baseName, usedNames);
        var tradition = SampleTradition(rng);

        var strength = MathUtil.Clamp(
            rng.NextGaussian(coeff.StrengthMean, coeff.StrengthSd)
                + (tradition == Tradition.Storied ? coeff.StoriedStrengthBonus : 0),
            coeff.StrengthMin, coeff.StrengthMax);

        var fame = MathUtil.Clamp(
            strength * 0.5
                + (tradition == Tradition.Storied ? coeff.StoriedFameBonus : 0)
                + (tradition == Tradition.Emerging ? coeff.EmergingFamePenalty : 0),
            0, 100);

        // 校風・采配能力は独立ストリーム(Fork)で付与し、強さ・名声の生成列を乱さない
        // （既存の全国統計テスト非破壊）。ティアと独立＝「引き出しは多いが凡将」等の掛け合わせが生まれる。
        var styleRng = rng.Fork(0x57A1_0000UL ^ (ulong)id);
        var style = SampleStyle(styleRng);
        var tacticalSense = (int)MathUtil.Clamp(styleRng.NextGaussian(50, 16), 5, 95);

        // 県内地区（地理固定割の県のみ）。独立ストリーム(Fork)で均等抽選＝他の生成列を乱さない。
        int? districtId = districtCount > 0
            ? rng.Fork(0xD157_0000UL ^ (ulong)id).NextInt(0, districtCount)
            : null;

        // 監督傾向（issue #55, 決定3: 傾向なし55% / 1個35% / 2個10%）。校風と別軸で0〜2個重なる。
        // 独立ストリーム(Fork)で付与＝強さ・名声・校風・地区の生成列を1ビットも乱さない（全国統計テスト非破壊）。
        var traits = SampleManagerTraits(rng.Fork(0x77A1_0000UL ^ (ulong)id));

        return new School
        {
            Id = id,
            Name = name,
            PrefectureId = prefId,
            Establishment = establishment,
            Tradition = tradition,
            Strength = strength,
            Fame = fame,
            Style = style,
            TacticalSense = tacticalSense,
            DistrictId = districtId,
            ManagerTraits = traits,
        };
    }

    private static readonly Match.Tactics.ManagerTrait[] AllTraits =
        (Match.Tactics.ManagerTrait[])System.Enum.GetValues(typeof(Match.Tactics.ManagerTrait));

    /// <summary>
    /// 監督傾向の付与（issue #55, 決定3）。傾向なし55% / 1個35% / 2個10%。2個目は1個目と異なり、かつ
    /// 継投で相反する組（エース酷使＋継投早め）を避ける。渡された隔離ストリームだけを消費（決定論）。
    /// </summary>
    private static IReadOnlyList<Match.Tactics.ManagerTrait> SampleManagerTraits(IRandomSource rng)
    {
        var r = rng.NextDouble();
        var count = r < 0.55 ? 0 : r < 0.90 ? 1 : 2;
        if (count == 0) return System.Array.Empty<Match.Tactics.ManagerTrait>();

        var first = AllTraits[rng.NextInt(0, AllTraits.Length)];
        if (count == 1) return new[] { first };

        Match.Tactics.ManagerTrait second;
        do
        {
            second = AllTraits[rng.NextInt(0, AllTraits.Length)];
        }
        while (second == first || IsConflicting(first, second));
        return new[] { first, second };
    }

    /// <summary>エース酷使 ⇔ 継投早め は継投しきい値で相反するため同居させない。</summary>
    private static bool IsConflicting(Match.Tactics.ManagerTrait a, Match.Tactics.ManagerTrait b)
        => (a == Match.Tactics.ManagerTrait.AceOveruse && b == Match.Tactics.ManagerTrait.QuickHook)
           || (a == Match.Tactics.ManagerTrait.QuickHook && b == Match.Tactics.ManagerTrait.AceOveruse);

    /// <summary>校風の付与（設計書11 §3。型なしが最多、他は少数ずつ）。</summary>
    private static Match.Tactics.SchoolStyle SampleStyle(IRandomSource rng)
    {
        var r = rng.NextDouble();
        if (r < 0.40) return Match.Tactics.SchoolStyle.Standard;
        if (r < 0.55) return Match.Tactics.SchoolStyle.SmallBall;
        if (r < 0.68) return Match.Tactics.SchoolStyle.PowerHitting;
        if (r < 0.80) return Match.Tactics.SchoolStyle.DefensiveMinded;
        if (r < 0.90) return Match.Tactics.SchoolStyle.TotalBaseball;
        return Match.Tactics.SchoolStyle.AceDependent;
    }

    private static Tradition SampleTradition(IRandomSource rng)
    {
        var r = rng.NextDouble();
        if (r < 0.12) return Tradition.Storied;   // 古豪 少数
        if (r < 0.62) return Tradition.Midlevel;  // 中堅
        return Tradition.Emerging;                // 新興
    }
}
