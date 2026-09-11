# ランタイムのビルド構成（Debug / Develop / Release）

エディタの Play が起動する `SEED.exe` を、どの Cargo プロファイルでビルドするか選べる。
選択はツールバーの Play ボタンの隣にあるコンボボックスで行う。**既定は Develop**。

---

## 1. なぜ 3 つあるのか

`cargo build`（dev プロファイル）で作った `SEED.exe` は、自前コード（`SEED` クレート）が
まったく最適化されない。同じシーンで計測すると **1 フレーム 18 ms 対 7 ms**、
つまり release のおよそ 2.5 倍の時間がかかっていた。
一方 release はデバッグ情報が乏しく、初回ビルドも長い。

そこで中間の構成（Develop）を用意し、普段の Play の既定にしている。

| 構成 | cargo プロファイル | 出力先 | 自前コードの最適化 | デバッグ情報 | 用途 |
|---|---|---|---|---|---|
| Debug   | `dev`     | `runtime/target/debug/`   | なし（opt-level 0） | あり | ステップ実行で隅々まで追いたいとき。最も遅い |
| Develop | `develop` | `runtime/target/develop/` | 弱め（opt-level 1） | あり | **既定**。普段の Play |
| Release | `release` | `runtime/target/release/` | 最大              | 乏しい | 配布相当の速度を測りたいとき |

Develop は `Cargo.toml` で `inherits = "dev"` にしてあるため、
`[profile.dev.package.*]` に並べてある依存クレート個別の最適化
（wgpu / rapier / nalgebra / 画像デコードなど）も **そのまま引き継ぐ**。
`debug = true` / `debug-assertions = true` も dev 由来で維持される。

```toml
# ルート Cargo.toml
[profile.develop]
inherits  = "dev"
opt-level = 1
```

実測（本プロファイルを最初に入れたとき）:
`cargo build --profile develop` のフルビルドは **約 6 分 40 秒**、成果物は約 33 MB。

---

## 2. 使い方

1. ツールバー（Play ボタンの隣）のコンボボックスで構成を選ぶ。
2. 選ぶと Edit ランタイムがいったん終了し、**その構成で `cargo build --profile <プロファイル>`**
   を実行してから起動し直す。開いていたシーンは自動で読み直される。
3. その構成をまだビルドしていなければ、ここでフルビルドが走る（数分〜十数分）。
   2 回目以降は差分ビルドなので速い。

選んだ構成は `editor/settings/editor_preferences.json` の
`runtime_build_config_id` に保存され、次回起動時も同じ構成で立ち上がる。

### 切り替えられないとき

- **Play 中は無効**（コンボがグレーアウトする）。Stop してから選び直す。
- **未保存の変更があると確認ダイアログが出る**。ランタイムを建て直すため、
  保存していない編集内容は失われる。

### 構成ごとに target が分かれる意味

構成ごとに出力フォルダが違うので、ビルドキャッシュは**共有されない**。
Debug ↔ Develop を往復してもお互いを作り直すことはないが、
その代わり構成ごとに数 GB の `target/<構成>/` ができる。
ディスクを空けたいときは使っていない構成のフォルダを消してよい
（次に選んだときフルビルドになるだけ）。

---

## 3. 構成の定義ファイル

構成の一覧は **`editor/config/runtime_build_configs.json`**（git 管理対象）にある。
エディタのコードには構成名が埋まっていないので、増やすときはこの JSON を編集する。

```json
{
  "format_version": 1,
  "default": "develop",
  "configs": [
    {
      "id": "develop",
      "label": "Develop",
      "cargo_profile": "develop",
      "target_dir": "develop",
      "description": "最適化 1 ＋ デバッグ情報。普段の Play 向け（既定）"
    }
  ]
}
```

| キー | 意味 |
|---|---|
| `format_version` | ファイル書式のバージョン。現在 `1`。新しすぎる値でも読める範囲は読んで警告だけ出す |
| `default` | 環境設定に選択が無いときに使う構成の `id` |
| `configs[].id` | 識別子。`editor_preferences.json` に保存されるのはこの値。**一度決めたら変えない**（変えると利用者の選択が既定に戻る） |
| `configs[].label` | コンボボックスの表示名 |
| `configs[].cargo_profile` | `cargo build --profile <ここ>` に渡す名前。ルート `Cargo.toml` の `[profile.*]` と一致させる |
| `configs[].target_dir` | `runtime/target/<ここ>/SEED.exe` を探す。**`cargo_profile` とは別**（`dev` プロファイルの出力先は `debug`） |
| `configs[].description` | コンボボックスのツールチップ |

### 構成を増やす手順

1. ルート `Cargo.toml` に `[profile.<名前>]` を足す（`inherits = "dev"` か `"release"`）。
2. `editor/config/runtime_build_configs.json` の `configs` へ 1 件足す。
   `cargo_profile` はプロファイル名、`target_dir` は cargo の出力フォルダ名
   （`dev` 以外はプロファイル名と同じ）。
3. エディタを再起動する。コンボに増えている。C# 側の変更は不要。

### 既定を変えるには

`runtime_build_configs.json` の `"default"` を別の `id` にする。
ただし既に選択を保存している利用者には効かない（その人の
`editor_preferences.json` の `runtime_build_config_id` が優先される）。

### 壊れていても起動する

この JSON が無い・壊れている・有効な構成が 1 件も無い場合は、
コードに埋め込んだ既定（Debug / Develop / Release の 3 件、既定は Develop）へ
フォールバックする。理由は `editor/logs/SEEDEditor.log` に `[BuildConfig]` 付きで残る。
個別の構成だけが不正（必須キーの欠落・`id` の重複）なら、その 1 件を捨てて残りを使う。

---

## 4. exe の探索順と `SEED_RUNTIME_EXE`

エディタが起動する `SEED.exe` は次の順で決まる。

| 優先 | 探索先 | 用途 |
|---|---|---|
| 1 | 環境変数 **`SEED_RUNTIME_EXE`**（実在するファイルを指しているときだけ） | 計測・自動検証で別ビルドを使わせる |
| 2 | エディタ exe と同じフォルダの `SEED.exe` | 配布形態のエディタ |
| 3 | `<リポジトリ>/runtime/target/<構成の target_dir>/SEED.exe` | 開発形態（ここで構成が効く） |

**`SEED_RUNTIME_EXE` は構成より強い。** 設定されている間はコンボで何を選んでも
その exe が起動する（コンボの選択は保存されるだけ）。
利用者のエディタが `SEED.exe` をロックしていて上書きできない状況で、
別の `--target-dir` へビルドしたものを使いたいときに設定する。

リポジトリの位置は「`runtime/Cargo.toml` を持つフォルダ」を上へ辿って探す。
エディタ exe 側から見つからなければカレントディレクトリからも探す
（`dotnet build -p:OutputPath=<一時フォルダ>` でビルドしたエディタを
リポジトリルートから起動しても解決できるようにするため）。
どちらでも見つからない場合だけ、従来どおりエディタ exe の 4 階層上をリポジトリとみなす。

---

## 5. 関連するコード

| 役割 | ファイル |
|---|---|
| 構成 1 件のモデル | `editor/src/Runtime/BuildConfig/RuntimeBuildConfig.cs` |
| カタログ（JSON 読み込み・検証・フォールバック） | `editor/src/Runtime/BuildConfig/RuntimeBuildConfigCatalog.cs` |
| 構成 → exe の絶対パス | `editor/src/Runtime/BuildConfig/RuntimeExeLocator.cs` |
| exe → cargo の作業ディレクトリ | `editor/src/Runtime/BuildConfig/RuntimeSourceDirLocator.cs` |
| 構成 → `cargo build` の引数 | `editor/src/Runtime/BuildConfig/CargoBuildCommand.cs` |
| 切り替え（停止 → 張り替え → ビルド → 再起動） | `editor/src/Runtime/RuntimeManager.cs::SwitchBuildConfigAsync` |
| コンボボックスの制御 | `editor/src/MainWindow.RuntimeBuildConfig.cs` |
| 選択の永続化 | `editor/src/EditorPreferences.cs`（`runtime_build_config_id`） |
| 単体テスト | `editor/tests/RuntimeBuildConfigTests/` |

パッケージ化（配布ビルド）はこの構成とは独立していて、
パッケージ化ウィンドウ側の `BuildType`（Debug / Release）で決まる。
