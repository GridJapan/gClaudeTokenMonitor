# gjClaudeTokenMonitor (ctm)

Claude Code のトークン消費量をリアルタイムで計測するターミナルモニター。
Go 標準ライブラリのみ・外部依存ゼロ・単一 exe。

`~/.claude/projects/**/*.jsonl`（Claude Code のセッションログ）を差分読み（tail）し、
assistant メッセージの `usage` を集計して、トークン数とコストを 1 秒ごとに更新表示する。

```
 Claude Token Monitor                                    2026-08-18 02:33:27
 [LIVE] 2:DAILY 3:MODELS 4:PROJECTS 5:SESSIONS 6:BLOCKS
 -----------------------------------------------------------------------------

 TOTAL   5.47G tokens    $3937    10,981 msgs
         in 24.1K | cache-w 150.16M | cache-r 5.31G | out 3.33M

 CURRENT BLOCK (5h)  22:00 -> 03:00  ###################......  91%
   tokens 52.83M   cost $57.31   burn 697,805 tok/min   26m left
   projected at block end: 71.35M tokens / $88.85

 LAST 60 MIN  ___▂▃▁_______▃▁________▂▂█  peak 2.62M/min
```

## ビルドと実行

```powershell
.\build.ps1
```

`bin\ctm.exe`（CLI・常駐レコーダー）と `bin\CtmMonitor.exe`（Windows トレイ / タスクバー UI）ができる。

- **ctm.exe**: Go 1.22 以上。標準ライブラリのみ・外部依存ゼロ。クロスビルド可（`GOOS=linux go build -o bin/ctm .`）
- **CtmMonitor.exe**: Windows 標準の `csc.exe`（.NET Framework 4.x）でビルド。ランタイムの追加インストール不要

```powershell
.\bin\ctm.exe              # TUI
.\bin\CtmMonitor.exe       # タスクバー常駐 UI
```

## 構成

| ディレクトリ | 内容 |
|---|---|
| ルート `*.go` | ctm 本体（CLI・集計・常駐記録・使用率取得） |
| `app/` | Windows UI（C#）と起動ラッパー |
| `tools/` | 補助スクリプト（全文トランスクリプト抽出・レポート生成） |
| `docs/` | システム構成・用語統一表 |

## 表示

| キー | ビュー | 内容 |
|---|---|---|
| `1` | LIVE | 総計 / 現在の 5h ブロック / 直近 60 分のスパークライン / 今日 / モデル比率 / 直近メッセージ |
| `2` | DAILY | 日別（input / cache-write / cache-read / output / 合計 / コスト） |
| `3` | MODELS | モデル別 |
| `4` | PROJECTS | プロジェクト（cwd）別 |
| `5` | SESSIONS | セッション別。`*` は直近 30 分に動きがあるもの |
| `6` | BLOCKS | 5 時間ブロック（レートリミット窓）の履歴 |

`r` で再スキャン、`q` / `Esc` / `Ctrl+C` で終了、`Tab` でビュー送り。

## コマンド

```
ctm [live] [flags]        リアルタイム表示（既定）
ctm show   [flags]        集計を 1 回出力（-json でスクリプト向け）
ctm events [flags]        1 メッセージ 1 行の NDJSON 明細
ctm record [flags]        全セッションを常時アーカイブ（常駐）
ctm status                常駐レコーダーの状態
ctm stop                  常駐レコーダーを停止
ctm query  -from t        アーカイブから期間を切り出して集計
ctm limits [show|history] プラン使用制限の使用率と実測消費
ctm version / ctm help
```

### 共通フラグ（live / show / events）

```
-dir string        Claude Code の projects ディレクトリ (既定: ~/.claude/projects)
-pricing string    料金表を上書きする JSON ファイル
-days int          直近 N 日だけ集計 (0 = 全期間)
-session string    1 セッションに絞る: ID 接頭辞 / "new" / "last"
-since string      基準時刻 ("15:04" / "2006-01-02 15:04:05" / RFC3339)
-since-now         起動時点以降だけ集計
```

`live` は `-interval` `-view`、`record` は `-out` `-interval` `-quiet`、
`query` は `-from` `-to` `-session` `-exclude` `-save` `-archive` を追加で取る。

旧フラグ（`-json` `-events` `-record` `-record-status` `-record-stop` `-version`）は
そのまま新コマンドへ読み替えるので、既存のショートカットは壊れない。

例:

```powershell
.\bin\ctm.exe                                  # TUI
.\bin\ctm.exe show -json | ConvertFrom-Json    # スクリプトから
.\bin\ctm.exe query -from "13:00"              # 今日 13 時以降
.\bin\ctm.exe query -from "01:38" -session 1a2b3c4d
```

## 別セッションの消費量を計測する

Claude Code はセッションごとに `<sessionId>.jsonl` を 1 本作る。ファイル名がそのまま
sessionId なので、「モニタ起動時に存在しなかったログ」＝「あとから始めたセッション」として
識別できる。これを使って別セッションだけを切り出す。

**1. これから始めるセッションを測る（推奨）**

先にモニタを `-session new` で起動し、そのあとで別ウィンドウの Claude Code を開く。
新しいログファイルが現れた瞬間にモニタがそれを掴み、以降 TOTAL / 5h ブロック / バーンレートは
すべてそのセッション**だけ**の数字になる。

```powershell
.\bin\ctm.exe -session new
```

掴む前のヘッダは `[waiting for a new session]`、掴んだあとは `[new session xxxxxxxx]`。

**2. 終わったセッションを測る**

```powershell
.\bin\ctm.exe -session last -once
```

最終更新が最も新しいログ＝さっきまで動いていたセッション。

**3. sessionId を直接指定する**

`5` の SESSIONS ビューに sessionId とプロジェクト名が並ぶので、接頭辞をコピーする。
`-json` の `by_session`（最終更新の新しい順）からスクリプトで拾ってもよい。

```powershell
.\bin\ctm.exe -session 0dd3442d -json | jq .total
```

**4. すでに開いているセッションを「いまから」測る**

`-session` と `-since-now` は組み合わせられる。指定したセッションの、モニタ起動以降の分
だけを数える。

```powershell
$sid = (.\bin\ctm.exe -json | ConvertFrom-Json).by_session |
       Where-Object { $_.note -eq 'my-project' } |
       Select-Object -First 1 -ExpandProperty key
.\bin\ctm.exe -session $sid -since-now
```

**5. セッションを絞らず「今から先」だけ測る**

```powershell
.\bin\ctm.exe -since-now
```

「これから 1 時間の作業でいくら使うか」を測るときはこちら。

> **順番に注意**: `-session new` は起動時のログ一覧を基準にする。モニタを立ち上げる**前**に
> 相手のセッションを開くと「新しいログ」として検出できない。
> `-session last` は最終更新が最新のログを選ぶので、Claude Code の中から実行すると
> 自分自身のセッションを掴むことがある。別ターミナルから実行すること。

## 常時記録（-record）

「測ろうと思ってから準備する」のをやめ、**常に全セッションを記録し続ける**モード。
計測対象に手を入れないので観測者効果が起きない。

```powershell
.\bin\ctm.exe -record
```

`~/.claude/projects/` 配下を丸ごと対象にするため、Claude Code を何個立ち上げても、
途中で新しいセッションを開いても、設定なしで記録される。

### 保存先

```
~/.ctm/
├── events/2026-08-22.ndjson   1 メッセージ 1 行（機械可読・日付ローテート）
├── daily/2026-08-22.md        同じ内容を人が読める表で
├── state.json                 各ログの読み取り位置
├── record.lock                稼働中プロセスのハートビート
└── record.log                 レコーダー自身のログ
```

### 壊れにくさ

- **再起動に強い**: `state.json` にログごとのバイトオフセットを保存する。PC を再起動しても続きから再開し、取りこぼしも二重記録もない
- **二重起動を拒否**: `record.lock` のハートビートを見て、別のレコーダーが同じディレクトリに書いていれば起動しない。前のプロセスが死んでいれば（3 間隔ぶん古ければ）引き継ぐ
- **クラッシュ耐性**: 書き込みは tick 単位でフラッシュし、成功後にオフセットを進める。最悪でも 1 間隔ぶんを再読するだけで、失われない
- **依存ゼロ**: 単一 exe。Python もランタイムも要らない

### Windows で常駐させる

`bin\ctm-record.vbs` をスタートアップフォルダに置くと、ログオン時にコンソールウィンドウなしで起動する。

```powershell
copy bin\ctm-record.vbs "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\"
```

やめるときはそのファイルを消す。

### 制御

常駐レコーダーはウィンドウを持たないので、CLI から状態を見て止める。

```powershell
.\bin\ctm.exe -record-status    # 稼働状態・pid・本日の記録量
.\bin\ctm.exe -record-stop      # 次の間隔で正常終了させる
```

```
レコーダー: 稼働中
  pid          16536
  記録先       C:\Users\<user>\.ctm
  最終更新     2026-08-22 14:53:14 (0m前)
  追跡中のログ 68 本
  本日の記録   384 メッセージ / 123.31M トークン / $103.74 / 3 セッション
```

`-record-stop` は `record.stop` を置くだけで、レコーダーが次の tick で気づいてフラッシュ・ロック解放をしてから終了する。kill と違って書きかけを失わない。
3 分以上ハートビートが止まっていれば `-record-status` が「応答なし」と出す。

### 記録から切り出す

記録済みなので、計測は**あとから期間を指定して切り出すだけ**。

```powershell
.\bin\ctm.exe query -from "01:38" -session 1a2b3c4d
```

メッセージ数・種別内訳・コスト・**経過と実稼働の区別**・稼働クラスタ・キャッシュ失効による再構築コストを出す。

## プラン使用制限（ctm limits）

Claude Code が画面に出す「5時間制限 / 週間・全モデル / 週次・Fable」の**公式な使用率**を取得し、
同じ窓で ctm が実測した消費と並べて記録する。

```powershell
.\bin\ctm.exe limits
```

```
プラン使用制限  max / default_claude_max_20x

  5時間制限
   使用率    0.0% ............................
   リセット  4h55m後 (08-22 20:09)
   実測消費  7 メッセージ / 3.28M トークン / $2.074

* 週間・全モデル
   使用率    8.0% ##..........................
   リセット  106h45m後 (08-27 01:59)
   実測消費  1,036 メッセージ / 275.44M トークン / $231.58
   窓の容量  3.44G トークン / $2895 相当（この使用率からの逆算）
   残り      $2663 相当
```

### 取得方法

使用率はローカルのどこにも保存されていない。Claude Code は描画のたびに Anthropic へ問い合わせている。
ctm は同じエンドポイントを、Claude Code が保存済みの OAuth トークンで叩く。

```
GET https://api.anthropic.com/api/oauth/usage
  Authorization: Bearer <accessToken>      ~/.claude/.credentials.json の claudeAiOauth
  anthropic-beta: oauth-2025-04-20         必須
```

トークンに `user:profile` スコープが必要（`user:inference` だけでは 403）。
方法の出典は [steipete/CodexBar](https://github.com/steipete/CodexBar) の `docs/claude.md`。

### 記録

常駐レコーダーが既定で 5 分ごとに取得し、逐次追記する。`-usage-interval 0` で無効化、
`-usage-interval 10m` などで変更できる。エンドポイントは Anthropic 側が絞るので、
失敗すると指数的にバックオフする。

```
~/.ctm/limits/
├── 2026-08-22.ndjson   1 サンプル 1 行（機械可読）
└── 2026-08-22.md       同じ内容を人が読める表で
```

1 行には**使用率と実測消費が同居する**ので、「何%のときに、いくら分を消費していたか」が残る。

```json
{"ts":"2026-08-22T15:15:06+09:00","key":"weekly-fable","label":"週次・Fable",
 "percent":2,"resets_at":"2026-08-26T16:59:59Z","window_start":"2026-08-20T01:59:59+09:00",
 "messages":1,"tokens":489290,"cost_usd":9.82,
 "implied_limit_tokens":24464500,"implied_limit_cost_usd":491.22}
```

`implied_limit_*` は「その使用率から逆算した窓の容量」。ポーリングの合間の残量推定に使える。

```powershell
.\bin\ctm.exe limits history            # 今日の記録
.\bin\ctm.exe limits history -day "2026-08-21" -w weekly_all
```

## 計測ロジック

- **重複排除**: Claude Code のログは同一メッセージを `requestId` 単位で複数行書くため、
  `message.id + requestId` をキーに重複を排除する（これをやらないと 2 倍前後の過大計上になる）。
- **差分読み**: ファイルごとにバイトオフセットを保持し、追記分だけを読む。
  行末が改行で終わっていない書きかけの行は次回に持ち越す。
- **キャッシュ内訳**: `usage.cache_creation.ephemeral_5m_input_tokens` /
  `ephemeral_1h_input_tokens` を分けて課金レートを変える（1h は 2x、5m は 1.25x）。
- **5h ブロック**: 最初のメッセージの「時」を起点に 5 時間の窓を作り、
  窓を超えるか 5 時間以上の空白があれば新しいブロックを開始する。
- **バーンレート**: 直近 5 分のトークン数 / コストから 1 分あたりの速度を出し、
  ブロック終了時点の着地見込みを線形外挿する。
- `model` が `<synthetic>` の行、usage が全ゼロの行は集計から除外する。
- **JSON 出力**: 非 ASCII を `\uXXXX` にエスケープして出す。日本語のプロジェクト名が
  レガシーコードページの Windows コンソールを通ると壊れ、`ConvertFrom-Json` が失敗する
  ため。純 ASCII ならコードページに関係なく同じ文字列へ復元される。

## 料金表

USD / 100 万トークン（Anthropic のリスト価格）。キャッシュはこの input レートに対して
read = 0.1x、write(5m) = 1.25x、write(1h) = 2.0x を掛ける。

| モデル | input | output |
|---|---|---|
| claude-fable-5 / claude-mythos-5 | $10 | $50 |
| claude-opus-5 | $5 | $25 |
| claude-opus-5 (fast mode) | $10 | $50 |
| claude-opus-4-8 / 4-7 / 4-6 / 4-5 | $5 | $25 |
| claude-opus-4-1 / 4-0 / claude-3-opus | $15 | $75 |
| claude-sonnet-5 / 4-6 / 4-5 / 4-0 | $3 | $15 |
| claude-haiku-4-5 | $1 | $5 |
| claude-3-5-haiku | $0.80 | $4 |
| claude-3-haiku | $0.25 | $1.25 |

モデル ID は `anthropic.` / `us.anthropic.` 接頭辞、`[1m]` 接尾辞、`@`/日付スナップショット
（`claude-haiku-4-5-20251001`）を正規化してから照合する。未知のモデルはコスト 0 として集計し、
フッターに警告を出す。

料金を差し替える場合:

```json
{ "claude-opus-5": { "input": 5, "output": 25 } }
```

```powershell
.\bin\ctm.exe -pricing .\pricing.local.json
```

**注意**: 表示されるコストは公開リスト価格ベースの推定値。定額プラン（Pro / Max）では
実際の請求は発生せず、「もし API 従量課金だったらいくら分か」の目安として読むこと。

## 構成

| ファイル | 役割 |
|---|---|
| `main.go` | CLI・イベントループ・JSON 出力 |
| `scanner.go` | jsonl の差分読み（tail）とプロジェクト名の解決 |
| `parser.go` | 1 行を Entry に変換・重複キー生成 |
| `pricing.go` | 料金表とコスト計算 |
| `store.go` | 重複排除・集計・5h ブロック・スナップショット |
| `render.go` | ANSI TUI 描画 |
| `console_windows.go` | Windows コンソール（VT 有効化・1 キー入力・端末サイズ） |
| `console_unix.go` | 非 Windows のフォールバック |
| `query.go` | アーカイブからの期間切り出し・稼働クラスタ・中断コスト検出 |
| `usage.go` | プラン使用率の取得（OAuth API）とサンプル生成 |
| `limits.go` | 使用率の記録・表示・履歴 |
| `record.go` | 常時アーカイブ（日付ローテート・再起動復帰・二重起動防止） |
| `session_test.go` | セッションフィルタのテスト |

## ライセンス

MIT
