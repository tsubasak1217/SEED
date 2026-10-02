# エディタのプロジェクト設定ウィンドウ（と、パッケージ化の開発用のビルドの欄。2026-10-02）

上部バーの右端の「プロジェクト設定」ボタンで開くウィンドウ（`editor/src/ProjectSettings/ProjectSettingsWindow.xaml(.cs)` と部分クラス群）の正典。
左のカテゴリツリーで小項目を選ぶと右に設定の画面が出て、「保存して閉じる」で `<assets>/project_settings.json` に書く（「キャンセル」は捨てる）。
ファイルの各キーの意味の正典はランタイム側の文書（下の表のリンク先）で、ここは「画面のどこで何を選ぶと、JSON に何が書かれるか」を書く。

- 書き出しは `ProjectSettingsData.SaveTo`（原子的な置き換え・先頭に `format_version`。[asset_migration.md](asset_migration.md)）。
  エディタの型に無いキーは `ProjectSettingsData.ExtraData`（トップレベル）や各節の `ExtraData` に保ち、保存で消さない。
- 読めなかった `project_settings.json`（新しいエンジンで保存された・変換に失敗した）は、既定値で上書きしないよう開かずに閉じる。
- ランタイムはどの設定も**起動時に読む**。エディタのシーンビュー（Edit）と 2 回目以降の Play はランタイムのプロセスを使い回す
  （`RuntimeManager` の常駐の Play）ので、保存しても次の Play で効かないことがある。確実に見るにはエディタを開き直す
  （パッケージ・Android の APK は作り直す）。[backlog.md](backlog.md) に記録。

## 1. 小項目の一覧

| 大項目 | 小項目 | 書く節・キー | 中身の置き場 | キーの正典 |
|---|---|---|---|---|
| 必須 | ゲーム名 | `game_name` | `ProjectSettingsWindow.xaml.cs` | [project_system.md](project_system.md) |
| 必須 | シーンマネージャ | `scenes` / `start_scene` | 同上 | 同上 |
| グラフィックス | 解像度設定 | `window_width` / `window_height` / `render_resolution_mode` / `target_fps` / `vsync` / `screen_orientation` / `android` | 同上・`.Android.cs`・`.AndroidPlatform.cs` | [packaging.md](packaging.md) §8.1・[android.md](android.md) §15・§18 |
| グラフィックス | レンダリング品質 | `render_quality` | `.RenderQuality.cs` | [rendering_roadmap.md](rendering_roadmap.md)「描画品質プリセット」 |
| グラフィックス | **描画の構成** | `render`（`profile`・旗） | `.Render.cs`（§2） | [rendering_profiles.md](rendering_profiles.md) |
| グラフィックス | **文字の描画** | `font`（`distance_field`・`msdf_coloring`） | `.Font.cs`（§3） | [ui_components.md](ui_components.md) §12.10 |
| グラフィックス | RTシャドウ | `rt_shadows` | `ProjectSettingsWindow.xaml.cs` | [rendering_roadmap.md](rendering_roadmap.md) |
| グラフィックス | シャドウマップ品質 | `shadow` | 同上 | [shadow_mapping.md](shadow_mapping.md) |
| プラグイン | プラグイン管理 | `plugins` | 同上 | [plugin_system.md](plugin_system.md) |

オーディオ・物理・入力・ビルド・タグ＆レイヤーは準備中（プレースホルダーの画面だけ）。

## 2. 描画の構成（`render` 節。2026-10-02）

2D/UI だけのアプリで 3D の描画資源（bindless・影・GI・レイトレーシング・G-Buffer・後処理・Play のピッキング）を作らない
「描画の構成」を選ぶ。意味と効き方の正典は [rendering_profiles.md](rendering_profiles.md)（§2 設定・§3 構成と旗）。

| 欄 | 選択肢 | JSON に書くもの |
|---|---|---|
| 構成 | `runtime/config/render_profiles.json` の構成（表示名（名前）。既定の構成に「・既定」）。今は「3D あり（従来どおり）（full・既定）」「2D/UI だけ（ui）」 | 既定の構成（full）を選ぶと `render.profile` を**書かない**。それ以外は `"render": { "profile": "ui" }` |
| 旗の上書き（8 つ） | 「構成のまま（有効／無効）」・「有効」・「無効」の 3 状態。「構成のまま」の括弧は、選んでいる構成のその旗の値 | 「構成のまま」は**キーを書かない**。有効・無効は `"render": { "<旗>": true / false }`（`scene_3d` `deferred` `shadows` `gi` `bindless` `ray_tracing` `post` `picking`） |
| GPU メモリの確保 | 「構成のまま（…）」・performance・memory_usage | 「構成のまま」は書かない。ほかは `"render": { "memory_hint": "performance" / "memory_usage" }` |

- 画面の下に「この設定で起動したときの実効」を要約して出す（`scene_3d` が無効だと影・GI・bindless・RT・G-Buffer は個々の値によらず無効と出す。
  ランタイムの `RenderProfileFlags::allocates_*` と同じ規則）。
- 実効で 3D のシーンを描かない設定のときは黄色の注意（「3D のモデル・地形・水・天球・SEED.Draw3D があるプロジェクトでは消えて見える」）を出す。
  反映の時期（次の起動から）と「3D のシーンがあるのに ui を選ぶと描かれない」の注意はいつも出す。
- 何も選ばなければ節ごと書かない。**利用者が選び直した欄だけ**をその場で `_data.Render` へ書く（開いて保存しただけでは、手で書いた
  `"profile": "full"` などは変えない）。
- 手で書いた知らないキー・ランタイムが読めない値（`"gi": 3` など）は保ち、画面に黄色で出す（ランタイムは警告して捨てる）。
  節がオブジェクトでない（`"render": "ui"`）ときも、画面で選び直すまではそのまま書き戻す。
- 構成の一覧はエディタのビルドで `runtime/config/render_profiles.json` を埋め込んで読む（`RenderProfileCatalog`。描画品質プリセットの
  `RenderQualityPresetCatalog` と同じ流儀。ランタイムも同じファイルを埋め込むので食い違わない）。読めない（埋め込みが無い・書式の誤り）
  ときは組み込みの既定の一覧（full / ui）に切り替え、黄色で警告を出す。構成を足すと、エディタを作り直したときに一覧に出る。

| 置き場 | 役割 |
|---|---|
| `editor/src/ProjectSettings/RenderProfileSettings.cs` | `render` 節の型と JSON 変換（3 状態の旗・memory_hint・知らないキーの保持・オブジェクトでない節の保持） |
| `editor/src/ProjectSettings/RenderProfileFlagCatalog.cs` | 旗の表（キー・表示名・説明・3D に依るか）と値の読み方（ランタイムの `flags.rs` の写し） |
| `editor/src/ProjectSettings/RenderProfileFlagValues.cs` | 旗の値の組（既定＝すべて用意する）・実効の判断・要約 |
| `editor/src/ProjectSettings/RenderProfileCatalog.cs` | 構成の一覧（埋め込みの JSON・組み込みの既定の一覧）と実効の構成の見積り（`resolve.rs` と同じ順） |
| `editor/src/ProjectSettings/ProjectSettingsWindow.Render.cs` | 画面 |

## 3. 文字の描画（`font` 節。2026-10-02）

キャンバス（2D）の文字（TextComponent）のグリフの距離場を選ぶ。正典は [ui_components.md](ui_components.md) §12（§12.10 が設定）。

| 欄 | 選択肢 | JSON に書くもの |
|---|---|---|
| 距離場 | MTSDF（既定。輪郭から作る 3 チャネルの MSDF ＋真の SDF・RGBA8）/ SDF（従来の 1 チャネル・R8） | MTSDF を選ぶと `font.distance_field` を**書かない**。SDF は `"font": { "distance_field": "sdf" }` |
| 辺の色分け（MTSDF のときだけ出す） | ink trap（既定）/ simple | ink trap を選ぶと書かない。simple は `"font": { "msdf_coloring": "simple" }` |

- 既定値を選ぶとキーを書かない（ランタイムの既定が変わったときに追従する）。手で書いた既定値は、その欄を選び直すまで残す。
- SDF を選んでいる間は「辺の色分け」を隠す（値は消さない）。説明に「アトラスの GPU メモリはどちらも 16 MiB」「ギズモ・操作ガイドの小さな文字は
  この設定によらず SDF」「PC の起動オプション `--font-distance-field=` が設定より優先」を出す。
- ランタイムが読めない値（`"distance_field": "bitmap"` など）・知らないキーは保ち、黄色で出す（ランタイムは警告して既定で動く）。
- 値の表は `editor/src/ProjectSettings/FontFieldCatalog.cs`（ランタイムの `field_settings.rs`・`glyph_field.rs`・`msdf/edge_color.rs` の写し）、
  節の型と JSON 変換は `FontFieldSettings.cs`、画面は `ProjectSettingsWindow.Font.cs`。

## 4. パッケージ化の「開発用のビルド」（`packaging_settings.json` の `debug_build_mark`。2026-10-02）

プロジェクト設定ではなく**パッケージ化ウィンドウ**の設定（`<assets>/packaging_settings.json`。パッケージ化の設定の置き場）。
pak に開発用のビルドの印（`.seed/build.json`）を入れるかを、画面で見て変えられるようにした。印の意味は [packaging.md](packaging.md) §4、
スクリプトから見える値は [scripting_api.md](scripting_api.md) §7.11（`SEED.Application.IsDebugBuild` / `IsDebugAllowed`）。

| プラットフォーム | 画面 | 決まり方 | JSON |
|---|---|---|---|
| Windows / macOS / iOS | 「ビルド設定」の「ビルド種別」の下に「開発用のビルド」のチェック（「SEED.Application.IsDebugAllowed を pak 実行でも真にする（デバッグ用の機能が使える。配布版では外す）」）と「いまの決まり方」の説明 | 既定は**ビルド種別に合わせる**（Debug なら入れる・Release なら入れない＝2026-10-01 からの挙動のまま）。チェックをビルド種別の既定と違う値にしたときだけ上書きになる。**ビルド種別を選び直すと自動に戻る** | 上書きがあるときだけ `"windows": { "debug_build_mark": true / false }`（`macos` / `ios` も同じ）。無ければ書かない（古いファイルは自動） |
| Android | 「ビルドの種類」の下に同じチェック（押せない）と説明 | ビルドの種類で決まる（開発用の APK は入れる・配布用は入れない。中核の `AndroidRunRequest.MarksDebugBuild`） | 書かない |

- Release で印を入れる設定にすると、黄色の注意（「このパッケージを配ると利用者の手元でデバッグ用の機能が開く」）を出す。
  印を入れたかは従来どおりビルドのログの「開発用のビルドの印: 入れる／入れない」で分かる。
- 値はパッケージ化ウィンドウのほかの欄と同じく、ビルドを始めたときに `packaging_settings.json` へ保存する（ビルドせずに閉じると保存しない）。
- 決め方の正典は `editor/src/Packaging/DebugBuildMarkPolicy.cs`（ビルドの `MarksDebugBuild` も画面も同じ関数を使う）。
  画面は `PackagingWindow.DebugBuildMark.cs`。

## 5. 確かめ方

| 何を | どうやって |
|---|---|
| 節の読み書き（節なし → 既定・値・知らないキーの保持・既定値は書かない・旗の 3 状態・オブジェクトでない節）・構成の一覧・実効の構成・開発用のビルドの印の決め方と `debug_build_mark` の往復 | `dotnet run --project editor/tests/ProjectSystemTests` |
| キー名・値・既定がランタイムのソースと一致する（`flags.rs`・`resolve.rs`・`catalog.rs`・`field_settings.rs`・`glyph_field.rs`・`edge_color.rs` を読んで突き合わせる） | 同上（`RenderKeysMatchRuntime`・`FontKeysMatchRuntime`） |
| Android の表示が中核の規則と同じ | `dotnet run --project editor/tests/AndroidRunUiTests`（`DebugBuildMarkMatchesCore`） |
| 画面の見た目と、操作 → JSON（実物の窓を表示せずに組み立てて PNG にし、コンボ・チェックを操作して保存の中身を表明する。一時フォルダだけに書く） | `dotnet run --project editor/tests/ProjectSettingsPreviewProbe -- --out <出力先>`（省略時は `%TEMP%\seed_project_settings_preview`） |
