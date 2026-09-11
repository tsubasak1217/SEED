# プロジェクト概念（.seedproj）

SEED エディタは Visual Studio の `.sln` に相当する **プロジェクト** を持つ。
ゲーム 1 本 = 1 プロジェクト = 1 フォルダで、エディタはそのフォルダを基準に
アセット・プラグイン・キャッシュ・セーブ・ビルド出力の位置を決める。

この文書がプロジェクト概念の正典。関連する実装は `editor/src/Project/`
（モデルとパス解決）、`editor/src/Startup/`（スタート画面と起動時の解決）にある。

---

## 1. フォルダ構成

```
<ProjectRoot>/
  <Name>.seedproj      プロジェクトファイル（JSON。この 1 枚が入口）
  assets/              ゲームのアセット。assets:// のルート
    project_settings.json     ゲーム名・開始シーン・シーン一覧・解像度・プラグイン有効化
    packaging_settings.json   パッケージ化の設定
    scenes/Main.scene         新規作成時に置かれる開始シーン
  plugins/             ネイティブプラグイン DLL

  cache/               モデル等の変換キャッシュ（ランタイムが生成）
  save/                セーブデータ（ランタイムが生成）
  logs/                ゲーム実行ログ
  build/               パッケージ化の出力（build/windows など）
```

`cache/` `save/` `logs/` `build/` は **実行時・ビルド時に自動生成される**。
エディタは新規作成時にも作らない（何も起きていないのに空フォルダが並ぶのを避けるため）。
バージョン管理からは除外してよい。

### ランタイムとの契約（変更してはいけない前提）

ランタイムは `--assets-root` で受け取ったアセットルートの **親フォルダ** から
実行時フォルダを導出する。したがって **`assets` はプロジェクトルート直下の 1 階層**でなければならない。

| フォルダ | ランタイム側の解決箇所 |
|---|---|
| `cache/` | `runtime/src/engine/core/loader/asset_cache.rs` の `cache_dir()` |
| `save/` | `runtime/src/engine/core/save/path.rs` の `decide_save_dir()` |
| `plugins/` | `runtime/src/engine/core/app_base/app/app_init.rs`（名前は `"plugins"` 固定） |

エディタ側の導出は `editor/src/Project/ProjectPaths.cs` にあり、
両者が一致していることは `editor/tests/ProjectSystemTests` で固定してある。

> `.seedproj` の `plugins_dir` を `"plugins"` 以外にすると、エディタは新しい名前を使うが
> **ランタイムは `plugins/` を見続ける**（ランタイム側が固定のため）。既定のままにすること。

---

## 2. `.seedproj` の仕様

JSON。形式の正典は `editor/src/Project/SeedProjectFile.cs`。

```json
{
  "format_version": 1,
  "name": "MyGame",
  "display_name": "私のゲーム",
  "engine_version": "0.1.0",
  "created_at": "2026-09-11T12:34:56.0000000+00:00",
  "assets_dir": "assets",
  "plugins_dir": "plugins"
}
```

| キー | 型 | 意味 |
|---|---|---|
| `format_version` | int | 形式バージョン。**エディタより大きい値は拒否**して開かない |
| `name` | string | 識別名。既定ではファイル名（拡張子なし）と同じ。空なら読み込み時にファイル名で補完 |
| `display_name` | string | 画面に出す名前。空なら `name` を使う |
| `engine_version` | string | 作成したエディタの版（記録専用。`SEEDEditor.csproj` の `<Version>` 由来） |
| `created_at` | string | 作成日時（ISO 8601。記録専用） |
| `assets_dir` | string | アセットルート（ルートからの相対）。既定 `"assets"` |
| `plugins_dir` | string | プラグインフォルダ（ルートからの相対）。既定 `"plugins"` |

- **知らないキーは保存で失われない**（`JsonExtensionData` で保全する）。
  新しいエディタが足したキーを、古いエディタが開いて保存しても消えない。
- キーを増やすときは `format_version` を上げず、**既定値を持つ任意キー**として足す。
  読めなくなる変更をするときだけ上げる。

---

## 3. 起動と、開くプロジェクトの決まり方

`SEEDEditor.exe` は 1 本で「スタート画面」と「エディタ本体」を兼ねる。
どちらを出すかは `editor/src/App.xaml.cs` の `OnStartup` が決める
（`StartupUri` は使っていない）。

### 起動引数・環境変数

| 指定 | 形式 | 意味 |
|---|---|---|
| プロジェクト | `--project <path>` / `--project=<path>` | 開くプロジェクト。`.seedproj` でもフォルダでも可 |
| プロジェクト | 位置引数 `<path>.seedproj` | エクスプローラーのダブルクリックがこの形で渡してくる |
| プロジェクト | 環境変数 `SEED_PROJECT` | 引数を渡せない起動経路の保険 |
| ヘッドレス | `--headless` / `SEED_HEADLESS=1` | 画面外配置・モーダル抑止（`docs/editor_mcp.md`） |
| シーン | `--scene <path>` / `--scene=<path>` | 起動時に開く `.scene`（実在する `.scene` のみ有効） |
| AI ブリッジ | `--ai-port` / `--ai-token` | MCP からの束縛用（`docs/editor_mcp.md`） |

フォルダを渡した場合は中の `.seedproj` を探す。複数あればフォルダ名と同じ stem を優先する。

### 解決順（`editor/src/Startup/ProjectStartupResolver.cs`）

1. **プロジェクトの明示指定がある**（`--project` > 位置引数 > `SEED_PROJECT`）
   - 実在すればそれを開く。
   - 実在しなければ **他のプロジェクトへは絶対に倒さない**。
     通常起動ならスタート画面にエラーを出し、ヘッドレスなら終了コード **2** で終了する。
     （黙って別のプロジェクトを開くと、利用者は意図しないデータを編集してしまう）
2. **指定が無く、ヘッドレス**
   - 「最近開いたプロジェクトの先頭で実在するもの」を開く。
   - 候補が無ければ終了コード **2** で終了（画面を出せないので選ばせられない）。
3. **指定が無く、通常起動**
   - スタート画面を出す。

`.seedproj` が壊れている・読めない場合は、通常起動ならスタート画面にその理由を表示し、
ヘッドレスなら終了コード **3** で終了する。

### プロジェクトの切り替え

エディタ本体の「ファイル → 別のプロジェクトを開く...」は、
**新しいプロセスをスタート画面で起動して現在のウィンドウを閉じる**。
同一プロセスで差し替えないのは、各パネル・`RuntimeManager`・ランタイム子プロセスが
起動時のアセットルートを前提に状態を持っているため。
プロセスを分ければ「古いプロジェクトの状態が残る」事故が原理的に起きない。

---

## 4. ファイル関連付け（`.seedproj` のダブルクリック）

スタート画面の「.seedproj をこのエディタに関連付ける」で、
`HKEY_CURRENT_USER\Software\Classes` 配下へ登録する（**管理者権限は不要**）。

| キー（HKCU 相対） | 既定値 |
|---|---|
| `Software\Classes\.seedproj` | `SEED.Project` |
| `Software\Classes\SEED.Project` | `SEED プロジェクト` |
| `Software\Classes\SEED.Project\DefaultIcon` | `"<SEEDEditor.exe>",0` |
| `Software\Classes\SEED.Project\shell\open\command` | `"<SEEDEditor.exe>" "%1"` |

- 値の組み立ては `editor/src/Project/FileAssociationValues.cs`（純関数。テストで固定）。
- レジストリへの反映・解除・状態確認は `editor/src/Project/FileAssociation.cs`。
- 登録・解除の直後に `SHChangeNotify(SHCNE_ASSOCCHANGED)` を投げる
  （これが無いとエクスプローラーが古い関連付けをキャッシュしたままになる）。
- 同じボタンが状態に応じて「関連付ける / 解除する」に切り替わる。

---

## 5. 最近開いたプロジェクト

`editor/settings/recent_projects.json`（エディタ本体側の設定。プロジェクトを跨いで共有）。

```json
{
  "format_version": 1,
  "entries": [
    { "path": "C:\\games\\MyGame\\MyGame.seedproj", "name": "私のゲーム",
      "last_opened": "2026-09-11T12:34:56.0000000+00:00" }
  ]
}
```

- 新しい順。上限 20 件（`RecentProjectsStore.MAX_ENTRIES`）。
- 記録されるのは **実際に開けたプロジェクトだけ**（壊れたパスを積み上げない）。
- スタート画面では実在しない行を薄く表示し、右クリックで「一覧から外す」「フォルダを開く」。

### 旧形式からの移行

この名前のファイルは、以前は **最近開いたシーン（`.scene` 絶対パスの文字列配列）** だった。
初回に一度だけ中身を見て、ルートが配列なら `recent_scenes.json` へ移してから
新形式で作り直す（`RecentProjectsStore.MigrateLegacySceneList`）。
最近のシーンは `editor/src/ProjectSettings/RecentScenesManager.cs` が
`recent_scenes.json` を読み書きする（読み書きの前に必ず上の移行を先に走らせる）。

---

## 6. パッケージ化の出力先

`packaging_settings.json` の `output_path` が **空のときの既定**は
`<ProjectRoot>/build/<platform>`（`windows` / `macos` / `android` / `ios` / `ps5` / `switch`）。
決定は `editor/src/Packaging/PackagingOutputDefaults.cs` の 1 か所で、
パッケージ化ウィンドウの出力フォルダ欄の下に実際のパスをヒントとして表示する。

`cargo build` を実行する `runtime/` フォルダは **エンジン側の資産** であり、
プロジェクトの外にある。ランタイム exe の位置
（`runtime/target/<debug|release>/SEED.exe`）から解決する。

---

## 7. 実装の地図

| やること | ファイル |
|---|---|
| `.seedproj` の読み書き | `editor/src/Project/SeedProjectFile.cs` |
| 各フォルダの絶対パス導出 | `editor/src/Project/ProjectPaths.cs` |
| いま開いているプロジェクト | `editor/src/Project/ProjectContext.cs` |
| 新規プロジェクトの生成 | `editor/src/Project/ProjectCreator.cs` |
| 最近のプロジェクト一覧 | `editor/src/Project/RecentProjectsStore.cs` |
| 関連付け（値／レジストリ） | `editor/src/Project/FileAssociationValues.cs` / `FileAssociation.cs` |
| エディタ版の取得 | `editor/src/Project/EditorVersion.cs` |
| 起動引数の解析 | `editor/src/Headless/EditorStartupOptions.cs` |
| 起動時の振り分け判断 | `editor/src/Startup/ProjectStartupResolver.cs` |
| スタート画面 | `editor/src/Startup/StartWindow.xaml(.cs)` |
| 新規作成ダイアログ | `editor/src/Startup/NewProjectDialog.xaml(.cs)` |
| 起動の分岐 | `editor/src/App.xaml.cs` |
| エディタ設定フォルダの解決 | `editor/src/Settings/EditorPaths.cs` |
| 単体テスト | `editor/tests/ProjectSystemTests/` |

`MainWindow.AssetsPath` は `ProjectContext.AssetsDir` を返すだけの読み取り窓口。
アセットルートの答えは `ProjectContext` 1 か所にしかない。

### 拡張点: 生成後フック

`ProjectCreator.ProjectCreated`（`event Action<ProjectPaths>`）は
新規プロジェクトの生成が終わった直後に発火する。
テンプレートライブラリからの初期コンテンツ投入はここへ差し込む。
フックが例外を投げても生成は成功扱いになる（追加投入の失敗で、
作ったばかりのプロジェクトを捨てさせないため）。

## 既存ゲーム「わらしべフィッシング」の移行記録（2026-09-11）

- 置き場: `projects/WarashibeFishing/`（`WarashibeFishing.seedproj` + `assets/` + `plugins/`）。同一リポジトリで追跡し、
  git の扱いは従来どおり文字系のみ（画像・モデル・音声・地形ボクセル・フォントは `.gitignore` で除外）。
- 旧構成 `runtime/assets`（`D:\SEED_assets` への NTFS ジャンクション）は廃止した。`D:\SEED_assets` は削除しておらず、
  動作確認後に手で消してよい（`runtime/assets_realdir_backup_20260903/` と `runtime/plugins/GameTools/` も同様）。
- `templates/` は参照している 5 ファイル（フォント 2・スカイボックス 1・砂浜テクスチャ 2）だけ `assets/templates/` に残し、
  残りはテンプレートライブラリ `<repo>/templates/` へ移した（`docs/template_library.md`）。
- 旧ジャンクション時代にエディタが書き込んだ絶対パス参照（`C:\...untimessets\...`）は、
  ゲーム側 185 か所・ライブラリ側 554 か所を `assets://` 相対へ正規化した。
- `project_settings.json` は以前 `.gitignore` で除外していたが、プロジェクトの一部として追跡に含めた。
- プラグインは `projects/WarashibeFishing/plugins/`（GameTools・SamplePlugin）。`game_tools` の `build.rs` はここへ配置する。
- 実行時生成物はプロジェクト直下の `cache/`・`save/`・`logs/`・`build/`（ランタイムは `assets/` の親を基準に解決する）。
- エディタ設定（`editor/settings/`）はエンジン側のまま。起動時のシーン復元・最近のシーン一覧はプロジェクトをまたいで共有される
  （プロジェクト単位にするのは `docs/backlog.md` の課題）。
