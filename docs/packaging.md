# パッケージ化（配布ビルド）

エディタの「パッケージ化」ウィンドウ（`editor/src/Packaging/`）が行う処理と、
`assets.pak` に何が入るかの規則をまとめる。

関連ファイル:

| ファイル | 役割 |
|---|---|
| `editor/src/Packaging/PackagingWindow.xaml.cs` | UI・cargo build 実行・進捗表示 |
| `editor/src/Packaging/PackagingData.cs` | 設定の永続化（`{assets}/packaging_settings.json`） |
| `editor/src/Packaging/Collect/PackagingRules.cs` | **収録規則の正典**（走査拡張子・同伴ファイル・既定の除外） |
| `editor/src/Packaging/Collect/AssetReferenceScanner.cs` | テキスト 1 本からの参照抽出 |
| `editor/src/Packaging/Collect/AssetCollector.cs` | 参照グラフの閉包を取り、収録ファイルを決める |
| `editor/src/Packaging/Collect/AssetPackagingSettings.cs` | 収録ルールのユーザー設定 |
| `editor/src/Packaging/Pak/PakWriter.cs` | `assets.pak` の書き出し |
| `editor/src/Packaging/Pak/AssetPathRewriter.cs` | 絶対パス → `assets://` の書き換え |
| `editor/src/Packaging/Scripts/ScriptPackager.cs` | **ユーザースクリプトの事前コンパイル**とスクリプトホストの同梱 |
| `editor/src/Packaging/Runtime/DotnetRuntimeBundler.cs` | **.NET ランタイムの同梱**（検出・バージョン選択・コピー） |
| `runtime/src/engine/core/scripting/mod.rs` | 同梱 `dotnet/` の検出と CLR の初期化（`BUNDLED_DOTNET_ROOT_DIR`） |
| `scripting/src/Compilation/` | コンパイル共通実装・型マップ規約（`PrecompiledScriptArtifact`） |
| `runtime/src/engine/pak.rs` | ランタイム側の PAK リーダー（フォーマットの正典） |
| `editor/tests/PackagingCollectorTests/` | 収録・PAK の単体テストとドライラン |
| `editor/tests/ScriptPrecompileTests/` | 事前コンパイル・型解決の単体テストとスクリプト同梱の実行 |

---

## 1. ビルドの流れ

1. **cargo build** — 選択したプラットフォームのターゲットでランタイムをビルドする。
   x64 Windows をホストが x64 Windows のときにビルドする場合だけ `--target` を**付けない**。
   `--target` を付けると cargo は `target/<triple>/release/` を使い、
   普段の `cargo run` が使う `target/release/` とビルドキャッシュを共有しないため、
   同じコードを 2 回フルビルドすることになる。
2. **バイナリのコピー** — `SEED.exe` を `{出力先}/{ゲーム名}/{ゲーム名}.exe` へ複製する。
3. **スクリプトの事前コンパイル** — アセット配下の `.cs` を `SEEDUserScripts.dll` へまとめ、
   スクリプトホスト一式とともに出力フォルダへ置く（§5）。**失敗したらここで中止する**。
4. **.NET ランタイムの同梱** — スクリプト実行に必要な .NET を `dotnet/` へ写す（§5）。
   設定 OFF・検出失敗のときはスキップし、**中止はしない**。
5. **収録アセットの収集** — 参照グラフを辿って `assets.pak` に入れるファイルを決める（§2）。
6. **PAK 書き出し** — ストリーミングで `assets.pak` を書く（§4）。

各フェーズの所要秒数はログに `[時間] フェーズ名: N.N 秒` の形で出る。

---

## 2. 収録ファイルの決め方（参照グラフ）

「アセットフォルダを丸ごと詰める」のではなく、**起点から到達できるものだけ**を入れる。

### 起点

| 起点 | 内容 |
|---|---|
| `project_settings.json` | ランタイムが必ず読む。常に同梱する |
| `start_scene` / `scenes[].path` | 登録シーン。実体が無いものは警告して飛ばす |
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

フォーマットは `runtime/src/engine/pak.rs` の `PakReader` が読む形式で固定
（magic `"SEED"` / version 1 / entry_count / エントリ表 / データ部）。**変更しない**。

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
できた DLL を実行ファイルの隣に置く。

### 同梱されるもの

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

`App::new`（`runtime/src/engine/core/app_base/app/mod.rs`）が経路を 2 つに分ける。

| 起動 | 条件 | 動作 |
|---|---|---|
| エディタ / Play | `--assets-root` あり | その場で `.cs` をコンパイルする（ホットリロード可） |
| パッケージ版 | `--assets-root` なし | 実行ファイルの隣の `SEEDUserScripts.dll` を読むだけ |

スクリプトホスト（`SEEDScripting.dll`）の探索順は
`{cwd}/../scripting/bin/Debug/net9.0/`（開発ビルド出力）→ `{exe のフォルダ}`。
**開発ビルド出力から読んだときだけ**テンポラリへシャドウコピーする
（エディタからの再ビルドを妨げないため）。パッケージ配置ではコピーしない
——実行ファイルと同じフォルダには `assets.pak` も居るので、
コピーすると起動のたびにゲーム丸ごとを複製することになる。

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
未ビルドで、`scripting/bin/Debug/net9.0/` が無い状態。先にビルドすること。

### UI を使わずにスクリプト同梱だけを実行する

パッケージ版の検証用に、エディタを起動せず同じ処理を回せる。

```
dotnet run --project editor/tests/ScriptPrecompileTests -- "<runtime>" "<アセットルート>" "<出力先>"
```

引数なしで実行すると単体テスト（型マップ・別フォルダ同名ファイルの解決など）が走る。

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
  ├ SEEDScripting.dll ほか
  └ dotnet/
      ├ host/fxr/9.0.20/hostfxr.dll
      └ shared/Microsoft.NETCore.App/9.0.20/*   （coreclr.dll / hostpolicy.dll / BCL 一式）
```

フォルダ名 `dotnet` はランタイム側 `runtime/src/engine/core/scripting/mod.rs` の
`BUNDLED_DOTNET_ROOT_DIR` と一致必須（テストで両側から突き合わせている）。

#### 起動時の切り替え（ランタイム）

`ScriptingHost::load` が `{exe のフォルダ}/dotnet/host/fxr` の有無だけを見て切り替える。

| 条件 | 使う .NET | stderr の 1 行目 |
|---|---|---|
| `dotnet/host/fxr` がある | 同梱ランタイム | `[SEED] dotnet root: bundled <path>` |
| 無い | PC にインストール済みの .NET | `[SEED] dotnet root: global` |

`dotnet/` があっても `host/fxr` が無ければ同梱扱いにしない（作りかけの配布物で
hostfxr が見つからず起動失敗するのを避けるため）。

CLR の初期化に失敗したときは、黙って落とさず stderr に理由と対処を出してから
**スクリプト無しで起動を続ける**。

```
[SEED] scripting host failed to load: One of the dependent libraries is missing.
[SEED]   .NET 9 ランタイムが見つからない可能性があります。パッケージ化で「.NET ランタイムを同梱」を…
[SEED]   スクリプト無しで起動を続けます。
```

#### 同梱する .NET の選び方

必要な major.minor は **`SEEDScripting.runtimeconfig.json` の framework version から読む**
（配布物が実際に読むファイルそのものを正典にしているので、スクリプトホストの
ターゲットフレームワークを上げれば自動で追従する）。

.NET ルートの検出順:

1. 環境変数 `DOTNET_ROOT`
2. `dotnet --list-runtimes` の出力（`Microsoft.NETCore.App 9.0.20 [<共有フォルダ>]` を解析し、2 段上をルートとする）
3. `%ProgramFiles%\dotnet`

先に見つかった候補から順に「要求 major.minor に一致する最新パッチ」を探し、
最初に CLR と hostfxr が両方揃ったものを採用する。

- **major.minor が違うものは選ばない**（`9.0` 要求に `10.0` を使わない）。
  ロールフォワードの判断は hostfxr が `rollForward` に従って行う領分。
- hostfxr は **CLR と同じバージョンを優先**し、無ければ**それ以上で最新**。
  CLR より古い hostfxr は選ばない（新しいフレームワークを解決できないため）。
- 出力先に同じバージョンが既にあり、ファイル数と合計サイズが一致すればコピーを省く。

#### 設定

パッケージ化ウィンドウの「共通設定」→ **`.NET ランタイムを同梱`**（既定 ON）。
`packaging_settings.json` の `bundle_dotnet_runtime` に保存される。

| | サイズ | 配布先の要件 |
|---|---|---|
| ON（既定） | +約 75 MB（実測 187 ファイル / 74.3 MB） | 無し |
| OFF | — | .NET 9 ランタイムのインストールが必要 |

検出できなかった場合はエラーにせず警告してスキップする（framework-dependent のまま
パッケージ化は完了する）。「.NET が入った PC でなら動く配布物」にはなるので、
パッケージ化そのものを止める理由にはしない。

#### UI を使わずに同梱だけを実行する

```
dotnet run --project editor/tests/PackagingCollectorTests -- --bundle-dotnet "<出力フォルダ>"
```

出力フォルダの `SEEDScripting.runtimeconfig.json` を読むので、
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
（このリポジトリでは `runtime/assets`。`runtime/assets` は実体へのジャンクションなので、
実体側のパスを渡すと `.scene` に書かれた絶対パス参照が一致せず、モデルが丸ごと落ちて見える）。

引数なしで実行すると単体テストが走る。

```
dotnet run --project editor/tests/PackagingCollectorTests
```

---

## 8. 既知の制限

- **「.NET ランタイムを同梱」を OFF にすると、対象マシンに .NET 9 のインストールが必要**。
  スクリプトホストは framework-dependent（`SEEDScripting.runtimeconfig.json` が
  `Microsoft.NETCore.App 9.0` を要求する）なので、未インストールの PC では
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
- `app_init.rs` の `project_settings.json` 読み込みは `std::fs` で exe 隣の `assets/` を見るため、
  PAK モードではウィンドウサイズ・プラグイン設定が既定値になる（同じく backlog 参照）。
- 新しいアセット形式を足したときは、`PackagingRules` の
  `ScannableExtensions` / `SiblingExtensions` / `FolderCompanions` の追従を忘れないこと。
  登録漏れは**ビルドエラーにならず**、パッケージ版だけが壊れる形で出る。
