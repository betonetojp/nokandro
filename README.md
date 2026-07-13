# nokandro

Package name: `com.nokakoi.nokandro`

## Overview

`nokandro` is a lightweight Android app that connects to the Nostr protocol. It reads incoming posts using TTS (Text-to-Speech), shares your "Now Playing" music status, and acts as a remote signer for both **NIP-46** (Bunker / nostrconnect) and **NIP-55** (`nostrsigner:` + content provider). The UI is split into **Main** and **Bunker** tabs.

## Main Features

- Connects to a Nostr relay (WebSocket) to receive events and publish status in real-time.
- **Text-to-Speech (TTS)**: Reads incoming posts from followed users (configurable).
- **Now Playing**: Detects music playing on your device (via notification listener) and publishes it as a Nostr status (kind: 30315).
- **NIP-46 Bunker**: Acts as a remote signer so external Nostr clients can request event signing, encryption/decryption via the `bunker://` protocol.
- **nostrconnect://**: Accepts client-initiated NIP-46 connections by pasting or scanning a `nostrconnect://` URI (`relay` and `secret` required per NIP-46). Multiple concurrent sessions are supported.
- **NIP-55 Signer**: Handles `nostrsigner:` requests and NIP-55 content provider methods (`SIGN_EVENT`, `NIP04_DECRYPT`, etc.) for external signer-compatible clients.
- **Boot auto-start (optional)**: Bunker can be auto-started after device reboot via a switch in the Bunker tab.
- Retrieves the follow list, public mute list, and muted words list for filtering.
- Different TTS voices can be specified for followed users and others.
- **Off timer**: Automatically stops the service after a configurable number of minutes.
- **Turn off Off timer when it ends**: When enabled, the Off timer switch will automatically turn OFF when the timer expires. This prevents the service from being restarted if the timer is enabled again by accident.
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
- **Turn off Off timer when it ends**: When enabled, the Off timer switch will automatically turn OFF when the timer expires. This prevents the service from being restarted if the timer is enabled again by accident.
- **Start / Stop**: Start or stop the background service.
- **(Last Content)**: Displays the most recent message received.

## Bunker Tab (NIP-46 Remote Signer)

The Bunker tab provides a NIP-46 remote signer that lets external Nostr clients (e.g., web apps) sign events and perform encryption/decryption using your private key, without sharing the key itself.

### bunker:// (Signer-initiated)

- **Bunker relay**: The relay used for bunker communication (editable only when the bunker is stopped).
- **NIP-46 Bunker switch**: Starts or stops the bunker service.
- **Auto start bunker on device boot**: When enabled, bunker starts automatically after reboot if bunker was enabled and valid `nsec`/relay are saved.
- **Bunker URI**: The `bunker://` URI to paste into a NIP-46 compatible client. Displayed when the bunker is running. The secret is **one-time for each new pairing** (URI updates after each successful new client connect and when you **Remove** a client). Share the latest URI from the screen for the next new client.
- **Copy bunker URI**: Copies the current URI (including the active secret) to the clipboard.
- **Reset Secret**: Resets the bunker secret, removes **all** paired clients, and stops the bunker. After you turn the bunker on again, share the new URI with every client.
- **Authorized clients**: Lists paired clients. Tap a name to edit its label; use **Remove** to revoke one client (rotates the bunker URI secret and updates the display/copy; other paired clients can reconnect without the new URI).
- **Status**: Shows bunker connection status and client activity.

### nostrconnect:// (Client-initiated)

- **Deep link**: Opening a `nostrconnect://` link from another app launches nokandro, switches to the Bunker tab, fills the URI field, and shows a **confirmation dialog** before starting the session (`NostrConnectDeepLinkActivity`).
- **nostrconnect:// URI input**: Paste a `nostrconnect://` URI provided by the client app.
- **Connect**: Starts a session with the specified client.
- **Scan QR**: Scans a QR code containing a `nostrconnect://` URI, fills the input field, and shows the same **confirmation dialog** as the deep link.
- **Client list**: Shows active nostrconnect sessions. Tap a client name to edit its label; use **Remove** to disconnect.
- The URI must include at least one `relay` and a `secret` (NIP-46). The signer returns the `secret` in the connect response `result` field for the client to validate. If no relay is specified, parsing will fail.

**Supported NIP-46 methods**: `connect`, `get_public_key`, `sign_event`, `nip04_encrypt`, `nip04_decrypt`, `nip44_encrypt`, `nip44_decrypt`, `switch_relays`, `ping`.

**Supported NIP-55 methods**: `get_public_key`, `sign_event`, `nip04_encrypt`, `nip04_decrypt`, `nip44_encrypt`, `nip44_decrypt`, `decrypt_zap_event` (via `nostrsigner:` and content providers).

For **ContentProvider** calls (`content://com.nokakoi.nokandro.*`): if the calling app is not yet authorized, nokandro opens the same approval dialog as the `nostrsigner:` intent path (Approve / Deny / Remember my choice) and returns **null** for that query (retry after granting). Permanent denial returns a cursor with a `rejected` column per NIP-55.

### NIP-55 Signer Permissions

Saved permissions (both granted and rejected choices when "Remember my choice" is checked) are listed at the bottom of the Bunker tab.
- You can view the list of caller applications, request types (like `get_public_key`, `sign_event` with event kinds), and their status (Allowed/Rejected).
- You can revoke/remove any individual permission by tapping the **Remove** button.


> **Note**: Enter your `nsec` in the Main tab before using the Bunker or nostrconnect features.
> 
> - `bunker://`: New clients are auto-authorized when they present the current bunker URI secret; after pairing, the secret rotates (one new client per URI snapshot). Previously paired clients reconnect without the secret. **Remove** also rotates the secret. **Reset Secret** clears all pairings and requires a new URI for everyone.
> - `nostrconnect://`: The client must send the correct `secret` in `connect`; invalid secrets are rejected. The signer returns the URI `secret` in the connect response `result` for the client to validate.

For detailed documentation on the Bunker feature, see [BUNKER.md](BUNKER.md).

## Widget & Tasker

- **Home screen widget**: Add the **Nostr Post** widget (picker name) to your home screen. The widget shows **Quick Post** on its label; tap the icon to start/stop the service, or tap the label to open the quick post dialog.
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
- **Bunker stops after sitting unused**:
  - The bunker foreground service now restores itself after Android sticky-restarts the process (credentials are reloaded from secure storage when the switch was left ON).
  - If it still stops, open the Bunker tab and tap **Exclude from battery optimization** (or allow the one-time prompt when enabling Bunker).
  - OEM battery / background restrictions can still kill the app; exclude nokandro there as well if needed.
- **Bunker not connecting**:
  - Ensure nsec is entered in the Main tab.
  - Check that the bunker relay is reachable.
  - If using boot auto-start, ensure the Bunker auto-start switch is ON and battery/background restrictions are not forcibly stopping the app after boot.
  - If Status shows `Secret mismatch` or `invalid secret`, the bunker URI may be outdated. **Copy bunker URI** from the Bunker tab (the secret changes after each new pairing and when you **Remove** a client). Use **Reset Secret** only when you want to clear all pairings and start over.
- **nostrconnect not connecting**:
  - Ensure nsec is entered in the Main tab.
  - If the client URI has no relay hints, fixed fallback relays are used. The client must be reachable on at least one of them.
  - Check that the `nostrconnect://` URI is valid and complete.

## Signing and safety

- This app handles your `nsec` (private key). It is stored on the device only; use at your own risk.
- **Secure Key Storage**: The private key (`nsec`) is encrypted at rest using Android Keystore and Jetpack Security (`EncryptedSharedPreferences`). Existing plaintext keys from older versions are automatically and securely migrated to the encrypted storage on first launch. Empty UI values are never written over a stored `nsec` (so a failed load cannot wipe the key). Encrypted preference files are excluded from Auto Backup.
- **bunker://**: New clients must present the **current** bunker URI secret to pair (auto-authorized on success). The secret then rotates (one new client per URI snapshot). Previously paired clients reconnect **without** the secret. **Remove** also rotates the secret. **Reset Secret** clears all pairings.
- **nostrconnect://**: The client proves the URI `secret` in `connect`; the signer returns the secret in the connect response for validation.
- **NIP-55**: Each sensitive request from another app can show an approval dialog (Approve / Deny / Remember my choice); permanent denial is stored per caller.

## ⚠️ For Android 13+ Users

Due to added features, "Notification Access" permission is now required.
When installing the APK downloaded from GitHub, you might see a "Restricted Settings" message when trying to enable this permission, and the switch may be greyed out.

In that case, please follow these steps to allow restricted settings:
1. Go to Android Settings -> "Apps" -> "nokandro".
2. Tap the menu in the top right corner (three dots) and select "Allow restricted settings".
3. Return to the app and try enabling the permission switch again.

---

## 概要

`nokandro` は Nostr プロトコルに接続し、受信したメッセージを TTS（音声合成）で読み上げたり、再生中の音楽情報（Now Playing）を共有したり、**NIP-46**（Bunker / nostrconnect）および **NIP-55**（`nostrsigner:` + ContentProvider）リモート署名として動作する Android アプリです。UIは **Main** タブと **Bunker** タブに分かれています。

## 主な機能

- リレー（WebSocket）へ接続してリアルタイムでイベント送受信を行います。
- **読み上げ (TTS)**: フォロー中のユーザー等の投稿を読み上げます（有効時）。
- **Now Playing**: 端末で再生中の音楽（通知リスナー経由）を検知し、Nostrステータス (kind: 30315) として投稿します。
- **NIP-46 Bunker**: 外部の Nostr クライアントからの署名・暗号化/復号リクエストに応答するリモート署名機能です。
- **nostrconnect://**: クライアントが提供する `nostrconnect://` URI を貼り付けや QR スキャンで接続できます。複数セッションの同時接続に対応しています。
- **NIP-55 Signer**: 外部署名対応クライアント向けに `nostrsigner:` と NIP-55 ContentProvider（`SIGN_EVENT`, `NIP04_DECRYPT` など）をサポートします。
- **端末再起動時の自動起動（任意）**: Bunker タブのスイッチで、端末再起動後に Bunker を自動起動できます。
- 指定した `npub` に基づくフォローリスト・ミュートリスト・ミュートワードを取得し、読み上げの判定やフィルタリングを行います。
- **オフタイマー**: 指定時間後にサービスを自動停止します。
- **Turn off Off timer when it ends**: 有効にすると、Off timer が終了した時に自動的に Off timer スイッチが OFF になります。これにより、誤ってタイマーが再度有効にされてしまうのを防ぎます。
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
- **Turn off Off timer when it ends**: 有効にすると、Off timer が終了した時に自動的に Off timer スイッチが OFF になります。これにより、誤ってタイマーが再度有効にされてしまうのを防ぎます。
- **Start / Stop**: サービスの開始・停止ボタン。
- **(Last Content)**: 最後に受信したメッセージが表示されます。

## Bunker タブ（NIP-46 リモート署名）

Bunker タブは NIP-46 リモート署名機能を提供します。外部の Nostr クライアント（Webアプリ等）が秘密鍵を共有せずに、イベントの署名や暗号化/復号をリクエストできます。

### bunker://（署名者起点）

- **Bunker relay**: Bunker 通信に使用するリレー（Bunker停止中のみ編集可能）。
- **NIP-46 Bunker スイッチ**: Bunker サービスの開始/停止。
- **Auto start bunker on device boot**: 有効にすると、Bunker が ON の状態かつ有効な `nsec`/relay が保存されている場合、端末再起動後に自動起動します。
- **Bunker URI**: NIP-46 対応クライアントに貼り付ける `bunker://` URI。Bunker 実行中に表示されます。**新規ペア成功ごと**に secret がローテし URI が更新されます（1 URI = 1 新規クライアント。次の新規には画面の最新 URI を Copy）。**Remove** 時も同様に更新されます。
- **Copy bunker URI**: 現在の URI（secret 含む）をクリップボードにコピーします。
- **Reset Secret**: シークレットをリセットし、**すべて**のペア済みクライアントを削除して Bunker を停止します。再度 ON にしたら全クライアントに新 URI を共有してください。
- **Authorized clients**: 認証済みクライアントを一覧表示します。名前タップでラベル編集、**Remove** で当該クライアントのみ削除（bunker URI の secret がローテし表示・コピーも更新。他のペア済みクライアントは新 URI なしで再接続可能）。
- **Status**: Bunker の接続状態やクライアントの動作状況を表示します。

### nostrconnect://（クライアント起点）

- **ディープリンク**: 他アプリから `nostrconnect://` を開くと Bunker タブに URI が入り、**接続確認ダイアログ**（Connect / Cancel）が表示されます。
- **nostrconnect:// URI 入力欄**: クライアントアプリから提供された `nostrconnect://` URI を貼り付けます。
- **Connect**: 指定したクライアントとのセッションを開始します。
- **Scan QR**: `nostrconnect://` URI を含む QR コードをスキャンし、入力欄に貼り付けたうえでディープリンクと同様の**接続確認ダイアログ**が表示されます。
- **クライアントリスト**: アクティブな nostrconnect セッションを表示します。名前をタップしてラベルを編集、**Remove** で切断できます。
- NIP-46に従い、URIには少なくとも1つ以上の `relay` と `secret` パラメータが必須です（実装上、リレーが指定されていない場合は接続エラーになります）。

**対応 NIP-46 メソッド**: `connect`, `get_public_key`, `sign_event`, `nip04_encrypt`, `nip04_decrypt`, `nip44_encrypt`, `nip44_decrypt`, `switch_relays`, `ping`

**対応 NIP-55 メソッド**: `get_public_key`, `sign_event`, `nip04_encrypt`, `nip04_decrypt`, `nip44_encrypt`, `nip44_decrypt`, `decrypt_zap_event`（`nostrsigner:` および ContentProvider 経由）

**ContentProvider**（`content://com.nokakoi.nokandro.*`）: 呼び出し元アプリが未許可の場合、`nostrsigner:` 経路と同様の承認ダイアログ（Approve / Deny / Remember my choice）を表示し、当該 `query` は **null** を返します（許可後に再 `query` してください）。恒常拒否時は NIP-55 どおり `rejected` 列付きカーソルを返します。

### NIP-55 署名権限の管理

「Remember my choice」にチェックを入れて保存された権限（許可または拒否）の一覧が、Bunkerタブの最下部に表示されます。
- 呼び出し元アプリのパッケージ名、要求の種類（`get_public_key`、`sign_event`（event kind含む）など）、およびステータス（Allowed/Rejected）を確認できます。
- 不要になった権限は、右側の **Remove** ボタンを押すことで個別に削除・破棄できます。


> **注意**: Bunker や nostrconnect を使用するには Main タブで `nsec` を入力してください。
> 
> - bunker:// は新規接続時に現在の URI の secret が必要です。ペア成功後に secret がローテします（同じ URI を別の新規 pubkey に使い回せません）。既知クライアントは secret なしで再接続。**Remove** でもローテ。**Reset Secret** で全ペア削除＋新 URI。
> - nostrconnect:// は接続応答の `result` に URI の `secret` を返します。クライアントが検証します。

Bunker 機能の詳細なドキュメントは [BUNKER.md](BUNKER.md) を参照してください。

## ウィジェット & Tasker

- **ホーム画面ウィジェット**: ウィジェット一覧では **Nostr Post**、画面上のラベルは **Quick Post** と表示されます。アイコンタップでサービスの ON/OFF、ラベルタップでクイック投稿ダイアログを開けます。
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
- **しばらく使っていないと Bunker が止まる**:
  - フォアグラウンドサービスはプロセス再起動（Sticky）時に、スイッチが ON のままならセキュア保存の nsec などから自動復元します。
  - それでも止まる場合は、Bunker タブの **Exclude from battery optimization**（または Bunker 有効化時の案内）でバッテリー最適化から除外してください。
  - メーカー独自の電池・バックグラウンド制限がある端末では、そこでも nokandro を除外してください。
- **Bunker が接続できない**:
  - Main タブで nsec が入力されているか確認してください。
  - Bunker リレーが到達可能か確認してください。
  - 端末再起動後の自動起動を使う場合は、Bunker 自動起動スイッチが ON か、また電池最適化などで起動が制限されていないか確認してください。
  - Status に `Secret mismatch` や `invalid secret` が出る場合、bunker URI が古い可能性があります。Bunker タブの **Copy bunker URI** で最新 URI を使ってください（新規ペア成功後や **Remove** 後に secret は変わります）。全員やり直すときだけ **Reset Secret** を使います。
- **nostrconnect が接続できない**:
  - Main タブで nsec が入力されているか確認してください。
  - URI にリレーが指定されていない場合、固定のフォールバックリレーが使用されます。クライアントがそのいずれかに到達可能である必要があります。
  - `nostrconnect://` URI が正しく完全であるか確認してください。

## 署名と安全性

- 本アプリは nsec（秘密鍵）を取り扱います。端末内にのみ保存されますが、利用は自己責任でお願いします。
- **セキュアキー保存**: 秘密鍵（`nsec`）は Android Keystore および Jetpack Security（`EncryptedSharedPreferences`）を利用して暗号化された状態で保存されます。旧バージョンから移行する場合、初回起動時に従来の平文キーは透過的かつ安全に暗号化領域へマイグレーションされます。読み込み失敗などで UI が空でも、保存済みの `nsec` を空文字で上書きしません。暗号化 prefs は Auto Backup 対象外です。
- **bunker://**: 新規クライアントは **現在の** bunker URI の secret でペアします（成功時に自動認可）。その直後に secret がローテします（1 URI = 1 新規）。既知クライアントは secret なしで再接続します。**Remove** でもローテします。**Reset Secret** で全ペア削除です。
- **nostrconnect://**: クライアントが `connect` で URI の secret を提示し、応答の `result` で検証します。
- **NIP-55**: 他アプリからのリクエストは承認ダイアログ（Approve / Deny / Remember my choice）を出せます。恒常拒否は呼び出し元ごとに保存されます。

## ⚠️ Android 13以降をお使いの方へ

機能追加に伴い、「通知へのアクセス」権限が必要になりました。
GithubからAPKをダウンロードしてインストールした場合、権限を有効にする際に「制限付き設定」というメッセージが表示され、スイッチが押せないことがあります。

その場合は、以下の手順で制限を解除してください：
1. Androidの本体設定から「アプリ」→「nokandro」を開く
2. 右上のメニュー（︙）から「制限付き設定を許可」を選択
3. 再度アプリ内のスイッチから権限を有効にしてください

