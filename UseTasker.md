# Using Tasker with nokandro

You can control the **Start** and **Stop** of nokandro's background service from [Tasker](https://tasker.joaoapps.com/) using broadcast intents.

## Prerequisites

- Configure and save all settings (Relay, npub, etc.) in the nokandro app at least once before using Tasker.
- Tasker reads the saved settings automatically when starting the service.

## Broadcast Actions

| Action | Effect |
|---|---|
| `com.nokakoi.nokandro.ACTION_START` | Start the background service using saved settings |
| `com.nokakoi.nokandro.ACTION_STOP` | Stop the background service |

## Setting Up Tasker

### Start

1. In Tasker, create or open a **Task**.
2. Tap **+** → **System** → **Send Intent**.
3. Fill in the fields as follows:

   | Field | Value |
   |---|---|
   | Action | `com.nokakoi.nokandro.ACTION_START` |
   | Package | `com.nokakoi.nokandro` |
   | Target | **Broadcast Receiver** |

4. Leave all other fields empty. Tap the back/save button.

### Stop

1. In Tasker, create or open a **Task**.
2. Tap **+** → **System** → **Send Intent**.
3. Fill in the fields as follows:

   | Field | Value |
   |---|---|
   | Action | `com.nokakoi.nokandro.ACTION_STOP` |
   | Package | `com.nokakoi.nokandro` |
   | Target | **Broadcast Receiver** |

4. Leave all other fields empty. Tap the back/save button.

## Notes

- If the service is already running, `ACTION_START` is ignored.
- If npub is not saved in the app settings, `ACTION_START` is ignored.
- You can combine these tasks with Tasker **Profiles** (e.g., trigger Start when connecting Bluetooth headphones, and Stop when disconnecting).

---

# Tasker から nokandro を操作する

nokandro のバックグラウンドサービスを [Tasker](https://tasker.joaoapps.com/) からブロードキャストインテントで **Start / Stop** できます。

## 前提条件

- Tasker から操作する前に、nokandro アプリで Relay・npub などの設定を一度保存しておく必要があります。
- Start 時は保存済みの設定が自動的に読み込まれます。

## ブロードキャストアクション

| アクション | 動作 |
|---|---|
| `com.nokakoi.nokandro.ACTION_START` | 保存済み設定でサービスを起動する |
| `com.nokakoi.nokandro.ACTION_STOP` | サービスを停止する |

## Tasker の設定手順

### Start（起動）

1. Tasker でタスクを作成または開く。
2. **＋** → **システム** → **インテントを送信** を選択。
3. 以下のように入力する：

   | 項目 | 値 |
   |---|---|
   | アクション | `com.nokakoi.nokandro.ACTION_START` |
   | パッケージ | `com.nokakoi.nokandro` |
   | ターゲット | **ブロードキャストレシーバー** |

4. その他の項目は空欄のままで保存。

### Stop（停止）

1. Tasker でタスクを作成または開く。
2. **＋** → **システム** → **インテントを送信** を選択。
3. 以下のように入力する：

   | 項目 | 値 |
   |---|---|
   | アクション | `com.nokakoi.nokandro.ACTION_STOP` |
   | パッケージ | `com.nokakoi.nokandro` |
   | ターゲット | **ブロードキャストレシーバー** |

4. その他の項目は空欄のままで保存。

## 注意事項

- サービスが既に起動中の場合、`ACTION_START` は無視されます。
- npub がアプリに保存されていない場合、`ACTION_START` は無視されます。
- Tasker の**プロファイル**と組み合わせることで、例えば「Bluetoothヘッドフォン接続時に自動起動、切断時に自動停止」といった自動化が可能です。
