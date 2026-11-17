---
paths:
  - "engine/KokoSim.Engine.Tests/**"
  - "engine/KokoSim.Balance/**"
---
# engine テストの書き方（日常ループを遅くしない）

- **数百試合以上のフルゲームループを回す統計テストは Heavy に置く**（fast 側の膨張防止。issue #361）。
- **xUnit は同じクラスのテスト（Theory の各ケースも）を直列に回す**＝1クラスが重いと、それだけで日常ループの
  壁時間が決まる（issue #639: `SkipDelegateAlwaysEndsTests` の3ケースが直列426秒で、日常ループが7分台になっていた）。
  fast に残す重いテストは、中身を `Parallel.For` で並列化する（押下点・シードごとに状態を作り直して独立にする）。
  所要時間の内訳は `bash tools/test-timing.sh`（日常ループを回してクラス別に集計。`"Category=Heavy"` を渡せば Heavy）で出る。
  2026-11-09 にも同じ形で日常ループが6分半へ戻っていた（同じ試合をテストごとに回し直すクラスが直列300秒）。
  **同じシードの試合を複数テストで使うなら、クラス内で1回だけ並列に作って共有する**（`PictureVsVerdictTests` が前例）
  CI の rest シャードが自動で拾う（網羅性は tools/check-heavy-shards.sh が検証）
- 統計シム（GameSimulation/AtBatSimulation）は決定論保存で並列化済み（事前Fork＋整数部分和）。同シード同結果は維持される。
  テスト内で数百試合を逐次 for で回す場合も同じ作法（シード事前確定＋試合ローカルのチーム生成＋整数部分和）で並列化する
