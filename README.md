# nokandro

Package name: `com.nokakoi.nokandro`

## Overview

`nokandro` is a lightweight Android app that connects to the Nostr protocol. It reads incoming posts using TTS (Text-to-Speech), shares your "Now Playing" music status, and acts as a NIP-46 remote signer (Bunker). The UI is split into **Main** and **Bunker** tabs.

## Main Features

- Connects to a Nostr relay (WebSocket) to receive events and publish status in real-time.
- **Text-to-Speech (TTS)**: Reads incoming posts from followed users (configurable).
- **Now Playing**: Detects music playing on your device (via notification listener) and publishes it as a Nostr status (kind: 30315).
- **NIP-46 Bunker**: Acts as a remote signer so external Nostr clients can request event signing, encryption/decryption via the `bunker://` protocol.
- Retrieves the follow list, public mute list, and muted words list for filtering.
- Different TTS voices can be specified for followed users and others.
- **Off timer**: Automatically stops the service after a configurable number of minutes.
- **Relay list fetch**: Fetches your relay list (kind: 10002) from known relays and lets you pick one.
- **Home screen widget**: Start/stop the service and quick-post from a widget.
- **Tasker / automation support**: Start and stop the service via broadcast intents.
- The background service uses foreground notifications, ensuring stability and allowing stop control from the notification area.

## Installation and First Launch (APK Distribution)

1. Download and install the provided APK.
2. On first launch, you may be prompted for permissions such as notifications. If enabling "Broadcast Now Playing", you will be prompted to grant "Notification Listener" permission.

## Main Tab

- **App Title & Version**: Tap the version number (top right) to open the latest GitHub release page. If a newer version is available it is shown in red.
- **Follow list / Muted user / Muted words**: Displays loading status of these lists.
- **Relay**: The URL of the relay to connect to. Use the **▼** button to fetch your relay list (kind: 10002) and select one.
- **Your nsec (optional)**: Your Nostr private key (bech32 `nsec...`). Required for publishing "Now Playing" status and for the Bunker feature. Entering an nsec automatically derives the corresponding npub.
- **Your npub**: Your Nostr public key (bech32 `npub...` or hex).
- **Enable Text-to-Speech**: Master switch for the TTS feature. When OFF, all TTS settings below are hidden.
- **(TTS Settings)**:
  - **Max characters**: Truncation length for displayed/spoken text.
  - **Ellipsis**: String to append when text is truncated.
  - **Speak petname**: Reads the petname (nickname) of the user before the message.
  - **Skip content-warning**: Skips posts that have a content-warning tag.
  - **TTS language**: Filter available voices by language.
  - **Voice for followed users**: Select voice for followed users.
  - **Allow non-followed posts**: Toggle to also read posts from users you don't follow.
  - **Voice for non-followed users**: Select voice for others.
  - **Refresh Voices**: Reload available TTS voices.
  - **Speech rate**: Adjust reading speed (0.50x – 1.50x). Can be changed while the service is running.
- **Stop on audio disconnect**: Automatically stops the service when audio output is disconnected (e.g., Bluetooth).
- **Broadcast Now Playing**: Toggle to enable publishing your current music track to Nostr. Requires "Notification Listener" permission and a valid nsec.
- **Off timer**: When enabled, the service will automatically stop after the specified number of minutes. A countdown is displayed while running.
- **Start / Stop**: Start or stop the background service.
- **(Last Content)**: Displays the most recent message received.

## Bunker Tab (NIP-46 Remote Signer)

The Bunker tab provides a NIP-46 remote signer that lets external Nostr clients (e.g., web apps) sign events and perform encryption/decryption using your private key, without sharing the key itself.

- **Bunker relay**: The relay used for bunker communication (editable only when the bunker is stopped).
- **NIP-46 Bunker switch**: Starts or stops the bunker service.
- **Bunker URI**: The `bunker://` URI to paste into a NIP-46 compatible client. Displayed when the bunker is running.
- **Copy bunker URI**: Copies the URI to the clipboard.
- **Reset Secret**: Deletes the saved bunker secret and stops the bunker. A new URI with a fresh secret will be generated on next start.
- **Status**: Shows bunker connection status and client activity.

**Supported NIP-46 methods**: `connect`, `get_public_key`, `sign_event`, `nip04_encrypt`, `nip04_decrypt`, `nip44_encrypt`, `nip44_decrypt`, `ping`.

> **Note**: Enter your `nsec` in the Main tab before using the Bunker.

For detailed documentation on the Bunker feature, see [BUNKER.md](BUNKER.md).

## Widget & Tasker

- **Home screen widget**: Add the "Nostr Post" widget to your home screen to toggle the service on/off or open a quick post dialog.
- **Tasker / automation**: Send broadcast intents to control the service:
  - `com.nokakoi.nokandro.ACTION_START` — Start the service using saved preferences.
  - `com.nokakoi.nokandro.ACTION_STOP` — Stop the service.

## Follow List, Public Mute, and Muted Words

- **Follow list**: Retrieved from the relay (kind: 3). Followed users get priority TTS voice selection.
- **Public mute**: Retrieved from the relay (kind: 10000). Muted users are skipped by TTS.
- **Muted words**: Retrieved from the relay. Posts containing muted words are skipped.

## Notifications and Service Operation

- While the service is running, it resides in the notification area.
- You can stop the service from the "Stop" action in the notification.

## Common Issues

- **Now Playing not updating**:
  - Ensure "Notification Listener" permission is granted to nokandro.
  - Verify your music player posts standard Android media notifications.
- **TTS not playing**:
  - Check device volume.
  - Ensure "Enable Text-to-Speech" is ON.
- **Service stops unexpectedly**:
  - Check device battery optimization settings.
  - If "Stop on audio disconnect" is enabled, the service stops when audio output changes (e.g. Bluetooth disconnect).
- **Bunker not connecting**:
  - Ensure nsec is entered in the Main tab.
  - Check that the bunker relay is reachable.

## ⚠️ For Android 13+ Users

Due to added features, "Notification Access" permission is now required.
When installing the APK downloaded from GitHub, you might see a "Restricted Settings" message when trying to enable this permission, and the switch may be greyed out.

In that case, please follow these steps to allow restricted settings:
1. Go to Android Settings -> "Apps" -> "nokandro".
2. Tap the menu in the top right corner (three dots) and select "Allow restricted settings".
3. Return to the app and try enabling the permission switch again.

---

## 概要

`nokandro` は Nostr プロトコルに接続し、受信したメッセージを TTS（音声合成）で読み上げたり、再生中の音楽情報（Now Playing）を共有したり、NIP-46 リモート署名（Bunker）として動作する Android アプリです。UIは **Main** タブと **Bunker** タブに分かれています。

## 主な機能

- リレー（WebSocket）へ接続してリアルタイムでイベント送受信を行います。
- **読み上げ (TTS)**: フォロー中のユーザー等の投稿を読み上げます（有効時）。
- **Now Playing**: 端末で再生中の音楽（通知リスナー経由）を検知し、Nostrステータス (kind: 30315) として投稿します。
- **NIP-46 Bunker**: 外部の Nostr クライアントからの署名・暗号化/復号リクエストに応答するリモート署名機能です。
- 指定した `npub` に基づくフォローリスト・ミュートリスト・ミュートワードを取得し、読み上げの判定やフィルタリングを行います。
- **オフタイマー**: 指定時間後にサービスを自動停止します。
- **リレーリスト取得**: リレーリスト (kind: 10002) を取得して接続先を選択できます。
- **ホーム画面ウィジェット**: ウィジェットからサービスのON/OFFや投稿が可能です。
- **Tasker / 自動化**: ブロードキャスト Intent でサービスの開始・停止を制御できます。
- バックグラウンドサービスはフォアグラウンド通知を使用し、通知領域から停止操作が可能です。

## インストールと初回起動

1. APK をインストールしてください。
2. 初回起動時、通知権限などが求められます。「Broadcast Now Playing」を有効にする際は、別途「通知へのアクセス（Notification Listener）」権限の許可を促されます。

## Main タブ

- **タイトル・バージョン**: 右上のバージョン番号をタップすると、GitHub の最新リリース配布ページを開きます。更新がある場合は赤字で表示されます。
- **Follow list / Muted user / Muted words**: リストの読み込み状況表示。
- **Relay**: 接続先リレーの URL。**▼** ボタンでリレーリスト (kind: 10002) を取得して選択できます。
- **Your nsec (任意)**: 自分の秘密鍵 (`nsec...`)。「Now Playing」の投稿や Bunker 機能に必要です。入力すると npub が自動的に導出されます。
- **Your npub**: 自分の公開鍵 (`npub...` または hex)。
- **Enable Text-to-Speech**: TTS 読み上げ機能のメインスイッチ。OFF にすると TTS 設定は非表示になります。
- **(TTS 設定)**:
  - **Max characters**: テキスト表示・読み上げの最大文字数制限。
  - **Ellipsis**: テキスト省略時に末尾に追加される文字列。
  - **Speak petname**: メッセージの前にユーザーのペットネーム（ニックネーム）を読み上げます。
  - **Skip content-warning**: コンテンツ警告タグのある投稿をスキップします。
  - **TTS language**: ボイス選択リストの言語フィルタ。
  - **Voice for followed users**: フォロー済みユーザー用の声。
  - **Allow non-followed posts**: フォローしていないユーザーの投稿も読み上げるかどうかの設定。
  - **Voice for non-followed users**: その他のユーザー用の声。
  - **Refresh Voices**: ボイスリストの再取得。
  - **Speech rate**: 読み上げ速度の調整（0.50x – 1.50x）。サービス実行中でも変更可能です。
- **Stop on audio disconnect**: オーディオ機器（Bluetoothなど）の切断検知時に自動停止します。
- **Broadcast Now Playing**: 再生中の音楽情報を Nostr に投稿する機能の ON/OFF。ON にするには通知リスナー権限と nsec が必要です。
- **Off timer**: 有効にすると、指定した分数の経過後にサービスを自動停止します。実行中はカウントダウンが表示されます。
- **Start / Stop**: サービスの開始・停止ボタン。
- **(Last Content)**: 最後に受信したメッセージが表示されます。

## Bunker タブ（NIP-46 リモート署名）

Bunker タブは NIP-46 リモート署名機能を提供します。外部の Nostr クライアント（Webアプリ等）が秘密鍵を共有せずに、イベントの署名や暗号化/復号をリクエストできます。

- **Bunker relay**: Bunker 通信に使用するリレー（Bunker停止中のみ編集可能）。
- **NIP-46 Bunker スイッチ**: Bunker サービスの開始/停止。
- **Bunker URI**: NIP-46 対応クライアントに貼り付ける `bunker://` URI。Bunker 実行中に表示されます。
- **Copy bunker URI**: URI をクリップボードにコピーします。
- **Reset Secret**: 保存されたシークレットを削除し Bunker を停止します。次回起動時に新しいシークレットの URI が生成されます。
- **Status**: Bunker の接続状態やクライアントの動作状況を表示します。

**対応 NIP-46 メソッド**: `connect`, `get_public_key`, `sign_event`, `nip04_encrypt`, `nip04_decrypt`, `nip44_encrypt`, `nip44_decrypt`, `ping`

> **注意**: Bunker を使用するには Main タブで `nsec` を入力してください。

Bunker 機能の詳細なドキュメントは [BUNKER.md](BUNKER.md) を参照してください。

## ウィジェット & Tasker

- **ホーム画面ウィジェット**: 「Nostr Post」ウィジェットをホーム画面に追加すると、サービスのON/OFFやクイック投稿が可能です。
- **Tasker / 自動化**: ブロードキャスト Intent でサービスを制御できます。
  - `com.nokakoi.nokandro.ACTION_START` — 保存済みの設定でサービスを開始。
  - `com.nokakoi.nokandro.ACTION_STOP` — サービスを停止。

## フォローリスト・公開ミュート・ミュートワード

- **フォローリスト**: リレーから取得 (kind: 3)。フォロー済みユーザーは TTS のボイスが優先選択されます。
- **公開ミュート**: リレーから取得 (kind: 10000)。ミュートされたユーザーの投稿は読み上げをスキップします。
- **ミュートワード**: リレーから取得。ミュートワードを含む投稿は読み上げをスキップします。

## 通知とサービス動作

- サービス実行中は通知領域に表示されます。
- 通知の「Stop」アクションからサービスを停止できます。

## 注意点

- **Now Playing が投稿されない**:
  - Android 設定で `nokandro` に「通知へのアクセス」権限が許可されているか確認してください。
  - 音楽プレイヤーアプリが標準的なメディア通知を出している必要があります。
- **TTS が聞こえない**:
  - 音量設定や `Enable Text-to-Speech` スイッチを確認してください。
- **サービスが停止する**:
  - バッテリー最適化設定を確認してください。
  - "Stop on audio disconnect" が有効な場合、オーディオ機器の切断を検知して停止します。
- **Bunker が接続できない**:
  - Main タブで nsec が入力されているか確認してください。
  - Bunker リレーが到達可能か確認してください。

## 署名と安全性

- 本アプリは nsec（秘密鍵）を取り扱います。外部流出しないよう端末内に保存されますが、利用は自己責任でお願いします。
- Bunker 機能ではシークレットによる接続認証が行われ、認証済みクライアントのみがリクエストを送信できます。

## ⚠️ Android 13以降をお使いの方へ

機能追加に伴い、「通知へのアクセス」権限が必要になりました。
GithubからAPKをダウンロードしてインストールした場合、権限を有効にする際に「制限付き設定」というメッセージが表示され、スイッチが押せないことがあります。

その場合は、以下の手順で制限を解除してください：
1. Androidの本体設定から「アプリ」→「nokandro」を開く
2. 右上のメニュー（︙）から「制限付き設定を許可」を選択
3. 再度アプリ内のスイッチから権限を有効にしてください

