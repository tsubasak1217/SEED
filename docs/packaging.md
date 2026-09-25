# パッケージ化（配布ビルド）

エディタの「パッケージ化」ウィンドウ（`editor/src/Packaging/`）が行う処理と、
`assets.pak` に何が入るかの規則をまとめる。エディタを起動せずに同じ PAK を作るツール（SeedPak）と、
Android の APK へ同梱する形は §10。

関連ファイル:

| ファイル | 役割 |
|---|---|
| `editor/src/Packaging/PackagingWindow.xaml.cs` | UI・cargo build 実行・進捗表示 |
| `editor/src/Packaging/PackageLayout.cs` | **配布物のフォルダ構成の正典（エディタ側）**。`bin` / `caches` / `logs` / `saved` の名前と旧レイアウトの後始末 |
| `runtime/src/engine/core/package_layout.rs` | **同（ランタイム側）**。両者のフォルダ名は一致必須（双方のテストで文字列固定） |
| `editor/src/Packaging/PackagingData.cs` | 設定の永続化（`{assets}/packaging_settings.json`） |
| `editor/src/Packaging/Collect/PackagingRules.cs` | **収録規則の正典**（走査拡張子・同伴ファイル・既定の除外） |
| `editor/src/Packaging/Collect/AssetReferenceScanner.cs` | テキスト 1 本からの参照抽出 |
| `editor/src/Packaging/Collect/AssetCollector.cs` | 参照グラフの閉包を取り、収録ファイルを決める |
| `editor/src/Packaging/Collect/AssetPackagingSettings.cs` | 収録ルールのユーザー設定 |
| `editor/src/Packaging/Pak/PakWriter.cs` | `assets.pak` の書き出し |
| `editor/src/Packaging/Pak/AssetPathRewriter.cs` | 絶対パス → `assets://` の書き換え |
| `editor/src/Packaging/Pak/AssetPakBuilder.cs` | **PAK 作りの手順**（収集 → 報告 → 書き出し → 報告）とログの書式。パッケージ化ウィンドウと SeedPak が共有する |
| `editor/tools/SeedPak/` | エディタを起動せずに `assets.pak` を作るコンソールツール（§10） |
| `editor/src/Packaging/Scripts/ScriptPackager.cs` | **ユーザースクリプトの事前コンパイル**とスクリプトホストの同梱 |
| `editor/src/Packaging/Runtime/DotnetRuntimeBundler.cs` | **.NET ランタイムの同梱**（検出・バージョン選択・コピー） |
| `runtime/src/engine/core/scripting/mod.rs` | 同梱 `dotnet/` の検出と CLR の初期化（`BUNDLED_DOTNET_ROOT_DIR`） |
| `scripting/src/Compilation/` | コンパイル共通実装・型マップ規約（`PrecompiledScriptArtifact`） |
| `runtime/src/engine/pak/mod.rs` | ランタイム側の PAK リーダー（フォーマットの正典）。読み口は `pak/source.rs` の `PakSource`（`Read + Seek + Send`） |
| `runtime/src/engine/package_source.rs` | 配布物の読み口（`PackageSource`）。Android の APK 内 pak を読むのに使う（§10） |
| `runtime/android/native/src/apk_package/` | Android: APK の `assets/seed/` を配布物として読む実装（§10） |
| `editor/tests/PackagingCollectorTests/` | 収録・PAK の単体テストとドライラン |
| `editor/tests/ScriptPrecompileTests/` | 事前コンパイル・型解決の単体テストとスクリプト同梱の実行 |

---

## 1. ビルドの流れ

1. **cargo build** — 選択したプラットフォームのターゲットでランタイムをビルドする。
   x64 Windows をホストが x64 Windows のときにビルドする場合だけ `--target` を**付けない**。
   `--target` を付けると cargo は `target/<triple>/release/` を使い、
   普段の `cargo run` が使う `target/release/` とビルドキャッシュを共有しないため、
   同じコードを 2 回フルビルドすることになる。
2. **旧レイアウトの後始末** — 出力フォルダ**直下**に残っている旧配置の成果物
   （`*.dll` / `*.deps.json` / `*.runtimeconfig.json` / `dotnet/`）を削除する。
   `caches` / `logs` / `saved` は利用者データなので**触らない**。
3. **バイナリのコピー** — `SEED.exe` を `{出力先}/{ゲーム名}/{ゲーム名}.exe` へ複製する。
4. **スクリプトの事前コンパイル** — アセット配下の `.cs` を `SEEDUserScripts.dll` へまとめ、
   スクリプトホスト一式とともに `bin/` へ置く（§5）。**失敗したらここで中止する**。
5. **.NET ランタイムの同梱** — スクリプト実行に必要な .NET を `bin/dotnet/` へ写す（§5）。
   設定 OFF・検出失敗のときはスキップし、**中止はしない**。
6. **収録アセットの収集** — 参照グラフを辿って `assets.pak` に入れるファイルを決める（§2）。
7. **PAK 書き出し** — ストリーミングで `assets.pak` を書く（§4）。

各フェーズの所要秒数はログに `[時間] フェーズ名: N.N 秒` の形で出る。

### 出力フォルダの構成

配布物は「実行ファイル・アセット・副次ファイル・実行時生成物」が一目で分かる形に揃える。
以前は DLL が数十個 exe の隣に並んでいて、利用者から見て
「どれが本体でどれを消してよいか」がまったく分からなかった。

```text
{ゲーム名}/
  {ゲーム名}.exe          … 実行ファイル
  assets.pak              … アセット（§4）
  bin/                    … 実行に必要な副次ファイル（パッケージ化が作る）
    SEEDScripting.dll / SEEDScripting.runtimeconfig.json / SEEDScripting.deps.json
    Microsoft.CodeAnalysis*.dll / SEEDUserScripts.dll
    dotnet/               … 同梱 .NET ランタイム（§5）
  caches/                 … 実行時生成（パイプラインキャッシュ `wgpu_pipeline_cache_<バックエンド>_<ベンダー ID>_<デバイス ID>.bin`＝アダプタごと。
                             2026-09 以前の `pipeline_cache.bin` は読み込みだけに使う。モデル派生キャッシュ `*.smdl` の
                             置き場でもあるが、PAK 実行では現状これが効かない → §8）
  logs/                   … 実行時生成（起動ログ `seed_*.log`。§9）
  saved/                  … 実行時生成（セーブデータ `save.json`）
```

フォルダ名の正典は 2 か所にあり、**名前は一致必須**である。

| 側 | ファイル |
|---|---|
| エディタ | `editor/src/Packaging/PackageLayout.cs` |
| ランタイム | `runtime/src/engine/core/package_layout.rs` |

ずれてもビルドは通り、**配布物だけが壊れる**ため、両側のテストで文字列を固定してある。

- `caches` / `logs` / `saved` は **パッケージ化では作らない**。空フォルダは zip 化・展開で
  落ちることが多く、「あるはず」を前提にすると配布先でだけ壊れる。
  必要になった時点でランタイムが `create_dir_all` する。
- 同じ理由でこの 3 つは**削除もしない**（利用者のセーブ・ログが入っている）。
- 消してよいのは `caches` だけ（消せば次回起動で作り直される）。

### 配布先に必要なもの

**何も要らない**（Windows 10 以降であれば）。

- **C ランタイム**は exe に静的リンクしてある（`runtime/.cargo/config.toml` の
  `target-feature=+crt-static`）。以前は `VCRUNTIME140.dll` / `VCRUNTIME140_1.dll` を
  import しており、「Microsoft Visual C++ 再頒布可能パッケージ」が入っていない PC では
  **ダブルクリックしても何も起きなかった**（プロセス生成前にローダーが失敗するため、
  §9 の起動ログ機構すら動かない）。静的リンク後の依存 DLL は
  `kernel32` / `user32` / `gdi32` / `ole32` / `d3dcompiler_47` など Windows 標準のものだけになる。
  確認方法: `dumpbin /dependents SEED.exe` に `VCRUNTIME140*` が出ないこと。
- **.NET ランタイム**は `bin/dotnet/` へ同梱する（既定 ON。§5）。

---

## 2. 収録ファイルの決め方（参照グラフ）

「アセットフォルダを丸ごと詰める」のではなく、**起点から到達できるものだけ**を入れる。

### 起点

| 起点 | 内容 |
|---|---|
| `project_settings.json` | ランタイムが必ず読む。常に同梱する |
| `start_scene` / `scenes[].path` | 登録シーン。実体が無いものは警告して飛ばす |
| 追加の起点（SeedPak の `--extra-scene`。段階C-4） | 呼び出し側が足すシーン。登録シーンと同じく、そのシーンと参照先を入れる。**パッケージ化ウィンドウは使わない**（配布物は登録シーンから作る）。Android の実行（エディタ・SeedAndroid）が、シーンマネージャに未登録の起動シーン（開いているシーン）を APK の pak に入れるために渡す（§10.2・[android.md](android.md) §20.10）。実体が無いもの・アセットルートの外は欠落（参照元 `(指定された起点)`）として報告して飛ばす |
| エンジン内蔵参照 | `runtime/src` の `.rs` に書かれた `assets://` のうち**実在するもの**（`terrain/layers.json` など）。テスト用ダミーは実在しないので自然に落ちる |
| 追加同梱フォルダ | 設定で指定したフォルダを丸ごと（§3 の逃げ道） |
| 常時同梱拡張子 | 既定は空（`.cs` は事前コンパイル DLL で配るため。§3 参照）。設定した場合は除外ルールに従う |
| 全 `.cs`（走査専用） | 除外ルールに当たらない全 .cs を、参照グラフでの到達可否と無関係に**走査だけ**する（同梱はしない） |

アセット配下の全 .cs は `SEEDUserScripts.dll` へ一括コンパイルされる方式（§5）なので、
どの .cs の文字列リテラルも実行時に使われ得る。だが C# の**型名**だけで参照されるスクリプト
（例: ファクトリの `new MoveMission()`、静的クラスの `FishCatalog.ForLevel(...)`）はパスの
参照グラフに一切現れず、通常の閉包探索（起点から辿り着けたファイルだけを見る方式）では
永遠に見つからない。そこで除外ルールに当たらない全 .cs を走査専用の起点として積み、
中に書かれた `assets://` 参照だけを拾う（`AssetCollector.AddScriptScanSeeds`）。
.cs 自体はこの経路では同梱しない（パスで実際に参照されている .cs は、これとは別に
通常の閉包経由で今も同梱される。§8 参照）。

`templates` のような除外フォルダ配下の .cs は走査もしない。サンプルコードに書かれた
assets:// 参照だけでそのフォルダの資産が丸ごと収録される事故を防ぐためで、
「除外は参照より弱い」という原則自体は変えていない
（そういう .cs が実際にパスで参照されていれば、従来どおり同梱・走査される）。

### 参照の抽出（4 系統）

到達したファイルのうち、`PackagingRules.ScannableExtensions` にある拡張子だけを
テキストとして開き、次を参照として取り出す
（ランタイム `asset_fs::normalize_asset_path` の分類に対応）。

1. **`assets://` 形式** — 終端は `"` `'` 空白 `<` `>` `)`。**確実な参照**。
2. **アセットルートの絶対パス** — 表記ゆれ 4 形式すべて。**確実な参照**。
   - `C:/proj/assets/…`（スラッシュ）
   - `C:\proj\assets\…`（バックスラッシュ）
   - `C:\\proj\\assets\\…`（JSON エスケープ）
   - `C:\/proj\/assets\/…`（JSON エスケープ）
3. **glTF の `"uri"`** — `.bin` やテクスチャ。glTF ファイルからの相対。`%20` などの URL エンコードを解く。**確実な参照**。
4. **相対パス（推測）** — 引用符で囲まれた文字列のうち、拡張子を持ち、
   「参照元フォルダ基準」または「アセットルート基準」で**実在するもの**だけを採用する。
   `terrain/layers.json` の `"mainGame/terrain/brush/leaf2.png"` のような表記がこれ。
   外れても警告は出さない（数値・識別子の誤検出を警告にしないため）。

C# スクリプト（`.cs`）も走査対象なので、文字列リテラルに書かれた
`assets://…`（既定値・定数）は 1 の経路で拾える。

**コメント中の参照**（`// … assets://a/b.txt）から読む。` のように終端文字が現れないまま
日本語が続く形）は、拡張子の切れ目まで詰めた前方部分が実在すればそれを採用する。

### 参照グラフには現れない同伴ファイル

ランタイムが「拡張子を差し替えて隣を読む」種類の依存は、規則として
`PackagingRules` に持っている。ここに載せ忘れると**パッケージ版だけ壊れる**。

| きっかけ | 足すもの | 根拠 |
|---|---|---|
| `.tvox` | 同名の `.tscatter` / `.tcover` | `terrain_ops.rs`（拡張子を差し替えて読む） |
| `.tvox` | 同じフォルダの `terrain_meta.json` | `terrain_meta_ops.rs` |
| `.obj` | 同名の `.mtl` | OBJ のマテリアル定義 |

なお地形チャンクは**シーンの `TerrainChunkComponent` に書かれた `tvox_path` だけ**が
読み込まれる（フォルダ走査ではない。`terrain_ops.rs` の `walk`）。
そのため、シーンに対応するチャンクアクタが無い `.tvox` は収録されない。

### 拡張子の無い `assets://` 参照

`assets://terrain/Scene1` のようにフォルダを指す参照、および
スクリプトが文字列連結で組み立てるパスの前半部分は、
**同名のフォルダが存在すればその配下を丸ごと同梱**する。
フォルダも無ければ欠落報告はしない（文章の断片と区別できないため）。

---

## 3. 設定項目（パッケージ化ウィンドウ「アセット収録」）

`{assets}/packaging_settings.json` の `assets` セクションに保存される。

| 項目 | JSON キー | 既定値 | 意味 |
|---|---|---|---|
| 全ファイル同梱 | `include_all_files` | `false` | 参照解決を行わず全部入れる（従来の挙動。緊急避難用） |
| 除外フォルダ | `excluded_folders` | `.backup`, `templates`, `assets_realdir_backup_*`, `__MACOSX` | パスのどこかの階層名が一致したら除外（`*` 可） |
| 除外拡張子 | `excluded_extensions` | `.lock` `.blend` `.blend1` `.zip` `.psd` `.tmp` `.bak` | 未参照なら捨てる拡張子 |
| 除外ファイル名 | `excluded_file_names` | `.DS_Store`, `._*`, `Thumbs.db`, `desktop.ini` | OS が作るゴミファイル（`*` 可） |
| 追加同梱フォルダ | `additional_folders` | （空） | 参照グラフで辿れないアセットを丸ごと入れる**逃げ道** |
| 常時同梱拡張子 | `always_included_extensions` | （空） | 参照が無くても入れる拡張子（除外ルールには従う） |

> 常時同梱拡張子の既定は 2026-09-09 に `.cs` から**空**へ変えた。
> スクリプトは §5 のとおり DLL へ事前コンパイルして配るので、ソースを PAK に入れる必要がない。
> （`.scene` / `.actor` から参照されている `.cs` は参照グラフ経由で今も入る。§8 参照）

### 除外は参照より弱い

除外ルールは「**未参照ファイルの掃除**」であって、参照より優先しない。
参照されていれば除外設定に当たっていても同梱し、ログに警告として出す。
たとえば `templates` は既定で除外だが、`title.scene` が
`assets://templates/fonts/Digital/NikkyouSans-mLKax.ttf` を参照しているので、
そのフォントだけは入る。

例外は 1 つだけで、`packaging_settings.json`（エディタ専用設定。出力先の絶対パスを含む）は
参照されていても**決して同梱しない**（`PackagingRules.NeverIncludedRelativePaths`）。

---

## 4. PAK の書き出し

フォーマットは `runtime/src/engine/pak/mod.rs` の `PakReader` が読む形式で固定
（magic `"SEED"` / version 1 / entry_count / エントリ表 / データ部）。**変更しない**。

収集（§2）→ 結果の報告（§6）→ 書き出し → 書き出し結果の報告、という手順とログの書式は
`AssetPakBuilder` にまとめてあり、パッケージ化ウィンドウと SeedPak（§10）は同じものを呼ぶ
（同じ入力からは 1 バイトも違わない PAK になる。`PackagingCollectorTests` で固定）。

メモリを使わないよう 2 段構えで書く。

1. `.json` `.scene` `.actor` `.actor2d` `.inputmap` `.anim` `.icons` は
   絶対パスを `assets://` へ書き換えるためサイズが変わるので、**先にメモリ上で変換**して
   サイズを確定させる（合計でも数 MB）。
2. ヘッダーとエントリ表を書いてから、残りのファイルを 1 本ずつ**ストリームコピー**する。

エントリ表に書いたサイズと実際の書き込み量は必ず一致させる。
書き出し中に外部からファイルが変わった場合は 0 埋め／切り捨てで整合させ、件数を警告に出す
（PAK 自体は壊れない）。

### 書き換え後のパス表記について

エスケープ済みの絶対パス `C:\\proj\\assets\\a\\b.glb` は
`assets://a\\b.glb` になる（スキームより後ろの区切りは元のまま）。
ランタイムの `PakReader::read` が `\` を `/` に正規化して読むため、これで動く（従来と同じ挙動）。

---

## 5. スクリプト（事前コンパイル）

パッケージ版でユーザースクリプトを動かすための仕組み。
**ソース（`.cs`）も Roslyn も配布物には入れない**。ビルド時に 1 回だけコンパイルし、
できた DLL を実行ファイルの隣の **`bin/`** へ置く。

### 同梱されるもの

いずれも `{ゲーム名}/bin/` 直下（出力フォルダ直下ではない）。

| ファイル | 中身 | サイズの目安 |
|---|---|---|
| `SEEDUserScripts.dll` | アセット配下の全 `.cs` を 1 つにまとめた事前コンパイル済みアセンブリ | 0.4 MB |
| `SEEDScripting.dll` | スクリプトホスト（`SEEDScript` 基底クラス・`SEED.*` API・FFI 入口） | 0.2 MB |
| `SEEDScripting.runtimeconfig.json` / `.deps.json` | hostfxr が CLR を初期化するのに必要 | 数 KB |
| `Microsoft.CodeAnalysis*.dll` | スクリプトホストの依存（`deps.json` に載っているため同梱する） | 9 MB |

言語別の `resources` サブフォルダ（`cs/` `de/` …）は**コピーしない**。
中身は Roslyn の診断メッセージのローカライズだけで、実行には要らない
（開発時のシャドウコピーも同じくフォルダ直下しか写しておらず、その構成で動いている）。

`.pdb` も同梱しない（配布物に開発機のソースパスを載せないため）。

### 起動時の流れ（ランタイム）

`App::new` が `app/script_boot.rs`（`runtime/src/engine/core/app_base/app/`）で経路を分ける。

| 起動 | 条件 | 動作 |
|---|---|---|
| Android（同梱 .NET） | `LaunchArgs.embedded_clr` あり | 端末の `files/bin/` か APK の `bin/` の `SEEDUserScripts.dll` をバイト列で読む（その場コンパイルはしない。[android.md](android.md) §17） |
| エディタ / Play | `--assets-root` あり | その場で `.cs` をコンパイルする（ホットリロード可） |
| パッケージ版 | `--assets-root` なし | `{exe のフォルダ}/bin/SEEDUserScripts.dll` を読むだけ |

スクリプトホスト（`SEEDScripting.dll`）の探索順は
`{cwd}/../scripting/bin/Debug/net10.0/`（開発ビルド出力）→ `{exe のフォルダ}/bin/`。
**exe 直下は候補にしない**（旧配置の残骸を拾って新旧のホストが混ざるのを防ぐため）。

**開発ビルド出力から読んだときだけ**テンポラリへシャドウコピーする
（エディタからの再ビルドを妨げないため）。パッケージ配置ではコピーしない
——`bin/` には同梱 .NET（75 MB 超）も居るので、
コピーすると起動のたびに副次ファイルを丸ごと複製することになる。

成功すると stderr に 1 行出る。

```
[SEED] precompiled scripts loaded: 40 type(s)
```

DLL が無い場合は 1 行だけ残して**スクリプト無しで起動を続ける**
（スクリプトを使っていないゲーム、および古いパッケージのため）。

### 型解決の仕組み（`.scene` のパス → 型）

`.scene` にはスクリプトの型名ではなく**ソースのパス**が入っている。
PAK 化のときに絶対パスは `assets://` 形式へ書き換わるので、
配布物では次の 2 形式が混在する。

```
"type_name": "assets://common/scripts/SceneFlow.cs"
"type_name": "assets://title\scripts\TitleMotion.cs"    ← 区切りが混ざることもある
```

ソースを配らない以上、パスから型を引く表がどこかに要る。そこで
**`SEEDUserScripts.dll` のマニフェストリソースへ型マップを焼き込む**
（`scripting/src/Compilation/PrecompiledScriptArtifact.cs`）。
1 行 = `アセットルート相対パス(TAB)型の FullName`、パスは `/` 区切り・小文字に正規化する。

解決の優先順（`ScriptAssemblyManager.Resolve`）:

1. **アセット相対キー一致** — `assets://a/Foo.cs` と `assets://b/Foo.cs` を取り違えない
2. 絶対パス完全一致 — エディタが保存した絶対パス形式
3. ファイル名一致 — 表記ゆれのフォールバック
4. 型名一致 — 旧形式（型名で保存されたもの）

### コンパイルに失敗したとき

**パッケージ化そのものを中止する**（スクリプトが動かない配布物を作らない）。
エラーは `パス(行): 内容` の形でログへ全件出る。
中途半端な DLL も残さない（古い成果物が配られるのを防ぐため）。

「スクリプトホストが見つかりません」と出た場合はエディタ（ソリューション）が
未ビルドで、`scripting/bin/Debug/net10.0/` が無い状態。先にビルドすること。

### UI を使わずにスクリプト同梱だけを実行する

パッケージ版の検証用に、エディタを起動せず同じ処理を回せる。

```
dotnet run --project editor/tests/ScriptPrecompileTests -- "<runtime>" "<アセットルート>" "<出力先>"
```

引数なしで実行すると単体テスト（型マップ・別フォルダ同名ファイルの解決・バイト列からのロードなど）が走る。
同じ処理は SeedPak の `--scripts-only` でも行える（`--out` の下に `bin/` を作る。§10.2）。

### .NET ランタイムの同梱（self-contained 配布）

スクリプトホストは framework-dependent なので、CLR の初期化には .NET ランタイムが要る。
**配布先の PC に .NET が入っていないと、スクリプトが 1 つも動かないまま起動する**
（ゲーム自体は落ちないので、気付きにくい形で壊れる）。
これを避けるため、ビルドマシンにインストール済みの .NET を配布物へ丸ごと写す。

担当は `editor/src/Packaging/Runtime/DotnetRuntimeBundler.cs`。
実行タイミングは**スクリプト事前コンパイルの直後**（PAK 化の前）。

#### 出力レイアウト

インストール版の .NET と同じ形にする。hostfxr が自分自身の DLL パスから
.NET ルートを逆算し、その下の `shared/` から CLR を解決するため、
2 つを同じルート配下に揃えて置くことがそのまま動作条件になる。

```
{ゲーム出力フォルダ}/
  ├ MyGame.exe
  ├ assets.pak
  └ bin/
      ├ SEEDScripting.dll ほか
      └ dotnet/
          ├ host/fxr/10.0.12/hostfxr.dll
          └ shared/Microsoft.NETCore.App/10.0.12/*   （coreclr.dll / hostpolicy.dll / BCL 一式）
```

フォルダ名 `dotnet` はランタイム側 `runtime/src/engine/core/scripting/mod.rs` の
`BUNDLED_DOTNET_ROOT_DIR` と一致必須（テストで両側から突き合わせている）。
手前の `bin/` 1 段は `PackageLayout.BinDirName` / `package_layout::BIN_DIR_NAME`。

#### 起動時の切り替え（ランタイム）

`ScriptingHost::load` が `{exe のフォルダ}/bin/dotnet/host/fxr` の有無だけを見て切り替える。

| 条件 | 使う .NET | stderr の 1 行目 |
|---|---|---|
| `bin/dotnet/host/fxr` がある | 同梱ランタイム | `[SEED] dotnet root: bundled <path>` |
| 無い | PC にインストール済みの .NET | `[SEED] dotnet root: global` |

`bin/dotnet/` があっても `host/fxr` が無ければ同梱扱いにしない（作りかけの配布物で
hostfxr が見つからず起動失敗するのを避けるため）。
旧配置（exe 直下の `dotnet/`）も同梱扱いにしない。

CLR の初期化に失敗したときは、黙って落とさず stderr に理由と対処を出してから
**スクリプト無しで起動を続ける**。

```
[SEED] scripting host failed to load: One of the dependent libraries is missing.
[SEED]   .NET 10 ランタイムが見つからない可能性があります。パッケージ化で「.NET ランタイムを同梱」を…
[SEED]   スクリプト無しで起動を続けます。
```

#### 同梱する .NET の選び方

必要な major.minor は **`bin/SEEDScripting.runtimeconfig.json` の framework version から読む**
（配布物が実際に読むファイルそのものを正典にしているので、スクリプトホストの
ターゲットフレームワークを上げれば自動で追従する）。

.NET ルートの検出順:

1. 環境変数 `DOTNET_ROOT`
2. `dotnet --list-runtimes` の出力（`Microsoft.NETCore.App 10.0.12 [<共有フォルダ>]` を解析し、2 段上をルートとする）
3. `%ProgramFiles%\dotnet`

先に見つかった候補から順に「要求 major.minor に一致する最新パッチ」を探し、
最初に CLR と hostfxr が両方揃ったものを採用する。

- **major.minor が違うものは選ばない**（`10.0` 要求に `9.0` や `11.0` を使わない）。
  ロールフォワードの判断は hostfxr が `rollForward` に従って行う領分。
- hostfxr は **CLR と同じバージョンを優先**し、無ければ**それ以上で最新**。
  CLR より古い hostfxr は選ばない（新しいフレームワークを解決できないため）。
- 出力先に同じバージョンが既にあり、ファイル数と合計サイズが一致すればコピーを省く。

#### 設定

パッケージ化ウィンドウの「共通設定」→ **`.NET ランタイムを同梱`**（既定 ON）。
`packaging_settings.json` の `bundle_dotnet_runtime` に保存される。

| | サイズ | 配布先の要件 |
|---|---|---|
| ON（既定） | +約 77 MB（実測 190 ファイル / 76.7 MB。.NET 10.0.12） | 無し |
| OFF | — | .NET 10 ランタイムのインストールが必要 |

検出できなかった場合はエラーにせず警告してスキップする（framework-dependent のまま
パッケージ化は完了する）。「.NET が入った PC でなら動く配布物」にはなるので、
パッケージ化そのものを止める理由にはしない。

#### UI を使わずに同梱だけを実行する

```
dotnet run --project editor/tests/PackagingCollectorTests -- --bundle-dotnet "<出力フォルダ>"
```

引数は**ゲーム出力フォルダ**（exe と同じ場所）で、同梱先はその下の `bin/dotnet/` になる。
`bin/SEEDScripting.runtimeconfig.json` を読むので、
先にスクリプト同梱（上記 `ScriptPrecompileTests`）を済ませておくこと。

---

## 6. ログの読み方

```
── 収録アセットの収集 ──
⚠ 登録シーンの実体がありません（スキップ）: assets://demo/scenes/demo_title.scene
エンジン内蔵参照: 4 ファイルを追加
参照走査: 146 ファイルを解析
[時間] 収録アセットの収集: 0.2 秒
収録: 325 ファイル / 51.5 MB
除外: 2120 ファイル / 914.1 MB（アセット全体 2445 ファイル / 965.6 MB）
⚠ 除外ルールに一致するが参照されているため同梱: 5 ファイル
❌ 参照先が見つからないパス: 4 件
    actors/kamome.actor  ← mainGame/scripts/KamomeManager.cs
```

| 行 | 見方 |
|---|---|
| `⚠ 登録シーンの実体がありません` | `project_settings.json` の `scenes` が実体と食い違っている。登録を消すか実体を作る |
| `⚠ 除外ルールに一致するが参照されているため同梱` | 除外設定が実態と合っていない。放置しても動くが、設定を見直す材料 |
| `❌ 参照先が見つからないパス` | **パッケージ版で読み込み失敗になる箇所**。`← ` の右が参照元ファイル |
| `[時間] …` | フェーズごとの所要秒数 |

---

## 7. 収録内容の事前確認（ドライラン）

PAK を書かずに「何が入って何が落ちるか」だけを確認できる。

```
dotnet run --project editor/tests/PackagingCollectorTests -- "<アセットルート>" "<runtime/src>"
```

`<アセットルート>` には**エディタが使うパスをそのまま**渡すこと
（プロジェクトの `assets/` フォルダ。例: `D:\SEED_projects\WarashibeFishing\assets`。
実体側のパスを渡すと `.scene` に書かれた絶対パス参照が一致せず、モデルが丸ごと落ちて見える）。

引数なしで実行すると単体テストが走る。

```
dotnet run --project editor/tests/PackagingCollectorTests
```

ドライランの収録ルールは既定値（`packaging_settings.json` を読まない）。`--write-pak` で書き出す PAK も同じ。
プロジェクトの設定どおりの PAK をエディタ無しで作るなら SeedPak（§10）を使う。

---

## 8. 既知の制限

- **「.NET ランタイムを同梱」を OFF にすると、対象マシンに .NET 10 のインストールが必要**。
  スクリプトホストは framework-dependent（`SEEDScripting.runtimeconfig.json` が
  `Microsoft.NETCore.App 10.0` を要求する）なので、未インストールの PC では
  CLR の初期化に失敗し、**スクリプト無しでゲームが起動する**（ゲーム自体は落ちない）。
  既定は同梱 ON なので通常は問題にならない（§5「.NET ランタイムの同梱」）。
  なお同梱するのは**ビルドマシンにインストール済みの .NET**であり、
  `dotnet publish --self-contained` は使っていない。
- **同梱できるのは Windows 版だけ**。`DotnetRuntimeBundler` は `hostfxr.dll` という
  Windows のファイル名しか見ないため、macOS / Linux 向けに作る場合は
  `libhostfxr.dylib` / `libhostfxr.so` への対応が要る（現状 Windows 以外は
  そもそもこのマシンからビルドできないので実害は無い）。
- **`.scene` / `.actor` から参照されている `.cs` は今も PAK に入る**。
  常時同梱の既定は空にしたが、`type_name` が `.cs` のパスである以上、
  参照グラフの閉包に乗る。動作には影響しないが、配布物にソースが残る。
  完全に外すには「参照されていても入れない拡張子」の仕組みが要る（`docs/backlog.md` 参照）。
- **PAK 実行ではモデルの派生キャッシュ（`*.smdl`）が一切効かない**。
  キャッシュの有効性判定に元ファイルの mtime + サイズを使っており
  （`asset_cache::source_stamp`）、PAK モードでは元ファイルがディスク上に存在しないため
  読み込みも書き出しも即座に打ち切られる。したがって配布版の `caches/` に入るのは
  今のところパイプラインキャッシュ（アダプタごとの `wgpu_pipeline_cache_*.bin`）だけで、モデルは毎回パースし直している
  （起動が遅くなるだけで動作はする）。`docs/backlog.md` 参照。
- `project_settings.json` の読み込み（ウィンドウサイズ・ゲーム名・描画解像度モード・
  目標フレームレート・垂直同期・プラグイン設定）は 2026-09-09 に
  `asset_fs` 経由へ直したので PAK モードでも効く。ただしプラグイン DLL 自体は同梱されないため、
  パッケージ実行ではプラグインは常に 0 件で続行する（backlog 参照）。
- 新しいアセット形式を足したときは、`PackagingRules` の
  `ScannableExtensions` / `SiblingExtensions` / `FolderCompanions` の追従を忘れないこと。
  登録漏れは**ビルドエラーにならず**、パッケージ版だけが壊れる形で出る。
- **Android は開発用（デバッグ署名の APK）と配布用（アップロード鍵で署名した APK / AAB。2026-09-26・段階D）**（§10.3・[android.md](android.md) §24）。
  APK 内の `assets.pak` から起動し、スクリプトは段階B で APK に同梱した .NET 10 の CoreCLR で動く（[android.md](android.md) §17）。
  セーブ・キャッシュの書き込みは端末のアプリ専用フォルダへ振り替えてある
  （セーブ `files/save/`・キャッシュ `/data/user/0/<パッケージ名>/cache`。[android.md](android.md) §14）。
  パッケージ化ウィンドウの Android 出力は段階C-2 で実働し（中核 `editor/src/Android/` の `Goal = Build`）、段階D で配布用・AAB・アイコン・
  Google Play の要件チェックを足した。実際の Google Play Console への提出は未確認（backlog）。

### 8.1 配布版の動作に効くプロジェクト設定

エディタの「プロジェクト設定 → 解像度設定」で編集し、`project_settings.json` に保存される。
パッケージ版は起動時にこの JSON を読むだけなので、**再パッケージせずに JSON を直接書き換えても効く**
（配布先で「重い」と言われたときに、その場で値を変えて試せる）。

| キー | 既定 | 意味 |
|---|---|---|
| `window_width` / `window_height` | 1920 / 1080 | ゲームウィンドウの初期解像度（物理ピクセル） |
| `render_resolution_mode` | `"window"` | `"fixed"` でウィンドウを拡縮しても内部解像度で描き、最終出力をレターボックスする |
| `target_fps` | `60` | フレームレート上限（`0` で無制限）。CPU・GPU の空回りを止めて発熱を抑える |
| `vsync` | `"auto"` | 垂直同期。`"auto"` はパッケージ版＝有効／エディタ埋め込み＝無効。`"on"` / `"off"` で固定 |
| `game_name` | 空 | ウィンドウタイトル（未設定なら `"SEED"`） |
| `streaming` | （省略可） | モデルの非同期ロード（ワーカースレッド・先読み・GPU アップロード予算・バッチ常駐時間）。キーの一覧と既定値は [docs/model_streaming.md](model_streaming.md) 6 章 |
| `screen_orientation` | `"both"` | **Android の APK だけ**に効く画面の向き。`"both"`（縦横 4 方向に追従）/ `"portrait"`（縦に固定）/ `"landscape"`（横に固定）。エディタでは「プロジェクト設定 → 解像度設定 → 画面の向き（モバイル）」。起動時に読む値ではなく、APK を作るときにマニフェストの `screenOrientation` へ焼き込む（SeedAndroid → `app/build.gradle.kts` の変換表。**書き換えたら APK を作り直す**）。デスクトップには効かない。[android.md](android.md) §15 |
| `android` | （無し） | **Android の APK だけ**に効くアプリの識別情報 `application_id` / `app_name` / `version_code` / `version_name`（どれも省略可。既定は `.seedproj` の名前から `com.seedengine.<英数字化した名前>`・プロジェクトの表示名・`1`・`"1.0"`）と、ランチャーのアイコン `icon`（元の PNG。アセットルートからの相対パス・`assets://`・絶対パス。省略時はシステムの既定のアイコン）・`icon_background`（アダプティブアイコンの背景色 `#RRGGBB`。既定は白）（段階D）。エディタでは「プロジェクト設定 → 解像度設定 → Android アプリ情報（モバイル）」。APK を作るときに `applicationId` / ランチャーの名前 / `versionCode` / `versionName` / 各密度のアイコンへ焼き込む（**書き換えたら APK を作り直す**。ID を変えると端末では別のアプリになる）。デスクトップには効かない。[android.md](android.md) §18・§24.7 |
| `render_quality` | （無し） | 描画品質プリセットの選び方（プラットフォームごと。段階D-2）。`{"desktop": {"preset": …}, "android": {"preset": …, "render_scale": 0.75, …}}`。節もキーも省略でき、省略時はデスクトップ `desktop`（何も下げない＝従来どおり）・Android `mobile`（軽量）。節の中の `preset` 以外のキーはプリセットのつまみの上書き（`render_scale` 0.5〜1.0・`shadows`・`gi` 等。一覧は [rendering_roadmap.md](rendering_roadmap.md) の「描画品質プリセット」）。エディタでは「プロジェクト設定 → グラフィックス → レンダリング品質」。起動時に 1 回読む（起動ログ `[SEED QUALITY]`）。Android は pak に入るので書き換えたら APK を作り直す。[android.md](android.md) §22 |

遅いドライブ（USB 外付け・低速 SSD）で「プレイ中に時々カクつく」と言われたら、まず
`streaming` を見る。配布版でも環境変数 `SEED_STREAMING=0` で非同期ロードを丸ごと切って
切り分けられる（[docs/model_streaming.md](model_streaming.md) 6 章）。

`target_fps` と `vsync` は起動ログの `[SEED INIT] target_fps=… vsync=… embedded=…` と
`[SEED INIT] vsync=… embedded=… present_mode=…` に解決結果が出る（§9 のログ）。
ゲーム内では `SEED.Time.Fps` / `SEED.Time.FrameTimeMs` で実測値を取れる。

---

## 9. 配布版の起動ログとトラブルシュート

配布した exe は `windows_subsystem = "windows"` でコンソールを持たないため、
そのままではランタイムの `eprintln!` も C# の `Console.Error` も**行き先が無く捨てられる**。
起動に失敗しても「ダブルクリックしても何も起きない」としか見えず、原因が一切残らない。

これを避けるため、**パッケージ実行のときだけ**標準出力・標準エラーをログファイルへ
差し替える機構がランタイムに入っている（`runtime/src/engine/core/startup_log/`）。

### ログの場所

```
{exe と同じフォルダ}\logs\seed_YYYYMMDD_HHMMSS.log
```

- 配布フォルダの中に置く方針（exe / `assets.pak` / 副次ファイルの `bin` / 実行時生成の
  `caches`・`logs`・`saved` という構成。§1「出力フォルダの構成」）。
  ユーザーが自分で見つけてそのまま送れる。
- exe の隣に**書けない**場合（読み取り専用メディア、Program Files 配下へ展開したなど）は
  `%LOCALAPPDATA%\{exe名}\logs\` へ退避する。どちらにも書けないときはログ無しで起動する
  （ゲームは必ず起動する — ログが作れないことを理由に落とすことはしない）。
- 起動ごとに 1 ファイル。**最新 10 件**だけ残し、古いものは起動時に自動削除する。
- 文字コードは UTF-8（BOM 付き）。メモ帳でそのまま開ける。

### 何が入るか

| 内容 | 出どころ |
|---|---|
| `[SEED ENV] …` | 起動時の環境情報（ログ先頭に 1 回） |
| `[SEED] …` / `[SEED INIT] …` / `[App] …` | ランタイム（Rust）のログ |
| `[SEEDScripting] …` | スクリプトホスト（C#）のログ。CLR は起動時に標準ハンドルを引くので同じファイルへ入る |
| `[SEED PANIC] …` | panic の内容・位置・バックトレース |

先頭の `[SEED ENV]` には次が並ぶ。**問い合わせを受けたらまずここを見る**。

```
[SEED ENV] ===== SEED runtime 起動 v0.1.0 (release) =====
[SEED ENV] 起動日時: 2026-09-10 14:30:59
[SEED ENV] 起動形態: パッケージ実行（配布物）
[SEED ENV] 実行ファイル: D:\dist\MyGame\MyGame.exe
[SEED ENV] 実行ファイル更新日時（ビルド日時の目安）: 2026-09-09 21:00:00
[SEED ENV] 起動引数: （なし）
[SEED ENV] カレントディレクトリ: C:\Windows\System32
[SEED ENV] OS: Windows 10.0 (build 26200)
[SEED ENV] ログファイル: D:\dist\MyGame\logs\seed_20260910_143059.log
[SEED ENV] exe フォルダの内容（直下＋1 階層下）: D:\dist\MyGame
[SEED ENV]   assets.pak  (53612544 バイト / 51.1 MiB)
[SEED ENV]   bin/  (フォルダ)
[SEED ENV]   bin/SEEDScripting.dll  (168448 バイト / 164.5 KiB)
[SEED ENV]   bin/dotnet/  (フォルダ)
[SEED ENV]   …
```

フォルダ一覧は**特定のファイル名を決め打ちで判定していない**（`bin/dotnet/` や
`SEEDScripting.dll` の置き場所が変わっても追従不要）。直下と 1 階層下だけを、
1 フォルダあたり最大 40 件・全体で最大 300 件まで載せ、超えた分は
`… 他 N 件を省略` に畳む。

### 起動に失敗したとき（panic ダイアログ）

panic すると、ログにメッセージ・位置・バックトレースが残り、
パッケージ実行では次のダイアログが出る（ユーザーが黙って消えたと勘違いしないため）。

```
[{exe名} — 起動エラー]
  起動に失敗しました。

  利用できる GPU アダプタが見つかりませんでした（…）

  ログ: D:\dist\MyGame\logs\seed_20260910_143059.log
```

ダイアログは最初の 1 回だけ出す（複数スレッドが同時に panic しても積み重ならない）。
ユーザーが OK を押すまで閉じない（専用スレッドで出しているため、
イベントループが壊れていても勝手に消えない）。

> **バックトレースは配布版では `<unknown>` になる。**
> パッケージ化は `SEED.exe` しかコピーせず、`SEED.pdb` が配布先に無いため。
> 一次切り分けに要るのは `[SEED PANIC] メッセージ:` と `[SEED PANIC] 位置:`
> （ファイル名と行番号）で、こちらは PDB 無しでも必ず出る。
> スタックまで追う必要が出たら `docs/backlog.md` の該当項目を参照。

### よくある原因

| 症状 | ログの見どころ | 原因と対処 |
|---|---|---|
| ダイアログ「利用できる GPU アダプタが見つかりません」 | `[SEED INIT] GPU アダプタ候補: 0 件` | DirectX 12 / Vulkan 非対応の GPU、またはドライバが古い。候補が 1 件以上あるのに落ちる場合は `surface対応=false` の行を見る（マルチ GPU で表示側と描画側が食い違っている） |
| スクリプトが何も動かない | `[SEED] dotnet root:` と `precompiled scripts loaded:` の有無 | `bin/dotnet/` が展開時に落ちている／`SEEDUserScripts.dll` が `bin/` に無い。`[SEED ENV]` のフォルダ一覧で実際の配置を確認する |
| ダブルクリックしても何も起きず、ログも作られない | — | 起動ログ機構より前にローダーが失敗している。DLL 不足なら `dumpbin /dependents` で確認する（C ランタイムは静的リンク済みなので `VCRUNTIME140*` は出ないはず。§1「配布先に必要なもの」） |
| モデル・画像が出ない、真っ黒 | `[SEED ENV]` の `assets.pak` のサイズ | `assets.pak` が無い／0 バイト／展開に失敗している。パッケージ化ログの `参照先が見つからないパス`（§6）も確認する |
| ウィンドウが既定解像度になる | `[SEED INIT] init_asset_fs done` の後 | `project_settings.json` が PAK に入っていない（§2 の起点） |
| そもそもログが出ない | — | exe の隣にも `%LOCALAPPDATA%` にも書けていない。ZIP をデスクトップなど書き込み可能な場所へ展開し直す |

### エディタ実行では何も変わらない

`--assets-root=` または `--pipe=` 付きの起動（エディタからの編集／埋め込み Play）と、
`assets.pak` を持たない開発ビルドの単体起動では、標準ハンドルに一切触れない。
ログは従来どおりエディタの Output パネル／コンソールへ流れ、ログファイルも作られない。
panic フックだけは同じように設置されるが、**ダイアログは出さず**ログ出力のみになる。

---

## 10. Android（APK 内 pak）と SeedPak ツール

### 10.1 Android の配布物

Android では、Windows の出力フォルダ（実行ファイルを除く）と同じ相対構成を APK の `assets/seed/` に入れ、
ランタイムは AAssetManager 経由でそれを読む（正典は [android.md](android.md) §13）。

| 配布物の中身 | Windows | Android |
|---|---|---|
| 配布物のルート | `{ゲーム名}/`（実行ファイルのフォルダ） | APK の `assets/seed/` |
| `assets.pak` | 実行ファイルの隣 | `assets/seed/assets.pak`（Gradle の `noCompress` で非圧縮のまま格納） |
| PAK の外に置くアセット（PAK に無いときのフォールバック先） | `{ゲーム名}/assets/<相対パス>` | `assets/seed/assets/<相対パス>`（大文字小文字を区別する） |
| `bin/`（スクリプト DLL） | 同梱（§5） | `assets/seed/bin/`（SeedPak `--scripts` の出力。中身は PC と同じ。ランタイムはバイト列で読む。端末の `files/bin/` に置いた差し替えが優先。[android.md](android.md) §17.7） |
| .NET ランタイム | `bin/dotnet/`（§5。DotnetRuntimeBundler） | `.so` は APK の `lib/<ABI>/`、BCL・deps.json・目録 `bundle.json` は `assets/seed/dotnet/<ABI>/`。SeedAndroid（`editor/src/Android/Dotnet/DotnetRuntimeBundle.cs`）が NuGet のランタイムパックから組み立て、初回起動時に端末の `files/dotnet/` へ展開する（[android.md](android.md) §17） |
| `caches/` `logs/` `saved/` | 実行時に作る | APK には置けないので端末のアプリ専用フォルダへ振り替える: `saved/` → `files/save/`、`caches/` → `/data/user/0/<パッケージ名>/cache`、`logs/` は作らない（logcat）。[android.md](android.md) §14 |
| 起動ログ | `logs/seed_*.log`（§9） | logcat（タグ `SEED`） |

- 名前の正典は `runtime/src/engine/core/package_layout.rs`（`PAK_FILE_NAME` / `LOOSE_ASSETS_DIR_NAME`）と
  `editor/src/Packaging/PackageLayout.cs`（`PakFileName`）。両側のテストで文字列を固定している。
- `project_settings.json` は PAK の中に入る（§2 の起点）。PAK の外に置くのはスクリプトの `bin/` だけ（段階B）。
- Android のランタイムはスクリプトの DLL を「端末の `files/bin/` → APK の `bin/`」の順に探す（`SEEDScripting.dll` がある最初の置き場）。
  パス指定の読み込み（`load_assembly_and_get_function_pointer`）が Android 版 CoreCLR で使えないため、`SEEDScripting.dll` と
  `SEEDUserScripts.dll` はバイト列で読む（C# の `ScriptBridge.LoadPrecompiledScriptsFromBytes`）。Roslyn の DLL も同じ `bin/` に入るが、
  Android では読み込まれない（その場コンパイルをしない）。
- 起動モードはデータの有無で決まる: APK に `assets/seed/assets.pak` があればパッケージ実行、無ければ開発用の置き場
  （run-as で送ったアセット）。

### 10.2 SeedPak（エディタを起動せずに PAK を作る）

`editor/tools/SeedPak/` のコンソールアプリ。パッケージ化ウィンドウの「収録アセットの収集」「PAK 書き出し」フェーズと
**同じコード**（`AssetPakBuilder` → `AssetCollector` / `PakWriter` / `AssetPathRewriter`）で `assets.pak` を作る。
`editor/tests/*Tests` と同じく、WPF 非依存のソースをリンクして取り込む素のコンソールアプリで、パッケージ化のロジックが
WPF に依存し始めるとこのツールのビルドが壊れて気付ける（`editor/SEEDEditor.csproj` は `tools\**` を本体から除外している）。

```
dotnet run --project editor/tools/SeedPak -- --project <プロジェクトフォルダ> --out <出力フォルダ>
dotnet run --project editor/tools/SeedPak -- --assets <アセットルート> --out <出力フォルダ>
dotnet run --project editor/tools/SeedPak -- --project <プロジェクトフォルダ> --out <出力フォルダ> --extra-scene scenes/Stage2.scene
```

| 引数 | 意味 |
|---|---|
| `--project <フォルダ>` | プロジェクトフォルダ。アセットルートをエディタと同じ導き方で決める: `.seedproj` があればその `assets_dir`（`ProjectPaths`）、無ければ `<フォルダ>/assets`、それも無くフォルダ自体に `project_settings.json` があればそのフォルダ（規則の正典は `editor/src/Project/ProjectFolderResolver.cs`。SeedAndroid の `--project` も同じ） |
| `--assets <フォルダ>` | アセットルートを直接指定する（`--project` と排他） |
| `--out <フォルダ>` | 出力フォルダ。`<フォルダ>/assets.pak` だけを書く（無ければ作る） |
| `--runtime-src <フォルダ>` | エンジンのソース `runtime/src`（エンジン内蔵の `assets://` 参照を起点に加える。§2）。既定はツールの位置・カレントから上へ辿ったリポジトリの `runtime/src`。見つからなければ省略して続ける |
| `--scripts` | 加えて `<出力フォルダ>/bin/` にスクリプトを作る。パッケージ化ウィンドウと同じ `ScriptPackager`（§5）で、アセット配下の `.cs` を `SEEDUserScripts.dll` へ事前コンパイルし、スクリプトホスト（`SEEDScripting.dll`・`SEEDScripting.runtimeconfig.json`・`.deps.json`・Roslyn）を `scripting/bin/Debug/net10.0/` から写す。スクリプトホストの場所は `--runtime-src` の親（`runtime/`）から探す |
| `--scripts-only` | `bin/` だけを作る（PAK は作らない。Android の `-PushScripts` で DLL だけを差し替えるとき） |
| `--extra-scene <シーン>` | 段階C-4。`project_settings.json` の登録シーンに加えて**収録の起点にするシーン**（繰り返し指定できる）。アセットルートからの相対パス・`assets://…`・アセットルート内の絶対パス（登録シーンと同じ書き方）。そのシーンと、そこから参照をたどれるものを PAK に入れる（`AssetPakBuilder.Collect` の `extraSeeds` → `AssetCollector.Collect(extraSeeds)`。既定の起点は 1 つも減らさない）。無いシーン・アセットルートの外は `⚠ 追加の起点の実体がありません（スキップ）` と欠落の報告（参照元 `(指定された起点)`）を出して飛ばす（終了コードは変えない）。`--scripts-only` とは併用できない（引数の誤り） |

- 収録ルールはパッケージ化ウィンドウと同じ `<アセットルート>/packaging_settings.json` の `assets`（無ければ既定値）。
- アセットルートは**エディタが使うパスと同じ表記**で渡すこと（§7 と同じ注意。シーン内の絶対パス参照の照合と
  `assets://` への書き換えがこの表記を基準にする）。SeedPak は `Path.GetFullPath` で絶対化する。
- ログはウィンドウと同じ書式（§6）で標準出力へ出る。
- 終了コード: `0` 成功 / `1` 引数・入力の誤り / `2` 収録対象 0 件（PAK は書かない） / `3` 書き出し失敗 / `4` スクリプトのコンパイル・同梱の失敗（`--scripts` / `--scripts-only`）。
- `bin/` は上書きで書き足す（古いファイルは消さない。置き場を作り直すのは呼び出し側。SeedAndroid は毎回作り直す）。
- SeedPak は `scripting/SEEDScripting.csproj` を参照しているので、`dotnet run` のたびにスクリプトホストもビルドされ、同梱する
  `SEEDScripting.dll` が常に最新になる（`ScriptPrecompileTests` と同じ組み方）。
- Android の APK へ入れるときは SeedAndroid（`editor/tools/SeedAndroid`。[android.md](android.md) §5。`runtime/android/build_and_run.ps1` は
  そのラッパー）の `--project <フォルダ>` がこのツールを `--out runtime/android/app/src/main/assets/seed --scripts` で呼ぶ
  （`push`・`--push-scripts` では `--scripts-only`）。アセット・パッケージ化のコードが前回から変わっていなければ呼ばない。
  起動するシーン（エディタの開いているシーン・SeedAndroid の `--scene`）がシーンマネージャに未登録なら、そのシーンを `--extra-scene` で渡す
  （判断は `editor/src/Android/Project/AndroidPakSceneSeeds.cs`。足すシーンは pak の指紋にも入るので、未登録のシーンへ切り替えた最初の実行だけ
  pak・APK を作り直す。[android.md](android.md) §20.10）。パッケージ化ウィンドウの Android 出力はシーンを渡さないので、登録シーンだけから作る。

2026-09-24 に最小アセット（BrainStem.glb ＋ 平行光・3 ファイル）で、SeedPak の出力と「変更前のパッケージ化ウィンドウと
同じ呼び出し（`AssetCollector` → `PakWriter` の直呼び）」の出力の SHA-256 が一致することを確かめた。

### 10.3 パッケージ化ウィンドウの Android 出力（段階C-2・2026-09-25、配布用は段階D・2026-09-26）

「パッケージ化」→ Android →「ビルド開始」は、Windows の流れ（`cargo build` → 実行ファイル・`bin/`・`assets.pak` を並べる）を通らず、
Android のビルド・配置・起動の中核（`editor/src/Android/`。SeedAndroid・エディタの Android 実行と同じクラス）を `Goal = Build`（端末は使わない）で呼び、
できた APK / AAB（`runtime/android/app/build/outputs/` の下。中核の結果の `ArtifactPath`）を出力フォルダへ写す。詳細は [android.md](android.md) §20.6・§24。

| 項目 | 内容 |
|---|---|
| 出力 | `{出力フォルダ}/{ゲーム名}/{ゲーム名}-{ABI}-{debug\|release}.{apk\|aab}`（名前の決まりは `editor/src/Packaging/AndroidApkOutput.cs`。配布用の APK と AAB は並べて置ける） |
| 設定（`packaging_settings.json` の `android`） | `output_path`・`arch`（`Arm64V8a` / `X86_64` / `Both`。画面では「ABI」）・`build_type`（画面では「Rust の最適化」。開発用だけ。Release は `cargo --release`）・`variant`（`Debug` / `Release`。画面では「ビルドの種類」。段階D）・`format`（`Apk` / `Aab`。配布用だけ。段階D）・`signing`（`keystore_path`〈絶対パスかプロジェクトのルートからの相対パス〉・`key_alias`。**パスワードは書かない**。段階D） |
| 署名 | 開発用はデバッグ署名。配布用はアップロード鍵（パスワードはエディタの保護保存〈`editor/settings/android_signing_secrets.json`・DPAPI〉か環境変数 `SEED_ANDROID_KEYSTORE_PASSWORD`。無ければビルドしない） |
| APK の中身 | `assets.pak` と `bin/`（SeedPak `--scripts`。収録は同じ `assets` の設定）・同梱 .NET（常に入る。「.NET ランタイムを同梱」の切り替えは Android では出さない）・`libSEED.so`・（設定があれば）アイコン |
| 配布用の確認 | Google Play の要件の一覧（ビルドの前と後。「要件を確認」でビルドせずにも）。[android.md](android.md) §24.8 |

- 以前の `android.ndk_path`（NDK のパス）は廃止した。道具の場所は環境変数と既定の場所から自動で探す（[android.md](android.md) §3）。
  古い設定ファイルの `ndk_path` は読み飛ばし、次の保存で消える。
- 変更の無い工程は飛ばす（エディタの実行・SeedAndroid と置き場を共有するので、入力が同じならすぐ終わる）。ウィンドウを閉じると作成を中断する。
