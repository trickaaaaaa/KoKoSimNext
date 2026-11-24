namespace KokoSim.Engine.Match.Field;

/// <summary>守備位置の日本語表記の単一ソース（実況・テロップ・HUD・レポートが引く）。未定義の値は表記ごとの既定を返す。</summary>
public static class FieldPositionNames
{
    /// <summary>カタカナ呼称（実況・場内放送）。野手9つ＋指名打者、それ以外は空。</summary>
    public static string Katakana(FieldPosition p) => p switch
    {
        FieldPosition.Pitcher => "ピッチャー",
        FieldPosition.Catcher => "キャッチャー",
        FieldPosition.FirstBase => "ファースト",
        FieldPosition.SecondBase => "セカンド",
        FieldPosition.ThirdBase => "サード",
        FieldPosition.Shortstop => "ショート",
        FieldPosition.LeftField => "レフト",
        FieldPosition.CenterField => "センター",
        FieldPosition.RightField => "ライト",
        FieldPosition.DesignatedHitter => "指名打者",
        _ => "",
    };

    /// <summary>2文字表記（投手・一塁・遊撃・中堅…、DHは「指名」）。それ以外は空。</summary>
    public static string Kanji2(FieldPosition p) => p switch
    {
        FieldPosition.Pitcher => "投手",
        FieldPosition.Catcher => "捕手",
        FieldPosition.FirstBase => "一塁",
        FieldPosition.SecondBase => "二塁",
        FieldPosition.ThirdBase => "三塁",
        FieldPosition.Shortstop => "遊撃",
        FieldPosition.LeftField => "左翼",
        FieldPosition.CenterField => "中堅",
        FieldPosition.RightField => "右翼",
        FieldPosition.DesignatedHitter => "指名",
        _ => "",
    };

    /// <summary>1文字略記（投捕一二三遊左中右・DHは指）。それ以外は「—」。</summary>
    public static string Abbr(FieldPosition p) => p switch
    {
        FieldPosition.Pitcher => "投",
        FieldPosition.Catcher => "捕",
        FieldPosition.FirstBase => "一",
        FieldPosition.SecondBase => "二",
        FieldPosition.ThirdBase => "三",
        FieldPosition.Shortstop => "遊",
        FieldPosition.LeftField => "左",
        FieldPosition.CenterField => "中",
        FieldPosition.RightField => "右",
        FieldPosition.DesignatedHitter => "指",
        _ => "—",
    };
}
