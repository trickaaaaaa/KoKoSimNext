using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Tactics;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>優勝争いの格付けマーク（設計書06 §3.5b）。◎優勝候補 / ○対抗 / ▲ダークホース / 無印。</summary>
public enum ContenderMark { Favorite, Contender, DarkHorse, None }

/// <summary>チームの3軸レーティング（表示用0〜99。打力/投手/守備）。</summary>
public sealed record TeamRating(int Batting, int Pitching, int Defense)
{
    public int Overall => (int)System.Math.Round((Batting + Pitching + Defense) / 3.0);
}

/// <summary>優勝争いに名を連ねる1校（格付け・シード・3軸・寸評）。</summary>
public sealed record PreviewContender(
    int Seed, ContenderMark Mark, string Name, Tier Tier, TeamRating Rating, string Blurb);

/// <summary>大会プレビュー（設計書06 §3.5b, mock-tournament-preview.html）。自動生成の読み物データ。</summary>
public sealed record TournamentPreview(
    string Title, string Meta, string Lead, IReadOnlyList<PreviewContender> Contenders);

/// <summary>
/// 出場校（強さ＋校風）から大会プレビューを自動生成する（設計書06 §3.5b）。
/// 3軸は強さ＋校風プロファイル＋校ごとのFork分散で合成し、上位を格付けして寸評・リードを組む。
/// 純データ生成（UnityEngine非依存）。UIはこのモデルを描画するだけ。決定論（同じ出場校→同じプレビュー）。
/// 注: 3軸・寸評はプレビュー表示用の合成であり、試合シミュレーションのバランス係数ではない。
/// </summary>
public static class TournamentPreviewBuilder
{
    // 校風プロファイル（打力, 投手, 守備）の表示オフセット。校風で戦力の"色"が変わる（設計書11 §3）。
    private static (int B, int P, int D) StyleOffset(SchoolStyle s) => s switch
    {
        SchoolStyle.SmallBall => (2, -3, 4),        // 機動力: 守走で1点、投打は控えめ
        SchoolStyle.PowerHitting => (9, -5, -3),    // 強打待球: 打線が売り、投手に不安
        SchoolStyle.DefensiveMinded => (-6, 5, 9),  // 守り勝つ: 守備・投手が厚く打力は薄い
        SchoolStyle.TotalBaseball => (2, 2, 2),     // 全員野球: 総合的に少し上振れ（層）
        SchoolStyle.AceDependent => (-5, 11, -3),   // 豪腕依存: エースの投手力に一極集中
        _ => (0, 0, 0),                             // 型なし
    };

    public static TournamentPreview Build(
        string title, IReadOnlyList<School> entrants, int berths, string nextStageName)
    {
        if (entrants.Count == 0) throw new System.ArgumentException("出場校が空です。");

        // シード＝強さ順（Knockout と同じ序列）。同強さは Id で安定化。
        var ranked = entrants.OrderByDescending(s => s.Strength).ThenBy(s => s.Id).ToList();
        var ratings = ranked.ToDictionary(s => s.Id, Rate);

        var contenders = new List<PreviewContender>();
        var darkHorse = PickDarkHorse(ranked, ratings, berths);

        for (var i = 0; i < ranked.Count; i++)
        {
            var s = ranked[i];
            var mark = i == 0 ? ContenderMark.Favorite
                : i <= 2 ? ContenderMark.Contender
                : ReferenceEquals(s, darkHorse) ? ContenderMark.DarkHorse
                : ContenderMark.None;
            if (mark == ContenderMark.None) continue;

            var rating = ratings[s.Id];
            contenders.Add(new PreviewContender(
                Seed: i + 1, Mark: mark, Name: s.Name, Tier: s.Tier, Rating: rating,
                Blurb: Blurb(mark, rating, s.Style)));
        }
        // ◎○○▲ の順（マーク→シード）。
        contenders = contenders
            .OrderBy(c => (int)c.Mark).ThenBy(c => c.Seed).ToList();

        var meta = $"出場 {entrants.Count}校 ／ 上位{berths}校が{nextStageName}へ ─ 自動生成プレビュー";
        var lead = BuildLead(contenders, entrants.Count, berths, nextStageName);
        return new TournamentPreview(title, meta, lead, contenders);
    }

    private static TeamRating Rate(School s)
    {
        var (ob, op, od) = StyleOffset(s.Style);
        // 校ごとの個体差（Fork＝強さ・校風の生成列と独立、同校なら決定論）。
        var rng = new Xoshiro256Random(0xB17E_0000UL ^ (ulong)s.Id);
        int Axis(int off, int salt)
            => (int)MathUtil.Clamp(
                System.Math.Round(s.Strength + off + rng.Fork((ulong)salt).NextGaussian(0, 3.5)), 25, 99);
        return new TeamRating(Axis(ob, 1), Axis(op, 2), Axis(od, 3));
    }

    // ダークホース＝上位シード外で、単軸が突出 or 伏兵型校風の最上位校（設計書06 §3.5b「侮れない伏兵」）。
    private static School? PickDarkHorse(
        IReadOnlyList<School> ranked, IReadOnlyDictionary<int, TeamRating> ratings, int berths)
    {
        var from = System.Math.Max(3, berths);          // ◎○○より下から探す
        var to = System.Math.Min(ranked.Count, from + 8);
        School? best = null;
        var bestScore = int.MinValue;
        for (var i = from; i < to; i++)
        {
            var s = ranked[i];
            var r = ratings[s.Id];
            var peak = System.Math.Max(r.Batting, System.Math.Max(r.Pitching, r.Defense));
            var styleBonus = s.Style is SchoolStyle.SmallBall or SchoolStyle.DefensiveMinded ? 6 : 0;
            var score = peak + styleBonus - i; // 上位ほど有利、単軸ピーク＋伏兵型を加点
            if (score > bestScore) { bestScore = score; best = s; }
        }
        return best;
    }

    private static string AxisName(TeamRating r, bool strongest)
    {
        var pairs = new (string Name, int V)[] { ("打線", r.Batting), ("投手陣", r.Pitching), ("守備", r.Defense) };
        var ordered = strongest
            ? pairs.OrderByDescending(p => p.V).ToList()
            : pairs.OrderBy(p => p.V).ToList();
        return ordered[0].Name;
    }

    private static string StyleFlavor(SchoolStyle s) => s switch
    {
        SchoolStyle.SmallBall => "機動力を絡めた野球",
        SchoolStyle.DefensiveMinded => "堅い守りと粘り",
        SchoolStyle.PowerHitting => "一発のある打線",
        SchoolStyle.AceDependent => "エースの存在感",
        SchoolStyle.TotalBaseball => "選手層の厚み",
        _ => "勝負強さ",
    };

    private static string Blurb(ContenderMark mark, TeamRating r, SchoolStyle style)
    {
        var strong = AxisName(r, strongest: true);
        var weak = AxisName(r, strongest: false);
        return mark switch
        {
            ContenderMark.Favorite =>
                $"死角の少ない優勝候補。{strong}を軸に、撃ち勝つ試合も守り勝つ試合もできる。",
            ContenderMark.Contender =>
                $"{strong}が武器の実力校。{weak}に不安を残すが、噛み合えば頂点も見えてくる。",
            ContenderMark.DarkHorse =>
                $"戦力値では一歩譲るが、{StyleFlavor(style)}で上位を脅かす伏兵。短期決戦では侮れない。",
            _ => "",
        };
    }

    private static string BuildLead(
        IReadOnlyList<PreviewContender> contenders, int entrantCount, int berths, string nextStageName)
    {
        var fav = contenders.FirstOrDefault(c => c.Mark == ContenderMark.Favorite);
        var conts = contenders.Where(c => c.Mark == ContenderMark.Contender).ToList();
        var dark = contenders.FirstOrDefault(c => c.Mark == ContenderMark.DarkHorse);

        var sb = new System.Text.StringBuilder();
        sb.Append("新チーム始動の秋、まだ誰も完成していない。");
        if (fav is not null) sb.Append($"そんな中で頭ひとつ抜けるのは{fav.Name}。");
        if (conts.Count >= 2) sb.Append($"対するは{conts[0].Name}と{conts[1].Name}。");
        else if (conts.Count == 1) sb.Append($"対抗は{conts[0].Name}。");
        if (dark is not null) sb.Append($"伏兵・{dark.Name}も不気味だ。");
        sb.Append($"上位{berths}校に与えられる{nextStageName}への切符を、{entrantCount}校が争う。");
        return sb.ToString();
    }
}
