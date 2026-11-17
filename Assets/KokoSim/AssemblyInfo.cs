// Editor の EditMode テスト（Assets/Editor/Tests）から *ForCapture 系の internal な実測口を
// リフレクション無しで呼べるようにする。リフレクションだと名前を変えたときにテストが黙って壊れる。
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Assembly-CSharp-Editor")]
