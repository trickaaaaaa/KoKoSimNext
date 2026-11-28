namespace KokoSim.Engine.Season;

/// <summary>
/// 公式戦に勝った後のマネージャーの会話を、どちらで出すか決める（issue #1688）。
/// 大喜び（<see cref="WinBigEventId"/>）は「格上（総合力が自校以上）に勝った／決勝に勝った／甲子園で勝った」ときだけ。
/// それ以外（格下に順当勝ち）はあっさり（<see cref="WinEventId"/>）。接戦かどうかでは分けない。
/// 総合力は画面に出す点数（四捨五入・<see cref="Nation.Tiers.DisplayPoints"/>）どうしで比べる＝総合ランクの見た目と食い違わない。
/// 乱数も時刻も使わない純関数。
/// </summary>
public static class PostgameTalk
{
    public const string WinEventId = "postgame_win";
    public const string WinBigEventId = "postgame_win_big";
    public const string LossEventId = "postgame_loss";
    /// <summary>勝ちの会話の直後に続く「今日のMVP」の会話（issue #1745）。公式戦に勝ったときだけ。</summary>
    public const string MvpEventId = "postgame_mvp";

    /// <summary>勝ちの会話のイベントID。相手の点数が自校以上なら格上＝大喜び。</summary>
    public static string WinEventFor(
        int ownDisplayedPoints, int opponentDisplayedPoints, bool wonFinal, TournamentKind kind)
    {
        var upset = opponentDisplayedPoints >= ownDisplayedPoints;
        var atKoshien = kind is TournamentKind.SummerKoshien or TournamentKind.SenbatsuKoshien;
        return upset || wonFinal || atKoshien ? WinBigEventId : WinEventId;
    }
}
