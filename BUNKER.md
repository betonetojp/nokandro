# NIP-46 Bunker 機能 詳細ガイド

nokandro の **Bunker** 機能は、[NIP-46 (Nostr Connect)](https://github.com/nostr-protocol/nips/blob/master/46.md) に準拠したリモート署名サービスです。外部の Nostr クライアント（Webアプリ等）に秘密鍵を渡すことなく、イベントの署名や暗号化/復号を代行します。

2つの接続方式をサポートしています:
- **`bunker://`** — 署名者起点: nokandro が URI を生成し、クライアントに共有します。
- **`nostrconnect://`** — クライアント起点: クライアントが URI を生成し、nokandro で読み取ります。

---

## 目次

- [概要](#概要)
- [仕組み](#仕組み)
- [セットアップ手順 (bunker://)](#セットアップ手順-bunker)
- [nostrconnect:// での接続](#nostrconnect-での接続)
- [対応メソッド](#対応メソッド)
- [セキュリティモデル](#セキュリティモデル)
- [シークレットの管理](#シークレットの管理)
- [技術的な詳細](#技術的な詳細)
- [Webクライアントとの接続例](#webクライアントとの接続例)
- [トラブルシューティング](#トラブルシューティング)
- [制限事項](#制限事項)

---

## 概要

従来、Nostr クライアントで投稿やDMを行うには秘密鍵 (nsec) をクライアントに直接入力する必要がありました。NIP-46 Bunker を使うと、秘密鍵は nokandro 内に安全に保持したまま、外部クライアントからの署名リクエストに応答する形で動作します。

```
  bunker:// (署名者起点):
  nokandro (Bunker)          --- bunker:// URI --->          外部クライアント
    秘密鍵を保持          <-- kind:24133 (リレー経由) -->      (Webアプリ等)
                                                               秘密鍵なし

  nostrconnect:// (クライアント起点):
  外部クライアント        --- nostrconnect:// URI --->   nokandro
    (Webアプリ等)          <-- kind:24133 (リレー経由) -->   秘密鍵を保持
```

### メリット

- **秘密鍵が外部に漏れない**: クライアントに nsec を入力する必要がありません。
- **複数クライアント対応**: 同時に複数のクライアントからの接続を受け付けます。
- **暗号化対応**: NIP-04 / NIP-44 による暗号化・復号をリモートで実行できます。

---

## 仕組み

### 通信フロー

#### bunker:// （署名者起点）

1. nokandro が Bunker リレーに WebSocket 接続し、`kind:24133`（NIP-46 リクエスト）を購読します。
2. 外部クライアントが `bunker://` URI を使って接続リクエスト (`connect`) を送信します。
3. nokandro がシークレットを検証し、接続を承認します (`ack`)。
4. 以降、クライアントは `sign_event` や `nip04_decrypt` などのメソッドをリクエストでき、nokandro が秘密鍵を使って処理・応答します。

#### nostrconnect:// （クライアント起点）

1. クライアントアプリが `nostrconnect://` URI（クライアントの公開鍵 + リレー + シークレット）を生成します。
2. nokandro で URI を貼り付けるか、QR コードをスキャンします。
3. nokandro が指定されたリレーに接続し、`connect` レスポンスをクライアントに送信します。
4. 以降のフローは bunker:// と同様です。

### プロトコル概要

- **通信チャネル**: リレー経由の `kind:24133` イベント
- **リクエスト暗号化**: NIP-04 または NIP-44（クライアントの使用方式を自動検出）
- **認証**: `bunker://` URI に含まれるシークレットによる接続認証
- **方向**: クライアント → Bunker（リクエスト）、Bunker → クライアント（レスポンス）

---

## セットアップ手順 (bunker://)

### 1. nsec の入力

**Main タブ**で `nsec`（秘密鍵）を入力します。nsec を入力すると npub が自動的に導出されます。

> ⚠️ nsec が未入力の状態では Bunker を起動できません。

### 2. Bunker リレーの設定

**Bunker タブ**を開き、Bunker リレーの URL を設定します（デフォルト: `wss://ephemeral.snowflare.cc/`）。

- Bunker リレーは nokandro と外部クライアントの通信を中継するためのリレーです。
- メインのリレー（Main タブ）とは別のリレーを使用できます。
- リレーの編集は Bunker が停止している間のみ可能です。

### 3. Bunker の起動

**NIP-46 Bunker** スイッチを ON にします。

起動すると以下が表示されます：
- **Bunker URI**: `bunker://` で始まる URI
- **Status**: 接続状態（`Connected to relay` → `Subscribed (kind:24133)` の順に表示）

### 4. URI の共有

**Copy bunker URI** ボタンで URI をクリップボードにコピーし、接続先のクライアントに貼り付けます。

URI の形式:
```
bunker://<pubkey_hex>?relay=<relay_url>&secret=<secret>
```

### 5. クライアントからの接続

クライアント側で bunker URI を入力すると、`connect` リクエストが送信されます。nokandro がシークレットを検証し、成功すると Status に `Client connected: xxxx... (total: N)` と表示されます。

---

## nostrconnect:// での接続

`nostrconnect://` はクライアント側が URI を生成し、署名者（nokandro）がそれを読み取って接続する方式です。

### 1. nsec の入力

**Main タブ**で `nsec`（秘密鍵）を入力します（bunker:// と共通）。

### 2. クライアントが URI を生成

クライアントアプリ（Webアプリ等）が以下の形式の URI を生成します：

```
nostrconnect://<client_pubkey_hex>?relay=wss://...&secret=xxx
```

または、直接クエリパラメータ形式：

```
nostrconnect://<client_pubkey_hex>?name=AppName&relay=wss://...&secret=xxx
```

### 3. nokandro で接続

**Bunker タブ**で以下のいずれかの方法で接続します：

- **貼り付け**: URI 入力欄に `nostrconnect://` URI を貼り付け、**Connect** ボタンをタップ。
- **QR スキャン**: **Scan QR** ボタンをタップし、クライアントが表示する QR コードをスキャン。スキャン成功時は自動接続します。

### 4. セッション管理

接続済みのクライアントは **クライアントリスト** に表示されます：

- クライアント名をタップするとラベル（表示名）を編集できます。
- **✕** ボタンで切断・削除できます。
- セッションはアプリ再起動後も自動的に復元されます。

### フォールバックリレー

URI にリレーが指定されていない場合、以下の固定リレーが使用されます：

- `wss://nostr.oxtr.dev/`
- `wss://theforest.nostr1.com/`
- `wss://relay.primal.net/`
- `wss://ephemeral.snowflare.cc/`

---

## 対応メソッド

| メソッド | 説明 | パラメータ |
|---------|------|-----------|
| `connect` | クライアントの接続認証 | `[client_pubkey, secret?]` |
| `get_public_key` | Bunker の公開鍵を取得 | なし |
| `sign_event` | イベントに署名 | `[unsigned_event_json]` |
| `nip04_encrypt` | NIP-04 暗号化 | `[third_party_pubkey, plaintext]` |
| `nip04_decrypt` | NIP-04 復号 | `[third_party_pubkey, ciphertext]` |
| `nip44_encrypt` | NIP-44 暗号化 | `[third_party_pubkey, plaintext]` |
| `nip44_decrypt` | NIP-44 復号 | `[third_party_pubkey, ciphertext]` |
| `ping` | 接続確認 | なし → `pong` |

### sign_event の動作

1. クライアントから未署名のイベント JSON を受信
2. nokandro が `id`（SHA-256 ハッシュ）を計算
3. 秘密鍵で BIP-340 Schnorr 署名を生成
4. 署名済みイベント JSON を返却

### 暗号化メソッドの動作

- `nip04_*`: AES-256-CBC ベースの暗号化/復号（`?iv=` 形式）
- `nip44_*`: XChaCha20 + HKDF ベースの暗号化/復号
- リクエスト自体の暗号化方式はクライアントが送信した形式から自動判定します（`?iv=` の有無）

---

## セキュリティモデル

### シークレット認証

- **bunker://**: Bunker URI にはランダム生成されたシークレット（128ビット / 32桁hex）が含まれます。クライアントは `connect` リクエスト時にこのシークレットを提示する必要があります。
- **nostrconnect://**: クライアントが生成した URI にシークレットが含まれます。nokandro が `connect` レスポンスを返す際にシークレットを提示します。
- いずれの方式でも、シークレットが一致しない場合、接続は拒否されます。

### 接続クライアント管理

- 認証済みクライアントの公開鍵は `HashSet` で管理されます。
- `connect` / `ping` 以外のメソッドは、認証済みクライアントからのリクエストのみ受け付けます。
- 未認証クライアントからのリクエストは無視されます。

### 秘密鍵の保護

- 秘密鍵は nokandro アプリ内（SharedPreferences）にのみ保存されます。
- 外部に送信されることはありません。
- 署名は常に nokandro 内で行われ、署名結果のみが返却されます。

---

## シークレットの管理

### bunker:// のシークレット

#### 自動生成と永続化

- 初回起動時、ランダムなシークレットが生成されます。
- シークレットは SharedPreferences に保存され、Bunker の再起動時にも同じ URI が維持されます。
- これにより、クライアント側の bunker URI を毎回再入力する必要がありません。

#### シークレットのリセット

**Reset Secret** ボタンを押すと：

1. 確認ダイアログが表示されます（削除されるシークレットのプレビュー付き）。
2. 「Delete」を選択すると、保存されたシークレットが削除されます。
3. **Bunker が自動的に停止**します。
4. 次回起動時に新しいシークレットで新しい URI が生成されます。

> ⚠️ リセット後は、以前の URI で接続していたクライアントからの接続ができなくなります。新しい URI を再共有してください。

### nostrconnect:// のシークレット

- nostrconnect:// URI に含まれるシークレットはクライアントが生成します。
- nokandro は接続時にシークレットを `connect` レスポンスに含めてクライアントに返します。
- シークレットはセッション情報の一部として SharedPreferences に永続化されます。

---

## 技術的な詳細

### アーキテクチャ

```
MainActivity (Bunker タブ)
    │
    ├── BunkerService (Android フォアグラウンドサービス)
    │       │
    │       ├── NostrBunker (bunker:// プロトコル処理)
    │       │       │
    │       │       ├── WebSocket → Bunker リレー
    │       │       ├── NostrCrypto.Decrypt / Encrypt (NIP-04/44)
    │       │       ├── NostrCrypto.Sign (BIP-340 Schnorr)
    │       │       └── NostrCrypto.GetPublicKey (secp256k1)
    │       │
    │       └── NostrConnectSession (nostrconnect:// セッション管理)
    │               │
    │               ├── WebSocket → クライアント指定リレー (複数可)
    │               ├── NostrConnectUri (URI パース: pubkey, relay, secret, metadata)
    │               ├── セッション永続化 (SharedPreferences)
    │               └── NostrCrypto (共通の暗号化・署名処理)
    │
    └── LocalBroadcast (UI 更新)
            ├── ACTION_BUNKER_STARTED  → URI 表示
            ├── ACTION_BUNKER_LOG      → ステータス更新
            ├── ACTION_BUNKER_STOPPED  → UI リセット
            └── ACTION_NC_SESSION_*    → nostrconnect セッション更新
```

### フォアグラウンドサービス

- `BunkerService` は Android のフォアグラウンドサービスとして動作します。
- サービスタイプ: `remoteMessaging`（Android 14+ 対応）
- 通知領域に常駐し、「Stop」アクションで停止可能です。
- bunker:// と nostrconnect:// の両方のセッションを同一サービス内で管理します。
- メインの TTS サービス (`NostrService`) とは独立して動作します。

### 再接続

- リレーとの接続が切断された場合、5秒後に自動再接続を試みます。
- bunker:// は再接続時も同じシークレット・URI が維持されます。
- nostrconnect:// セッションは SharedPreferences に永続化されており、アプリ再起動時に自動復元・再接続されます。

### 暗号化の自動検出

リクエストの暗号化方式（NIP-04 / NIP-44）は受信データの形式から自動判定します：

- `?iv=` を含む → NIP-04（AES-256-CBC）
- `?iv=` を含まない → NIP-44（XChaCha20 + HKDF）

レスポンスは同じ方式で暗号化して返します。

### JSON 処理

.NET の `TrimMode=link` によるリフレクション削除に対応するため、`System.Text.Json.JsonSerializer` ではなく手動 JSON 構築 (`EscapeJsonString`) を使用しています。

---

## Webクライアントとの接続例

### bunker:// での接続

1. **nokandro 側**
   - Main タブで nsec を入力
   - Bunker タブで NIP-46 Bunker スイッチを ON
   - 表示された `bunker://...` URI をコピー

2. **Web クライアント側**
   - ログイン方法として「NIP-46 / Remote Signer / Bunker」を選択
   - コピーした URI を貼り付け
   - 接続を開始

3. **接続後**
   - nokandro の Status に `Client connected` が表示される
   - Web クライアントから投稿やDM送信を行うと、nokandro が署名を行い結果を返す
   - nokandro の Status に `sign_event → OK` 等のログが表示される

### nostrconnect:// での接続

1. **Web クライアント側**
   - ログイン方法として「NIP-46 / Remote Signer」等を選択
   - クライアントが `nostrconnect://...` URI または QR コードを表示

2. **nokandro 側**
   - Main タブで nsec を入力
   - Bunker タブで `nostrconnect://` URI を貼り付けて **Connect** をタップ、または **Scan QR** で QR コードをスキャン

3. **接続後**
   - nokandro のクライアントリストにクライアント名が表示される
   - Web クライアントから投稿やDM送信を行うと、nokandro が署名を行い結果を返す
   - クライアント名をタップしてラベルを編集可能

---

## トラブルシューティング

### Bunker が起動しない

- **Main タブで nsec が入力されているか確認してください。**
  - nsec が未入力だと "nsec required for bunker" のトーストが表示されます。
  - nsec の形式が不正な場合は "Invalid nsec" と表示されます。

### クライアントが接続できない (bunker://)

- **シークレットの一致を確認**
  - URI を正確にコピー&ペーストしてください（末尾の空白や改行に注意）。
  - Status に `Secret mismatch` と表示される場合、URI が古い可能性があります。Reset Secret → 再起動で新しい URI を生成してください。

- **リレーの到達性を確認**
  - Bunker リレーがクライアント側からもアクセス可能か確認してください。
  - Status が `Connected to relay` にならない場合、リレー URL に誤りがあるか、リレーがダウンしている可能性があります。

- **暗号化方式の不一致**
  - Status に `Failed to decrypt NIP-46 request` と表示される場合、クライアントの暗号化ライブラリに問題がある可能性があります。

### クライアントが接続できない (nostrconnect://)

- **URI の形式を確認**
  - `nostrconnect://` で始まる URI であることを確認してください。`bunker://` とは異なります。
  - クライアント公開鍵（64文字 hex）が正しいことを確認してください。

- **リレーの到達性を確認**
  - URI に含まれるリレーが双方からアクセス可能か確認してください。
  - URI にリレーが含まれていない場合はフォールバックリレーが使用されます。

- **QR スキャンが失敗する**
  - カメラ権限が許可されているか確認してください。
  - QR コードが `nostrconnect://` URI をエンコードしていることを確認してください。

### Status に "Relay rejected" と表示される

- リレーが nokandro からのイベント送信を拒否しています。
- 別のリレーを試してください。

### 「Restricted Settings」で通知が出せない

- Android 13 以降で GitHub から APK をインストールした場合、通知権限の「制限付き設定」が有効になることがあります。
- README.md の「Android 13以降をお使いの方へ」の手順に従って制限を解除してください。

---

## 制限事項

- **単一秘密鍵**: 1つの nsec のみサポートします。複数アカウントの同時使用はできません。
- **自動承認**: `connect` でシークレットが一致すれば自動的に承認されます。リクエストごとの確認ダイアログはありません。
- **bunker:// クライアントリスト**: bunker:// を再起動すると接続済みクライアントリストはクリアされます。クライアント側で再接続が必要です（多くのクライアントは自動再接続を行います）。
- **nostrconnect:// セッション永続化**: nostrconnect:// セッションは SharedPreferences に保存され、アプリ再起動後も自動復元されます。ただし、クライアントリストからの手動削除は即座に反映されます。
- **メソッド制限**: `create_account` などの一部 NIP-46 メソッドは未実装です。
- **ネットワーク依存**: リレーが利用できない場合、クライアントとの通信はできません。
