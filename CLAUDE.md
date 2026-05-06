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

**Serial API Calls**: VOICEVOX API は producer 側 (`SegmentProducer.ProduceAsync`) が `foreach` で 1 件ずつ処理することで直列性を担保している。`VoiceVoxApiClient` 自身に明示的なロックは無いので、もし複数スレッドから同時に呼び出すコードを足す場合は呼び出し元で直列化すること。各 speaker は session 内で初回のみ `/initialize_speaker` する (`TextToSpeechProcessor.SynthesizeAndPlayAsync` 側で 1 回だけ呼ぶ)。

**Cache Keys**: `SHA256(speaker_id + raw_text + speed + pitch + volume)` を 16 進文字列化したものをファイル名として使用。`{CacheDirectory}/{SHA256_HEX}.mp3` と `{SHA256_HEX}.meta.json` (speaker_id, parameters, text hash) のペアで保存。**SHA256 生成ロジックを変えると既存キャッシュが全て無効化されるため変更禁止**。

**Streaming Playback**: `TextToSpeechProcessor.SynthesizeAndPlayAsync` で `Channel<TextSegment>` を介した producer (`SegmentProducer.ProduceAsync`) / consumer (`AudioPlayer.PlayStreamingAsync`) を並走させる。キャッシュ済みセグメントは即座に channel に流して再生開始、未キャッシュ分は producer が直列に API を叩きながら順次 channel に流す。consumer 側が `AudioConstants.FillerStartDelayMs` 以内に次セグメントを取れない場合は `FillerManager.GetRandomFillerAudioAsync` で相槌音声を挿入して無音を回避する。片側の例外は linked CTS で双方を停止する。キャッシュ書き込みは channel 書き込みの後で fire-and-forget して producer の次の API 呼び出しを遅らせない (producer 終了時に `finally` 内で `Task.WhenAll` 待機、例外パスでも保証)。`writer.TryComplete()` は `pendingCacheSaves` の `WhenAll` より**前**に呼ぶこと — 順序を逆にすると最終セグメント再生後の `WaitToReadAsync` が `FillerStartDelayMs` を超え、フィラーを誤発火する。

**Audio Format Chain**: API (WAV) → NAudio で変換 → NAudio.Lame で MP3 化 → `.mp3` + `.meta.json` でキャッシュ。

**Engine Lifecycle**: `VoiceVoxEngineManager` が起動済みプロセスを検出してから launch。VOICEVOX / AIVIS Speech 両対応 (設定で切替)。`KeepEngineRunning: true` で常駐、false で終了時に停止。

## Project Structure

```
src_dotnet/
├── VoicevoxRunCached/
│   ├── Program.cs                     # Entry point
│   ├── GlobalUsings.cs                # Services.{Audio,Cache,Commands,Infrastructure,Synthesis} を global using
│   ├── Configuration/                 # AppSettings + FluentValidation Validators
│   ├── Constants/                     # AudioConstants 等のマジックナンバー集約
│   ├── Models/                        # VoiceRequest, TextSegment, AudioFormat
│   ├── Services/                      # コア実装
│   │   ├── CommandRouter.cs                # コマンドルーティング
│   │   ├── ArgumentParser.cs               # CLI 引数解析
│   │   ├── ServiceRegistration.cs          # DryIoc コンテナ構築
│   │   ├── Audio/                          # 音声再生・デバイス・フォーマット
│   │   │   ├── AudioPlayer.cs              # ストリーミング再生
│   │   │   ├── AudioDeviceManager.cs       # デバイス事前準備 (warm-up)
│   │   │   ├── AudioFormatDetector.cs      # WAV/MP3 検出
│   │   │   ├── AudioConversionUtility.cs   # WAV→MP3 変換
│   │   │   ├── MediaFoundationManager.cs   # MediaFoundation 初期化
│   │   │   └── WavePlayerManager.cs        # 共有 IWavePlayer ライフサイクル
│   │   ├── Cache/                          # キャッシュ層
│   │   │   ├── AudioCacheManager.cs        # キャッシュ統合ファサード
│   │   │   ├── CacheKeyGenerator.cs        # SHA256 キー生成 (変更禁止)
│   │   │   ├── MemoryCacheStore.cs         # IMemoryCache ラッパ
│   │   │   ├── DiskCacheStore.cs           # mp3 + meta の永続化
│   │   │   ├── CacheEvictionPolicy.cs      # 期限切れ・サイズ超過の eviction
│   │   │   └── CacheErrorMapper.cs         # I/O 例外 → VoicevoxRunCachedException 変換
│   │   ├── Synthesis/                      # 合成・再生パイプライン
│   │   │   ├── TextToSpeechProcessor.cs    # メインパイプライン (CommandRouter から呼ばれる)
│   │   │   ├── SegmentProducer.cs          # Channel<TextSegment> へ流す producer (直列 API 呼び出し)
│   │   │   ├── AudioExportService.cs       # --out 用の保存処理
│   │   │   ├── VoiceVoxApiClient.cs        # REST API クライアント
│   │   │   ├── VoiceVoxEngineManager.cs    # エンジン起動・死活
│   │   │   ├── TextSegmentProcessor.cs     # 文単位分割
│   │   │   └── FillerManager.cs            # フィラー音声生成・取得
│   │   ├── Commands/                       # コマンドハンドラ
│   │   │   ├── SpeakerCommandHandler.cs        # speakers
│   │   │   ├── DeviceCommandHandler.cs         # devices
│   │   │   ├── InitCommandHandler.cs           # --init
│   │   │   ├── CacheCommandHandler.cs          # --clear
│   │   │   └── BenchmarkCommandHandler.cs      # --benchmark
│   │   └── Infrastructure/                 # 横断的ユーティリティ
│   │       ├── ApplicationBootstrap.cs     # 設定 + Serilog + MF 初期化
│   │       ├── ConfigurationManager.cs / ConfigurationValidationService.cs
│   │       ├── LoggingManager.cs           # Serilog 構成
│   │       ├── RetryPolicyService.cs       # Polly リトライ
│   │       ├── CancellationManager.cs      # Ctrl+C 協調キャンセル
│   │       ├── ConsoleHelper.cs            # 色付き出力
│   │       ├── ErrorHandlingUtility.cs     # 例外 → ErrorCode → ExitCode
│   │       ├── ProgressSpinner.cs          # 進捗表示
│   │       ├── IClock.cs                   # 時刻抽象 (テスト用に差し替え可)
│   │       └── AssemblyVersion.cs          # アセンブリバージョン参照
│   ├── Benchmarks/                    # AudioProcessingBenchmarks (BenchmarkDotNet)
│   ├── Exceptions/                    # VoicevoxRunCachedException, ErrorCodes
│   └── appsettings.json
└── VoicevoxRunCached.Tests/           # TUnit ベースのユニットテスト
```

DI コンテナは `Services/ServiceRegistration.cs` で DryIoc を構築する。新しいクラスを singleton 登録する箇所はここに集約されている。
`GlobalUsings.cs` で `Services.{Audio,Cache,Commands,Infrastructure,Synthesis}` を global using しているため、各ファイルでの個別 using は不要。

## Common Development Patterns

### Adding a New Command

1. `Services/Commands/` にハンドラクラス (例: `XxxCommandHandler`) を作成。共通インターフェースは無く、具象クラスで `HandleXxxAsync()` 等の固有メソッドを公開する
2. `Services/ServiceRegistration.cs` に `container.Register<XxxCommandHandler>(Reuse.Singleton)` で登録
3. `CommandRouter` のコンストラクタ引数として受け取り、`ExecuteCommandAsync` の switch 文に分岐を追加

テキスト読み上げ系の追加機能は、独立したハンドラを作らず `TextToSpeechProcessor` に直接機能追加するか、`AudioExportService` のような単機能ヘルパに分けるのが現状の構成。

### Reusable Utilities (新規実装の前にこれらを探すこと)

`Services/Infrastructure/` 配下に共通ユーティリティが揃っている。新しい関数を作る前に再利用を検討する:

- **`ConsoleHelper`**: 色付き出力、エラー表示。`Console.WriteLine` を直接使わず原則これ経由
- **`ErrorHandlingUtility`**: 例外 → ErrorCode → ExitCode 変換。`HandleExceptionAndGetExitCode` がコマンド末尾の定型処理
- **`RetryPolicyService`**: HTTP/ファイル操作のリトライ Polly ポリシー
- **`CancellationManager`**: Ctrl+C の協調キャンセル
- **`MediaFoundationManager`** (`Services/Audio/`): NAudio の MediaFoundation 初期化を一元管理
- **`ConfigurationManager` / `ConfigurationValidationService`**: AppSettings 読込と FluentValidation による検証
- **`IClock`**: 時刻抽象。`DateTime.UtcNow` を直接呼ばず、テスト容易性のためこのインターフェース経由で取得する

### Modifying Cache Behavior

- SHA256 生成ロジックは**変更禁止** (既存キャッシュが全て無効化される)
- 期限切れは `ExpirationDays`、容量上限は `MaxSizeGB` で eviction
- キャッシュは executable と同じディレクトリに保存される (`UseExecutableBaseDirectory: true`)
- 再生前に必ず `.meta.json` の speaker_id とパラメータが一致するか検証する

### Working with Text Segmentation

`TextSegmentProcessor.ProcessTextAsync(text, speakerId)` が `List<TextSegment>` を返す。

- 分割は日本語の文末記号 (`。 ! ?`) と改行で行われる
- 1 セグメントが `maxSegmentLength` (デフォルト 100 文字) を超えると強制分割
- 空白のみ・空セグメントは filter される
- キャッシュヒット判定はセグメント単位なので、テキストの一部変更でも未変更部分は再利用される
- **キャッシュキーは Speed/Pitch/Volume を含むため、ユーザー指定値は必ず `VoiceRequest` に乗せて `AudioCacheManager.ProcessTextSegmentsAsync(segments, request)` に渡すこと** (デフォルト値で問い合わせると永遠にミスヒットする)

### Handling API Calls

`VoiceVoxApiClient` は VOICEVOX エンジンの制約上、呼び出し元側で直列実行されることを前提としている。

- 現状 `SegmentProducer.ProduceAsync` の `foreach` で 1 件ずつ処理されているため OK
- 並列化を試みる場合は VOICEVOX エンジン側の制約を実機で確認してから、クライアント内に lock を入れること
- speaker 初期化 (`/initialize_speaker`) は `TextToSpeechProcessor.SynthesizeAndPlayAsync` 側からセッション内 1 回だけ呼ぶ
- リトライ・エラー変換は `VoiceVoxApiClient.ExecuteApiAsync` の単一 catch ブロック (`MapToApiException` ヘルパ) でまとめて適用 (各メソッドは生の HTTP 呼び出しだけ書く)

### Logging Conventions

**インスタンスクラスは必ずコンストラクタで `Microsoft.Extensions.Logging.ILogger` を受け取り `_logger.LogXxx(...)` で書く** (CommandHandler 系も例外なし)。`Serilog.Log` の static 呼び出しは Serilog 設定そのもの (`LoggingManager`)、ブートストラップ (`Program` / `ApplicationBootstrap`)、static utility (`AudioConversionUtility`, `MediaFoundationManager`) など、DI が使えない箇所のみに残す。

`ILogger` と `ConsoleHelper` の使い分け:
- **`ILogger`** = ログ用途。Serilog の構造化ログ・ファイル/JSON sink に流れる。内部エラー・警告・運用追跡情報はこちらに書く
- **`ConsoleHelper`** = ユーザー向け UI 出力。色付き、TTY のみ。コマンド成功/失敗の人向けメッセージはこちら
- 両方残したい場合 (例: `Cache cleared!`) は `_logger.LogInformation` + `ConsoleHelper.WriteSuccess` を併記
- **内部エラーを `ConsoleHelper.WriteWarning` に流すのは誤用** — JSON ログに残らないので運用追跡が壊れる

レベル指針:
- Error: ユーザー対応が必要な復旧可能エラー
- Warning: 想定外だが処理継続できる状態
- Information: 主要マイルストーン (engine 起動, cache clear, セグメント分割完了 等)
- Debug: 詳細フロー (HTTP リクエスト URL, segment 処理, cache hit 等)
- **`DEBUG`/`if DEBUG` のような prefix・条件付きログは付けない** (出力が見にくくなるため)

## Configuration

`appsettings.json` で `VoiceVox` / `Cache` / `Audio` / `Filler` / `Logging` / `Test` セクションを定義。各セクションは `Configuration/AppSettings.cs` の型にバインドされ `Validators.cs` (FluentValidation) で検証される。Serilog は `LoggingManager` が `LoggingSettings` を使ってプログラム的に構成 (`appsettings.json` から `Serilog:*` セクションを直接読み込んでいるわけではない)。詳細プロパティは appsettings.json と AppSettings.cs を直接参照。

## Pre-Commit Hooks and Code Quality

`.pre-commit-config.yaml` で `git commit` 時に以下が自動実行される:

1. **dotnet-build** (Release 構成)
2. **dotnet-format** (idempotent、いつでも安全に実行可)
3. **dotnet-test** (`VoicevoxRunCached.Tests/` の TUnit テスト)
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
