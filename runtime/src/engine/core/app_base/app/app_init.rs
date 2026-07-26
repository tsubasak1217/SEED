// ============================================================
//  app_init.rs — App 初期化処理
//
//  【含む処理】
//  - handle_resumed: ApplicationHandler::resumed の実装本体
//    （ウィンドウ生成、GPU 初期化、IPC READY 通知）
//  - init_asset_fs: アセットファイルシステムの初期化
//  - load_play_scene: Play モードのシーン自動ロード
// ============================================================

use std::sync::Arc;

use winit::event_loop::ActiveEventLoop;

use crate::engine::core::renderer::Renderer;
use crate::engine::core::window::{create_window, WindowConfig};
use crate::engine::methods::drawer::{DrawContext, IdBuffer};
use crate::engine::structs::tensor::Vector3;

use super::{App, RuntimeMode};

/// デバッグカメラの初期ワールド位置 [x, y, z]。
const DEFAULT_CAMERA_POSITION: [f32; 3] = [0.0, 2.0, -10.0];

impl App {
    /// ApplicationHandler::resumed の実装本体。
    ///
    /// ウィンドウを生成し、GPU レンダラーと DrawContext を初期化する。
    /// 初期化完了後に IPC へ `READY:{hwnd}` を通知する。
    /// Play モードの場合は続いてシーンを自動ロードする。
    pub(super) fn handle_resumed(&mut self, event_loop: &ActiveEventLoop) {
        eprintln!("[SEED INIT] handle_resumed start  mode={:?}", self.mode);
        // プロジェクト設定のウィンドウ解像度を全モードで一度だけ読み込みキャッシュする。
        // カメラ新規追加時の既定アスペクト比・ルートキャンバスの自動解像度計算に使用する。
        self.project_resolution = self.load_window_size_from_settings();
        // Play・スタンドアロン時はプロジェクト設定のウィンドウ解像度を初期サイズに使う。
        // Edit（エディタ埋め込み）は WPF コンテナが実サイズを支配するため指定不要。
        let physical_size = if self.mode == RuntimeMode::Play {
            Some(self.project_resolution)
        } else {
            None
        };
        let window = Arc::new(create_window(event_loop, &WindowConfig {
            parent_hwnd: self.parent_hwnd,
            physical_size,
            ..WindowConfig::default()
        }));

        // GPU 初期化（wgpu DX12 デバイス + シェーダーコンパイル）は数秒かかることがある。
        // この間はメインスレッドがブロックされるため、ウィンドウを表示してしまうと
        // Windows に「応答なし」と判定される。
        // → 初期化完了後に表示することで "Not Responding" を回避する。
        eprintln!("[SEED INIT] Renderer::new() start");
        let renderer = Renderer::new(window.clone());
        eprintln!("[SEED INIT] Renderer::new() done");

        let size = window.inner_size();
        self.camera.set_aspect_ratio(size.width, size.height);

        // デバッグカメラを既定位置に配置する
        self.camera.base.transform.position = Vector3::new(
            DEFAULT_CAMERA_POSITION[0],
            DEFAULT_CAMERA_POSITION[1],
            DEFAULT_CAMERA_POSITION[2],
        );

        // scene_format = HDR オフスクリーン（Rgba16Float）。シーン描画パイプラインは
        // これでビルドし、トーンマップ後の直描き（プレビューブリット）のみ surface_format。
        let ctx = DrawContext::new(
            renderer.device(),
            renderer.queue(),
            crate::engine::core::renderer::HDR_FORMAT,
            renderer.surface_format(),
            renderer.depth_format(),
            renderer.pipeline_cache(),
        );
        eprintln!("[SEED INIT] DrawContext created");

        let scene = crate::engine::core::app_base::scene::Scene::new("Untitled");
        let camera_buf = ctx.create_camera_buffer();
        let id_buffer  = IdBuffer::new(&ctx.device, size.width, size.height);
        let line_model_buf = ctx.create_identity_model_bg_for_unlit();

        let canvas_overlay_camera_buf = ctx.create_camera_buffer();

        self.draw_ctx      = Some(ctx);
        self.scene         = Some(scene);
        self.camera_buf    = Some(camera_buf);
        self.canvas_overlay_camera_buf = Some(canvas_overlay_camera_buf);
        self.id_buffer     = Some(id_buffer);
        self.line_model_buf = Some(line_model_buf);

        // 軸ギズモ・アイコンオーバーレイ（エディタモードのみ初期化）
        if self.mode == RuntimeMode::Edit {
            use crate::engine::core::font::axis_gizmo::AxisGizmo;
            use crate::engine::core::font::icon_overlay::IconOverlay;
            let dev = &self.draw_ctx.as_ref().unwrap().device;
            let que = &self.draw_ctx.as_ref().unwrap().queue;
            // 軸ギズモ・アイコンオーバーレイはメインパス（HDR オフスクリーン）へ描くため
            // HDR_FORMAT でビルドする（Phase R3）。
            let scene_fmt = crate::engine::core::renderer::HDR_FORMAT;
            self.axis_gizmo = Some(AxisGizmo::new(
                dev,
                scene_fmt,
                renderer.depth_format(),
            ));
            self.icon_overlay = Some(IconOverlay::new(
                dev,
                que,
                scene_fmt,
                renderer.depth_format(),
            ));
            eprintln!("[SEED INIT] axis_gizmo + icon_overlay created");
        }

        self.renderer = Some(renderer);
        self.window   = Some(window);
        self.clock    = crate::engine::core::clock::Clock::new();


        // asset_fs を初期化する（全モード共通）
        self.init_asset_fs();
        eprintln!("[SEED INIT] init_asset_fs done");

        // プラグインをロードする
        self.load_plugins();
        eprintln!("[SEED INIT] load_plugins done  count={}", self.plugin_registry.len());

        // シーンレジストリ（シーンマネージャ登録名 → パス）をロードする。
        // スクリプトの SEED.Scene.Load / Transition("名前") の名前解決に使う。
        self.load_scene_registry();

        // プロジェクト設定のグラフィックス項目（rt_shadows 等）を起動時に読み込む。
        self.load_graphics_settings();

        // Play モードでは指定シーン（または start_scene）を自動ロードする。
        // シーンロード・モデルロードはメインスレッドをブロックするため、
        // ウィンドウ表示より先に完了させる。
        if self.mode == RuntimeMode::Play {
            eprintln!("[SEED INIT] load_play_scene start  scene_path={:?}", self.scene_path);
            // シーン復元の所要時間を恒久計測する。
            // モデルキャッシュのヒット／ミスは体感ロード時間を桁で変えるため
            // （Sponza 級はミス時に glTF 再パース＋メッシュレット再構築が走る）、
            // 退行に気付けるよう完了ログに必ず実測値を残す。
            let t_load = std::time::Instant::now();
            self.load_play_scene();
            let load_ms = t_load.elapsed().as_secs_f64() * 1000.0;
            let actor_count = self.scene.as_ref().map(|s| s.actors.len()).unwrap_or(0);
            eprintln!("[SEED INIT] load_play_scene done  actors={actor_count} ({load_ms:.0}ms)");
            // 物理スレッドは初回フレームまで起動を遅延する。
            // ここで起動するとロード中に物理演算が進み、アクターが意図しない初期状態になる。
            // update_physics() / update_physics_2d() の先頭で自動起動される。
        }

        // ── ウィンドウ表示（全初期化完了後）────────────────────────────
        // GPU 初期化・シーンロード・プラグインロードはすべてメインスレッドをブロックする。
        // これらが完了してからウィンドウを表示することで、Windows が「応答なし」と
        // 誤判定するのを防ぐ。
        // 非表示ウィンドウへの request_redraw は WM_PAINT が配送されないため、
        // set_visible の直後に明示的に redraw を要求して最初のフレームを確実に描画する。
        if let Some(w) = &self.window {
            w.set_visible(true);
            w.request_redraw();
        }
        eprintln!("[SEED INIT] window set_visible + request_redraw");

        let hwnd = self.window_hwnd();
        if let Some(ipc) = &self.ipc {
            ipc.send(&format!("READY:{hwnd}"));
        }
        eprintln!("[SEED INIT] READY sent  hwnd=0x{hwnd:X}");
        self.send_hierarchy();
        // ロード済みプラグイン一覧をエディタへ通知する
        if !self.plugin_registry.is_empty() {
            let json = self.plugin_registry.to_json();
            if let Some(ipc) = &self.ipc {
                ipc.send(&format!("PLUGIN_LIST:{json}"));
            }
        }

        // ── ボクセル地形スモークテスト（常設デバッグフック）────────────────────
        // 環境変数 SEED_TERRAIN_SMOKE=1 のときだけ、地形を生成してカメラを合わせ、
        // 明確に地形を変形させる（盛り 1・掘り 1）。スクリーンショット検証専用で、
        // 通常の Play / Edit 実行では絶対に走らない。
        // 物理コリジョンのスモーク（SEED_TERRAIN_PHYS_SMOKE=1＋--mode=play）を優先する。
        // 地形の静的トライメッシュコライダーに落下球が乗ることを実機で検証する専用フック。
        if std::env::var("SEED_TERRAIN_PHYS_SMOKE").as_deref() == Ok("1") {
            self.run_terrain_physics_smoke();
        } else if std::env::var("SEED_TERRAIN_SMOKE").as_deref() == Ok("1") {
            self.run_terrain_smoke();
        }
    }

    /// アセットルートを解決して asset_fs を初期化する。
    ///
    /// - `self.assets_root` が指定されている場合はそれを使う
    /// - 未指定の場合は実行ファイルの隣にある assets/ フォルダを使う
    /// - assets.pak が実行ファイルの隣にあれば PAK モードで初期化する
    fn init_asset_fs(&self) {
        use crate::engine::asset_fs;
        use std::path::PathBuf;

        // アセットルートを決定する
        let assets_root: PathBuf = if let Some(root) = &self.assets_root {
            PathBuf::from(root)
        } else {
            // 実行ファイルの隣の assets/ ディレクトリを使う
            std::env::current_exe()
                .ok()
                .and_then(|p| p.parent().map(|d| d.join("assets")))
                .unwrap_or_else(|| PathBuf::from("assets"))
        };

        // 実行ファイルの隣に assets.pak があれば PAK モードにする
        let pak_path = assets_root.parent()
            .map(|dir| dir.join("assets.pak"))
            .filter(|p| p.exists());

        asset_fs::init(assets_root, pak_path.as_deref());
    }

    /// プロジェクト設定（project_settings.json）からゲームウィンドウの初期解像度を読む。
    ///
    /// asset_fs 初期化前（ウィンドウ生成前）に呼ばれるため、load_plugins と同じ
    /// アセットルート解決でファイルを直接読む。フィールドが無い・不正な場合は
    /// 既定の Full HD（エディタのプロジェクト設定の既定値と一致させる）。
    fn load_window_size_from_settings(&self) -> (u32, u32) {
        /// ウィンドウ初期解像度の既定値（Full HD。エディタ側の既定値と一致させる）
        const DEFAULT_WINDOW_SIZE: (u32, u32) = (1920, 1080);
        /// 解像度として受け付ける最小・最大値（異常値による生成失敗を防ぐ）
        const MIN_WINDOW_DIM: u64 = 160;
        const MAX_WINDOW_DIM: u64 = 7680;

        // アセットルートを解決する（load_plugins と同一ロジック）
        let assets_root = if let Some(root) = &self.assets_root {
            std::path::PathBuf::from(root)
        } else {
            std::env::current_exe()
                .ok()
                .and_then(|p| p.parent().map(|d| d.join("assets")))
                .unwrap_or_else(|| std::path::PathBuf::from("assets"))
        };

        let settings_path = assets_root.join("project_settings.json");
        let Ok(text) = std::fs::read_to_string(&settings_path) else { return DEFAULT_WINDOW_SIZE };
        let Ok(v) = serde_json::from_str::<serde_json::Value>(&text) else { return DEFAULT_WINDOW_SIZE };

        // 幅・高さを個別に取り出し、範囲内ならペアで採用する（片方欠けは既定値）
        let (w, h) = (
            v["window_width"].as_u64().unwrap_or(0),
            v["window_height"].as_u64().unwrap_or(0),
        );
        if (MIN_WINDOW_DIM..=MAX_WINDOW_DIM).contains(&w)
            && (MIN_WINDOW_DIM..=MAX_WINDOW_DIM).contains(&h)
        {
            (w as u32, h as u32)
        } else {
            DEFAULT_WINDOW_SIZE
        }
    }

    /// プロジェクトのプラグインフォルダからプラグインをロードする。
    ///
    /// プラグインフォルダ: `{assets_root}/../plugins/`
    /// 有効化リスト: `{assets_root}/project_settings.json` の plugins フィールド
    fn load_plugins(&mut self) {
        use crate::engine::plugin::registry::PluginRegistry;
        use crate::engine::plugin::manifest::PluginEntry;

        // アセットルートを解決する
        let assets_root = if let Some(root) = &self.assets_root {
            std::path::PathBuf::from(root)
        } else {
            std::env::current_exe()
                .ok()
                .and_then(|p| p.parent().map(|d| d.join("assets")))
                .unwrap_or_else(|| std::path::PathBuf::from("assets"))
        };

        // プラグインフォルダ = assets の隣の plugins/ ディレクトリ
        let plugins_dir = assets_root.parent()
            .map(|p| p.join("plugins"))
            .unwrap_or_else(|| std::path::PathBuf::from("plugins"));

        // project_settings.json から有効化リストを読み込む
        let settings_path = assets_root.join("project_settings.json");
        let enabled_list: Vec<PluginEntry> = if settings_path.exists() {
            let text = std::fs::read_to_string(&settings_path).unwrap_or_default();
            // plugins フィールドだけ取り出す
            serde_json::from_str::<serde_json::Value>(&text)
                .ok()
                .and_then(|v| v.get("plugins").cloned())
                .and_then(|arr| serde_json::from_value(arr).ok())
                .unwrap_or_default()
        } else {
            Vec::new()
        };

        self.plugin_registry = PluginRegistry::load_from_dir(&plugins_dir, &enabled_list);
        eprintln!("[App] プラグイン {} 件ロード完了", self.plugin_registry.len());
    }

    /// Play モードでシーンをロードする。
    ///
    /// - `self.scene_path` が指定されている場合: そのシーンを読む（エディタ「現在のシーンでプレイ」）
    /// - 未指定の場合: `assets://project_settings.json` の `start_scene` を読む
    ///
    /// ロードしたシーンの debug_camera データをデバッグカメラの初期位置に適用する。
    /// メインカメラが存在しない場合のフォールバックとして機能する。
    /// project_settings.json の "scenes" 配列からシーンレジストリを読み込む。
    ///
    /// 形式: [{"name": "game", "path": "assets://scenes/game.scene"}, ...]
    /// （エディタのプロジェクト設定「シーンマネージャ」で登録・編集する）。
    /// ファイルや配列が無い場合は空レジストリのまま（名前解決は全て失敗し、
    /// スクリプトはパス直接指定のみ使用可能）。
    pub(super) fn load_scene_registry(&mut self) {
        use crate::engine::asset_fs;

        self.scene_registry.clear();
        let Ok(json) = asset_fs::read_string("assets://project_settings.json") else { return };
        let Ok(v) = serde_json::from_str::<serde_json::Value>(&json) else { return };
        let Some(scenes) = v["scenes"].as_array() else { return };
        for entry in scenes {
            let name = entry["name"].as_str().unwrap_or("");
            let path = entry["path"].as_str().unwrap_or("");
            if !name.is_empty() && !path.is_empty() {
                self.scene_registry.insert(name.to_string(), path.to_string());
            }
        }
        eprintln!("[SEED INIT] scene registry loaded  count={}", self.scene_registry.len());
    }

    /// project_settings.json のグラフィックス関連設定を読み込み、App の対応フィールドへ反映する。
    ///
    /// 現状は `rt_shadows`（インラインレイトレ影の有効フラグ）のみ。
    /// エディタからは IPC の `RT_SHADOWS:1` / `RT_SHADOWS:0` でも実行中に切替可能（起動時はここが初期値）。
    /// ファイルが無い／パース不可／キーが無い場合は既定値 false のまま変更しない。
    pub(super) fn load_graphics_settings(&mut self) {
        use crate::engine::asset_fs;

        let Ok(json) = asset_fs::read_string("assets://project_settings.json") else { return };
        let Ok(v) = serde_json::from_str::<serde_json::Value>(&json) else { return };
        // 影方式（旧キー rt_shadows: bool）→ ShadowMode。既定 false=ShadowMap（後方互換）。
        self.render_features.shadow = if v["rt_shadows"].as_bool().unwrap_or(false) {
            crate::engine::core::renderer::ShadowMode::Rt
        } else {
            crate::engine::core::renderer::ShadowMode::ShadowMap
        };
        // ビネットポストパス（Phase R3, 既定 OFF）。キーが無ければ false のまま。
        self.post_vignette_enabled = v["post_vignette"].as_bool().unwrap_or(false);
        // ブルーム／FXAA（Phase R4, 既定すべて OFF）。読み側は unwrap_or でデフォルト維持。
        // project_settings.json はコミットしないため、キーが無くても後方互換で OFF になる。
        use crate::engine::core::renderer::{
            DEFAULT_BLOOM_THRESHOLD, DEFAULT_BLOOM_KNEE, DEFAULT_BLOOM_INTENSITY,
        };
        self.post_fx.bloom_enabled   = v["bloom"].as_bool().unwrap_or(false);
        self.post_fx.bloom_threshold = v["bloom_threshold"].as_f64().unwrap_or(DEFAULT_BLOOM_THRESHOLD as f64) as f32;
        self.post_fx.bloom_knee      = v["bloom_knee"].as_f64().unwrap_or(DEFAULT_BLOOM_KNEE as f64) as f32;
        self.post_fx.bloom_intensity = v["bloom_intensity"].as_f64().unwrap_or(DEFAULT_BLOOM_INTENSITY as f64) as f32;
        self.post_fx.fxaa_enabled    = v["fxaa"].as_bool().unwrap_or(false);
        // 透明描画方式（Phase R5, 既定 "sort" = 距離ソート）。キー欠落時も後方互換で距離ソート。
        self.post_fx.transparency = crate::engine::core::renderer::TransparencyMode::from_str(
            v["transparency"].as_str().unwrap_or("sort"),
        );
        // メッシュレットカリングは常時有効化したため設定キー "meshlet_cull" は読み込まない
        //（旧プロジェクトの project_settings.json に本キーが残っていても無視する＝互換維持）。
        // Deferred（G-Buffer）レンダリング（Phase D3 Deferred Phase B, 既定 true）。キー欠落時も有効。
        // OFF で従来のフォワード経路へ完全フォールバックする（A/B パリティ検証用）。
        self.post_fx.deferred = v["deferred"].as_bool().unwrap_or(true);
        // RT屈折の逐次グラブ（既定 false）。キー欠落時は無効＝従来の 1 回グラブ共有挙動を維持する。
        // ガラス 1 個描画ごとに背景ミップチェーンを再グラブし、ガラス越しガラスの多重屈折を表現する重い設定。
        self.post_fx.refract_sequential_grab = v["refract_sequential_grab"].as_bool().unwrap_or(false);
        // DDGI（Phase RT-GI, 既定 有効・強度 1.0）。既定値から出発し、存在するキーだけ上書きする。
        // enabled は RT 非対応 GPU では実行時に強制無効化される（frame_renderer のゲート）。
        // GI 方式（旧キー gi_enabled: bool）→ GiMode。旧既定は enabled=true のため、
        // キー欠落時は Rt（従来「GI 有効」）へ写像し現状の見た目を維持する。
        self.render_features.gi = if v["gi_enabled"].as_bool().unwrap_or(true) {
            crate::engine::core::renderer::GiMode::Rt
        } else {
            crate::engine::core::renderer::GiMode::Flat
        };
        if let Some(x) = v["gi_intensity"].as_f64()         { self.post_fx.gi.intensity = x as f32; }
        if let Some(x) = v["gi_probes_per_frame"].as_u64()  { self.post_fx.gi.probes_per_frame = x as u32; }
        if let Some(x) = v["gi_rays_per_probe"].as_u64()    { self.post_fx.gi.rays_per_probe = x as u32; }
        if let Some(x) = v["gi_hysteresis"].as_f64()        { self.post_fx.gi.hysteresis = x as f32; }
        if let Some(x) = v["gi_recursive_weight"].as_f64()  { self.post_fx.gi.recursive_weight = x as f32; }
        // 反射（SSR / RT）強度（Phase D6）。欠落時は既定 1.0 を維持。
        if let Some(x) = v["reflection_intensity"].as_f64() { self.post_fx.reflection_intensity = x as f32; }
        if let Some(x) = v["ao_intensity"].as_f64() { self.post_fx.ao_intensity = x as f32; }
        // 新キー features（あれば機能マトリクス全体を上書き。project_settings.json 将来対応）。
        if let Some(fv) = v.get("features") {
            if let Ok(f) = serde_json::from_value::<crate::engine::core::renderer::RenderFeatures>(fv.clone()) {
                self.render_features = f;
            }
        }
        // 環境光（Phase R1.5, 既定 白 × 0.05 ＝従来のハードコード値）。読み側は unwrap_or でデフォルト維持。
        // `ambient_color` は [r,g,b] 配列（欠落・不正時は白）。`ambient_intensity` は 0..1 想定（0 で暗闇）。
        use crate::engine::core::renderer::{DEFAULT_AMBIENT_COLOR, DEFAULT_AMBIENT_INTENSITY};
        if let Some(arr) = v["ambient_color"].as_array() {
            if arr.len() == 3 {
                self.ambient_color = [
                    arr[0].as_f64().unwrap_or(DEFAULT_AMBIENT_COLOR[0] as f64) as f32,
                    arr[1].as_f64().unwrap_or(DEFAULT_AMBIENT_COLOR[1] as f64) as f32,
                    arr[2].as_f64().unwrap_or(DEFAULT_AMBIENT_COLOR[2] as f64) as f32,
                ];
            }
        }
        self.ambient_intensity =
            v["ambient_intensity"].as_f64().unwrap_or(DEFAULT_AMBIENT_INTENSITY as f64) as f32;
        eprintln!(
            "[SEED INIT] graphics settings loaded  shadow={:?} post_vignette={} bloom={} fxaa={} transparency={} deferred={} seq_grab={} gi={:?} gi_intensity={}",
            self.render_features.shadow, self.post_vignette_enabled,
            self.post_fx.bloom_enabled, self.post_fx.fxaa_enabled,
            self.post_fx.transparency.as_str(), self.post_fx.deferred,
            self.post_fx.refract_sequential_grab,
            self.render_features.gi, self.post_fx.gi.intensity,
        );
    }

    /// render_features が前回ログ時から変わっていれば [SEED FEATURES] 行を 1 回出力する。
    /// 起動時・IPC 切替時の実効モード（降格・未実装の注記を含む）を可視化する集約点。
    pub(crate) fn log_render_features_if_changed(&mut self) {
        let rt_sup = crate::engine::core::renderer::rt_shadow::rt_shadows_supported();
        let key = (self.render_features, rt_sup);
        if self.features_log_state != Some(key) {
            eprintln!("[SEED FEATURES] {}", self.render_features.log_line(rt_sup));
            // 反射は deferred（G-Buffer）有効時のみ動く独立パス。反射を要求していても
            // deferred が無効なら反射パスは一切走らないため、その旨を 1 行付記する（Phase D6）。
            if self.render_features.reflection != crate::engine::core::renderer::ReflectionMode::Off
                && !self.post_fx.deferred
            {
                eprintln!("[SEED FEATURES] 反射:deferred無効のため停止（反射は G-Buffer 有効時のみ動作）");
            }
            // SSGI も deferred（G-Buffer）有効時のみ動く独立パス。実効 GI が Ssgi でも deferred が
            // 無効なら SSGI は走らずフラットアンビエントで描画されるため、その旨を 1 行付記する（Phase SSGI）。
            if self.render_features.resolve(rt_sup).gi == crate::engine::core::renderer::GiMode::Ssgi
                && !self.post_fx.deferred
            {
                eprintln!("[SEED FEATURES] GI(SSGI):deferred無効のため停止（フラットアンビエントで描画）");
            }
            self.features_log_state = Some(key);
        }
    }

    pub(super) fn load_play_scene(&mut self) {
        use crate::engine::asset_fs;

        // ロードするシーンパスを決定する
        let scene_path_str: String = if let Some(path) = &self.scene_path {
            // エディタから --scene= で指定されたパス
            path.clone()
        } else {
            // project_settings.json の start_scene を読む
            let json = match asset_fs::read_string("assets://project_settings.json") {
                Ok(s) => s,
                Err(_) => return,
            };
            match serde_json::from_str::<serde_json::Value>(&json) {
                Ok(v) => {
                    let s = v["start_scene"].as_str().unwrap_or("").to_string();
                    if s.is_empty() { return; }
                    s
                }
                Err(_) => return,
            }
        };

        // PAK モードで resolve するとファイルシステム読みになるため、
        // 仮想パス (assets://...) のまま Scene::load に渡す。
        let scene_path = std::path::Path::new(&scene_path_str);

        let result = if let Some(ctx) = &self.draw_ctx {
            Some(crate::engine::core::app_base::scene::Scene::load(
                scene_path,
                ctx,
                self.scripting_host.as_ref(),
            ))
        } else {
            None
        };

        match result {
            Some(Ok((new_scene, cam_data))) => {
                // シーンに保存されたデバッグカメラ位置を適用する
                if let Some(cam) = cam_data {
                    self.apply_camera_data(&cam);
                }
                // 2D アクターが含まれていれば WL 0 をキャンバス世界線として登録する。
                // IPC の LoadScene 処理と同様に has_any_2d_actor を再帰チェックする。
                fn has_any_2d_actor(actors: &[crate::engine::structs::objects::Actor]) -> bool {
                    actors.iter().any(|a| a.is_2d() || has_any_2d_actor(a.children()))
                }
                if has_any_2d_actor(&new_scene.actors) {
                    self.canvas_world_lines.insert(0);
                } else {
                    self.canvas_world_lines.remove(&0);
                }
                self.scene = Some(new_scene);
                // 地形チャンク（TerrainChunkComponent 付き）を .tvox から復元し、
                // terrain:// ガードで model=None のまま読まれた ModelComponent を埋める。
                self.rebuild_terrain_after_load();

                // ── Play 開始前の地形 LOD 事前収束（ウィンドウ Play 経路）──────────────
                // ここは READY 送信・ウィンドウ表示より前のロードフェーズ。メインカメラ位置基準で
                // 全チャンクの目標 LOD を 1 回でまとめて再メッシュし、最初の描画フレームで
                // backlog をゼロにする。これにより Play 開始直後の毎フレーム分割収束
                // （約 20 秒間 8〜10fps）を回避し、起動時間へ前倒しする。
                let cam_pos = self.play_converge_camera_pos();
                self.converge_terrain_lod_blocking(cam_pos);
            }
            Some(Err(e)) => { eprintln!("[SEED INIT] load_play_scene FAILED: {e}"); }
            None => {}
        }
    }
}
