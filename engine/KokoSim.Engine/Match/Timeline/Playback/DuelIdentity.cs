using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Timeline.Playback;

/// <summary>選手1名の対決ビュー向け識別（氏名＋背番号）。表示専用（issue #393）。</summary>
public readonly record struct DuelPlayerIdentity(string Name, int Number)
{
    public static DuelPlayerIdentity? Of(Player? p) => p is null ? null : new DuelPlayerIdentity(p.Name, p.UniformNumber);
}

/// <summary>
/// 守備側の守備位置→選手（対決ビューの背番号デカール・頭上フルネームラベル用, issue #393）。
/// 交代で試合中に変わるため <see cref="Timeline.Playback.MatchProgression.CurrentDefenseIdentity"/> がその都度組む
/// （チームカラーと違い <c>LiveMatchRequest</c> の静的経路には載せない, design-18 §3.5.1 と同じ理由）。
/// </summary>
public sealed record DuelFieldIdentity(IReadOnlyDictionary<FieldPosition, DuelPlayerIdentity> Defense)
{
    private static readonly FieldPosition[] Positions =
    {
        FieldPosition.Pitcher, FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
        FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField, FieldPosition.CenterField,
        FieldPosition.RightField,
    };

    /// <summary>純関数（issue #393 完了条件）。乱数不使用・守備側 TeamState の現在の守備位置から組む。</summary>
    public static DuelFieldIdentity Build(TeamState defense)
    {
        var map = new Dictionary<FieldPosition, DuelPlayerIdentity>();
        foreach (var pos in Positions)
        {
            if (DuelPlayerIdentity.Of(defense.PlayerAtPosition(pos)) is { } id) map[pos] = id;
        }
        return new DuelFieldIdentity(map);
    }
}
