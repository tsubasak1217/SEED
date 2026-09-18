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
    editor/view/       エディタ視点（デバッグカメラの位置・向き）のユーザー別サイドカー
                       `<シーン相対パス>.view.json`。`.scene` には書かない（共有すると必ず衝突するため）
  save/                セーブデータ（ランタイムが生成）
  logs/                ゲーム実行ログ
  build/               パッケージ化の出力（build/windows など）
```

`cache/` `save/` `logs/` `build/` は **実行時・ビルド時に自動生成される**。
`cache/editor/view/` はエディタ専用のセッション状態で、失っても視点が既定に戻るだけ
（実装: `runtime/src/engine/core/app_base/editor_view_state.rs`。fov / far / speed などの共有設定は
従来どおり `.scene` の `settings.debug_camera`）。
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

### 3.1 engine_version の不一致チェック

チーム制作でエディタ（エンジン）を更新しながらゲームを作る運用を想定し、
プロジェクトを**開く直前**に `.seedproj` の `engine_version` と、実行中エディタの版
（`EditorVersion.Current`。`SEEDEditor.csproj` の `<Version>` 由来）を比較する。

判定ロジックは `editor/src/Project/EngineVersionCheck.cs`（WPF 非依存の純粋関数。
`editor/tests/ProjectSystemTests` で検証）。比較は `"major.minor.patch[...]"` を
`.` 区切りの非負整数列として扱い、`+` 以降のビルドメタデータと `-` 以降の
プレリリース識別子は比較前に落とす。要素数が異なる場合は短い方を 0 で補う
（`"0.1.0"` と `"0.1.0.0"` は同値）。

| 判定 (`EngineVersionComparison`) | 意味 | 挙動 |
|---|---|---|
| `Same` | 一致 | 何もしない |
| `ProjectOlder` | プロジェクトの方が古い版で作られた | ダイアログで「このまま開く」／「engine_version を更新して開く」を選ばせる |
| `ProjectNewer` | プロジェクトの方が新しい版で作られた | 強めの警告ダイアログで「このまま開く」／「開かない（スタート画面へ戻る）」を選ばせる |
| `Unknown` | どちらか（大抵は旧プロジェクトで `engine_version` が空）が解釈できない | ログにのみ記録し、ダイアログは出さない |

通知・更新（「更新して開く」を選んだ場合の `.seedproj` 保存し直しを含む）は
`editor/src/Project/EngineVersionGate.cs` が担う。呼び出し箇所は
`editor/src/App.xaml.cs`（`OpenProjectAndShowEditor`）と
`editor/src/Startup/StartWindow.xaml.cs`（`OpenProject`）の 2 か所で、
どちらも `ProjectContext.OpenFromFile` の直前に `EngineVersionGate.CheckBeforeOpen` を呼ぶ。
「更新して開く」を選んだ場合の保存は既存の `SeedProjectFile.Save` をそのまま使うため、
未知キー（`JsonExtensionData`）は失われない。

ダイアログは既存の `editor/src/Headless/EditorDialogs.cs` を経由するため、
ヘッドレス起動（`docs/editor_mcp.md`）では自動的に抑止され、ログへ 1 行落ちるだけで
**「このまま開く・engine_version は更新しない」側で続行する**（AI エージェント運用等の
自動化を止めないための意図的な既定値。通常起動時の「安全側＝触らない」という
既定方針とは異なる、本機能固有の例外）。判定結果は `Same` を含め、
`EditorLog` へ必ず 1 行残る。

### 3.2 アセット形式のアップグレード（ツールメニュー）

`engine_version` がエンジン全体の版であるのに対し、**アセットは形式ごとに版を持つ**
（`.scene` / `.actor` / `.anim` …）。正典は `docs/asset_migration.md`。

- **プロジェクトを開いた直後**、バックグラウンドで
  `SEED.exe --upgrade-project <プロジェクト> --dry-run` を 1 回だけ走らせる
  （1 バイトも書き込まない）。古い形式のファイルがあれば Output ログとトーストで
  「古い形式のファイルが n 件あります。ツール → プロジェクトの形式をアップグレード で更新できます」と知らせる。
  ランタイム exe が未ビルドなら黙ってログだけ。ヘッドレス起動では通知しない。
  実装は `editor/src/Migration/ProjectUpgradeNotice.cs`（起こすのは `MainWindow.Migration.cs`）。
- **「ツール → プロジェクトの形式をアップグレード...」** で
  `editor/src/Migration/Presentation/ProjectUpgradeWindow.cs` が開く。
  開いた直後に dry-run を実行して「形式ごとの件数・対象ファイル・未来版／失敗」を見せ、
  「アップグレードを実行」を押すと、
  ①対象ファイルのロックを 1 回でまとめて確認（他の人がロック中なら止める）
  →②実行 →③結果表示 →④`VersionControlService.RequestRefresh()` で VCS パネルへ反映、
  の順に進む。`.actor` を書き換えるとシーンの `prefab_hash` が貼り直されるため、
  VCS パネルには `.scene` も変更として並ぶ（正常）。
- **オーナーが 1 回実行して送信する**運用を想定している。開いただけでは誰のファイルも変わらない。

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

### 5.1 タスクバーのジャンプリスト（右クリックの「最近」欄）

タスクバーの SEED アイコン（起動中でもピン留めでも）を右クリックすると、「最近」欄に最近開いたプロジェクトの
`.seedproj` が並ぶ（Visual Studio の「最近使ったもの」相当）。

- 項目は `.seedproj` そのもの（JumpPath）。クリックすると関連付けで SEEDEditor.exe が起動してそのプロジェクトが開き、
  右クリックには Windows 標準の「フォルダーの場所を開く」が付く（プロジェクトのフォルダが開く）。
- 表示名は Windows が `.seedproj` のファイル名から決める（JumpPath は表示名を持てない）。
- 一覧の下のアプリ項目「SEED」を右クリック →「ファイルの場所を開く」でエンジン（exe）のフォルダが開く。
  以前あった「スタート画面を開く」タスクは、アプリ項目のクリックと同じ動作だったため廃止した。
- JumpPath は「その exe が `.seedproj` の登録ハンドラ」のときだけ表示されるため、更新前に関連付け
  （HKCU の ProgID と `Applications\SEEDEditor.exe`）を確認し、無ければ登録する。ヘッドレス起動では触らない。
- 元データはスタート画面と同じ `recent_projects.json`。実在する `.seedproj` だけを最大 10 件並べる。
  更新はプロジェクトを開いたとき・一覧から外したとき・スタート画面を出したとき（`ProjectJumpList.Refresh`）。
- 項目の組み立て（`ProjectJumpListBuilder`）は WPF 非依存で、`ProjectSystemTests` で検証している。
- 一覧は exe のパス単位で OS が保持する（`%APPDATA%\Microsoft\Windows\Recent\CustomDestinations`）。
  別の場所にビルドしたエディタは別の一覧になる。

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
| engine_version の不一致判定（純粋ロジック） | `editor/src/Project/EngineVersionCheck.cs` |
| engine_version 不一致時の通知・更新（ダイアログ・保存） | `editor/src/Project/EngineVersionGate.cs` |
| アセット形式の版の表・覗き読み・変換の呼び出し | `editor/src/Migration/`（正典は `docs/asset_migration.md`） |
| 形式アップグレードのメニュー・ダイアログ・起動時の案内 | `editor/src/MainWindow.Migration.cs` / `editor/src/Migration/Presentation/ProjectUpgradeWindow.cs` / `editor/src/Migration/ProjectUpgradeNotice.cs` |
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

- 置き場: `D:\SEED_projects\WarashibeFishing\`（`WarashibeFishing.seedproj` + `assets/` + `plugins/`）。
  当初は `projects/WarashibeFishing/` として同一リポジトリで文字系だけ追跡していたが、2026-09-15 に
  リポジトリの外へ移し、SEED リポジトリの追跡から外した（`projects/` は `.gitignore` で丸ごと除外）。
  ゲームプロジェクトのバージョン管理はエンジン（git）とは別系統にする（Lore を予定）。
  ドキュメント中の `<project>/` はこのプロジェクトフォルダを指す。
- 旧構成 `runtime/assets`（`D:\SEED_assets` への NTFS ジャンクション）は廃止した。`D:\SEED_assets` は削除しておらず、
  動作確認後に手で消してよい（`runtime/assets_realdir_backup_20260903/` と `runtime/plugins/GameTools/` も同様）。
- `templates/` は参照している 5 ファイル（フォント 2・スカイボックス 1・砂浜テクスチャ 2）だけ `assets/templates/` に残し、
  残りはテンプレートライブラリ `<repo>/templates/` へ移した（`docs/template_library.md`）。
- 旧ジャンクション時代にエディタが書き込んだ絶対パス参照（`C:\...untimessets\...`）は、
  ゲーム側 185 か所・ライブラリ側 554 か所を `assets://` 相対へ正規化した。
- `project_settings.json` は以前 `.gitignore` で除外していたが、プロジェクトの一部として追跡に含めた。
- プラグインは `<project>/plugins/`（GameTools・SamplePlugin）。`game_tools` の `build.rs` はここへ配置する。
- 実行時生成物はプロジェクト直下の `cache/`・`save/`・`logs/`・`build/`（ランタイムは `assets/` の親を基準に解決する）。
- エディタ設定（`editor/settings/`）はエンジン側のまま。起動時のシーン復元・最近のシーン一覧はプロジェクトをまたいで共有される
  （プロジェクト単位にするのは `docs/backlog.md` の課題）。
