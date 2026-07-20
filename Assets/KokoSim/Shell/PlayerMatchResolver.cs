using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 自校の一戦だけを詳細試合エンジン（GameEngine）で解決する Shell 実装（設計書05 §1.4）。
    /// 自校ロスター＋スタメン（GameSession.Lineup）を持つのはこの層なのでここに置く。相手校は
    /// DevelopingPlayer を持たないため総合力から生成（StrengthTeamFactory）。自校＝後攻(home)固定（OPEN-Q #6）。
    /// 渡される rng は TournamentRunner が本流から Fork した隔離ストリーム（本流の乱数列に影響しない）。
    /// スタメン未設定時は自動編成（RosterTeamBuilder.Build）へフォールバックする。
    /// </summary>
    public sealed class PlayerMatchResolver : IPlayerMatchResolver
    {
        public PlayerMatchDetail Resolve(School manager, School opponent, IRandomSource rng)
        {
            var mgrTeam = BuildManagerTeam(manager.Name);
            var oppTeam = StrengthTeamFactory.Create(opponent.Strength, opponent.Name, rng.Fork(1));
            var ctx = new GameContext();
            // 自校＝後攻(home)。away=相手, home=自校。
            var result = GameEngine.Play(oppTeam, mgrTeam, ctx, rng.Fork(2));
            return new PlayerMatchDetail(result, ManagerIsAway: false);
        }

        public PlayerMatchLive BeginLive(School manager, School opponent, IRandomSource rng)
        {
            // Resolve と同一の teams＋同一Fork で組む（rng.Fork(1)=相手生成, rng.Fork(2)=試合）。
            // 唯一の差は CaptureTimelines=true（観戦用タイムライン）。これは RNG 中立なので、
            // 全打席を進めた結果は Resolve のボックススコアと一致する＝観戦しても大会結果は変わらない。
            var mgrTeam = BuildManagerTeam(manager.Name);
            var oppTeam = StrengthTeamFactory.Create(opponent.Strength, opponent.Name, rng.Fork(1));
            var ctx = new GameContext { CaptureTimelines = true };
            var prog = new MatchProgression(oppTeam, mgrTeam, ctx, rng.Fork(2));
            return new PlayerMatchLive(prog, ManagerIsAway: false);
        }

        private static Team BuildManagerTeam(string name)
        {
            var lineup = GameSession.Current.Lineup;
            if (lineup != null) return RosterTeamBuilder.BuildFromLineup(lineup);
            // 未設定＝スタメン画面を通らなかった場合は能力順の自動編成（従来同等の編成）。
            return RosterTeamBuilder.Build(RosterService.Roster, name);
        }
    }
}
