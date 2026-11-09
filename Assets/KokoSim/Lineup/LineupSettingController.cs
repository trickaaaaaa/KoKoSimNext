using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Engine.Players;    // Condition（調子・ConditionFaceの入力型）
using KokoSim.Unity.Components; // 部品辞書（RankChip / PositionChip / NumberPatch / CaptainStamp / StatBarRow）
using KokoSim.Unity.Shell;

namespace KokoSim.Unity.Lineup
{
    /// <summary>
    /// 試合前オーダー（スタメン設定）のコントローラ。UIDocument（sourceAsset = LineupSetting.uxml）へ
    /// LineupSettingState をバインドし、打順1〜9・守備位置・DH・先発・控えの編集と、
    /// 選んだ2選手の入れ替え比較（能力の横棒＋守備適性＋持ち球＋成績）を描画・操作する。
    /// 見た目はスコアブック試作スキン（scorebook.uss の部品とトークン）。ロジックは State/エンジンに委ねる。
    /// 差し色（スクールカラー）は描画のたびに SchoolAccent.Apply で自校の色を流し込む。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class LineupSettingController : MonoBehaviour
    {
        private LineupSettingState _state;
        private VisualElement _root;
        private Color _accent = SchoolAccent.Default;

        private void OnEnable()
        {
            _state = new LineupSettingState();
            _root = GetComponent<UIDocument>().rootVisualElement;
            _accent = SchoolAccent.ForManager();

            Click("lu-ok", OnOk);
            Click("lu-cancel", OnCancel);
            // DH切替（2択）。選ばれていない側を押したときだけ切り替える。
            _root.Q<Label>("lu-dh-on")?.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); SetDh(true); });
            _root.Q<Label>("lu-dh-off")?.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); SetDh(false); });

            Render();
        }

        private void SetDh(bool on)
        {
            if (_state.BuildView().UsesDh == on) return;
            _state.ToggleDh();
            Render();
        }

        private void Render()
        {
            var v = _state.BuildView();
            BuildHeader(v);

            // 編成不備・故障者起用の警告。編成不備は確定も塞ぐ。正常時は警告行そのものを消す。
            var warn = _root.Q<Label>("lu-warn");
            if (warn != null)
            {
                warn.text = v.WarnText;
                warn.style.display = string.IsNullOrEmpty(v.WarnText) ? DisplayStyle.None : DisplayStyle.Flex;
            }
            _root.Q<Button>("lu-ok")?.SetEnabled(v.CanConfirm);

            _root.Q<Label>("lu-dh-on")?.EnableInClassList("sbk-seg__opt--on", v.UsesDh);
            _root.Q<Label>("lu-dh-off")?.EnableInClassList("sbk-seg__opt--on", !v.UsesDh);

            BuildRows(v);
            BuildStartingPitcher(v);
            BuildBench(v);
            BuildCompare(v);
            SchoolAccent.Apply(_root, _accent);
        }

        // ── ヘッダー：大会と回戦（補足色）＋「vs 対戦校」（Medium）。大会外表示（テスト等）は空。 ──
        private void BuildHeader(LineupSettingView v)
        {
            var gs = GameSession.Current;
            var runner = gs.Runner;
            string match = "", opp = "";
            if (gs.PendingMatchIsPractice)
            {
                // 練習試合も同じ画面を通る（issue #269）。文脈は大会名ではなく「練習試合」。
                match = "練習試合";
                opp = "vs " + (gs.PracticeOpponent?.Name ?? "");
            }
            else if (runner != null)
            {
                match = gs.Title;
                if (!runner.Finished && runner.NextOpponent != null)
                {
                    match += "　" + runner.RoundName;
                    opp = "vs " + runner.NextOpponent.Name;
                }
            }
            SetText("lu-match", match);
            SetText("lu-opp", opp);

            var rank = _root.Q<VisualElement>("lu-team-rank");
            if (rank != null)
            {
                rank.Clear();
                var chip = UiComponents.RankChip(v.TeamRankGrade);
                chip.AddToClassList("rank-chip--sbk36");
                rank.Add(chip);
                var num = new Label(v.TeamOverallValue.ToString());
                num.AddToClassList("lu-head__value");
                num.AddToClassList("f-num-bd");
                rank.Add(num);
            }
        }

        // ── スタメン9行 ──
        private void BuildRows(LineupSettingView v)
        {
            var host = _root.Q<VisualElement>("lu-rows");
            if (host == null) return;
            host.Clear();
            foreach (var r in v.Rows)
            {
                var row = new VisualElement();
                row.AddToClassList("sbk-card");
                row.AddToClassList("lu-row");
                if (r.IsPicked) { row.AddToClassList("sbk-card--sel"); row.AddToClassList("acc-bd"); }

                var ord = new Label(r.Order.ToString());
                ord.AddToClassList("lu-c-ord");
                ord.AddToClassList("lu-row__ord");
                ord.AddToClassList("f-display");
                if (r.IsPicked) ord.AddToClassList("acc-fg");
                row.Add(ord);

                row.Add(Cell("lu-c-num", new NumberPatch(r.UniformNumber)));
                row.Add(PosChip(r));
                row.Add(NameCell(r.Name, r.IsCaptain, r.Injury));

                var info = new Label(r.GradeLabel + " " + r.HandLabel);
                info.AddToClassList("lu-c-info");
                info.AddToClassList("lu-row__info");
                row.Add(info);

                var avg = new Label(r.AvgText);
                avg.AddToClassList("lu-c-avg");
                avg.AddToClassList("lu-row__avg");
                avg.AddToClassList(r.AvgHigh ? "f-num-bd" : "f-num");
                if (r.AvgHigh) avg.AddToClassList("acc-fg");
                row.Add(avg);
                row.Add(NumCell("lu-c-hr", r.HomeRunsText));
                row.Add(NumCell("lu-c-rbi", r.RbiText));
                row.Add(Cell("lu-c-cond", Face(r.Condition, "cond-face--26")));

                var slot = r.Order - 1;
                var pidx = r.PlayerIndex;
                row.RegisterCallback<ClickEvent>(_ => { _state.ClickRow(slot); Render(); });
                row.RegisterCallback<PointerEnterEvent>(_ => { _state.SetHovered(pidx); RenderCompareOnly(); });
                row.RegisterCallback<PointerLeaveEvent>(_ => { _state.ClearHovered(pidx); RenderCompareOnly(); });
                host.Add(row);
            }
        }

        // 守備位置チップ：1回目のクリックで選択、2回目に別の枠のチップで守備位置を入替（9枠すべて対象＝「投」も動かせる,
        // issue #233）。行クリック（打順入替）へ伝播させない。
        private VisualElement PosChip(LineupRowView r)
        {
            var chip = new PositionChip(r.PosKanji) { Clickable = true, Picked = r.IsPosPicked };
            chip.AddToClassList("lu-c-pos");
            var slot = r.Order - 1;
            chip.RegisterCallback<ClickEvent>(evt => { evt.StopPropagation(); _state.ClickPosition(slot); Render(); });
            return chip;
        }

        // 選手名（明朝）＋主将マーク。故障中は名前の下に怪我タグ（2段）。
        private static VisualElement NameCell(string name, bool captain, string injury)
        {
            var cell = new VisualElement();
            cell.AddToClassList("lu-c-name");
            cell.AddToClassList("lu-row__name-block");

            var line = new VisualElement();
            line.AddToClassList("lu-row__name-cell");
            var n = new Label(Nm(name));
            n.AddToClassList("lu-row__name");
            n.AddToClassList("f-display");
            line.Add(n);
            if (captain) line.Add(new CaptainStamp());
            cell.Add(line);

            if (!string.IsNullOrEmpty(injury)) cell.Add(InjuryTag(injury));
            return cell;
        }

        // ── 先発投手カード（DHの有無を問わず出す。カード全体がボタン） ──
        private void BuildStartingPitcher(LineupSettingView v)
        {
            var host = _root.Q<VisualElement>("lu-sp");
            if (host == null) return;
            host.Clear();
            if (!v.HasStartingPitcher) return;

            var card = new VisualElement();
            card.AddToClassList("sbk-card");
            card.AddToClassList("lu-sp-card");
            if (v.StartingPitcherPicked) { card.AddToClassList("sbk-card--sel"); card.AddToClassList("acc-bd"); }

            var cap = new Label("先発");
            cap.AddToClassList("lu-c-ord");
            cap.AddToClassList("lu-sp-card__cap");
            card.Add(cap);
            card.Add(Cell("lu-c-num", new NumberPatch(v.StartingPitcherNumber)));
            var pos = new PositionChip("投");
            pos.AddToClassList("lu-c-pos");
            card.Add(pos);

            // 名前のブロック：明朝の名前（20px）の下に学年・投打（14px）。故障中は学年の行に怪我タグ。
            var block = new VisualElement();
            block.AddToClassList("lu-c-name");
            var line = new VisualElement();
            line.AddToClassList("lu-row__name-cell");
            var n = new Label(Nm(v.StartingPitcherName));
            n.AddToClassList("lu-row__name");
            n.AddToClassList("f-display");
            line.Add(n);
            if (v.StartingPitcherIsCaptain) line.Add(new CaptainStamp());
            block.Add(line);
            var sub = new VisualElement();
            sub.AddToClassList("lu-row__name-cell");
            var info = new Label(v.StartingPitcherGradeLabel + " " + v.StartingPitcherHandLabel);
            info.AddToClassList("lu-sp-card__info");
            sub.Add(info);
            if (!string.IsNullOrEmpty(v.StartingPitcherInjury))
            {
                var tag = InjuryTag(v.StartingPitcherInjury);
                tag.style.marginLeft = 8;
                sub.Add(tag);
            }
            block.Add(sub);
            card.Add(block);

            // 成績：「防 1.74　回 10 1/3　振 8」。ラベル（補足色13px）と数値（Oswald 17px）を別の Label に組む。
            var stats = new VisualElement();
            stats.AddToClassList("lu-sp-stats");
            StatPair(stats, "防", v.StartingPitcherEraText);
            StatPair(stats, "回", v.StartingPitcherInningsText);
            StatPair(stats, "振", v.StartingPitcherStrikeoutsText);
            card.Add(stats);
            card.Add(Cell("lu-c-cond", Face(v.StartingPitcherCondition, "cond-face--26")));

            var spIdx = v.StartingPitcherIndex;
            card.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _state.ClickStartingPitcher(); Render(); });
            card.RegisterCallback<PointerEnterEvent>(_ => { _state.SetHovered(spIdx); RenderCompareOnly(); });
            card.RegisterCallback<PointerLeaveEvent>(_ => { _state.ClearHovered(spIdx); RenderCompareOnly(); });
            host.Add(card);
        }

        private static void StatPair(VisualElement host, string label, string value)
        {
            var l = new Label(label);
            l.AddToClassList("lu-sp-stats__lab");
            host.Add(l);
            // 投球回の端数（⅓ ⅔）はどの書体にも字形が無いので、整数部の右上に小さな「1/3」を添えて組む。
            var frac = "";
            if (value.EndsWith("⅓")) frac = "1/3";
            else if (value.EndsWith("⅔")) frac = "2/3";
            var whole = frac.Length > 0 ? value.Substring(0, value.Length - 1) : value;
            var val = new Label(whole);
            val.AddToClassList("lu-sp-stats__val");
            val.AddToClassList("f-num-bd");
            host.Add(val);
            if (frac.Length > 0)
            {
                var f = new Label(frac);
                f.AddToClassList("lu-sp-stats__frac");
                f.AddToClassList("f-num-bd");
                host.Add(f);
            }
        }

        // ── 控え ──
        private void BuildBench(LineupSettingView v)
        {
            var host = _root.Q<VisualElement>("lu-bench");
            if (host == null) return;
            host.Clear();
            foreach (var b in v.Bench)
            {
                var row = new VisualElement();
                row.AddToClassList("sbk-card");
                row.AddToClassList("lu-bench-row");
                // 選んだ控えは墨の枠（監督決定⑥）。比較パネルの「比較相手」と同じ色。
                if (b.IsPicked) row.AddToClassList("sbk-card--sel-ink");

                row.Add(Cell("lu-c-num", new NumberPatch(b.UniformNumber)));

                // 控えの投/野タグは出さない（役割はプレイヤーが決める, Issue #93）。識別は背番号と名前。
                var block = new VisualElement();
                block.AddToClassList("lu-c-name");
                block.AddToClassList("lu-row__name-block");
                var name = new Label(Nm(b.Name));
                name.AddToClassList("lu-bench-row__name");
                name.AddToClassList("f-display");
                block.Add(name);
                if (b.Injury.Length > 0) block.Add(InjuryTag(b.Injury));
                row.Add(block);

                var cond = new VisualElement();
                cond.AddToClassList("lu-bench-row__cond");
                cond.Add(Face(b.Condition, "cond-face--24"));
                row.Add(cond);

                var idx = b.Index;
                row.RegisterCallback<ClickEvent>(_ => { _state.ClickBench(idx); Render(); });
                row.RegisterCallback<PointerEnterEvent>(_ => { _state.SetHovered(idx); RenderCompareOnly(); });
                row.RegisterCallback<PointerLeaveEvent>(_ => { _state.ClearHovered(idx); RenderCompareOnly(); });
                host.Add(row);
            }
        }

        // ── 入れ替え比較（左＝選択中＝自チーム側の色、右＝ホバー中＝墨） ──
        private void BuildCompare(LineupSettingView v)
        {
            BuildTabs(v);
            BuildCompareHead(v);

            var rows = _root.Q<VisualElement>("lu-cmp-rows");
            if (rows != null)
            {
                rows.Clear();
                // 操作説明は出さない（未選択でも1番打者が片側に出る＝空にならない）。
                foreach (var r in v.Rows2) rows.Add(CompareTable.Row(r));
            }

            BuildApt(v);
            BuildPitchCharts(v);
            BuildStats(v);
        }

        private void BuildTabs(LineupSettingView v)
        {
            var tabs = _root.Q<VisualElement>("lu-cmp-tabs");
            if (tabs == null) return;
            tabs.Clear();
            for (var i = 0; i < v.TabLabels.Length; i++)
            {
                var t = new Label(v.TabLabels[i]);
                t.AddToClassList("sbk-seg__opt");
                t.AddToClassList("f-body-bd");
                if (v.Tab == i) t.AddToClassList("sbk-seg__opt--on");
                var tab = i;
                t.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _state.SetTab(tab); Render(); });
                tabs.Add(t);
            }
        }

        private void BuildCompareHead(LineupSettingView v)
        {
            var head = _root.Q<VisualElement>("lu-cmp-head");
            if (head == null) return;
            head.Clear();
            head.Add(CompareTable.Head(HeadOf(v.CardA), HeadOf(v.CardB)));
        }

        private static CompareTable.PlayerHeadData HeadOf(CompareCardView card) => new CompareTable.PlayerHeadData
        {
            Present = card.Present,
            Name = card.Name,
            Meta = card.GradeLabel + "　" + card.RoleText,
            Strength = card.Strength,
            Skills = card.Skills,
        };

        private void BuildApt(LineupSettingView v)
        {
            var host = _root.Q<VisualElement>("lu-cmp-apt");
            if (host == null) return;
            host.Clear();
            host.EnableInClassList("lu-hidden", !v.ShowApt);
            if (!v.ShowApt) return;
            // 判断画面は実効守備力ランク一本（issue #371）。行見出しは姓。
            host.Add(UiComponents.AptitudeStrip(
                v.HasAptA ? v.AptA : null, v.HasAptB ? v.AptB : null,
                v.CardA.Present ? v.CardA.ShortName : "", v.CardB.Present ? v.CardB.ShortName : "",
                tight: true));
        }

        // 持ち球のミニ球種変化チャート（投手能力タブのときだけ・タブでゲートする, Issue #93/#293）。
        private void BuildPitchCharts(LineupSettingView v)
        {
            var host = _root.Q<VisualElement>("lu-cmp-chart");
            if (host == null) return;
            host.Clear();
            if (!v.ShowPitchChart || (!v.CardA.Present && !v.CardB.Present)) return;
            host.Add(CompareTable.PitchCharts(v.CardA.Present, v.CardA.Pitches, v.CardB.Present, v.CardB.Pitches));
        }

        // 通算/公式戦/今大会の成績（選んだ選手ごとに1表。④で存置）。
        private void BuildStats(LineupSettingView v)
        {
            var host = _root.Q<VisualElement>("lu-cmp-stats");
            if (host == null) return;
            host.Clear();
            host.style.display = (v.CardA.Present || v.CardB.Present) ? DisplayStyle.Flex : DisplayStyle.None;
            if (v.CardA.Present) host.Add(StatCard(v.CardA));
            if (v.CardB.Present) host.Add(StatCard(v.CardB));
        }

        private static VisualElement StatCard(CompareCardView card)
        {
            var wrap = new VisualElement();
            var name = new Label(card.Name + " の成績");
            name.AddToClassList("lu-cmp-stat-name");
            wrap.Add(name);

            // 成績表（issue #388）: ラベルは左に1列だけ、スコープ（通算/公式戦/今大会）を横に並べる。
            var table = new VisualElement();
            table.AddToClassList("stat-table");
            var head = new VisualElement();
            head.AddToClassList("stat-table__row");
            head.AddToClassList("stat-table__row--head");
            var corner = new Label(string.Empty);
            corner.AddToClassList("stat-table__lab");
            head.Add(corner);
            foreach (var scope in card.Stats.Scopes)
            {
                var sc = new Label(scope);
                sc.AddToClassList("stat-table__scope");
                head.Add(sc);
            }
            table.Add(head);
            foreach (var row in card.Stats.Rows)
            {
                var tr = new VisualElement();
                tr.AddToClassList("stat-table__row");
                var lab = new Label(row.Label);
                lab.AddToClassList("stat-table__lab");
                tr.Add(lab);
                foreach (var value in row.Values)
                {
                    var val = new Label(value);
                    val.AddToClassList("stat-table__val");
                    val.AddToClassList("f-num");   // 値は数字か「—」だけ（書体3役の数字セル）
                    tr.Add(val);
                }
                table.Add(tr);
            }
            wrap.Add(table);
            return wrap;
        }

        // 人名の姓名の区切り（全角空白）は明朝＋字間で間延びするので、表示だけ半角に詰める。
        private static string Nm(string name) => (name ?? "").Replace('　', ' ');

        // ── 確定操作 ──
        private void OnOk()
        {
            if (!_state.CanConfirm) return;   // 編成不備（ベンチ入り9人未満）では確定させない
            var spec = _state.ToLineupSpec();
            GameSession.Current.Lineup = spec;
            // 次の試合のスタメン画面はここから始まる（issue lineup-carry-over-previous-game）。選手IDで持つのでセーブにも載る。
            GameSession.Current.LastLineup = KokoSim.Engine.Season.LineupMemory.FromSpec(spec);
            // AwaitingMatchStart は立てたまま → 試合開始前画面（対戦カード）を挟む（issue #7 / #325）。
            // クリック配信中に同期 Show すると UITK のイベント木が壊れるため遅延切替を使う。
            ScreenRouter.Instance?.ShowDeferred("MatchPreview");
        }

        private void OnCancel()
        {
            var gs = GameSession.Current;
            gs.AwaitingMatchStart = false;    // 試合は消化しない
            // 練習試合はここで取りやめ＝申込ごと無かったことにする（費用・週枠は未確定なので何も戻さない）。
            gs.ClearPracticeMatchFlow();
            ScreenRouter.Instance?.ShowDeferred("HomeDashboard");
        }

        private void RenderCompareOnly()
        {
            BuildCompare(_state.BuildView());
            SchoolAccent.Apply(_root.Q<VisualElement>(className: "lu-cmp"), _accent);
        }

        // ── 部品ヘルパ ──
        private static Label InjuryTag(string text)
        {
            var l = new Label(text);
            l.AddToClassList("sbk-injury");
            return l;
        }

        private static ConditionFace Face(Condition c, string sizeClass)
        {
            var face = new ConditionFace();
            face.AddToClassList(sizeClass);
            face.Set(c);
            return face;
        }

        private static VisualElement Cell(string cls, VisualElement child)
        {
            var cell = new VisualElement();
            cell.AddToClassList(cls);
            cell.Add(child);
            return cell;
        }

        private static Label NumCell(string cls, string text)
        {
            var l = new Label(text);
            l.AddToClassList(cls);
            l.AddToClassList("lu-row__cnt");
            l.AddToClassList("f-num");
            return l;
        }

        private void SetText(string name, string text)
        {
            var label = _root.Q<Label>(name);
            if (label != null) label.text = text;
        }

        private void Click(string name, System.Action action)
        {
            var btn = _root.Q<Button>(name);
            if (btn != null) btn.clicked += action;
        }
    }
}
