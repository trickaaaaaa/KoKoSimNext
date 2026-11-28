using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Replay;
using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 自校の一戦を解いた入力の控え（issue #1580）。<see cref="PlayerMatchResolver"/> が試合を組んだときに積み、
    /// 終局の記録（<see cref="GameSession"/>）が取り出して注目試合の再生データへ変える。
    /// 一括解決は入力がその場で決まる（<see cref="Inputs"/>）。ライブ観戦はプレイヤーの采配の決定列が試合の終わりまで
    /// 伸びるので、進行体（<see cref="Progression"/>）を覚えておき、終局で取る。
    /// </summary>
    public sealed class PendingOwnReplay
    {
        public bool ManagerIsAway { get; }
        public School Opponent { get; }
        public MatchReplayInputs Inputs { get; }
        public MatchProgression Progression { get; }

        public PendingOwnReplay(bool managerIsAway, School opponent, MatchReplayInputs inputs, MatchProgression progression)
        {
            ManagerIsAway = managerIsAway;
            Opponent = opponent;
            Inputs = inputs;
            Progression = progression;
        }

        /// <summary>終局時点の入力一式。ライブ観戦は進行体から、場面ジャンプ等で再生できない試合は null。</summary>
        public MatchReplayInputs ResolveInputs() => Inputs ?? Progression?.CaptureReplayInputs();
    }

    /// <summary>
    /// 裏試合（自校が出ない大会の試合）の注目試合の再生データを台帳へ積む受け口（issue #1580）。
    /// 残す理由は <see cref="MatchReplayPolicy"/>＝自県の上位ティア校の試合・甲子園の全試合。
    /// 大会の種別・年度・試合日を足してから <see cref="GameSession.MatchReplays"/> へ積む。背景スレッドから呼ばれる。
    /// </summary>
    internal sealed class MatchReplayCollector : IMatchReplayCollector
    {
        private readonly TournamentKind _kind;
        private readonly int _yearIndex;
        private readonly int _managerSchoolId;
        private readonly int _managerPrefectureId;

        public MatchReplayCollector(TournamentKind kind, int yearIndex)
        {
            _kind = kind;
            _yearIndex = yearIndex;
            // 背景スレッドから読むので、組み立て時（メインスレッド）に控える。
            _managerSchoolId = NationService.ManagerSchoolId;
            _managerPrefectureId = NationService.PrefectureId;
        }

        public NotableReason Evaluate(School away, School home)
            => MatchReplayPolicy.Evaluate(_kind, away, home, _managerSchoolId, _managerPrefectureId);

        public void Collect(MatchReplayRecord record)
        {
            var month = 0;
            var day = 0;
            if (record.MatchDay >= 0)
            {
                var date = TournamentDate.DateOf(TournamentEntry.Schedule, _kind, record.MatchDay);
                month = date.Month;
                day = date.Day;
            }
            var book = GameSession.Current?.MatchReplays;
            if (book == null) return;
            book.Add(record with
            {
                Year = _yearIndex, Kind = _kind, Month = month, Day = day, RoundName = RoundNameOf(record.RoundsRemaining),
            });
            book.Prune(_yearIndex);
        }

        /// <summary>決勝・準決勝・準々決勝だけ名前を付ける（それより前は大会の総ラウンド数が要るので空＝閲覧側が決める）。</summary>
        private static string RoundNameOf(int roundsRemaining)
        {
            switch (roundsRemaining)
            {
                case 1: return "決勝";
                case 2: return "準決勝";
                case 3: return "準々決勝";
                default: return "";
            }
        }
    }
}
