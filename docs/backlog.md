# 作業バックログ（未着手・保留の課題）

セッションやエージェントをまたいで共有する「今後やらないといけないこと」の一覧。
着手するときは該当項目を読み、完了したら項目を削除する（履歴は git に残る）。
新しい課題が見つかったら、ここに追記する（発見日・背景・関連ファイルを必ず書く）。

記法: `- [ ] 題名 — 背景 / 関連 / 備考`。優先度は上から順（高→低）。

## エディタ

- [ ] **アニメーションパネルが子アクタ選択で切り替わる問題** — 2026-09-07。パネルは選択アクタの Animator に連動するため、クリップの対象である子アクタを触ると空になる。
  案: (1) 選択アクタに Animator が無ければ先祖の Animator を保持、(2) 🔒 ロックトグル、(3)「選択アクタの現在値をキーに記録」ボタン（ビューポートで動かして記録）。
  関連: `editor/src/Panels/AnimationTimelinePanel.xaml.cs`（`OnSelectionChanged` / `OnActorComponentsReceived`）。当面の回避策は「直接開く」でファイル単独モード（プレビュー不可）。

- [ ] **Text / SkinnedSprite ノードの pivot が事実上効いていない** — 2026-09-07。`CanvasTransform::to_mesh_mat4(sx, sy)` が `to_sprite_mat4` に委譲しており、pivot オフセットが `pivot × size_scale`（≒0.5px）にしかならない。描画と枠は同じ行列なので位置ズレは無いが、Inspector の pivot を変えても回転中心が動かない。
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

## ランタイム / スクリプト API

- [ ] **`SEED.Draw`（2D プリミティブ）の未検証項目** — 2026-09-07。GPU の実描画（位置・重なり・アンチエイリアス、3D キャンバス上の深度）は目視未確認。同一 layer の並びは「スプライト → プリミティブ → テキスト」固定で、プリミティブでテキストを覆いたい場合は統合ソートが必要。`Arc` の Fill=リング / Outline=線 の意味は直感に反する可能性あり（`Ring` あり）。フェザー 1px 固定なので 3D キャンバス上では遠いと太く見える（解析 SDF 化は図形別シェーダが必要）。関連: `runtime/src/engine/core/renderer/primitive2d/`。

- [ ] **`SEED.Draw3D` の未検証項目** — 2026-09-07。リボンの押し出し向き・線幅の実測、半透明の上／2D UI の下に入るか、Play 中のビューポート矩形と uniform の一致。折れ線のジョイント処理なし・アンチエイリアスなし（仕様として docs 記載）。関連: `renderer/primitive3d/`。

- [ ] **プリミティブ描画キューが描画されないフレームで溜まる** — 最小化中などは `take_commands` が呼ばれず上限まで溜まって警告が出る（メモリは有界）。2D/3D 共通。

- [ ] **実行時に生成・付け替えしたアクタの物理コライダーが物理スレッドに反映されない** — 既存 Instantiate と同じ制約。`Instantiate(path, parent)` / `SetParent` も同様。関連: `app/script_scene_ops.rs`、docs/scripting_api.md の注記。

- [ ] **`SEED.Vector2` / `SEED.Vector3` の `[SerializeField]` はインスペクタで編集できない** — 2026-09-07 に確認。`ScriptInspectorBuilder.BuildValueRow` は float/int/bool/string と参照型だけを扱い、それ以外は読み取り専用行になる（`[Serializable]` が無いので `Children` 展開にも乗らない）。色や座標を Vector3/Vector2 で公開している既存スクリプトは値を変えられない。float 2〜3 本に割るか、インスペクタ側に Vector 行を足すかの判断が要る。関連: `editor/src/Scripting/ScriptInspectorBuilder.cs`、`scripts/FishRadar.cs`、`scripts/FishingFight.cs`。

- [ ] **`GameObject.Parent` が O(N)** — DFS 走査で親を探す実装。毎フレーム大量に呼ぶ用途には向かない。

- [ ] **スクリプトのホットリロードでプールが二重生成される可能性** — 2026-09-07。`.cs` 保存でインスタンスが作り直されると `OnStart` が再実行される。旧インスタンスの `OnDestroy` が呼ばれる保証を未確認（FishingFight のビートアイコンプールで 16 個ずつ増える恐れ。Play 再開始で解消）。

- [ ] **`OnStart` 内 `Instantiate` の成否が未検証** — FishingFight のビートアイコンプールが初例。失敗するとリトライせず無効ハンドルが残る。関連: `runtime/assets/mainGame/scripts/FishingFight.cs::EnsureIconPool`。

## ゲーム（わらしべフィッシング）

- [ ] **HIT 演出の帯の角度を変えるにはクリップの作り直しが必要** — 位置キーは θ=−12° を展開した実座標。回転トラックだけ変えても位置は追従しない。2026-09-07 にアイテムごとの 4 クリップへ分割（アンカーが違うため 1 本のクリップでまとめて動かせない）。関連: `runtime/assets/mainGame/animations/hit_banner_band_top.anim` ほか 3 本、`scripts/HitBanner.cs`。

- [ ] **HIT 演出・糸ゲージ・レーダーの見た目が未目視** — 2026-09-07。アイテムごとの Animator による帯／文字の同期、`Draw.Rect` の回転小片で描く糸ゲージ（旧 48 スプライトとの一致）、`Draw.RegularPolygon` の三角マーカー、プリミティブ化したレーダー背景円・中心点の大きさと色は、ランタイム再ビルド後の実機確認が必要。関連: `scripts/HitBanner.cs`、`scripts/FishingFight.cs`、`scripts/FishRadar.cs`。

- [ ] **未参照になったテクスチャ** — 2026-09-07 のレーダーのプリミティブ化で `radar_bg.png` / `radar_dot.png` がどのアクタからも参照されなくなった。他で使わないなら削除してよい。関連: `runtime/assets/mainGame/textures/ui/`。

- [ ] **レーダーの点・ビートアイコンの見た目確認** — 2026-09-07。`Draw.Circle` の点の位置・サイズ（`radarSpace` 相対のスケール一致）、`BeatIcon.actor` プールの出現位置は未目視。関連: `scripts/FishRadar.cs`、`scripts/FishingFight.cs`。

## 未コミットの他セッション差分（要確認）

- [ ] **`app_init.rs` / `ipc_handler.rs` / `script_scene_ops.rs` / `play_mode_ops.rs` に別セッションの未コミット変更** — 2026-09-07 時点。Play 開始時のシーン登録表再読込など。作業ツリーに残っているので、そのセッション側でコミットするか破棄するか判断する。
