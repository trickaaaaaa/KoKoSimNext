# design-21 表示能力→物理層の「効き」（ability_effect）と圧縮の撤去

> 対応: issue prefecture-dominance-physics（OPEN-QUESTIONS Q24-3〜Q24-5）。オーナー決定 2026-08-19「案A＝全員一律≈0.55・投手も寝かせる・エースはスターで突出」。
> 目的は「同じ校が県で27大会連続優勝」を止めつつ、**表示ランクと中身を乖離させない**こと。

---

## 1. 何が問題だったか（3行）

1. エンジンは「チーム全体が10Lv上＝単試合 90%」と急だった（実測 §5）。県1位（raw 80）vs 2位（65）が 99% で、同じ校が毎年優勝した。
2. 2026-08-18 の対処＝**AI校だけ**を生成で寝かせるティア圧縮（`tier_compression_*` 一様0.55）は勝率を寝かせたが、A校の 6指標バーが C+ になり、エースは 138km/h になった（スター型で 145 へ戻したが 6指標バーの乖離は残った）。
3. オーナー: 「乖離しないほうがいい」→ **raw＝中身**に戻し、勾配を寝かせる役を**物理層の入口**へ移す（案C→案A）。

## 2. 決定事項

| 項目 | 決定 |
|---|---|
| ティア圧縮 `roster.tier_compression_slope/knee` | **恒等（1.0／100）が既定**。式は比較・実験用に残す（`simulate-tiers --compress-slope`） |
| 表示→物理の効き `ability_effect:` | **全員対称**（自校もAI校も）。`effective = pivot + scale×(Lv − pivot)`、pivot 50。打撃 0.55／守備 0.45／走力 0.55／**投手 0.55** |
| 投手の扱い | 制球・キレ・球威ランク・捕手リードは Lv で寝かせる。**最速 km/h と目安球数は画面に出る単位なので据え置き**、球速は「反応オフセット」で打者側の反応だけを寝かせる（§3.2） |
| スター | `roster.ai_star_*` は存置。lift の定義を「raw−圧縮後」→ **(強さ−40)×0.25** へ（raw 80 校でエース/主軸 +10） |
| 集計モデル `nation.aggregate_scale` | 4.0 → **7.2**（raw 差で引くようになったぶん。旧 4/0.55≈7.3 と実質据え置き） |
| 球速の価値 `batting.contact_quality_per_kmh` | +0.003 → **−0.005**（§4） |

## 3. 効きの入口（不変条件#1: 表示層→物理層の変換は一箇所）

正典は `data/coefficients.yaml` の `ability_effect:` 1ブロック。ローダが batting/fielding/baserunning の3ブロックへ同じ値を配る
（`AbilityEffectConsistencyTests` が食い違いを固定。差し替えは `GameContext.WithAbilityEffect` を通す）。
`AbilityEffectCoefficients.Identity`（全部 1.0）は従来と**1ビット一致**（恒等は丸めも通さない）。

### 3.1 入口は4つだけ

| 入口 | 何を寝かせるか |
|---|---|
| `Player.ToBatter(effect)` | ミート/パワー/選球眼（Batting）・走力（Running）。弾道は「型」＝寝かせない |
| `Player.ToFielder(effect)` | 守備/捕球/肩/送球（Fielding）・走力（Running）・**捕手リード（Pitching）** |
| `AbilityEffectCoefficients.Pitcher(PitcherAttributes)` | 制球・PitchRank・各球種の Power/Sharpness（Pitching）。最速 km/h・目安球数・投法・球質タイプは据え置き。反応オフセットを焼く |
| 解決式が `Player` の能力を直接読む場所 | `c.AbilityEffect.Batting(batter.Bunt)`／`Running(runner.Steal/Baserunning)` のように必ずこの写像を通す（RunnerClock の恒等オーバーロードは廃止＝呼び手が effect を渡す） |

GameEngine では打席頭の `EffectiveMatchup()` が唯一の入口: `batter.ToBatter(ctx.Batting.AbilityEffect)`／
`PitchingFatigue.Effective(ctx.Batting.AbilityEffect.Pitcher(currentPitcher.Pitching), ...)`（**効き→消耗→調子→スキル→プレッシャー**の順。
消耗の落ち幅は「目安球数を超えた分」で決まる実測単位なので寝かせない）。

### 3.2 球速は km/h を変えず「反応」だけ寝かせる

球速は最速・毎球の球速がそのまま画面に出るので、Lv で寝かせると表示と食い違う（Lv90 の投手が 140km/h しか投げない）。
そこで `PitcherAttributes.ReactionVelocityOffsetKmh = PitchingKmh(最速) − 最速`（`PitchingKmh(v) = 132 + scale×(v−132)`、132＝Lv50 の最速）を
`Pitcher()` が焼き、`PitchSelection` が毎球の実球速（最速−ドロップ）に**足して**反応球速 `PitchPlan.ReactionVelocityKmh` を作る。

- 読む側: 球威 `Stuff`（PitchSelection）・タイミング `TimingOffsetMean`・打球質 `PitcherContactQualityBias`（ContactModel）・バント難度（BuntResolver）
- 読まない側: 弾道（飛翔時間・誘発変化量は実球速）・表示・実況・タイムライン・PitchRecord
- **差分で持つ**ので、消耗・調子・ギア・毎球のドロップ・変化球の緩急（球速比）はそのまま効く。平均投手（132km/h）はオフセット0＝帯の平均は動かない

## 4. 球速の価値（同時に直したもの）

単一対戦の実測（`velo` ハーネス）で、球速 Lv 50→90 の投手は**打球あたり安打率が .323→.376 と速いほど打たれていた**。原因は
`contact_quality_per_kmh` +0.003（速い球ほど強い打球）と「速い球ほど絶対変化量が小さい」効果の重なりで、球速の価値が三振ぶんしか無く
打球質で打ち消されていた。−0.005 で球速+10 ≈ 制球+10（AVG −.017 ずつ、K% 20.6→24.5）。副作用として遅い投手が打たれるようになり
`matchup_grid.average_p35_b75` の天井を .360→.390、`tiers_low` の帯を縁ぶんだけ広げた（§6）。

## 5. 実測（永続ロスター同士＝実ゲーム経路・300試合×先攻後攻・スター lift 0.25）

| 効き（投手/打撃/守備/走力） | 80vs80 の得点 | 80vs75 | 80vs70 | 80vs65 | 80vs60 | 60vs50 | 50vs40 |
|---|---|---|---|---|---|---|---|
| 1.0/1.0/1.0/1.0（従来） | — | 74.5% | 86% | 96% | — | 94% | 93% |
| 1.0/0.5/0.4/0.5（投手を寝かせない） | **1.3−1.3** | 65% | 78% | 89% | 94% | 85% | 87% |
| 1.0/1.0/0.4/0.5 | 3.0−3.0 | 71% | 87% | 97% | — | 90% | — |
| **0.55/0.55/0.45/0.55（採用）** | 2.7−2.7 | 63% | 77% | 88% | 95% | 84% | 84% |

投手を寝かせないと、A校の投手は控えまで Lv 77〜90 が素の効きで投げるので**強豪同士が全部 1−1 の投手戦**になる（現実の甲子園は 3〜4 点ずつ）。
2026-08-18 の圧縮世界が両立して見えたのは、圧縮が「控え投手まで」寝かせてエースだけスターで戻していたから＝実質「投手も寝かせてエースだけ突出」。

- 集計 20年×49地区（`nationchamp`・scale 7.2・seed 2026/7/42）: 最多優勝校の中央値 4〜5／10回以上 1〜4地区／最大 12〜17（帯 `nation_dominance` 内）
- 表示ティア別のエース最速（seed 2026）: C 140／B 148／A・S 149.6（Lv99 のクランプ。150 超は出ない）
- 自校（開始直後・才能中心32）の梯子: E(40) 5.4〜5.5点／D(50) 3.2点・完封され 13%／C(60) 2.0点・29%／勾配 0.60。効き恒等だと 7.2／2.7・19%／0.85・55%・勾配 0.34
- 盗塁: 実ゲーム経路（AiTacticsBrain）で 60vs60 0.83企図/試合（恒等 0.81）、75(機動力)vs55 1.24（恒等 3.52）＝格上の盗塁祭りが減る。
  ただし成功率が 31〜48% と低いのは AI の非最適選択（`optimal_*`）由来で本書以前からの性質（Q24-5 参照）

## 6. 帯・テストの扱い

- `tiers_low`: E40 得点床 5.5→5.2／D50 床 3.2→3.0／C60 床 1.9→1.7／完封され天井 D50 0.14→0.16・C60 0.28→0.32（縁に触れた分だけ）
- `matchup_grid.average_p35_b75` 天井 .360→.390（格子は物理層直＝効きを通らない）
- `Bands_WouldCatch_TheUncompressedGradient` → `Bands_WouldCatch_TheUnmutedGradient`（効き恒等で測ると赤くなること）
- 采配・絵のテスト（盗塁の始動・三盗・重盗・矛盾ガード・注入テスト）は探針に極端な能力（走力95 vs 肩20）を使うので、
  **効き＝恒等**で回す（`AbilityEffectCoefficients.Identity`／`GameContext.WithAbilityEffect`）。判定と絵の一致テストは両側とも同じ効きを通す
- 決定論ベースラインは再生成（RNG 消費順は不変・結果が変わる）

## 7. 残課題

- 表示ランクの意味: 学校 raw 80 の 6指標は 80 前後（乖離なし）。ただし選手の「B ランク」は物理層では 66.5 相当で効く＝**Lv1 あたりの結果の差が小さい**世界。育成の手応え（+10 で何が変わるか）はプレイで要確認
- 盗塁の企図頻度・成功率（AI の非最適選択と閾値 `steal_min_success` 0.72 の関係）は効きの導入前から低い（N(50,12) 同士で 0.09/試合）。別 issue で校正
- 既存セーブは在校生が入学時能力を保持し、AI校のロスターは新入生から raw へ切り替わる（2〜3年で移行）
