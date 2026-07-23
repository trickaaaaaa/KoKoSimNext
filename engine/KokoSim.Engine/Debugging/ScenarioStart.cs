namespace KokoSim.Engine.Debugging;

/// <summary>
/// 場面ジャンプの開始局面（設計書17 §3.4, F2）。「9回裏2死満塁・1点ビハインド・3-2」のような状況を
/// 宣言的に指定して、そこから試合を始める。
///
/// <para><b>注入で作った試合は digest・統計集計の対象外</b>（開始状態が baseline と違うため）。
/// トレースヘッダに <c>scenario:"&lt;id&gt;"</c> が刻まれ、<see cref="Match.Game.GameResult.ScenarioId"/> にも載る。</para>
///
/// <para>塁上の走者は「直前の打者たち」を置く（<c>TeamState.PreviousBatter</c>）。タイブレークの
/// 継続打者と同じ流儀で、走者に誰を置くかを別途宣言せずに済ませるための割り切り。</para>
/// </summary>
public sealed record ScenarioStart
{
    /// <summary>開始イニング（1始まり）。</summary>
    public int Inning { get; init; } = 1;

    /// <summary>表（先攻の攻撃）から始めるか。</summary>
    public bool IsTop { get; init; } = true;

    /// <summary>開始時のアウト数（0-2）。</summary>
    public int Outs { get; init; }

    public bool OnFirst { get; init; }
    public bool OnSecond { get; init; }
    public bool OnThird { get; init; }

    /// <summary>先頭打者のボールカウント（0-3）。</summary>
    public int Balls { get; init; }
    /// <summary>先頭打者のストライクカウント（0-2）。</summary>
    public int Strikes { get; init; }

    public int AwayScore { get; init; }
    public int HomeScore { get; init; }

    /// <summary>開始時の打順（1-9）。攻撃側に適用する。</summary>
    public int BatterOrder { get; init; } = 1;

    /// <summary>守備側投手の当日の累計球数（疲労・継投判断の入力）。</summary>
    public int PitcherFatiguePitches { get; init; }

    /// <summary>宣言が矛盾していないか（範囲外は黙って直さず投げる＝シナリオYAMLの誤りを早期に殺す）。</summary>
    public void Validate()
    {
        Require(Inning >= 1, "inning は1以上");
        Require(Outs is >= 0 and <= 2, "outs は0〜2");
        Require(Balls is >= 0 and <= 3, "count.balls は0〜3");
        Require(Strikes is >= 0 and <= 2, "count.strikes は0〜2");
        Require(BatterOrder is >= 1 and <= 9, "batter は1〜9");
        Require(AwayScore >= 0 && HomeScore >= 0, "score は0以上");
        Require(PitcherFatiguePitches >= 0, "pitcher_fatigue は0以上");
    }

    private static void Require(bool ok, string what)
    {
        if (!ok) throw new System.ArgumentException($"シナリオの指定が不正です: {what}");
    }
}
