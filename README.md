# nokandro

Package name: `com.nokakoi.nokandro`

[English](#overview) | [日本語 (Japanese)](#japanese)

> 日本語のドキュメントは本ページの下部にあります。[日本語セクションへ移動](#japanese)

## Overview

`nokandro` is a lightweight Android app that connects to the Nostr protocol. It reads incoming posts using TTS (Text-to-Speech), shares your "Now Playing" music status, and acts as a remote signer for both **NIP-46** (Bunker / nostrconnect) and **NIP-55** (`nostrsigner:` + content provider). The UI is split into **Main** and **Signer** tabs.

## Main Features

- Connects to a Nostr relay (WebSocket) to receive events and publish status in real-time.
- **In-App Updater**: Tap the version number to check for updates from GitHub Releases. Formatted release notes and download size are shown in an in-app dialog, allowing direct one-tap download and installation of new APKs. A gold badge indicates when a new release is available.
- **Profile Avatar**: Fetches your Nostr profile metadata (kind: 0) via WebSocket and displays your profile picture as a circular avatar inside the npub field (with memory and disk caching).
- **Text-to-Speech (TTS)**: Reads incoming posts from followed users (configurable).
- **Now Playing**: Detects music playing on your device (via notification listener) and publishes it as a Nostr status (kind: 30315).
- **NIP-46 Remote Signer**: Acts as a remote signer (`bunker://` and `nostrconnect://`) so external Nostr clients can request event signing and encryption/decryption without exposing private keys. Multiple concurrent sessions and client website icon fetching (including PWA and GitHub Pages subpaths) are supported.
- **NIP-55 Signer**: Handles `nostrsigner:` intents and NIP-55 content provider methods (`SIGN_EVENT`, `NIP04_DECRYPT`, etc.) for external signer-compatible clients.
- **NIP-55 Permission Manager**: Organizes remembered permissions grouped by caller application with app icons and names, collapsible permission cards, human-readable kind annotations, Allowed/Rejected badges, and batch clear per application.
- **Modern UI & Sticky Footer**: Material Design 3 styling with a persistent sticky footer showing real-time service status (Running/Stopped badges), off timer remaining time, and quick controls.
- **Boot auto-start (optional)**: Bunker can be auto-started after device reboot via a switch in the Signer tab.
- Retrieves the follow list, public mute list, and muted words list for filtering.
- Different TTS voices can be specified for followed users and others.
- **Off timer**: Automatically stops the service after a configurable number of minutes.
- **Turn off Off timer when it ends**: When enabled, the Off timer switch will automatically turn OFF when the timer expires.
- **Relay list fetch**: Fetches your relay list (kind: 10002) from known relays and lets you pick one.
- **Home screen widgets**: Includes a 2x1 quick toggle & post widget, plus a feature-rich 4x2 **Nostr Live Note widget** featuring a real-time post stream, kind 0 author avatars, kind 7 star reactions, 20-post browsing navigation (`< 20/20 >`), independent service toggle, and dedicated TTS/Muted controls.
- **Monochrome Notification Icon**: Silhouette notification vector icon (`ic_stat_nokandro`) with dynamic tinting for full compatibility with modern Android versions (Google Pixel, Android 12-15) without solid circular masking.
- **Tasker / automation support**: Start and stop the service via broadcast intents.
- The background service uses foreground notifications, ensuring stability and allowing stop control from the notification area.

## Installation and First Launch (APK Distribution)

1. Download and install the provided APK.
2. On first launch, you may be prompted for permissions such as notifications. If enabling "Broadcast Now Playing", you will be prompted to grant "Notification Listener" permission.
3. When using the In-App Updater for the first time, Android will prompt you to grant "Install unknown apps" permission for nokandro.

## Main Tab

- **App Title & Version (In-App Updater)**: Tap the version number (top right) to check for updates. When a newer version is available on GitHub Releases, a gold badge displays `vCurrent -> vNew`. Tapping it opens an update dialog with rich formatted markdown release notes and download size, allowing you to download and install the update directly inside the app (first-time update will prompt to enable "Install unknown apps" in Settings).
- **Your npub & Profile Avatar**: Your Nostr public key (bech32 `npub...` or hex). nokandro automatically queries relays for your `kind: 0` profile metadata and displays your profile picture as a circular avatar in the input field.
- **Your nsec (optional)**: Your Nostr private key (bech32 `nsec...`). Required for publishing "Now Playing" status and for the Signer (Bunker) feature. Entering an nsec automatically derives the corresponding npub.
- **Relay**: The URL of the relay to connect to. Use the **v** button to fetch your relay list (kind: 10002) and select one.
- **Follow list / Muted user / Muted words**: Displays loading status of these lists.
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
  - **Speech rate**: Adjust reading speed (0.50x - 1.50x). Can be changed while the service is running.
- **Stop on audio disconnect**: Automatically stops the service when audio output is disconnected (e.g., Bluetooth).
- **Broadcast Now Playing**: Toggle to enable publishing your current music track to Nostr. Requires "Notification Listener" permission and a valid nsec.
- **Off timer**: When enabled, the service will automatically stop after the specified number of minutes. A countdown is displayed while running.
- **Turn off Off timer when it ends**: When enabled, the Off timer switch will automatically turn OFF when the timer expires.
- **Sticky Footer**: Stays pinned at the bottom of the screen. Shows live service state badges (Running / Stopped), off timer countdown, and the Start / Stop button.
- **(Last Content)**: Displays the most recent message received.

## Signer Tab (NIP-46 Remote Signer & NIP-55 Signer)

The Signer tab provides remote signing capabilities for both NIP-46 (Bunker / nostrconnect) and NIP-55 external client applications, letting them sign events and perform encryption/decryption using your private key without sharing the key itself.

### bunker:// (Signer-initiated)

- **Bunker relay**: The relay used for bunker communication (editable only when the bunker is stopped).
- **NIP-46 Bunker switch**: Starts or stops the bunker service.
- **Auto start bunker on device boot**: When enabled, bunker starts automatically after reboot if bunker was enabled and valid `nsec`/relay are saved.
- **Bunker URI**: The `bunker://` URI to paste into a NIP-46 compatible client. Displayed when the bunker is running. The secret is **one-time for each new pairing** (URI updates after each successful new client connect and when you **Remove** a client). Share the latest URI from the screen for the next new client.
- **Copy bunker URI**: Copies the current URI (including the active secret) to the clipboard.
- **Reset Secret**: Resets the bunker secret, removes **all** paired clients, and stops the bunker. After you turn the bunker on again, share the new URI with every client.
- **Authorized clients**: Lists paired clients with website icons and names. Tap a name to edit its label; use **Remove** to revoke one client (rotates the bunker URI secret and updates the display/copy; other paired clients can reconnect without the new URI).
- **Status**: Shows bunker connection status and client activity.

### nostrconnect:// (Client-initiated)

- **Deep link**: Opening a `nostrconnect://` link from another app launches nokandro, switches to the Signer tab, fills the URI field, and shows a **confirmation dialog** before starting the session (`NostrConnectDeepLinkActivity`).
- **nostrconnect:// URI input**: Paste a `nostrconnect://` URI provided by the client app.
- **Connect**: Starts a session with the specified client.
- **Scan QR**: Scans a QR code containing a `nostrconnect://` URI, fills the input field, and shows the same **confirmation dialog** as the deep link.
- **Client list**: Shows active nostrconnect sessions with client website icons (supports standard favicons, PWA apple-touch-icons, and repository subpaths). Tap a client name to edit its label; use **Remove** to disconnect.
- The URI must include at least one `relay` and a `secret` (NIP-46). The signer returns the `secret` in the connect response `result` field for the client to validate.

**Supported NIP-46 methods**: `connect`, `get_public_key`, `sign_event`, `nip04_encrypt`, `nip04_decrypt`, `nip44_encrypt`, `nip44_decrypt`, `switch_relays`, `ping`.

**Supported NIP-55 methods**: `get_public_key`, `sign_event`, `nip04_encrypt`, `nip04_decrypt`, `nip44_encrypt`, `nip44_decrypt`, `decrypt_zap_event` (via `nostrsigner:` and content providers).

For **ContentProvider** calls (`content://com.nokakoi.nokandro.*`): if the calling app is not yet authorized, nokandro opens the same approval dialog as the `nostrsigner:` intent path (Approve / Deny / Remember my choice) and returns **null** for that query (retry after granting). Permanent denial returns a cursor with a `rejected` column per NIP-55.

### NIP-55 Signer Permissions

Saved permissions (both granted and rejected choices when "Remember my choice" is checked) are listed at the bottom of the Signer tab:
- **Grouped by Application**: Permissions are organized in cards by caller application, displaying the app icon and application name.
- **Collapsible Lists**: Tap the application header to expand or collapse its permissions list.
- **Readable Annotations**: Shows request types along with human-readable event kinds (e.g. `Note (kind 1)`, `Reaction (kind 7)`).
- **Status Badges**: Clear visual badges for Allowed and Rejected permissions.
- **Management Controls**: Revoke an individual permission using its **Remove** button, or remove all permissions for an application at once with the **Clear** button.

[Note]: Enter your `nsec` in the Main tab before using the Bunker or nostrconnect features.
- `bunker://`: New clients are auto-authorized when they present the current bunker URI secret; after pairing, the secret rotates (one new client per URI snapshot). Previously paired clients reconnect without the secret. **Remove** also rotates the secret. **Reset Secret** clears all pairings and requires a new URI for everyone.
- `nostrconnect://`: The client must send the correct `secret` in `connect`; invalid secrets are rejected. The signer returns the URI `secret` in the connect response `result` for the client to validate.

For detailed documentation on the remote signer feature, see [BUNKER.md](BUNKER.md).

## Widget & Tasker

- **Home screen widget**: Add the **Nostr Post** widget (picker name) to your home screen. The widget shows **Quick Post** on its label; tap the icon to start/stop the service, or tap the label to open the quick post dialog.
- **Tasker / automation**: Send broadcast intents to control the service:
  - `com.nokakoi.nokandro.ACTION_START` - Start the service using saved preferences.
  - `com.nokakoi.nokandro.ACTION_STOP` - Stop the service.

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
  - If it still stops, open the Signer tab and tap **Exclude from battery optimization** (or allow the one-time prompt when enabling Bunker).
  - OEM battery / background restrictions can still kill the app; exclude nokandro there as well if needed.
- **Bunker not connecting**:
  - Ensure nsec is entered in the Main tab.
  - Check that the bunker relay is reachable.
  - If using boot auto-start, ensure the Bunker auto-start switch is ON and battery/background restrictions are not forcibly stopping the app after boot.
  - If Status shows `Secret mismatch` or `invalid secret`, the bunker URI may be outdated. **Copy bunker URI** from the Signer tab (the secret changes after each new pairing and when you **Remove** a client). Use **Reset Secret** only when you want to clear all pairings and start over.
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

## Note for Android 13+ Users

Due to added features, "Notification Access" permission is now required.
When installing the APK downloaded from GitHub, you might see a "Restricted Settings" message when trying to enable this permission, and the switch may be greyed out.

In that case, please follow these steps to allow restricted settings:
1. Go to Android Settings -> "Apps" -> "nokandro".
2. Tap the menu in the top right corner (three dots) and select "Allow restricted settings".
3. Return to the app and try enabling the permission switch again.

## Note for In-App Updates (Unknown App Sources Permission)

When installing an update directly from within nokandro for the first time, Android 8.0+ requires permission to allow nokandro to install unknown apps:
1. Tap the version number and tap "Update" in the update dialog.
2. A "Permission Required" dialog will appear. Tap "Open Settings".
3. In the Android Settings screen that opens, toggle "Allow from this source" to ON.
4. Return to nokandro and tap "Update" again to begin downloading and installing the new version.

---

<a id="japanese"></a>
## 概要

`nokandro` は Nostr プロトコルに接続し、受信したメッセージを TTS（音声合成）で読み上げたり、再生中の音楽情報（Now Playing）を共有したり、**NIP-46**（Bunker / nostrconnect）および **NIP-55**（`nostrsigner:` + ContentProvider）リモート署名として動作する Android アプリです。UIは **Main** タブと **Signer** タブに分かれています。

## 主な機能

- リレー（WebSocket）へ接続してリアルタイムでイベント送受信を行います。
- **アプリ内アップデーター (In-App Updater)**: バージョン番号をタップして GitHub Releases の最新版を直接チェック。マークダウン整形されたリリースノートとファイルサイズをダイアログで確認し、アプリ内から直接 APK のダウンロード・インストールを行えます。最新版がある場合は金色のバッジが表示されます。
- **プロフィールアバター表示**: WebSocket 経由で自身の Nostr プロフィールメタデータ (kind: 0) を取得し、`Your npub` 欄の左側に円形アバター写真を表示します（メモリおよびディスクキャッシュ対応）。
- **読み上げ (TTS)**: フォロー中のユーザー等の投稿を読み上げます（有効時）。
- **Now Playing**: 端末で再生中の音楽（通知リスナー経由）を検知し、Nostrステータス (kind: 30315) として投稿します。
- **NIP-46 リモート署名**: 外部の Nostr クライアントからの署名・暗号化/復号リクエストに応答するリモート署名機能です（`bunker://` および `nostrconnect://` に対応）。クライアントの Web アイコン取得（PWA apple-touch-icon や GitHub Pages サブパス対応）や表示名のインライン編集が可能です。
- **NIP-55 Signer**: 外部署名対応クライアント向けに `nostrsigner:` と NIP-55 ContentProvider（`SIGN_EVENT`, `NIP04_DECRYPT` など）をサポートします。
- **NIP-55 権限マネージャー**: 保存された権限を呼び出し元アプリごとにアプリアイコン・名前付きカードでグループ化。展開/折りたたみ、わかりやすいイベント種別注釈、Allowed/Rejected バッジ、アプリ単位の一括削除（Clear）に対応しています。
- **モダンUI & スティッキーフッター**: Material Design 3 に準拠。画面下部に常駐するフッターにリアルタイムのサービス稼働状況（Running/Stopped バッジ）やオフタイマー残時間を表示します。
- **端末再起動時の自動起動（任意）**: Signer タブのスイッチで、端末再起動後に Bunker を自動起動できます。
- 指定した `npub` に基づくフォローリスト・ミュートリスト・ミュートワードを取得し、読み上げの判定やフィルタリングを行います。
- **オフタイマー**: 指定時間後にサービスを自動停止します。
- **Turn off Off timer when it ends**: 有効にすると、Off timer が終了した時に自動的に Off timer スイッチが OFF になります。
- **リレーリスト取得**: リレーリスト (kind: 10002) を取得して接続先を選択できます。
- **ホーム画面ウィジェット**: サービスのON/OFFとクイック投稿ができる 2x1 ウィジェットに加え、Nostrの最新投稿をリアルタイム表示する 4x2「**Nostr Live Note ウィジェット**」を搭載。投稿者のアイコン・アバター表示、最大20件の履歴ナビゲーション（左右タップおよび下部中央の `< 20/20 >` 操作）、スターリアクション（★/☆ React）、サービス稼働（投稿受信）と音声読み上げ（TTS/Muted）の個別切り替えに対応しています。
- **通知アイコンのシルエット対応**: Google Pixel や Android 12〜15 などの新しい端末で通知アイコンが丸く塗りつぶされる問題に対応した、背景透明・単色ストロークのベクターアイコン（`ic_stat_nokandro`）を搭載。
- **Tasker / 自動化**: ブロードキャスト Intent でサービスの開始・停止を制御できます。
- バックグラウンドサービスはフォアグラウンド通知を使用し、通知領域から停止操作が可能です。

## インストールと初回起動

1. APK をインストールしてください。
2. 初回起動時、通知権限などが求められます。「Broadcast Now Playing」を有効にする際は、別途「通知へのアクセス（Notification Listener）」権限の許可を促されます。
3. アプリ内アップデーターを初めて使用する際、nokandro による「不明なアプリのインストール」権限の許可が求められます。

## Main タブ

- **タイトル・バージョン（In-App Updater）**: 右上のバージョン番号をタップすると、GitHub Releases の最新版をチェックします。更新がある場合は金色バッジ（`v現在 -> v最新`）が表示されます。タップするとマークダウン整形されたリリースノートとファイルサイズが表示され、アプリ内から直接 APK をダウンロードしてインストールできます（初回アップデート時は設定画面で「不明なアプリのインストール」を許可する案内が表示されます）。
- **Your npub & プロフィールアバター**: 自分の公開鍵 (`npub...` または hex)。リレーからプロフィールメタデータ (kind: 0) を自動取得し、入力欄の左側に円形アバターとして表示します。
- **Your nsec (任意)**: 自分の秘密鍵 (`nsec...`)。「Now Playing」の投稿や Signer（Bunker）機能に必要です。入力すると npub が自動的に導出されます。
- **Relay**: 接続先リレーの URL。**v** ボタンでリレーリスト (kind: 10002) を取得して選択できます。
- **Follow list / Muted user / Muted words**: リストの読み込み状況表示。
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
  - **Speech rate**: 読み上げ速度の調整（0.50x - 1.50x）。サービス実行中でも変更可能です。
- **Stop on audio disconnect**: オーディオ機器（Bluetoothなど）の切断検知時に自動停止します。
- **Broadcast Now Playing**: 再生中の音楽情報を Nostr に投稿する機能の ON/OFF。ON にするには通知リスナー権限と nsec が必要です。
- **Off timer**: 有効にすると、指定した分数の経過後にサービスを自動停止します。実行中はカウントダウンが表示されます。
- **Turn off Off timer when it ends**: 有効にすると、Off timer が終了した時に自動的に Off timer スイッチが OFF になります。
- **スティッキーフッター**: 画面下部に固定表示。各サービスのリアルタイム稼働状況（Running / Stopped バッジ）、タイマー残時間、Start / Stop ボタンを配置。
- **(Last Content)**: 最後に受信したメッセージが表示されます。

## Signer タブ（NIP-46 リモート署名 & NIP-55 Signer）

Signer タブは NIP-46 リモート署名および NIP-55 外部署名機能を提供します。外部の Nostr クライアント（Webアプリ等）が秘密鍵を共有せずに、イベントの署名や暗号化/復号をリクエストできます。

### bunker://（署名者起点）

- **Bunker relay**: Bunker 通信に使用するリレー（Bunker停止中のみ編集可能）。
- **NIP-46 Bunker スイッチ**: Bunker サービスの開始/停止。
- **Auto start bunker on device boot**: 有効にすると、Bunker が ON の状態かつ有効な `nsec`/relay が保存されている場合、端末再起動後に自動起動します。
- **Bunker URI**: NIP-46 対応クライアントに貼り付ける `bunker://` URI。Bunker 実行中に表示されます。**新規ペア成功ごと**に secret がローテし URI が更新されます（1 URI = 1 新規クライアント。次の新規には画面の最新 URI を Copy）。**Remove** 時も同様に更新されます。
- **Copy bunker URI**: 現在の URI（secret 含む）をクリップボードにコピーします。
- **Reset Secret**: シークレットをリセットし、**すべて**のペア済みクライアントを削除して Bunker を停止します。再度 ON にしたら全クライアントに新 URI を共有してください。
- **Authorized clients**: 認証済みクライアントを Web アイコン・名前付きで一覧表示します。名前タップでラベル編集、**Remove** で当該クライアントのみ削除（bunker URI の secret がローテし表示・コピーも更新。他のペア済みクライアントは新 URI なしで再接続可能）。
- **Status**: Bunker の接続状態やクライアントの動作状況を表示します。

### nostrconnect://（クライアント起点）

- **ディープリンク**: 他アプリから `nostrconnect://` を開くと Signer タブに URI が入り、**接続確認ダイアログ**（Connect / Cancel）が表示されます。
- **nostrconnect:// URI 入力欄**: クライアントアプリから提供された `nostrconnect://` URI を貼り付けます。
- **Connect**: 指定したクライアントとのセッションを開始します。
- **Scan QR**: `nostrconnect://` URI を含む QR コードをスキャンし、入力欄に貼り付けたうえでディープリンクと同様の**接続確認ダイアログ**が表示されます。
- **クライアントリスト**: アクティブな nostrconnect セッションをクライアント Web アイコン付き（PWA apple-touch-icon や GitHub Pages 等のサブパスにも対応）で表示します。名前をタップしてラベルを編集、**Remove** で切断できます。
- NIP-46に従い、URIには少なくとも1つ以上の `relay` と `secret` パラメータが必須です。

**対応 NIP-46 メソッド**: `connect`, `get_public_key`, `sign_event`, `nip04_encrypt`, `nip04_decrypt`, `nip44_encrypt`, `nip44_decrypt`, `switch_relays`, `ping`

**対応 NIP-55 メソッド**: `get_public_key`, `sign_event`, `nip04_encrypt`, `nip04_decrypt`, `nip44_encrypt`, `nip44_decrypt`, `decrypt_zap_event`（`nostrsigner:` および ContentProvider 経由）

**ContentProvider**（`content://com.nokakoi.nokandro.*`）: 呼び出し元アプリが未許可の場合、`nostrsigner:` 経路と同様の承認ダイアログ（Approve / Deny / Remember my choice）を表示し、当該 `query` は **null** を返します（許可後に再 `query` してください）。恒常拒否時は NIP-55 どおり `rejected` 列付きカーソルを返します。

### NIP-55 署名権限の管理

「Remember my choice」にチェックを入れて保存された権限（許可または拒否）の一覧が、Signer タブの最下部に表示されます。
- **アプリごとのグループ表示**: 呼び出し元アプリごとにカードでグループ化され、アプリアイコンとアプリ名が表示されます。
- **折りたたみ表示**: アプリヘッダーをタップして権限一覧を展開・折りたたみできます。
- **わかりやすい注釈**: 要求種別とわかりやすいイベント種別注釈（例: `Note (kind 1)`, `Reaction (kind 7)` 等）が表示されます。
- **ステータスバッジ**: Allowed（許可）/ Rejected（拒否）がひと目で判別できるバッジで表示されます。
- **削除コントロール**: 不要になった権限は個別の **Remove** ボタンで破棄できるほか、アプリ単位の **Clear** ボタンで当該アプリの全権限を一括破棄できます。

[注意]: Bunker や nostrconnect を使用するには Main タブで `nsec` を入力してください。
- bunker:// は新規接続時に現在の URI の secret が必要です。ペア成功後に secret がローテします（同じ URI を別の新規 pubkey に使い回せません）。既知クライアントは secret なしで再接続。**Remove** でもローテ。**Reset Secret** で全ペア削除＋新 URI。
- nostrconnect:// は接続応答の `result` に URI の `secret` を返します。クライアントが検証します。

リモート署名機能の詳細なドキュメントは [BUNKER.md](BUNKER.md) を参照してください。

## ウィジェット & Tasker

- **ホーム画面ウィジェット**: ウィジェット一覧では **Nostr Post**、画面上のラベルは **Quick Post** と表示されます。アイコンタップでサービスの ON/OFF、ラベルタップでクイック投稿ダイアログを開けます。
- **Tasker / 自動化**: ブロードキャスト Intent でサービスを制御できます。
  - `com.nokakoi.nokandro.ACTION_START` - 保存済みの設定でサービスを開始。
  - `com.nokakoi.nokandro.ACTION_STOP` - サービスを停止。

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
  - それでも止まる場合は、Signer タブの **Exclude from battery optimization**（または Bunker 有効化時の案内）でバッテリー最適化から除外してください。
  - メーカー独自の電池・バックグラウンド制限がある端末では、そこでも nokandro を除外してください。
- **Bunker が接続できない**:
  - Main タブで nsec が入力されているか確認してください。
  - Bunker リレーが到達可能か確認してください。
  - 端末再起動後の自動起動を使う場合は、Bunker 自動起動スイッチが ON か、また電池最適化などで起動が制限されていないか確認してください。
  - Status に `Secret mismatch` や `invalid secret` が出る場合、bunker URI が古い可能性があります。Signer タブの **Copy bunker URI** で最新 URI を使ってください（新規ペア成功後や **Remove** 後に secret は変わります）。全員やり直すときだけ **Reset Secret** を使います。
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

## Android 13以降をお使いの方へ

機能追加に伴い、「通知へのアクセス」権限が必要になりました。
GitHubからAPKをダウンロードしてインストールした場合、権限を有効にする際に「制限付き設定」というメッセージが表示され、スイッチが押せないことがあります。

その場合は、以下の手順で制限を解除してください：
1. Androidの本体設定から「アプリ」->「nokandro」を開く
2. 右上のメニューから「制限付き設定を許可」を選択
3. 再度アプリ内のスイッチから権限を有効にしてください

## アプリ内アップデートの初回実行時について（不明なアプリのインストール許可）

アプリ内の更新ダイアログから直接最新版をインストールする場合、Android 8.0 以降では端末のセキュリティ設定により、nokandro に対する「不明なアプリのインストール（この提供元のアプリを許可）」権限が初回のみ必要となります。

1. バージョン番号をタップし、表示された更新ダイアログで「Update」をタップします。
2. 初回のみ「Permission Required」ダイアログが表示されますので、「Open Settings」をタップします。
3. Android の設定画面が開きますので、「この提供元のアプリを許可」のスイッチを ON にしてください。
4. nokandro アプリに戻り、再度「Update」をタップすると APK のダウンロードとインストールが自動的に行われます。


