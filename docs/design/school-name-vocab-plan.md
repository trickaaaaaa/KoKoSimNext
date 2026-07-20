# 校名語彙の大規模化・都道府県別化 計画（実装は今度）

設計書05 §2.1（校名生成）の拡張計画。**この文書は計画のみ。実装・データ投入は未着手。**

## 背景・目的

- 現状の校名は `SchoolNameVocab`（地名・公立接尾・私立語幹・私立接尾＋私立率）をパターン合成する方式。
- 語彙が**全国共有**かつ小さいため、大規模県（東京289・神奈川211校）では組合せが枯渇し、
  `UniqueName` の接尾連結（例「緑西東高校」）が多発。地域色も無い。
- 目的: **都道府県ごとに実在の地名を大量投入**し、①バリエーション増 ②地域色（神奈川＝横浜/湘南/相模…）
  ③大規模県でも接尾連結が起きない容量、を実現する。将来の「開始県の選択」機能とも噛み合う。

## 現状の把握（実装前提）

- 語彙型: `engine/KokoSim.Engine/Nation/SchoolNameGenerator.cs` の `SchoolNameVocab`
  （`PlacePrefixes` / `PublicSuffixes` / `PrivateStems` / `PrivateSuffixes` / `PrivateRatio`）。
- 生成: `SchoolNameGenerator.Generate(vocab, rng)` … 私立率で分岐し「語幹/地名 ＋ 接尾 ＋ 高校」。
- 県内一意化: `NationGenerator.UniqueName`（**乱数不使用**の決定論。重複時のみ接尾挿入、最後は「第N」）。
- データ: `data/school-names.yaml`（既に地名35・公立接尾20・私立語幹16・私立接尾8へ拡張済み）。
  ローダは `engine/KokoSim.Config/SchoolNamesFile.cs`。
- 県名↔Id: `data/prefectures.yaml`（`Prefecture.Id = JIS番号-1`、id13=kanagawa 等）。

### 判明している2つの要注意点
1. **Unity は school-names.yaml を読んでいない。** `Assets/KokoSim/Home/HomeState.cs` と
   `TournamentPreviewState.cs` は `new SchoolNameVocab()`（ハードコード極小デフォルト）で生成。
   YAML を読むのは `KokoSim.Balance`（NationSimulation/ManagerSimulation）のみ。
   → **在庫の拡張語彙すらゲーム内に反映されていない**。Unity への配線が必須。
2. **統計回帰帯の保護条件が明確。** `NextInt`/`NextDouble` は1ドロー固定消費（`Xoshiro256Random`）、
   `Generate` は常に3ドロー。よって**語彙サイズ変更は乱数ストリームを乱さず、強さ/名声/校風＝不変**。
   → **`Generate` のドロー数を「3」に保つ限り、統計回帰帯は再校正不要**（不変条件#5を自動的に満たす）。
   逆に、3-part 名など**ドロー数を変える設計にするなら、名前生成を `rng.Fork(id)` の独立ストリームへ隔離**
   （style/district と同じ手法）し、帯を**1回だけ再ベースライン**すること。まずは3ドロー維持案を推奨。

## 設計案

### A. データスキーマ（YAML）
`data/school-names.yaml` を都道府県別へ拡張。県キーは `prefectures.yaml` のローマ字名で対応付け。

```yaml
# 共有（全国共通の接尾・私立語幹）
public_suffixes: [東, 西, 南, 北, 中央, 第一, 第二, 台, 丘, 工業, 商業, 農業, 総合, ...]
private_stems:   [聖凛, 明星, 帝都, 星陵, 誠英, 光陵, 青雲, 開智, ...]
private_suffixes:[学院, 学園, 大付属, 国際, 工科, ...]
private_ratio: 0.30

# 県別の地名（公立校名の母体）。実在の市/郡/旧国名/地域名/地形名を各県30〜50語。
places_by_prefecture:
  hokkaido: [札幌, 函館, 旭川, 帯広, 釧路, 北見, 苫小牧, 室蘭, 石狩, 十勝, 空知, ...]
  kanagawa: [横浜, 川崎, 湘南, 相模, 横須賀, 鎌倉, 藤沢, 平塚, 厚木, 大和, 港北, 金沢, 麻生, ...]
  # …47県ぶん
```

- **共有接尾は据え置き**（方角/番号/学科系は全国共通で自然）。地名だけ県別化するのが費用対効果最大。
- 県別リスト欠落時は従来の共有 `place_prefixes` へフォールバック（後方互換）。

### B. コード変更（最小・帯保護）
1. `SchoolNameVocab` に県別地名を追加:
   `IReadOnlyDictionary<int, IReadOnlyList<string>> PlacesByPrefecture`（id→地名）＋共有リストは現状維持。
2. `SchoolNameGenerator.Generate(vocab, rng, prefId)` へ `prefId` を追加。公立分岐で
   `vocab.PlacesByPrefecture` の該当県リスト（無ければ共有 `PlacePrefixes`）から選ぶ。
   **ドロー順・回数は現状のまま（NextDouble→NextInt→NextInt＝3ドロー）を厳守**（帯保護）。
3. `NationGenerator.CreateSchool` が `prefId` を `Generate` へ渡す（既に prefId を保持）。
4. ローダ `SchoolNamesFile.cs` に `places_by_prefecture` の parse を追加（ローマ字名→id は
   `prefectures.yaml` の順で解決、または id 直キー）。

### C. Unity への配線（必須・別軸の課題）
Unity は `KokoSim.Engine.dll` のみ参照し、`KokoSim.Config`（YamlDotNet）を持たない。以下から選択:
- **C-1（推奨）**: `school-names.yaml` を `Assets/StreamingAssets/`（または Resources の TextAsset）へ配置し、
  Unity 側に軽量ローダを追加して `SchoolNameVocab` を構築。オーサリングは YAML 1本のまま。
- **C-2**: ビルド前に YAML→C# 静的データを生成（コード生成）。Unity は IO 不要だが生成ステップが要る。
- **C-3**: `SchoolNameVocab` の record デフォルトを拡張版に置換（暫定・県別化は不可、二重管理）。
  → 県別化には不向き。C-1 を本命とする。

### D. リサーチ作業（本タスクの主工数＝「今度やる」の中身）
- 47都道府県 × 各30〜50の実在地名（市/郡/旧国名/地域名/主要地形）を収集。
  公立高校の命名慣行（地名＋方角/学科）に合う語を選ぶ。
- **容量目安**: 公立比率0.70なら、東京289校→公立≈202。`地名P × 公立接尾S ≫ 202` を満たすこと。
  例: P=40, S=14 → 560通り。全県で P≥35 を目標にすれば接尾連結はほぼ解消。
- **実在校名との一致回避はベストエフォート**（現行 YAML コメントの方針を踏襲）。
- 総量 ≈ 47×40 ≈ 1,880語＋共有接尾。YAML で管理可能な規模。

## 検証計画（実装時）
1. 生成後、各県で `UniqueName` の「第N」フォールバック発生数を計測 → 0 に近いこと（容量充足の指標）。
2. 各県で校名の distinct 率・接尾連結（"東東"等は修正済み／"〇〇西北"の三段連結）発生率を計測。
3. `Assert.EndsWith("高校")`・県内一意（既存 NationTests）を維持。
4. **統計回帰**: `Generate` の3ドロー維持を守れば `-c Release Category=Heavy` は不変で緑のはず。
   万一 Fork 隔離案を採るなら帯を1回再ベースラインし `data/balance-targets.yaml` を更新。
5. 決定論: 同シード同結果（不変条件#2）。Unity 側も同一 vocab で再現一致。

## ロールアウト順（推奨）
1. **先に Unity 配線（C-1）**だけ入れて既存の拡張 YAML を反映 → すぐに単調さが改善（低コストの即効）。
2. スキーマA＋コードB＋県別データ（D）を段階投入（数県ずつでも可・フォールバックがあるため部分適用OK）。
3. 検証→必要なら地名を追補。

## 未決・確認事項
- 公立/私立の県別比率を変えるか（都市部は私立多め等）＝ `PrivateRatio` の県別化の要否。
- 私立語幹も地域色を出すか（例: 関西系の「学園」志向）＝当面は全国共有で可。
- 「開始県の選択」機能と統合する場合、県メタ（校数・強豪度・地名）を `prefectures.yaml` へ集約する設計と
  合わせて進めると単一ソース化できる（別計画）。

関連: [[tournament-mode]]（FieldPrefectureId=13＝神奈川で本語彙を使用）
