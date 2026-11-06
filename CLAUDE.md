# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

VOICEVOX REST API のラッパー C# Console アプリ。テキストを文単位に分割し、SHA256 キーで Ogg Opus キャッシュしながら逐次合成・再生する。完成済みのプロジェクト。

## Development Commands

リポジトリ直下の `Taskfile.yml` ([go-task](https://taskfile.dev)) で実行する。`task` だけで一覧が出る。

```bash
task build                    # Debug build
task build:release            # Release build
task test                     # TUnit テスト (dotnet run --project VoicevoxRunCached.Tests)
task format                   # コード整形
task format:check             # 整形差分の検証のみ
task check                    # format → Release build → test (コミット前の確認)
task run -- "こんにちは" -s 0  # サンプル実行 (エンジンはアプリが自動起動)
task run:test                 # appsettings.json の Test.Message で実行
task publish                  # standalone EXE を src_dotnet/publish に出力
task publish:zip              # 同上 + バージョン付き ZIP
task clean                    # bin / obj / publish を削除
task fix-all -- -Fast         # _fix-all.ps1 (引数なしで tests + SonarQube + ReSharper まで)
```

テストは .NET 10 + Microsoft.Testing.Platform のため `dotnet test` では動かない (`task test` を使う)。完全なコマンド一覧は後述の「Command Interface」を参照。

## Architecture Overview

### Core Request Flow

1. **CLI Entry** (`Program.cs`) → `ApplicationBootstrap` で初期化し `CommandRouter` へ
2. **Command Dispatch** (`CommandRouter.ExecuteCommandAsync` の switch 文) → 各 `XxxCommandHandler` へ
3. **Segment Processing** (`TextSegmentProcessor`) → 日本語の文単位に分割
4. **Cache Lookup** (`AudioCacheManager`) → SHA256 キーで Opus ヒット判定
5. **API Generation** (`VoiceVoxApiClient`) → 未キャッシュ分のみ直列で API 呼び出し
6. **Audio Playback** (`AudioPlayer`) → 順序通り再生、生成待ちの間は filler 音声で繋ぐ
7. **File Output** → `--out` 指定時に WAV/Opus で保存

### Critical Implementation Details

**Serial API Calls**: VOICEVOX API は producer 側 (`SegmentProducer.ProduceAsync`) が `foreach` で 1 件ずつ処理することで直列性を担保している。`VoiceVoxApiClient` 自身に明示的なロックは無いので、もし複数スレッドから同時に呼び出すコードを足す場合は呼び出し元で直列化すること。各 speaker は session 内で初回のみ `/initialize_speaker` する (`TextToSpeechProcessor.SynthesizeAndPlayAsync` 側で 1 回だけ呼ぶ)。

**Cache Keys**: `CacheKeyGenerator.Compute` が `SHA256("{text}|{speakerId}|{speed:F2}|{pitch:F2}|{volume:F2}")` (InvariantCulture) を 16 進小文字化したものをファイル名として使用。`{CacheDirectory}/{SHA256_HEX}.opus` と `{SHA256_HEX}.meta.json` (CreatedAt, Text, SpeakerId, Speed, Pitch, Volume) のペアで保存。**SHA256 生成ロジックを変えると既存キャッシュが全て無効化されるため変更禁止**。**Speed/Pitch/Volume は `/audio_query` のクエリでは効かない** (VOICEVOX が無視する) ので、`VoiceVoxApiClient.ApplyVoiceParameters` が返却 JSON の `speedScale`/`pitchScale`/`volumeScale` を書き換えてから `/synthesis` に渡す。値は `CacheKeyGenerator.NormalizeParameter` (F2 書式経由。`Math.Round` は丸め方式が異なるので使わない) で丸め、キーと合成パラメータを一致させる。

**Cache Data Format**: キャッシュ由来の `TextSegment.AudioData` は **Ogg Opus**、今回合成したものは **WAV** (VOICEVOX は 24kHz)。両者が混在するため、音声バイト列を読むときは必ず `AudioFormatDetector.CreateWaveStream` で形式判定する (`WaveFileReader` 決め打ち禁止)。Opus はデコード後 **48kHz** になるのでセグメント間でサンプルレートが異なる。再生はセグメントごとに `Init` するので問題ないが、結合する `AudioExportService` は 48kHz mono 16bit に揃えてから書く。

**Streaming Playback**: `TextToSpeechProcessor.SynthesizeAndPlayAsync` で `Channel<TextSegment>` を介した producer (`SegmentProducer.ProduceAsync`) / consumer (`AudioPlayer.PlayStreamingAsync`) を並走させる。キャッシュ済みセグメントは即座に channel に流して再生開始、未キャッシュ分は producer が直列に API を叩きながら順次 channel に流す。consumer 側が `AudioConstants.FillerStartDelayMs` 以内に次セグメントを取れない場合は `FillerManager.GetRandomFillerAudioAsync` で相槌音声を挿入して無音を回避する。片側の例外は linked CTS で双方を停止する。キャッシュ書き込みは channel 書き込みの後で fire-and-forget して producer の次の API 呼び出しを遅らせない (producer 終了時に `finally` 内で `Task.WhenAll` 待機、例外パスでも保証)。`writer.TryComplete()` は `pendingCacheSaves` の `WhenAll` より**前**に呼ぶこと — 順序を逆にすると最終セグメント再生後の `WaitToReadAsync` が `FillerStartDelayMs` を超え、フィラーを誤発火する。

**Audio Format Chain**: API (WAV) → `OpusCodec.EncodeWav` (Concentus, VOIP/VBR、`Cache.OpusBitrateKbps` 既定 32kbps。Opus 非対応レートは 48kHz にリサンプル) → `.opus` + `.meta.json` でキャッシュ。ビットレートはキャッシュキーに含めない (変えても既存キャッシュは流用され、作り直すなら `--clear`)。**デコード (`OpusCodec.DecodeToWav`) は 48kHz 固定** — Concentus 2.2.2 は 24kHz エンコード→24kHz デコードで出力がほぼ無音になる (`OpusCodecTests` が回帰検知)。v2 以前の `.mp3` キャッシュは読まず、`DiskCacheStore.DeleteLegacyFiles` (maintenance) と `DiskCacheStore.DeleteAll` (`--clear`) で削除される。

**Engine Lifecycle**: `VoiceVoxEngineManager` が起動済みプロセスを検出してから launch。VOICEVOX / AIVIS Speech 両対応 (設定で切替)。`KeepEngineRunning: true` で常駐、false で終了時に停止。

## Project Structure

```
src_dotnet/
├── VoicevoxRunCached/
│   ├── Program.cs                     # Entry point
│   ├── GlobalUsings.cs                # Services.{Audio,Cache,Commands,Infrastructure,Synthesis} を global using
│   ├── Configuration/                 # AppSettings + FluentValidation Validators
│   ├── Constants/                     # AudioConstants 等のマジックナンバー集約
│   ├── Models/                        # VoiceRequest, TextSegment, AudioFormat, CacheMetadata
│   ├── Services/                      # コア実装
│   │   ├── CommandRouter.cs                # コマンドルーティング
│   │   ├── ArgumentParser.cs               # CLI 引数解析
│   │   ├── AppServiceProvider.cs           # Jab (コンパイル時 DI) のプロバイダ
│   │   ├── Audio/                          # 音声再生・デバイス・フォーマット
│   │   │   ├── AudioPlayer.cs              # ストリーミング再生
│   │   │   ├── AudioDeviceManager.cs       # デバイス事前準備 (warm-up)
│   │   │   ├── AudioFormatDetector.cs      # WAV/Ogg Opus 検出
│   │   │   ├── OpusCodec.cs                # WAV ⇔ Ogg Opus (Concentus)
│   │   │   ├── AudioConversionUtility.cs   # テスト・warm-up 用 WAV 生成
│   │   │   └── WavePlayerManager.cs        # 共有 IWavePlayer ライフサイクル
│   │   ├── Cache/                          # キャッシュ層
│   │   │   ├── AudioCacheManager.cs        # キャッシュ統合ファサード
│   │   │   ├── CacheKeyGenerator.cs        # SHA256 キー生成 (変更禁止)
│   │   │   ├── DiskCacheStore.cs           # opus + meta の永続化
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
│   │   │   └── CacheCommandHandler.cs          # --clear
│   │   └── Infrastructure/                 # 横断的ユーティリティ
│   │       ├── ApplicationBootstrap.cs     # 設定の構築・検証 + Serilog 構成 + ANSI カラー有効化
│   │       ├── ConfigurationManager.cs / ConfigurationValidationService.cs
│   │       ├── LoggingManager.cs           # Serilog 構成
│   │       ├── RetryPolicyService.cs       # HTTP リトライ (自前実装の指数バックオフ)
│   │       ├── CancellationManager.cs      # Ctrl+C 協調キャンセル
│   │       ├── ConsoleHelper.cs            # 色付き出力
│   │       ├── ErrorHandlingUtility.cs     # 未知例外の表示
│   │       ├── ProgressSpinner.cs          # 進捗表示
│   │       ├── IClock.cs                   # 時刻抽象 (テスト用に差し替え可)
│   │       └── AssemblyVersion.cs          # アセンブリバージョン参照
│   ├── Exceptions/                    # VoicevoxRunCachedException, ErrorCodes
│   ├── Utilities/                     # JsonSerializerOptionsCache (meta.json / devices --json の日本語非エスケープ設定)
│   └── appsettings.json
└── VoicevoxRunCached.Tests/           # TUnit ベースのユニットテスト
```

DI コンテナは Jab (Source Generator) で、`Services/AppServiceProvider.cs` の `[Singleton(typeof(Xxx))]` 属性に登録を集約している。依存の登録漏れは JAB0002、循環依存は JAB0008 としてビルドエラーになる。設定・`ILogger`・`IClock` は `Instance = nameof(...)` でプロバイダのプロパティから渡す (`Program.cs` で `new AppServiceProvider { Settings = ..., Logger = ... }`)。IDisposable な singleton はプロバイダの `Dispose()` が登録順に破棄する。
`GlobalUsings.cs` で `Services.{Audio,Cache,Commands,Infrastructure,Synthesis}` を global using しているため、各ファイルでの個別 using は不要。

## Common Development Patterns

### Adding a New Command

1. `Services/Commands/` にハンドラクラス (例: `XxxCommandHandler`) を作成。共通インターフェースは無く、具象クラスで `HandleXxxAsync()` 等の固有メソッドを公開する
2. `Services/AppServiceProvider.cs` に `[Singleton(typeof(XxxCommandHandler))]` 属性を追加して登録
3. `CommandRouter` のコンストラクタ引数として受け取り、`ExecuteCommandAsync` の switch 文に分岐を追加

テキスト読み上げ系の追加機能は、独立したハンドラを作らず `TextToSpeechProcessor` に直接機能追加するか、`AudioExportService` のような単機能ヘルパに分けるのが現状の構成。

### Reusable Utilities (新規実装の前にこれらを探すこと)

`Services/Infrastructure/` 配下に共通ユーティリティが揃っている。新しい関数を作る前に再利用を検討する:

- **`ConsoleHelper`**: 色付き出力、エラー表示。`Console.WriteLine` を直接使わず原則これ経由
- **`ErrorHandlingUtility`**: 例外 → ErrorCode → ExitCode 変換。`HandleExceptionAndGetExitCode` がコマンド末尾の定型処理
- **`RetryPolicyService`**: VOICEVOX API 呼び出しのリトライ (5xx/429/timeout、自前実装の指数バックオフ。Polly は使っていない)
- **`CancellationManager`**: Ctrl+C の協調キャンセル
- **`OpusCodec`** (`Services/Audio/`): WAV ⇔ Ogg Opus 変換。キャッシュ・フィラー・`--out .opus` すべてこれ経由
- **`ConfigurationManager` / `ConfigurationValidationService`**: AppSettings 読込と FluentValidation による検証
- **`IClock`**: 時刻抽象。`DateTime.UtcNow` を直接呼ばず、テスト容易性のためこのインターフェース経由で取得する

### Modifying Cache Behavior

- SHA256 生成ロジックは**変更禁止** (既存キャッシュが全て無効化される)
- 期限切れは `ExpirationDays`、容量上限は `MaxSizeGB` で eviction。`AudioCacheManager.RunMaintenanceAsync` で両方まとめて実行し、`SegmentProducer` が新規保存のあった実行の最後に 1 回だけ呼ぶ (保存ごとに全走査しない)
- 期限切れは読み込み時 (`DiskCacheStore.LoadAsync`) にも判定され、該当ファイルは削除される
- 読み込み時・eviction の削除 (`DiskCacheStore.Delete`) は best-effort で失敗を Warning に留めるが、`--clear` (`DiskCacheStore.DeleteAll` / `FillerManager.ClearFillerCacheAsync`) と `--init` の旧形式フィラー削除は失敗を握りつぶさず `CacheErrorMapper` で `CacheException` にする (削除できていないのに成功表示しないため)
- キャッシュは executable と同じディレクトリに保存される (`UseExecutableBaseDirectory: true`)
- `.meta.json` の検証 (`DiskCacheStore.IsMetadataValid`) は Text 非空・SpeakerId >= 0 (speaker 0 は実在)・CreatedAt 設定済みのみ。パラメータ一致はキー自体が保証するので照合しない

### Working with Text Segmentation

`TextSegmentProcessor.ProcessTextAsync(text, speakerId)` が `List<TextSegment>` を返す。

- 分割は文末記号 (`。！？!?`) と改行で行われる。半角 `.` は直後が空白か文末のときだけ区切る (`3.14` や `Node.js` を割らない)
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

**インスタンスクラスは必ずコンストラクタで `Microsoft.Extensions.Logging.ILogger` を受け取り `_logger.LogXxx(...)` で書く** (CommandHandler 系も例外なし)。`Serilog.Log` の static 呼び出しは Serilog 設定そのもの (`LoggingManager`)、ブートストラップ (`Program` / `ApplicationBootstrap`)、static utility (`AudioConversionUtility`, `OpusCodec`) など、DI が使えない箇所のみに残す。

DI 登録クラスのコンストラクタでは `ILogger` / `IClock` などのサービスを省略可能 (`ILogger? logger = null` + `?? NullLogger.Instance`) にしない。Jab は既定値付き引数を未登録でも黙って既定値で生成するため、JAB0002 による登録漏れ検出が効かなくなる。テストでは `NullLogger.Instance` を明示的に渡す。

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

1. **dotnet-build**
2. **dotnet-format** (idempotent、いつでも安全に実行可)
3. ファイル系チェック (trailing whitespace, EOF, YAML/JSON 検証, merge conflict 検出)

テストはコミット時には実行しない。コミット前に `task check` (または `task test`) を手動で実行すること。

**フック失敗時**: `dotnet-format` 起因なら `task format` を再実行すれば大半は解消する。`--no-verify` で skip しない。

## Command Interface

```bash
VoicevoxRunCached <text> [options]
VoicevoxRunCached speakers
VoicevoxRunCached devices [--full] [--json]
VoicevoxRunCached --init
VoicevoxRunCached --clear
VoicevoxRunCached --test

Options:
--speaker, -s <id>    Speaker ID (default: VoiceVox.DefaultSpeaker = 1)
--speed <value>       Speech speed (default: 1.0)
--pitch <value>       Speech pitch (default: 0.0)
--volume <value>      Speech volume (default: 1.0)
--no-cache            Skip cache usage
--cache-only          Use cache only, don't call API
--out, -o <path>      Save output audio to file (.wav or .opus)
--no-play             Do not play audio (useful with --out)
--verbose             Show playback time and total execution time
--log-level <level>   trace|debug|info|warn|error|crit|none
--log-format <fmt>    simple|json
--help, -h            Show help message
```

## Technical Constraints

**API**: 全呼び出し直列。Connection timeout はデフォルト 30 秒で全 HTTP リクエストに適用。CLI 引数は `ArgumentParser` で検証し、数値でない値・負の speaker/volume・0 以下の speed は `InvalidArguments` で即エラーにする (黙ってデフォルト値で続行しない)。範囲判定は F2 に丸めた後の値で行う。`--out` 等の値に `--xxx` が来た場合も値の指定漏れとしてエラー。本文 (`args[0]`) は `PreprocessArgs` にかけない (`-sample` が `-s ample` に割られるため)。speaker ID の存在は合成が必要なときだけ `TextToSpeechProcessor.EnsureSpeakerExists` が `/speakers` で事前確認し、無ければ `InvalidArguments` (VOICEVOX は `/initialize_speaker` で 500 を返しリトライで十数秒待たされるため)。全セグメントがキャッシュヒットなら照会しない。`InvalidArguments` は CommandRouter でスタックトレースなしの Warning ログになる。

**Exit Codes**: `ErrorCodes.GetExitCode` がエラーコードの prefix から決める (ENGINE=3, CACHE=4, AUDIO=5, API=6, CONFIG=7, その他=1)。一部セグメントの合成失敗は `ErrorCodes.PartialFailureExitCode` (8)。**終了コード 2 は使わない** — Claude Code の Hooks では 2 がブロッキングエラーで、Stop フックから呼ばれた本ツールの失敗が Claude の停止を止めてしまう (`ErrorCodesTests` が回帰検知)。Ctrl+C は `ErrorCodes.CancelledExitCode` (130) で、`CommandRouter` と `Program` の `OperationCanceledException` catch の両方で返す。途中に `catch (Exception)` を書くときは、その前に `catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }` を置いてキャンセルを素通しする (握ると別のエラー扱いになったり、キャッシュを破損扱いで消したりする)。README の「終了コード」表と揃えること。

**Text Processing**: `。！？!?` と改行 (半角 `.` は直後が空白・文末のとき) で文単位に分割。空セグメントは除外。各セグメントが独立にキャッシュされる。

**Engine Management**: 起動時に既存 VOICEVOX プロセスを検査してから launch。`EnginePath` 空なら一般的なインストール場所から自動検出。

**Audio Device**: `OutputDevice = -1` で OS デフォルト、`0+` で WaveOut index (`WaveOutEvent.DeviceNumber`)。`devices` は WASAPI 一覧に加えて WaveOut index 一覧を出す (netstandard 版 NAudio には `WaveOut.GetCapabilities` が無いため `WaveInterop.waveOutGetDevCaps` で取得)。両者の番号は一致しないことがある。`PrepareDevice: true` で起動時に無音再生してデバイスを温め、最初のセグメントの取りこぼしを防ぐ。

## Code Standards

- **Target**: .NET 10 / C# 14 (primary constructors, collection expressions 等を活用)
- **Version**: `src_dotnet/Directory.Build.props` の `<Version>` 1 箇所で管理する。AssemblyVersion / FileVersion は SDK が自動生成するので csproj に書かない (`task publish:zip` と `_publish_zip.cmd` もここから読む)
- **Null Safety**: Nullable reference types 有効
- **Exceptions**: ドメインエラーは `VoicevoxRunCachedException` で wrap し `ErrorCode` を付与。framework 例外は無理に隠さず surface させる
