using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Unity.Components;   // 部品辞書（RankChip）
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 試合中の選手交代モーダル（設計書09 §6 / issue #22 C）。MatchLive.uxml の <c>sub-modal</c> にバインドし、
    /// 代打・代走・投手交代（指名）・守備交代・DH解除の5種を1つのカードで出し分ける。
    ///
    /// 見た目はスタメン設定（LineupSetting）と同じ部品辞書（lineup-row / bench-row / pos-chip / rank-chip /
    /// cmp-card / cmp-row）を使い、配置だけ MatchSubstitution.uss が持つ。判断ロジックはエンジンの
    /// <see cref="SubstitutionOptions"/> に委ね、ここは表示と1回の実行呼び出しだけを行う（不変条件#3 エンジン純度）。
    ///
    /// スタメン設定との違い（issue #22 C）:
    ///   ・退場済み／使用済みの選手はグレーアウトして選べない（高校野球＝リエントリー禁止）
    ///   ・確定は即時・取り消し不可（プレビューもロールバックも無い）
    /// </summary>
    public sealed class MatchSubstitutionPanel
    {
        private readonly VisualElement _root;
        private readonly System.Action _onApplied;

        private VisualElement _scrim, _kinds, _outRows, _inRows, _cmpRows;
        private Label _phase, _outsCap, _insCap, _note;
        private Button _ok;

        private MatchProgression _prog;
        private bool _teamIsAway;
        private SubstitutionOptions _opt;
        private SubstitutionKind _kind = SubstitutionKind.PinchHit;

        // 選択中の「退く側」と「入る側」。DH解除だけは _dhAt（守備位置 or null＝そのまま退場）を使う。
        private int _outIndex = -1;      // 代打/代走/守備交代=打順スロット, 代走は塁の添字を _baseIndex で別持ち
        private int _baseIndex = -1;
        private int _inIndex = -1;       // 控え（Bench）またはブルペン（Bullpen）の添字
        private FieldPosition? _dhAt;
        private bool _dhChoiceMade;

        public MatchSubstitutionPanel(VisualElement root, System.Action onApplied)
        {
            _root = root;
            _onApplied = onApplied;
        }

        private bool _open;

        /// <summary>モーダルを開いている間は裏の進行（自動再生・次の打席へ）を止める。</summary>
        public bool IsOpen => _open;

        /// <summary>UIDocument の要素を引き当てる（MatchLiveController の OnEnable から1回だけ呼ぶ）。</summary>
        public void Bind()
        {
            _scrim = _root.Q<VisualElement>("sub-modal");
            _kinds = _root.Q<VisualElement>("msub-kinds");
            _outRows = _root.Q<VisualElement>("msub-out-rows");
            _inRows = _root.Q<VisualElement>("msub-in-rows");
            _cmpRows = _root.Q<VisualElement>("msub-cmp-rows");
            _phase = _root.Q<Label>("msub-phase");
            _outsCap = _root.Q<Label>("msub-outs-cap");
            _insCap = _root.Q<Label>("msub-ins-cap");
            _note = _root.Q<Label>("msub-note");
            _ok = _root.Q<Button>("msub-ok");

            Click("msub-close", Close);
            Click("msub-cancel", Close);
            Click("msub-ok", Apply);
            Close();
        }

        /// <summary>この試合の進行体と監督のチームを差し替える（試合ごとに呼ぶ）。</summary>
        public void SetMatch(MatchProgression prog, bool teamIsAway)
        {
            _prog = prog;
            _teamIsAway = teamIsAway;
            Close();
        }

        public void Open()
        {
            if (_prog == null || _scrim == null) return;
            _opt = _prog.SubstitutionOptions(_teamIsAway);
            // 局面に合う種別から開く（攻撃中＝代打 / 守備中＝投手交代）。
            _kind = _opt.IsOffense ? SubstitutionKind.PinchHit : SubstitutionKind.ChangePitcher;
            ResetSelection();
            _open = true;
            _scrim.style.display = DisplayStyle.Flex;
            Render();
        }

        public void Close()
        {
            _open = false;
            if (_scrim != null) _scrim.style.display = DisplayStyle.None;
        }

        /// <summary>スクショ用: 種別タブを選ぶ（0=代打 1=代走 2=投手交代 3=守備交代 4=DH解除）。</summary>
        public void SelectKindForCapture(int index)
        {
            if (!_open || index < 0 || index >= KindTabs.Length) return;
            _kind = KindTabs[index].Kind;
            ResetSelection();
            Render();
        }

        private void ResetSelection()
        {
            _outIndex = -1;
            _baseIndex = -1;
            _inIndex = -1;
            _dhAt = null;
            _dhChoiceMade = false;
            // 代打・投手交代は「退く側」が局面で一意に決まるので最初から選んでおく。
            if (_kind == SubstitutionKind.PinchHit) _outIndex = _opt.UpcomingBatterSlot;
        }

        // ── 描画 ──

        private void Render()
        {
            if (_opt == null) return;
            if (_phase != null) _phase.text = _opt.IsFinished ? "試合終了" : _opt.IsOffense ? "攻撃中" : "守備中";
            BuildKinds();
            BuildOutRows();
            BuildInRows();
            BuildCompare();
            BuildFooter();
        }

        private static readonly (SubstitutionKind Kind, string Label)[] KindTabs =
        {
            (SubstitutionKind.PinchHit, "代打"),
            (SubstitutionKind.PinchRun, "代走"),
            (SubstitutionKind.ChangePitcher, "投手交代"),
            (SubstitutionKind.DefensiveSub, "守備交代"),
            (SubstitutionKind.ReleaseDh, "DH解除"),
        };

        private void BuildKinds()
        {
            if (_kinds == null) return;
            _kinds.Clear();
            foreach (var (kind, label) in KindTabs)
            {
                var cell = new Label(label);
                cell.AddToClassList("seg__cell");
                var enabled = CanUse(kind);
                if (kind == _kind) cell.AddToClassList("seg__cell--on");
                else if (!enabled) cell.AddToClassList("seg__cell--off");
                var k = kind;
                cell.RegisterCallback<ClickEvent>(e =>
                {
                    e.StopPropagation();
                    _kind = k;             // 選べない種別も選択は許し、理由を1行で出す（issue #22 C）
                    ResetSelection();
                    Render();
                });
                _kinds.Add(cell);
            }
        }

        private bool CanUse(SubstitutionKind kind) => kind switch
        {
            SubstitutionKind.PinchHit => _opt.CanPinchHit,
            SubstitutionKind.PinchRun => _opt.CanPinchRun,
            SubstitutionKind.ChangePitcher => _opt.CanChangePitcher,
            SubstitutionKind.DefensiveSub => _opt.CanDefensiveSub,
            SubstitutionKind.ReleaseDh => _opt.CanReleaseDh,
            _ => false,
        };

        // ── 左：退く選手 ──

        private void BuildOutRows()
        {
            if (_outRows == null) return;
            _outRows.Clear();
            if (_outsCap != null)
                _outsCap.text = _kind switch
                {
                    SubstitutionKind.PinchRun => "退く走者",
                    SubstitutionKind.ChangePitcher => "降板する投手",
                    SubstitutionKind.ReleaseDh => "打順（DHの位置）",
                    _ => "退く選手",
                };

            if (_kind == SubstitutionKind.ChangePitcher)
            {
                _outRows.Add(LineupRow(0, _opt.CurrentPitcher, selectable: false, picked: true, tag: "登板中"));
                return;
            }

            for (var i = 0; i < _opt.Lineup.Count; i++)
            {
                var p = _opt.Lineup[i];
                var slot = i;
                var tag = "";
                var selectable = false;
                switch (_kind)
                {
                    case SubstitutionKind.PinchHit:
                        selectable = false;                       // 対象は次打者で一意
                        if (i == _opt.UpcomingBatterSlot) tag = "次打者";
                        break;
                    case SubstitutionKind.PinchRun:
                        var b = BaseOf(p);
                        if (b >= 0) { selectable = true; tag = BaseName(b); }
                        break;
                    case SubstitutionKind.DefensiveSub:
                        // 投手の交代は「投手交代」タブに集約する（継投を伴わない入替を作らない）。
                        selectable = p != _opt.CurrentPitcher;
                        if (!selectable) tag = "投手";
                        break;
                    case SubstitutionKind.ReleaseDh:
                        if (_opt.UsesDh && i == _opt.DhSlot) tag = "DH";
                        break;
                }
                var picked = _kind == SubstitutionKind.PinchHit ? i == _opt.UpcomingBatterSlot
                    : _kind == SubstitutionKind.ReleaseDh ? _opt.UsesDh && i == _opt.DhSlot
                    : _outIndex == i;
                var row = LineupRow(i + 1, p, selectable, picked, tag);
                if (selectable)
                    row.RegisterCallback<ClickEvent>(e =>
                    {
                        e.StopPropagation();
                        _outIndex = slot;
                        _baseIndex = _kind == SubstitutionKind.PinchRun ? BaseOf(_opt.Lineup[slot]) : -1;
                        Render();
                    });
                _outRows.Add(row);
            }
        }

        private int BaseOf(Player p)
        {
            foreach (var r in _opt.Runners)
                if (r.Runner == p) return r.BaseIndex;
            return -1;
        }

        private static string BaseName(int i) => i == 0 ? "一塁" : i == 1 ? "二塁" : "三塁";

        /// <summary>打順行（スタメン設定と同じ lineup-row 部品）。</summary>
        private VisualElement LineupRow(int order, Player p, bool selectable, bool picked, string tag)
        {
            var row = new VisualElement();
            row.AddToClassList("lineup-row");
            if (picked) row.AddToClassList("lineup-row--picked");
            if (!selectable && !picked) row.AddToClassList("msub-row--off");

            var ord = new Label(order > 0 ? order.ToString() : "—");
            ord.AddToClassList("lineup-row__ord");
            row.Add(ord);

            var posCell = new VisualElement();
            posCell.AddToClassList("lineup-row__pos");
            var chip = new Label(PosJp(p.Position));
            chip.AddToClassList("pos-chip");
            chip.AddToClassList("pos-chip--fixed");
            posCell.Add(chip);
            row.Add(posCell);

            if (!string.IsNullOrEmpty(tag))
            {
                var t = new Label(tag);
                t.AddToClassList("msub-base-tag");
                row.Add(t);
            }

            var name = new Label(NameOf(p));
            name.AddToClassList("lineup-row__name");
            row.Add(name);

            var info = new Label(HandLabel(p));
            info.AddToClassList("lineup-row__info");
            row.Add(info);

            row.Add(UiComponents.RankChip(GradeOf(p)));
            return row;
        }

        // ── 中：入る選手 ──

        private void BuildInRows()
        {
            if (_inRows == null) return;
            _inRows.Clear();
            if (_kind == SubstitutionKind.ReleaseDh)
            {
                if (_insCap != null) _insCap.text = "DHの行き先";
                BuildDhChoices();
                return;
            }

            var usePen = _kind == SubstitutionKind.ChangePitcher;
            if (_insCap != null) _insCap.text = usePen ? "投手候補" : "控え";

            // 投手交代の候補はブルペン＋ベンチ（野手含む・issue #137）。
            var available = usePen ? _opt.PitcherCandidates : _opt.Bench;
            for (var i = 0; i < available.Count; i++)
            {
                var idx = i;
                var row = BenchRow(available[i], dim: false, picked: _inIndex == i, note: "");
                row.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _inIndex = idx; Render(); });
                _inRows.Add(row);
            }

            // 使い切った控え／登板済みの投手はグレーアウトで残す（リエントリー禁止が一目で分かる）。
            var used = usePen ? _opt.UsedPitcherCandidates : _opt.UsedBench;
            foreach (var p in used) _inRows.Add(BenchRow(p, dim: true, picked: false, note: "出場済"));

            if (available.Count == 0 && used.Count == 0)
            {
                var empty = new Label("控えが登録されていない。");
                empty.AddToClassList("cmp-card__empty");
                _inRows.Add(empty);
            }
        }

        private void BuildDhChoices()
        {
            if (!_opt.UsesDh)
            {
                // DH未使用（または解除済み）。理由は下の1行に出るので、ここは選択肢を出さない。
                var none = new Label("選べる行き先はない。");
                none.AddToClassList("cmp-card__empty");
                _inRows.Add(none);
                return;
            }
            // 分岐1: DHの選手がその守備位置に就く（元の守備者が退場）。分岐2: DHはそのまま退場。
            foreach (var pos in _opt.DhFieldingChoices())
            {
                var p = pos;
                var row = ChoiceRow(PosJp(pos) + "を守らせる", _dhChoiceMade && _dhAt == pos);
                row.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _dhAt = p; _dhChoiceMade = true; Render(); });
                _inRows.Add(row);
            }
            var withdraw = ChoiceRow("そのまま退かせる（投手が打席へ）", _dhChoiceMade && _dhAt == null);
            withdraw.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _dhAt = null; _dhChoiceMade = true; Render(); });
            _inRows.Add(withdraw);
        }

        private static VisualElement ChoiceRow(string text, bool picked)
        {
            var row = new VisualElement();
            row.AddToClassList("bench-row");
            if (picked) row.AddToClassList("bench-row--picked");
            var name = new Label(text);
            name.AddToClassList("bench-row__name");
            row.Add(name);
            return row;
        }

        /// <summary>控え行（スタメン設定と同じ bench-row 部品）。タグは選手本来の守備位置
        /// （投手交代候補の野手はそのまま守備位置を表示＝ブルペン投手は Position が投手なので従来通り「投」）。</summary>
        private VisualElement BenchRow(Player p, bool dim, bool picked, string note)
        {
            var row = new VisualElement();
            row.AddToClassList("bench-row");
            if (picked) row.AddToClassList("bench-row--picked");
            if (dim) row.AddToClassList("msub-row--off");

            var name = new Label(NameOf(p));
            name.AddToClassList("bench-row__name");
            row.Add(name);

            var tag = new Label(PosJp(p.Position));
            tag.AddToClassList("bench-row__tag");
            row.Add(tag);

            if (!string.IsNullOrEmpty(note))
            {
                var n = new Label(note);
                n.AddToClassList("bench-row__tag");
                row.Add(n);
            }

            row.Add(UiComponents.RankChip(GradeOf(p)));
            return row;
        }

        // ── 右：能力比較（スタメン設定と同じ cmp-card / cmp-row 部品） ──

        private void BuildCompare()
        {
            var outgoing = SelectedOutgoing();
            var incoming = SelectedIncoming();
            FillCard("msub-cmp-a", outgoing, "退く選手を選ぶ");
            FillCard("msub-cmp-b", incoming, "入る選手を選ぶ");

            if (_cmpRows == null) return;
            _cmpRows.Clear();
            var pitcherView = _kind == SubstitutionKind.ChangePitcher;
            var group = new Label(pitcherView ? "投球" : "打撃・走守");
            group.AddToClassList("msub-cmp-group");
            _cmpRows.Add(group);
            _cmpRows.Add(UiComponents.CompareHeader("退く", "入る"));
            foreach (var (label, a, b) in CompareRows(outgoing, incoming, pitcherView))
                _cmpRows.Add(CompareRowEl(label, a, b));
        }

        private Player SelectedOutgoing()
        {
            switch (_kind)
            {
                case SubstitutionKind.ChangePitcher: return _opt.CurrentPitcher;
                case SubstitutionKind.PinchHit:
                    return _opt.UpcomingBatterSlot >= 0 ? _opt.Lineup[_opt.UpcomingBatterSlot] : null;
                case SubstitutionKind.ReleaseDh:
                    return _opt.UsesDh && _opt.DhSlot >= 0 && _opt.DhSlot < _opt.Lineup.Count
                        ? _opt.Lineup[_opt.DhSlot] : null;
                default:
                    return _outIndex >= 0 && _outIndex < _opt.Lineup.Count ? _opt.Lineup[_outIndex] : null;
            }
        }

        private Player SelectedIncoming()
        {
            if (_kind == SubstitutionKind.ReleaseDh) return null;
            var list = _kind == SubstitutionKind.ChangePitcher ? _opt.PitcherCandidates : _opt.Bench;
            return _inIndex >= 0 && _inIndex < list.Count ? list[_inIndex] : null;
        }

        private void FillCard(string host, Player p, string placeholder)
        {
            var el = _root.Q<VisualElement>(host);
            if (el == null) return;
            el.Clear();
            if (p == null)
            {
                var empty = new Label(placeholder);
                empty.AddToClassList("cmp-card__empty");
                el.Add(empty);
                return;
            }
            var name = new Label(NameOf(p));
            name.AddToClassList("cmp-card__name");
            el.Add(name);

            var meta = new VisualElement();
            meta.AddToClassList("cmp-card__meta");
            var hand = new Label(HandLabel(p));
            hand.AddToClassList("bench-row__tag");
            meta.Add(hand);
            meta.Add(UiComponents.RankChip(GradeOf(p)));
            el.Add(meta);
        }

        private static IEnumerable<(string Label, int A, int B)> CompareRows(Player a, Player b, bool pitcherView)
        {
            if (pitcherView)
            {
                yield return ("球速", Velocity(a), Velocity(b));
                yield return ("制球", a?.Pitching?.Control ?? -1, b?.Pitching?.Control ?? -1);
                yield return ("キレ", a?.Pitching?.PitchRank ?? -1, b?.Pitching?.PitchRank ?? -1);
                yield return ("スタミナ", Stamina(a), Stamina(b));
                yield return ("精神力", a?.Mental ?? -1, b?.Mental ?? -1);
                yield break;
            }
            yield return ("ミート", a?.Contact ?? -1, b?.Contact ?? -1);
            yield return ("パワー", a?.Power ?? -1, b?.Power ?? -1);
            yield return ("弾道", a?.LaunchTendency ?? -1, b?.LaunchTendency ?? -1);
            yield return ("選球眼", a?.Discipline ?? -1, b?.Discipline ?? -1);
            yield return ("走力", a?.Speed ?? -1, b?.Speed ?? -1);
            yield return ("肩", a?.ArmStrength ?? -1, b?.ArmStrength ?? -1);
            yield return ("守備", a?.Fielding ?? -1, b?.Fielding ?? -1);
            yield return ("捕球", a?.Catching ?? -1, b?.Catching ?? -1);
            yield return ("精神力", a?.Mental ?? -1, b?.Mental ?? -1);
        }

        // 球速・スタミナは物理量なので 0-100 の表示スケールへ写す（表示専用・エンジンの値は触らない）。
        private static int Velocity(Player p)
            => p?.Pitching == null ? -1 : Mathf.Clamp(Mathf.RoundToInt((float)(p.Pitching.MaxVelocityKmh - 110.0) * 2f), 0, 100);

        private static int Stamina(Player p)
            => p?.Pitching == null ? -1 : Mathf.Clamp(Mathf.RoundToInt((float)p.Pitching.StaminaPitches * 100f / 160f), 0, 100);

        // 行の見た目は部品辞書（UiComponents.CompareRow）に集約。負値は「その選手がいない」を表す。
        private static VisualElement CompareRowEl(string label, int a, int b)
            => UiComponents.CompareRow(new CompareRowData
            {
                Label = label,
                ValueA = a, ValueB = b,
                HasA = a >= 0, HasB = b >= 0,
                Winner = a < 0 || b < 0 ? 0 : a > b ? -1 : b > a ? 1 : 0,
            });

        // ── 注記＋確定 ──

        private void BuildFooter()
        {
            var blocked = _opt.BlockedReasonFor(_kind);
            var ready = blocked == null && SelectionComplete();
            if (_note != null)
            {
                _note.text = blocked ?? (ready
                    ? "この交代は即時に確定し、取り消せない（退いた選手は再出場できない）。"
                    : SelectionHint());
                _note.EnableInClassList("msub-note--warn", blocked != null);
            }
            _ok?.SetEnabled(ready);
        }

        private bool SelectionComplete() => _kind switch
        {
            SubstitutionKind.PinchHit => _inIndex >= 0,
            SubstitutionKind.PinchRun => _baseIndex >= 0 && _inIndex >= 0,
            SubstitutionKind.ChangePitcher => _inIndex >= 0,
            SubstitutionKind.DefensiveSub => _outIndex >= 0 && _inIndex >= 0,
            SubstitutionKind.ReleaseDh => _dhChoiceMade,
            _ => false,
        };

        private string SelectionHint() => _kind switch
        {
            SubstitutionKind.PinchRun when _baseIndex < 0 => "代走に出す走者を選ぶ。",
            SubstitutionKind.DefensiveSub when _outIndex < 0 => "退く守備者を選ぶ。",
            SubstitutionKind.ReleaseDh => "DHの行き先を選ぶ。",
            SubstitutionKind.ChangePitcher => "登板させる投手を選ぶ。",
            _ => "控えから入る選手を選ぶ。",
        };

        private void Apply()
        {
            if (_prog == null || _opt == null || !SelectionComplete()) return;
            var ok = _kind switch
            {
                SubstitutionKind.PinchHit => _prog.PinchHit(_teamIsAway, _opt.Bench[_inIndex]),
                SubstitutionKind.PinchRun => _prog.PinchRun(_teamIsAway, _baseIndex, _opt.Bench[_inIndex]),
                SubstitutionKind.ChangePitcher => _prog.ChangePitcher(_teamIsAway, _opt.PitcherCandidates[_inIndex]),
                SubstitutionKind.DefensiveSub => _prog.DefensiveSub(_teamIsAway, _opt.Lineup[_outIndex], _opt.Bench[_inIndex]),
                SubstitutionKind.ReleaseDh => _prog.ReleaseDh(_teamIsAway, _dhAt),
                _ => false,
            };
            if (!ok)
            {
                // 局面がずれていた（＝この交代はもう成立しない）。最新の選択肢を引き直して理由を出す。
                _opt = _prog.SubstitutionOptions(_teamIsAway);
                ResetSelection();
                Render();
                if (_note != null)
                {
                    _note.text = "この交代はもう行えない（局面が変わった）。";
                    _note.AddToClassList("msub-note--warn");
                }
                return;
            }
            Close();
            _onApplied?.Invoke();
        }

        // ── 表示ヘルパ ──

        private static string NameOf(Player p)
            => p.UniformNumber > 0 ? p.UniformNumber + "  " + p.Name : p.Name;

        private static string HandLabel(Player p) => HandednessLabels.Combined(p.Throws, p.Bats);

        /// <summary>総合ランク（投手は投手能力・野手は打撃走守の平均）。表示専用。</summary>
        private static string GradeOf(Player p)
        {
            double overall;
            if (p.Pitching is { } pit)
                overall = (Velocity(p) * 0.40 + pit.Control * 0.25 + Stamina(p) * 0.15 + pit.PitchRank * 0.20);
            else
                overall = (p.Contact + p.Power + p.Discipline + p.Speed + p.Fielding + p.Catching) / 6.0;
            return Tiers.FromStrength(overall).ToString();
        }

        private static string PosJp(FieldPosition pos) => pos switch
        {
            FieldPosition.Pitcher => "投",
            FieldPosition.Catcher => "捕",
            FieldPosition.FirstBase => "一",
            FieldPosition.SecondBase => "二",
            FieldPosition.ThirdBase => "三",
            FieldPosition.Shortstop => "遊",
            FieldPosition.LeftField => "左",
            FieldPosition.CenterField => "中",
            FieldPosition.RightField => "右",
            _ => "—",
        };

        private void Click(string name, System.Action action)
        {
            var btn = _root.Q<Button>(name);
            if (btn != null) btn.clicked += action;
        }
    }
}
