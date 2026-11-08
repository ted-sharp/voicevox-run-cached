# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

VOICEVOX REST API のラッパー C# Console アプリ。テキストを文単位に分割し、SHA256 キーで Ogg Opus キャッシュしながら逐次合成・再生する。主な用途は Claude Code の Hooks からの通知の読み上げ。

経緯 (なぜそうなったか) は git log にある。この文書は「守ること / 理由 / 回帰検知するテスト」だけを書く。

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

テストは .NET 10 + Microsoft.Testing.Platform のため `dotnet test` では動かない (`task test` を使う)。

## Request Flow

1. `Program.cs` → `ApplicationBootstrap` (設定の読み込み・検証、Serilog) → `CommandRouter.ExecuteCommandAsync` の switch → 各ハンドラ
2. 読み上げは `TextToSpeechProcessor`: `SpeechLock` の中で、`TextSegmentProcessor` で文に分割 → `AudioCacheManager` でキャッシュ (Opus) を引く
3. `SegmentProducer` (producer) と `AudioPlayer.PlayStreamingAsync` (consumer) を `Channel<TextSegment>` で並走させる。producer はキャッシュ済みを WAV にデコードして流し、未キャッシュ分は `VoiceVoxApiClient` で 1 件ずつ合成して流す
4. `--out` 指定時は `AudioExportService` が WAV / Opus で保存

## 守ること

### プロセス間ロック (`SpeechLock` / `InterProcessLock`)
- エンジンかキャッシュに触れるコマンド (読み上げ・`--test`・`--init`・`speakers`・`--clear`) は `SpeechLock.RunAsync` で囲む。囲まないのはどちらにも触れない `devices` だけ。Hooks が同時に発火しても音声が重ならず、API 呼び出しとエンジン起動もプロセスをまたいで直列になる
- ロックは `%TEMP%\VoicevoxRunCached.speech.lock` を `FileShare.None` で開いている間。後から来た方は 100ms 間隔で再試行する。ファイルロックはプロセスが kill されても OS が解放する。名前付き Mutex は取得スレッドでしか解放できず await と相性が悪いので使わない
- `SpeechLock` はロックを**解放する前に** `VoiceVoxEngineManager.StopEngineIfNotKept` を呼ぶ (`KeepEngineRunning: false` で自分が起動したエンジンだけ止める)。解放後 (Dispose) に止めると、ロックを取った後続が動いているエンジンで合成を始めて途中で失敗する — 各テストクラスの `EngineStartedByUs_IsStoppedBeforeReleasingLock`
- `--clear` もロックの中で消す (保存途中に消すと meta.json の無い `.opus` が残り、書き込み中のファイルに当たると失敗する) — `CacheCommandHandlerTests`
- 待つ上限: `Lock.MaxWaitSeconds` (未設定 = 上限なし、0 = 待たない)。読み上げ・`--test` は `--max-wait <秒>` で上書きできる (`SpeechLock.ResolveMaxWait`)。超えたら `LOCK_001` (終了コード 9) で実行しない。`CommandRouter` はスタックトレースなしの Warning にする — `InterProcessLockTests` / `SpeechLockTests`
- 起動時の設定検証 (`DirectoryValidation.BeWritableDirectory`) はロックより前に走るので、書き込み確認のファイル名は毎回一意にし (固定名だと同時起動で共有違反)、`FileOptions.DeleteOnClose` で OS に消させる

### エンジン (`VoiceVoxEngineManager`)
- 起動済みのプロセスを確認してから起動する。VOICEVOX / AIVIS Speech 両対応。`EnginePath` が空なら一般的なインストール場所から探す
- 外部で起動済みだったエンジンには触れない。`KeepEngineRunning: true` なら常駐させる
- `UseShellExecute = true` + `WindowStyle = Hidden` で起動する (`false` だと Hooks の stdout / stderr のパイプを常駐エンジンが引き継ぎ、Hooks がタイムアウトまで待たされる) — `VoiceVoxEngineManagerTests.StartEngine_DoesNotInheritHandles`
- 読み上げでエンジンに接続するのは**合成が必要になったときだけ**。準備 (起動確認・`EnsureSpeakerExists`・`/initialize_speaker`) は `TextToSpeechProcessor.PrepareSynthesisAsync` で、`SegmentProducer` が最初に合成するときに 1 回だけ呼ぶ。全部キャッシュから読めれば (`--cache-only` を含む) エンジン停止中でも鳴り、起動待ちで通知が遅れない。先頭がキャッシュ済みなら起動を待つ間にそこから鳴る (その間フィラーも鳴りうる) — `TextToSpeechProcessorTests` / `SegmentProducerTests`
- 準備の失敗 (エンジン無し・話者無し) は文ごとの失敗にせず、そのまま投げる (終了コード 3 / 1)

### VOICEVOX API (`VoiceVoxApiClient`)
- 呼び出しは直列。直列性は `SegmentProducer.ProduceAsync` のループで 1 件ずつ await することで担保していて、クライアント自身にロックは無い。並列化するなら実機でエンジンの制約を確かめ、クライアント内に lock を入れる
- `/initialize_speaker` は 1 回の読み上げで 1 回だけ、`skip_reinit=true` を付ける (付けないと毎回 1.7 秒ほどかかる)
- Speed/Pitch/Volume は `/audio_query` のクエリでは効かない。`ApplyVoiceParameters` が返却 JSON の `speedScale` / `pitchScale` / `volumeScale` を書き換えてから `/synthesis` に渡す。値は `CacheKeyGenerator.NormalizeParameter` (F2 書式経由。`Math.Round` は丸め方が違うので使わない) で丸め、キーと一致させる
- タイムアウト: `ConnectionTimeout` (既定 30 秒) を `SendAsync` がリクエストごとに掛ける (`HttpClient.Timeout` は無限)。タイムアウトは `TaskCanceledException` → `ApiTimeout`。`/initialize_speaker` は読み込み完了まで応答しないので打ち切らない — `VoiceVoxApiClientTimeoutTests`
- エラー変換は `ExecuteApiAsync` の 1 つの catch (`MapToApiException`) に集約。`MapToErrorCode`: HTTP ステータスがあればステータス別、無ければ (接続拒否・途中切断) `EngineNotAvailable`
- リトライ (`RetryPolicyService`、自前の指数バックオフ。Polly は使わない): 500/502/503/504/429 と I/O・ソケットエラー。タイムアウトとステータスの無い `HttpRequestException` はリトライしない (1 件で約 93 秒ロックを握るため) — `RetryPolicyServiceTests`

### 合成・再生パイプライン (`SegmentProducer` / `AudioPlayer`)
- キャッシュ由来の音声は producer が `AudioCacheManager.DecodeOrDelete` で WAV にデコードしてから流す。デコードは 1 文 100ms 程度かかるので `Task.Run` で再生と並行させる (読み込み時にまとめると、全文キャッシュ済みでも鳴り始めが遅れる)。読めなければ削除済みなので未キャッシュとして合成し直す。`--cache-only` (`prepareSynthesis = null`) なら失敗として数える — `SegmentProducerTests` / `AudioCacheManagerIntegrationTests` / `TextToSpeechProcessorTests.CorruptedCache_IsResynthesizedAndRewritten`
- 1 回の入力に同じ文が複数あれば、2 回目以降は合成せず 1 回目の音声を使い、保存も 1 回だけ (同じキーの保存が並行すると共有違反) — `SegmentProducerTests`
- エンジンに届かない失敗 (`VoiceVoxApiClient.IsEngineUnavailable` = `EngineNotAvailable` / `ApiTimeout`。500 は文によるので除く) の後は、残りの未キャッシュ分を API を呼ばずに失敗として数え、channel に流さない (キャッシュ済みは流す)。1 件ごとにタイムアウトを待つとロックを握ったまま後続を待たせる。`--init` のフィラー生成も同じ — `SegmentProducerTests` / `FillerManagerTests`
- 片側の例外は `WhenAllOrCancelOnFaultAsync` が linked CTS をキャンセルしてもう一方も止める (`Task.WhenAll` だけだと再生が落ちても残りを合成し続ける) — `WhenAllOrCancelOnFaultTests`
- キャッシュ保存は channel に書いた後に fire-and-forget し、producer の `finally` で `Task.WhenAll` で待つ。`writer.TryComplete()` は `WhenAll` より**前** (逆だと最後の再生の後に `FillerStartDelayMs` を超えてフィラーが誤発火する) — `ChannelProducerOrderingTests`
- フィラー: 次のセグメントが `FillerStartDelayMs` 以内に来なければ `FillerManager.GetRandomFillerAudioAsync(speakerId, volume)` の相槌を挟む。読み上げと同じ話者の声 (話者ごとに `--init -s <id>` で生成)。音量は再生時に `--volume` を掛ける (話速・音高は既定のまま)。`--init` は `Filler.Enabled` に関係なく生成し、`Enabled` は再生にだけ効く。未生成の話者では鳴らさず、Warning は 1 実行 1 回
- フィラーの失敗は本編を止めない (`AudioPlayer.TryPlayFillerAsync` が Warning にし、その読み上げではフィラーを使わない)。ただし再生タイムアウト (`TimeoutException`) は投げ直す (共有 WavePlayer を途中で `Stop()` しており、遅れて届く `PlaybackStopped` で本編が切れうる)

### 音声形式
- `TextSegment.AudioData` は、合成したもの = WAV 24kHz (VOICEVOX)、キャッシュ由来 = producer がデコードした WAV 48kHz (デコード前は Ogg Opus)、フィラー = Opus (音量 1.0) / WAV (それ以外)。読むときは必ず `AudioFormatDetector.CreateWaveStream` で形式判定する (`WaveFileReader` 決め打ち禁止)。再生はセグメントごとに `Init` するのでレートが混在してよい。`AudioExportService` は 48kHz mono 16bit に揃えて結合する
- エンコードは `OpusCodec.EncodeWav` (Concentus, VOIP/VBR、`Cache.OpusBitrateKbps` 既定 32kbps。キャッシュ・フィラー・`--out .opus` 共通。Opus 非対応レートは 48kHz にリサンプル)。ビットレートはキャッシュキーに含めない (作り直すなら `--clear`)
- デコード (`OpusCodec.DecodeToWav`) は **48kHz 固定** (Concentus 2.2.2 は 24kHz エンコード→24kHz デコードでほぼ無音になる) — `OpusCodecTests`

### キャッシュ (`AudioCacheManager` / `DiskCacheStore` / `CacheEvictionPolicy`)
- キーは `CacheKeyGenerator.Compute` = `SHA256("{text}|{speakerId}|{speed:F2}|{pitch:F2}|{volume:F2}")` (InvariantCulture) の 16 進小文字。**生成ロジックの変更禁止** (既存キャッシュが全て無効になる)。`{key}.opus` と `{key}.meta.json` (CreatedAt, Text, SpeakerId, Speed, Pitch, Volume) のペアで保存
- キーに Speed/Pitch/Volume が入るので、ユーザー指定値を `VoiceRequest` に乗せて `ProcessTextSegmentsAsync(segments, request)` に渡す (既定値で引くと永遠にミスする)
- 期限 (`ExpirationDays`)・容量 (`MaxSizeGB`) の古さは**最終利用日時 = meta.json の更新日時** (LRU)。`SaveAsync` が CreatedAt (IClock) に設定し、`LoadAsync` がヒットのたびに更新する。meta.json の中身は判定に使わない。期限切れは読み込み時にも削除する
- `RunMaintenanceAsync` (旧 `.mp3`・`.opus.tmp`・期限切れ・容量超過の削除) は、新規保存のあった実行の最後に `SegmentProducer` が 1 回だけ呼ぶ (保存ごとに全走査しない)
- `SaveAsync` は **meta.json を先に**書き、`.opus` は `{key}.opus.tmp` に書いてから `File.Move` で置く (途中で落ちても残るのは meta.json か `.tmp` だけ。`.opus` が途中までだと以後ずっとヒットして再生に失敗する)。フィラー (`InitializeFillerCacheAsync`) も同じ
- `.opus.tmp` は `--clear` (`DeleteAll` / `ClearFillerCacheAsync`) が消す。キャッシュ側は maintenance の `DeleteTemporaryFiles` でも消す (ロックの中・全保存の後なので書き込み中のものは無い)。フィラー側は次の生成で上書きされるので対象外
- 削除の失敗: 読み込み時・eviction (`Delete`) は best-effort で Warning。`--clear` は削除が目的なので `CacheErrorMapper` で `CacheException` にする。`--init` の旧 `.mp3` フィラー削除は best-effort
- `--refresh` = キャッシュを読まずに合成して上書き。`--no-cache` = 読みも書きもしない (`NoCache || Refresh` で参照を飛ばし、`saveToCache: !NoCache`)
- `IsMetadataValid` は Text 非空・SpeakerId >= 0 (speaker 0 は実在)・CreatedAt 設定済みのみ (パラメータ一致はキーが保証する)
- v2 以前の `.mp3` は読まず、maintenance (`DeleteLegacyFiles`) と `--clear` (`DeleteAll`) で消す
- 保存先は既定で実行ファイルと同じディレクトリ (`UseExecutableBaseDirectory: true`)

### テキスト分割 (`TextSegmentProcessor`)
- `。！？!?` と改行で区切る。半角 `.` は直後が空白か文末のときだけ (`3.14` / `Node.js` を割らない)
- 文字も数字も含まないセグメント (「？」「」」など) は前に付ける (単独では読む音が無い)。ただし付けると `maxSegmentLength` を超えるなら付けない。判定は `Rune.IsLetterOrDigit` (UTF-16 単位だとサロゲートペアの漢字を記号とみなす)
- `maxSegmentLength` (既定 100) に達したら、後半にある最後の `、` `，` か空白の直後で切る (`FindSplitPosition`)。無ければ上限で切るが、末尾が high surrogate なら 1 文字手前。半角 `,` は `1,980` を割るので区切りにしない
- 空白のみ・空のセグメントは除く。キャッシュはセグメント単位なので一部を変えても他は再利用される

### CLI 引数 (`ArgumentParser`)
- 数値でない値・負の speaker/volume・0 以下の speed は `InvalidArguments` (黙って既定値で続けない)。範囲判定は F2 に丸めた後の値
- 値の位置に `--xxx` が来たら値の指定漏れ。不明なオプション・余分な引数も `InvalidArguments` (`UnexpectedArgument`。読み上げ・`--test`・`--init` 共通)
- `--log-level` / `--log-format` / `--Section:Key=値` は設定側が扱うので `TrySkipConfigurationArgs` で値ごと読み飛ばす (値が無ければエラー)
- オプションを足すときは `ApplyOptions` に追加する。値を取らないフラグは `FlagArgs` にも (無いと設定の読み込みで直後の引数が値として消費される) — `CommandLineConfigurationTests`
- 本文は `PreprocessArgs` にかけない (`-sample` が `-s ample` に割られる)。本文の位置にオプションの形 (`IsOptionLike`: `--` の直後が英字で空白を含まない、または `-s` / `-o`) があれば `InvalidArguments` (`--- 完了 ---` は読む)。先頭が `--` (`TextSeparator`) なら次を本文にする (`ConfigurationManager` も区切りと本文を設定から外す)
- speaker の存在確認 (`EnsureSpeakerExists`) は合成の準備でだけ行う (VOICEVOX は存在しない話者で 500 を返し、リトライの分待たされて原因が分からない)。`InvalidArguments` は `CommandRouter` でスタックトレースなしの Warning

### 終了コード・キャンセル
- `ErrorCodes.GetExitCode` がエラーコードの prefix から決める: ENGINE=3, CACHE=4, AUDIO=5, API=6, CONFIG=7, LOCK=9, その他=1。一部セグメントの失敗は `PartialFailureExitCode` (8)、全部失敗は `AudioGenerationFailed` (5)。`--init` も同じ。README の「終了コード」表と揃える
- **終了コード 2 は使わない** (Hooks では 2 がブロッキングエラーで、Stop フックの失敗が Claude の停止を止める) — `ErrorCodesTests`
- Ctrl+C は 130 (`CancelledExitCode`)。`CommandRouter` と `Program` の両方で返す。`CancellationManager` は 1 回目だけ握って `CancelAsync` し、2 回目以降は既定の強制終了に任せる
- `catch (Exception)` の前には `catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }` を置く (握ると別のエラー扱いになったり、キャッシュを破損扱いで消したりする)

### ログと出力
- インスタンスクラスはコンストラクタで `ILogger` を受け取る (CommandHandler も)。`Serilog.Log` の static 呼び出しは `LoggingManager`・`Program` / `ApplicationBootstrap`・static utility (`AudioConversionUtility`, `OpusCodec`) だけ
- DI 登録クラスのコンストラクタで `ILogger` / `IClock` などを省略可能 (`= null` + 既定値) にしない (Jab は既定値付き引数を未登録でも黙って生成し、JAB0002 が効かない)。DI 外で組む `DiskCacheStore` / `CacheEvictionPolicy` も必須。テストでは `NullLogger.Instance` / `SystemClock.Instance` を渡す
- `ILogger` = ログ (構造化・ファイル/JSON。コンソールへは**すべて stderr**)。内部エラー・警告・運用の追跡はこちら。`ConsoleHelper.WriteWarning` に流すのは誤用 (JSON ログに残らない)
- `ConsoleHelper` = ユーザー向け出力。`Console.WriteLine` を直接使わない。stdout: `WriteSuccess` / `WriteLine` (結果・進捗。`devices --json` 等の結果にログや警告を混ぜない)。stderr: `WriteError` / `WriteWarning` / `WriteErrorDetail` (Hooks は終了コード 0 / 2 以外で stderr を表示するので、失敗理由は必ずこちら)
- 色は出力先がリダイレクトされていないときだけ。リダイレクト時は `ApplicationBootstrap.ConfigureRedirectedOutputEncoding` が BOM なし UTF-8 にする (`Console.OutputEncoding` は呼び出し元コンソールのコードページを変えるので使わない)
- 両方残すとき (例: `Cache cleared!`) は `_logger.LogInformation` + `ConsoleHelper.WriteSuccess`
- レベル: Error = ユーザー対応が必要、Warning = 想定外だが続行、Information = 主要な節目、Debug = 詳細フロー
- **`DEBUG` / `if DEBUG` のような prefix・条件付きログは付けない**

### 設定 (`appsettings.json` → `AppSettings`)
- セクション: `VoiceVox` / `Cache` / `Audio` / `Filler` / `Lock` / `Logging` / `Test`。セクションを足したら、`ApplicationBootstrap.BuildAppSettings` (**セクションごとに Bind する**) と `ArgumentParser.ConfigurationSections` (`--Section:Key=値` を読み飛ばす対象) にも追加する — `ApplicationBootstrapTests.LockMaxWaitSeconds_IsLoaded` / `ArgumentParserTests.ConfigurationOverride_AcceptsEverySection`
- `Validators.cs` (FluentValidation) で検証。読み込み・バインドの失敗 (構文エラー・型の不一致・ファイル無し) も `ConfigurationException` (終了コード 7)。`Test` は `--test` のときだけ検証
- `Cache.Directory` と `Filler.Directory` が同じならエラー (容量超過の eviction がフィラーも消す。`--init` は `Enabled` に関係なく生成するので常に検証)
- 検証の前に、キャッシュ・フィラーの相対パスを (`UseExecutableBaseDirectory: true` なら実行ファイル基準の) 絶対パスにし、`VoiceVox.BaseUrl` の末尾 `/` を取る (`//version` になるため)
- 配列のプロパティ (`FillerTexts`) に初期値を入れない (ConfigurationBinder が設定値を追記する)
- Serilog は `LoggingManager` がプログラムで構成する (`Serilog:*` セクションは読まない)。`Format: json` は `JsonFormatter` (1 イベント 1 行)、`Level: none` は `LevelAlias.Off`

### オーディオデバイス
- `OutputDevice = -1` で OS 既定、`0+` で WaveOut の index。`devices` は WASAPI 一覧と WaveOut index 一覧を出す (番号は一致しないことがある)
- NAudio 3 の `WaveOut` には `DesiredLatency` が無いので、`WavePlayerManager.GetBufferMilliseconds` でバッファ 1 本の長さに換算する。Windows 専用 API の CA1416 を避けるため `Directory.Build.props` で全プロジェクトを Windows 専用と宣言している
- `PrepareDevice: true` の warm-up は `AudioDeviceManager.StartPreparation` で始める (コンストラクタで始めると全コマンドで鳴る)。読み上げは `--no-play` 以外のときロック取得直後に `AudioPlayer.BeginDevicePreparation` を呼ぶ

## Project Structure

```
src_dotnet/
├── VoicevoxRunCached/
│   ├── Program.cs / GlobalUsings.cs (Services.{Audio,Cache,Commands,Infrastructure,Synthesis} を global using)
│   ├── Configuration/      # AppSettings + FluentValidation Validators
│   ├── Constants/          # AudioConstants 等
│   ├── Models/             # VoiceRequest, TextSegment, AudioFormat, CacheMetadata
│   ├── Exceptions/         # VoicevoxRunCachedException, ErrorCodes
│   ├── Utilities/          # JsonSerializerOptionsCache
│   └── Services/
│       ├── CommandRouter.cs / ArgumentParser.cs / AppServiceProvider.cs (Jab)
│       ├── Audio/          # AudioPlayer, AudioDeviceManager, AudioFormatDetector, OpusCodec, WavePlayerManager, AudioConversionUtility
│       ├── Cache/          # AudioCacheManager, CacheKeyGenerator (変更禁止), DiskCacheStore, CacheEvictionPolicy, CacheErrorMapper
│       ├── Synthesis/      # TextToSpeechProcessor, SegmentProducer, SpeechLock, VoiceVoxApiClient, VoiceVoxEngineManager,
│       │                   # TextSegmentProcessor, FillerManager, AudioExportService
│       ├── Commands/       # SpeakerCommandHandler, DeviceCommandHandler, InitCommandHandler, CacheCommandHandler
│       └── Infrastructure/ # ApplicationBootstrap, ConfigurationManager, LoggingManager, RetryPolicyService, CancellationManager,
│                           # InterProcessLock, ConsoleHelper, ErrorHandlingUtility, ProgressSpinner, IClock, AssemblyVersion
└── VoicevoxRunCached.Tests/  # TUnit
```

DI は Jab (Source Generator)。`Services/AppServiceProvider.cs` の `[Singleton(typeof(Xxx))]` に登録を集約する。登録漏れは JAB0002、循環は JAB0008 でビルドエラー。設定・`ILogger`・`IClock` は `Instance = nameof(...)` でプロバイダのプロパティから渡す。IDisposable な singleton はプロバイダの `Dispose()` が破棄する。

## Common Development Patterns

### Adding a New Command
1. `Services/Commands/` にハンドラ (`XxxCommandHandler`) を作る。共通インターフェースは無い
2. エンジンかキャッシュに触れるなら `SpeechLock.RunAsync` で囲む
3. `AppServiceProvider` に `[Singleton(typeof(XxxCommandHandler))]` を追加し、`CommandRouter` のコンストラクタで受け取って switch に分岐を足す
4. 値を取らない `--xxx` なら `ArgumentParser.FlagArgs` にも追加する (`CommandLineConfigurationTests`)

読み上げ系の機能は独立したハンドラを作らず、`TextToSpeechProcessor` に足すか `AudioExportService` のような単機能ヘルパに分ける。

### Reusable Utilities (新規実装の前に探すこと)
- `ConsoleHelper` (色付き出力・stdout/stderr の振り分け)、`ErrorHandlingUtility` (例外 → 終了コード)、`RetryPolicyService`、`CancellationManager`、`InterProcessLock` / `SpeechLock`、`OpusCodec`、`ConfigurationManager` / `ConfigurationValidationService`
- `IClock`: `DateTime.UtcNow` を直接呼ばずこれ経由

## Pre-Commit Hooks

`.pre-commit-config.yaml` で `git commit` 時に dotnet-build・dotnet-format・ファイル系チェックが走る。テストは走らないので、コミット前に `task check` を実行する。`dotnet-format` で失敗したら `task format` をやり直す。`--no-verify` で飛ばさない。

## Command Interface

```bash
VoicevoxRunCached <text> [options]
VoicevoxRunCached -- <text> [options]       # <text> がコマンド名・オプションと同じ文字列でも本文として読む
VoicevoxRunCached speakers
VoicevoxRunCached devices [--full] [--json]
VoicevoxRunCached --init [--speaker <id>]   # フィラーを生成する話者 (default: VoiceVox.DefaultSpeaker)
VoicevoxRunCached --clear
VoicevoxRunCached --test [options]

Options:
--speaker, -s <id>    Speaker ID (default: VoiceVox.DefaultSpeaker)
--speed <value>       Speech speed (default: 1.0)
--pitch <value>       Speech pitch (default: 0.0)
--volume <value>      Speech volume (default: 1.0。フィラーの音量にも掛かる)
--no-cache            Skip cache usage (neither read nor write)
--cache-only          Use cache only, don't call API
--refresh             Re-synthesize all segments and overwrite the cache (--no-cache / --cache-only とは併用不可)
--out, -o <path>      Save output audio to file (.wav or .opus)
--no-play             Do not play audio (useful with --out)
--verbose             Show playback time and total execution time
--max-wait <seconds>  先の処理を待つ上限。超えたら終了コード 9 (default: Lock.MaxWaitSeconds。未設定 = 無制限)
--log-level <level>   trace|debug|info|warn|error|crit|none
--log-format <fmt>    simple|json
--help, -h            Show help message
```

## Code Standards

- .NET 10 / C# 14 (primary constructors, collection expressions 等)。Nullable 有効
- バージョンは `src_dotnet/Directory.Build.props` の `<Version>` 1 か所 (AssemblyVersion / FileVersion は書かない。`task publish:zip` と `_publish_zip.cmd` もここから読む)
- ドメインエラーは `VoicevoxRunCachedException` に `ErrorCode` を付けて投げる。framework 例外は無理に隠さない
