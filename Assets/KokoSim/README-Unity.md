# KokoSim Unity UI（Phase 6 — ホームダッシュボード）

純C#エンジン（Phase 1〜5）を UI Toolkit のホーム画面へ接続した最初のプレイアブル要素です。
**UnityMCP 経由で Editor（Unity 6000.3.9f1）にて配線・Play・週送りまで動作検証済み**（2026-07-15）。
`Assets/Scenes/SampleScene.unity` に GameObject「HomeDashboard」を配線済みなので、Editor で開いて Play すればそのまま動きます。
下記は手動で再配線する場合の手順です。

## 構成

- `Assets/Plugins/KokoSim/KokoSim.Engine.dll` … 純エンジン（netstandard2.1・外部依存なし）。`Assembly-CSharp` が自動参照。
- `Assets/UI/HomeDashboard.uxml` … ホーム画面レイアウト（設計書06 §3.1）。全体を `ScrollView` で縦スクロール。
- `Assets/UI/KokoSimTheme.uss` … スコアボードテーマ（モックの配色・等級カラーを UI Toolkit へ移植）。
- `Assets/UI/KokoSimRuntimeTheme.tss` … 既定ランタイムテーマ（`@import unity-theme://default`）。
- `Assets/UI/KokoSimPanelSettings.asset` … PanelSettings。`ScaleWithScreenSize`（基準1280×720・幅マッチ）で高DPIでも潰れない。
- `Assets/KokoSim/Home/HomeState.cs` … ViewModel（UnityEngine非依存。エンジンを駆動し週送り・育成・フィードを生成）。
- `Assets/KokoSim/Home/HomeDashboardController.cs` … UIDocument へバインドする MonoBehaviour。

## Editor での配線手順

1. Editor を開くと `Assets/` 配下の .cs/.uxml/.uss が自動インポートされ、DLL の .meta も生成されます。
2. 空のシーン（または `Assets/Scenes/SampleScene.unity`）に空の GameObject を作成し「HomeDashboard」と命名。
3. その GameObject に **UI Document** コンポーネントを追加（`Add Component → UI Toolkit → UI Document`）。
   - **Panel Settings**: 無ければ `Assets/` 右クリック → `Create → UI Toolkit → Panel Settings` で作成し割当。
   - **Source Asset**: `Assets/UI/HomeDashboard.uxml` を割当。
4. 同じ GameObject に `HomeDashboardController` を追加（UI Document を要求します）。
5. 再生（Play）。ホーム画面が表示され、「今週を進める ▶」で週が進み、成長が通知フィードに流れます。

## 前提設定

- **Player Settings → Api Compatibility Level = .NET Standard 2.1**（エンジンDLLが netstandard2.1 のため）。Unity 6 の既定はこの値です。異なる場合は変更してください。

## エンジンを更新したら

```bash
tools/sync-engine-dll.sh   # エンジンをビルドして Plugins へ再配置
```
その後 Editor にフォーカスを戻すと再インポートされます。

## 既知の移植上の注意（要 Editor 検証）

- UI Toolkit は CSS grid 非対応のため、モックの grid レイアウトは Flexbox で表現しています。
- フォントは丸ゴシック（`Assets/UI/KokoSimFont.asset` = ヒラギノ丸ゴ ProN の動的FontAsset）を `Assets/UI/KokoSimPanelSettings.asset` の TextSettings（`KokoSimTextSettings.asset`）既定フォントに設定。**⚠ ヒラギノはmacOS同梱の商用フォントで再配布不可。配布時は OFL の丸ゴ（M PLUS Rounded 1c / Kosugi Maru / Zen Maru Gothic 等）に差し替えること**（`Assets/Fonts/` に置換→FontAsset作り直し→TextSettingsへ再割当）。
- USS の一部プロパティ（gradient / box-shadow / animation）は UI Toolkit 非対応のため省略。点滅ドット等は演出未実装。
- ViewModel はデモ用に単独ロースターを生成しています。将来は Phase 3〜5 のセーブデータと接続します。
