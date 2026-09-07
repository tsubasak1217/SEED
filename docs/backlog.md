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
- [ ] **`.icons` と画像寸法のキャッシュがホットリロードへ未接続** — 2026-09-07。`font::inline::invalidate_caches()` を用意したが、アセット更新の通知経路（`canvas_component_ops.rs` の `sprite_tex_cache.remove` と同じ場所）からはまだ呼んでいない。`.icons` を編集してもエディタを再起動するまで反映されない。
- [ ] **インライン画像のレイアウトが 1 フレームに 2 回解かれる** — 2026-09-07。グリフ描画（`canvas_text::append_item`）とスプライト収集（`canvas_collect::collect_inline_image_sprites`）が同じ `resolve_layout_with_images` を別々に呼ぶ。記法を含まない本文は角括弧の有無で早期に抜けるため通常は無視できるが、記法を多用する画面では 1 回に減らす余地がある（レイアウト結果をフレーム内キャッシュする等）。
- [ ] **レイアウト用フォントが描画用レジストリと別実体** — 2026-09-07。`font::layout_fonts` は GPU 非依存の層（スプライト収集）から寸法を測るために独自の `FontRegistry` を持つ。組み込みフォントは静的参照なので複製されないが、外部フォント（.ttf）を指定すると描画側と合わせて 2 部常駐する。共有したい場合は描画器のレジストリを `Arc<Mutex<..>>` 化する必要がある。
- [ ] **インライン画像に太さ・影のぼかしが効かない** — 2026-09-07（仕様として割り切り）。`weight` は SDF のしきい値操作なので画像には適用できず、`shadow_softness` もスプライト経路ではぼかせない。影自体は同じオフセットで落ちる。

## わらしべフィッシングのチュートリアルモード（2026-09-07 実装時）

- [ ] **説明窓の素材が未着（仮素材で実装済み）** — 2026-09-07。`runtime/assets/mainGame/actors/UI/TutorialWindow.actor` のミニキャラと吹き出しは `assets://mainGame/textures/ui/white.png` を着色した矩形、送りマークは prologue の `nextArrow.png` を流用している。差し替えは Sprite の `texture_path`（と `width` / `height`）を変えるだけでよく、スクリプトの変更は不要。
- [ ] **キーアイコンが仮画像** — 2026-09-07。`runtime/assets/mainGame/ui/tutorial.icons` の `key_w` / `key_s` / `key_a` / `key_d` / `mouse_l` はすべて `white.png` を指している。本番画像ができたら `.icons` の `path` を差し替えるだけで説明文（`[icon:key_w]` 等）へ反映される。なお `.icons` のキャッシュはホットリロード未接続なので、差し替え後はエディタの再起動が要る（本ファイル「Text のインライン画像記法の残件」参照）。
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

- [ ] **ポーズメニューを MainGame で実際に開く確認が未実施** — 2026-09-08。ポーズメニューは `FishingController` が Esc を拾って `PauseMenu.Toggle(pauseMenuActorPath)` でプレハブを動的生成する方式で、シーンには何も置いていない。利用者が MainGame.scene を開いたまま作業していたため Play での目視確認ができていない。確認すべきは (1) Esc で暗幕とメニューが出て `Time.Scale = 0` になるか、(2) カーソルが出て W/S・クリックで選択が動くか、(3) 「ゲームにもどる」で釣り操作とカーソルロックが元に戻るか、(4) 「図鑑」→ 図鑑シーン → Esc で MainGame へ戻れるか、の 4 点。
- [ ] **旧セーブキー `best_size:魚` が孤立している** — 2026-09-08。魚 prefab に日本語の表示名を入れる前は `Fish.DisplayName` が全種で既定名 `魚` を返していたため、既存の `runtime/save/save.json` には全魚種ぶんが混ざった `best_size:魚` が 1 件だけある。今後は `best_size:<日本語名>` で記録されるので、この旧キーはどこからも読まれない。移行するか消すかは要判断（移行先が特定できないので削除が妥当）。
- [ ] **`resolve_dll_path` がカレントディレクトリ基準で SEEDScripting.dll を探す** — 2026-09-08。`runtime/src/engine/core/scripting/mod.rs::resolve_dll_path` は `cwd/../scripting/bin/Debug/net9.0/SEEDScripting.dll` → `cwd/SEEDScripting.dll` の順で探す。ランタイムの作業ディレクトリは `RuntimeManager.ResolveWorkingDirectory` が「exe の 2 階層上が `target` のときだけ」リポジトリ側へ上げるため、`docs/editor_mcp.md §5.5` が推奨する `cargo build --target-dir <別ディレクトリ>` で作った SEED.exe を `SEED_RUNTIME_EXE` で使うと DLL が見つからず、ランタイムが起動しない（エディタ側は「ランタイムが接続しません」としか言わない）。回避策は出力先へ `SEEDScripting.dll` 一式を手でコピーすること。exe の位置からも探すか、環境変数で明示できるようにしたい。
- [ ] **`seed_launch(scene:)` が `assets://` パスを受け付けない** — 2026-09-08。`editor/SeedMcpServer/Launcher.cs` は `Path.GetFullPath(scenePath)` をそのまま `--scene` へ渡すため、`assets://zukan/zukan.scene` は `…\SEED\assets:\zukan\zukan.scene` という壊れたパスになり、シーンが読めないまま「ランタイムが接続しません」でタイムアウトする（原因が一切表示されない）。絶対パスなら正常に動く。`assets://` を assets ルート基準へ解決するか、少なくともエラーとして弾きたい。
- [ ] **キャンバスの `auto_scale` がカメラ基準解像度より大きいキャンバスを縮小しない** — 2026-09-08。カメラの `target_width/height` が 1280x720 のとき、`auto_scale: true` の 1920x1080 キャンバスは 1 単位＝描画ターゲット 1px で描かれ、中央 1280x720 の外に置いた要素は画面に出ない（ヘッドレス Play のスクリーンショットで実測）。今回は図鑑・ポーズメニューのキャンバスを 1280x720 にして回避した。既存の `FishingUI` は端をアンカー基準で置いているため実害が出ていないだけなので、`auto_scale` の意図（基準解像度へフィットさせる）どおりに効いているか要確認。
- [ ] **`[SerializeField]` の参照解決がシーン全体のアクタ名 DFS なので、同じプレハブを複数生成すると参照が 1 個目へ集まる** — 2026-09-08。`ScriptReference.ResolveEntity` → `ScriptHost.TryFindActor(actorName)` は最初に一致したアクタを返すため、子アクタへの参照を持つプレハブを `Instantiate` で複数並べると 2 個目以降が壊れる。そのため図鑑のカードはプレハブ動的生成をやめ、シーンへ固定で 4 枚並べる構成にした。プレハブ内参照を「自分のサブツリー優先」で解決できるようにすると、カードやリスト項目の量産がずっと素直になる。
- [ ] **`runtime/assets/tutorial/scripts` がアクセス拒否でスクリプト収集から毎回スキップされる** — 2026-09-08。エディタのログに `[ScriptCompiler] 読み取れないフォルダをスキップ … Access to the path … is denied.` が再読込のたびに出る。assets が別ドライブへのジャンクションであることに由来する権限の問題と思われる。
