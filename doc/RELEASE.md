# リリース手順

CI は使わない。確認・ビルド・ZIP 作成はすべてローカルで行い、GitHub Release に ZIP を手で添付する。

以下の `X.Y.Z` はリリースするバージョン (例: `3.0.0`)。

## 前提

- バージョンは `src_dotnet/Directory.Build.props` の `<Version>` 1 か所。開発中から次のバージョンにしておく (手順 6)
- `RELEASE_NOTES.md` の先頭に `## Unreleased (X.Y.Z)` の節があり、変更を書き足してある
- バージョンの上げ方: キャッシュ形式・`appsettings.json`・CLI の非互換 (利用者が作り直し・書き換えを要するもの) はメジャー、機能追加はマイナー、修正のみはパッチ
- GitHub Release を `gh` で作るなら `gh auth login` 済みであること

## 1. 確認

```bash
git switch main
git pull
git status                # クリーンであること
task check                # format → Release ビルド → テスト
task run:test             # 実機で鳴ることを確認 (エンジンは自動起動)
```

`task check` の `task format` で差分が出たら、それをコミットしてからやり直す。

## 2. バージョンとリリースノートを確定する

- `Directory.Build.props` の `<Version>` が `X.Y.Z` であることを確認する
- `RELEASE_NOTES.md` の `## Unreleased (X.Y.Z)` を `## vX.Y.Z - YYYY-MM-DD` (リリース日) に書き換える
- 前回のタグからのコミットと見比べて、書き漏れがないか確認する

```bash
git log --oneline v<前回のバージョン>..HEAD
```

書き方 (開発中に書き足すときも同じ):

- 1 項目 1 行。利用者から見た変化だけを書き、実装の詳細・経緯・内部のリファクタリングは書かない (git log と CLAUDE.md にある)
- 破壊的変更 (キャッシュの作り直し・`--init` のやり直し・設定や呼び出し側の書き換えが必要なもの) は節の先頭に **破壊的変更** としてまとめる
- 分類は `破壊的変更` / `CLI/コマンド` / `改善` / `修正` / `削除` / `開発環境` から必要なものだけ。細かい修正は「その他 … を多数修正」にまとめる

## 3. コミットとタグ

タグは `vX.Y.Z` の注釈付きタグにする (既存のタグと同じ形)。

```bash
git commit -am "vX.Y.Z リリース"
git tag -a vX.Y.Z -m "vX.Y.Z"
git push origin main vX.Y.Z
```

## 4. 配布用 ZIP を作る

```bash
task publish:zip
# → src_dotnet/publish/VoicevoxRunCached-vX.Y.Z-win-x64.zip
```

Task が無い環境では `src_dotnet/_publish_zip.cmd` でも同じものができる。

ZIP を任意のフォルダに展開して確認する:

- ファイル名のバージョンが `X.Y.Z` であること
- `VoicevoxRunCached.exe` と `appsettings.json` が入っていること
- `VoicevoxRunCached.exe --help` が動き、`"テストです。"` が鳴ること (.NET ランタイムを入れていない環境でも動く self-contained の単一 EXE)

## 5. GitHub Release を作る

README の「インストール」は、利用者が [Releases](https://github.com/ted-sharp/voicevox-run-cached/releases) から ZIP を取る前提になっている。

本文は `RELEASE_NOTES.md` の該当する節だけを使う:

```bash
VER=X.Y.Z
awk -v h="## v$VER " 'index($0, h) == 1 {f = 1; next} /^## / {f = 0} f' RELEASE_NOTES.md > "$TEMP/release-notes-v$VER.md"
gh release create "v$VER" "src_dotnet/publish/VoicevoxRunCached-v$VER-win-x64.zip" \
  --title "v$VER" --notes-file "$TEMP/release-notes-v$VER.md"
```

ブラウザで作る場合は、Releases → Draft a new release で `vX.Y.Z` タグを選び、同じ本文を貼って ZIP を添付する。

## 6. 次の開発を始める

次のバージョン `X.Y.(Z+1)` などを決めて:

- `Directory.Build.props` の `<Version>` を上げる
- `RELEASE_NOTES.md` の先頭 (`# VoicevoxRunCached リリースノート` の直後) に `## Unreleased (次のバージョン)` を足す

```bash
git commit -am "次の開発バージョンを X.Y.(Z+1) にする"
git push origin main
```

## やり直し

push 前にタグを間違えた場合は `git tag -d vX.Y.Z` で消して付け直す。push 後・Release 公開後にタグを付け替えると、すでに取得した人とずれるので、修正はパッチリリース (`X.Y.(Z+1)`) で出す。
