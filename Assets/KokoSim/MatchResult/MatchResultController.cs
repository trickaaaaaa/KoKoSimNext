using System.Collections.Generic;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Stats;       // MatchRecord
using KokoSim.Unity.Components;   // UiComponents
using KokoSim.Unity.Shell;        // ScreenRouter
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.MatchResult
{
    /// <summary>
    /// 試合結果画面のコントローラ（GameResult.mock 準拠・2026-11-21 作り直し。旧 issue #13 B案）。
    /// ライブ観戦（MatchLive）の終局から自動で開き、イニングスコア・長打と、両校の打撃成績・投手成績・
    /// 打席結果を並べる。「閉じる」は MatchLive から預かった後処理（大会への結果反映）を呼び、ホームへ戻す。
    /// 表示専用＝ここではエンジンを進めない（帯不変・決定論に影響なし）。
    ///
    /// <para>数字の列は固定幅＋右寄せで桁を揃える（フォントの等幅数字に頼らない）。0 は薄く、安打・打点・長打・盗塁は
    /// 1以上で太字（本塁打だけ朱）。太字は書体クラス（f-num-bd / f-body-bd）の付け替えで出す＝疑似ボールド禁止。</para>
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class MatchResultController : MonoBehaviour
    {
        /// <summary>試合終了時に MatchLive が積む表示要求（一度きり）。</summary>
        public sealed class MatchResultRequest
        {
            /// <summary>過去の試合（戦績）の記録。あればこれを描き、<see cref="Result"/> 以下は使わない（issue #1413）。</summary>
            public MatchRecord Record;
            public GameResult Result;
            public bool ManagerIsAway;
            public string AwayName;
            public string HomeName;
            /// <summary>大会戦（勝者が要る文脈）か。旧画面の「引き分け（再試合）」表記用。今の画面は勝敗の文言を出さないので未使用。</summary>
            public bool RematchOnTie;
            /// <summary>「閉じる」で呼ぶ後処理（大会への結果反映＋画面遷移）。null ならホームへ戻すだけ。</summary>
            public System.Action OnClose;
        }

        public static MatchResultRequest Pending;

        /// <summary>
        /// 過去の試合（戦績の1行）の結果画面を開く（issue #1413）。<paramref name="onClose"/> は「閉じる」で戻る先の
        /// 切替（戦績タブへ戻す）。大会の後処理は無い＝試合直後の結果画面と違い、閉じても何も進まない。
        /// </summary>
        public static void ShowPast(MatchRecord record, System.Action onClose)
        {
            Pending = new MatchResultRequest { Record = record, OnClose = onClose };
            ScreenRouter.Instance?.ShowDeferred("MatchResult");
        }

        private VisualElement _root;
        private Button _close;
        private System.Action _onClose;

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            _close = _root.Q<Button>("mr-close");
            if (_close != null) _close.clicked += OnClose;

            var req = Pending;
            Pending = null;
            _onClose = req?.OnClose;

            if (req?.Record is { HasDetail: true } record)
            {
                Render(MatchResultState.Build(record));
                return;
            }

            var result = req?.Result;
            if (result == null) return;   // 要求なしで開かれた（スクショ等）: 骨組みだけ出す
            Render(MatchResultState.Build(result, req.ManagerIsAway, req.AwayName ?? "先攻", req.HomeName ?? "後攻"));
        }

        // 画面を離れるときに登録を解除する（OnEnable が毎回登録するため、外さないと往復のたび多重登録になる）。
        private void OnDisable()
        {
            if (_close != null) _close.clicked -= OnClose;
        }

        /// <summary>スクショ・デバッグ用: 要求を直接与えて描画する（Pending を経由しない）。</summary>
        public void RenderForCapture(GameResult result, bool managerIsAway, string awayName, string homeName)
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            Render(MatchResultState.Build(result, managerIsAway, awayName, homeName));
        }

        private void Render(MatchResultView v)
        {
            RenderLineScore(v);
            RenderExtraBaseHits(v);
            RenderSide(_root.Q<VisualElement>("mr-left"), v.Own);
            RenderSide(_root.Q<VisualElement>("mr-right"), v.Opponent);
        }

        // ── ② イニングスコア（校名 140｜各回 48｜計 64｜安 52｜失 52）。左に詰め、右の余白に長打を縦に並べる ──
        private void RenderLineScore(MatchResultView v)
        {
            var host = _root.Q<VisualElement>("mr-line");
            if (host == null) return;
            host.Clear();

            var head = Row("mres-line__row", "mres-line__row--head");
            head.Add(Cell("", "mres-line__team"));
            for (var i = 1; i <= v.InningCount; i++) head.Add(Cell(i.ToString(), "mres-line__inn", "mres-line__h", "f-num"));
            head.Add(Cell("計", "mres-line__total", "mres-line__h", "mres-line__h--sum", "f-body-bd"));
            head.Add(Cell("安", "mres-line__stat", "mres-line__h", "f-body"));
            head.Add(Cell("失", "mres-line__stat", "mres-line__h", "f-body"));
            host.Add(head);

            host.Add(LineRow(v.AwayLine, v.InningCount));
            host.Add(LineRow(v.HomeLine, v.InningCount, last: true));
        }

        private static VisualElement LineRow(MatchResultLineRow r, int innings, bool last = false)
        {
            var row = Row("mres-line__row");
            if (!last) row.AddToClassList("mres-line__row--sep");
            var team = Cell(r.TeamName, "mres-line__team", "mres-line__name");
            if (!r.Won) team.AddToClassList("mres-line__name--lose");
            row.Add(team);
            for (var i = 0; i < innings; i++)
            {
                var s = r.Innings[i];
                var c = s is { } n
                    ? Cell(n.ToString(), "mres-line__inn", "mres-line__runs", n == 0 ? "mres-zero" : "mres-ink")
                    : Cell("×", "mres-line__inn", "mres-line__x", "f-body");
                row.Add(c);
            }
            row.Add(Cell(r.Runs.ToString(), "mres-line__total", "mres-line__sum"));
            row.Add(Cell(r.Hits.ToString(), "mres-line__stat", "mres-line__v", "f-num"));
            row.Add(Cell(r.Errors.ToString(), "mres-line__stat", "mres-line__v", "f-num"));
            return row;
        }

        // ── ② 長打の行（本塁打／三塁打／二塁打。0人の項目は出さない） ──
        private void RenderExtraBaseHits(MatchResultView v)
        {
            var host = _root.Q<VisualElement>("mr-xbh");
            if (host == null) return;
            host.Clear();
            host.style.display = v.ExtraBaseHits.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (var g in v.ExtraBaseHits)
            {
                var item = Row("mres-xbh__item");
                item.Add(Cell(g.Label, "mres-xbh__tag", "f-body-bd"));
                for (var i = 0; i < g.Entries.Count; i++)
                {
                    var (names, school) = g.Entries[i];
                    var l = Cell(names + "（" + school + "）", "mres-xbh__names", "f-body");
                    if (i > 0) l.AddToClassList("mres-xbh__names--next");
                    item.Add(l);
                }
                host.Add(item);
            }
        }

        // ── ③ 片チームぶん（校名・打撃成績・投手成績・打席結果）。左右で完全に同じ組み方をする。 ──
        private static void RenderSide(VisualElement host, MatchResultSideView side)
        {
            if (host == null) return;
            host.Clear();
            host.Add(Cell(side.TeamName, "mres-side__name"));

            host.Add(Cell("打撃成績", "mres-block__title", "f-body-bd"));
            host.Add(BattingTable(side));
            host.Add(Cell("投手成績", "mres-block__title", "f-body-bd"));
            host.Add(PitchingTable(side));
            host.Add(Cell("打席結果", "mres-block__title", "f-body-bd"));
            host.Add(PaTable(side));
        }

        // 打撃成績: 打順 22｜守備 26｜選手｜打数 安打 打点（42）｜間10｜本塁打 三塁打 二塁打（42）｜間10｜四死球 三振 盗塁（42）｜打率 58
        private static readonly string[] BatHead = { "打数", "安打", "打点", "本塁打", "三塁打", "二塁打", "四死球", "三振", "盗塁" };

        private static VisualElement BattingTable(MatchResultSideView side)
        {
            var t = Table();
            var head = Row("mres-tr", "mres-tr--head");
            head.Add(Cell("", "mres-c-ord"));
            head.Add(Cell("", "mres-c-pos"));
            head.Add(Cell("選手", "mres-c-name", "mres-th"));
            for (var i = 0; i < BatHead.Length; i++) head.Add(NumHeadCell(BatHead[i], i));
            head.Add(Cell("打率", "mres-c-avg", "mres-th"));
            t.Add(head);

            foreach (var b in side.Batting) t.Add(BattingRow(b, total: false));
            t.Add(BattingRow(side.Total, total: true));
            return t;
        }

        private static Label NumHeadCell(string text, int i)
        {
            var c = Cell(text, "mres-c-n42", "mres-th");
            if (i == 3 || i == 6) c.AddToClassList("mres-c--group");   // 長打・四死球の塊の前の間隔10px
            return c;
        }

        private static VisualElement BattingRow(MatchResultBattingRow b, bool total)
        {
            var row = Row("mres-tr");
            if (total) row.AddToClassList("mres-tr--total");
            row.Add(Cell(b.Order > 0 ? b.Order.ToString() : "", "mres-c-ord", "mres-ord"));
            row.Add(total ? Cell("", "mres-c-pos") : PosChip(b.Pos, b.PosKind));
            row.Add(total ? Cell(b.Name, "mres-c-name", "mres-total__label", "f-body-bd") : Cell(b.Name, "mres-c-name", "mres-name"));
            // 1以上で太字の列: 安打・打点・本塁打（朱）・三塁打・二塁打・盗塁。打数・四死球・三振は普通の太さ。
            var cols = new (int V, bool Strong, bool Red)[]
            {
                (b.AtBats, false, false), (b.Hits, true, false), (b.Rbi, true, false),
                (b.HomeRuns, true, true), (b.Triples, true, false), (b.Doubles, true, false),
                (b.WalksHbp, false, false), (b.StrikeOuts, false, false), (b.StolenBases, true, false),
            };
            for (var i = 0; i < cols.Length; i++)
            {
                var c = NumCell(cols[i].V, cols[i].Strong || total, cols[i].Red && !total, "mres-c-n42");
                if (i == 3 || i == 6) c.AddToClassList("mres-c--group");
                row.Add(c);
            }
            var avg = Cell(b.Average, "mres-c-avg", "mres-num", total ? "f-num-bd" : "f-num");
            avg.AddToClassList(b.AverageDim ? "mres-zero" : "mres-ink");
            row.Add(avg);
            return row;
        }

        // 投手成績: 投手名｜投球回 球数 被安打 奪三振 四死球 失点（各64）。登板順。
        private static VisualElement PitchingTable(MatchResultSideView side)
        {
            var t = Table();
            var head = Row("mres-tr", "mres-tr--head");
            head.Add(Cell("投手", "mres-c-pname", "mres-th"));
            foreach (var h in new[] { "投球回", "球数", "被安打", "奪三振", "四死球", "失点" }) head.Add(Cell(h, "mres-c-n64", "mres-th"));
            t.Add(head);
            foreach (var p in side.Pitching)
            {
                var row = Row("mres-tr");
                row.Add(Cell(p.Name, "mres-c-pname", "mres-name"));
                row.Add(UiComponents.SetInnings(Cell("", "mres-c-n64", "mres-num", "mres-ink", "f-num"), p.Innings));
                row.Add(NumCell(p.Pitches, false, false, "mres-c-n64"));
                row.Add(NumCell(p.Hits, false, false, "mres-c-n64"));
                row.Add(NumCell(p.StrikeOuts, false, false, "mres-c-n64"));
                row.Add(NumCell(p.WalksHbp, false, false, "mres-c-n64"));
                row.Add(NumCell(p.Runs, true, false, "mres-c-n64"));
                t.Add(row);
            }
            return t;
        }

        // 打席結果: 打順 22｜守備 26｜打者名｜1〜N打席目（各82）。列の間隔6px。
        private static VisualElement PaTable(MatchResultSideView side)
        {
            var t = Table("mres-patable");
            var head = Row("mres-tr", "mres-tr--head");
            head.Add(Cell("", "mres-c-ord"));
            head.Add(Cell("", "mres-c-pos"));
            head.Add(Cell("打者", "mres-c-name", "mres-th"));
            for (var i = 1; i <= side.PaColumns; i++) head.Add(Cell(i + "打席", "mres-c-pa", "mres-th", "mres-th--center"));
            t.Add(head);
            foreach (var r in side.PaRows)
            {
                var row = Row("mres-tr");
                row.Add(Cell(r.Order > 0 ? r.Order.ToString() : "", "mres-c-ord", "mres-ord"));
                row.Add(PosChip(r.Pos, r.PosKind));
                row.Add(Cell(r.Name, "mres-c-name", "mres-name"));
                foreach (var c in r.Cells) row.Add(PaChip(c));
                t.Add(row);
            }
            return t;
        }

        private static VisualElement PaChip(MatchResultPaCell c)
        {
            var cls = c.Kind switch
            {
                MatchResultPaKind.Hit => "mres-pa--hit",
                MatchResultPaKind.HomeRun => "mres-pa--hr",
                MatchResultPaKind.Walk => "mres-pa--walk",
                MatchResultPaKind.Error => "mres-pa--err",
                MatchResultPaKind.Other => "mres-pa--other",
                _ => "mres-pa--none",
            };
            var strong = c.Kind is MatchResultPaKind.Hit or MatchResultPaKind.HomeRun or MatchResultPaKind.Walk or MatchResultPaKind.Error;
            return Cell(c.Text, "mres-c-pa", "mres-pa", cls, strong ? "f-body-bd" : "f-body");
        }

        private static VisualElement PosChip(string text, MatchResultPosKind kind)
        {
            var wrap = new VisualElement();
            wrap.AddToClassList("mres-c-pos");
            if (string.IsNullOrEmpty(text)) return wrap;
            var cls = kind switch
            {
                MatchResultPosKind.Pitcher => "mside-pos--p",
                MatchResultPosKind.Catcher => "mside-pos--c",
                MatchResultPosKind.Infield => "mside-pos--if",
                MatchResultPosKind.Outfield => "mside-pos--of",
                MatchResultPosKind.Dh => "mside-pos--dh",
                _ => "mres-pos--entry",
            };
            // 位置が2つ以上つながる行は、文字数ぶん欄を広げる（文字は詰めない）。
            var chip = Cell(text, "mside-pos", "mres-pos", cls, "f-body-bd");
            if (text.Length >= 2) chip.AddToClassList(text.Length switch { 2 => "mres-pos--w2", 3 => "mres-pos--w3", _ => "mres-pos--w4" });
            wrap.Add(chip);
            return wrap;
        }

        /// <summary>数字の1セル（固定幅・右寄せ）。0 は薄く、strong は1以上で太字の墨、red は1以上で太字の朱。</summary>
        private static Label NumCell(int v, bool strong, bool red, string widthClass)
        {
            var c = Cell(v.ToString(), widthClass, "mres-num");
            if (v == 0) { c.AddToClassList("mres-zero"); c.AddToClassList("f-num"); return c; }
            c.AddToClassList(red ? "mres-hr" : "mres-ink");
            c.AddToClassList(strong || red ? "f-num-bd" : "f-num");
            return c;
        }

        private static VisualElement Table(string extra = null)
        {
            var t = new VisualElement();
            t.AddToClassList("mres-table");
            if (extra != null) t.AddToClassList(extra);
            return t;
        }

        private static VisualElement Row(params string[] classes)
        {
            var r = new VisualElement();
            foreach (var c in classes) r.AddToClassList(c);
            return r;
        }

        private static Label Cell(string text, params string[] classes)
        {
            var l = new Label(text ?? "");
            foreach (var c in classes) l.AddToClassList(c);
            return l;
        }

        // 「閉じる」: MatchLive から預かった後処理（大会反映＋ホームへ）を呼ぶ。
        // クリック配信中に同期 Show するとUITKのイベント木が壊れるため、遷移は必ず遅延切替を使う。
        private void OnClose()
        {
            // 後処理は BackgroundGameOp に載るが、これは単一スロットで実行中は Run が false を返して
            // 何もしない。ここで受けてしまうと預かった後処理（成績畳み込み・実戦成長・大会反映）ごと
            // 落ちたうえ、遷移も起きずに結果画面へ取り残される。実行中は受け付けない（issue #278）。
            if (BackgroundGameOp.Running) return;
            var cb = _onClose;
            _onClose = null;
            if (cb != null) cb();
            else ScreenRouter.Instance?.ShowDeferred("HomeDashboard");
        }
    }
}
