# 作業バックログ（未着手・保留の課題）

セッションやエージェントをまたいで共有する「今後やらないといけないこと」の一覧。
着手するときは該当項目を読み、完了したら項目を削除する（履歴は git に残る）。
新しい課題が見つかったら、ここに追記する（発見日・背景・関連ファイルを必ず書く）。

記法: `- [ ] 題名 — 背景 / 関連 / 備考`。優先度は上から順（高→低）。

## エディタ

- [ ] **アニメーションパネルが子アクタ選択で切り替わる問題** — 2026-09-07。パネルは選択アクタの Animator に連動するため、クリップの対象である子アクタを触ると空になる。
  案: (1) 選択アクタに Animator が無ければ先祖の Animator を保持、(2) 🔒 ロックトグル、(3)「選択アクタの現在値をキーに記録」ボタン（ビューポートで動かして記録）。
  関連: `editor/src/Panels/AnimationTimelinePanel.xaml.cs`（`OnSelectionChanged` / `OnActorComponentsReceived`）。当面の回避策は「直接開く」でファイル単独モード（プレビュー不可）。

- [ ] **SkinnedSprite ノードの pivot が事実上効いていない** — 2026-09-07。`CanvasTransform::to_mesh_mat4(sx, sy)` が `to_sprite_mat4` に委譲しており、pivot オフセットが `pivot × size_scale`（≒0.5px）にしかならない。描画と枠は同じ行列なので位置ズレは無いが、Inspector の pivot を変えても回転中心が動かない。**Text は解決済み**（2026-09-07。枠あり = `box_width > 0` のときのみ、`to_mesh_mat4_no_pivot` + レイアウト側の平行移動で Sprite と同じ正規化 pivot が効くようにした。枠なしは従来どおり pivot 無効）。SkinnedSprite は未対応（メッシュ寸法を pivot の基準サイズとして採るか、実寸 px を直に採るかの規約を決めるところから）。
  対処には「Text は実測枠のサイズを pivot の基準にする」設計判断が要る。関連: `runtime/src/engine/components/canvas_transform.rs`、`font/canvas_text.rs`、`app/canvas_text_bounds.rs`。

- [ ] **アクター編集タブで親が回転している 2D アクタの移動書き戻し** — 2026-09-07。`actor_2d_layout_ctx` が None のフォールバック経路は親回転の逆適用をしない（World/Local どちらでも同じ既存の制約）。関連: `app/drag_handler.rs`、`app/canvas_gizmo_basis.rs::canvas_world_to_parent_local_pos`。

- [ ] **2D のモーダル変形／ギズモドラッグがプライマリ 1 体にしか効かない** — 2026-09-07。`apply_gizmo_new_mat` の 2D 分岐が `canvas_transform_drag_start` 単体しか見ない。複数選択を回転・拡縮したい場合に拡張が必要。関連: `app/drag_handler.rs`、`app/modal_transform.rs`。

- [ ] **モーダル変形のステータス表示（数値入力の表示欄）が無い** — 2026-09-07。3D/2D とも G/R/S 中の入力値・軸拘束をエディタのステータスバーに出す仕組みが未実装。関連: `app/modal_transform.rs`（`MODAL:*` IPC）、`editor/src/MainWindow*.cs`。

- [ ] **3D ワールドキャンバス配下の選択枠が 2 段以上ネストすると出ない** — 2026-09-07。`frame_renderer.rs` の `find_parent_actor_of_dfs` + `get_3d_canvas_world_mat` が直接の子のみ対象。フォルダを挟んでも同様。

- [ ] **スプライトボーンのパス解決がフォルダ非対応** — 2026-09-07。`sprite_bone_ops.rs::descend_by_path_mut`、`sprite_skin.rs::resolve_bone_matrix_by_path`、`canvas_collect.rs::dfs_of_relative_path` は直下のみ照合。ボーンアクタを手動でフォルダに入れると崩れる（sprite_skin は名前 DFS へフォールバック）。キャンバスの透過規則（`canvas_node_is_transparent`）をそのまま当てると行列連鎖の意味が変わるため要設計。

- [ ] **`collect_canvas_actors_in_rect`（矩形選択）が階層非対応** — `ct.position` をそのまま使う点包含判定で、親の変換を考慮しない既存の粗さ。関連: `app/actor_utils.rs`。

- [ ] **Text 枠の左右が送り幅基準** — 2026-09-07。左サイドベアリングが負のグリフ等で数 px 食い違う。縁取りパディングで概ね吸収されるため低優先。関連: `font/text_layout.rs::measure_text_box`。

- [ ] **`build_text_bounds_map()` がエディタ中毎フレーム実行** — HUD 規模では問題ないが、テキストが数千文字規模になる場合はキャッシュを検討。関連: `app/canvas_text_bounds.rs`。

- [ ] **旧コードで作られた既存シーンの 2D「グループ」は通常アクタのまま** — 2026-09-07。2D フォルダノード導入前に作ったグループは `is_folder=false`。自動マイグレーションは無く、フォルダにしたい場合は作り直し。必要なら名前ベースの救済を検討。

- [ ] **MCP の `seed_screenshot` は画面キャプチャ方式（隠れると撮れない）** — 2026-09-07。エディタ側の画面 DC → BitBlt で実装したため、エディタウィンドウが最小化・他ウィンドウで隠れていると正しく撮れない。堅牢化するならランタイム側の読み戻し（`runtime/src/engine/core/renderer/screenshot.rs` は環境変数駆動でスワップチェーン読み戻し済み）を IPC 駆動（`SCREENSHOT:<path>` → `SCREENSHOT_DONE:<path>`）へ拡張する。注意点: スワップチェーンの `COPY_SRC` は `screenshot::is_enabled()` のときだけ付くため、代わりに `RT_LDR`（`COPY_SRC` 付き）を読むのが素直。`target:"editor"`（ウィンドウ全体）はランタイム側では実現できないのでエディタ側キャプチャを残す必要がある。関連: `docs/editor_mcp.md`、`editor/src/AI/Capture/WindowScreenCapture.cs`。

- [ ] **MCP ツールの実機未検証** — 2026-09-07。エディタを起動した状態での `seed_screenshot` / `seed_select` / `seed_play` / `seed_anim_preview` の往復は未確認（実装時にエディタが起動していなかった）。特に `SELECT:` 送信でインスペクタ表示が追従するか、`play_control` の状態遷移待ちが実測でどれくらいかかるかは要確認。関連: `docs/editor_mcp.md` の「代表的なループ」。

## ランタイム / スクリプト API

- [ ] **`SEED.Draw`（2D プリミティブ）の未検証項目** — 2026-09-07。GPU の実描画（位置・重なり・アンチエイリアス、3D キャンバス上の深度）は目視未確認。同一 layer の並びは「スプライト → プリミティブ → テキスト」固定で、プリミティブでテキストを覆いたい場合は統合ソートが必要。`Arc` の Fill=リング / Outline=線 の意味は直感に反する可能性あり（`Ring` あり）。フェザー 1px 固定なので 3D キャンバス上では遠いと太く見える（解析 SDF 化は図形別シェーダが必要）。関連: `runtime/src/engine/core/renderer/primitive2d/`。

- [ ] **`SEED.Draw3D` の未検証項目** — 2026-09-07。リボンの押し出し向き・線幅の実測、半透明の上／2D UI の下に入るか、Play 中のビューポート矩形と uniform の一致。折れ線のジョイント処理なし・アンチエイリアスなし（仕様として docs 記載）。関連: `renderer/primitive3d/`。

- [ ] **プリミティブ描画キューが描画されないフレームで溜まる** — 最小化中などは `take_commands` が呼ばれず上限まで溜まって警告が出る（メモリは有界）。2D/3D 共通。

- [ ] **実行時に生成・付け替えしたアクタの物理コライダーが物理スレッドに反映されない** — 既存 Instantiate と同じ制約。`Instantiate(path, parent)` / `SetParent` も同様。関連: `app/script_scene_ops.rs`、docs/scripting_api.md の注記。

- [ ] **`SEED.Vector2` / `SEED.Vector3` の `[SerializeField]` はインスペクタで編集できない** — 2026-09-07 に確認。`ScriptInspectorBuilder.BuildValueRow` は float/int/bool/string と参照型だけを扱い、それ以外は読み取り専用行になる（`[Serializable]` が無いので `Children` 展開にも乗らない）。色や座標を Vector3/Vector2 で公開している既存スクリプトは値を変えられない。float 2〜3 本に割るか、インスペクタ側に Vector 行を足すかの判断が要る。関連: `editor/src/Scripting/ScriptInspectorBuilder.cs`、`scripts/FishRadar.cs`、`scripts/FishingFight.cs`。

- [ ] **`GameObject.Parent` が O(N)** — DFS 走査で親を探す実装。毎フレーム大量に呼ぶ用途には向かない。

- [ ] **スクリプトのホットリロードでプールが二重生成される可能性** — 2026-09-07。`.cs` 保存でインスタンスが作り直されると `OnStart` が再実行される。旧インスタンスの `OnDestroy` が呼ばれる保証を未確認（FishingFight のビートアイコンプールで 16 個ずつ増える恐れ。Play 再開始で解消）。

- [ ] **`OnStart` 内 `Instantiate` の成否が未検証** — FishingFight のビートアイコンプールが初例。失敗するとリトライせず無効ハンドルが残る。関連: `runtime/assets/mainGame/scripts/FishingFight.cs::EnsureIconPool`。

- [ ] **docs の `Mathf.Clamp01(v)` が実装と食い違う** — 2026-09-07。実装は `Clamp01(ref float)`（void）で、値を返すのは `Clamped01(v)`。docs 4 章の記述を実装に合わせるか、値返し版を `Clamp01` として追加するかの判断が要る。関連: `docs/scripting_api.md` 4 章、`scripting/src/Api/Mathf.cs`。

- [ ] **ScriptEvent 内のアクタ名がアクタのリネームに追従しない** — 2026-09-07。結線 JSON に埋まった `actor` は `rename_refs.rs` の値ゲート（フィールド値そのものが旧名）に当たらない。構造体リスト内の参照メンバと同じ既存制限。対応するなら CLR に型タグ問い合わせ FFI を 1 本足し、`scriptevent` フィールドは JSON をパースして書き換える。関連: `runtime/src/engine/core/app_base/app/rename_refs.rs`、`scripting/src/Api/ScriptEvent.cs`。

- [ ] **スクリプト遷移時の地形 LOD 事前収束が同期でフェードを止め得る** — 2026-09-07。`install_loaded_scene` は Play 中に `converge_terrain_lod_blocking` を呼ぶ（無いと遷移後に長時間の低 fps）。地形規模によってはフェード中に一瞬固まる。気になれば遷移時のみ非同期収束に切り替える。関連: `app/app_init.rs::install_loaded_scene`、`app/script_scene_ops.rs`。

- [ ] **LOAD_SCENE（常駐 Play プロセス再利用）で `pointer.reset()` が呼ばれない** — 2026-09-07。旧シーンのホバー/押下エンティティを持ち越す可能性。スクリプト遷移経路と同じ理由でリセットすべきに見えるが、挙動維持のため `SceneInstallOptions.reset_pointer=false` のまま。関連: `app/ipc_handler.rs` の LOAD_SCENE。

- [ ] **プロローグ会話システムの実機未検証項目** — 2026-09-07。文字送り・送りマーク点滅・カメラ補間の見た目、日本語＋空白＋角括弧を含むフォントパス（ゆずポップ Regular）の実読み込み、CamTarget_* の高さ（目線位置は推定値）、CamTarget_Owner が Hut に埋まる可能性、Text の自動折り返し無し（`
` 手動改行）。関連: `runtime/assets/prologue/scripts/Dialogue/`、`proLogue.scene`。

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

- [ ] **`MessageBox.Show` の大半がまだ `EditorDialogs.Show` を通っていない** — 2026-09-07。ヘッドレスでモーダルが出ると UI スレッドが固まり MCP 呼び出しが全滅するため、AI 経路（Play / シーンロード / シーン保存 / 自動リロード / スクリプトコンパイル / LOAD_ERROR）だけを差し替えた。インスペクタ・地形・プロジェクト設定・スプライトリグ等の 30 箇所以上は素の `MessageBox.Show` のまま。順次 `SEEDEditor.Headless.EditorDialogs.Show` へ寄せる。関連: `editor/src/Headless/EditorDialogs.cs`。

- [ ] **ヘッドレスでの `seed_screenshot(target:"editor")` は真っ黒になる** — 2026-09-07。エディタ UI 全体は画面 DC からしか撮れず、ウィンドウが画面外にあると撮れない。WPF 側を `RenderTargetBitmap` でレンダリングして返す経路を作れば解決できるが、埋め込みランタイム部分は空になる（GPU 子ウィンドウは WPF のビジュアルツリーに無い）。

- [x] **ヘッドレス一連の実機 E2E 実施済み** — 2026-09-07。`seed_launch(headless) → seed_screenshot(gpu) → seed_play → seed_screenshot → seed_play(stop) → seed_shutdown` を MCP サーバー経由で実走行し、画面に何も出ないまま PNG（1068x568・実内容あり）を取得、プロセスも残らないことを確認した。

- [ ] **`seed_launch` が起動したエディタは MCP サーバー終了後も残る** — 2026-09-07。ジョブオブジェクトで括っていないため、`seed_shutdown` を忘れるとプロセスが残る。必要なら Job Object + `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` を検討。関連: `editor/SeedMcpServer/Launcher.cs`。

## 2D キャンバス（2026-09-07 の入れ子アンカー修正の残件）

- [ ] **CanvasComponent を持たないノードの pivot は「基準サイズ 1x1」で解決される** — 2026-09-07。`collect_sprite_items` / `collect_canvas_rects` / `pick_2d` は、自ノードに CanvasComponent が無いとき `to_mat4_sized(1.0, 1.0)` で子への座標系原点（`self_world_rs`）を作る。このため pivot が `pivot × 1px` の平行移動として残り、Sprite（例: pivot=(0.5,0.5)）の子は**最大 1px** 親の中心からずれる。目視できない量だが規約としては誤り。「Canvas 領域を持たないノードの pivot は子の座標系に影響しない（＝ 0 基準）」へ寄せるのが筋。影響が全入れ子 2D に及ぶため単独タスクで実施する。関連: `runtime/src/engine/core/app_base/app/canvas_collect.rs`（`my_eff_w/h` の `.unwrap_or((1.0, 1.0))`）、`physics2d_ops.rs`（`canvas_eff_w/h`）。

- [ ] **2D ノードのレイアウト計算が 5 か所に重複コピーされている** — 2026-09-07。`collect_sprite_items` / `collect_canvas_rects` / `collect_canvas_id_items` / `pick_2d::walk_pick_candidates_2d` / `physics2d_ops::collect_actor2d_contexts` が、root_auto 上書き → eff_viewport → アンカー → eff_ct → size_scale → self_world_rs → 子への継承、という同じ 60〜80 行をそれぞれ持っている。今回の「子のアンカー基準サイズ」バグは、この重複のうち 1 か所（`physics2d_ops` のギズモ用アンカー）だけ挙動が違ったせいで「ギズモは正しいのに描画がずれる」という形で表面化した。アンカー部分は共通ヘルパー（`node_anchor_offset` / `child_anchor_basis`）へ切り出したが、残りは未統合。`CanvasNodePlacement::resolve()` のような純関数へ一本化したい。関連: 上記 5 ファイル。

- [ ] **入れ子 2D の anchor 仕様がドキュメント化されていない** — 2026-09-07。「anchor は**親の CanvasComponent 領域**に対する比率で、Canvas 領域を持たない親（Sprite など）の子では anchor は効かない（親の原点＝親の position 点が基準）」という規則を docs 側に明記する。エディタのインスペクタでも、親が Canvas 領域を持たないときは anchor 欄をグレーアウトするのが親切。
