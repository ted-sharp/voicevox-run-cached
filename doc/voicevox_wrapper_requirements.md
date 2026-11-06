# VOICEVOX REST API ラッパー要件定義書（最終仕様）

## プロジェクト概要

- **タイトル**: VOICEVOX REST API ラッパーConsoleアプリ（インテリジェントキャッシュ付き）
- **概要**: VOICEVOX/AIVIS Speech を対象に、テキスト→音声変換・再生・キャッシュ・エンジン自動起動を行うWindows向けC#コンソールアプリ

## 実装サマリ

- **キャッシュ**: 文単位セグメント化 + Ogg Opus 保存 + 期限/容量管理
- **再生**: NAudio (`WaveOutEvent`) ベース、`Channel<TextSegment>` による producer/consumer ストリーミング、デバイス暖気運転（オプション）
- **エンジン管理**: 自動起動、起動完了待機、永続化（任意）、子プロセス kill による確実な停止
- **フィラー**: 生成待機時の自然な間つなぎ音声（任意）
- **CLI**: `speakers`/`devices`/`--init`/`--clear`/`--test`、各種オプション
- **出力**: `--out` で WAV/Opus 保存、`--no-play` で再生抑止
- **ログ**: レベル/フォーマット切替、ファイル出力 + ローテーション、`--verbose` で詳細タイミング
- **エラー処理**: `VoicevoxRunCachedException` + `ErrorCode` で集約、終了コードへマッピング

## コマンド/オプション仕様

```bash
VoicevoxRunCached <text> [options]
VoicevoxRunCached --test [options]            # appsettings.json の Test.Message で実行
VoicevoxRunCached speakers
VoicevoxRunCached devices [--full] [--json]
VoicevoxRunCached --init
VoicevoxRunCached --clear

Options:
  --speaker, -s <id>       話者ID (default: 1)
  --speed <value>          話速 (default: 1.0)
  --pitch <value>          音高 (default: 0.0)
  --volume <value>         音量 (default: 1.0)
  --no-cache               キャッシュを使用しない
  --cache-only             キャッシュのみ使用（API呼び出しなし）
  --out, -o <path>         出力ファイル（.wav/.opus。それ以外はエラー）
  --no-play                再生しない（`--out`と併用）
  --verbose                詳細タイミング表示（ログレベルは変えない。`--log-level debug` を併用）
  --log-level <level>      trace|debug|info|warn|error|crit|none
  --log-format <fmt>       simple|json
  --help, -h               ヘルプ表示
```

## 設定ファイル構造（`appsettings.json`）

```json
{
  "VoiceVox": {
    "EngineType": "VOICEVOX", // or "AivisSpeech"
    "BaseUrl": "http://127.0.0.1:50021",
    "EngineArguments": "--host 127.0.0.1 --port 50021",
    "DefaultSpeaker": 1,
    "EnginePath": "",
    "ConnectionTimeout": 30,
    "AutoStartEngine": true,
    "StartupTimeoutSeconds": 30,
    "KeepEngineRunning": true
  },
  "Cache": {
    "Directory": "./cache/audio/",
    "UseExecutableBaseDirectory": true,
    "ExpirationDays": 30,
    "MaxSizeGB": 1.0,
    "OpusBitrateKbps": 32
  },
  "Audio": {
    "OutputDevice": -1,
    "Volume": 1.2,
    "PrepareDevice": false,
    "PreparationDurationMs": 200,
    "PreparationVolume": 0.01,
    "DesiredLatency": 100,
    "NumberOfBuffers": 3
  },
  "Filler": {
    "Enabled": false,
    "Directory": "./cache/filler/",
    "UseExecutableBaseDirectory": true,
    "FillerTexts": [
      "えーっと、",
      "あのー、",
      "あのう、",
      "ええと、",
      "ええっと、",
      "えとえと、"
    ]
  },
  "Logging": {
    "Level": "info",
    "Format": "simple",
    "EnableFileLogging": false,
    "MaxFileSizeMb": 10,
    "MaxFileCount": 5
  },
  "Test": {
    "Message": "テストメッセージ"
  }
}
```

## 主要設計

- **セグメント化**: 句読点で文分割し、部分変更時もキャッシュを最大活用
- **キャッシュ形式**: Ogg Opus (VOIP/VBR, 既定 32kbps) + `.meta.json`（作成日時/テキスト/話者/各種パラメータ）
- **容量制御**: `MaxSizeGB` 超過時に古い順で削除、`ExpirationDays` で期限切れ自動削除
- **デバイス準備**: 初回音切れ対策として極小音量の事前再生（可変長）
- **エンジン自動起動**: パス未指定時は既知パス探索、起動完了まで `/version` で待機
- **AIVIS対応**: `EngineType` 切替、ポート/スピーカーIDの例示

## 依存関係

- `NAudio` — 音声再生・WAV 処理・リサンプル
- `Concentus`, `Concentus.OggFile` — Ogg Opus エンコード/デコード
- `Microsoft.Extensions.Configuration` — `appsettings.json` バインド
- `Microsoft.Extensions.Logging` — DI 経由のロギング抽象
- `Serilog` (Console / File sink) — 構造化ロギング
- `FluentValidation` — `AppSettings` の検証
- `Jab` — DI コンテナ (Source Generator によるコンパイル時生成)
- `Aloe.Utils.CommandLine` — CLI 引数処理
- `TUnit` (テストプロジェクト) — Microsoft.Testing.Platform ベースのテストランナー
- `System.Text.Json`

## 簡易フロー

```mermaid
flowchart TD
    A[起動/引数解析] --> B{コマンド}
    B -->|speakers| C[話者一覧]
    B -->|devices| D[デバイス一覧]
    B -->|--init| E[フィラー初期化]
    B -->|--clear| F[キャッシュ削除]
    B -->|<text>| G[エンジン確認/自動起動]
    G --> H[セグメント化/キャッシュ参照]
    H -->|キャッシュ| I[即時再生]
    H -->|未キャッシュ| J[バックグラウンド生成]
    J --> K[生成完了/再生]
    I --> L[完了]
    K --> L[完了]
```

## トラブルシューティング要点

- エンジン未接続: BaseUrl確認、`AutoStartEngine` 利用、`EnginePath` 設定
- 音切れ: `Audio.PrepareDevice=true`、`PreparationDurationMs`/`PreparationVolume` 調整
- デバイス選択: `devices --full` で確認、`OutputDeviceId` 設定

## 移行メモ（初期仕様との差分）

- キャッシュ形式: WAV → MP3 + `.meta.json` ペア → (v3) Ogg Opus + `.meta.json` ペア
- キャッシュ層: ディスクのみ (`AudioCacheManager` ファサード経由。メモリキャッシュ層は単発 CLI では効かないため削除)
- 追加コマンド: `devices`, `--init`, `--clear`, `--test`
- 新オプション: `--out`, `--no-play`, `--log-level`, `--log-format`
- 新設定: `UseExecutableBaseDirectory`, `EngineType`, `KeepEngineRunning`, `MemoryCacheSizeMb`, `DesiredLatency`, `NumberOfBuffers`, `EnableFileLogging` / `MaxFileSizeMb` / `MaxFileCount`
- 削除: `Audio.OutputDeviceId` (再生本体は `WaveOutEvent` 一本化のため、index 指定のみ)
- アーキテクチャ: Jab (コンパイル時 DI) による DI 化、`SegmentProducer` 切り出し、`Channel<TextSegment>` による producer/consumer 並走
