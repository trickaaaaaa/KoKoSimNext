using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 大会展望の文を部品から組む（issue #784）。語彙は <see cref="PreviewPhrases"/>（data/preview-phrases.yaml）、
/// ここは「どの置き場から何番目を採るか」の規則だけを持つ。
///
/// <para><b>被らせない仕組み</b>: 文は3つの部品（置き場A×B×C）の組み合わせ。同じ置き場の組を使う学校・選手に、
/// 大会の中で<b>別々の通し番号 k</b> を振り、k を組み合わせ番号へ写す。写像は (k×P＋塩) mod M
/// （M＝3つの本数の積、P は M と互いに素）なので、k が M 未満なら別々の k は必ず別々の組み合わせになる。
/// 置き場の組が違う2校は、どれかの部品が別の置き場から来るので文が一致しない。</para>
///
/// <para><b>決定論</b>: 乱数は使わない。塩は置き場の組と年度から FNV-1a で作る（<c>string.GetHashCode</c> は
/// 実行ごとに変わるので使わない）。同じ出場校・同じ年なら同じ文。年度が変われば同じ学校でも文が変わる。</para>
/// </summary>
internal static class PreviewWriter
{
    // M（多くても数千）と互いに素な大きな素数。
    private const ulong Stride = 1_000_003UL;

    // ===== 学校の寸評 =====

    /// <summary>
    /// 出場校全体の寸評（書き出し＋特徴＋締め）。通し番号は「置き場の組」ごとに戦力順で振る
    /// ＝1校だけ欲しいとき（<see cref="TournamentPreviewBuilder.BuildRosterFor"/>）も全体を数えれば同じ文になる。
    /// 材料は格付け・順位・3軸・校風だけ（全出場校ぶん軽く出せる）。
    /// </summary>
    public static Dictionary<int, string> SchoolBlurbs(
        IReadOnlyList<School> ranked, IReadOnlyDictionary<int, TeamRating> ratings,
        IReadOnlyDictionary<int, ContenderMark> marks, PreviewStage stage, int yearIndex, PreviewPhrases phrases)
    {
        var result = new Dictionary<int, string>(ranked.Count);
        var counters = new Dictionary<string, int>();
        for (var i = 0; i < ranked.Count; i++)
        {
            var s = ranked[i];
            var r = ratings[s.Id];
            var band = Band(marks[s.Id], i + 1, ranked.Count);
            var league = stage == PreviewStage.DistrictLeague && band is "fav" or "cont" or "dark";
            var strength = band is "fav" or "cont" or "dark" or "upper" ? ".strong" : ".weak";
            var openKey = league ? "school.open.league." + band : "school.open." + band;
            var profile = Profile(r);
            // 「凹凸が無い」は強い学校なら穴が無い、弱い学校ならどれもまだ武器でない＝意味が逆なので強弱で分ける
            var bodyKey = "school.body." + profile + (profile == "balanced" ? strength : "");
            var closeKey = "school.close." + StyleKey(s.Style) + strength;

            var group = openKey + "|" + bodyKey + "|" + closeKey;
            var k = Next(counters, group);
            // ◎○▲ は展望の上段に数校だけ並ぶ＝同じ印どうしは書き出しから変える（対抗2校が同じ一文で始まらない）。
            // 書き出しが違えば文全体も違うので、被らない保証はそのまま。
            var text = band is "fav" or "cont" or "dark"
                ? Pick1(phrases.Pool(openKey), Next(counters, openKey), Salt(openKey, yearIndex))
                  + Compose(phrases, k, Salt(group, yearIndex), bodyKey, closeKey)
                : Compose(phrases, k, Salt(group, yearIndex), openKey, bodyKey, closeKey);
            result[s.Id] = Fill(text, ("{field}", ranked.Count.ToString()), ("{seed}", (i + 1).ToString()),
                ("{strong}", AxisName(r, strongest: true)), ("{weak}", AxisName(r, strongest: false)));
        }
        return result;
    }

    /// <summary>
    /// 書き出しの置き場。◎○▲はそのまま、無印は出場校の中の順位で4段に分ける
    /// （上位25%＝upper／55%まで＝middle／80%まで＝lower／それ以下＝bottom）。
    /// </summary>
    internal static string Band(ContenderMark mark, int seed, int fieldSize)
    {
        switch (mark)
        {
            case ContenderMark.Favorite: return "fav";
            case ContenderMark.Contender: return "cont";
            case ContenderMark.DarkHorse: return "dark";
        }
        var p = fieldSize <= 1 ? 0.0 : (seed - 1) / (double)(fieldSize - 1);
        return p < 0.25 ? "upper" : p < 0.55 ? "middle" : p < 0.80 ? "lower" : "bottom";
    }

    /// <summary>
    /// 3軸の形。差が4以内なら balanced、最上位の抜け方が最下位の落ち方以上なら spike（突出）、
    /// そうでなければ hole（弱点）。その学校の中での比較だけで決める。
    /// </summary>
    internal static string Profile(TeamRating r)
    {
        var axes = new[] { ("bat", r.Batting), ("pitch", r.Pitching), ("def", r.Defense) }
            .OrderByDescending(a => a.Item2).ToArray();
        var (top, mid, low) = (axes[0], axes[1], axes[2]);
        if (top.Item2 - low.Item2 <= 4) return "balanced";
        return top.Item2 - mid.Item2 >= mid.Item2 - low.Item2 ? "spike." + top.Item1 : "hole." + low.Item1;
    }

    internal static string StyleKey(SchoolStyle s) => s switch
    {
        SchoolStyle.SmallBall => "smallball",
        SchoolStyle.PowerHitting => "power",
        SchoolStyle.DefensiveMinded => "defense",
        SchoolStyle.TotalBaseball => "total",
        SchoolStyle.AceDependent => "ace",
        _ => "standard",
    };

    private static string AxisName(TeamRating r, bool strongest)
    {
        var pairs = new (string Name, int V)[] { ("打線", r.Batting), ("投手陣", r.Pitching), ("守備", r.Defense) };
        return (strongest ? pairs.OrderByDescending(p => p.V) : pairs.OrderBy(p => p.V)).First().Name;
    }

    // ===== 注目選手 =====

    /// <summary>注目選手の特徴（紹介文の置き場の名前）と、その値。</summary>
    internal readonly record struct Pick(Player Player, string Trait, double Score, double Level, bool Ace);

    // 能力の目盛りは軸ごとにずれている（同じ学校の最高値でもミートは長打より15ほど高く出る＝実測 2026-11-08）。
    // 水準の判定は差し引いた値で行い、「県内屈指」と書く基準を軸ごとに揃える。
    private const double ContactOffset = 15, SpeedOffset = 4, ArmOffset = 12, EyeOffset = 10, GloveOffset = 9;

    /// <summary>
    /// その学校で一番光っている選手を選ぶ。投手力で採る学校（3軸の投手が打力を上回る）はエース（先発）。
    /// それ以外は打者の6つの特徴（長打・ミート・走力・肩・選球眼・守備）とエースの投球から、
    /// 「水準（軸のずれを差し引いた値）＋チーム内での抜け具合×0.5」が最も高い1人・1特徴を採る。
    /// 同じ特徴の中では値が高い選手ほど点が高い＝採った選手はその特徴でチーム1番（「チーム随一」が嘘にならない）。
    /// 守備位置・投手適性は見ない（投手としての紹介はエースだけ）。隠し能力も見ない。
    /// </summary>
    public static Pick ChooseNotable(Match.Game.Team team, bool pitcherLed)
    {
        var ace = team.UsesDh ? team.StartingPitcher! : team.BattingOrder[team.PitcherSlot];
        var acePick = AcePick(ace);
        if (pitcherLed) return acePick;

        var members = team.BattingOrder.Concat(team.Reserves)
            .Concat(team.StartingPitcher is { } sp ? new[] { sp } : System.Array.Empty<Player>())
            .Distinct().ToList();
        var traits = new (string Trait, System.Func<Player, int> Value, double Offset)[]
        {
            ("power", p => p.Power, 0), ("contact", p => p.Contact, ContactOffset), ("speed", p => p.Speed, SpeedOffset),
            ("arm", p => p.ArmStrength, ArmOffset), ("eye", p => p.Discipline, EyeOffset), ("glove", p => p.Fielding, GloveOffset),
        };
        var best = acePick;
        foreach (var (trait, value, offset) in traits)
        {
            var mean = members.Average(value);
            // 同点は背番号の若い選手（並びを固定して決定論にする）
            foreach (var p in members.OrderBy(m => m.UniformNumber))
            {
                var v = value(p);
                var level = v - offset;
                var score = level + 0.5 * (v - mean);
                if (score > best.Score) best = new Pick(p, trait, score, level, Ace: false);
            }
        }
        return best;
    }

    /// <summary>
    /// エースの特徴は球質タイプで決め、水準はその型の看板の値（本格派＝球速、技巧派＝制球…）で測る。
    /// 看板の値からは<b>その型の上乗せを差し引く</b>（issue #908）: 上乗せは球速 +10 に対し制球 +3・+2 と軸ごとに
    /// 大きさが違い、素の値で打者と比べると本格派のエースばかり勝っていた（実測: 注目選手に選ばれたエースの91%が本格派）。
    /// 差し引いた値＝その投手の地力の軸で、打者の特徴の水準（軸ごとのずれを差し引いた値）と同じ物差しになる。
    /// </summary>
    private static Pick AcePick(Player ace, PitcherArchetypeCoefficients? archetypes = null)
    {
        var ac = archetypes ?? DefaultArchetypes;
        var a = ace.Pitching;
        var vel = PitcherAttributes.LevelFromVelocityKmh(a.MaxVelocityKmh);
        var type = a.Archetype ?? PitcherArchetypes.Infer(vel, a.Control, a.PitchRank);
        // 推定した型（生成時の型を持たない投手）は上乗せを受けていないので差し引かない。
        var (dv, dc, _, dr) = a.Archetype is { } born ? PitcherArchetypes.Offsets(born, ac) : (0.0, 0.0, 0.0, 0.0);
        var (trait, level) = type switch
        {
            PitcherArchetype.Power => ("ace.power", vel - dv),
            PitcherArchetype.Finesse => ("ace.finesse", a.Control - dc),
            PitcherArchetype.SoftToss => ("ace.soft", (a.Control - dc + a.PitchRank - dr) / 2.0),
            _ => ("ace.balanced", (vel - dv + a.Control - dc + a.PitchRank - dr) / 3.0),
        };
        return new Pick(ace, trait, level, level, Ace: true);
    }

    private static readonly PitcherArchetypeCoefficients DefaultArchetypes = new();

    /// <summary>
    /// 水準。star は「{area}屈指」と書いてよい選手。基準は舞台が広いほど上げる
    /// （県80／地区84／全国88。強さ75前後の学校の看板が県で star になる＝実測 2026-11-08）。
    /// </summary>
    internal static string LevelOf(double level, PreviewStage stage)
    {
        var star = Area(stage) switch { "全国" => 88, "地区" => 84, _ => 80 };
        return level >= star ? "star" : level >= 62 ? "solid" : "modest";
    }

    /// <summary>「{area}屈指」の {area}。県の大会は県内、秋の地区大会は地区、甲子園・神宮は全国。</summary>
    internal static string Area(PreviewStage stage) => stage switch
    {
        PreviewStage.Regional => "地区",
        PreviewStage.SummerKoshien or PreviewStage.SenbatsuKoshien or PreviewStage.Jingu => "全国",
        _ => "県内",
    };

    /// <summary>
    /// 注目選手の紹介文（見出し＋描写＋結び）。通し番号は出場校の中の戦力順（seed−1）＝チームを組まずに
    /// 全校ぶんを数える必要が無い。置き場の組が同じなら、組み合わせ数（いまは320）までの大会で被らない。
    /// </summary>
    public static string PlayerBlurb(Pick pick, int seed, PreviewStage stage, int yearIndex, PreviewPhrases phrases)
    {
        var headKey = "player.head." + pick.Trait + "." + LevelOf(pick.Level, stage);
        var bodyKey = "player.body." + pick.Trait;
        var tailKey = "player.tail.g" + System.Math.Clamp(pick.Player.Grade, 1, 3);
        var group = headKey + "|" + bodyKey + "|" + tailKey;
        var text = Compose(phrases, seed - 1, Salt(group, yearIndex), headKey, bodyKey, tailKey);
        return Fill(text, ("{area}", Area(stage)));
    }

    /// <summary>1校詳細の寸評に挟む導入（「注目は2年の山田。」）。氏名の全角空白は詰める。</summary>
    public static string Intro(Pick pick, int seed, int yearIndex, PreviewPhrases phrases)
    {
        var text = Pick1(phrases.Pool("player.intro"), seed - 1, Salt("player.intro", yearIndex));
        return Fill(text, ("{grade}", pick.Player.Grade.ToString()),
            ("{name}", pick.Player.Name.Replace("　", "").Replace(" ", "")));
    }

    // ===== 組み立ての共通部 =====

    private static int Next(Dictionary<string, int> counters, string key)
    {
        counters.TryGetValue(key, out var k);
        counters[key] = k + 1;
        return k;
    }

    private static string Pick1(IReadOnlyList<string> pool, int k, ulong salt)
        => pool[(int)(((ulong)k * Stride + salt) % (ulong)pool.Count)];

    /// <summary>2部品版（◎○▲の特徴＋締め）。</summary>
    private static string Compose(PreviewPhrases phrases, int k, ulong salt, string keyA, string keyB)
    {
        var a = phrases.Pool(keyA);
        var b = phrases.Pool(keyB);
        var idx = ((ulong)k * Stride + salt) % ((ulong)a.Count * (ulong)b.Count);
        return a[(int)(idx % (ulong)a.Count)] + b[(int)(idx / (ulong)a.Count)];
    }

    private static string Compose(PreviewPhrases phrases, int k, ulong salt, string keyA, string keyB, string keyC)
    {
        var a = phrases.Pool(keyA);
        var b = phrases.Pool(keyB);
        var c = phrases.Pool(keyC);
        var m = (ulong)a.Count * (ulong)b.Count * (ulong)c.Count;
        var idx = ((ulong)k * Stride + salt) % m;
        var ia = (int)(idx % (ulong)a.Count);
        var rest = idx / (ulong)a.Count;
        var ib = (int)(rest % (ulong)b.Count);
        var ic = (int)(rest / (ulong)b.Count);
        return a[ia] + b[ib] + c[ic];
    }

    /// <summary>置き場の組×年度の塩（FNV-1a 64bit）。</summary>
    private static ulong Salt(string group, int yearIndex)
    {
        var h = 14695981039346656037UL;
        foreach (var ch in group) { h ^= ch; h *= 1099511628211UL; }
        h ^= (ulong)(uint)yearIndex;
        h *= 1099511628211UL;
        return h % 1_000_000_007UL;
    }

    private static string Fill(string text, params (string Key, string Value)[] pairs)
    {
        foreach (var (key, value) in pairs) text = text.Replace(key, value);
        return text;
    }
}
