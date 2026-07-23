using System.Runtime.CompilerServices;

// テストから internal（コールドゲーム判定など純粋な内部ロジック）を直接検証できるようにする。
[assembly: InternalsVisibleTo("KokoSim.Engine.Tests")]
