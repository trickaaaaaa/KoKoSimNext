using System;

namespace KokoSim.Unity.Components
{
    /// <summary>指示1件の定義。増やすときはこれを1件足すだけ（UI原則⑤: 直書き禁止の器）。</summary>
    public sealed class SignDef
    {
        public string Id;
        public string Label;
        /// <summary>
        /// 局面で変わる表示名（null=<see cref="Label"/> 固定）。同じ1枚の意味が塁状況で変わる札
        /// （盗塁→三盗→本盗・issue #622）のために、<see cref="TacticsCardDeck.RefreshEnabled"/> が
        /// 可否と一緒に文字も引き直す。
        /// </summary>
        public Func<string> LabelOf;
        /// <summary>
        /// カードの名前の段に出す字の割り方（null＝<see cref="Label"/> を字数の真ん中で2行に割る）。
        /// 真ん中が語の途中になる名前（ウエ／スト・セーフテ／ィバント等）は、ここで改行の位置を明示する
        /// （改行なし＝1行のまま）。文言は <see cref="Label"/> と同じにする＝割り方だけを決める（issue #1533）。
        /// </summary>
        public string CardLabel;
        /// <summary>将来のアイコン化用（null=文字タイル）。指定時はアイコンを主表示に回す。</summary>
        public string Icon;
        /// <summary>
        /// カードの絵（null=<see cref="Icon"/> の1文字を淡く敷く）。値は絵の名前で、
        /// components.uss の <c>.tcard__pic--{Art}</c> が Assets/UI/Images/signs/sign-{Art}.png を引く。
        /// </summary>
        public string Art;
        /// <summary>true=攻撃中に出す指示 / false=守備中に出す指示。</summary>
        public bool Offense;
        /// <summary>
        /// true=右の「構え」（解除するまで続く指示）/ false=左の「サイン」（その1球・その打席で使うと解ける指示）。
        /// 手札は1列のまま、この2つを左右の塊に分けて並べる（issue #1473・2026-11-23 オーナー判断・設計書09 のサインと構えの区分）。
        /// </summary>
        public bool Stance;
        /// <summary>排他グループ（例: "batting" / "policy"）。同グループ内で1つだけ選べる。</summary>
        public string Group;
        public string GroupLabel;
        /// <summary>既定（任せ/通常）。選択すると同グループの点灯が消える＝指示なし。</summary>
        public bool IsDefault;
        /// <summary>有効条件（null=常に有効）。走者・カウント等の局面条件を将来ここへ。</summary>
        public Func<bool> EnabledIf;
        public Action OnSelect;
        /// <summary>
        /// 選択を解いたときに呼ぶ（<see cref="TacticsCardDeck"/> のトグル用。null=解除操作なし）。
        /// 「解除するまで続く方針」のカードは、既定タイルを別に置く代わりに同じカードをもう一度押して消す。
        /// </summary>
        public Action OnClear;
    }
}
