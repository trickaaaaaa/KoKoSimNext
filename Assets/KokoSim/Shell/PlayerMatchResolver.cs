using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 自校の一戦だけを詳細試合エンジン（GameEngine）で解決する Shell 実装（設計書05 §1.4）。
    /// 自校ロスター＋スタメン（GameSession.Lineup）を持つのはこの層なのでここに置く。相手校は
    /// 永続ロスター（#80・<see cref="NationService.Rosters"/>）から組む＝展望・実戦・全国成績が同一ソース。
    /// 自校の先攻/後攻は <see cref="HomeAwayAssignment"/> が対戦の組み合わせ（校ID対＋年度＋週）から
    /// 決定論で決める（OPEN-Q 未決I, issue #70）。大会本流の乱数には依存しないので、対戦カード画面
    /// （<see cref="MatchPreview.MatchPreviewState"/>）でも同じ入口で先読みできる。
    /// 渡される rng は TournamentRunner が本流から Fork した隔離ストリーム（本流の乱数列に影響しない）。
    /// スタメン未設定時は自動編成（RosterTeamBuilder.Build）へフォールバックする。
    /// </summary>
    public sealed class PlayerMatchResolver : IPlayerMatchResolver
    {
        public PlayerMatchDetail Resolve(School manager, School opponent, IRandomSource rng, bool mercyRuleEnabled,
            TournamentMatchContext? context = null)
        {
            var mgrTeam = BuildManagerTeam(manager.Name);
            var oppTeam = BuildOpponentTeam(opponent, AceRestContext.From(context, manager.Tier));
            var ctx = Rules.ApplyTo(
                new GameContext { MercyRuleEnabled = mercyRuleEnabled, Field = Stadium(context, rng) }, CalendarYear());
            var (mgrRecovery, oppRecovery) = PitchRecoveryFor(context, ctx.PitchRecovery, manager, mgrTeam, opponent, oppTeam);
            var managerIsAway = ManagerIsAway(manager, opponent);
            var result = managerIsAway
                ? GameEngine.Play(mgrTeam, oppTeam, ctx, rng.Fork(2), awayPitchRecovery: mgrRecovery, homePitchRecovery: oppRecovery)
                : GameEngine.Play(oppTeam, mgrTeam, ctx, rng.Fork(2), awayPitchRecovery: oppRecovery, homePitchRecovery: mgrRecovery);
            return new PlayerMatchDetail(result, ManagerIsAway: managerIsAway);
        }

        public PlayerMatchLive BeginLive(School manager, School opponent, IRandomSource rng, bool mercyRuleEnabled,
            TournamentMatchContext? context = null)
        {
            // Resolve と同一の teams＋同一Fork＋同一 mercyRuleEnabled+context で組む（相手＝校ID固定生成, rng.Fork(2)=試合）。
            // 唯一の差は観測ゲート（CaptureTimelines=観戦用タイムライン／CapturePitches=1球進行の供給口,
            // 設計書15 §8）。いずれも RNG 中立なので、全打席を進めた結果は Resolve のボックススコアと
            // 一致する＝観戦しても大会結果は変わらない。
            var mgrTeam = BuildManagerTeam(manager.Name);
            var oppTeam = BuildOpponentTeam(opponent, AceRestContext.From(context, manager.Tier));
            var ctx = Rules.ApplyTo(
                new GameContext
                {
                    CaptureTimelines = true, CapturePitches = true,
                    // 自校の投手交代はプレイヤーの采配権にする（Resolve側＝観戦しない裏解決には付けない）。
                    PlayerControlsPitcherChanges = true,
                    MercyRuleEnabled = mercyRuleEnabled, Field = Stadium(context, rng),
                },
                CalendarYear());
            var (mgrRecovery, oppRecovery) = PitchRecoveryFor(context, ctx.PitchRecovery, manager, mgrTeam, opponent, oppTeam);
#if KOKOSIM_DEBUG || UNITY_EDITOR || DEVELOPMENT_BUILD
            // 大会フローの実試合にもデバッグHUDの観測を差し込む（設計書17 §5/§12.4, #95）。HUDが閉じていれば
            // AttachTo は恒等（Enabled==false→ctxをそのまま返す）＝観戦の有無で大会結果が変わらない契約
            // （このメソッド冒頭のコメント／Resolve との box score 一致）を壊さない。CaptureTrace は RNG 中立。
            // 裏処理（Resolve）には付けない＝コストゼロを維持する。
            ctx = Unity.Debugging.DebugTraceHub.AttachTo(ctx);
#endif
            var managerIsAway = ManagerIsAway(manager, opponent);
            var prog = managerIsAway
                ? new MatchProgression(mgrTeam, oppTeam, ctx, rng.Fork(2), awayPitchRecovery: mgrRecovery, homePitchRecovery: oppRecovery)
                : new MatchProgression(oppTeam, mgrTeam, ctx, rng.Fork(2), awayPitchRecovery: oppRecovery, homePitchRecovery: mgrRecovery);
            return new PlayerMatchLive(prog, ManagerIsAway: managerIsAway);
        }

        /// <summary>
        /// 試合間の回復モデル（設計書02 §1.1e-2, issue #41 本流配線）で調整済みの実効目安投球数を、
        /// 自校・相手校それぞれ<b>別の辞書</b>で求める（校をまたいだ Player 値等価の衝突を構造的に避ける。
        /// <see cref="PitchRecoveryLookup.ForMatch"/> 参照）。context が null（大会外・練習試合等）なら
        /// 両方とも空辞書＝GameEngine 側は素の目安投球数のまま（従来挙動と完全一致）。
        /// </summary>
        private static (IReadOnlyDictionary<Player, double> Manager, IReadOnlyDictionary<Player, double> Opponent)
            PitchRecoveryFor(
                TournamentMatchContext? context, PitchRecoveryCoefficients coeff,
                School manager, Team mgrTeam, School opponent, Team oppTeam)
            => PitchRecoveryLookup.ForMatch(
                context, coeff,
                PitchRecoveryLookup.AllPlayers(mgrTeam),
                p => PitcherLedgerKey.For(isManagerSchool: true, manager.Id, p.SourceId, p.UniformNumber),
                PitchRecoveryLookup.AllPlayers(oppTeam),
                p => PitcherLedgerKey.For(isManagerSchool: false, opponent.Id, p.SourceId, p.UniformNumber));

        /// <summary>
        /// この対戦の自校先攻/後攻。対戦カード画面（<see cref="MatchPreview.MatchPreviewState"/>）も
        /// 同じ入口を通すことで、表示した先攻/後攻と実際の試合が必ず一致する契約を守る。
        /// </summary>
        public static bool ManagerIsAway(School manager, School opponent)
            => HomeAwayAssignment.ManagerIsAway(manager.Id, opponent.Id, GameClock.YearIndex, GameClock.Week);

        /// <summary>現代ルール（現状トグルなし・年代連動の既定値, 設計書05 §1.3）。DH判定と
        /// タイブレーク/球数制限の GameContext 反映（issue #252）の両方でこの1インスタンスを使う。</summary>
        private static readonly ModernRules Rules = new();

        /// <summary>専用の球場抽選ストリームid（設計書13。本編・継投等の乱数を汚さない＝Fork は親状態を進めない）。</summary>
        private const ulong StadiumStreamId = 13;

        /// <summary>
        /// いま戦っている大会の球場寸法（設計書13 §1）。甲子園は固有球場、夏・秋の県大会は汎用ティア
        /// （序盤=市営級／準決勝・決勝=県営級）、それ以外（地区・神宮）は既定。<b>Resolve と BeginLive が
        /// 同じ値を使う</b>（観戦しても結果が変わらない契約）ので、両者ともこの1メソッドを通す。
        /// </summary>
        private static KokoSim.Engine.Match.Field.FieldGeometry Stadium(TournamentMatchContext? context, IRandomSource rng)
            => MatchStadium.For(GameSession.Current.Kind, context?.RoundsRemaining, rng.Fork(StadiumStreamId));

        /// <summary>現在の暦年（<see cref="GameSession.Current"/> の YearIndex から換算）。</summary>
        private static int CalendarYear() => SeasonClock.SeasonBaseYear + (GameSession.Current.Year - 1);

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
        public static Team BuildOpponentTeam(School opponent) => BuildOpponentTeam(opponent, aceRest: null);

        /// <summary>
        /// <paramref name="aceRest"/> 付きの内部版。展望（<see cref="MatchPreview.MatchPreviewState"/>）は
        /// 引数なしの公開版を使い続けるため常時エース先発のまま＝「展望の先発は当日と異なることがある」を
        /// 表記変更なしで許容する仕様（OPEN-QUESTIONS Q16 論点(c), 2026-07-21確定）。実際の試合解決
        /// （<see cref="Resolve"/>/<see cref="BeginLive"/>）だけがエース温存判断（issue #42）を反映する。
        /// </summary>
        private static Team BuildOpponentTeam(School opponent, AceRestContext? aceRest)
        {
            var yearIndex = GameSession.Current.Year;
            var calendarYear = CalendarYear();
            // 永続ロスター（#80）から相手校チームを組む。使い捨て生成を廃し、秋に対戦した2年生が翌夏に成長して
            // 戻ってくる。展望（TournamentPreview）も同じ Rosters.TeamFor を通り実戦と一致する。
            var team = NationService.Rosters.TeamFor(opponent, yearIndex, Rules, calendarYear, aceRest);
            var brain = EnemyAiFactory.BrainFor(opponent);
            var orderRng = AiRosterFactory.CohortSeed(opponent.Id, yearIndex).Fork(0x0BDE_0000UL);
            var reordered = brain.ComposeBattingOrder(team.BattingOrder, orderRng);
            // 投手枠・DH枠は並べ替え前のインデックス基準なので、engine の単一入口
            // （Team.WithBattingOrder＝同一参照から引き直す純関数）へ通す。Q21 の解決（2026-07-22）で
            // 相手校にも当季の調子差が付き、この並べ替えは日常的に非恒等になった＝投手枠を引き直さないと
            // 9番へ落ちた野手が登板扱いになる（issue #352）。
            return team.WithBattingOrder(reordered) with { Tactics = brain };
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
