using System.Runtime.CompilerServices;

// テストから internal（コールドゲーム判定など純粋な内部ロジック）を直接検証できるようにする。
[assembly: InternalsVisibleTo("KokoSim.Engine.Tests")]
// Balance CLI の計測専用コマンド（係数・帯は動かさない）から internal の合成式を直接読めるようにする
// （issue #1792・PitcherRoleCalibration が TeamStrengthProfile.PitcherComposite/NonPitcherComposite を読む）。
[assembly: InternalsVisibleTo("KokoSim.Balance")]
