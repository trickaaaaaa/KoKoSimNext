using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Stats;

/// <summary>個人守備の累積成績（複数試合の集計）。現状は失策数のみ（issue #91）。</summary>
public sealed class FieldingStatLine
{
    public int Errors { get; private set; }

    /// <summary>1試合ぶんを畳み込む。</summary>
    public void Add(FieldingLine l) => Errors += l.Errors;

    /// <summary>集計済みの別ラインを足し込む（スコープ間の複写用。<see cref="BattingStatLine.Merge"/> と対）。</summary>
    public void Merge(FieldingStatLine o) => Errors += o.Errors;

    /// <summary>セーブ復元専用（issue #507・<see cref="Saves.StatsSaves"/> だけが使う）。</summary>
    internal void Restore(int errors) => Errors = errors;
}
