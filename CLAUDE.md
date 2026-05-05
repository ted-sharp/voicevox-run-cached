# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

VOICEVOX REST API のラッパー C# Console アプリ。テキストを文単位に分割し、SHA256 キーで MP3 キャッシュしながら逐次合成・再生する。完成済みのプロジェクト。

## Development Commands

`src_dotnet/VoicevoxRunCached/` で作業する。

```bash
# 基本
dotnet build                          # Debug build
dotnet build -c Release               # Release build (benchmark に必要)
dotnet format                         # コード整形 (pre-commit でも実行)
dotnet run "こんにちは、世界！"        # サンプル実行
dotnet run -- --test                  # appsettings.json の Test.Message で実行

# src_dotnet/ から
_publish.cmd          # standalone EXE を ./publish に出力
_publish_zip.cmd      # 同上 + ZIP 化
_fix-all.ps1 -Fast    # format + build (pre-commit 相当のクイック確認)
_fix-all.ps1          # 上記 + tests + SonarQube + ReSharper
```

完全なコマンド一覧は後述の「Command Interface」を参照。

## Architecture Overview

### Core Request Flow

1. **CLI Entry** (`Program.cs`) → `ApplicationBootstrap` で初期化し `CommandRouter` へ
2. **Command Dispatch** (`CommandRouter.ExecuteCommandAsync` の switch 文) → 各 `XxxCommandHandler` へ
3. **Segment Processing** (`TextSegmentProcessor`) → 日本語の文単位に分割
4. **Cache Lookup** (`AudioCacheManager`) → SHA256 キーで MP3 ヒット判定
5. **API Generation** (`VoiceVoxApiClient`) → 未キャッシュ分のみ直列で API 呼び出し
6. **Audio Playback** (`AudioPlayer`) → 順序通り再生、生成待ちの間は filler 音声で繋ぐ
7. **File Output** → `--out` 指定時に WAV/MP3 で保存

### Critical Implementation Details

**Serial API Calls**: VOICEVOX API は直列必須。`VoiceVoxApiClient` 内で `_synthesisLock` (Lock 型) によりロックされる。並列化を入れる場合は API 制約を再確認すること。各 speaker は session 内で初回のみ `/initialize_speaker` する。

**Cache Keys**: `SHA256(speaker_id + raw_text + speed + pitch + volume)` を 16 進文字列化したものをファイル名として使用。`{CacheDirectory}/{SHA256_HEX}.mp3` と `{SHA256_HEX}.meta.json` (speaker_id, parameters, text hash) のペアで保存。**SHA256 生成ロジックを変えると既存キャッシュが全て無効化されるため変更禁止**。

**Streaming Playback**: キャッシュ済みセグメントは即座に再生開始しつつ、未キャッシュ分はバックグラウンドで生成。生成待ちの隙間は `FillerManager` が自然な相槌音声を再生して無音を回避する。

**Audio Format Chain**: API (WAV) → NAudio で変換 → NAudio.Lame で MP3 化 → `.mp3` + `.meta.json` でキャッシュ。

**Engine Lifecycle**: `VoiceVoxEngineManager` が起動済みプロセスを検出してから launch。VOICEVOX / AIVIS Speech 両対応 (設定で切替)。`KeepEngineRunning: true` で常駐、false で終了時に停止。

## Project Structure

```
src_dotnet/VoicevoxRunCached/
├── Program.cs                     # Entry point
├── Configuration/                 # AppSettings + FluentValidation Validators
├── Models/                        # VoiceRequest, TextSegment, AudioFormat
├── Services/                      # コア実装
│   ├── Audio/                     # WavePlayerManager
│   └── Commands/                  # コマンドハンドラ群 + 補助クラス
│       ├── TextToSpeechCommandHandler.cs   # 通常の合成・再生
│       ├── SpeakerCommandHandler.cs        # speakers
│       ├── DeviceCommandHandler.cs         # devices
│       ├── InitCommandHandler.cs           # --init
│       ├── CacheCommandHandler.cs          # --clear
│       ├── BenchmarkCommandHandler.cs      # --benchmark
│       ├── EngineCoordinator.cs            # エンジン起動/停止の共通処理
│       ├── SegmentProcessor.cs             # セグメント単位の生成制御
│       ├── TextToSpeechProcessor.cs        # 合成パイプライン本体
│       └── AudioExportService.cs           # --out 用の保存処理
├── Benchmarks/                    # AudioProcessingBenchmarks (BenchmarkDotNet)
├── Exceptions/                    # VoicevoxRunCachedException, ErrorCodes
└── appsettings.json
```

## Common Development Patterns

### Adding a New Command

1. `Services/Commands/` にハンドラクラス (例: `XxxCommandHandler`) を作成。共通インターフェースは無く、具象クラスで `HandleXxxAsync()` 等の固有メソッドを公開する
2. `CommandRouter` のコンストラクタでフィールドとして `new` し、`ExecuteCommandAsync` の switch 文 (CommandRouter.cs:56) に分岐を追加 (`ApplicationBootstrap` への DI 登録は不要)

### Reusable Utilities (新規実装の前にこれらを探すこと)

`Services/` 直下に共通ユーティリティが揃っている。新しい関数を作る前に再利用を検討する:

- **`ConsoleHelper`**: 色付き出力、エラー表示。`Console.WriteLine` を直接使わず原則これ経由
- **`ErrorHandlingUtility`**: 例外 → ErrorCode → ExitCode 変換。`HandleExceptionAndGetExitCode` がコマンド末尾の定型処理
- **`RetryPolicyService`**: HTTP/ファイル操作のリトライ Polly ポリシー
- **`CancellationManager`**: Ctrl+C の協調キャンセル
- **`MediaFoundationManager`**: NAudio の MediaFoundation 初期化を一元管理
- **`ConfigurationManager` / `ConfigurationValidationService`**: AppSettings 読込と FluentValidation による検証

### Modifying Cache Behavior

- SHA256 生成ロジックは**変更禁止** (既存キャッシュが全て無効化される)
- 期限切れは `ExpirationDays`、容量上限は `MaxSizeGB` で eviction
- キャッシュは executable と同じディレクトリに保存される (`UseExecutableBaseDirectory: true`)
- 再生前に必ず `.meta.json` の speaker_id とパラメータが一致するか検証する

### Working with Text Segmentation

`TextSegmentProcessor.SplitIntoSegments(text)` が `List<TextSegment>` を返す。

- 分割は日本語の文末記号 (`。 ! ?`) で行われる
- 空白のみ・空セグメントは filter される
- キャッシュヒット判定はセグメント単位なので、テキストの一部変更でも未変更部分は再利用される

### Handling API Calls

`VoiceVoxApiClient` は設計上完全に直列。

- 全呼び出しが `_synthesisLock` (Lock 型) 内で実行される
- 並列化を試みる場合は VOICEVOX エンジン側の制約を実機で確認してから
- speaker 初期化 (`/initialize_speaker`) は初回 API 呼び出し時に自動実行

### Logging Conventions

Serilog を `ILogger` 経由で使う。

- Error: ユーザー対応が必要な復旧可能エラー
- Warning: 想定外だが処理継続できる状態
- Information: 主要マイルストーン (engine 起動, cache clear 等)
- Debug: 詳細フロー (segment 処理, cache hit 等)
- **`DEBUG`/`if DEBUG` のような prefix・条件付きログは付けない** (出力が見にくくなるため)

## Configuration

`appsettings.json` で `VoiceVox` / `Cache` / `Audio` / `Filler` / `Logging` / `Test` セクションを定義。各セクションは `Configuration/AppSettings.cs` の型にバインドされ `Validators.cs` (FluentValidation) で検証される。Serilog は `LoggingManager` が `LoggingSettings` を使ってプログラム的に構成 (`appsettings.json` から `Serilog:*` セクションを直接読み込んでいるわけではない)。詳細プロパティは appsettings.json と AppSettings.cs を直接参照。

## Pre-Commit Hooks and Code Quality

`.pre-commit-config.yaml` で `git commit` 時に以下が自動実行される:

1. **dotnet-build** (Release 構成)
2. **dotnet-format** (idempotent、いつでも安全に実行可)
3. **dotnet-test** (テストプロジェクトが存在すれば。現状は無し)
4. ファイル系チェック (trailing whitespace, EOF, YAML/JSON 検証, merge conflict 検出)

**フック失敗時**: `dotnet-format` 起因なら `dotnet format` を再実行すれば大半は解消する。`--no-verify` で skip しない。`_fix-all.ps1 -Fast` で同等のクイック確認ができる。

## Command Interface

```bash
VoicevoxRunCached <text> [options]
VoicevoxRunCached speakers
VoicevoxRunCached devices [--full] [--json]
VoicevoxRunCached --init
VoicevoxRunCached --clear
VoicevoxRunCached --benchmark
VoicevoxRunCached --test

Options:
--speaker, -s <id>    Speaker ID (default: 1)
--speed <value>       Speech speed (default: 1.0)
--pitch <value>       Speech pitch (default: 0.0)
--volume <value>      Speech volume (default: 1.0)
--no-cache            Skip cache usage
--cache-only          Use cache only, don't call API
--out, -o <path>      Save output audio to file (.wav or .mp3)
--no-play             Do not play audio (useful with --out)
--verbose             Show detailed timing information
--log-level <level>   trace|debug|info|warn|error|crit|none
--log-format <fmt>    simple|json
--help, -h            Show help message
```

## Technical Constraints

**API**: 全呼び出し直列。Connection timeout はデフォルト 30 秒で全 HTTP リクエストに適用。不正な speaker ID は最初の API 呼び出し前に弾く。

**Text Processing**: `。 ! ?` で文単位に分割。空セグメントは除外。各セグメントが独立にキャッシュされる。

**Engine Management**: 起動時に既存 VOICEVOX プロセスを検査してから launch。`EnginePath` 空なら一般的なインストール場所から自動検出。

**Audio Device**: `OutputDevice = -1` で OS デフォルト、`0+` で specific index。`PrepareDevice: true` で起動時に無音再生してデバイスを温め、最初のセグメントの取りこぼしを防ぐ。`OutputDeviceId` で WASAPI endpoint を直接指定可能。

## Code Standards

- **Target**: .NET 10 / C# 14 (primary constructors, collection expressions 等を活用)
- **Null Safety**: Nullable reference types 有効
- **Exceptions**: ドメインエラーは `VoicevoxRunCachedException` で wrap し `ErrorCode` を付与。framework 例外は無理に隠さず surface させる
