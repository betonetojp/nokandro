# nokandro

Package name: `com.nokakoi.nokandro`

## Overview

`nokandro` is a lightweight Android app that connects to the Nostr protocol and shares your "Now Playing" music status or reads received messages using TTS (Text-to-Speech). You can start and stop the background service from the main screen, and control its operation from the notification area.

## Main Features

- Connects to a relay (WebSocket) to receive events (optional) and publish status in real-time.
- **Text-to-Speech (TTS)**: Reads incoming posts from followed users (if enabled).
- **Now Playing**: Detects music playing on your device (via notification listener) and publishes it as a Nostr status (kind: 30315).
- Retrieves the follow list based on the specified `npub` (your public key) and prioritizes reading posts from followed users.
- Different TTS voices can be specified for followed users and others.
- The background service uses foreground notifications, ensuring stability and allowing stop control from the notification area.

## Installation and First Launch (APK Distribution)

1. Download and install the provided APK.
2. On first launch, you may be prompted for permissions such as notifications. If enabling "Broadcast Now Playing", you will be prompted to grant "Notification Listener" permission.

## Main Screen Description

- **App Title & Version**: Tap the version number (top right) to open the latest GitHub release page.
- **Relay**: The URL of the relay to connect to (e.g., `wss://yabu.me`).
- **Your npub**: Your Nostr public key (bech32 `npub...` or hex).
- **Your nsec (optional)**: Your Nostr private key (bech32 `nsec...`). Required for publishing "Now Playing" status.
- **Allow non-followed posts**: Toggle to read posts from users you don't follow.
- **Broadcast Now Playing**: Toggle to enable publishing your current music track to Nostr. Requires "Notification Listener" permission.
- **Status**: Debug/status text showing the current music state or broadcast result.
- **Enable Text-to-Speech**: Master switch for the TTS feature.
- **(TTS Settings)**:
  - **TTS language**: Filter available voices by language.
  - **Voice for followed users**: Select voice for followed users.
  - **Voice for non-followed users**: Select voice for others.
  - **Refresh Voices**: Reload available TTS voices.
  - **Speech rate**: Adjust reading speed.
- **Start**: Start the background service.
- **Stop**: Stop the background service.
- **Follow list / Public mute**: Displays loading status of these lists.
- **Max characters**: Truncation length for displayed/spoken text.
- **(Last Content)**: Displays the most recent message received or status update.

## Follow List and Public Mute

- **Follow list**: Retrieves your follow list from the relay (kind: 3). Followed users get priority handling (voice selection).
- **Public mute**: Retrieies your mute list (kind: 10000). Muted users are skipped by TTS.

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

## ⚠️ For Android 13+ Users

Due to added features, "Notification Access" permission is now required.
When installing the APK downloaded from GitHub, you might see a "Restricted Settings" message when trying to enable this permission, and the switch may be greyed out.

In that case, please follow these steps to allow restricted settings:
1. Go to Android Settings -> "Apps" -> "nokandro".
2. Tap the menu in the top right corner (three dots) and select "Allow restricted settings".
3. Return to the app and try enabling the permission switch again.

---

## 概要

`nokandro` は Nostr プロトコルに接続し、再生中の音楽情報（Now Playing）を共有したり、受信したメッセージを TTS（音声合成）で読み上げる Android アプリです。

## 主な機能

- リレー（WebSocket）へ接続してリアルタイムでイベント送受信を行います。
- **読み上げ (TTS)**: フォロー中のユーザー等の投稿を読み上げます（有効時）。
- **Now Playing**: 端末で再生中の音楽（通知リスナー経由）を検知し、Nostrステータス (kind: 30315) として投稿します。
- 指定した `npub` に基づくフォローリストを取得し、読み上げの判定やボイスの使い分けを行います。
- バックグラウンドサービスはフォアグラウンド通知を使用し、通知領域から停止操作が可能です。

## インストールと初回起動

1. APK をインストールしてください。
2. 初回起動時、通知権限などが求められます。「Broadcast Now Playing」を有効にする際は、別途「通知へのアクセス（Notification Listener）」権限の許可を促されます。

## メイン画面の説明

- **タイトル・バージョン**: 右上のバージョン番号をタップすると、GitHub の最新リリース配布ページを開きます。更新がある場合は赤字で表示されます。
- **Relay**: 接続先リレーの URL。
- **Your npub**: 自分の公開鍵 (`npub...` または hex)。
- **Your nsec (任意)**: 自分の秘密鍵 (`nsec...`)。「Now Playing」の投稿（署名）に必要です。
- **Allow non-followed posts**: フォローしていないユーザーの投稿も読み上げるかどうかの設定。
- **Broadcast Now Playing**: 再生中の音楽情報を Nostr に投稿する機能の ON/OFF。ON にするには通知リスナー権限が必要です。
- **Status**: 現在の音楽情報や投稿ステータスを表示するデバッグ領域。
- **Enable Text-to-Speech**: TTS 読み上げ機能のメインスイッチ。
- **(TTS 設定)**:
  - **TTS language**: ボイス選択リストの言語フィルタ。
  - **Voice for followed users**: フォロー済みユーザー用の声。
  - **Voice for non-followed users**: その他のユーザー用の声。
  - **Refresh Voices**: ボイスリストの再取得。
  - **Speech rate**: 読み上げ速度の調整。
- **Start / Stop**: サービスの開始・停止ボタン。
- **Follow list / Public mute**: リストの読み込み状況表示。
- **Max characters**: テキスト表示・読み上げの最大文字数制限。
- **(Last Content)**: 最後に受信したメッセージやステータスログが表示されます。

## 注意点

- **Now Playing が投稿されない**:
  - Android 設定で `nokandro` に「通知へのアクセス」権限が許可されているか確認してください。
  - 音楽プレイヤーアプリが標準的なメディア通知を出している必要があります。
- **TTS が聞こえない**:
  - 音量設定や `Enable Text-to-Speech` スイッチを確認してください。

## 署名と安全性

- 本アプリは nsec（秘密鍵）を取り扱います。外部流出しないよう端末内に保存されますが、利用は自己責任でお願いします。

## ⚠️ Android 13以降をお使いの方へ

機能追加に伴い、「通知へのアクセス」権限が必要になりました。
GithubからAPKをダウンロードしてインストールした場合、権限を有効にする際に「制限付き設定」というメッセージが表示され、スイッチが押せないことがあります。

その場合は、以下の手順で制限を解除してください：
1. Androidの本体設定から「アプリ」→「nokandro」を開く
2. 右上のメニュー（︙）から「制限付き設定を許可」を選択
3. 再度アプリ内のスイッチから権限を有効にしてください

