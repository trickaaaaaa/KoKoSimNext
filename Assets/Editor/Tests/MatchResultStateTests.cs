using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Stats;
using KokoSim.Unity.MatchResult;
using System.Linq;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>試合結果画面（GameResult.mock・2026-11-21 作り直し）の表示モデル。</summary>
    public class MatchResultStateTests
    {
        private static BattingLine Bat(int order, FieldPosition pos, string name, int ab = 4, int h = 0,
            int d = 0, int t = 0, int hr = 0, int rbi = 0, int bb = 0, int so = 0, int hbp = 0, int sb = 0)
            => new(order, pos, name, ab + bb + hbp, ab, h, d, t, hr, rbi, bb, so, HitByPitches: hbp, StolenBases: sb);

        private static GameResult Game(BattingLine[] away = null, BattingLine[] home = null,
            int[] awayLine = null, int[] homeLine = null, PlayLogEntry[] log = null)
            => new GameResult
            {
                AwayName = "北都大付属", HomeName = "桜丘", AwayRuns = 3, HomeRuns = 4, InningsPlayed = 9,
                TotalPitches = 0, PitcherChanges = 0,
                AwayLineScore = awayLine ?? new[] { 0, 1, 0, 2, 0, 0, 0, 0, 0 },
                HomeLineScore = homeLine ?? new[] { 1, 0, 0, 1, 0, 0, 2, 0 },
                AwayBatting = away ?? new BattingLine[0],
                HomeBatting = home ?? new BattingLine[0],
                Log = log ?? new PlayLogEntry[0],
            };

        [Test]
        public void PitchingRows_CarryHitsForBothSides()
        {
            var r = new GameResult
            {
                AwayName = "A", HomeName = "H", AwayRuns = 3, HomeRuns = 2, InningsPlayed = 9, TotalPitches = 0, PitcherChanges = 0,
                AwayPitching = new[] { new PitchingLine("先発", 27, 33, 7, 2, 5, 1, 110, HitBatters: 1), new PitchingLine("継投", 3, 4, 2, 1, 0, 2, 20) },
                HomePitching = new[] { new PitchingLine("相手投手", 27, 36, 9, 3, 8, 4, 130) },
            };

            var v = MatchResultState.Build(r, managerIsAway: true, "A", "H");

            Assert.AreEqual(2, v.Own.Pitching.Count);
            Assert.AreEqual(7, v.Own.Pitching[0].Hits);
            Assert.AreEqual(2, v.Own.Pitching[1].Hits);
            Assert.AreEqual(9, v.Opponent.Pitching[0].Hits);
            Assert.AreEqual(2, v.Own.Pitching[0].Runs);
            Assert.AreEqual(2, v.Own.Pitching[0].WalksHbp, "四死球＝四球＋死球");
            Assert.AreEqual("9", v.Own.Pitching[0].Innings);
        }

        [Test]
        public void LineScore_UsesInningsActuallyPlayed_AndMarksSkippedBottomHalf()
        {
            var v = MatchResultState.Build(Game(), managerIsAway: false, "北都大付属", "桜丘");

            Assert.AreEqual(9, v.InningCount);
            Assert.IsNull(v.HomeLine.Innings[8], "裏の攻撃が無かった最終回は×");
            Assert.AreEqual(2, v.HomeLine.Innings[6]);
            Assert.IsTrue(v.HomeLine.Won);
            Assert.IsFalse(v.AwayLine.Won);

            var cold = MatchResultState.Build(Game(awayLine: new[] { 0, 0, 0, 0, 0, 0, 0 }, homeLine: new[] { 3, 2, 1, 1, 0, 3 }),
                managerIsAway: false, "北都大付属", "桜丘");
            Assert.AreEqual(7, cold.InningCount, "コールドは行った回数まで（9へ伸ばさない）");

            var extra = MatchResultState.Build(Game(awayLine: new int[11], homeLine: new int[11]), false, "北都大付属", "桜丘");
            Assert.AreEqual(11, extra.InningCount);
        }

        [Test]
        public void ExtraBaseHits_GroupBySchool_WithSurnameAndInitial()
        {
            var away = new[]
            {
                Bat(1, FieldPosition.CenterField, "北村　朝陽", h: 2, d: 1),
                Bat(2, FieldPosition.RightField, "三宅　海翔", h: 1, d: 1),
                Bat(8, FieldPosition.LeftField, "福原　理人", h: 2, hr: 1),
            };
            var home = new[] { Bat(7, FieldPosition.FirstBase, "中井　康太", h: 2, hr: 1) };

            var v = MatchResultState.Build(Game(away, home), managerIsAway: false, "北都大付属", "桜丘");

            Assert.AreEqual(2, v.ExtraBaseHits.Count, "0人の三塁打は出さない");
            Assert.AreEqual("本塁打", v.ExtraBaseHits[0].Label);
            Assert.AreEqual(("中井", "桜"), v.ExtraBaseHits[0].Entries[0], "自校から");
            Assert.AreEqual(("福原", "北"), v.ExtraBaseHits[0].Entries[1]);
            Assert.AreEqual("二塁打", v.ExtraBaseHits[1].Label);
            Assert.AreEqual(("北村、三宅", "北"), v.ExtraBaseHits[1].Entries[0]);
        }

        [Test]
        public void SchoolMarks_UseTwoCharactersWhenInitialsCollide()
        {
            Assert.AreEqual(("桜", "北"), MatchResultState.SchoolMarks("桜丘", "北都大付属"));
            Assert.AreEqual(("北都", "北山"), MatchResultState.SchoolMarks("北都大付属", "北山商"));
        }

        [Test]
        public void Substitutes_SitUnderReplacedPlayer_InBothBattingAndPaRows()
        {
            var home = new[]
            {
                Bat(1, FieldPosition.RightField, "関　太一", ab: 3, h: 1),
                Bat(1, FieldPosition.Shortstop, "代打　一郎", ab: 1, h: 1) with { Entry = BoxEntry.PinchHit, Positions = new[] { FieldPosition.LeftField } },
                Bat(2, FieldPosition.CenterField, "橋本　悠馬", ab: 3),
                Bat(2, FieldPosition.Catcher, "代走　次郎", ab: 0) with { Entry = BoxEntry.PinchRun },
            };
            var log = new[]
            {
                new PlayLogEntry(1, false, "関　太一", PlateAppearanceResult.Single, 0, BatterOrder: 1),
                new PlayLogEntry(1, false, "橋本　悠馬", PlateAppearanceResult.InPlayOut, 0, BatterOrder: 2, OutsThisPa: 2),
                new PlayLogEntry(4, false, "代打　一郎", PlateAppearanceResult.InPlayOut, 1, BatterOrder: 1, IsSacFly: true, OutsThisPa: 1),
            };

            var v = MatchResultState.Build(Game(home: home, log: log), managerIsAway: false, "北都大付属", "桜丘");
            var bat = v.Own.Batting;
            var pa = v.Own.PaRows;

            Assert.AreEqual(4, bat.Count);
            Assert.AreEqual(4, pa.Count, "打席に立たなかった代走も打席結果に行がある");
            Assert.AreEqual(0, bat[1].Order, "途中出場は打順の数字を空欄に");
            Assert.AreEqual("打左", bat[1].Pos, "代打のあと守備に就いたら「打」に就いた位置が続く");
            Assert.AreEqual("走", bat[3].Pos);
            Assert.AreEqual(MatchResultPosKind.Entry, bat[3].PosKind);
            Assert.AreEqual("代打 一郎", pa[1].Name);
            Assert.AreEqual("犠飛", pa[1].Cells[1].Text, "代打は打順の枠の打席番号を引き継ぐ");
            Assert.AreEqual("併殺", pa[2].Cells[0].Text);
            Assert.AreEqual("·", pa[3].Cells[0].Text);
            Assert.AreEqual(MatchResultState.MinPaColumns, v.Own.PaColumns, "最低4列");
        }

        [Test]
        public void BattingTotals_AndAverageText()
        {
            var home = new[]
            {
                Bat(1, FieldPosition.RightField, "関　太一", ab: 3, h: 1, bb: 1, hbp: 1, sb: 1),
                Bat(2, FieldPosition.CenterField, "橋本　悠馬", ab: 0, bb: 2),
            };
            var v = MatchResultState.Build(Game(home: home), managerIsAway: false, "北都大付属", "桜丘");

            Assert.AreEqual(".333", v.Own.Batting[0].Average);
            Assert.AreEqual(2, v.Own.Batting[0].WalksHbp);
            Assert.AreEqual("—", v.Own.Batting[1].Average, "打数0は—");
            Assert.IsTrue(v.Own.Batting[1].AverageDim);
            Assert.AreEqual(3, v.Own.Total.AtBats);
            Assert.AreEqual(4, v.Own.Total.WalksHbp);
            Assert.AreEqual(".333", v.Own.Total.Average);
            Assert.AreEqual("1.000", MatchResultState.AvgText(2, 2));
        }

        [Test]
        public void PaText_SeparatesSacrificesAndDoublePlays()
        {
            Assert.AreEqual("犠打", MatchResultState.PaText(new BoxScoreGridCell(1, 1, PlateAppearanceResult.InPlayOut, 0) { IsSacBunt = true }));
            Assert.AreEqual("犠飛", MatchResultState.PaText(new BoxScoreGridCell(1, 1, PlateAppearanceResult.InPlayOut, 1) { IsSacFly = true }));
            Assert.AreEqual("併殺", MatchResultState.PaText(new BoxScoreGridCell(1, 1, PlateAppearanceResult.InPlayOut, 0) { OutsThisPa = 2 }));
            Assert.AreEqual("凡打", MatchResultState.PaText(new BoxScoreGridCell(1, 1, PlateAppearanceResult.InPlayOut, 0) { OutsThisPa = 1 }));
            Assert.AreEqual("三振", MatchResultState.PaText(new BoxScoreGridCell(1, 1, PlateAppearanceResult.Strikeout, 0) { OutsThisPa = 2 }));
            Assert.AreEqual(MatchResultPaKind.HomeRun, MatchResultState.PaKind(PlateAppearanceResult.HomeRun));
            Assert.AreEqual(MatchResultPaKind.Walk, MatchResultState.PaKind(PlateAppearanceResult.HitByPitch));
        }

        // 守った位置は順番どおりに全部つなげる（issue #1608・新聞式）。
        [Test]
        public void Pos_JoinsEveryFieldedPositionInOrder()
        {
            var home = new[]
            {
                Bat(1, FieldPosition.Shortstop, "関　太一") with { Positions = new[] { FieldPosition.Shortstop, FieldPosition.SecondBase } },
                Bat(2, FieldPosition.DesignatedHitter, "橋本　悠馬") with { Positions = new[] { FieldPosition.DesignatedHitter, FieldPosition.LeftField } },
                Bat(1, FieldPosition.LeftField, "代打　一郎") with
                { Entry = BoxEntry.PinchHit, Positions = new[] { FieldPosition.LeftField, FieldPosition.CenterField, FieldPosition.RightField } },
                Bat(3, FieldPosition.Catcher, "守備　三郎") with { Entry = BoxEntry.DefensiveSub, Positions = new[] { FieldPosition.Catcher } },
                Bat(4, FieldPosition.Catcher, "代走　次郎") with { Entry = BoxEntry.PinchRun },
                Bat(5, FieldPosition.FirstBase, "旧　記録"),
            };
            var v = MatchResultState.Build(Game(home: home), managerIsAway: false, "北都大付属", "桜丘");
            var bat = v.Own.Batting;

            Assert.AreEqual("遊二", bat[0].Pos);
            Assert.AreEqual("指左", bat[1].Pos, "DH解除で守備に就いたら「指」に続く");
            Assert.AreEqual("打左中右", bat[2].Pos, "代打は「打」から始まり就いた位置が全部続く");
            Assert.AreEqual("捕", bat[3].Pos);
            Assert.AreEqual("走", bat[4].Pos, "守備に就かなかった代走は「走」だけ");
            Assert.AreEqual("一", bat[5].Pos, "並びが空の行は行の守備位置");
            Assert.AreEqual(MatchResultPosKind.Outfield, bat[2].PosKind, "色は最後に就いた位置");
            Assert.AreEqual(MatchResultPosKind.Entry, bat[4].PosKind);
            Assert.AreEqual("打左中右", MatchResultState.PosText(home[2]));
        }

        // 過去の試合（戦績）は試合直後と同じ記録から描く（issue #1413）。セーブして読み直した記録でも表示モデルが一致する。
        [Test]
        public void ViewFromSavedAndReloadedRecord_MatchesViewFromGameResult()
        {
            var home = new[]
            {
                Bat(1, FieldPosition.RightField, "関　太一", ab: 3, h: 1, hr: 1, rbi: 2, bb: 1, hbp: 1, sb: 1),
                Bat(1, FieldPosition.Shortstop, "代打　一郎", ab: 1, h: 1) with { Entry = BoxEntry.PinchHit, Positions = new[] { FieldPosition.LeftField } },
                Bat(2, FieldPosition.CenterField, "橋本　悠馬", ab: 3, d: 1),
            };
            var log = new[]
            {
                new PlayLogEntry(1, false, "関　太一", PlateAppearanceResult.HomeRun, 1, BatterOrder: 1),
                new PlayLogEntry(1, false, "橋本　悠馬", PlateAppearanceResult.InPlayOut, 0, BatterOrder: 2, OutsThisPa: 2),
                new PlayLogEntry(4, false, "代打　一郎", PlateAppearanceResult.InPlayOut, 1, BatterOrder: 1, IsSacFly: true, OutsThisPa: 1),
            };
            var result = Game(home: home, log: log);
            var direct = MatchResultState.Build(result, managerIsAway: false, "北都大付属", "桜丘");

            var record = MatchRecordFactory.From(result, new MatchRecord { ManagerIsAway = false, OwnName = "桜丘", OpponentName = "北都大付属" });
            var state = new SaveState();
            state.MatchRecords.Add(record);
            Assert.IsTrue(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var back, out var err), err);
            var reloaded = MatchResultState.Build(back.MatchRecords.Records[0]);

            Assert.AreEqual(Dump(direct), Dump(reloaded));
        }

        private static string Dump(MatchResultView v)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(v.InningCount).Append('|');
            foreach (var l in new[] { v.AwayLine, v.HomeLine })
                sb.Append(l.TeamName).Append(l.Won).Append(l.Runs).Append(l.Hits).Append(l.Errors)
                  .Append(string.Join(",", l.Innings.ConvertAll(i => i?.ToString() ?? "x"))).Append('|');
            foreach (var g in v.ExtraBaseHits)
                sb.Append(g.Label).Append(string.Join(",", g.Entries.ConvertAll(e => e.Names + "(" + e.School + ")"))).Append('|');
            foreach (var side in new[] { v.Own, v.Opponent })
            {
                sb.Append(side.TeamName).Append(side.PaColumns).Append('|');
                foreach (var b in side.Batting.Concat(new[] { side.Total }))
                    sb.Append(b.Order).Append(b.Pos).Append(b.PosKind).Append(b.Name).Append(b.IsSub)
                      .Append(b.AtBats).Append(b.Hits).Append(b.Rbi).Append(b.HomeRuns).Append(b.Triples).Append(b.Doubles)
                      .Append(b.WalksHbp).Append(b.StrikeOuts).Append(b.StolenBases).Append(b.Average).Append('|');
                foreach (var p in side.Pitching)
                    sb.Append(p.Name).Append(p.Innings).Append(p.Pitches).Append(p.Hits).Append(p.StrikeOuts).Append(p.WalksHbp).Append(p.Runs).Append('|');
                foreach (var r in side.PaRows)
                {
                    sb.Append(r.Order).Append(r.Pos).Append(r.Name);
                    foreach (var c in r.Cells) sb.Append(c.Text).Append(c.Kind);
                    sb.Append('|');
                }
            }
            return sb.ToString();
        }
    }
}
