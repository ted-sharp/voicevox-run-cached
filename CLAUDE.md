# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

VOICEVOX REST API のラッパー C# Console アプリ (VOICEPEAK・CeVIO AI にも対応)。テキストを文単位に分割し、SHA256 キーで Ogg Opus キャッシュしながら逐次合成・再生する。主な用途は Claude Code の Hooks からの通知の読み上げ。

## 文書の書き分け

同じ説明を 2 か所に書かない (片方だけ直されて食い違う)。情報の種類ごとに置き場所を 1 つに決める。

| 文書 | 書くこと | 書かないこと |
|---|---|---|
| CLAUDE.md (この文書) | 守ること (壊すと困る約束) を 1 項目 1〜2 行で。対象のクラスと、回帰を検知するテスト名を添える | 理由の詳しい説明・経緯・使い方 |
| コードのコメント (XML doc の `<remarks>`・行コメント) | なぜそうしたか。読む人がその場所で必要とする理由 (実機で確かめた挙動・やめた案と理由も) | 経緯 (いつ・誰が・何を直したか) |
| テストのコメント | 何の回帰を検知するか (`// 回帰: …`) | |
| README.md | 利用者向け: 使い方・オプション・設定・終了コード・仕組み・トラブルシューティング | 実装の詳細 |
| RELEASE_NOTES.md | 1 項目 1 行・利用者から見た変化だけ | 経緯・実装の詳細 |
| doc/RELEASE.md | リリース手順 | |
| git log | 経緯 (何を・なぜ変えたか) | |

挙動を変えたら次をそろえて直す:

1. コードのコメント (理由)
2. CLAUDE.md の「守ること」とテスト名 (約束を足した・変えたとき)
3. README.md (利用者から見えるとき。オプション表・終了コード表・設定例のコメントも)
4. RELEASE_NOTES.md の Unreleased (利用者から見えるとき)

CLAUDE.md に理由を書きたくなったら、そのクラスのコメントに書いて、ここにはクラス名を書く。特定のクラスに属さない規則 (全体に掛かるコーディング規約など) だけは、ここに理由を一言添える。

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
task install                  # publish して %LOCALAPPDATA%\Programs\VoicevoxRunCached に入れる (INSTALL_DIR=... で変更。設定・cache・logs は残す)
task uninstall                # 同上の exe と appsettings.json を消す
task clean                    # bin / obj / publish を削除
task fix-all -- -Fast         # _fix-all.ps1 (引数なしで tests + SonarQube + ReSharper まで)
task hooks:install            # コミット時のチェック (.githooks) を有効にする (clone 後に 1 回)
```

- テストは .NET 11 + Microsoft.Testing.Platform のため `dotnet test` では動かない (`task test` を使う)。1 クラスだけなら `dotnet run --project src_dotnet/VoicevoxRunCached.Tests -- --treenode-filter "/*/*/クラス名/*"`
- リリース手順は `doc/RELEASE.md`
- コマンドとオプションは README の「コマンドリファレンス」と `--help` (`ArgumentParser.ShowUsage`)

## Request Flow

1. `Program.cs` → `ApplicationBootstrap` (設定の読み込み・検証、Serilog) → `CommandRouter.ExecuteCommandAsync` の switch → 各ハンドラ
2. 読み上げは `TextToSpeechProcessor`: `SpeechLock` の中で、`TextSegmentProcessor` で文に分割 → `AudioCacheManager` でキャッシュ (Opus) を引く
3. `SegmentProducer` (producer) と `AudioPlayer.PlayStreamingAsync` (consumer) を `Channel<TextSegment>` で並走させる。producer はキャッシュ済みを WAV にデコードして流し、未キャッシュ分は `ISpeechEngine` で 1 件ずつ合成して流す
4. `--out` 指定時は `AudioExportService` が WAV / Opus で保存

## 守ること

理由は各クラスのコメントにある。

### プロセス間ロック (`SpeechLock` / `InterProcessLock`)
- エンジンかキャッシュに触れるコマンド (読み上げ・`--test`・`--init`・`speakers`・`--clear`) は `SpeechLock.RunAsync` で囲む。囲まないのは `devices` だけ
- ロックは `%TEMP%\VoicevoxRunCached.speech.lock` を `FileShare.None` で開くファイルロック。名前付き Mutex は使わない
- ロックを**解放する前に**全エンジンの `StopIfNotKept` を呼ぶ — 各テストクラスの `EngineStartedByUs_IsStoppedBeforeReleasingLock`
- `--clear` もロックの中で消す — `CacheCommandHandlerTests`
- 待つ上限は `Lock.MaxWaitSeconds` / `--max-wait` (`SpeechLock.ResolveMaxWait`。未設定 = 上限なし、0 = 待たない)。超えたら `LOCK_001` (終了コード 9)、`CommandRouter` はスタックトレースなしの Warning — `InterProcessLockTests` / `SpeechLockTests`
- テストは本物のロックファイルを使わない (`TestLockFile.CreateSpeechLock` / `AppServiceProvider.SpeechLockPath`)。既定が共有のファイルであることは `AppServiceProviderTests.SpeechLock_UsesSharedLockFileByDefault`
- ロックより前に走る書き込み確認 (`DirectoryValidation.BeWritableDirectory`) のファイル名は毎回一意にし、`FileOptions.DeleteOnClose` で消す

### 合成エンジンの切り替え (`ISpeechEngine` / `SpeechEngineProvider`)
- 読み上げ・`--init`・`speakers`・`SpeechLock` は `ISpeechEngine` だけを使い、`SpeechEngineProvider.Get(SpeechEngineKind)` で取り出す
- エンジンは `Synthesis.Engine` か `--engine <名前>` (大文字小文字を区別しない。数字は不可)
- 話者は文字列。`SpeechEngineProvider.NormalizeSpeaker` でエンジンごとに検証・正規化する (キャッシュキーに入るので同じ話者は同じ文字列に)。省略時は各エンジンの `DefaultSpeaker` (空なら `InvalidArguments`)。`--engine` と `--speaker` の順は問わない — `ArgumentParserTests`
- エンジンごとの話速・音高の範囲は `SpeechEngineProvider.ValidateParameters` でロックの前に検証する
- エンジンを足すときは `SpeechEngineKind`・`SpeechEngineProvider` (コンストラクタ・`All`・`Get`・`DefaultSpeaker`・`NormalizeSpeaker`・`ValidateParameters`)・`AppServiceProvider` に足す。設定クラスは `IEngineFillerSettings` を実装し、バリデーターで `Include(new EngineFillerSettingsValidator())`。キャッシュキーは変更不要 (`CacheKeyGenerator.SpeakerKey`)

### VOICEVOX エンジンの起動 (`VoiceVoxEngineManager`)
- 起動済みか確かめてから起動する。外部で起動済みのエンジンには触れない。`KeepEngineRunning: true` なら常駐させる
- `EnginePath` が空なら VVPP のエンジン (`FindVoicevoxVvppEngine`。UUID で探す) を最初に、エディタ (`{製品名}.exe`) を最後に探す — `VoiceVoxEngineManagerTests.VvppEngine_*` / `EngineRelativePaths_FindEngineBeforeEditor`
- `UseShellExecute = true` + `WindowStyle = Hidden` で起動する — `StartEngine_DoesNotInheritHandles`
- 起動したプロセスが終わったら、同じ実行ファイルのプロセスが無ければタイムアウトを待たずに失敗にする。あれば起動を待ち、自分が起動したものとして扱わない (止めない)。判定は CeVIO と共通の `ProcessInstances.IsAnotherInstanceRunning` (名前でなくフルパスで比べる) — `ProcessInstancesTests` / `EngineExitsDuringStartup_FailsWithoutWaitingForTimeout` / `EngineHandedToStartingInstance_KeepsWaiting` / `StartingInstanceExits_FailsWithoutWaitingForTimeout`
- 読み上げでエンジンに接続するのは合成が必要になったときだけ。準備 (`ISpeechEngine.PrepareAsync`) は `SegmentProducer` が最初に合成するときに 1 回呼ぶ — `TextToSpeechProcessorTests` / `SegmentProducerTests`
- 準備の失敗は文ごとの失敗にせず終了コード 3 / 1。producer は投げずに `SynthesisResult.PreparationError` で返し (以降は合成せずキャッシュ済みの文だけ流す)、`TextToSpeechProcessor` が再生を終えてから投げる — `SegmentProducerTests.PrepareFailure_IsReturnedAndCachedSegmentsStillFlow`

### VOICEVOX API (`VoiceVoxApiClient`)
- 呼び出しは直列 (`SegmentProducer.ProduceAsync` のループで 1 件ずつ await)。クライアント自身にロックは無い。並列化するなら実機で確かめてクライアント内に lock を入れる
- `/initialize_speaker` は 1 回の読み上げで 1 回、`skip_reinit=true` を付ける。`ConnectionTimeout` で打ち切らない
- Speed/Pitch/Volume は `ApplyVoiceParameters` が `/audio_query` の返却 JSON を書き換えて渡す。値は `CacheKeyGenerator.NormalizeParameter` で丸める (`Math.Round` は使わない)
- タイムアウトは `SendAsync` がリクエストごとに掛ける (`HttpClient.Timeout` は無限)。タイムアウトは `ApiTimeout` — `VoiceVoxApiClientTimeoutTests`
- エラー変換は `ExecuteApiAsync` の `MapToApiException` / `MapToErrorCode` に集約する
- リトライは `RetryPolicyService` (自前。Polly は使わない): 500/502/503/504/429 と I/O・ソケットエラーだけ。タイムアウトとステータスの無い `HttpRequestException` はリトライしない — `RetryPolicyServiceTests`

### VOICEPEAK (`VoicepeakEngine`)
- `voicepeak.exe` を 1 文ごとに起動し、一時 WAV (`%TEMP%\vrc_voicepeak_*.wav`。読んだら消す) に出させる。本文は 1 回 140 文字まで
- 話者は `ナレーター名[:感情=値,...]` (値 0〜100)。`NormalizeSpeaker` が感情を名前順にし、値 0 を除く — `VoicepeakEngineTests.NormalizeSpeaker_*`
- `PrepareAsync` は exe の有無だけを見る。合成の失敗はすべて `IsEngineUnavailable` を true にして残りを合成しない — `VoicepeakEngineTests.NoOutputFile_FailsAndStopsRemainingSynthesis`
- 引数は `ArgumentList` に `--say=本文` のように `=` 付きで渡す
- `--speed` ×100、`--pitch` ×2000。音量は `AudioConversionUtility.ApplyGain` で WAV に掛ける — `VoicepeakEngineTests.ApplyGain_ReadsWavWithJunkChunk`
- タイムアウト (`Voicepeak.TimeoutSeconds`) でプロセスツリーを止めて (終了を待ってから一時 WAV を消す) `ApiTimeout`。exe が無い・起動できないは `EngineNotAvailable`
- 標準入出力はリダイレクトし (標準入力は閉じる)、終了を待つ。`StopIfNotKept` は何もしない

### CeVIO AI (`CevioEngine` / `CevioHostManager` / `CevioRemoteService`)
- COM に触れるのは `CevioRemoteService` だけ (遅延バインディング)。同期の COM 呼び出しは `CevioEngine.RunComAsync` で包む。テストは `FakeCevioRemoteService`
- ホストの起動に `ServiceControl2V40.StartHost` を使わない。`CevioHostManager` が `UseShellExecute = true` で起動する — `CevioHostManagerTests.StartHost_DoesNotInheritHandles`。自分で起動したホストだけを `KeepHostRunning: false` のとき閉じる
- 起動したプロセスが終わったら、同じ実行ファイルのプロセスがあれば起動を待ち (閉じない)、無ければすぐ `EngineNotAvailable` — `CevioHostManagerTests.HostHandedToStartingInstance_WaitsAndIsNotClosed` / `HostExitsDuringStartup_FailsWithoutWaitingForTimeout` / `StartingInstanceExits_FailsWithoutWaitingForTimeout`
- `CloseHost` も `SynthesisTimeoutSeconds` で待つのをやめる — `CevioHostManagerTests.CloseHostNoResponse_GivesUpWithinTimeout`
- キャストは `PrepareAsync` で `AvailableCasts` と照合してから `Talker.Cast` に代入する (無ければ `InvalidArguments`) — `CevioEngineTests.Prepare_UnknownCast_ThrowsInvalidArgumentsWithoutSelectingIt`
- 話者は `キャスト名[:感情=値,...]` (0〜100)。`NormalizeSpeaker` が感情を名前順に並べる。感情を書けば書いていない感情は 0、書かなければキャストの既定値。合成のたびにキャスト・話速・音高・全感情を設定し直す — `CevioEngineTests`
- 換算は `ToCevioSpeed` / `ToCevioTone`。0〜100 を超える値は丸めず `CevioEngine.ValidateParameters` でロックの前に `InvalidArguments` — `CevioEngineTests.ValidateParameters_*`。音量は Volume 50 固定 + `ApplyGain`
- 合成の失敗: ホストが閉じていれば `EngineNotAvailable`、動いていれば `AudioGenerationFailed`。COM の例外は `EngineNotAvailable` — `CevioEngineTests.ComError_IsEngineNotAvailable`
- 合成の前に `IsHostStarted` を確かめ、COM の呼び出しは `SynthesisTimeoutSeconds` で打ち切って `ApiTimeout` にする。Ctrl+C も完了を待たずに返す — `CevioEngineTests.Synthesize_NoResponse_*` / `Synthesize_HostClosedBeforeSynthesis_DoesNotCallCevio`
- 設定は `Cevio` セクション。`MaxFillersPerWait` の省略時は上限なし。`DefaultSpeaker` は空を許す (`-s` 必須)

### 合成・再生パイプライン (`SegmentProducer` / `AudioPlayer`)
- キャッシュ由来の音声は producer が `AudioCacheManager.DecodeOrDelete` で WAV にデコードしてから流す (`Task.Run` で再生と並行)。読めなければ未キャッシュとして合成し直し、`--cache-only` なら失敗として数える — `SegmentProducerTests` / `AudioCacheManagerIntegrationTests` / `TextToSpeechProcessorTests.CorruptedCache_IsResynthesizedAndRewritten`
- 1 回の入力に同じ文が複数あれば、2 回目以降は 1 回目の結果 (失敗なら失敗) を使い、保存も 1 回だけ — `SegmentProducerTests`
- `ISpeechEngine.IsEngineUnavailable` の失敗の後は、残りの未キャッシュ分を呼ばずに失敗として数え、channel に流さない (キャッシュ済みは流す)。`--init` も同じ — `SegmentProducerTests` / `FillerManagerTests`
- 片側の例外は `WhenAllOrCancelOnFaultAsync` でもう一方も止める — `WhenAllOrCancelOnFaultTests`
- キャッシュ保存は fire-and-forget し、producer の `finally` で待つ。`writer.TryComplete()` は `WhenAll` より**前** — `ChannelProducerOrderingTests`
- フィラーは次のセグメントが `FillerStartDelayMs` 以内に来なければ挟む。読み上げと同じエンジン・話者の声で、音量は `--volume` を掛ける (話速・音高は既定のまま)。`--init` は `Filler.Enabled` に関係なく生成し、`Enabled` は再生にだけ効く。未生成の話者では鳴らさず、Warning は 1 実行 1 回
- フィラーの文と回数はエンジンごと (`FillerTexts` は空なら `Filler.FillerTexts` で、`--init` と再生の両方が使う。`MaxFillersPerWait` は null = 上限なし (JSON に null と書く)、0 = 鳴らさない、VOICEPEAK の省略時は 2) — `FillerWaitBudgetTests` / `FillerManagerTests.EngineFillerTexts_*` / `ApplicationBootstrapTests.MaxFillersPerWait_IsLoaded`
- フィラーの失敗は本編を止めない (`AudioPlayer.TryPlayFillerAsync`)。ただし再生タイムアウト (`TimeoutException`) は投げ直す

### 音声形式
- `TextSegment.AudioData` は形式 (WAV / Ogg Opus) もサンプルレートも混在する (内訳は `TextSegment.AudioData` のコメント)。読むときは必ず `AudioFormatDetector.CreateWaveStream` で判定する (`WaveFileReader` 決め打ち禁止)。再生はセグメントごとに `Init` する。`AudioExportService` は 48kHz mono 16bit に揃えて結合する
- エンコードは `OpusCodec.EncodeWav` (`Cache.OpusBitrateKbps`、キャッシュ・フィラー・`--out .opus` 共通)。ビットレートはキャッシュキーに含めない
- デコード (`OpusCodec.DecodeToWav`) は **48kHz 固定** — `OpusCodecTests`

### キャッシュ (`AudioCacheManager` / `DiskCacheStore` / `CacheEvictionPolicy`)
- キーは `CacheKeyGenerator.Compute`。**生成ロジックの変更禁止** (既存キャッシュが全て無効になる) — `AudioCacheManagerTests.ComputeCacheKey_Voicevox_MatchesKeyBeforeEngineSupport`。`{key}.opus` と `{key}.meta.json` のペアで保存
- meta.json の `SpeakerId` は数値も文字列として読む — `DiskCacheStoreTests.LoadAsync_MetadataWithNumericSpeakerId_Hits`
- ユーザー指定値を `VoiceRequest` に乗せて `ProcessTextSegmentsAsync(segments, request)` に渡す (既定値で引くと永遠にミスする)
- 古さは最終利用日時 = meta.json の更新日時 (LRU)。`SaveAsync` が設定し `LoadAsync` がヒットのたびに更新する
- `RunMaintenance` は新規保存のあった実行の最後に `SegmentProducer` が 1 回だけ呼ぶ
- `SaveAsync` は meta.json を先に、`.opus` は `.opus.tmp` に書いてから `File.Move` で置く。フィラーも同じ
- `.opus.tmp` は `--clear` と maintenance の `DeleteTemporaryFiles` が消す
- 削除の失敗: 読み込み時・eviction は best-effort で Warning、`--clear` は `CacheException`。容量の掃除は消せなかった分を合計から引かず、次に古いものを消す — `CacheEvictionPolicyTests.CleanupByMaxSize_UndeletableEntry_DeletesNextOldest`
- `--refresh` = 読まずに合成して上書き。`--no-cache` = 読みも書きもしない
- `IsMetadataValid` は Text 非空・Speaker 非空 (`"0"` は実在)・CreatedAt 設定済みのみ
- v2 以前の `.mp3` は読まず、maintenance (`DeleteLegacyFiles`) と `--clear` (`DeleteAll`) で消す
- 保存先は既定で実行ファイルと同じディレクトリ (`UseExecutableBaseDirectory: true`)

### フィラーの掃除 (`FillerManager`)
- 最終利用日時 = フィラーのファイルの更新日時。読み上げのたびに `TextToSpeechProcessor` が合成・再生の**前**に `FillerManager.MarkUsed` でその話者の今の文の分を更新する (`Filler.Enabled: false` なら更新しない) — `TextToSpeechProcessorTests.NoPlay_UpdatesFillerLastUsed`。`--init` は生成済みの分も更新する
- `--init` の最後に、最終利用から `Cache.ExpirationDays` を過ぎたフィラーを消す (best-effort)。対象はキーの形 (16 進 64 文字) の `.opus` だけ — `FillerManagerTests.Init_DeletesFillersOfOldTexts` / `Init_KeepsCurrentAndRecentlyUsedFillers` / `Init_DoesNotDeleteNonFillerFiles`
- `--init` は生成の前に、キーの形の `.opus.tmp` を消す (best-effort) — `FillerManagerTests.Init_DeletesLeftoverTemporaryFiles`
- 記録を始めた印 (`FillerManager.UsageTrackingMarkerFileName`) が無ければ、消さずに全部の日時を今にして印を作る — `FillerManagerTests.Init_FirstCleanup_KeepsFillersCreatedBeforeTracking`

### `--out` (`AudioExportService`)
- 書き込めるか (フォルダと、既存のファイルを置き換えられるか) は `EnsureWritableOutput` でロックを待つ前に確かめる (`AUDIO_*`、終了コード 5) — `TextToSpeechProcessorTests.OutPathNotWritable_FailsBeforeSynthesis` / `AudioExportServiceTests.EnsureWritableOutput_*`
- `{out}.tmp` に書いてから置き換え、失敗しても `.tmp` を残さない (消せなくても Warning で、元の失敗の理由を上書きしない) — `AudioExportServiceTests.Export_ReplacesExistingFileWithoutLeavingTemporaryFile`

### テキスト分割 (`TextSegmentProcessor`)
- `。！？!?` と改行で区切る。半角 `.` は直後が空白か文末のときだけ
- 文字も数字も含まないセグメントは前に付ける (`maxSegmentLength` を超えるなら付けない)。判定は `Rune.IsLetterOrDigit`
- `maxSegmentLength` (既定 100) に達したら後半の最後の `、` `，` か空白の直後で切る (`FindSplitPosition`)。無ければ上限で切るが high surrogate は割らない。半角 `,` は区切りにしない
- 空白のみ・空のセグメントは除く

### CLI 引数 (`ArgumentParser`)
- 不正な値 (数値でない・負の speaker/volume・0 以下の speed) は `InvalidArguments`。範囲判定は F2 に丸めた後の値
- 値の指定漏れ・不明なオプション・余分な引数は `InvalidArguments` (全コマンド共通。ロックの前に検証する) — `ArgumentParserTests` / `CommandRouterTests`
- コマンド名は大文字小文字を区別する — `CommandRouterTests.CommandName_IsCaseSensitive`
- `--log-level` / `--log-format` / `--Section:Key=値` は `TrySkipConfigurationArgs` で値ごと読み飛ばす
- オプションを足すときは `ApplyOptions` に。値を取らないフラグは `FlagArgs` にも — `CommandLineConfigurationTests`
- 本文は `PreprocessArgs` にかけない。本文の位置にオプションの形 (`IsOptionLike`) があれば `InvalidArguments`。先頭が `--` なら次を本文にする (`ConfigurationManager` も区切りと本文を設定から外す。両方そろえる)
- speaker の存在確認は `PrepareAsync` でだけ行う。`InvalidArguments` は `CommandRouter` でスタックトレースなしの Warning

### 終了コード・キャンセル
- `ErrorCodes.GetExitCode` が prefix から決める: ENGINE=3, CACHE=4, AUDIO=5, API=6, CONFIG=7, LOCK=9, その他=1。一部失敗は 8、全部失敗 (失敗数 = セグメント数) は `AudioGenerationFailed` (5) で、最初の失敗の理由を添える (`SynthesisResult.ToAllFailedException`) — `TextToSpeechProcessorTests.CorruptedCache_CacheOnly_*` / `SynthesisResultTests`。README の「終了コード」表と揃える
- **終了コード 2 は使わない** (Hooks のブロッキングエラー) — `ErrorCodesTests`
- Ctrl+C は 130。`CommandRouter` と `Program` の両方で返す。`CancellationManager` は 1 回目だけ握る
- `catch (Exception)` の前には `catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }` を置く (握ると Ctrl+C が別のエラー扱いになったり、読み込み途中のキャッシュを壊れたものとして消したりする)

### ログと出力
- インスタンスクラスはコンストラクタで `ILogger` を受け取る。`Serilog.Log` の static 呼び出しは `LoggingManager`・`Program` / `ApplicationBootstrap`・static utility (`AudioConversionUtility`, `OpusCodec`) だけ
- DI 登録クラスのコンストラクタで `ILogger` / `IClock` などを省略可能 (`= null` + 既定値) にしない (理由は `AppServiceProvider` のコメント)。DI 外で組む `DiskCacheStore` / `CacheEvictionPolicy` も必須。テストでは `NullLogger.Instance` / `SystemClock.Instance`
- `ILogger` = ログ (コンソールへは**すべて stderr**)。`ConsoleHelper` = ユーザー向け出力 (`Console.WriteLine` を直接使わない)。stdout は結果・進捗 (`WriteSuccess` / `WriteLine`)、stderr は失敗理由 (`WriteError` / `WriteWarning` / `WriteErrorDetail`)。`ConsoleHelper.WriteWarning` をログ代わりにしない
- 色は出力先がリダイレクトされていないときだけ。リダイレクト時は `ApplicationBootstrap.ConfigureRedirectedOutputEncoding` (`Console.OutputEncoding` は使わない)
- レベル: Error = ユーザー対応が必要、Warning = 想定外だが続行、Information = 主要な節目、Debug = 詳細フロー
- **`DEBUG` / `if DEBUG` のような prefix・条件付きログは付けない**

### 設定 (`appsettings.json` → `AppSettings`)
- セクションを足したら `ApplicationBootstrap.BuildAppSettings` (セクションごとに Bind) と `ArgumentParser.ConfigurationSections` にも足す — `ApplicationBootstrapTests.LockMaxWaitSeconds_IsLoaded` / `ArgumentParserTests.ConfigurationOverride_AcceptsEverySection`
- `Validators.cs` (FluentValidation) で検証。読み込み・バインドの失敗も `ConfigurationException` (終了コード 7)。`Test` は `--test` のときだけ検証
- `Cache.Directory` と `Filler.Directory` が同じならエラー (`Filler.Enabled` に関係なく検証する)
- 検証の前に相対パスを絶対パスにし、`VoiceVox.BaseUrl` の末尾 `/` を取る
- 配列のプロパティ (`FillerTexts`) に初期値を入れない (ConfigurationBinder が追記する)
- Serilog は `LoggingManager` がプログラムで構成する (`Serilog:*` は読まない)

### オーディオデバイス
- `OutputDevice = -1` で OS 既定、`0+` で WaveOut の index
- バッファは `WavePlayerManager.GetBufferMilliseconds` で換算する。CA1416 を避けるため `Directory.Build.props` で全プロジェクトを Windows 専用と宣言している
- warm-up は `AudioDeviceManager.StartPreparation` で始める (コンストラクタで始めない)。読み上げは `--no-play` 以外のときロック取得直後に `AudioPlayer.BeginDevicePreparation`

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
│       ├── Synthesis/      # TextToSpeechProcessor, SegmentProducer, SpeechLock, ISpeechEngine, SpeechEngineProvider,
│       │                   # VoicevoxEngine, VoicepeakEngine, VoiceVoxApiClient, VoiceVoxEngineManager, TextSegmentProcessor, FillerManager, AudioExportService
│       │                   # CevioEngine, CevioHostManager, CevioRemoteService (COM)
│       ├── Commands/       # SpeakerCommandHandler, DeviceCommandHandler, InitCommandHandler, CacheCommandHandler
│       └── Infrastructure/ # ApplicationBootstrap, ConfigurationManager, LoggingManager, RetryPolicyService, CancellationManager,
│                           # InterProcessLock, ProcessInstances, ConsoleHelper, ErrorHandlingUtility, ProgressSpinner, IClock, AssemblyVersion
└── VoicevoxRunCached.Tests/  # TUnit
```

DI は Jab (Source Generator)。`Services/AppServiceProvider.cs` の `[Singleton(typeof(Xxx))]` に登録を集約する。登録漏れは JAB0002、循環は JAB0008 でビルドエラー。設定・`ILogger`・`IClock` は `Instance = nameof(...)` でプロバイダのプロパティから渡す。IDisposable な singleton はプロバイダの `Dispose()` が破棄する。

## Common Development Patterns

### Adding a New Command
1. `Services/Commands/` にハンドラ (`XxxCommandHandler`) を作る。共通インターフェースは無い
2. エンジンかキャッシュに触れるなら `SpeechLock.RunAsync` で囲む
3. `AppServiceProvider` に `[Singleton(typeof(XxxCommandHandler))]` を追加し、`CommandRouter` のコンストラクタで受け取って switch に分岐を足す
4. 値を取らない `--xxx` なら `ArgumentParser.FlagArgs` にも追加する (`CommandLineConfigurationTests`)
5. コマンドの引数は `ArgumentParser` でロックの前に検証し、不明なものは `UnexpectedArgument` にする (引数を取らないなら `EnsureNoCommandArguments`)

読み上げ系の機能は独立したハンドラを作らず、`TextToSpeechProcessor` に足すか `AudioExportService` のような単機能ヘルパに分ける。

### Reusable Utilities (新規実装の前に探すこと)
- `ConsoleHelper`、`ErrorHandlingUtility`、`RetryPolicyService`、`CancellationManager`、`InterProcessLock` / `SpeechLock`、`ProcessInstances`、`OpusCodec`、`ConfigurationManager` / `ConfigurationValidationService`、`DirectoryValidation.BeWritableDirectory`
- `IClock`: `DateTime.UtcNow` を直接呼ばずこれ経由

## Pre-Commit Hooks

`.githooks/pre-commit` が `git commit` 時に build と、ステージした `.cs` の整形確認を並行して走らせる (ビルドに関係するファイルをステージしていなければ何もしない)。clone 後に `task hooks:install` で有効にする。整形差分で止まったら `task format` → `git add` してコミットし直す。フックは using の並び順とテストを見ないので、コミット前に `task check` を実行する。`--no-verify` で飛ばさない。

## Code Standards

- .NET 11 / C# 15 (primary constructors, collection expressions 等)。Nullable 有効
- バージョンは `src_dotnet/Directory.Build.props` の `<Version>` 1 か所
- ドメインエラーは `VoicevoxRunCachedException` に `ErrorCode` を付けて投げる。framework 例外は無理に隠さない
