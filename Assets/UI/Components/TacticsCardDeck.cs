using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Components
{
    /// <summary>
    /// TacticsCardDeck（部品辞書 / 試合画面改修）: 采配指示を「重なって並ぶ手札」として出すカード列。
    /// <see cref="SignPad"/> の後継で、定義は <see cref="SignPad.SignDef"/> をそのまま使う
    /// （SetDefs / SetPhase / Select / ClearSelection の4本も同じ形＝画面側の配線は変えずに差し替えられる）。
    ///
    /// SignPad の見た目差し替えではなく別部品にしたのは、
    /// ①カード同士を重ねる ②カーソルが乗った1枚を最前面へ出す、がUSSだけでは書けないため
    /// （USS に隣接セレクタも z-index も無い）。重なりと最前面化は C# 側の絶対配置＋描画順で作る。
    ///
    /// カードは将来 <see cref="SignPad.SignDef.Icon"/> の位置に絵を入れる前提で大きめに取る。
    /// 名前も絵も「隣のカードに隠れない左の帯」の中央へ置く＝カード全幅で中央揃えにすると
    /// 隣の下に潜って読めない（トランプの隅の数字と同じ考え方）。
    ///
    /// <b>当たり判定はカード自身が持たない</b>。カードは重なり順を入れ替える（＝木から外して付け直す）ので、
    /// そこにポインタの出入りを持たせると、押している最中に対象が木から消えてクリックが成立しない。
    /// 判定は「並びが一生変わらない透明な短冊」(<c>_hits</c>) に持たせ、カードは表示専用にする。
    /// 選択は Click ではなく PointerDown で採る（押した時点で確定＝合成イベントに依存しない）。
    /// </summary>
    public sealed class TacticsCardDeck : VisualElement
    {
        // カードの箱の寸法はこの部品が持つ（BaseballFieldElement / Match2DPlaybackElement と同じく、
        // 座標を自分で計算する自作要素は自分の幾何を持つ）。components.uss 側は色と枠だけを担当する。
        /// <summary>カード1枚の幅。将来ここへ絵を入れる前提で大きめに取る。</summary>
        public const float CardWidth = 106f;
        /// <summary>カード1枚の高さ（絵：名前 ≒ 2:1）。</summary>
        public const float CardHeight = 144f;
        /// <summary>隣のカードに覆われる幅。CardWidth − Overlap が「隠れない左の帯」になる。</summary>
        public const float Overlap = 30f;

        private const float Step = CardWidth - Overlap;

        /// <summary>カード名の級数は文字数で決める（左の帯に収まる比率。モックで詰めた値）。</summary>
        private static readonly float[] CapRatioByLen = { 0.34f, 0.34f, 0.30f, 0.24f, 0.19f, 0.155f, 0.135f };

        private readonly VisualElement _inner;
        private readonly VisualElement _hits;
        private IReadOnlyList<SignPad.SignDef> _defs = Array.Empty<SignPad.SignDef>();
        // グループ→選択中の非既定カードId（既定選択はエントリなし＝無点灯）。フェーズ切替でも保持する。
        private readonly Dictionary<string, string> _selected = new();
        private readonly List<VisualElement> _cards = new();      // 表示順（＝重なり順）
        private readonly Dictionary<string, VisualElement> _byId = new();
        private int _hotIndex = -1;
        private bool _offense;
        private bool _built;

        public TacticsCardDeck()
        {
            AddToClassList("tcards");
            pickingMode = PickingMode.Ignore;   // 帯の余白はクリックを盤面へ通す（中断ポップアップが開く）
            _inner = new VisualElement { pickingMode = PickingMode.Ignore };
            _inner.AddToClassList("tcards__inner");
            Add(_inner);
            // 当たり判定の層。カードより後ろの兄弟＝常に手前で拾う。中身の短冊は並べ替えない。
            _hits = new VisualElement { pickingMode = PickingMode.Ignore };
            _hits.AddToClassList("tcards__hits");
            Add(_hits);
        }

        /// <summary>指示の定義一式を差し替える。</summary>
        public void SetDefs(IReadOnlyList<SignPad.SignDef> defs)
        {
            _defs = defs ?? Array.Empty<SignPad.SignDef>();
            _built = false;
            Rebuild();
        }

        /// <summary>攻守フェーズを切り替える（攻撃中=攻撃指示だけ／守備中=守備指示だけ）。</summary>
        public void SetPhase(bool offense)
        {
            if (_built && _offense == offense) return;
            _offense = offense;
            Rebuild();
        }

        /// <summary>プログラム選択（クリックと同じ経路で OnSelect も呼ぶ）。</summary>
        public void Select(string id)
        {
            var def = FindDef(id);
            if (def == null) return;
            if (def.IsDefault) _selected.Remove(def.Group);
            else _selected[def.Group] = def.Id;
            def.OnSelect?.Invoke();
            RefreshHighlights();
        }

        /// <summary>全グループを既定（無点灯）へ戻す。OnSelect は呼ばない（1球消費後の自動解除用）。</summary>
        public void ClearSelection()
        {
            _selected.Clear();
            RefreshHighlights();
        }

        /// <summary>スクショ用: 指定カードをホバー拡大の状態にする（実際のカーソルは要らない）。</summary>
        public void SetHotForCapture(string id)
        {
            ClearHot();
            if (id == null || !_byId.TryGetValue(id, out var card)) return;
            var index = _cards.IndexOf(card);
            if (index >= 0) SetHot(index);
        }

        private SignPad.SignDef FindDef(string id)
        {
            foreach (var d in _defs) if (d.Id == id) return d;
            return null;
        }

        private void Rebuild()
        {
            _built = true;
            _inner.Clear();
            _hits.Clear();
            _cards.Clear();
            _byId.Clear();
            _hotIndex = -1;

            foreach (var def in _defs)
            {
                if (def.Offense != _offense) continue;
                var card = BuildCard(def);
                _cards.Add(card);
                _byId[def.Id] = card;
                _inner.Add(card);
                _hits.Add(BuildHit(def, _cards.Count - 1));
            }
            Layout();
            RefreshHighlights();
        }

        // 当たり判定の短冊。位置はカードと同じだが、こちらは並べ替えないので
        // 押している最中に木から外れることがない（＝クリックが必ず成立する）。
        private VisualElement BuildHit(SignPad.SignDef def, int index)
        {
            var hit = new VisualElement { pickingMode = PickingMode.Position };
            hit.AddToClassList("tcards__hit");
            hit.RegisterCallback<PointerEnterEvent>(_ =>
            {
                ClearHot();
                if (!IsOff(index)) SetHot(index);
            });
            hit.RegisterCallback<PointerLeaveEvent>(_ => ClearHot());
            // 押した時点で確定（Click は down/up の対と合成に依存するので使わない）。
            hit.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (IsOff(index)) return;
                Select(def.Id);
                evt.StopPropagation();
            });
            return hit;
        }

        private bool IsOff(int index) =>
            index < 0 || index >= _cards.Count || _cards[index].ClassListContains("tcard--off");

        private VisualElement BuildCard(SignPad.SignDef def)
        {
            var card = new VisualElement { pickingMode = PickingMode.Ignore };   // 判定は _hits が持つ
            card.AddToClassList("tcard");
            if (def.IsDefault) card.AddToClassList("tcard--default");
            var enabled = def.EnabledIf?.Invoke() ?? true;
            if (!enabled) card.AddToClassList("tcard--off");

            // 上＝将来ここに絵を入れる場所。いまは Icon の1文字を淡く敷いて「絵の枠」だと分かるようにする。
            var art = new VisualElement { pickingMode = PickingMode.Ignore };
            art.AddToClassList("tcard__art");
            var ghost = new Label(def.Icon ?? "") { pickingMode = PickingMode.Ignore };
            ghost.AddToClassList("tcard__ghost");
            art.Add(ghost);
            card.Add(art);

            // 下＝名前。左の帯（隣に隠れない幅）の中央へ置く。
            var cap = new VisualElement { pickingMode = PickingMode.Ignore };
            cap.AddToClassList("tcard__cap");
            var name = new Label(def.Label ?? "") { pickingMode = PickingMode.Ignore };
            name.AddToClassList("tcard__name");
            cap.Add(name);
            card.Add(cap);
            return card;
        }

        // カードは絶対配置で Step ずつずらして敷く。重なり順を変えても位置が動かないので、
        // 最前面化（BringToFront）と復帰（元の添字へ Insert）を絵のずれ無しで行える。
        private void Layout()
        {
            style.height = CardHeight;
            var width = _cards.Count > 0 ? Step * (_cards.Count - 1) + CardWidth : 0f;
            _inner.style.width = width;
            _hits.style.width = width;

            for (var i = 0; i < _cards.Count; i++)
            {
                var card = _cards[i];
                card.style.left = i * Step;
                card.style.width = CardWidth;
                card.style.height = CardHeight;

                // 短冊は「見えている帯」の幅。最後の1枚だけ何にも覆われないので全幅を取る。
                var hit = _hits[i];
                hit.style.left = i * Step;
                hit.style.width = i == _cards.Count - 1 ? CardWidth : Step;

                // 絵も名前も「隣に隠れない左の帯」の中央へ。帯の幅＝Step。
                var ghost = card.Q<Label>(className: "tcard__ghost");
                if (ghost != null)
                {
                    ghost.style.width = Step;
                    ghost.style.fontSize = Mathf.Round(Step * 0.62f);
                }
                var name = card.Q<Label>(className: "tcard__name");
                if (name != null)
                {
                    name.style.width = Step;
                    var len = Mathf.Clamp(name.text?.Length ?? 1, 1, CapRatioByLen.Length - 1);
                    name.style.fontSize = Mathf.Round(Step * CapRatioByLen[len]);
                }
            }
        }

        private void SetHot(int index)
        {
            var card = _cards[index];
            card.AddToClassList("tcard--hot");
            card.BringToFront();   // 拡大したカードが隣に隠れないよう最前面へ
            _hotIndex = index;
        }

        private void ClearHot()
        {
            if (_hotIndex < 0 || _hotIndex >= _cards.Count) return;
            var card = _cards[_hotIndex];
            card.RemoveFromClassList("tcard--hot");
            // 重なり順を定義順へ戻す（絶対配置なので入れ直しても位置は動かない）。
            _inner.Insert(_hotIndex, card);
            _hotIndex = -1;
        }

        private void RefreshHighlights()
        {
            foreach (var pair in _byId)
            {
                var def = FindDef(pair.Key);
                var on = def != null && !def.IsDefault
                         && _selected.TryGetValue(def.Group, out var sel) && sel == pair.Key;
                pair.Value.EnableInClassList("tcard--on", on);
            }
        }
    }
}
