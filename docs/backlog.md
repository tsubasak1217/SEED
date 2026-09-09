# 作業バックログ（未着手・保留の課題）

セッションやエージェントをまたいで共有する「今後やらないといけないこと」の一覧。
着手するときは該当項目を読み、完了したら項目を削除する（履歴は git に残る）。
新しい課題が見つかったら、ここに追記する（発見日・背景・関連ファイルを必ず書く）。

記法: `- [ ] 題名 — 背景 / 関連 / 備考`。優先度は上から順（高→低）。

関連ドキュメント: アニメーションタイムラインの操作・仕様は
[docs/editor_animation_timeline.md](editor_animation_timeline.md)（フレーム編集 / キー挿入 /
ライブプレビュー / 既知の制限）が正典。

## エディタ

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

## ランタイム / スクリプト API

- [ ] **`SEED.Draw`（2D プリミティブ）の未検証項目** — 2026-09-07。GPU の実描画（位置・重なり・アンチエイリアス、3D キャンバス上の深度）は目視未確認。同一 layer の並びは「スプライト → プリミティブ → テキスト」固定（2026-09-08 に 3 種の描画順を統合したので、テキストを覆いたいときはプリミティブの `layer` をテキストより大きくすればよい）。`Arc` の Fill=リング / Outline=線 の意味は直感に反する可能性あり（`Ring` あり）。フェザー 1px 固定なので 3D キャンバス上では遠いと太く見える（解析 SDF 化は図形別シェーダが必要）。関連: `runtime/src/engine/core/renderer/primitive2d/`。

- [ ] **`SEED.Draw3D` の未検証項目** — 2026-09-07。リボンの押し出し向き・線幅の実測、半透明の上／2D UI の下に入るか、Play 中のビューポート矩形と uniform の一致。折れ線のジョイント処理なし・アンチエイリアスなし（仕様として docs 記載）。関連: `renderer/primitive3d/`。

- [ ] **プリミティブ描画キューが描画されないフレームで溜まる** — 最小化中などは `take_commands` が呼ばれず上限まで溜まって警告が出る（メモリは有界）。2D/3D 共通。

- [ ] **実行時に生成・付け替えしたアクタの物理コライダーが物理スレッドに反映されない** — 既存 Instantiate と同じ制約。`Instantiate(path, parent)` / `SetParent` も同様。関連: `app/script_scene_ops.rs`、docs/scripting_api.md の注記。

- [ ] **`SEED.Vector2` / `SEED.Vector3` の `[SerializeField]` はインスペクタで編集できない** — 2026-09-07 に確認。`ScriptInspectorBuilder.BuildValueRow` は float/int/bool/string と参照型だけを扱い、それ以外は読み取り専用行になる（`[Serializable]` が無いので `Children` 展開にも乗らない）。色や座標を Vector3/Vector2 で公開している既存スクリプトは値を変えられない。float 2〜3 本に割るか、インスペクタ側に Vector 行を足すかの判断が要る。関連: `editor/src/Scripting/ScriptInspectorBuilder.cs`、`scripts/FishRadar.cs`、`scripts/FishingFight.cs`。

- [ ] **`GameObject.Parent` が O(N)** — DFS 走査で親を探す実装。毎フレーム大量に呼ぶ用途には向かない。

- [ ] **ホットリロードで `OnDestroy` が呼ばれる保証が未確認** — 2026-09-09（2026-09-07 の「プールが二重生成される」から残った部分）。二重生成そのものは解消済み: エディタが既定で Play 中のホットリロードを保留するようになり（`docs/editor_auto_reload.md`）、あわせて `OnStart` で `Instantiate` していた箇所を `SpawnOnce.GetOrInstantiate` へ寄せた（FishingController / PauseMenu / ResultPanel / FightEvalBanner / HitBanner / FishingFight）。**未確認のまま残るのは「作り直し時に旧インスタンスの `OnDestroy` が呼ばれるか」**で、`OnDestroy` で解除しているイベント購読・静的登録が残留しないかは実機で見ていない。関連: `runtime/src/engine/core/scripting/mod.rs`、`runtime/assets/common/scripts/UI/SpawnOnce.cs`。

- [ ] **`OnStart` 内 `Instantiate` の成否が未検証** — FishingFight のビートアイコンプールが初例。失敗するとリトライせず無効ハンドルが残る。関連: `runtime/assets/mainGame/scripts/FishingFight.cs::EnsureIconPool`。

- [ ] **docs の `Mathf.Clamp01(v)` が実装と食い違う** — 2026-09-07。実装は `Clamp01(ref float)`（void）で、値を返すのは `Clamped01(v)`。docs 4 章の記述を実装に合わせるか、値返し版を `Clamp01` として追加するかの判断が要る。関連: `docs/scripting_api.md` 4 章、`scripting/src/Api/Mathf.cs`。

- [ ] **ScriptEvent 内のアクタ名がアクタのリネームに追従しない** — 2026-09-07。結線 JSON に埋まった `actor` は `rename_refs.rs` の値ゲート（フィールド値そのものが旧名）に当たらない。構造体リスト内の参照メンバと同じ既存制限。対応するなら CLR に型タグ問い合わせ FFI を 1 本足し、`scriptevent` フィールドは JSON をパースして書き換える。関連: `runtime/src/engine/core/app_base/app/rename_refs.rs`、`scripting/src/Api/ScriptEvent.cs`。

- [ ] **スクリプト遷移時の地形 LOD 事前収束が同期でフェードを止め得る** — 2026-09-07。`install_loaded_scene` は Play 中に `converge_terrain_lod_blocking` を呼ぶ（無いと遷移後に長時間の低 fps）。地形規模によってはフェード中に一瞬固まる。気になれば遷移時のみ非同期収束に切り替える。関連: `app/app_init.rs::install_loaded_scene`、`app/script_scene_ops.rs`。

- [ ] **LOAD_SCENE（常駐 Play プロセス再利用）で `pointer.reset()` が呼ばれない** — 2026-09-07。旧シーンのホバー/押下エンティティを持ち越す可能性。スクリプト遷移経路と同じ理由でリセットすべきに見えるが、挙動維持のため `SceneInstallOptions.reset_pointer=false` のまま。関連: `app/ipc_handler.rs` の LOAD_SCENE。

- [ ] **プロローグ会話システムの実機未検証項目** — 2026-09-07。文字送り・送りマーク点滅・カメラ補間の見た目、日本語＋空白＋角括弧を含むフォントパス（ゆずポップ Regular）の実読み込み、CamTarget_* の高さ（目線位置は推定値）、CamTarget_Owner が Hut に埋まる可能性、Text の自動折り返し無し（`
` 手動改行）。関連: `runtime/assets/prologue/scripts/Dialogue/`、`proLogue.scene`。

- [ ] **Animator のクリップ設定 `loop_mode` がキーフレーム .anim では無視される** — 2026-09-09。`animation_ops.rs` のキーフレーム経路は `.anim` ファイルの `loop_mode` を使い、Animator 側の設定はモデル内蔵アニメ（`normalize_model_time`）でしか参照されない。インスペクタで「ループ」にしても .anim が once なら 1 回で止まる（TutorialMouse で発生）。Animator 側の設定を上書きとして優先させるのが自然。関連: `runtime/src/engine/core/app_base/app/animation_ops.rs:109,172`。

## ゲーム（わらしべフィッシング）

- [ ] **ルートキャンバスの `auto_scale` が実質無効（設計解像度が効かない）** — 2026-09-07 に HIT 帯演出の位置ズレを追って判明。ビューポート所属のルートキャンバス（`CanvasViewportRef::Camera / MainCamera`）は `build_root_canvas_auto_size_map` により width/height が**実描画解像度で上書き**される。その後 `auto_scale_factor = eff_viewport / my_eff_w` を計算するが、`my_eff_w` は上書き後（＝ eff_viewport 自身）なので**係数は常に 1.0** になる。結果として `CanvasComponent.width/height`（例: 1920x1080）は完全に無視され、子の座標・アンカー基準・スプライト寸法はすべて**ウィンドウの実ピクセル**になる。UI をウィンドウサイズ非依存に組めないうえ、`auto_scale` チェックが何もしない。直すなら基準サイズ（`cc.width/height`）と実解像度を分け、アンカー基準サイズは設計解像度・スケール係数は 実解像度/設計解像度 とする。影響範囲が広い（既存 UI の座標が全部変わる）ため要判断。関連: `runtime/src/engine/core/app_base/app/canvas_collect.rs`（`build_root_canvas_auto_size_map` / `auto_scale_factor` / `my_eff_w`）。

- [ ] **Edit の 2D シーンビュー（EDIT_VIEW:2d）はカメラのパン・ズームを IPC から動かせない** — 2026-09-07。`canvas_cameras[0]`（pan_x/pan_y/ortho_half_h）はマウス入力（MMB ドラッグ・ホイール）でしか変化せず、初期状態は「キャンバス左上がビュー中央・1 キャンバス px = 1 画面 px」。ヘッドレス（MCP）ではマウスを送れないため、キャンバス全体を映した設計ビューのスクリーンショットが撮れない（今回は Play + `seed_screenshot(game)` で代用した）。`CAM2D_SET:{pan_x},{pan_y},{half_h}` のような IPC か「選択物にフィット」コマンドがあると AI からの目視確認が回る。関連: `runtime/src/engine/core/app_base/app/frame_renderer.rs`（use_ortho_2d_camera 付近）、`app/ipc_handler.rs`（EDIT_VIEW）。

- [ ] **HIT 演出の帯の角度を変えるにはクリップの作り直しが必要** — 位置キーは θ=−12° を展開した実座標。回転トラックだけ変えても位置は追従しない。2026-09-07 にアイテムごとの 4 クリップへ分割（位置キーが各アイテムのローカル座標のため）。同日、4 アイテムのアンカーを画面中心 (0.5,0.5) へ統一し、静止位置・入退場のキーを再生成した。関連: `runtime/assets/mainGame/animations/hit_banner_band_top.anim` ほか 3 本、`scripts/HitBanner.cs`。

- [ ] **糸ゲージ・レーダーの見た目が未目視** — 2026-09-07。HIT 帯演出（帯 2 本＋文字 2 つの同期・入退場）は 2026-09-07 にヘッドレス Play で目視確認・修正済み。残るのは、`Draw.Rect` の回転小片で描く糸ゲージ（旧 48 スプライトとの一致）、`Draw.RegularPolygon` の三角マーカー、プリミティブ化したレーダー背景円・中心点の大きさと色で、どちらも「ウキを投げて魚と勝負している間」しか描かれないため待機状態のスクリーンショットでは確認できない（実プレイが要る）。関連: `scripts/HitBanner.cs`、`scripts/FishingFight.cs`、`scripts/FishRadar.cs`。

- [ ] **未参照になったテクスチャ** — 2026-09-07 のレーダーのプリミティブ化で `radar_bg.png` / `radar_dot.png` がどのアクタからも参照されなくなった。他で使わないなら削除してよい。関連: `runtime/assets/mainGame/textures/ui/`。

- [ ] **レーダーの点・ビートアイコンの見た目確認** — 2026-09-07。`Draw.Circle` の点の位置・サイズ（`radarSpace` 相対のスケール一致）、`BeatIcon.actor` プールの出現位置は未目視。関連: `scripts/FishRadar.cs`、`scripts/FishingFight.cs`。

## 未コミットの他セッション差分（要確認）

- [ ] **`app_init.rs` / `ipc_handler.rs` / `script_scene_ops.rs` / `play_mode_ops.rs` に別セッションの未コミット変更** — 2026-09-07 時点。Play 開始時のシーン登録表再読込など。作業ツリーに残っているので、そのセッション側でコミットするか破棄するか判断する。

## エディタ MCP / ヘッドレス（2026-09-07 実装の残件）

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

- [ ] **2D ノードのレイアウト計算が 5 か所に重複コピーされている** — 2026-09-07。`collect_sprite_items` / `collect_canvas_rects` / `collect_canvas_id_items` / `pick_2d::walk_pick_candidates_2d` / `physics2d_ops::collect_actor2d_contexts` が、root_auto 上書き → eff_viewport → アンカー → eff_ct → size_scale → self_world_rs → 子への継承、という同じ 60〜80 行をそれぞれ持っている。今回の「子のアンカー基準サイズ」バグは、この重複のうち 1 か所（`physics2d_ops` のギズモ用アンカー）だけ挙動が違ったせいで「ギズモは正しいのに描画がずれる」という形で表面化した。アンカー部分は共通ヘルパー（`node_anchor_offset` / `child_anchor_basis`）へ切り出したが、残りは未統合。`CanvasNodePlacement::resolve()` のような純関数へ一本化したい。関連: 上記 5 ファイル。

- [ ] **入れ子 2D の anchor 仕様がドキュメント化されていない** — 2026-09-07。「anchor は**親の CanvasComponent 領域**に対する比率で、Canvas 領域を持たない親（Sprite など）の子では anchor は効かない（親の原点＝親の position 点が基準）」という規則を docs 側に明記する。エディタのインスペクタでも、親が Canvas 領域を持たないときは anchor 欄をグレーアウトするのが親切。

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

- [ ] **説明窓の素材が未着（仮素材で実装済み）** — 2026-09-07。`runtime/assets/mainGame/actors/UI/TutorialWindow.actor` のミニキャラと吹き出しは `assets://mainGame/textures/ui/white.png` を着色した矩形、送りマークは prologue の `nextArrow.png` を流用している。差し替えは Sprite の `texture_path`（と `width` / `height`）を変えるだけでよく、スクリプトの変更は不要。
- [ ] **キーアイコンが仮画像** — 2026-09-07。`runtime/assets/mainGame/ui/tutorial.icons` の `key_w` / `key_s` / `key_a` / `key_d` / `mouse_l` はすべて `white.png` を指している。本番画像ができたら `.icons` の `path` を差し替えるだけで説明文（`[icon:key_w]` 等）へ反映される。`.icons` のキャッシュは 1 秒間隔のポーリングで自動反映されるため、差し替え後はエディタの再起動不要（本ファイル「Text のインライン画像記法の残件」参照）。
- [ ] **チュートリアルの実機確認が未実施** — 2026-09-07。スクリプトのコンパイルとシーン JSON の整合（参照先アクタ・フィールド名）はプログラムで照合したが、Play での目視確認をしていない。特に (1) `Time.Scale = 0` 中に釣りの各状態が破綻しないか、(2) 説明窓の追従（`Camera.WorldToCanvas`）が 1920x1080 設計キャンバス上で意図した位置に出るか、(3) 台本（必ず食いつく／漂流物を出す）が手順どおり効くか、の 3 点は実機で確認すること。
- [ ] **`runtime/assets/tutorial/scripts` の空ディレクトリが残っている** — 2026-09-07。旧チュートリアルシーン（`tutorial.scene` / `TutorialFlow.cs`）は削除済みだが、実行中のエディタがディレクトリのハンドルを掴んでいるため空フォルダだけ消せなかった。エディタを閉じてから削除すること。

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
- [ ] **`resolve_dll_path` がカレントディレクトリ基準で SEEDScripting.dll を探す** — 2026-09-08。`runtime/src/engine/core/scripting/mod.rs::resolve_dll_path` は `cwd/../scripting/bin/Debug/net9.0/SEEDScripting.dll` → `cwd/SEEDScripting.dll` の順で探す。ランタイムの作業ディレクトリは `RuntimeManager.ResolveWorkingDirectory` が「exe の 2 階層上が `target` のときだけ」リポジトリ側へ上げるため、`docs/editor_mcp.md §5.5` が推奨する `cargo build --target-dir <別ディレクトリ>` で作った SEED.exe を `SEED_RUNTIME_EXE` で使うと DLL が見つからず、ランタイムが起動しない（エディタ側は「ランタイムが接続しません」としか言わない）。回避策は出力先へ `SEEDScripting.dll` 一式を手でコピーすること。exe の位置からも探すか、環境変数で明示できるようにしたい。
- [ ] **`seed_launch(scene:)` が `assets://` パスを受け付けない** — 2026-09-08。`editor/SeedMcpServer/Launcher.cs` は `Path.GetFullPath(scenePath)` をそのまま `--scene` へ渡すため、`assets://zukan/zukan.scene` は `…\SEED\assets:\zukan\zukan.scene` という壊れたパスになり、シーンが読めないまま「ランタイムが接続しません」でタイムアウトする（原因が一切表示されない）。絶対パスなら正常に動く。`assets://` を assets ルート基準へ解決するか、少なくともエラーとして弾きたい。
- [ ] **キャンバスの `auto_scale` がカメラ基準解像度より大きいキャンバスを縮小しない** — 2026-09-08。カメラの `target_width/height` が 1280x720 のとき、`auto_scale: true` の 1920x1080 キャンバスは 1 単位＝描画ターゲット 1px で描かれ、中央 1280x720 の外に置いた要素は画面に出ない（ヘッドレス Play のスクリーンショットで実測）。今回は図鑑・ポーズメニューのキャンバスを 1280x720 にして回避した。既存の `FishingUI` は端をアンカー基準で置いているため実害が出ていないだけなので、`auto_scale` の意図（基準解像度へフィットさせる）どおりに効いているか要確認。
- [x] **`[SerializeField]` の参照解決がシーン全体のアクタ名 DFS なので、同じプレハブを複数生成すると参照が 1 個目へ集まる** — 2026-09-08 に解決。参照文字列へパス形式（`./Child` / `../Sibling` / `Root/Child`）を導入し、素の名前は「自分のサブツリー優先 → シーン全体」で解決するようにした（正典: `runtime/src/engine/core/scripting/actor_ref_path.rs`、docs/scripting_api.md「参照文字列のパス指定」）。`GameObject.FindChild(nameOrPath)` も追加。図鑑カードは `assets://zukan/actors/ZukanCard.actor` のプレハブインスタンス 4 枚になった。
  **残る制限（この項目の続き）**:
  - パス形式で保存された参照は**アクタのリネームに追従しない**（`rename_refs.rs` は「フィールド値が旧アクタ名そのもの」のときだけ書き換えるため）。素の名前で保存された参照はこれまでどおり追従する。
  - 参照ボックスの「参照先が見つかりません」警告（`ReferencePicker.RefreshLabel`）は、パス形式のとき**判定を諦めて出さない**。持ち主基準の解決をエディタ側で再現していないため。
  - 参照ボックスのダブルクリックによる Hierarchy ジャンプ（`ActorRefJump.RevealActorByName`）もパス形式では効かない（名前一致で探すため）。
  - `ScriptEvent` の結線先アクタ（`ScriptEventBinding`）は従来どおりシーン全体 DFS のまま。プレハブ内のイベント結線は同名インスタンスで壊れうる。
- [ ] **`runtime/assets/tutorial/scripts` がアクセス拒否でスクリプト収集から毎回スキップされる** — 2026-09-08。エディタのログに `[ScriptCompiler] 読み取れないフォルダをスキップ … Access to the path … is denied.` が再読込のたびに出る。assets が別ドライブへのジャンクションであることに由来する権限の問題と思われる。

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

正典: `runtime/assets/mainGame/scripts/CatchPresenter.cs` / `ResultPanel.cs`。

- [ ] **スクリプトのメソッドを外から叩く IPC（`SCRIPT_DEBUG:<name>,<arg>`）が無い** — 2026-09-08。今回の目視確認では「釣果パネルだけを出す一時シーン＋一時ドライバスクリプト」を作って撮影し、確認後に消した。ヘッドレスで任意のゲーム進行（例: 釣り上げの瞬間）を再現できないため、3D 側（スロー放物線・横カメラ・しぶき）は<b>未検証のまま</b>。`SEED.Debug.OnCommand` のような購読口と `seed_send_ipc` からの `SCRIPT_DEBUG:` を実装すれば、AI による検証の守備範囲が大きく広がる。
- [ ] **スロー放物線・横カメラ・しぶきパーティクルが実機未確認** — 2026-09-08。魚を実際に釣り上げないと通らない経路のため、ヘッドレスでは撮れていない。特に (1) `assets://mainGame/actors/FX/Splash.actor` を実行時 `Instantiate` したときに GPU パーティクルが放出されるか（Play 開始時に存在しないエミッタの扱い）、(2) 横カメラの θ/φ/距離の既定値で弧が画面に収まるか、(3) `Time.Scale` を下げているあいだに他システムが破綻しないか、の 3 点は人の目で確認すること。
- [ ] **旧「釣果テキスト」（FishingUI/catchUIs 配下）が未使用のまま残っている** — 2026-09-08。`CatchPresenter` は `ResultPanel` へ移行したので、`CatchName` / `CatchSize` / `CatchRank` / `CatchBest` / `CatchPrompt` は誰からも参照されない。シーン（MainGame.scene）は AI が触らない約束のため残置。利用者が削除すること。
- [ ] **Text（`box_width = 0`）の実際の描画位置が指定 y よりわずかに上に出る** — 2026-09-08。`ResultPanel.actor` のレイアウトはヘッドレス撮影を見ながら数値を合わせ込んだ。`vertical_align: middle` の基準がフォントのどの高さなのかを確かめて、指定位置＝視覚的な中心になるようにするか、ずれ幅を仕様として docs へ書くのが望ましい。

## 釣果リザルト演出 — 縦跳び化とデバッグコマンド（2026-09-08 実装時）

正典: `runtime/assets/mainGame/scripts/CatchPresenter.cs` / `ResultPanel.cs`、`docs/editor_mcp.md` 10 章。

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

- [ ] **パッケージ版ではユーザースクリプト（.cs）が一切コンパイルされない** — 2026-09-09（実測）。
  `runtime/src/engine/core/app_base/app/mod.rs:1411` の
  `if let (Some(host), Some(root)) = (&scripting_host, &args.assets_root)` が
  コンパイルの発火条件で、パッケージ実行では `args.assets_root` が `None` のため
  `compile_scripts` が呼ばれない。さらに `SEEDScripting.dll` と hostfxr 一式が
  出力フォルダへコピーされないので、条件を直しても実行時に読めない。
  <b>現状のパッケージ版はスクリプト無しで動く状態</b>。
  対策案は 2 つ: ①パッケージ時にスクリプトを事前コンパイルして DLL を同梱する、
  ②ランタイムが PAK から `.cs` を一時フォルダへ展開して `compile_scripts` に渡す。
  収録側の準備（`.cs` を常時同梱する規則）は済んでいる。
  関連: `runtime/src/engine/core/scripting/mod.rs::compile_scripts`、
  `editor/src/Packaging/Collect/PackagingRules.cs`。
- [x] **`asset_fs` を通らない `std::fs` 直読みが残っており PAK モードで既定値になる** — 2026-09-09 記載 / 同日対応。
  `app_init.rs` の `project_settings.json` 読み込み（ウィンドウサイズ・プラグイン有効化リスト）を `asset_fs::read_string` 経由に変更し、
  `init_asset_fs` を `handle_resumed` の先頭（ウィンドウ生成前）へ前倒しした。最小 PAK での実機確認で 1280x720 を確認済み。
  パッケージ実行時のプラグインは未対応のまま（`is_packaged()` なら 0 件で続行し 1 行ログ）。DLL 同梱の仕組みが要る。
  関連: `runtime/src/engine/core/app_base/app/app_init.rs`（`parse_window_size` テスト 5 件）。
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
