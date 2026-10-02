# 2026-10-02 に合流した 7 コミットのレビュー

- 基準: **83bfe1fe**（7 コミットをすべて含む合流）。行番号はすべて 83bfe1fe 基準。レビュー中に HEAD は b7138f79 へ進んだが、それ以降の 0045ce2b・7c7ab934・3111058f は対象外。
- 方法: コミット済みの内容を `git show` / `git grep` で読んだだけ。**ビルド・テスト・実行は一切していない**（指示どおり）。下の「何が起きるか」はすべてコードを読んで導いたもので、再現はしていない。
- 分担: 監督役（プレビューの保存経路・prefab_hash・RootAdoptChild・bb6416fa・サムネイルの道具の削除）＋サブレビュア 4 名（MTSDF／render.profile／SEED.UI とサムネイル／プレビューの IPC とエディタ側）。サブレビュアの指摘のうち監督役がコードで裏を取ったものは「確認: 済」、取っていないものは「確認: 未（サブレビュアの読み）」と書いた。

## 要約

| 重さ | 件数 | 主なもの |
|---|---|---|
| 致命 | 0 | — |
| 高 | 1 | プレビューの出し入れを Ctrl+Z / Ctrl+Y すると、インスペクタが古い DFS のまま残り、次の編集が別の実アクタへ書かれる |
| 中 | 5 | サムネイルの道具の `--work` が `<work>/assets` を確認なしで消す／AI・MCP の編集とロジック配置がプレビューへ通って黙って消える／MTSDF の安全弁・細い線・アトラスの容量 |
| 低 | 14 | 下記 |

保存系の濾過（`.scene`・`.actor`・コピー・写し）からプレビューがファイルへ漏れる経路は見つからなかった（「問題なし」の 1・2）。

---

## 1. プレビューの出し入れを Ctrl+Z / Ctrl+Y すると、インスペクタが古い DFS のまま残り、次の編集が別の実アクタへ書かれる  【重さ: 高】
- 場所:
  - runtime/src/engine/core/app_base/app/ipc_handler.rs:283-338（Undo の腕）・339-389（Redo の腕）
  - runtime/src/engine/core/app_base/undo.rs:385-397（`ActorTreeSnapshotCommand` は選択を返さない）
  - runtime/src/engine/core/app_base/app/editor_preview/undo.rs:36-41（中身へそのまま委ねる）
  - runtime/src/engine/core/app_base/app/actor_ops.rs:1319-1321（`rebuild_actors_for_wl` は `actor_virtual_selected_idx` だけ消し、`selected_actor_dfs_ids` は残す）
  - editor/src/Panels/HierarchyPanel.xaml.cs:706-753（`RestoreSelectionAfterSync` → `UpdateSelectedId` は誰にも知らせない）・960-965（`SelectTreeItem` は選択イベントを抑止）
  - editor/src/Panels/InspectorPanel.Lock.cs:197-249（DFS のずれを確かめるのはロック中だけ）
  - （コミット b5ee44db。根は木の写しの Undo 全般にある既存の穴）
- 何が起きるか:
  1. ScreenStack の Screens の下へ画面のプレビュー（部分木 N ノード）を出す。
  2. DFS でそれより後ろにある実アクタ X（例: ModalHost・TabBar）を選び、インスペクタに出す。
  3. Ctrl+Z でプレビューの追加を戻す → X の DFS は N 減るが、インスペクタの `_currentActorId` は古い番号のまま、見た目も X のまま。
  4. インスペクタで値を変える → `SET_*:{古い番号}` が、その番号に今いる**別の実アクタ**へ当たる。普通の編集として Undo に積まれ `*` も付くので、そのまま保存される。
  - 同じ古い番号で、インスペクタの［ダイアログをプレビュー］も別の親を指す（`DescribePreviewParent` が別ノードの名前と安定キーで埋める。HierarchyPanel.Preview.cs:173-179）。
  - ランタイムに残った古い `selected_actor_dfs_ids` は、次のプレビュー操作の `capture_selection`（editor_preview/ops.rs:479-493）で別アクタとして解決され、そのアクタが選ばれる。
- 根拠:
  - 順方向の操作は選択を entity で控えて引き直し、SELECTED と ACTOR_COMPONENTS を送り直している（ops.rs:146-162・499-521）。
  - 同じ操作の Undo / Redo の腕は HIERARCHY（QUIET 付き）だけを送り、SELECTED も ACTOR_COMPONENTS も送らない。
  - エディタはヒエラルキーの同期で選択の番号だけを黙って差し替え、インスペクタへ知らせない。
  - 普通のアクタの追加・削除の Undo にも同じ穴があるが、本コミットで「Ctrl+Z でプレビューが消える」が正式な消し方（docs/editor_screen_preview.md §7）になり、部分木も大きいので踏みやすくなった。
- 確認: 済（上の 6 か所を読んで、イベントが出ないことを確かめた。実行はしていない）
- 直し方の案: 木の組み直しを伴う Undo/Redo の後は、`selected_actor_dfs_ids` を消して `SELECTED:-1` を送る（またはプレビューの順方向と同じく entity で引き直して送る）。エディタ側は `RestoreSelectionAfterSync` で番号が変わったら `ActorDfsSelected` を上げてインスペクタを取り直す。
- → 2026-10-03 に済（ランタイム: 木を組み直す Undo/Redo の前に、選択を「ルートからの名前の道筋＋同じ名前の兄弟の中で何番目か」（エディタの StableKey と同じ規則）で控え、組み直した木で引き直して、ヒエラルキー（組み直したときは間引かず即時）→ `SELECTED` →（主があれば）`ACTOR_COMPONENTS` の順に送る。道筋で引けなくても木の形（DFS 順の子の数の並び）が前後で同じなら同じ番号のまま残し（名前の変更の Undo/Redo）、消えた・親の変わったアクタは選択から外し、何も残らなければ `SELECTED:-1`。組み直しは entity を全部作り直すので、案の「entity で引き直す」は Undo/Redo では使えない（`app/undo_selection.rs`・`ipc_handler.rs` の Undo / Redo の腕。送り方はプレビューの順方向の `restore_selection` と共用）。エディタ: `HierarchyPanel.RestoreSelectionAfterSync` が番号の変わった選択を `ActorDfsSelected` でインスペクタへも知らせる（`Hierarchy/SelectionRestorePlan.cs`）。その副作用を防ぐため、貼り付けの `SELECTED` がヒエラルキーより先に届いていた順番（`clipboard.rs`）を入れ替え、配置の選択（`select_placed_actors`）の前に間引きで遅らせたヒエラルキーを流す（`flush_deferred_hierarchy`）。通し確認（画面外の SEED.exe・TCP の IPC）: 直す前は Ctrl+Z の後に `SELECTED` が来ず、インスペクタの古い番号 17 への `SET_VISIBLE` が別のアクタ `Lid` に当たった → 直した後は `SELECTED:999000004` と `Shell` の `ACTOR_COMPONENTS` が届き、`Shell` に当たる。Redo・プレビューの中を選んでいたとき（`SELECTED:-1`）・普通のアクタの追加の Undo・名前の変更の Undo（同じ番号のまま）も確かめた。単体テスト: undo_selection 6 件・SelectionRestorePlan 4 件。コミットは監督役が追記）

## 2. SeedTemplateThumbnails の `--work` の中身を確かめずに `assets/` を再帰削除する。実プロジェクトの assets を消せるうえ、同時に 2 つ動かすと互いを壊す  【重さ: 中】
- 場所:
  - editor/tools/SeedTemplateThumbnails/Stage/StageProject.cs:76-77
  - editor/tools/SeedTemplateThumbnails/ThumbnailGenerator.cs:102-106
  - editor/tools/SeedTemplateThumbnails/ThumbnailInputs.cs:64
  - （コミット 7cc55bbb）
- 何が起きるか:
  - `dotnet run --project editor/tools/SeedTemplateThumbnails -- --work D:\SEED_projects\WakeOrPay`（または `--work projects/WarashibeFishing`）と渡すと、`<work>\assets` が確認なしで丸ごと消える（ごみ箱も通らない）。続けて `<work>\shots\` の再帰削除と `<work>\runtime*.log` の削除も走る。
  - 実プロジェクトはこのリポジトリの git に入っていない（`.gitignore` の `projects/`、実体は D:\SEED_projects）ので、git からは戻せない。
  - 既定の置き場 `%TEMP%\seed_template_thumbnails` は固定名なので、レーンを並べて 2 つ同時に動かすと、後から始めた方が先の方のランタイムが使っている `assets\` とログを消し、先の方は読み込みの失敗・合図の時間切れから起動し直しを繰り返す。
- 根拠: `AssetsRoot = Path.Combine(Path.GetFullPath(workRoot), "assets"); if (Directory.Exists(AssetsRoot)) Directory.Delete(AssetsRoot, recursive: true);`。`workRoot` は `--work` の値そのまま（ThumbnailInputs.cs:64）で、そこがプロジェクト（`assets/project_settings.json` を持つ）か・道具が作った所かを調べていない。docs/template_library.md:554 は「起動のたびに作り直し」と書くだけで、消すことの警告が無い。
- 確認: 済（監督役とサブレビュアが別々に同じ結論）
- 直し方の案: 道具が作った印のファイル（例 `<work>/.seed_thumbnails`）があるときだけ消し、印が無く `assets/project_settings.json` がある所は拒否する。あわせて既定の置き場を実行ごとの下位フォルダにするか、ロックファイルで同時実行を断る。

## 3. AI / MCP からの値の編集・コンポーネント追加・移動とロジック配置がプレビューにも通り、成功扱いのまま黙って消える  【重さ: 中】
- 場所:
  - runtime/src/engine/core/app_base/app/editor_preview/guard.rs:41-77（拒否表に `AI_*`・`LOGIC_PLACE`・`LOGIC_PLACE_BEGIN` が無い）
  - runtime/src/engine/core/app_base/app/ipc_handler.rs:1790-1805（`AiRemoveActor`・`AiMoveActor`・`AiAddComponent`・`AiSetValue` は判定なしで実行）
  - runtime/src/engine/core/app_base/app/logic_placement_ops.rs:104-106・511（`parent_dfs` の下へ `insert_group_actor`）
  - editor/src/AI/・editor/SeedMcpServer（プレビューの判定がどこにも無い）
  - docs/editor_screen_preview.md §3 末尾（「値の編集…は断らない（エディタが読み取り専用にしている）」）
  - （コミット b5ee44db）
- 何が起きるか:
  - AI パネルや MCP で「タイトルの文字を変えて」と頼む → `seed_find_actor` はプレビューの中の `Title` の DFS をプレビューの印なしで返す → `AI_SET_VALUE` が普通の編集として成功する → 保存・Play・作り直しのどれかで黙って消える。`AI_ADD_COMPONENT`・`AI_MOVE_ACTOR` も同じ。
  - `LOGIC_PLACE:{"parent_dfs": <プレビューの中>, ...}`（`seed_send_ipc` など）で置いたアクタはプレビューの中に入り、保存されない。ヒエラルキーの右クリックからはプレビューの行にロジック配置の項目が出ない（HierarchyPanel.Preview.cs:224-235）ので、届くのは MCP・スクリプト経由。
- 根拠: ガードは「値の編集はエディタの UI が読み取り専用にしている」という前提で値を通しているが、AI ツールと MCP は UI を通らない。`git grep -i preview` で AI・SeedMcpServer に該当なし。
- 確認: 済（拒否表・AI の腕・ロジック配置の挿入先を読んだ）
- 直し方の案: ガードにプレビューの中への `AI_*`・`LOGIC_PLACE(_BEGIN)` の拒否を足す（できれば `SET_*` も。エディタの UI は既に送らないので害は無い）。`seed_find_actor` の結果にプレビューの印を載せ、docs §3 の前提の文を書き換える。
- → 2026-10-03 に済（`editor_preview/guard.rs` の拒否表に `AI_SET_VALUE`・`AI_ADD_COMPONENT`・`AI_MOVE_ACTOR`・`AI_REMOVE_ACTOR`・`LOGIC_PLACE` / `LOGIC_PLACE_BEGIN`（アクタの配置は `parent_dfs`、制御点の追記は `actor_dfs_id` で判定）・インスペクタの値の編集（`field_edit.rs` の分類で対象のアクタがある `SET_*` 全部。`SET_VISIBLE`・`SET_ACTIVE` を含む）・`SET_ACTOR_TRANSFORM`・`SET_CANVAS_TRANSFORM`・コンポーネントの追加・削除・複製・制御点の書き換え・デバッグカメラの値の反映を足した（文言「プレビューの中は編集できません…」。名前の変更・削除は従来どおり通す）。`seed_find_actor` の結果に `is_preview`。エディタの AI ツール（`set_value` など 4 つ）は送る前にプレビューの中かを確かめてエラーを返す（`EditorCommandExecutor.PreviewGuard.cs`。送るだけだと「設定しました」と返り、AI には成功に見えるため）。docs §3 の前提の文を書き換えた。通し確認: 直す前は 8 種の命令がどれも `PREVIEW_ERROR` 無しで通った（`AI_REMOVE_ACTOR`・`LOGIC_PLACE(_BEGIN)`・`SET_VISIBLE` は木や表示を変え、`SCENE_MODIFIED` も来た）→ 直した後は全部 `PREVIEW_ERROR` で木も変わらず、外の実アクタへの同じ命令は通る。単体テスト 2 件。コミットは監督役が追記）

## 4. MTSDF の安全弁の落ち先（アルファ）が同じ合成の結果なので、合成の誤りでは落ちても直らない  【重さ: 中（推測を含む）】
- 場所: runtime/src/engine/core/font/msdf/bake.rs:163-171、distance.rs:333-346（`assign_far_by_depth`）・473-483（`combine` の最初の分岐）・655-656（コミット 65951959）
- 何が起きるか: アルファの真の距離 `d[3]` も `combine` が選んだ 1 つの選び手から取るので、重なった輪郭で選び間違えると中央値とアルファが同じように壊れる。中央値の検査に落ちて RGB へアルファを写しても形は崩れたまま。`alpha_verify` が落ちてもログだけで次の手が無い。
  - サブレビュアの手追いの例: 外枠 O[0,60]²・穴 H[4,56]²・中の四角 I[28,32]²・O の最初の辺の中点 (0,30) に重なる小さな輪郭 Q[-2,2]×[28,32]。O と H の深さが同じ 1 になり、遠い距離 12.5 で同点 → 点 (26,30) で I への距離 -2 の代わりに O の値が選ばれ、I の左側の傾きがアルファごと消える。
- 根拠: 深さは「サンプル点（最初の辺の中点）を含むほかの輪郭の数」（distance.rs:341）なので、重なりがあると入れ子の順と一致しない。テスト `far_nested_contours_do_not_hide_near_contour` は重なりの無い形だけ。
- 確認: 未（サブレビュアの手追い。**推測**: 組み込みの書体は輪郭が重ならないので起きない。可変フォントの静的化をしていない書体など、輪郭の重なる利用者の書体で起きうる）
- 直し方の案: `alpha_verify` も落ちたら、打ち切りなし（cutoff・far を無限大）で焼き直すか、`rasterize_glyph_sdf` の結果を RGBA へ広げて使う。遠い輪郭どうしの同点を作らない決め方にする。

## 5. MTSDF で 1 テクセルより細い線が大きな表示で薄れる・消えるのを、検査が原理的に数えない  【重さ: 中（書体次第・推測を含む）】
- 場所: runtime/src/engine/core/font/msdf/verify.rs:216-231、params.rs:129-136、bake.rs:139-146（em を決めるのは輪郭の長さだけ）（コミット 65951959）
- 何が起きるか: 太さ T < 1 テクセルのまっすぐな線が 2 つのテクセルの中心の間に来ると、両方が外側の値になり、バイリニアで補間しても 0.5 に届かない（中央値もアルファも同じ）。em 40・150 px で T=0.86 なら被覆率約 24%、T=0.6 なら 0%（サブレビュアの式からの計算）。組み込みの書体の倍率で T < 1 テクセルは約 0.035 em（em の正方形の単位）で、明朝の横画や Light・Thin の太さがこのあたり。以前の SDF（em 64）より細部の限界が後退する。
- 根拠: 消える画素は参照の縁から 2 画素（0.5 テクセル）以内に入るので `artifact_px` に数えられない。`detail_px` は計算しているがどこでも使っていない。「一」のような輪郭の短い字は em 40 のまま。テスト `thin_horizontal_strokes_never_vanish_at_1x` は組み込みの書体の 12〜20 px だけ。
- 確認: 未（サブレビュアの手計算。監督役は verify.rs の許す幅が参照の画素 2.0 ＝ 0.5 テクセルであることだけ確かめた）
- 直し方の案: 距離場の山が 0.5 に届かない細部を検出したら em を上げる、または `detail_px` を em の引き上げの判定に使う。

## 6. MTSDF のアトラスの容量が約半分になり、満杯のあとは新しい字が警告 1 回のあと描かれない  【重さ: 中（既知・backlog と docs/ui_components.md §12.14 に記載済み）】
- 場所: runtime/src/engine/core/font/atlas.rs:200-219・293-318、font/mod.rs:130-137・506-508（コミット 65951959）
- 何が起きるか: 既定が mtsdf になったので、設定を書いていない既存のプロジェクトでも上限がふつうの全角字で約 4,500 字から約 2,600 字に下がる。画数の多い字は em 64 まで大きくなり 1 字で最大 2.56 字ぶん。追い出しが無いので、満杯のあとは「かなは出るが漢字は欠ける」穴あきの本文になる。
- 根拠: `alloc_shelf` は最初に入る棚を使う方式で、`None` になった字は `unplaceable` に入る。
- 確認: 未（サブレビュアの読み。既知の制限）
- 直し方の案: backlog の「2 枚目のページ」か「追い出し」。それまでは字数の多いプロジェクトに `font.distance_field: "sdf"` を案内する。

## 7. 地形の部分木の中のプレビューは、Undo と Play の後に DFS をずらし、前の Undo が別のアクタへ当たる  【重さ: 低】
- 場所:
  - runtime/src/engine/core/app_base/app/actor_ops.rs:1510-1532（Undo の写しは地形ルートを位置の印だけにする）・1259-1281（組み直しは地形の現物を Keep）
  - runtime/src/engine/core/app_base/app/editor_preview/ops.rs:436-449（Play の開始で地形の中のプレビューも外す）
  - runtime/src/engine/core/app_base/app/play_snapshot.rs:62-73（地形ルートは `Keep(entity)`）
  - （コミット b5ee44db）
- 何が起きるか:
  - Undo: 地形のノードの下へプレビュー（N ノード）を出す → 前後の写しはどちらも地形の印だけなので、Ctrl+Z しても地形の現物ごと Keep され、プレビューは残ったまま履歴だけ 1 件戻る。プレビューを出す前に積んだ「地形より DFS が後ろのアクタ」の値の編集を次の Ctrl+Z で戻すと、N ずれた別のアクタへ当たる。
  - Play: Play の開始で外したプレビューは Keep の現物から消えたまま戻らない（backlog に記載済み）。そのため Play 前の履歴の DFS が N ずれ、Play を止めた後の Ctrl+Z が別のアクタへ当たる（**この結果は docs・backlog に書かれていない**）。
- 根拠: `snapshot_actors` は地形ルートで `terrain_marker_data` を返し、`rebuild_actors_for_wl` はマーカーが同じ位置なら現物を戻す。プレビューの印は地形の部分木の中でも `outermost_preview_roots` に拾われる。
- 確認: 済（監督役）。地形の下に UI のプレビューを置くことはまず無いので低。
- 直し方の案: 地形ルートの部分木の中への `PREVIEW_PREFAB` を断る（`resolve_insert_target` で祖先に地形ルートがあれば `PREVIEW_ERROR`）。
- → 2026-10-03 に済（`editor_preview/ops.rs::resolve_insert_target` が、差し込み先か祖先に地形ルートがあれば `PREVIEW_ERROR`「地形の中にはプレビューを出せません…」で断る（`tree.rs` の `entity_or_ancestor_matches`。単体テスト 1 件）。docs/editor_screen_preview.md §9・backlog を更新。通し確認（`TERRAIN_INIT` した空のシーン）: 直す前は地形ルート・チャンクの下へ `PREVIEW_ADDED` → 直した後は断り、地形の外の 3D アクタの下へは出せる。コミットは監督役が追記）

## 8. prefab_hash の直し × 開いたままの一括アップグレード → 保存すると古い版で上書きされ、偽の「更新あり」バナーが出る  【重さ: 低】
- 場所: runtime/src/engine/core/app_base/scene.rs:801-804（`build_actor` が `prefab_hash` を写すようになった。コミット b5ee44db）、runtime/src/engine/core/migration/upgrade/prefab_rehash.rs:53-99、editor/src/MainWindow.Migration.cs:60-74
- 何が起きるか: **未保存の編集があるシーンを開いたまま**「ツール → プロジェクトの形式をアップグレード...」を実行 → ディスクの `.actor` が書き換わり、`.scene` の `prefab_hash` も新しい値へ貼り直される → シーンの自動再読込（`EditorPreferences.AutoReloadScene` 既定オン）は「未保存の編集があるので再読込しない」で見送る（設定をオフにしている場合も同じく読み直さない）→ そのまま Ctrl+S すると、メモリの古い `prefab_hash` で `.scene` を上書きする → 次に開くと `PREFAB_STATUS` が stale を数えて「プレハブが更新されています」のバナーを出し、［更新する］（インスタンスの変更を上書きする再展開）へ誘う。
  - 直す前は読み込みで hash が落ちていたので、同じ手順でも「版が不明」でバナーは出なかった。未保存の編集が無ければ自動再読込が読み直すので起きない。
- 根拠: 貼り直しはディスクの `.scene` のテキストだけを書き換える（prefab_rehash.rs）。アップグレードの窓は実行後に開いているシーンへ何もせず（MigrationMessages.cs に開き直しの案内も無い）、自動再読込は未保存なら見送る（editor/src/Scene/SceneAutoReloader.cs:424-427）。
- 確認: 済（監督役）。旧シーン（hash 無し）の扱いは変わっていない（「問題なし」の 5）。
- 直し方の案: アップグレードの実行前に、開いているシーンが未保存なら保存か破棄を求め、実行後は開いているシーンを読み直す。
- → 2026-10-03 に済（「プロジェクトの形式をアップグレード...」の前に、未保存なら［はい］保存してから／［いいえ］破棄して／［キャンセル］やめる、を選ばせる。保存は非同期なので完了（`OnSaveCompleted`）を待ってから窓を開き、失敗したら開かない。窓の中で実行したら（`ProjectUpgradeWindow.Executed`。失敗した実行も含む）開いているシーンをディスクから読み直す。判定は `Migration/UpgradeUnsavedPolicy.cs`（MigrationTests 2 件）、配線は `MainWindow.Migration.cs`。エディタの GUI では未確認（利用者のエディタを動かさない取り決めのため、単体テストとビルドまで）。コミットは監督役が追記）

## 9. `DialogHandle.Close(DialogResult)` が `Choose` を通らないため、`InputText` と `SelectedIndex` の約束が崩れる  【重さ: 低】
- 場所: scripting/src/Api/UI/Navigation/ModalHandle.cs:114（a3d4280f で追加）→ :44-49 → ModalPlane.cs:113-123。比較先は Dialog.cs:190-205（`Choose`）
- 何が起きるか: 入力欄つきのダイアログを外から `h.Close(DialogResult.Positive)` で閉じると `InputText` は null のまま（ModalHandle.cs:74-77 は「Positive で閉じたときだけ入る」と書いているので、`Completed` で `h.InputText!` を使うと NRE）。`Close(DialogResult.Selected)` では `SelectedIndex` が -1 のままで `Items[h.SelectedIndex]` が範囲外。入力欄のフォーカスの解除・キーボードを避ける持ち上げの片付け（`field.Unfocus()`・`ClearKeyboardLift()`）も通らない。
- 根拠: `base.Close` は `plane.RequestClose` へ直行し、`SetInputText`・`SetSelectedIndex` は `Choose` の中でしか呼ばれない。
- 確認: 済（`Choose` と `RequestClose` を読んだ）
- 直し方の案: `Plane is Dialog d` のときは `Choose` 相当（留め金・入力の確定・片付け）を通す入口へ回す。外からの Selected は Dismissed に倒すか docs に書く。

## 10. `TextField.AllowSelection = false` でもコピーは許可されたまま  【重さ: 低】
- 場所: scripting/src/Api/UI/Widgets/TextField.cs:374・435・476（コミット a3d4280f）、runtime の文字入力の session.rs:262
- 何が起きるか: 選択を畳むのは C# の Update の後追いなので、Ctrl+A と Ctrl+C がスクリプトの 2 回の Update の間に届くと、Rust 側が全選択済みの状態でクリップボードへ書く。docs/ui_text_input.md の「選択が無いのでコピー・切り取りも起きない」と食い違い、コピペを防ぐ目的（W3-2b (6)）に穴が残る。
- 根拠: session は `EditKey::Copy` を `allow_copy && !is_collapsed()` で判定し、C# は `AllowSelection` を session の設定へ渡していない。
- 確認: 未（サブレビュアの読み。起きる頻度は推測）
- 直し方の案: `AllowSelection = false` の間は session へ `AllowCopy = false` も渡す。

## 11. ui の構成では、エディタのモデル／アクタのサムネイルがすべて「空の描画」の誤った理由で失敗する  【重さ: 低】
- 場所: runtime/src/engine/core/app_base/app/frame_renderer.rs:2090-2101、thumbnail_ops.rs:459-545・825-827（コミット 65ac203a）
- 何が起きるか: `"render": {"profile": "ui"}` のプロジェクトを Edit で開くと、プロジェクトパネルのモデルのサムネイル（`THUMBNAIL:`）と `RENDER_ACTOR_THUMBNAIL` が 1 件ずつ撮影まで進み、毎回 `ERROR_EMPTY_RENDER`（「モデルが無い／読み込みに失敗した可能性」）になる。理由が誤って伝わり、glb の数だけ GPU のフレームを無駄に使う。
- 根拠: `scene_3d=false` のとき `all_mcs` を空にする（サムネイルの世界線も対象）。構図の計算は ECS を直接引くので成功し、撮影の段まで進む。docs/rendering_profiles.md §10 の「ui で動かないもの」に記載が無い。
- 確認: 一部済（`all_mcs` を空にする所だけ読んだ）
- 直し方の案: `begin_thumbnail_job` の先頭で `!flags.scene_3d` なら理由を書いたエラーで返し、§10 に 1 行足す。
- → 2026-10-03 に済（`begin_thumbnail_job` の先頭で `scene_3d=false` なら「この描画の構成（…scene_3d=false…）では…撮影できません」で断る（単体テスト 1 件）。docs/rendering_profiles.md §10 に 1 行。通し確認（ui のプロジェクトの Wake or Pay を Edit で開き、3D のアクタの `RENDER_ACTOR_THUMBNAIL`）: 直す前は撮影まで進み、被写体（BrainStem）の写っていない絵を `RENDER_ACTOR_THUMBNAIL_DONE` として書いた（レビューの予想の `ERROR_EMPTY_RENDER` ではなく、別の 2D の何かが写った 128px の絵）→ 直した後はすぐ理由つきの ERROR で、PNG は書かない。コミットは監督役が追記）

## 12. picking=false の単体の Play で `RENDER_ACTOR_THUMBNAIL` を受けると、30 秒待ってから誤った理由で失敗する  【重さ: 低】
- 場所: runtime/src/engine/core/app_base/app/id_buffer_ops.rs:82-84（コミット 8099eca2）、thumbnail_ops.rs:462・290-297、frame_renderer.rs:8791-8792・9325-9335・9408-9423
- 何が起きるか: ui の構成か `"picking": false` の、エディタに接続していない Play が TCP の IPC で図鑑のサムネイルの要求を受けると、ジョブは撮影の段で止まり、30 秒（`JOB_DEADLINE_SECONDS`）の後に「フレームが回っていない可能性」という誤った理由で失敗する。
- 根拠: `ensure_id_buffer` は方針 Never で何も作らずに戻るが、`begin_thumbnail_job` は続行する。ID バッファが無いと読み戻しを予約しないので、`receive_id_mask(None, …)` の「次の poll で失敗させる」経路にも届かない。8099eca2 より前は ID バッファが常にあったので起きなかった。
- 確認: 一部済（`begin_thumbnail_job` が `ensure_id_buffer` の結果を見ないことを読んだ）
- 直し方の案: `ensure_id_buffer` の直後に `self.id_buffer.is_none()` なら「この描画の構成（picking=false）では撮影できません」で返す。
- → 2026-10-03 に済（`ensure_id_buffer` の後に ID バッファが無ければすぐ断る: 作らない構成（picking=false）なら「この描画の構成（picking=false）では撮影できません」、作ってよい構成なのに用意できなければ準備の理由（`id_buffer_ops.rs` の `prepare_id_buffer_for_capture`・`id_buffer_unavailable_reason`。単体テスト 1 件）。docs/rendering_profiles.md §14.5 に追記。通し確認（`full`＋`picking=false` の単体の Play・TCP）: 直す前は 30.1 秒待って「30 秒以内に描画が完了しませんでした（フレームが回っていない可能性）」→ 直した後は 0.9 秒で正しい理由。picking=true（OnDemand）は今までどおり撮れる。コミットは監督役が追記）

## 13. ヒエラルキーの目アイコンはプレビューの行でも押せ、普通の編集として未保存になる  【重さ: 低】
- 場所: editor/src/Panels/HierarchyPanel.xaml.cs:829-836、HierarchyPanel.Preview.cs:207-214（コミット b5ee44db）
- 何が起きるか: プレビューの行で目アイコンを押すと `SET_ACTOR_VISIBLE` が送られ、ランタイムは普通の編集として Undo に積み、`SCENE_MODIFIED` で `*` を付ける。変更はメモリの中だけで、作り直し・Play で戻る。docs §9 の「読み取り専用はインスペクタとヒエラルキー」と食い違う。
- 根拠: トグルは `node.Id` で無条件に送り、プレビューの行は薄く表示するだけ（インスペクタ側の目アイコンは無効にしてある）。
- 確認: 済
- 直し方の案: `node.IsPreview` のときはトグルを無効にし、理由をツールチップで出す。
- → 2026-10-03 に済（プレビューの行の目アイコンを無効にし、理由をツールチップで出す（無効でも出す）。`HierarchyPanel.Preview.cs` の `ApplyPreviewVisibilityToggleState`。ランタイムも `SET_VISIBLE` をプレビューの中へ通さない（#3）。エディタの GUI では未確認。コミットは監督役が追記）

## 14. シーンビューの右クリック「アクタファイル化」がプレビューの中のノードにも出る（backlog の記述と食い違い）  【重さ: 低】
- 場所: editor/src/MainWindow.Viewport.cs:535-536、editor/src/Panels/HierarchyPanel.xaml.cs:1540-1551、runtime/.../actor_ops.rs:1356-1424、docs/backlog.md（「エディタはプレビューのノードのドラッグ（＝アクタファイル化）とメニューを出さない」）
- 何が起きるか: プレビューの中のノード（枠つきの差し込みでは中身の根も「中」）をビューポートで選ぶと `EXPORT_ACTOR` が通る（ランタイムが断るのは根だけ）。書き出す中身は `clear_links_recursive`（editor_preview/tree.rs:232-239）で入れ子のプレハブのリンクを外した写しなので、元のプレハブへ上書き保存すると入れ子のインスタンスが平坦化される。成功後はプレビューのノードに `prefab_source`・`prefab_hash` が付く（メモリの中だけ）。
- 確認: 済（`ShowExportActorDialog` にプレビューの判定が無いことを読んだ）
- 直し方の案: `ShowExportActorDialog` でプレビューの中なら断ってトーストを出し、backlog の文を直す。
- → 2026-10-03 に済（`HierarchyPanel.ShowExportActorDialog` がプレビューの中なら書き出さずに false を返し、シーンビューの右クリックはトースト「プレビューの中はアクタファイル化できません…」を出す。backlog の文を直した（2026-10-02 の「メニューを出さない」はヒエラルキーだけの話だった）。ランタイムの `EXPORT_ACTOR` は中のノードをまだ断らない（MCP の `seed_send_ipc` で直接送る経路。backlog）。エディタの GUI では未確認。コミットは監督役が追記）

## 15. プレビューだけを選んだ COPY は黙って何もせず、前のクリップボードが残る  【重さ: 低】
- 場所: runtime/src/engine/core/app_base/app/clipboard.rs:36-55（コミット b5ee44db）
- 何が起きるか: プレビューの根（または中のノード）だけを選んで Ctrl+C（シーンビューの「コピー」も同じ）→ `new_clipboard` が空なので `actor_clipboard` を書き換えない → 続けて普通のノードを選んで Ctrl+V すると、**前にコピーした別のもの**（別のタブ・別のシーンでコピーしたアクタ）が貼られ、保存される。エディタへの知らせも無い。
- 根拠: `if !new_clipboard.is_empty() { self.actor_clipboard = new_clipboard; … }` で、空のときは何も返さず return する。
- 確認: 済（監督役）
- 直し方の案: プレビューを除いて空になったら `actor_clipboard` を空にするか、`PREVIEW_ERROR`（「プレビューはコピーできません」）を返す。
- → 2026-10-03 に済（`do_copy` で選択がプレビューだけなら `actor_clipboard` を空にし、`PREVIEW_ERROR`「プレビューはコピーできません…」を返す。実アクタとの混在は実アクタだけをコピー（従来どおり）。`clipboard.rs` の `copy_outcome`（単体テスト 2 件）。通し確認: 直す前は続く `PASTE` で前にコピーした別のアクタ（`Gap1`）が貼られた → 直した後は貼られない。コミットは監督役が追記）

## 16. MTSDF の誤差補正の距離の確かめが、符号を直す前の距離を基準にしている  【重さ: 低】
- 場所: runtime/src/engine/core/font/msdf/error_correction.rs:159-163、distance.rs:523-532（`pseudo_distance_at` は符号を直さない）・586（`generate` だけが `correct_sign` を通る）（コミット 65951959）
- 何が起きるか: 合成の符号と走査線の内外が食い違う所（自己交差・向きの誤った輪郭）で、基準の値が 0.5 を挟んで反転し、本当は 0.45 の細い隙間を「直したほうが近い」と平らにする、または偽の縁を残す。
- 確認: 未（サブレビュアの読み。msdfgen が走査線で符号を直すとき距離の確かめを切る、という対比は記憶によるもの）
- 直し方の案: 基準の距離にも同じ内外の符号を当てる。

## 17. サムネイルの道具の見張りスレッドが Dispose 後の `Process.Id` を読み、道具ごと落ちうる  【重さ: 低】
- 場所: editor/tools/SeedTemplateThumbnails/Runtime/QuietProcess.cs:180-182・206・229-233（コミット 7cc55bbb）
- 何が起きるか: 後片付け（Kill の直後でランタイムがまだ終わっていない）で `Cancel()` → `Process.Dispose()` の順に呼ばれる間に、見張りスレッドが `FindMainWindow` で `Process.Id` を読むと InvalidOperationException（背景スレッドの未処理例外で道具が終了）。時間幅はごく短く、まれ。
- 確認: 未（サブレビュアの読み。Close 後の `Process.Id` の挙動は記憶による）
- 直し方の案: pid を起動時に控えて使う、または Dispose でスレッドを Join してから Process を Dispose する。

## 18. 最近使ったプレハブの一覧は、エディタを複数開くと取りこぼし・壊れうる  【重さ: 低】
- 場所: editor/src/Preview/PreviewRecentStore.cs:12・100-112・170-185（コミット b5ee44db）
- 何が起きるか: 読み直してから書くまでに別のエディタが割り込むと片方の追加が消える。一時ファイル名が `screen_preview_recent.json.tmp` で固定なので、一方の `File.Move` がもう一方の書きかけを本体へ移すと JSON が壊れ、次は空の一覧になる。失うのは最近の一覧だけ。
- 確認: 未（サブレビュアの読み）
- 直し方の案: 一時ファイル名を固有にし、コメント「片方の追加を消さない」を実際の保証に合わせる。

## 19. 同じファイル名のテンプレートが 2 つのカタログにあると、撮り終えた後に道具が落ちる  【重さ: 低（今のカタログでは起きない）】
- 場所: editor/tools/SeedTemplateThumbnails/Program.cs:125-126（コミット 7cc55bbb）
- 何が起きるか: templates/actors と templates/ui の両方に button.actor があると `ToDictionary(..., OrdinalIgnoreCase)` が ArgumentException。PNG を書いた後なので結果の一覧も終了コード 0〜3 も出ない。
- 確認: 未（サブレビュアの読み）
- 直し方の案: 並び順の鍵をライブラリ相対パスにする。

## 20. docs・規約と実装の食い違い（4 件）  【重さ: 低】
- docs/backlog.md:526（コミット b5ee44db）: 「シーンを読み込むと `prefab_hash` が落ち…（直していない）」が未完了のまま。同じコミットの scene.rs:801-804 で直してある。→ 完了にして日付と直した場所を書く。（確認: 済）
  → 2026-10-03 に済（項目を完了にし、直したコミットと場所を書いた。通し確認: App.scene の `prefab_hash` 5 件 → 読み込んで `SAVE_SCENE_COPY` した .scene も 5 件。コミットは監督役が追記）
- docs/editor_screen_preview.md §8（b5ee44db）: (a) `PREVIEW_REFRESH_PATH` は組み直しに失敗した根ごとに `PREVIEW_ERROR` も返す（ops.rs:314-318）のに表に無い。(b) `PREVIEW_PREFAB` の `ADDED_ROOT_LOST` の経路は、木を変え・Undo を積み・HIERARCHY を送った後で `PREVIEW_ERROR` を返す（ops.rs:155-163）ので「失敗は木が変わらない」と食い違う（通常は起きない経路）。（確認: 済）
  → 2026-10-03 に済（§8 の表に、`PREVIEW_REFRESH_PATH` の根ごとの `PREVIEW_ERROR`（その根は古いまま。最後に必ず `PREVIEW_REFRESHED`）と、`PREVIEW_PREFAB` の `ADDED_ROOT_LOST` だけは木を変え・Undo を積み・通知した後で `PREVIEW_ERROR` を返す例外を書いた。コミットは監督役が追記）
- .claude/rules/renderer-gpu-resources.md:11（caabe0b1）: 「ui 構成ではクラスタを作らない」とあるが、`ClusterResources::new` は構成によらず作る（drawer/mod.rs:218。docs/rendering_profiles.md §10 も「止めていない（3.4 MiB）」）。規約に従って書き足す人が誤った前提を持つ。（確認: 未）
- docs/scripting_api.md:4146-4147（a3d4280f）: `spinner.Size = 48f` は部品が動き始めた後に書いても見た目に反映されない（ApplyLook は Refresh のときだけ。ProgressSpinner.cs:79-103）。例を `SetSize`・`SetThickness` にする。（確認: 未）

---

## 確認したが問題なしと判断した点

1. **プレビューがファイルへ漏れる経路は無い**: 書き口を `git grep`（`to_json`・`to_json_with_actors`・`actor_file::save`・`fs::write`・`write_atomic*`）で全部挙げて確かめた。`.scene` は `Scene::save` → `to_json`（トップレベルの根を飛ばし子孫を `strip_editor_previews`）、`SAVE_ACTOR`・`EXPORT_ACTOR` は `actor_file::save`（根なら拒否・子孫は複製して除く）、コピーは `do_copy`、写しは `collect_snapshot_actors`。`--upgrade-project`・`prefab_rehash`・パッケージ化はディスクのファイルだけを扱うので、メモリにしかないプレビューは入らない。Play の写し・Undo の写しは印を残し、`build_actor` が印と `layer_bias` を戻す。
2. **プレビューの中身が保存側へ出る木の操作は無い**: `apply_delete` は `recursive` の値によらず常に部分木ごと消し（actor_ops.rs:1143-1176）、`REMOVE_ACTOR` も部分木ごと（actor_utils.rs:596-623）なので、根を消して中身が親やルートへ繰り上がることは無い。`DROP_ACTOR` の置き先はトップレベルのルートキャンバスだけ（canvas_drop.rs:147-183）。付け替え・グループ化・包む・追加・貼り付けはガードが断る。
3. **HIERARCHY_QUIET で本物の編集の `*` を取りこぼさない**: ランタイムは間引きで遅らせている HIERARCHY があるときは印を送らず、印の後は必ず即時に HIERARCHY を送る（`do_send_hierarchy` はシーンがある経路でしか呼ばれない）。エディタは印と HIERARCHY の両方を同じ受信スレッドで同期的に数えるので、数が残り続けることは無い。
4. **Delete の振り分けの番号の詰めは、エディタの木が 1 通遅れていても合う**: 作り直しで部分木の大きさが変わった直後・Undo でプレビューが既に消えた直後のどちらでも、`DELETE` の番号はランタイムの木で同じアクタを指す（手で計算）。`PREVIEW_CLEAR` が断られる状態（写しの閲覧中など）では `DELETE` も断られる。
5. **prefab_hash の直しは旧シーンの扱いを変えない**: hash の無い旧シーンは `None` のまま `unknown`（バナーの対象外）。`PREFAB_STATUS` はインスタンスの中へ降りない。ドロップ・ロジック配置・再展開は根の hash を明示的に上書きし、プレビューは `clear_links_recursive` で印を外すので、版ずれの数にもプレビューは入らない。
6. **RootAdoptChild（fc706e68）**: `_adoptTaken` は 1 回限りで、`Begin` が実体を作り `Finish` は始まった後にしか呼ばれないので、根の段は後続の SetRoot / Replace より先に必ず組み立てられて引き取る。根は `KeepState` 既定 true で積むので手放されず、引き取った後の SetRoot / Replace はプレハブから作る。Play 中の付け替えは Play を止めたときに Play 開始の写しから戻る（Keep は地形と編集タブだけ）。`UiWidget.Of` はスタックの OnStart 前は null を返すので、根より先に別の画面を積む経路も無い。
7. **bb6416fa の `IsUnderProjectRoot`**: `GetFullPath` で区切りをそろえ、末尾に区切りを足した前方一致で兄弟フォルダ（D:\A と D:\AB）を含めない。プロジェクトが未確定なら従来どおり制限しない。例外は false で起動を止めない。`--scene` 指定はこの判定を通らない（明示の指定なので妥当）。
8. **MTSDF（65951959）の式と端の条件**: 辺への距離・色分け（simple・ink trap・しずく形）・輪郭の向き・重なりの合成・誤差補正は msdfgen と同じ式（サブレビュア）。長さ 0 の辺・制御点の重なり・解の数 -1・0 ベクトルの正規化・ニュートン法の NaN は安全側に倒れる。アトラスは Rgba8Unorm（sRGB 変換なし。監督役が確認）で、0 で埋めた隙間 2 テクセルと ClampToEdge で隣の字を拾わない。`fs_sdf` は `d_shape == d_true` のとき旧式と代数的に同じ。`font.distance_field` の未知の値・型違いは警告して既定。
9. **render.profile（65ac203a・8099eca2）で作らない資源を踏む経路**: 影の置き場は層の数を保った 1x1、GI は `supported=false` で attach も record もしない、bindless を読む所はすべて置き場が Some になる条件と同じ `rt_shadows_supported() && bindless_supported()` で決まり、ID バッファの読み手はすべて `if let Some` で守られている（サブレビュア）。skin_compute.wgsl の barrier は一様な分岐の外、共有メモリ 7,168 B、インスタンス 0 は dispatch しない。`render_profiles.json` は `include_str!` で埋め込みなので pak・APK に無くても読める。`SEED_FIXED_FRAME_DT` は 0・負・NaN・inf を無視する。
10. **SEED.UI（a3d4280f）の状態機械と名前の一致**: 開く動きの途中の Close・二重 Close・連打・戻る・メニューの準備中の Close・面ができる前の Close を Phase の守りと結果の留め金でたどり、取りこぼしもハングも無い。dialog.actor・dialog_item.actor・toast.actor・progress_spinner.actor の子の名前はコードの道の定数と一致。追加のテーマのトークンは default_theme.json・トークンの一覧・UiTokenCatalog の全部にある。列挙は末尾に足しただけ（既存のシーンの数値の意味は変わらない）。大きさの見張りは自分の LayoutSize へ戻ってこないので無限ループにならない（サブレビュア）。

## 読めなかった範囲・注意

- 実行・ビルド・テストは一切していない。4〜6・16 の数値と例はサブレビュアの手計算で、msdfgen・ab_glyph・ttf-parser との突き合わせは記憶による。
- frame_renderer.rs（約 1 万行）は差分の箇所と、ID バッファ・bindless・天球・影・サムネイルの読み手だけ。skin_compute_equivalence の harness と random_model、render.profile のエディタ側（7c7ab934 以降）は読んでいない。実 GPU（Mali を含む）でのビット一致も未確認。
- ui_gallery.scene の全体、UiComponentsTests の DialogExtensionTests・WidgetExtensionTests、scripting_api.html、PrefabPreviewPickerWindow の XAML、ScreenPreviewTests の IpcTests 以外は流し読みか未読。
- サムネイルの道具の ActorPatch・StageNodes・ThumbnailSample の解釈・画像の合成は流し読み。
- 83bfe1fe より後のコミット（0045ce2b の Prewarm・Push(GameObject)、3111058f の PREFAB_LIVE_PATCH・書き戻しなど）は対象外。特に 3111058f はプレハブの書き戻しを含むので、プレビュー（保存されない部分木）との組み合わせは別途見る価値がある。
