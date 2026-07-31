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
    /// </summary>
    public sealed class TacticsCardDeck : VisualElement
    {
        // カードの箱の寸法はこの部品が持つ（BaseballFieldElement / Match2DPlaybackElement と同じく、
        // 座標を自分で計算する自作要素は自分の幾何を持つ）。components.uss 側は色と枠だけを担当する。
        /// <summary>カード1枚の幅。将来ここへ絵を入れる前提で大きめに取る。</summary>
        public const float CardWidth = 132f;
        /// <summary>カード1枚の高さ（絵：名前 ≒ 2:1）。</summary>
        public const float CardHeight = 180f;
        /// <summary>隣のカードに覆われる幅。CardWidth − Overlap が「隠れない左の帯」になる。</summary>
        public const float Overlap = 44f;

        private const float Step = CardWidth - Overlap;

        /// <summary>カード名の級数は文字数で決める（左の帯に収まる比率。モックで詰めた値）。</summary>
        private static readonly float[] CapRatioByLen = { 0.34f, 0.34f, 0.30f, 0.24f, 0.19f, 0.155f, 0.135f };

        private readonly VisualElement _inner;
        private IReadOnlyList<SignPad.SignDef> _defs = Array.Empty<SignPad.SignDef>();
        // グループ→選択中の非既定カードId（既定選択はエントリなし＝無点灯）。フェーズ切替でも保持する。
        private readonly Dictionary<string, string> _selected = new();
        private readonly List<VisualElement> _cards = new();      // 表示順（＝重なり順）
        private readonly Dictionary<string, VisualElement> _byId = new();
        private bool _offense;
        private bool _built;

        public TacticsCardDeck()
        {
            AddToClassList("tcards");
            pickingMode = PickingMode.Ignore;   // 帯の余白はクリックを盤面へ通す（中断ポップアップが開く）
            _inner = new VisualElement { pickingMode = PickingMode.Ignore };
            _inner.AddToClassList("tcards__inner");
            Add(_inner);
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
            if (id != null && _byId.TryGetValue(id, out var card)) SetHot(card);
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
            _cards.Clear();
            _byId.Clear();

            foreach (var def in _defs)
            {
                if (def.Offense != _offense) continue;
                var card = BuildCard(def);
                _cards.Add(card);
                _byId[def.Id] = card;
                _inner.Add(card);
            }
            Layout();
            RefreshHighlights();
        }

        private VisualElement BuildCard(SignPad.SignDef def)
        {
            var card = new VisualElement { pickingMode = PickingMode.Position };
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

            card.RegisterCallback<PointerEnterEvent>(_ =>
            {
                if (card.ClassListContains("tcard--off")) return;
                ClearHot();
                SetHot(card);
            });
            card.RegisterCallback<PointerLeaveEvent>(_ => ClearHot());
            card.RegisterCallback<ClickEvent>(_ =>
            {
                if (card.ClassListContains("tcard--off")) return;
                Select(def.Id);
            });
            return card;
        }

        // カードは絶対配置で Step ずつずらして敷く。重なり順を変えても位置が動かないので、
        // 最前面化（BringToFront）と復帰（Add で末尾へ送り直す）を絵のずれ無しで行える。
        private void Layout()
        {
            style.height = CardHeight;
            _inner.style.width = _cards.Count > 0 ? Step * (_cards.Count - 1) + CardWidth : 0f;

            for (var i = 0; i < _cards.Count; i++)
            {
                var card = _cards[i];
                card.style.left = i * Step;
                card.style.width = CardWidth;
                card.style.height = CardHeight;

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

        private void SetHot(VisualElement card)
        {
            card.AddToClassList("tcard--hot");
            card.BringToFront();   // 拡大したカードが隣に隠れないよう最前面へ
        }

        private void ClearHot()
        {
            foreach (var c in _cards) c.RemoveFromClassList("tcard--hot");
            // 重なり順を定義順へ戻す（絶対配置なので Add で送り直しても位置は動かない）。
            foreach (var c in _cards) _inner.Add(c);
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
