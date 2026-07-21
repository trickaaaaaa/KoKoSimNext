using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 自校の一戦だけを詳細試合エンジン（GameEngine）で解決する Shell 実装（設計書05 §1.4）。
    /// 自校ロスター＋スタメン（GameSession.Lineup）を持つのはこの層なのでここに置く。相手校は
    /// DevelopingPlayer を持たないため学校から生成（StrengthTeamFactory.ForSchool）。
    /// 自校＝後攻(home)固定（OPEN-Q #6）。
    /// 渡される rng は TournamentRunner が本流から Fork した隔離ストリーム（本流の乱数列に影響しない）。
    /// スタメン未設定時は自動編成（RosterTeamBuilder.Build）へフォールバックする。
    /// </summary>
    public sealed class PlayerMatchResolver : IPlayerMatchResolver
    {
        public PlayerMatchDetail Resolve(School manager, School opponent, IRandomSource rng)
        {
            var mgrTeam = BuildManagerTeam(manager.Name);
            var oppTeam = BuildOpponentTeam(opponent);
            var ctx = new GameContext();
            // 自校＝後攻(home)。away=相手, home=自校。
            var result = GameEngine.Play(oppTeam, mgrTeam, ctx, rng.Fork(2));
            return new PlayerMatchDetail(result, ManagerIsAway: false);
        }

        public PlayerMatchLive BeginLive(School manager, School opponent, IRandomSource rng)
        {
            // Resolve と同一の teams＋同一Fork で組む（相手＝校ID固定生成, rng.Fork(2)=試合）。
            // 唯一の差は CaptureTimelines=true（観戦用タイムライン）。これは RNG 中立なので、
            // 全打席を進めた結果は Resolve のボックススコアと一致する＝観戦しても大会結果は変わらない。
            var mgrTeam = BuildManagerTeam(manager.Name);
            var oppTeam = BuildOpponentTeam(opponent);
            var ctx = new GameContext { CaptureTimelines = true };
            var prog = new MatchProgression(oppTeam, mgrTeam, ctx, rng.Fork(2));
            return new PlayerMatchLive(prog, ManagerIsAway: false);
        }

        /// <summary>DH判定に使う現代ルール（現状トグルなし・年代連動の既定値, 設計書05 §1.3）。</summary>
        private static readonly ModernRules Rules = new();

        /// <summary>
        /// 相手校ラインナップを「校ID＋年度」から決定論生成する。大会展望が同じ入口で同じチームを引くので、
        /// 展望で見た注目選手が実際の対戦相手としてそのまま出てくる（設計書06 §3.5b）。
        /// 渡された試合 rng には依存させない（＝大会の進行状況や観戦の有無で相手が変わらない）。
        /// Resolve / BeginLive の双方がこの1メソッドを使うことで、両者が同一チームになる契約を守る。
        /// 敵AI采配（設計書11）を注入し、代打・代走・守備固め・サイン・伝令を校の三層プロファイルに
        /// 応じて運用させる（Issue #40）。ブレイン自体は rng を消費しない＝チーム生成の決定論は不変。
        /// 打順の能力ベース編成＋DH使用判断（issue #54）は StrengthTeamFactory 側（校ID＋年度の決定論）
        /// で完結済み。ここではその上に調子（Condition）ベースの並べ替え（issue #48）だけを重ねる。
        /// 判断可否の乱数は校ID＋年度から Fork した専用ストリーム（試合 rng とは独立）＝この並べ替えも
        /// 校ID＋年度だけで決定論的に決まる。
        /// </summary>
        public static Team BuildOpponentTeam(School opponent)
        {
            var yearIndex = GameSession.Current.Year;
            var calendarYear = SeasonClock.SeasonBaseYear + (yearIndex - 1);
            var team = StrengthTeamFactory.ForSchool(opponent, yearIndex, modernRules: Rules, calendarYear: calendarYear);
            var brain = EnemyAiFactory.BrainFor(opponent);
            var orderRng = StrengthTeamFactory.SeedFor(opponent.Id, yearIndex).Fork(0x0BDE_0000UL);
            var reordered = brain.ComposeBattingOrder(team.BattingOrder, orderRng);
            // DhSlot は並べ替え前のインデックス基準なので、DH使用時は同一参照を辿って引き直す
            // （調子の並べ替えは常に恒等だが、将来 Q21 が解決して相手校にも調子差が付いたときの保険）。
            var dhSlot = team.UsesDh ? IndexOfReference(reordered, team.BattingOrder[team.DhSlot]) : team.DhSlot;
            return team with { BattingOrder = reordered, DhSlot = dhSlot, Tactics = brain };
        }

        private static int IndexOfReference(IReadOnlyList<Player> list, Player target)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (ReferenceEquals(list[i], target)) return i;
            }
            return -1;
        }

        /// <summary>
        /// 自校ラインナップを組む。試合開始前画面（対戦カード）も同じ入口を通し、
        /// 表示したスタメンと実際に出場するスタメンが必ず一致する契約を守る。
        /// </summary>
        public static Team BuildManagerTeam(string name)
        {
            var lineup = GameSession.Current.Lineup;
            if (lineup != null) return RosterTeamBuilder.BuildFromLineup(lineup);
            // 未設定＝スタメン画面を通らなかった場合は能力順の自動編成（従来同等の編成）。
            return RosterTeamBuilder.Build(RosterService.Active, name);
        }
    }
}
