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

/// <summary>
/// 注目選手1人（設計書06 §3.5b「注目選手ピックアップ」）。
/// 氏名・背番号・学年・投打・最速球速は<b>実際の対戦相手ラインナップと同一の真値</b>
/// （StrengthTeamFactory.ForSchool 由来）。StatLine の成績値だけは能力から導いた読み物用の見込み値。
/// </summary>
public sealed record NotablePlayer(
    string SchoolName, int UniformNumber, string Name, int Grade, string PositionLabel,
    string HandednessLabel, bool IsPitcher, string StatLine, string Blurb);

/// <summary>登録メンバー1人（背番号・氏名・守備位置・学年・投打まで。詳細指標は出さない＝設計§3.5b）。</summary>
public sealed record RosterMember(
    int UniformNumber, string Name, string PositionLabel, int Grade, string HandednessLabel);

/// <summary>出場校1校の登録メンバー（ベンチ入り20人）。</summary>
public sealed record RosterExcerpt(
    string SchoolName, Tier Tier, string SeedLabel, string TeamBlurb, IReadOnlyList<RosterMember> Members);

/// <summary>大会プレビュー（設計書06 §3.5b, mock-tournament-preview.html）。自動生成の読み物データ。</summary>
public sealed record TournamentPreview(
    string Title, string Meta, string Lead, IReadOnlyList<PreviewContender> Contenders,
    IReadOnlyList<NotablePlayer> NotablePlayers, IReadOnlyList<RosterExcerpt> Rosters);

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
        string title, IReadOnlyList<School> entrants, int berths, string nextStageName, int yearIndex = 1)
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

        // 注目選手・登録メンバーは格付け校ぶんだけ実チーム（＝実戦と同一ソース）から組む。
        // 全出場校を先食い生成しない（出場200校規模でもコストは格付け校ぶんに収まる）。
        var byName = ranked.ToDictionary(s => s.Name, s => s);
        var notables = new List<NotablePlayer>();
        var rosters = new List<RosterExcerpt>();
        foreach (var c in contenders)
        {
            if (!byName.TryGetValue(c.Name, out var school)) continue;
            var team = StrengthTeamFactory.ForSchool(school, yearIndex);
            notables.Add(PickNotable(c, team));
            rosters.Add(BuildRoster(c, team));
        }

        return new TournamentPreview(title, meta, lead, contenders, notables, rosters);
    }

    // ===== 注目選手・登録メンバー（材料は実戦と同一の ForSchool 出力） =====

    /// <summary>
    /// 1校の看板選手を選ぶ。投手力が打力を上回る校はエース、そうでなければ主砲。
    /// 表示中の3軸バーと選出理由が一致するので「なぜこの選手か」が読み手に伝わる。
    /// </summary>
    private static NotablePlayer PickNotable(PreviewContender c, Match.Game.Team team)
    {
        // 同値は打者側に寄せる（投手ばかりが並ぶのを避け、投打の顔ぶれが混ざるようにする）。
        var pitcherLed = c.Rating.Pitching > c.Rating.Batting;
        var p = pitcherLed
            // 投手はエース（先発）を採る。実際に対戦するのはこの投手なので「展望で見た投手と当たる」が成立する。
            // 誰を看板にするかは球速では決めない（型と成績の見せ方は StatLine/Blurb が担う）。
            ? team.BattingOrder[team.PitcherSlot]
            : team.BattingOrder.Where((_, i) => i != team.PitcherSlot)
                  .OrderByDescending(x => x.Power * 2 + x.Contact).First();

        return new NotablePlayer(
            SchoolName: c.Name,
            UniformNumber: p.UniformNumber,
            Name: p.Name,
            Grade: p.Grade,
            PositionLabel: PositionLabel(p.Position),
            HandednessLabel: HandednessLabel(p.Throws, p.Bats),
            IsPitcher: pitcherLed,
            StatLine: pitcherLed ? PitcherStatLine(p) : BatterStatLine(p),
            Blurb: pitcherLed ? PitcherBlurb(p) : BatterBlurb(p));
    }

    private static RosterExcerpt BuildRoster(PreviewContender c, Match.Game.Team team)
    {
        var members = team.BattingOrder.Concat(team.Bullpen).Concat(team.Bench)
            .OrderBy(p => p.UniformNumber)
            .Select(p => new RosterMember(
                p.UniformNumber, p.Name, PositionLabel(p.Position), p.Grade, HandednessLabel(p.Throws, p.Bats)))
            .ToList();
        var seedLabel = c.Mark == ContenderMark.DarkHorse ? "ノーシード" : $"第{c.Seed}シード";
        return new RosterExcerpt(c.Name, c.Tier, seedLabel, c.Blurb, members);
    }

    private static string PositionLabel(Match.Field.FieldPosition pos) => pos switch
    {
        Match.Field.FieldPosition.Pitcher => "投",
        Match.Field.FieldPosition.Catcher => "捕",
        Match.Field.FieldPosition.FirstBase => "一",
        Match.Field.FieldPosition.SecondBase => "二",
        Match.Field.FieldPosition.ThirdBase => "三",
        Match.Field.FieldPosition.Shortstop => "遊",
        Match.Field.FieldPosition.LeftField => "左",
        Match.Field.FieldPosition.CenterField => "中",
        Match.Field.FieldPosition.RightField => "右",
        _ => "―",
    };

    private static string HandednessLabel(Players.Handedness throws, Players.Handedness bats)
        => $"{(throws == Players.Handedness.Left ? "左" : "右")}投{bats switch
        {
            Players.Handedness.Left => "左",
            Players.Handedness.Switch => "両",
            _ => "右",
        }}打";

    // --- 成績の見込み値（能力からの決定論導出＝読み物用の合成。裏試合は成績が付かないため実測値は存在しない） ---

    /// <summary>球質タイプ。生成時に確定した型があればそれを使い、無ければ能力から推定する。</summary>
    private static Players.PitcherArchetype ArchetypeOf(Players.Player p)
    {
        var a = p.Pitching!;
        return a.Archetype ?? Players.PitcherArchetypes.Infer(
            Players.PitcherAttributes.LevelFromVelocityKmh(a.MaxVelocityKmh), a.Control, a.PitchRank);
    }

    private static string PitcherStatLine(Players.Player p)
    {
        var a = p.Pitching!;
        var skill = (a.Control + a.PitchRank) / 2.0;
        // 上位校のエースが防御率1点台に収まる傾斜（県大会の実感に合わせる）。skill85→約0.9 / 70→約2.4 / 50→4.4。
        var era = System.Math.Round(MathUtil.Clamp(4.40 - (skill - 50) * 0.10, 0.35, 6.50), 2);
        var k = (int)System.Math.Round(MathUtil.Clamp(12 + (a.PitchRank - 40) * 0.55, 6, 60));
        // 球質タイプを先に置き、球速は「特徴のひとつ」として併記する（球速を主役にしない）。
        var type = Players.PitcherArchetypes.Label(ArchetypeOf(p));
        return $"{type} ／ 最速{(int)System.Math.Round(a.MaxVelocityKmh)}km/h ／ 今大会 防御率{era:0.00}・{k}奪三振";
    }

    private static string BatterStatLine(Players.Player p)
    {
        var avg = MathUtil.Clamp(0.230 + (p.Contact - 50) * 0.0055, 0.180, 0.520);
        var hr = (int)System.Math.Round(MathUtil.Clamp((p.Power - 55) * 0.14, 0, 9));
        var avgText = avg.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture).TrimStart('0');
        return hr > 0 ? $"今大会 打率{avgText}・{hr}本" : $"今大会 打率{avgText}・{p.Steal / 12}盗塁";
    }

    /// <summary>球質タイプで語り口を分ける（球速の閾値では分けない＝技巧派・軟投派も主役になれる）。</summary>
    private static string PitcherBlurb(Players.Player p)
    {
        // 夏/秋どちらの大会でも使える言い回しにする（大会種別に依存させない）。
        var grade = p.Grade < 3 ? $"まだ{p.Grade}年、伸びしろも十分。" : "最終学年、集大成の大会になる。";
        return ArchetypeOf(p) switch
        {
            Players.PitcherArchetype.Power =>
                $"県内屈指の本格派。角度のある直球で押し込み、空振りを量産する。{grade}",
            Players.PitcherArchetype.Finesse =>
                $"球速こそ派手ではないが制球は県内随一。四球で崩れず、変化球を散らして打たせて取る。{grade}",
            Players.PitcherArchetype.SoftToss =>
                $"速球に頼らない軟投派。緩急と抜群の制球で打者のタイミングを外し続ける。{grade}",
            _ =>
                $"突出した武器はないが穴もない。試合を作る力があり、大崩れしない安定感が持ち味。{grade}",
        };
    }

    private static string BatterBlurb(Players.Player p)
    {
        var grade = p.Grade < 3 ? $"{p.Grade}年生ながら中軸を担う。" : "最終学年の主軸。";
        if (p.Power >= 75)
            return $"県屈指のスラッガー。打球初速は野手最速クラスで、一発は文句なしの飛距離。{grade}";
        if (p.Speed >= 72)
            return $"出塁すれば必ず次の塁を狙う積極走塁で、相手バッテリーに重圧をかける快足。{grade}";
        return $"確実性の高いミートで走者を還す。勝負強さが光る打者。{grade}";
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
