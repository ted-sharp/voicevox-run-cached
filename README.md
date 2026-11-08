# VoicevoxRunCached

VOICEVOX REST API のラッパー。テキストを文単位で分割し、SHA256 キーで Ogg Opus キャッシュしながら逐次合成・再生するコマンドラインツール。

- **ワンコマンド**: テキストを渡すだけで API 呼び出し → Opus 化 → 再生まで完結
- **セグメント単位キャッシュ**: 文の一部だけ変えても変更されていない部分はキャッシュから即再生
- **ストリーミング再生**: キャッシュ済み部分を再生しつつ未キャッシュ部分をバックグラウンド生成、待機中はフィラー音声で繋ぐ
- **エンジン自動管理**: VOICEVOX / AIVIS Speech のプロセス起動・常駐・停止を自動化

## 目次

- [必要環境](#必要環境)
- [インストール](#インストール)
- [使い方](#使い方)
- [コマンドリファレンス](#コマンドリファレンス)
- [設定 (`appsettings.json`)](#設定-appsettingsjson)
- [仕組み](#仕組み)
- [パフォーマンス計測](#パフォーマンス計測)
- [トラブルシューティング](#トラブルシューティング)
- [Claude Code Hooks との連携](#claude-code-hooks-との連携)
- [開発](#開発)
- [ライセンス](#ライセンス)

## 必要環境

- **OS**: Windows x64
- **VOICEVOX エンジン** (デフォルト `http://127.0.0.1:50021`) — [VOICEVOX 公式](https://voicevox.hiroshiba.jp/) からダウンロード。AIVIS Speech にも対応 (設定で切替)
- 既定で `AutoStartEngine: true` のためエンジンは自動起動される。外部で起動済みのエンジンにも接続可能

## インストール

### リリース版

1. [Releases](https://github.com/ted-sharp/voicevox-run-cached/releases) から `VoicevoxRunCached-v[x.x.x]-win-x64.zip` をダウンロード
2. 任意のフォルダ (例: `C:\Tools\VoicevoxRunCached\`) に展開
3. 必要に応じて `appsettings.json` を編集
4. (任意) 環境変数 `PATH` に追加するとどこからでも `VoicevoxRunCached.exe` で実行可能

### ソースからビルド

```bash
git clone https://github.com/ted-sharp/voicevox-run-cached.git
cd voicevox-run-cached
task publish        # 開発用 (src_dotnet/publish/VoicevoxRunCached/)
task publish:zip    # 配布用 (バージョン付き ZIP)
```

[Task](https://taskfile.dev) が無い場合は `src_dotnet/_publish.cmd` / `_publish_zip.cmd` でも同じ出力になる。

## 使い方

```bash
# 基本
VoicevoxRunCached.exe "こんにちは、世界！"

# パラメータ指定
VoicevoxRunCached.exe "テストです。" --speaker 2 --speed 1.2 --pitch 0.1

# ファイル保存 (再生なし)
VoicevoxRunCached.exe "保存します。" --out out.opus --no-play

# キャッシュのみ (API を呼ばない。エンジンが止まっていても再生できる)
VoicevoxRunCached.exe "既に生成済みのテキスト" --cache-only

# キャッシュを作り直す (辞書登録で読みを直した・エンジンを更新したとき)
VoicevoxRunCached.exe "読みを直したテキスト" --refresh

# 詳細タイミング表示
VoicevoxRunCached.exe "メッセージ" --verbose
```

## コマンドリファレンス

```
VoicevoxRunCached <text> [options]
VoicevoxRunCached -- <text> [options]   # <text> がコマンド名 (speakers など) と同じでも本文として読み上げる
VoicevoxRunCached --test [options]      # appsettings.json の Test.Message で実行
VoicevoxRunCached speakers              # スピーカー一覧
VoicevoxRunCached devices [--full] [--json]  # オーディオデバイス一覧
VoicevoxRunCached --init [-s <id>]      # フィラー音声キャッシュを初期化 (話者ごと。省略時は DefaultSpeaker)
VoicevoxRunCached --clear               # キャッシュ全削除
```

| Option | 説明 | デフォルト |
|---|---|---|
| `--speaker, -s <id>` | スピーカー ID (0 以上) | `VoiceVox.DefaultSpeaker` (1) |
| `--speed <value>` | 速度。0 より大きい値 (目安 0.5–2.0) | 1.0 |
| `--pitch <value>` | ピッチ (目安 -0.15–0.15) | 0.0 |
| `--volume <value>` | 音量。0 以上 (目安 0.0–2.0) | 1.0 |
| `--no-cache` | キャッシュを使わない (読みも書きもしない) | false |
| `--cache-only` | キャッシュのみ使う (API を呼ばず、エンジンにも接続しない。キャッシュに無い文があれば終了コード 4) | false |
| `--refresh` | キャッシュを読まずに全部合成し直し、キャッシュを上書きする (`--no-cache` / `--cache-only` とは併用不可) | false |
| `--out, -o <path>` | ファイル保存 (`.wav` / `.opus` を拡張子で判定。それ以外はエラー) | — |
| `--no-play` | 再生しない (`--out` と併用推奨) | false |
| `--verbose` | 再生時間と総実行時間を表示 | false |
| `--max-wait <seconds>` | 先に動いている読み上げなどを待つ上限 (秒、0 以上。0 なら待たない)。超えたら読み上げずに終了コード 9 (`--test` でも使える) | `Lock.MaxWaitSeconds` (未設定なら上限なし) |
| `--log-level <level>` | `trace`/`debug`/`info`/`warn`/`error`/`crit`/`none` (`none` はログを一切出さない) | info |
| `--log-format <fmt>` | `simple` / `json` (`json` は 1 イベント 1 行の JSON。ファイル出力も同じ書式) | simple |
| `--help, -h` | ヘルプ表示 (引数のどこにあっても表示し、読み上げ・コマンドは実行しない) | — |

`--speed` / `--pitch` / `--volume` は小数第 2 位に丸めて使われる (キャッシュキーも丸めた値で作る)。「目安」は VOICEVOX エディタの調整範囲で、本ツールが検証するのは数値であること・speed が 0 より大きいこと・volume と speaker が 0 以上であることだけ。不正な値はエラーで終了する。

不明なオプション (`--speeed` などの打ち間違い) と余分な引数もエラーで終了する (終了コード 1。既定値のまま読み上げない)。本文に空白を含めるときは全体を引用符で囲む。本文が `speakers` / `devices` / `--init` のようにコマンドやオプションと同じ文字列のときは、`VoicevoxRunCached -- speakers` のように前に `--` を置く。本文の位置にオプションの形をした引数 (`--refresh` のように `--` の直後が英字で空白を含まないもの、`-s` / `-o`) があるときは本文の書き忘れとしてエラーにするので、その形の文を読み上げるときも前に `--` を置く (`--- 完了 ---` のような文はそのまま読む)。

### 終了コード

| コード | 意味 |
|---|---|
| 0 | 成功 |
| 1 | 一般エラー (不正な引数・想定外の例外など) |
| 3 | エンジンエラー (エンジンの起動失敗・エンジンに接続できない・エンジンが 500/503 を返した) |
| 4 | キャッシュエラー (`--clear` で削除できなかった等) |
| 5 | 音声エラー (全セグメント (`--init` では全フィラー) の合成失敗・音声形式の不正・`--out` の保存失敗) |
| 6 | API エラー (タイムアウト・不正なリクエスト/レスポンス・レート制限など) |
| 7 | 設定エラー (`appsettings.json` が無い・JSON の書式が不正・値の型や範囲が不正) |
| 8 | 一部のセグメントだけ合成に失敗 (再生・保存は成功した分だけ行われる。`--init` では一部のフィラーだけ生成に失敗) |
| 9 | 先に動いている読み上げなどが待つ上限 (`--max-wait` / `Lock.MaxWaitSeconds`) までに終わらなかったので、実行しなかった |
| 130 | Ctrl+C でキャンセル (後片付けが終わらないときにもう一度 Ctrl+C を押すと強制終了する。その場合の終了コードは OS 既定の値) |

2 は使わない (Claude Code の Hooks では終了コード 2 がブロッキングエラーとして扱われるため。[Claude Code Hooks との連携](#claude-code-hooks-との連携) を参照)。

### 出力先と文字コード

- **stdout**: コマンドの結果と進捗 (`speakers` / `devices` の一覧、`Processing segments...` など)。`devices --json` の stdout は JSON だけになる
- **stderr**: ログ、エラー・警告とその補足 (解決策など)、起動待ちのスピナー
- 色 (ANSI エスケープ) は、出力先がコンソールのときだけ付く
- リダイレクト・パイプ時は BOM なし UTF-8 で出力する (コンソールに直接出すときは従来どおりコンソールのコードページ)。Windows PowerShell 5.1 で `$x = VoicevoxRunCached.exe speakers` のように取り込むと、既定ではコンソールのコードページで解釈されて文字化けする。その場合は `[Console]::OutputEncoding = [Text.Encoding]::UTF8` を先に実行する

## 設定 (`appsettings.json`)

各セクションは `Configuration/AppSettings.cs` の型にバインドされ、FluentValidation で検証される。下記コメントが各設定の役割を兼ねている。

```jsonc
{
  "VoiceVox": {
    "EngineType": "VOICEVOX",     // VOICEVOX | AivisSpeech
    "BaseUrl": "http://127.0.0.1:50021",  // localhost より 127.0.0.1 推奨 (DNS 解決遅延回避)
    "EngineArguments": "--host 127.0.0.1 --port 50021",
    "DefaultSpeaker": 1,
    "EnginePath": "",             // 空なら一般的なインストール場所から自動検出
    "ConnectionTimeout": 30,      // 秒。HTTP リクエスト 1 回の上限 (タイムアウトはリトライしない)。話者の初期化 (モデルの読み込み) は完了まで待つ
    "AutoStartEngine": true,
    "StartupTimeoutSeconds": 30,
    "KeepEngineRunning": true     // false ならアプリ終了時にエンジンも停止
  },
  "Cache": {
    "Directory": "./cache/audio/",
    "UseExecutableBaseDirectory": true,  // 相対パスを exe 基準で解決
    "ExpirationDays": 30,         // 最後に使ってからこの日数を過ぎたキャッシュを削除 (使うたびに延長)
    "MaxSizeGB": 1.0,             // 上限超過時は最後に使ったのが古い順に削除
    "OpusBitrateKbps": 32         // Opus のビットレート 12-128。キャッシュ・フィラー・--out .opus 共通 (キーに含まれないので既存キャッシュは流用)
  },
  "Audio": {
    "OutputDevice": -1,           // -1 = OS デフォルト, 0+ = WaveOut index
    "Volume": 1.0,                // 再生音量 0.0-1.0 (WaveOut の上限が 1.0)
    "PrepareDevice": false,       // true で再生前に小音量の音を流してデバイスを温める (読み上げで再生するときだけ)
    "PreparationDurationMs": 200,
    "PreparationVolume": 0.01,
    "DesiredLatency": 100,        // WaveOut の希望レイテンシ (ms。全バッファの合計)
    "NumberOfBuffers": 3          // WaveOut のバッファ数
  },
  "Filler": {
    "Enabled": false,             // true で生成待機中に相槌音声を再生して無音を回避 (初回は --init で事前生成)
    "Directory": "./cache/filler/",  // Cache.Directory と同じにはできない (設定エラー)
    "UseExecutableBaseDirectory": true,
    "FillerTexts": ["えーっと、", "あのー、", "あのう、", "ええと、", "ええっと、", "えとえと、"]
  },
  "Lock": {
    // "MaxWaitSeconds": 10       // 先の読み上げなどを待つ上限 (秒)。既定は未設定 = 上限なし、0 なら待たない。超えたら終了コード 9。--max-wait で上書き
  },
  "Logging": {
    "Level": "info",              // trace|debug|info|warn|error|crit|none
    "Format": "simple",           // simple | json (1 イベント 1 行の JSON。コンソール・ファイル共通)
    "EnableFileLogging": false,   // true でファイル出力を有効化
    "MaxFileSizeMb": 10,          // ログファイル単体の最大サイズ (MB)
    "MaxFileCount": 5             // ローテーションで保持するファイル数
  },
  "Test": { "Message": "..." }    // --test で読み上げる文
}
```

**AIVIS Speech に切替えるには**: `EngineType: "AivisSpeech"`, `BaseUrl: "http://127.0.0.1:10101"`, `EngineArguments` のポート、`DefaultSpeaker: 888753760` を変更。

## 仕組み

### セグメント単位キャッシュ

入力テキストは文末記号 (`。！？!?`) と改行で文単位に分割される。半角 `.` は直後が空白か文末のときだけ区切る (`3.14` や `Node.js` は割らない)。記号だけになる部分 (「えっ！？」の「？」、「「完了。」」の「」」など) は前の文にまとめる。100 文字を超える文は、後半にある最後の読点 (`、` `，`) か空白の直後で分割する (無ければ 100 文字目で切るが、絵文字などのサロゲートペアは割らない)。各セグメントはテキスト・スピーカー ID・速度・ピッチ・音量から作る SHA256 をキーに、`.opus` (Ogg Opus) + `.meta.json` として独立にキャッシュされる。v2 以前の `.mp3` キャッシュは使われず、次回の新規合成時か `--clear` で削除される。

- **部分変更でも再利用**: テキストの一部だけ変えても、変更されていない文はキャッシュヒット
- **即時再生**: キャッシュ済みセグメントは待機なしで再生開始 (エンジンの起動・接続は最初に合成が必要になったときに行うので、先頭がキャッシュ済みならエンジンの起動を待つ間にそこから鳴る)
- **壊れたキャッシュの作り直し**: 読めない `.opus` は削除して合成し直し、キャッシュを上書きする (`--cache-only` では合成せず、その文は失敗として数える)
- **バックグラウンド生成**: 未キャッシュ分は再生と並行して API 呼び出し
- **エンジン異常時の打ち切り**: 合成中にエンジンに接続できなくなった (接続拒否・503・タイムアウト) ら、残りの未キャッシュ分の合成は中止する (キャッシュ済みの文は再生する)。1 文ごとにリトライとタイムアウトを待ち続けない
- **リトライ**: エンジンが 500 / 502 / 503 / 504 / 429 を返したときは最大 3 回まで試す。タイムアウト (`ConnectionTimeout`) と、エンジンに接続できないときはリトライしない (上記のとおり残りの合成を中止する)

キャッシュは最後に使った日から `ExpirationDays` で期限切れになるので、よく使う文ほど作り直されない。VOICEVOX のユーザー辞書で読みを直したときやエンジンを更新したときは、その文を `--refresh` 付きで一度実行してキャッシュを上書きする。

```bash
# 初回 (全セグメント生成)
VoicevoxRunCached.exe "おはようございます。今日は良い天気ですね。"

# 部分変更 (前半はキャッシュヒット、後半のみ生成)
VoicevoxRunCached.exe "おはようございます。今日は雨が降っています。"
```

### フィラー音声

`Filler.Enabled: true` にすると、セグメント生成待ちの隙間に「えーっと、」「あのー、」などのフィラー音声を再生して無音を回避する (既定は無効)。

フィラーは `--init` で事前生成したキャッシュからしか再生されない。有効にする前に一度 `--init` を実行しておくこと (`--init` は `Filler.Enabled` に関係なく生成する。未生成だとフィラーは鳴らず、Warning ログが 1 回出るだけ)。`--clear` はフィラーのキャッシュも消すので、その後は `--init` をやり直す。

フィラーは読み上げと同じ話者の声で再生する (途中で声が変わらないように)。`--init` が生成するのは `DefaultSpeaker` の分なので、`--speaker 3` のように別の話者で読み上げるなら `--init -s 3` でその話者の分も生成しておく。生成していない話者ではフィラーは鳴らない。フィラーの音量は読み上げの `--volume` に合わせる (話速・音高は既定のまま)。フィラーの読み込みや再生に失敗しても本編の読み上げは止めず、その回はフィラーを使わない (再生がタイムアウトしたときは、再生デバイスの異常として本編と同じく止める)。

### 同時実行

読み上げ (キャッシュ参照・合成・再生・`--out`)・`--init` (フィラー生成)・`speakers`・`--clear` は、一時フォルダのロックファイル (`%TEMP%\VoicevoxRunCached.speech.lock`) で 1 プロセスずつに直列化される。同時に起動すると後から来た方は先の処理が終わるまで待つので、音声が重ならず、エンジンへの API 呼び出しや自動起動も 1 つずつになる (Ctrl+C で待機を中止できる。読み上げ中に `speakers` を実行すると、読み上げが終わるまで待つ)。プロセスが強制終了されてもロックは OS が解放する。`devices` は対象外。`KeepEngineRunning: false` のときは、自分が起動したエンジンをロックを解放する前に止めるので、待っていた後続の処理は必要ならエンジンを起動し直す。

待つ時間には上限を設けられる (`Lock.MaxWaitSeconds`、読み上げ・`--test` では `--max-wait` で上書き)。上限までに先の処理が終わらなければ、実行せずに終了コード 9 で終わる。Hooks が続けて発火したとき、古い通知を後からまとめて読み上げないようにしたい場合に使う。`--init` / `--clear` / `speakers` は `--max-wait` を取らないので、上限を変えるときは `VoicevoxRunCached --clear --Lock:MaxWaitSeconds=60` のように設定の上書きで指定する。

### vs `curl` 直叩き

`curl` で同等の処理をするには `/initialize_speaker` → `/audio_query` → `/synthesis` → 別途プレイヤーで再生、と最低 4 ステップ必要。中間ファイルもキャッシュも手動管理。本ツールは 1 コマンドで全自動。

## パフォーマンス計測

`--verbose` で再生にかかった時間と全体の実行時間を表示する (`--no-play` 時は全体の実行時間のみ)。

```
Audio playback completed in 2376.1ms
Total execution time: 2445.4ms
```

処理の詳細 (HTTP リクエストやキャッシュヒットなど) を見たい場合は `--log-level debug` を併用する。

## トラブルシューティング

**「VOICEVOXエンジンが利用できません。」と表示される**
→ エンジンが起動しているか、`appsettings.json` の `BaseUrl` が正しいか確認。`AutoStartEngine: true` の場合は `EnginePath` 自動検出が失敗していないかも確認。

**音声が再生されない**
→ まず `Audio.OutputDevice = -1` (OS デフォルト) を試す。特定のデバイスで鳴らすには `VoicevoxRunCached.exe devices` の「Audio.OutputDevice (WaveOut index)」欄の番号を `Audio.OutputDevice` に設定する。上段の WASAPI 一覧 (`--full` の `[n]`) とは番号が一致しないことがあるので注意 (デバイス名は WinMM の制約で 31 文字までに切られる)。

**音声の冒頭が途切れる (USB / Bluetooth デバイスで多発)**
→ `Audio.PrepareDevice: true` にして `PreparationDurationMs` を 300–500 に増やす。

**キャッシュをクリアしたい**
→ `VoicevoxRunCached.exe --clear` (または `Cache.Directory` のフォルダを手動削除)。`--clear` はフィラーのキャッシュも削除するので、フィラーを使う場合は `--init` を再実行する。

**ユーザー辞書で読みを直したのに、古い読みのまま再生される**
→ キャッシュから再生されている。その文を `--refresh` 付きで一度実行すると合成し直してキャッシュを上書きする (`--no-cache` は合成し直すがキャッシュを更新しない)。

## Claude Code Hooks との連携

[Claude Code](https://claude.ai/code) の Hooks から呼び出して作業の節目を音声通知できる。`~/.claude/settings.json` に以下のように追加:

```json
{
  "hooks": {
    "Stop": [
      {
        "matcher": "",
        "hooks": [
          {
            "type": "command",
            "command": "\"C:\\Program Files\\VoicevoxRunCached\\VoicevoxRunCached.exe\" \"作業が完了しました。\" --speaker 3"
          }
        ]
      }
    ]
  }
}
```

`UserPromptSubmit` / `PostToolUse` / `Notification` など他のフックでも同様に呼び出せる。

**注意**:
- Hooks 環境では `PATH` が不十分な場合があるため、`.exe` のフルパスを直接指定すること。`.bat` 経由は `cmd /c "<バッチのフルパス>"` でラップする
- パスにスペースが含まれる場合はダブルクォートで囲み、Windows パスの `\` は JSON では `\\` にエスケープする
- 失敗しても終了コード 2 は返さないので、Claude Code の動作は止めない (Hooks では 2 がブロッキングエラーで、Stop フックなら Claude の停止が止められる)。失敗は 0 以外の終了コードとしてユーザーに表示されるだけ。エラーメッセージは stderr に UTF-8 で出るので、その表示で失敗の理由もわかる
- 複数のフック (複数セッションなど) が同時に発火した場合は、後から来た方が先の読み上げの終了を待ってから読み上げる (音声は重ならない)。待ち時間も Hooks の `timeout` に含まれるので、長い文を続けて読ませる場合は `timeout` を長めにする
- エンジンに接続するのは合成が必要なときだけ。決まった文言の通知は 2 回目以降キャッシュから再生されるので、エンジンが止まっていても起動を待たずにすぐ鳴る
- キャッシュに無い文を合成するとき、既定 (`AutoStartEngine: true`) ではエンジンが停止していても自動起動されるが、起動を待つ分だけ通知が遅れる。遅延を避けたい場合はエンジンを事前に起動しておく (`KeepEngineRunning: true` (既定) なら一度起動したエンジンは常駐する)

## 開発

- **.NET 10 / C# 14** (primary constructors, collection expressions 等を活用)
- **NAudio**: 再生・WAV 処理・リサンプル
- **Concentus + Concentus.OggFile**: Ogg Opus のエンコード/デコード (純 C#、ネイティブ依存なし)
- **Serilog**: 構造化ログ (Console + File sink)
- **FluentValidation**: 設定検証
- **Jab**: DI コンテナ (Source Generator によるコンパイル時 DI。登録漏れはビルドエラー)
- **TUnit**: ユニットテスト

リポジトリ直下の `Taskfile.yml` でよく使う操作をまとめている (`task` で一覧表示)。

```bash
task build                        # Debug ビルド
task test                         # テスト
task check                        # format → Release ビルド → テスト (コミット前に実行)
task run -- "テストメッセージ"
```

バージョンは `src_dotnet/Directory.Build.props` の `<Version>` で一元管理している。

設計方針・アーキテクチャ詳細は [`CLAUDE.md`](./CLAUDE.md) を参照。

## ライセンス

MIT License。

## 関連リンク

- [VOICEVOX 公式](https://voicevox.hiroshiba.jp/)
- [Claude Code](https://claude.ai/code)
