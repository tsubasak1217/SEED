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

        let ctx = DrawContext::new(
            renderer.device(),
            renderer.queue(),
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
            self.axis_gizmo = Some(AxisGizmo::new(
                dev,
                renderer.surface_format(),
                renderer.depth_format(),
            ));
            self.icon_overlay = Some(IconOverlay::new(
                dev,
                que,
                renderer.surface_format(),
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
            self.load_play_scene();
            let actor_count = self.scene.as_ref().map(|s| s.actors.len()).unwrap_or(0);
            eprintln!("[SEED INIT] load_play_scene done  actors={actor_count}");
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
        self.rt_shadows = v["rt_shadows"].as_bool().unwrap_or(false);
        eprintln!("[SEED INIT] graphics settings loaded  rt_shadows={}", self.rt_shadows);
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
            }
            Some(Err(_)) => {}
            None => {}
        }
    }
}
