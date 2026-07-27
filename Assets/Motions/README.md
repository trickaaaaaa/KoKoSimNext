# Assets/Motions — 自作モーション（コミット可）

対決ビュー（設計書18）で使う**自分で収録したモーキャップ**を置く。Rokoko Studio で収録し、
Mixamo プロファイルで FBX エクスポートしたもの。自作物なのでリポジトリに含めてよい。

- **`Assets/Animo/` との違い**: あちらは購入パック（AA Basic Baseball 等）で**再配布NG＝gitignore**。
  こちらは自作なので追跡する。両方を跨いだクリップの名前引きは
  `DuelStageRig.ClipFbxSources`（先勝ち＝自作が優先）に一本化してある。
- **インポート設定はコードで固定**: `Assets/Editor/DuelMotionImportDefaults.cs`（AssetPostprocessor）が
  初回インポート時だけ Humanoid / Create From This Model / 圧縮オフ / クリップ名＝ファイル名を当てる。
  以後の微調整は Inspector が勝つ（`importSettingsMissing` 判定）。
- **クリップ内部の緩急は改変しない**（設計書18の掟）。同期は再生開始時刻の逆算で行う。
  前後のトリム（`firstFrame`/`lastFrame`）とルートの扱いは可。
  差し替え時に測り直す定数は `DuelStageRig` の `PitcherYaw` / `ReleaseTime` / `FollowSeconds`
  （プローブ: `RunFacingProbe` / `RunClipSpeedProbe` / `RunRootPathProbe`）。
- **モーション検証は Play mode で行う**。Edit modeの単発描画は骨を動かしてもスキンが前ポーズのまま
  止まるため `RunPoseSheet` の絵は当てにならない（骨・ルートの数値は Edit mode でも正しい）。

## 収録側の注意（次に撮るとき）

1. **セットの向きと投げる方向を揃える**。本クリップは約68°ずれており、yawをリリース基準に寄せる
   妥協で吸収している（`PitcherYaw=236°`）。揃っていれば妥協は要らない。
2. **立ち位置のドリフトを出さない**。フォロー〜復帰で横1.2m流れており、投手がマウンドから
   外れるため Bake Into Pose: Position XZ で水平移動を抜いている＝踏み込みの前進感が消えている。
3. 頭の静止は長すぎなくてよい（`firstFrame` で落とせる）。末尾はフォロースルーが収まる程度まで。

## 収録物

| ファイル | 中身 | 収録 |
|---|---|---|
| `Duel/Pitcher_Pitching_01.fbx` | 投手の投球1球（セット→リリース→フォロー） | Rokoko / 2026-07-27 |
| `Duel/Batter_Batting_01.fbx` | 打者の1スイング（構え→予備動作→スイング→走り出し） | Rokoko / 2026-07-27 |

打者クリップは**構えとスイングが1本**なので、待機ループと一発物で同じクリップを区間違いで使う
（`DuelStageRig` の `BatterIdleEnd` / `BatterSwingStart` / `BatterRunStart`）。
空振り専用の収録が無いため、空振り・ファールは走り出し前で打ち切って構えへ戻している＝
**空振りとミートの絵が同じ**。分けたい場合は空振り（振り切って泳ぐ）を別テイクで収録する。
