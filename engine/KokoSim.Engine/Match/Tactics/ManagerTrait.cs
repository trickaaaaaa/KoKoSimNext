namespace KokoSim.Engine.Match.Tactics;

/// <summary>
/// 監督傾向＝采配の癖（issue #55, 設計書11 §3の拡張）。校風（<see cref="SchoolStyle"/>＝チームの型）とは
/// 別軸で、1校に0〜2個まで重ねて付与する（決定1: A-1）。校風が排他1種なのに対し、こちらは複数可＝
/// 「バント大好き＋盗塁大好き」のような掛け合わせが作れる。効果量はすべて <see cref="EnemyAiCoefficients"/>
/// （data/coefficients.yaml enemy_ai:）駆動で、采配系は <see cref="ManagerTraitEffects.ApplyTactics"/> が
/// <see cref="TacticsCoefficients"/> に重ね、継投系は <see cref="ManagerTraitEffects.FatigueOverride"/> が
/// チーム別 <see cref="Game.FatigueCoefficients"/> を作る（決定4: B-1）。抜擢型だけは試合前のオーダー編成に効く。
/// </summary>
public enum ManagerTrait
{
    /// <summary>バント多用（送りバントの確率↑・開始回を早める）。</summary>
    BuntHeavy,

    /// <summary>盗塁・エンドラン好き（盗塁/エンドラン確率↑・盗塁成功見込みの下限を緩和）。</summary>
    RunAndGun,

    /// <summary>エース酷使（継投しきい値を引き上げ＝エースを引っ張る。疲労ペナルティで被打率上昇の諸刃）。</summary>
    AceOveruse,

    /// <summary>継投早め（継投しきい値を大きく下げ＋守備固めを1イニング早める）。</summary>
    QuickHook,

    /// <summary>代打積極（代打の発動イニングを早め・候補条件を緩めて発動頻度↑）。</summary>
    AggressivePinchHit,

    /// <summary>抜擢型（試合前オーダー編成: 背番号10〜の控えでも調子が良ければ先発起用・押し出された正位置選手はベンチへ）。</summary>
    Promoter,

    /// <summary>スクイズ好き（スクイズ確率↑。ティアゲートは従来どおり適用）。</summary>
    SqueezeLover,

    /// <summary>強気ギア（飛ばすギアを早め・長め＝勝負どころを広く取る。スタミナ消耗と引き換え）。</summary>
    AggressiveGear,

    /// <summary>慎重（一塁空きの強打者に敬遠を選びやすい＝敬遠をためらわない）。</summary>
    Cautious,
}
