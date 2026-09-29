# 作業バックログ（未着手・保留の課題）

セッションやエージェントをまたいで共有する「今後やらないといけないこと」の一覧。
着手するときは該当項目を読み、完了したら項目を削除する（履歴は git に残る）。
新しい課題が見つかったら、ここに追記する（発見日・背景・関連ファイルを必ず書く）。

記法: `- [ ] 題名 — 背景 / 関連 / 備考`。優先度は上から順（高→低）。
パス表記: `<project>/` はゲームプロジェクトのフォルダ（例: `D:\SEED_projects\WarashibeFishing`）。
2026-09-15 にゲームプロジェクトを SEED リポジトリの外へ移した（`projects/` は追跡しない）。

関連ドキュメント: アニメーションタイムラインの操作・仕様は
[docs/editor_animation_timeline.md](editor_animation_timeline.md)（フレーム編集 / キー挿入 /
ライブプレビュー / 既知の制限）が正典。

## エディタ

- [ ] **検索バーの閉じるボタンだけ文字の「✕」のまま** — 2026-09-19。エディタ中の「×」ボタンは共通ファクトリ（`editor/src/Controls/CloseIconButton.cs`）へ寄せてベクターアイコン（`Icon.Close`）に統一したが、スクリプトエディタの検索／置換バーの閉じるボタンだけは文字リテラル `"✕"` の `Button` として残っている（`editor/src/Panels/ScriptEditor/FindReplaceBar.cs::MakeButton` を「<」「>」「置換」と共有しているため）。当たり判定は `MinWidth 28` × 文字高 ≒ 18px 以上あるので押しづらさは無いが、`.claude/rules/editor-icons.md` の「アイコン代わりの記号文字を使わない」には反する。直すなら MakeButton をテキスト用とアイコン用に分けるところから。関連: `editor/src/Panels/ScriptEditor/FindReplaceBar.cs:74`。

- [ ] **ビルド構成の切り替えで未保存のシーン編集は失われる** — 2026-09-12。ツールバーのビルド構成コンボ（Debug / Develop / Release）は Edit ランタイムを終了して建て直すため、ランタイム側にしか無い未保存の編集は消える。現状は `_isDirty` のときに確認ダイアログを出して同意を取るだけで、保存してから切り替える導線は無い。Play の「保存 → 切り替え → 復元」と同じ仕組み（`SaveCurrentSceneToTempAsync`）で退避・復元できるはず。関連: `editor/src/MainWindow.RuntimeBuildConfig.cs::OnRuntimeBuildConfigChanged`、`editor/src/Runtime/RuntimeManager.cs::SwitchBuildConfigAsync`、docs/runtime_build_configs.md。

- [ ] **構成ごとに `runtime/target/<構成>/` が増えてディスクを食う** — 2026-09-12。Debug / Develop / Release はビルドキャッシュを共有しないため、3 構成すべてを使うと target が 3 セット（各数 GB）できる。エディタからは掃除できず、利用者が手で消すしかない。使っていない構成の target を消す導線（またはサイズ表示）があってもよい。関連: docs/runtime_build_configs.md。

- [ ] **SkinnedSprite ノードの pivot が事実上効いていない** — 2026-09-07。`CanvasTransform::to_mesh_mat4(sx, sy)` が `to_sprite_mat4` に委譲しており、pivot オフセットが `pivot × size_scale`（≒0.5px）にしかならない。描画と枠は同じ行列なので位置ズレは無いが、Inspector の pivot を変えても回転中心が動かない。**Text は解決済み**（2026-09-07。枠あり = `box_width > 0` のときのみ、`to_mesh_mat4_no_pivot` + レイアウト側の平行移動で Sprite と同じ正規化 pivot が効くようにした。枠なしは従来どおり pivot 無効）。SkinnedSprite は未対応（メッシュ寸法を pivot の基準サイズとして採るか、実寸 px を直に採るかの規約を決めるところから）。
  対処には「Text は実測枠のサイズを pivot の基準にする」設計判断が要る。関連: `runtime/src/engine/components/canvas_transform.rs`、`font/canvas_text.rs`、`app/canvas_text_bounds.rs`。

- [ ] **アクター編集タブで親が回転している 2D アクタの移動書き戻し** — 2026-09-07。`actor_2d_layout_ctx` が None のフォールバック経路は親回転の逆適用をしない（World/Local どちらでも同じ既存の制約）。関連: `app/drag_handler.rs`、`app/canvas_gizmo_basis.rs::canvas_world_to_parent_local_pos`。

- [ ] **2D 複数選択時のギズモピボットが 2D/3D 混在で壊れる** — 2026-09-08。`selected_actors_centroid` はキャンバス px（2D）とワールド座標（3D）を区別せず平均するため、混在選択ではピボット位置が意味の無い点になる。変形自体はプライマリの種別だけに効く（＝結果は壊れない）が、ギズモの表示位置と回転中心がずれる。関連: `app/gizmo_handler.rs::selected_actors_centroid`。
- [ ] **2D 複数選択ドラッグ中の物理押し戻しはプライマリ 1 体にしか効かない** — 2026-09-08。`apply_drag_pushback_2d` はドラッグ中エンティティ（プライマリ DFS+1）のスナップショットだけをシフトするため、複数選択でコライダーがめり込んだ場合に非プライマリ側は押し戻されない。関連: `app/physics2d_ops.rs`。

- [ ] **モーダル変形のステータス表示（数値入力の表示欄）が無い** — 2026-09-07。3D/2D とも G/R/S 中の入力値・軸拘束をエディタのステータスバーに出す仕組みが未実装。関連: `app/modal_transform.rs`（`MODAL:*` IPC）、`editor/src/MainWindow*.cs`。

- [ ] **3D ワールドキャンバス配下の選択枠が 2 段以上ネストすると出ない** — 2026-09-07。`frame_renderer.rs` の `find_parent_actor_of_dfs` + `get_3d_canvas_world_mat` が直接の子のみ対象。フォルダを挟んでも同様。

- [ ] **スプライトボーンのパス解決がフォルダ非対応** — 2026-09-07。`sprite_bone_ops.rs::descend_by_path_mut`、`sprite_skin.rs::resolve_bone_matrix_by_path`、`canvas_collect.rs::dfs_of_relative_path` は直下のみ照合。ボーンアクタを手動でフォルダに入れると崩れる（sprite_skin は名前 DFS へフォールバック）。キャンバスの透過規則（`canvas_node_is_transparent`）をそのまま当てると行列連鎖の意味が変わるため要設計。

- [ ] **エディタの CPU ピッキング（アクター編集 2D タブ）が `layer` を見ていない** — 2026-09-08。GPU ピック（`collect_canvas_id_items`）は描画と同じ「ゾーン → レイヤー → 種別」で並ぶようにしたが、`app/pick_2d.rs` の候補ソートは種別（Sprite/Canvas）・ゾーン・階層の深さ・DFS 順だけで `layer` を無視する。レイヤーで前後を入れ替えたキャンバスでは、見た目の最前面と違うアクターが最初の候補になる。関連: `app/pick_2d.rs::kind_rank` 付近のソート。

- [ ] **1 アクターに Sprite と Text が同居するとピック対象が片方しか作られない** — 2026-09-08。`canvas_collect.rs::collect_canvas_id_items` は「矩形スプライト → スキンスプライト → テキスト」の優先で最初に見つかった 1 つだけを ID アイテムにする。描画では両方出るので、テキストだけがはみ出している領域はクリックできない。実害が出たら 2 アイテム出す（それぞれの枠で）ように直す。

- [ ] **`collect_canvas_actors_in_rect`（矩形選択）が階層非対応** — `ct.position` をそのまま使う点包含判定で、親の変換を考慮しない既存の粗さ。関連: `app/actor_utils.rs`。

- [ ] **Text 枠の左右が送り幅基準** — 2026-09-07。左サイドベアリングが負のグリフ等で数 px 食い違う。縁取りパディングで概ね吸収されるため低優先。関連: `font/text_layout.rs::measure_text_box`。

- [ ] **`build_text_bounds_map()` がエディタ中毎フレーム実行** — HUD 規模では問題ないが、テキストが数千文字規模になる場合はキャッシュを検討。関連: `app/canvas_text_bounds.rs`。

- [ ] **旧コードで作られた既存シーンの 2D「グループ」は通常アクタのまま** — 2026-09-07。2D フォルダノード導入前に作ったグループは `is_folder=false`。自動マイグレーションは無く、フォルダにしたい場合は作り直し。必要なら名前ベースの救済を検討。

- [ ] **MCP の `seed_screenshot` は画面キャプチャ方式（隠れると撮れない）** — 2026-09-07。エディタ側の画面 DC → BitBlt で実装したため、エディタウィンドウが最小化・他ウィンドウで隠れていると正しく撮れない。堅牢化するならランタイム側の読み戻し（`runtime/src/engine/core/renderer/screenshot.rs` は環境変数駆動でスワップチェーン読み戻し済み）を IPC 駆動（`SCREENSHOT:<path>` → `SCREENSHOT_DONE:<path>`）へ拡張する。注意点: スワップチェーンの `COPY_SRC` は `screenshot::is_enabled()` のときだけ付くため、代わりに `RT_LDR`（`COPY_SRC` 付き）を読むのが素直。`target:"editor"`（ウィンドウ全体）はランタイム側では実現できないのでエディタ側キャプチャを残す必要がある。関連: `docs/editor_mcp.md`、`editor/src/AI/Capture/WindowScreenCapture.cs`。

- [ ] **MCP ツールの実機未検証** — 2026-09-07。エディタを起動した状態での `seed_screenshot` / `seed_select` / `seed_play` / `seed_anim_preview` の往復は未確認（実装時にエディタが起動していなかった）。特に `SELECT:` 送信でインスペクタ表示が追従するか、`play_control` の状態遷移待ちが実測でどれくらいかかるかは要確認。関連: `docs/editor_mcp.md` の「代表的なループ」。

- [ ] **インスペクタのロックはリネームでも解除される** — 2026-09-12。ロック対象の同一性を「DFS ID ＋ ロック時の名前」で見ているため（DFS ID はツリーの位置なので、手前のアクターが増減すると別アクターを指す）、対象をリネームすると一致が崩れて自動解除される。削除と ID ずれを区別できないのが根本原因で、直すならランタイム側にアクターの安定 ID（世代付きハンドル等）が要る。関連: `editor/src/Panels/InspectorPanel.Lock.cs::ValidateInspectorLock`、docs/inspector_features.md §2-2。

- [ ] **「画像比率に設定」は TGA / WebP で寸法を取れない** — 2026-09-12。WPF の `BitmapDecoder` に該当コーデックが無い環境では元画像の寸法が読めず、ボタンを押しても何も起きない（ツールチップとログには理由を出す）。テクスチャの参照ダイアログは `.tga` / `.webp` も選べるので、必要になったら簡易ヘッダパーサ（TGA は先頭 18 バイト、WebP は VP8/VP8L/VP8X ヘッダ）を足す。関連: `editor/src/Panels/Inspector/ImageSizeCache.cs`、docs/inspector_features.md §1-4。

## 音声辞書（AudioDictionary）の残件（2026-09-11 実装時）

- [x] ~~実機でのランタイム経路の確認~~ — 2026-09-11 に一時アセットルート（`%TEMP%` 配下・`D:\SEED_assets` は不使用）でランタイム単体起動（`SEED.exe --mode=play --assets-root=... --scene=...`）して確認済み。`AudioDictionary` ハンドルの `TryGetPath` / `DefaultVolume`、`PlayDict` の解決、辞書モード AudioComponent の `play_on_start`（`IsPlaying=True` を frame 5/30/120 で確認）、解決できないキーの警告が 1 度だけ出ること、までを実測した。**この検証で 2 件の不具合を見つけて修正済み**（`slot_is_kind` への登録漏れ＝`GetComponent<AudioDictionary>()` が常に null／音源を解決できない `play_on_start` の警告が毎フレーム出る）。**残るのはエディタ UI 経路のみ**（下記 2 項目）。

- [ ] **プラグインホストのセーブテストが並列実行で落ちることがある（本件と無関係の既存問題）** — 2026-09-11 に `cargo test` 全体実行で発見。`engine::plugin::host::tests::set_save_int_writes_flag_and_keeps_other_keys` はプロセス共通の環境変数 `SEED_SAVE_DIR` を書き換えるため、他のセーブ系テストと並列に走ると保存先が入れ替わって `save.json を読めない` で落ちる（単独実行では通る）。テスト側でグローバル状態を直列化するか、環境変数に頼らない注入へ変える必要がある。関連: `runtime/src/engine/plugin/host.rs:117` 付近、`runtime/src/engine/core/save/path.rs`。

- [ ] **インスペクタの辞書編集 UI が未操作確認** — 2026-09-11。グループ追加・行追加・用途名編集・音声ファイルのドロップ・グループ削除の往復（`SET_AUDIO_DICT` → `ACTOR_COMPONENTS` 再送）はビルドと純ロジックのテストのみ。特に「テキスト欄を編集して Enter → インスペクタ再構築でフォーカスが飛ぶ」体感と、行数が多いときのスクロールは実機で見たい。関連: `editor/src/Panels/InspectorPanel.AudioDictionary.cs`。

- [ ] **辞書キー選択ウィンドウのドロップ経路が未操作確認** — 2026-09-11。AudioComponent の音源欄へ Hierarchy からアクタをドロップ →`GET_ACTOR_COMPONENTS` →キー選択ウィンドウ→`SET_AUDIO_FIELD:dictionary_key` の一連。シーンビューからのドラッグ（`SceneViewActorDfsId`）も同じ経路を通るはずだが未確認。関連: `editor/src/Controls/AudioDictionaryKeyWindow.cs`、`InspectorPanel.AudioDictionary.cs::ResolvePendingAudioDictKeyPick`。

- [ ] **辞書モードの AudioComponent は音量欄が出ない（仕様）が、`volume` の値自体は保持される** — 2026-09-11。辞書モードでは音量も辞書から解決するため、インスペクタの音量行を隠している。ファイルパスモードへ戻すと以前の値が復活する。1 インスタンスだけ音量を変えたいという要望が出たら「辞書の既定値 × コンポーネント倍率」への変更を検討する（現状は置換）。関連: `app/audio_dictionary_ops.rs::resolve_audio_component_source`。

- [ ] **`docs/scripting_api.md` の `HasComponent(name)` 一覧が `has_component` の実装より少ない** — 2026-09-11 に気付いた既存のズレ（今回 `AudioDictionary` を追記し「など」に緩めただけ）。実装は `Model` / `SkinnedSprite` / `LineRenderer` / `Skybox` / `Text` / `WaterVolume` / `WaterLink` / `ControlPoint` なども受け付ける。正典側（docs）を実装に合わせて洗い直すのは別作業。関連: `runtime/src/engine/core/scripting/host_api.rs::has_component`。

- [ ] **アセット側（シーン・アクタ・ゲームスクリプト）への適用は次フェーズ** — 2026-09-11。今回の作業ではエンジン・エディタ・スクリプト API のみを実装し、`<project>/assets` 配下は一切変更していない。既存のパス直書き（`SEED.Audio.Play("assets://...")`）を辞書キーへ移行する作業が残っている。

## ランタイム / スクリプト API

- [ ] **`SEED.Draw`（2D プリミティブ）の未検証項目** — 2026-09-07。GPU の実描画（位置・重なり・アンチエイリアス、3D キャンバス上の深度）は目視未確認。同一 layer の並びは「スプライト → プリミティブ → テキスト」固定（2026-09-08 に 3 種の描画順を統合したので、テキストを覆いたいときはプリミティブの `layer` をテキストより大きくすればよい）。`Arc` の Fill=リング / Outline=線 の意味は直感に反する可能性あり（`Ring` あり）。フェザー 1px 固定なので 3D キャンバス上では遠いと太く見える（解析 SDF 化は図形別シェーダが必要）。関連: `runtime/src/engine/core/renderer/primitive2d/`。
  → 2026-09-28 の W2-8: 2D キャンバスでは `DrawStyle.Crisp`（見た目の拡張）でフェザーを画面の 1 画素にできる（dp のキャンバスで縁がにじまない）。GPU の実描画はグラフの見本で PC の目視を行った（3D キャンバス上は未確認のまま）。
  拡張なしの従来の図形の三角形分割は重い（365 点の折れ線で約 16,000 枚。最適化なしの build で 1 フレーム約 13 ms）。軽い分割（`stroke_polyline_lean`・`fill_convex`）は拡張つきの図形だけで、従来の図形へ広げるかは見た目の差（つなぎの形）を確かめてから決める。

- [ ] **`SEED.Draw3D` の未検証項目** — 2026-09-07。リボンの押し出し向き・線幅の実測、半透明の上／2D UI の下に入るか、Play 中のビューポート矩形と uniform の一致。折れ線のジョイント処理なし・アンチエイリアスなし（仕様として docs 記載）。関連: `renderer/primitive3d/`。

- [ ] **プリミティブ描画キューが描画されないフレームで溜まる** — 最小化中などは `take_commands` が呼ばれず上限まで溜まって警告が出る（メモリは有界）。2D/3D 共通。

- [ ] **実行時に生成・付け替えしたアクタの物理コライダーが物理スレッドに反映されない** — 既存 Instantiate と同じ制約。`Instantiate(path, parent)` / `SetParent` も同様。関連: `app/script_scene_ops.rs`、docs/scripting_api.md の注記。

- [ ] **`SEED.Vector2` / `SEED.Vector3` の `[SerializeField]` はインスペクタで編集できない** — 2026-09-07 に確認。`ScriptInspectorBuilder.BuildValueRow` は float/int/bool/string と参照型だけを扱い、それ以外は読み取り専用行になる（`[Serializable]` が無いので `Children` 展開にも乗らない）。色や座標を Vector3/Vector2 で公開している既存スクリプトは値を変えられない。float 2〜3 本に割るか、インスペクタ側に Vector 行を足すかの判断が要る。関連: `editor/src/Scripting/ScriptInspectorBuilder.cs`、`scripts/FishRadar.cs`、`scripts/FishingFight.cs`。

- [ ] **`GameObject.Parent` が O(N)** — DFS 走査で親を探す実装。毎フレーム大量に呼ぶ用途には向かない。

- [ ] **ホットリロードで `OnDestroy` が呼ばれる保証が未確認** — 2026-09-09（2026-09-07 の「プールが二重生成される」から残った部分）。二重生成そのものは解消済み: エディタが既定で Play 中のホットリロードを保留するようになり（`docs/editor_auto_reload.md`）、あわせて `OnStart` で `Instantiate` していた箇所を `SpawnOnce.GetOrInstantiate` へ寄せた（FishingController / PauseMenu / ResultPanel / FightEvalBanner / HitBanner / FishingFight）。**未確認のまま残るのは「作り直し時に旧インスタンスの `OnDestroy` が呼ばれるか」**で、`OnDestroy` で解除しているイベント購読・静的登録が残留しないかは実機で見ていない。関連: `runtime/src/engine/core/scripting/mod.rs`、`<project>/assets/common/scripts/UI/SpawnOnce.cs`。

- [ ] **`OnStart` 内 `Instantiate` の成否が未検証** — FishingFight のビートアイコンプールが初例。失敗するとリトライせず無効ハンドルが残る。関連: `<project>/assets/mainGame/scripts/FishingFight.cs::EnsureIconPool`。

- [ ] **docs の `Mathf.Clamp01(v)` が実装と食い違う** — 2026-09-07。実装は `Clamp01(ref float)`（void）で、値を返すのは `Clamped01(v)`。docs 4 章の記述を実装に合わせるか、値返し版を `Clamp01` として追加するかの判断が要る。関連: `docs/scripting_api.md` 4 章、`scripting/src/Api/Mathf.cs`。

- [ ] **ScriptEvent 内のアクタ名がアクタのリネームに追従しない** — 2026-09-07。結線 JSON に埋まった `actor` は `rename_refs.rs` の値ゲート（フィールド値そのものが旧名）に当たらない。構造体リスト内の参照メンバと同じ既存制限。対応するなら CLR に型タグ問い合わせ FFI を 1 本足し、`scriptevent` フィールドは JSON をパースして書き換える。関連: `runtime/src/engine/core/app_base/app/rename_refs.rs`、`scripting/src/Api/ScriptEvent.cs`。

- [ ] **スクリプト遷移時の地形 LOD 事前収束が同期でフェードを止め得る** — 2026-09-07。`install_loaded_scene` は Play 中に `converge_terrain_lod_blocking` を呼ぶ（無いと遷移後に長時間の低 fps）。地形規模によってはフェード中に一瞬固まる。気になれば遷移時のみ非同期収束に切り替える。関連: `app/app_init.rs::install_loaded_scene`、`app/script_scene_ops.rs`。

- [ ] **LOAD_SCENE（常駐 Play プロセス再利用）で `pointer.reset()` が呼ばれない** — 2026-09-07。旧シーンのホバー/押下エンティティを持ち越す可能性。スクリプト遷移経路と同じ理由でリセットすべきに見えるが、挙動維持のため `SceneInstallOptions.reset_pointer=false` のまま。関連: `app/ipc_handler.rs` の LOAD_SCENE。

- [ ] **プロローグ会話システムの実機未検証項目** — 2026-09-07。文字送り・送りマーク点滅・カメラ補間の見た目、日本語＋空白＋角括弧を含むフォントパス（ゆずポップ Regular）の実読み込み、CamTarget_* の高さ（目線位置は推定値）、CamTarget_Owner が Hut に埋まる可能性、Text の自動折り返し無し（`
` 手動改行）。関連: `<project>/assets/prologue/scripts/Dialogue/`、`proLogue.scene`。

- [x] **OBJ ローダーが `assets://` 仮想パスを解決しない（派生キャッシュが無いと必ず失敗する）**（2026-09-13 対応: obj_loader を asset_fs 経由のメモリ解析にし、MTL・テクスチャも仮想パスで解決。実データで確認）
  — 2026-09-13（モデル非同期ロードの実装中に発覚）。`obj_loader::load` は受け取ったパスを
  そのまま `tobj::load_obj` へ渡すため、`assets://mainGame/models/waterEffect/WaterColmn.obj`
  のような仮想パスは `Parse error: open file failed` になる。glTF 経路は
  `asset_cache::try_load_model` → `resolve_src` でキャッシュヒット時だけ救われていたので
  見えていなかっただけで、**キャッシュが無い環境（新規プロジェクト・別マシン・PAK 配布）では
  OBJ モデルが必ず読めない**。実測: わらしべフィッシングを D:\SEED_projects\WarashibeFishing で
  起動すると `WaterColmn.obj` / `Ripple.obj`（`mainGame/actors/Effects/WaterColumn.actor` が参照）が
  両方失敗する。直し方は `obj_loader::load` の入口で `asset_fs::resolve` してから渡す
  （テクスチャの `base_dir` も解決後のパス基準にする）。PAK 対応まで考えるなら
  `asset_fs::read_bytes` でバイト列を取って `tobj::load_obj_buf` を使う必要がある。
  関連: `runtime/src/engine/core/loader/obj_loader.rs`、`runtime/src/engine/asset_fs.rs`。

- [ ] **`Instantiate` の `.actor` JSON 読みはまだメインスレッド同期** — 2026-09-13。
  モデル本体は非同期化したが、`Scene::load_actor_into` が `asset_fs::read_string` で
  `.actor`（数 KB〜数十 KB の JSON）を読む部分は同期のまま。遅いドライブではサイズより
  シーク待ちが効くので 1 件 10ms 級になり得る。実害が小さいのは、プリフェッチ走査が
  同じ `.actor` をワーカー側で先に読んでいて OS のページキャッシュが温まっているため
  （`streaming.prefetch=false` では素で効く）。直すなら、プリフェッチ走査で読んだ
  JSON テキストを小さな RAM キャッシュへ残し、`load_actor_into` がそこを先に引く。
  関連: `runtime/src/engine/core/app_base/scene.rs::load_actor_into`、
  `runtime/src/engine/core/loader/async_loader.rs::scan_prefab_and_enqueue`。

- [ ] **モデル非同期ロードの「保留 → GPU 反映」経路が実機未検証** — 2026-09-13。
  `app/model_streaming.rs::install_streamed_model`（ワーカー完成 → GPU アップロード →
  `ModelComponent` へ差し込み）は、プリフェッチが効いている通常プレイでは通らない
  （`request` が即 `Ready` を返すため）。検証には「先読みが間に合わないうちに魚が出る」状況が要り、
  それには実際に竿を振る操作（`SEED.Input.MouseDelta` を使うキャスト・ジェスチャ）が必要で、
  `game_input_*`（MCP）や PostMessage では再現できなかった（チュートリアルの関門で止まる）。
  単体テスト（重複排除・LRU・アップロード予算）と、先読み経路（`RequestState::Ready`）は検証済み。
  実機で確認するときは `SEED_STREAMING=noprefetch` を付けて起動し、
  ログに `[SEED stream] メイン適用: N 件 / x.xx ms` が出ること・魚が数フレーム遅れて現れることを見る。
  関連: `docs/model_streaming.md`、`runtime/src/engine/core/app_base/app/model_streaming.rs`。

- [ ] **Animator のクリップ設定 `loop_mode` がキーフレーム .anim では無視される** — 2026-09-09。`animation_ops.rs` のキーフレーム経路は `.anim` ファイルの `loop_mode` を使い、Animator 側の設定はモデル内蔵アニメ（`normalize_model_time`）でしか参照されない。インスペクタで「ループ」にしても .anim が once なら 1 回で止まる（TutorialMouse で発生）。Animator 側の設定を上書きとして優先させるのが自然。関連: `runtime/src/engine/core/app_base/app/animation_ops.rs:109,172`。

## ゲーム（わらしべフィッシング）

- [ ] **ルートキャンバスの `auto_scale` が実質無効（設計解像度が効かない）** — 2026-09-07 に HIT 帯演出の位置ズレを追って判明。ビューポート所属のルートキャンバス（`CanvasViewportRef::Camera / MainCamera`）は `build_root_canvas_auto_size_map` により width/height が**実描画解像度で上書き**される。その後 `auto_scale_factor = eff_viewport / my_eff_w` を計算するが、`my_eff_w` は上書き後（＝ eff_viewport 自身）なので**係数は常に 1.0** になる。結果として `CanvasComponent.width/height`（例: 1920x1080）は完全に無視され、子の座標・アンカー基準・スプライト寸法はすべて**ウィンドウの実ピクセル**になる。UI をウィンドウサイズ非依存に組めないうえ、`auto_scale` チェックが何もしない。直すなら基準サイズ（`cc.width/height`）と実解像度を分け、アンカー基準サイズは設計解像度・スケール係数は 実解像度/設計解像度 とする。影響範囲が広い（既存 UI の座標が全部変わる）ため要判断。関連: `runtime/src/engine/core/app_base/app/canvas_collect.rs`（`build_root_canvas_auto_size_map` / `auto_scale_factor` / `my_eff_w`）。
  **2026-09-10 追記（回避策あり・項目は残す）**: プロジェクト設定 `render_resolution_mode: "fixed"` を使うと、描画解像度が `window_width × window_height` に固定されるため `eff_viewport` と `my_eff_w` が常に一致し、**ウィンドウをどうリサイズしても UI の見た目が変わらなくなる**（実用上はこれで回避できる）。ただし根本原因（設計解像度 `cc.width/height` が無視されること）は直っていないので、`CanvasComponent` の設計解像度と `window_width/height` を食い違わせると相変わらず無視される。正典: `docs/rendering_flow.md` §2.21 / §4.5。

- [ ] **Edit の 2D シーンビュー（EDIT_VIEW:2d）はカメラのパン・ズームを IPC から動かせない** — 2026-09-07。`canvas_cameras[0]`（pan_x/pan_y/ortho_half_h）はマウス入力（MMB ドラッグ・ホイール）でしか変化せず、初期状態は「キャンバス左上がビュー中央・1 キャンバス px = 1 画面 px」。ヘッドレス（MCP）ではマウスを送れないため、キャンバス全体を映した設計ビューのスクリーンショットが撮れない（今回は Play + `seed_screenshot(game)` で代用した）。`CAM2D_SET:{pan_x},{pan_y},{half_h}` のような IPC か「選択物にフィット」コマンドがあると AI からの目視確認が回る。関連: `runtime/src/engine/core/app_base/app/frame_renderer.rs`（use_ortho_2d_camera 付近）、`app/ipc_handler.rs`（EDIT_VIEW）。

- [ ] **HIT 演出の帯の角度を変えるにはクリップの作り直しが必要** — 位置キーは θ=−12° を展開した実座標。回転トラックだけ変えても位置は追従しない。2026-09-07 にアイテムごとの 4 クリップへ分割（位置キーが各アイテムのローカル座標のため）。同日、4 アイテムのアンカーを画面中心 (0.5,0.5) へ統一し、静止位置・入退場のキーを再生成した。関連: `<project>/assets/mainGame/animations/hit_banner_band_top.anim` ほか 3 本、`scripts/HitBanner.cs`。

- [ ] **糸ゲージ・レーダーの見た目が未目視** — 2026-09-07。HIT 帯演出（帯 2 本＋文字 2 つの同期・入退場）は 2026-09-07 にヘッドレス Play で目視確認・修正済み。残るのは、`Draw.Rect` の回転小片で描く糸ゲージ（旧 48 スプライトとの一致）、`Draw.RegularPolygon` の三角マーカー、プリミティブ化したレーダー背景円・中心点の大きさと色で、どちらも「ウキを投げて魚と勝負している間」しか描かれないため待機状態のスクリーンショットでは確認できない（実プレイが要る）。関連: `scripts/HitBanner.cs`、`scripts/FishingFight.cs`、`scripts/FishRadar.cs`。

- [ ] **未参照になったテクスチャ** — 2026-09-07 のレーダーのプリミティブ化で `radar_bg.png` / `radar_dot.png` がどのアクタからも参照されなくなった。他で使わないなら削除してよい。関連: `<project>/assets/mainGame/textures/ui/`。

- [ ] **レーダーの点・ビートアイコンの見た目確認** — 2026-09-07。`Draw.Circle` の点の位置・サイズ（`radarSpace` 相対のスケール一致）、`BeatIcon.actor` プールの出現位置は未目視。関連: `scripts/FishRadar.cs`、`scripts/FishingFight.cs`。

- [ ] **`cargo test` の並列実行で落ちる順序依存テストが 2 件ある** — 2026-09-10 に描画解像度モードの作業中に発見（この作業とは無関係で、既存の問題）。
  `engine::plugin::host::tests::set_save_int_writes_flag_and_keeps_other_keys` と
  `engine::core::font::inline::icon_set::tests::poll_*` が、**全体実行では落ちるのに単体実行では通る**。
  新規追加テストを `--skip` して変更前と同じテスト集合にしても落ちるので、今回の変更が原因ではないことは確認済み
  （そのときは icon_set 側も一緒に落ちた＝どちらが落ちるかは実行ごとに変わる）。
  前者の機序: `save::SAVE_STORE` は `OnceLock` で、**初回アクセス時に解決した保存先パスを `SaveStore.path` に焼き込む**
  （`save/store.rs::flush` は `self.path` へ書く）。テストは `SEED_SAVE_DIR` を設定してから `save()` するが、
  他のテストが先にストアを初期化していると `flush()` は旧パスへ書き、直後の `resolve_save_path()`（毎回環境から再解決）は
  テスト用の一時ディレクトリを返すため「書いたはずのファイルが無い」になる。
  直すなら「テスト用にストアをリセットする API を足す」か「`SaveStore` が毎回 `resolve_save_path()` を引き直す」かの判断が要る。
  関連: `runtime/src/engine/plugin/host.rs:133`、`runtime/src/engine/core/save/mod.rs:41`、`runtime/src/engine/core/save/store.rs:269`。

## フレームレート上限・垂直同期・fps 表示（2026-09-10 実装時の残件）

- [ ] **設定の反映に再起動が要る** — 2026-09-10。`target_fps` / `vsync` は `App::handle_resumed` で
  起動時に 1 回だけ読む。エディタのプロジェクト設定で変えても、実行中の Play には反映されない。
  `target_fps` は `App` のフィールドを差し替えるだけなので IPC で live 反映できる余地がある
  （`vsync` はスワップチェーン再構成が要るので別問題）。関連:
  `runtime/src/engine/core/app_base/app/app_init.rs`、`app/frame_pacing.rs`、`renderer/present_mode.rs`。

- [ ] **fps 表示に即時テキスト描画 API が無い** — 2026-09-10。`SEED.Draw` は矩形・円などの図形だけで
  文字を出せないため、`DebugCommands` の fps 表示は `SEED.Text` コンポーネントへ書き込む方式にした。
  シーンへの結線を不要にするため、初回 F3 で `mainGame/actors/UI/FpsLabel.actor`（自前のキャンバス＋
  左上アンカーの Text）を `SpawnOnce.GetOrInstantiate` で自動生成している。
  デバッグ HUD 全般のために `Draw.Text`（スクリーン座標の即時テキスト）があれば、
  この専用アクタ自体が不要になる。関連: `scripting/src/Api/Draw.cs`、
  `<project>/assets/mainGame/scripts/DebugTools/DebugCommands.cs`、
  `<project>/assets/mainGame/actors/UI/FpsLabel.actor`。

- [ ] **フレーム制限の実機検証が未実施** — 2026-09-10。純関数（待ち時間計算・present mode 選択）は
  単体テスト済みだが、実際に 60fps へ張り付くか・`timeBeginPeriod(1)` でスリープ粒度が
  期待どおりになるか・`vsync: on` でティアリングが消えるかは未確認。関連: `app/frame_pacing.rs`。

## 未コミットの他セッション差分（要確認）

- [ ] **`app_init.rs` / `ipc_handler.rs` / `script_scene_ops.rs` / `play_mode_ops.rs` に別セッションの未コミット変更** — 2026-09-07 時点。Play 開始時のシーン登録表再読込など。作業ツリーに残っているので、そのセッション側でコミットするか破棄するか判断する。

## エディタ MCP / ヘッドレス（2026-09-07 実装の残件）

- [ ] **`.mcp.json` の起動引数が JSON のエスケープで壊れていて、MCP サーバーが起動しない** — 2026-09-24（.NET 10 移行の作業中に発見。未修正）。`"args": ["/c", "editor\\SeedMcpServer\run-mcp.cmd"]` の `\r` が JSON では復帰文字（CR）になり、`cmd /c` には `editor\SeedMcpServer<CR>un-mcp.cmd` が渡って「内部コマンドまたは外部コマンド…として認識されていません」で終了コード 1 になる（Python の `json.load` ＋ `subprocess` で再現済み）。同日の Claude Code セッションでは seed-editor が `CONNECTION_CLOSED` で接続できなかった。直すなら `"editor\\SeedMcpServer\\run-mcp.cmd"`。docs/editor_mcp.md §3 の例 `"editor\SeedMcpServer\run-mcp.cmd"` も JSON として不正（`\S`）なので合わせて直す。関連: `.mcp.json`、`editor/SeedMcpServer/run-mcp.cmd`、docs/editor_mcp.md §2〜3。

- [ ] **2026-09-08 追加の MCP ツールの残件** — `seed_save_get/set/delete/flush`（IPC `SAVE_DATA:`）、`seed_find_actor`、`seed_input` をヘッドレスで実走行して確認済み。残る制限:
  - `save_data` に「全キー一覧」の op が無い（キー名を知っている前提）。`SaveStore` に列挙 API を足せば `op:"list"` を追加できる。
  - `seed_find_actor` の名前解決は**エディタ側の Hierarchy ノードモデル**で行う（ランタイムの `actor_ref_path.rs` とは別実装）。検索用途では先頭セグメントをシーン全体 DFS へ緩和しているため、参照フィールドの保存書式（ルート起点の厳密なパス）とは規則が完全一致しない。
  - `seed_input` は MCP サーバー側で `game_input_*` を順に撃つだけなので、待ち時間は MCP プロセスのタイマー精度に依存する（拍に合わせる用途は `game_input_sequence` を使うこと）。`seed_batch` からは呼べない（元のコマンド名を並べる）。
  - `save_data` は変更系として扱う（読み取り専用インスタンスでは拒否）。`op:"get"` だけは観測系なので、細分化するなら `AiOperationPolicy` 側でコマンド名を分ける必要がある。

- [ ] **`MessageBox.Show` の大半がまだ `EditorDialogs.Show` を通っていない** — 2026-09-07。ヘッドレスでモーダルが出ると UI スレッドが固まり MCP 呼び出しが全滅するため、AI 経路（Play / シーンロード / シーン保存 / 自動リロード / スクリプトコンパイル / LOAD_ERROR）だけを差し替えた。インスペクタ・地形・プロジェクト設定・スプライトリグ等の 30 箇所以上は素の `MessageBox.Show` のまま。順次 `SEEDEditor.Headless.EditorDialogs.Show` へ寄せる。関連: `editor/src/Headless/EditorDialogs.cs`。

- [ ] **ヘッドレスでの `seed_screenshot(target:"editor")` は真っ黒になる** — 2026-09-07。エディタ UI 全体は画面 DC からしか撮れず、ウィンドウが画面外にあると撮れない。WPF 側を `RenderTargetBitmap` でレンダリングして返す経路を作れば解決できるが、埋め込みランタイム部分は空になる（GPU 子ウィンドウは WPF のビジュアルツリーに無い）。

- [x] **ヘッドレス一連の実機 E2E 実施済み** — 2026-09-07。`seed_launch(headless) → seed_screenshot(gpu) → seed_play → seed_screenshot → seed_play(stop) → seed_shutdown` を MCP サーバー経由で実走行し、画面に何も出ないまま PNG（1068x568・実内容あり）を取得、プロセスも残らないことを確認した。

- [ ] **`seed_launch` が起動したエディタは MCP サーバー終了後も残る** — 2026-09-07。ジョブオブジェクトで括っていないため、`seed_shutdown` を忘れるとプロセスが残る。必要なら Job Object + `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` を検討。関連: `editor/SeedMcpServer/Launcher.cs`。

## 2D パーティクル（2026-09-08 実装の残件）

`ParticleEmitter` を 2D キャンバスアクターへ付けられるようにし、UI の統合描画列
（`renderer/ui_draw_order.rs` の `UiDrawKind::Particle`）へ `layer` 順で差し込むようにした。
仕様は docs/scripting_api.md の「2D キャンバス（UI）のパーティクル」が正典。以下は残件。

- [ ] **2D 由来の孤児粒子はレイヤー順を失い、SS 合成時にしか描かれない** — 2026-09-08。
  エミッタが消えた後も寿命ぶん残る粒子群（孤児）はシーン走査に現れないため統合描画列に載せられず、
  `ParticleSystem::draw_orphans_2d` で「2D 前面ゾーンの末尾」にまとめて描いている。
  さらにその呼び出しは `scene_canvas_ss`（Play / Edit View3D の SS 合成）のオーバーレイパスにしか
  入れていないため、2D シーンビュー・アクター編集タブでは 2D の孤児粒子が描かれない。
  直すなら孤児にゾーン／レイヤー／最終行列を保持させ、統合描画列へ合流させる。
  関連: `renderer/particle_system.rs::draw_orphans_2d`、`app/frame_renderer.rs`（オーバーレイパス）。

- [ ] **フォルダノードに付けた 2D エミッタは描かれない** — 2026-09-08。`canvas_collect.rs` の
  フォルダ透過分岐（`canvas_node_is_transparent`）では粒子アイテムを積んでいない。
  スプライト／テキストも同じ規約なので実害は小さいが、エミッタだけは「Transform を持たない
  フォルダ」に置きたくなる場面があり得る。関連: `app/canvas_collect.rs::collect_sprite_items`。

- [ ] **2D パーティクルは GPU ピッキングの対象外** — 2026-09-08。`collect_canvas_id_items` に
  パーティクルの ID アイテムを積んでいないため、シーンビューで粒子をクリックしてもエミッタは選べない
  （エミッタアクター自体は他のコンポーネントか階層から選ぶ）。関連: `app/canvas_collect.rs`。

- [ ] **2D エミッタの `world_mat` 書き戻しはフレーム後半の暗黙依存** — 2026-09-08。
  2D は `sim_space=Local` 固定で compute が `world_mat` を見ないことを根拠に、
  キャンバス走査の後で `upload_2d_world_mats` が uniform の先頭 64 バイトだけを差し替えている。
  将来 2D でも World シムを許すなら、この前提が崩れる（先に行列を確定させる設計変更が要る）。
  実装時に「渡す行列は既に GPU 列優先」を取り違えて二重転置し、全粒子がキャンバス原点へ集まる
  不具合を出した。単体テストで守れていないので、型（newtype）で区別する余地がある。
  関連: `renderer/particle_system.rs::upload_2d_world_mats`。

## 2D キャンバス（2026-09-07 の入れ子アンカー修正の残件）

- [ ] **CanvasComponent を持たないノードの pivot は「基準サイズ 1x1」で解決される** — 2026-09-07。`collect_sprite_items` / `collect_canvas_rects` / `pick_2d` は、自ノードに CanvasComponent が無いとき `to_mat4_sized(1.0, 1.0)` で子への座標系原点（`self_world_rs`）を作る。このため pivot が `pivot × 1px` の平行移動として残り、Sprite（例: pivot=(0.5,0.5)）の子は**最大 1px** 親の中心からずれる。目視できない量だが規約としては誤り。「Canvas 領域を持たないノードの pivot は子の座標系に影響しない（＝ 0 基準）」へ寄せるのが筋。影響が全入れ子 2D に及ぶため単独タスクで実施する。関連: `runtime/src/engine/core/app_base/app/canvas_collect.rs`（`my_eff_w/h` の `.unwrap_or((1.0, 1.0))`）、`physics2d_ops.rs`（`canvas_eff_w/h`）。

- [x] **2D ノードのレイアウト計算が 5 か所に重複コピーされている** — 2026-09-07。
  → **2026-09-27 に W2-1a で完了**。`runtime/src/engine/core/canvas_layout/`（純関数 `placement::resolve`・木を 1 回たどる
  `CanvasLayoutPass`・表 `CanvasLayoutTable`）へ一本化し、5 か所は表を読むだけにした（描画は `drawn_nodes`、枠・ID 描画は
  `is_drawn`、当たり判定は `is_pickable`、2D 物理は `physics_contexts_from_table`）。旧 5 か所をテスト用に写した
  `app/canvas_layout_equivalence/legacy.rs` とランダムな木 3,000 個（12 万ノード）で**ビット単位の一致**を確認。
  旧実装どうしの食い違いは直さずに残した（下の W2 節「W2-1a で見つけた旧実装の食い違い」）。3D ワールドキャンバスの子の走査
  （`walk_3d_canvas_children_id`・`collect_3d_canvas_child_outlines`）は別の 2 か所として残っている（同じ W2 節）。以下は当時の記述。`collect_sprite_items` / `collect_canvas_rects` / `collect_canvas_id_items` / `pick_2d::walk_pick_candidates_2d` / `physics2d_ops::collect_actor2d_contexts` が、root_auto 上書き → eff_viewport → アンカー → eff_ct → size_scale → self_world_rs → 子への継承、という同じ 60〜80 行をそれぞれ持っている。今回の「子のアンカー基準サイズ」バグは、この重複のうち 1 か所（`physics2d_ops` のギズモ用アンカー）だけ挙動が違ったせいで「ギズモは正しいのに描画がずれる」という形で表面化した。アンカー部分は共通ヘルパー（`node_anchor_offset` / `child_anchor_basis`）へ切り出したが、残りは未統合。`CanvasNodePlacement::resolve()` のような純関数へ一本化したい。関連: 上記 5 ファイル。

- [ ] **入れ子 2D の anchor 仕様がドキュメント化されていない** — 2026-09-07。
  → 2026-09-27（W2-1a）: 規則は docs/canvas_camera_rework.md §6.1 に書いた（コードの正典は `canvas_layout/anchor.rs`）。
  残りはインスペクタのグレーアウト。以下は当時の記述。「anchor は**親の CanvasComponent 領域**に対する比率で、Canvas 領域を持たない親（Sprite など）の子では anchor は効かない（親の原点＝親の position 点が基準）」という規則を docs 側に明記する。エディタのインスペクタでも、親が Canvas 領域を持たないときは anchor 欄をグレーアウトするのが親切。

## MCP 安全機構（2026-09-07 のシーン上書き事故対応の残件）

事故の経緯と入れた対策は **docs/editor_mcp.md の 7 章・8 章**（正典）を参照。
ここには「今回のスコープ外として意図的に残したもの」だけを書く。

- [ ] **`seed_attach` の実機フローが未検証** — 2026-09-07。実装（エディタ側の環境設定チェックボックス＋ポート・トークン表示、MCP 側 `seed_attach`）は入れて単体テストも通したが、**動作中のエディタを使った往復は行っていない**（利用者のエディタが起動中だったため、静的検証のみで完了させた）。次に対話エディタを触るときに「環境設定でオン → 表示された値で `seed_attach` → 変更系ツールが通る → オフに戻すと拒否される」を一度確認すること。

- [ ] **`--ai-token` なしのインスタンスへは `seed_attach` できない** — 2026-09-07。トークンは起動引数からしか設定できないため、利用者が普通に起動したエディタ（トークンなし）は環境設定で「AI 操作を許可」しても `seed_attach` の対象にならない（ブリッジ側のトークン検証は素通しなので、実際には既定ポートへ直接叩けば通ってしまう）。**厳密にやるなら、環境設定でオンにした瞬間にトークンを生成して表示し、以後そのトークンを要求する**（＝ブリッジのトークンを実行時に設定できるようにする）。今回は「既定は読み取り専用」で実害を止めることを優先した。関連: `editor/src/AI/AiOperationPolicy.cs`（`Token` が `Configure` でしか入らない）。

- [ ] **保存の可否をシーンの中身（scene_id / name）でも照合していない** — 2026-09-07。`SAVE_SCENE` はパスの一致だけを見ている。`.scene` のトップレベルには `name` があるので、「ファイルに書かれている name とランタイムのシーン名が食い違うときも拒否する」という二重化ができる。ただし別名保存直後などは正当に食い違うため、規約を決めてからにする。関連: `runtime/src/engine/core/app_base/app/scene_save_ops.rs`。

- [ ] **`.backup` の総量に上限が無い** — 2026-09-07。1 ファイルあたり 10 世代までは切り詰めるが、ファイル数が増えれば `<assets>/.backup/` は際限なく育つ（大きなシーンだと 1 世代で数 MB）。古い世代を日数で掃除する仕組みか、エディタ側に「バックアップを整理」メニューが要る。関連: `runtime/src/engine/core/app_base/safe_write.rs`、`editor/src/Assets/SafeFileWriter.cs`。

- [ ] **終了時の保存失敗で `_pendingClose` が残る（既存の不具合）** — 2026-09-07 に発見、今回は未修正。`OnWindowClosing` の「保存して終了」で保存が失敗すると `OnSaveCompleted` の失敗側が `_pendingClose` をクリアしないため、フラグが立ったままになる。次に別の保存が成功した瞬間にウィンドウが閉じる。今回パス不一致で保存が失敗しうるようになったので、遭遇確率は上がっている。関連: `editor/src/MainWindow.Scene.cs::OnSaveCompleted`。

- [ ] **`.actor` / `.anim` にはシーンロックが無い** — 2026-09-07。ロックは `.scene` だけ。プレハブ（`.actor`）を 2 つのエディタで同時に開くと、後勝ちで上書きされる（バックアップからは戻せる）。`SceneLock` は拡張子を問わない実装なので、アクタータブの開閉に繋げるだけで対応できる。関連: `editor/src/Scene/SceneLock.cs`、`editor/src/MainWindow.FileOps.cs`。

## アニメーションタイムライン（2026-09-07 の複数選択・ナビゲーション実装での残件）

- [ ] **トラック名リストとドープシート行がピクセル単位では揃っていない** — 2026-09-07。左のトラックリスト（`ListBox`）はヘッダー Border の高さ・`ListBoxItem` の実高さがドープシート側の `RulerHeight` / `TrackRowHeight` と一致していない（従来からの状態で、今回は変更していない）。そのため `Shift`+ホイールの縦スクロール同期は「ピクセル量 ÷ 行高 = アイテム数」への換算による**近似**にとどまる。厳密に揃えるには、リスト側を `ScrollViewer.CanContentScroll=False`（ピクセルスクロール）にし、`ItemContainerStyle` で行高を `TrackRowHeight` に固定し、ヘッダー高さをルーラー高に合わせる必要がある。関連: `editor/src/Panels/AnimationTimelinePanel.xaml`、`DopeSheetPanel.xaml.cs::ScrollVerticalBy`。

- [ ] **トラックリストにフォーカスがあるときの `Delete` はキー選択より優先される** — 2026-09-07。`LstTracks` 自身の `KeyDown`（`OnTrackListKeyDown`）が先に走るため、キーを複数選択した状態でもリスト側にフォーカスがあるとトラックが消える。パネル側の `HandleTimelineKey` は「キー選択があればキー削除」を優先する実装なので、両者で判断が違う。リスト側でもキー選択の有無を見るか、リストの `Delete` は削除ボタン/右クリックメニューへ寄せるのが筋。関連: `editor/src/Panels/AnimationTimelinePanel.xaml.cs::OnTrackListKeyDown`。

- [ ] **`duration` を超えるキーの貼り付けは最終フレームへ丸めて重なる** — 2026-09-07。`AnimKeyClipboard.Paste` はクリップ外へキーを作らない方針でフレームをクランプするため、長いキー列をクリップ末尾付近へ貼ると複数キーが最終フレームで潰れて上書きし合う（データは失われる）。「貼り付けで足りない長さを自動的に伸ばすか確認する」ほうが親切。関連: `editor/src/Panels/AnimationTimeline/AnimKeyClipboard.cs`。

## テストの独立性（2026-09-13 に気付いた既存不具合・シャドウ改修とは無関係）

- [ ] **`plugin::host::tests::set_save_int_writes_flag_and_keeps_other_keys` が全体実行だと落ちる**
  — 2026-09-13。単体（`cargo test set_save_int_writes_flag_and_keeps_other_keys`）では通るが、
  `cargo test`（`--test-threads=1` でも同じ）だと `save.json を読めない … (os error 3)` で失敗する。
  セーブ先パスの解決（`save::resolve_save_path` / `SEED_SAVE_DIR`）がプロセスグローバルで、
  他テストが同じ状態を触るか、後片付け（`remove_dir_all`）の順序に依存しているのが原因と思われる。
  並列時は `font::inline::image_meta::tests::missing_path_is_cached_as_failure` も一緒に落ちる
  （こちらもプロセスグローバルのキャッシュ依存）。**シャドウ改修の前から存在する問題**で、
  renderer 側（`cargo test renderer::`）は全て緑。テストごとに一時ディレクトリを作るか、
  グローバル状態を触るテストを 1 本のミューテックスで直列化するのが筋。
  症状として **`cargo test` を回すとリポジトリ内へ `runtime/save/save.json`
  （テストの値 `{"tutorial_done":1,"fish_record_kani":42}`）が生成される**。
  `resolve_save_path()` は毎回 `SEED_SAVE_DIR` を読む実装なので、
  「保存先がプロセスグローバルの環境変数＋グローバルなセーブストア」である以上、
  他テストと実行順によって書き先が既定（cwd 基準の `save/`）へ落ちることがある、
  という筋読み（未確定。直すときは実際の順序を追うこと）。
  `.gitignore` は `projects/*/save/` しか除外していないため、うっかり `git add -A` すると混入する
  （2026-09-13 は手で削除した）。`runtime/save/` を .gitignore へ足すか、テスト側でパスを固定するのが筋。
  関連: `runtime/src/engine/plugin/host.rs:164`、`runtime/src/engine/core/font/inline/image_meta.rs`、
  `runtime/src/engine/core/save/path.rs`。
- [ ] **ほかにもプロセスグローバルな状態に依存して並列実行でたまに落ちるテストがある** — 2026-09-25（Android 段階B の `cargo test` で気付いた。
  どちらも単体では毎回通る・段階B では触っていないファイル）。
  - `engine::core::scripting::debug_command::tests::pushes_and_peeks_in_order` … `PENDING`（SCRIPT_DEBUG の待ち行列）を
    `drops_oldest_when_full`・`ipc::tests::parses_script_debug_commands` と共有している。
  - `engine::core::font::inline::icon_set::tests::poll_keeps_previous_content_on_parse_failure` … .icons のキャッシュ（`invalidate_all`）を
    他の icon_set のテストと共有している。
  - `engine::core::font::inline::image_meta::tests::poll_keeps_previous_aspect_on_decode_failure` … 画像のアスペクト比のキャッシュ
    （`invalidate_all`）を他の image_meta のテストと共有している（2026-09-28 の W2-2 の全体実行で `set_save_int_writes_flag_and_keeps_other_keys`
    と一緒に落ちた。2 つとも単体では通る。W2-2 は触っていないファイル）。
  上の項目と同じく、グローバル状態を触るテストをミューテックスで直列化するか、状態をテストごとに持てる形にする。

## deferred の幾何法線（2026-09-13 の遠景ドットノイズ対策の残件）

- [ ] **幾何法線 Ng を G-Buffer へ焼けば根治できる** — 2026-09-13。現在 Ng は深度バッファの
  画面微分で復元しているため、①深度不連続（草・地形 → RT1.w の authored フラグで回避）
  ②f32 桁落ち（カメラ相対微分で解決済み）③**深度の量子化**（遠景 → 今回、誤差を見積もって
  N へ倒す対策を入れた）という 3 つの誤差源を抱える。`surface.wgsl` の Surface コメントにある
  案 (a)「octahedral 8:8 で G-Buffer へ 1 チャンネル追加」を採れば 3 つとも消え、
  幾何ゲートを全距離で正しく効かせられる。コストは G-Buffer 帯域 +2byte/px と、
  全 G-Buffer 書き込み側（static / skinned / terrain / grass / シェーディングアセット）の改修。
  関連: `renderer/shaders/deferred_lighting.wgsl`（`DEFERRED_NG_ERR_TRUST_LO/HI`）、
  `gbuffer_write.wgsl`、`surface.wgsl`。

- [ ] **遠景（誤差 0.06 超＝典型的なカメラで 30m 以上）のメッシュでは幾何ゲートが効かない**
  — 2026-09-13 の対策の副作用（意図的）。「法線マップが表と言い張る薄い面の裏面光漏れ」が
  その距離では防げない。上の G-Buffer 化で解消する。近距離（〜10m）は従来どおり完全に効く。

- [ ] **1 画素幅の細い物体では Ng が依然として復元できない** — 2026-09-13。シルエット境界対策
  （深度差の小さい側の片側差分）は「左右・上下のどちらか一方が同一サーフェス」であることが前提。
  1px 幅の枝・柵のように両側とも別サーフェスの画素は正しく復元できず、輪郭に 1px の縁が残りうる
  （ハードウェア微分でも同じ）。上の G-Buffer 化で根治する。

- [ ] **フォワード経路のシルエットは未対策** — 2026-09-13。`surface_gather.wgsl::geometric_normal`
  は補間ワールド座標のハードウェア微分のままで、半透明フォワードのシルエットでは同じ 1px の
  黒縁が理屈の上では出る（頂点補間なので深度バッファほど壊れないうえ、半透明は面積が小さく
  実機では未確認）。直すなら頂点法線を varying で渡して幾何法線の代わりに使うのが早い。

## シャドウマップ品質（2026-09-13 の改修時の残件）

正典: [docs/shadow_mapping.md](shadow_mapping.md)。今回入れたのは
「影の最大距離 + 分割係数 + 法線オフセット + カスケード別深度バイアス + 回転 Vogel ディスク PCF」と、
`project_settings.json` の `shadow` ブロックによるデータ化まで。

- [ ] **`shadow.distance` より遠くは影が完全に消える（フェードが無い）** — 2026-09-13。カスケード外は
  可視率 1.0 を返すため、影距離の縁で影がぷつりと切れる。既定 150m では手前に寄った絵でしか
  境界が画面に入らないので今回は許容したが、広い屋外を俯瞰するカメラだと切れ目が見える。
  最終カスケードの外縁で影を 1.0 へ線形にフェードさせる（＋境界のスムーズブレンド）のが定石。
  関連: `runtime/src/engine/core/renderer/shaders/shadow.wgsl::sample_shadow_dir`（UV 範囲外の早期 return）。

- [ ] **カスケード境界のスムーズブレンドが無い** — 2026-09-13（R2 からの積み残し）。カスケードを
  またぐと影の解像度と PCF の実効半径が段階的に変わるため、境界線がうっすら見えることがある。
  2 カスケードぶんサンプルして `view_z` で線形補間するのが定石（コストは境界帯のみ）。

- [ ] **シャドウ解像度の変更はランタイム再起動が必要** — 2026-09-13。`shadow.resolution` は深度
  テクスチャと group4 の複合 BindGroup（`LightBuffer::new`）に結び付いているため、実行中に変えられない。
  エディタ UI にもその旨を表示している。ライブ変更したいなら ShadowResources と LightBuffer の
  複合 BG を作り直す経路が要る。関連: `renderer/shadow.rs::ShadowResources::new`、`renderer/lighting.rs`。

- [ ] **シャドウ品質はプロジェクト設定のみ（シーン設定に無い）** — 2026-09-13。影の**方式**
  （shadowmap / rt）はシーン設定（`.scene` の `settings.rendering.features`）で切り替わるのに、
  品質パラメータは `project_settings.json` だけにある。シーンごとに詰めたくなったら
  `SceneSettingsData.rendering` へ同じブロックを足し、`apply_scene_settings` で
  `set_shadow_quality` を呼ぶ（解像度だけは起動時固定なので除外するか、再生成経路が要る）。

- [ ] **PCF の回転ディザが拡大表示で粒状に見える場合がある** — 2026-09-13。IGN はピクセル座標だけの
  関数なのでフレーム間ではちらつかないが、影の輪郭を画面上で大きく引き伸ばすと 1px 単位のディザが
  見える。気になるなら `pcf_taps` を増やす（16 まで）か `pcf_radius_texels` を下げる。
  根治するならタップ後に 3x3 の空間フィルタを掛けるか、TAA 側で均す。

## フレーム性能（2026-09-07 の統合バッチ／スクリプト／RT 最適化での残件）

- [ ] **統合バッチの部分書き込み（変更行だけの `write_buffer`）は未実装** — 2026-09-07。`InstancedModelBatch::update` は可視インスタンスを (LOD, メッシュノード) ごとの連続バッファへ詰め直し、`queue.write_buffer` で**全行**を書く。1 体だけ動いたフレームでも全行アップロードになる。行の位置（compact index）は LOD バケット割り当てが変わらない限り安定なので、「前フレームの compact 列と一致する区間はスキップし、変わった行の範囲だけオフセット付きで書く」ことは原理的に可能。ただし ①バケットが 1 つでも動くと全行がずれる ②`write_buffer` はサイズより呼び出し回数のほうが支配的になりやすい、の 2 点があるため、**先に新設のサブスコープ（`描画/統合バッチ更新/バッチ更新`）で「バッチ更新」の実測を取ってから**着手すること。関連: `runtime/src/engine/core/renderer/gpu_resources.rs::InstancedModelBatch::update`。

- [ ] **インスタンス単位の関与カリング（視錐台 ∪ 影 ∪ RT）はラスタ側へ未適用** — 2026-09-07。`renderer/render_relevance.rs` の `RelevanceVolume` は用意して RT（TLAS/BLAS/スキン変形）にだけ適用した。ラスタの統合バッチへ適用すると「関与しないインスタンスは行列再計算もアップロードもしない」ができるが、**過去に同種の視錐台カリングが誤棄却（画面端ポッピング・チャンク消え）で撤去された経緯**があるため、合併領域の妥当性を実機で確認してからにすること。なお釣りシーンの主負荷である魚は 1 バッチを共有して全員が動くため、バッチ単位のゲートでは効かない（インスタンス単位でしか効果が出ない）点に注意。関連: `runtime/src/engine/core/app_base/app/merge_batch_gate.rs`。

- [ ] **`rt_enumerate` / `rt_enumerate_skinned` が呼び出しごとに作業 Vec / BTreeMap を確保する** — 2026-09-07。`rt_enumerate` は `inst_lod`（インスタンス数ぶんの Vec）を毎回作り、1 フレームにキャスタ数 × 3 回呼ばれる。`rt_enumerate_skinned` はさらに `BTreeMap` と逆引き Vec を作る。バッチ側にスクラッチを持たせれば消せる（`update()` で同じ対処を入れた）。関連: `runtime/src/engine/core/renderer/gpu_resources.rs`。

- [ ] **`ScriptSystem` が毎フェーズ `Vec<ScriptCall>` と `Arc::clone` を作る** — 2026-09-07。1 フレーム 7 フェーズ × スクリプト数ぶんの `Arc` 増減と Vec 確保が発生する。収集バッファをフレーム間で使い回すか、`Arc` ではなくホストの生ポインタを持てば消せる。関連: `runtime/src/engine/systems/script_system.rs`。

- [ ] **FFI アクセサはコンポーネント名・フィールド名の文字列 match でディスパッチしている** — 2026-09-07。`read_floats` / `write_floats` は呼び出しのたびに `&str` の多段 match を通る。呼び出し回数が多い経路（Transform の位置・回転）では、C# 側で ID を持たせて整数ディスパッチにすると削れる。今回は「Actor ツリーの全探索」（O(アクタ数)/呼び出し）のほうが桁違いに重かったのでそちらを索引化した。関連: `runtime/src/engine/core/scripting/host_api.rs`。

- [ ] **`actor_virtual_pos` は計算されているが誰も使っていない** — 2026-09-07。`frame_renderer.rs` のエディタ状態収集で `self.actor_virtual_world_pos()`（Actor ツリー走査）を呼んで束縛しているが、参照箇所が 1 つも無い（デッドコード）。消してよいはずだが、今回のタスク範囲外なので触っていない。関連: `runtime/src/engine/core/app_base/app/frame_renderer.rs`。

- [ ] **エディタ状態収集の残り（Play 中も走る DFS 群）** — 2026-09-07。ギズモ位置・ギズモ軸基底は `in_editor` で囲って Play 中は計算しないようにしたが、同区間にはピック ID レイアウト算出・各種シーンギズモ収集など Actor ツリー走査を伴う処理がまだ残る。計測（`エディタ状態収集` は 1.4ms / フレーム）に対して割に合うかを見てから、消費側が `show_gizmo_pre` 配下だと確認できたものから順に囲うこと。関連: `runtime/src/engine/core/app_base/app/frame_renderer.rs`。

## 釣りシーンのスクリプト側 per-frame コスト（2026-09-07 の静的監査／未修正）

- [ ] **`FishingController.UpdateFishRadar` が毎フレーム `Fish.All` / `DriftItem.All` を全走査する** — `FishingController.cs` 2341-2422（`Update` から無条件呼び出し・1625 行）。距離判定より**先に** `Transform.Position` を FFI で読むため、`Fish.UpdateFarLevelCulling`（`Fish.cs` 815）で既に遠距離カリング済みの魚まで毎フレーム 1 回ずつ FFI が走る。レーダー射程で空間分割するか、少なくとも既存のカリングフラグを見てから Transform を触ること。
- [ ] **`FishManager` が管理魚 1 体につき毎フレーム 2 回 `GetComponent<Transform>()` を呼ぶ** — `IsAlive`（267-272 / 538-541）と `ClampFishToRings`（497-527、`LateUpdate` から毎フレーム）。`TrySpawnOne`（324 行）で解決済みの `Transform` ハンドルを `spawnedFish` と一緒に保持すれば 0 回にできる。
- [ ] **`Fish` 1 体の 1 フレームで Transform の get/set が 5〜7 回に膨らむ** — `UpdateApproach` の遠距離枝（1072-1122）は `Position` を 3 回読み 2 回書く（`SwimTowardHeading` 1192-1202 が内部で再取得するため）。取得済み座標を引数で渡し、Y 補正を合成してから 1 回だけ書くようにすれば 2〜3 回に減る。
- [ ] **`FishingFight.DrawGauge` が毎フレーム 48 回の `Draw.Rect`** — `FishingFight.cs` 2428-2491。設計上の割り切りだが、点灯状態が変わったセグメントだけ描き直せば大半のフレームで数回に減る。
- [ ] **`FishingFight.ApplyBeatIcons` がアイコンプール全件に毎フレーム Position/Scale/Color を書く** — 2702-2734。非表示（alpha=0）で不変のアイコンにも書いている。`ApplyIconSizes` が既にやっている「変化時だけ書く」パターンを広げる。
- [ ] **`FishingController.UpdateLine` が毎フレーム `LineHelper.Catenary` の戻り配列を確保する** — 3788-3806。`lineSegments + 1` 点の配列をフレームごとに作って `SetPoints` へ渡している。事前確保したバッファを埋める形にし、浮きが動いていないフレームは `SetPoints` 自体を省く。
- [ ] **`FishingFight.ApplyStatusText` が毎フレーム文字列補間して `Text.Content` を設定する** — 2799-2816。フェーズ名は数秒不変なのに毎フレーム作り直している。直近値をキャッシュして変化時だけ設定する。
- [ ] **`CameraMove.UpdateFov`（452 行）と `PlayerMove`（415 / 792 行）が毎フレーム `GetComponent` を呼ぶ** — いずれも単一インスタンスなので影響は小さいが、`IsValid` が落ちたときだけ引き直す遅延キャッシュにできる（ホットリロード追従も保てる）。

## 統合バッチ更新ゲートの計測で見えた残件（2026-09-07・`seed_profile` 導入時）

- [ ] **`描画/統合バッチ更新/GpuModel 表` が 0.14 ms/フレーム** — 2026-09-07。統合バッチ更新の内訳のうち、バッチ更新（0.60 ms）に次いで重い区間。バッチ数ぶんの `gpu_model_by_path` 引き直しと思われるが未調査。バッチ集合が変わらないフレームは表を作り直さずに済むはず。関連: `runtime/src/engine/core/app_base/app/frame_renderer.rs`。
- [ ] **統合バッチのキーに制御文字 `\u0001` が混ざる** — 2026-09-07。マテリアルオーバーライド署名の区切りに `\u0001` を使っているため、`seed_profile` のダンプやログで `yasi.glb\u0001#10` のように読めない文字が出る。表示用に置換するか、区切りを可読な文字にする。関連: `ModelComponent::batch_key_into`。
- [ ] **ヘッドレス計測では MainGame のスクリプトが実質走らない** — 2026-09-07。`seed_launch(headless)` + `seed_play` で測ると `スクリプト/Update` が 0.001 ms/フレームで型別内訳が 1 行も出ない（対話エディタでの計測では Fish などが上位に出る）。ヘッドレスでスクリプトが起動しない条件を切り分けないと、スクリプト側の性能を MCP から計測できない。

## 時間スケール（`SEED.Time.Scale`）の残件（2026-09-07 実装時）

- [ ] **実機での停止確認が未実施** — 2026-09-07。`Time.Scale` の適用先（スクリプト dt／アニメーション／物理 3D・2D／パーティクル／水位・インタラクション場／ConstantUpdate 回数）は Rust 単体テストと経路読解で確認したが、Play 実行での目視確認をしていない。特に「物理スレッドが scale=0 で本当に静止するか」「0.5 でちょうど半速に見えるか」は実機で確認すること。関連: `runtime/src/engine/physics/thread.rs` / `thread2d.rs`。
- [ ] **高い `Time.Scale` は物理の積分精度を落とす** — 2026-09-07。物理は「ステップ間隔 1/60 秒を保ったまま 1 ステップの積分時間を `PHYSICS_FIXED_STEP * scale` へ伸縮させる」方式にした（スロー時に滑らかにするため）。そのため倍速側では 1 ステップの dt が大きくなり、貫通・ジッタが出やすくなる。倍速を実用するなら「1 フレームに複数ステップ踏む」方式へ切り替える必要がある。関連: `runtime/src/engine/physics/thread.rs`。
- [ ] **`Camera.WorldToScreen` は 3D カメラ専用** — 2026-09-07。エディタの 2D シーンビュー／アクター編集タブが使う 2D オルソカメラ（`canvas_cameras`）は `CameraComponent` ではないため、この API では射影できない。Play 中の 3D カメラでは正しく動くが、Edit ビューのプレビュー座標が欲しくなったら別経路が要る。関連: `runtime/src/engine/core/scripting/camera_project.rs`。

## Text のインライン画像記法の残件（2026-09-07 実装時）

- [ ] **実機での描画確認が未実施** — 2026-09-07。記法パーサ・`.icons` 解析・折り返し・境界矩形・切り詰めは Rust 単体テストで確認したが、Play / エディタでの目視確認をしていない。特に「画像の縦位置（x ハイト中央そろえ）が本文中で自然に見えるか」「枠つきテキスト＋pivot で画像がグリフとズレないか」は実機で確認すること。関連: `runtime/src/engine/core/font/inline/`。
- [x] **`.icons` と画像寸法のキャッシュがホットリロードへ未接続** — 2026-09-07 記載 / 2026-09-09 対応。通知経路への接続ではなく、`ICON_SET_POLL_INTERVAL`（1 秒）ごとの更新時刻ポーリング方式で解決。`icon_set::poll_changes` / `image_meta::poll_changes` が各エントリの実ファイル mtime を比較し、変化したものだけ再読込する（解析・デコード失敗時は前回の内容を維持し、次回また再試行する）。`inline::poll_asset_changes()` を `App::build_text_expand_map`（フレーム頭）から呼び、変化があれば `invalidate_text_expand_cache` と `doc::reset_warnings` を実行する。PAK 実行では丸ごとスキップ。関連: `runtime/src/engine/core/font/inline/{icon_set.rs, image_meta.rs, mod.rs}`、`runtime/src/engine/core/app_base/app/text_expand.rs`。実機（Play/エディタでの反映確認）は未検証。
- [ ] **インライン画像のレイアウトが 1 フレームに 2 回解かれる** — 2026-09-07。グリフ描画（`canvas_text::append_item`）とスプライト収集（`canvas_collect::collect_inline_image_sprites`）が同じ `resolve_layout_with_images` を別々に呼ぶ。記法を含まない本文は角括弧の有無で早期に抜けるため通常は無視できるが、記法を多用する画面では 1 回に減らす余地がある（レイアウト結果をフレーム内キャッシュする等）。
- [ ] **レイアウト用フォントが描画用レジストリと別実体** — 2026-09-07。`font::layout_fonts` は GPU 非依存の層（スプライト収集）から寸法を測るために独自の `FontRegistry` を持つ。組み込みフォントは静的参照なので複製されないが、外部フォント（.ttf）を指定すると描画側と合わせて 2 部常駐する。共有したい場合は描画器のレジストリを `Arc<Mutex<..>>` 化する必要がある。
- [ ] **インライン画像に太さ・影のぼかしが効かない** — 2026-09-07（仕様として割り切り）。`weight` は SDF のしきい値操作なので画像には適用できず、`shadow_softness` もスプライト経路ではぼかせない。影自体は同じオフセットで落ちる。

## わらしべフィッシングのチュートリアルモード（2026-09-07 実装時）

- [ ] **説明窓の素材が未着（仮素材で実装済み）** — 2026-09-07。`<project>/assets/mainGame/actors/UI/TutorialWindow.actor` のミニキャラと吹き出しは `assets://mainGame/textures/ui/white.png` を着色した矩形、送りマークは prologue の `nextArrow.png` を流用している。差し替えは Sprite の `texture_path`（と `width` / `height`）を変えるだけでよく、スクリプトの変更は不要。
- [ ] **キーアイコンが仮画像** — 2026-09-07。`<project>/assets/mainGame/ui/tutorial.icons` の `key_w` / `key_s` / `key_a` / `key_d` / `mouse_l` はすべて `white.png` を指している。本番画像ができたら `.icons` の `path` を差し替えるだけで説明文（`[icon:key_w]` 等）へ反映される。`.icons` のキャッシュは 1 秒間隔のポーリングで自動反映されるため、差し替え後はエディタの再起動不要（本ファイル「Text のインライン画像記法の残件」参照）。
- [ ] **チュートリアルの実機確認が未実施** — 2026-09-07。スクリプトのコンパイルとシーン JSON の整合（参照先アクタ・フィールド名）はプログラムで照合したが、Play での目視確認をしていない。特に (1) `Time.Scale = 0` 中に釣りの各状態が破綻しないか、(2) 説明窓の追従（`Camera.WorldToCanvas`）が 1920x1080 設計キャンバス上で意図した位置に出るか、(3) 台本（必ず食いつく／漂流物を出す）が手順どおり効くか、の 3 点は実機で確認すること。
- [ ] **`<project>/assets/tutorial/scripts` の空ディレクトリが残っている** — 2026-09-07。旧チュートリアルシーン（`tutorial.scene` / `TutorialFlow.cs`）は削除済みだが、実行中のエディタがディレクトリのハンドルを掴んでいるため空フォルダだけ消せなかった。エディタを閉じてから削除すること。

## アクターの表示フラグ（visible）の残件（2026-09-07 実装時）

- [ ] **実機での目視確認が未実施** — 2026-09-07。Rust 単体テスト（実効表示の祖先伝播 / serde 往復 / ヒエラルキー JSON）と両言語のコンパイルは通したが、Play・エディタでの目視確認をしていない。特に (1) ヒエラルキー行の目アイコンがクリックで切り替わり、行選択やリネームタイマを誤爆させないか、(2) 非表示アクタがビューポートでクリック選択できなくなる（＝ヒエラルキーから選ぶ運用になる）ことの使い勝手、(3) Undo（Ctrl+Z）で目アイコンが戻るか、の 3 点は実機で確認すること。関連: `editor/src/Controls/VisibilityToggle.cs`。
- [ ] **非表示中はパーティクルのシミュレーションも止まる** — 2026-09-07（仕様として割り切り）。`particle_system::gather_emitters` は非表示サブツリーを丸ごと収集対象から外すため、非表示中は放出も更新も進まず、再表示すると止まっていた状態から再開する。「見えないが裏で流れ続ける」挙動が要るなら、収集は続けたうえで `RawEmitter` に可視フラグを持たせ、`draw_pool` 側で描画だけを落とす作りへ変える必要がある。関連: `runtime/src/engine/core/renderer/particle_system.rs`。
- [ ] **エディタ専用のデバッグ描画は visible を見ていない** — 2026-09-07。2D コライダーのワイヤフレーム（`collider2d_wireframe.rs`）・地形チャンク・各種シーンギズモ（ライト／パーティクル／制御点）は非アクティブ判定のみで、非表示アクタでも線が出る。エディタ上の補助表示なので実害は小さいが、統一するなら各収集へ `!actor.visible` を足す。
- [ ] **スクリプトからの `GameObject.Visible` set はフレーム末尾適用** — 2026-09-07。`Destroy` / `SetParent` と同じ遅延モデルで、同フレーム中の get だけは保留値テーブル（`scripting/visible_pending.rs`）が設定値を返す。同フレーム中に「他のアクタから見た実効表示」を問い合わせるとまだ古い値になる（現状そのような API は無い）。

## スプライトの白フチ対策（アルファブリード）の残件（2026-09-07 実装時）

- [ ] **実機での目視確認が未実施** — 2026-09-07。アルファブリード（`runtime/src/engine/core/renderer/texture/alpha_bleed.rs`）は単体テストで挙動を確認したが、Play での見た目確認をしていない。特に (1) 既存スプライトの輪郭から白線が消えているか、(2) 拡大表示・回転したスプライトでにじみが目立たないか、(3) 大きな PNG（2K 以上）でロード時のヒッチが出ないか、の 3 点は実機で確認すること。
- [ ] **3D マテリアルのベースカラーテクスチャにはブリードを掛けていない** — 2026-09-07。葉・金網のようなアルファマスク／アルファブレンドのマテリアルも原理的には同じ白フチが出る（しかもミップがあるぶん縮小時に悪化する）。掛ける場所は `asset_cache::build_ready_texture` の**ミップ生成直前**、対象は `TextureUsage::ColorSrgb` のみ（法線・MR・データ系は除外）。ただしモデルの派生キャッシュに焼かれるため `CACHE_FORMAT_VERSION` の更新が必須で、全モデルのキャッシュ再生成（大規模シーンで数分〜）を強いる。現時点で 3D 側の白フチ報告が無いので見送り。
- [ ] **Option B（プリマルチプライドアルファ化）は未採用** — 2026-09-07。スプライト経路を「アップロード時に RGB へアルファを乗算 ＋ ブレンドを `One / OneMinusSrcAlpha`」へ変えると、補間で色とアルファが同時に減衰するため白フチが数学的に発生しなくなる（アルファブリードは近似的な回避）。ただし同じ `GpuSpriteTexture` をポストエフェクト焼き込み（`renderer/postfx/bake.rs`）とキャンバス ID パス（`canvas_id.wgsl`）が共有しており、焼き込み結果を再びスプライトとして描く経路では二重乗算になる。全消費者の洗い出しとシェーダ側の同時変更が要るため、アルファブリードで足りない症状が出てから着手する。
- [ ] **テクスチャ単位のブリード除外設定が無い** — 2026-09-07。適用可否はローダ側のハードコード（スプライト／キャンバス UI／パーティクルは掛ける、地形マスク・法線・SDF アトラスは掛けない）で決めており、個別テクスチャのオプトアウト手段は無い。`.meta` 相当のインポート設定を持ち込むのは大掛かりなので、必要になってから検討する。

## プロジェクトパネルのモデルサムネイル（2026-09-12 実装時の残件）

- [x] **サムネイル生成中、エディタのビューポートに被写体が映り込む** — 2026-09-12 記載 / 同日対応。
  撮影フレームを**専用のオフスクリーンカラーターゲット**（
  `runtime/src/engine/core/renderer/thumbnail/target.rs`）へ描き、present しないようにした。
  出力先の差し替えは `Renderer::begin_offscreen_frame` の 1 か所だけで、
  `RenderFrame` が持つ出力を `FrameOutput::{Swapchain, Offscreen}` に分けてある
  （途中の描画コマンド列・ID パス・中間バッファは通常フレームと完全に同一）。
  切り替えの単位はジョブではなく**セッション**（`thumbnail_session.is_some()` の間は
  すべてのフレームがオフスクリーン）。ターゲットは描画解像度ちょうどで確保し、
  描画解像度が変わったときだけ作り直す。
  実測（A/B）: 修正前は提示フレームのダンプ 119 枚のうち複数枚が被写体の全画面絵だったが、
  修正後は 44 枚すべてが通常シーンの絵と**バイト一致**した（図鑑側 `RENDER_ACTOR_THUMBNAIL` でも 49 枚一致）。
  副次効果: 読み戻し元が「描画解像度そのもの」になったので、提示テクスチャ（ウィンドウ実寸・
  レターボックスの黒帯付き）と ID バッファ（描画解像度）の解像度が食い違う経路が消えた
  — 内部解像度固定（fixed）が効く非埋め込み Play 中に `RENDER_ACTOR_THUMBNAIL` を投げると
  合成が解像度不一致で失敗しえた（狭い経路で未再現。コード上の読み取り）。
  なお生成中は present が止まるため、**ビューポートはセッション中ずっと静止画**になる
  （被写体は映らない）。Play 中に処理しない方針はこの理由で維持している。
- [ ] **Play から Edit へ戻っても、待っていたサムネイル要求は自動では再開しない** — 2026-09-12。
  Play 中は要求を送らず待ち行列も畳む仕様のため、Edit へ戻った時点では要求が残っていない。
  フォルダを開き直せば飛ぶ。Play 遷移を購読して再スケジュールすれば自動化できる。
- [ ] **ウィンドウ最小化中はサムネイルを作れない** — 2026-09-12（オフスクリーン化のときに実測）。
  提示テクスチャを使わなくなったので原理的には作れるはずだが、
  `frame_renderer.rs` の**最小化ガード**（`window.inner_size()` が 0×0 ならフレームを丸ごとスキップ）が
  先に効くため 1 枚も描かれない。スタンドアロンのウィンドウを最小化して `THUMBNAIL:` を
  投げたところ、6 分待っても PNG は生成されず、ジョブのデッドライン（30 秒）による
  `THUMBNAIL_FAILED` も受け取れなかった（このときハーネス側もパイプ書き込みで止まっており、
  どちらの問題かは切り分けていない）。
  直すなら「0×0 のときは最後に有効だったサイズ（`Renderer::render_size()`）を
  `window_size` / `real_window_size` の代わりに使う」形になるが、フレーム内の
  ビューポート計算すべてに関わるので影響範囲が広い。
  なお**エディタ埋め込み時**は子ウィンドウのクライアント矩形が親の最小化で 0 にならないため、
  この経路には当たらないと考えられる（未検証）。
- [ ] **長い連続生成の間、`SCREENSHOT:` 要求が待たされる** — 2026-09-12。
  撮影フレームは present しないので、スクリーンショットはセッションが畳まれた後の
  提示フレームで処理される。数十件を一括生成している間は要求が滞留し、
  エディタ側のタイムアウトに当たる可能性がある（従来は撮影フレームを掴んでしまい、
  サムネイルの被写体が写った「間違った絵」が返っていたので、待つこと自体は改善）。
  必要になったら「セッション中に撮影要求が来たら 1 枚だけ提示フレームを挟む」で解ける。
- [ ] **キャッシュ PNG の掃除機構が無い** — 2026-09-12。
  `<プロジェクト>/cache/thumbnails/` はモデルを更新するたびに新しいハッシュのファイルが増え、
  古いものは残り続ける（1 枚あたり数〜数十 KB）。
  プロジェクト設定か「キャッシュを掃除」メニューで消せるようにしたい。

## 図鑑（魚サムネイル生成）の残件（2026-09-08 実装時）

- [ ] **全 31 種の魚 prefab で「表示名」（`Fish.cs` の `fishName`）が未入力** — 2026-09-08。`.actor` の `ScriptComponent.fields` は既定値と異なる値だけを保存する仕様で、31 種すべてに `fishName` のキー自体が無い。そのため `FishCatalog.cs` の `displayName` はローマ字のアクタ名（`iwashi` / `maguro` …）にフォールバックしている。ゲーム内表示も `Fish.DisplayName` が既定名 `魚` を返す状態なので、**図鑑を出す前に各 prefab のインスペクタで日本語名を入れる**必要がある。入れたあと `ツール > 図鑑画像を生成`（または `python tools/gen_fish_catalog.py`）を流し直せば `FishCatalog.cs` に反映される。
- [ ] **一部モデルのローカル AABB が実体より大きい** — 2026-09-08。`iseebi` / `tatsunootoshigo` / `ryuuguunotsukai` は `Model::local_aabb`（glTF の全メッシュの生頂点を包む箱）が実際に描画される範囲の数倍あり、AABB だけで構図を決めると被写体が隅に小さく写った。サムネイル側は「撮った ID マスクの実測値で構図を追い込む」ことで回避済みだが、**同じ AABB を LOD 選択とフラスタムカリングも使っている**（`gpu_resources.rs` の `world_aabbs`）ので、これらのモデルは実際より遠くから LOD が落ちない／カリングが効きにくい可能性がある。原因（描画されない補助メッシュ / スキンのバインドポーズのずれ）の切り分けは未実施。
- [ ] **スクリプトインスペクタのボタン属性（`[InspectorButton]`）は未実装** — 2026-09-08。FishManager のインスペクタから直接「図鑑画像を生成」を押せるようにする案は見送った。`ScriptCompiler.ExtractFields` は `FieldInfo` しか列挙しておらずメソッド／クラス属性を運ぶ経路が無いため、属性定義・メタデータ抽出・`ScriptFieldInfo` と並ぶ受け皿・`ScriptInspectorBuilder` の描画・呼び出し経路をすべて新設する必要がある。現状は `ツール > 図鑑画像を生成` メニューと MCP ツールで同じことができるので、他にもボタンを出したい要求が溜まってから着手する。
- [ ] **`scriptcheck` の csproj がユーザースクリプトを全部拾えていない** — 2026-09-08。`<Compile Include=".../mainGame/scripts/*.cs" />` が非再帰のため `mainGame/scripts/Tutorial/**` と `common/scripts/**` が漏れ、`TutorialRules` / `Easing` 未定義のエラーが出る（本物のエラーではない）。`**\*.cs` へ変え、`common/scripts` も足すと解消する。なお両方を含めた状態でも `Tutorial/Missions/MissionContext.cs` に `TutorialDirector.Player` / `.Interject` 未定義の実エラーが 2 件残っており、これは図鑑とは無関係の既存の未完成箇所。
- [ ] **ヘッドレス起動時に `camera gizmo model load failed` が毎フレーム出る** — 2026-09-08。`SEED_RUNTIME_EXE` で別ビルドのランタイムを使うとギズモ用モデル（`templates/` 配下）が exe 隣の相対パスで解決できず、STDERR に毎フレーム 2 行出続けてログが埋まる。図鑑生成の動作自体には影響しないが、ログが読めなくなるので 1 度だけ警告して以後は抑制したい。

## ポーズメニュー・図鑑シーンの残件（2026-09-08 実装時）

- [x] **ポーズメニューを MainGame で実際に開く確認** — 2026-09-08 にヘッドレス Play で実施。Esc で暗幕とメニューが出ること・「ゲームにもどる」で復帰すること・「図鑑」→ 図鑑シーン → Esc で MainGame へ戻ることを確認した。当初メニューが出なかった原因は `SEED.Entity.IsValid` が `default` のハンドルを有効と誤判定していたこと（`scripting/src/Api/Entity.cs` で修正。回帰テストは `editor/tests/ScriptEntityHandleTests`）。
- [ ] **ポーズメニューのスプライトが FishingUI のダイアログより奥に描かれる** — 2026-09-08。動的生成した PauseMenu キャンバスは root の末尾に付くのに、暗幕（PauseOverlay）と選択の下敷き（PauseHighlight）が FishingUI のダイアログ枠より奥に出る。選択行の下敷きは 2 行目・3 行目ではダイアログ枠に完全に隠れて見えない。**「文字だけダイアログの上に浮く」半分は 2026-09-08 に解決済み**（UI 描画順をゾーン → レイヤー → 種別で統合。`renderer/ui_draw_order.rs` / `ui_draw_pass.rs`）。残るスプライト同士の前後は純粋に `layer` 値の設計問題（PauseOverlay / PauseHighlight のレイヤーが FishingUI のダイアログ枠より小さい）と思われるので、両者のレイヤー帯を決め直すこと。
- [ ] **`GameObject.IsValid` は「破棄済みか」を見ていない** — 2026-09-08。`SEED.Entity.IsValid` は「未束縛でない」かどうかだけを見るので、`Destroy` 済みのアクタを指すハンドルも `true` を返す（ランタイムへ生存を問い合わせていない）。`docs/scripting_api.md` は「参照先が今も生きているか」と説明しており、実装と食い違っている。生存問い合わせの FFI を足すか、ドキュメントの表現を実装に合わせるかの判断が要る。
- [ ] **旧セーブキー `best_size:魚` が孤立している** — 2026-09-08。魚 prefab に日本語の表示名を入れる前は `Fish.DisplayName` が全種で既定名 `魚` を返していたため、既存の `runtime/save/save.json` には全魚種ぶんが混ざった `best_size:魚` が 1 件だけある。今後は `best_size:<日本語名>` で記録されるので、この旧キーはどこからも読まれない。移行するか消すかは要判断（移行先が特定できないので削除が妥当）。
- [ ] **`resolve_dll_path` の開発時候補がカレントディレクトリ基準** — 2026-09-08 記載 / 2026-09-10 に候補パスのみ更新。`runtime/src/engine/core/scripting/mod.rs::resolve_dll_path` は `cwd/../scripting/bin/Debug/net10.0/SEEDScripting.dll`（開発ビルド出力）→ `{exe のフォルダ}/bin/SEEDScripting.dll`（配布配置）の順で探す。ランタイムの作業ディレクトリは `RuntimeManager.ResolveWorkingDirectory` が「exe の 2 階層上が `target` のときだけ」リポジトリ側へ上げるため、`docs/editor_mcp.md §5.5` が推奨する `cargo build --target-dir <別ディレクトリ>` で作った SEED.exe を `SEED_RUNTIME_EXE` で使うと DLL が見つからず、ランタイムが起動しない（エディタ側は「ランタイムが接続しません」としか言わない）。回避策は出力先の **`bin/` サブフォルダ**へ `SEEDScripting.dll` 一式を手でコピーすること（2026-09-10 のレイアウト移行で exe 直下は候補から外れた）。開発ビルド出力を exe の位置からも探すか、環境変数で明示できるようにしたい。
- [ ] **`seed_launch(scene:)` が `assets://` パスを受け付けない** — 2026-09-08。`editor/SeedMcpServer/Launcher.cs` は `Path.GetFullPath(scenePath)` をそのまま `--scene` へ渡すため、`assets://zukan/zukan.scene` は `…\SEED\assets:\zukan\zukan.scene` という壊れたパスになり、シーンが読めないまま「ランタイムが接続しません」でタイムアウトする（原因が一切表示されない）。絶対パスなら正常に動く。`assets://` を assets ルート基準へ解決するか、少なくともエラーとして弾きたい。
- [ ] **キャンバスの `auto_scale` がカメラ基準解像度より大きいキャンバスを縮小しない** — 2026-09-08。カメラの `target_width/height` が 1280x720 のとき、`auto_scale: true` の 1920x1080 キャンバスは 1 単位＝描画ターゲット 1px で描かれ、中央 1280x720 の外に置いた要素は画面に出ない（ヘッドレス Play のスクリーンショットで実測）。今回は図鑑・ポーズメニューのキャンバスを 1280x720 にして回避した。既存の `FishingUI` は端をアンカー基準で置いているため実害が出ていないだけなので、`auto_scale` の意図（基準解像度へフィットさせる）どおりに効いているか要確認。
  **2026-09-10 追記**: `render_resolution_mode: "fixed"` では描画ターゲットが `window_width × window_height`（例 1280x720）に固定されるので、キャンバスもその解像度に合わせておけば「1 単位＝描画ターゲット 1px」の前提が崩れず、ウィンドウサイズによらず同じ見た目になる。上の項目と同じく回避策であって根本解決ではない。
- [x] **`[SerializeField]` の参照解決がシーン全体のアクタ名 DFS なので、同じプレハブを複数生成すると参照が 1 個目へ集まる** — 2026-09-08 に解決。参照文字列へパス形式（`./Child` / `../Sibling` / `Root/Child`）を導入し、素の名前は「自分のサブツリー優先 → シーン全体」で解決するようにした（正典: `runtime/src/engine/core/scripting/actor_ref_path.rs`、docs/scripting_api.md「参照文字列のパス指定」）。`GameObject.FindChild(nameOrPath)` も追加。図鑑カードは `assets://zukan/actors/ZukanCard.actor` のプレハブインスタンス 4 枚になった。
  **残る制限（この項目の続き）**:
  - パス形式で保存された参照は**アクタのリネームに追従しない**（`rename_refs.rs` は「フィールド値が旧アクタ名そのもの」のときだけ書き換えるため）。素の名前で保存された参照はこれまでどおり追従する。
  - 参照ボックスの「参照先が見つかりません」警告（`ReferencePicker.RefreshLabel`）は、パス形式のとき**判定を諦めて出さない**。持ち主基準の解決をエディタ側で再現していないため。
  - 参照ボックスのダブルクリックによる Hierarchy ジャンプ（`ActorRefJump.RevealActorByName`）もパス形式では効かない（名前一致で探すため）。
  - `ScriptEvent` の結線先アクタ（`ScriptEventBinding`）は従来どおりシーン全体 DFS のまま。プレハブ内のイベント結線は同名インスタンスで壊れうる。
- [ ] **`<project>/assets/tutorial/scripts` がアクセス拒否でスクリプト収集から毎回スキップされる** — 2026-09-08。エディタのログに `[ScriptCompiler] 読み取れないフォルダをスキップ … Access to the path … is denied.` が再読込のたびに出る。assets が別ドライブへのジャンクションであることに由来する権限の問題と思われる。

## プレハブのシーンへの反映（2026-09-08 実装時）

正典: `docs/editor_prefab.md`。

- [ ] **実機での目視確認が未実施** — 2026-09-08。Rust 単体テスト（内容ハッシュ・版ずれ集計・`prefab_hash` の serde 往復）と 3 系統のビルドは通したが、Play・エディタでの目視確認をしていない。特に (1) アクタータブで `.actor` を保存したときにトースト「プレハブ … を N 個のインスタンスへ反映しました」が出てヒエラルキーが更新されるか、(2) Ctrl+Z で反映前へ 1 操作で戻るか、(3) シーンを開き直したときの版ずれバナーの［更新する］／［無視］が効くか、(4) バナーが AvalonDock のツールバーと重ならないか、の 4 点は実機で確認すること。関連: `editor/src/MainWindow.Prefab.cs`。
- [ ] **`PREFAB_REAPPLY_PATH` / `PREFAB_STATUS` のパーサに単体テストが無い** — 2026-09-08。`ipc.rs` の本体パーサは `read_loop` 内のインライン `match` で、地形コマンドのような純粋関数（`parse_terrain_command`）に切り出されていないためテストできない。`PREFAB_REAPPLY:` と `PREFAB_REAPPLY_PATH:` の前方一致衝突（`:` と `_` で分かれるので実際には衝突しない）は目視確認のみ。アクタ系コマンドも `parse_actor_command` として切り出せば同じ形でテストできる。
- [ ] **アクタータブの保存が、開いていないシーンのインスタンスには当然届かない** — 2026-09-08（仕様）。自動反映の対象は「エディタが今開いているシーン」だけ。同じプレハブを使う他のシーンは、そのシーンを開いたときに版ずれバナーで気付く作りになっている。プロジェクト全体を走査して一括反映する仕組みは無い（`.scene` を開かずに書き換えることになるため、意図的に作っていない）。
- [ ] **ネストプレハブの版ずれは内側を見ていない** — 2026-09-08。`collect_prefab_status_in_actor` はプレハブインスタンスのルートを見つけたら配下へ降りない（再展開が 1 段のみという現行仕様に揃えたもの）。プレハブ A の中にプレハブ B が入っている場合、B の更新は検出されない。ネストプレハブの再帰展開（`prefab_ops.rs` の TODO(P2)）と同時に対応するのが筋。
- [ ] **版ずれバナーの［更新する］は参照パスの数だけ Undo 履歴を積む** — 2026-09-08。`PREFAB_REAPPLY_PATH` を対象パスごとに送るため、3 本のプレハブが更新されていると Ctrl+Z を 3 回押す必要がある。1 操作にまとめるなら、複数パスを 1 コマンドで受ける IPC か、ランタイム側の `CompositeCommand` が要る。
- [ ] **`prefab_hash` は `.actor` のテキストをそのままハッシュしている** — 2026-09-08。整形（インデント・キー順）だけが変わっても版ずれとして検出される。ランタイムが `serde_json::to_string_pretty` で書き出す限り安定するが、`.actor` を外部エディタで整形すると「中身は同じなのに更新扱い」になる。意味的な同値判定（`ActorData` を正規化してからハッシュ）にするかは要判断。
- [ ] **Text の「リセット」は枠を 0 に戻す（新規追加の 300×60 と不一致）** — 2026-09-08。`app/component_reset_ops.rs` のリセットは `TextComponent::default()`（枠なし = 0）を使うため、新規追加直後（`new_for_editor_add` の 300×60）にリセットすると枠が消えて掴みにくい状態へ戻る。`Default` はテスト・内部生成でも枠なし前提のため今回は変更せず据え置き。リセットの既定を「追加時と同じ」にするか「枠なし」に統一するかは要決定。

## 釣果リザルト演出（2026-09-08 作り直し時）

正典: `<project>/assets/mainGame/scripts/CatchPresenter.cs` / `ResultPanel.cs`。

- [ ] **スクリプトのメソッドを外から叩く IPC（`SCRIPT_DEBUG:<name>,<arg>`）が無い** — 2026-09-08。今回の目視確認では「釣果パネルだけを出す一時シーン＋一時ドライバスクリプト」を作って撮影し、確認後に消した。ヘッドレスで任意のゲーム進行（例: 釣り上げの瞬間）を再現できないため、3D 側（スロー放物線・横カメラ・しぶき）は<b>未検証のまま</b>。`SEED.Debug.OnCommand` のような購読口と `seed_send_ipc` からの `SCRIPT_DEBUG:` を実装すれば、AI による検証の守備範囲が大きく広がる。
- [ ] **スロー放物線・横カメラ・しぶきパーティクルが実機未確認** — 2026-09-08。魚を実際に釣り上げないと通らない経路のため、ヘッドレスでは撮れていない。特に (1) `assets://mainGame/actors/FX/Splash.actor` を実行時 `Instantiate` したときに GPU パーティクルが放出されるか（Play 開始時に存在しないエミッタの扱い）、(2) 横カメラの θ/φ/距離の既定値で弧が画面に収まるか、(3) `Time.Scale` を下げているあいだに他システムが破綻しないか、の 3 点は人の目で確認すること。
- [ ] **旧「釣果テキスト」（FishingUI/catchUIs 配下）が未使用のまま残っている** — 2026-09-08。`CatchPresenter` は `ResultPanel` へ移行したので、`CatchName` / `CatchSize` / `CatchRank` / `CatchBest` / `CatchPrompt` は誰からも参照されない。シーン（MainGame.scene）は AI が触らない約束のため残置。利用者が削除すること。
- [ ] **Text（`box_width = 0`）の実際の描画位置が指定 y よりわずかに上に出る** — 2026-09-08。`ResultPanel.actor` のレイアウトはヘッドレス撮影を見ながら数値を合わせ込んだ。`vertical_align: middle` の基準がフォントのどの高さなのかを確かめて、指定位置＝視覚的な中心になるようにするか、ずれ幅を仕様として docs へ書くのが望ましい。

## 釣果リザルト演出 — 縦跳び化とデバッグコマンド（2026-09-08 実装時）

正典: `<project>/assets/mainGame/scripts/CatchPresenter.cs` / `ResultPanel.cs`、`docs/editor_mcp.md` 10 章。

- [ ] **チュートリアル中に魚を釣ると釣果パネルを閉じられずソフトロックする** — 2026-09-08（実測）。
  `ResultPanel.IsConfirmPressed` は `InputGate.Allows(GameAction.UiConfirm)` を要求するが、
  `TutorialDirector.ApplyMissionGates` は `InputGate.DenyAll()` のあとミッションの
  `allowUiConfirm` だけを開ける。`allowUiConfirm = false` のミッション中に釣り上げると、
  Enter も左クリックも効かず `CatchPresenter` が `Result` フェーズから抜けられない
  （ヘッドレス検証で Enter を 2 回送っても閉じないことを確認済み）。
  対策候補: (1) 釣果パネルの決定入力を `InputGate` の対象外にする、
  (2) 釣りが成立しうるミッションの `allowUiConfirm` を必ず true にする、のいずれか。**要判断**。
- [x] **釣り上げ演出の横カメラが、魚を画面中央に置けていない** — 2026-09-08 に解決。
  原因は構図のズレではなく<b>カットが効いていなかった</b>こと。
  `CameraMove.RequestSnap` は「次の LateUpdate 1 回」で消費されるが、その時点の
  フェーズはまだ `Fade` で、`SelectCatchGoal` は `Fade` を除外していたため
  <b>カット前の目標（CastCameraTarget）へスナップ</b>して要求が捨てられていた。
  横の構図へ切り替わるのは `SlowArc` に入ってからで、そこには何のカットも無く
  位置・回転・FOV が指数補間で寄っていた（＝白が晴れたあともカメラが動き、
  途中の画では魚が中心から外れる）。
  対策として `CameraMove.SetOverrideGoal(位置, 回転, snap, 画角)` を新設し、
  `CatchPresenter` が計算した姿勢を直接渡してカットするようにした
  （シーンの `catchTarget` 結線に依存しない・移動ロールも掛からない）。
  ヘッドレス実測: カット後 0.45〜3.2 秒のあいだ `MainCamera` の
  位置・回転が 1 ビットも動かないことを `seed_find_actor` で確認済み。
- [ ] **しぶきパーティクル（`assets://mainGame/actors/FX/Splash.actor`）の放出が未確認** — 2026-09-08。
  実行時 `Instantiate` は成功している（生成失敗の警告は出ていない）が、
  ヘッドレスのスクリーンショットではしぶきを確認できなかった。
  カット直後はカメラがまだ寄り切っておらず着水点が画面外に近いため、
  「出ていない」のか「撮れていない」のかを分離できていない。
  実行時生成のエミッタが放出するかどうかを、Splash 単体のシーンで切り分けること。
- [ ] **`SEED.Debug.OnCommand` の登録表はシーン遷移で消えない** — 2026-09-08（仕様）。
  静的な `Dictionary` なので、`OnDestroy` で `OffCommand` を呼ばないハンドラは
  破棄済みスクリプトを掴んだまま残る。`Play` 停止時に
  `SEED.Debug.ResetCommandHandlers()` を自動で呼ぶ仕組みを入れるかは要判断
  （他の静的状態（`PauseMenu` など）も同じ課題を抱えており、まとめて決めるのが筋）。
- [ ] **シーン上の `ResultPanel` インスタンスの `ResultBody` がスケール 0 のまま保存されている** — 2026-09-08。
  プレハブ側は原寸（1）で保存し直した（コミット a5504def）が、`MainGame.scene` に置いてある
  インスタンスには旧プレハブの値（0）が残っている。実行時は `OnStart` が必ず 0 → 開きで
  上書きするので<b>ゲーム中の見た目には影響しない</b>が、エディタの編集画面では中身が見えない。
  直すならインスタンスへプレハブを再適用（`seed_prefab_reapply`）するか、
  インスペクタで `ResultBody` のスケールを 1 に戻して保存すること（シーン編集なので要利用者判断）。
- [ ] **文字の影（`shadow_*`）のアルファが文字色のアルファと連動しない** — 2026-09-08（現状は仕様）。
  `runtime/src/engine/core/font/canvas_text.rs` の `append_item` は
  「本体が完全透明でも、影が見えるなら描く」（`draw_body` と `draw_shadow` が独立）設計で、
  影のアルファは `shadow_color` だけで決まる。そのため<b>文字をアルファ 0 で隠しても影だけが残る</b>
  （リザルトの「New Record!!」で実際に発生。2026-09-08 にアクタの `Visible` で隠す方式へ変えて回避済み）。
  影のアルファへ文字色のアルファを掛ける（`shadow_color[3] * color[3]`）ほうがフェード演出とは
  素直に噛み合うが、<b>影だけを見せる</b>表現ができなくなる非互換変更で、影付きテキスト全部に影響する。
  変えるかどうかは要判断。当面は「アルファではなく `Visible` で隠す」を UI 側の作法とする。
- [ ] **HIT バナーのレベル配色が実機スクリーンショット未確認** — 2026-09-08。
  `HitBanner` は Lv 文字の RGB を `LateUpdate` で毎フレーム上書きする
  （アニメーションクリップの色トラックが RGBA を丸ごと書くため。フレーム順
  「`update_animations` → スクリプトの `LateUpdate`」は `frame_renderer.rs` で確認済み）。
  ただしヒットを起こすデバッグコマンドが無く、ヘッドレスでの絵の確認ができていない。
  `hit_test`（前アタリを飛ばしてヒット状態へ入る）を足すと、この手の確認が 1 手で済むようになる。

## UI 演出の 2D パーティクル（2026-09-08 実装時）

正典: `docs/ui_effects.md`（どの演出のエミッタがどこにあるか）。

- [ ] **粒のテクスチャが白い四角しかない** — 2026-09-08。`mainGame/textures/ui/` に星・円・キラの素材が無いため、
  すべての UI 粒が `white.png`（白い四角）＋加算ブレンドで出ている。小さい粒なので破綻はしないが、
  星形や十字のキラ素材を 1 枚足して `texture_paths` を差し替えると印象がはっきり上がる
  （`texture_paths` は複数枚を並べると粒ごとにランダム選択される）。
- [ ] **HIT バナーの火花だけ実行時 `Instantiate` に依存している** — 2026-09-08。演出アイテムが
  `MainGame.scene` 上のアクタで、AI はシーンを触らない約束のため、プレハブへ子を足す方式が取れなかった。
  利用者がシーンを触れるなら、`FishingUI/HitBannerItems` の下へ `HitSparkle2D` を 2 体置いて
  `HitBanner` から参照させるほうが素直（生成コードとインスペクタ設定「火花の位置基準アクタ名」が不要になる）。
- [ ] **`MainGame.scene` の `ResultPanel` がプレハブと切れており、粒が届かない** — 2026-09-08（実測）。
  シーン上の `ResultPanel` は `prefab_source` を持たない古い複製で、プレハブ側の `Prompt` の子すら無い。
  そのためプレハブへ足した `NewRecordSparkle` / `RegisteredConfetti` は<b>ゲーム中には出ない</b>
  （ヘッドレスで確認済み。プレハブを生成させた一時シーンでは出る）。
  対策は「シーンの `ResultPanel` アクタを削除して生成フォールバックに任せる」か
  「プレハブのインスタンスで置き直す」のどちらか。**シーン編集なので要利用者判断**。
- [ ] **`ParticleEmitter.Tint` はアルファを無視する** — 2026-09-08（仕様）。色カーブ（HSVA）の
  H/S/V だけを定数へ塗り替え、アルファのカーブ（＝消え方）は残す設計にした。
  「実行時に半透明にしたい」という要求が出たら、アルファカーブへ倍率を掛ける別 API
  （`TintAlpha` など）を足すこと。`Tint` に倍率の意味を足すと、繰り返し代入で減衰していくので危険。

## ヒエラルキー選択まわり（2026-09-09 調査時）

- [ ] **`seed_select` / AI ホストの ACTOR_COMPONENTS 待ちが応答 id を相関していない** — 2026-09-09（実測）。
  `IEditorAiHost.SelectActorAsync` は「次に届いた ACTOR_COMPONENTS」をそのまま返すため、
  直前の問い合わせの応答が先に届くと**別アクターの構成が返る**。
  ヘッドレスで 135 ノードを連続 `seed_select` した実測: 間隔なしで 71/135、
  120 ms 間隔でも 2〜3/135 が「1 つ前のノードの応答」だった
  （`components.id` が要求 id と違うことで判定できる）。
  インスペクタ本体は `incomingId != _currentActorId` で弾いているので画面は無事。
  直し方は「要求した DFS ID と一致する応答だけを受理する」フィルタを待ち受け側に足す。
- [ ] **スクリプトのシーン遷移でエディタの「現在シーンパス」が更新されない** — 2026-09-09。
  `apply_script_transition_scene` は `HIERARCHY_RESET` + ヒエラルキーは送るようにしたが、
  `SCENE_LOADED:` は送っていない（Play 中にエディタのシーンパスを書き換えると、
  Stop 後に戻す経路が無く保存先の判断が壊れるため、意図的に見送った）。
  上書き保存はランタイム側の `path_mismatch` 保護があるので事故にはならないが、
  「Play 中はタイトルバーが遷移前シーンのまま」という表示上のズレは残っている。
- [ ] **ヘッドレスで `game_input_*` からタイトルのシーン遷移を起こせない** — 2026-09-09。
  `title.scene` を Play して Enter / 左クリックを注入しても `TitleFlow` が反応せず、
  遷移（title → prologue → mainGame）をヘッドレスで再現できなかった。
  シーン遷移まわりの回帰確認を自動化するには、遷移を直接起こすデバッグ手段
  （`seed_send_ipc` 相当のスクリプトコマンド、または SceneFlow のデバッグフラグ）が要る。

## パッケージ化（2026-09-09 の収録ルール改修時）

正典: `docs/packaging.md`（収録規則・設定項目・ログの読み方・ドライラン）。

- [x] **パッケージ版ではユーザースクリプト（.cs）が一切コンパイルされない** — 2026-09-09 記載 / 同日対応。
  対策案①（事前コンパイル）を採用。パッケージ化時に `ScriptAssemblyManager.CompileToFile` で
  アセット配下の全 `.cs` を `SEEDUserScripts.dll` へまとめ、スクリプトホスト一式とともに
  出力フォルダへ同梱するようにした（`editor/src/Packaging/Scripts/ScriptPackager.cs`）。
  ランタイムは `--assets-root` が無いとき exe の隣の `SEEDUserScripts.dll` を読む
  （`ScriptingHost::load_precompiled_scripts`）。`.scene` のパスから型を引くための対応表は
  DLL のマニフェストリソースへ埋め込む。実機確認済み: 40 型ロード・`Script type not found` 0 件。
  詳細は `docs/packaging.md` §5。
- [x] **配布には対象マシンに .NET 9 ランタイムのインストールが必要（self-contained 未対応）** — 2026-09-09 対応。
  案①（.NET 一式の同梱）を実装。ビルドマシンにインストール済みの .NET を
  `{出力}/dotnet/{host/fxr/<ver>/hostfxr.dll, shared/Microsoft.NETCore.App/<ver>/*}` へ写し、
  ランタイムは exe の隣に `dotnet/host/fxr` があれば
  `nethost::load_hostfxr_with_dotnet_root` でそちらを使う（無ければ従来の探索）。
  同梱バージョンは `SEEDScripting.runtimeconfig.json` の framework version から決める
  （9.0 をハードコードしない）。既定 ON・実測 187 ファイル / 74.3 MB。
  実機確認: hostfxr / hostpolicy / coreclr の 3 つとも同梱 `dotnet/` 配下からロードされ、
  `precompiled scripts loaded: 40 type(s)`。`dotnet/` を外すと `dotnet root: global` で従来どおり動く。
  案②（案内ダイアログ）は 2026-09-10 に**実装済み**（下の「起動ログと panic 通知」を参照）。
  panic 時に `MessageBoxW` で「起動に失敗しました＋ログのパス」を出す。
  なお CLR 初期化失敗（`dotnet/` 欠落など）は panic ではなく続行なのでダイアログは出ず、
  従来どおり原因と対処を stderr（＝起動ログ）へ出す（`App::new`）。
  関連: `editor/src/Packaging/Runtime/DotnetRuntimeBundler.cs`、
  `runtime/src/engine/core/scripting/mod.rs`、`docs/packaging.md` §5。
- [x] **パッケージ版は起動ログが一切残らず、起動失敗が「何も起きない」にしか見えない** — 2026-09-10 記載 / 同日対応。
  リリースビルドは `windows_subsystem = "windows"` でコンソールを持たないため、
  ランタイムの `eprintln!` も C# の `Console.Error` も捨てられていた。
  パッケージ実行（`--assets-root=` / `--pipe=` が無く exe の隣に `assets.pak` がある）のときだけ
  `CreateFileW` + `SetStdHandle` で標準出力／標準エラーを
  `{exe と同じフォルダ}\logs\seed_YYYYMMDD_HHMMSS.log` へ差し替える機構を追加
  （`runtime/src/engine/core/startup_log/`）。exe の隣に書けないときは `%LOCALAPPDATA%\{exe名}\logs`。
  最新 10 件だけ残す。ログ先頭に環境情報（実行ファイル・OS・exe フォルダの中身一覧）を 1 回記録し、
  panic はメッセージ・位置・バックトレースをログに残したうえで `MessageBoxW` で案内する。
  エディタ実行では標準ハンドルに触れない（Output パネルへの出力は従来どおり・ログファイルも作らない）。
  実機確認済み: Rust の `[SEED]` 系と C# の `[SEEDScripting] loaded 40 precompiled …` が同じ 1 本に入ること、
  panic ダイアログが出てログにバックトレースが残ること、エディタ経路でログが作られないこと、
  10 件で世代管理されること。詳細は `docs/packaging.md` §9。
- [x] **配布物の exe の隣に DLL が数十個散らかる／どれが実行時生成物か分からない** — 2026-09-10 記載 / 同日対応。
  配布フォルダの構成を「`{ゲーム名}.exe` / `assets.pak` / `bin/`（副次ファイル）/
  `caches`・`logs`・`saved`（実行時生成）」に一元化した。
  フォルダ名の正典を 2 か所（`runtime/src/engine/core/package_layout.rs` と
  `editor/src/Packaging/PackageLayout.cs`）に置き、両側のテストで文字列を突き合わせている。
  スクリプトホスト／事前コンパイル DLL／同梱 .NET は `bin/`（`bin/dotnet/`）へ、
  セーブは `saved/`、モデル派生キャッシュと `pipeline_cache.bin` は `caches/` へ移した。
  `caches`・`logs`・`saved` は**パッケージ化では作らない**（空フォルダは zip で落ちるため、
  必要になった時点でランタイムが作る）。同じフォルダへ再パッケージすると旧配置の残骸が
  直下に残るため、ビルド開始時に `PackageLayout.RemoveLegacyLayout` で掃除する
  （利用者データの `caches`・`logs`・`saved`・`save` は対象外）。
  実機確認済み: 手組みした新レイアウトで `dotnet root: bundled …\bin\dotnet` /
  `precompiled scripts loaded: 40 type(s)` / 終了時に `caches/pipeline_cache.bin` 生成 /
  直下に DLL ゼロ、エディタ経路は `user scripts compiled: 40` と `runtime/cache` のまま。
  詳細は `docs/packaging.md` §1「出力フォルダの構成」。
  **非互換の注意**: 旧配布物のセーブは `{exe}/save/save.json` にあり、新しい `{exe}/saved/` は
  それを読まない（移行処理は入れていない）。未リリースなので実害は無いが、
  既存インストールへ上書き配布する運用を始める前に移行の要否を判断すること。
- [x] **配布物が VC++ 再頒布可能パッケージ未導入の PC で起動しない** — 2026-09-10 記載 / 同日対応。
  `SEED.exe` が `VCRUNTIME140.dll` / `VCRUNTIME140_1.dll` を import しており、
  未導入の PC では**ダブルクリックしても何も起きない**（プロセス生成前にローダーが失敗するため、
  起動ログ機構すら動かず原因が残らない）。`runtime/.cargo/config.toml` に
  `[target.x86_64-pc-windows-msvc] rustflags = ["-C", "target-feature=+crt-static"]` を追加して
  C ランタイムを静的リンクした。debug / release とも追加のリンクエラーなくビルドでき、
  `dumpbin /dependents` の依存は Windows 標準 DLL のみになった
  （`VCRUNTIME140*` と `api-ms-win-crt-*` が消滅）。代償は exe サイズ増（28.5 MB → 28.9 MB）と、
  CRT のセキュリティ修正を受けるには SEED 自体の再ビルド・再配布が要ること。
- [ ] **PAK 実行ではモデルの派生キャッシュ（`*.smdl`）が一切効かない** — 2026-09-10 発見（今回のレイアウト移行時）。
  `asset_cache::try_load_model` / `store_model` はどちらも先頭で `source_stamp`
  （元ファイルの mtime + サイズ）を取るが、PAK モードのアセットルートは
  `{exe}/assets`（実在しないフォルダ）なので `std::fs::metadata` が必ず失敗し、
  読み込みも書き出しも即 return する（エラーログも出ないので気付きにくい）。
  結果、配布版は毎回モデルをパース＋テクスチャ変換し直している（起動が遅いだけで動作はする）。
  実測: `caches/` に生成されるのは `pipeline_cache.bin` だけで `*.smdl` は 0 件。
  直すなら検証子を「mtime + サイズ」から **PAK エントリのサイズ + PAK 自体の mtime**、
  あるいは PAK ビルド時に焼くコンテンツハッシュへ替える必要がある。
  関連: `runtime/src/engine/core/loader/asset_cache.rs`、`runtime/src/engine/asset_fs.rs`。
- [ ] **panic のバックトレースが配布版では `<unknown>` になる（PDB を同梱していない）** — 2026-09-10。
  上の起動ログでバックトレース自体は出るようになったが、パッケージ化は `SEED.exe` しかコピーしないため
  `SEED.pdb`（release でも 10 MB 生成される）が配布先に無く、フレームがすべて `<unknown>` になる。
  panic の**メッセージと発生位置（file:line:col）は出る**ので一次切り分けには足りるが、
  スタックまで欲しい場合は (a) PDB を同梱する（配布サイズ +10 MB・ソースパスが露出）か、
  (b) PDB をビルド側で保管してアドレスから後付けで解決する、のどちらかが要る。
  関連: `editor/src/Packaging/PackagingWindow.xaml.cs`（バイナリのコピー）。
- [ ] **同じ秒に 2 つ目のインスタンスを起動するとログが作られない** — 2026-09-10。
  ログファイル名は秒単位（`seed_YYYYMMDD_HHMMSS.log`）で、ログ混線を防ぐため
  書き込み共有を許していない（`FILE_SHARE_READ | FILE_SHARE_DELETE`）。
  そのため同一秒に起動した 2 つ目はファイルを開けず、**ログ無しで起動する**（ゲームは動く）。
  多重起動が実際に問題になるなら、ファイル名に連番か PID を足すのが素直。
  関連: `runtime/src/engine/core/startup_log/redirect.rs`、`log_path.rs`。
- [ ] **.NET 同梱は Windows 専用** — 2026-09-09。
  `DotnetRuntimeBundler` は `hostfxr.dll` という Windows のファイル名しか見ないため、
  macOS / Linux 向けパッケージでは同梱がスキップされる（`libhostfxr.dylib` / `.so` 未対応）。
  そもそも現状これらのプラットフォームはこのマシンからビルドできないので実害は無い。
- [ ] **`scripting::debug_command` の単体テストが並列実行だと落ちる** — 2026-09-09 発見（今回の変更とは無関係）。
  `pushes_and_peeks_in_order` と同モジュールの別テストがグローバルの待ち行列を共有しており、
  `cargo test scripting::` だと相互に干渉する（`--test-threads=1` なら全通過）。
  テスト側で行列を分離するか、テストごとにクリアする仕組みが要る。
  関連: `runtime/src/engine/core/scripting/debug_command.rs`。
- [ ] **`.scene` / `.actor` から参照される `.cs` は今も PAK に入る（ソースが配布物に残る）** — 2026-09-09。
  常時同梱の既定は空にしたが、`ScriptComponent` の `type_name` が `.cs` のパスなので
  参照グラフの閉包に乗る（実測 34 ファイル / 1.4 MB）。動作には影響しない。
  完全に外すには「参照されていても入れない拡張子」（`NeverIncludedRelativePaths` の拡張子版）が要る。
  ただし `.cs` を落とすと欠落参照の検出（シーンが消えたスクリプトを指している）も効かなくなるため、
  「収録はしないが参照検証はする」扱いが必要。関連: `editor/src/Packaging/Collect/AssetCollector.cs`。
- [x] **`asset_fs` を通らない `std::fs` 直読みが残っており PAK モードで既定値になる** — 2026-09-09 記載 / 同日対応。
  `app_init.rs` の `project_settings.json` 読み込み（ウィンドウサイズ・プラグイン有効化リスト）を `asset_fs::read_string` 経由に変更し、
  `init_asset_fs` を `handle_resumed` の先頭（ウィンドウ生成前）へ前倒しした。最小 PAK での実機確認で 1280x720 を確認済み。
  パッケージ実行時のプラグインは未対応のまま（`is_packaged()` なら 0 件で続行し 1 行ログ）。DLL 同梱の仕組みが要る。
  関連: `runtime/src/engine/core/app_base/app/app_init.rs`（`parse_window_size` テスト 5 件）。
- [ ] **`cargo test` の `set_save_int_writes_flag_and_keeps_other_keys` が全体実行では落ちる** — 2026-09-09（別作業中に観測、原因未調査）。
  単体（`cargo test -- <テスト名>`）では通るが、`cargo test` の全体実行（`--test-threads=1` でも）で失敗する。
  テスト冒頭のコメントどおり「ストアの初回アクセスより前」に `SAVE_DIR_ENV` を設定する必要があるのに、
  同一プロセス内の他テストが先にセーブストアへ触れてパスを確定させてしまうためと見られる。
  セーブ経路をテスト用に注入可能にするか、このテストを別バイナリへ分けるのが素直。
  <b>今回の変更（スクリプト事前コンパイル）とは無関係で、新規テストを除外しても再現する</b>。
  関連: `runtime/src/engine/plugin/host.rs:164`。
- [ ] **ボクセル地形 `.tvox` が空／一様チャンクでも非圧縮のまま保存される（1 チャンク約 0.5 MB）** — 2026-09-09（容量調査で観察、未検証）。
  `terrain/NewScene`（769 個・344 MB）と `templates/terrain/Untitled`（1,087 個・342 MB）は同一サイズ（467,213 バイト）のファイルが大量にあり、
  中身が空または一様なチャンクをそのまま書き出している可能性が高い。空チャンク省略や RLE 等の圧縮で元データを 1/10 以下にできそう。
  なお `terrain/NewScene` はどのシーンからも参照されていない（削除可否は利用者判断）。関連: `runtime/src/engine/core/app_base/app/terrain_ops.rs`。
- [ ] **`KamomeManager.cs` の既定プレハブパスが実在しない** — 2026-09-09（収録ドライランで検出）。
  `mainGame/scripts/KamomeManager.cs:63` の `kamomePrefabPath` 既定値が
  `"assets://actors/kamome.actor"` だが、その実体は無い（正しくは `mainGame/actors/` 配下のはず）。
  インスペクタで上書きされていれば動くが、既定値のままのインスタンスがあると生成に失敗する。
  <b>アセット・スクリプトの内容変更なので要利用者判断</b>。
- [ ] **`project_settings.json` に実体の無い `demo_*` シーンが 3 件登録されたまま** — 2026-09-09。
  `demo/scenes/demo_title.scene` / `demo_game.scene` / `demo_result.scene` は実体が無い。
  パッケージ化のたびに警告が出る。登録を消すのが素直（シーン設定なので要利用者判断）。
- [ ] **`mainGame/terrain/Island/chunk_3_1_3.tvox` がシーンから参照されていない** — 2026-09-09。
  ディスクには 48 チャンクあるが `MainGame.scene` の `TerrainChunkComponent` は 47 個。
  地形の読み込みはシーンのコンポーネント一覧だけを見る（`terrain_ops.rs` の `walk`）ため、
  このチャンクはエディタでもゲームでも読まれておらず、収録もされない。
  地形の一部が欠けているのか、単なる残骸なのかは要確認。
- [ ] **竿先の近く（成立距離 4.0m 以内）でヒットすると、やり取り無しで釣り上がる経路が残っている** — 2026-09-10 記載 / 同日にキャスト側だけ対応。
  釣り上げの成立条件は距離だけ（`FishingController.UpdateFight`: `CurrentFloatDistance() <= catchDistanceMeters`）で、
  魚 HP は見ていない（2026-09-09 改定）。`catchDistanceMeters` を 4.0m にしたことで、
  4m 以内でヒットが成立すると<b>次のフレームにやり取り無しで釣り上がる</b>。
  <b>対応済み</b>: Inspector の「最短飛距離(m)」（`minCastDistance`）を廃止し、
  最短飛距離を「成立距離 ＋ 最短飛距離の余裕（既定 5.0m）」＝ 既定 9.0m の算出値に一本化した
  （`EffectiveMinCastDistance`）。＝ どんなに弱く投げても成立距離の外に着水する。
  <b>残っている経路</b>: 未ヒットの巻き取り中（`reelEndDistance = 1.5m` まで寄る）に
  アタリ→合わせが成立した場合は、掛かった瞬間の距離が 4m 以内になり得る。
  対処するなら「掛かった直後の数拍は成立判定を止める」等が要る（要利用者判断）。
  なお `MainGame.scene` には旧 `minCastDistance`（10.000）の保存値が残るが、読まれないので実害は無い
  （実効値は 9.0m へ変わる）。
  関連: `<project>/assets/mainGame/scripts/FishingController.cs`（`catchDistanceMeters` / `minCastMarginBeyondCatch` / `UpdateFight`）。
- [ ] **「魚回復」の走り（Run）は世界端クランプで頭打ちになり、予約が捨てられる経路がある** — 2026-09-10（回復の予約化に伴う既知の制限）。
  隙（Rest）中に拾った「魚回復」は<b>その場では効かせず貯めて</b>、隙が終わる瞬間にまとめて魚 HP へ入れ、
  続く走り（`FishingFight.Phase.Run`）で「新しい魚 HP に対応する目標距離」まで沖へ走る仕様にした。
  魚 HP に上限クランプは無いので、たくさん拾えば最大値の 100% を超え、目標距離もそのぶん線形に伸びる。
  ただしウキが実際に出られるのは `FishingController` の世界端クランプ
  （`maxCastDistance` ＋ `floatDragMarginDistance`）までなので、
  <b>目標距離がそれを超えると走る距離が頭打ちになる</b>（HP だけが増えて画に出ない）。
  また、<b>肉を拾った隙の中で魚 HP を削り切った</b>場合は `Tick` がフェーズ遷移ごと止まるため
  予約が適用されないまま釣り上がる（プレイヤーに不利にはならないので現状は許容）。
  数値バランスを詰めるときは「肉 1 個の効果量 × 1HP あたりの距離」と最長飛距離の関係を見ること。
  関連: `<project>/assets/mainGame/scripts/FishingFight.cs`（`RecoverFishHp` / `CommitPendingFishHpRecovery` / `ComputeFloatDistanceStep`）。
- [ ] **わらしべ連鎖の途中で食べられた魚は `LastCaughtFish` とチュートリアル判定に乗らない** — 2026-09-10（連鎖リザルト対応で確認・未着手）。
  釣り上げ時のリザルト表示と図鑑登録は連鎖の全匹ぶん行うようにしたが、
  `FishingEvents.Catch` / `CatchPresented` は従来どおり「1 回の釣り上げにつき 1 回」のままで、
  引数も `FishingController.LastCaughtFish` も**最後の 1 匹**しか運ばない。
  そのため `Tutorial/Missions/CatchTargetMission.cs`（狙った魚種を釣ったか）と
  `Story/KaijuStoryTrigger.cs` は、連鎖の踏み台になった魚を「釣った」とは数えない。
  イベントの粒度を上げると上記 2 つの購読側（単一フラグ運用）が壊れるため、
  必要になったら連鎖用の別イベント（例 `fishing.chain_catch`）を足すのが素直。
  関連: `<project>/assets/mainGame/scripts/CatchPresenter.cs`、`FishingController.cs`（`ChainCatchHistory`）。
- [ ] **docs/packaging.md §8.1「再パッケージせずに JSON を直接書き換えても効く」が今の読み順と合わない** — 2026-09-24
  （APK 内 pak の作業中に気付いた・未対応）。`project_settings.json` は収集の起点として必ず PAK に入り（§2）、
  `asset_fs::read_bytes` は PAK を優先する（PAK に無いときだけ実行ファイルの隣の `assets/` を読む）ため、配布先で
  `assets/project_settings.json` を置いても PAK の中身が勝って効かない。記述を直すか、`project_settings.json` だけ
  「PAK の外にあればそちらを優先」にするかを決める（後者なら Android の APK 内 `assets/seed/assets/` も同じ仕組みで効く）。

## 釣果リザルトの魚画像アニメ（2026-09-10 Animator 化時）

正典: `<project>/assets/mainGame/scripts/ResultPanel.cs`、
クリップ生成は `tools/gen_result_fish_clips.py`
（`mainGame/animations/result_fish_in.anim` / `result_fish_out.anim`）。
実行時生成の重ねスプライト方式（`ResultFishOverlay.actor` ＋ `SpawnOnce`）は撤去し、
FishImage 自身の `Animator` がクリップで倍率を動かす方式へ置き換えた。

- [ ] **シーン上の `ResultPanel/ResultBody/FishImage` のスケールが `[0, 0]` のまま保存されている** — 2026-09-10。
  クリップ作成中の値がそのまま残っている（`ResultBody` の同種の残件は 2026-09-08 の項を参照）。
  ゲーム中は登場クリップが必ず倍率を書くので<b>見た目には影響しない</b>が、
  エディタの編集画面ではリザルトの魚の絵が見えない。インスペクタで 1 に戻して保存するか、
  プレハブを再適用すること（シーン編集なので要利用者判断）。
- [ ] **`mainGame/animations/resultFishImage.anim` が未参照のまま残っている** — 2026-09-10。
  Animator 化の前に作られた仮クリップ（`new_clip`・位置と回転を動かさないキーだけ）で、
  シーンの `clips` からは外したのでどこからも読まれない。消すかどうかは要利用者判断。
- [ ] **ポーズ中に「表示保持」が進み、絵の出入りだけが止まる** — 2026-09-10（軽微・仕様として許容）。
  パネルの待ち時間は実時間（`Time.Unscaled*`）で数えるが、`Animator` はゲーム時間で進む
  （`PauseMenu` は `Time.Scale = 0`）。そのため保持中にポーズすると、
  ポーズ中に退場クリップの再生要求だけが出て、絵は等倍のまま固まる（解除で動き出す）。
  パネルの待ちもゲーム時間へ寄せると直るが、スロー中に開いた場合の秒数が変わるため据え置く。

## ビートバトル連打の重さ調査で見つかった残件（2026-09-10）

「ビートバトル中に速い譜面で連打すると重くなる」の実測調査で判明したもの。
本題（SE を毎回デコードし直していた件）は `runtime/src/engine/core/audio/mod.rs` の
PCM キャッシュ化＋同時発音数の上限で解決済み。以下はその過程で見つかった**別件**。

- [ ] **`地形/LOD 再メッシュ` が単発 70〜140 ms のスパイクを出す【フレーム最大値の最大の犯人】** — 2026-09-10。
  MainGame を Play しているあいだ、クリックの有無に関係なく `frame_max_ms` が常時 90〜140 ms に達し、
  その内訳はほぼ `地形/LOD 再メッシュ`（`self_ms` の窓内最大が 70〜93 ms）だった。
  カメラ・ウキが動くほど頻度が上がるので、釣りのやり取り中はとくに目立つ。
  体感としては「マウス入力にラグ」「たまにガクッと止まる」として出るはずで、
  オーディオ側を直した後もこれは残っている（＝連打が原因ではない、前からある別問題）。
  分割適用（1 フレームあたりのチャンク数を制限する）か、GPU 転送だけを非同期にするのが素直。
  計測手順は docs/editor_mcp.md 5.4（`seed_profile`）。関連: 地形 LOD の再メッシュ適用箇所。
- [ ] **回答クリック SE `Motion-Swish07-1.mp3` が 1.75 秒もある** — 2026-09-10（アセット側の課題）。
  実測で 44.1kHz ステレオ 154,368 サンプル＝**1.750 秒**。連打すると 1 つの音が鳴り終わる前に
  次が始まるので、同じ波形が何重にも重なる（＝振幅が足し合わさって歪む）。
  ランタイム側は同一 SE の同時発音を 4 声に制限したが、**素材自体を短く（末尾の無音を落として
  0.2〜0.3 秒程度に）トリミングすれば重なり自体が起きなくなる**。
  他の SE も同様に「無音の尻尾」が付いていないか確認するとよい。
  関連: `<project>/assets/mainGame/audios/`、`FishingFight.answerClickSePath`。
- [ ] **`engine::plugin::host::tests::set_save_int_writes_flag_and_keeps_other_keys` がスイート全体では落ちる** — 2026-09-10（既存不具合・オーディオ改修とは無関係）。
  単体（`cargo test --bin SEED set_save_int_writes_flag_and_keeps_other_keys`）では通るが、
  `cargo test --bin SEED` では `save.json を読めない` で落ちる。`--test-threads=1` でも落ちる。
  原因はセーブ先パスがプロセス共有（`OnceLock`）で、先に走った別テストが確定させてしまうため。
  テスト冒頭の `SEED_SAVE_DIR` 設定が効かない。保存先の解決をテストから差し替えられる形にするか、
  このテストを別プロセス（`#[ignore]` ＋ 専用実行）へ隔離するのが素直。
  関連: `runtime/src/engine/plugin/host.rs` の `mod tests`、`runtime/src/engine/core/save/path.rs`。
- [ ] **`runtime/Cargo.lock` が実態を反映していない「死んだ」ファイル** — 2026-09-10。
  ワークスペース構成のため実際に使われるのはリポジトリ直下の `Cargo.lock` で、
  `runtime/Cargo.lock` には rodio / gilrs / rapier など現行の依存が丸ごと欠けている。
  バージョン調査の際にこちらを読むと誤る。消してよいか要判断。
- [ ] **`<何か>/target/debug` 以外の場所へビルドしたランタイムを `SEED_RUNTIME_EXE` で起動すると、標準出力がエディタログに出ない** — 2026-09-10（未確定・再現条件のみ記録）。
  計測のため `--target-dir` を `tmp/rt_target` にしてビルドし、そのランタイムをヘッドレスエディタから
  起動したところ、`[STDERR]` は出るのに `[STDOUT]`（＝`SEED.Debug.Log` の `[Script]` 行）が
  ログに 1 行も出ず、ゲームの状態確認ができなかった。通常の `runtime/target/debug/SEED.exe` では出る。
  併せてエディタのアセットルート判定も `<exe の 2 階層上>/assets` になるため（`ResolveAssetsPath` は
  親ディレクトリ名が `target` かどうかを見る）、AI の計測用に別出力でビルドする運用は現状かなり脆い。
  docs/editor_mcp.md 5.5 の手順を使うときの注意点として、原因を特定して直すか手順に注記したい。

## 釣りの調整 5 件（2026-09-11）

糸の減り 2 倍／漂流物の固定順化／レーダー星型化／魚回復の距離頭打ち修正／
巻き取り中カメラ距離 1.5 倍、の 5 件を入れたときに判明したもの。

**先に解消したもの**: 上の「**「魚回復」の走り（Run）は世界端クランプで頭打ちになり、予約が捨てられる経路がある**」
（2026-09-10）のうち、<b>前半（世界端クランプで走る距離が頭打ちになる）は本日の修正で解消済み</b>。
`FishingController.UpdateReeling` の上限を `maxCastDistance + floatDragMarginDistance` の固定値から
`max(FishingFight.DesiredFloatDistance, maxCastDistance) + floatDragMarginDistance` へ変えた。
同項の後半（肉を拾った隙の中で魚 HP を削り切ると予約が適用されないまま釣り上がる）は<b>未対応のまま残る</b>。

- [ ] **`catchDistanceMeters` のシーン上書き（10.0）がコード既定（4.0）を潰している** — 2026-09-11（今回の調査で発見・未対応）。
  `FishingController.cs` の宣言は `catchDistanceMeters = 4.0f` だが、`MainGame.scene` の
  `Player|Fishing` に `"catchDistanceMeters": "10.000"` の保存値が残っているため、
  実機で効いているのは<b>10 m</b>。直近のコミット「釣り上げ成立距離 4 m」の意図が反映されていない。
  シーン側の保存値を 4.000 にするか消すかは要利用者判断（シーン編集のため今回は触っていない）。
- [ ] **岸際カメラ（`shoreCamDistance`）が実戦で一度も発動しない** — 2026-09-11（上の項の副作用・未対応）。
  `MainGame.scene` は `catchDistanceMeters` も `nearShoreDistanceMeters` も 10.000 で同値。
  `FishState.Hooked` の毎フレーム処理は `UpdateFight`（釣り上げ判定）→ `UpdateReeling`（`UpdateNearShore`）
  → `UpdateShoreCamera` の順で、距離が 10 m 以下になった瞬間に `UpdateFight` が先に
  `FinishReeling()` して `Hooked` を抜けるため、`NearShore` が `Hooked` のまま true になる隙が無い。
  結果 `UpdateShoreCamera` の `wantsShoreView` が真にならず、`shoreCamDistance`（既定 7 / シーン 15）は
  読まれない。上の `catchDistanceMeters` を 4 m へ直せば自然に生き返る。
  関連: `<project>/assets/mainGame/scripts/FishingController.cs`（`UpdateNearShore` / `UpdateShoreCamera`）。
- [ ] **`MainGame.scene` に漂流物の旧「抽選の重み」の保存値が残っている** — 2026-09-11（実害なし）。
  種類の決定を固定順（`DriftItemManager.spawnOrder`）へ変えたので
  `stunWeight` / `fishRecoverWeight` / `lineRecoverWeight` は削除済みだが、
  シーンには 3 つとも `"1.000"` が残る。読まれないので害は無い。掃除するかは要利用者判断。
- [ ] **糸ゲージ・カメラ・走る距離の各変更が実機未検証** — 2026-09-11。
  今回の 5 件はスクリプトのコンパイル（`ScriptPrecompileTests`）と机上計算だけで確認しており、
  Play での目視をしていない。とくに次の 3 点は実機で見ること。
  (1) 糸の減り 2 倍（`linePerSecondOfOffset` 0.6 / `missLoss` 0.12）で難度が上がりすぎていないか。
  (2) 巻き取り中のカメラ距離 1.5 倍（`FishingController.restCameraDistanceScale`）で、
      出題（0.5 倍）↔ 巻き取り（1.5 倍）の切り替わりが飛んで見えないか。
  (3) 魚回復を複数拾ったときにウキが出る距離（HP 超過ぶんの外挿）で、
      糸のたるみ（`UpdateLine` の `slack` は<b>キャスト時点</b>の飛距離から決まり、
      引かれて伸びても更新されない）が不自然に張って見えないか。
- [ ] **ランク A / S の魚は以前からヒット直後の走りが切り詰められていた** — 2026-09-11（今回の修正で解消・要目視）。
  `HP100% の距離 ＝ 掛かった距離 ＋ ヒット直後の引き距離`（引き距離は既定 30 m × ランク倍率
  S:1.5 / A:1.25）なので、最長飛距離付近で A・S を掛けると 85〜100 m になり、
  旧クランプ（40 ＋ 30 ＝ 70 m）に当たって<b>魚回復を拾う前から</b>頭打ちだった。
  本日の修正でこの切り詰めも消えるため、A・S の魚は以前より遠くまで走る。
  演出として遠すぎないかは実機で確認すること（遠すぎるなら `hookRunDistanceDefault` か
  ランク倍率を下げる。海面は半径 1000 m あるので世界の外へ出る心配は無い）。

## 怪獣ストーリー会話の吹き出し（2026-09-11）

「Lv10 カイジュウを釣り上げたあとの会話で吹き出しが出ない（文字送り SE だけ鳴る）」の
調査で判明したもの。**本体の不具合は修正済み**（`MainGame.scene` の
`FishingUI/StoryDialogueWindow` の `anchor` を `[0.0, 0.0]` → `[0.5, 1.0]` に変更）。
原因は「CanvasComponent を持たない通常ノード（＝フォルダでもない Actor2D）は、
子へ `NO_ANCHOR_BASIS = [0,0]` を渡すので**子の anchor が無効になる**」という
キャンバスの規則（`app/canvas_collect.rs::child_anchor_basis`）に、
プロローグ（`DialogueWindow` 自身が CanvasComponent を持つ）から複製した
子の anchor `[0.5, 1.0]` がそのまま持ち込まれていたこと。
アンカーが落ちるぶん、吹き出し一式が設計位置より (960, 1080) px 左上＝画面外に出ていた。

- [ ] **`StoryDialogueWindow` の子 5 つに死んだ `anchor [0.5, 1.0]` が残っている** — 2026-09-11（実害なし）。
  `DialogueBalloon` / `DialogueNameplate` / `DialogueSpeakerText` / `DialogueBodyText` /
  `DialogueNextArrow` の `anchor` は上の規則で読まれないが、値としては残っている。
  将来この窓に CanvasComponent を足すと 5 つ同時に飛ぶ。`[0.0, 0.0]` へ揃えておくのが安全。
  同じ形は `TutorialWindow` / `MissionPanel` / `MissionClearBanner` の配下にもある（同様に無効）。
- [ ] **話者名（`DialogueSpeakerText`）が名札の上へはみ出す** — 2026-09-11（未対応・見た目のみ）。
  名札 Sprite と話者名 Text は同じ位置・同じ pivot `[0.5, 0.5]` だが、Text 側は
  `box_width` / `box_height` が 0（＝枠なしレイアウト）のため pivot が効かず、
  名札の中央ではなく上端へ寄る（実測でフォントサイズ 1 つ分ほど上）。
  プロローグ側は話者名を名札の**子**にして `box 184.3×27.6` を入れているのでズレない。
  直すなら Text に名札と同じ枠サイズを入れるか、プロローグと同じ親子関係にする。
- [ ] **吹き出しの下半分が空く** — 2026-09-11（未対応・見た目のみ）。
  吹き出しを 1320×585（元テクスチャの比 3.99:1 に対し 2.26:1）へ縦に伸ばしている一方、
  本文は上寄せ 2 行ぶんしか占めないため、下に 300px 以上の空白が残る。
  吹き出しを縮めるか本文を中央へ寄せるかは要利用者判断。

## 会話カメラの画角補間・会話前姿勢への復帰（2026-09-11）

`DialogueCameraDirector` が位置・回転に加えて**画角(FOV)**も補間するようになり、
`DialogueDirector` に「終了時にカメラを戻す」（既定 ON / Lerp 1.0 秒）が入った。
スクリプト側の実装は完了しているが、シーン側の調整が 2 件残っている
（`MainGame.scene` / `proLogue.scene` に別セッションの未コミット差分があるため未編集）。

- [ ] **プロローグの `DialogueDirector` は「終了時にカメラを戻す」を OFF にする** — 2026-09-11（要シーン編集）。
  既定 ON のままだと、会話終了後にカメラが開幕の姿勢（`MainCamera` の (70, 2.8, -8)）へ
  1 秒かけて戻ってから `PrologueFlow.GoToMainGame` のフェードが始まる。
  プロローグは会話直後にシーン遷移するので、この戻りは不要。
  対象: `proLogue.scene` の `DialogueDirector` アクター（インスペクタのチェックを外すだけ）。
- [ ] **ストーリーカメラの `fov_y_deg` を台詞ごとに詰める** — 2026-09-11（演出調整）。
  `StoryCam_*` / `CamTarget_*` は現在すべて 25（＝ MainCamera と同値）なので、
  画角補間を入れても見た目は変わらない。寄り・引きを画角でも表現したい場合は
  各目標アクターの Camera の「垂直視野角」を変える（そこへ補間される）。

## チュートリアル締め演出のデータ駆動化（2026-09-11）

- [ ] **構造体配列の要素型に入れ子の `[Serializable]` 構造体を持てない** — 2026-09-11。`SEED.ScriptStructArray.TryGetLayout` はメンバがスカラ／参照／1 段配列以外だと false を返し、配列フィールド全体が非対応に落ちる（インスペクタからミッション一覧が消え、実行時にシーンの保存値も注入されない）。`TutorialMission` に `CutsceneSettings` をネストできず、平坦なメンバ 12 個（ラベル「演出:〜」）で実装した。関連: `scripting/src/Api/ScriptStructArray.cs`（`BuildMemberInfo`）。
- [ ] **構造体配列のメンバ行では `[Header]` が描画されない** — 2026-09-11。`ScriptStructElementBuilder` は `BuildValueRow` を直接呼ぶため。ラベル接頭辞で代用。関連: `editor/src/Scripting/ScriptStructElementBuilder.cs`。
- [ ] **ミッション 1 件のインスペクタが 54 行になり、Cutscene 以外でも「演出:〜」が出る** — 2026-09-11。「無関係パラメータは非表示」の方針とずれるが、構造体配列メンバの条件付き表示はエンジン側対応が要る。
- [ ] **締め演出のカメラ目標アクタ指定・画角上書きは実機未確認** — 2026-09-11。コードパスと型の検証まで。関連: `mainGame/scripts/Tutorial/Missions/CutsceneMission.cs`。

## 怪獣の強制呼び出し（KaijuLure / 2026-09-11）

Lv9 の魚が掛かったら（直接ヒット・わらしべ乗り換えのどちらでも）必ず怪獣（Lv10）が
寄ってきて食いつく仕組みを入れた。実装は `mainGame/scripts/KaijuLure.cs`（進行管理）＋
`Fish.BeginLure/EndLure`（振る舞い）＋`FishManager.TryMaterializeByPrefabKey`（実体化）。
コンパイル（`ScriptPrecompileTests.exe`）は通っているが、**実機は未確認**。

- [ ] **実機で「Lv9 ヒット → 怪獣が寄って食いつく」を通しで確認する** — 2026-09-11（未検証）。
  確認したい点: 出現距離 30m・速度倍率 2.0（＝到着まで約 6.7 秒）で、Lv9 のやり取りが
  終わる前に間に合うか。間に合わないなら `FishingController` の「怪獣の出現距離(m)」を縮めるか
  「呼ばれた怪獣の速度倍率」を上げる（どちらもインスペクタ値）。
- [ ] **怪獣の出現が画面に映らないか（湧きの瞬間が見えないか）未確認** — 2026-09-11。
  掛かっている魚から「竿先 → 魚」の延長線上、沖側 30m の水中に実体化する。
  カメラが沖を向いている構図（岸際の巻き）では視界に入る可能性がある。
- [ ] **呼び出し用に実体化した怪獣は常時実体化（Pinned）のままで、呼び出しが不発でも消えない**
  — 2026-09-11（仕様上の割り切り）。`KaijuLure` が「既に実体化している怪獣が居れば使い回す」ため
  増えるのは高々 1 体だが、その 1 体はシーンが続く限り実体として残る
  （最上位レベルの円環へ押し戻されるので、通常は沖で回遊している）。
  気になるようなら `VirtualFish.Pinned` を可変にして、呼び出し終了時に外す仕組みが要る。
- [ ] **使い回す怪獣が遠い場合は出現位置へ瞬間移動させている** — 2026-09-11（未検証）。
  自然に実体化した怪獣は最上位の円環（いちばん沖）に居るため、そのまま泳がせると
  到着が間に合わない。「掛かっている魚から出現距離より遠い個体だけ」を移すので
  通常は画面外のはずだが、実機で瞬間移動が見えないかは未確認。
  関連: `KaijuLure.PullCloser`。
- [ ] **怪獣が寄っている最中に別の格上が横取りした場合の挙動が未検証** — 2026-09-11。
  横取り（`SwapHookedFish` で別の魚へ乗り換え）が起きても `KaijuLure` は解除されず、
  怪獣は新しく掛かった魚を追い続ける（＝そのまま怪獣まで繋がる）想定。
- [ ] **締め演出（FollowNormal / CameraView）の出現位置はミッション開始の 1 フレームでカメラ姿勢を 1 回だけ読む** — 2026-09-11。`end_kaiju` は直前ミッションの台詞カメラが解除された直後に始まるため、通常構図へ戻りきる前の姿勢を拾って跳ぶ場所がずれる可能性がある。実機で確認し、ずれるなら開始を数フレーム遅らせるか基準を TargetOrFloat にする。関連: `mainGame/scripts/Tutorial/Missions/CutsceneMission.cs`。

## 巻き取りの手応え修正・デバッグ HUD の非表示（FishingFight / 2026-09-11）

「魚 HP が高いと巻いてもウキが寄ってこない」を直した。原因は
`ComputeFloatDistanceStep`（隙フェーズ）の引き返し項に
`|目標距離 − 見た目距離| × visibleDistanceReturnRate` という**上限の無いバネ**が
`max` で混ざっていたこと。ズレが `reelVisibleSpeed ÷ visibleDistanceReturnRate`（既定 4m）を
超えると引き返しが巻きの寄せを食い切り、正味の寄り速度が
「巻いた距離 × 巻き効率（＝竿 ÷（竿＋魚力））」まで落ちていた（格上ほど 0 に近い）。
引き返しを `fishPullSpeed × 戦闘力比` の一定速度だけに戻し、巻き入力が続いている間
（`ReelingRecently`）は `reelPullbackScale` 倍（既定 0）へ弱めた。
あわせて状態テキスト（フェーズ名・予告・魚 HP ％）を `showDebugHud` ＋
`SEED.Application.IsDebugAllowed` でゲートし、パッケージ版では出ないようにした。
コンパイル（`ScriptPrecompileTests.exe`）は通っているが、**実機は未確認**。

- [ ] **実機で「巻けば必ず寄る」手触りとバランスを確認する** — 2026-09-11（未検証）。
  机上（式のシミュレーション）では、格上（魚力 9 倍）・HP100% から 4 秒巻いて
  ホイール 4 目/秒で −3.0m、30 目/秒で −23.9m（修正前はそれぞれ −0.75m / −9.0m）。
  寄る速さは「reelVisibleSpeed × 巻き量を消化しているフレームの割合」＝
  実質「ホイールで巻いた距離 × (reelVisibleSpeed ÷ reelInSpeedMax)」
  （MainGame.scene の現設定では 6 ÷ 18 ＝ 1/3）なので、
  速すぎ／遅すぎと感じたら `巻きの見た目寄せ速度(m/秒)`（既定 6）で調整する。
- [ ] **釣り上げ成立が事実上「巻き続けられるか」だけになった** — 2026-09-11（仕様上の割り切り）。
  成立条件はもともと距離のみ（`catchDistanceMeters` 既定 4m）で魚 HP を見ないため、
  巻きが必ず寄せる今は HP を削り切らなくても寄せ切れば釣れる。
  魚 HP の役割は「引き返せる余力（引き返しは目標距離を追い越さない）」に寄った。
  強い魚の歯応えが足りなければ `巻き中の引き返し倍率`（既定 0）を 0.3〜0.5 へ上げるか、
  `魚の引き速度(m/秒)` を上げる（`reelVisibleSpeed > fishPullSpeed ×
  pullRateMultiplierMax × reelPullbackScale` は必ず保つこと）。
- [ ] **漂流物「魚回復（肉）」の走り（Run）が以前より大きく戻す可能性** — 2026-09-11（未検証）。
  走りの行き先は `max(走り始めた距離, DesiredFloatDistance)` の**絶対値**で、
  修正前はバネのおかげで「見た目距離 ≒ 目標距離 − 4m」に保たれていたため戻り幅も
  4m ＋ 回復ぶんで収まっていた。いまは巻くほど見た目距離が目標距離より内側へ入るので、
  肉を拾った瞬間の戻り幅が「そのときのズレ ＋ 回復ぶん」になる（机上で 3 サイクル巻くと
  ズレは 15m 前後）。実機で理不尽に感じるなら、走りの行き先を
  「走り始めた距離 ＋ 回復した HP × metersPerHp」の**相対量**に変えるのが素直。
  関連: `FishingFight.ComputeFloatDistanceStep`（Phase.Run 分岐）・`CommitPendingFishHpRecovery`。
- [ ] **パッケージ版でフェーズ名・魚 HP ％が消えていることの実機確認** — 2026-09-11（未検証）。
  エディタ Play では従来どおり見える（`IsDebugAllowed` が true のため）。
  消えるのは中央テキストのフェーズ名・次フェーズ予告・魚 HP ％だけで、
  余白の開始カウントダウン・右下の残り距離・糸ゲージ・判定バナーは残す設計。
  **開始カウントダウンも消したい場合は `ApplyStatusText` の LeadIn 分岐も
  `ShowDebugHud` でゲートする**（いまは意図的に常時表示にしてある）。

- [ ] **漂流物の種類を糸 HP 連動の抽選にした（実機未確認）** — 2026-09-11。
  `DriftItemManager` の「出現の決め方」既定を `LineHpWeighted` にした。
  確率は `ひるみ = stunRatio(0.34)` 固定／残り 0.66 を
  `糸回復 = (1 − 糸HP) + minRecoverWeight(0.15)` と `魚回復 = 糸HP + 0.15` の重みで分ける。
  机上では 糸HP 0.1 → 糸回復 53% / 魚回復 13%、0.5 → 33% / 33%、1.0 → 糸回復 7.6% / 魚回復 58%。
  **偏りが体感と合わなければ `ひるみの割合` と `回復の最小重み` で調整する**
  （満タン時に糸回復が出る割合は `minRecoverWeight / (1 + minRecoverWeight × 2) × 0.66`）。
  旧来の固定巡回は `FixedOrder` に残してある。

- [ ] **レーダーが空のときの魚の補充（実機未確認）** — 2026-09-11。
  `FishingController.UpdateFishRestock` → `FishManager.TryMaterializeOneNear`。
  ヒット前にレーダー射程内（25m）へ「食いついてくれる魚」が 0 匹のとき、
  射程 × 0.85 の距離へ 1 匹だけ用意する（クールタイム 20 秒）。
  **実体化半径が「自動」（現設定）だと帯の中に仮想個体が残らないため、実際にはほぼ
  「いちばん遠い個体を移動させる」経路が働く**（総数は変わらない）。移動対象は
  レーダー射程の外に居る個体だが、カメラを引いた構図では消失が見えるかもしれない。
  見えるようなら `実体化半径(m)` を有限値にして仮想個体を残すか、移動でなく新規生成に倒す。
  なお「そのままでは食いつかない距離」は比率任せ（21m ≫ 感知 9.5m）で、魚 prefab 側の
  `餌の感知距離` を大きく変えたら `補充位置のレーダー射程比率` も見直すこと。

- [ ] **わらしべ連鎖の上限を「やり取り単位」で持ち越すようにした（実機未確認）** — 2026-09-11。
  チュートリアル「魚で魚を釣ろう」（`chainLimit: 1`）のあと、同じ魚を掛けたまま
  次のミッション（`chainDisabled: false`）へ進むと連鎖が解禁され、2 段目・3 段目が起きていた。
  `FishingController` 側に `chainLimitForCurrentFight`（`TryEatHookedFish` が
  `chainCatchHistory.Count` と突き合わせる）を足して二重化した。
  副作用として、**チュートリアル中に上限を受け取ったやり取りは、チュートリアルが
  終わってもそのやり取りの間だけ上限を守る**（新しく掛け直すか逃げられれば解除）。

- [ ] **`MissionBase.Update` が `IsCleared` で早期 return するため、ミッション側の
  「抑止の張り直し」が効かない構成がある** — 2026-09-11（設計上の穴・未対応）。
  `ChainCatchMission.OnUpdate` の張り直しは `chainLimit == requiredCount`（＝上限に達した
  瞬間に達成する）構成では 1 度も走らない。今回は釣り側の二重化で回避したが、
  同じ作りのミッションを増やすなら `MissionBase` 側に「達成後も走る後始末の枠」が要る。

- [ ] **音声辞書（AudioDictionary）への集約が mainGame だけ** — 2026-09-11（未対応）。
  `mainGame/MainGame.scene` の `AudioDict` アクタへ効果音を集約し、mainGame の
  スクリプト／プレハブは辞書キー参照へ移行した。**未対応は次の 4 つ**。
  いずれも「辞書はシーンごとに要る」ため、対象シーンに `AudioDict` を置くのが先。
  - `common/`（`TypewriterText` のメッセージ音など）… 複数シーンで共有されるうえ、
    `TypewriterText.SePath` が**パスを受け取る API** なので、キー化するには
    common 側の API を変える（またはシーン全体から引ける「キー → パス解決」API を足す）必要がある。
    そのため `mainGame/Tutorial/TutorialWindow.cs` の `messageSePath` も**パスのまま残した**。
  - `zukan/` / `title/` / `prologue/` のスクリプトと `.scene`。
  - `mainGame/actors/BeachAmbient.actor`（＋シーン内インスタンス）… `AmbientLoop` が
    クロスフェードのため **AudioComponent の `Volume` を毎フレーム書き換える**が、
    辞書モードでは音量も辞書から解決されてコンポーネント側の音量が無視される
    （`resolve_audio_component_source`）。**辞書化するとクロスフェードが壊れる**ので
    パス指定のまま残した。辞書化したいなら「音量だけはコンポーネント側を使う」
    抜け道か、フェード専用の再生 API が要る。
  - `mainGame/audios/reel.mp3` はどこからも参照されていない（パッケージにも入らない）。

- [ ] **音声辞書移行後の実機確認（ゲームプレイ中の音）** — 2026-09-11（未検証）。
  ランタイム単体起動（`--mode=play --scene=assets://mainGame/MainGame.scene`）で
  シーンのロード（actors=30）と **`[SEED audio]` 警告 0 件**までは確認済み。
  ただし入力を伴う音（キャスト・アタリ・巻き取りループ・メトロノーム・ドラム・
  判定クリック・足音・漂流物・リザルトの K.O.・ミッションクリア）は**鳴らしていない**。
  キーの一致自体はスクリプトで突合済み（未解決 0 件・未参照 0 件）だが、
  音量バランス（辞書へ移した既定値）は耳で確認すること。
  とくに **漂流物の取得音は仕様が変わった**: 従来は prefab の `pickupSePath`
  （糸回復=水音 / 魚回復=擦れ音 / ひるみ=スタン音）が鳴っていたが、
  今回から `FishingController.ApplyDriftEffect` が種類別の音
  （`kaihuku` / `powerCharge` / `hirumiHit`）を鳴らす。prefab 側は空にしたので
  1 回の取得で鳴るのは 1 音。


---

## プロジェクト概念（.seedproj）— 2026-09-11 導入時の持ち越し

正典: `docs/project_system.md`。導入時に「今はやらない」と判断したもの。

- [ ] **`.seedproj` の `plugins_dir` はランタイムが見ない** — ランタイムは
  アセットルートの親の `"plugins"` 固定でプラグインを探す
  （`runtime/src/engine/core/app_base/app/app_init.rs`）。エディタ側だけが
  `plugins_dir` を尊重するので、既定（`"plugins"`）以外にすると食い違う。
  直すならランタイムへプラグインフォルダを渡す引数が要る。当面は既定固定で運用する。

- [ ] **プロジェクトの切り替えは別プロセス**（「ファイル → 別のプロジェクトを開く...」）。
  同一プロセスでの差し替えは、パネル・RuntimeManager・ランタイム子プロセスが
  起動時のアセットルートを前提に状態を持っているため未対応。
  同一プロセス切り替えをやるなら、全パネルの「プロジェクト変更」再初期化経路が要る。

- [ ] **プロジェクトのテンプレート（初期コンテンツ投入）は未実装**。
  差し込み口だけ用意してある（`ProjectCreator.ProjectCreated` イベント）。

- [ ] **`.seedproj` を関連付けても既定アプリの「開く方法」は変えない**。
  HKCU の ProgID と `shell\open\command` を書くだけで、
  `UserChoice`（利用者が明示的に選んだ既定アプリ）は触らない（OS が署名で保護しており、
  書き換えは行儀が悪い）。既に別アプリを既定に選んでいる場合はそちらが優先される。

## テンプレートライブラリ（2026-09-11 実装時）

- [ ] **ライブラリのシーンが参照するサンプルモデル 4 点の実体が無い** — 2026-09-11。
  `templates/` のシーンは旧ジャンクション時代の絶対パス参照を `assets://` 相対へ正規化済み（554 か所）だが、
  `models/bunny/bunny.obj`・`models/main_sponza/NewSponza_Main_glTF_003.gltf`・
  `models/pkg_a_curtains/NewSponza_Curtains_glTF.gltf`・`models/sampleModels/ABeautifulGame/glTF/ABeautifulGame.gltf`
  はライブラリに実体が無い（インポート画面では欠落参照として表示される）。実体を入れるか参照を消すかを決める。
  関連: `docs/template_library.md` §7。

- [ ] **既存ゲームが `assets/templates/` 配下に残している 5 ファイル** — 2026-09-11。
  フォント 2（`templates/fonts/Digital/*.ttf`）・スカイボックス 1（`templates/skybox/*.hdr`）・砂浜テクスチャ 2
  （`templates/terrain/textures/aerial_beach_01_1k.blend/textures/*.jpg`）は移行時に参照分だけ残した。
  正規の置き場（`mainGame/fonts/` 等）へ移して参照を書き換えるのが本筋。
  関連: `docs/project_system.md`（移行の節）。

- [x] **フォントファイル専用のアイコンが無い** — 2026-09-12 起票 / 2026-09-12 完了。
  `editor/gen_icons.py` の一覧へ `Icon.File.Font`（mdi `format-font`）を足して `Icons.xaml` を再生成し、
  `FileTypeIcons` の `.ttf`/`.otf`/`.ttc` を文書アイコンの流用から差し替えた。
  同時に `.blend` 系へ `Icon.File.Blender`（mdi `blender-software`）を追加している。

## プロジェクトパネルの非表示ルール／テキスト編集（2026-09-12 実装時の残件）

- [ ] **Windows の隠し属性・システム属性は見ていない** — 2026-09-12。非表示判定（`ProjectPanelVisibilityRules`）は
  **名前だけ**で行う（拡張子・完全一致・ワイルドカード・先頭ドット）。エクスプローラで「隠しファイル」属性を
  付けただけのファイルはパネルに出る。属性を見るには `FileAttributes` が要り、純ロジック（WPF/IO 非依存）から
  外れるので今回は入れていない。必要になったら「列挙側（`EnumerateSafe`）で属性を見て落とす」形で足すのが筋。
  関連: `editor/src/Assets/ProjectPanelVisibilityRules.cs`、`docs/editor_project_panel.md` §7。

- [ ] **隠しフォルダの中に居るときに表示トグルを OFF にすると、パンくずだけ取り残される** — 2026-09-12。
  例えば `.backup` を開いた状態でトグルを OFF にすると、ツリーからはそのフォルダが消えるが
  ファイル一覧は中身を描き続ける（作業中に足元が消えないようにする意図的な挙動）。
  混乱するようならアセットルートへ戻す・通知を出すなどを検討する。
  関連: `editor/src/Panels/ProjectPanel.Visibility.cs`（`RebuildForVisibilityChange`）。

- [ ] **`.mat` はいまも OS の既定関連付けアプリで開く** — 2026-09-12。中身は JSON テキストなので、
  内蔵エディタ（`text_editable_extensions.json` へ `{ "extension": ".mat", "language": "json" }` を足すだけ）で
  開けるようにできる。Phase R7 の最小実装（`OpenMaterialFile`）をいつ畳むかの判断が要るため今回は触らず。
  関連: `editor/src/Panels/ProjectPanel.xaml.cs`（`OpenMaterialFile`）、`docs/editor_script_panel.md` §1。

- [ ] **開いているタブは外部の書き換えを検出しない** — 2026-09-12（従来からの挙動）。
  テキスト編集の対象が増えたぶん、外部ツールと取り合いになる場面（`.icons` を VS Code で直す等）が
  増える。タブのファイルを `FileSystemWatcher` で見て「外部で変更された。読み直す？」を出すのが本筋。
  関連: `editor/src/Panels/ScriptEditorPanel.cs`、`docs/editor_script_panel.md` §6。

- [ ] **専用エディタとテキスト編集の同時編集が保存を取り合う** — 2026-09-12。`.anim`（アニメーション
  タイムライン）・`.inputmap`（入力マップ）は、専用エディタで開いたままテキストでも編集できる。
  どちらもファイル全体を書き出すので、後から保存したほうが相手の変更を消す。検出も警告も無い。
  タブを開くときに「同じファイルを他パネルが開いていないか」を問い合わせる口が要る。
  関連: `editor/src/Panels/ProjectPanel.TextEdit.cs`、`docs/editor_script_panel.md` §6。

## エディタ視点サイドカー（cache/editor/view/**.view.json）— 2026-09-15 実装時の残件

- [ ] **旧シーンで `settings` 節が無いものは、再保存時に fov/far/speed が既定へ戻る** — 2026-09-15。
  `.scene` のトップレベル `debug_camera` にしか値が無い旧シーンを保存し直すと、トップレベルが消える一方で
  `settings.debug_camera` が作られないため。全 `.scene` を走査した結果、該当は `templates/scenes/animation.scene` の
  `speed=3.095869`（→ 5.0 に戻る）1 件のみで、実プロジェクトのシーンはすべて `settings.debug_camera` を保持済み。
  対処するならロード時に「`settings` が無く `debug_camera` がある」ときだけ fov/far/speed を `scene.settings` へ
  移し替える（`settings` 節を新規生成すると rendering/lod/physics の project_settings.json フォールバックを潰すので要注意）。
  関連: `runtime/src/engine/core/app_base/scene.rs`（`load`）。
- [ ] **シーンの改名・削除でサイドカーが孤児になる（GC なし）** — 2026-09-15。`cache/thumbnails/` の肥大化と同種。
  まとめて「cache の掃除」機能を用意するのが筋。関連: `runtime/src/engine/core/app_base/editor_view_state.rs`。
- [ ] **シーン切り替え時の視点保存は未実装** — 2026-09-15。保存せずに別シーンへ移ると視点は記録されない。
  素朴に「切り替え前に書く」と、同一シーンの再読み込みで保存済み視点へ戻らなくなるため、
  実装するなら「切り替え先が別シーンのときだけ書く」判定が要る。関連: `app/scene_save_ops.rs`。
- [ ] **`SAVE_SCENE_COPY` は今も `.scene` へ視点を埋め込む** — 2026-09-15。現状の唯一の用途が
  `%TEMP%\SEED\_play_temp.scene`（共有されない）なので許容しているが、将来 SAVE_SCENE_COPY を
  「コピーを書き出す」ユーザー機能に流用する場合は、そのコピーに個人の視点が入らないよう分岐を足すこと
  （判断箇所は `app/scene_save_ops.rs::write_scene_file`）。

## engine_version 不一致チェック — 2026-09-15 実装時の残件

- [ ] **`EngineVersionGate` は自動テスト対象外** — 2026-09-15。WPF（`MessageBox`）依存のため `ProjectSystemTests` に
  リンクできず、ダイアログ選択→保存の分岐は手動確認のみ。判定ロジック本体（`EngineVersionCheck`）はテスト済み。
  関連: `editor/src/Project/EngineVersionGate.cs`。
- [ ] **「以後確認しない」オプションが無い** — 2026-09-15。チームで意図的に `engine_version` を更新せず運用したい場合、
  プロジェクトを開くたび同じ確認ダイアログが出続ける。関連: `editor/src/Project/EngineVersionGate.cs`。

## バージョン管理（Lore）— 2026-09-15 スパイク時の残件（正典: docs/vcs_lore.md）

- [ ] **ロック強制の前提となる認証（JWT）が未整備** — 2026-09-15。`[server.auth]` 無しではロック所有者も push ユーザーも
  `<unknown>` になり「他人のロック」を判定できない。候補はチーム共有鍵で自己発行する JWT（LAN）／外部 OIDC（クラウド）。
  決まるまでロック UI は取得・解放・表示に留める。関連: `tools/seed-loreserver/README.md`「ロック強制を実装するときの設計案」。
- [ ] **push フックから変更ファイル一覧が取れない** — 2026-09-15。`HookContext` は repository / branch / revision / user /
  metadata（client_ip）のみ。ロック照合には revision から差分を出す必要があり、200 ms のフック制限に収まらない恐れ。
  クライアント側の保存ゲートを主、サーバ側は粗い判定に留める案。関連: `tools/seed-loreserver/src/hooks/push_guard.rs`。
- [ ] **`seed_file_lock_store` は単一プロセス・同期 I/O** — 2026-09-15。`std::fs` で async ランタイムをブロックし、
  Mutex を握ったまま書くので高頻度では読み取りも詰まる。低頻度・小ファイル前提の割り切り。マルチノード化時は S3/DynamoDB へ。
- [ ] **seed-loreserver の常駐化・本番設定** — 2026-09-15。ポートを既定（41337/41339）へ戻し、証明書を恒久化し、
  ログオン時の自動起動（タスク スケジューラ）と、チーム接続時の `host = "0.0.0.0"` ＋ファイアウォール規則を用意する。
- [ ] **upstream（Lore）更新で壊れる箇所の確認手順** — 2026-09-15。`Cargo.lock` の継承、`[patch.crates-io] quinn-proto`、
  `.cargo/config.toml` の `--cfg`、`ServerConfig` のフィールド、`LockStore` trait、`HookContext`、
  `FailedPrecondition` のマッピング。関連: `tools/seed-loreserver/README.md`「upstream 更新時の手順」。
- [ ] **CLI の罠をエディタ側で吸収する** — 2026-09-15。`stage . --scan`、空コミット防止、`sync` の競合判定は `flagConflict*`、
  `resolve mine`（=リモート）／`theirs`（=ローカル）の言い換え、リネームは `stage move`。関連: `docs/vcs_lore.md` §3.1。
- [ ] **未確認事項** — 複数マシン・ネットワーク越しの性能、認証有効時のロック所有者表示、`--cache` 付き clone のオフライン能力、
  C# 非同期 API（`.WaitAsync()` / `.AsyncIter()`）と `LoreError` の実送出、数 GB 規模でのマージ性能、
  リリース版 `loreserver.exe` のプラグイン構成、`--release` ビルド。

### エディタ統合（中核層 editor/src/VersionControl/）— 2026-09-18 実装時の残件（正典: docs/editor_version_control.md）

- [ ] **履歴にメッセージ・作者・日時が出ない** — 2026-09-18。`REVISION_HISTORY_ENTRY` には番号・ハッシュ・親しか無い。
  `Lore.RevisionMetadataGet` を 1 件ずつ引く実装が要る（`RevisionInfo` の型は用意済み）。
- [ ] **保存経路・プロジェクトパネルからの通知が未接続** — 2026-09-18。`safe_write` 完了 → `NotifyChangedAsync`、
  作成・削除・改名 → `NotifyMovedAsync`（素のファイル移動は Lore 上で履歴が切れる）。パネル実装時に配線する。
- [ ] **実行中の Lore 呼び出しを中断できない** — 2026-09-18。LoreVcs に Cancel/Abort が無い。タイムアウトは UI を待たせない保険で、
  ワーカーは走り続ける。長い操作に「キャンセル」ボタンを出さない。CLI をサブプロセス化して kill する案もある。
- [ ] **push 拒否・接続失敗の判定が Lore のメッセージ文言に依存** — 2026-09-18。pre-1.0 で変わり得る。変わると `Failed` に落ちる（安全側）。
  直す場所は `LorePushDiagnosis.NEEDS_SYNC_MARKERS` と `LoreConnectionDiagnosis.CONNECTION_MARKERS` の 2 か所。
- [ ] **`LoreFileStageArgs.CaseChange` の意味が未確認** — 2026-09-18。大文字小文字だけの改名で失敗する可能性。
- [ ] **リポジトリの作成・クローンが境界に無い** — 2026-09-18。「プロジェクトをバージョン管理下に置く」「参加する」UI を作るときに足す。
  `.loreignore` の編集 UI も無い。
- [ ] **実サーバ結合テスト（`SEED_LORE_TEST_SERVER`）は手動実行** — 2026-09-18。openssl と `loreserver.exe` の場所が固定値。CI に入れるなら設定化する。

### Version Control パネル — 2026-09-18 実装時の残件（正典: docs/editor_version_control.md）

- [ ] **実際のボタン操作での通し確認が未実施** — 2026-09-18。状態機械の単体テストと中核層の結合テストは通っているが、
  パネルから `.lore` のあるプロジェクトを操作する経路（送信・取得・競合解決・ロック・改名の「移動」表示）は実機で未確認。
  確認手順は `docs/editor_version_control.md` のパネルの節。
- [ ] **保存経路から `NotifyChangedAsync` が未接続** — 2026-09-18。いまは `WorkingCopyWatcher` が走査（`ScanOffline`）で代替している。
  ファイル数が増えたら `safe_write` 完了から dirty を打ち、`TrackedOnly` で済ませる。
- [ ] **`ProjectPanel` の `FileSystemWatcher` にデバウンスが無い** — 2026-09-18。「最新を取得」で大量のファイルが変わると
  ファイルグリッドの全再構築が連続する。
- [ ] **`FileTypeIcons.LoadPng` が pack リソースを引けないと `IOException`** — 2026-09-18。本体では起きないが、
  行の生成ループ中に投げるとパネルが壊れる。フォールバックを検討。
- [ ] **履歴のメタデータ補完は 30 件まで** — 2026-09-18。それ以上は番号だけになる（追加読み込みの導線が無い）。差分表示も未実装。
- [ ] **管理下に置く導線が無い** — 2026-09-18。`.lore` の無いプロジェクトでは案内テキストのみ。リポジトリ作成・クローン・`.loreignore` 編集の UI。
- [ ] **ブランチ一覧はドロップダウンを開いたときだけ取得** — 2026-09-18（サーバ往復のため）。初回は現在のブランチのみ表示。

## アセット形式のマイグレーション — 2026-09-18 M1 実装時の残件（正典: docs/asset_migration.md）

- [ ] **`ModelComponentData.instances` に `#[serde(default)]` が無い** — 2026-09-18。`instances` を持たない `.scene` / `.actor` は
  読み込みが丸ごと失敗する（見本作成中に発覚）。関連: `runtime/src/engine/components/model_component.rs:126`。
- [ ] **一括アップグレード後に `prefab_hash` が一斉に古くなる** — 2026-09-18。`.actor` が 1 行でも変わると生テキストのハッシュが変わり、
  エディタに「プレハブが更新された」が並ぶ（表示のみ）。シーン側の `prefab_hash` を貼り直す後処理が要る。
  わらしべフィッシングの現行データは `prefab_hash` を持たないので未発生。
- [ ] **旧 enum 表記の `alias` は削除候補** — 2026-09-18。全員が一括アップグレードを取り込んだ後に消す:
  `particle_emitter_component.rs:159,162,288`、`canvas_component.rs:37`。`ParticleBlend::from_str_opt` の旧名受理は IPC 経路なので別判断。
- [ ] **M2 完了（2026-09-18: M2a ランタイム a8c8575f、M2b エディタ bd6067e4）の残件**:
  `SceneSettingsData.cs` / `FishCatalogGenerator.cs` / `AssetCollector.cs` はまだ門を通っていない（Rust の変換を素通しする C# 読み手。
  読むキーが変換対象になったら同じコミットで直す）／`engine_version` の不一致ダイアログから形式アップグレードへの導線／
  `chunk_config.json` が版の仕組みに未搭載で `File.WriteAllText` のまま／`MigrateJsonRunner.Run` は同期で UI を最大 30 秒止めうる
  （古いファイルを開いたときのみ）／アップグレードダイアログの一覧は 200 行で打ち切り／一括ゲートは全パスを 1 回で照会するため
  数千ファイルで 5 秒の期限に掛かる可能性（分割照会は未実装）。
- [ ] **`.inputmap` の v1 → v2 が Rust 一本になった回帰リスク** — 2026-09-18。ランタイム exe が未ビルドの構成では v1 の `.inputmap` を
  開けない（黙って空で開かないことはテストで固定）。一括アップグレードを 1 回通せば以後は起きない。
- [ ] **`.tvox` の `safe_write` 化（3 か所）** — 2026-09-18。`app/terrain_ops.rs:4704, 4789, 4881`。同ファイルに別セッションの未コミット
  変更があるため見送り。`.tcover` / `.tscatter` と同じく `safe_write::write_atomic`（世代なし）へ寄せるだけ。
- [ ] **独自の版機構のままの形式** — 2026-09-18。`terrain_meta.json` / `.tvox` / `.tcover` / `.tscatter` / `.seedproj` / シェーディング WGSL は
  この仕組みに未搭載（M3 で判断）。
- [ ] **`--upgrade-project` で `.scene` が 2 回書かれることがある** — 2026-09-18（アップグレード＋`prefab_hash` の貼り直し）。
  `.backup/` に 2 世代残るだけで結果は正しいが、1 回にまとめる余地。
- [ ] **`.sprite_mesh` の明示的な `"version": 0` を弾くようになった** — 2026-09-18。従来は受理していた（実データに該当なし）。
- [ ] **`scripting::debug_command` の 2 テストが並列実行で落ちる** — 2026-09-18。共有グローバルのリングバッファによる既設のフレーキー
  （`--test-threads=1` では通る）。

## SEED アカウント — 2026-09-18 サーバ側（発行窓口）実装時の残件（正典: docs/seed_accounts.md）

- [ ] **オーナーの交代・追加ができない** — 2026-09-18。owner の権限は失効不可で、委譲の API も無い。
- [ ] **失効が即時に効くのは「サーバへ権限の問い合わせが飛ぶ操作」まで** — 2026-09-18 実測。クローンとリポジトリ作成は失効した瞬間に
  拒否されるが、既に接続していてメタデータを手元に持つクライアントの送信・取得・ロックは、トークンの期限（既定 8 時間）まで通る
  （Lore 側がトークンの `resources` だけを見るため）。急ぐときは `token_ttl_hours` を短くするか、サーバを再起動して接続を切るか、
  署名鍵を作り直す（全員が再ログインになる）。恒久策は upstream に storage session / lock 用の authorizer フックを入れてもらうこと。
- [ ] **リポジトリを消しても台帳の権限が残る** — 2026-09-18。`DeleteResource` は権限を消さない（害は無いが掃除の口が無い）。
- [ ] **`repository_creators` は設定ファイルの名前一覧** — 2026-09-18。台帳の役割になっておらず、変更にはサーバの再起動が要る。
- [ ] **`tools/seed-loreserver/Cargo.lock` の `windows-sys` 依存辺が再統合された** — 2026-09-18。依存を足した際に Cargo が正規化した
  （バージョン変更は 0 件、追加は `protoc-bin-vendored*` の 9 件）。upstream のタグを上げるときに README の手順で lock を作り直せば解消する。
- [ ] **平文 HTTP** — 2026-09-18。LAN の外へ出すときは TLS を前段に置くか、窓口自体を TLS 化する。
- [ ] **窓口に監査ログが無い** — 2026-09-18。誰がいつ誰を招待・失効したかは `accounts.json` の `created_by` / `used_by` にしか残らない。
  使用済み・期限切れの招待も溜まり続ける。
- [ ] **窓口は current-thread ランタイムでストアの I/O が同期** — 2026-09-18。数人規模なら問題ないが、増えたら `spawn_blocking` へ。
- [ ] **`lore lock query --path` が使えない** — 2026-09-18。Lore 側が未対応の組合せ。パスで引くときは `lock status`。
  `seed_file_lock_store` 側で対応すれば upstream より便利になる。
- [ ] **本番サーバの認証の有効化は未実施** — 2026-09-18。発行窓口と権限サービスは本番に入れた（Lore は匿名のまま）。残りは利用者の操作:
  Hub でアカウント作成 → プロジェクトを開いてパネルの「アカウント」→「このプロジェクトでアカウントを有効にする」→
  `C:\Users\k023g\SEED_lore\server\enable-auth.ps1` を実行（`[server.auth]` と `auth_url` を足して安全に再起動）。

### SEED アカウント（エディタ側）— 2026-09-18 実装時の残件（正典: docs/editor_accounts.md）

- [ ] **パネルの「アカウント」ボタンが文字ボタン** — 2026-09-18。`gen_icons.py` がネットワーク取得を要するため見送った。
  `Icon.Vcs.Accounts`（MDI `account-multiple-outline` など）を CATALOG に足してベクター化する。
- [ ] **アカウント設定の UI が無い** — 2026-09-18。窓口 URL の上書きと自動ログインの可否は `editor/settings/accounts.json` の直編集のみ。
- [ ] **ログイン失敗が気づきにくい** — 2026-09-18。パネルのヘッダー表示と Output ログだけ。能動的な通知が要る。
- [ ] **招待コードの有効時間を発行画面から変えられない** — 2026-09-18（既定 72 時間固定）。
- [ ] **平文 HTTP のみ** — 2026-09-18。TLS を前段に置く構成では URL 上書きで届くが、証明書検証の設定項目は無い。

## エディタの UI 書式（ボタン共通スタイル）— 2026-09-18 実装時の残件（正典: docs/editor_ui_style.md）

- [ ] **ボタン文言に絵文字が残っている** — 2026-09-18。`ColorPickerWindow` の「💉 スポイト」、`ScriptEditorPanel` の「⚙ 設定」「📖 API ガイド」。
  `.claude/rules/editor-icons.md` の絵文字禁止に抵触。ベクターアイコンへ置換する。
- [ ] **意図的に据え置いたツールバー系のテンプレートが分散** — 2026-09-18。MainWindow・ProjectPanel・SpriteRig に 10 個以上。
  将来 `Seed.Button.Toolbar` として畳む余地。
- [ ] **`Seed.Button.Link` は定義のみで利用箇所ゼロ** — 2026-09-18。使う場面が出たら実画面で確認する。
- [ ] **AvalonDock のフローティングウィンドウと自動非表示タブは未確認** — 2026-09-18。暗黙スタイルが漏れてキャプションボタンが
  灰色の四角になっていないか、実機で一度見る。

## Version Control パネル（VS「Git 変更」風）— 2026-09-18 作り直し時の残件

- [ ] **未送信／未取得の件数が出せない** — 2026-09-18。Lore は前後関係の真偽値しか返さない（あり／なし／未確認の 3 状態で表示）。
  件数には Lore 側 API か `RevisionNumber` 差分の近似が要る（Diverged では分離不能）。
- [ ] **変更ツリーの畳み状態はセッション限り** — 2026-09-18。節の開閉は保存するが、フォルダーの畳みは保存していない。
- [ ] **履歴の「さらに読み込む」は近似** — 2026-09-18。Lore が残り件数を返さないため「上限ちょうど返った」で判定。
- [ ] **`VersionControlService.UseProviderForVerification` が本番アセンブリの公開 API に残る** — 2026-09-18（描画プローブ用）。
  `InternalsVisibleTo` で絞る案。プローブは WPF 内部フィールドをリフレクションで書き換えており、`App.xaml` の pack URI を
  アセンブリ修飾（`/SEEDEditor;component/...`）にすれば不要になる（20 か所ほどの一括修正）。
- [ ] **単一子フォルダーの連結表示** — 2026-09-18。`a/b/c` を 1 行に畳むと深い assets 階層が読みやすい。
- [ ] **実機での確認が未了** — 2026-09-18。節の開閉の保存・復元、数百件の変更でのツリーの操作感、狭い幅での競合行の収まり、
  「その他 …」の「ロックをすべて解除」が自分のロックだけを対象にすること。

### ロックの保存ゲート・自動ロック — 2026-09-18 実装時の残件（正典: docs/editor_version_control.md 4.6）

- [ ] **一括保存のゲートが 1 ファイルずつ往復する** — 2026-09-18。スクリプトエディタの「すべて保存」は N × 0.4 秒。
  `ILockService.GetStatusAsync` は複数パスを取れるので、先に 1 回引いて判定する入口が要る。
- [ ] **自分が保持中のパスの照会を省ける** — 2026-09-18。台帳に載っている間は他人が奪えないので、`Self` のキャッシュ寿命を延ばせば
  編集中ファイルの保存が無料になる。
- [ ] **ランタイム側は守られていない** — 2026-09-18。`.scene` を実際に書くのは Rust（`scene_save_ops.rs`）。エディタ以外が
  `SAVE_SCENE` を送る経路ができたらゲートを通らない。
- [ ] **ロックの方針を変える UI が無い** — 2026-09-18。`editor/settings/locking.json` を手で置く（`{"enforcement":"warn_only"}`）。
- [ ] **プロジェクトパネルのバッジ**（他人がロック中のファイルに印）は未実装 — 2026-09-18。
- [ ] **`ScriptEditorPanel.PromptSaveOnExit()` が `EditorDialogs` を通さず `MessageBox.Show` を直接呼ぶ** — 2026-09-18（既存）。
  ヘッドレス終了時に止まり得る。
- [ ] **実機での確認が未了** — 2026-09-18。Ctrl+S の体感（0.4 秒）、起動時シーンの「編集中」表示（ログイン完了後の取り直し）、
  別名保存で新規ファイルへ書くときに止まらないこと、終了時の解放待ち（最大 3 秒）。

### ブランチのマージ／削除 — 2026-09-19 実装時の残件（正典: docs/editor_version_control.md）

- [ ] **branch merge の競合で「リモートを採用」の語が合っていない** — 2026-09-19。実際には「取り込み元のブランチを採用」。
  動作は正しい（出どころで対応表を分けている）が、ラベルを出どころで出し分けたい。
- [ ] **マージの中止（`BranchMergeAbort`）が未公開** — 2026-09-19。競合したら解決するしか道がない。
- [ ] **アーカイブ済みブランチの一覧・復帰が未公開** — 2026-09-19。`BranchList{Archived=true}` で取れるが、エディタから元に戻せない。
- [ ] **既定ブランチ名がエディタ設定の定数（main）** — 2026-09-19。プロジェクト設定から読めるようにする。
- [ ] **結合テストのポートが固定（41357/41359）** — 2026-09-19。前回の中断で残った `loreserver.exe` があると別のストアに繋いで
  原因不明の失敗になる。フィクスチャに起動前チェックを入れる。
- [ ] **`BranchMergeInto`（切り替えずに現ブランチを相手へ押し込む）は未使用** — 2026-09-19。link / layer を使い始めたら
  `BranchMergeStart` / `BranchArchive` の `IgnoreLinks` / `IncludeLayers` も再検討。

### マージエディタ（競合の中身を並べて解決）— 2026-09-19 実装時の残件（正典: docs/editor_version_control.md 4.7）

- [ ] **差分は行単位まで** — 2026-09-19。1 文字だけ違う行も「削除 + 追加」の 2 行になる。
  単語単位（行内差分）を足すと、`"x": 1.0` → `"x": 2.0` のような変更が読みやすくなる。
- [ ] **結果の手編集がチェック操作で失われる** — 2026-09-19。チェックを触ると結果を作り直すため。
  いまは状態行に常時その旨を出しているだけ。ブロック単位で「手編集あり」を覚えて
  そこだけ再生成しない、または作り直す前に確認する仕組みが要る。
- [x] **開いているシーン自体を解決しても再読込されない** — 2026-09-19 記載 → 同日、誤りと確認。
  解決でファイルが書き換わるので `SceneAutoReloader` がそのまま読み直す（実機のログで確認）。
  あわせて「競合中は読みに行かない（エラーダイアログを出さない）」「競合中は上書き保存させない」を
  `Scene/SceneConflictGuard.cs` で追加した。
- [ ] **競合中に未保存の編集があると、解決後も自動では読み直されない** — 2026-09-19。
  `SceneAutoReloader` は未保存の編集があるとき再読込を見送る（編集を捨てないため）。
  競合中は上書き保存も拒否するので、利用者は「別名で保存して逃がす」か「編集を捨てて
  メニューから再読込」を選ぶことになる。競合が起きた時点で未保存なら、その旨を案内したい。
- [x] **競合の一覧を作るたびにファイルを読んで差分まで取る** — 2026-09-19 記載 → 同日解消。
  「両方を取り込む」の可否判定のため、`ConflictRowItem` が未解決の競合 1 件につき 1 回読み、
  さらに**ブロックごとに LCS を 2 回**回していた。競合の行から**ボタンを全部外した**ので
  （ファイル名が見えないという指摘。docs/editor_version_control.md 4.7.8）判定そのものが不要になり、
  行の生成は文字列操作だけになった。バイナリ等の解決は 2 択ダイアログが受け持つ。
- [ ] **印の残存検査が `=======` 単体も弾く** — 2026-09-19。Markdown の見出し下線（`=` が 7 個ちょうど）を
  印と誤判定して、マージエディタの「マージを確定」や中身を指定した解決が通らなくなる
  （`MergeValidation.FindMarkerLine`）。JSON アセットでは起きないが、
  `.md` / `.txt` の競合では起こり得る。始まりの印とペアになっているかまで見れば直せる。
- [ ] **巨大な競合ブロックは差分を諦める** — 2026-09-19。LCS の表が
  `MergeLineDiff.MAX_TABLE_CELLS`（100 万マス）を超えると「全行が変更」表示へ退避する。
  合成（2 択・どちらも選ばない）の正しさは落ちないが、色が読めなくなり、
  **「両方を取り込む」が必ず不可になる**（退避の結果に `Removed` が入るため。
  壊すより安全側だが、本来 union できる巨大ブロックでもボタンが押せない）。
  分割統治の差分に替えれば上限を上げられる。
- [ ] **マージエディタに検索が無い** — 2026-09-19。数百行の競合では目当ての場所へ辿り着きにくい。
  「次の競合」はあるが、文字列検索は無い。
- [ ] **両側がまったく同じ行を挿し込むと union で 2 回出る** — 2026-09-19。2 人が同名・同内容の
  アクターを足した場合、「両方を取り込む」は意味どおり 2 体にする（JSON としては妥当なので
  検査も通る）。結果はマージエディタで確認してから確定できるので害は小さいが、
  「同一の挿入は 1 つにまとめるか」を尋ねる余地はある。
- [ ] **実機での確認の残り** — 2026-09-19。使い捨て loreserver ＋ 競合状態の使い捨てプロジェクトを
  ヘッドレスのエディタで開き、UI Automation で確認済み: 『比較…』でマージエディタが開く／
  3 面の描画（赤・緑・詰め物・既定チェック）／『すべて取り込み元』→『マージを確定』で
  Lore 側にマージのコミットができ脇ファイルが消える／『すべて両方を取り込む』（パネル）で
  base つきの add-add 競合が union されアクター 4 体の妥当な JSON になる（いずれも sync 由来）。
  （★この確認で押した『比較…』『すべて両方を取り込む』（パネル）は 2026-09-19 に廃止した。
  いまの入口は行のダブルクリック／Enter だけ。docs/editor_version_control.md 4.7.8）
  **未確認**: ブランチのマージ由来（見出しのブランチ名・両方の並び順の入れ替わり）、
  左マージンのチェックのクリック（ヒット判定）、**上段 2 面を独立にスクロールしたときの体感**、
  行のダブルクリック／Enter でマージエディタが開くこと、
  狭い幅で**ファイル名が先に出てフォルダ側が省略される**こと、
  バイナリ（`.png` 等）の競合で 2 択ダイアログが出て正しい側が残ること。
- [ ] **エディタの外で作った競合には出どころの印が無い** — 2026-09-19。CLI で `branch merge start`
  した競合をエディタで開くと、印（`cache/editor/vcs/merge_origin`）が無いので sync 扱いになり、
  見出しが「リモート／自分の変更」になる（両方の並び順も sync の規則）。動作は壊れないが
  表示が実態と合わない。Lore の status から「マージ中の相手」が取れるなら印に頼らない形にできる。

## シーンロックの置き場移動（2026-09-19 事故対応時の残件）

経緯: シーンのロックファイルを `<scene>.lock`（アセットの中）へ置いていたため
バージョン管理に混入し、「test を main にマージしたのに反映されない」事故になった。
ロックの置き場を `cache/editor/scene_locks/` へ移し、マージを止めたときの
モーダルと VCS 操作のログを足した（正典: `docs/editor_version_control.md` 4.6.6）。

- [ ] **リポジトリへ入ってしまった旧位置のロックを一括で掃除する手段が無い** — 2026-09-19。
  新しいエディタは**そのマシンが張った無効なロック**だけを削除するので、
  「開いたシーンの分だけ」少しずつ消える。別マシン（＝クローンに付いてきた汚染）の
  ロックは意図的に消さないため、`*.loreignore` 済みのブランチでは status にも出ず
  ツリーに残り続ける（使い捨てサーバで確認済み: 追跡済みロックの削除は
  無視対象になったあと stage できない）。当座は Lore の CLI か、無視指定を一時的に
  外して削除 → 送信する手作業が要る。エディタから「バージョン管理に入っている
  ロックファイルを一覧して消す」保守コマンドがあるとよい。
- [ ] **旧位置のロックを尊重するかの判定はマシン名の文字列一致に頼っている** — 2026-09-19。
  同じ名前の PC が 2 台あると（社内の使い回しイメージ等）、他人のロックを
  「自分のマシンのもの」と誤認して削除し得る。実害はロックが 1 つ消えるだけだが、
  マシン固有 ID（`MachineGuid` など）を書いておけば厳密にできる。
- [ ] **マージを止めたモーダルから「送信」「元に戻す」へ直接行けない** — 2026-09-19。
  いまは OK だけで、利用者がパネルへ戻って自分で押す必要がある。
  1 行メッセージと二重に出るのが冗長でもあるので、いずれ 1 か所へ寄せたい。

## スクリプトエディタのディスク追従・戻る／進む（2026-09-19 実装時の残件）

経緯: バージョン管理でブランチをマージ・切り替えると、開いているスクリプトが
ディスク上で書き換わったり消えたりするのに、タブが開いた瞬間の中身を持ち続けていた。
開いているタブを実ファイルへ追従させる仕組みと、Visual Studio 風の戻る／進むを足した
（正典: `docs/editor_script_panel.md` 5. と 6.）。

### ディスク追従

- [ ] **フォルダごと消されている間は監視を張れない** — 2026-09-19。
  `FileSystemWatcher` は存在するフォルダにしか張れないため、
  マージでフォルダごと消えたタブは、ファイルが戻ってきても
  **ウィンドウのアクティブ化まで気付かない**（エディタへ戻れば必ず追いつくので実害は小さい）。
  直すなら「存在する一番近い親フォルダを `IncludeSubdirectories` 付きで監視する」だが、
  アセットルートまで遡ると巨大なツリーを再帰監視することになるため、
  遡る段数の上限とセットで考えること。
- [ ] **点検のたびに全タブのファイルを全文読んでハッシュを取っている** — 2026-09-19。
  タブが数個・ファイルが数 KB という前提なら 1 ms 未満だが、
  上限（`max_editable_bytes` 既定 1 MiB）に近いファイルを何枚も開いていると
  ウィンドウを切り替えるたびに UI スレッドで数十 MB 読むことになる。
  `(更新日時, サイズ)` が前回と同じならハッシュを省く、という定番の間引きが要る
  （mtime を保ったまま中身だけ変えるツールには弱くなるので、そこを許容できるか次第）。
- [ ] **実体が無いまま開いたタブでは、保存済みブレークポイントが復元されない** — 2026-09-19。
  「見つかりません」タブは空の本文で開くため、`BreakpointSet` が行範囲外として全て落とす。
  その後ファイルが現れても、落ちたあとなので戻らない。
  （開いている最中に消えた → 戻ったタブは本文を保持しているので影響しない）
- [ ] **削除されたファイルの親フォルダごと消えていると、保存でも作り直せない** — 2026-09-19。
  「保存すると作り直されます」と帯に出るが、`File.WriteAllText` が
  `DirectoryNotFoundException` で失敗してログに出るだけになる。
  フォルダを作ってから書くか、帯の文言を状況で出し分けるかのどちらか。
- [ ] **「ファイルが見つかりません」タブでも検索バーの対象は裏のエディタのまま** — 2026-09-19。
  消える前の古い本文に対して検索がヒットする（表示は差し替わっているので見えない）。
  実害は小さいが、消えたタブでは検索を無効にしたほうが素直。

### 戻る／進む

- [ ] **閉じたファイルの履歴点は行・桁で固定されたまま** — 2026-09-19。
  タブを閉じた時点で行・桁に落とすので、閉じている間にそのファイルが
  外部で編集されると指す場所がずれる。開き直しても固定値のまま（アンカーへは戻さない）。
  Visual Studio も同様の割り切りだが、「再度開いたらアンカーへ昇格させる」余地はある。
- [ ] **ツールバー・メニューに「戻る／進む」が無い** — 2026-09-19。
  マウスのサイドボタンとキー（`Ctrl+-` / `Ctrl+Shift+-` / `Alt+←→`）だけなので、
  存在に気付きにくい。ツールバーへ矢印ボタンを置くなら
  `Icon.Back` があるので「進む」側のアイコン（`arrow-right`）を
  `gen_icons.py` の `CATALOG` へ足す必要がある。
- [ ] **検索以外の「短い距離のジャンプ」は記録されない** — 2026-09-19。
  同一ファイル内では `JumpLineThreshold`（10 行）以上離れた移動だけを記録する。
  9 行先のメソッドへクリックで飛んだ場合などは履歴に残らない。
  Visual Studio と同じ割り切りだが、閾値の妥当性は使ってみてから見直すこと。

### 同日のレビューで挙がった、直さずに残した分（2026-09-19）

- [ ] **移動に失敗しても履歴だけが消費される** — 2026-09-19。
  `ScriptNavigationHistory.Step` は候補を取り出して現在位置を反対側へ積んでから返すため、
  その後 `JumpTo` がファイルを開けなかった（ロック中・権限不足）場合に
  「画面は動かないのに履歴点が消える」。`IsNavPositionAvailable` が `File.Exists` までは
  見ているので発生は稀。直すなら「移動が成功してから履歴を確定する」形にする。
- [ ] **コード整形（Ctrl+K,D）でブレークポイントが 1 行目へ寄る** — 2026-09-19。
  本文の総入れ替えでアンカーが潰れるため。今回の変更以前からの挙動だが、
  再読み込み側に `BreakpointSet.ResetTo` での復元を入れたので、整形にも同じ復元を
  通せば一貫する（履歴のアンカーと抑止は今回入れた）。
- [ ] **プロジェクト全体コンパイルの診断が再読み込みで更新されない** — 2026-09-19。
  開いているタブの診断（波線・エラー一覧）は再読み込み時に消して再解析するが、
  `_projectDiagnostics` は古いまま残る。次の保存・Play 開始まで、
  エラー一覧に差し替え前の行番号が残存する。
- [ ] **再読み込みの読み取りに失敗し続けると同じログが出続ける** — 2026-09-19。
  失敗時は状態も基準値も変えないため、ウィンドウをアクティブにするたびに再試行してログが出る。
- [ ] **クラッシュ復元の本文流し込みだけ Freeze を通していない** — 2026-09-19。
  `RestoreDoc` の `doc.Editor.Text = content` は本文の総入れ替えだが、
  `FreezeNavigationAnchors` / `WithNavigationSuppressed`（docs 6.0）を通っていない。
  起動直後で履歴が空なので現状は実害が無い、という前提に乗っている。
- [ ] **検索の「次へ／前へ」は一致が無くても履歴点を記録する** — 2026-09-19。
  `FindReplaceBar.BeforeJump` を検索実行の前に発火しているため。
  同一場所のマージで潰れるので実害は小さい。

### 音声の試聴・波形サムネイル・関連付けで開く（2026-09-20）

- [ ] **`.ogg` は試聴できない** — 2026-09-20。
  Windows が標準で Vorbis の復号器を持たないため、試聴ボタンを無効にして理由を出している。
  利用者が別途コーデックを入れていても押せないまま（拡張子で判断しているため）。
  波形サムネイルも同じ理由で出ず、形式アイコンのままになる。
  ダブルクリックすれば既定のプレイヤーで開けるので、聴く手段自体は残っている。
  直すなら「押したときに実際に開いてみて、駄目なら無効化を覚える」形にする。
- [ ] **波形サムネイルのキャッシュを掃除する口が無い** — 2026-09-20。
  `cache/editor/waveforms/` は増える一方で、音声を差し替えると古い PNG が残る
  （鍵に更新時刻とサイズが入るので**誤表示はしない**が、ディスクは食う）。
  モデルサムネイル（`cache/thumbnails/`）にも同じ問題がある。
  キャッシュ全体の掃除メニューをまとめて用意するのが筋。
- [ ] **試聴の再生位置を示すものが無い** — 2026-09-20。
  再生中でも波形の上に再生位置の線は出ず、途中から聴く・途中で飛ばすこともできない。
  長い BGM の確認には足りない。波形の上をクリックしてシークできると実用になる。
- [ ] **試聴の音量が固定** — 2026-09-20。
  `AudioPreviewController.PreviewVolume`（控えめな定数）で、画面から変えられない。
  設定へ出すか、ボタンの右クリックで変えられるようにするのが候補。
- [ ] **波形サムネイルの縦は正規化していない** — 2026-09-20。
  振幅をそのまま描くので、録音レベルの小さい素材は細い線に見える。
  Unity は正規化しない流儀なので合わせてあるが、
  「小さい音の素材ばかりのプロジェクトでは見分けにくい」という声が出たら要再考。
- [ ] **関連付けで開いた後のことは追えない** — 2026-09-20。
  `UseShellExecute` で投げたら終わりで、開いた側が失敗しても分からない
  （関連付けが 1 つも無い場合だけは `ERROR_NO_ASSOCIATION` で検出してトーストを出す）。
- [ ] **`AudioDictionaryCatalog.AudioExtensions` だけ別集合のまま** — 2026-09-20。
  音声拡張子の唯一の情報源は `AssetPreviewKinds` に寄せたが、
  `Controls/AudioDictionaryCatalog.cs` の配列は順序をランタイム側の定数と揃えて
  `editor/tests/AudioDictionaryTests` が固定しているため、統合せずに残した。
  役割（AudioComponent の音声パス欄が受け付ける並び）が違う、という整理で
  両方のコメントに相互参照を書いてある。増やすときは両方を見ること。


## Android（2026-09-24 段階0 実装時の残件・正典: docs/android.md）

段階0（エミュレータで 1 枚絵・回転・ホーム復帰・タッチ受信・panic ログまで）で見つけた「今はやらない」課題。
段階の区分（A / B / C / D）は docs/android.md §9。

- [x] **実機の描画が重い（GPU 待ちが支配的と見られる・段階D）** — 2026-09-24 記載 / 2026-09-25 対応（D-2。docs/android.md §22）。
  描画品質プリセット（`runtime/config/render_presets.json`・`renderer/quality/`）を入れ、Android の既定を `mobile`
  （ゲーム画面を 0.5 倍〈段階D-3 で 0.75 倍に見直し〉で描いて拡大・前方描画・SSGI／AO／反射／ブルーム・水面反射なし・影 1024。UI は画面の解像度のまま）にした。
  パスごとの GPU タイムスタンプ計測（`renderer/gpu_timing/`・`seed.gpu_timing=1`）も入れた。デスクトップの既定 `desktop` は何も下げない
  （PC の Play は画素一致で確認）。実機の効果は段階D-3 で計測した（`desktop` 16.4 fps → `mobile` 59.2 fps。下の「D-2 の実機計測」）。以下は記載時のメモ。
  Pixel 6a（Mali-G78・ドライバ r54p1）の debug ビルドで
  縦 約 18〜19 fps／横 約 37〜39 fps。`[PERF]` では 1 フレーム約 50 ms のうち提示待ち（`finish`）28〜41 ms・
  `begin_frame` 最大 19 ms で、CPU 側は数 ms。デスクトップ向けの描画経路（deferred・MRT 5 枚・シャドウ 2048・SSGI）を
  端末の実解像度 1080x2400 のまま回しているため。モバイル向け描画プリセット（描画解像度スケール・重い後処理の既定オフ）で
  対処する。縦より横が速い理由は未調査（描画する画素数は同じ）。関連: `runtime/src/engine/core/renderer/`、
  project_settings の `render_resolution`。
- [x] **D-2（段階D）の実機計測と `mobile` の見直し** — 2026-09-25 記載（段階D-2 で未実施）/ 同日対応（段階D-3）。
  Pixel 6a・縦 1080x2400・debug の .so・`proj_bench`（DamagedHelmet 9 体・影ありの平行光・点光源 4・床・UI）で、組み合わせごとに 25 秒測った
  （表は docs/android.md §22.7）。`desktop` 16.4 fps（GPU 59.2 ms。lighting 41.5・ssgi 11.0）→ `mobile` 59.2 fps（GPU 10.5 ms）。
  最大の要因はデファードのライティング（等倍 41.5 ms。前方描画の約 5 倍）と SSGI（11 ms）。前方描画の上では描画スケール 0.5 と 0.75 の差が
  GPU 約 2 ms しかなく、0.5 は床の縁や細部のぼけが目立つため `mobile` を 0.5 → 0.75、`mobile_high` を 0.75 → 1.0 にした
  （`runtime/config/render_presets.json`）。座標はタップの位置・`SafeArea` とも `desktop` / `mobile` で一致。残りは下の 4 件。以下は記載時のメモ。
  実装・PC での確認・APK の作成と導入まで済んだが、確認の時間中ずっと端末の画面が消えていて（`mWakefulness=Dozing`）、実機のプリセットごとの
  fps・GPU の内訳・スクリーンショット・座標（タッチ・UI の当たり判定が描画スケールで変わらないこと）を測れていない。docs/android.md §22.6 の手順
  （`am start --es seed.gpu_timing 1 --es seed.quality <名前> --es seed.quality_overrides '<キー=値>'` → `[SEED GPU]` / `[SEED HEARTBEAT]`）で
  `desktop`・`mobile`・つまみ 1 つずつ（`render_scale` 0.5/0.75・`deferred=false`・`gi=flat`・`shadows=false`）を測り、`mobile` の中身を数値で決め直す。
- [ ] **デファードのライティングが Mali で前方描画の約 5 倍重い原因の調査** — 2026-09-25（段階D-3 の実機計測）。Pixel 6a（Mali-G78）の等倍で
  lighting 41.5 ms（前方描画の forward 8.0 ms）。描画スケール 0.5 でも 10.8 ms。G-Buffer（MRT 5 枚・36 byte/画素）の読み・クラスタのライト走査・
  シャドウのサンプリングのどれが効いているかを、区間を分けて（`renderer/gpu_timing/`）か、つまみ（影なし・点光源なし）で切り分ける。
  `mobile` は前方描画なので今は効かないが、シェーディングアセット（L3）・SSGI を使うゲームが `deferred: true` へ戻すと効いてくる。
  関連: `renderer/deferred.rs`・`renderer/gbuffer.rs`・`renderer/clustered.rs`・docs/android.md §22.7・§22.8。
- [ ] **固定分 約 4.5 ms（クラスタ構築・トーンマップ・UI・提示）の削減** — 2026-09-25 記載（D-2 の「描画スケールを下げてもフル解像度で残るパス」を
  段階D-3 の実測で置き換え）。クラスタ構築 約 1 ms・トーンマップ 約 1.9 ms・UI・提示のコピーは描画スケールに関係なく画面の解像度で走り、
  `mobile` の GPU 10.5 ms のうち約 4.5 ms を占める。トーンマップ後の LDR 中間（Rgba16Float）・UI・提示のコピーは論理サイズ（画面の解像度）のまま。
  UI が無いフレームはトーンマップを提示先へ直接書けば 1 パスと 1 枚ぶんの帯域を減らせる。LDR 中間を 8bit にする案もある（パイプラインの
  カラー形式が HDR 前提なので別パイプラインが要る）。クラスタ構築は静止したシーンでは毎フレーム作り直さずに済む余地。
  関連: `app/frame_renderer.rs` のトーンマップ・`RenderFrame::present_to_swapchain`・`renderer/clustered.rs`。
  → 2026-09-28 午後（W2 の見本の実機。roadmap §3.9）: **3D の物が無い UI だけのシーンでも**スクロール中の GPU は 7.3〜7.9 ms/フレームで、うち
  トーンマップ 0.9〜1.6・提示 0.9〜1.0・オーバーレイ 0.35〜1.1・前方 0.3〜0.5・クラスタ 0.2〜0.4 ms が毎フレーム走る（ui は 3.5〜4.1 ms）。60 fps の律速ではないが、
  UI だけのアプリでは 3D の経路を飛ばして UI を提示先へ直接描く道があれば約 3 ms/フレームの GPU（電池）を減らせる（推論）。
- [x] **横向き・動くシーン・影ありの実機計測** — 2026-09-25（段階D-3 の残り）/ 2026-09-26 対応。わらしべフィッシング（動くシーン・影あり）で縦・横、
  debug と release の .so を測った（docs/android.md §22.10）。結果は下の「利用者のゲーム（わらしべフィッシング）で `mobile` が 60 fps に届かない」。
  以下は記載時のメモ。計測は縦・静止した `proj_bench` だけで、影の深度パスは
  静的スキップで省略されていた（動くシーンでは毎フレーム影を描く）。横向き（§7 で縦より速かった理由も未調査）・アニメーションするモデル・
  動くカメラで、`mobile` の fps と GPU の内訳（`shadow` 区間）を測る。debug の .so で CPU が律速になれば release の .so（`--release`）でも測る。
- [ ] **重いシーンで 60 fps を割るなら `mobile` の描画スケールを 0.6 へ** — 2026-09-25（段階D-3・判断の保留）。`proj_bench` では 0.75 で 59.2 fps
  （GPU 10.5 ms）だが、実ゲーム（わらしべフィッシングの水面・地形・パーティクル）で 60 fps を割るなら 0.6（GPU 9.2 ms・0.75 との差 約 1.3 ms）へ
  下げる（`runtime/config/render_presets.json` の 1 行。プロジェクトごとなら `render_quality.android.render_scale`）。
  2026-09-26 の実測（docs/android.md §22.10）: わらしべフィッシングでは 0.6 だけでは届かない（縦 42.5・横 44.4 fps）。影も止めると縦 58.2・横 53.5。
  プリセットの見直しは下の「利用者のゲーム（わらしべフィッシング）で `mobile` が 60 fps に届かない」の調査の後に判断する。
- [x] **前方描画（`mobile`）で地形が赤・緑のベタ塗り、水面が平坦な一色になる** — 2026-09-26 記載（利用者の実機 Pixel 6a・わらしべフィッシング）/ 同日対応。
  前方描画に地形のレイヤブレンドが無く、汎用メッシュのシェーダが頂点カラー（レイヤの重み）を色として塗っていた。水面は反射パスが走らず反射 RT が黒ダミーのままだった。
  地形は G-Buffer 版と同じブレンド関数を共有する前方描画パイプライン（`renderer/terrain_forward.rs`・`terrain_layer_blend.wgsl`）、水面は反射パスが走らない
  フレームだけ天球を映すフォールバック（`renderer/water/sky_fallback.rs`）で直した。`desktop`（デファード）は静止シーンで画素一致。正典は docs/android.md §22.9。
- [x] **上の修正の実機確認（見た目・fps・60 fps に届くか）** — 2026-09-26 記載 / 同日対応。Pixel 6a・debug の .so（複製したわらしべフィッシングの MainGame）で、
  修正前の APK の赤・緑の地形と平坦な海が、修正後は砂と葉のレイヤの色・空（雲）と波を映す海になった（縦・横。横は利用者のスクリーンショットと同じ場面）。
  `[SEED QUALITY] preset=mobile render_scale=0.75 …`。GPU の増分は forward +0.53・water +0.40 ms。fps は 60 に届かない（すぐ下の新しい項目。
  docs/android.md §22.9・§22.10）。以下は記載時のメモ。修正後の APK は作ったが、確認の間（05:33〜05:54）端末の画面が消えたままで
  入れていない（一時停止中の写しの作業のときも 06:06〜06:27・07:04〜07:25 に `tmp/android_fix1/dev_batch.sh` で待ったが、画面が消えたままで未実施）。わらしべフィッシングの MainGame（`seed.scene mainGame/MainGame.scene`）で、見た目（地形のレイヤ・海の空の映り込み）・`[SEED QUALITY]`・
  HEARTBEAT の fps・`[SEED GPU]` の forward / water 区間を測り、60 fps を割るなら `render_scale=0.6`・`shadows=false` などのつまみで届くかを数値で決める
  （docs/android.md §22.6・§22.9。修正前の見た目は利用者のスクリーンショット）。
- [ ] **利用者のゲーム（わらしべフィッシング）で `mobile` が 60 fps に届かない** — 2026-09-26（上の修正の実機確認で判明。docs/android.md §22.10）。
  Pixel 6a・debug の .so・MainGame の開始位置で `mobile` 縦 38.2・横 44.6 fps（ベンチの `proj_bench` は 59.2）。`render_scale=0.6` 縦 42.5・横 44.4、
  `shadows=false` 縦 55.8・横 44.1、両方で縦 58.2・横 53.5。release の .so でも上がらない（横 39.0 / 39.8 / 43.1 / 47.9）: 待ちを除く CPU は 8〜11 ms に
  減るが、1 フレーム約 25 ms のうち描画先の取得待ちと提示待ちが約 14 ms に増える。GPU のタイムスタンプの合計は 11〜15 ms（16.7 ms 未満）。
  **主因は CPU ではなく GPU か提示の側と推定（未実証）**。候補: (1) 水面パスの軽量化（water 約 4 ms。屈折背景のグラブ・水面パス。§22.9 の空の映り込みで
  +0.4 ms）、(2) compute 区間 約 3.5 ms の削減（フレームの頭〜影の前: スキニング・パーティクルのシミュレーション・メッシュレットカリング等。
  `proj_bench` では 0.04 ms。前方描画のクラスタ構築は別の cluster 区間で 0.05 ms）、(3) 提示待ちの調査（present mode〈端末は Fifo〉・フレーム遅延
  〈`desired_maximum_frame_latency` 2。`renderer/mod.rs`〉・フル解像度で残るパス〈トーンマップ・UI・提示のコピー。上の「固定分」〉・タイムスタンプに出ない
  GPU の仕事）、(4) 熱による低下（電池 29.1 → 36.5 ℃ で熱状態 1。後半の再計測は横 44.6 → 36.9 fps）。影を止めると縦では待ちを除く CPU も
  18.0 → 13.3 ms に減る（横では変わらず。熱の影響の可能性）。3D の画素が約 3 倍の横が縦より速い理由も未調査。
  関連: `renderer/water/`・`app/frame_renderer.rs`・`renderer/gpu_timing/`・`runtime/config/render_presets.json`。
- [ ] **前方描画では散布の草（kind=grass）が描かれない** — 2026-09-26（上の修正の調査で発見・わらしべフィッシングは草を使っていないので未対応）。
  草のシェーダ（`grass_gbuffer.wgsl`）は G-Buffer の MRT へ焼く自己完結のシェーダで、前方描画のメインパスに対応する経路が無い（`frame_renderer.rs` の
  G-Buffer パスの中でだけ `draw_grass` を呼ぶ）。`mobile`（Android の既定）で草のある地形は草が消える。直すなら前方描画用の草のフラグメント
  （`light_common` の group4 のライト＋`evaluate_lighting`）を足し、前方描画の不透明ループで描く。
- [ ] **前方描画では散布モデル（kind=model）の不透明部分が描かれない** — 2026-09-26（同上）。G-Buffer パスでは `self.terrain.scatter_models` を
  `draw_gbuffer_indirect` で描くが、前方描画の不透明ループ（`draw_model_indirect`）には無い（半透明の部分だけは透明パスへ入るので、木なら葉だけが出る）。
  直すなら前方描画の不透明ループに `scatter_models` のループを 1 つ足す（メッシュレットの前処理は前方描画でも走っている）。
  散布データ（`.tscatter`）のあるプロジェクトで確かめられなかったので入れていない。
- [ ] **前方描画の水面は空しか映らない** — 2026-09-26（上の修正の限界）。物体（島・小屋）の映り込み・反射の粗さのぼけ・水中から見上げた水面の内側の反射は無い。
  空の映り込みは反射のぼけが無いぶん雲の輪郭がくっきりする（静止シーンの平均色はデファードと 1〜2 階調差・ばらつきは約 2 倍）。天球が無いシーンは
  従来どおり反射なし（反射パスのミス経路のような GI／アンビエントへのフォールバックは入れていない）。ぼけが要るなら天球のミップを作って粗さでレベルを選ぶ。
- [ ] **前方描画の陰が暗い（GI がフラット）** — 2026-09-26（見た目の差として記録）。`mobile` は `gi=flat` なので、陰はシーンのアンビエント
  （わらしべフィッシングの MainGame は `ambient_intensity` 0.05）だけで、デスクトップの DDGI の照り返しが無い。Android だけ明るくしたいなら
  シーンのアンビエントを上げるか、`render_quality.android` に `"deferred": true, "gi": "ssgi"` を書いて SSGI を戻す（重い。docs/android.md §22.7）。
  Android 向けだけアンビエントを持ち上げるつまみ（例 `ambient_boost`）を足す案もある。
- [ ] **品質のつまみの残り（テクスチャの最大解像度・クラスタの分割数・カスケード数）** — 2026-09-25（D-2）。候補に挙がったが入れていない。
  テクスチャは読み込み時にミップの上段を捨てる形が素直（`loader/asset_cache` と GPU への積み込み）。クラスタの分割数（16×9×24）と
  CSM のカスケード数（3）はシェーダの定数なので、変えるならシェーダの差し替えが要る。実機の内訳で必要と分かったら足す。
- [ ] **背景ゾーン・3D 空間のキャンバスは描画スケールの解像度で描かれる** — 2026-09-25（D-2）。前面ゾーンの UI だけが論理サイズ
  （画面の解像度）のまま重なる。背景ゾーンは 3D より奥に描くためメインパスの中にあり、3D のキャンバスは 3D の一部なので縮小される。
  文字を背景ゾーンに置くとぼける。気になるなら背景ゾーンだけ別の論理サイズのパスで先に描いて 3D を重ねる構成を検討する。
- [ ] **SeedAndroid / エディタの実行から品質の起動オプションを渡せない（低優先）** — 2026-09-25（D-2）。計測の `seed.quality` /
  `seed.quality_overrides` / `seed.gpu_timing` は `am start` を直接打つ必要がある。`run` に `--quality` 等を足すと計測が楽になる
  （`Steps/LaunchStep` の extras・`AndroidRunRequest`・SeedAndroid の引数）。
- [ ] **実機 GPU の features / limits の余裕を一覧で出す診断（低優先）** — 2026-09-24。実機では `MULTI_DRAW_INDIRECT` が無く
  `request_device` が落ちたため、間接描画の 3 feature を「対応していれば要求」にして通した（Mali-G78 では
  `max_bind_groups: 5` 等の limits は足りていた）。wgpu の `check_limits` は超過した limit を最後の 1 件しか返さないので、
  別の端末で limit 超過に当たると 1 件ずつしか分からない。起動時に「要求 limits とアダプタ limits」を全件比べてログに出すと
  端末ごとの検証が速い。関連: `runtime/src/engine/core/renderer/mod.rs`（`Renderer::new`）。
- [x] **Activity 破棄時にセーブの未書き出し分が失われる** — 2026-09-24 記載 / 同日対応（A-3）。suspended（背面へ回る）で同期に `save::flush_if_dirty()`（`app/background_lifecycle.rs`）、保険として `MainActivity.onDestroy` からプロセス終了前に JNI（`nativeFlushSaveData`。`jni_exports.rs`）で書き出す。エミュレータで検証用フック `debug.seed.save_test` により両経路を確認（docs/android.md §14.2・§14.7）。正式な終了処理（winit の更新・自前の破棄通知）は未着手のまま（プロセス終了の方針は据え置き）。以下は記載時のメモ。`MainActivity.onDestroy` はプロセスを即終了させる
  （winit 0.30 が onDestroy をアプリへ通知しない・EventLoop は 1 プロセス 1 回のため。docs/android.md §8）。
  Windows は `CloseRequested` で `save::flush_if_dirty()` を呼ぶが、Android にはその経路が無い。
  段階A で onPause / suspended 時の flush と、正式な終了処理（winit の更新または自前の破棄通知）を入れる。
  関連: `runtime/android/app/src/main/java/com/seedengine/runtime/MainActivity.java`、`app/render.rs`。
- [x] **タッチの本実装（段階A）** — 2026-09-24 記載 / 同日対応。複数指の状態機械（`input/touch/`）・指0 → マウスの駆動
  （Android）・マウス左ボタン → 指の合成（PC）・C# の `Input.TouchSupported` / `TouchCount` / `GetTouch(i)` / `Touches` を実装。
  キャンバス UI のポインタイベントは指0 がマウスを動かす経路で判定される。正典は docs/android.md §12。残りは直後に並べた各項目。
- [ ] **実機でのタッチ → スクリプト／ボタン反応の確認（段階B 待ち）** — 2026-09-24。2026-09-25 追記: 段階B でスクリプトが動くようになり、
  スクリプトの `Input.TouchCount` / `GetTouch`（Began / Moved / Ended・位置・移動量）はエミュレータと実機 Pixel 6a で確認した
  （`input tap` / `swipe` でモデルの大きさ・向きが変わる。docs/android.md §17.11）。**残りはキャンバス UI のボタン**（ポインタイベントの
  配信先がスクリプト）が実機で反応するところ。ボタン付きのキャンバスとスクリプトを載せたプロジェクトで確かめる。以下は記載時のメモ。
  Android ではスクリプトが動かないため、
  `Input.GetTouch` の値と、キャンバス UI のポインタイベント（配信先がスクリプト）でボタンが反応するところまでは実機で未確認。
  入力状態（指の一覧・タッチ由来のマウス）は `[SEED TOUCH FRAME]` ログで確認済み（エミュレータ）。段階B でスクリプトが動いたら、
  PC で使った確認用スクリプト（docs/android.md §12.4 の TouchProbe）を実機に載せて確かめる。
- [x] **実機（Pixel 6a）でタッチ状態・複数指を確認する** — 2026-09-24 記載 / 同日対応。合成タッチ列（`debug.seed.touch_test=1`）で `n=1 → 2 → 3` の
  追跡と指0 だけがマウスを駆動すること、`input tap` / `swipe` と実際の指のなぞりで `[SEED TOUCH FRAME]` とマウスの追従を確認（docs/android.md §12.5）。
  人の 2 本指以上（OS 経由の複数指）は実機では未確認（非 root では注入できない）。以下は記載時のメモ。エミュレータ（`input tap` / `swipe` / `input mouse tap`・
  コンソールの protocol B 3 本指・合成タッチ列）と PC では確認済みだが、実機は端末が私物として使用中で、起動直後にユーザーが別アプリへ
  移って最初のフレーム前にプロセスが終了したため未完（docs/android.md §12.5）。端末が空いているときに
  `debug.seed.touch_test=1`（合成 3 本指）と `input tap` / `swipe` で `[SEED TOUCH FRAME]` を確かめる。手順は §12.4。
- [ ] **ジェスチャ（ピンチ・回転・長押し・ダブルタップ・フリック）の組み込み API が無い（段階A 以降）** — 2026-09-24。
  今は `GetTouch` の位置と `DeltaPosition` からスクリプトで組み立てる（docs/scripting_api.md §6.5 の例）。
  `Touch.TapCount` / 押下時間 / 圧力（winit の `Touch::force`）も未公開。必要になったら `TouchState` に足して FFI の並びを拡張する。
  → **2026-09-28 の W2-2 で、タップ・長押し・ドラッグ・フリック（と押下の取り消し・指ごとの捕捉）は CanvasGesture のジェスチャーアリーナに入った**
  （キャンバス UI のノード向け。docs/input_gestures.md）。**ピンチ・回転・ダブルタップ・`Touch.TapCount`・押下時間・圧力は無いまま**（ピンチは W2-8 のグラフのズームで足す）。
  → **2026-09-28 の W2-8 でピンチを足した**（`CanvasGesture.pinch`・`OnGesturePinch*`・`GestureEvent.Scale`。input_gestures.md §2.6）。回転・ダブルタップ・`Touch.TapCount`・押下時間・圧力は無いまま。
- [ ] **キャンバス UI のポインタイベントは指0 の 1 本だけ（複数指の UI 操作は未対応）** — 2026-09-24。
  `pointer_events.rs` はマウス 1 本（＝指0）で Enter / Down / Click を判定する。2 本目以降の指でボタンを押す、
  2 つのボタンを同時に押す（仮想パッド＋ボタン）といった操作はできない。指ごとのポインタ状態を持たせる必要がある。
  また指を離した後もマウス位置が最後の位置に残るため、ボタンのホバー状態（OnPointerEnter 済み）が次に触れるまで残る（Unity と同じ挙動）。
  → 2026-09-28 の W2-2: **CanvasGesture を付けたノードは指ごと**（2 本の指で別々のボタンを同時に押せる・スクロールに負けた押下は取り消し）。
  `OnPointer*` 自体は従来のまま（指0 だけ）なので、複数指の UI は CanvasGesture で作る（docs/input_gestures.md §7）。
- [ ] **MCP の入力注入からタッチを合成しない** — 2026-09-24。エディタ／MCP の `INPUT_MOUSE_*` 注入は `Input` の注入層に入り、
  実マウスとは別に OR 合成される。PC の「マウス左ボタン → 指」の合成は実マウスだけが対象なので、AI の操作で
  `Input.GetTouch` を使うスクリプトを試すことはできない。注入層にもタッチ（指の追加・移動・離し）の操作を足すのが筋。
  関連: `runtime/src/engine/core/input/inject/`、`input/touch/bridge.rs`。
- [ ] **タッチパネル付き PC の実タッチは未検証** — 2026-09-24。Windows の winit は WM_TOUCH / WM_POINTER を `Touch` として送り、
  OS はタッチをマウスへ昇格して別に送る（winit は昇格マウスを区別しない）。`bridge.rs` は「実タッチが触れている間は
  マウスから指を合成しない・合成中の指は実タッチで Canceled にする」ことで二重に数えないようにしたが、実機（タッチパネル）では未確認。
  `Input.TouchSupported` はプラットフォーム単位の値なのでタッチパネル付き PC でも false。
- [x] **APK 内 pak（AssetManager）非対応（段階A）** — 2026-09-24 記載 / 同日対応。Windows の出力と同じ相対構成を APK の
  `assets/seed/` に入れ（`assets.pak` は Gradle の `noCompress` で非圧縮）、APK に pak があればパッケージ実行で起動する
  （`launch.rs`。無ければ従来の run-as 経路）。`PakReader` を `Read + Seek + Send` の読み口（`PakSource`）へ一般化し、
  配布物の読み口 `PackageSource`（Android は `apk_package::ApkPackageSource`＝AAssetManager）を `LaunchArgs.package_source`
  で渡す。pak はエディタ無しで `editor/tools/SeedPak`（パッケージ化ウィンドウと同じ `AssetPakBuilder`）が作り、
  `build_and_run.ps1 -ProjectDir` が APK へ入れる。エミュレータで push 無しの描画を確認。正典は docs/android.md §13。
  残りは直後に並べた各項目。
- [x] **実機（Pixel 6a）で APK 内 pak の起動を確認する** — 2026-09-24 記載 / 同日対応。push 無しの APK で「APK 内の pak で起動します … 非圧縮
  （APK 内の位置 52547452）」→ `asset_fs: packaged pak=apk:seed/assets.pak entries=3` → BrainStem が描画（約 18〜19 fps。docs/android.md §13.6）。
  以下は記載時のメモ。エミュレータ（x86_64）では push 無しで描画まで確認したが、
  実機は作業中ずっと私物として使用中（別アプリが前面）だったため未実施。arm64 の .so はビルド済み（コードは ABI に依存しない）。
  端末が空いているときに `build_and_run.ps1 -Abi arm64-v8a -Serial <実機> -ProjectDir <プロジェクト> -LogcatSeconds 30` で、
  起動ログの「APK 内の pak で起動します … 非圧縮」と `asset_fs: packaged pak=apk:seed/assets.pak` と描画を確かめる。
- [x] **パッケージ実行（APK 内 pak）ではセーブ・キャッシュを書けない（A-3 で対応）** — 2026-09-24 記載 / 同日対応。`engine/platform/paths.rs` の `PlatformPaths`（起動時に 1 回設定）へ Android の files・cache を渡し、セーブは `files/save/`、キャッシュは `/data/user/0/<pkg>/cache` に起動モードに関係なく書く（docs/android.md §14.1）。以下は記載時のメモ。`save/path.rs` と
  `package_layout::decide_cache_dir` はパッケージ実行（`asset_fs::is_packaged()`）で実行ファイル基準に切り替わるため、Android では
  `/system/bin/saved`・`/system/bin/caches` を指し、書き込みはエラーを返すだけで保存されない（落ちはしない）。
  開発用の経路（run-as のアセット）では従来どおり内部フォルダの `save/`・`cache/` に書ける。下の「保存先・キャッシュ…」と合わせて、
  プラットフォームの書き込み可能なフォルダを `engine/platform` 経由で渡す。
- [ ] **APK 内 pak の読み出しは読み口 1 本の直列化（低優先）** — 2026-09-24。`ApkAsset`（ndk の Asset に Send を足した包み）を
  `Mutex<PakReader>` で共有する（デスクトップのファイルと同じ）。非同期ロードのワーカーが増えて読み込みの直列化が効いてきたら、
  非圧縮の pak で取れる `Asset::open_file_descriptor` の fd に対する pread（位置を共有しない読み出し）に置き換えれば Mutex が要らなくなる。
  関連: `runtime/android/native/src/apk_package/`、`runtime/src/engine/pak/`。
- [x] **段階B: Android のパッケージ実行で事前コンパイル DLL を配布物から読む** — 2026-09-24 記載 / 2026-09-25 対応。同梱 .NET の起動材料
  （`LaunchArgs.embedded_clr`）があれば、`SEEDScripting.dll` / `SEEDUserScripts.dll` を「files/bin → 外部 files/bin → APK の bin/」の
  最初の置き場からバイト列で読む（`core/scripting/script_binaries.rs`・`app/script_boot.rs`・C# の `LoadPrecompiledScriptsFromBytes`。
  docs/android.md §17.7）。以下は記載時のメモ。`App::new` はスクリプトの経路を
  「`assets_root` があればソースをコンパイル、無ければ実行ファイルの隣の `bin/SEEDUserScripts.dll`」で分けるが、Android の
  パッケージ実行は `assets_root`（内部フォルダ。フォールバック先）を持つ。段階B では `LaunchArgs.package_source` の有無でも分け、
  DLL を配布物の `bin/`（APK の `assets/seed/bin/`）から `PackageSource` で読んで `load_assembly_from_bytes` へ渡す。
- [x] **`build_and_run.ps1 -LogFile` の logcat で日本語が文字化けする（既存）** — 2026-09-24 記載 / 2026-09-25 対応（段階C-1）。logcat は SeedAndroid
  （`editor/src/Android/Adb/AndroidLogcatSession.cs`）が adb の出力を UTF-8 のまま読み、`--log-file`（ps1 の `-LogFile`）へ UTF-8 で書く。
  子プロセスの出力は行ごとに UTF-8 か ANSI かを見分ける（`Processes/MixedEncodingLineReader.cs`）。エミュレータで保存した logcat の日本語が化けないことを確認。
  以下は記載時のメモ。pwsh が adb の UTF-8 出力を
  コンソールのコードページ（CP932）として読んでから `Set-Content -Encoding utf8` するため、エンジンの日本語ログが化ける
  （`[SEED INIT]` 等の ASCII 部分は読める）。pwsh 7.4 のネイティブコマンドのバイト列そのままのリダイレクト（`> file`）で保存するか、
  読み取りの間だけ `[Console]::OutputEncoding` を UTF-8 にする。
- [x] **保存先・キャッシュ・パイプラインキャッシュの置き場（段階A）** — 2026-09-24 記載 / 同日対応（A-3）。置き場は上の項目のとおり。パイプラインキャッシュはアダプタごとのファイル（`wgpu_pipeline_cache_*.bin`）を suspended で保存し、全生成箇所がキャッシュを受け取るようにした（`renderer/pipeline_cache/`。docs/android.md §14.3）。初期化の同期実行（ANR の恐れ）は下の新しい項目へ分けた。以下は記載時のメモ。セーブ（`save/path.rs`）と
  派生キャッシュ（`loader/asset_cache.rs`）は「アセットルートの親」規則でたまたまアプリ専用フォルダに落ちている。
  パイプラインキャッシュ（`renderer/mod.rs::pipeline_cache_path`）は実行ファイルの隣（Android では `/system/bin`）
  前提のため保存されず、毎回シェーダを作り直す（パイプライン生成がエミュレータで約 1.6 秒、実機 Pixel 6a で約 3.4 秒）。プラットフォームのデータフォルダを
  エンジンへ明示的に渡す仕組みにする。なお初期化（最初の resumed の `handle_resumed`）は android_main スレッドで
  同期に走り、その間に端末側でサーフェスが破棄されると UI スレッドはネイティブの応答を待ち続ける
  （native_app_glue の `android_app_set_window` は時間切れ無しで待つ）。起動が長いほど ANR の恐れがあるため、
  キャッシュ化と初期化の分割・非同期化も合わせて検討する。
  2026-09-24（タッチ実装の実機確認時）の実測: APK 更新直後の初回起動で、起動から `load_play_scene done` まで約 44 秒
  （モデルキャッシュの作り直し 3.3 秒を含む。残りの内訳は未調査。前面では別のゲームが動いていた）。その間に操作された
  戻るジェスチャ・タッチは初期化が終わってからまとめて届き、ActivityTaskManager に `Activity pause timeout`（起動 +6 秒）・
  `Activity stop timeout`（+19 秒）が出て、最初のフレームの提示前にバックグラウンドでプロセスが終了した（`app died, no saved state`）。
- [x] **バックグラウンド中もシミュレーションが回る（段階A）** — 2026-09-24 記載 / 同日、物理スレッドを対応（A-3）。`core/background_gate.rs` を suspended で立て、物理スレッド（3D / 2D）は `physics/background_pause.rs` で条件変数に眠る（エミュレータで背面 約 11 秒の CPU 時間が 3D 2 tick・2D 1 tick）。前面へ戻った最初のフレームは `Clock::forget_elapsed` で背面の時間を捨てる。音声・ゲームパッドは下の新しい項目へ分けた。以下は記載時のメモ。suspended 中はイベントループが
  `ControlFlow::Wait` で眠るが、物理スレッド（3D/2D）は回り続ける（エミュレータで計 20% 前後の CPU）。音声も止めていない。
  suspended で一時停止・resumed で再開する。関連: `app/surface_lifecycle.rs`。
- [x] **戻るキーの扱い（段階A）** — 2026-09-24 記載 / 同日対応（A-3）。Unity と同じく `KeyCode.Escape` として届ける（`core/input/key_remap.rs` の置き換え表を `PlatformTraits::key_remap` で選ぶ。デスクトップは空の表）。アプリは自動で終了しない（判断はスクリプト）。docs/android.md §14.5。以下は記載時のメモ。GameActivity はキーをネイティブへ渡し、winit が処理済み扱いにするため
  `onBackPressed` が呼ばれず何も起きない（`[SEED KEY] pressed logical=Named(BrowserBack)` とログに出るだけ）。
  ゲーム側に渡す／`moveTaskToBack` する等の方針を決める。
- [x] **画面の向き・安全領域（段階A）** — 2026-09-24 記載 / 同日対応（A-4）。プロジェクト設定の `screen_orientation`（both / portrait / landscape）を `build_and_run.ps1` が読み、`app/build.gradle.kts` の変換表でマニフェストの `screenOrientation`（fullSensor / sensorPortrait / sensorLandscape）へ焼き込む。スクリプトの `SEED.Screen`（`Width` / `Height` / `SafeArea` / `Orientation` / `DPI`）を追加し、Android は `ScreenReporter.java`（WindowInsets と Display.getRotation）→ JNI → `platform/screen/` → フレームごとの写しで値を渡す。エミュレータで 4 方向・切り欠きの overlay 3 種・内部解像度固定・向きの固定を確認（docs/android.md §15）。回転ロックの尊重と縦画面のキャンバスは下の新しい項目へ分けた。以下は記載時のメモ。マニフェストは `screenOrientation="fullSensor"`（端末の回転ロックを
  無視する）・切り欠きは `shortEdges`。プロジェクト設定からの向き指定、回転ロックの尊重（`fullUser`）、
  安全領域（切り欠き・ナビゲーションバー）を返す API が要る。内部解像度固定（`render_resolution=fixed`）と
  キャンバスの自動スケールが縦長画面で意図どおりかも未確認（どちらも project_settings の解像度基準）。
- [x] **キャンバス UI へ安全領域を自動で反映する仕組みが無い（段階A 以降）** — 2026-09-24（A-4 実装時）。
  → **2026-09-28 に W2-1b で解消**: 安全領域の部品 `CanvasSafeAreaComponent`（パネルの領域を `Screen.SafeArea` の内側へ辺ごとに縮める。
  子のアンカー・コンテナは縮めた領域を基準にする。フレームごとの画面の写しから読むので回転・システムバーに追従）。docs/canvas_camera_rework.md §6.5。
  以下は当時の記述。`Screen.SafeArea` は読めるが、
  キャンバスのアンカー・パディングは描画面全体が基準のままで、四隅にアンカーした UI はカメラ穴・ジェスチャーバーに重なる
  （エミュレータの四隅スプライトで確認）。Unity の SafeArea 用 RectTransform のように、キャンバス（またはアンカー基準）に
  「安全領域の内側に合わせる」選択肢を足す。値は `core/scripting/screen_bridge.rs` の写しか `platform::screen::snapshot` を使えば、
  スクリプトと同じフレーム内で一貫する。関連: `app/canvas_collect.rs`（`build_canvas_viewport_map` / ルートの自動解像度）。
- [ ] **縦画面でキャンバスの自動スケールが縦横別々に掛かる（縦持ち対応のゲームで見た目が崩れる）** — 2026-09-24（A-4 の確認時）。
  → 2026-09-28（W2-1b）: アプリ向けには**ルートキャンバスの単位 dp**（`CanvasComponent.unit`。縦横同じ倍率＝表示倍率で画素へ換算）と
  コンテナ・親に合わせる（`CanvasLayoutItem.fill_*`）で、縦横別の倍率に頼らずに組めるようになった（docs/canvas_camera_rework.md §6.3・§6.4）。
  px の `auto_scale` の等倍スケールの選択肢（既存のゲーム向け）は未着手のまま。以下は当時の記述。
  `auto_scale` のルートキャンバス（1920x1080 基準）を 1080x2400 の縦画面に出すと、横 0.5625 倍・縦 2.22 倍になり、正方形のスプライトが
  縦長に伸びる（レイアウトの付き直し自体は回転に追従している）。縦持ちのゲームでは子の `keep_aspect_ratio` を使うか縦長の基準解像度で作る
  必要がある。「縦横で小さい方の倍率に合わせる」等の等倍スケールの選択肢を検討する。関連: `components/canvas_component.rs`（`auto_scale`）。
- [ ] **端末の回転ロックを尊重する向きの選択肢が無い** — 2026-09-24（A-4 実装時）。`screen_orientation` は fullSensor / sensorPortrait /
  sensorLandscape（どれもセンサー優先で回転ロックを無視）だけ。ユーザーの回転ロックに従う `fullUser` / `userPortrait` / `userLandscape` を
  選べるようにするなら、`app/build.gradle.kts` の変換表・エディタの `ScreenOrientationSetting.Choices`・`build_and_run.ps1` の既定値の説明を揃えて足す。
- [ ] **回転の直後の 1 フレームほど、安全領域が全画面・向きが縦横比の値になる** — 2026-09-24（A-4 実装時・低優先）。Java の報告と winit の
  `Resized` が別経路で前後するため、描画面が先に変わったフレームは一致する報告が無い（エミュレータで 270 → 0・180 → 90 のときに 1 回ずつ）。
  気になるなら、描画面の大きさの変化を検出したら報告が届くまで写しの更新を 1〜2 フレーム待つ、などを検討する。
  関連: `platform/screen/report.rs`（`select_for_frame`）、`app/screen_publish.rs`。
- [x] **実機（Pixel 6a）で安全領域と向きを確認する** — 2026-09-24 記載 / 同日対応。実際のパンチホールは 132 px で、穴のある辺だけが内側へ寄ることを
  4 方向で確認（0 度 `safe=(0,132,1080,2205)` Portrait・90 度 `(132,0,2268,1017)` LandscapeLeft・180 度 `(0,0,1080,2268)` PortraitUpsideDown・
  270 度 `(0,0,2268,1017)` LandscapeRight・dpi 420。回転の設定は検証前の `default` / `lock 0` へ戻した。docs/android.md §15.5）。
  分割画面・自然な向きが横のタブレットは未確認のまま。以下は記載時のメモ。エミュレータ（内蔵の切り欠き・overlay の corner / double）では
  4 方向の値を確認したが、実機は作業中ずっと私物として使用中で、その後 USB の接続も外れたため未実施。実際のパンチホールで
  `[SEED SCREEN] Java 報告` の内訳（cutout）と `safe=` を 4 方向で確かめる（実機では overlay を触らない。回転の強制は §10 の
  `cmd window user-rotation lock` を使い、控えた設定へ必ず戻す）。分割画面・自然な向きが横のタブレットも未確認。
- [ ] **`Screen.DPI` は論理 DPI（densityDpi）だけ** — 2026-09-24（A-4 実装時・低優先）。Android の densityDpi は 120/160/240/320/420/480… の
  区分値で、物理的な大きさ（インチ）の計算には粗い。必要になったら `DisplayMetrics.xdpi / ydpi` を報告に足して別 API にする。
- [x] **音声（oboe）が鳴るか未確認（段階A）** — 2026-09-24 記載 / 同日対応（A-5）。エミュレータと実機で、テスト音源（AudioComponent の自動再生）が
  2ch・44100 Hz・F32 の AAudio ストリームで再生されることを確認（`dumpsys audio` の player `state:started`・audio_flinger のトラック `Active=yes`・
  ホスト PC 側でエミュレータの音声セッションに波形）。背面での停止・音声フォーカス・音量キーも対応した（docs/android.md §16）。以下は記載時のメモ。
  rodio → cpal → oboe（`c++_static`）は、実機で出力ストリームを開くところまで確認した（無音・音量 0 の AudioComponent で
  `OboeAudio: OboeVersion1.8.1` → `AAudioStreamBuilder_openStream() returns 0 = AAUDIO_OK`）。実際に音が鳴るか・バックグラウンドで止まるかは未確認。
- [ ] **出力デバイスの切り替え（ヘッドホンの抜き差し・Bluetooth）で音が止まったまま戻らない恐れ（未確認）** — 2026-09-24（A-5 実装時）。
  cpal 0.15 の oboe はストリームの切断（AAudio の `DISCONNECTED`）をエラーの通知で知らせるだけで開き直さない（エンジン側はログを出すだけ）。
  出力は `core/audio/output/`（`AudioOutput`）に閉じているので、エラーの通知を受けたら「ミキサーを残したまま出力ストリームだけを開き直す」
  （ミキサーの出口をコールバックから取り戻して新しいストリームへつなぐ）形で直せる。実機でヘッドホン・Bluetooth を抜き差しして確かめる。
- [ ] **音声フォーカスの恒久的な喪失（`AUDIOFOCUS_LOSS`）を端末の上で確かめていない** — 2026-09-24（A-5 実装時）。エミュレータでは SEED が前面のまま
  他のアプリに `AUDIOFOCUS_GAIN` を要求させる手段が無かった（分割画面は片側が空で解除、YouTube Music の音声プレビューは一時的な要求だけ）。
  状態の扱いは単体テスト（`output_policy.rs`）で固定済み。音楽アプリを入れた端末で、SEED を前面にしたまま通知の操作パネルから再生を始めて確かめる。
  実機では、音声フォーカスの喪失・ダッキング・音量キーも未実施（私物の端末で他のアプリを動かさない・音量を変えないため）。
- [ ] **AAudio の player が `USAGE_MEDIA`・性能モードが既定のまま（低優先）** — 2026-09-24（A-5 実装時）。cpal 0.15 の oboe は usage・content type・
  性能モード（`PerformanceMode::LowLatency`）を指定する口が無い（音声フォーカスの要求だけ `USAGE_GAME`）。リズムゲームで遅延が問題になったら、
  oboe を直接使う出力（`AudioOutput` の差し替え）か cpal の更新を検討する。ダッキングの下げ幅（×0.2、`output_policy::DUCKED_GAIN`）も定数のまま。
- [ ] **debug ビルドの音声のオーディオスレッドが重い（実機で 1 コアの約 34%）** — 2026-09-24（A-5 の確認時）。Pixel 6a の debug ビルドで、
  ループ音 1 つを鳴らしている間の `AudioTrack` スレッドが 10 秒で 336 tick（エミュレータは 68 tick）。rodio・cpal・symphonia・hound 等の
  音声系クレートはルートの `Cargo.toml` の `[profile.dev.package.*]` で最適化されていない（wgpu・物理系はされている）。
  実機の debug ビルドで音が途切れるようなら、音声系クレートに `opt-level = 2` を足す。
- [ ] **実機 logcat の無害なノイズ（低優先）** — 2026-09-24。Pixel 6a で毎回出る: SELinux の
  `avc: denied { search }`（cgroup / cgroup2 のルート。`android_main` スレッドが最初のフレーム時に 4 件。CPU 数の問い合わせ
  と推測・未特定）、wgpu の `Unrecognized present mode SHARED_DEMAND_REFRESH / SHARED_CONTINUOUS_REFRESH`
  （サーフェスの構成ごとに 2 行）、`Unable to find extension: VK_KHR_display` 等（インスタンス拡張の探索）。
  どれも動作に影響しないが、調査のときに紛らわしいので出どころを特定して抑えるか docs に明記しておく。
- [ ] **ゲームパッド非対応** — 2026-09-24。gilrs は Android 未対応で「パッド無効」で続行する。
  Android のゲームパッドは GameActivity の入力イベントから取る必要がある。
- [x] **スクリプト未対応（段階B）** — 2026-09-24 記載 / 2026-09-25 対応。.NET 10 の Android 版 CoreCLR（＋同じ版の bionic パックの
  hostfxr / hostpolicy）を APK に同梱し（`runtime/android/dotnet_runtime.json` → `build_and_run.ps1` が NuGet から組み立てる）、
  初回起動時に `files/dotnet/` へ展開して `Hostfxr::load_from_path` → `initialize_for_runtime_config_with_dotnet_root` →
  `load_assembly_from_bytes` → `get_delegate_loader` で起動する（`core/scripting/clr_host/embedded.rs`）。エミュレータと実機 Pixel 6a で
  スクリプト（OnStart・Update・タッチ・Screen・例外・暗号 API）が動くことを確認。Mono へも切り替えられる（エミュレータで確認）。
  正典は docs/android.md §17。残りは直後に並べた各項目。以下は記載時のメモ。`platform::CURRENT.scripting_supported == false` でスクリプトホストを探さない。
  `scripting/unsupported_platform.rs` の `load` は常に Err。linux-bionic 向け CoreCLR を同梱して `Hostfxr::load_from_path` で戻す。
- [ ] **同梱 .NET の展開と CLR の起動が android_main で同期に走る（段階B・2026-09-25）** — 初回の展開（実機で 0.5〜0.8 秒・インストール直後の
  エミュレータで 5.6 秒の例あり）と CLR の起動（60〜175 ms）・ユーザースクリプトの読み込みを、イベントループの前に android_main のスレッドで行う。
  その間 UI スレッドはサーフェスの受け渡し（`android_app_set_window`）で待つので、「起動の初期化が android_main スレッドで同期に走る」の
  項目と同じ ANR の恐れが増える。展開を別スレッドで先に始め、スクリプトの読み込みの直前で待つ等。関連: `runtime/android/native/src/dotnet_runtime/`、
  `app/script_boot.rs`。
- [ ] **同梱 .NET の BCL を絞っていない（APK が約 25 MB・展開後 58〜65 MB / ABI 増える）** — 2026-09-25（段階B）。パックの `lib/<TFM>/*.dll` を
  全部入れている（R2R 済み。arm64 65.2 MB・x86_64 57.9 MB）。スクリプトが使う範囲に絞れば小さくなるが、trim（ILLink）は動的に読むスクリプト DLL を
  壊す（docs/android.md §11.2）ので、「使う DLL の一覧」を事前コンパイル時に求めて deps.json と一緒に絞る等の別の方法が要る。
  2 ABI 入りの APK は assets の BCL が両方入る（lib/ と違って ABI で分けられない）。配布は arm64 だけの想定。
- [ ] **アプリのデータフォルダのファイルを実行する方式が将来の Android で禁止される恐れ** — 2026-09-25（段階B）。BCL の R2R の DLL（と `copy` の .so）は
  `files/dotnet/` から実行可能として読み込まれ、SELinux は許可しつつ記録する（`avc: granted { execute } … app_data_file`。ファイルごとに 1 行）。
  auditallow は「監視中」の印で、将来禁止されると hostfxr ＋ ファイルの dotnet-root の方式は使えない。そのときは .NET for Android と同じく
  アセンブリをメモリから渡す自前のホスト（`coreclr_initialize` ＋ 外部アセンブリのプローブ）か NativeAOT（段階D）へ移る。
  2026-09-26 追記: NativeAOT は評価した（docs/android.md §24.12。配布用だけ AOT にすれば `.so` 1 つで済み、この方式の心配も無くなる。未実装）。
  あわせて、この記録で logcat が埋まる（起動ごとに十数行）。
- [ ] **TLS（SslStream・HttpClient の https）と X509 を Android で確かめていない** — 2026-09-25（段階B）。暗号ライブラリの JNI の初期化
  （`DotnetJniLibraries.java` の `System.loadLibrary` ＋ パックの .jar）で SHA256・RandomNumberGenerator は動いた。TLS は `DotnetProxyTrustManager` を
  使うので同じ仕組みで動く見込みだが未確認。`native_library_mode = "copy"` では暗号 API がプロセスごと落ちる（docs/android.md §17.8）。
- [ ] **Mono の実機での確認・Mono の暗号 API** — 2026-09-25（段階B）。Mono（`dotnet_runtime = "mono"`）はエミュレータで起動とスクリプトの実行を
  確かめた（CLR の起動 0.8〜1.4 秒・ユーザースクリプトの読み込み 0.4 秒で CoreCLR の約 10 倍遅い）。実機（ヒープのタグ付けの対策込み）は未確認。
  暗号 API は linux-bionic パックが OpenSSL 版のため動かない（§11.1）。
- [ ] **Android でスクリプトのデバッグ（netcoredbg のアタッチ）ができない** — 2026-09-25（段階B）。`DOTNET_EnableDiagnostics=0` で診断機能を止め、
  DAC（`libmscordaccore.so`・`libmscordbi.so`）も入れていない。必要になったら、デバッグ用の APK だけ診断機能と DAC を入れ、adb forward で
  netcoredbg を付ける形を検討する。
- [ ] **Android の `bin/` に Roslyn の DLL（約 9 MB）が入る（読み込まれない）** — 2026-09-25（段階B）。SeedPak `--scripts` は PC の配布物と同じ
  ScriptPackager の出力（スクリプトホスト一式）を置くため。Android ではその場コンパイルをしないので不要（APK が約 3.5 MB・`-PushScripts` の転送が 9 MB 増える）。
  SeedPak に「実行時に要るものだけ」の指定を足すか、Android の同梱で `SEEDScripting.dll` / `SEEDUserScripts.dll` / runtimeconfig だけに絞る。
- [ ] **`-PushScripts` の 1 回が約 9 秒（うち SeedPak の `dotnet run` のビルド確認とコンパイルが 5〜6 秒）** — 2026-09-25（段階B）。SeedPak は
  `scripting/` を参照しているので `dotnet run` のたびにビルドの確認が走る。段階C のエディタ統合では、エディタが持っている ScriptPackager を
  直接呼ぶ（プロセスを起動しない）形にすれば数百 ms になる見込み。
  2026-09-25（段階C-1）: SeedAndroid の `push` はエミュレータで 8.3 秒（SeedPak＋転送 6.5・起動 1.8）。中核（`Steps/SeedPakProcess.cs`）は SeedPak を
  子プロセスで呼ぶままにした（ps1 と同じ経路・中断で止められる）。C-2 で同じプロセスの `AssetPakBuilder` / `ScriptPackager` を呼ぶ実装に差し替える
  （`PackageContentStep` / `PushScriptsStep` の中だけの変更で済む形にしてある）。
  2026-09-25 追記（段階C-2）: C-2 でも子プロセスのまま（エディタの実行・パッケージ化ウィンドウとも中核の `SeedPakProcess` を通る）。
  中核は SeedAndroid にもリンクされ、SeedAndroid は Roslyn（ScriptPackager）を参照しないため、差し替えるなら「pak とスクリプトを作る窓口」を
  中核に置き、エディタだけが同じプロセスの実装を渡す形にする。Play 中のエディタ（PC の実行）が `scripting/bin` の DLL を握っていると
  SeedPak の `dotnet run` の再ビルドが失敗し得る点は未確認のまま。
- [ ] **外部アプリ専用フォルダの `files/bin/` は実機で読めない（候補としては残している）** — 2026-09-25（段階B）。`adb push` で置いた DLL は
  Pixel 6a（Android 16）で Permission denied（§4.5 と同じ理由）。`-PushScripts` は run-as の内部フォルダへ送るように変えた。外部フォルダは
  エミュレータで手で置くとき用の候補で、読めなければ警告して飛ばす（docs/android.md §17.7）。
- [ ] **C# スクリプトのログのタグが CoreCLR（DOTNET）と Mono（SEED）で違う** — 2026-09-25（段階B）。CoreCLR の Android 版 `Console` は logcat へ
  タグ `DOTNET` で直接書き、Mono は標準出力（→ エンジンの転送でタグ `SEED`）。`build_and_run.ps1` の logcat の絞り込みには両方を入れた。
  揃えるなら、スクリプトホストの `SEED.Debug.Log` をエンジンのログ関数（FFI）経由にする。
- [x] **エディタのパッケージ化ウィンドウの Android 出力が実働しない（段階C）** — 2026-09-24 記載 / 2026-09-25 対応（段階C-2）。ウィンドウは
  中核を `Goal = Build`（端末は使わない）で呼び、できた APK を `{出力フォルダ}/{ゲーム名}/{ゲーム名}-{ABI}-debug.apk` へ写す（デバッグ署名。
  画面に明記）。NDK のパスの欄と `android.ndk_path` は廃止、アーキテクチャは「ABI」（arm64-v8a / x86_64 / 両方）、ビルド種別は「Rust の最適化」。
  道具の見つかり方を欄に出す。docs/android.md §20.6・docs/packaging.md §10.3。以下は記載時のメモ。`PackagingWindow.xaml.cs` は
  runtime/ で `cargo build --target <triple>` して `runtime/target/<triple>/<profile>/libSEED.so` を拾う前提だが、
  runtime/ 単体を Android 向けにビルドすると winit の Android 機能（android-game-activity）が有効にならず、
  android-activity の compile_error で失敗する（段階0 以前は nethost-sys の build.rs の panic で失敗していた）。
  .so は `runtime/android/native`（cargo ndk）が作る（出力先の並びは同じ `runtime/target/<triple>/<profile>/`）。
  あわせて APK 化（Gradle）とアセットの同梱が要る。段階0 ではエディタには手を入れていない。
  2026-09-24 追記: アセットの同梱の形は決まった（APK の `assets/seed/assets.pak`。docs/android.md §13）。ウィンドウからは
  `AssetPakBuilder`（SeedPak と共通）で `runtime/android/app/src/main/assets/seed/` へ書き、`build_and_run.ps1` の工程を呼べばよい。
  2026-09-25 追記（段階C-1）: 工程は C# の中核 `editor/src/Android/`（エディタ本体に入っている）になった。ウィンドウからは
  `AndroidRunPipeline.RunAsync(new AndroidRunRequest { Goal = Build, ProjectDir = …, Abis = … }, progress, ct)` を呼び、
  出来た `runtime/android/app/build/outputs/apk/debug/app-debug.apk` を出力フォルダへ写せばよい（署名・リリース版は段階D）。
- [ ] **debug の libSEED.so が約 445 MB / ABI（段階C）** — 2026-09-24。フルデバッグ情報のため。APK へはシンボルを削って
  約 53 MB で入るが、ビルド・コピー・削りの時間が掛かる。Android の開発ビルドだけ `debug = "line-tables-only"` 等にする案。
- [ ] **Android ビルドでだけ出る警告（Windows 専用コードの cfg 漏れ）** — 2026-09-24。`input/cursor_visibility.rs` の
  `MAX_COUNTER_STEPS`、`startup_log/wide.rs` の `NUL_UTF16`、`frame_renderer.rs` の `my_hwnd` が未使用になる。実害なし・低優先。
- [ ] **logcat が既存の常時診断ログで埋まる** — 2026-09-24。`app/play_diag.rs` の `PLAY_DIAG_ENABLED` が `true || ...` で常時有効
  （`[PLAY_HB]` が毎秒）、物理が動いていると `[PERF f=...]` も 60 フレームごとに出る。Windows 版でも同じ。撤去予定の一時診断のはず。
- [ ] **`runtime/Cargo.lock` はワークスペース化前の残骸** — 2026-09-24。ワークスペースの lock はルートの `Cargo.lock`。
  runtime/ 側は 2026-05 から更新されておらず参照もされない。削除してよいか確認する（低優先）。
- [x] **実機（Pixel 6a）で A-3 を確認する（特にパイプラインキャッシュの短縮幅）** — 2026-09-24 記載 / 同日対応。パイプライン生成は
  キャッシュ無し 3595 ms（インストール直後）/ 3014 ms（ファイルを消した起動）→ キャッシュ有り 528 / 522 ms（約 85% 減。キャッシュ 618 KiB・保存 2.6〜6.2 ms）。
  書き込み先・セーブ（ホーム → am kill で毎回残る／前面のまま force-stop では残らない）・物理スレッドの停止と再開・戻るキー（Escape の down / up）も
  確認した（docs/android.md §14.7）。以下は記載時のメモ。
  エミュレータではセーブ（suspended・onDestroy の JNI）・書き込み先・物理停止・戻るキーを確認したが、実機は作業中ずっと私物として
  使用中（別アプリが前面）で、その後 USB の接続も外れたため未実施。arm64 の .so はビルド済み（コードは ABI に依存しない）。
  エミュレータのパイプライン生成はホスト（gfxstream → NVIDIA）のドライバが自前のキャッシュを持つため、キャッシュの有無で約 7% しか
  変わらない（1236 → 1150 ms 前後）。実機（Mali-G78。段階0 でパイプライン生成 約 3.4 秒）で「初回と 2 回目」「キャッシュを消した起動」を
  比べる。手順は docs/android.md §14.6。
- [ ] **`build_and_run.ps1` の起動（毎回 force-stop）ではパイプラインキャッシュが保存されない** — 2026-09-24。
  保存は suspended（背面へ回る）と Drop だけで、force-stop はどちらも起こさない。開発中にホームへ戻さないまま .so の差し替えを繰り返すと、
  毎回シェーダを作り直す。最初の数フレームを描いた後にも 1 回保存する（内容が同じなら書かない仕組みは既にある）か、スクリプトの
  再起動前に `input keyevent KEYCODE_HOME` を挟む。関連: `runtime/src/engine/core/renderer/pipeline_cache/mod.rs`、`runtime/android/build_and_run.ps1`。
- [ ] **起動の初期化が android_main スレッドで同期に走る（ANR の恐れ）** — 2026-09-24（「保存先・キャッシュ…」の項目から分離）。
  最初の resumed の `handle_resumed` がパイプライン生成・シーン読込まで同期で行い、その間に端末側でサーフェスが破棄されると UI スレッドは
  ネイティブの応答を待ち続ける（native_app_glue の `android_app_set_window` は時間切れ無し）。パイプラインキャッシュで短くはなるが、
  初期化の分割・非同期化は未着手。記録: APK 更新直後の初回起動で 44 秒かかり、`Activity pause timeout`・`stop timeout` の後に
  プロセスが終了した例がある（上の記載時のメモ）。
- [ ] **バックグラウンド中もゲームパッドのスレッドが動く／`[PLAY_WD]` が誤報する** — 2026-09-24。
  A-3 で物理スレッドは止めた。音声は A-5（2026-09-24）で出力ストリームごと一時停止するようにした（背面の `AudioTrack` スレッドの CPU 時間は
  10 秒で 0 tick。docs/android.md §16）。残りはゲームパッド（gilrs。Android では無効だがポーリングスレッドは残る）で、止めるなら
  `core::background_gate` を見る。以下は記載時のメモ。A-3 の時点では音声（rodio → cpal → oboe）も背面で動いていた。
  また `app/play_diag.rs` のフレーム監視（`[PLAY_WD] stuck at stage=frame_end … (A)イベントループスレッド自体がブロック`）が、
  背面でイベントループが Wait に入っている間ずっと 5 秒ごとに誤報する（撤去予定の一時診断。背面中は黙らせるか撤去する）。
- [x] **段階C-1: ビルド・配置・起動の手順を C# へ移す（SeedAndroid）** — 2026-09-25 対応。`runtime/android/build_and_run.ps1` の中身
  （道具の解決・cargo ndk・SeedPak・同梱 .NET の組み立て・Gradle・adb install・run-as の転送・am start・logcat）を WPF 非依存の中核
  `editor/src/Android/` に移し（エディタ本体にも入る）、コンソールツール `editor/tools/SeedAndroid`（devices / build / install / run / push / stop / logcat）
  から使う。ps1 は従来の引数で SeedAndroid を呼ぶだけのラッパー。入力の指紋と置き場の記録から変わっていない工程を自動で飛ばす
  （エミュレータで 2 回目の run が 75 → 15 秒）。アプリの識別情報（`project_settings.json` の `android` 節・プロジェクト設定ウィンドウ）も入れた。
  正典は docs/android.md §4.6・§5・§18・§19。単体テスト `editor/tests/AndroidPipelineTests`。
- [x] **段階C-2: エディタの「実行」に実行先セレクタ（PC / 実機 / エミュレータ）を付ける** — 2026-09-25 記載 / 同日対応。実行ボタンの隣の
  実行先コンボ（開くたびに adb で探し直す・選べない端末は理由付きで無効・前回の選択はプロジェクトごと）、状態機械 Idle → Building → Running →
  Stopping → Idle（アプリ側の終了は pidof で検知）、進み具合と logcat を色付きで Output パネルへ、停止ボタン（ビルドの中止・端末のアプリの停止）、
  PC の実行との排他、パッケージ化ウィンドウの Android 出力。判断はすべて WPF 非依存（`editor/src/AndroidRun/`）で単体テスト `editor/tests/AndroidRunUiTests`。
  正典は docs/android.md §20。以下は記載時のメモ。中核の入口は
  docs/android.md §4.6 の表（`AndroidDeviceActions.ListDevicesAsync` / `AndroidRunPipeline.RunAsync` / `StopAppAsync`・`AndroidRunState.LastTarget`）。
  イベント（`AndroidPipelineEvent`）を Output パネルへ、`CancellationToken` を停止ボタンへつなぐ。エディタからの pak とスクリプトは同じプロセスの
  `AssetPakBuilder` / `ScriptPackager` を呼ぶ形にすると速い（上の `-PushScripts` の項目。C-2 では未実施）。Play 中のエディタが `scripting/bin` の DLL を握っていると
  SeedPak の `dotnet run` の再ビルドが失敗し得る点にも注意（未確認）。
- [ ] **段階C-2 の端末での確認（エディタから実行・停止・アプリの終了の検知・Output）** — 2026-09-25（段階C-2）。作業中は端末が無く、
  PC のメモリ不足でエミュレータも起動しなかったため、エディタの Android 実行は単体テスト（偽の中核）と中核の `Goal = Build`・端末 0 台の一覧までしか
  確かめていない。端末（エミュレータ・Pixel 6a）で、(1) コンボに端末が出て選べる (2) 実行 → Output に工程の行・「変更なし」・logcat（DOTNET は
  フィルタ「ゲーム」）(3) 停止ボタンで `pidof` が空になる (4) ~~端末の戻るキーでアプリを終えると数秒で Idle に戻る~~ (5) ビルド中の停止で子プロセスが残らない
  (6) エディタを開き直すと前回の端末が選ばれている、を確かめる（手順は docs/android.md §20・報告の GUI 確認手順）。
  2026-09-25 追記（段階C-4）: 中核の部分は実機 Pixel 6a で確かめた（`stop` の後 `pidof` が空・`IsAppRunningAsync` が False。docs/android.md §20.14）。
  (4) は前提が誤り: **戻るキーではアプリは終わらない**（§14.5 のとおりスクリプトへ Escape として渡すだけ。実機で同じ pid のまま・`IsAppRunningAsync` は True のまま）。
  アプリの終了の検知は、クラッシュ・強制停止（設定のアプリ情報から）等でプロセスが終わったときに確かめる。エディタの画面での (1)〜(6) は未確認のまま。
- [x] **エディタの Android 実行は開始シーンから・未保存の変更は警告だけ** — 2026-09-25 記載（段階C-2）/ 同日対応（段階C-3）。
  PC の Play と同じ「開いているシーン」から起動する（am start の extra `seed.scene` → MainActivity → JNI → `LaunchArgs.scene_path`。
  pak に無ければ端末が警告して開始シーン）。未保存の変更があれば「保存して実行 / 保存せず実行 / キャンセル」を尋ねる。正典は docs/android.md §20.10・§20.11。
  Rust がツールバーのビルド構成と連動しない件は下の項目へ分けた。
- [ ] **エディタの Android 実行は Rust を debug で作る（ツールバーのビルド構成と連動しない）** — 2026-09-25（段階C-2。C-3 で上の項目から分けた・低優先）。
  ツールバーの Debug / Develop / Release は PC のランタイム用。Android も連動させるなら `AndroidEditorRunRequests.ForPlay` に `Release` 等を渡す
  （.so の作り直しに数分かかるので、切り替えたときの初回の遅さを Output に出す）。
- [ ] **実行先の一覧は開いたときだけ更新（接続・切断の自動検知なし）** — 2026-09-25（段階C-2・低優先）。`adb track-devices` で見張れば、
  端末を挿した・外したときにコンボと実行ボタンを自動で更新できる（選んでいた端末が外れた後も、開き直すまでは「使える」表示のまま。
  C-3 からは実行すればエミュレータで実行する）。
- [ ] **エミュレータの AVD を選ぶ設定の画面が無い** — 2026-09-25（段階C-3）。端末が無いときに起動する AVD はエディタの設定
  `editor/settings/editor_preferences.json` の `"android": { "emulator_avd": "…" }`（未設定なら `seed_pixel6_api35`、無ければ `emulator -list-avds` の先頭）。
  環境設定の窓（`EditorPreferencesWindow`）か実行先セレクタの右クリックに、`emulator -list-avds` の一覧から選ぶ欄を足す。SeedAndroid はこの設定を読まず `--avd` で指定する
  （同じ既定の規則。読ませるならリポジトリの `editor/settings/` を探す処理が要る）。
- [x] **Android の実行の一時停止が無い（段階D で IPC を TCP にするときに扱う）** — 2026-09-25 記載（段階C-3 で利用者の要望を受けて）/ 同日対応（段階D-1）。
  ランタイムの IPC に TCP の通信路を足し（`runtime/src/engine/core/app_base/ipc_transport/`。127.0.0.1 で 1 本ずつ accept・挨拶 `READY:0`・切断で一時停止を解く・
  `DETACH` の後は据え置き）、エディタは起動の後に adb forward 越しにつないで、PC の Play と同じ実行バーで `PAUSE` / `RESUME` を送る（`ANDROID PLAY` / `ANDROID PAUSE`）。
  行の送受信は PC の名前付きパイプと共通（`editor/src/Ipc/IpcLineChannel.cs`）。SeedAndroid に `pause` / `resume` / `screenshot`。正典は docs/android.md §21。
  以下は記載時のメモ。PC の Play は実行ボタンで一時停止できるが、Android は実行中の実行ボタンが「一時停止の絵柄で無効」のまま。端末のランタイムとエディタをつなぐ
  IPC（PC は名前付きパイプ）を TCP（adb forward）にする段階D で、一時停止・再開・シーンの読み直しを同じ命令で送れるようにする
  （シーンの読み直しは下の「Android の実行中のシーンの読み直し」へ分けた）。
- [x] **Android の一時停止中の画面がエディタの見た目（デバッグカメラ・グリッド）になる** — 2026-09-25（段階D-1 の実機確認で気付いた・要判断）/ 同日対応（段階D-1 の追加）。
  TCP の `PAUSE` はランタイムの `remote_paused`（ゲームの時間・物理・スクリプト・アニメーション・入力の注入だけを止める。判定は `App::is_simulation_paused()`）にし、
  描画の条件の `paused` は PC の PAUSE（名前付きパイプ）だけが立てる → 端末はゲームの画面のまま、PC の PAUSE は従来どおりエディタの見た目
  （`ipc_transport::pause_keeps_game_view`。docs/android.md §21.4）。PC の TCP の通信路で、一時停止中の絵が実行中とほぼ同じ（グリッド無し）なのを確かめた。
  ~~実機の画面では未確認~~ → 2026-09-26 に実機（Pixel 6a）でも確かめた（下の「実機で一時停止中の画面を確かめていない」）。以下は記載時のメモ。
  一時停止はランタイムの `paused`（PC の PAUSE と同じ）なので、描画がデバッグカメラ（一時停止の瞬間のメインカメラの位置・向きに合わせる）と
  エディタの見た目に切り替わる。実機の `proj_probe` では、構図はそのままでエディタのグリッドが床に重なった（docs/android.md §21.9。画角・ビューポートが
  メインカメラと違うシーンでは構図も変わり得る）。PC は一時停止でウィンドウをビューポートへ埋め込み、デバッグカメラで見回せるが、端末ではデバッグカメラを動かせない。
  「ゲームの画面のまま止める」にするなら、`paused` のうち「時間・物理・スクリプトを止める」と「エディタの見た目に切り替える」を分ける
  （`frame_renderer.rs` 等の `mode == Play && !paused` の条件が約 50 か所）か、一時停止の命令に「見た目は変えない」版を足す。
- [x] **Android の IPC の待ち受けに、端末の他のアプリもつなげる（デバッグ版だけ・要判断）** — 2026-09-25（段階D-1 の自己点検で気付いた）/ 同日対応（段階D-1 の追加）。
  起動ごとに使い捨ての接続トークン（128 ビットの乱数）を起動オプション `seed.ipc_token` で渡し、ランタイムは最初の行 `HELLO:<トークン>` が合った接続だけに
  `READY:0` を返して命令を受け付ける（違う・来ない〈5 秒〉は `IPC_DENIED:<理由>` で閉じる。`ipc_transport/auth.rs`）。SeedAndroid の pause 等は
  `cache/android/run_state.json` の `ipc_launches` に記録したトークンを使う。トークンは Output・logcat に出さない。実機で違うトークン・HELLO 無し・無言の接続が
  断られるのを確かめた（docs/android.md §21.11・§21.12）。残り: 他のアプリが先につなぐと HELLO の時間切れまでエディタを待たせられる（下の「1 本だけ」）。以下は記載時のメモ。
  bind は 127.0.0.1 なので端末の外（Wi-Fi 等）からは届かないが、同じ端末で INTERNET 権限を持つアプリは 127.0.0.1:52735 へつなげ、IPC の命令
  （一時停止・`INPUT_*`・`SAVE_DATA`・`SCREENSHOT` の書き先の指定・`STOP` 等）を送れる（影響は自分のアプリのサンドボックスの中。待ち受けるのは
  デバッグ版の APK を `run` / `push` で起動したときだけ）。対策案: エディタ／SeedAndroid が実行ごとに作る使い捨てのトークンを起動オプション
  （`seed.ipc_token`）で渡し、ランタイムは最初の 1 行（例 `HELLO:<トークン>`）が合うまで挨拶も命令も受けない。SeedAndroid の `pause` 等は
  プロジェクトの `cache/android/run_state.json` に残したトークンを使う。
- [x] **実機で一時停止中の画面（ゲームの画面のまま・グリッド無し）を確かめていない** — 2026-09-25 記載（段階D-1 の追加）/ 2026-09-26 対応。Pixel 6a・`proj_probe`・
  開発用の APK（debug の .so・targetSdk 36）で `run` → `screenshot`（実行中）→ `pause` → `screenshot` ×2（6 秒あけて）→ `resume` → `screenshot`（再開後）。4 枚が画素一致
  （8 階調を超える差 0 画素）＝一時停止中もゲームのカメラのまま・グリッド無し。スクリプトの毎秒のログは一時停止中に止まり（直前 `t=36.1s` → 15 秒後の再開の直後
  `t=37.1s`）、描画は続いた。docs/android.md §21.12。以下は記載時のメモ。確認のとき実機（Pixel 6a）の画面が消えて
  ロック中で、アプリが描画できなかった（`presented_frames total=0`。メインループが回らないので `PAUSE` / `RESUME` も処理されない）。接続トークンの照合は実機で
  確かめた（照合は受け付けのスレッドで行うので描画が止まっていても動く）。次に実機のロックが解けて前面がランチャーのときに、SeedAndroid で
  `run` → `pause` → `screenshot` → グリッドが無いこと（段階D-1 の時点の絵〈グリッドあり〉と比べる）→ `resume` → `stop` を行う。
- [ ] **一時停止中も時間で動くシェーダ（水面等）が動き続ける（PC・Android 共通・要判断）** — 2026-09-25（段階D-1 の追加で気付いた・低優先）。
  `frame_renderer.rs` の `shader_time` は一時停止中に壁時計（`ambient_time`）へ切り替わる（編集中・PC の PAUSE で水面等を止めないための既存の規則）。
  端末は一時停止中もゲームの画面のままになったので、ゲームの時間は止まっているのに水面等だけ動いて見える。止めたいなら `remote_paused` のときは
  `anim_time` のまま（進めない）にする。
- [ ] **ステップ実行（一時停止中に 1 フレームだけ進める）が PC にも Android にも無い** — 2026-09-25（段階D-1）。ランタイムの IPC に `STEP` の命令は無く、
  実行バーのステップ系のボタンは C# スクリプトのデバッガ（netcoredbg）のもの。足すなら ランタイムの命令（1 フレームだけ `paused` を外して時間を 1 フレーム分進める）・
  物理スレッドの 1 ステップ・実行バーのボタン（PC と Android 共通）・SeedAndroid の `step` を合わせて設計する。
- [ ] **エディタの AI ツールから Android の実行を撮る・操作する経路が無い** — 2026-09-25（段階D-1）。`seed_screenshot`・`game_input_*`・`seed_save_*` 等は PC の
  ランタイム（`RuntimeManager` のパイプ）へだけ送る。Android の実行中に同じ命令を TCP の通信路（`AndroidRunController` が持つ `IAndroidIpcLink`）へ回すには、
  AI ホスト（`MainWindow.AiHost.cs`）の送り先を実行先で切り替え、端末のパスとの受け渡し（スクリーンショットは端末に書いて run-as で取り出す。
  `Android/Ipc/AndroidIpcScreenshot.cs` を使える）を足す。SeedAndroid の `screenshot` は使える。
- [ ] **IPC の撮影（`SCREENSHOT`）のたびにメインループが止まる（低優先）** — 2026-09-26（実機確認で気付いた）。Pixel 6a・1080x2400 で、開発用（debug の .so）は
  1 回あたり約 0.46〜0.59 秒（5 回）、最適化した .so では約 0.14〜0.31 秒（4 回）。その間の fps（`[SEED HEARTBEAT]`・スクリプトの `Time`）が下がるので、fps を測るときは
  撮らない。GPU からの読み戻しの完了を待つこと（`renderer/screenshot.rs` は `map_async` の後に `device.poll(Wait)` で待つ作り）と PNG の書き出しが効いていると
  見られる（内訳は未計測）。直すなら読み戻しの完了を次のフレームで拾い、PNG の符号化と書き出しを別スレッドへ回す。docs/android.md §21.10。
- [ ] **Android の IPC は 1 本だけ（エディタの実行中は SeedAndroid の pause 等が使えない）・待ち行列に残った古い接続** — 2026-09-25（段階D-1・低優先）。
  ランタイムは 1 本ずつ accept するので、2 本目は挨拶が来ずに時間切れ（理由付きのエラー）になる。あきらめて閉じた 2 本目は OS の待ち行列に残り、1 本目が切れた後に
  受け付けられてすぐ切れる（黙って切れた扱いなので、一時停止中なら再開する）。複数の接続を受ける・新しい接続で古い接続を置き換える、のどちらかにするなら
  `ipc_transport/tcp.rs` と `session_policy.rs` を直す。
  段階D-1 の追加（接続トークン）の後も、同じ端末の他のアプリが先につないで何も送らなければ、ランタイムはその接続を HELLO の時間切れ（5 秒）まで持つので、
  その間はエディタがつなげない（命令は送れない。繰り返されるとエディタの接続〈20 秒までやり直す〉が諦めることがある）。照合の間も次の接続を受け付ける形にすれば防げる。
- [ ] **ランチャーから起動したアプリには IPC でつなげない** — 2026-09-25（段階D-1・低優先）。待ち受けは起動オプション（`seed.ipc_port`。デバッグ版の APK だけ）が
  あるときだけなので、ランチャー・`install` の後の手動の起動では待ち受けない。デバッグ版は既定で待ち受ける、にするなら `launch.rs` の決め方を変える
  （その場合も bind は 127.0.0.1 だけ・INTERNET 権限はデバッグ版だけのまま）。
- [ ] **Android でエディタから実行しても `SEED.Application.IsEditorPlay` は false** — 2026-09-25（段階D-1・要判断）。判定は「Play かつ名前付きパイプでつながった」
  （`app_env.rs`）のままにした（TCP は起動の時点ではつながっていない。段階D-1 の前と同じ）。エディタからの Android の実行でもデバッグ表示を出したいなら、
  起動オプションに「エディタからの実行」を足して判定に使う。
- [x] **Android の実行中のシーンの読み直し（IPC）が無い** — 2026-09-25（段階C-3 の「一時停止が無い」の項目から分けた）/ 同日対応（段階D の実行中の差し替え）。
  デバッグ版は端末の内部 `files/assets` を pak より先に読む上書き層にし（`asset_fs::FilesystemLayer::Overlay`）、エディタは Android の実行中に保存したシーンと
  参照するアセットのうち端末と違うものだけを run-as で送って `RELOAD_SCENE:<相対パス>` を送る（アセットは `RELOAD_ASSET:<相対パス>`、スクリプトは DLL を
  `files/bin` へ送って `RELOAD_SCRIPTS`）。ランタイムは要求をフレームの境界でまとめて適用する（シーンの読み直しは 1 回）。`run` は上書きを消す。
  SeedAndroid に `push --assets` / `reload`。正典は docs/android.md §23。PC の TCP と実機（Pixel 6a。SeedAndroid と、エディタの段取りを WPF 抜きで動かすプローブ）で
  確かめた（エディタの画面〈WPF〉からは未確認）。
  以下は記載時のメモ。PC の `LOAD_SCENE` と同じ命令を送れば読み直せるが、端末の APK の pak にある版を読むだけ（エディタで保存した最新の内容は届かない。
  届けるには pak の差し替えかアセットの転送が要る）。
- [x] **実行中の差し替えを実機で確かめていない** — 2026-09-25 記載（段階D の実行中の差し替え）/ 2026-09-26 対応。Pixel 6a・`proj_probe`・SeedAndroid で、
  モデルの上書き → `push --assets`（0.9 秒・1 ファイル）→ `reload asset`（応答 58 ms・端末 42.1 ms）で画面のモデルが変わり、シーンの光の色 → `reload scene`
  （応答 39 ms・端末 15.2 ms）、スクリプトの文言 v2 → v3 → `reload scripts`（11.2 秒。ほぼ SeedPak と 9.1 MB の転送。端末の読み直し 29.7 ms）で同じプロセスのまま
  `[PROBE v3]`。上書きの解除はインストールの工程（APK を入れ直した run）と起動の工程（同じ APK の run）の両方で 2 行が出て、`files/` に `assets`・`bin` が無く、
  画面は最初と同一（PNG の MD5 一致）。docs/android.md §23.10。
- [x] **実行中の差し替えをエディタの画面から確かめていない** — 2026-09-26 記載（段階D の実行中の差し替え）/ 同日対応（エディタの段取りを実機で。画面〈WPF〉の
  目視は下の「実行中の差し替えをエディタの画面（WPF）で目視していない」へ分けた）。WPF 抜きのプローブで `AndroidRunController`＋`AndroidHotReloadController`＋
  本物の中核を動かし、Pixel 6a・`proj_probe` でモデル（保存から差し替え完了まで 1.04 秒・端末 15.5 ms）→ シーン（0.83 秒・端末 9.6 ms）→ スクリプト（5.17 秒・
  同じプロセスのまま `[PROBE v4] OnStart`）の順に保存した。Output の行は docs/android.md §23.7 の書式どおり（§23.10）。以下は記載時のメモ。エディタを起動しない制約のため、
  Android の実行中に保存 → 0.6 秒後に Output へ「差し替え: …」「反映: …」が出て端末の画面が変わる、を画面で見ていない（WPF 非依存の段取りは
  `AndroidRunUiTests`、実機の差し替えは SeedAndroid で確認済み）。確かめる手順は docs/android.md §23.7。
- [ ] **実行中の差し替えをエディタの画面（WPF）で目視していない** — 2026-09-26（上の項目の残り）。段取り（`AndroidRunController`・`AndroidHotReloadController`）は
  実機で確かめたが、`MainWindow.AndroidRun.cs` の配線そのもの・Output パネルの色・端末の画面の変化をエディタの画面で見ていない（エージェントはエディタを起動しない）。
  利用者が Android の実行中にシーン・モデル・スクリプトを保存し、Output の行（docs/android.md §23.7）と端末の画面を確かめる。
- [ ] **Android の実行中のビューポートの案内をエディタの画面（WPF）で目視していない** — 2026-09-26（docs/android.md §20.16 の実装時）。
  判断（`AndroidViewportPolicy`）は単体テストで、エディタは別の出力先のビルドで確かめたが、`MainWindow.AndroidRun.cs` の `ApplyAndroidViewport` の配線
  （PC のランタイムの子ウィンドウ・ホストを隠す／停止で出し直す、READY・最初のフレーム・起動時スプラッシュの解除で案内が消えないこと）はエディタの画面で見ていない。
  利用者が Android で実行 → 一時停止 → 再開 → 停止し、案内の文言と停止後に Edit の画面が戻ってマウス操作が効くことを確かめる。
  次の作業（一時停止中に端末のシーンの写しをビューポートに出す）は `AndroidViewportPolicy` の Paused の行を差し替えて行う。
- [ ] **実行中の差し替えの制限（画像を参照するモデル・一部のキャッシュ・削除）** — 2026-09-25（段階D の実行中の差し替え）。
  (1) モデルが外部の画像を参照している（`.gltf` の外部 `uri`・`.mtl`）とき、画像だけを差し替えてもモデルのテクスチャは変わらない（画像は `InPlace`＝スプライト等の
  キャッシュだけを捨てる。モデルを保存し直すと入る）。直すならモデルの読み込みが使った画像を記録し、画像の差し替えで依存するモデルも捨てる。
  (2) 空（スカイボックス）・パーティクルの形状等、キャッシュを捨てる対象に入れていないものがある（`app/hot_reload_ops.rs` の `forget_asset_caches`）。
  (3) 削除したファイルは端末へ伝えない（上書き層に残る。`run` で消える）。(4) フォント・`project_settings.json`・スクリプトホスト（`scripting/`）は差し替えられない。
  (5) `run` 以外で端末のアプリのデータを消すと送った記録（`cache/android/asset_overlay.json`）が古くなり、手元で変えていないファイルは送られない
  （差し替えの前に `files/assets` の有無を見る等で防げる）。
  ~~(6) 上書き（`files/assets`）を消すのは起動する `run` だけなので、`install`・`run --no-launch` の後にランチャーから起動すると、残った上書きが新しい pak より
  優先される~~ → 同日対応: APK を入れ直した直後（インストールの工程）でも `files/assets`・`files/bin` を run と同じ規則で消し、Output に 1 行出す
  （`editor/src/Android/Steps/PushedOverrides.cs`・`InstallStep.cs`。単体テスト `PushOverrideTests`。docs/android.md §17.7・§23.4）。
  残り: 同じ APK が入っていてインストールを飛ばした `install`（と `run --no-launch`）では消さないので、その後ランチャーから起動すると差し替えた内容のまま動く（低優先）。
  関連: `runtime/src/engine/core/app_base/hot_reload/`・`editor/src/Android/HotReload/`・`editor/src/Android/Steps/PushedOverrides.cs`。
- [ ] **Android の IPC のポートの設定の画面が無い** — 2026-09-25（段階D-1・低優先）。エディタの設定 `editor/settings/editor_preferences.json` の
  `"android": { "ipc_port": … }`（未設定なら 52735、0 で使わない）を手で書く。上の「AVD を選ぶ設定の画面」と一緒に作る。
- [ ] **Android の IPC をワイヤレスデバッグ（adb の Wi-Fi 接続）で確かめていない** — 2026-09-25（段階D-1）。仕組みは adb forward なので同じはずだが、
  USB の実機（Pixel 6a）でだけ確かめた（ワイヤレスデバッグは段階D-1 の対象外）。
- [ ] **自動で起動したエミュレータは実行の後も残す・エミュレータ自身のログを捨てている** — 2026-09-25（段階C-3・低優先）。起動に時間がかかるので
  使い回すため止めない（止めるのは利用者: 窓を閉じる／`adb emu kill`）。メモリの少ない PC では「実行を止めたらエミュレータも止める」設定があるとよい。
  また emulator.exe の標準出力は切り離した起動で見えないコンソールへ捨てている（ハンドルを継承させないため）。起動に失敗したときに理由を見せるなら、
  `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` で書き出し先のファイルのハンドルだけを継承させる（`Processes/DetachedProcess.cs`）。
- [ ] **Android（自動）は実機が 2 台以上で前回のものが無いと選ばない** — 2026-09-25（段階C-3・低優先）。私物の端末へ勝手に入れないため
  （SeedAndroid の指定なしと同じ考え方）。エラーの文に一覧を出すので、実行先セレクタで端末を選べば実行できる。
- [x] **シーンマネージャに未登録のシーンは、開いていても Android では開始シーンで起動する** — 2026-09-25（段階C-3 の確認で判明）/ 同日対応（段階C-4）。
  (a) を採った: 起動するシーン（エディタの開いているシーン・SeedAndroid の `--scene`）が未登録なら、中核の準備が SeedPak の `--extra-scene` で収録の起点に足す
  （判断 `editor/src/Android/Project/AndroidPakSceneSeeds.cs` → `AssetPakBuilder.Collect(extraSeeds)` → `AssetCollector.Collect(extraSeeds)`。パッケージ化ウィンドウは渡さない）。
  足すシーンは pak の指紋に入り、切り替えた最初の実行だけ pak・APK・install をやり直す。PC 側の「pak に入っていません」の警告は保険として残し、文言を
  「収録に失敗した／--skip-gradle／push」の理由に変えた。実機 Pixel 6a で、未登録の `Third`（自分だけが参照するモデル付き）・`日本語 シーン` から起動できた
  （docs/android.md §20.10・§20.14・docs/packaging.md §10.2）。残りは下の「段階C-4 の制限」。以下は記載時のメモ。
  APK の pak は参照グラフで作る（`editor/src/Packaging/Collect/AssetCollector.cs` の起点は project_settings.json・`start_scene`・`scenes[]` 等）ので、
  どこからも参照されていないシーンはディスクにあっても入らない。今は PC 側（`Steps/LaunchStep` が置き場の pak の表を引く）と端末（logcat）で
  理由を出して開始シーンで起動する（エミュレータで確認: 未登録の `scenes/日本語 シーン.scene` → 開始シーン、登録後 → そのシーン）。
  直すなら (a) 開いているシーン（未登録のときだけ）を SeedPak の起点に足す（`--seed` を足し、pak の指紋にも入れる。未登録のシーンへ切り替えるたびに
  pak・Gradle・install が走る）、(b) エディタからの Android の実行だけ全 `.scene` を起点にする（pak が大きくなる）、(c) 足りないシーンと依存を run-as で
  `files/assets` へ送る（エンジンは pak → APK → アプリ専用フォルダの順に読むので読める）。PC の Play は一時ファイルで何でも動くので、差を埋めるなら (a) が素直。
- [ ] **段階C-4 の制限: 未登録のシーンを起点に足す判断は登録の有無だけで見る（低優先）** — 2026-09-25（段階C-4）。(1) 他のシーン・スクリプトの `assets://` から
  参照されるので元々 pak に入る未登録のシーンでも起点に足すので、そのシーンへ切り替えると pak・Gradle・install をやり直す（pak の中身は同じ）。
  (2) 収録の設定が全ファイル同梱（`include_all_files`）でも足すシーンを指紋に入れるので、未登録のシーンへ切り替えるたびに作り直す。
  (3) 登録済みのシーンへ戻すと足さない pak に作り直す（パッケージ化の APK に開発中のシーンを残さないための仕様）。減らすなら、中核の準備で収録の設定
  （`packaging_settings.json`）を読む・置き場の pak の表（`Project/PakEntryIndex`）に既にあれば足さない、等（後者は置き場の中身に判断が依存するので注意）。
  関連: `editor/src/Android/Project/AndroidPakSceneSeeds.cs`・`Plan/AndroidStepFingerprints.PackageContent`。
- [ ] **収録の「エンジン内蔵参照」の走査が Rust のテストコードの `assets://` も拾う** — 2026-09-25（段階C-4 の実機確認で気付いた・既存）。
  `AssetCollector.ReadRuntimeBuiltinReferences` は `runtime/src` の全 `.rs` の `"assets://…"` を起点にする（実在するものだけ）ので、
  `runtime/src/engine/platform/launch_options.rs` のテストの `"assets://scenes/Second.scene"`・`"assets://scenes/Main.scene"`、`app_init.rs` の doc コメントの
  `"assets://scenes/game.scene"` と同じ名前のファイルを持つプロジェクトでは、それが参照されていなくても pak に入る（段階C-3・C-4 の確認用プロジェクトの `Second` がこれで入っていた）。
  `#[cfg(test)]` の中・コメントを飛ばす、テストの文字列を実在しそうにない名前にする、等。関連: `editor/src/Packaging/Collect/AssetCollector.cs`、docs/packaging.md §2。
- [x] **`push` した DLL は端末の `files/bin/` に残り、その後の `run`（APK の `bin/`）より優先される** — 2026-09-25（段階C-4 の実機確認で気付いた）/ 同日対応（段階C-4 の追加修正）。
  `run`（`Goal = Run`・`--project`・`--push-scripts` なし。エディタの実行ボタンも同じ）は APK の内容を正とし、起動の工程でアプリを止めた後・起動の前に
  自分のアプリの `files/bin/` を run-as で消す（あったときだけ Output に「push した DLL の上書きを解除しました…」の 1 行。消せなければ警告して起動は続ける）。
  `push`・`run --push-scripts`・開発用の `--assets-dir`（`files/bin/` が唯一の置き場）は消さない。判断は `Steps/LaunchStep.ClearsPushedScripts`（段階D で `Steps/PushedOverrides.ClearsScripts` へ移した）、
  消去は `AdbClient.RunAsRemoveDirectoryAsync`（相対パスだけ・`..` 不可）。単体テスト `AndroidPipelineTests` の `PushOverrideTests`。docs/android.md §17.7・§20.3。
  ~~**実機での確認は未実施**~~ → 2026-09-26 に実機で確かめた（段階D の実行中の差し替えの確認で、`files/bin` に DLL を置いた状態から `run` → 起動の工程で
  「push した DLL の上書きを解除しました」・logcat の `スクリプトの置き場: apk:seed/bin/`・`run-as … ls files` に `bin` が無い。docs/android.md §23.10）。
  記載時の手順: `push --project <proj_probe>` → `run --project <proj_probe>` で、Output の「push した DLL の上書きを解除しました」・logcat の `スクリプトの置き場: apk:seed/bin/`・
  `adb exec-out run-as com.seedengine.runtime ls files` に `bin` が無いことを確かめる。
  ~~残り: `install` だけ（起動しない）では消さないので、その後ランチャーから起動すると `push` の DLL で動く（低優先）。~~ → 段階D（実行中の差し替え）で対応:
  APK を入れ直した直後（`install`・`run` のインストールの工程）でも同じ規則で消す（判断は `Steps/PushedOverrides.ClearsScripts`。同じ APK でインストールを
  飛ばしたときは消さない）。以下は記載時のメモ。
  端末は `files/bin/` → APK の `bin/` の順に探すので、`push` の後に `.cs` を直して `run` しても（APK の DLL は新しくなるのに）古い `push` の DLL で動く
  （docs/android.md §17.7 の「差し替えを消すと APK の中のものへ戻る」を忘れると気付きにくい）。`run`（`--push-scripts` なし）のときに `files/bin/` を消す、
  か残っていれば警告する。エディタの実行は `push` を使わないが、SeedAndroid と併用すると起きる。関連: `editor/src/Android/Steps/PushScriptsStep.cs`・`LaunchStep.cs`。
- [ ] **スクリプトからアプリを終える API が無い（Android では戻るキーでも終わらない）** — 2026-09-25（段階C-4 の実機確認で記載・低優先）。
  戻るキーは Escape としてスクリプトへ渡るだけで（docs/android.md §14.5・docs/scripting_api.md の Android の戻るキー）、SEED のスクリプト API には
  アプリを終える口（Unity の `Application.Quit` 相当）が無いので、ゲームの中からは終えられない（最近のタスクから消す等の OS の操作は未確認）。
  エディタの「端末でアプリが終わったので実行を終えました」（pidof の見張り）も、戻るキーでは働かない（プロセスが終わったとき＝クラッシュ・強制停止等で働く）。
  ~~その Output の文言と `AndroidRunPhase.cs`・`Android/Pipeline/AndroidDeviceActions.cs` のコメントが「戻るキー」を理由に挙げている~~ → 段階C-4 の追加修正で
  「（最近のタスクから消した・強制停止・クラッシュ等。直前の logcat を確認してください）」に直した（`AndroidRunOutputFormatter.AppExitedText`。
  「ホーム画面」も挙げない: ホームへ戻ってもプロセスは背面で続く。最近のタスクから消したときに終わることは実機では未確認〈AOSP の既定の振る舞い〉）。
  終了確認のダイアログからアプリを終える等に要るなら、エンジンの終了要求（Android は `finish()`）をスクリプト API へ出す（add-script-api）。
  → 2026-09-27（W1-6）: 戻るの最上位で「閉じずに背面へ」回す `App.MoveTaskToBack`（`moveTaskToBack(true)`）を足した（アプリ向けの既定。docs/android.md §25.15）。
  終える API は入れていない（MainActivity の破棄はプロセスごと終わるので次の起動が冷え、目覚ましアプリには背面へで足りる）。ゲームの「終了」ボタンに要るなら、
  `finishAndRemoveTask`＋セーブの書き出しの順を決めて足す（未着手）。
- [ ] **段階C-3 の「Android（自動）で実機を優先する」経路とエディタの画面を実機・GUI で確かめていない** — 2026-09-25（段階C-3）。確認中は Pixel 6a が
  USB につながっていたが触らない約束のため、エミュレータの自動起動は「選んだ端末が見えなければエミュレータ」の経路（同じ起動・待ち合わせのコード）で確かめた。
  実機がつながった状態の `Android（自動）`（実機を選ぶ）、実機を外した状態の `Android（自動）`（エミュレータを起動）、実行先セレクタの `Android（自動）` の行と
  `（未接続）` の行の実行、未保存の確認（保存して実行 → 保存の完了 → 実行）を、エディタで確かめる（手順は報告の GUI 確認手順）。
  2026-09-25 追記（段階C-4）: SeedAndroid の `--serial auto` で、実機がつながった状態の選び方（前回使った実機・記録の無いプロジェクトでは「つながっている実機」）を
  実機 Pixel 6a で確かめた（エミュレータは起動しない。docs/android.md §20.14）。実機を外した状態の自動起動とエディタの画面は未確認のまま。
- [ ] **エディタからの Android 実行で DLL だけを送る高速経路（Push）を出していない** — 2026-09-25（段階C-2）。`.cs` だけを変えたときも
  pak とスクリプト → Gradle → install を回す（エミュレータで約 27 秒。§5.3）。SeedAndroid の `push`（約 8 秒）に当たる操作をプレイバーへ
  足すか、スクリプトだけの変更を見分けて自動で Push にする。
- [ ] **エディタを閉じても端末のアプリは止めない・Android の実行の MCP ツールが無い** — 2026-09-25（段階C-2・低優先）。閉じる操作を adb で
  待たせないため、閉じるときは子プロセス（ビルド・logcat）を止めるだけ。AI ツールの `seed_play` は PC の実行だけを扱い、エディタで Android の実行が
  動いている間は `play` を拒否する（docs/editor_mcp.md 6.4）。端末での確認を AI に任せるなら、安全機構（インスタンス束縛・端末の指定の明示）付きで
  MCP ツールを足す。
- [ ] **Android の実行の後に Gradle のデーモン（最大ヒープ 2 GB）が残る** — 2026-09-25（段階C-2）。Gradle の既定（3 時間で自動終了）。
  メモリの少ない PC では、実行を終えたら `gradlew --stop` を呼ぶ・`org.gradle.daemon.idletimeout` を短くする等を検討する。
- [ ] **Output パネルの警告と「ビルド」の色が同じ黄** — 2026-09-25（段階C-2・低優先）。色の種類は分けてある（`OutputTone.Warning` / `Build`）ので、
  分けるなら OutputPanel の「色の種類 → ブラシ」の表だけを直す（docs/editor_ui_style.md 7 章）。
- [x] **段階C-1 の実機（Pixel 6a・arm64）での一気通貫の確認** — 2026-09-25 記載 / 同日対応（段階C-4）。実機で `run`（arm64 のビルドから 261.9 秒・
  `[PROBE v2] OnStart rid=linux-bionic-arm64`）、2 回目の `run`（5 工程を飛ばして 31.4 秒）、`push`（`files/bin/` から）、`stop`（`pidof` が空）、`--serial auto`、
  アプリの識別情報（`com.seedengine.c1test`・`C1 Space Test`・7 / 0.8）を確かめた（docs/android.md §20.14）。以下は記載時のメモ。
  C-1 の作業中は実機が USB につながっておらず、SeedAndroid の `run` /
  `push` / `stop` はエミュレータ（x86_64）だけで確かめた（arm64 の APK の `build` までは確認済み。docs/android.md §19）。端末が空いているときに
  `dotnet run --project editor/tools/SeedAndroid -- run --project <プロジェクト> --serial <実機> --logcat-seconds 30` で、インストール・起動・
  スクリプトの `SEED.Debug.Log` と、2 回目の run で工程が飛ばされることを確かめる。
- [ ] **ABI・プロジェクト・アプリ ID を行き来するたびに APK を作り直す（低優先）** — 2026-09-25（段階C-1）。Gradle の置き場と APK は 1 つなので、
  エミュレータ（x86_64）と実機（arm64）を交互に使うと、毎回 同梱 .NET の組み立て（ABI の入れ替え）と Gradle が走る（約 20〜50 秒）。
  APK を指紋ごとに `app/build/seed/apk/` へ取っておき、同じ指紋なら写すだけにする案（`GradleBuildStep` と置き場の記録に閉じた変更で済む）。
- [ ] **工程の入力の指紋はファイルの大きさと更新時刻だけ（中身は読まない）** — 2026-09-25（段階C-1・低優先）。数 GB のアセット・450 MB の .so を毎回読まないため
  （make・MSBuild と同じ）。中身が変わったのに大きさも時刻も同じだと作り直されない（`--rebuild` で逃げられる）。また工程の入力の表
  （`Plan/AndroidBuildInputs.cs`）に無いファイルを工程が読むようになると「変えたのに作り直されない」になる（エンジンが `include_bytes!` で
  リポジトリの別の場所を埋め込む等。今は `editor/resources/icons/viewport/location.png` を入れてある）。
- [ ] **SeedAndroid の Ctrl+C を対話のコンソールで確かめていない** — 2026-09-25（段階C-1）。中断の仕組み（子プロセスとその子孫を止めて
  OperationCanceledException）は単体テスト（ping を途中で止める）と logcat の秒数の時間切れでは確かめたが、ビルドの途中で人が Ctrl+C を押す操作は
  未確認（cmd.exe・Gradle のデーモンが同じコンソールの Ctrl+C を受けたときの振る舞い）。
- [ ] **ps1 ラッパー・SeedAndroid の `dotnet run` のたびにツールのビルドの確認が走る（2〜4 秒）** — 2026-09-25（段階C-1・低優先）。
  ビルド済みの `editor/tools/SeedAndroid/bin/Debug/net10.0/SeedAndroid.exe` を直接呼べば省ける（中核を変えたら作り直しが要る）。

### 配布（段階D・2026-09-26。正典: docs/android.md §24）

- [x] **署名付きの release APK / AAB・アイコン・Google Play の要件・16 KB ページの最終確認** — 2026-09-25 記載（段階D のロードマップ）/ 2026-09-26 対応。
  配布用（release）のビルド（アップロード鍵で署名・debuggable でない・INTERNET なし・Rust は --release。鍵が無ければビルドしない）、`keystore create`、
  アイコンの生成、ビルドの前と後の要件チェック（`check`）、targetSdk 36（Google Play は 2026-08-31 以降 36 以上）。配布物の .so の LOAD の 16 KB 整列を
  毎回確かめる。確認結果は docs/android.md §24.11。
- [x] **配布用（release）を実機で動かしていない** — 2026-09-26 記載（段階D）/ 同日対応。bundletool の `install-apks`（`base`・`config.arm64_v8a`・`config.ja`・
  `config.xxhdpi`・DEBUGGABLE なし）→ 起動（`am start` の TotalTime 552 ms・最初の提示まで約 4.3 秒〈同梱 .NET の展開とパイプライン生成 3.2 秒を含む〉。ホームで
  キャッシュを保存した後の 2 回目は 290 ms・約 1.1 秒）→ `[SEED QUALITY] preset=mobile`・fps 59.2〜59.5（開発用 58.8〜59.1。どちらも 60 Hz で頭打ち。CPU 時間は
  1 コアあたり 69% 対 75.5〜79.4%）→ 戻るキーは targetSdk 36 でも Escape として届き、アプリは終了しない（開発用も同じ）→ アンインストール。SeedAndroid の
  `install --variant release` でも入って動いた。docs/android.md §24.11。targetSdk 36 の開発用 APK の回転・安全領域・音声フォーカスは下の新しい項目へ分けた。
  以下は記載時のメモ。確認の 20 分間、実機（Pixel 6a）が利用中で前面がランチャーにならなかった。
  AAB → bundletool の `build-apks`（済み）→ `install-apks` → 起動（`com.seedengine.release_probe`）→ logcat の `[SEED HEARTBEAT]` の fps と
  CLR の起動・スクリプトの読み込みの所要時間を開発用（debug の .so）と比べる → 戻るキー（`input keyevent KEYCODE_BACK`。targetSdk 36 でも
  アプリが前面のまま＝KEYCODE_BACK がネイティブへ届く）→ アンインストール、を行う。SeedAndroid の `install --variant release` の経路も同じ機会に。
  targetSdk 36 にした開発用の APK（戻るキー・回転・安全領域・音声フォーカス）も実機で一通り確かめ直す。
- [ ] **targetSdk 36 の開発用 APK で回転・安全領域・音声フォーカスを実機で確かめ直していない** — 2026-09-26（上の項目の残り）。戻るキーは確かめた
  （Escape として届き、アプリは終了しない）。回転・安全領域（docs/android.md §15）と音声フォーカス（§16）は targetSdk 36 に上げる前の確認のまま。
- [ ] **NativeAOT で配布用のスクリプトを動かす（評価済み・未実装）** — 2026-09-26（段階D の評価。docs/android.md §24.12）。ILC で `linux-bionic-arm64` の
  .so へ変換できることと、Android の入口だけを根にすれば 2.5 MB（今の同梱 .NET は APK の約 31 MB）になることを確かめた。必要な変更: ユーザースクリプトを
  同じ .so へ組み込み登録する入口（`AssemblyLoadContext.LoadFromStream` は使えない）・Roslyn の分離・公開シンボルと Rust の `dlopen` の経路・
  リフレクション / `MakeGenericType` の注記と修正・暗号（OpenSSL）の方針。見積もり 2〜3 週間。開発用は CoreCLR のまま。
- [ ] **Google Play Console へ実際に出していない** — 2026-09-26。内部テストへの AAB のアップロード・Play App Signing の登録・事前審査の警告
  （ネイティブのデバッグシンボル等）は未確認（アカウントが要る）。docs/android.md §24.9 の手順で最初の 1 回を確かめる。
- [ ] **予測型の「戻る」の無効化は一時的な回避** — 2026-09-26。targetSdk 36 で KEYCODE_BACK が届かなくなるのを `android:enableOnBackInvokedCallback="false"`
  で止めている（公式にも一時的な回避とある）。将来の Android で効かなくなる前に、`OnBackInvokedCallback`（`MainActivity`）で戻るを受けてネイティブの
  Escape へ渡す形へ移す（予測型の戻るのアニメーションにも対応できる）。
  → 2026-09-29（W2 の手直し P1-3）: その形を **opt-in** で作った（プロジェクト設定 `android.predictive_back: true` で `enableOnBackInvokedCallback="true"`・
  `OnBackInvokedCallback`／`OnBackAnimationCallback` で受けて合成の KEYCODE_BACK → Escape。docs/android.md §25.18）。**既定（設定なし・false）は今も `"false"` の回避のまま**
  （WarashibeFishing などゲームの戻るの振る舞いを変えないため）。将来の Android で `"false"` が効かなくなったら、既定を true 側へ移す（ゲームも SEED.UI を
  使わなければ、起動したときの「アプリが受ける」のまま Escape が届く）。
- [ ] **Gradle のデーモンが配布用のビルドの後も署名のパスワードを環境に持つ（低優先）** — 2026-09-26。デーモンはビルドのたびにクライアントの環境変数に
  合わせるので、次のビルドまで `ORG_GRADLE_PROJECT_seed.signing.*` が残る（同じ Windows ユーザーのプロセスからは読める。DPAPI と同じ前提）。
  配布用だけ `--no-daemon` にするか、ビルドの後に一時的なデーモンを止める案（起動が 10〜20 秒遅くなる）。
- [ ] **ネイティブのデバッグシンボルを AAB に入れていない** — 2026-09-26。Play Console のクラッシュ（ANR・ネイティブの落ち）のスタックに関数名が出ない。
  release に `ndk { debugSymbolLevel = "SYMBOL_TABLE" }` を足すと AAB の BUNDLE-METADATA に入る（AAB は大きくなるが配られる APK は変わらない）。
- [ ] **アセットの中のアイコンの PNG が pak にも入る（低優先）** — 2026-09-26。`project_settings.json` の `android.icon` が参照走査で拾われ、
  使わない PNG（数十 KB）が pak に入る。収録の規則で `android.icon` を起点から外すか、アセットの外に置く案内だけにするか。
- [ ] **AAB に x86_64 も入れると BCL が全端末に 2 ABI 分配られる** — 2026-09-26。同梱 .NET の BCL は assets なので ABI で分けられない（要件チェックが注意を出す）。
  配布は arm64-v8a だけにする決まりで回避。分けるなら BCL を ABI ごとの asset pack にするか NativeAOT。
- [ ] **`useLegacyPackaging`（.so を圧縮）でインストール後の大きさが増える** — 2026-09-26。Google Play でも使える（docs/android.md §24.6）が、非圧縮の .so を
  APK から直接読む既定より端末の容量を使う。同梱 .NET が dotnet-root の実ファイルを前提にしているため。NativeAOT にすれば .so は 1 つで済む。
- [ ] **アイコンのモノクロ層（テーマアイコン）と前景の余白の設定が無い（低優先）** — 2026-09-26。前景は安全域 66dp に収めるだけ。ロゴが小さく見えるなら
  前景の倍率の設定（`android.icon_foreground_scale` 等）やモノクロ用の PNG の設定を足す。
- [ ] **bundletool を SeedAndroid に組み込んでいない（低優先）** — 2026-09-26。AAB を端末で試すのは手作業（docs/android.md §24.10）。`install --format aab` で
  `build-apks --connected-device` → `install-apks` を行う案（jar の取得場所・Java の場所・署名のパスワードの渡し方を決める）。`build_and_run.ps1` にも配布用の引数は無い。
- [ ] **パッケージ化ウィンドウの配布用の欄を画面で確かめていない** — 2026-09-26。署名（参照・保存・消す・新しいキーストアを作る）・要件の一覧（アイコンと色）・
  ビルドの種類で欄が出入りすること。エージェントはエディタを起動しないため、利用者が目で確かめる（判断は単体テスト `AndroidRunUiTests`）。
- [ ] **versionCode の記録は PC ごと（`cache/android/release_history.json`）** — 2026-09-26（低優先）。別の PC・別の人のビルドは知らない。
  本当の正は Play Console の最後の versionCode（Play Developer API で読めるが、認証が要る）。
- [ ] **NativeBuildStep の修正（cargo ndk の前に jniLibs の .so を消す）に単体テストが無い** — 2026-09-26（実機確認で見つけた不具合の修正）。cargo-ndk が
  コピーを飛ばして別のプロファイルの .so が APK に入る不具合（docs/android.md §10）を `Steps/NativeBuildStep.cs` で直し、実際のビルドで両方向（配布用の後の
  開発用 `run --rebuild` → debug の .so、debug の成果物を新しくした後の配布用 `build` → release の .so）を確かめたが、単体テストは足していない。
  `AndroidPipelineTests` に、一時フォルダと偽の cargo で「前の .so を消してから呼ぶ」「消せないときは理由付きのビルドの失敗」を足す。
- [ ] **`--skip-rust` は jniLibs の .so のプロファイル違いを警告しない（低優先）** — 2026-09-26。`--skip-rust` は jniLibs の .so をそのまま使うので、配布用の
  ビルドに debug の .so が入っても（逆も）気づかない。案: .so の工程の記録（`step_stamps.json`）に作ったプロファイルを残し、`--skip-rust` のときに今回と違えば
  警告する（配布用なら止める）。
- [x] **一時停止中の写しの実機確認** — 2026-09-26 記載 / 同日対応（docs/android.md §20.17）。Pixel 6a（debug の .so・わらしべフィッシング）で
  SeedAndroid の `pause` → `snapshot` → `resume`（アクタ 304・飛ばした 0・端末 76.6 ms・合計 0.31 秒・端末の cache に残らない）、取り出した写しを
  PC の `SEED.exe` の Play / Edit で読み込み（Edit は表示中の保存・編集を拒否し、`SNAPSHOT_VIEW_ENDED:memory` で戻る）、エディタの段取りのプローブ
  （本物の `AndroidRunController` で端末へ）で一時停止のクリックから写しが出るまで 2.25 秒・再開で 0.39 秒・停止で 0.50 秒で復元。
  以下は記載時のメモ。作業中は端末（Pixel 6a）の画面が
  消えたままだった（06:06〜06:27・07:04〜07:25 に待った）。PC（Play の `SEED.exe` での書き出し・別の `SEED.exe` での読み込み・Edit の `SEED.exe` での閲覧と戻し・
  エディタの段取りの WPF 抜きのプローブ）では確かめた。端末での書き出しの時間（debug の .so）・run-as の取り出し・端末の絵との比較・
  `AndroidRunController` を通した一時停止のクリックから写しが出るまでの時間が未確認。
- [ ] **端末の一時停止の写しへの編集を端末へ反映する（持ち越し）** — 2026-09-26（一時停止中の写しの実装時。docs/android.md §20.17）。今は閲覧専用
  （保存・編集をエディタとランタイムの両方で拒否）。反映するなら、写しの上の編集（Transform・コンポーネントの値）を差分として取り、端末へ
  「一時停止中の世界へ当てる」命令で送る（シーンの読み直しでは一時停止中の状態が消える）。写しの読み込みと表示は独立したクラス
  （`editor/src/SceneSnapshot/SceneSnapshotViewSession`）にしてあるので、その外に「差分を取って送る」段取りを足す形にする。
- [ ] **写しに入らない実行時の状態（低優先）** — 2026-09-26（§20.17）。スクリプトのシーンに保存しない変数・物理の速度・アニメーションの再生位置・
  パーティクル・2D の UI（キャンバス）の描画状態は写らない（`.scene` の形式に無い）。エディタのシーンパネルはアニメーション・スクリプトを動かさないので
  キャラクターはバインドポーズで見える。必要なら写し専用の節（アニメーションの時刻・再生中のクリップ）を足す。
- [ ] **写しを出すかどうかの設定が無い（低優先）** — 2026-09-26（§20.17）。一時停止のたびに取り出して出す。大きなシーンで重いときに止める設定
  （例: エディタの設定 `android.pause_snapshot`）を足す。
- [ ] **写しの表示中にシーンのファイルが外で変わったとき、戻した後に自動では読み直さない（低優先）** — 2026-09-26（§20.17）。表示中は自動再読込を
  見送り（状態表示に理由）、戻した後の次の変更通知までは読み直さない（「ディスクから再読込」で取り込める）。
- [ ] **写しの表示（バナー・無効表示・ビューポートの切り替え・保存の確認の窓）をエディタの画面で確かめていない** — 2026-09-26（§20.17）。
  エージェントはエディタを起動しないため、判断は単体テスト、段取りは WPF 抜きのプローブ（実機＋PC の Edit の SEED.exe）で確かめた。利用者が画面で確かめる。
- [ ] **`app/build.gradle.kts` の `sourceSets` の `srcDir` が AGP 9.1.0 で非推奨の警告を出す（低優先）** — 2026-09-27（W1-2 の作業中に気付いた既存の警告）。
  Gradle の構成の段階で `'fun srcDir(srcDir: Any): Any' is deprecated. Use 'directories' mutable set instead.` が 3 行出る（`main` の
  `jniLibs.srcDir("src/seedDotnet/jniLibs")`・`assets.srcDir("src/seedDotnet/assets")`・`res.srcDir("src/seedIcon/res")`）。ビルドは通る。
  `directories` へ書き換えるか、W1-2 の機能の断片と同じく `androidComponents.onVariants` の `addStaticSourceDirectory` へ寄せる
  （res の層が main と別になる違いがあるので、アイコンの `@mipmap/ic_launcher` の解決を確かめてから）。

## .NET 10 への統一（2026-09-24 段階B-0 実装時の残件）

- [ ] **.NET 10 SDK の既定アナライザーで CA2024 の警告が 6 件出る** — 2026-09-24。`net10.0-windows` へ上げたら、async メソッド内で
  `process.StandardOutput/StandardError.EndOfStream` を見ている箇所が CA2024（非同期メソッドで EndOfStream を使わない。
  バッファが空だと同期で読みに行きスレッドを塞ぐ）になった。ビルドは通る。直すと読み取り方（`ReadLineAsync` が null を返すまで読む等）が
  変わるため、挙動を変えない方針の移行作業では直していない。関連: `editor/src/AI/AIAssistantPanel.cs`（2048, 2071 行）、
  `editor/src/AI/Providers/CliAgentProvider.cs`（329, 347, 444, 462 行）。
- [ ] **エディタの `System.Security.Cryptography.ProtectedData` 参照に NU1510 の警告が出る** — 2026-09-24。
  エディタは WPF 経由で `Microsoft.WindowsDesktop.App` を参照しており、ProtectedData はそちらに同梱されている（.NET 9 の参照パックにも
  入っていた）ため、.NET 10 SDK が「明示参照は不要」と警告する。実行時は元からフレームワーク側が使われている（.NET 9 / 10 とも
  ビルド出力に ProtectedData.dll が無く、SEEDEditor.deps.json にも載らない）ので、外しても挙動は変わらない見込み（外したビルドは未確認）。
  WPF を使わない `AccountsTests` は引き続きこのパッケージが要る。関連: `editor/SEEDEditor.csproj`。
- [ ] **スクリプトデバッガ（netcoredbg）が .NET 10 の CLR にアタッチできるか未確認** — 2026-09-24。ランタイムの CLR が 10.0.12 になった。
  この PC の `tools/netcoredbg/` には `dbgshim.dll`（9.0.13）等だけがあり `netcoredbg.exe` 本体が無いため、アタッチ・ブレークポイントを
  試せていない。netcoredbg を配置したら、.NET 10 対応の版か（必要なら新しい版へ差し替え）を確かめる。関連: docs/scripting_debugger.md、
  `editor/src/Debugger/NetcoredbgLocator.cs`。

## アプリ基盤（W1 Android サービス層 / W2 UI 部品群）— 2026-09-27 W0 設計時（正典: docs/app_platform_roadmap.md）

目覚ましアプリ Wake or Pay（`D:\SEED_projects\WakeOrPay`、アプリの正典は `docs/WAKEORPAY_SEED_SPEC.md`）を SEED で作るために、
エンジン本体へ足す汎用機能。段階・受け入れ基準・検証方法は docs/app_platform_roadmap.md。ここには着手前の作業単位と、
W0 の調査で見つかった既存の不具合・制限を置く。関連する既存の項目は「Android」節（起動の初期化の同期・同梱 .NET の展開の同期・
アプリを終える API が無い・安全領域の自動反映・縦画面の自動スケール・ジェスチャー・指0 だけのポインタイベント）。

### W0 の調査で見つかった既存の不具合・制限

- [x] **SaveData の書き出しに「既存の save.json を消してから rename」の隙間がある** — 2026-09-27（W0）記載 / 同日 W1-S で対応。
  削除をやめて rename だけにし、一時ファイルの `sync_all`・Android のフォルダの sync・1 世代前（`save.json.bak`）・壊れた本体の退避
  （`save.json.corrupt-<時刻>`）・`SaveData.RecoveredFrom` を入れた（`runtime/src/engine/core/save/durable_file.rs`・`recovery.rs`。
  docs/app_platform_roadmap.md §2.7 の「実装」の表）。以下は記載時のメモ。`store.rs::flush` は
  一時ファイルへ書いた後、既存を `remove_file` してから `rename` する（コメント「Windows の rename は上書き不可」）。削除と rename の間に
  落ちると save.json が無くなり、読み込みは `.tmp` から自動で戻らない（空のセーブで始まり、次の保存で上書きされる）。Rust の
  `std::fs::rename` は Unix・Windows とも既存の宛先を置き換える（標準ライブラリの文書で確認）ので、削除は要らない。あわせて fsync が無い・
  壊れたファイルを空として読んで上書きする（前の世代が無い）。お金と履歴を持つアプリには足りない。直し方は docs/app_platform_roadmap.md §2.7
  （W1-S）。関連: `runtime/src/engine/core/save/store.rs:136-165,269-289`、`save/mod.rs:163-180`。
- [x] **`SaveData.SetString` が値をスタックに確保する（大きな値でプロセスが落ちうる）** — 2026-09-27（W0）記載 / 同日 W1-S で対応。
  共通の入れ物 `scripting/src/Api/Interop/Utf8Arg.cs`（最大長が 1 KB 以下と保証できる文字列だけスタック、それ以外は `ArrayPool<byte>`）に
  `ScriptHost.cs` の文字列を渡す FFI 24 か所をすべて乗せ替えた。PC の Play で 2 MB の往復を確認。以下は記載時のメモ。`ScriptHost.SaveSetString` は
  値の UTF-8 を `stackalloc byte[vl]`（上限なし）で確保してから FFI へ渡す。数百 KB〜MB の JSON を 1 キーに入れる使い方（Wake or Pay の保存の設計）では
  スタックが溢れ、.NET のスタックオーバーフローは捕まえられないのでプロセスごと落ちる（溢れる大きさはスレッドのスタック次第で未測定）。
  一定の長さを超えたら `ArrayPool<byte>` を使う。読み取り側（`SaveGetString`）は必要長の 2 段階でヒープへ切り替えているので問題ない。
  文字列を渡す他の FFI も同じ形か点検する。関連: `scripting/src/Api/ScriptHost.cs:826-841`、`runtime/src/engine/core/scripting/host_api.rs::ffi_save_string`。
- [x] **UI スレッドからの自動書き出しが、スクリプトの複数キーの更新の途中を書きうる** — 2026-09-27（W0）記載 / 同日 W1-S で対応。
  `SaveData.Batch(Action)`（深さと待たせた書き出しの要求をストアと同じ Mutex に持ち、Batch の途中の書き出しは最も外側の終わりに 1 回だけ）と、
  scripting_api.md §7.7 の「1 文書を 1 キーに入れる」例の両方。残る割り切りは下の W1-S の節。以下は記載時のメモ。`MainActivity.onDestroy` は
  UI スレッドから `nativeFlushSaveData` を呼ぶ。ストアの Mutex が守るのは 1 回の Set と 1 回の書き出しだけなので、スクリプトがキーを
  順に書き換えている最中に入ると半端な組み合わせがディスクに残りうる（推測。実測はしていない）。`SaveData.Batch` を足すか、
  「1 文書を 1 キーに入れる」使い方を scripting_api.md に書く。関連: `runtime/android/native/src/jni_exports.rs`、`save/mod.rs`。
- [x] **配布版で起動の理由（Intent）を受け取る経路が無い** — 2026-09-27（W0）記載 / 同日 W1-4a・W1-6 で対応。`MainActivity.forwardLaunchOptions` は
  デバッグ版だけ（他のアプリの Intent で途中のシーンへ飛べないため）で、`onNewIntent` も無い。目覚まし・通知のボタン・ディープリンクで
  起きたことをスクリプトが知れない。W1-P6（エクスポートしない activity-alias 経由の Intent だけを信用する）で解く。関連:
  `runtime/android/app/src/main/java/com/seedengine/runtime/MainActivity.java:192-233`。
  → W1-4a で `onNewIntent` と起動理由（`App.LaunchReason`・`platform.launch`。目覚まし・通知の操作は PlatformEntry 経由だけを信用）、W1-6 でディープリンク
  （`LaunchKind.DeepLink`・`LaunchInfo.Uri`。他のアプリも送れる入力として扱う）を入れた（docs/android.md §25.12.4・§25.15.6）。`forwardLaunchOptions` はデバッグ版だけのまま。
- [x] **プロジェクトごとに権限・サービス・受信機・intent-filter を足す仕組みが無い** — 2026-09-27（W0）記載 / 同日 W1-2 で対応。main のマニフェストには
  `<uses-permission>`・`<service>`・`<receiver>`・`<provider>` が 1 つも無く、`INTERNET` はデバッグ版のマニフェストだけ。
  `AndroidAppSettings.ExtraData` は保存で消えないだけで、ビルドのどこからも読まれない。W1-2（`android.features` → マニフェストの断片）で解く。
  → W1-2 で `android.features` → 機能の表（`runtime/android/platform_features.json`）→ 生成したマニフェストの断片（`app/src/seedFeatures/`）の
  仕組みができた（権限と `<application>` の下の要素の木。受信機・サービスは W1-3・W1-4 で表に行を足す。docs/android.md §25.10）。
  配布版の `INTERNET` は下の別項目（機能の表に行を足すだけで済む形になった）。
  関連: `runtime/android/app/src/main/AndroidManifest.xml`、`src/debug/AndroidManifest.xml`、`editor/src/Android/Gradle/GradleInvocation.cs`。
- [ ] **配布版でネットワークを使うゲーム・アプリが作れない（`INTERNET` はデバッグ版だけ）** — 2026-09-27（W0 の上の項目から分けた）。
  Flutter 版の Wake or Pay で「リリース版だけ INTERNET が無い」事故があった。W1-2 の機能の表に `internet`（`android.permission.INTERNET`）の行を
  足せば `android.features` で opt-in できるが、W1 の語彙（alarm / notifications / deep_links）に無いので足していない。足すときは
  要件チェックの `release_unexpected_permissions`（INTERNET を「配布用には要らない」と注意する）を、features に internet があれば出さないように直す。
- [x] **アプリ向けの既定を選べない（システムバーを常に隠す・`appCategory="game"` 固定）** — 2026-09-27（W0）記載 / 同日 W1-2 で起動時の既定を対応。
  `MainActivity` は常にシステムバーを隠し（`hideSystemBars`）、マニフェストは `android:appCategory="game"` 固定。時刻や電池が見えるべきアプリには向かない。
  → W1-2 で `android.system_bars`（`SystemBarsController.java` が生成した bool を読む）と `android.app_category`（`-Pseed.appCategory` →
  `manifestPlaceholders`）で選べるようにした。実行中の切り替え（`Window.SetSystemBarsVisible`）は W1-6 で入れた（docs/android.md §25.15.3）。バーの文字色は
  下の別項目（W1-6 でも入れず持ち越し）。実機の見た目は下の W1-2 の項目。

### W1: Android サービス層（`SEED.Platform`）

**W1 は 2026-09-27 に完了（W1-7 の通しの確認。roadmap §2.8・§2.9.2）**。この節に残る `[ ]` は W1 の持ち越しで、W2 以降（W3 のアプリの作業で
要る順）に扱う。手作業の要る実機の確認（通知のボタン・権限の画面・電源ボタン・戻る／ホーム／スワイプ・通知からアプリへ戻る）とリリース版での確認は
roadmap §2.8 の各行の「未実施」のとおり。任意の W1-9（解除前の鳴動画面）は下の項目。

- [x] **W1-0 スパイク: 別プロセスの鳴動サービスとフルスクリーン通知からの冷えた起動** — 2026-09-27 記載 / 同日実施。Java だけの
  使い捨てアプリ（`runtime/android/spikes/platform_spike/`）で、Pixel 6a（Android 16）の画面オフ・ロック中に、両プロセスが無い状態から
  予定時刻 +0.73 s で音・+0.95 s でロック画面の上に鳴動画面が出る、最近のタスクから消しても音が続く、強制停止で予約が消え停止状態からの
  復帰で `BootReceiver` が張り直す、`ContentResolver.call` の往復 0.64〜0.87 ms、`mediaPlayback`・`systemExempted` とも予約経由で起動できる、
  を確かめ、E-01〜E-03・E-10 を決めた。結果の正典は docs/app_platform_roadmap.md §2.9.1。残りは下の 3 件（W1-0 の残り 2 件と、W1-4 の頭へ
  移した実 GameActivity の計測）。
- [x] **W1-0 の残り: Doze の下の時刻精度の再試験** — 2026-09-27 記載 / 同日 W1-4b の T3 で実施（roadmap §2.9.2）。1 回目は `force-idle` の後、発火の前に利用者が端末を使い始めて Doze を
  抜けたので無効（配信は +1 ms だったが INACTIVE のとき）。端末を使っていない時間に `runtime/android/spikes/platform_spike/scripts/t3_doze.sh` で
  測り直す（使い始めたら予約を取り消して中止する）。AC-1 の後半・roadmap §2.2 の (4)。
  → SEED の APK（Wake or Pay）で測り直した: `battery unplug` → `force-idle` で IDLE → 30 秒後に DeviceIdleController が自分で ACTIVE → QUICK_DOZE_DELAY
  （`device_idle: [0,alarm]`。`setAlarmClock` の予約が `min_time_to_alarm`〈この端末は 1 時間〉より近いと深い Doze に留まらない Android の仕組み）→ 発火は
  `device_idle_wake_from_idle`・`AlarmReceiver` の受信 +240 ms・音 +535 ms・エンジンの最初のフレーム +2128 ms。時刻どおりに鳴った。「発火の時点で IDLE」の
  状態は `setAlarmClock` では作れない（1 回目が INACTIVE だったのもこの仕組みの見込み。推論）。
- [x] **W1-0 の残り: 再起動の後の張り直しと Direct Boot（ロック解除前に鳴るか）** — 2026-09-27 記載 / 同日 W1-7 の T4 で実施（roadmap §2.9.2）。
  **ロック解除の前に張り直して鳴った**: `LOCKED_BOOT_COMPLETED`（14:30:02・`RUNNING_LOCKED`）で `alarms.rescheduled(boot)`、予定から +26 ms で発火・
  `RingService` が前景に入り（`ALARM_MANAGER_ALARM_CLOCK`）最小の音量で鳴動（利用者の耳でも確認）・60 秒後に安全弁で止まり音量が戻った。鳴動画面
  （`MainActivity`。directBootAware でない）はフルスクリーン通知から起動されなかった（`START … PlatformEntry … result code=-92`。解除前の画面は W1-9）。
  再起動の後は最初のロック解除まで adb が `unauthorized` のままで、Direct Boot の間は adb で見られない（端末保護ストレージの記録と logcat の
  バッファで後から確かめた）。以下は記載時のメモ。`adb reboot` は利用者の許可が要るので未実施。
  許可の後に `scripts/t7_reboot_procedure.sh --reboot-permitted` で、`LOCKED_BOOT_COMPLETED` での張り直しと、ロックを解除しないまま鳴るか・
  directBootAware の鳴動画面が出るかを見る。AC-4・E-10・W1-9 の前提。
  → W1-4b（2026-09-27）で 1 回の許可は得たが見送った（試験の日は利用者が外出先で端末を使っていて、再起動で PIN の入力が要り、解除まで他のアプリの通知も
  止まるため。「迷えば未実施」の指示）。スパイクは削除したので、SEED の APK 向けの手順（デバッグの受信機の `SCHEDULE` と音を最小にする引数・控えの読み出し・
  `am get-started-user-state 0`・解除前は受信機が届かないので `am force-stop` で止めて音量を確かめる）を roadmap §2.9.2 の T4 に置いた。見るもの: `PlatformProvider`
  （directBootAware でない）は解除まで作られない見込みで、`:seed_platform` の起動時の照合（`AlarmStartup`）も解除まで走らない（推論）。
- [x] **実際の GameActivity をフルスクリーン通知から冷えた状態で出す計測（W1-4b で）** — 2026-09-27（W1-0 から移した）/ 同日 W1-4b の T1 で実施（roadmap §2.9.2）。
  → 画面オフ・ロック中・両プロセスが無い状態から、3 回の中央値で音 +261 ms・フルスクリーン通知の起動 +308 ms・エンジンの最初のフレーム +2030 ms（最大 +2102 ms）・
  通知から最初のフレームまで 1722 ms。AC-1・AC-12 を満たした（空の開始シーン・スクリプトの型 1 つ。実アプリの鳴動画面では W1-7 で測り直す）。W1-0 の鳴動画面は
  Java だけの Activity（予定時刻から +0.95 s）で、SEED の冷えた起動（.NET の展開・CLR・GPU・シーン。X-1）を含む時間は測っていない。
  `PlatformEntry` と起動理由を作った直後に測り、AC-12（フルスクリーン通知から 3 秒以内）と AC-1（予定時刻から画面 3 秒以内）に届くかを見る。
  → 2026-09-27 W1-4a で `PlatformEntry` と起動理由はできた（docs/android.md §25.12）。端末が USB に無いので計測は W1-4b（§25.12.8 の (b)）。
  `LaunchInfo.FiredAtUtcMs`（配信を受けた時刻）をスクリプトから読めるので、最初のフレームまでの遅れをスクリプトのログでも出せる。
- [ ] **Android 17 の背面の音の制限への対応（X-7）** — 2026-09-27（W1-0）。Android 17 は、見えている画面も前景サービスも無いアプリの音・
  音声フォーカス・音量の変更を黙って失敗させ、targetSdk 37 では背面の前景サービスに「使用中」の権能を求める（例外は正確なアラームの権限＋
  `USAGE_ALARM`。https://developer.android.com/about/versions/17/changes/bg-audio ）。Android 16 の実機でも、画面の無い鳴動中に
  `AudioHardening … would be muted … level: full` が記録された（今は記録だけ）。鳴動は `USAGE_ALARM` に固定し、`ForceVolume`・`KeepVolume` の
  音量の変更と v2 の読み上げが免除に入るかを Android 17 で確かめる。targetSdk を 37 に上げる前に済ませる。roadmap §2.6・§4。
- [ ] **`adb install` が Play Protect の確認で止まる（初めてのパッケージ）** — 2026-09-27（W1-0）。ロック画面の裏に出た「アプリをスキャンに
  送信しますか」の確認で約 43 分止まった（利用者は操作しておらず、確認の画面が出たまま完了）。自動の実機試験と、SeedAndroid の `install`
  （新しい applicationId を初めて入れるとき）でも起こりうる（未確認）。インストールに上限時間を付け、止まったら端末で確認に答えるよう案内する。
- [x] **W1-0 の後片付け: 端末に `com.seedengine.platformspike` が残っている** — 2026-09-27 記載 / 同日 W1-4b の頭で対応。試験の終盤（04:32 ごろ）に端末が USB から外れ、
  アンインストールできなかった（予約・前景サービス・Doze と電池の模擬は外れる前に戻してあることを 04:12 の記録で確認）。再接続して
  `runtime/android/spikes/platform_spike/scripts/cleanup.sh` を流す。
  → 11:11 に `cleanup.sh` の端末の操作を行った（このアプリの保留中の予約 0・プロセスなしを確かめたので受信機への STOP・CANCEL_ALL は省き、
  `deviceidle unforce`・`battery reset`・`am force-stop`・`adb uninstall`〈Success〉）。`pm list packages` に無いことを確かめた。
- [x] **app_platform_roadmap.md §2.6 の誤り 2 件** — 2026-09-27 記載 / 同日対応（W1-0）。「サイドロードではフルスクリーン通知が既定で無効」は
  誤りで、Play 以外のインストールでは既定で有効（AOSP の記述と実機）。adb の `am broadcast -a …`（`-n`/`-p` なし）はマニフェストの受信機に
  届かない（手順に `-n` を足した）。§2.6・§2.9 を直した。
- [x] **W1-1 橋渡し（native → Java の呼び出しが 1 つも無い）** — 2026-09-27 記載 / 同日実装（実機の確認は下の項目）。`SeedPlatform.invoke(module, method, byte[] json)` と
  `nativeOnPlatformEvent(byte[] json)` の 2 本（＋起動時の `nativeRegisterPlatformBridge(Class)`）、`ScriptHostApi` の新カテゴリ（`platform_invoke`・
  `platform_poll_events`）、C# の `SEED.Platform`（`Platform`・`PlatformDiagnostics`・`PlatformEvents`）、デスクトップの模擬（`DesktopSimBridge`）、
  `:seed_platform` の `PlatformProvider`（ping・version・register_callback・emit_test_event・poll_events）と `EventJournal`、デバッグ版の
  `DebugPlatformReceiver`。JNI は `jni` クレート 0.22.4（Cargo.lock の版を共有）。client は unstable で取り、最初の呼び出しは背面で接続を始めて
  `connecting` で失敗し、つながると `platform.connected` が届く。Rust の単体テスト（新規 28 件）・PC の Play（模擬の ping と試験イベント）・APK の作成まで確認。
  正典は docs/android.md §25。
- [ ] **W1-1 の実機の確認（JNI の往復・`Bundle.putBinder` の呼び鈴・unstable な client）** — 2026-09-27。実装した日は Pixel 6a が USB に無く、
  実機では一度も通していない。docs/android.md §25.7 の手順で、Wake or Pay の `scenes/PlatformSmoke.scene` を `SeedAndroid run --scene` で起動し、
  `connecting` → `platform.connected` → ping 3 回（`:seed_platform` の pid・1 ms 未満か）→ 試験イベントの到達を logcat で見る。あわせて adb の
  `DebugPlatformReceiver`（PING・EMIT_TEST_EVENT）、`:seed_platform` が最初の接続まで居ないこと、`run-as … kill -9 <:seed_platform の pid>` の後に
  `platform.disconnected` → 次の呼び出しで接続し直すこと（ゲームのプロセスが道連れにならないこと）を確かめる。結果を roadmap §2.10 と android.md §25.8 へ。
  → 2026-09-27（W1-6 の実機の回。docs/android.md §25.15.10）: PlatformSmoke の流れは通った（`connecting` → `platform.connected` 660 ms・`:seed_platform` の pid 3775 →
  ping 1.3 / 0.82 / 0.74 ms → 試験イベントが届いた）。`DebugPlatformReceiver`・`kill -9` の後の接続し直しは未実施。
- [ ] **プラットフォームの記録の永続化と「既読」の扱い（W1-1 の割り切り）** — 2026-09-27（W1-1）。`EventJournal` はメモリの中だけで、
  `platform.poll_events` で取り出した時点で既読にして捨てる。`:seed_platform` が死ぬと未読が消え、取り出した直後にメインプロセスが死ぬと
  そのイベントはスクリプトへ届かない。目覚ましの記録（鳴った・止めた）は取りこぼせないので、W1-3 で端末保護ストレージへ書き、W1-4 で
  エンジンの受け取りの確認（ack）と、前面へ戻ったとき（onResume）の未読の取り直し（呼び鈴の取りこぼしの保険。roadmap §2.2）を決める。
  → **W1-3 で永続化は済み**（端末保護ストレージの `seed_platform/journal.json` へ足す・取り出すたびに原子的に書く。docs/android.md §25.11.6）。
  残りは ack と onResume の取り直し（W1-4）。取り出した直後にメインプロセスが死ぬと届かないのは変わらない。
  → W1-4a（2026-09-27）の範囲に入れなかったので、ack と onResume の取り直しは W1-4b 以降（W1-5 の通知の操作の知らせと合わせて決める）。
  → W1-5（2026-09-27）で決めた: 通知の本文・ボタンの操作は記録（EventJournal）を通らず、PlatformEntry 行きの Intent（起動理由。死んでいれば起動、生きていれば
  `platform.launch`）で届くので、ack・onResume の取り直しの要否は通知と独立（残るのは目覚ましの記録だけ。W1-4b 以降のまま）。権限のイベントもメインプロセスの中で作る（seq 0）。
  → W1-4b（2026-09-27）で見えた例: デバッグの受信機の放送だけで起きたメインプロセス（エンジンが居ない）も接続のときに呼び鈴を登録するので、その間に
  取り出された記録（`platform.alarm.fired` など）は `SeedPlatform.deliverEvent` で「エンジンが読み込まれていないので、イベントを捨てました」になる
  （既読になって、後で起きたエンジンには届かない）。配布版では接続を始めるのはエンジンの呼び出しだけ（`SeedPlatform.init` は入れ物を作るだけ）なので、
  起きるのはデバッグの受信機を使ったときだけの見込み（コードを読んだ推論）。ack を入れるときは「エンジンが受け取ったら既読」にする。ack はまだ W1-7 で決める。
  関連: `runtime/android/app/src/main/java/com/seedengine/runtime/platform/service/EventJournal.java`。
- [ ] **最初の SEED.Platform の呼び出しが `connecting` で失敗する（使い勝手）** — 2026-09-27（W1-1）。`:seed_platform` の起動（約 120 ms）を
  描画のスレッドで待たないための形だが、アプリの API（W1-3 の `Alarms.Schedule` など）が起動の直後に失敗しうる。W1-2 の `android.features` に
  機能が書かれたプロジェクトでは、起動時（Activity の onCreate の後）に背面で接続を始め、最初のフレームまでにつながっている形を検討する
  （機能を使わないゲームは今どおり起こさない）。あわせてスクリプトから「つながっているか」を IPC 無しで読む手段（今はイベントで知るだけ）。
  W1-2 では手を付けていない（範囲外）。機能を APK へ焼き込む仕組み（生成する `res/values/seed_platform.xml`）はできたので、W1-3 で
  「起動時に接続する」bool を機能の表から生成し、MainActivity が読む形にできる。
- [x] **`DeadObjectException` の後の呼び直しは冪等な命令が前提** — 2026-09-27（W1-1）記載 / 同日 W1-3 で目覚ましの命令を冪等にした。
  `PlatformConnection.invoke` は `:seed_platform` の死を知ると client を閉じて接続し直す（待ってよい呼び出し元は同じ命令をもう一度送る）。
  死んだ瞬間に処理済みだった命令は 2 回走りうる。→ `alarm.schedule` は同じ ID で置き換え、`alarm.cancel` は無い ID でも成功、`cancel_all` も
  何度でも同じ結果（docs/android.md §25.11）。W1-4 の停止（`StopRinging`）も同じ形にする。
  → W1-4a で `alarm.stop_ringing` も冪等にした（鳴っていなければ `stopped:false` で成功。docs/android.md §25.12.3）。
- [x] **`DebugPlatformReceiver` を権限で守っていない（デバッグ版だけ）** — 2026-09-27（W1-1）記載 / 同日 W1-7 で対応。`app/src/debug/AndroidManifest.xml` の
  受信機に `android:permission="android.permission.DUMP"` を付けた。実機（Pixel 6a・T4 の後なので Android 17）の `dumpsys package android` で DUMP は
  `prot=signature|privileged|development`、`com.android.shell` は `granted=true`。付けた後も adb の `am broadcast` の命令（`LIST`・`SCHEDULE`・`STOP_RINGING` など）は
  すべて答えた。他のアプリから送れないことは、送り手のアプリが端末に無いので実機では試していない（保護の段階からの推論）。以下は記載時のメモ。exported=true・permission なしなので、
  デバッグ版の APK が入った端末では他のアプリからも ping・試験イベントを送れる（害は小さい）。androidx の `ProfileInstallReceiver` と同じく
  `android:permission="android.permission.DUMP"`（adb のシェルは持つ）で守れる見込みだが、実機で adb から届くことを確かめてから変える。
  → W1-4b（2026-09-27）で目覚ましの命令（`SCHEDULE`・`CANCEL_ALL`・`STOP_RINGING`・`GET_RINGING`・`LIST`）を足したので、デバッグ版では他のアプリから
  予約・停止・取り消しもできる（`force_volume` で端末のアラームの音量も一時的に変えられる）。デバッグ版だけなので害は限られるが、守る優先度は上がった。
- [x] **W1-2 機能の opt-in（`android.features`・`deep_links`・`system_bars`・`app_category`）** — 2026-09-27 記載 / 同日実装（実機の確認は下の項目）。
  エディタのモデル（`AndroidAppSettings`・`AndroidDeepLinkSetting`・`AndroidSystemBarsSetting`・`AndroidAppCategorySetting`）とプロジェクト設定ウィンドウ
  （「Android のプラットフォーム機能（アプリ向け）」。判断は WPF 非依存の `AndroidPlatformSettingsEditor`）、機能の表 `runtime/android/platform_features.json`、
  SeedAndroid の断片の生成（`editor/src/Android/Platform/`。APK の工程の Gradle の前に `app/src/seedFeatures/` へ。空でも必ず書く・追跡しない・
  中身の指紋で APK を作り直す）、Gradle の `androidComponents.onVariants`（`addStaticManifestFile`・`addStaticSourceDirectory`。E-04）と
  `seed.appCategory`、`SystemBarsController.java`、Play の要件チェック（`AndroidPlatformFeatureChecks`・`permission_policies`）。
  単体テスト（`AndroidPipelineTests` の `PlatformFeatureTests`・`PlatformSettingsTests`、`ProjectSystemTests`）と Wake or Pay の実ビルドの aapt2 で確認。
  正典は docs/android.md §25.10。
- [ ] **W1-2 の実機の確認（システムバーを出したままの起動・安全領域・生成した権限）** — 2026-09-27（W1-2）。実装した日は Pixel 6a が USB に無かった。
  Wake or Pay（`system_bars: "visible"`・`app_category: "productivity"`・`features: ["alarm", "notifications"]`）を `SeedAndroid run --scene scenes/PlatformSmoke.scene`
  で起動し、ステータスバーとナビゲーションバーが出たままか（`dumpsys window` の InsetsSource の visible か screenshot）、logcat の
  `[SEED SCREEN] Java 報告` の insets にバーの分が入るか（ScreenReporter が `systemBars()` を足す。コードを読んだだけ）、`システムバー: 出したまま` の
  起動ログ、`dumpsys package com.wakeorpay.seed` の権限（USE_EXACT_ALARM は許可済み・POST_NOTIFICATIONS は未許可のはず）を見る。
  上の W1-1 の実機の確認（§25.7）と一緒に行う。結果を android.md §25.10.6 と roadmap §2.10 へ。
  → 2026-09-27（W1-6 の実機の回）: `システムバー: 出したまま（system_bars=visible）` の起動ログ・`InsetsSource statusBars visible=true`・窓の `fl=` に `FULLSCREEN` なし・
  `[SEED SCREEN] Java 報告: … insets=(0,132,0,63)`（`bars=(0,132,0,63)`）を見た。権限の `dumpsys package`・appCategory は未確認。
- [ ] **プロジェクト設定ウィンドウの「Android のプラットフォーム機能」小節の目視** — 2026-09-27（W1-2）。WPF の画面はエージェントが見られないので、
  チェックボックス・コンボ・ディープリンクの行（追加・削除・`deep_links` の機能を切るとたたまれる）・注意と誤りの行の見た目と、保存で止まる動きを
  利用者が確かめる。判断と値の出し入れは単体テスト済み。関連: `editor/src/ProjectSettings/ProjectSettingsWindow.AndroidPlatform.cs`。
- [ ] **前景サービスの種類が `<service>` に宣言されているかの要件チェック（W1-4 で）** — 2026-09-27（W1-2 から持ち越し）。roadmap §2.5 は
  「前景サービスの種類が宣言されているか」も挙げていたが、W1-2 の時点ではサービスが無いので、`FOREGROUND_SERVICE_MEDIA_PLAYBACK` の Play Console の
  申告の注意だけを足した。`RingService` を機能の表に足す W1-4 で、配布物のマニフェスト（`aapt2 dump xmltree`）の `android:foregroundServiceType` と
  `FOREGROUND_SERVICE_<種類>` の権限の組を確かめる項目を `AndroidPlatformFeatureChecks` に足す。
  → W1-4a（2026-09-27）で `RingService`（`mediaPlayback`）を機能の表に足したが、この要件チェックは範囲外として入れていない（機能の表の
  単体テストと aapt2 の手の確認で種類と権限の組を確かめた）。W1-4b か W1-7 で足す。
- [ ] **システムバーを出したままのときの文字色（明暗）を選べない** — 2026-09-27（W1-2）。W1-6 の指示（`Window` の 3 つの切り替え）に入らず持ち越し（W1-7 か W2 のテーマと一緒に）。
  → 2026-09-28 の W2-9（テーマ）でも入れていない（指示の範囲外）。テーマの明暗（`UiTheme.Brightness`・`UiTheme.Changed`）が出来たので、`Window` にバーの明暗の切り替えを足し、
  明るいテーマで暗いアイコンにする形で入れる（W2-11 か W3）。
  テーマの既定のまま（暗い AppCompat のテーマなので白い文字の見込み。
  推論・実機で未確認）。明るい画面のアプリではステータスバーの時刻・電池が見えにくい。`Window` の API（W1-6 の `SetSystemBarsVisible` の隣。と必要ならプロジェクト設定）で
  `WindowInsetsControllerCompat.setAppearanceLightStatusBars` を選べるようにする。
- [x] **W1-3 目覚ましの予約（setAlarmClock・予約の控え・再起動／時刻・タイムゾーン／更新／権限の変化で張り直す・音源の書き出し）** — 2026-09-27 記載 / 同日実装（実機の確認は下の項目）。
  W1-0: `BootReceiver` は強制停止からの復帰（Android 15+ は停止状態から出たときに `BOOT_COMPLETED`。実機では `LOCKED_BOOT_COMPLETED` も）も兼ねる。
  張り直しは必ず `setAlarmClock`（`BOOT_COMPLETED` から直接鳴らさない）。
  → `:seed_platform` の `platform/service/alarm/`（`AlarmModule`・`AlarmStore`〈端末保護ストレージの `seed_platform/alarms.json`・一時ファイル → fsync →
  rename〉・`AlarmScheduler`〈要求コード固定＋data の URI で予約ごとの PendingIntent〉・`AlarmBook`・`AlarmReceiver`・`BootReceiver`〈6 つの放送〉）、
  機能の表の `alarm` に受信機 2 つ、`EventJournal` の永続化、`platform.paths` とメインプロセスの音源の書き出し（`bridge/alarm/sound_export.rs`・
  糊の `alarm_prep.rs`）、デスクトップの模擬（`desktop_sim/alarm_*`）、C# の `Alarms`・`AlarmRequest`・`ScheduledAlarm`・`AlarmFiredEvent` 等。
  Rust の単体テスト・`AndroidPipelineTests`・PC の Play（5 秒後の予約 → `platform.alarm.fired` → 控えが空）・APK の aapt2 と dexdump まで確認。正典は docs/android.md §25.11。
- [ ] **W1-3 の実機の確認（予約・dumpsys alarm・発火の記録・BootReceiver・音源の書き出し）** — 2026-09-27（W1-3）。実装した日は Pixel 6a が USB に
  無かった。docs/android.md §25.11.7 の手順で、`scenes/PlatformSmoke.scene` の 5 秒後の予約が `dumpsys alarm` に alarm clock として出ること・
  `platform.alarm.fired` が届き控えが空になること・強制停止 → 開き直しで `BootReceiver` が過ぎた予約を `platform.alarm.missed`（device_off）に
  すること・`run-as` で端末保護ストレージ（`/data/user_de/0/<APP>/files/seed_platform/`）の控え・記録が読めることを見る。`sound_asset` の書き出しは
  Wake or Pay に音のアセットがまだ無いので、音を足してから確かめる。W1-1・W1-2 の実機の確認（上）と一緒に行う。
  → 2026-09-27（W1-6 の実機の回）: 5 秒後の予約が予定から 13 ms で `platform.alarm.fired`・控えが空になった。`dumpsys alarm`・`BootReceiver`・`run-as` は未実施。
- [ ] **目覚ましの音源の書き出しに掃除が無い** — 2026-09-27（W1-3）。`files/seed_platform/sounds/<内容のハッシュ>.<拡張子>` は内容が変わるたびに増え、
  消さない（予約から外れた音も残る）。書き出しはメインプロセス、控えは `:seed_platform` にあり、掃除をどちらがいつ行うか（控えのどの予約からも
  指されていないファイルを、予約の直後の書き出しとぶつからない時機に消す）を決める。音は小さい見込みなので急がない。関連:
  `runtime/src/engine/platform/bridge/alarm/sound_export.rs`。
- [x] **Android 10〜14 で強制停止の後に予約が戻らない（起動のたびの張り直しが無い）** — 2026-09-27（W1-3）記載 / 同日 W1-4a で対応
  （`PlatformProvider.onCreate` → `AlarmStartup` が背面のスレッドで `AlarmBook.reconcile`: 控えのまだ先の予約で PendingIntent が無いものだけを張り直し、
  過ぎた予約は `alarm.missed(device_off)`。docs/android.md §25.12.6。アプリが `:seed_platform` を起こすまでは戻らないのは同じ。実機は未確認）。
  強制停止で予約も PendingIntent も消え、
  Android 15+ は停止状態から出たときに `BOOT_COMPLETED` が届いて `BootReceiver` が張り直すが、14 以前は次の再起動まで届かない（公式「Android 15 の
  動作の変更」）。roadmap §2.6 の「保険として起動のたびの全予約の張り直し」は W1-3 では入れていない（`:seed_platform` の起き方〈プロバイダの
  onCreate・受信機〉と `BootReceiver` の記録が二重にならない入れ方を決める必要がある）。W1-4 か W1-7 で、エンジンの接続（`register_callback`）の
  ときに控えの予約で張られていないもの（`FLAG_NO_CREATE` で PendingIntent が無いもの）だけを張り直す案で入れる。
- [x] **再起動で `platform.alarms.rescheduled`（boot）が 2 回記録されうる** — 2026-09-27（W1-3）記載 / 同日 W1-7 で対応。`BootRearmGate` が
  張り直しを済ませた起動の番号（`Settings.Global.BOOT_COUNT`）を端末保護ストレージの `boot_rearm.json` に書き、同じ起動の 2 回目以降の起動の放送は
  消えている予約だけを張り直して（`AlarmBook.reconcile`）、何かを張り直した・鳴らなかったときだけ記録する。実機（W1-7）: 強制停止の後に停止状態から出たとき
  4 回（`LOCKED`・`BOOT`・`LOCKED`・`BOOT`）届き、1 回目だけ張り直し、残り 3 回は「同じ起動の 2 回目以降」で記録なし（予約 0 件の回）。
  普通の再起動は T4 で `rescheduled(boot)` 1 件（`BOOT_COMPLETED` は解除後で、控えが空だったので記録なし）。以下は記載時のメモ。`LOCKED_BOOT_COMPLETED` と `BOOT_COMPLETED` の
  両方で張り直すため（2 回目は `missed` 0 で張り直すだけ）。アプリの処理は冪等に書けば害は無いが、同じプロセスで直前に張り直したなら 2 回目の記録を
  省く形を W1-7 で検討する。
  → W1-4b（2026-09-27）: 停止状態から出たとき（`adb install -r` の後の最初の放送〈`-f 0x20`〉）は、`LOCKED_BOOT_COMPLETED`・`BOOT_COMPLETED` が **2 回ずつ（計 4 回）**
  `BootReceiver` に届いた（どれも控え 0 で記録なし。同じ `:seed_platform` のプロセス・約 35 ms の間）。控えがあるときは `rescheduled`（boot）が最大 4 回になりうる。
- [x] **W1-4 鳴動（前景サービス・音量の指定と漸増・バイブ・WakeLock・音声フォーカスの喪失で止めない・安全弁・重なりを捨てない・フルスクリーン通知・起動理由・信頼できる起動での showWhenLocked）** — 2026-09-27 記載 /
  同日 W1-4a（実機の計測を除く部分）を実装。正典は docs/android.md §25.12。実機の計測と調整は下の「W1-4b」。
  W1-0 からの見直し: 頭で実 GameActivity の冷えた起動を測る（上の項目）。`AlarmReceiver` は真っ先に `startForegroundService`（配信に付く一時許可は
  10 秒）し、控えの fsync はその後。音の準備を前倒しする（冷えたプロセスでは `startForeground` から音まで約 340 ms）。端末の使用中はヘッドアップ通知に
  なるので、本文のタップから鳴動画面へ行けるようにする。音は `USAGE_ALARM` 固定（X-7）。
  W1-3 からの持ち越し: `RingService` の起動は `AlarmReceiver.onReceive` の先頭の印のところへ（`AlarmBook.fire` より前）。`AlarmClockInfo` の
  showIntent（`AlarmScheduler.showIntent`。今はランチャーと同じ起動）を `PlatformEntry` 行きに替える。`AlarmReceiver` は directBootAware なので、
  ロック解除の前に鳴ったときの `RingService`（directBootAware にするか・Java だけの鳴動画面か）を W1-9 と合わせて決める。既定の音（`res/raw`）と、
  `sound_path` が読めないときの落とし方。控えの鳴らし方の欄（`force_volume` 等）はここで初めて使う。
- [x] **W1-4b 鳴動の実機の確認と計測（端末が USB に戻ってから）** — 2026-09-27（W1-4a）記載 / 同日 W1-4b で実施（下の → ）。docs/android.md §25.12.8 の手順で、PlatformSmoke の
  1 回の予約が鳴って 3 秒で止まること（`dumpsys activity services` の RingService・`dumpsys media.audio_flinger` の USAGE_ALARM・`dumpsys notification` の
  `seed_platform_alarm`）、`LaunchReason` のログ、`window.set_show_when_locked` を見る。続けて (a) 最近のタスクから消しても鳴る（AC-2。`am stack remove`）、
  (b) 画面オフ・ロック中の冷えた起動（AC-1・AC-12。フルスクリーン通知 → `PlatformEntry` → GameActivity。別名に置いた lib_name の meta-data が要るか・
  別名の `onNewIntent` の部品名もここで確かめる）、(c) 使用中のヘッドアップ通知の本文のタップ・「開く」。音の開始の速さ（専用のスレッドでの準備）も測る。
  実機では `force_volume` を試さない（端末の音量を変えない約束）。
  → 2026-09-27（W1-6 の実機の回）: 1 回の予約が鳴り、3004 ms で `StopRinging` → `ring_stopped(stopped)`・`GetRinging` が空。振動は `dumpsys vibrator_manager` で
  `usage: ALARM` 3031 ms・`cancelled_by_user`。計測と (a)〜(c)・`dumpsys` の RingService / audio_flinger / notification は未実施。
  → **2026-09-27 W1-4b で実施**（roadmap §2.9.2・android.md §25.12.8 の「実測」）。デバッグの受信機に `SCHEDULE`・`CANCEL_ALL`・`STOP_RINGING`・`GET_RINGING`・`LIST`
  （と音を最小にする `force_volume`・`vibrate`・`fade_in_seconds` の引数）を足し、スクリプトの無い開始シーンで鳴らした。(b) 冷えた起動は音 +261 ms・最初のフレーム +2030 ms
  （中央値）、`dumpsys` で `RingService` の `isForeground=true types=0x2`・`USAGE_ALARM` の `state:started`・通知 `seed_platform_alarm`（fullscreenIntent と contentIntent）・
  ロック画面の上（`mKeyguardOccluded=true`）。(a) タスクを消しても音は続いた。別名の `onNewIntent` は既存の MainActivity へ届いた（Activity は積まれない）。
  (c) はアプリが前面のときにヘッドアップ通知になり、利用者の「開く」のタップ（ロック中は指紋の解除を求めた）で `notification_action` が届いた。本文のタップは未確認。
  残りは下の新しい項目と W1-7。
- [x] **鳴動の待ち行列がプロセスの中だけ** — 2026-09-27（W1-4a）記載 / 同日 W1-7 で対応。鳴動の状態（鳴っている 1 つと待ち行列）を端末保護ストレージの
  `ringing.json` に写し（`RingStateStore`）、殺されたら見張りの予約（`RingWatchdog`）で待ち行列ごと戻す（下の項目）。戻せないとき（強制停止・再起動）は
  待ち行列の予約も `ring_stopped(error)` を記録する。待ち行列を含む復元は実機では試していない（1 件の鳴動で試した）。以下は記載時のメモ。
  鳴動中に配信された予約は `RingRegistry` の待ち行列にだけあり、控えからは消してある。
  待っている間に `:seed_platform` が殺されると、その予約は黙って失われる（`ring_stopped` も `missed` も記録されない）。待ち行列を控えに残すか、
  端末保護ストレージへ書くかを W1-7 で決める。関連: `platform/service/alarm/ring/RingRegistry.java`。
- [x] **`:seed_platform` が殺されると鳴動が黙って止まる（`START_NOT_STICKY`）** — 2026-09-27（W1-4a）記載 / 同日 W1-7 で対応（設計案の (b)）。
  鳴動中は `ringing.json` に状態を控え、見張りの予約（`setExactAndAllowWhileIdle`・`ELAPSED_REALTIME_WAKEUP`・20 秒先・5 秒ごとに先へ送る）を張る。
  殺されると見張りが発火し、`RingWatchdogReceiver` → `RingRecovery` が鳴動を戻す（前景サービスの起動は正確な予約の配信の一時許可で `Allowed`・理由
  `ALARM_MANAGER_WHILE_IDLE`）。実機（W1-7 の T5。2 回）: `kill -9` から 19.8 s / 15.3 s で見張りが発火し、音が戻り（無音 20.1 s / 15.4 s）、`force_volume` を
  控えの元の値でかけ直し、`STOP_RINGING` で止まって音量が 5 へ戻り `ringing.json` と見張りが消えた。戻した回数は 5 回まで（起動の直後に落ち続けるときに繰り返さない）。
  安全弁の時刻は最初の鳴り始めから数える。強制停止・再起動では見張りも消えるので戻せず、次に `:seed_platform` が起きたときに `ring_stopped(error)` を記録する。
  残る制限は下の「W1-7 で見つけたこと」。以下は記載時のメモ。低メモリ等でプロセスごと殺されると音が止まり、
  `ring_stopped` も記録されない。`START_REDELIVER_INTENT` で作り直すと、背面からの前景サービスの起動の制限に当たるかを含めて W1-4b の実機で確かめて決める
  （鳴らしている予約を端末保護ストレージへ書いておけば、作り直したときに続きを鳴らせる）。
  → **W1-4b の T5（2026-09-27）で実機で確かめた（直していない）**: 鳴り始め +4.4 s に `run-as … kill -9 <:seed_platform>` → 約 0.1 s でプロセスの死・
  `am_foreground_service_stop`・鳴動の通知の取り消し（`notification_canceled`）・AudioFlinger のトラックの終了（音が止まった）。10 s 待っても作り直されない
  （`dumpsys activity services` が空）。メインプロセスは生き残った（unstable な client。ロック画面の上の MainActivity もそのまま）。次の命令で `:seed_platform` は作り直されたが
  `RingRegistry` は空で `stop_ringing` は `stopped:false`、`ring_stopped` も `missed` も記録されず、アプリも利用者も鳴動が消えたことを知る手段が無い。
  **設計案**（roadmap §2.9.2）: ① 鳴らし始めに鳴動中の予約・鳴り始め・安全弁の時刻・`force_volume` の前の音量を端末保護ストレージ（`seed_platform/ringing.json`）へ書き、
  止めたら消す。② 復帰は (a) `START_STICKY` で作り直された `onStartCommand(null)` から続きを鳴らす（作り直しの遅れと背面からの前景サービスの起動の制限が未確認）か、
  (b) 鳴動中は数秒先の「見張りの予約」（`setAlarmClock`）を張り直し続け、止めたら取り消す。死ぬと見張りが発火して `AlarmReceiver` が ①から鳴動を戻す
  （`setAlarmClock` の配信が前景サービスの起動を許されるのは T1〜T3 で確かめた）。(b) を推す。③ 戻せないときは `alarm.ring_stopped{reason:"error"}` を記録する。
  ④ 起動時の照合（`AlarmStartup`）で ①の残りを見つけたら音量を戻す（下の項目）。どれも T5 をもう一度行って確かめる（音量を変える予約では行わない）。
- [x] **`:seed_platform` が鳴動中に死ぬと `force_volume` の音量が戻らない** — 2026-09-27（W1-4b）記載 / 同日 W1-7 で対応。変える**前に**元の音量を
  `ringing.json` の volume の欄へ書き（`AlarmStreamVolume.force`）、戻したら消す。殺されて見張りで戻した鳴動は控えの元の値を「元」としてかけ直し、止めたときに戻す。
  戻せなかったとき（強制停止・再起動の後）は `RingRecovery.onStartup` が戻す。ただし **Android 17（T4 の再起動で更新された端末）は受信機だけで起きた（前景サービスも見える画面も無い）
  プロセスからの `setStreamVolume` を AudioHardening が無視した**（実機 W1-7。`AudioHardening volume control … ignored … level: partial`）。そこで戻ったことを
  `getStreamVolume` で確かめ、戻っていなければ控えを残し、アプリが前面に出たとき（`MainActivity.onResume` → `LeftoverVolumeNudge` → `PlatformProvider.call`）か
  次の鳴動で戻す（実機: 強制停止 → 背面の起動では 1 のまま → MainActivity を開いて約 0.1 s で 5 に戻った）。**アプリを開くか次の目覚ましが鳴るまでは
  利用者のアラームの音量が下がったまま**（下の「W1-7 で見つけたこと」）。以下は記載時のメモ（コードを読んで見つけた。実機では試していない）。
  `AlarmStreamVolume.restore`（STREAM_ALARM を鳴る前の値へ戻す）は `RingAudio` の停止の経路（`stopOnAudioThread`）でしか呼ばれず、元の値はプロセスの中にしか無い。
  低メモリ・強制停止などで鳴動中に `:seed_platform` が消えると、利用者のアラームの音量が `force_volume` の値のまま残る（次の目覚ましが小さすぎる・大きすぎる）。
  直し方の案: 変える前の値を端末保護ストレージへ書き（上の項目の①）、`:seed_platform` の起動時の照合と `BootReceiver` で残っていれば戻して消す。
  W1-4b の実機の試験は、利用者の音量を守るため `force_volume` を使う予約では `:seed_platform` を殺していない（T5 は `force_volume` なしで行った）。
  関連: `platform/service/alarm/ring/AlarmStreamVolume.java`・`RingAudio.java`。
- [x] **既定の音のループの継ぎ目で音が途切れている見込み** — 2026-09-27（W1-4b）記載 / 同日 W1-7 で対応。`RingLoopPlayer`（同じ音源の MediaPlayer を
  `setNextMediaPlayer` で次々につなぐ。音源の形式を問わない〈予約の mp3・ogg にも効く〉。1 秒より短い音源は `setLooping` のまま）。実機の
  `dumpsys media.audio_flinger` のトラックの記録で、`setLooping` の鳴動（W1-7 の T4・古い APK）は 21 s の間に継ぎ目ごとの `AT::remove … I` → `AT::add … A` が
  8 回（途切れ 59〜68 ms・中央値 66 ms）、連鎖の鳴動（T7）は 12.8 s（継ぎ目 5 回ぶん）で最初の `add` と止めたときの `remove … T` の 2 行だけ（途切れ 0）。
  聴いては確かめていない（音は最小）。以下は記載時のメモ。`res/raw/seed_alarm_default.wav`（2.22 s）を `MediaPlayer.setLooping(true)` で
  鳴らすと、`dumpsys media.audio_flinger` の履歴で AudioFlinger のトラックが約 2.4 s ごとに止まり（`AT::remove … I`）約 65〜70 ms で再開する（`AT::add … A`）。
  全試験で同じ形（T2 の 19 s の鳴動で 4 回）。聴いて確かめていない（利用者の依頼で音を小さくしていた）。直し方の案: `setNextMediaPlayer` で 2 つを交互に・
  `AudioTrack` の静的モードと `setLoopPoints`・音源の尾に無音を入れない書き出し、のどれかを試し、トラックが止まらないことを同じ履歴で確かめる。予約の音（`sound_path`）の
  MP3・OGG でも同じか（形式による）。関連: `ring/RingAudio.java`・`runtime/android/tools/gen_alarm_default_tone.py`。
- [ ] **冷えた起動の最初のフレームまでの内訳（X-1 の根拠）** — 2026-09-27（W1-4b）。画面オフ・ロック中の冷えた起動で、エンジンの最初のフレームは予定時刻 +2.03 s
  （中央値。AC の 3 s に収まった）。内訳はメインプロセスの起動から `MainActivity.onCreate` まで 0.32〜0.77 s・DrawContext の作成 0.52〜0.70 s（パイプラインキャッシュ
  656 KiB を読んだ上で）・同梱 .NET の準備と CLR の起動 0.1〜0.2 s。空のシーンでこれなので、実アプリの鳴動画面（シーン・スクリプト・UI）に使える余りは約 1 秒。
  あわせて、システムの `Displayed`（+1.1 s）からエンジンの最初のフレーム（+2.0 s）まで約 0.9 s は窓の背景だけが見えている見込み（画面は見ていない。推論）。
  案: 鳴動画面に使う描画パイプラインだけを先に作る・窓の背景を鳴動画面の色にする（テーマの `windowBackground`）。既存の backlog「起動の初期化が android_main
  スレッドで同期に走る」と一緒に扱う。W1-7 で実アプリの鳴動画面で測り直す。
- [ ] **アプリが前面のときの次の目覚ましはヘッドアップ通知になる・「開く」はロック中は解除を求める** — 2026-09-27（W1-4b の T6）。MainActivity がロック画面の上にいる
  （画面点灯）ときに次の予約が鳴ると、フルスクリーン通知は起動されずヘッドアップ通知になった（`sysui_heads_up_status 1`）ので、`onNewIntent`（`platform.launch`）は来ない。
  前面のアプリは `platform.alarm.fired` と `GetRinging` で鳴動を知る必要がある（scripting_api.md の鳴動の節に注意として書く）。通知の「開く」はロック中は
  `dismissKeyguardThenExecute` で解除（指紋・PIN）を求め、解除の後に既存の MainActivity へ `onNewIntent`（`notification_action`）で届いた（Activity は積まれない）。
  確かめた中で解除なしで鳴動画面が出たのはフルスクリーン通知だけ（本文のタップは未確認）。「開く」・本文のタップを解除なしで出せるか（別名 `PlatformEntry` をマニフェストで `showWhenLocked` にする等。
  SystemUI の判断は未確認）を W1-7 で確かめる。
- [ ] **鳴動の通知を利用者がスワイプで消せる（Android 14+）** — 2026-09-27（W1-4a）。前景サービスの通知でも Android 14 以降は消せる（公式の変更。記憶・要確認）。
  消しても鳴動は続くが、通知からアプリへ戻れなくなる。`setDeleteIntent` で出し直すか、アプリの鳴動画面への別の入口（ステータスバーの目覚ましの印）で
  足りるかを W1-5 で決める。
  → W1-5（2026-09-27）では変えなかった（W1-5 の指示の範囲は新しい通知の API と権限だけ）。鳴動中はその予約がもう控えから消えているので、ステータスバーの
  目覚ましの印は次の予約のもの（鳴動への入口にならない）。出し直すなら `setDeleteIntent` に `:seed_platform` の exported=false の受信機（Activity を開かないので
  トランポリンの禁止に当たらない）を置き、`RingService` が同じ通知を出し直す案。W1-4b の実機で「消せるか」を確かめてから決める。
  → W1-4b（2026-09-27）の手がかり: ロック中の鳴動の通知には、システムが `NO_CLEAR` を付けていた（`dumpsys notification` の `flags=ONGOING_EVENT|ONLY_ALERT_ONCE|NO_CLEAR|
  FOREGROUND_SERVICE|HIGH_PRIORITY`。`originalFlags` には無い）。ロック中は消せない見込み。解除した後にスワイプで消せるかは確かめていない（手作業が要る）。
- [x] **目覚ましで起動したアプリがロック画面の上に残る（`SetShowWhenLocked(false)` の呼び忘れ）** — 2026-09-27（W1-4a）記載 / 同日 W1-6 で決めて対策（下の → ）。`alarm` の起動では
  エンジンが showWhenLocked を上げ、下ろすのはアプリ（scripting_api.md §7.13 に明記）。呼び忘れると AC-5 に反する。鳴動が止まった（`ring_stopped`）後に
  エンジンが自動で下ろす既定を足すかを W1-6 で決める（鳴動画面を出したまま解除の後の画面を続けたいアプリもあるので、今は自動にしていない）。
  → W1-6 で決めた: `ring_stopped` での自動の下ろしは入れない（上の理由のまま）。代わりに**ランチャー・最近のタスクからの開き直し**（`onNewIntent` で起動理由が
  `launcher`）で `LaunchReason` が `setShowWhenLocked(false)`・`setTurnScreenOn(false)` に戻す（忘れ対策。開き直せる＝ロックは解除済みなので鳴動画面は隠れない。
  docs/android.md §25.15.7）。残る穴: アプリを開いたまま電源ボタンを押したときは、下ろし忘れていればロック画面が出ない（AC-5）ので、アプリが下ろす約束は変わらない
  （scripting_api.md §7.13 の重要）。`deep_link`・通知の本文のタップ・`other` の開き直しでは下ろさない（指示の範囲が `launcher`。広げるかは下の W1-6 の項目）。実機では未確認。
- [x] **W1-5 通知と権限（チャネル・常駐・ボタン・トランポリン無し・実行時権限の結果イベント・正確なアラーム／フルスクリーン通知の状態と設定画面）** — 2026-09-27 記載 /
  同日実装（実機の確認は下の項目）。正典は docs/android.md §25.13（通知）・§25.14（権限）。`:seed_platform` の `service/notification/NotificationModule`、
  メインプロセスの `local/Permission*Command` と `platform/permission/`、`MainActivity` の `onResume`・`onRequestPermissionsResult` の受け口、デスクトップの模擬、
  C# の `Notifications`・`Permissions` と関連の型、PlatformSmoke の拡張。
- [ ] **W1-5 の実機の確認（通知・権限）** — 2026-09-27（W1-5）。実装した日は Pixel 6a が USB に無かった。docs/android.md §25.13.5・§25.14.7 の手順で、
  `dumpsys notification --noredact` に `smoke_note`（tag・id 7300・チャネル smoke・ongoing・ボタン 2 つ）が出て 3 秒で消えること、ボタン・本文のタップで
  `notification_action` / `notification_tap` の起動理由が取れること（冷えた起動・起動中の `platform.launch`）、`Permissions.Request(PostNotifications)` の確認の画面と
  `permission_result`、設定の画面から戻ったときの `permission_changed`、`onRequestPermissionsResult` と `onResume` の順序（記憶では結果が先）、確認の画面を
  外側のタップで閉じたときの rationale（記憶では変わらない＝`denied` のまま。公式の文書に記述が無い）を確かめる。
  確認の画面が出たら確かめる側は操作しない。`pm grant/revoke`・`appops set` で状態を作るのは利用者の了承を得てから。
  → 2026-09-27（W1-6 の実機の回）: 新しく入れた直後は `Check(PostNotifications) = Denied` で `Show` が `notifications_disabled`、最後の `Permissions.Request` の
  確認の画面で利用者が許可 → `permission_result`（granted）→ `permission_changed`（granted）。通知の表示・ボタン・本文のタップ・設定の画面は未実施。
- [ ] **`denied_permanently` の判定が「拒否の覚え」に頼る** — 2026-09-27（W1-5）。Android の `shouldShowRequestPermissionRationale` だけでは「一度も求めていない・
  確認の画面を外側で閉じた」と「二度拒否されて画面が出ない」を見分けられないので、メインプロセスの SharedPreferences（`seed_platform_permissions`）に
  「はっきり拒否された」を覚える（`platform/permission/PermissionHistory`）。アプリのデータを消した後・端末の移行の後は覚えが無く、永続の拒否が
  `denied` に見え続ける（`Request` しても画面が出ずに `denied` が返り、覚えも付かない。画面の外側で閉じた場合と区別できないため）。`Request` を続けて呼んで
  画面が出ないこと（結果が一瞬で返る等）を手がかりにする案は未検討。
- [ ] **`notifications` だけ・`alarm` だけの APK を見分けない** — 2026-09-27（W1-5）。機能 `notifications` は部品が無く、どちらの機能も `POST_NOTIFICATIONS` を入れるので、
  Java は宣言の有無（`DeclaredPermissions`）で判定し、`alarm` だけの APK でも `Notifications` が使える（指示の「notifications が無い APK では feature_not_enabled」とは
  `alarm` だけの APK で食い違う）。厳密に分けるなら、SeedAndroid が生成する `res/values/seed_platform.xml` に機能ごとの bool を足して読む（W1-2 の仕組み）。
- [ ] **アプリの通知の小さなアイコンが Android 標準の絵** — 2026-09-27（W1-5）。`NotificationFactory` は `android.R.drawable.ic_popup_reminder` を使う
  （アプリのアイコンはステータスバーでは形の影しか出ないため使っていない）。プロジェクト設定（例 `android.notification_icon`）から単色のアイコンを生成して使う形は未着手。
- [ ] **設定の画面を開いた要求は次の onResume まで結果が来ない** — 2026-09-27（W1-5）。`startActivity` が例外なく戻ったのに設定の画面が前面に出なかった
  （背面からの起動の制限など）ときは、利用者が次にアプリを前面へ戻すまで `permission_result` が届かない。スクリプトに時間切れは無い。
- [x] **W1-6 画面とアプリ（`Window.SetShowWhenLocked`・`SetKeepScreenOn`・`SetSystemBarsVisible`・`App.MoveTaskToBack`・`OpenUrl`・`Haptics`・ディープリンク）** — 2026-09-27 記載 /
  同日実装（実機の確認は下の項目）。正典は docs/android.md §25.15。
  → `Window.SetShowWhenLocked` と `App.LaunchReason`（起動理由）は W1-4a で先に入れた（docs/android.md §25.12.5）。残りはこの段階で。
  「アプリを終える API が無い」（Android 節）は `MoveTaskToBack`（閉じずに背面へ）で目覚ましアプリの用は足りるが、終える API もここで一緒に決める。
  → メインプロセスの `local/` に 7 命令と共通の形 `WindowToggleCommand`、`SystemBarsController` の実行中の切り替え（安全領域は既存の WindowInsets の経路で追従する
  ことをコードで確かめた）、`App.OpenUrl`（URL の規則＋`startActivity`＋`ActivityNotFoundException` で `no_handler`。`resolveActivity`・`<queries>` は使わない）、
  `Haptics`（`DeviceVibrator` を `RingVibration` と共通化。`VIBRATE` を main に常設し機能 `alarm` から外した）、ディープリンク（`deep_link`・`uri`）、開き直しの忘れ対策、
  デスクトップの模擬（`OpenUrl` は http / https / mailto だけを `ShellExecuteW` で開く。`SEED_PLATFORM_SIM_NO_OPEN=1` で開かない。単体起動の `--deep-link`）、C# の
  `Window`・`App`・`Haptics`・`LaunchKind.DeepLink`・`LaunchInfo.Uri`。終える API は入れないと決めた（Android 節の項目に理由）。
- [ ] **W1-6 の実機の確認の残り** — 2026-09-27（W1-6）。同日に Pixel 6a（Android 16）で大半を確かめた（docs/android.md §25.15.10: バーの出し入れ・`KEEP_SCREEN_ON`・
  `OpenUrl` の断る URL と `no_handler`・触感が `usage TOUCH / MEDIA` で届くこと・ディープリンクの起動中と冷えた起動・ディープリンクで呼んだ `MoveTaskToBack` の後の
  タスク・開き直しの忘れ対策）。残り: (1) 安全領域の値が変わる端末（切り欠きがステータスバーより低い・切り欠きが無い）でバーの出し入れに追従するか
  （Pixel 6a は切り欠きとステータスバーが同じ 132 px で値が変わらない）、(2) 触感が設定で切られていない端末で実際に振動するか（この端末は `ignored_for_settings`）、
  (3) https の URL がブラウザで開いて戻れるか・`OpenAppSettings` の画面（アプリの外へ出るので確かめ用のスクリプトでは実機で呼んでいない）、(4) 冷えた起動の
  ディープリンクをスクリプトの `App.LaunchReason` で読むこと（開始シーンが Main なので Java のログだけを見た）。端末に入っているのは `deep_links` を足した版の APK。
- [ ] **開き直しで showWhenLocked を下ろすのが `launcher` だけ** — 2026-09-27（W1-6）。指示の範囲に合わせ、`deep_link`・`notification_tap`・`other` の `onNewIntent`
  では下ろさない。どれも利用者がロックを解除して操作した結果なので下ろしてよい見込み（`notification_action` の「開く」は鳴動の通知からなので除く）。広げるなら
  `LaunchReason.applyWindowFlags` の条件だけ。関連: `runtime/android/app/src/main/java/com/seedengine/runtime/platform/LaunchReason.java`。
- [ ] **`App.OpenUrl` に `CATEGORY_BROWSABLE` を付けるか** — 2026-09-27（W1-6）。今は付けない（公式の「URL を開く」の例は付けるが、`tel` などで BROWSABLE を宣言しない
  受け手があると `no_handler` になるため）。URL がサーバの文面などアプリの外から来る場合、BROWSABLE を付けると「ブラウザから開かれてよい」と宣言した部品だけが
  対象になる（安全側）。付けるなら端末の電話・メールのアプリで `tel` / `mailto` が開けるかを実機で確かめてから。関連: `platform/app/UrlLauncher.java`。
- [ ] **`Haptics` の Android 12L 以前（API 29〜32）は振動の種類が付かない** — 2026-09-27（W1-6）。`VibrationAttributes.createForUsage` と `vibrate(effect, attributes)` が
  API 33 からなので、それより前は `vibrate(effect)`（種類なし）。利用者の「タッチの触感」の設定が効くかは未確認（Pixel 6a は Android 16 なので確かめられない。
  エミュレータ API 29〜32 で見る）。関連: `platform/haptics/HapticFeedback.java`。
- [x] **W1-S 保存の耐久性** — 2026-09-27 記載 / 同日対応。上の「SaveData の書き出しに…隙間がある」「SetString が値をスタックに…」「UI スレッドからの自動書き出し…」を
  直した（docs/app_platform_roadmap.md §2.7・§2.10）。実機の `kill -9` の繰り返し（AC-10）は W1-7 で行う。W1-S で見つけた、今はやらないことは下の 5 件。
- [ ] **`SaveData.Batch` に取り消し（ロールバック）が無い** — 2026-09-27（W1-S）。`action` が途中で例外を投げると、それまでに書き換えたキーは
  メモリに残り、次の書き出しでディスクへ届く（「お金は減ったが履歴は無い」がありうる）。Batch の間に書き換えたキーの元の値を覚える取り消しの記録
  （最初の書き換えのときだけ元の値を控える）を足せば、例外のときに元へ戻せる。今は「Batch は小さく」の方針と、Wake or Pay が「1 文書を 1 キー」で作る前提で入れていない。
  関連: `runtime/src/engine/core/save/batch.rs`・`store.rs`、`scripting/src/Api/SaveData.cs::Batch`。
- [ ] **Batch の途中に onDestroy の書き出しが来ると、Batch より前の未書き出しの変更も失われる** — 2026-09-27（W1-S）。UI スレッドの `nativeFlushSaveData` は
  Batch の途中なら書かずに戻り、Java はすぐ `Process.killProcess` するので、Batch の終わりの書き出しは来ない（ディスクは前回の書き出しのまま＝半端にはならない）。
  改善案: (a) UI スレッドから来たときだけ Batch の終わりを短い上限つき（例 200 ms）で待つ（android_main がその Batch の中で UI スレッドを待つと詰まるので上限は必須）、
  (b) 上の取り消しの記録を使い、Batch の前の状態を書く。Batch はスクリプトのスレッドで同期に終わるので窓は短い（実測はしていない）。関連: `save/mod.rs::flush_if_dirty`、
  `runtime/android/native/src/jni_exports.rs`。
- [x] **（検討）書き出しの 2 と 3 の間で落ちたとき、完全な `.tmp` があっても 1 世代前から読む** — 2026-09-27（W1-S）記載 / 同日 W1-7 で隙間そのものを無くした。
  W1-7 の実機の `kill -9` × 100（AC-10）の 74 回目がちょうどこの隙間に当たり（`save.json` が無く `.bak` と完全な `.tmp`）、次の起動は `.bak` から
  （`RecoveredFrom = Backup`）だった。手順 2 を「本体の写しを `.bak.new` に作り `.bak` へ rename」に変え、本体を動かさないようにした
  （`durable_file.rs`。写しは PC では hard link、**Android では複製**: 最初に入れた hard link の方式は実機で SELinux に拒まれ〈`avc: denied { link }`・
  `Permission denied`〉、rename へ戻っていた〈2 回目の × 100 は隙間に当たらず 0 件だっただけ〉ので、Android は hard link を試さず複製にした。
  写しを作れないとき〈容量が足りない等〉だけ W1-S の rename に戻り、そのときだけ隙間が残る）。`.tmp` を読む案は採らない。以下は記載時のメモ。
  本体を .bak へ回した直後・
  .tmp を本体にする前に落ちると、sync 済みの完全な `.tmp`（新しい世代）が残るが、読み込みは `.bak`（前の世代）を使う（`RecoveredFrom = Backup`）。
  `Save()` が戻る前に落ちたので「保存は済んでいない」として一貫しているが、`.tmp` が JSON として読めるなら新しい世代を採る選択もある
  （そのときは `RecoveredFrom` に別の値が要る）。W1-S の仕様（本体 → .bak → 空）どおりにしてある。関連: `save/recovery.rs`・`durable_file.rs` の先頭の表。
- [ ] **大きなセーブほど `Save()` が重い（毎回ファイル全体を書き直し、sync で待つ）** — 2026-09-27（W1-S）。PC のデバッグビルドで 2.4 MB の save.json の
  `Save()` が約 0.1 秒（SaveSmoke の実測。組み立て・書き込み・`FlushFileBuffers` を含む）。スクリプトのスレッドで同期に走るので、そのフレームが止まる。
  端末（フラッシュへの fsync）では未計測。数 MB の文書を頻繁に保存するなら、キーを分けて小さく保つか、書き出しを別スレッドへ出す仕組みが要る。関連: `save/store.rs::write_now`。
  → W1-7（2026-09-27）の実機: 2.4 MB の `Save()` は約 230 ms（W1-S の rename の方式）→ 約 290 ms（W1-7 の複製の方式。前の世代をもう 1 回書いて sync する）。
  前の世代の中身はストアが直前に書いたものなので、メモリに持っておけば読み直しは省ける（書き直しと sync は残る）。
- [ ] **`safe_write::write_atomic`（シーン・アクター・地形の書き込み）に、W1-S の前の SaveData と同じ問題がある** — 2026-09-27（W1-S で見つけた。範囲外なので直していない）。
  (1) 「Windows の rename は置換先が存在すると失敗する」という古いコメント（Rust の `rename` は置き換える）、(2) rename に失敗したら置換先を消してから rename し直し、
  それも失敗すると一時ファイルまで消す（元のファイルと新しい内容の両方を失いうる。`write_atomic_with_backup` の呼び出しは `.backup` の複製が残るが、
  `write_atomic` だけの呼び出し〈地形の被覆・散布・エディタの表示状態〉には残らない）、(3) sync が無い。`save/durable_file.rs` の順序に揃えるのが素直。
  関連: `runtime/src/engine/core/app_base/safe_write.rs:59-80`。
- [x] **W1-7 通しの確認（AC-1〜14）** — 2026-09-27 記載 / 同日実施（結果の表は roadmap §2.8・§2.9.2。直したものは docs/android.md §25.12.11）。
  再起動の試験（T4）・鳴動の復元（見張りの予約）・音量の控え・継ぎ目の無いループ・起動の放送の重なり・デバッグ受信機の保護・SaveData の hard link の書き出し（AC-10）を
  行い、実機のスクリプトを `runtime/android/tools/platform_device_tests/` に整えた。確かめ用のシーンと Java の JVM 単体テストは作らなかった（下の項目）。
  見つけたこと・残したことは下の「W1-7 で見つけたこと」。以下は記載時のメモ。確かめ用のシーン（例 `templates/scenes/platform_probe.scene`）、Java の JVM 単体テスト、docs。
- [ ] **W1-7 で見つけたこと: Android 17 の AudioHardening が背面のプロセスからの音量の変更を無視する** — 2026-09-27（W1-7 の T5 の強制停止。端末は T4 の再起動で Android 17 に更新されていた）。
  `:seed_platform` が受信機だけで起きた（前景サービスも見える画面も無い）とき、`AudioManager.setStreamVolume` が無視された（logcat
  `AS.HardeningEnforcer: AudioHardening volume control for api 100 ignored for com.wakeorpay.seed (10424), level: partial`。鳴動中〈前景サービスあり〉は
  `would be ignored … level: full` の注意だけで変わった）。そのため鳴動中の強制停止・再起動で `force_volume` で下げた音量は、**アプリを開く
  （`LeftoverVolumeNudge`）か次の目覚ましが止まるまで下がったまま**（その間、他のアプリの目覚ましも小さい）。W1-7 では控えを残して戻す機会を増やしただけ。
  案: (a) 見張りの予約と同じく正確な予約の配信（前景サービスの一時許可）で短い前景サービスを起こして戻す（通知が一瞬出る。種類と Play の申告の確認が要る）、
  (b) アプリの既定を `force_volume` なし（利用者の音量のまま鳴らす）にし、使うアプリには注意を書く、(c) Android 17 の背面の音の制限（X-7）と合わせて決める。
  試験の道具も同じ制限を受け、`cmd media_session volume --stream 4 --set` は効かなかった（`cmd audio set-volume` は効いた）。関連: `ring/AlarmStreamVolume.java`・`RingRecovery.java`。
- [ ] **W1-7 で見つけたこと: 見張りが発火するまで（最長 20 秒）鳴動が無音になる** — 2026-09-27（W1-7）。`RingWatchdog.DELAY_MS` = 20 秒・張り直し 5 秒ごと。
  実機の `kill -9` で無音は 20.1 s / 15.4 s。短くすると張り直しが増える（AlarmManager の 5 秒の最小の先の時間より長くする）。目覚ましの用途で許せるかを
  アプリの要件と合わせて決める。関連: `ring/RingWatchdog.java`。
- [ ] **W1-7 で見つけたこと: 再起動をまたいだ鳴動は鳴らし直さない** — 2026-09-27（W1-7）。鳴動中に端末が再起動すると、起動の放送（`BootReceiver`）で
  前の鳴動を `ring_stopped(error)` として終わらせ、音量を戻す（戻す試みは背面なので AudioHardening に無視されうる。上の項目）。起動の放送からは mediaPlayback の
  前景サービスを起こせない（E-10）ので、続けるなら起動の放送で見張りの予約を数秒先に張り、その配信から鳴らし直す案（実機で許可されるかは未確認）。
  Wake or Pay では「再起動で目覚ましを逃れる」使い方に関わるので、アプリの仕様と合わせて決める（アプリは `ring_stopped(error)` で知れる）。
- [ ] **W1-7 で見つけたこと: 再起動の直後に端末の時計が約 75 秒進んでいた** — 2026-09-27（W1-7 の T4）。Pixel 6a の再起動の直後の時計（RTC）が
  ネットワークの時刻より約 75 秒進んでいて、約 1.5 分後に `TIME_SET` で戻った（logcat の epoch が戻り、`alarms.rescheduled(time_changed)` が 2 件＝時計が
  戻る前と後）。予約は UTC の絶対時刻なので、この間に来る予約は最大 75 秒早く鳴りうる（T4 の予約は後だったので影響なし）。端末の性質で直せない。記録だけ。
- [ ] **W1-7 で見つけたこと: 見張りの復元で実機で試していない道** — 2026-09-27（W1-7）。待ち行列ごとの復元・戻した回数の上限（5 回）・殺されている間に
  安全弁の時刻を過ぎた鳴動（`timeout` と繰り上げ）・前景サービスを起こせなかったとき・新しい目覚ましが先に鳴って前のプロセスの鳴動を置き換えたとき。
  コードを読んで確かめただけ。Java の JVM 単体テスト（`RingRegistry`・`RingStateStore`・`RingRecovery` の規則）を足すと安く確かめられる（W1-4a からの持ち越しと合わせて）。
- [ ] **W1-7 で見つけたこと: AC-9 の「デバッグ命令で panic」が無い** — 2026-09-27（W1-7）。エンジンをわざと落とす命令が無いので、T8 はメインプロセスを
  `run-as … kill -9` した（panic はプロセスの abort になるので、どちらも「エンジンのプロセスが無くなる」）。panic の経路（Rust の panic → abort の後の
  後始末・ログ）を確かめたいなら、デバッグ版だけの命令を足す。
- [ ] **W1-7 で見つけたこと: 戻せない音量を戻す試みのログが重なる** — 2026-09-27（W1-7）。背面で起きた `:seed_platform` は、起動の放送 4 回と
  `PlatformProvider.call` のたびに戻す試みをし、AudioHardening に無視されるたびに警告を 1 行ずつ出した（強制停止の試験で 8 行）。害はログだけ。
- [ ] **W1-7 で見つけたこと: OS・アプリの更新の直後の最初の鳴動は、画面が 3 秒に間に合わない（パイプラインキャッシュ）** — 2026-09-27（W1-7 の T1）。
  T4 の再起動で端末が Android 17 に更新され、描画のパイプラインキャッシュ（656 KiB）が捨てられた（`[SEED PIPELINE CACHE] 読込 656 KiB → 採用後 0 KiB`）。
  その後の最初の冷えた起動は DrawContext 3249 ms・最初のフレーム +4608 ms（フルスクリーン通知から 4.30 s。AC-12 の 3 s を超える。音は +514 ms で間に合う）。
  キャッシュを作り直して保存させると 504 ms・+1124 ms に戻った。キャッシュはエンジンが背面へ回るとき（`suspended`）にしか保存されないので、更新の後に
  アプリを開かないまま目覚ましが鳴ると、その 1 回は画面が遅い（kill されて終わる起動が続くと、いつまでも保存されない）。案: 最初のフレームの後にも 1 回保存する・
  鳴動画面のシーンのパイプラインを先に作る・画面が出るまでの窓の背景（G-2）。X-1 と合わせて決める。関連: `runtime/src/engine/core/app_base/background_lifecycle.rs`。
- [ ] **W1-7 で見つけたこと: 鳴らす前の書き込みで音の開始が少し遅れた見込み** — 2026-09-27（W1-7 の T1）。直した後の冷えた起動の音（AudioFlinger の
  トラックの開始）は +514 ms（1 回目。配信 +221 ms）/ +340 ms（2 回目）で、W1-4b の中央値 +368 ms と比べて 1 回目が遅い。配信そのものの遅れのほか、
  鳴らす前に `ringing.json` を書く fsync（受信機の `announce` と、音声のスレッドの `force_volume` の前の音量の控え）が音の準備の前に入った。2 秒の基準には
  十分間に合う。詰めるなら、音量の控えの書き込みと MediaPlayer の準備を並べる。計測は 2 回だけ（端末が Android 17 に変わった後）。
- [ ] **W1-7 で見つけたこと: 再起動で保留中の OS の更新が当たることがある（試験の条件）** — 2026-09-27（W1-7 の T4）。`adb reboot` の起動が長く
  （画面まで約 82 s）、Pixel 6a が Android 16（`CP1A.260405.005`）から **Android 17（SDK 37・`CP2A.260705.006`）** に変わっていた（エンジンのログ
  「端末: Android=17」・`getprop ro.build.version.release`）。W1-7 の T4 の張り直し以降の結果はすべて Android 17 のもの。W1-4b までの数値（Android 16）と
  並べるときは注意。再起動を伴う試験の前に、保留中の更新が無いかを利用者に確かめてもらうとよい。
- [ ] **W1-7 で見つけたこと: Direct Boot の間は adb が使えない（試験の道具）** — 2026-09-27（W1-7 の T4）。再起動の後、最初のロック解除まで adb が
  `unauthorized` のままだった。Direct Boot の間の振る舞いは、解除した後に端末保護ストレージの記録（`journal.json`）と logcat のバッファ（main は約 5 分で
  消える。events・system は残っていた）から確かめた。W1-9 で解除前の鳴動画面を確かめるときも同じ（解除前の出来事を端末保護ストレージへ書き残す仕組みがあると楽）。
- [x] **W1-7 の手作業の確認（通知のボタン・本文のタップ・電源ボタン・通知の設定画面）** — 2026-09-28 実施（利用者の手を借りた。roadmap §2.9.2 の
  「W1-7 の手作業の確認」M1〜M8）。AC-7（鳴動の通知の「開く」・本文を、生きているとき・メインプロセスが死んでいるときの両方）・AC-2 の後半（タスクを消した後に
  通知からアプリへ戻る）・AC-5（普通の起動で電源ボタン → ロック画面）を満たし、ロック中のヘッドアップ通知のタップが指紋の解除を求めることを記録した。
  残り: W1-5 のアプリの通知（`Notifications.Show` のボタン・payload）のタップ、実行時の確認の画面・`appops` の拒否（AC-8 の残り）、戻る／ホーム／スワイプで止まらないこと（AC-3）。
- [x] **W1-7 で見つけたこと: 通知をオフにするとアプリが止められ、`PermissionChanged` が届かない** — 2026-09-28（W1-7 の手作業の確認 M7）。
  → **2026-09-28 に直した**（W2-10a と同じ回）: 前回の状態をメインプロセスの SharedPreferences（`seed_platform_permissions` の `last_status.<種類>`・
  `PermissionStatusMemory`。commit で同期に書く）に保存し、起動し直した最初の onResume でも比べて `granted → denied` を流す。比べる部分を純粋な Java の
  `PermissionChangeTracker` に分け、JVM の検査（`runtime/android/tools/jvm_checks/run_jvm_checks.sh`）で 14 項目。docs は android.md §25.14.5・§25.14.7 と
  scripting_api.md の `PermissionChangedEvent`。**直した後の実機の確認は未実施**（利用者が通知のスイッチを切り替える手作業が要る。手順は android.md §25.14.7）。
  以下は記載時のメモ。
  端末の通知の設定で「すべての通知」をオフにすると `POST_NOTIFICATIONS` が取り消され、Android がアプリの両プロセスを止めた（`Killing … PermissionHelper`。
  鳴動中なら `:seed_platform` ごと止まる＝見張りの予約は残るので戻せる見込み。試していない）。戻ると新しいプロセスで起動し直し、`PermissionMonitor` の前回の状態は
  メモリにしかないので比べる相手が無く、`platform.permission_changed`（granted → denied）は届かない。オンに戻したとき（denied → granted）はプロセスが止められず届いた。
  アプリは起動時の `Permissions.Check` で拒否を知れるので実害は小さいが、scripting_api.md の `PermissionChanged` の説明に「取り消しは起動し直しで知る」と書くか、
  前回の状態を SharedPreferences に残して起動し直しでも比べるかを決める。関連: `platform/permission/PermissionMonitor.java`。
- [x] **（任意）W1-8 センサー（重力を除いた加速度）** — 2026-09-27 記載 / 同日対応（実機の確認を除く）。Wake or Pay の起床確認「振る」を v1 に残すと決まった（アプリ仕様 §10 U-04）ので
  `SEED.Platform.Sensors` を入れた（docs/android.md §25.16・scripting_api.md §7.13・app_platform_roadmap.md §2.10）。W1-8 で見つけた、今はやらないことは下の 7 件。
- [ ] **W1-8 の実機の確認** — 2026-09-27（W1-8）。→ その後の実機の回で静置（1 秒に 49〜56 個・最大 0.0 m/s²）と振る（振り始め ≥ 2 m/s² を最大 180 秒待って
  30 秒計測する方式。docs/android.md §25.16.7）を行い、**激しく振っている間は標本が 1 秒に約 19〜26 個に落ちた**（作業の記録の数値）。W1-7 の切り分け:
  SEED の Java 層（`FeedListener` → `SampleAccumulator`）は受けた標本を全部数え、頻度に関わる処理は無い（捨てるのは有限でない値と古い登録の世代だけ）。
  既定の重力を除いた加速度はセンサーハブの `Linear Acceleration Sensor | Google`（handle 0x01010009・連続）なので、端末側（ハブの融合の出力）の見込み（推論）。
  確かめるには振りながら `LSM6DSR Accelerometer`（加速度）と同時に登録して 1 秒の数を比べる（手作業）。落ちても閾値 12 m/s² の判定には足りる数なら直さない。
  鳴動画面（ロック画面の上）の前面で受け取れることと、加速度だけの端末は未確認のまま。以下は記載時のメモ。
  利用者の外出中で端末がロック・Dozing のままだったため、起動したエンジンが最初のフレームの前に suspended になり、
  スクリプト（SensorSmoke）が走らなかった（`dumpsys sensorservice` に登録なし。docs/android.md §25.16.8）。画面が点いてロックが解除された状態で §25.16.7 の
  1〜3（静置: 最大 < 0.5 m/s²・2 秒で約 100 個・時刻の古さ 0〜1000 ms・登録の記録 `FeedListener`／振る: `scenes/ShakeSmoke.scene` で 10 秒振って最大 ≥ 12 m/s²）を行う。
  あわせて、ロック画面の上に出した鳴動画面の前面で受け取れること（W1-7 の通しの確認で）と、加速度だけの端末（`accelerometer_lowpass`）での振る舞い。
- [ ] **メインプロセスだけのモジュールが `sensor` だけ** — 2026-09-27（W1-8）。`local/MainProcessCommands` の `LOCAL_MODULES` に入れたモジュールは、表に無い命令を
  `:seed_platform` へ送らずに `unknown_method` で答える。W1-6 までの `window`・`app`・`haptics`・`permission` は入れていない（振る舞いを変えないため）ので、
  表に無い命令は `:seed_platform` を起こし、最初は `connecting`、つながると `unknown_method`。揃えるなら 4 つを足すだけ（呼び出し側の振る舞いの変化として docs に書く）。
- [ ] **センサー: 種類が `linear_acceleration` だけ・精度とまとめ配信を使っていない** — 2026-09-27（W1-8）。`SensorKind`／`SensorSource` の表に足せば増やせる
  （ジャイロ・重力・回転ベクトル等）。`onAccuracyChanged` の精度はスクリプトへ出していない。`registerListener` の `maxReportLatencyUs`（まとめ配信で電池を減らす）も使っていない。
- [ ] **センサー: `accelerometer_lowpass` の向きの変化の過渡・時定数が固定** — 2026-09-27（W1-8）。加速度だけの端末では重力を時定数 0.25 秒の低域通過で見積もるので、
  向きを素早く変えた直後に重力の差が一瞬だけ加速度に見える（式の計算で 90 度を一瞬で回すと約 12.8 m/s² が 1 標本）。時定数をアプリのデータにするか、
  重力のセンサー（`TYPE_GRAVITY`）がある端末ではそれを引くかは、実機の加速度だけの端末で確かめてから決める。関連: `platform/sensor/GravityFilter.java`。
- [ ] **センサー: デスクトップの模擬は頻度に合わせた標本を作らない** — 2026-09-27（W1-8）。PC では `Sensors.SimulateSample` しない限り `SampleCount` が 0 のまま
  （Android の静置では約 50 Hz で 0 に近い値が来る）。`SampleCount > 0` を「センサーが生きている」の目安にするアプリは PC と実機で振る舞いが変わる。
  模擬の壁時計で rate_hz ぶんの 0 の標本を数える案。関連: `desktop_sim/sensor_state.rs`。
- [ ] **センサー: スクリプトの差し替え（`RELOAD_SCRIPTS`）でも止まらない** — 2026-09-27（W1-8）。Android では `Sensors.Stop` を呼ばない限り、前面にいる間は登録したまま
  （背面では自動で外れる）。実行中の差し替えで古いスクリプトが Stop せずに消えると、新しいスクリプトが Start し直すまで（か背面へ回るまで）動き続ける。
  差し替えのときに `sensor.*` を全部止める口（エンジンの Play の区切りに当たるもの）を Android にも用意する案。
- [ ] **`Sensors.Read` は毎フレーム JSON を作って読む** — 2026-09-27（W1-8）。W1 の他の API と同じ `platform_invoke` の経路（引数の JSON を作る → JNI → 返答の JSON を
  2 回読む）で、フレームごとに小さな割り当てがある。計測で重ければ、読むだけの専用の FFI（数を並べた構造体を返す）にする。
- [ ] **（任意）W1-9 Direct Boot（再起動後・ロック解除前の鳴動）** — 2026-09-27。夜中の自動更新の再起動の後でも鳴らすため。
  → W1-7 の T4（2026-09-27）で、**解除前に張り直して音は鳴る**（受信機・鳴動のサービス・控えは directBootAware・端末保護ストレージ。同梱の音）ことを確かめた。
  残るのは解除前の鳴動画面: フルスクリーン通知からの `PlatformEntry`（MainActivity。directBootAware でない）の起動は `result code=-92` で失敗した
  （画面は出ず、通知と音だけ）。アプリのスクリプトは解除まで動かないので、止めるのは安全弁か解除の後。以下は記載時のメモ。
  予約の控えと既定の音を端末保護ストレージに置き、受信機と鳴動サービスを `directBootAware` にする案。W1-0 で、exported=false・directBootAware の
  受信機に `LOCKED_BOOT_COMPLETED` が届き端末保護ストレージから張り直せることは確認（強制停止からの復帰で観測）。再起動での確認とロック解除前の
  鳴動画面は未確認。解除前は Java だけの鳴動画面か、音と通知だけにする（E-10 の決定）。

### W2: UI 部品群

- [x] **W2-0 スパイク: Android の文字入力（GameActivity の IME を winit の外から使えるか）・クリップの描画・描かないときのイベントループ** — 2026-09-27。
  → 2026-09-27 に実施（結果と決定は roadmap §3.8）。E-06 は (a)「操作は android-activity の API、知らせは MainActivity の上書き」に決定。
  切り抜きは scissor（矩形）＋SDF（角丸・円）、描かないは「次のフレームの要求を 1 か所で決めて Wait / WaitUntil」。試作は既定で無効の
  起動の指定 `ui_spike`（PC `--ui-spike=`・環境変数 `SEED_UI_SPIKE`、Android `--es seed.ui_spike`）で残した。以下はその持ち越し。
- [x] **W2-0 の実機（Pixel 6a）の確認が未実施** — 2026-09-27。adb が `unauthorized`（USB デバッグの許可待ち）のまま 1 時間以上戻らなかった。
  → 2026-09-28 に W2-0 の APK の控えで確かめた（roadmap §3.8.7）。E-06 は (a) で確定（Simeji のかな入力・「漢字」への変換・完了・数字のキーボード・
  IME の高さ 979 px・戻る）。描かないは 0 fps・CPU 82.9% → 6.0%・タップで同じ周回に再開（0.07〜4.78 ms）。残る未確認は Gboard・複数行・release の .so。
- [ ] **一度も本文が入っていないときに `text_input_state()` を読むと落ちる**（android-activity 0.6.1 が null の本文を `slice::from_raw_parts` に渡す。
  debug は panic → abort、release は未定義動作）— 2026-09-28（W2-0 の実機で `ime` の試作の最初の起動が落ちた。roadmap §3.8.1 の I-12）。
  本番はネイティブから読まない。試作の `ime` を使うときは起動の前に `clear` を積む（§3.8.6）。android-activity を上げるときに直っているかを見る。
- [ ] **W2-0 の試作のコードを本番に置き換えたら消す** — 2026-09-27。`runtime/src/engine/core/ui_spike/`・`app/ui_spike_hooks.rs`・
  `runtime/android/native/src/ui_spike/`・`app/.../spike/ImeSpikeLog.java`（MainActivity の 4 つの上書きの中の呼び出し）・`LaunchArgs.ui_spike`・
  起動オプション `ui_spike`。`renderer/ui_clip.rs` と `ui_draw_pass.rs` の切り抜き（ランの分割・scissor）は W2-1 で本番の形にする前提で残す。
  → 2026-09-27（W2-1a）: **切り抜きの分は済**（名前で根を指定する `UiClipCollector`・`ui_spike` の `clip=`・計測のログ `log_clip_runs` を消し、
  `ui_clip.rs`・`ui_draw_pass.rs` を本番の形にした）。残りは描かないとき（`idle=`・`wake_ms=`。W2-10a で本番化）と文字入力（`ime`。
  roadmap §3.8.6 の実機確認〈W2-6a の頭〉で使うので残した）。
  → 2026-09-28（W2-10a）: **描かないときの分も済**（`ui_spike/idle_redraw.rs`・`ui_spike_hooks.rs` の描かない部分・`idle=`・`wake_ms=` を消し、
  本番の `engine/core/redraw`・`app/redraw_hooks.rs` に置き換えた。`idle=` などは知らない項目として警告になる）。残りは文字入力（`ime`）だけ。
- [ ] **winit 0.30 は Android の文字入力のイベント（`TextEvent`・`TextAction`）を読み捨てる** — 2026-09-27（W2-0 で読んだ）。android-activity は
  glue のフラグから 1 度だけ取り出す作りなので、winit が先に取ると SEED は受け取れない（特に完了などのアクション）。W2-6 は MainActivity の
  `stateChanged`・`onEditorAction` の上書きで受け取る（roadmap §3.8.1 の I-1・I-5）。winit を上げるときに扱いを見直す。
- [ ] **GameTextInput の選択・変換中の区間の添字は UTF-16 の単位**（android-activity の `TextSpan` の説明と `text_input_state()` の丸めはバイト数が前提）
  — 2026-09-27（W2-0 で読んだ）。W2-6 の受け口で UTF-8 の境界へ変換する。`set_text_input_state` へ渡す添字も UTF-16（§3.8.1 の I-6）。
- [ ] **UI スレッド以外で `text_input_state()` を読むと途中の本文を読みうる**（GameTextInput の本文のバッファはロックの外で上書きされる）
  — 2026-09-27（W2-0 で読んだ）。本番は Java の上書きで状態を受け取り、ネイティブからは読まない（§3.8.1 の I-7）。
- [x] **切り抜きの試作の制限** — 2026-09-27（W2-0）。
  → 2026-09-27 に W2-1a で解消: 根は `CanvasClipComponent`、2D パーティクル・`SEED.Draw` の図形（座標空間の表に番号）も切り、
  メインパスはビューポート（Play のゲーム領域）の内側へ交差させ、当たり判定（`pick_2d`）も同じ領域で切る。残る制限
  （回転したノードの AABB・3D ワールドキャンバス・GPU の ID 描画）は下の「W2-1a の切り抜きの残りの制限」。以下は当時の記述。2D パーティクル・スクリプトの図形（`SEED.Draw`）・メインパスで描くもの（背景ゾーン・エディタのビュー。
  ビューポートの内側に交差させる必要がある）・3D ワールドキャンバスは切り抜かない。回転したノードは 4 隅の AABB になる。当たり判定（`pick_2d`）は
  切り抜きを見ない。根は名前の指定。W2-1 でコンポーネントにし、描画と当たり判定で同じ領域の表を使う（§3.8.2 の C-5・§3.8.4）。
- [x] **描かない試作の制限** — 2026-09-27（W2-0）。起こす理由が WindowEvent だけで、JNI で届くプラットフォームのイベント・文字入力・IPC・スクリプトの
  要求では起きない（次の入力か `wake_ms` まで遅れる）。止まっていた時間がそのまま次のフレームの dt に入る。エディタに埋め込んだ実行と Edit では無効。
  W2-10 で起こす理由を足し、dt を切り詰める（§3.8.3・§3.8.4）。
  → **2026-09-28 の W2-10a で解消**（docs/redraw_policy.md）: IPC・JNI（プラットフォームのイベント・文字入力・画面・音声フォーカス）・スクリプト（どのスレッドからでも）が
  `EventLoopProxy` で起こし、起きた最初のフレームの dt を 1/60 秒で切り詰める。エディタに埋め込んだ Play でも使う（Edit は毎フレーム描く）。
- [x] **IPC の命令はフレームの中でしか処理されない** — 2026-09-27（W2-0 の PC の試作で確認）。描画を止めている間は `SCREENSHOT`・`STOP` などが
  次のフレームまで遅れた（`wake_ms=1000` で最大 1 秒）。`about_to_wait` の `pump_ipc_while_frames_stalled` は、`Wait` で眠っている間は呼ばれない。
  W2-10 で IPC の読み取りのスレッドがイベントループを起こす（EventLoopProxy）。
  → **2026-09-28 の W2-10a で解消**: `read_loop` が命令を積むたびに `redraw::wake::raise(Ipc)`。止めている間の `SCRIPT_DEBUG` の往復 0.0〜0.6 ms・
  `SCREENSHOT` 69〜80 ms（毎フレーム描いているときの 70〜78 ms と同じ）を PC で確かめた（redraw_policy.md §8）。
- [ ] **Windows の IME はエディタに埋め込んだ Play（WPF の子ウィンドウ）での候補窓の位置・WPF の IME との取り合いが未確認** — 2026-09-27（W2-0）。
  今の SEED は `set_ime_allowed` を呼ばない（winit の既定で IME は切り離されたまま）ので、Play の窓では日本語を入力できない。単体の SEED.exe での
  許可と候補窓の位置の指定は試作の `ime` で通したが、日本語の実入力は W2-6 で確かめる（§3.8.1 の I-11）。
- [x] **W2-1a レイアウト計算の一本化と切り抜きの本番化** — 2026-09-27 に完了（roadmap §3.8.5 の W2-1 行・docs/canvas_camera_rework.md §6）。
  `canvas_layout`（純関数と 1 回の走査・表）、`CanvasClipComponent`（インスペクタ・スクリプト `SEED.CanvasClip`）、描画の scissor
  （スプライト・テキスト・2D パーティクル・Draw の図形・メインパスのビューポートとの交差）と当たり判定の切り抜き。
  WarashibeFishing の画面（図鑑は画素一致、会話の画面は UI の範囲で一致）とボタンの押せる範囲（56 点の探り）は不変。
- [x] **W2-1b 土台の残り（dp・レイアウト〈Stack・Wrap・Grid〉・安全領域の部品）** — 2026-09-27。W2-1a の表（`CanvasLayoutTable`）の上に作る。
  縦画面の `auto_scale` に頼らない。エディタの GPU の ID 描画の切り抜きもここで足す（下の「W2-1a の切り抜きの残りの制限」）。
  → **2026-09-28 に完了**（roadmap §3.8.5 の W2-1 行・docs/canvas_camera_rework.md §6.3〜6.5）。`CanvasStack`・`CanvasWrap`・`CanvasGrid`・
  `CanvasLayoutItem`（`canvas_layout/containers/`・`measure.rs`。測る → 並べるを 1 回の走査の中で。測る回数はノード数に比例）、
  ルートキャンバスの単位 dp（`CanvasComponent.unit`）、`CanvasSafeAreaComponent`、エディタの GPU の ID 描画の切り抜き、
  3D ワールドキャンバスの子の走査の表への寄せ、インスペクタ（`InspectorPanel.CanvasLayout.cs`）・IPC（`SET_CANVAS_LAYOUT_FIELD`・
  `SET_CANVAS_UNIT`）・スクリプト（`SEED.CanvasStack` など）・PC の検証用の模擬（`SEED_SIM_SAFE_AREA`・`SEED_SIM_SCALE_FACTOR`）。
  WarashibeFishing の画面（図鑑は画素一致）とボタンの押せる範囲（56 点の探り）は不変。残りは下の「W2-1b の残り」。
- [ ] **W2-1a で見つけた旧実装の食い違い（2D 物理・ギズモ・`ScreenPosition` と描画の間）** — 2026-09-27（W2-1a の同値の検査で読んだ）。
  一本化では**振る舞いを変えないことを優先して直していない**（`physics2d_ops.rs` の `PhysicsFrame` に閉じ込めた）。どれも旧
  `collect_actor2d_contexts` だけの計算で、描画・当たり判定と位置が食い違いうる:
  1. 子のアンカー基準にサイズ倍率を掛けている（描画は掛けない）。scale_size のキャンバスの子孫の anchor が、親の累積スケールが 1 でないとき
     （例: ルートの auto_scale でウィンドウ ≠ 設計解像度）描画とずれる。scale_transform=true の子ではスケールが二重に掛かる
  2. 子のワールド原点の pivot の基準の大きさがアスペクト比維持（keep_aspect_ratio）を見ない
  3. 回転を角度の和と sin/cos で積み上げる（描画は行列の積。浮動小数の丸めが違う）
  4. CanvasTransform を持たないノード（3D アクター等）の下もたどり、visible を見ない（描画・当たり判定はそこで打ち切る／見る）。
     CanvasComponent の引き方が「最初の Canvas スロット」（描画は「コンポーネントが引ける最初の Canvas スロット」。壊れたデータでだけ差が出る）
  影響するのはギズモの位置・ドラッグの書き戻し・2D 物理のボディ位置・スクリプトの `CanvasTransform.ScreenPosition`。WarashibeFishing は
  2D 物理と ScreenPosition を使っていない（2026-09-27 に確かめた）。直すなら表の `world_rs` と `anchor_offset` を読むだけにできる（1〜3 が消える）が、
  既存のプロジェクトの物理・ギズモの位置が変わるので、利用者の判断で別の作業にする。
- [ ] **自動スケールの割り算の扱いが読み手ごとに違う（大きさ 0 のルートキャンバス）** — 2026-09-27（W2-1a）。描画・枠・ID 描画はビューポート ÷ 大きさを
  そのまま割り（無限大）、当たり判定・2D 物理は分母を `f32::EPSILON` 以上にする（巨大な倍率）。大きさ 0 で auto_scale のルートキャンバスという退化した入力
  でだけ差が出る。W2-1a では `AutoScaleDivisor` で読み手ごとの旧実装の結果を保った。大きさ 0 をインスペクタで禁止するか、どちらかに揃える。
- [ ] **世界線が親と違う子の DFS 番号の数え方（旧実装の枠・ID 描画・当たり判定だけ違っていた）** — 2026-09-27（W2-1a）。旧実装の枠・ID 描画・当たり判定は
  世界線が違う子をサブツリーごと**数えずに**飛ばし、`find_actor_by_dfs`（子は世界線を問わず数える）と番号がずれていた（＝選択が別のアクターに付く）。
  W2-1a の表は `find_actor_by_dfs` と同じ数え方にそろえた（**ここだけ旧実装と違う**）。エディタの操作（`set_world_line_recursive`）と
  `.scene` の読み込みは子の世界線を親と同じにするので、この形のデータは普通は作られない（同値の性質テストもこの不変条件の木で行った）。
- [x] **3D ワールドキャンバスの子の走査が 2 か所に残っている** — 2026-09-27（W2-1a）。
  → **2026-09-28 に W2-1b で解消**: 2 か所とも表（`build_world_canvas_layout` / `CanvasLayoutPass`）を読む形へ寄せた。旧い走査の写し
  （`canvas_layout_equivalence/legacy_world_canvas.rs`）とランダムな木 1,500 個で、CanvasTransform を持たないノードの下（描画と同じく出さなくなった）
  以外はビット単位で一致（`world_canvas_walks.rs`）。以下は当時の記述。`canvas_collect.rs` の `walk_3d_canvas_children_id`（GPU の ID 描画）と
  `collect_3d_canvas_child_outlines`（エディタの枠）は、描画（W2-1a の表を読む `collect_sprite_items`）と同じ計算を別に持つ。しかも
  CanvasTransform を持たないノードの下も素通しでたどる（描画はそこで打ち切る）ので、そういう木では描画と ID・枠が食い違う。表（`build_world_canvas_layout`）
  を読む形へ寄せる。
- [ ] **W2-1a の切り抜きの残りの制限** — 2026-09-27。（2026-09-28 の W2-1b で (3) の GPU の ID 描画の切り抜きは済: アイテムに切り抜きの番号を持たせ
  `draw_canvas_id_items` で scissor を張る。残りは (1)(2)(4)）。(1) 回転したノードは 4 隅の外接矩形で切る（正確に切るにはオーバーレイパスのステンシル。§3.8.4）。
  (2) 3D ワールドキャンバス（透視）の配下は切らない。(3) **エディタの GPU の ID 描画（3D ビューでのキャンバスの選択）は切り抜きを見ない**
  （切り抜かれて見えない子もクリックで選べる。エディタの 2D ビューの選択と Play のポインタイベントは CPU の `pick_2d` なので切り抜く）。
  ID 描画のアイテムに切り抜きの番号を持たせ、`draw_canvas_id_items` で scissor を張る（W2-1b）。(4) 角丸・円の切り抜きは W2-4（シェーダーの SDF）。
- [x] **W2-2 ジェスチャーアリーナ（タップ・長押し・ドラッグ・フリック・押下の取り消し・指ごとの捕捉・タッチの時刻）** — 2026-09-27。
  → **2026-09-28 に済**（roadmap §3.8.5・正典 docs/input_gestures.md）。`CanvasGestureComponent` を付けたノードだけが参加し、付けていないノードの
  `OnPointer*` は不変（WarashibeFishing の複製で図鑑のボタンの縁 56 点のクリックが変更の前後で一致）。残りは下の「W2-2 の残り」。
- [ ] **W2-2 の残り（ジェスチャー）** — 2026-09-28。(1) **実機（Pixel 6a）で指の確認が未実施**（タップ・スクロールの中のボタンの押下の待ち・
  フリックの速度・複数指・背面へ回したときの取り消し。単体テストと PC の注入だけ。W2-6a・W2-10a の頭でまとめて行う）。(2) **Android のタッチの時刻は
  受け取った時刻で代用**: winit 0.30.13 は MotionEvent の eventTime を `WindowEvent::Touch` に渡さず、履歴の標本（historical）も捨てる
  （`platform_impl/android/mod.rs` の `handle_input_event` を読んだ）。移動は vsync ごとにまとめて届くので、速度の推定は 1 フレームに 1 標本・受け取りの揺れを含む。
  直すなら winit を上げる（時刻が渡るようになったら）か、MainActivity の `dispatchTouchEvent` で eventTime を JNI で控えて突き合わせる。
  → **(2) は 2026-09-29 に済（W2 の手直し P1-1）**: `MainActivity.processMotionEvent`（GameActivity の glue へ渡す前）で MotionEvent の時刻と履歴を控えて JNI で
  エンジンの箱へ送り、winit の Touch と ID・段階・位置のビットで突き合わせて記録の時刻にする（履歴の標本もジェスチャーの記録へ。API 34 以上は ns。
  docs/input_gestures.md §5・§5.1）。
  (3) **閾値の表はプロジェクト設定の JSON（`"gestures"`）だけ**（エディタのプロジェクト設定の画面に欄が無い）。(4) **「動いている」の申告
  （`GestureArenaSet::activity`）は呼び出し元が無い**（W2-10a で「描く理由」へつなぐ → 2026-09-28 につないだ。済）。(5) **W2-3 への申し送り**: 行の再利用で押している行のノードが
  消えると PressCancel の配り先が無い（部品側で戻す）。スクロールの慣性中のタップで止める・慣性中の押下の見た目の扱いは部品側。
  → **2026-09-28 の W2-3 で手当て**: 一覧が行を使い回す前に `GameObject.CancelGestures`（行と子孫の押下・ドラッグを取り消し、PressCancel が次のフレームに届く）。
  慣性の途中のタップはエンジンが止め、その指を中の行へ渡さない（ui_scroll_list.md §4）。
  (6) ピンチ・ダブルタップ・3D ワールドキャンバスのノードは無い（→ ピンチは 2026-09-28 の W2-8 で足した。ダブルタップ・3D ワールドキャンバスは無いまま）。(7) 内部解像度固定（レターボックス）では dp を画面の DPI から求めるので、slop・最小の
  ヒット領域が内部解像度の画素で少しずれる。(8) **動いているエディタでのインスペクタの目視は未確認**（ビルドとエディタのテストは通る）。
  (9) 指が触れている間（と指のイベントが来たフレーム）は、ジェスチャーの当たり判定のためにレイアウトの表をもう 1 つ作る（ポインタイベントの表と
  同じ文脈だが、ポインタイベントは表を外へ出さないので使い回していない）。シーンに CanvasGesture が 1 つも無ければ作らない。重くなったら共有する。
  関連: `runtime/src/engine/core/input/gesture/`・`app/gesture_events.rs`・`app/gesture_scene.rs`・`editor/src/Panels/InspectorPanel.CanvasGesture.cs`。
- [x] **W2-3 スクロールと一覧（慣性・跳ね返り・入れ子・行の再利用・左スワイプの操作）** — 2026-09-27。
  → **2026-09-28 に済**（正典 docs/ui_scroll_list.md。roadmap §3.8.5）。スクロールの本体は Rust（`CanvasScrollComponent`＋実行中の状態 `CanvasScrollState`・
  `engine/core/canvas_scroll/`。慣性・跳ね返り・スナップは Flutter の式と定数、入れ子の受け渡し、触れて止める、レイアウトの走査で中身をずらす・切り抜きと一緒に
  見える範囲の外を飛ばす）、一覧とスワイプは C#（`SEED.UI.ListView`・`SwipeActions`）。W2-2 の申し送り（押している行が消えると PressCancel の届け先が無い）は
  `GameObject.CancelGestures` で手当てした。あわせて `GameObject.Visible` を同じフレームに `Instantiate` したアクターにも効くようにした（一覧の行を隠したまま作る）。
  WarashibeFishing の複製の 56 点のクリックと図鑑の画素は変更の前後で一致。残りは下の「W2-3 の残り」。
- [ ] **W2-3 の残り（スクロールと一覧）** — 2026-09-28。(1) **実機（Pixel 6a）で慣性の手触りが未確認**（単体テストと PC の注入だけ。手順は ui_scroll_list.md §11。
  既定の摩擦・減衰は Flutter の値のまま）。(2) **端の表示が無い**（Android 12 以降の伸び・それ以前の光。Clamp は黙って止まる）。(3) **スクロールバーが無い**（任意。
  部品で描ける）。(4) iOS の続けてのフリックの加速（Flutter `carriedMomentum`）・`ScrollDecelerationRate.fast`・ゴムの戻り（`rubberBandSpring`）は入れていない。
  (5) **窓の scale は 1 を前提**（窓の大きさを子の座標で測る。回転は指の移動を窓の軸へ直すが、見える範囲は外接矩形）。(6) **窓と中身の大きさは前のフレームの描画から**
  （1 フレーム遅れ。Play の最初の描画まで大きさ 0。ListView は行を置かない）。描画のレイアウトの表を作る所で World を借りられないため、表の行を写してから
  次のフレームで状態へ書く。(7) **ListView の新しく作った行は次のフレームから**（プレハブの構築がフレーム末尾のため。前後の余白 250 の分まで先に作って隠す）。
  (8) **ListView は持ち主のスクリプトの Update から `Update()` を呼ぶ普通のクラス**（SEEDScript の部品としては付けられない。付けたいなら SEEDScripting の型を
  スクリプトとして付ける経路が要る）。(9) ページのスナップは過減衰のばねで寄せるので、とても速いフリックは目標を少し越えて戻る（Flutter の PageView と同じ式）。
  (10) **エディタのインスペクタ（Canvas Scroll）の目視は未確認**（ビルドとエディタのテストは通る）。(11) スクロールの中の行の文字の大きさは測れないので、
  テキストだけの行は位置の 1 点として見える範囲を判定する（はみ出しは余白 250 で受ける）。
  関連: `runtime/src/engine/core/canvas_scroll/`・`canvas_layout/scroll_view.rs`・`app/scroll_events.rs`・`scripting/src/Api/UI/`・`editor/src/Panels/InspectorPanel.CanvasScroll.cs`。
- [ ] **2D の全ノードを毎フレームたどる既存の処理がノード数に比例して重い** — 2026-09-28（W2-3 の性能の確認で発見。直していない）。静的な 1,000 行の一覧
  （2,032 ノード）で、見える範囲の外を飛ばしても debug ビルドのフレームは約 50〜65 ms（ListView の 111 ノードでは 4 ms）。プロファイラの内訳の大きいもの:
  「物理/2D 同期」6.5〜12.4 ms（2D 物理の文脈の表をフレームごとに作る）・「UI/2D スクリーン座標収集」6.0〜11.8 ms（`publish_screen_positions` が毎フレーム
  全アクターをたどる）・「UI/ポインタイベント」5.9〜11.4 ms（ポインタのイベントの表）。どれも描画の表とは別に 2D の木を 1 回ずつたどっている。
  直すなら (a) 2D 物理のボディやスクリーン座標を読むスクリプトが無ければ作らない、(b) 描画の表と文脈が同じなら使い回す、(c) 変わっていない部分木の
  結果を覚える。行の多い一覧は ListView で作れば避けられる。関連: `app/physics2d_ops.rs`・`host_api::publish_screen_positions`・`app/pointer_events.rs`。
  → 2026-09-28 午後の実機（Pixel 6a。roadmap §3.9 の原因 3）: W2 の見本のスクロール中、「物理/2D 同期」「UI/2D スクリーン座標収集」「UI/ポインタイベント」
  「UI/ジェスチャー」「描画/スプライト収集・ソート」「エディタ状態収集」の合計が dev の .so でギャラリー 13.2 ms（フレームの 35%）・ナビゲーション 6.8 ms（51%）、
  release の .so で 2.1 ms（25%）・1.9 ms（26%）。見本には 2D 物理のボディが無いのに 2D 同期が毎フレーム全 2D アクタの文脈を作り（`update_physics_2d` →
  `collect_actor2d_contexts`）、Android の Play でも「エディタ状態収集」（release 0.1〜0.24 ms）が走る。
  → **2026-09-28 夕に一部を済**（roadmap §3.9.1。`app/frame_scan_gates.rs`）: Play で 2D のコライダー（Collider2dComponent）が 1 つも無ければ 2D 物理の同期で文脈の表を
  作らない（ボディは Collider2d からしか作られないので空の表と同じ結果）、raycast_target の Sprite / SkinnedSprite が 1 つも無ければポインタイベントの木の走査を
  省く（候補は必ず空）、「エディタ状態収集」の MC の走査 2 回（ピックの情報と影を落とすモデル）を 1 回にした（内訳を見るため「…/MC の走査」「…/テキスト展開」を分けて測る）。
  **残り**: 「UI/2D スクリーン座標収集」（`CanvasTransform.ScreenPosition` 用。読むスクリプトが無くても毎フレーム全アクタのレイアウトの表を作る。
  最初に読んだフレームから値が要るので「読まれたら作る」にすると最初の 1 回の値が変わる。案: 読み込んだスクリプトのアセンブリが
  `CanvasTransform.get_ScreenPosition` を参照しているかをメタデータで調べ、参照が無ければ作らない〈反射で読むものは拾えない〉）、「UI/ジェスチャー」
  「描画/スプライト収集・ソート」（描画に要る）。**わらしべフィッシングは Collider2d を 1 つも使っていない**（全シーン・プレハブを数えた。MainGame の
  `start_physics_2d` も 0 個）ので、2D 物理の同期は Play では毎フレーム省かれる。
- [x] **W2-4 基本の部品（ボタン・トグル・スライダ＋数値欄・選択・進捗・グラデーション・9 スライス・円の切り抜き）** — 2026-09-27。
  Draw にグラデーションが無く、9 スライスも `batch2d.rs:19` の TODO のまま。
  → **2026-09-28 に済**（正典 docs/ui_components.md。roadmap §3.8.5）。形と塗りは SpriteComponent の 4 つの欄（`shape`・`fill`・`nine_slice`・`shadow`）と
  別のパイプライン（`sprite_shape.wgsl`。SDF の角丸・楕円・弧・縁・グラデーション 2〜4 色と放射・9 スライス〈伸ばす／繰り返す〉・ぼかしの影）。**既定の欄の
  スプライトは従来の経路のまま**（WarashibeFishing の図鑑の画素・56 点のクリックが変更の前後で一致）。CanvasClip に形（角丸・楕円・スプライトの形）を足し、
  いちばん内側の 1 つを SDF で切る。当たり判定（ポインタイベント・ジェスチャー）も形に合わせた（円のボタンの角は押せない）。部品は `SEED.UI` のスクリプト
  （Button・Toggle・Checkbox・Slider・NumberField・SegmentedControl・ChipGroup・RadioGroup・ProgressBar・ProgressRing）とテーマのトークン（既定の値は
  埋め込みの default_theme.json）、見本は `templates/ui/`（プレハブ 13・ギャラリーのシーン）。SEEDScripting の型を ScriptComponent の型名で付けられることを
  確かめた（W2-3 の残りの (8) の「付ける経路」は既にあった＝ScriptAssemblyManager.Resolve が読み込み済みのアセンブリの型名を引く）。残りは下の「W2-4 の残り」。
- [ ] **W2-4 の残り（形と塗り・基本の部品）** — 2026-09-28。(1) **角丸・楕円の切り抜きで画素ごとに切るのはスプライトだけ**: テキスト（インライン画像を含む）・
  `SEED.Draw` の図形・2D パーティクル・スキンスプライトは外接矩形の scissor だけ（角丸のカードの角へ文字がはみ出すと角で切れない）。直すなら文字の
  パイプライン（FontSystem）とプリミティブのパイプラインにも切り抜きの SDF（ラン単位の uniform か頂点の欄）を足す。(2) **外側の角丸・楕円の祖先は外接矩形**
  （W2-0 の決定のまま。入れ子の角丸を正確に切るならステンシル）。(3) **エディタの GPU の ID 描画は形を見ない**（3D ビューでのキャンバスの選択は外接矩形）。
  (4) **インスペクタの形と塗りの行に「⟲ 既定値に戻す」が無い**: 既定の欄は保存しない（`skip_serializing_if`）ので、共通のリセット（`component_data_with_field_reset`）が
  入れ子の欄の既定値を引けない。直すなら既定の JSON を省略なしで作ってから引く。(5) **動いているエディタでのインスペクタ（形・塗り・9 スライス・影・切り抜きの形）の
  目視は未確認**（ビルドと IPC の解釈の単体テストだけ）。(6) **実機（Pixel 6a）での見た目と手触りは未確認**（手順は ui_components.md §9）。(7) 9 スライスの片の境目で
  線形の補間が隣の片を半画素にじませる（画像の片の間に余白を取れば出ない）。(8) 弧は円だけ・影に広がり（spread）と内側の影が無い・押下の色は補間しない。
  (9) 部品は一覧の行に置いたとき `ListView.Recycled` で押下の見た目を戻す手当てが要る（`SetInteractable` か押下の状態を戻す API を足す）。
  関連: `runtime/src/engine/core/renderer/ui_shape/`・`shaders/sprite_shape.wgsl`・`scripting/src/Api/UI/`・`editor/src/Panels/InspectorPanel.SpriteStyle.cs`。
- [ ] **枠の無い Text の `vertical_align` がベースライン基準で、Middle でも字面が上へずれる（既存）** — 2026-09-28（W2-4 のギャラリーで発見。直していない）。
  `font/text_layout.rs` の `layout_origin` は Top で 1 行目の**ベースライン**を原点に置く（コメントは「ブロック上端が原点」）ため、Middle は「ベースライン − 高さの半分」に
  なり、字面の中心が指定の位置より約 0.9 × 文字の大きさ上に出る（14 の文字で約 13 画素）。枠あり（`box_width` > 0）は枠の中で正しく揃うので、W2-4 の部品のプレハブは
  すべて枠ありの Text にした。既存のゲームが今の位置に合わせて置いているので、直すなら版を上げて既存のシーンの位置を補正するか、枠なしの揃え方を新しい欄にする。
  関連: `runtime/src/engine/core/font/text_layout.rs`・`canvas_text.rs`。
- [x] **W2-5 時刻ホイール（24 時間・ループ・スナップ・触感）** — 2026-09-27。
  → **2026-09-28 に済**（正典 docs/ui_components.md §11。roadmap §3.8.5）。C# の `SEED.UI.WheelPicker`（列）・`SEED.UI.TimeWheel`（時・分・午前/午後。値は TimeOnly）。
  スクロールは W2-3 の CanvasScroll の Interval のスナップをそのまま使い **Rust は変えていない**。行の曲面の見た目は Flutter の ListWheelViewport の式の純関数（`WheelLook`）、
  端をつなげる循環は ListView の使い回し（約 20 万単位の並びの真ん中から始め、止まるたびに遠ければ真ん中へ）、触感は指とその慣性の間だけ。見本は `templates/ui/prefabs/`
  の `wheel_row.actor`・`wheel_picker.actor`・`time_wheel.actor` とギャラリーの段。残りは下の「W2-5 の残り」。
- [ ] **W2-5 の残り（時刻ホイール）** — 2026-09-28。(1) **実機（Pixel 6a）の手触り・触感は未確認**（手順は ui_components.md §11.9）。触感は中央の行が変わるたびに
  1 フレーム 1 回まで鳴らす（速いフリックで最大 60 回/秒）ので、Android の EFFECT_CLICK が強すぎ・多すぎなら間隔の下限を足す。(2) **Flutter の拡大鏡の 2 度描き**
  （帯の中だけ拡大・不透明、外は 0.447）はせず、帯の中の度合いで連続に補間している（帯の境をまたぐ行は中間の濃さ）。オフアクシス（列ごとの左右の傾き）と
  行の中での遠近の横の歪みも無い。(3) **最小・最大の時刻**（CupertinoDatePicker の minimumDate/maximumDate。値で時・分の列の選べない行が変わる）が TimeWheel に無い
  （列の `SetItemEnabled` で作れる）。(4) **キーボードのフォーカスは仮**（`WheelFocus` = 最後に触れたホイール）。W2-6（入力欄）・W2-7（画面のスタック）の
  フォーカスへ寄せる → **2026-09-28 の W2-7 で `UiFocus`（フォーカスの範囲）の窓口にした**（覆われた画面・選んでいないタブのホイールは矢印キーを受けない）。PC でマウスのホイール（`Input.MouseScroll`）で回す操作も無い。(5) **無限のスクロールではない**（中身 ≒ 20 万単位の中で止まるたびに真ん中へ戻す。
  止めずに最速のフリックを 25 回以上続けると端で跳ね返る）。(6) ScrollTo の曲線は easeInOut だけ（Flutter の午前/午後の連動は easeOut）。(7) 読み上げ（アクセシビリティ）が無い。
  関連: `scripting/src/Api/UI/Wheel/`・`scripting/src/Api/UI/Widgets/WheelPicker.cs`・`TimeWheel.cs`・`templates/ui/`。
- [ ] **一覧の行の使い回しがシーン操作のコマンド（ヒエラルキーの送信つき）になる（既存・W2-3）** — 2026-09-28（W2-5 の確かめで `SEED_REDRAW_LOG=1` の
  理由に `motion+lifecycle` が 1 フレームおきに出ることから見つけた。直していない）。ListView が行を付け替えるたびの `GameObject.Visible`・`CancelGestures`
  （と W2-5 の行の文字の表示の切り替え）は、スクリプトのシーン操作のコマンド（`apply_script_scene_commands`）として当たり、描く理由の `lifecycle` を申告し、
  エディタへヒエラルキーを送る（Play は 400ms に 1 回へ間引き）。一覧・ホイールを回している間、IPC がつながっていれば 400ms ごとにシーン全体の木を JSON にして送る。
  直すなら表示・取り消しのコマンドではヒエラルキーを送らない（木の形は変わらない）、か表示の変化だけ差分で送る。関連: `app/script_scene_ops.rs`・`app/hierarchy_sync.rs`・
  `scripting/src/Api/UI/ListView.cs`。
- [ ] **W2-6 文字入力と IME・文字の寸法（`Text.Measure`）・グリフの追い出し** — 2026-09-27。
  グリフのアトラス（4096²・約 2,500 字）はあふれると追い出さずに描かない（`font/atlas.rs:17-19,183-194`）。日本語のアプリでは足りなくなりうる。
- [x] **W2-7 画面の組み立て（タブ・画面のスタック・ダイアログ・シート・戻るの段・トースト）** — 2026-09-27。
  → **2026-09-28 に済**（正典 docs/ui_navigation.md。roadmap §3.8.5）。C# の `SEED.UI` の `ScreenStack`・`UiScreen`・`TabHost`・`TabBar`・`ModalHost`・`Dialog`・
  `BottomSheet`・`TopSheet`・`ToastHost`・`Toast`・`BackDispatcher`・`UiFocus`。Rust は `CanvasLayoutItem` の実行中だけの `translate`・`translate_fraction`・
  `layer_bias`（保存しない）と `Screen.DpScale`。見本は `templates/ui/scenes/ui_navigation.scene`。残りは下の「W2-7 の残り」。
- [ ] **W2-7 の残り（画面の組み立て）** — 2026-09-28。(1) **実機（Pixel 6a）で未確認**（戻るボタン・戻るジェスチャーの段の順〈UC-6〉・背面へ回って戻る・
  バーを表示した安全領域〈UC-7〉・動きの 60 fps。手順は ui_navigation.md §12）。エディタに埋め込んだ Play でも未確認（PC の Esc がエディタの操作と重ならないか）。
  (2) **部分木の透明度（CanvasGroup の alpha）が無い**: フェードは幕（背景の色）を通して入れ替え、ダイアログの札・トーストは透明度で出入りしない。
  足すなら `layer_bias` と同じく表の配置へ「祖先の濃さの積」を持たせ、描画アイテムの色へ掛ける。(3) **`SEED.Draw` の図形に `layer_bias` が効かない**
  （図形は座標空間の持ち主のノードしか持たない。`PrimitiveSpace` に底上げを持たせれば効く）→ **2026-09-28 の W2-8 で解消**（`PrimitiveSpace.layer_bias` と
  `primitive2d/pass.rs` の `apply_space_layer_bias`。積んだ画面の中のグラフが背景の下に隠れない。底上げ 0 のシーンは同じ並び）。(4) **画面の中の表示のレイヤーは段の値〈10,000・タブの中 1,000〉
  より小さく**という約束だけ（大きなレイヤーの表示を持つ画面を積むと前後が崩れる。検査やエディタの警告は無い）。(5) ダイアログの本文の高さは文字の数からの
  見積もり（`DialogLayout`。W2-6c の `Text.Measure` へ替える）（→ 2026-09-29 の W2 の手直し P2-1 でエンジンの折り返しの規則と組み込みの書体の送り幅に合わせた。
  `Text.Measure` への置き換えは残る）。(6) **下からのシートの「先に広げる」が無い**（半分の段で中身の一覧を上へ引くと一覧が先に動く。
  W2-3 の入れ子は内側が先・端の残りを外側へだけ。Android の nested pre-scroll 相当を足すなら canvas_scroll/nesting.rs）。開く・閉じる曲線は ScrollTo の
  easeInOut 固定。(7) 上からの覆いの作りは最小（Flutter 版の固定の頭・一覧の外の閉じる・左右の余白・四隅の角丸は W3 のプレハブで）。
  (8) **予測型の戻る**（Android 14+ の戻るジェスチャーの途中ののぞき見・`OnBackInvokedCallback`）は扱わない（→ **2026-09-29 の W2 の手直し P1-3 で opt-in として
  入れた**: `BackDispatcher.WouldHandle`・進み具合のプレビュー〈画面・札・板を縮める。実行中の倍率 `CanvasLayoutItem.VisualScale`〉。docs/ui_navigation.md §5.1）。(9) `CanvasLayoutItem` の実行中だけの欄は
  インスペクタに出ない（Play 中に値を見るには `SCRIPT_DEBUG` かスクリプト）。(10) Wake or Pay の仕様 §3.7 は「タブの最上位 → 背面へ」だけで、
  W2-7 の依頼の「根のタブ以外なら根のタブへ」（`TabHost.BackToFirstTab` 既定 true）と違う。W3 でどちらにするか決める（false で仕様どおり）。
  関連: `scripting/src/Api/UI/Navigation/`・`runtime/src/engine/core/canvas_layout/`・`templates/ui/`。
- [x] **W2-8 グラフ（折れ線・積み上げ棒・軸・吹き出し・パンとズーム）** — 2026-09-27。
  → **2026-09-28 に済**（正典 docs/ui_charts.md。roadmap §3.8.5）。C# の `SEED.UI` の `LineChart`・`BarChart`・`ChartView`（純粋な計算は `Charts/Model/`）。
  Rust は W2-2 のアリーナの 2 本指のピンチ（`gesture/pinch.rs`）、`SEED.Draw` の見た目の拡張（`DrawStyle`）・`Draw.Area`・拡張つきの図形の軽い三角形分割・
  図形への `layer_bias`。見本は `templates/ui/scenes/ui_charts.scene`。残りは下の「W2-8 の残り」。
- [ ] **W2-8 の残り（グラフ）** — 2026-09-28。(1) **実機（Pixel 6a）で未確認**（2 本指のピンチの手触り・2.625 倍の縁・払う慣性・縦の一覧の中の斜めの指〈UC-3〉・
  パンの間の 60 fps。手順は ui_charts.md §10）。(2) **PC の入力の注入（IPC の `INPUT_SEQUENCE`・MCP の `game_input_*`）は 1 本の指（マウス）だけ**なので、
  ピンチは PC で通しの確認ができない（Rust のアリーナと C# の `ChartViewport` の単体テストだけ）。注入に指の番号を足せば確かめられる（`input/inject/`）。
  (3) **グラフの大きさはノードの Sprite の幅・高さ**（レイアウトのコンテナが伸ばした大きさをスクリプトから読む API が無い。`CanvasLayoutItem` の大きさの指定で合わせる）。
  (4) **吹き出しの大きさは文字の数からの見積もり**（`DialogLayout`。W2-6c の `Text.Measure` へ替える）（→ 2026-09-29 の W2 の手直し P2-1 で組み込みの書体の
  送り幅に合わせ、吹き出しは文字に合って小さくなった〈例「9/11 06:49」82 → 62 px〉。`Text.Measure` への置き換えは残る）。(5) **祖先が非表示のグラフも毎フレーム `SEED.Draw` を積む**
  （座標空間が解決できず Rust が捨てる。FFI の呼び出しだけ残る。棒 365 本で約 0.4 ms。祖先の表示を読む API があれば止められる）。(6) ピンチは倍率と中点だけ
  （回転なし。1 本離した後の残った指のパンなし）。(7) 払った後の慣性は端で止まる（跳ね返りなし）。(8) **最適化なしの build でしか測っていない**
  （365 本の積み上げの棒は 1 フレームの UI の積み込み 7.2 ms。配布版・実機の値は未計測）。(9) 縦の格子線・凡例・目盛りの文字の衝突の検出は無い。
  (10) 軽い三角形分割の折れ線はつなぎの内側で本体が重なる（半透明の太い線ではわずかに濃い）。つなぎは丸だけ。
  関連: `scripting/src/Api/UI/Charts/`・`runtime/src/engine/core/renderer/primitive2d/`・`runtime/src/engine/core/input/gesture/pinch.rs`・`templates/ui/`。
- [x] **W2-9 テーマ（トークンの JSON・実行中の切り替え）と `templates/ui/` の見本・ギャラリー** — 2026-09-27 記載 / 2026-09-28 実装。
  正典 docs/ui_theme.md。継承（`extends`）と一部だけの上書き・既定のテーマへの落ち方、知らない名前・型の誤りの警告、トークンの表 1 か所（`UiTokenCatalog`。docs の表は
  テストが一致を確かめる）、明暗（`light` / `dark` の節・`brightness`・`UiBrightnessMode`・端末の明暗 `app.ui_mode` と `platform.ui_mode_changed`）、切り替えの
  その場の当て直し（`UiRegistry`）と色の補間・`UiTheme.Changed`、書体のトークン（`font.*`）、飾りの結び付け `ThemeStyle`、種の色 `seed_color`（Wake or Pay の写し方）、
  見本のテーマ 3 つとギャラリーの仕上げ（テーマの帯・一覧・画面の組み立て・グラフ）。残りは下の「W2-9 の残り」。
- [x] **SEED.UI の部品が既定で読むプレハブがパッケージに入らない（W2-7 からの既存の不具合）** — 2026-09-28（W2-9 のギャラリーの APK の組み立てで発見）。
  → **2026-09-28 夕に済**（roadmap §3.9.1・docs/packaging.md）: 収録の起点の「エンジン内蔵参照」に、runtime/src の Rust と並べて**エンジンの C# ライブラリ
  （scripting/src）の定数の `assets://` パス**を足した（`AssetCollector.ReadRuntimeBuiltinReferences`。コメント行の例は拾わない・プロジェクトにあるファイルだけ入る）。
  部品の欄に書き出す案は、利用者が作るすべてのシーンに既定の値を書かせることになり、書き忘れ・定数の変更で同じ不具合が戻るので採らなかった。
  回避の文字列を消した試験のプロジェクトで `screen_frame.actor` が pak に入り（40 ファイル。起点に C# ライブラリを使わない場合は 39・無し）、実機の APK で
  ナビゲーションの見本が描かれることを確かめた（§3.9.1）。以下は記載時のメモ。
  `ModalHost` の `DialogPrefab`・`SheetPrefab`・`OverlayPrefab`、`ToastHost` の `ToastPrefab`、`ScreenStack` の `FramePrefab` の既定の値
  （`assets://ui/prefabs/dialog.actor` など）は SEEDScripting の中の定数で、パッケージの収録（`AssetCollector`。プロジェクトの .cs・シーン・データの
  `assets://` の文字列と runtime/src だけを辿る）が拾わない。欄を空のまま使うシーン（`templates/ui/scenes/ui_navigation.scene` の ModalHost・ToastHost・
  ScreenStack）を APK にすると、プレハブが pak に無く、ダイアログ・シート・覆い・トースト・画面の枠を作れない見込み（**推論。実機では未確認**）。
  W2-9 のギャラリーは欄にパスを明示して避けた（pak に入ることを確かめた）。直すなら収録の起点に SEEDScripting の既定のプレハブ（`templates/ui/prefabs/` の
  該当ファイル）を足すか、部品の既定の値をシーンに書き出す。関連: `editor/src/Packaging/Collect/AssetCollector.cs`・`scripting/src/Api/UI/Navigation/`。
  → **2026-09-28 午後に実機で確認**（Pixel 6a。roadmap §3.9）: `ui_navigation.scene` を APK にすると画面が真っ黒（背景の 1 枚だけ）で、logcat に
  `[Script] Instantiate 失敗 (assets://ui/prefabs/screen_frame.actor): IO error: No such file or directory`。pak の中身（61 ファイル中 39）に `screen_frame.actor` が
  無かった。同じ pak にギャラリーのシーンがあったのでダイアログ・シート・覆い・トーストは入っていた（ナビゲーションだけのプロジェクトでは欠ける見込み。推論）。
  試験ではプロジェクトのスクリプトに `"assets://ui/prefabs/screen_frame.actor"` の文字列を書いて収録させて避けた（`ScreenStack` は枠を作れないとログだけで画面が空になる）。
- [ ] **W2-9 の残り（テーマ）** — 2026-09-28。(1) **実機（Pixel 6a）で未確認**: 端末のダークテーマの切り替えで `onConfigurationChanged` → `platform.ui_mode_changed` →
  テーマが切り替わること・`app.ui_mode` の値（手順は ui_theme.md §11・android.md §25.17）。(2) **エディタに埋め込んだ Play では OS の明暗の変化が届かない**
  （子のウィンドウに WM_SETTINGCHANGE が来ない。単体起動は届く。問い合わせは効く）。(3) **種の色の規則は Material 3 の fromSeed（HCT）の近似ではない**
  （色相を保って明るさだけを動かす。第 3・第 4 の色・面の色みは作らない）。Wake or Pay の W3 で Flutter 版と見比べて足りなければ HCT を足す。
  (4) **補間は色だけ**で、補間の間は全部品を毎フレーム当て直す（部品 128 のギャラリーで debug の PC は約 50 回/秒。部品の多い画面・実機で重ければ、色だけの軽い当て直しの口を部品に足す）。
  (5) **部品の文字の書体は常にテーマのもの**（部品ごとの書体の上書きが無い）。太さの違う書体ファイルの同時使用は W2-6c。(6) トークンの値の範囲は調べない（型だけ）。
  (7) 見本のシーン `ui_navigation.scene`・`ui_charts.scene` の飾りには `ThemeStyle` を付けていない（焼き込みの色のまま）。(8) 既定の暗い方の `on_primary` / `primary` は
  4.35:1（AA の 4.5 に少し足りない。W2-4 の値のまま）。(9) テーマをエディタで編集する画面（E-07 の (b) の辞書コンポーネント）は無い。(10) `ThemeStyle` は単色の塗り・縁・角丸・
  文字だけ（グラデーションの色・影の色は結び付けられない）。(11) システムバーのアイコンの明暗をテーマの明暗に合わせる仕組みは無い（下の「システムバーを出したままのときの
  文字色（明暗）を選べない」。`UiTheme.Changed` から当てる形にする）。
  (12) **エディタに埋め込んだ Play の区切りでテーマの状態が戻らない**: `UiTheme` は SEEDScripting の静的な状態で、埋め込みの Play（ENTER_PLAY / EXIT_PLAY）は
  スクリプトを読み直さないので、前の Play の最後のテーマ・選び方が次の Play に残る（W2-4 の `UiTheme.Use` から同じ。`SEED.Debug` のコマンドの受け手など
  ほかの C# の静的な状態も同じ）。今は「起動のスクリプトの OnStart でテーマを当てる」約束で避ける（ギャラリーは `UiGalleryThemeBar.StartTheme`）。
  直すなら Play の区切りで C# へ知らせる入口（ScriptBridge の `ResetPlaySession` と `clr_host/entry_points.rs`・`play_mode_ops.rs` の enter_play / exit_play）を足す。
  (13) テーマの JSON はスクリプトが文字列で読むので、パッケージの収録が拾うには完全な `assets://` のパスをどこかに書く必要がある（`extends` の相対パスは拾われない。
  ui_theme.md §7.1）。テーマの JSON の `extends` を収録が辿る（JSON の相対パスを解く）ようにすれば要らなくなる。
  関連: `scripting/src/Api/UI/Theme/`・`Widgets/ThemeStyle.cs`・`runtime/src/engine/platform/bridge/desktop_sim/ui_mode_*.rs`・`templates/ui/`。
- [ ] **W2-10 描かなくてよいときは描かない** — 2026-09-27。前面では毎フレーム描き続け、UI と提示だけで Pixel 6a の GPU 約 4.5 ms を使う
  （上の「固定分 約 4.5 ms」の項目）。止まっている画面の多いアプリの電池に効く。
  → W2-0 で方式を決めた（roadmap §3.8.3・§3.8.4）。部品を作り始める前に「描く理由」の API だけ先に決める（§3.8.5）。
  → **2026-09-28 に W2-10a（「描く理由」の API と判定。既定で無効）が済**（正典 docs/redraw_policy.md。roadmap §3.8.5）。残りは下の「W2-10a の残り」と、
  部品と画面ができた後に通しで詰めること（止める判定のフレームの数・時計の表示・UC-10 の計測）。
- [ ] **W2-10a の残り（描く理由）** — 2026-09-28。(1) **実機（Pixel 6a）で未確認**（止めたときの CPU・GPU・タップ・文字入力〈`RedrawWaker`〉・
  プラットフォームのイベント・音声フォーカス・回転で起きること。手順は redraw_policy.md §9）。(2) **止めている間も物理のスレッドは進む**（W2-0 の実機で
  止めている間の CPU 6% の大半）。背面の `background_pause` と同じく止めるかを W2-10 で決める（結果の待ち行列には上限 120 件を付けた。`physics/result_backlog.rs`）。
  (3) **シェーダーの時間で動くもの**（水面・草の風・L3 の time・コースティクス）・インタラクション場・地表カバー・水位・DDGI などの時間の積み上げ・地形の LOD の
  積み残しは「動いている」を申告しない（使う画面は `SetContinuous`）。(4) **ファイルの保存の検知**（Play 中のシェーダーのホットリロード・水面・アイコン・InputMap の
  1 秒ごとの更新時刻の確認）は止めている間は止まる（IPC の `RELOAD_*` は起こす）。(5) ゲームパッドの操作では起きない（フレームの頭で読む）。
  (6) **エディタに埋め込んだ Play での動きは未確認**（単体の SEED.exe の TCP の IPC で確かめた。名前付きパイプも同じ `read_loop` を通る）。
  (7) エディタのプロジェクト設定の画面に `render_policy` の欄が無い（JSON を直接書く。保存しても `ExtraData` で消えない）。(8) 止めていた後の最初のフレームが重い
  （W2-0 の R-13）。(9) スクリプトの `Invoke`（遅れて呼ぶ）の仕組みが SEED に無い（`RequestAfter` が申告の代わり。W2-3 以降の部品が要るなら作る）。
  関連: `runtime/src/engine/core/redraw/`・`app/redraw_hooks.rs`。
- [ ] **Windows の日本語キーボードの「半角/全角」キーが押されたまま残る（`Input` の既存の不具合）** — 2026-09-28（W2-10a の PC の確認で発見）。
  UiSpike を前面に出さずに起動しただけ（キーボードに触っていない）で、`KeyboardState` の押しているキーに `Backquote` が入ったまま消えなかった
  （`SEED_REDRAW_LOG=1` の `押している: keys={Backquote}`）。JIS 配列の「半角/全角」は winit では `Backquote` の位置のキーで、Windows がこのキーの離しを送らない
  ため（と見られる。推論。winit がフォーカスを得たときに押されているキーを合成するときにも入りうる）。影響: スクリプトの `Input.IsPressAnyKey`・
  `Input.GetKey(Backquote)` が押しっぱなしに見える。W2-10a では実キーボードのキーを「押している」の理由から外して避けた（redraw_policy.md §3）。
  直すなら `KeyboardInput` の `KeyCode::Backquote`（と IME の切り替えのキー）を押しっぱなしの集合へ入れない、かフォーカスを失ったときに集合を空にする。
  関連: `runtime/src/engine/core/input/keyboard.rs`・`app/event_handler.rs`。
- [ ] **AI 補完へ届くスクリプト API の文書が §2 の途中で切れている（既存）** — 2026-09-28（W2-10a の調査で見つけた。数えたのは調べたエージェント）。
  `editor/src/Panels/ScriptEditor/InlineCompletion/ScriptApiReference.cs:32` の `MaxChars = 12000` で打ち切るため、`Compact()` の後の約 125,000 字のうち AI に届くのは md の 518 行付近
  （第 2 節）まで。§7.12 以降（Screen・Platform・W2-10a の Redraw）は補完に届いていない。上限を上げるか、節ごとに要るものだけを選んで渡す。
- [ ] **add-script-api の Skill に「新しい種類の FFI（`ScriptHostApi` の欄）を足す手順」が無い（既存）** — 2026-09-28（W2-10a）。W1-1 の `SEED.Platform`・
  W2-10a の `SEED.Redraw` は、`host_api.rs` の構造体と `HOST_API` の末尾・`ScriptHost.cs` の構造体の末尾に同じ順で 1 欄ずつ足し、別ファイルに
  `pub(super) extern "system" fn ffi_xxx` を置く形で足した（サイズや版の照合は無いので、両方のビルドが要る）。Skill に節を足す。
- [x] **開発用の APK の libSEED.so が最適化なし（`dev`）で、UI の見本のスクロールが 19〜26 fps** — 2026-09-28（W2 の実機の回で発見。roadmap §3.9 の原因 1）。
  → **2026-09-28 夕に済**（roadmap §3.9.1・docs/android.md §5.4）: 開発用の .so を PC の Play と同じ構成の表（`editor/config/runtime_build_configs.json`）の既定＝`develop`
  （`cargo ndk … build --profile develop`）で作るようにした（`editor/src/Android/Native/AndroidNativeProfile.cs`）。SeedAndroid の `--native-profile <debug|develop|release>`・
  設定 JSON の `native_profile`・エディタの実行ボタンはツールバーのランタイムのビルド構成で選べる（`debug` で従来の最適化なし）。指紋は cargo のプロファイル名。
  cargo-ndk が写さなかったときはプロファイルの出力を自分で写す保険も足した。増分のビルド（SEED クレートの数ファイルの変更）は cargo 2 分 25 秒〜3 分 46 秒（4 回）。
  以下は記載時のメモ。
  SeedAndroid の `run` / `install` とエディタの Android の実行は、`--release`（配布用）でなければ `cargo ndk … build`（`dev`＝SEED クレートは opt-level 0）で .so を作る
  （`editor/src/Android/Steps/NativeBuildStep.cs`・`AndroidRunRequest.OptimizesNative`）。同じ APK の中身で .so だけを替えると、スクロール中のギャラリーは
  dev 23〜26 fps（CPU 38.0 ms/フレーム）→ `develop`（opt-level 1）59.7 fps（8.7 ms）→ release 59.7 fps（8.5 ms）、グラフは 19〜24 fps（41.5 ms）→ 59.7（9.7）→ 59.3〜60.0（9.2）。
  PC の Play は既に `[profile.develop]`（ルートの Cargo.toml）が既定。直すなら開発用の APK の既定を `--profile develop` にし（cargo-ndk 4.1.2 が jniLibs へ写すことは
  手で確かめた）、ネイティブをデバッグするときだけ `dev` を選べる指定を残す。指紋（`AndroidStepFingerprints.Native`）にプロファイルを入れる。初回の develop の
  ビルドは依存を含めて 6 分 21 秒（差分のビルドの時間は未計測）。develop はギャラリーのスクロールの最初に山が残る（最大 26 ms・16.7 ms 超 5/240。release は 1/240）。
- [x] **「描画/UI 描画順の統合・GPU 積み込み」が毎フレームすべてを作り直す（テキストのレイアウト・GPU バッファ・`SEED.Draw` の三角形分割）** — 2026-09-28
  → **2026-09-28 夕に主な部分が済**（roadmap §3.9.1）: まず区間を分けて測り（`描画/UI/テキスト`・`…/配置`・`…/グリフ焼き`・`…/GPU 転送`・`描画/UI/図形`・
  `…/三角形分割`・`描画/UI/スプライト`）、スクロール開始の山の正体が**初めて出る字の SDF の総当たり**（1 字 ≒ 185 万回の比較。PC の最適化なしで 1 フレーム最大 58 ms）と
  **アウトラインの無い字（スペース）を毎フレーム焼きに行く**ことだと分かったので、(1) SDF を厳密な距離変換（`font/sdf_edt.rs`。総当たりと全画素一致をテストで確認）、
  (2) 置けない字の表（`FontSystem::unplaceable`）、(3) 行分割と字の配置の使い回し（`font/text_layout_cache.rs`。書体・本文・条件・画像が同じなら前のフレームの配置）、
  (4) テキストの GPU バッファの使い回し（`font/text_gpu_stream.rs`。全ゾーンで 1 本・`write_buffer`）、(5) `SEED.Draw` の三角形分割の使い回し
  （`renderer/primitive2d/tess_cache.rs`。形が同じなら前のフレームの分割）、(6) 画面の完全に外の `SEED.Draw` の図形は頂点を射影・積まない
  （`renderer/primitive2d/offscreen_cull.rs`）、(7) 使い回しの表を引くハッシュを速い Fx 方式に（`core/fast_hash.rs`）・全頂点を射影できる図形は対応表を作らない
  （`primitive2d/pass.rs` の `append_projected_mesh`）を入れた。隠れたグラフを C# 側で積まない案は `GameObject.Parent` が重くて損だったので外した（下の新しい項目）。
  よく使う字を先に焼く (d) は、(1) で 1 字の焼きが数十〜数百倍速くなったので入れていない（実機の山の数値は §3.9.1）。残りは下の「SEED.Draw の頂点の射影が毎フレーム全頂点」。
  以下は記載時のメモ。
  （W2 の実機の回。roadmap §3.9 の原因 2。直していない）。`UiZoneDraw::build`（`renderer/ui_draw_pass.rs`）が毎フレーム、見えている全テキストのレイアウトと
  グリフの四角形（`CanvasTextRenderer::build_grouped` → `append_item` → `resolve_layout_with_images`）、ゾーンごとに新しい頂点・添字のバッファ
  （`font/mod.rs` の `build_gpu_batch` の `create_buffer_init`）、全 `SEED.Draw` の三角形分割（`Primitive2dRenderer::push`）を作る。実機の平均は dev の .so で
  ギャラリー 12.8 ms・グラフ 30.4 ms、release で 1.39 ms・3.72 ms（グラフのフレームの 40%）。グラフを隠すと 0.39 ms・GPU 7.6 → 5.4 ms。主にスクロールの最初の
  十数フレームに山（release 12.9・develop 19.8・dev 209 ms。初めて見える文字のグリフのラスタライズと見られる。推論＝区間の中を分けて測っていない）。
  C# のグラフも毎フレーム全部の図形を積み直す（棒グラフ 3 つで release 0.45〜0.80 ms。隠しても減らない）。直すなら (a) テキストのレイアウトと局所座標の四角形を
  文字列・書体・大きさ・折り返しの幅が変わるまで覚える、(b) 頂点・添字のバッファを使い回す（`InstanceStream` と同じ形）、(c) `SEED.Draw` の保持型の描画
  （データ・表示範囲が変わるまで三角形分割を覚える）と、隠れた・動いていないグラフは積まない、(d) 数字などよく使うグリフを先に焼く。まず区間を
  「テキスト」「プリミティブ」「スプライト」「アップロード」に分けて測る。関連: `app/frame_renderer.rs` の 5002 行付近・`font/canvas_text.rs`・`renderer/primitive2d/`・
  `scripting/src/Api/UI/Charts/`。
- [ ] **`GameObject.Parent`（FFI `ffi_parent_of`）が呼ぶたびにアクタの木全体を先行順にたどる**（2026-09-28 夕の 60 fps 安定化で発見。直していない）—
  子から親を引く表が無く、`host_api.rs` の `find_parent` が根から全アクタの子の並びを見る（O(アクタ数)）。グラフ（`ChartView.Paint`）で「描く面から根まで
  `Visible` を辿り、隠れていれば積まない」を試したところ、見えているグラフでも 1 つ 1 フレーム約 0.02〜0.08 ms 増え（実機 develop のグラフの見本で
  `Update/BarChart` 0.397 → 0.473〜0.488 ms・`Update/LineChart` 0.109 → 0.150〜0.155 ms）、隠れたグラフを積まない得より損が大きかったので外した。
  直すなら (a) 描画の表（`CanvasLayoutTable` の `is_drawn`＝祖先を含む実効の表示）を 1 回の FFI で引ける口（例 `GameObject.VisibleInHierarchy`）を足す、
  (b) アクタの親の表（entity → 親）をシーンの変更のときだけ作り直して `ffi_parent_of` を O(1) にする。関連: `runtime/src/engine/core/scripting/host_api.rs`・
  `scripting/src/Api/GameObject.cs`・`scripting/src/Api/UI/Charts/ChartView.cs`・docs/ui_charts.md §11。
- [ ] **SEED.Draw の頂点の射影（NDC 化）が毎フレーム全頂点**（2026-09-28 夕の 60 fps 安定化で残したもの。roadmap §3.9.1）— 三角形分割は使い回すようになったが、
  行列（スクロールで毎フレーム変わる）と色を掛ける `Primitive2dRenderer::push` の頂点ごとの射影は毎フレーム全部やり直す。グラフの見本では UI の積み込みの大半
  （実機 develop のグラフの見本で「描画/UI/図形」3.05 ms/フレーム〈UI の積み込み 3.72 ms のうち〉・PC の最適化なしで 9.0 ms。475 図形）。
  **画面（NDC）の完全に外の図形は積まない**ところまでは 2026-09-28 夕に入れた（`renderer/primitive2d/offscreen_cull.rs`。分割と一緒に覚えた外接矩形の 4 隅を射影して判定。
  窓を 540×700 にしてページの下を画面の外にした PC の見本で、画素は変更前と一致・「描画/UI/図形」の自分の時間 9.0 → 5.5 ms〈最適化なし〉）。
  scissor（スクロールの切り抜き）の外は判定していない（scissor を張らないパスがあるため）。残りの案: (b) 頂点を局所座標のまま GPU へ置き、行列を図形ごとの
  uniform / ストレージで渡して頂点シェーダで射影する（CPU は行列だけ。ただし NDC の丸めが変わるので画素の比較で差が出うる）、(c) C# のグラフに保持型の描画
  （データ・表示範囲が変わるまで図形を積み直さない）を足す、(d) scissor を必ず張るパスでは切り抜きの外も捨てる。関連: `renderer/primitive2d/pass.rs`・
  `renderer/shaders/primitive2d.wgsl`・`scripting/src/Api/UI/Charts/`。
- [ ] **UI だけのシーンでも CPU に「描画/Submit・Present」1.4〜2.5 ms と「BeginFrame(スワップチェーン取得)」の山（最大 7.4 ms）が残る** — 2026-09-28
  （W2 の実機の回。roadmap §3.9）。どの .so でも同じ値（wgpu・ドライバ側は dev でも opt-level 2）で、release のナビゲーションでは最大の区間。中身は分けて測っていない
  （毎フレーム作って捨てる文字のバッファの後始末が乗っているかは未確認。推論）。
  → 2026-09-28 夕（roadmap §3.9.1）: 文字のバッファを使い回すようにした後も実機 develop で平均 2.2〜3.0 ms・最大 4〜8 ms と変わらず（文字のバッファの後始末ではなかった）、
  UI の積み込みを減らした後はギャラリー・画面の組み立てのフレームで最大の区間。wgpu・ドライバ（Vulkan の submit・present）の中は分けて測っていない。
- [x] **ギャラリー・グラフの見本（`templates/ui/scenes/ui_gallery.scene`・`ui_charts.scene`）が 540×1200 dp 固定** — 2026-09-28（W2 の実機の回で発見）。
  Pixel 6a（1080×2400 px・2.625 倍＝411×914 dp）では右の約 130 dp（テーマの帯の 4 つ目のボタン「森・丸」・「ゆっくり」・数値欄・グラフの右端）が切れ、
  上のテーマの帯・見出しがステータスバーに重なる（`system_bars: visible`。安全領域を見ていない）。ナビゲーションの見本は画面の大きさと安全領域に合って収まる。
  直すなら見本のルートを画面の幅に合わせ（アンカーで伸ばす・行の折り返し）、上端に安全領域の余白を取る（ナビゲーションの見本と同じ作り）。
  → **2026-09-29 に W2 の手直し P2-5 で直した**（docs/ui_theme.md §9・docs/ui_charts.md §8）: 両方の見本をルート → Body（CanvasComponent・親いっぱい・
  CanvasSafeArea 4 辺・縦の CanvasStack）→ Page（flex 1・切り抜き・縦のスクロール。中身の大きさは `auto`＝中身の並びに合う。窓そのものが縦の CanvasStack）→
  Content（縦の CanvasStack・余白 16）に組み直した（背景・重ねる面・トーストは画面全体のまま）。ギャラリーは段（`ShapesSection` など。縦の Stack）と行ごとの CanvasWrap、
  テーマの帯は縦の Stack（4 つのボタンと明暗のセグメントの項目を flex で等分・セグメントは最大 300）、時刻ホイールは島の子にして列（説明 ＋ 島）ごと折り返す。
  グラフの見本は見出しとグラフの組（縦の Stack・間隔 4）を 5 つ、WakeHistory の ± は anchor x 1（右の端から −84・−40）。部品の名前は変えていない。
  `gallery,scroll,<ノードの名前>` を足した（段の位置は画面の幅で変わるため）。`UiGalleryDemo` の `RoundClip/HitPad`（パスの FindChild は直下の子から辿るので
  W2-9 で Page/Content の下へ移ってから引けていなかった。コードを読んで確かめ、先頭の名前だけ深さ優先で引くよう直した）。PC（起動 7 回）: 窓 540（倍率 1）・
  411 dp の模擬（`SEED_SIM_SCALE_FACTOR=1.3139`・`SEED_SIM_SAFE_AREA=0,32,0,21`）・360 dp（倍率 1.5）の撮影で、ページの右の余白の列（中身の右の端より右）が
  すべて背景の色（違う画素 0）、411 dp の模擬で帯の上の 31 行（ステータスバー）とページの下の 20 行（ジェスチャーの帯）も背景の色だけ、いちばん下までスクロールした
  最後の段の下の端はギャラリー 1,147.5 px・グラフの見本 1,158.0 px（ページの下の端 1,179 px の上。その下は Content の末尾の余白）。テーマの切り替え・明暗・トグル・ボタン・チップ・ホイールの値・一覧のフルスワイプの削除（411 dp で行の幅 379）・ダイアログ・
  グラフの選択とハンドル・± が動き、`[UI]` のログに警告・例外なし。単体テスト 3 件（`SampleLayoutTests.cs`。壊した見本〈変更前の絶対配置・幅 400 の部品・枠の無い見出し〉で
  落ちることも確かめた）。**実機（Pixel 6a）は未確認**。
- [x] **W2 の実機の手触りの確認の残り** — 2026-09-28（roadmap §3.9 で fps の計測へ切り替えたため未実施）。スクロールの慣性・跳ね返りの感想と行のスワイプ
  （ui_scroll_list.md §11）、時刻ホイールの回し心地・循環・触感（ui_components.md §11.9）、戻るの段の順（ダイアログ → シート → 画面 → 根）と予測型の戻るの見た目
  （ui_navigation.md §12）、グラフのピンチの手触り（中点の固定・縦のスクロールとの取り合い。倍率 1〜6 の範囲と吹き出しはログで確認済み。ui_charts.md §10）、
  端末のダークテーマの切り替えの追従（ui_theme.md §11）、M7（android.md §25.14.7）。
  release の .so の試験の APK（`com.seedengine.uidevice`。作業フォルダの UiDevice）が端末に入ったまま。上の「開発用の APK の .so」を直してから行うのがよい。
  → 2026-09-28 夕: 開発用の APK の .so は develop になり（上の項目・済）、UI の見本はスクロール中 59.3〜60.0 fps（roadmap §3.9.1）。端末には s5 の試験の APK
  （`com.seedengine.uidevice`・develop の .so・回避の文字列の無い作業フォルダ `tmp/w2_perf/UiDeviceNoWorkaround`）が入っている。手触りの確認を再開できる。
  → **2026-09-29 に利用者と実施**（roadmap §3.9.2。作業フォルダ `tmp/w2_feel/`）。スクロール・行のスワイプ・戻るの段・グラフ・明暗の追従は期待どおり、時刻ホイールと
  M7 は下の項目の不具合・要望つき。行っていない分は下の「W2 の実機の確認の残り（2026-09-29 の後）」。
- [ ] **W2 の実機の確認の残り（2026-09-29 の後）** — 2026-09-29（roadmap §3.9.2 で行っていない分）。時刻ホイールの触感（端末の「タップ時のバイブ」を一時的に
  オンにして。ui_components.md §11.9 の 2）、3 ボタンのナビゲーションの戻る・キーボードが出ているときの戻る・回転・シートのつまみを払って閉じる・覆いとトーストの払い
  （ui_navigation.md §12 の 1〜6 の残り）、慣性の途中のタップ・PressCancel・1,000 行の比較（ui_scroll_list.md §11 の 3・4・6）、グラフの縁と目盛りの目視（ui_charts.md §10 の 1）、
  ギャラリーの形と塗りの目視（ui_components.md §9）。M7 の「スクロールへ届く」は下の項目を直してからもう一度。
  → 2026-09-29（W2 の手直し P1 の後。roadmap §3.9.3）に足す分: **M7 の「スクリプトへ届く」**（P1-2 で直した。通知の取り消し → 開き直すを利用者の操作で）、
  **時刻ホイールを止めて離す**（P1-1。利用者の指で。`[SEED GESTURE] release … rule=… lift_probe_dp=…` で R2 の閾値の余裕を見る）、**予測型の戻るの残り**
  （ui_navigation.md §12 の予測型の戻るの 5・6: 3 ボタンのナビゲーション・すばやく 2 回・途中で画面が切り替わる・回転。キーボードが出ているときの優先順）。
  予測型の戻るの 1〜4（根・積んだ画面・ダイアログ）は利用者の指で確かめた。
  → 2026-09-30（W2 の手直し P2 の後の実機の回。roadmap §3.9.4）: **時刻ホイールを止めて離す**は利用者の指で確かめた（勝手に回ることは無し。R2 の余裕は
  下の「W2 の手直し P1 の残り」の (2)）。P2 のフルスワイプの削除・循環なしの時刻ホイールの端・グラフの日付線のハンドル・ダイアログの余白と出入りの動きも
  利用者の指で確かめた（どれも「問題ない」）。ここに残るのは上の M7 の「スクリプトへ届く」・予測型の戻るの 5・6・時刻ホイールの触感（タップ時のバイブをオンにして）・
  3 ボタンのナビゲーションの戻る・キーボードが出ているときの戻る・回転・シートのつまみ・覆いとトーストの払い・慣性の途中のタップ・1,000 行・グラフの縁と目盛りの目視
  （変わらず未実施）。
- [x] **行を大きく左へ払ったらそのまま削除（フルスワイプ）。利用者の要望** — 2026-09-29（W2 の手触りの確認。roadmap §3.9.2）。利用者の言葉:「大きく左にスワイプしたら
  そのまま削除されるといい。削除されるスワイプの閾値を越した瞬間に『削除』のテキストを右から左側へ補間で移動させ、端末を単発のバイブレーションで揺らすと、手触りとして
  分かりやすい」（iOS のメールなどのフルスワイプの削除に当たる）。案: `SwipeActions` にフルスワイプ（既定はオフ）を足す。閾値は行の幅の 0.6 程度（操作のボタンの幅より先。
  戻す側は 0.55 などの幅を持たせて行き来でばたつかない）。越えた瞬間に操作の面を行の幅いっぱいへ伸ばし、「削除」の文字を右端から左端（行の左の余白の位置）へ 0.15 秒
  程度（テーマの motion トークン）で補間して、触感は 1 回（`Haptics.Tap`。強めにするなら `Haptics.Vibrate(20)` 程度）。閾値の手前へ戻したら文字を右端へ同じ時間で戻す
  （触感はなし、または弱い 1 回）。離したとき閾値の先なら行を左へ流し切り（0.2 秒）、高さを畳んでから（0.2 秒）削除のイベント（例 `FullSwipeCommitted`）、手前なら今の
  開く・閉じるの判定のまま。一覧（`ListView`）は畳み終わってからデータを消す順にする。関連: `scripting/src/Api/UI/SwipeActions.cs`・`SwipeMath.cs`・`SwipeGroup.cs`、
  docs/ui_scroll_list.md。
  → **2026-09-29 に W2 の手直し P2-3 で対応**（docs/ui_scroll_list.md §7・§7.1・§10・§11 の 8、docs/scripting_api.md／.html §7.15、docs/ui_theme.md §8・§9）:
  `SwipeActions` にフルスワイプ（`FullSwipe`。既定オフ）。ドラッグは行の幅いっぱいまで、行の幅 × `ratio.swipe_full`（0.6）で構え（触感 1 回。`ArmHaptic` 既定 Tap・
  Vibrate は 20 ms）、× `ratio.swipe_full_cancel`（0.55）を下回ると解く（触感なしが既定。`DisarmHaptic`）。「削除」の文字（`FullSwipeLabel`）は構えている間は Front の
  後ろの端 ＋ `space.l` に付いて指と一緒に動き、構える・解くで `motion.swipe_full`（0.15 秒）の補間。構えたまま離すと Front を行の幅の外まで `motion.swipe_dismiss`
  （0.2 秒）で流し切って `FullSwiped`（閉じる向きへ速く払ったときは確定しない）、開いた行のボタンのタップからは `Commit()`。確定の後は `Reset` まで受けない。
  純粋な計算は `SwipeMath`（構え・解く・離したときの決め方・文字の置き場・ドラッグの範囲・畳む長さ）と新しい `SwipeModel`（状態の機械）。ずらし方は Front・文字に
  `CanvasLayoutItem` があれば `Translate`、無ければ `Position`。`ListView` は並び（行の長さ）を変えたら付いたままの行も置き直すようにした。ギャラリーの一覧を見本にした
  （`list_row.actor` を親の幅に合わせるスワイプの行に・`List` に CanvasComponent・行のスクリプト `UiGalleryListRow.cs`・持ち主 `UiGallerySections` が高さを
  `motion.swipe_collapse`〈0.2 秒〉で畳んでからデータを消す・見本の触感は Vibrate・`gallery,haptic`・`gallery,list`・`[UI] gallery: swipe …` のログ）。
  トークン 6 つ（上の 5 つ＋ `color.on_error`）。単体テスト 12 件（UiComponentsTests の SwipeTests.cs）、PC で構え → 流し切り → 畳み → 件数 99・50% へ戻して開く・
  タップで削除 → 98・触感の回数（vibrate 1・2 回目、tap 1 回目）を確かめた。実機（Pixel 6a）の手触りは未確認。
  → 2026-09-30 に実機（Pixel 6a・develop の .so）で利用者の指で確かめた（roadmap §3.9.4）: 構えた瞬間の文字の動き・震え（Vibrate 20 ms）・流し切りと詰まり・
  タップでの削除とも「問題ない」。ログ: 構え 33 回（どれも Vibrate）・確定 31 回（うちタップ 1）→ 件数 100 → 69・解く 3 回・開く 17 回・閉じる 13 回、例外なし。
- [x] **時刻ホイールは 0 の位置で止まる（循環しない）ほうがよい。利用者の要望** — 2026-09-29（roadmap §3.9.2）。利用者の言葉:「回転は止まります。0 の位置で止まらず
  ループしているという事です」（速く払うと 0 を越えて何周も回る）。今の作り（コードで確認）: `WheelPicker` には既に `Looping`（インスペクタ「端をつなげる」・既定 true）が
  あるが、`TimeWheel` が時・分の列へ `looping: true` を固定で渡している（`scripting/src/Api/UI/Widgets/TimeWheel.cs` の `Configure` の呼び出し）。案: `TimeWheel` に循環の
  有無（全体か列ごと）を足して列へ渡す。循環しないときは時 0〜23・分 0〜59（刻みの最後の値）の両端で W2-3 の `CanvasScroll` の端（跳ね返り）に当たって止まる。
  12 時間表記の時の列（12・1〜11）は循環をやめると 11 → 12 の午前/午後の連動が変わるので、循環なしは 24 時間表記から先に入れるなど決める。Wake or Pay の時刻ホイールは
  循環なしを既定にする（W3）。関連: docs/ui_components.md §11.3・§11.10。
  → **2026-09-29 に W2 の手直し P2-2 で対応**（docs/ui_components.md §11.3・§11.6・§11.8・§11.9・§11.10。docs/scripting_api.md／.html §7.17）:
  `TimeWheel` に `Loop`（既定 true。インスペクタ「端をつなげる」）を足し、時・分の列の `Configure` へ `looping: Loop` を渡す（`WheelPicker.Configure` は
  `looping` を直接受け取るので Rust・`WheelLoop`（cycles = 1 の経路）は変えていない）。実行中の切り替え `SetLoop(bool)`（`SetMinuteStep` と同じ流儀。値は保ち、
  列を作り直して今の値の行へ黙って置く）。**12 時間表記の時の列は Loop の有無によらず 24 行のまま**にした（23 ↔ 0 の継ぎ目が無くなるだけ・11 ↔ 12 の午前/午後の
  連動は変えていない。`TimeWheelMath` は Loop を知らない）。単体テスト `UiComponentsTests` 103/103（既存 101 ＋新規 2 件。循環なしの `WheelLoop.StepItem` の
  端・12 時間表記の連動が Loop の有無で変わらないこと）。ギャラリーに循環なしの見本 `TimeNoLoop`（24 時間・5 分刻み。`templates/ui/scenes/ui_gallery.scene`・
  `UiGalleryDemo.cs` の `TimeWheelNames`・`loop,<名前>,on|off` 命令）を追加、後ろの段（一覧以降）を 224 だけ下げ、Page の `content_height` を
  1944 → 2168 にした。PC の確かめ（IPC の入力の注入）: **ギャラリーのページの起動直後の最初のスクロールが数秒不安定**でフリックが隣の列（`Time24`）に
  当たることがあったため、スクロールに頼らない孤立シーンを別に作って確かめ直した（下の発見の項目）。時 0/23・分 0/55（5 分刻み）の端は強いフリックでも越えない
  （`wheelpos` の pos/row/item が払う前後で同じ・撮影でも隣の周の行が出ない）。12 時間表記でも項目 0（午前 12 時）・23（午後 11 時）の端は同じ。
  `SetLoop` の実行中の切り替えは `Time24` で確認: `loop,Time24,off` で 23 時から上へ強い払いをしても 23 時のまま、`loop,Time24,on` に戻すと同じ強さの払いで
  0 時をまたいで 3 周ぶん回った。**利用者の指での確かめは未実施**（PC のログ・撮影のみ）。Wake or Pay は W3 で `Loop = false`・24 時間表記に固定する
  （別リポジトリなのでこの件では触っていない）。
  → 2026-09-29〜30（P2 の検証・実機の回。roadmap §3.9.4）: 12 時間表記・循環なしの 11 ↔ 12 の連動を PC で時の列を 1 行ぶんゆっくり引いて確かめた（11:00 午前 →
  12:00 午後・午前/午後の列が午後へ・逆も）。`SetLoop` は、午前/午後の列を指で変えた後（入れ替わった状態）に時の列を置き直すと次に時の列を動かしたとき時が 12 ずれる
  （コードを読んだ推論）ので、置き直すときに連動の状態も「入れ替わりなし」へそろえた（`SetUse24Hour`・`SetMinuteStep` と同じ）。実機（Pixel 6a）で利用者の指で
  確かめた: 循環なしの端（時 0・23、分 0・55）の止まり方は「自然」。
- [ ] **Wake or Pay の時刻は 24 時間表記だけにする（午前/午後の列を出さない）。アプリの決定** — 2026-09-29（利用者の指摘「午前、午後の表示は不要」。roadmap §3.9.2）。
  W3 の画面で `TimeWheel` を 24 時間表記に固定する。エンジンの 12 時間表記（午前/午後の列・連動）は汎用の部品として残す。
- [x] **指を離す瞬間の小さな飛びで、最速に近いフリックが起きる（時刻ホイール・Android）** — 2026-09-29（利用者の指摘「細かい指の動きに反応してしまっているのか、
  指を離す際にダイアルが想定していない方向や量で動いてしまうことがある」。roadmap §3.9.2 の (c)）。ログ（`[SEED TOUCH FRAME]` と見本の `[UI] time`）: ホイールの上で
  離した 94 回のうち、指をほぼ止めてから離した 25 回（最後の 3 フレームの移動がどれも 1 dp 未満）の 3 回で、離した直後に 1 フレーム 3〜4 行（約 5,800〜7,700 dp/秒）で
  流れ始めて 98〜109 分進んだ。3 回とも、離した瞬間の指の位置の飛び（上へ 1.5〜4.2 dp）と同じ向きで、2 回はそれまでの指の向きと逆。見立て（推論）: Android では速度の
  標本の時刻が受け取った時刻（docs/input_gestures.md §5。winit が MotionEvent の時刻と履歴の標本を渡さない）なので、離す直前の最後の移動が前の標本と数 ms 以内に
  並ぶと、数 dp の飛びでも最小二乗の傾きが数千 dp/秒になり上限（8,000）近くまで出る。案: (1) 根本は MotionEvent の eventTime と履歴の標本を使う（GameActivity の入力を
  JNI で読む・winit に手を入れる）、(2) 速度の推定で前の標本との間隔が短すぎる標本（例 4 ms 未満）をまとめる／間隔の下限を 1 フレームにする、(3) 離した速度を「最後の
  100 ms の移動量 ÷ 時間」の数倍で切り詰め、最後の標本が向きを反転していれば捨てる、(4) ホイールでは小さい離しの速度（例 300 dp/秒未満）を 0 にして最寄りの行へ寄せる
  だけにする。確かめ方: 今回の `[SEED TOUCH FRAME]` の 3 回を標本の時刻の揺れつきで再生する単体テスト。関連: `runtime/src/engine/core/input/gesture/velocity.rs`・
  `pointer_track.rs`・`arena.rs`（`on_up`）・`recognizers/fling.rs`、作業フォルダ `tmp/w2_feel/release_jitter.py`。
  → **2026-09-29 に W2 の手直し P1-1 で直した**（roadmap §3.9.3）: (1) の根本（MotionEvent の時刻と履歴を `processMotionEvent` で控えて JNI で突き合わせる）と、
  速度の推定の頑健化 R1〜R4（止まっていた・持ち上げの揺れ・幅の短い推定・間隔の短い標本。docs/input_gestures.md §5.1）。問題の 3 回の再生は速度 0（規則が無いと 8,000 dp/秒）。
  実機の `adb input` で、記録の時刻が MotionEvent の時刻（差 62〜203 ns）・止めて離すと `rule=stopped` を確かめた。**利用者の指で時刻ホイールを止めて離す確かめは未実施**
  （上の「W2 の手直し P1 の残り」の (2)。R2 の閾値の余裕を `lift_probe_dp` で見る）。(4) のホイールだけの小さな速度の切り捨ては入れていない（P2 で要るか決める）。
  → 2026-09-30 に利用者の指で確かめた（W2 の手直し P2 の後の実機の回。roadmap §3.9.4）: 時刻ホイールで指を止めてから離すを 10 回ほど繰り返してもらい、
  「勝手に回ることは無し」。(4) の切り捨ては要らなかったので入れない（R2 の余裕の数は「W2 の手直し P1 の残り」の (2)）。
- [x] **予測型の戻るのアニメーションが出ない** — 2026-09-29（利用者「戻るアクションで、他のアプリのようにアプリ自体が縮小して後ろが見えるようにはならない」。
  roadmap §3.9.2 の (d)）。原因（コードとログで確認）: マニフェストの `android:enableOnBackInvokedCallback="false"`（Android 16 以降で KEYCODE_BACK を Escape へ写すための
  一時的な回避。`runtime/android/app/src/main/AndroidManifest.xml`・docs/android.md §14.5・§24）で、戻るは全部アプリのコールバック（`CoreBackPreview … backType=4`）になり、
  根でも SEED が自分で `moveTaskToBack` するのでシステムの「縮んでホームが見える」動きが使われない。案: (a) Android 13+ の `OnBackInvokedCallback`（14+ は
  `OnBackAnimationCallback` の進み具合と指の位置）を Java 側で登録し、進み具合を SEED.UI の `BackDispatcher` へ流して、段ごとに自前でのぞき・縮みを描く（画面のスタックの
  pop のプレビュー、ダイアログ・シートの縮み。確定で今の Dispatch・取り消しで戻す）、(b) 段が空（根）のときはコールバックを外してシステムの既定（ホームへの縮小）に任せる、
  (c) Escape への写しが要るゲームと予測型を使う UI アプリをプロジェクト設定で選べるようにする。docs/ui_navigation.md §13 の「予測型の戻るは扱わない」を見直す。
  → **2026-09-29 に W2 の手直し P1-3 で (a)(b)(c) を入れた**（roadmap §3.9.3・docs/android.md §25.18・docs/ui_navigation.md §5.1）: プロジェクト設定 `android.predictive_back: true`
  （既定 false＝WarashibeFishing などは従来どおり）、`OnBackInvokedCallback`／`OnBackAnimationCallback`・合成の KEYCODE_BACK → Escape、`BackDispatcher.WouldHandle` が
  受ける層の有無を知らせて根ではシステムへ渡す（API 36 以上は `moveTaskToBackCallback`）、受ける層があれば進み具合で画面・札・板を縮める（実行中の倍率
  `CanvasLayoutItem.VisualScale`）。**利用者の指で確かめた**: 根で「アプリ全体が縮んで後ろにホーム」「離すとホームへ」、積んだ画面で「縮んで後ろにホームのタブ」
  「取り消しで元へ」「離すと縮んだ姿勢から下りる」。残りは上の「W2 の手直し P1 の残り」の (9)。
- [x] **ダイアログの上下の余白が 7.6 dp しかない（札が中身の高さに伸びない・本文の行の見積もりが多い）** — 2026-09-29（利用者「ダイアログのテキストやボタンのレイアウトが
  やたら端が狭くて変な感じ。これがデフォルトならもう少し余裕を持たせてほしい」。roadmap §3.9.2 の (a)）。実測（撮影を画素で測った）: 札 311.6 × 160.0 dp、左の余白 24.0 dp
  （`size.dialog_padding` は既に Material 3 の 24 dp）。札の背景がプレハブの高さ 160 dp のままで（`Dialog.Layout` は札の `CanvasLayoutItem.PreferredSize` の高さに 0 を書くだけ）、
  中身 144.8 dp（題 28＋本文 44.8＋ボタン 40＋間隔 16 × 2）が中央へ寄って上下の余白が 7.6 dp になった。本文は実際 1 行（227 dp）なのに見積もり（全角 1 em）が 2 行で、
  本文とボタンの間に 22.4 dp の空きも出る。トークンの値の問題ではない。案: 札の背景（Sprite・Canvas）の高さを中身から決める（余白 × 2 ＋ 題 ＋ 間隔 ＋ 本文 ＋ 間隔 ＋ ボタン）、
  本文の高さを `Text.Measure`（W2-6c）で測る、あふれたときにスタックが中央へ寄せる動きを確かめる。直した後に Material 3 のダイアログの間隔（題と本文・本文とボタンの間。
  値は記憶によるので取り直す）と見比べ、必要なら間隔のトークンを足す。関連: `scripting/src/Api/UI/Navigation/Dialog.cs`（`Layout`・`SetText`）・`DialogLayout`・
  `templates/ui/prefabs/dialog.actor`（Card の高さ 160）。
  → **2026-09-29 に W2 の手直し P2-1 で直した**（docs/ui_navigation.md §3.2・§11）: 札の高さ = 余白 ＋ 題 ＋ 間隔 ＋ 本文 ＋ 間隔 ＋ ボタンの行 ＋ 余白
  （出さない区画とその間隔は数えない。`Navigation/Model/DialogMetrics.cs`）を C# で求め、札の `CanvasLayoutItem.PreferredSize` と背景の `Sprite.Size` の両方へ書く
  （エンジンは変えていない）。間隔は区画ごとなので札の CanvasStack の間隔を 0 にし、上の区画の枠の高さに足す。間隔のトークン `size.dialog_title_gap` 16・
  `size.dialog_actions_gap` 24 を足した（Flutter master の AlertDialog〈Material 3〉の contentPadding の上 16・下 24 をソースで確かめた）。本文の見積もりは
  エンジンの折り返しの規則（`TextWrapEstimate`）と組み込みの書体の送り幅の表（`BuiltInFontAdvance`。全角 0.7168 em）に合わせ、題と本文の Text の行間を
  見積もりの行の高さ 1.4 にした（例の本文は 1 行）。PC の撮影で、題 ＋ 本文 1 行 ＋ 3 ボタンの札は 312 × 178.4、上の余白（札の上端 → 題の字面）31.25 dp
  （計算 31.30）・下の余白 24.06 dp（計算 24）。グラフの吹き出しも同じ見積もりなので文字に合って小さくなった（字面は収まる）。
- [x] **ダイアログの出入りで、文字とボタンが札の左上を中心に縮む（背景は中心で縮む）** — 2026-09-29（利用者「ダイアログを閉じるときに、テキストだけ左上を原点にして消えていく
  感じがあって違和感。ダイアログの背景と SRT が一体化するように」。roadmap §3.9.2 の (b)）。実測: `motion.dialog` を 3 秒にした作業フォルダだけのテーマで閉じる途中を撮影し、
  倍率 0.933・0.914・0.900 の 3 枚で、題の左上と OK の文字の右下が「札の左上を中心に倍率を掛けた位置」と 1〜5 px で一致（中心を中心にした位置とは 10〜16 dp ずれる）。
  見立て（コードを読んだ推論）: 札の `CanvasTransform.Pivot` は (0.5, 0.5)（`Dialog.CardPivot`）で背景のスプライトはそこで縮むが、札は入れ子のキャンバス（`CanvasComponent`
  の pivot は [0, 0]）で、子は左上を原点に置かれて倍率が掛かる。案: (a) エンジンで、入れ子のキャンバスの子へ親の倍率・回転を掛けるときも親の変形の中心を通す（背景と
  同じ中心で動く）、(b) 部品で札のキャンバスの pivot もそろえる／中身を中心に置いた入れ物ごと縮める。確かめ方は今回と同じ（遅いテーマで撮って中心からの倍率に乗るか）。
  関連: `Dialog.cs`（`CardPivot`・`ApplyOpen`）・`NavNode.SetScale`・キャンバスのレイアウトの表（子への変形の掛け方）。
  → **2026-09-29 に W2 の手直し P2-1 で直した**（エンジンは変えていない）: 出入りの倍率を、予測型の戻るのプレビュー（P1-3）と同じ実行中の見た目の倍率
  `CanvasLayoutItem.VisualScale`（矩形の中心の周りに部分木ごと縮む）へ替えた。同じ欄なので `Dialog` が「開き具合の倍率 × プレビューの倍率」を書く
  （`DialogMetrics.CardScale`。確定した戻るの後はプレビューの倍率を保ったまま出る）。`NavNode.SetScale`・`Dialog.CardPivot` は消した（シート・覆い・トーストは
  `CanvasTransform.Scale` を出入りに使っていない）。PC で `motion.dialog` を 3 秒・直線にした作業フォルダだけのテーマで閉じる途中を 23 枚撮り、札の中心は
  (270, 600) から 0.05 px 以内、題の字面の左上と OK の文字の右下は中心の周りに倍率を掛けた位置から 1.6 px 以内（倍率 0.900 の枚で、以前の「左上へ寄る」位置とは
  15.6 / 8.9 px 違う）。
- [x] **グラフが親の幅に合わせて伸び縮みしない（見本で右が見切れる）** — 2026-09-29（利用者「グラフ全体が画面内に収まっておらず、右側で見切れている」。roadmap §3.9.2）。
  見本のシーンが 540 dp 固定なのは上の項目「ギャラリー・グラフの見本が 540×1200 dp 固定」だが、グラフの部品そのものも大きさをノードの Sprite の幅・高さから読む
  （`ChartView.ReadSize`。コードで確認。docs/ui_charts.md §11 の持ち越し）ので、コンテナ・アンカーで伸ばしても追従しない。案: レイアウトが決めた大きさをスクリプトから
  読める口（`CanvasLayoutTable` の結果）を足して `ReadSize` をそちらへ替える、または「親の幅に合わせる」欄でグラフが自分の Sprite の幅を書き直す。見本は画面の幅に合わせて作り直す。
  → 2026-09-29（W2 の手直し P1-4）: **読む口を足した**（`CanvasTransform.HasLayout`・`LayoutSize`〈キャンバスの単位〉・`LayoutRect`〈画面の画素の外接矩形〉。前のフレームの
  描画の表の値＝1 フレーム遅れ。docs/scripting_api.md の CanvasTransform・docs/canvas_camera_rework.md §6.8）。PC で見本の WakeWeek が 2 フレーム目から
  `size=(508,170) rect=(16,76,508,170)` を返すことを確かめた。**`ChartView.ReadSize` の差し替えと見本の作り直しは P2**（最初のフレームは HasLayout=false なので
  Sprite の値で作って次のフレームで作り直す・コンテナが大きさを変えたフレームは背景と中身が 1 フレームずれる、に注意）。
  → 2026-09-29 W2 の手直し P2-4 で `ReadSize` を「`HasLayout` なら `LayoutSize`、無ければ Sprite」に替えた（`ChartSizing`。docs/ui_charts.md §2.1）。最初のフレームは
  **レイアウトを読めるまで描かない**（最大 3 フレーム。超えたら Sprite の大きさで描く＝3D ワールドキャンバスの下でも出る）ことにした（Sprite の大きさで 1 フレーム描くと
  伸ばされたグラフの中身が背景からはみ出すため）。作業フォルダの写しのシーン（縦の CanvasStack〈cross_align stretch〉と fill_width の下。Sprite の幅はわざと 300）で、
  窓 540 は 508 dp・411 dp の模擬は 379 dp で描かれ、右が切れないことを撮影で確かめた。**見本のシーン（ページが 540 dp 固定）の作り直しは P2-5**（残る）。
  → **2026-09-29 に W2 の手直し P2-5 で見本も作り直した**（上の「ギャラリー・グラフの見本…が 540×1200 dp 固定」）: グラフの見本の 5 つとギャラリーの 2 つが
  親の幅（窓 540 で 508 dp・411 dp の模擬で 379 dp・360 dp で 328 dp）で描かれ、右の端は中身の右の端（411 dp で 518.98 px）で切れない。
- [x] **日付線（選んだ点の縦の線）の下に丸いハンドルを付け、ハンドルで左右へ動かして選びを変える。利用者の要望** — 2026-09-29（roadmap §3.9.2）。利用者の言葉:
  「日付線の下部に丸いハンドルを用意して、そこを押して左右に動かすと日付線だけを動かせるといい」。案: 選んだ点の縦の線の下端（横の軸の上）に丸いハンドル（見た目 16〜20 dp・
  当たりは 48 dp 以上＝`min_hit_size_dp`）。ハンドルの上で押したドラッグはパン・ピンチより先に勝つ（ハンドルの上だけ先にドラッグを主張する）。動かす間は最寄りの点（棒は列）へ
  吸い付き、吹き出しが付いてくる。点が変わるたびに触感 1 回（`Haptics.Tap`。ホイールと同じく 1 フレーム 1 回まで）。面の端へ寄せたら横へ送る（自動のパン）。離したら選びは残す。
  関連: `scripting/src/Api/UI/Charts/ChartView.cs`（`SelectAtLocal`・ジェスチャー）・`LineChart.cs`・`BarChart.cs`。
  → **2026-09-29 に W2 の手直し P2-4 で折れ線に入れた**（docs/ui_charts.md §6.1）: プレハブ `line_chart.actor` と見本の折れ線 3 つ（中身の写し）に子 `Handle`
  （楕円・横のドラッグだけ・タップなし・当たり 48 dp・`GestureRelay`）。丸の中心の X = 日付線・丸の下端 = 面の下の縁、直径 `size.chart_handle` 18・縁 `size.chart_handle_border` 2
  （面の色）・塗りは選んだ系列の色・吹き出しと同じ手前のレイヤー。指の X は「ハンドルの左上 ＋ イベントの `LocalPosition`」（dp・Scale・VisualScale の下でもグラフの単位）で、
  選んでいる系列の値のある点のうち見えている範囲で最寄りの点へ吸い付く（`ChartHit.NearestValuedX`。面の外の指は端の点で止まる）。点が変わるたびに `PointSelected`・触感
  （`HandleHaptic`。1 フレーム 1 回まで）。離しても取り消されても選びは残す。PC で、30 日の折れ線を右へ 4 点・左へ 5 点引くと 1 つずつ 9 回変わり触感 9 回（窓 540・411 dp の模擬・
  見た目の倍率 0.8 のどれでも）、4 倍に拡大した全期間でハンドルのドラッグはパンにならずハンドルの外はパンになる、ハンドルの上から縦に引くとページがスクロールする、を確かめた。
  **自動のパンと棒グラフのハンドルは入れていない**（下の「W2 の手直し P2-4 の残り」）。
- [x] **起動し直した直後の `platform.permission_changed` がスクリプトに届かない（M7 の実機）** — 2026-09-29（roadmap §3.9.2・docs/android.md §25.14.8）。通知をオフにされて
  Android がアプリを止め、開き直した最初の onResume で `granted → denied` は出て（前回の状態の保存が効いた）エンジンも受け取ったが、見本のシーンの試験用スクリプト
  （`OnStart` で `PlatformEvents.OnEvent`・`this.On`）には届かなかった（受け手の `OnStart` が知らせの 0.86 秒後）。§25.14.5 の「後から `On` するスクリプトは受け取れないことが
  ある」が実機で起きた。案: (a) 起動から最初のスクリプトのフレームまでに届いたプラットフォームの知らせを取っておき、シーンのスクリプトの `OnStart` が済んだ後に配る、
  (b) 最後の変化を読み直せる口（例: 種類ごとの最後の `PermissionChanged`）を足す、(c) それまでは「起動時は `Permissions.Check` で確かめる」を Wake or Pay の起動の手順に入れる。
  関連: プラットフォームの知らせの配り方（`runtime/src/engine/platform/bridge/event_queue.rs`・`runtime/src/engine/core/scripting/platform_bridge.rs`・
  `scripting/src/Api/Platform/PlatformEvents.cs`）・docs/scripting_api.md の `PermissionChangedEvent`。
  → **2026-09-29 に W2 の手直し P1-2 で (a) を入れた**（roadmap §3.9.3・docs/android.md §25.14.5）: 最初のシーンのスクリプトの OnStart が済むまでエンジンは基盤の箱から
  取り出さず、OnStart の次のフレームの BeginFrame でまとめて配る（Play の区切りでは従来どおり捨てる）。実機で、起動の直後に積んだ試験イベントと `platform.connected` が
  見本のスクリプトへ届くことを確かめた（変更前の APK は 2 件とも届かなかった）。**M7 そのもの（通知の許可の取り消し → 開き直す）の再確認は未実施**（利用者の操作が要る。
  「W2 の実機の確認の残り（2026-09-29 の後）」の M7 の「スクリプトへ届く」）。
- [ ] **ギャラリーの見本: デバッグの命令で明暗の選び方を変えても帯のセグメントの表示が変わらない** — 2026-09-29（W2 の手触りの確認の準備で発見。roadmap §3.9.2）。
  `SCRIPT_DEBUG:theme,mode,system` で選び方は System になる（`[UI] theme: mode System → Dark`）が、テーマの帯の「テーマ・端末・明・暗」の表示は「テーマ」のまま。
  利用者の指の操作では起きない。案: `UiGalleryThemeBar` の命令の処理でセグメントの選びも（知らせずに）そろえる。関連: `templates/ui/scripts/UiGalleryThemeBar.cs`。
- [ ] **W2 の手直し P1 の残り（2026-09-29。roadmap §3.9.3）** — P1（タッチの時刻と離しの速度・起動直後のイベントの保持・予測型の戻る・レイアウトの矩形）で
  見つけた・残したもの。直していない。
  (1) **9 本目の指で winit が panic しうる（推論・未実行）**: GameActivity の glue は指を 8 本に切る（GameActivityEvents.cpp の上限）のに、winit 0.30.13 は
  action の指の添字（8 以上になりうる）で `pointer_at_index` を呼ぶ（`platform_impl/android/mod.rs` を読んだ）。P1-1 の控え（`TouchTimeline`）は glue に合わせて
  8 本まで。実機で 9 本指を当てて確かめ、要るなら winit を上げるかパッチ。
  (2) **離しの速度の R2（持ち上げの揺れ）の閾値が、実機の「ほぼ止めた指」に近い**: 記録（roadmap §3.9.2 (c)）の止めた指は 12〜48 dp/秒で、R2 の閾値は
  最小のフリックの速度 50 dp/秒（`min_fling_velocity_dp`）。時刻ホイールを利用者の指で止めて離してもらい、`[SEED GESTURE] release … lift_probe_dp=` で余裕を見て決める
  （`velocity_lift_off_ms` などは project_settings.json の `"gestures"` で変えられる）。止めた時間が 40 ms より短いと R2 の区間に払いが入り、小さな逆向きのフリックが残りうる。
  → 2026-09-30（W2 の手直し P2 の後の実機の回。roadmap §3.9.4）: 利用者に時刻ホイールを止めて離してもらった依頼の間の離し 76 回のうち、速度を 0 にしたのは
  8 回（`rule=stopped` 1・`lift_off` 7）で、その `lift_probe_dp` は 0・0・14・14・30・43・47・50（閾値 50 ちょうどが 1 回）。フリックのまま残った離しの最小は
  70 dp/秒（`lift_probe_dp` 70）、200 dp/秒未満は 6 回（70〜182）。利用者は「止めて離して勝手に回ることは無し」。**余裕は小さい**（止めた指の揺れと遅い払いが
  50 の両側 20 dp/秒以内にいる）が、今の値で困っていないので変えない。上げるなら 60〜65（遅い払いの 70 を残す）。`tmp/w2_fix2/device/logs/req2_release_analysis.txt`。
  (3) **R3（幅の短すぎる推定を捨てる）は DragStart・DragUpdate の速度にも効く**: ドラッグの最初の 1 フレームの速度（`CanvasScroll.Velocity`・`GestureEvent.Velocity`）が 0 になる
  （離しの速度・フリックは影響なし。C# の部品は DragEnd と Fling の速度だけを読むことを確かめた）。見た目への影響は無い見込み。
  (4) **`CanvasTransform.ScreenPosition` はスクロールの平行移動を足していないように読める（推論・未実行）**: `collect_actor2d_contexts` を読んだ。スクロールしても
  ScreenPosition は動かず、`LayoutRect`（P1-4）だけ動く可能性。スクロールの中のノードで両方をログへ出して確かめる。
  (5) **Scale ≠ 1 かつ pivot ≠ 0 のキャンバスのノードで、子の箱の原点とキャンバスの領域がずれるように読める（推論・未実行）**（P1-4 の調べで読んだ）。
  (6) **`LayoutSize` の索引は、スクリプトが読むフレームごとに表の行数に比例して作り直す**（資源はフレームごとに差し替わるため。P2 でグラフが毎フレーム読むと
  行の多いシーンで効く。要るなら表と一緒に索引も持ち越す）。
  → 2026-09-29 W2 の手直し P2-4 で測った（PC・`runtime/target/debug`〈最適化なし〉・ギャラリー・何もしない 5 秒の `PROFILE_DUMP`。docs/ui_charts.md §3.1 の末尾）:
  グラフが毎フレーム `HasLayout`・`LayoutSize` を読むと `Update/LineChart` が 0.074 → 0.259 ms（+0.185。フレームで最初に読むので索引を作る）、
  `Update/BarChart` は 0.035 → 0.045 ms（引くだけ）。索引を作る最初の読み出しは 149〜270 µs、2 回目は 2〜4 µs。最適化した .so と on_demand の止まっている間を考えて
  **読む頻度は下げなかった**（下げると大きさの変化への追従が遅れる）。消すなら Rust で索引を表と一緒に作って持ち越す（描画の受け渡しで作る。P2 では Rust を触らないので残す）。
  (7) **プロジェクト設定 `android.predictive_back` の欄がエディタの設定ウィンドウに無い**（JSON を手で書く。ウィンドウで保存しても消えない）。
  (8) `BackGestureEvent` が JSON の数の読み取りにセンサーの `SensorJson.GetFloat` を借りている（`PlatformJson` へ移すとよい）。
  (9) 予測型の戻る（P1-3）: 手ぶりの途中でコールバックを外したときの振る舞い（onBackCancelled が来る見込み）・ボタンの戻る（3 ボタンのナビゲーション）で
  `onBackStarted` が来るか・IME が出ているときの優先順は実機で確かめていない（記憶による前提。docs/android.md §25.18）。API 33〜35 でランチャー以外から起動した根は、
  受ける層が無くても自分のコールバックを残す（システムの戻るが finish → プロセスの終了になるおそれがあるため。見た目はホームへ戻る予測アニメーションにならない）。
  (10) エディタの Play でインスペクタから CanvasLayoutItem の欄を書き換える（serde を通す往復）と、Play 中の実行中の欄（`translate`・`visual_scale`）が既定値へ戻る
  （`translate` の従来の振る舞いと同じ）。(11) `scroll_view::translate_frame` は `visual_shift` を更新しないので、スクロールの中の `CanvasSafeArea` はスクロールした位置で
  安全領域を求める（既存）。
- [ ] **W2 の手直し P2-1（ダイアログ）の残り・発見（2026-09-29。W2 の手直し P2 で発見）** — 札の高さ・本文の見積もり・出入りの倍率を直したとき
  （docs/ui_navigation.md §3.2・§11）に見つけた・残したもの。直していない。
  (1) **縮めて描いた文字の細い横線が消えることがある**: 予測型の戻るで 0.9 倍に縮めたダイアログを確定して閉じる途中の撮影（倍率 0.899）で、題の「上」の下の横線
  （大きさ 20 × 0.9 でおよそ 1 画素の太さ）がほぼ描かれなかった（直前の 0.9006 倍の枚では描かれている。作業フォルダ `tmp/w2_fix2/item1/shots/nav/n22_commit_03.png`）。
  見立て（推論・シェーダは読んでいない）: SDF の文字を画素の中心で 1 回だけ読むので、1 画素より細い線が画素の中心の間に落ちると塗られない。動きの一瞬なので実害は小さいが、
  縮めた姿勢で止まる予測型の戻るの手ぶりの間は見え続けうる。関連: `runtime/src/engine/core/font/`（SDF の描き方）。
  (2) **本文が長いと札が画面より高くなる**（Material 3 は本文をスクロールする）・**ボタンが中の幅に入りきらなくても縦に積まない**（Flutter の AlertDialog は
  OverflowBar で縦に積む）。今は `DialogMetrics` が中身の高さをそのまま足す。
  (3) **開いている間にテーマを替えても札の大きさ・間隔・文字の大きさは作り直さない**（`Dialog.Layout` は開くときの 1 回。色・角丸・書体は `ApplyLook` で追従）。
  (4) **Flutter の AlertDialog（Material 3）と違う 2 か所を決める**: 本文の無いときの題 → ボタンは Flutter が 20（titlePadding の下）・SEED は
  `size.dialog_actions_gap` の 24、題の無いときの本文の上は Flutter が 16（contentPadding の上）・SEED は余白の 24。合わせるならトークンを足す。
  (5) **見積もりは組み込みの書体（M PLUS Rounded 1c Regular）の送り幅の表**なので、テーマの `font.family` でほかの書体を当てると本文の行の数・ボタンと吹き出しの
  幅がずれる。本文の中の埋め込みの記法（`[icon:…]`）は文字のまま数える（大きめ）。`Text.Measure`（W2-6c）で測れば要らなくなる。
  (6) 行頭禁則のぶら下げ（エンジンの規則）で、本文の句読点は中の幅から最大 2 文字はみ出す（ギャラリーの本文「…なる。」の句点が右の余白へ約 11 dp 出る。
  余白 24 の中なので札からは出ない）。(7) 実機（Pixel 6a）では未確認（札の余白・閉じる動きの一体・予測型の戻るとの合成）。
  → 2026-09-30 に実機で利用者の指で確かめた（roadmap §3.9.4。ナビゲーションの見本の 3 ボタンのダイアログ）: 余白・間隔は「自然」、閉じる・開く動きで中身が背景と
  一体で動く（開く 22 回・閉じる 22 回〈Negative 10・Neutral 5・Positive 4・幕や戻るの Dismissed 3〉）。**予測型の戻るとの合成は実機では未確認**（今回の APK は
  `android.predictive_back` なし。PC の模擬で確かめ済み）。
- [ ] **ギャラリーのページの最初のスクロールが数秒不安定（`gallery,scroll` の反映が遅れる）— W2 の手直し P2-2 で発見（2026-09-29）**。
  `TimeNoLoop` の PC の確かめで、シーンを開いて 6 秒ほどの所で `SCRIPT_DEBUG:gallery,scroll,1158` を送ると、直後の撮影ではまだ上端のまま（スクロールしていない）で、
  数秒後にはちゃんと反映されていた（`tmp/w2_fix2/item2/shots/noloop/00_scrolled.png` は上端、同じ回の `A3_minute_low.png` では正しく `TimeNoLoop` が見えている）。
  その反映の途中で送ったフリック（`INPUT_SEQUENCE`）が、意図した `TimeNoLoop` ではなく、まだそこに映っていた `Time24` の列に当たった
  （`Time24` の分がスクリプトで動かしていないのに大きく動いた。`tmp/w2_fix2/item2/run1_output.txt`・`run2_output.txt` のログ）。複数回 `gallery,scroll` を送り直し・
  長く待っても不安定さが残ったことがある一方、Page の `CanvasScroll` を経由しない孤立シーン（スクロールなし）では常に安定していた。見立て（未調査）:
  `templates/ui/scripts/UiGallerySections.cs` の `gallery,scroll` は `CanvasScroll.JumpTo` を直接呼ぶだけなので、Page 側（`content_height` を
  1944 → 2168 に増やした直後・List の 100 行や 2 つのグラフを含む重いシーン）の窓・中身の長さの確定に数フレーム〜数秒かかる何かと関係がありそう
  （`WheelPicker` 自身の `_awaitingContent` に似た待ちが Page の CanvasScroll 側にもある、または起動直後は他の部品の初期化で該当フレームの処理が
  後回しになる、などが推論）。`TimeWheel.Loop` 自体とは無関係（Rust のスクロールの仕組みは変えていない）。確かめ方: シーンを開いてすぐ・数秒待ってからの
  2 通りで `gallery,scroll` を送って毎フレームの位置をログへ出す・`CanvasScroll` の `HasMetrics`/`ContentSize` 相当を読めるようにする。
  関連（存在は確認・中身は未調査）: `runtime/src/engine/components/canvas_scroll_component.rs`・`runtime/src/engine/core/canvas_scroll/`・
  `templates/ui/scripts/UiGallerySections.cs` の `scroll` 命令。
  → W2 の手直し P2-3 の PC の確かめ（2026-09-29。起動 3 回とも起動の 6 秒後に `gallery,scroll,1152` を送った）では、3 回とも最初の送りから 1 秒後の
  `gallery,list` で一覧の窓の画面の矩形が y=460（ページの端 1092 へ収めた位置）になっていて、不安定さは出なかった（`tmp/w2_fix2/item3/run*_output.txt`）。
  → W2 の手直し P2-5 の PC の確かめ（2026-09-29。ページの中身の大きさを `auto` にした後。ギャラリーの起動 5 回とも起動の 6 秒後に `gallery,scroll,<段の名前>`）でも、
  最初の送りから 0.8 秒後の探りの `CanvasScroll.Position` と撮影が毎回その段の位置になっていた（`tmp/w2_fix2/item5/run*_*.txt`）。
- [ ] **W2 の手直し P2-3（フルスワイプで削除）の残り・発見（2026-09-29。W2 の手直し P2 で発見）** — `SwipeActions` のフルスワイプとギャラリーの一覧の見本
  （docs/ui_scroll_list.md §7.1）を作ったときに見つけた・残したもの。直していない。
  (1) **小数の画素の位置の文字の細い横線が欠ける**: 消した行を畳む途中（下の行が小数の画素の位置へ詰まる）の撮影で「アラーム」の「ア」・「7」「5」の上の横線や
  「土」が欠けた（`tmp/w2_fix2/item3/shots/run2/A22_after_release.png`・`A23_…`）。一覧を払った慣性の途中（`shots/run3/G1〜G4_inertia.png`）と、小数の位置
  （726.4）で止まった明るい方の撮影（`shots/run3/H2_light_closed.png`）でも同じように欠けるので、フルスワイプ固有ではない。上の「W2 の手直し P2-1 の残り」の (1)
  （縮めた文字の細い線）と同じ見立て（推論・シェーダは読んでいない: SDF の文字を画素の中心で 1 回だけ読み、1 画素より細い線が画素の中心の間に落ちると塗られない）。
  PC の 1 倍（1 dp = 1 画素）で目立つ。実機（2.625 倍）での見え方は未確認。案: 文字の頂点を画素へそろえる・SDF を面積で読む。
  (2) **部品の中で「後ろの面の文字」が「手前の面」を突き抜ける落とし穴**: 描く順は「ゾーン → レイヤー → 種別（スプライト → 図形 → パーティクル → 文字）」なので、
  同じレイヤーでは木の順が後の Sprite より前の Text が手前に出る。スワイプの行は最初 Actions の「削除」が閉じた行の上に出た（`shots/run1/contact_sheet.png`）ため、
  Front の部分木をレイヤー 1 にした（テストで確かめる）。部品のプレハブを作るたびに踏みうるので、エディタで「同じ部分木の中で木の順とレイヤーの前後が食い違う
  文字」を知らせる・docs の部品の作り方に書く、などを決める。
  (3) **消した行を畳む動きは見本にあり、`ListView` に API が無い**: 見本の `UiGallerySections` が `SetExtentOf`・`SwipeMath.CollapsedExtent`・行の `VisualScale` で
  畳んでからデータを消す。Wake or Pay の目覚ましの一覧（W3）で同じ流れが要るなら部品へ上げる（例 `ListView.CollapseAndRemove(番号, 秒, 消す処理)`）。
  (4) **見本の行の中の文字の枠・区切り線は幅が固定**: 行（Row・Actions・Front）は親の幅に合わせるが、Title・Sub の文字の枠（468）と Divider の幅（468）は
  固定のまま（レイアウトは文字の枠を伸ばさない）。P2-5 で一覧の幅が 500 より狭くなると区切り線が右の端まで出る（一覧の切り抜きで切れる）。
  → **2026-09-29 に W2 の手直し P2-5 で直した**: `list_row.actor` の Front に CanvasComponent（500×56）と縦の CanvasStack（左右の余白 16・上 6・cross_align stretch）を付け、
  Title（高さ 26）・Sub（23）・Divider（1）を行の幅 − 32 に伸ばす（縦の位置は今までどおり 6・32・55）。文字の枠の幅は行のスクリプト `UiGalleryListRow` が
  レイアウトの幅（`LayoutSize.x`）に合わせて書き直す（行ができた後と、一覧の持ち主が一覧の幅の変化を `RefitTextBoxes` で知らせた後だけ）。411 dp の模擬で
  Title・Sub の枠と区切り線が 346.99 dp（行 378.99 − 32）、窓 540 で 476（508 − 32。一覧を払って行を作り足した後・行を消した後も同じ）になった。
  (5) 見本で、流し切りの 0.2 秒の間に一覧を大きく動かして流し切っている行が使い回されると、`Reset` で確定が取り消されて削除されない（実害は小さい）。
  (6) 実機（Pixel 6a）の手触り・振動（見本は `Haptics.Vibrate(20)`）は未確認（docs/ui_scroll_list.md §11 の 8）。
  → 2026-09-30 に実機で利用者の指で確かめた（roadmap §3.9.4）: 文字の動き・震え・流し切り・詰まり・タップでの削除とも「問題ない」。`Haptics.Tap` の選び方
  （端末の「タップ時のバイブ」をオンにした場合）は未確認。
- [ ] **W2 の手直し P2-4（グラフ: 親の幅に合わせる・日付線のハンドル）の残り・発見（2026-09-29。W2 の手直し P2 で発見）** — `ChartView.ReadSize` を `LayoutSize` へ替え、
  折れ線に日付線のハンドルを付けたとき（docs/ui_charts.md §2.1・§6.1・§11）に残した・見つけたもの。直していない。
  (1) **ハンドルを面の端へ寄せても自動でパンしない**（見えている範囲の端の点で止まる。利用者の案の「面の端へ寄せたら横へ送る」）。入れるなら、指が面の端から一定の幅の中に
  いる間、端からの深さに比例した速さで `Viewport.PanTo` し、吸い付きを毎フレームやり直す（指が止まっていても動くので `Redraw.Request` を続ける）。
  (2) **棒グラフ（`BarChart`）のハンドルは無い**。`ChartView.HandleAnchor`（列の中心の X と色）・`SelectNearestX`（`ChartHit.NearestX` か `SlotAt` で列へ吸い付き、
  `BarSelected`）を上書きし、`bar_chart.actor` と見本の棒（中身の写し）に `Handle` を足せば動く。横の棒（`Orientation = Horizontal`）は列の軸が縦なので、置き場
  （面の左の縁）とドラッグの軸（縦）を分ける必要がある。
  (3) **見本の WakeHistory の ± のボタンは固定の位置（x 424・468）**。グラフを画面の幅に合わせて細くすると右の外へ出る（P2-4 の確かめの写しでは 250・294 へずらした）。
  P2-5 の見本の作り直しで、右の端に寄せる置き方（グラフの右の端からの位置）を決める。グラフの子の anchor が「伸ばされた大きさ」に付いてくるかは確かめていない（推論）。
  → **2026-09-29 に W2 の手直し P2-5 で直した**（部品のコードは変えていない）: ± を anchor x 1・位置 x −84・−40（今の 424・468 を 508 の幅の右の端から数えた値）に。
  コンテナが置いたグラフの子の anchor は伸ばされた矩形に付く（`resolve_in_rect` が子のアンカーの基準を矩形にする）ことを PC で確かめた: 411 dp の模擬で
  + の右の端 513.72 px ＝ グラフの右の端 518.98 px − 4 dp、窓 540 で − 440〜476・+ 484〜520 px（変更前と同じ位置）。± のタップで倍率 1 → 2 → 4 → 2 → 1。
  (4) コンテナが大きさを変えたフレームは背景と中身が 1 フレームずれる（`LayoutSize` が前のフレームの描画の値のため。画面の回転の直後など）。消すには、レイアウトの表を
  スクリプトのフェーズより前に作る・グラフの中身をレイアウトの子（Sprite の fill）にするなどエンジンの側の変更が要る。
  (5) 最初のフレームは描かない（レイアウトを待つ）ので、グラフは従来より 1 フレーム遅れて出る。3D ワールドキャンバスの下では最初の 3 フレーム出ない。
  (6) ハンドルを押しても大きくならない（押した見た目なし。スライダのつまみは `size.slider_thumb_pressed` で大きくなる）。右の端の点ではハンドルの丸が面の右の余白（8 dp）から
  1 dp はみ出す（中心を日付線に合わせたため）。
  (7) 実機（Pixel 6a）は未確認: 手触り（18 dp の丸・48 dp の当たり・面の下の縁の置き場）・触感・2 本目の指でピンチしたときの取り消し（アリーナの規則からの推論では
  `handle end (canceled)` になり選びは残る）・縦のスクロールとの取り合い（docs/ui_charts.md §10 の 6）。
  → 2026-09-30 に実機で利用者の指で確かめた（roadmap §3.9.4。ギャラリーの折れ線〈Interactive = false・倍率 1〉）: ハンドルの見た目・位置・吸い付き・吹き出しの追従・
  縦に引いたときのページのスクロールとも「ok」。ログ: ハンドルのドラッグ 4 回・選びの変化 417 回（添字の差 1 が 268 回・2 が 133 回・3〜5 が 11 回＝速く動かすと
  点を飛ばして最寄りへ）・添字 0〜29 の端で止まる。**残り**: 触感（`Haptics.Tap`。端末のタップ時のバイブがオフのため）・ズームした後のパンとの取り合い
  （PC の A_540 では確かめた）・2 本目の指のピンチの取り消し。
  (8) PC の確かめの撮影で、ハンドルの丸の縁（面の色 2 dp）と横軸の線が重なる所の見え方は問題なかったが、明るいテーマ（面が白）では縁が背景に溶けて丸が少し小さく見える
  （撮影していない。推論）。
- [ ] **W2 の手直し P2-5（見本の画面の幅）の残り・発見（2026-09-29。W2 の手直し P2 で発見）** — ギャラリー・グラフの見本を dp の画面いっぱい＋安全領域に
  組み直したとき（docs/ui_theme.md §9・docs/ui_charts.md §8）に残した・見つけたもの。直していない。
  (1) **親に合わせる（fill_width）＋中身の高さに合わせる（fit_height）コンテナは、主軸の箱を「幅 0 で測った高さ」で並べ、中身を 2 回測る**:
  `pass.rs` の `fill_parent_slot` は合わせない軸（縦）を `natural_size`（何も決めない測り方）で決め、CanvasComponent 1×1 のコンテナは幅 0 の箱で子を並べて測る
  （折り返しは 1 行に 1 つ）。その高さの矩形の中で子を並べてから fit で中身の高さへ縮める（コードを読んだ）。主軸の揃えが先頭（`start`）で伸ばす子が無ければ
  正しく置かれるが、`center`・`end` の揃えや flex の子は「幅 0 の高さ」を基準に置かれうる（推論・未実行）。測る手間は PC で測った: 最初に見本の Content を
  この作り（`nav_list.actor` の Content と同じ）にしたときより、窓（Page）を縦の Stack にして幅を渡す今の作りの方が、debug の SEED.exe・窓 540・何もしない
  5 秒の `PROFILE_DUMP` で「UI/2D スクリーン座標収集」が 4.32 → 3.81 ms、フレームが 16.40 → 15.03 ms（`tmp/w2_fix2/item5/shots/perf_after`・`perf_d`）。
  見本は今の作りにしたが、`nav_list.actor` の Content はこの作りのまま。直すなら fit の軸は合わせない軸の自然な大きさを使わず中身から決める。
  (2) **見出しの文字は画面の幅で折り返さない**（レイアウトは文字の枠を伸ばさない＝規則 3。行の文字だけ見本のスクリプトが枠を合わせる）。今の見出しは組み込みの書体の
  送り幅の表で 360 dp の中身の幅（328 dp）に収まる見積もり（いちばん長い「一覧（W2-3。100 行・…）」が 328.2 dp。撮影で右の余白へ出ていない）。見出しを長くするなら
  2 行に分けるか、`Text.Measure`（W2-6c）で枠の幅と高さを決める。
  (3) 窓 540 のグラフの見本は中身（1,176.4 dp）が画面に収まるのでスクロールしない（以前は中身の高さを 1,300 に固定していて 100 dp 余計に動いた）。
  (4) 見本の `Page` の Sprite（透明・540×1076 など）は大きさをレイアウトが決めるので値に意味が無い（切り抜きと当たりの矩形のために残した）。
  (5) 実機（Pixel 6a）での見え方は未確認（安全領域の実際の値・2.625 倍の文字の枠・テーマの帯の 4 つのボタンの文字の余白）。
  → 2026-09-30 に実機（Pixel 6a・411×914 dp・develop の .so）で確かめた（roadmap §3.9.4）: ギャラリー・画面の組み立て・グラフの見本は画面の幅に収まり、
  テーマの帯はステータスバーの下から始まる（撮影 `tmp/w2_fix2/device/shots/u1_gallery_start.png`・`u3_before_chart.png`）。4 つのボタンの文字も枠の中。
  (6) **コンテナで並べた分、レイアウトの計算が増えた**: 同じ debug の SEED.exe・窓 540・continuous・起動の 8 秒後から何もしない 5 秒の `PROFILE_DUMP` で、
  ギャラリーの「UI/2D スクリーン座標収集」（`collect_2d_screen_positions`＝ScreenPosition のために毎フレーム全ノードを並べる走査）が P2-4 の後の絶対配置
  2.60 ms → P2-5 の後 3.81 ms、フレームの平均が 12.90 → 15.03 ms（ノード 455 → 477。`tmp/w2_fix2/item5/shots/perf_before`・`perf_d`）。最適化した .so では
  測っていない。フレームの残りの増え（約 0.9 ms）がどの区間か（描画の側のレイアウトの表を作る所か）は分けていない。「UI/2D スクリーン座標収集」は ScreenPosition を
  読むスクリプトが無くても毎フレーム走る（`frame_renderer.rs` のコメント「アクタ数に比例する無条件コスト」）。
  → 2026-09-30 に実機（Pixel 6a・develop の .so）で測った（roadmap §3.9.4）: ギャラリーを `adb input swipe` で 24 回払った 41 秒で、描いていた時間（on_demand で止めていた
  18 回の区間を除いた 28.48 秒）あたり 1,703 フレーム＝**59.8 fps**（3 秒ごとの窓で 59.6〜60.5）。組み直しの後も 60 fps を保っている（§3.9.1 の 59.3〜60 と同じ程度）。
  `tmp/w2_fix2/device/logs/fps_analysis.txt`。
- [ ] **W2-11 通しの確認（UC-1〜12）** — 2026-09-27。
- [ ] **PC の Play のスクリプトのコンパイルで `System.Text.Json` を参照できない** — 2026-09-27（Wake or Pay の W3-D で発見）。`using System.Text.Json;` が `CS0234: 'Json' does not exist in the namespace 'System.Text'` で失敗する。Play のコンパイル（ScriptAssemblyManager の Roslyn）が「その時点で読み込まれているアセンブリ」だけを参照に入れるため、共有フレームワークの `System.Text.Json.dll` が参照に入らない。Wake or Pay は反射なしの小さな JSON（`assets/scripts/Json/`）を自作して回避した。直し方の案: 参照の集合を「読み込み済み」ではなく、同梱の .NET の `shared/Microsoft.NETCore.App/<版>/` の参照用アセンブリ一式（または許可リスト）から作る。Android の同梱 CoreCLR と SeedPak の事前コンパイル（`--scripts`）の参照も同じ集合にそろえる。関連: `scripting/` の Compilation、`docs/scripting_api.md`（使える .NET の範囲を明記する）。
- [ ] **SEED.exe を前面に出さずに起動する引数が無い（自動の見た目の検査が利用者の画面を奪う）** — 2026-09-27（W2-1a の画素比較で発見。利用者から「頻繁に起動しているのはなぜ？」）。今は STARTUPINFO の SW_SHOWNOACTIVATE で起動し最背面へ送って凌いでいる。案: `--window=hidden|offscreen|minimized` と、描画をオフスクリーンのテクスチャに向けてスクリーンショットだけ撮る `--headless-render`（決まったフレーム数で撮って終了）。W2 以降の見た目の回帰検査（UC の自動化）と CI で使う。関連: `runtime/src/main.rs`、IPC の SCREENSHOT。
  → 2026-09-28（W2-7 の回帰）: 最背面の窓でも**利用者の OS のマウスカーソルが窓の上にあると `OnPointerEnter`（ホバー）が起き**、図鑑の矢印の色が変わって
  画素比較が揺れた（推論。撮り直しで差 0）。オフスクリーンの撮影か「実のカーソルを無視して注入だけを使う」起動の引数があれば避けられる。
- [x] **`frame_renderer.rs` の未使用の import `sprite_world_corners`** — 2026-09-27（W2-1a の後に rust-analyzer が指摘）。警告の総数は変わっていないが、W2-1a で使われなくなった可能性。W2-1b で確かめて消す。
  → **2026-09-28 に W2-1b で消した**: rustc も `unused import` を出していた（W2-1a より前から、どこからも呼ばれていなかった）。import と、呼び手の無くなった
  関数 `canvas_collect::sprite_world_corners` の本体を消した。
- [ ] **W2-1b の残り（レイアウトの部品）** — 2026-09-28。(1) **枠の無い Text の大きさを測れない**: コンテナの子の自分の大きさは Text の枠（BoxWidth/BoxHeight）か
  `CanvasLayoutItem.preferred_*` でしか決まらない（文字の寸法の計測は W2-6 の `Text.Measure`。測れるようになったら measure.rs の「自分の大きさ」へ足す）。
  (2) **伸ばす子の下限・上限で余った分を配り直さない**（CSS の flex のような反復は無い。重みで分けて収めるだけ）。(3) **スキンスプライト・テキスト・
  2D パーティクルは伸ばさない**（Sprite だけが矩形いっぱいに描かれる）。(4) **安全領域・親に合わせる・コンテナは回転していないノードを前提**
  （回転したノードの安全領域は外接矩形）。(5) スクリプトから**ルートキャンバスの単位（dp）を読み書きできない**（C# に Canvas の型が無い。
  インスペクタだけ）。スクリプトから **1 dp の画素数（dp の倍率）を読む API も無い**（入力の座標は画素のまま。W2-2 のジェスチャーで閾値を dp で持つときに要る）。
  → W2-2（2026-09-28）でジェスチャーのイベントの中だけ読めるようにした（`GestureEvent.DpScale`・`DeltaDp`・`VelocityDp`。閾値はエンジンが dp で持つ）。
  いつでも読める API（`Screen.DpScale` など）はまだ無い。→ **2026-09-28 の W2-7 で `Screen.DpScale` を足した**（dp のルートのレイアウトと同じ値）。
  (6) **動いているエディタでの追加・編集の目視は未確認**（WPF のインスペクタは既存の書き方に倣っただけ。ビルドとエディタのテストは通る）。
  (7) エディタの GPU の ID 描画の切り抜きは CPU の計算（`canvas_id_scissors`）までを単体テストで確かめただけで、3D ビューでの実際のクリックは未確認。
  関連: `runtime/src/engine/core/canvas_layout/`・`editor/src/Panels/InspectorPanel.CanvasLayout.cs`・`scripting/src/Api/Canvas*.cs`。

### W3: Wake or Pay の移植で見つかったエンジンの不具合・制限（2026-09-30〜。移植先は D:\SEED_projects\WakeOrPay）

- [ ] **パッケージの収録が末尾 `/` のフォルダ参照を拾わない（Android でデータファイルが APK に入らない）** — 2026-09-30（W3-0 で発見）。
  スクリプトの `"assets://common/data/"` のような末尾が `/` のフォルダの参照が収録されず、Wake or Pay の APK にデータの JSON が 1 つも入らなかった
  （pak の収録 27 件。プロジェクトの `packaging_settings.json` の `additional_folders` に `common/data`・`common/themes` を足して 47 件にして回避）。
  原因（コードを読んで確認・直していない）: `editor/src/Packaging/Collect/AssetPathUtil.NormalizeRelative` が末尾の `/` を落とさず
  （`CollapseDotSegments` も `..`・`./` を含まない限り素通し）、`AssetCollector` の `_dirsOnDisk`（末尾 `/` なしで登録）と一致しない
  （`CollectFrom` の `_dirsOnDisk.Contains(rel)`、参照の候補の照合 `_dirsOnDisk.Contains(cand)` も同じ形の見込み）。
  案: `NormalizeRelative` で末尾の `/` を落とす（ファイルの参照には影響しない）＋単体テスト。PC の Play はディスクから直接読むので気づけない。
  W3-D/W3-S の DomainSmoke も Android では同じ理由で動かない見込み（推測）。
- [ ] **PC の 1 倍で小さな文字の細い横線が消える・かすれる** — 2026-09-30（W3-0 で発見）。16 px 以下で長音符「ー」が消えたりかすれたりする
  （「トークン」が「ト クン」、「データ」が「デ タ」。1.3139 倍の模擬では正常）。上の「W2 の手直し P2-3 の残り」(1)・「P2-1 の残り」(1) と同じ見立て
  （SDF を画素の中心で 1 回だけ読む）。実機（2.625 倍）では未確認。
- [ ] **中身に合わせる（fit）コンテナを親のスタックに置くと背景の Sprite が伸びない** — 2026-09-30（W3-0 で発見）。ダイアログの札と同じ規則
  （`CanvasLayoutItem` の中身に合わせた大きさが背景の Sprite へ届かない）。Wake or Pay はアラームのタブの権限の帯の高さを固定して回避。
- [ ] **`Text.Measure` が無いので複数行の文字の高さ・枠の幅を見積もりで決めている** — 2026-09-30（W3-0）。Wake or Pay のシェルは文字の枠の幅を
  仮に 360/328 dp で固定・複数行は行の数から高さを決めている。W2-6c の `Text.Measure` で置き換える。
- [ ] **W3-1（アラームの一覧と編集）で見つかった UI 部品の制限** — 2026-09-30（Wake or Pay の W3-1 で発見。プロジェクト側で回避済み・エンジンは直していない）。
  (1) **`SEED.UI.Slider` の溝の長さが部品の開始時のプレハブの幅で固定**（レイアウトの幅の変化に追従しない。Wake or Pay は `FullWidthSlider` を作って回避）。
  (2) **`ChipGroup`・`RadioGroup` の文字の大きさが `text.label` に固定**（部品ごとに変えられない。基のテーマの `text.label` を上げて回避）。
  (3) **Dialog に危険（赤）のボタンの種類と、選択肢の一覧のダイアログ（Material の SimpleDialog・メニュー）が無い**（「破棄して戻る」「削除」が赤にならない。長押しのメニューはボタンのダイアログで代用）。
  (4) **文字の大きさの意味が Flutter と違う**: SEED の文字の大きさは書体の ascent − descent（Flutter の em の約 1.395 倍）なので、同じ数値だと Flutter より約 28% 小さく見える。
  既定のテーマの `text.*` は Material の sp の値のまま（Wake or Pay は `wop_base.json` で sp × 1.395 にして補った）。案: テーマの文字の大きさを em で解釈する・既定値を直す（既存のプロジェクトの見た目が変わるので要判断）。
  (5) **pivot が 0 でないノードの子の位置がずれる**（コンテナに置かれないノードでは、子が親の「位置の点」から数えられるため、pivot が 0 以外のトグルやボタンのつまみ・文字がずれる。pivot 0 のキャンバスで回避）。
  (6) **`GameObject` の子を並べて見る API が無い**（子の名前を知っている前提で探すしかない）。
  (7) **音の再生の終わりを知る手段が無い**（鳴り終わりの知らせも長さの問い合わせも無い。サウンドの試し聴きの ■ を自動で ▶ に戻せない）。
  (8) **`TimeWheel` の選択の帯の色を部品ごとに変えられない**（`surface_variant` 固定）。
  (9) **書体に絵文字が無い**（覚悟ゲージの 💀・鳴動画面の 💸 👨 📳 など。Wake or Pay は絵文字を落として語だけ出す `PictographText` で代用。色つきの絵文字の書体か、絵の差し込みが要る）。
- [ ] **PC の 1 倍で小さな文字の横線が欠けて別の字に見える** — 2026-09-30（W3-1 で発見。上の「PC の 1 倍で小さな文字の細い横線が消える・かすれる」の続き）。
  「スヌーズ」が「メメ　ズ」、「上限」が「⊥限」、「トークン」の「ー」が消える（`tmp/w3_1/shots/verify_a/15_sub_rate.png`〈上書き済み〉・`verify_d/01_cap_warning.png`・`verify_c/05_dismissed.png`）。
  1.31 倍の模擬では出ない。W3-1 の担当は文字の GPU バッファの使い回し（0bf8981c）を疑ったが、欠けているのはどれも**細い横画**（ス・ヌの上の横線 → メに見える、上の右の短い横線 → ⊥）なので、
  P2-1・P2-3 の (1) と同じ「SDF を画素の中心で 1 回だけ読み、1 画素より細い線が画素の中心の間に落ちると塗られない」見立ての方が合う（推論・未検証）。
  PC の 1 倍の見た目の確認（エディタの Play・自動の撮影）を誤らせるので、W2-6c（文字の寸法）と一緒に直したい。実機（2.625 倍）は未確認。
