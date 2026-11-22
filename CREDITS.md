# クレジット

ゲーム内のクレジット（システム →「クレジット」・`Assets/UI/SaveSlots.uxml` の `ss-page-credits`）と同じ内容の開発用の控え。
素材を足したら**両方**を更新する。規約の確認日と、ゲーム内に載せる文言もここに残す（issue #1318）。

## ゲーム内に載せるもの

| 区分 | 表記 | 規約の要点（確認日） |
|---|---|---|
| 効果音（打球・捕球） | On-Jin ~音人~　https://on-jin.com/ | 個人・サークルは有料ゲームでも連絡不要。クレジットは任意（書くならサイト名とアドレス）。素材のまま第三者が取れる状態は禁止＝ビルドに素のファイルを置かない（[規約](https://on-jin.com/kiyaku.php)・2026-11-21）。全角の「～」はフォントに無いので半角 ~ で書く |
| 歓声 | 効果音ラボ | 商用可・クレジット任意・素材単体の再配布は禁止（2026-11-09） |
| 場内放送 | VOICEVOX:No.7 | **ゲームでの利用は商用扱いで、No.7 製作委員会への事前相談が必要**（[公式](https://voiceseven.com/)・2026-11-21）。製品版での扱いは未決（相談して許諾を取るか差し替えるか）。**体験版には入れない**（`KOKOSIM_DEMO` で鳴らさず、`PlayerBuilder` が素材をビルドから外し、この行も隠す） |
| 3Dモデル（防具・帽子） | Meshy（CC BY 4.0） | 無料プランの生成物は CC BY 4.0（クレジットで商用可）。有料プランなら表記不要だが、どちらでも載せる（[Meshy](https://help.meshy.ai/en/articles/9992001-can-i-use-my-generated-assets-for-commercial-projects)）。対象: `DuelGearMeshes.cs` が読む Cap1・CatcherMask1・CatcherHelmet0・BatterHelmet0・ChestProtector1・LegGuard1 |
| 3Dモデル（ミット・グラブ） | Tripo AI（CC BY 4.0） | 下の「3Dモデル」節 |
| 地図データ（県選択画面） | 「国土数値情報（行政区域データ）」（国土交通省）https://nlftp.mlit.go.jp/ksj/ を加工して作成。Natural Earth（パブリックドメイン） | 行政区域データ（N03）は CC BY 4.0＝出典の表記で商用利用可（[データ頁](https://nlftp.mlit.go.jp/ksj/gml/datalist/KsjTmplt-N03-2024.html)・[利用規約](https://nlftp.mlit.go.jp/ksj/other/agreement.html)・2026-11-23）。国が作成したかのような表記はしない。北海道の振興局と東京の市区町村の区割りに使い、頂点を間引いて `data/japan-map.yaml` にしている（`tools/gen-japan-map.py`）。Natural Earth は表記不要だが載せる |
| フォント | Noto Sans JP・しっぽり明朝 B1・DotGothic16・Anton・Dela Gothic One（SIL OFL 1.1） | 全文をゲーム内に載せる（`Assets/Resources/Licenses/OFL-*.txt`。正典は `Assets/UI/Fonts/OFL-*.txt`） |

## 3Dモデル（Tripo）

以下は [Tripo AI](https://www.tripo3d.ai/) の無料プランで画像から生成したモデルで、
[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) で提供されています。
デシメート・姿勢の変更・内側の充填などの加工をしています。

| ファイル | 内容 |
|---|---|
| `Assets/DuelSpike/Generated/AIGear/AIGear_MittT_lo.fbx` | 捕手ミット（対決ビュー） |
| `Assets/DuelSpike/Generated/AIGear/AIGear_GloveT_lo.fbx` | 野手グラブ（対決ビュー。購入品が無い環境用） |

## 音声（VOICEVOX:No.7）

場内放送（ウグイス嬢）の声は **VOICEVOX:No.7** です（[VOICEVOX](https://voicevox.hiroshiba.jp/) で生成）。
生成物はリポジトリに含めず、`tools/voicevox-announcements.py` で各自の環境に作ります
（出力先 `Assets/KokoSim/Audio/Vendor/VoiceVox/` は Git 管理外）。

## テロップ画像のフォント

試合テロップの画像（`Assets/UI/Telops/`）は、Anton と Dela Gothic One で描いた文字を画像に焼き込んでいます。

## 載せていないもの（表記不要）

| 素材 | 理由 |
|---|---|
| 応援曲（Suno・有料プランで生成） | 商用可・表記不要。Steam の AI 生成物の申告には入れる |
| モーション（Asset Store「AA Basic Baseball」・Mixamo・自前の Rokoko 収録） | ゲーム内での利用は可・表記不要 |
| 3Dモデル（TurboSquid「School Baseball Player 0001」・CGTrader のグラブ） | 標準ライセンスで表記不要。素のモデルを取り出せる形で配らない |
| 3Dモデル（Quaternius） | CC0 |

## Steam の AI 生成物の申告（ストアページを作るときに使う）

Suno（応援曲）・Meshy と Tripo（装備の3Dモデル）・VOICEVOX（場内放送。製品版に入れる場合）。
