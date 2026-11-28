using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Unity.Components; // 部品辞書（RankChip / AbilityRow）

namespace KokoSim.Unity.Member
{
    /// <summary>
    /// メンバー設定のコントローラ（設計書06 §3.3b）。UIDocument（sourceAsset = MemberSetting.uxml）へ
    /// MemberSettingState をバインドし、背番号1〜20の割当と2選手比較を描画・操作する。
    /// 4列＝背番号1〜9（球場の絵の上のカード）｜10〜20（縦1列）｜ベンチ外｜2選手比較。
    /// 見た目は部品辞書（components.uss／scorebook.uss）＋トークンのみ。ロジックは State/エンジンに委ねる。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class MemberSettingController : MonoBehaviour
    {
        // 背番号1〜9のカードの中心（球場の絵の設計座標 556×700）。投捕一二三遊左中右の順。
        // 遊は二塁ベースの左下（三遊間寄り）、二は右下（一二塁間寄り）。外野の3人は扇形の内側。
        private static readonly Vector2[] CardCenter =
        {
            new Vector2(278f, 488f), new Vector2(278f, 640f), new Vector2(452f, 548f),
            new Vector2(398f, 418f), new Vector2(104f, 548f), new Vector2(158f, 418f),
            new Vector2(110f, 312f), new Vector2(278f, 252f), new Vector2(446f, 312f),
        };
        private const float CardW = 168f;
        private const float CardH = 56f;
        private const float PickFrame = 2.5f;   // 選択の枠の太さ（USS の .ms-fcard / .ms-row の border-width と同じ）

        private MemberSettingState _state;
        private VisualElement _root;
        private Button _advanceBtn;
        private Button _newTeamOk;               // 新チームの背番号決定の「背番号を確定」（issue #954）
        private VisualElement _fieldHost;
        private MemberFieldArtElement _field;
        private Color _accent = KokoSim.Unity.Shell.SchoolAccent.Default;   // 比較表の自チーム側の色（オーダー画面と同じ）

        private void OnEnable()
        {
            _state = new MemberSettingState();
            _accent = KokoSim.Unity.Shell.SchoolAccent.ForManager();
            _root = GetComponent<UIDocument>().rootVisualElement;

            // 球場の絵（設計座標で芝・土・ライン・塁を描く）をカードの背面へ。
            _fieldHost = _root.Q<VisualElement>("ms-field");
            if (_fieldHost != null)
            {
                _fieldHost.Q<MemberFieldArtElement>()?.RemoveFromHierarchy();   // 画面を開き直したときの二重挿入を防ぐ
                _field = new MemberFieldArtElement();
                _fieldHost.Insert(0, _field);
                _field.RegisterCallback<GeometryChangedEvent>(_ => PositionFieldCards());
            }

            Click("ms-auto", () => { _state.AutoAssign(); Render(); });

            // ホバーを戻すのは、カーソルが一覧（本体の4列）の外へ出たときだけ。枠・行・列どうしの隙間では戻さない。
            _root.Q<VisualElement>(className: "ms-body")?.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                if (_state.ClearHovered()) RenderCompareOnly();
            });

            // 共通トップバーの「今週を進める」を配線（issue #134: 以前は未配線で死にボタンだった）。
            // ScreenRouter が SetActive で付け外しするだけなので、OnDisable で必ず外す（多重登録＝多重進週の防止）。
            _advanceBtn = _root.Q<Button>("advance");
            if (_advanceBtn != null) _advanceBtn.clicked += OnAdvance;

            _newTeamOk = _root.Q<Button>("ms-newteam-ok");
            if (_newTeamOk != null) _newTeamOk.clicked += OnConfirmUniformNumbers;

            Render();
        }

        private void OnDisable()
        {
            if (_advanceBtn != null) _advanceBtn.clicked -= OnAdvance;
            if (_newTeamOk != null) _newTeamOk.clicked -= OnConfirmUniformNumbers;
        }

        // 新チームの背番号を確定してホームへ戻る（issue #954・案C）。割当は画面で編集済み＝待ちを下ろすだけ。
        private void OnConfirmUniformNumbers()
        {
            if (!KokoSim.Unity.Shell.UniformNumberService.Pending) return;
            KokoSim.Unity.Shell.UniformNumberService.Confirm();
            KokoSim.Unity.Shell.ScreenRouter.Instance?.ShowDeferred("HomeDashboard");
        }

        // 背番号決定待ちの間だけ上部帯を出し、トップバーの週送りを隠す（主ボタンは「背番号を確定」の1つだけ）。
        private void RenderNewTeamBand()
        {
            var pending = KokoSim.Unity.Shell.UniformNumberService.Pending;
            var band = _root.Q<VisualElement>("ms-newteam");
            if (band != null) band.EnableInClassList("ms-hidden", !pending);
            SetText("ms-newteam-lead", KokoSim.Unity.Shell.UniformNumberService.Lead);
            if (_advanceBtn != null) _advanceBtn.style.display = pending ? DisplayStyle.None : DisplayStyle.Flex;
        }

        // 全タブ共通の進週処理へ集約（大会モード中はホームへ回送して日送りへ引き継ぐ）。
        // 年度替わりで部員が入れ替わったら一覧を引き直す（issue #1250）。
        private void OnAdvance() => KokoSim.Unity.Shell.WeekAdvance.FromSideScreen(() =>
        {
            _state.SyncRoster();
            Render();
        });

        private void Render()
        {
            var v = _state.BuildView();

            KokoSim.Unity.Components.ScoreboardStrip.Fill(_root);
            KokoSim.Unity.Shell.TopBarMeters.Fill(_root);   // 部費残高・名声（ManagerService 単一ソース）
            var rank = _root.Q<VisualElement>("team-rank");
            if (rank != null)
            {
                rank.Clear();
                rank.Add(UiComponents.RankChip(v.TeamRankGrade));
            }

            RenderNewTeamBand();

            // 大会中は操作の入口をすべて止める（×・自動割当を隠す／押せなくする。選手の行は比較用に見るだけ）。
            _root.EnableInClassList("ms-locked", v.Locked);
            _root.Q<Button>("ms-auto")?.SetEnabled(!v.Locked);

            SetText("ms-assigned", v.AssignedCount.ToString());
            SetText("ms-out-count", v.BenchOutCount.ToString());

            BuildField(v);
            BuildBench(v);
            BuildPool(v);
            BuildCompare(v);

            // 選択の枠・主将の印へスクールカラーを流す（比較パネルは BuildCompare が流す）。
            foreach (var col in _root.Query<VisualElement>(className: "ms-col").ToList())
                KokoSim.Unity.Shell.SchoolAccent.Apply(col, _accent);
        }

        // ── 背番号1〜9：球場の絵の上にカード（168×56）を置く ──
        private void BuildField(MemberSettingView v)
        {
            var host = _root.Q<VisualElement>("ms-field-slots");
            if (host == null) return;
            host.Clear();
            for (var n = 1; n <= 9; n++)
            {
                var slot = v.Slots[n - 1];
                var card = new VisualElement();
                card.AddToClassList("ms-fcard");
                card.userData = n;   // 配置（PositionFieldCards）で中心座標を引くための添字

                if (slot.PlayerIndex >= 0)
                {
                    MarkPicked(card, slot.IsPicked);
                    card.Add(NumBadge(slot.Number));
                    card.Add(Who(slot.Name, slot.GradeLabel, slot.HandLabel, fitLongName: true));
                    card.Add(UiComponents.RankChip(slot.RankGrade));
                    if (slot.IsCaptain) card.Add(CaptainMark(corner: true));
                }
                else
                {
                    card.AddToClassList("ms-fcard--empty");
                    card.Add(VacantFrame());
                    card.Add(NumBadge(slot.Number, vacant: true));
                    card.Add(VacantLabel());
                }
                WireSlot(card, slot);
                host.Add(card);
            }
            PositionFieldCards();
        }

        // カードの中心を球場の絵の座標へ合わせる（絵が縮めば置き場所も同じ倍率で動く。カードの大きさは変えない）。
        private void PositionFieldCards()
        {
            if (_field == null || _field.Scale <= 0f) return;
            var host = _root.Q<VisualElement>("ms-field-slots");
            if (host == null) return;
            foreach (var child in host.Children())
            {
                if (!(child.userData is int n) || n < 1 || n > CardCenter.Length) continue;
                var c = _field.ToLocal(CardCenter[n - 1]);
                child.style.left = c.x - CardW * 0.5f;
                child.style.top = c.y - CardH * 0.5f;
            }
        }

        // ── 背番号10〜20：縦1列 ──
        private void BuildBench(MemberSettingView v)
        {
            var host = _root.Q<VisualElement>("ms-bench");
            if (host == null) return;
            host.Clear();
            for (var n = 10; n <= 20; n++)
            {
                var slot = v.Slots[n - 1];
                var row = new VisualElement();
                row.AddToClassList("ms-row");

                if (slot.PlayerIndex >= 0)
                {
                    MarkPicked(row, slot.IsPicked);
                    row.Add(NumBadge(slot.Number));
                    row.Add(Who(slot.Name, slot.GradeLabel, slot.HandLabel));
                    if (slot.IsCaptain) row.Add(CaptainMark());
                    row.Add(ClearButton(slot.Number));
                }
                else
                {
                    row.AddToClassList("ms-row--empty");
                    row.Add(VacantFrame());
                    row.Add(NumBadge(slot.Number, vacant: true));
                    row.Add(VacantLabel());
                }
                WireSlot(row, slot);
                host.Add(row);
            }
        }

        // 枠の配線（クリックで選択・配置、ホバーで比較の右へ）。球場のカードも10〜20の行も同じ。
        private void WireSlot(VisualElement el, SlotView slot)
        {
            var n = slot.Number;
            var pidx = slot.PlayerIndex;
            el.RegisterCallback<ClickEvent>(_ => { _state.ClickSlot(n); Render(); });
            el.RegisterCallback<PointerEnterEvent>(_ => { _state.SetHovered(pidx); RenderCompareOnly(); });
        }

        // カーソル移動での比較プレビューは比較パネルだけ更新（盤面は再構築しない＝ホバー対象がぶれない）。
        private void RenderCompareOnly() => BuildCompare(_state.BuildView());

        // ── ベンチ外：名前と学年・投打だけの行。0人のときは入れ物の中央に一言 ──
        private void BuildPool(MemberSettingView v)
        {
            var host = _root.Q<VisualElement>("ms-pool");
            if (host == null) return;
            host.Clear();
            var none = v.Pool.Count == 0;
            _root.Q<VisualElement>("ms-out-scroll")?.EnableInClassList("ms-hidden", none);
            _root.Q<VisualElement>("ms-out-empty")?.EnableInClassList("ms-hidden", !none);

            foreach (var p in v.Pool)
            {
                var row = new VisualElement();
                row.AddToClassList("ms-row");
                row.AddToClassList("ms-row--out");
                MarkPicked(row, p.IsPicked);
                row.Add(Who(p.Name, p.GradeLabel, p.HandLabel));
                if (p.IsCaptain) row.Add(CaptainMark());

                var idx = p.Index;
                row.RegisterCallback<ClickEvent>(_ => { _state.ClickPool(idx); Render(); });
                row.RegisterCallback<PointerEnterEvent>(_ => { _state.SetHovered(idx); RenderCompareOnly(); });
                host.Add(row);
            }
        }

        // ── 2選手比較（共通部品 CompareTable＝オーダー画面と同じ表。操作説明の文字は出さない） ──
        private void BuildCompare(MemberSettingView v)
        {
            var tabs = _root.Q<VisualElement>("ms-cmp-tabs");
            if (tabs != null)
            {
                tabs.Clear();
                for (var i = 0; i < v.TabLabels.Length; i++)
                {
                    var t = new Label(v.TabLabels[i]);
                    t.AddToClassList("ms-tab");
                    if (v.Tab == i) t.AddToClassList("ms-tab--on");
                    var tab = i;
                    t.RegisterCallback<ClickEvent>(_ => { _state.SetTab(tab); Render(); });
                    tabs.Add(t);
                }
            }

            var head = _root.Q<VisualElement>("ms-cmp-head");
            if (head != null)
            {
                head.Clear();
                head.Add(CompareTable.Head(HeadOf(v.CardA), HeadOf(v.CardB)));
            }

            var rows = _root.Q<VisualElement>("ms-cmp-rows");
            if (rows != null)
            {
                rows.Clear();
                foreach (var r in v.Rows) rows.Add(CompareTable.Row(r));
            }

            BuildApt(v);

            // 持ち球のミニ球種変化チャート（投手能力タブのときだけ・タブでゲートする, Issue #93/#293）。
            var chart = _root.Q<VisualElement>("ms-cmp-chart");
            if (chart != null)
            {
                chart.Clear();
                if (v.ShowPitchChart && (v.CardA.Present || v.CardB.Present))
                    chart.Add(CompareTable.PitchCharts(v.CardA.Present, v.CardA.Pitches, v.CardB.Present, v.CardB.Pitches));
            }

            KokoSim.Unity.Shell.SchoolAccent.Apply(_root.Q<VisualElement>(className: "ms-compare"), _accent);
        }

        private static CompareTable.PlayerHeadData HeadOf(CompareCard card) => new CompareTable.PlayerHeadData
        {
            Present = card.Present,
            Name = card.Name,
            Meta = card.GradeLabel + "\u3000" + card.RoleText,
            Strength = card.Strength,
            Skills = card.Skills,
        };

        // ── 守備適性（野手能力タブ内・9マスの帯。行見出しは姓） ──
        private void BuildApt(MemberSettingView v)
        {
            var host = _root.Q<VisualElement>("ms-cmp-apt");
            if (host == null) return;
            host.Clear();
            host.EnableInClassList("ms-hidden", !v.ShowApt);
            if (!v.ShowApt) return;
            host.Add(UiComponents.AptitudeStrip(
                v.HasAptA ? v.AptA : null, v.HasAptB ? v.AptB : null,
                v.CardA.Present ? Surname(v.CardA.Name) : "", v.CardB.Present ? Surname(v.CardB.Name) : "",
                tight: true));
        }

        private static string Surname(string name) => KokoSim.Engine.Season.PlayerNameGenerator.Surname(name);

        // ── 部品ヘルパ ──

        private static Label NumBadge(int n, bool vacant = false)
        {
            var b = new Label(n.ToString());
            b.AddToClassList("num-badge");
            b.AddToClassList("f-num-bd");
            if (vacant) b.AddToClassList("num-badge--vacant");
            return b;
        }

        // 名前（明朝）＋学年・投打の2行。球場のカードは幅が狭いので、長い名前は級数を落として切らずに収める。
        private static VisualElement Who(string name, string grade, string hand, bool fitLongName = false)
        {
            var who = new VisualElement();
            who.AddToClassList("ms-who");
            var nm = new Label(name);
            nm.AddToClassList("ms-who__name");
            nm.AddToClassList("f-display");
            if (fitLongName)
            {
                var letters = 0;
                foreach (var ch in name) if (!char.IsWhiteSpace(ch)) letters++;
                if (letters >= 7) nm.AddToClassList("ms-who__name--xlong");
                else if (letters >= 6) nm.AddToClassList("ms-who__name--long");
            }
            who.Add(nm);
            var sub = new Label(grade + " " + hand);
            sub.AddToClassList("ms-who__sub");
            who.Add(sub);
            return who;
        }

        // 選択中の枠（スクールカラー）。色は Render の最後に SchoolAccent.Apply が流す。
        private static void MarkPicked(VisualElement el, bool picked)
        {
            if (!picked) return;
            el.AddToClassList("ms-pick");
            el.AddToClassList("acc-bd");
        }

        // 空いている枠の点線（部品辞書 DashedFrame）。透明の枠（選択用）の外周に重なるよう、枠の太さぶん外へ出す。
        private static DashedFrame VacantFrame()
        {
            var f = new DashedFrame { Radius = 9f, Dash = 4f, Gap = 3f };
            f.style.left = -PickFrame; f.style.top = -PickFrame;
            f.style.right = -PickFrame; f.style.bottom = -PickFrame;
            return f;
        }

        private static Label VacantLabel()
        {
            var l = new Label("未割当");
            l.AddToClassList("ms-vacant");
            return l;
        }

        // 主将の印（スクールカラーの丸に「主」）。corner=true は球場のカードの右上の角にはみ出して置く形。
        private static Label CaptainMark(bool corner = false)
        {
            var l = new Label("主");
            l.AddToClassList("ms-cap");
            l.AddToClassList("acc-bg");
            l.AddToClassList("f-body-bd");
            if (corner) l.AddToClassList("ms-cap--corner");
            return l;
        }

        private Label ClearButton(int number)
        {
            var x = new Label("×");
            x.AddToClassList("ms-row__x");
            x.RegisterCallback<ClickEvent>(evt =>
            {
                evt.StopPropagation();
                _state.ClearSlot(number);
                Render();
            });
            return x;
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
