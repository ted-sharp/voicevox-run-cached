# VOICEVOX REST API ラッパー要件定義書

## プロジェクト概要

**要望ID**: REQ-0001  
**タイトル**: VOICEVOX REST API ラッパーConsoleアプリ開発  
**概要**: WindowsでVOICEVOX REST APIを簡単に呼び出せるC# Consoleアプリ。音声データキャッシュ機能付き

## 要件ジャッジ結果

**実装判定**: ✅ Yes  
**優先度**: 中  
**影響範囲**: 新規開発、VOICEVOX依存、音声再生機能

**技術スタック**:
- **音声再生**: NAudio (MP3/WAV対応、.NET Core対応)
- **HTTP通信**: HttpClient
- **設定管理**: appsettings.json + Microsoft.Extensions.Configuration
- **キャッシュ**: ファイルベース (SHA256ハッシュ)
- **コマンドライン**: System.CommandLine

**判断理由**:
- 技術的実現可能性が高い
- VOICEVOXの並列処理制限を考慮したシリアル処理 + キャッシュ戦略
- .NET Core環境でのNAudio活用でMP3対応

## 変更対象ファイル・関数リスト

### **Program.cs**
- `Main(string[] args)` - エントリーポイント、引数解析
- `ParseArguments()` - コマンドライン引数パース
- `ShowUsage()` - ヘルプ表示

### **Services/VoiceVoxApiClient.cs**
- `GenerateAudioQueryAsync()` - /audio_query API呼び出し
- `SynthesizeAudioAsync()` - /synthesis API呼び出し
- `GetSpeakersAsync()` - /speakers API呼び出し
- `InitializeSpeakerAsync()` - /initialize_speaker API呼び出し

### **Services/AudioCacheManager.cs**
- `GetCachedAudioAsync()` - キャッシュ検索
- `SaveAudioCacheAsync()` - キャッシュ保存
- `ComputeCacheKey()` - SHA256ハッシュ生成
- `CleanupExpiredCache()` - 期限切れキャッシュ削除

### **Services/AudioPlayer.cs**
- `PlayAudioAsync()` - NAudioによる音声再生
- `StopAudio()` - 再生停止

### **Models/VoiceRequest.cs**
- テキスト、話者ID、音声パラメータを格納するDTOクラス

### **Configuration/AppSettings.cs**
- appsettings.json設定マッピングクラス

## データ設計方針

### **ファイルキャッシュ構造**
- **キャッシュディレクトリ**: `./cache/audio/`
- **ファイル名**: `{SHA256ハッシュ}.wav`
- **メタデータファイル**: `{SHA256ハッシュ}.meta.json`
  - 作成日時、有効期限、テキスト内容、話者ID格納

### **appsettings.json構造**
```json
{
  "VoiceVox": {
    "BaseUrl": "http://localhost:50021",
    "DefaultSpeaker": 1,
    "ConnectionTimeout": 30
  },
  "Cache": {
    "Directory": "./cache/audio/",
    "ExpirationDays": 30,
    "MaxSizeGB": 1.0
  },
  "Audio": {
    "OutputDevice": -1,
    "Volume": 1.0
  }
}
```

### **NuGetパッケージ依存関係**
- `NAudio` (2.2.1+) - 音声再生、MP3対応
- `Microsoft.Extensions.Configuration` - 設定管理
- `System.CommandLine` - コマンドライン引数解析
- `System.Text.Json` - JSON処理

## 画面設計方針
（Consoleアプリのため不要）

## 処理フロー

```mermaid
flowchart TD
    A[コマンド実行] --> B{引数解析}
    B -->|--speakers| C[話者一覧表示]
    B -->|--help| D[ヘルプ表示]
    B -->|テキスト指定| E[設定ファイル読込]
    
    E --> F{キャッシュ確認}
    F -->|キャッシュ有| G[キャッシュから音声取得]
    F -->|キャッシュ無| H{--cache-only?}
    
    H -->|Yes| I[エラー: キャッシュなし]
    H -->|No| J[VOICEVOX API呼び出し]
    
    J --> K[/audio_query API]
    K --> L[/synthesis API] 
    L --> M[音声データ取得]
    M --> N[キャッシュ保存]
    
    G --> O[NAudio再生]
    N --> O
    O --> P[完了]
    
    C --> P
    D --> P
    I --> Q[エラー終了]
```

## 開発工数見積

**Phase 1: 基本機能（3.5人日）**
- プロジェクト設定・パッケージ導入: 0.5人日
- 設定管理・引数解析実装: 1.0人日  
- VoiceVox API クライアント実装: 1.5人日
- 基本的な音声再生機能: 0.5人日

**Phase 2: キャッシュ機能（2.0人日）**
- キャッシュマネージャー実装: 1.0人日
- ファイルI/O・ハッシュ処理: 0.5人日
- 期限管理・容量制限: 0.5人日

**Phase 3: エラーハンドリング・最適化（1.5人日）**
- 例外処理・ログ出力: 0.5人日
- パフォーマンス最適化: 0.5人日
- テスト・デバッグ: 0.5人日

**合計工数: 7.0人日**

## 段階的機能アップデート方針

**Stage 1: MVP（最小機能）**
- テキスト→音声生成→再生の基本フロー
- 固定話者（ずんだもん）のみ対応
- キャッシュなし

**Stage 2: 基本機能完成**
- 全話者対応・パラメータ調整
- ファイルキャッシュ機能
- エラーハンドリング強化

**Stage 3: 運用最適化**
- キャッシュ管理機能（期限・容量）
- 設定ファイル外部化
- ログ出力・デバッグ機能

**Stage 4: 拡張機能**
- 複数音声の連続再生
- 音声ファイル出力オプション
- 音声品質設定

## コマンドライン引数仕様

```bash
VoiceVoxWrapper.exe <text> [options]

Options:
  --speaker, -s <id>     話者ID (default: 1)
  --speed <value>        話速 (default: 1.0)  
  --pitch <value>        音高 (default: 0.0)
  --volume <value>       音量 (default: 1.0)
  --no-cache            キャッシュを使用しない
  --cache-only          キャッシュのみ使用（API呼び出しなし）
  --speakers            利用可能な話者一覧表示
  --help, -h            ヘルプ表示
```

## 未確定事項 & TODO

### 🔍 要確認事項

1. **音声出力デバイス選択**
   - デフォルトデバイス以外の指定方法
   - デバイス一覧取得・選択UI

2. **キャッシュ運用ポリシー**
   - 最大キャッシュサイズの適切な初期値
   - キャッシュクリア手動コマンドの要否

3. **エラー時の動作**
   - VOICEVOX未起動時に自動起動するか
   - ネットワークエラー時のリトライ回数

4. **パフォーマンス要件**
   - 初回音声生成の許容待機時間
   - 同時実行数の上限設定

5. **設定ファイル配置**
   - exe同階層 vs %APPDATA% vs ユーザー指定

### 📋 TODO

- [ ] **Phase 1**: 基本機能実装
- [ ] **Phase 2**: キャッシュ機能追加  
- [ ] **Phase 3**: エラーハンドリング強化
- [ ] **Phase 4**: 運用最適化
- [ ] 上記未確定事項の方針決定
- [ ] 実装後のパフォーマンステスト
- [ ] ユーザーマニュアル作成

## 技術的考慮事項

**VOICEVOX API制限**:
- 並列処理非対応（シリアル処理必須）
- GPU使用時でも複数リクエスト同時処理不可
- 初回話者利用時のレイテンシ対策として事前初期化推奨

**NAudio制限**:
- Windows API依存（Linux等では機能制限）
- .NET Core 3.1以降で利用可能

**エラーハンドリング方針**:
- VOICEVOX未起動時の分かりやすいエラーメッセージ
- ネットワークタイムアウト処理
- 不正な話者ID指定時の処理
- キャッシュディスク容量不足対応