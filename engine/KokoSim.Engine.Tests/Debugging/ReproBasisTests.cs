using System.Collections.Generic;
using System.Text.Json;
using KokoSim.Engine.Core;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Debugging;

/// <summary>
/// 設計書17 F0（再現基盤）の受け入れ:
///  - <see cref="Xoshiro256Random.CaptureState"/>/<see cref="Xoshiro256Random.FromState"/> の往復で乱数列が一致
///    （Box-Muller の予備値を含む＝正規乱数を跨いでも半周ズレない）
///  - Fork注入で構築した <see cref="MatchProgression"/> を Save()→Restore() して digest が一致（穴#2の解消）
///  - 既存セーブ（RngState=null）は従来どおり復元できる（後方互換）
///  - 再現トークンの往復（生成→解釈→復元）で同一場面になる／別ロスターを黙って再生しない
/// </summary>
public sealed class ReproBasisTests
{
    private static Player Pos(FieldPosition pos) => new()
    {
        Position = pos,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

    private static Team Team(string name)
    {
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase), Pos(FieldPosition.SecondBase),
            Pos(FieldPosition.ThirdBase), Pos(FieldPosition.Shortstop), Pos(FieldPosition.LeftField),
            Pos(FieldPosition.CenterField), Pos(FieldPosition.RightField),
            Pos(FieldPosition.Pitcher) with { Name = name + "P", Pitching = PitcherAttributes.LeagueAverage },
        };
        return new Team
        {
            Name = name, BattingOrder = order, PitcherSlot = 8,
            Bullpen = new[] { Pos(FieldPosition.Pitcher) with { Name = name + "R", Pitching = PitcherAttributes.LeagueAverage } },
        };
    }

    // ===== RNG 状態の捕捉と復元 =====

    [Fact]
    public void CaptureState_Restore_ReproducesSubsequentDraws()
    {
        var a = new Xoshiro256Random(12345UL);
        for (var i = 0; i < 37; i++) a.NextDouble();

        var state = a.CaptureState();
        var b = Xoshiro256Random.FromState(state);

        for (var i = 0; i < 200; i++)
        {
            Assert.Equal(a.NextUInt64(), b.NextUInt64());
            Assert.Equal(a.NextDouble(), b.NextDouble());
            Assert.Equal(a.NextInt(0, 1000), b.NextInt(0, 1000));
        }
    }

    /// <summary>
    /// Box-Muller の予備値が捕捉状態に含まれること。奇数回だけ NextGaussian を引いた直後に捕捉すると、
    /// 予備値を落とす実装では復元後の1本目がズレる（この回帰を固定する）。
    /// </summary>
    [Fact]
    public void CaptureState_PreservesPendingGaussianSpare()
    {
        var a = new Xoshiro256Random(777UL);
        a.NextGaussian(); // 1本引くと予備値が1つ残る

        var b = Xoshiro256Random.FromState(a.CaptureState());
        for (var i = 0; i < 50; i++) Assert.Equal(a.NextGaussian(), b.NextGaussian());
    }

    [Fact]
    public void FromState_RejectsBrokenState()
    {
        Assert.Throws<System.ArgumentException>(() => Xoshiro256Random.FromState(new ulong[3]));
        Assert.Throws<System.ArgumentException>(() => Xoshiro256Random.FromState(new ulong[4])); // 全ゼロ
    }

    // ===== Fork注入経路の中断保存（穴#2） =====

    [Theory]
    [InlineData(3UL, 1)]
    [InlineData(7UL, 14)]
    [InlineData(42UL, 25)]
    public void InjectedRng_SaveRestoreContinue_MatchesUninterrupted(ulong seed, int stopAfterPa)
    {
        var full = GameResultDigest.Sha256Of(
            GameEngine.Play(Team("A"), Team("H"), new GameContext(), new Xoshiro256Random(seed)));

        // 大会の隔離Fork と同じ「シードを持たない乱数源」で始める。
        var injected = (IRandomSource)new Xoshiro256Random(seed);
        var prog = new MatchProgression(Team("A"), Team("H"), new GameContext(), injected);
        for (var i = 0; i < stopAfterPa && prog.Advance(); i++) { /* 途中まで進める */ }

        var save = prog.Save();                   // 以前はここで例外だった（穴#2）
        Assert.NotNull(save.RngState);
        var json = JsonSerializer.Serialize(save); // 保存物はそのままシリアライズできる
        var back = JsonSerializer.Deserialize<GameSaveState>(json)!;

        var resumed = GameReplay.Restore(Team("A"), Team("H"), new GameContext(), back);
        while (resumed.Steps.MoveNext()) { /* 続行 */ }

        Assert.Equal(full, GameResultDigest.Sha256Of(GameEngine.BuildResult(resumed.Progress)));
    }

    /// <summary>打席途中（Pitch窓）で中断しても、pitch粒度の位置ごと復元して結果が一致する。</summary>
    // seed はバウンド導入（Issue #63）で rng ストリームが変わり、打席途中2球で止まる局面に到達する
    // 値へ更新（旧 5→2。8打席後の次打席が2球以上続くこと＝mid-PA save が成立する前提を満たす）。
    [Theory]
    [InlineData(2UL)]
    [InlineData(11UL)]
    public void InjectedRng_SaveMidPlateAppearance_MatchesUninterrupted(ulong seed)
    {
        var full = GameResultDigest.Sha256Of(
            GameEngine.Play(Team("A"), Team("H"), new GameContext(), new Xoshiro256Random(seed)));

        var prog = new MatchProgression(Team("A"), Team("H"), new GameContext(),
            (IRandomSource)new Xoshiro256Random(seed));
        for (var i = 0; i < 8 && prog.Advance(); i++) { }
        // 次の打席の途中（2球目の窓）で止める。
        var stops = 0;
        while (stops < 2 && prog.AdvancePitch() == AdvancePitchResult.Pitch) stops++;

        var save = prog.Save();
        Assert.Equal(stops, save.ConfirmedPitchesInCurrentPa);

        var resumed = GameReplay.Restore(Team("A"), Team("H"), new GameContext(), save);
        while (resumed.Steps.MoveNext()) { }

        Assert.Equal(full, GameResultDigest.Sha256Of(GameEngine.BuildResult(resumed.Progress)));
    }

    /// <summary>後方互換: RngState を持たない既存セーブは従来どおりシードから復元される。</summary>
    [Fact]
    public void LegacySave_WithoutRngState_StillRestores()
    {
        var full = GameResultDigest.Sha256Of(
            GameEngine.Play(Team("A"), Team("H"), new GameContext(), new Xoshiro256Random(9UL)));

        var legacyJson = "{\"Seed\":9,\"ConfirmedPlateAppearances\":12,\"Decisions\":[]}";
        var save = JsonSerializer.Deserialize<GameSaveState>(legacyJson)!;
        Assert.Null(save.RngState);
        Assert.Equal(0, save.ConfirmedPitchesInCurrentPa);

        var resumed = GameReplay.Restore(Team("A"), Team("H"), new GameContext(), save);
        while (resumed.Steps.MoveNext()) { }

        Assert.Equal(full, GameResultDigest.Sha256Of(GameEngine.BuildResult(resumed.Progress)));
    }

    // ===== 再現トークン =====

    [Fact]
    public void ReproToken_RoundTripsThroughText()
    {
        var rng = new Xoshiro256Random(2026_07_21UL);
        var fp = ReproToken.Fingerprint(Team("A"), Team("H"), new GameContext());
        var token = new ReproToken(rng.CaptureState(), 37, 4, fp);

        Assert.True(ReproToken.TryParse(token.ToString(), out var back));
        Assert.Equal(token.RngState, back.RngState);
        Assert.Equal(37, back.PlateAppearance);
        Assert.Equal(4, back.Pitch);
        Assert.Equal(fp, back.FixtureFingerprint);
        Assert.True(back.Verify(Team("A"), Team("H"), new GameContext()));
    }

    [Fact]
    public void ReproToken_RestoresTheSameSituation()
    {
        var full = GameResultDigest.Sha256Of(
            GameEngine.Play(Team("A"), Team("H"), new GameContext(), new Xoshiro256Random(31UL)));

        var rng = new Xoshiro256Random(31UL);
        var token = new ReproToken(rng.CaptureState(), 20, 0,
            ReproToken.Fingerprint(Team("A"), Team("H"), new GameContext()));

        Assert.True(ReproToken.TryParse(token.ToString(), out var parsed));
        var resumed = GameReplay.Restore(Team("A"), Team("H"), new GameContext(), parsed.ToSaveState());
        while (resumed.Steps.MoveNext()) { }

        Assert.Equal(full, GameResultDigest.Sha256Of(GameEngine.BuildResult(resumed.Progress)));
    }

    /// <summary>別ロスターは指紋で弾く（黙って違う試合を再生しない・設計書17 §3.3）。</summary>
    [Fact]
    public void ReproToken_DetectsDifferentFixture()
    {
        var token = new ReproToken(new Xoshiro256Random(1UL).CaptureState(), 0, 0,
            ReproToken.Fingerprint(Team("A"), Team("H"), new GameContext()));

        Assert.True(token.Verify(Team("A"), Team("H"), new GameContext()));
        Assert.False(token.Verify(Team("A"), Team("X"), new GameContext()));                       // 別校
        Assert.False(token.Verify(Team("A"), Team("H"), new GameContext { TieBreakEnabled = true })); // 別ルール
    }

    [Theory]
    [InlineData("")]
    [InlineData("k0:0000000000000001:1:0:abcdef12")]  // 旧バージョン
    [InlineData("k1:zz:1:0:abcdef12")]                 // 16進でない
    [InlineData("k1:0000000000000001:1:0:short")]      // 指紋の桁数違い
    public void ReproToken_RejectsMalformedText(string text)
    {
        Assert.False(ReproToken.TryParse(text, out _));
    }
}
