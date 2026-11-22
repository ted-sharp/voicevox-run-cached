# VoicevoxRunCached

VOICEVOX REST API のラッパー。テキストを文単位で分割し、SHA256 キーで Ogg Opus キャッシュしながら逐次合成・再生するコマンドラインツール。

- **ワンコマンド**: テキストを渡すだけで API 呼び出し → Opus 化 → 再生まで完結
- **セグメント単位キャッシュ**: 文の一部だけ変えても変更されていない部分はキャッシュから即再生
- **ストリーミング再生**: キャッシュ済み部分を再生しつつ未キャッシュ部分をバックグラウンド生成、待機中はフィラー音声で繋ぐ
- **エンジン自動管理**: VOICEVOX / AivisSpeech のプロセス起動・常駐・停止を自動化
- **VOICEPEAK・CeVIO AI にも対応**: `--engine voicepeak` / `--engine cevio` で切り替える ([エンジンを切り替える](#エンジンを切り替える))
- **再生デバイスの使い分け**: 話者ごとに鳴らすデバイスを選べ、違うデバイスなら同時に鳴る ([再生デバイスを選ぶ](#再生デバイスを選ぶ))

## 目次

- [必要環境](#必要環境)
- [インストール](#インストール)
- [はじめに](#はじめに)
- [用途別ガイド](#用途別ガイド)
  - [よく使う例](#よく使う例)
  - [Claude Code Hooks から通知を読み上げる](#claude-code-hooks-から通知を読み上げる)
  - [エンジンを切り替える](#エンジンを切り替える)
  - [再生デバイスを選ぶ](#再生デバイスを選ぶ)
  - [フィラーで待ち時間を埋める](#フィラーで待ち時間を埋める)
  - [キャッシュを作り直す](#キャッシュを作り直す)
- [リファレンス](#リファレンス)
  - [コマンドとオプション](#コマンドとオプション)
  - [終了コード](#終了コード)
  - [出力先と文字コード](#出力先と文字コード)
  - [設定 (`appsettings.json`)](#設定-appsettingsjson)
  - [ファイルの場所](#ファイルの場所)
- [仕組み](#仕組み)
  - [セグメント単位キャッシュ](#セグメント単位キャッシュ)
  - [フィラー音声](#フィラー音声)
  - [同時実行](#同時実行)
- [トラブルシューティング](#トラブルシューティング)
- [開発](#開発)
- [ライセンス](#ライセンス)
- [関連リンク](#関連リンク)

## 必要環境

- **OS**: Windows x64
- **VOICEVOX エンジン** (デフォルト `http://127.0.0.1:50021`) — [VOICEVOX 公式](https://voicevox.hiroshiba.jp/) からダウンロード。AivisSpeech にも対応 ([AivisSpeech を使う](#aivisspeech-を使う))
- 既定で `AutoStartEngine: true` のためエンジンは自動起動される。外部で起動済みのエンジンにも接続可能
- **VOICEPEAK** (任意) — `--engine voicepeak` で使う場合のみ。コマンドラインに対応した製品版が必要
- **CeVIO AI** (任意) — `--engine cevio` で使う場合のみ。使えるのはライセンスのあるキャスト

## インストール

### リリース版

1. [Releases](https://github.com/ted-sharp/voicevox-run-cached/releases) から `VoicevoxRunCached-v[x.x.x]-win-x64.zip` をダウンロード
2. 書き込めるフォルダ (例: `C:\Tools\VoicevoxRunCached\`) に展開。キャッシュを exe と同じフォルダに作るので、`C:\Program Files` のような管理者権限が要る場所に置くと起動時に設定エラー (終了コード 7) になる ([ファイルの場所](#ファイルの場所))
3. 必要に応じて `appsettings.json` を編集 (項目は「[設定](#設定-appsettingsjson)」)
4. (任意) 環境変数 `PATH` に追加するとどこからでも `VoicevoxRunCached.exe` で実行可能

### ソースからビルド

必要なもの:

- [.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0)
- [Task](https://taskfile.dev) (タスクランナー。ビルド・テストなどの操作を `Taskfile.yml` にまとめている)

```bash
winget install Task.Task      # Task のインストール (入れた後はターミナルを開き直す)
```

```bash
git clone https://github.com/ted-sharp/voicevox-run-cached.git
cd voicevox-run-cached
task install        # ビルドして %LOCALAPPDATA%\Programs\VoicevoxRunCached に入れる
task publish        # ビルドのみ (src_dotnet/publish/VoicevoxRunCached/)
task publish:zip    # 配布用 (バージョン付き ZIP)
```

- 入れる場所は `INSTALL_DIR` で変えられる (`task install INSTALL_DIR='C:\Tools\VoicevoxRunCached'`。`task uninstall` にも同じものを渡す)
- もう一度実行すると exe だけを入れ替え、編集済みの `appsettings.json` と `cache\` / `logs\` は残す。新しい版で設定項目が変わったときは、`src_dotnet/publish/VoicevoxRunCached/appsettings.json` と見比べる
- PATH は変更しない。入っていなければ、追加するコマンドが表示される
- `task uninstall` は exe と `appsettings.json` を消し、`cache\` / `logs\` は残す

出力は .NET ランタイムを含む単一の `VoicevoxRunCached.exe` (約 100MB) と `appsettings.json` で、実行する PC に .NET を入れる必要はない (リリース版の ZIP も同じ)。一緒に出る `VoicevoxRunCached.pdb` はデバッグ用で、実行には要らない。

## はじめに

インストールしたら、次の順に試す。

1. **鳴らしてみる**。エンジンが止まっていれば自動で起動する (初回は起動と合成を待つ。同じ文の 2 回目からはキャッシュからすぐ鳴る)

   ```bash
   VoicevoxRunCached.exe "こんにちは、世界！"
   ```

2. **話者を選ぶ**。`speakers` で話者 (スタイル) と ID の一覧が出るので、`-s` に ID を渡して聞き比べる

   ```bash
   VoicevoxRunCached.exe speakers
   VoicevoxRunCached.exe "こんにちは" -s 3
   ```

3. **既定の話者にする**。`appsettings.json` の `VoiceVox.DefaultSpeaker` を選んだ ID にすると、`-s` を省略したときその声になる
4. **確かめる**。`--test` は `Test.Message` (記号や数字を含む長めの文) を読み上げる

   ```bash
   VoicevoxRunCached.exe --test
   ```

次にやりたいことに合わせて[用途別ガイド](#用途別ガイド)へ。Claude Code の通知に使うなら「[Claude Code Hooks から通知を読み上げる](#claude-code-hooks-から通知を読み上げる)」。

## 用途別ガイド

### よく使う例

```bash
# パラメータ指定
VoicevoxRunCached.exe "テストです。" --speaker 2 --speed 1.2 --pitch 0.1

# ファイル保存 (再生なし)
VoicevoxRunCached.exe "保存します。" --out out.opus --no-play

# キャッシュのみ (API を呼ばない。エンジンが止まっていても再生できる)
VoicevoxRunCached.exe "既に生成済みのテキスト" --cache-only

# 1 回だけ別の再生デバイスで鳴らす (番号は「再生デバイスを選ぶ」)
VoicevoxRunCached.exe "別のデバイスで鳴らします。" --device 2
```

オプションの一覧は「[コマンドとオプション](#コマンドとオプション)」。

### Claude Code Hooks から通知を読み上げる

[Claude Code](https://claude.ai/code) の Hooks から呼び出して作業の節目を音声通知できる。`~/.claude/settings.json` に以下のように追加:

```json
{
  "hooks": {
    "Stop": [
      {
        "matcher": "",
        "hooks": [
          {
            "type": "command",
            "command": "\"C:\\Tools\\VoicevoxRunCached\\VoicevoxRunCached.exe\" \"作業が完了しました。\" --speaker 3"
          }
        ]
      }
    ]
  }
}
```

パスは展開・インストールした場所に合わせる (`task install` なら `%LOCALAPPDATA%\Programs\VoicevoxRunCached\VoicevoxRunCached.exe`)。`UserPromptSubmit` / `PostToolUse` / `Notification` など他のフックでも同様に呼び出せる。

Claude Code に応答ごとの一言を書かせてキャラクターの声で読み上げる組み込み例 (Stop / Notification / SessionStart のスクリプト・キャラクター別の口調と定型文・ユーザー辞書) は [voicevox-run-cached-claude-hooks](https://github.com/ted-sharp/voicevox-run-cached-claude-hooks) にある。

**書き方**

- Hooks 環境では `PATH` が不十分な場合があるため、`.exe` のフルパスを直接指定すること。`.bat` 経由は `cmd /c "<バッチのフルパス>"` でラップする
- パスにスペースが含まれる場合はダブルクォートで囲み、Windows パスの `\` は JSON では `\\` にエスケープする

**失敗したとき**

- 失敗しても終了コード 2 は返さないので、Claude Code の動作は止めない。失敗は 0 以外の終了コード ([終了コード](#終了コード)) としてユーザーに表示されるだけ
- Hooks では 2 がブロッキングエラーで、Stop フックなら Claude の停止が止められてしまうため、2 は使わない
- エラーメッセージは stderr に UTF-8 で出るので、その表示で失敗の理由もわかる

**続けて発火したとき**

- 複数のフック (複数セッションなど) が同時に発火しても音声は重ならず、順番に鳴る (違うデバイスに分けたものは同時に鳴る。[同時実行](#同時実行))
- 待ち時間も Hooks の `timeout` に含まれるので、長い文を続けて読ませる場合は `timeout` を長めにする。古い通知を後からまとめて読ませたくなければ `--max-wait` で待つ上限を決める

**通知の遅れ**

- エンジンに接続するのは合成が必要なときだけ。決まった文言の通知は 2 回目以降キャッシュから再生されるので、エンジンが止まっていても起動を待たずにすぐ鳴る
- キャッシュに無い文を合成するとき、既定 (`AutoStartEngine: true`) ではエンジンが停止していても自動起動されるが、起動を待つ分だけ通知が遅れる。遅延を避けたい場合はエンジンを事前に起動しておく (`KeepEngineRunning: true` (既定) なら一度起動したエンジンは常駐する)
- `KeepEngineRunning: false` のとき、自分が起動したエンジンは合成が終わった時点で止める (再生の途中でも。エラー・1 回目の Ctrl+C のときも止める)
- 合成の途中で Hooks の `timeout` によって強制終了されたときや、Ctrl+C を 2 回押したときはエンジンが動いたまま残る。以後の実行はそれを外部で起動済みのエンジンとみなして止めないので、残ったエンジンは手動で終了する

### エンジンを切り替える

既定は VOICEVOX。`--engine <name>` でその実行だけ切り替える。`Synthesis.Engine` を変えると既定のエンジンになる。キャッシュのキーにはエンジンと話者が入るので、エンジンを切り替えてもキャッシュは混ざらない。

#### AivisSpeech を使う

AivisSpeech は VOICEVOX と同じ API なので、`--engine` は `voicevox` のまま、`VoiceVox` セクションの接続先を変える。

```jsonc
"VoiceVox": {
  "EngineType": "AivisSpeech",
  "BaseUrl": "http://127.0.0.1:10101",
  "EngineArguments": "--host 127.0.0.1 --port 10101",
  "DefaultSpeaker": 888753760
}
```

#### VOICEPEAK を使う

VOICEPEAK (製品版) のコマンドライン (`voicepeak.exe`) で合成する。`--engine voicepeak` を付けるか、`Synthesis.Engine` を `"Voicepeak"` にする。設定は `Voicepeak` セクション ([設定](#設定-appsettingsjson))。

```
VoicevoxRunCached.exe speakers --engine voicepeak                       # ナレーターと感情の一覧
VoicevoxRunCached.exe "作業が完了しました。" --engine voicepeak -s "Otomachi Una"
VoicevoxRunCached.exe "作業が完了しました。" --engine voicepeak -s "Otomachi Una:joy=50,sad=20"
VoicevoxRunCached.exe --init --engine voicepeak -s "Otomachi Una"       # フィラーも VOICEPEAK の声で生成
```

- 話者は `ナレーター名` か `ナレーター名:感情=値,...` (値は 0〜100)。感情が違えば別のキャッシュになる
- `--speed` は 0.5〜2.0、`--pitch` は -0.15〜0.15 (VOICEPEAK の -300〜300 に換算)。範囲外はエラー (終了コード 1)。`--volume` は合成した音声に掛ける
- **1 文の合成に 10 秒ほどかかる** (`voicepeak.exe` を文ごとに起動するため。`speakers` も同じくらいかかる)。キャッシュ済みの文はすぐ鳴るので、Hooks のような決まった文の通知に向く。初めての文の待ち時間はフィラーで埋められる (VOICEPEAK 用の長めのフィラーを待ち 1 回に 2 回まで。[フィラーで待ち時間を埋める](#フィラーで待ち時間を埋める))
- 存在しない話者・感情の名前は、最初の文の合成が失敗した時点で残りの文の合成をやめる (VOICEPEAK が理由を返さないので、`speakers --engine voicepeak` で名前を確認する)

#### CeVIO AI を使う

[CeVIO AI](https://cevio.jp/) がインストールされていれば、`--engine cevio` で CeVIO AI のキャストで読み上げられる (外部連携インターフェイス (COM) を使う。ライセンスのあるキャストだけ)。`Synthesis.Engine` を `"Cevio"` にすれば既定のエンジンになる。設定は `Cevio` セクション ([設定](#設定-appsettingsjson))。

```
VoicevoxRunCached.exe speakers --engine cevio                       # 使えるキャストと感情
VoicevoxRunCached.exe "こんにちは" --engine cevio -s "花隈千冬"
VoicevoxRunCached.exe "こんにちは" --engine cevio -s "花隈千冬:嬉しい=70,普通=30"
VoicevoxRunCached.exe --init --engine cevio -s "花隈千冬"           # フィラー (話者・感情ごと)
```

- **話者** (`-s`): キャスト名。感情は `キャスト名:感情=値,感情=値` (値は 0〜100 の整数) で指定し、書いていない感情は 0 になる。感情を書かなければキャストの既定値 (多くは `普通=100`)。存在しないキャスト・感情は終了コード 1。`Cevio.DefaultSpeaker` が空なら `--engine cevio` では `-s` が必須
- **パラメータ**: `--speed` は VOICEVOX と同じ倍率 (2.0 で約 2 倍速) で 0.25〜4.0、`--pitch` は -0.15〜0.15 (CeVIO の音の高さの端で、約 ±5 半音)。範囲外はエラー (終了コード 1)。`--volume` は VOICEVOX と同じく波形に掛ける倍率 (0.5 で −6dB、0 で無音)。CeVIO の声質・抑揚は既定値のまま
- **起動**: CeVIO AI が起動していなければ自動で起動する (`Cevio.AutoStartHost`。起動に 10 秒ほどかかる)。自分で起動した CeVIO AI は既定で読み上げの後も残す (`Cevio.KeepHostRunning: false` なら終わるときに閉じる。外部で起動済みだった CeVIO AI は閉じない)。CeVIO AI は常にウィンドウを表示する
- **キャッシュ**: VOICEVOX と同じく文ごとに保存する。CeVIO の辞書で読みを直したときやキャストを更新したときは `--refresh` で作り直す ([キャッシュを作り直す](#キャッシュを作り直す))
- **失敗したとき**: 合成の前に CeVIO AI が無い・起動に失敗したときは終了コード 3 (応答しないまま `SynthesisTimeoutSeconds` を過ぎたときは 6)。読み上げの途中で CeVIO AI を閉じると (応答しないときも)、残りの文の合成をやめ、鳴らせた文があれば 8、無ければ 5 で終わる

### 再生デバイスを選ぶ

既定では OS の既定のデバイスで鳴る。別のデバイスで鳴らすときや、話者ごとにデバイスを分けるときは次の順に設定する。

1. **デバイスの番号を確かめる**。`devices --speak` は一覧を出し、各デバイスから「デバイス 0 番です」のようにその番号を順に読み上げる

   ```
   > VoicevoxRunCached.exe devices --speak
   ...
   Audio.OutputDevice (WaveOut index):
     -1: OS default
      0: "スピーカー (Realtek(R) Audio)"
      1: "ヘッドホン (USB Audio)"

   Speaking each WaveOut index on its device...
      0: "スピーカー (Realtek(R) Audio)"
      1: "ヘッドホン (USB Audio)"
   ```

   - 下段の行が出るたびに、そのデバイスから番号が聞こえる。-1 (OS 既定) は読み上げない
   - 声は読み上げと同じく `--engine` / `-s` で選べる。初回は合成するので少し待つ
   - 番号だけ見るなら `devices` (読み上げない)

2. **全部の読み上げを 1 つのデバイスで鳴らす**なら、その番号を `Audio.OutputDevice` に書く
3. **話者ごとに分ける**なら、`Audio.SpeakerDevices` に話者と番号の対応を書く (対応表に無い話者は `Audio.OutputDevice` で鳴る)

   ```jsonc
   "Audio": {
     "OutputDevice": -1,
     "SpeakerDevices": [
       { "Engine": "Voicevox", "Speaker": "3", "Device": 1 },
       { "Engine": "Cevio", "Speaker": "花隈千冬", "Device": 0 }
     ]
   }
   ```

4. **試す**。`VoicevoxRunCached.exe "テストです。" -s 3` が 1 番のデバイスで鳴ればよい。1 回だけ別のデバイスで鳴らすなら `--device <番号>`

**`SpeakerDevices` の書き方**

- `Speaker` は `-s` と同じ書き方。VOICEPEAK・CeVIO は感情 (`:` から後ろ) を見ずに、ナレーター名・キャスト名で照合する。VOICEVOX はスタイルごとの ID で書く
- `Engine` は `Voicevox` / `Voicepeak` / `Cevio`。省くと `Voicevox`
- 合う項目が複数あれば上のものを使う
- 決まる順は `--device` → `SpeakerDevices` → `OutputDevice`
- 書き方が不正な項目 (話者が空・エンジン名の間違いなど) は設定エラー (終了コード 7)

**同時に鳴らす**

- 違うデバイスに向けた読み上げは同時に鳴り、同じデバイスの読み上げは順番に鳴る。OS 既定 (-1) と番号での指定を混ぜるときの注意は「[同時実行](#同時実行)」

**番号の注意**

- 使う番号は `devices` の「Audio.OutputDevice (WaveOut index)」欄のもの。上段の WASAPI 一覧 (`devices --full` の `[n]`) とは番号が一致しないことがある
- USB / Bluetooth のデバイスを抜き差しすると、番号が変わることがある。違うデバイスで鳴るようになったら `devices --speak` で確かめ直す
- 無い番号を指定すると、合成の前にエラー (終了コード 5) になる
- デバイス名は WinMM の制約で 31 文字までに切られる

### フィラーで待ち時間を埋める

キャッシュに無い文の合成を待つ間、「えーっと、」「あのー、」などのフィラー音声を挟んで無音を埋められる (既定は無効)。

1. **フィラーを生成する**。`--init` で既定のエンジン・`DefaultSpeaker` の声のフィラーを作る

   ```bash
   VoicevoxRunCached.exe --init
   VoicevoxRunCached.exe --init -s 3                                  # ほかの話者でも読み上げるなら、その話者の分も
   VoicevoxRunCached.exe --init --engine voicepeak -s "Otomachi Una"  # ほかのエンジンも同じ
   ```

2. **有効にする**。`appsettings.json` の `Filler.Enabled` を `true` にする

- フィラーは読み上げと同じ話者の声で鳴る (途中で声が変わらないように)。生成していない話者では鳴らず、Warning ログが 1 回出るだけ
- 音量は読み上げの `--volume` に合わせる (話速・音高は既定のまま)
- フィラーの文 (`FillerTexts`) を変えたら `--init` で生成し直す。`--clear` の後も `--init` をやり直す
- 文と、待ち 1 回に鳴らす回数はエンジンごとに決められる。古いフィラーの削除も含めて「[フィラー音声](#フィラー音声)」

### キャッシュを作り直す

- **読みを直したとき**: VOICEVOX のユーザー辞書で読みを直したときやエンジンを更新したときは、その文を `--refresh` 付きで一度実行する。合成し直してキャッシュを上書きする (`--no-cache` は合成し直すがキャッシュを更新しない)

  ```bash
  VoicevoxRunCached.exe "読みを直したテキスト" --refresh
  ```

- **全部消すとき**: `VoicevoxRunCached.exe --clear` (または `Cache.Directory` のフォルダを手動で削除)。フィラーのキャッシュも消えるので、フィラーを使うなら `--init` をやり直す
- 古いキャッシュは自動で消える (最後に使ってから `Cache.ExpirationDays` 日、または合計が `Cache.MaxSizeGB` を超えたとき)。「[セグメント単位キャッシュ](#セグメント単位キャッシュ)」

## リファレンス

### コマンドとオプション

```
VoicevoxRunCached.exe <text> [options]
VoicevoxRunCached.exe -- <text> [options]   # <text> がコマンド名 (speakers など) と同じでも本文として読み上げる
VoicevoxRunCached.exe --test [options]      # appsettings.json の Test.Message で実行
VoicevoxRunCached.exe speakers [--engine <name>]  # 話者の一覧
VoicevoxRunCached.exe devices [--full] [--json]  # オーディオデバイス一覧
VoicevoxRunCached.exe devices --speak [--engine <name>] [-s <id>]  # 一覧を出し、各デバイスでその番号を順に読み上げる
VoicevoxRunCached.exe --init [--engine <name>] [-s <id>]  # フィラー音声キャッシュを初期化 (話者ごと。省略時は DefaultSpeaker)
VoicevoxRunCached.exe --clear               # キャッシュ全削除
```

| Option | 説明 | デフォルト |
|---|---|---|
| `--engine <name>` | 合成エンジン (`voicevox` / `voicepeak` / `cevio`。大文字小文字は区別しない) | `Synthesis.Engine` (`Voicevox`) |
| `--speaker, -s <id>` | 話者。VOICEVOX は ID (0 以上)、VOICEPEAK はナレーター名、CeVIO AI はキャスト名 (どちらも感情付きも可。[エンジンを切り替える](#エンジンを切り替える)) | エンジンの `DefaultSpeaker` (VOICEVOX は `VoiceVox.DefaultSpeaker` = 1) |
| `--speed <value>` | 速度。0 より大きい値 (目安 0.5–2.0) | 1.0 |
| `--pitch <value>` | ピッチ (目安 -0.15–0.15) | 0.0 |
| `--volume <value>` | 合成する音声の音量。0 以上 (目安 0.0–2.0)。キャッシュと `--out` にも反映される (再生時の音量は `Audio.Volume`) | 1.0 |
| `--no-cache` | キャッシュを使わない (読みも書きもしない) | false |
| `--cache-only` | キャッシュのみ使う (API を呼ばず、エンジンにも接続しない。キャッシュに無い文があれば終了コード 4) | false |
| `--refresh` | キャッシュを読まずに全部合成し直し、キャッシュを上書きする (`--no-cache` / `--cache-only` とは併用不可) | false |
| `--out, -o <path>` | ファイル保存 (`.wav` / `.opus` を拡張子で判定。それ以外はエラー)。書き込めない場所 (読み取り専用やほかのアプリが開いているファイルも) なら合成・再生の前にエラー (終了コード 5) | — |
| `--no-play` | 再生しない (`--out` と併用推奨) | false |
| `--device <index>` | 再生デバイスの番号 (`devices` の WaveOut index。-1 は OS 既定。[再生デバイスを選ぶ](#再生デバイスを選ぶ)) | `Audio.SpeakerDevices` で話者に合う項目、無ければ `Audio.OutputDevice` |
| `--verbose` | 再生時間と総実行時間を表示 | false |
| `--max-wait <seconds>` | 合成と再生の順番待ちを合わせて待つ上限 (秒、0 以上。[同時実行](#同時実行)) | `Lock.MaxWaitSeconds` (未設定なら上限なし) |
| `--log-level <level>` | `trace`/`debug`/`info`/`warn`/`error`/`crit`/`none` (`none` はログを一切出さない) | info |
| `--log-format <fmt>` | `simple` / `json` (`json` は 1 イベント 1 行の JSON。ファイル出力も同じ書式) | simple |
| `--help, -h` | ヘルプ表示 (引数のどこにあっても表示し、読み上げ・コマンドは実行しない) | — |

**値の扱い**

- `--speed` / `--pitch` / `--volume` は小数第 2 位に丸めて使われる (キャッシュキーも丸めた値で作る)
- 「目安」は VOICEVOX エディタの調整範囲。VOICEVOX で本ツールが検証するのは、数値であること・speed が 0 より大きいこと・volume と speaker が 0 以上であることだけ。不正な値はエラーで終了する
- VOICEPEAK・CeVIO は話速・音高の範囲も検証する ([エンジンを切り替える](#エンジンを切り替える))

**引数の書き方**

- 不明なオプション (`--speeed` などの打ち間違い) と余分な引数はエラーで終了する (終了コード 1。既定値のまま読み上げない)。`speakers` / `devices` / `--init` / `--clear` も同じ
- 本文に空白を含めるときは全体を引用符で囲む
- コマンド名は大文字小文字を区別する (`Speakers` は本文として読む)
- 本文の前に `--` を置くと、次のような文もそのまま読み上げる
  - コマンドやオプションと同じ文字列 (`VoicevoxRunCached.exe -- speakers`)
  - オプションの形をした文 (`--refresh` のように `--` の直後が英字で空白を含まないもの、`-s` / `-o`)。本文の位置にあると本文の書き忘れとしてエラーにする。`--- 完了 ---` のような文は `--` なしで読む
- `appsettings.json` の値は `--Section:Key=値` でその実行だけ上書きできる (例: `--Lock:MaxWaitSeconds=60`)

### 終了コード

| コード | 意味 |
|---|---|
| 0 | 成功 |
| 1 | 一般エラー (不正な引数・想定外の例外など) |
| 3 | エンジンエラー (エンジンの起動失敗・エンジンに接続できない・エンジンが 500/503 を返した) |
| 4 | キャッシュエラー (`--clear` で削除できなかった等) |
| 5 | 音声エラー (全セグメント (`--init` では全フィラー) の合成失敗・音声形式の不正・`--out` の保存失敗・無い再生デバイスの番号) |
| 6 | API エラー (タイムアウト・不正なリクエスト/レスポンス・レート制限など) |
| 7 | 設定エラー (`appsettings.json` が無い・JSON の書式が不正・値の型や範囲が不正) |
| 8 | 一部のセグメントだけ合成に失敗 (再生・保存は成功した分だけ行われる。`--init` では一部のフィラーだけ生成に失敗) |
| 9 | 順番待ちが上限 (`--max-wait` / `Lock.MaxWaitSeconds`) を超えたので、実行しなかった (読み上げでは鳴らさなかった) |
| 130 | Ctrl+C でキャンセル (後片付けが終わらないときにもう一度 Ctrl+C を押すと強制終了する。その場合の終了コードは OS 既定の値) |

2 は使わない (Claude Code の Hooks では終了コード 2 がブロッキングエラーとして扱われるため。[Claude Code Hooks から通知を読み上げる](#claude-code-hooks-から通知を読み上げる) を参照)。

### 出力先と文字コード

- **stdout**: コマンドの結果と進捗 (`speakers` / `devices` の一覧、`Processing segments...` など)。`devices --json` の stdout は JSON だけになる
- **stderr**: ログ、エラー・警告とその補足 (解決策など)、起動待ちのスピナー
- 色 (ANSI エスケープ) は、出力先がコンソールのときだけ付く
- リダイレクト・パイプ時は BOM なし UTF-8 で出力する (コンソールに直接出すときは従来どおりコンソールのコードページ)。Windows PowerShell 5.1 で `$x = VoicevoxRunCached.exe speakers` のように取り込むと、既定ではコンソールのコードページで解釈されて文字化けする。その場合は `[Console]::OutputEncoding = [Text.Encoding]::UTF8` を先に実行する

### 設定 (`appsettings.json`)

各項目の意味は、exe と一緒に置く `appsettings.json` ([リポジトリの同じファイル](./src_dotnet/VoicevoxRunCached/appsettings.json)) にコメントで書いてある。値が不正なら起動時に設定エラー (終了コード 7) になる。値は `--Section:Key=値` でその実行だけ上書きできる ([コマンドとオプション](#コマンドとオプション))。

| セクション | 内容 | 使い方を説明している節 |
|---|---|---|
| `Synthesis` | 既定の合成エンジン | [エンジンを切り替える](#エンジンを切り替える) |
| `VoiceVox` | VOICEVOX の接続先・既定の話者・エンジンの自動起動と常駐 | [はじめに](#はじめに)・[AivisSpeech を使う](#aivisspeech-を使う)・[Hooks](#claude-code-hooks-から通知を読み上げる) の「通知の遅れ」 |
| `Voicepeak` | VOICEPEAK の exe・既定の話者・タイムアウト | [VOICEPEAK を使う](#voicepeak-を使う) |
| `Cevio` | CeVIO AI の既定の話者・起動と終了・タイムアウト | [CeVIO AI を使う](#cevio-ai-を使う) |
| `Cache` | キャッシュの保存先・期限・容量・ビットレート | [キャッシュを作り直す](#キャッシュを作り直す)・[ファイルの場所](#ファイルの場所) |
| `Audio` | 再生デバイス・再生時の音量・ウォームアップ・バッファ | [再生デバイスを選ぶ](#再生デバイスを選ぶ)・[トラブルシューティング](#トラブルシューティング) |
| `Filler` | フィラーの有効化・保存先・文 (エンジンごとの文と回数は各エンジンのセクション) | [フィラーで待ち時間を埋める](#フィラーで待ち時間を埋める)・[フィラー音声](#フィラー音声) |
| `Lock` | 順番待ちの上限 | [同時実行](#同時実行) |
| `Logging` | ログのレベル・書式・ファイル出力 | [ファイルの場所](#ファイルの場所) |
| `Test` | `--test` で読み上げる文 | [はじめに](#はじめに) |

### ファイルの場所

| もの | 既定の場所 | 変え方 |
|---|---|---|
| 音声のキャッシュ | `VoicevoxRunCached.exe` と同じフォルダの `cache\audio\` | `Cache.Directory` |
| フィラー | 同じく `cache\filler\` | `Filler.Directory` (`Cache.Directory` と同じにはできない) |
| ログ (`Logging.EnableFileLogging: true` のとき) | exe と同じフォルダの `logs\voicevox-run-cached-YYYYMMDD.log` (日ごと) | 変えられない |
| ロックファイル ([同時実行](#同時実行)) | `%TEMP%\VoicevoxRunCached.speech.lock` と `%TEMP%\VoicevoxRunCached.playback.{番号}.lock` (OS 既定は `default`) | 変えられない |

- キャッシュ・フィラーの相対パスは、`UseExecutableBaseDirectory: true` (既定) なら exe 基準、`false` ならカレントディレクトリ基準で解決する
- キャッシュのフォルダ (`Filler.Enabled: true` ならフィラーのフォルダも) は起動時に書き込めるか確かめ、書き込めなければ設定エラー (終了コード 7) になる

## 仕組み

### セグメント単位キャッシュ

入力テキストは文単位のセグメントに分割され、セグメントごとにキャッシュされる。

**分割**

- 文末記号 (`。！？!?`) と改行で区切る。半角 `.` は直後が空白か文末のときだけ区切る (`3.14` や `Node.js` は割らない)
- 記号だけになる部分 (「えっ！？」の「？」、「「完了。」」の「」」など) は前の文にまとめる
- 100 文字を超える文は、後半にある最後の読点 (`、` `，`) か空白の直後で分割する。無ければ 100 文字目で切るが、絵文字などのサロゲートペアは割らない

**保存**

- キーはテキスト・エンジン・話者・速度・ピッチ・音量から作る SHA256。`.opus` (Ogg Opus) と `.meta.json` の組で保存する
- キャッシュは最後に使った日から `ExpirationDays` で期限切れになるので、よく使う文ほど作り直されない
- v2 以前の `.mp3` キャッシュは使われず、次回の新規合成時か `--clear` で削除される

**再生と合成**

- **部分変更でも再利用**: テキストの一部だけ変えても、変更されていない文はキャッシュヒット
- **即時再生**: キャッシュ済みセグメントは待機なしで再生開始 (エンジンの起動・接続は最初に合成が必要になったときに行うので、先頭がキャッシュ済みならエンジンの起動を待つ間にそこから鳴る)
- **壊れたキャッシュの作り直し**: 読めない `.opus` は削除して合成し直し、キャッシュを上書きする (`--cache-only` では合成せず、その文は失敗として数える)
- **バックグラウンド生成**: 未キャッシュ分は再生と並行して API 呼び出し
- **エンジン異常時の打ち切り**: 合成中にエンジンに接続できなくなった (接続拒否・503・タイムアウト) ら、残りの未キャッシュ分の合成は中止する (キャッシュ済みの文は再生する)。1 文ごとにリトライとタイムアウトを待ち続けない
- **リトライ**: エンジンが 500 / 502 / 503 / 504 / 429 を返したときは最大 3 回まで試す。タイムアウト (`ConnectionTimeout`) と、エンジンに接続できないときはリトライしない (上記のとおり残りの合成を中止する)

```bash
# 初回 (全セグメント生成)
VoicevoxRunCached.exe "おはようございます。今日は良い天気ですね。"

# 部分変更 (前半はキャッシュヒット、後半のみ生成)
VoicevoxRunCached.exe "おはようございます。今日は雨が降っています。"
```

### フィラー音声

有効にする手順は「[フィラーで待ち時間を埋める](#フィラーで待ち時間を埋める)」。ここでは鳴らし方と削除の規則をまとめる。

**鳴らし方**

- フィラーは `--init` で事前生成したキャッシュからしか再生されない (`--init` は `Filler.Enabled` に関係なく生成する。`Enabled` は再生にだけ効く)
- 次の文を待つ間に挟む。フィラーの読み込みや再生に失敗しても本編の読み上げは止めず、その回はフィラーを使わない (再生がタイムアウトしたときは、再生デバイスの異常として本編と同じく止める)

**文と回数 (エンジンごと)**

- `FillerTexts`: 各エンジンのセクションに書く。空なら `Filler.FillerTexts`
- `MaxFillersPerWait`: 次の文を待つ 1 回に鳴らす上限。`null` なら届くまで繰り返す、`0` ならそのエンジンでは鳴らさない
- 省略したときは VOICEVOX・CeVIO が上限なし、VOICEPEAK が 2。VOICEPEAK は 1 文の合成に 10 秒ほどかかるので、既定で長めの文を待ち 1 回に 2 回までにしている。VOICEPEAK で上限をなくすには `null` を書く

**削除**

- 最後に使ってから `Cache.ExpirationDays` 日を過ぎたフィラー (文を変える前の分・使わなくなった話者の分) は、`--init` のときに削除される
- 読み上げに使った話者の分は、読み上げるたびに延長される (`--no-play` でも延長される。`Filler.Enabled: false` の間は延長されない)
- 3.1.0 以前に作ったフィラーは、更新後の最初の `--init` では削除されず、そこから日数を数える (フィラーのフォルダに `last-used-tracking.txt` を作る)
- 生成の途中で落ちて残った一時ファイル (`.opus.tmp`) も `--init` のときに削除する
- 削除するのはフィラーのファイルだけで、同じフォルダのほかのファイルは消さない

### 同時実行

エンジンとキャッシュに触れる処理と、再生とを、一時フォルダのロックファイルで別々に直列化する。

| ロック | 対象 | ファイル |
|---|---|---|
| 合成 | 読み上げのキャッシュ参照・合成、`--init` (フィラー生成)・`speakers`・`--clear`、`devices --speak` (`devices` だけなら対象外) | `%TEMP%\VoicevoxRunCached.speech.lock` |
| 再生 | 読み上げの再生。デバイスごとに別 | `%TEMP%\VoicevoxRunCached.playback.{番号}.lock` (OS 既定は `default`) |

- 同時に起動すると、後から来た方は先の合成が終わるまで待つ。エンジンへの API 呼び出しや自動起動は 1 つずつになる (読み上げ中に `speakers` を実行すると、その読み上げの合成が終わるまで待つ)
- 合成が終わったら、再生の途中でも合成のロックを手放す。**同じデバイス**の読み上げは先の再生が終わるまで待ち (音声は重ならない)、**違うデバイス**の読み上げは同時に鳴る ([再生デバイスを選ぶ](#再生デバイスを選ぶ))
- 再生の順番を待っている間も合成は進めるので、先の再生が終わればすぐ鳴り始める
- OS 既定 (-1) と、それと同じ実体の番号 (たとえば 0) は別のデバイスとして扱うので、重なって鳴る。重ねたくない読み上げどうしは、-1 か番号のどちらかにそろえる
- 待っているものが 2 つ以上あるとき、どれが次に実行されるかは来た順とは限らない
- Ctrl+C で待機を中止できる。プロセスが強制終了されてもロックは OS が解放する
- `KeepEngineRunning: false` のときは、自分が起動したエンジンを合成のロックを解放する前に止める。待っていた後続の処理は、必要ならエンジンを起動し直す

**待つ上限** (`Lock.MaxWaitSeconds`、読み上げ・`--test` では `--max-wait` で上書き)

- 上限までに先の処理が終わらなければ、実行せずに終了コード 9 で終わる。未設定なら上限なし、0 なら待たない
- 読み上げでは、合成のロックと再生のロックを待った時間の合計で数える (合成にかかった時間は数えない)
- 再生のロックを待ちきれなかったときは、鳴らさずに終わる。合成した分はキャッシュに残り、`--out` も保存する
- Hooks が続けて発火したとき、古い通知を後からまとめて読み上げないようにしたい場合に使う
- `--init` / `--clear` / `speakers` / `devices --speak` は `--max-wait` を取らない。上限を変えるときは `VoicevoxRunCached.exe --clear --Lock:MaxWaitSeconds=60` のように設定の上書きで指定する

## トラブルシューティング

**「VOICEVOXエンジンが利用できません。」と表示される**
→ エンジンが起動しているか、`appsettings.json` の `BaseUrl` が正しいか確認。`AutoStartEngine: true` の場合は `EnginePath` 自動検出が失敗していないかも確認。起動したエンジンがすぐ終了したとき (`EnginePath` の間違いなど) は、`StartupTimeoutSeconds` を待たずにこのエラーになる。

**音声が再生されない・違うデバイスで鳴る**
→ まず `Audio.OutputDevice = -1` (OS デフォルト) を試す。番号で指定しているときは、デバイスの抜き差しで番号が変わっていないか `devices --speak` で確かめる ([再生デバイスを選ぶ](#再生デバイスを選ぶ))。

**音声の冒頭が途切れる (USB / Bluetooth デバイスで多発)**
→ `Audio.PrepareDevice: true` にして `PreparationDurationMs` を 300–500 に増やす。

**ユーザー辞書で読みを直したのに、古い読みのまま再生される**
→ キャッシュから再生されている。その文を `--refresh` 付きで一度実行する ([キャッシュを作り直す](#キャッシュを作り直す))。

**遅い・何が起きているか知りたい**
→ `--verbose` で再生にかかった時間と全体の実行時間を表示する (`--no-play` 時は全体の実行時間のみ)。

```
Audio playback completed in 2376.1ms
Total execution time: 2445.4ms
```

処理の詳細 (HTTP リクエストやキャッシュヒットなど) を見たい場合は `--log-level debug` を併用する。

## 開発

- **.NET 11 / C# 15** (primary constructors, collection expressions 等を活用)
- **NAudio**: 再生・WAV 処理・リサンプル
- **Concentus + Concentus.OggFile**: Ogg Opus のエンコード/デコード (純 C#、ネイティブ依存なし)
- **Serilog**: 構造化ログ (Console + File sink)
- **FluentValidation**: 設定検証
- **Jab**: DI コンテナ (Source Generator によるコンパイル時 DI。登録漏れはビルドエラー)
- **TUnit**: ユニットテスト

リポジトリ直下の `Taskfile.yml` でよく使う操作をまとめている (`task` で一覧表示)。実行には [Task](https://taskfile.dev) が要る (入れ方は「[ソースからビルド](#ソースからビルド)」)。

```bash
task build                        # Debug ビルド
task test                         # テスト
task check                        # format → Release ビルド → テスト (コミット前に実行)
task run -- "テストメッセージ"
task hooks:install                # コミット時のチェック (ビルド・整形) を有効にする (clone 後に 1 回)
```

バージョンは `src_dotnet/Directory.Build.props` の `<Version>` で一元管理している。リリース手順は [`doc/RELEASE.md`](./doc/RELEASE.md)。

開発で守ること (と回帰を検知するテスト) は [`CLAUDE.md`](./CLAUDE.md)、そうした理由は該当するコードのコメントにある。

## ライセンス

MIT License (本ツールのコード)。

生成した音声の利用は、VOICEVOX の[利用規約](https://voicevox.hiroshiba.jp/term/)と話者 (キャラクター) ごとの利用規約に従うこと。`--out` で保存した音声などを公開するときは、多くの話者で「VOICEVOX:ずんだもん」のようなクレジット表記が必要になる。AivisSpeech では、使う音声合成モデルのライセンスに従う。

## 関連リンク

- [VOICEVOX 公式](https://voicevox.hiroshiba.jp/)
- [Claude Code](https://claude.ai/code)
- [voicevox-run-cached-claude-hooks](https://github.com/ted-sharp/voicevox-run-cached-claude-hooks) (Claude Code Hooks への組み込み例)
