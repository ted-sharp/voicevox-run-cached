# VoicevoxRunCached リリースノート

## Unreleased

- アーキテクチャ/リファクタリング:
  - `SegmentProducer` を `TextToSpeechProcessor` から切り出し、合成 producer の責務を分離
  - `IClock` 抽象を導入し、時刻取得のテスト容易性を向上
  - キャッシュ層に `CacheErrorMapper` を集約し、I/O 例外 → `VoicevoxRunCachedException` への変換を一元化
  - `AudioCacheManager` を `Services/Cache/` 配下に移動
  - エンジン子プロセスを kill によって確実に停止
  - 例外ハンドリング (`ErrorHandlingUtility`) と Async 呼び出しの統一
- 改善/修正:
  - キャッシュ I/O エラー時に API へ fallback する経路を追加
  - 浮動小数粒度バグ修正 (キャッシュキーの揺らぎ防止)
  - `ConsoleHelper` のログ二重出力を解消
  - MediaFoundation API 呼び出しを統一、`WavePlayer` の状態二重持ちを解消
  - 不要なフォールバック・防御コード・死コードを一掃
  - `--out` でキャッシュ済みセグメント (MP3) と新規合成 (WAV) が混在すると出力に失敗する / MP3 を .wav として書き出す問題を修正
  - speaker 0 のキャッシュが常に無効扱いで毎回削除・再合成されていた問題を修正
  - 半角 `.` は直後が空白か文末のときだけ文末として扱う (`3.14` `Node.js` を分割しない。該当テキストのキャッシュは一度だけ作り直しになる)
  - 再生タイムアウトを「音声長 + 30 秒」に変更 (長文 × 低速で正常再生がタイムアウトしていた)
  - **`--speed` / `--pitch` / `--volume` が音声に反映されていなかった問題を修正** (VOICEVOX は `/audio_query` のクエリ指定を無視するため、返却 JSON の `speedScale` 等を書き換えて合成する。値はキャッシュキーと同じ F2 粒度に丸める)
    - 旧版で作られた既定値以外のキャッシュ (中身は既定値の音声) は自動で無効化・再合成される。既定値のキャッシュはそのまま使われる
  - CLI 引数の不正値 (非数値・負の speaker/volume・丸めると 0 以下になる speed・値なし・値の位置に `--xxx`) をエラーにする
  - 再生中の Ctrl+C がタイムアウトとして報告されることがある問題を修正
  - フラグ指定のたびに出ていた `Unknown option 'true'` 警告を解消
  - キャッシュ eviction を保存ごとの全走査から「新規保存のあった実行の最後に 1 回」に変更し、未使用だった期限切れ一括削除も有効化 (判定はファイル更新日時で行い meta.json を読まない)
  - アセンブリバージョンが 1.6.0.0 のまま固定されていた問題を修正
- 削除:
  - メモリキャッシュ層と `Cache.MemoryCacheSizeMb` 設定 (1 実行ごとに終了する CLI では効果がないため。既存設定に残っていても無視される)
- 開発環境:
  - `Taskfile.yml` を追加 (`task build` / `task test` / `task check` / `task publish:zip` 等)
  - バージョンを `src_dotnet/Directory.Build.props` の `<Version>` に一元化
  - コミット時のテスト実行を廃止 (`task check` で手動実行)

## v2.0.0 - 2026-01-17

- プラットフォーム/フレームワーク:
  - .NET 10 への移行（C# 14 機能を活用）
  - ソリューション構造を `.sln` から `.slnx` に変更
- アーキテクチャ/リファクタリング:
  - `Program.cs` を複数のサービスに分割してモジュール化
  - `ApplicationBootstrap` による依存性注入（DI）設定を導入
  - `CommandRouter` によるコマンド処理の統一化
  - 各機能を独立したサービスとして実装
- 新規使用ライブラリ:
  - `FluentValidation` を追加（設定値の検証機能）
  - `Serilog` を追加（構造化ロギング、コンソール/ファイル/Seq対応）
  - `Polly` を追加（リトライポリシーによるエラー耐性向上）
  - `Microsoft.Extensions.Caching.Memory` を追加（メモリキャッシュ機能）
  - `BenchmarkDotNet` を追加（パフォーマンス測定機能）
  - `Aloe.Utils.CommandLine` を追加（コマンドライン引数処理の改善）
- CLI/コマンド:
  - `--benchmark` オプションを追加（パフォーマンス測定機能）
- 改善/修正:
  - キャッシュの JSON エスケープ処理を変更(日本語のまま出力)
  - `--no-play` オプションの処理でもセグメントとキャッシュを作成

## v1.6.0 - 2025-08-11

- CLI/コマンド:
  - `devices` コマンドを追加（`--full`/`--json` オプション対応）
  - `--out`/`--no-play` を追加し、WAV/MP3への保存と再生抑止をサポート
  - `--log-level`/`--log-format` を追加してログ出力を制御
- 設定/構成:
  - `Audio.OutputDeviceId` を追加（WASAPIエンドポイントIDの優先指定）
  - ログ設定（`Logging.Level`/`Logging.Format`）を追加
- オーディオ/変換:
  - `MediaFoundationManager` を導入し、MP3変換を改善
  - MP3変換失敗時のWAVフォールバックを追加
- 安定性/UX:
  - 待機中のキャンセル処理およびキャンセル時の後処理を追加
  - キャッシュ関連のロック処理を改善
  - ロガーを追加し、出力設定を拡充

## v1.5.0 - 2025-08-09

- キャッシュ: `Lock.EnterScope` を用いたスレッド安全化とI/O安定性向上
- API: 軽量リトライ追加で一時的な接続失敗への耐性を改善
- エンジン: 作業ディレクトリ設定で起動安定性を改善
- クリーナップ: 二重Disposeの除去、軽微なメッセージ整備

## v1.4.0 - 2025-07-29

- AudioPlayerでの統合フィラー再生実装
- VoiceVoxEngineManagerの検索パス拡張

## v1.3.0 - 2025-07-24

- インテリジェントフィラー（自然な間つなぎ音声の自動再生）
- エンジン自動起動・管理（VOICEVOX/AIVIS Speech両対応、永続化・タイムアウト保護）
- 詳細な実行時間表示（`--verbose`）
- パフォーマンス大幅向上（`127.0.0.1`化、エンジン再利用）
- 音声品質・安定性向上、バグ修正多数
- 新設定項目・CLIオプション追加
- 設定ファイルのBaseURL変更・新項目追加（破壊的変更）

## v1.1.0 - 2025-07-23

- 音声品質改善（デバイス準備機能、暖気運転）
- Audio設定追加
- 初回音切れ・USBオーディオ安定性バグ修正

## v1.0.0 - 2025-07-22

- 初回リリース
- VOICEVOXによるテキスト音声変換
- WAVキャッシュ・CLI操作・Windows対応
