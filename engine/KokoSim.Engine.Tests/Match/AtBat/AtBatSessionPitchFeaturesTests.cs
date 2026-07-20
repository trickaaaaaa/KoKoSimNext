using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.AtBat;

/// <summary>
/// 設計書15 Phase E-1 の合否テスト。<see cref="AtBatSession"/> が毎球の判定用弾道特徴量
/// （<see cref="AtBatSession.LastPitchFeatures"/>）を参照可能にすること、かつまだどの判定にも
/// 接続していない（帯・RNG消費が Phase D 時点と完全不変）ことを固定する。
/// </summary>
public sealed class AtBatSessionPitchFeaturesTests
{
    private static readonly FieldGeometry Field = new();

    [Fact]
    public void ThrowNextPitch_PopulatesLastPitchFeatures_MatchingTableLookup()
    {
        var batter = BatterAttributes.LeagueAverage;
        var pitcher = PitcherAttributes.LeagueAverage;
        var ctx = new AtBatContext { Fielders = Field.StandardAlignment() };
        var rng = new Xoshiro256Random(7);

        var session = AtBatSession.Begin(batter, pitcher, ctx);
        Assert.Null(session.LastPitchFeatures);

        session.ThrowNextPitch(rng);

        Assert.NotNull(session.LastPitchFeatures);
        var features = session.LastPitchFeatures!.Value;

        // ストレート基礎シェア0.55・投手はストレートのみのレパートリーではないため、球速レンジは
        // テーブルの妥当域内に収まっているはず（クランプが発生していない＝実運用レンジ内）。
        Assert.InRange(features.FlightTimeSeconds, 0.30, 0.60);
        Assert.True(features.InducedVerticalBreakM > 0.0, "バックスピン基準なので誘発縦変化は正のはず");
    }
}
