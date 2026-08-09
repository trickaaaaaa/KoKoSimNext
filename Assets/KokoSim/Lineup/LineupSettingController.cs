using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Unity.Components; // 部品辞書（RankChip / AbilityRow）

namespace KokoSim.Unity.Lineup
{
    /// <summary>
    /// 試合前スタメン設定のコントローラ。UIDocument（sourceAsset = LineupSetting.uxml）へ LineupSettingState を
    /// バインドし、打順1〜9・守備位置・DH・先発・控えの編集と、選んだ2選手の能力比較＋守備適性＋通算/今大会成績を
    /// 描画・操作する。見た目は部品辞書（components.uss）＋トークンのみ。ロジックは State/エンジンに委ねる。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class LineupSettingController : MonoBehaviour
    {
        // 守備適性ミニダイヤ（比較パネル内）のノード配置（top%, left%）。メンバー設定と同一。
        private LineupSettingState _state;
        private VisualElement _root;

        private void OnEnable()
        {
            _state = new LineupSettingState();
            _root = GetComponent<UIDocument>().rootVisualElement;

            Click("lu-ok", OnOk);
            Click("lu-cancel", OnCancel);
            var sw = _root.Q<VisualElement>("lu-dh-switch");
            if (sw != null) sw.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _state.ToggleDh(); Render(); });

            Render();
        }

        private void Render()
        {
            var v = _state.BuildView();

            // 画面専用ヘッダーに「この試合」の文脈を出す（共通ナビは非表示）。大会外表示（テスト等）は空。
            var gs = KokoSim.Unity.Shell.GameSession.Current;
            var runner = gs.Runner;
            var match = "";
            if (gs.PendingMatchIsPractice)
            {
                // 練習試合も同じスタメン設定画面を通る（issue #269）。文脈は大会名ではなく「練習試合」。
                match = "練習試合　vs " + (gs.PracticeOpponent?.Name ?? "");
            }
            else if (runner != null)
            {
                match = gs.Title;
                if (!runner.Finished && runner.NextOpponent != null)
                    match += "　" + runner.RoundName + "　vs " + runner.NextOpponent.Name;
            }
            SetText("lu-match", match);

            // 編成不備（ベンチ入り9人未満）は警告を出して確定を塞ぐ。正常時は警告行そのものを消す。
            var warn = _root.Q<Label>("lu-warn");
            if (warn != null)
            {
                warn.text = v.WarnText;
                warn.style.display = string.IsNullOrEmpty(v.WarnText) ? DisplayStyle.None : DisplayStyle.Flex;
            }
            _root.Q<Button>("lu-ok")?.SetEnabled(v.CanConfirm);
            SetText("lu-hint", v.HintText);

            var rank = _root.Q<VisualElement>("lu-team-rank");
            if (rank != null) { rank.Clear(); rank.Add(UiComponents.RankChip(v.TeamRankGrade)); }

            BuildRows(v);
            BuildDhBar(v);
            BuildBench(v);
            BuildCompare(v);
        }

        // ── 打順テーブル ──
        private void BuildRows(LineupSettingView v)
        {
            var host = _root.Q<VisualElement>("lu-rows");
            if (host == null) return;
            host.Clear();
            foreach (var r in v.Rows)
            {
                var row = new VisualElement();
                row.AddToClassList("lineup-row");
                if (r.IsPicked) row.AddToClassList("lineup-row--picked");

                var ord = new Label(r.Order.ToString());
                ord.AddToClassList("lineup-row__ord");
                ord.AddToClassList("f-num-bd");
                row.Add(ord);

                var num = new Label(NumText(r.UniformNumber));
                num.AddToClassList("num-badge");
                num.AddToClassList("num-badge--sm");
                num.AddToClassList("f-num");
                row.Add(num);

                var posCell = new VisualElement();
                posCell.AddToClassList("lineup-row__pos");
                posCell.Add(PosChip(r));
                row.Add(posCell);

                if (r.IsCaptain) row.Add(CaptainMark());
                var name = new Label(r.Name);
                name.AddToClassList("lineup-row__name");
                row.Add(name);

                // 選手名と「学年・投打」の間に通算の簡易成績（控えめグレー・名前より目立たせない）。
                var stat = new Label(r.StatText);
                stat.AddToClassList("lineup-row__stat");
                row.Add(stat);

                var info = new Label(r.GradeLabel + "  " + r.HandLabel);
                info.AddToClassList("lineup-row__info");
                row.Add(info);

                // 故障中は警告チップ（部品辞書の .chip--out を流用＝新規の見た目を作らない）。
                if (r.Injury.Length > 0) row.Add(InjuryChip(r.Injury));

                // 選手個人の総合ランクチップは出さない（Issue #30 で廃止・#220 の決定1-D と同じ扱い）。
                // カテゴリ別ランクは右の比較カードで見る。

                var slot = r.Order - 1;
                var pidx = r.PlayerIndex;
                row.RegisterCallback<ClickEvent>(_ => { _state.ClickRow(slot); Render(); });
                row.RegisterCallback<PointerEnterEvent>(_ => { _state.SetHovered(pidx); RenderCompareOnly(); });
                row.RegisterCallback<PointerLeaveEvent>(_ => { _state.ClearHovered(pidx); RenderCompareOnly(); });
                host.Add(row);
            }
        }

        private VisualElement PosChip(LineupRowView r)
        {
            var chip = new Label(r.PosKanji);
            chip.AddToClassList("pos-chip");
            // 「指」だけ色を分ける（守備に就かない枠）。「投」は他の守備位置と同格＝専用の見た目を持たない。
            if (r.IsDhSlot) chip.AddToClassList("pos-chip--dh");
            if (r.IsPosPicked) chip.AddToClassList("pos-chip--picked");
            // 打順行と同じ操作モデル：1回目のクリックで選択、2回目に別スロットのチップで守備位置を入替。
            // 9枠すべてが対象＝「投」も動かせる（任意の選手を投手にできる, issue #233）。
            // 行クリック（打順入替）へ伝播させないよう停止する。
            var slot = r.Order - 1;
            chip.RegisterCallback<ClickEvent>(evt => { evt.StopPropagation(); _state.ClickPosition(slot); Render(); });
            return chip;
        }

        // ── DH制トグル＋先発投手 ──
        private void BuildDhBar(LineupSettingView v)
        {
            var sw = _root.Q<VisualElement>("lu-dh-switch");
            if (sw != null) sw.EnableInClassList("switch--on", v.UsesDh);

            var sp = _root.Q<VisualElement>("lu-sp");
            if (sp == null) return;
            sp.Clear();
            if (!v.UsesDh) return;   // DH制のときだけ先発投手ピルを出す

            var pill = new VisualElement();
            pill.AddToClassList("sp-pill");
            if (v.StartingPitcherPicked) pill.AddToClassList("sp-pill--picked");
            var cap = new Label("先発");
            cap.AddToClassList("sp-pill__cap");
            pill.Add(cap);
            var name = new Label(v.StartingPitcherName);
            name.AddToClassList("sp-pill__name");
            pill.Add(name);
            pill.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _state.ClickStartingPitcher(); Render(); });
            sp.Add(pill);
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
                row.AddToClassList("bench-row");
                if (b.IsPicked) row.AddToClassList("bench-row--picked");

                var num = new Label(NumText(b.UniformNumber));
                num.AddToClassList("num-badge");
                num.AddToClassList("num-badge--sm");
                num.AddToClassList("f-num");
                row.Add(num);

                var name = new Label(b.Name);
                name.AddToClassList("bench-row__name");
                row.Add(name);
                // 控えの投/野タグは出さない（役割はプレイヤーが決める, Issue #93）。識別は背番号と名前。
                if (b.Injury.Length > 0) row.Add(InjuryChip(b.Injury));
                // 打順行と同じく総合ランクチップは出さない（Issue #30/#270）。

                var idx = b.Index;
                row.RegisterCallback<ClickEvent>(_ => { _state.ClickBench(idx); Render(); });
                row.RegisterCallback<PointerEnterEvent>(_ => { _state.SetHovered(idx); RenderCompareOnly(); });
                row.RegisterCallback<PointerLeaveEvent>(_ => { _state.ClearHovered(idx); RenderCompareOnly(); });
                host.Add(row);
            }
        }

        // ── 2選手比較＋成績 ──
        private void BuildCompare(LineupSettingView v)
        {
            FillCompareCard("lu-cmp-a", true, v.CardA, v.ShowPitchChart);
            FillCompareCard("lu-cmp-b", false, v.CardB, v.ShowPitchChart);

            var tabs = _root.Q<VisualElement>("lu-cmp-tabs");
            if (tabs != null)
            {
                tabs.Clear();
                for (var i = 0; i < v.TabLabels.Length; i++)
                {
                    var t = new Label(v.TabLabels[i]);
                    t.AddToClassList("lu-tab");
                    if (v.Tab == i) t.AddToClassList("lu-tab--on");
                    var tab = i;
                    t.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _state.SetTab(tab); Render(); });
                    tabs.Add(t);
                }
            }

            var rows = _root.Q<VisualElement>("lu-cmp-rows");
            if (rows != null)
            {
                // 投手能力タブは球速を km/h で出すので値セルを表ごと広げる（issue #246）。
                UiComponents.SetCompareUnitColumn(rows, KokoSim.Unity.Shell.PlayerCompareTabs.HasUnitValues(v.Tab));
                // 行高は試合中の交代モーダルと同じ密な組（32px基準・UI原則①, issue #452）。
                UiComponents.SetCompareDense(rows, true);
                rows.Clear();
                if (v.Rows2.Count == 0)
                {
                    var empty = new Label("選手をクリックで選択 → 別の選手にカーソルを合わせて比較");
                    empty.AddToClassList("lu-cmp-empty");
                    rows.Add(empty);
                }
                else
                {
                    // タブ自体が「野手能力」「投手能力」を示しているため、表側の重複見出しは出さない（issue #326③）。
                    // 比較ヘッダも出さない（issue #234）。意味のない「A」「B」の見出しは置かず、どちらの
                    // カラムかは真上に並ぶ比較カードの選手名＋カラムのアクセント色で判別する
                    // （メンバー設定画面と同じ形）。
                    foreach (var r in v.Rows2) rows.Add(CompareRowEl(r));
                }
            }

            BuildApt(v);
            BuildStats(v);
        }

        private void BuildApt(LineupSettingView v)
        {
            var host = _root.Q<VisualElement>("lu-cmp-apt");
            if (host == null) return;
            host.Clear();
            if (!v.ShowApt) return;

            // 盤面（apt-field 210px×2枚＋見出し＝252px）から帯（約90px）へ差し替えた（issue #452・案A, 2026-08-09）。
            // 3画面で同じ部品にするため。理由と副次効果は MemberSettingController.BuildApt と同じ。
            host.Add(UiComponents.AptitudeStrip(
                v.HasAptA ? v.AptA : null, v.HasAptB ? v.AptB : null, "1人目", "2人目"));
        }

        // 通算/今大会成績（選んだ選手ごとに1枚＝2列）。
        private void BuildStats(LineupSettingView v)
        {
            var host = _root.Q<VisualElement>("lu-cmp-stats");
            if (host == null) return;
            host.Clear();
            if (!v.CardA.Present && !v.CardB.Present) return;

            var title = new Label("成績");
            title.AddToClassList("lu-cmp-group");
            host.Add(title);
            if (v.CardA.Present) host.Add(StatCard(v.CardA));
            if (v.CardB.Present) host.Add(StatCard(v.CardB));
        }

        private VisualElement StatCard(CompareCardView card)
        {
            var wrap = new VisualElement();
            var name = new Label(card.Name);
            name.AddToClassList("lu-stat-name");
            wrap.Add(name);

            // 成績表（issue #388）: ラベルは左に1列だけ、スコープ（通算/公式戦/今大会）を横に並べる。
            var table = new VisualElement();
            table.AddToClassList("stat-table");

            // 見出し行（左上は空＝ラベル列の頭）。
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
                    tr.Add(val);
                }
                table.Add(tr);
            }

            wrap.Add(table);
            return wrap;
        }

        private void FillCompareCard(string host, bool isA, CompareCardView card, bool showPitchChart)
        {
            var el = _root.Q<VisualElement>(host);
            if (el == null) return;
            el.Clear();
            if (!card.Present)
            {
                var empty = new Label(isA ? "選手をクリック" : "選手にカーソル");
                empty.AddToClassList("cmp-card__empty");
                el.Add(empty);
                return;
            }
            var name = new Label(card.Name);
            name.AddToClassList("cmp-card__name");
            el.Add(name);

            var meta = new VisualElement();
            meta.AddToClassList("cmp-card__meta");
            meta.Add(GradeLabel(card.GradeLabel));
            if (card.IsCaptain)
            {
                var cap = new Label("主将");
                cap.AddToClassList("chip");
                cap.AddToClassList("chip--tag");
                cap.AddToClassList("chip--out");
                meta.Add(cap);
            }
            el.Add(meta);
            // カテゴリ別ランク（打撃力/走力/守備力/投手力）。総合ランク1チップの置換先＝#30 の決定。
            // 2行固定＋小サイズ（issue #326①）: カード幅が狭く1行4組では中途半端に折り返すため。
            // 見た目はメンバー設定の比較カードと同一部品（UI原則⑤）。
            el.Add(UiComponents.CategoryRankChipsTwoRow(card.Strength, small: true));

            // 持ち球のミニ球種変化チャート（部品 PitchChartView・compact, issue #94 案C）。
            // 投手能力タブのときだけ出す（タブでゲート・投/野区分ではゲートしない, Issue #93/#293）。
            if (showPitchChart)
            {
                var chartHost = new VisualElement();
                chartHost.AddToClassList("cmp-card__pitch");
                el.Add(chartHost);
                new PitchChartView(chartHost, compact: true).SetData(card.Pitches);
            }
        }

        // 行の見た目は部品辞書（UiComponents.CompareRow）に集約。ここは ViewModel の詰め替えだけ。
        private static VisualElement CompareRowEl(KokoSim.Unity.Shell.PlayerCompareRow r)
            => UiComponents.CompareRow(new CompareRowData
            {
                Label = r.Label,
                ValueA = r.ValueA, ValueB = r.ValueB,
                HasA = r.HasA, HasB = r.HasB,
                Winner = r.Winner,
                TextA = r.TextA, TextB = r.TextB,   // 球速の km/h テキスト（issue #94）
                FillA = r.FillA, FillB = r.FillB,
                GradeA = r.GradeA, GradeB = r.GradeB,   // ランク併記（issue #326②）
                HideBar = r.HideBar,   // 弾道のタイプ表示（issue #219）
            });

        // ── 確定操作 ──
        private void OnOk()
        {
            if (!_state.CanConfirm) return;   // 編成不備（ベンチ入り9人未満）では確定させない
            KokoSim.Unity.Shell.GameSession.Current.Lineup = _state.ToLineupSpec();
            // AwaitingMatchStart は立てたまま → 試合開始前画面（対戦カード）を挟み、そこの「試合開始」が
            // 直接自校戦を消化する（issue #7 / #325。ホームは中継しない）。
            // クリック配信中に同期 Show すると UITK のイベント木が壊れて全画面が非アクティブに落ちるため、
            // ScreenRouter 自身が用意する遅延切替（次フレーム）を使う（ScreenRouter の _pending 参照）。
            KokoSim.Unity.Shell.ScreenRouter.Instance?.ShowDeferred("MatchPreview");
        }

        private void OnCancel()
        {
            var gs = KokoSim.Unity.Shell.GameSession.Current;
            gs.AwaitingMatchStart = false;    // 試合は消化しない
            // 練習試合はここで取りやめ＝申込ごと無かったことにする（費用・週枠は未確定なので何も戻さない）。
            gs.ClearPracticeMatchFlow();
            KokoSim.Unity.Shell.ScreenRouter.Instance?.ShowDeferred("HomeDashboard");
        }

        private void RenderCompareOnly() => BuildCompare(_state.BuildView());

        // ── 部品ヘルパ ──
        /// <summary>故障チップ（設計書03 §3.5・UI原則②: 怪我は本当の警告なので警告色を使ってよい）。</summary>
        private static Label InjuryChip(string text)
        {
            var l = new Label(text);
            l.AddToClassList("chip");
            l.AddToClassList("chip--tag");
            l.AddToClassList("chip--out");
            return l;
        }

        private static Label CaptainMark()
        {
            var l = new Label("主");
            l.style.fontSize = 11;
            l.style.color = new Color(0.961f, 0.776f, 0.290f); // --color-lamp
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.marginRight = 3;
            return l;
        }

        private static Label GradeLabel(string text)
        {
            var l = new Label(text);
            l.style.fontSize = 11;
            l.style.color = new Color(0.60f, 0.65f, 0.63f);
            l.style.marginRight = 5;
            return l;
        }



        private static string NumText(int n) => n > 0 ? n.ToString() : "";

        private static VisualElement NewChild(string cls)
        {
            var el = new VisualElement();
            el.AddToClassList(cls);
            return el;
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
