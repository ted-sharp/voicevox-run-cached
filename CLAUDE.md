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

**Serial API Calls**: VOICEVOX API は producer 側 (`SegmentProducer.ProduceAsync`) がループで 1 件ずつ await することで直列性を担保している。`VoiceVoxApiClient` 自身に明示的なロックは無いので、もし複数スレッドから同時に呼び出すコードを足す場合は呼び出し元で直列化すること。各 speaker は session 内で初回のみ `/initialize_speaker` する (`TextToSpeechProcessor.SynthesizeAndPlayAsync` 側で 1 回だけ呼ぶ)。

**Inter-process Serialization**: `TextToSpeechProcessor.ProcessTextToSpeechAsync` は処理全体 (キャッシュ参照 → 合成 → 再生 → `--out`) を `InterProcessLock.AcquireAsync(InterProcessLock.SpeechLockPath, ...)` で囲み、プロセス間でも 1 つずつ実行する (Hooks の同時発火で音声が重ならない・API 呼び出しとエンジン起動もプロセスをまたいで直列)。ロックは `%TEMP%\VoicevoxRunCached.speech.lock` を `FileShare.None` で開いている間で、後から来た方は 100ms 間隔で再試行して待つ。名前付き Mutex は取得スレッドでしか解放できず await と相性が悪いので使わない。ファイルロックはプロセスが kill されても OS が解放する。`--init` (`InitCommandHandler.HandleInitAsync`) も同じロックで囲む (読み上げ中に実行しても API 呼び出しが並列にならず、エンジンの自動起動も二重にならない)。`speakers` / `devices` / `--clear` はロックしない。起動時の設定検証 (`DirectoryValidation.BeWritableDirectory`) はロックより前に走るので、書き込み確認のファイル名は毎回一意にしている (固定名だと同時起動で共有違反になる)。一意名なので削除に失敗すると溜まるため、`FileOptions.DeleteOnClose` で閉じたときに OS に消させる。

**Cache Keys**: `CacheKeyGenerator.Compute` が `SHA256("{text}|{speakerId}|{speed:F2}|{pitch:F2}|{volume:F2}")` (InvariantCulture) を 16 進小文字化したものをファイル名として使用。`{CacheDirectory}/{SHA256_HEX}.opus` と `{SHA256_HEX}.meta.json` (CreatedAt, Text, SpeakerId, Speed, Pitch, Volume) のペアで保存。**SHA256 生成ロジックを変えると既存キャッシュが全て無効化されるため変更禁止**。**Speed/Pitch/Volume は `/audio_query` のクエリでは効かない** (VOICEVOX が無視する) ので、`VoiceVoxApiClient.ApplyVoiceParameters` が返却 JSON の `speedScale`/`pitchScale`/`volumeScale` を書き換えてから `/synthesis` に渡す。値は `CacheKeyGenerator.NormalizeParameter` (F2 書式経由。`Math.Round` は丸め方式が異なるので使わない) で丸め、キーと合成パラメータを一致させる。

**Cache Data Format**: キャッシュ由来の `TextSegment.AudioData` は **Ogg Opus**、今回合成したものは **WAV** (VOICEVOX は 24kHz)。両者が混在するため、音声バイト列を読むときは必ず `AudioFormatDetector.CreateWaveStream` で形式判定する (`WaveFileReader` 決め打ち禁止)。Opus はデコード後 **48kHz** になるのでセグメント間でサンプルレートが異なる。再生はセグメントごとに `Init` するので問題ないが、結合する `AudioExportService` は 48kHz mono 16bit に揃えてから書く。

**Streaming Playback**: `TextToSpeechProcessor.SynthesizeAndPlayAsync` で `Channel<TextSegment>` を介した producer (`SegmentProducer.ProduceAsync`) / consumer (`AudioPlayer.PlayStreamingAsync`) を並走させる。キャッシュ済みセグメントは即座に channel に流して再生開始、未キャッシュ分は producer が直列に API を叩きながら順次 channel に流す (1 回の入力に同じ文が複数あれば、2 回目以降は合成し直さず 1 回目の音声を使い、保存も 1 回だけにする。同じキーの保存が並行すると同じファイルに書き込んで共有違反になりうるため)。consumer 側が `AudioConstants.FillerStartDelayMs` 以内に次セグメントを取れない場合は、`PlayStreamingAsync` に渡されたフィラー取得関数 (`FillerManager.GetRandomFillerAudioAsync(request.SpeakerId, ...)`) で相槌音声を挿入して無音を回避する。フィラーは読み上げと同じ話者の声で、話者ごとに `--init -s <id>` で生成する (`--init` は `Filler.Enabled` に関係なく生成し、`Enabled` は再生にだけ効く。未生成の話者では鳴らさず、Warning は 1 実行 1 回)。フィラーの取得・再生の失敗は本編を止めず (`AudioPlayer.TryPlayFillerAsync` が Warning にして、その再生の間はフィラーを使わない)。ただし再生のタイムアウト (`TimeoutException`) は投げ直す (`PlayWaveAsync` が共有 WavePlayer を再生途中で `Stop()` しており、続けて本編を再生すると遅れて届く `PlaybackStopped` で本編が切れうるため)。producer はエンジンに届かない失敗 (`VoiceVoxApiClient.IsEngineUnavailable` = `EngineNotAvailable` / `ApiTimeout`。500 の `EngineProcessError` は文によるので除く) が起きたら、残りの未キャッシュ分は API を呼ばず失敗として数える (channel には流さない。キャッシュ済みは流し続ける。`SegmentProducerTests` が回帰検知)。1 件ごとにタイムアウト (`ConnectionTimeout`) を待つと、その間ロックを持ったまま後続の読み上げも待たせるため。同じロックを持つ `--init` のフィラー生成 (`FillerManager.InitializeFillerCacheAsync`) も同じ判定で残りを打ち切る (`FillerManagerTests`)。片側の例外は `WhenAllOrCancelOnFaultAsync` が linked CTS をキャンセルしてもう一方も即座に止める (`Task.WhenAll` だけで待つと両方が終わるまで戻らず、再生が落ちても残りの合成を続けてしまう)。キャッシュ書き込みは channel 書き込みの後で fire-and-forget して producer の次の API 呼び出しを遅らせない (producer 終了時に `finally` 内で `Task.WhenAll` 待機、例外パスでも保証)。`writer.TryComplete()` は `pendingCacheSaves` の `WhenAll` より**前**に呼ぶこと — 順序を逆にすると最終セグメント再生後の `WaitToReadAsync` が `FillerStartDelayMs` を超え、フィラーを誤発火する。

**Audio Format Chain**: API (WAV) → `OpusCodec.EncodeWav` (Concentus, VOIP/VBR、`Cache.OpusBitrateKbps` 既定 32kbps。フィラー (`FillerManager`)・`--out .opus` (`AudioExportService`) も同じ設定値を渡す。Opus 非対応レートは 48kHz にリサンプル) → `.opus` + `.meta.json` でキャッシュ。ビットレートはキャッシュキーに含めない (変えても既存キャッシュは流用され、作り直すなら `--clear`)。**デコード (`OpusCodec.DecodeToWav`) は 48kHz 固定** — Concentus 2.2.2 は 24kHz エンコード→24kHz デコードで出力がほぼ無音になる (`OpusCodecTests` が回帰検知)。v2 以前の `.mp3` キャッシュは読まず、`DiskCacheStore.DeleteLegacyFiles` (maintenance) と `DiskCacheStore.DeleteAll` (`--clear`) で削除される。

**Engine Lifecycle**: `VoiceVoxEngineManager` が起動済みプロセスを検出してから launch。VOICEVOX / AIVIS Speech 両対応 (設定で切替)。`KeepEngineRunning: true` で常駐、false で終了時に停止。エンジンは `UseShellExecute = true` + `WindowStyle = Hidden` で起動する (`UseShellExecute = false` だと本ツールの継承可能なハンドルをすべて引き継ぎ、Hooks から呼ばれたときの stdout / stderr のパイプが常駐エンジンに残って閉じず、Hooks がタイムアウトまで待たされる。`VoiceVoxEngineManagerTests.StartEngine_DoesNotInheritHandles` が回帰検知)。読み上げでは**合成が必要なとき (キャッシュに無いセグメントがあるとき) だけ** `TextToSpeechProcessor.SynthesizeAndPlayAsync` が `EnsureRunningOrThrowAsync` を呼ぶ。全セグメントがキャッシュ済み・`--cache-only` ならエンジンに一切接続しない (エンジン停止中でも鳴り、起動待ちで通知が遅れない。`TextToSpeechProcessorTests` が回帰検知)。

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
│   │       ├── InterProcessLock.cs         # ファイルロックによるプロセス間排他 (読み上げの直列化)
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
4. `--xxx` 形式で値を取らないコマンドなら `ArgumentParser.FlagArgs` にも追加する (無いと設定の読み込みで直後の引数がコマンドの値として消費され、`--xxx --log-level debug` 等が効かない。`CommandLineConfigurationTests` に追加)

テキスト読み上げ系の追加機能は、独立したハンドラを作らず `TextToSpeechProcessor` に直接機能追加するか、`AudioExportService` のような単機能ヘルパに分けるのが現状の構成。

### Reusable Utilities (新規実装の前にこれらを探すこと)

`Services/Infrastructure/` 配下に共通ユーティリティが揃っている。新しい関数を作る前に再利用を検討する:

- **`ConsoleHelper`**: 色付き出力、エラー表示。`Console.WriteLine` を直接使わず原則これ経由 (stdout / stderr の振り分けと、リダイレクト時に色を外す処理を持つ)
- **`ErrorHandlingUtility`**: 例外 → ErrorCode → ExitCode 変換。`HandleExceptionAndGetExitCode` がコマンド末尾の定型処理
- **`RetryPolicyService`**: VOICEVOX API 呼び出しのリトライ (500/502/503/504/429 と I/O・ソケットエラー、自前実装の指数バックオフ。Polly は使っていない)。HttpClient のタイムアウトと、ステータスの無い `HttpRequestException` (接続拒否など) はリトライしない (タイムアウトをリトライすると 1 件で約 93 秒ロックを握る。`RetryPolicyServiceTests` が回帰検知)
- **`CancellationManager`**: Ctrl+C の協調キャンセル
- **`InterProcessLock`**: ファイルロックによるプロセス間の排他 (読み上げの直列化に使用)
- **`OpusCodec`** (`Services/Audio/`): WAV ⇔ Ogg Opus 変換。キャッシュ・フィラー・`--out .opus` すべてこれ経由
- **`ConfigurationManager` / `ConfigurationValidationService`**: AppSettings 読込と FluentValidation による検証
- **`IClock`**: 時刻抽象。`DateTime.UtcNow` を直接呼ばず、テスト容易性のためこのインターフェース経由で取得する

### Modifying Cache Behavior

- SHA256 生成ロジックは**変更禁止** (既存キャッシュが全て無効化される)
- 期限切れは `ExpirationDays`、容量上限は `MaxSizeGB` で eviction。`AudioCacheManager.RunMaintenanceAsync` で両方まとめて実行し、`SegmentProducer` が新規保存のあった実行の最後に 1 回だけ呼ぶ (保存ごとに全走査しない)
- 期限・容量超過の古さは**最終利用日時 = meta.json の更新日時**で判定する (LRU)。`DiskCacheStore.SaveAsync` が CreatedAt (IClock) に設定し、`LoadAsync` がヒットのたびに `_clock.UtcNow` に更新する。作成日時で数えると、よく使う定型文ほど定期的に消えて作り直しになるため。meta.json の中身 (CreatedAt) は判定に使わない
- 期限切れは読み込み時 (`DiskCacheStore.LoadAsync`) にも判定され、該当ファイルは削除される
- LRU なので、よく使う文は辞書登録・エンジン更新後も作り直されない。個別に作り直すのは `--refresh` (キャッシュを読まずに合成し、`DiskCacheStore.SaveAsync` が上書き保存する)。`--no-cache` は読みも書きもしない (`TextToSpeechProcessor` がキャッシュ参照を `NoCache || Refresh` で飛ばし、`SegmentProducer.ProduceAsync` の `saveToCache` に `!NoCache` を渡す)
- `DiskCacheStore.SaveAsync` は **meta.json を先に**書き、`.opus` を最後に置く (途中で落ちて残るのを meta.json だけにするため。meta.json だけなら読み込みはミスになり、期限切れの掃除で消える。逆順だと `.opus` だけが残り、期限切れの掃除 (meta.json を数える) で消えない)。`.opus` は `{key}.opus.tmp` (`TemporarySuffix`) に書いてから `File.Move` で置き換える。`--refresh` は既存のペアをその場で上書きするので、`.opus` に直接書くと途中で落ちたとき (Hooks のタイムアウトで止められた等) に正しい古い meta.json と途中までの `.opus` が残り、ずっとヒットして再生に失敗するため。フィラー (`FillerManager.InitializeFillerCacheAsync`) も同じ。途中で落ちて残った `.opus.tmp` は `--clear` (`DiskCacheStore.DeleteAll` / `FillerManager.ClearFillerCacheAsync`) が消す。キャッシュ側は名前がキーごとに違い溜まり続けるので、maintenance (`DiskCacheStore.DeleteTemporaryFiles`) でも消す (全保存の完了後に、プロセス間ロックの中で走るので書き込み中のものは無い)。フィラー側は名前がフィラーの文と話者で決まり (数は `FillerTexts` × 生成した話者まで)、同じフィラーを次に生成するときに上書きされるので対象外 (`FillerTexts` から外した文の分は `--clear` まで残る)
- 読み込み時・eviction の削除 (`DiskCacheStore.Delete`) は best-effort で失敗を Warning に留めるが、`--clear` (`DiskCacheStore.DeleteAll` / `FillerManager.ClearFillerCacheAsync`) は削除そのものが目的なので、失敗を握りつぶさず `CacheErrorMapper` で `CacheException` にする (削除できていないのに成功表示しないため)。`--init` の旧形式 (`.mp3`) フィラー削除は、もう読まれないファイルの後片付けなので best-effort (Warning を出してフィラー生成を続ける)
- キャッシュは executable と同じディレクトリに保存される (`UseExecutableBaseDirectory: true`)
- `.meta.json` の検証 (`DiskCacheStore.IsMetadataValid`) は Text 非空・SpeakerId >= 0 (speaker 0 は実在)・CreatedAt 設定済みのみ。パラメータ一致はキー自体が保証するので照合しない

### Working with Text Segmentation

`TextSegmentProcessor.ProcessTextAsync(text, speakerId)` が `List<TextSegment>` を返す。

- 分割は文末記号 (`。！？!?`) と改行で行われる。半角 `.` は直後が空白か文末のときだけ区切る (`3.14` や `Node.js` を割らない)
- 文字も数字も含まないセグメント (「えっ！？」の「？」、「「完了。」」の「」」など) は前のセグメントの末尾に付ける (`AddSegmentIfNotEmpty`)。単独で API に渡しても読む音が無く、余分な合成・キャッシュ・無音の間になるだけなので。ただし付けると `maxSegmentLength` を超えるときは付けない (上限で割った記号の塊を連結し続けて上限を超えないように)。判定は `Rune.IsLetterOrDigit` (UTF-16 の 1 文字ずつだとサロゲートペアの漢字を記号とみなす)
- 1 セグメントが `maxSegmentLength` (デフォルト 100 文字) に達すると、バッファ後半にある最後の `、` `，` か空白 (`Char.IsWhiteSpace`) の直後で切り、残りを次のセグメントに持ち越す (`TextSegmentProcessor.FindSplitPosition`)。後半に区切りが無ければ上限位置で切るが、末尾が high surrogate なら 1 文字手前で切る (ペアを割ると不正な UTF-16 になる)。半角 `,` は `1,980` を割るので区切りにしない
- 空白のみ・空セグメントは filter される
- キャッシュヒット判定はセグメント単位なので、テキストの一部変更でも未変更部分は再利用される
- **キャッシュキーは Speed/Pitch/Volume を含むため、ユーザー指定値は必ず `VoiceRequest` に乗せて `AudioCacheManager.ProcessTextSegmentsAsync(segments, request)` に渡すこと** (デフォルト値で問い合わせると永遠にミスヒットする)

### Handling API Calls

`VoiceVoxApiClient` は VOICEVOX エンジンの制約上、呼び出し元側で直列実行されることを前提としている。

- 現状 `SegmentProducer.ProduceAsync` のループで 1 件ずつ処理されているため OK
- 並列化を試みる場合は VOICEVOX エンジン側の制約を実機で確認してから、クライアント内に lock を入れること
- speaker 初期化 (`/initialize_speaker`) は `TextToSpeechProcessor.SynthesizeAndPlayAsync` 側からセッション内 1 回だけ呼ぶ。`skip_reinit=true` を付ける (読み込み済みなら即座に返る。付けないと毎回初期化し直して 1.7 秒ほどかかる)
- リトライ・エラー変換は `VoiceVoxApiClient.ExecuteApiAsync` の単一 catch ブロック (`MapToApiException` ヘルパ) でまとめて適用 (各メソッドは生の HTTP 呼び出しだけ書く)。エラーコードは `MapToErrorCode` が決める: HTTP ステータスがあればステータス別、無ければ (接続拒否・応答の途中切断) `EngineNotAvailable`

### Logging Conventions

**インスタンスクラスは必ずコンストラクタで `Microsoft.Extensions.Logging.ILogger` を受け取り `_logger.LogXxx(...)` で書く** (CommandHandler 系も例外なし)。`Serilog.Log` の static 呼び出しは Serilog 設定そのもの (`LoggingManager`)、ブートストラップ (`Program` / `ApplicationBootstrap`)、static utility (`AudioConversionUtility`, `OpusCodec`) など、DI が使えない箇所のみに残す。

DI 登録クラスのコンストラクタでは `ILogger` / `IClock` などのサービスを省略可能 (`ILogger? logger = null` + `?? NullLogger.Instance`) にしない。Jab は既定値付き引数を未登録でも黙って既定値で生成するため、JAB0002 による登録漏れ検出が効かなくなる。DI 外で手組みするクラス (`DiskCacheStore` / `CacheEvictionPolicy`) も同じく必須にする (渡し忘れると黙ってログ無し・実時刻になる)。テストでは `NullLogger.Instance` / `SystemClock.Instance` を明示的に渡す。

`ILogger` と `ConsoleHelper` の使い分け:
- **`ILogger`** = ログ用途。Serilog の構造化ログ・ファイル/JSON sink に流れる。内部エラー・警告・運用追跡情報はこちらに書く。コンソールへのログは**すべて stderr** (`standardErrorFromLevel: Verbose`)
- **`ConsoleHelper`** = ユーザー向け UI 出力。コマンド成功/失敗の人向けメッセージはこちら。出力先は役割で決まる:
  - stdout: `WriteSuccess` / `WriteLine` (コマンドの結果・進捗)。stdout は `devices --json` 等の結果だけにし、ログや警告を混ぜない
  - stderr: `WriteError` / `WriteWarning` / `WriteErrorDetail` (エラー・警告と、続く解決策などの補足)。Claude Code の Hooks は終了コードが 0 / 2 以外のとき stderr を表示するので、失敗理由は必ずこちら
  - 色は出力先がリダイレクトされていないときだけ付く。リダイレクト時は `ApplicationBootstrap.ConfigureRedirectedOutputEncoding` が BOM なし UTF-8 にする (`Console.OutputEncoding` は呼び出し元コンソールのコードページを変えるので使わない)
- 両方残したい場合 (例: `Cache cleared!`) は `_logger.LogInformation` + `ConsoleHelper.WriteSuccess` を併記
- **内部エラーを `ConsoleHelper.WriteWarning` に流すのは誤用** — JSON ログに残らないので運用追跡が壊れる

レベル指針:
- Error: ユーザー対応が必要な復旧可能エラー
- Warning: 想定外だが処理継続できる状態
- Information: 主要マイルストーン (engine 起動, cache clear, セグメント分割完了 等)
- Debug: 詳細フロー (HTTP リクエスト URL, segment 処理, cache hit 等)
- **`DEBUG`/`if DEBUG` のような prefix・条件付きログは付けない** (出力が見にくくなるため)

## Configuration

`appsettings.json` で `VoiceVox` / `Cache` / `Audio` / `Filler` / `Logging` / `Test` セクションを定義。各セクションは `Configuration/AppSettings.cs` の型にバインドされ `Validators.cs` (FluentValidation) で検証される。読み込み・バインドの失敗 (JSON の構文エラー・型の不一致・ファイル無し) も `ApplicationBootstrap.LoadSettings` が `ConfigurationException` (終了コード 7) にする。`Cache.Directory` と `Filler.Directory` が同じだと `AppSettingsValidator` がエラーにする (容量超過の eviction がフィラーの `.opus` も数えて消すため。`--init` は `Filler.Enabled` に関係なく生成するので常に検証)。検証の前に `BuildAppSettings` がキャッシュ・フィラーの相対パスを (`UseExecutableBaseDirectory: true` のとき実行ファイル基準の) 絶対パスにし、`VoiceVox.BaseUrl` の末尾 `/` を取り除く (API の URL は `{BaseUrl}/version` のように組み立てるので、残すと `//version` になる)。Serilog は `LoggingManager` が `LoggingSettings` を使ってプログラム的に構成 (`appsettings.json` から `Serilog:*` セクションを直接読み込んでいるわけではない)。`Format: json` はコンソール・ファイルとも Serilog 本体の `JsonFormatter` (1 イベント 1 行)、`Level: none` は `LevelAlias.Off`。詳細プロパティは appsettings.json と AppSettings.cs を直接参照。

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
VoicevoxRunCached -- <text> [options]       # <text> がコマンド名・オプションと同じ文字列でも本文として読む
VoicevoxRunCached speakers
VoicevoxRunCached devices [--full] [--json]
VoicevoxRunCached --init [--speaker <id>]   # フィラーを生成する話者 (default: VoiceVox.DefaultSpeaker)
VoicevoxRunCached --clear
VoicevoxRunCached --test

Options:
--speaker, -s <id>    Speaker ID (default: VoiceVox.DefaultSpeaker = 1)
--speed <value>       Speech speed (default: 1.0)
--pitch <value>       Speech pitch (default: 0.0)
--volume <value>      Speech volume (default: 1.0)
--no-cache            Skip cache usage (neither read nor write)
--cache-only          Use cache only, don't call API
--refresh             Re-synthesize all segments and overwrite the cache (--no-cache / --cache-only とは併用不可)
--out, -o <path>      Save output audio to file (.wav or .opus)
--no-play             Do not play audio (useful with --out)
--verbose             Show playback time and total execution time
--log-level <level>   trace|debug|info|warn|error|crit|none
--log-format <fmt>    simple|json
--help, -h            Show help message
```

## Technical Constraints

**API**: 全呼び出し直列。Connection timeout はデフォルト 30 秒で `/initialize_speaker` 以外の HTTP リクエストに適用 (1 回あたり。タイムアウトはリトライしない)。`HttpClient.Timeout` は無限にして `VoiceVoxApiClient.SendAsync` がリクエストごとに掛け、タイムアウトは `TaskCanceledException` (→ `ApiTimeout`) にする。`/initialize_speaker` はモデルの読み込みが終わってから応答する (応答 = 読み込み完了) ので打ち切らない (かかる時間はマシンとモデル次第。`VoiceVoxApiClientTimeoutTests` が回帰検知)。CLI 引数は `ArgumentParser` で検証し、数値でない値・負の speaker/volume・0 以下の speed は `InvalidArguments` で即エラーにする (黙ってデフォルト値で続行しない)。範囲判定は F2 に丸めた後の値で行う。`--out` 等の値に `--xxx` が来た場合も値の指定漏れとしてエラー。不明なオプション・余分な引数も `InvalidArguments` (`ArgumentParser.UnexpectedArgument`。読み上げ・`--test`・`--init` 共通)。設定の読み込み側が扱う `--log-level` / `--log-format` / `--Section:Key=値` だけは `TrySkipConfigurationArgs` で値ごと読み飛ばす (値の無い `--log-level` / `--Section:Key` はエラー。黙って反映されないため)。オプションを足すときは `ApplyOptions` (と `FlagArgs`) に追加しないとエラーになる。本文は `PreprocessArgs` にかけない (`-sample` が `-s ample` に割られるため)。ただし区切りなしで本文の位置に `--` で始まる引数があれば `InvalidArguments` (`ArgumentParser.OptionInsteadOfText`。本文を忘れた `--refresh` を本文として読み上げないため)。先頭が `--` (`ArgumentParser.TextSeparator`) なら次の引数を本文にする (`speakers` 等のコマンド名と同じ本文用。`ConfigurationManager` も区切りと本文を設定の読み込みから外す)。speaker ID の存在は合成が必要なときだけ `TextToSpeechProcessor.EnsureSpeakerExists` が `/speakers` で事前確認し、無ければ `InvalidArguments` (VOICEVOX は `/initialize_speaker` で 500 を返し、リトライの分だけ待たされたうえ原因の分からないエラーになるため)。全セグメントがキャッシュヒットなら照会しない (エンジンの起動確認も同じ)。`InvalidArguments` は CommandRouter でスタックトレースなしの Warning ログになる。

**Exit Codes**: `ErrorCodes.GetExitCode` がエラーコードの prefix から決める (ENGINE=3, CACHE=4, AUDIO=5, API=6, CONFIG=7, その他=1)。一部セグメントの合成失敗は `ErrorCodes.PartialFailureExitCode` (8)、全セグメント失敗は `AudioGenerationFailed` (5)。`--init` のフィラー生成も同じ (`InitCommandHandler`)。**終了コード 2 は使わない** — Claude Code の Hooks では 2 がブロッキングエラーで、Stop フックから呼ばれた本ツールの失敗が Claude の停止を止めてしまう (`ErrorCodesTests` が回帰検知)。Ctrl+C は `ErrorCodes.CancelledExitCode` (130) で、`CommandRouter` と `Program` の `OperationCanceledException` catch の両方で返す。`CancellationManager` は 1 回目の Ctrl+C だけ握ってキャンセルを要求し (`CancelAsync` でコールバックをハンドラのスレッドで実行しない)、2 回目以降は既定の強制終了に任せる。途中に `catch (Exception)` を書くときは、その前に `catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }` を置いてキャンセルを素通しする (握ると別のエラー扱いになったり、キャッシュを破損扱いで消したりする)。README の「終了コード」表と揃えること。

**Text Processing**: `。！？!?` と改行 (半角 `.` は直後が空白・文末のとき) で文単位に分割。空セグメントは除外、記号だけのセグメントは前に結合。各セグメントが独立にキャッシュされる。

**Engine Management**: 起動時に既存 VOICEVOX プロセスを検査してから launch。`EnginePath` 空なら一般的なインストール場所から自動検出。

**Audio Device**: `OutputDevice = -1` で OS デフォルト、`0+` で WaveOut index (`WaveOut.DeviceNumber`)。`devices` は WASAPI 一覧に加えて WaveOut index 一覧 (`WaveOut.GetCapabilities`) を出す。両者の番号は一致しないことがある。NAudio 3 の `WaveOut` には `DesiredLatency` が無いので、`Audio.DesiredLatency` (全バッファ合計) は `WavePlayerManager.GetBufferMilliseconds` でバッファ 1 本の長さに換算して `BufferMilliseconds` に渡す (NAudio 2 の `WaveOutEvent` と同じ計算)。NAudio 3 は Windows 専用 API に `SupportedOSPlatform("windows")` を付けているため、`Directory.Build.props` で全プロジェクトを Windows 専用と宣言している (無いと CA1416)。`PrepareDevice: true` で再生前に小音量の音を流してデバイスを温め、最初のセグメントの取りこぼしを防ぐ。warm-up は `AudioDeviceManager.StartPreparation` で始め、コンストラクタでは始めない (`CommandRouter` の依存として全コマンドで生成されるため)。読み上げでは `TextToSpeechProcessor` が `--no-play` 以外のときロック取得直後に `AudioPlayer.BeginDevicePreparation` を呼び、キャッシュ参照・エンジン起動と並行させる。

## Code Standards

- **Target**: .NET 10 / C# 14 (primary constructors, collection expressions 等を活用)
- **Version**: `src_dotnet/Directory.Build.props` の `<Version>` 1 箇所で管理する。AssemblyVersion / FileVersion は SDK が自動生成するので csproj に書かない (`task publish:zip` と `_publish_zip.cmd` もここから読む)
- **Null Safety**: Nullable reference types 有効
- **Exceptions**: ドメインエラーは `VoicevoxRunCachedException` で wrap し `ErrorCode` を付与。framework 例外は無理に隠さず surface させる
