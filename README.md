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
- **🎵 デバイス準備機能**: オーディオデバイスの暖気運転で音切れ防止
- **⚙️ カスタマイズ可能**: 音声パラメータ（速度、ピッチ、音量）の調整
- **🔧 エンジン自動起動**: VOICEVOXエンジンの自動起動とプロセス管理
- **🎯 フィラー機能**: 音声生成待機中の自然な間つなぎ音声
- **📊 詳細な実行時間測定**: `--verbose`オプションでパフォーマンス分析

## システム要件

- **OS**: Windows x64
- **前提条件**: VOICEVOX エンジン（自動起動対応）
  - デフォルト: `http://127.0.0.1:50021`
  - [VOICEVOX公式サイト](https://voicevox.hiroshiba.jp/)からダウンロード可能
  - エンジン自動起動機能により手動での事前起動は不要

## インストール

### リリース版を使用（推奨）

1. [Releases](https://github.com/ted-sharp/voicevox-run-cached/releases)から最新版をダウンロード
2. `VoicevoxRunCached-v1.1.0-win-x64.zip`を任意のフォルダに展開
   - 推奨場所: `C:\Program Files\VoicevoxRunCached\` または `C:\Tools\VoicevoxRunCached\`
3. `appsettings.json`でVOICEVOXエンジンの設定を確認・調整
4. **オプション**: パス設定やエイリアス設定（詳細は下記参照）
5. コマンドプロンプトまたはPowerShellから実行

### パス設定とエイリアス設定

どこからでもコマンドを実行できるように設定することをお勧めします：

#### 環境変数PATHに追加（推奨）

**方法1: GUI設定**
1. Windowsキー + R → `sysdm.cpl` → Enter
2. 「詳細設定」タブ → 「環境変数」
3. 「システム環境変数」の「Path」を選択 → 「編集」
4. 「新規」→ VoicevoxRunCached.exeがあるフォルダを追加
   - 例: `C:\Program Files\VoicevoxRunCached`
5. 「OK」で保存後、新しいコマンドプロンプトを開く

**方法2: PowerShellコマンド（管理者権限必要）**
```powershell
# 現在のPATHを表示
$env:PATH -split ";"

# PATHに追加（例: C:\Program Files\VoicevoxRunCached）
[Environment]::SetEnvironmentVariable("PATH", $env:PATH + ";C:\Program Files\VoicevoxRunCached", [EnvironmentVariableTarget]::Machine)
```

#### エイリアス設定

**PowerShell用エイリアス**
```powershell
# 一時的なエイリアス（現在のセッションのみ）
Set-Alias voice "C:\Program Files\VoicevoxRunCached\VoicevoxRunCached.exe"

# 永続的なエイリアス（PowerShellプロファイルに追加）
# プロファイルファイルを開く
notepad $PROFILE

# 以下の行を追加して保存
Set-Alias voice "C:\Program Files\VoicevoxRunCached\VoicevoxRunCached.exe"
```

**バッチファイル作成**

適切な保存場所：
- `%USERPROFILE%\bin\` （ユーザー専用、推奨）
- `C:\Tools\bin\` （カスタムツール用）

```batch
@echo off
rem voice.bat として上記の場所に保存
"C:\Program Files\VoicevoxRunCached\VoicevoxRunCached.exe" %*
```

**推奨手順（ユーザー専用bin作成）:**
```powershell
# 1. ユーザー専用binフォルダ作成
mkdir "$env:USERPROFILE\bin"

# 2. PATHに追加
[Environment]::SetEnvironmentVariable("PATH", $env:PATH + ";$env:USERPROFILE\bin", [EnvironmentVariableTarget]::User)

# 3. バッチファイル作成
@'
@echo off
"C:\Program Files\VoicevoxRunCached\VoicevoxRunCached.exe" %*
'@ | Out-File -FilePath "$env:USERPROFILE\bin\voice.bat" -Encoding ASCII
```

#### 設定後の使用例

PATH設定後:
```bash
# フルパス不要
VoicevoxRunCached.exe "こんにちは！"

# またはエイリアス使用
voice "こんにちは！"
```

### ソースからビルド

```bash
git clone https://github.com/ted-sharp/voicevox-run-cached.git
cd voicevox-run-cached/src_dotnet

# 通常のビルド（開発用）
_publish.cmd

# ZIP付きビルド（リリース用）
_publish_zip.cmd
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

**エイリアス設定後の簡単な使用方法:**
```bash
# 短いコマンドで実行
voice "こんにちは、世界！"
voice "テストメッセージです。" -s 1
voice "速度を変更します。" --speed 1.2
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
| `--verbose` | 詳細な実行時間情報を表示 | false |
| `--help, -h` | ヘルプを表示 | - |

### 特別なコマンド

```bash
# 利用可能なスピーカー一覧を表示
VoicevoxRunCached.exe speakers

# フィラー音声キャッシュの初期化
VoicevoxRunCached.exe --init

# 詳細な実行時間を表示
VoicevoxRunCached.exe "テストメッセージです。" --verbose

# エイリアス使用時
voice speakers
voice --init
voice "テストメッセージです。" --verbose
```

## 設定ファイル

`appsettings.json`で各種設定をカスタマイズできます：

```json
{
  "VoiceVox": {
    "BaseUrl": "http://127.0.0.1:50021",
    "DefaultSpeaker": 1,
    "ConnectionTimeout": 30,
    "AutoStartEngine": true,
    "EnginePath": "",
    "EngineArguments": "--host 127.0.0.1 --port 50021",
    "StartupTimeoutSeconds": 30,
    "EngineType": "VOICEVOX",
    "KeepEngineRunning": true
  },
  "Cache": {
    "Directory": "./cache/audio/",
    "ExpirationDays": 30,
    "MaxSizeGB": 1.0
  },
  "Audio": {
    "OutputDevice": -1,
    "Volume": 1.0,
    "PrepareDevice": false,
    "PreparationDurationMs": 200,
    "PreparationVolume": 0.01
  },
  "Filler": {
    "Enabled": true,
    "Directory": "./cache/filler/",
    "FillerTexts": [
      "えーっと", "あのー", "そのー", "んー", "まあ",
      "えー", "うーん", "ええと", "まー", "ふむ",
      "おー", "んと", "あー", "うー", "んーと",
      "あのう", "えーと"
    ]
  }
}
```

### 設定項目説明

#### VoiceVox設定
- **BaseUrl**: VOICEVOX エンジンのURL（DNS解決最適化のため127.0.0.1推奨）
- **DefaultSpeaker**: デフォルトのスピーカーID
- **ConnectionTimeout**: API接続タイムアウト（秒）
- **AutoStartEngine**: エンジンの自動起動を有効にするか
- **EnginePath**: エンジンの実行ファイルパス（空文字で自動検出）
- **EngineArguments**: エンジン起動時の引数
- **StartupTimeoutSeconds**: エンジン起動待機タイムアウト（秒）
- **EngineType**: エンジンタイプ（VOICEVOX/AivisSpeech）
- **KeepEngineRunning**: アプリ終了後もエンジンを起動したままにするか

#### Cache設定
- **Directory**: キャッシュファイル保存ディレクトリ
- **ExpirationDays**: キャッシュの有効期限（日数）
- **MaxSizeGB**: キャッシュの最大サイズ（GB）

#### Audio設定
- **OutputDevice**: 出力オーディオデバイス（-1で既定）
- **Volume**: 全体音量レベル
- **PrepareDevice**: デバイス準備機能を有効にするか（音切れ防止）
- **PreparationDurationMs**: デバイス準備時間（ミリ秒）
- **PreparationVolume**: 準備時の音量（極小音量での暖気運転）

#### Filler設定
- **Enabled**: フィラー機能を有効にするか
- **Directory**: フィラー音声キャッシュ保存ディレクトリ
- **FillerTexts**: 使用するフィラー音声のテキスト一覧

## VoicevoxRunCached vs curl比較

### curlを直接使用する場合

```bash
# 1. スピーカー初期化
curl -X POST "http://127.0.0.1:50021/initialize_speaker" -H "Content-Type: application/json" -d "{\"speaker\": 1}"

# 2. 音声クエリ生成
curl -X POST "http://127.0.0.1:50021/audio_query?speaker=1&text=こんにちは、世界！" -H "Content-Type: application/json" -o query.json

# 3. 音声合成
curl -X POST "http://127.0.0.1:50021/synthesis?speaker=1" -H "Content-Type: application/json" -d @query.json --output audio.wav

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

## パフォーマンス分析

`--verbose`オプションを使用すると、詳細な実行時間情報が表示されます：

```bash
VoicevoxRunCached.exe "テストメッセージです。" --verbose
```

**出力例:**
```
Engine check completed in 38.7ms
Processing segments...
Segment processing completed in 25.6ms
Found 1/1 segments in cache!
Playing audio...
Audio playback completed in 2376.1ms
Done!
Total execution time: 2445.4ms
```

### 実行時間の内訳

- **Engine check**: VOICEVOXエンジンの動作確認（初回起動時は長くなります）
- **Segment processing**: テキストの分割とキャッシュ確認
- **Audio playback**: 実際の音声再生時間
- **Total execution time**: アプリケーション全体の実行時間

### パフォーマンス最適化のポイント

1. **DNS解決最適化**: `127.0.0.1`使用により`localhost`のDNS解決遅延を回避
2. **エンジン再利用**: `KeepEngineRunning: true`により2回目以降のEngine checkが高速化
3. **セグメントキャッシュ**: 部分的な変更でも未変更部分は即座再生

## トラブルシューティング

### よくある問題

**Q: 「VOICEVOX エンジンに接続できません」エラー**
- A: VOICEVOX エンジンが起動していることを確認してください
- A: `appsettings.json`のBaseUrlが正しいことを確認してください

**Q: 音声が再生されない**
- A: Windowsの音量設定と出力デバイスを確認してください
- A: `appsettings.json`のAudio.OutputDeviceを-1に設定してください

**Q: 音声の開始部分が途切れる（特にUSBオーディオ・Bluetooth）**
- A: `appsettings.json`で`"PrepareDevice": true`に設定してください
- A: `PreparationDurationMs`を300-500に増やしてください
- A: デバイスによっては初回実行時に発生することがあります

**Q: キャッシュをクリアしたい**
- A: `./cache`フォルダ（または設定したディレクトリ）を削除してください

## 開発・貢献

### 開発環境

- .NET 9.0
- C# 13（最新言語機能）
- NAudio（音声処理）
- NAudio.Lame（MP3エンコード）
- Microsoft.Extensions.Configuration（設定管理）

### ビルド方法

#### 開発時のビルド
```bash
cd src_dotnet/VoicevoxRunCached
dotnet build
dotnet run "テストメッセージ"
```

#### 配布用パッケージ作成
```bash
cd src_dotnet

# 通常のビルド（./publish/VoicevoxRunCached/）
_publish.cmd

# ZIP付きビルド（./publish/VoicevoxRunCached-v{バージョン}-win-x64.zip）
_publish_zip.cmd
```

#### バージョン管理
- `VoicevoxRunCached.csproj`の`<Version>`タグでバージョン指定
- `_publish_zip.cmd`が自動的にバージョンを参照してZIPファイル名を決定

#### 技術的特徴
- **C# 13の最新機能を活用**:
  - Primary constructors
  - Collection expressions
  - Enhanced pattern matching
  - ref readonly parameters
  - Escape character improvements
- **MP3キャッシュ**: WAVからMP3への自動変換でストレージ効率化
- **スレッドセーフ**: 新しいLock型による安全な並行処理
- **デバイス準備**: 実際の極小音量再生による効果的な暖気運転

## ライセンス

このプロジェクトはMITライセンスの下で公開されています。

## 関連リンク

- [VOICEVOX公式サイト](https://voicevox.hiroshiba.jp/)

---

🤖 Generated with [Claude Code](https://claude.ai/code)