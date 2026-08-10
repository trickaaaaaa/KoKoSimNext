namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 消化済みカードの結果1件（樹形図のスコア併記用）。<see cref="TournamentRunnerState"/> の要素。
/// <c>TopWon</c> は上側スロット（添字 2i）が勝ったか。
/// </summary>
public readonly record struct SavedBracketCard(
    int Round, int Slot, bool TopWon, int WinnerScore, int LoserScore, bool MercyEnded);

/// <summary>
/// 大会進行体（<see cref="TournamentRunner"/>）の可変状態のセーブ DTO（issue #565）。
///
/// <para><b>載せるのは「進行して変化したもの」だけ</b>。係数・日程・resolver・自校ID・総ラウンド数は
/// 復元時に呼び出し側が渡し直す（<see cref="TournamentRunner.FromSaved"/>）。学校は
/// <see cref="School.Id"/> で持ち、復元時に <c>field</c> から引き直す＝全国の学校は決定論再生成なので
/// 実体を保存する必要がない（<c>School.cs</c> の設計注記と同じ扱い）。</para>
///
/// <para><b>初期シード配置も保存する</b>（<see cref="RoundEntrants"/> の 0 行目）。生成し直さないのは、
/// シードが <see cref="School.Strength"/> 順で決まる一方、自校の Strength は大会中の実戦成長で動くため
/// ＝復元時に組み直すと<b>樹形図の並びが変わる</b>（実測不要の論理的帰結だが、絵にも数字にも出ない形で
/// ブラケットがずれるので、順序そのものを保存する）。</para>
///
/// <para><b>観戦中（<c>_pending</c> 非null）の状態は載せない</b>。試合の途中で中断して保存する（F2）のは
/// 別の話で、<see cref="TournamentRunner.CanCaptureState"/> が false のときは保存させない
/// （呼び出し側＝Shell の <c>CanSave</c> が先に抑止する）。</para>
/// </summary>
public sealed class TournamentRunnerState
{
    /// <summary>大会名（例「2028年 選手権神奈川大会」）。</summary>
    public string Title { get; set; } = "";

    /// <summary>全国大会（甲子園）か。ラウンド名の付け方が変わる。</summary>
    public bool IsNationalTournament { get; set; }

    // --- 自校の状況 ---
    public bool PlayerActive { get; set; } = true;
    public bool IsChampion { get; set; }
    public string? ChampionName { get; set; }

    /// <summary>消化済みラウンド数（不戦勝も1ラウンド）。次戦の試合日（<c>MatchDay</c>）を決める。</summary>
    public int RoundIndex { get; set; }

    /// <summary>乱数の内部状態（<c>Xoshiro256Random.CaptureState</c>）。シード再生成では消費位置が合わない。</summary>
    public ulong[] RngState { get; set; } = System.Array.Empty<ulong>();

    /// <summary>
    /// ラウンド r（0基点）開始時の各スロットの残存校 Id。行そのものが null＝未到達ラウンド、
    /// 要素が null＝不戦勝の空き枠。
    /// </summary>
    public List<List<int?>?> RoundEntrants { get; set; } = new();

    /// <summary>現在の残存校 Id（次に解決するラウンドの入力）。</summary>
    public List<int?> Current { get; set; } = new();

    /// <summary>消化済みカードの結果（樹形図のスコア併記）。</summary>
    public List<SavedBracketCard> Cards { get; set; } = new();

    /// <summary>試合ログ（通算戦績の集計キー）。</summary>
    public List<BracketMatch> Matches { get; set; } = new();

    /// <summary>投手球数台帳（issue #41）。エース温存判断（issue #42）の入力。</summary>
    public List<TournamentPitchLedger.SavedOuting> PitchLedger { get; set; } = new();
}
