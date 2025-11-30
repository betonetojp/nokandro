# nokandro

Package name: `com.nokakoi.nokandro`

## Overview

`nokandro` is a lightweight Android app that connects to the Nostr protocol and reads received messages using TTS (Text-to-Speech). You can start and stop the background service from the main screen, and control its operation from the notification area.

## Main Features

- Connects to a relay (WebSocket) to receive events in real-time. The default connection is `wss://yabu.me` or a configured relay.
- Retrieves the follow list based on the specified `npub` (your public key) and prioritizes reading posts from followed users.
- Posts from non-followed users can be disabled for reading (via the `Allow non-followed posts` toggle).
- Different TTS voices can be specified for followed users and others (two voice selections).
- Displays received messages (last received content) in the main UI.
- The background service that performs TTS uses foreground notifications, and the service can be stopped from the notification.

## Installation and First Launch (APK Distribution)

1. Download and install the provided APK.
2. On first launch, you may be prompted for permissions such as network or notifications. If not granted, some features may be restricted.

## Main Screen Description

- `Follow list: not loaded` — Displays the loading status of the follow list.
- `Public mute: not loaded` — Displays the loading status of the public mute.
- `Max characters:` — Specifies the maximum number of characters for display (truncation).
- `Relay (wss://...)` — Enter the URL of the relay to connect to.
- `Your npub` — Enter your npub (bech32) or 64-character hex public key.
- `Invalid npub` — Error display if the input is invalid (usually hidden).
- `Get from Amber` — Retrieve npub from Amber (external signer apps supporting the NIP-55 style intent).
- `Speak petname` — Toggle whether to prepend the petname from the follow list to the reading.
- `Voice for followed users` — Select the TTS voice for followed users.
- `Allow non-followed posts` — Toggle whether to read posts from non-followed users.
- `Voice for non-followed users` — Select the TTS voice for non-followed users.
- `Refresh Voices` — Re-fetch available TTS voices.
- `Speech rate` — Adjust the TTS speech rate with a slider (reflected immediately in the service).
- `Start` — Start the background service.
- `Stop` — Stop the background service.
- `(no note yet)` — The last received message is displayed here.

## Follow List and Public Mute

- Follow list: Retrieves the list of public keys followed by the specified `npub` from the relay. The app displays the loading status of the follow list on the screen, and followed posts are prioritized (with different TTS voices or display prefixes), used for reading judgments.

- Public mute: Public keys included in the public mute list provided by the relay or events are excluded from reading. The app also displays the loading status of the public mute on the screen. Muting only affects reading, and handling of logs or internal records depends on the implementation.

## Notifications and Service Operation

- While the service is running, it resides in the notification area, and you can stop the service from actions like "Stop".
- On Android 13 and above, runtime permission for notifications (`POST_NOTIFICATIONS`) may be required.

## Common Issues and Troubleshooting

- Not receiving/reading:
  - `npub` may not be set, or `Allow others` is off and the user is not followed.
  - Check network connection or the specified relay URL.
- TTS not playing:
  - Check the device's volume/media volume.
  - If the selected TTS voice is not available on the device, it falls back to the default voice.
- Cannot stop from notification/service crashes:
  - Check the device's battery optimization settings or app permissions.

## Signing and Security

- The distributed APK is signed by the distributor. Attempting to overwrite with an APK signed differently will fail installation.

---

## 概要

`nokandro` は Nostr プロトコルに接続して受信したメッセージを TTS（音声合成）で読み上げる軽量な Android アプリです。メイン画面からバックグラウンドのサービスを起動・停止でき、通知領域で動作を制御します。

## 主な機能

- リレー（WebSocket）へ接続してリアルタイムでイベントを受信する。デフォルト接続先は `wss://yabu.me` または設定されたリレー。
- 指定した `npub`（自分の公開鍵）に基づくフォローリストを取得して、フォローしているユーザーの投稿を優先して読み上げる。
- フォローしていないユーザーの投稿は設定により読み上げを無効化可能（`Allow non-followed posts`切り替え）。
- フォロー済みユーザーとそれ以外で TTS の声を分けて指定可能（2つのボイス選択）。
- 受信したメッセージの表示（最後に受信した内容）をメイン画面で確認できます。
- バックグラウンドで常駐して TTS を行うサービスはフォアグラウンド通知を使用し、通知から停止操作が可能。

## インストールと初回起動（APK 配布）

1. 提供された APK をダウンロードしてインストールしてください。
2. 初回起動時にネットワークや通知などの権限を求められることがあります。許可しないと一部機能が制限されます。

## メイン画面の説明

- `Follow list: not loaded` — フォローリストの読み込み状態を表示します。
- `Public mute: not loaded` — 公開ミュートの読み込み状態を表示します。
- `Max characters:` — 表示用の最大文字数（切り詰め）を指定します。
- `Relay (wss://...)` — 接続先リレーの URL を入力します。
- `Your npub` — 自分の npub（bech32）または 64 文字の hex 公開鍵を入力します。
- `Invalid npub` — 入力が不正な場合のエラー表示（通常は非表示）。
- `Get from Amber` — Amber から npub を取得します。
- `Speak petname` — フォローリスト内の petnameを読み上げの先頭に付けるかどうかの切り替えます。
- `Voice for followed users` — フォロー済みユーザー用の TTS 音声を選択します。
- `Allow non-followed posts` — フォローしていない投稿を読み上げるかを切り替えます。
- `Voice for non-followed users` — フォロー外ユーザー用の TTS 音声を選択します。
- `Refresh Voices` — 利用可能な TTS 音声を再取得します。
- `Speech rate` — TTS の発話速度をスライダーで調整します（サービスに即時反映されます）。
- `Start` — バックグラウンドサービスを開始します。
- `Stop` — バックグラウンドサービスを停止します。
- `(no note yet)` — ここに最後に受信したメッセージが表示されます。

## フォローリストと公開ミュート（Follow list / Public mute）

- フォローリスト: 指定した `npub` に紐づくフォローしている公開鍵の一覧をリレーから取得します。アプリはフォローリストの読み込み状態を画面上に表示し、フォロー済みの投稿は優先的に扱われ（別の TTS 音声や表示プレフィックスなど）、読み上げの判定に用いられます。

- 公開ミュート: リレーやイベントで提供される公開ミュートリストに含まれる公開鍵は、読み上げ対象から除外されます。

## 通知とサービス動作

- サービス起動中は通知領域に常駐し、「Stop」などのアクションからサービスを停止できます。
- Android 13 以上では通知の実行時許可（`POST_NOTIFICATIONS`）が必要になる場合があります。

## よくあるトラブルと対処法

- 受信できない／読み上げない:
  - `npub` が未設定、または `Allow others` がオフで該当ユーザーがフォローされていない可能性があります。
  - ネットワーク接続や指定したリレー URL を確認してください。
- TTS が再生されない:
  - 端末の音量・メディア音量を確認してください。
  - 選択した TTS の声が端末で利用できない場合はデフォルトの声にフォールバックされます。
- 通知から停止できない・サービスが落ちる:
  - 端末の電池最適化設定やアプリ権限を確認してください。

## 署名と安全性

- 配布される APK は配布元が署名しています。署名が異なる APK を上書きしようとするとインストールに失敗します。
