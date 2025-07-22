# VoicevoxRunCached

VOICEVOX REST APIラッパーアプリケーション - インテリジェントキャッシュ機能付きテキスト音声合成ツール

## 概要

VoicevoxRunCachedは、VOICEVOX REST APIを使用してテキストから音声を生成するコマンドラインツールです。MP3形式でのキャッシュ機能により、一度生成した音声の高速再生を実現し、文単位でのセグメント処理によってキャッシュ効率を最大化します。

## 主な機能

- **🎤 音声合成**: VOICEVOX REST APIを使用した高品質なテキスト音声変換
- **⚡ インテリジェントキャッシュ**: MP3形式でのキャッシュによる高速音声再生
- **📝 セグメント化処理**: 文単位での分割によるキャッシュ効率向上
- **🚀 即時再生**: キャッシュヒット済みセグメントの即座再生開始
- **🔊 安定した音声再生**: 初回再生時の音声品質安定化
- **⚙️ カスタマイズ可能**: 音声パラメータ（速度、ピッチ、音量）の調整

## システム要件

- **OS**: Windows x64
- **前提条件**: VOICEVOX エンジンが実行されていること
  - デフォルト: `http://localhost:50021`
  - [VOICEVOX公式サイト](https://voicevox.hiroshiba.jp/)からダウンロード可能

## インストール

### リリース版を使用（推奨）

1. [Releases](https://github.com/ted-sharp/voicevox-run-cached/releases)から最新版をダウンロード
2. `VoicevoxRunCached-v1.0.0-win-x64.zip`を任意のフォルダに展開
3. `appsettings.json`でVOICEVOXエンジンの設定を確認・調整
4. コマンドプロンプトまたはPowerShellから実行

### ソースからビルド

```bash
git clone https://github.com/ted-sharp/voicevox-run-cached.git
cd voicevox-run-cached/src/VoicevoxRunCached
dotnet publish -c Release -r win-x64 --self-contained
```

## 使用方法

### 基本的な使用方法

```bash
# シンプルな音声合成
VoicevoxRunCached.exe "こんにちは、世界！"

# スピーカーを指定
VoicevoxRunCached.exe "テストメッセージです。" --speaker 1

# 音声パラメータの調整
VoicevoxRunCached.exe "速度を変更します。" --speed 1.2 --pitch 0.1 --volume 0.8
```

### コマンドライン引数

| 引数 | 説明 | デフォルト |
|------|------|-----------|
| `<text>` | 音声合成するテキスト（必須） | - |
| `--speaker, -s <id>` | 使用するスピーカーID | 1 |
| `--speed <value>` | 音声速度（0.5-2.0） | 1.0 |
| `--pitch <value>` | 音声ピッチ（-0.15-0.15） | 0.0 |
| `--volume <value>` | 音声音量（0.0-2.0） | 1.0 |
| `--no-cache` | キャッシュを使用しない | false |
| `--cache-only` | キャッシュのみ使用（API呼び出し無し） | false |
| `--help, -h` | ヘルプを表示 | - |

### 特別なコマンド

```bash
# 利用可能なスピーカー一覧を表示
VoicevoxRunCached.exe speakers
```

## 設定ファイル

`appsettings.json`で各種設定をカスタマイズできます：

```json
{
  "VoiceVox": {
    "BaseUrl": "http://localhost:50021",
    "DefaultSpeaker": 1,
    "TimeoutSeconds": 30
  },
  "Cache": {
    "Directory": "./cache",
    "ExpirationDays": 30
  },
  "Audio": {
    "OutputDevice": -1,
    "Volume": 1.0
  }
}
```

### 設定項目説明

- **VoiceVox.BaseUrl**: VOICEVOX エンジンのURL
- **VoiceVox.DefaultSpeaker**: デフォルトのスピーカーID
- **VoiceVox.TimeoutSeconds**: API接続タイムアウト（秒）
- **Cache.Directory**: キャッシュファイル保存ディレクトリ
- **Cache.ExpirationDays**: キャッシュの有効期限（日数）
- **Audio.OutputDevice**: 出力オーディオデバイス（-1で既定）
- **Audio.Volume**: 全体音量レベル

## VoicevoxRunCached vs curl比較

### curlを直接使用する場合

```bash
# 1. スピーカー初期化
curl -X POST "http://localhost:50021/initialize_speaker" -H "Content-Type: application/json" -d "{\"speaker\": 1}"

# 2. 音声クエリ生成
curl -X POST "http://localhost:50021/audio_query?speaker=1&text=こんにちは、世界！" -H "Content-Type: application/json" -o query.json

# 3. 音声合成
curl -X POST "http://localhost:50021/synthesis?speaker=1" -H "Content-Type: application/json" -d @query.json --output audio.wav

# 4. 音声再生（別途プレイヤーが必要）
# Windows Media Player や他のツールで audio.wav を再生
```

### VoicevoxRunCachedを使用する場合

```bash
# 1回のコマンドで完了！
VoicevoxRunCached.exe "こんにちは、世界！"

# 2回目の実行（キャッシュから即座再生）
VoicevoxRunCached.exe "こんにちは、世界！"
```

### 比較表

| 項目 | curl | VoicevoxRunCached |
|------|------|-------------------|
| **コマンド数** | 3〜4個のコマンド | 1個のコマンド |
| **中間ファイル** | query.json, audio.wav | 不要（自動管理） |
| **音声再生** | 別途プレイヤー必要 | 自動再生 |
| **キャッシュ** | 手動管理 | 自動キャッシュ |
| **2回目実行** | 同じ手順を繰り返し | 即座に再生 |
| **エラーハンドリング** | 手動チェック | 自動エラー処理 |
| **セグメント化** | 不可 | 自動文分割 |
| **部分キャッシュ** | 不可 | 自動最適化 |

## セグメント処理とキャッシュ

本アプリケーションは、入力テキストを文単位で分割してキャッシュ効率を最大化します：

### セグメント化の利点

- **部分的キャッシュヒット**: 一部だけ変更されたテキストでも、変更されていない部分はキャッシュを利用
- **即時再生開始**: キャッシュ済みセグメントは待機なしで再生開始
- **バックグラウンド生成**: 未キャッシュセグメントは再生と並行して生成

### 例

```bash
# 初回実行（全セグメント生成）
VoicevoxRunCached.exe "おはようございます。今日は良い天気ですね。"

# 2回目実行（全セグメント即座再生）
VoicevoxRunCached.exe "おはようございます。今日は良い天気ですね。"

# 部分変更（1セグメントだけ生成、残りは即座再生）
VoicevoxRunCached.exe "おはようございます。今日は雨が降っています。"
```

## トラブルシューティング

### よくある問題

**Q: 「VOICEVOX エンジンに接続できません」エラー**
- A: VOICEVOX エンジンが起動していることを確認してください
- A: `appsettings.json`のBaseUrlが正しいことを確認してください

**Q: 音声が再生されない**
- A: Windowsの音量設定と出力デバイスを確認してください
- A: `appsettings.json`のAudio.OutputDeviceを-1に設定してください

**Q: 音声の開始部分が途切れる**
- A: アプリケーションを再起動してください（初回実行時に発生することがあります）

**Q: キャッシュをクリアしたい**
- A: `./cache`フォルダ（または設定したディレクトリ）を削除してください

## 開発・貢献

### 開発環境

- .NET 9.0
- NAudio（音声処理）
- Microsoft.Extensions.Configuration（設定管理）

### ビルド方法

```bash
cd src/VoicevoxRunCached
dotnet build
dotnet run "テストメッセージ"
```

### リリース用ビルド

```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## ライセンス

このプロジェクトはMITライセンスの下で公開されています。

## 関連リンク

- [VOICEVOX公式サイト](https://voicevox.hiroshiba.jp/)

---

🤖 Generated with [Claude Code](https://claude.ai/code)