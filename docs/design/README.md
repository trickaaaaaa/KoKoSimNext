# pref-formats スキーマ定義

各県の秋季大会フォーマットを1ファイル= data/pref-formats/<pref>.yaml で定義する。
設計書05 §1.5 の大会フォーマット定義を、47県実形式（2025年秋基準）に対応させた拡張版。

## トップレベル

```yaml
pref: <slug>                 # 県スラッグ（例: aichi）
snapshot_year: 2025          # このデータの基準年（年度差があるため必須）
region: tokai                # 所属する秋季地区大会
districts: [...]             # 県内地区リスト（地理割の県のみ。抽選/一発は空 or 省略）
district_assignment: geographic | draw | none
                             # geographic=学校が地区固定 / draw=大会時抽選 / none=地区なし
stages: [...]                # 予選→県大会のステージ列（下記）
regional_berths: <int>       # この県から地区大会への出場枠
seed_exemption: false        # 夏実績校などの予選免除の有無
notes: [...]                 # 出典・特記（自由記述）
```

## stages[] の要素

```yaml
- name: <表示名>
  type: knockout | round_robin | group_split
  # --- group_split のとき ---
  groups: <int>              # 地区/支部/ブロック数
  grouping: geographic | draw | host_fixed   # host_fixed=当番校固定(東京)
  child: { type: knockout | round_robin }    # 各グループ内の形式
  advance_per_group: <int>   # 各グループから次へ進む数
  loser_bracket:             # 敗者復活（無ければ省略）
    enabled: true
    advance: <int>           # 敗者復活から救い上げる数
  # --- round_robin のとき ---
  teams_per_group: <int>     # 1リーグの数（神奈川=4）
  advance: <int>             # リーグから進む数
  # --- knockout のとき ---
  entries: <int>             # 参加数（判明時のみ）
  seed_rule: <text>          # シード規則（判明時のみ）
  third_place_match: false   # 3位決定戦（近畿枠決め等で使用）
```

## 地区大会側（別ファイル data/regional-tournaments.yaml）

```yaml
- region: hokushinetsu
  berths_per_pref: { niigata: 3, nagano: 3, toyama: 3, ishikawa: 3, fukui: 3 }
  host_prefecture_bonus: true      # 開催県+1
  host_by_year: { 2025: toyama, 2026: ... }
  champion_to_jingu: true          # 優勝校が明治神宮大会へ
- region: kinki
  biennial_rotation:               # 隔年制
    odd_year:  { shiga: 3, nara: 3, kyoto: 2, wakayama: 2 }
    even_year: { shiga: 2, nara: 2, kyoto: 3, wakayama: 3 }
  fixed: { osaka: 3, hyogo: 3 }
  host_by_year: { 2025: nara }
```

## 確度フラグの扱い
- 各ファイル冒頭に `# confidence: verified | structure_only` を記す
- structure_only は型のみ確定・数値は仮。ゲームは動くが、要項PDFで確定でき次第 verified へ

## 型の見本（実データ）
- 一発トーナメント型 → nara.yaml
- リーグ戦を含む型 → kanagawa.yaml
- 敗者復活を含む地区予選型 → hyogo.yaml
