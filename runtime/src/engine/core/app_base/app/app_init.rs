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
        let window = Arc::new(create_window(event_loop, &WindowConfig {
            parent_hwnd: self.parent_hwnd,
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
        let mmb_hud_cam_buf           = ctx.create_camera_buffer();

        self.draw_ctx      = Some(ctx);
        self.scene         = Some(scene);
        self.camera_buf    = Some(camera_buf);
        self.canvas_overlay_camera_buf = Some(canvas_overlay_camera_buf);
        self.mmb_hud_cam_buf           = Some(mmb_hud_cam_buf);
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

        self.sync_anim_seeds();

        // asset_fs を初期化する（全モード共通）
        self.init_asset_fs();
        eprintln!("[SEED INIT] init_asset_fs done");

        // プラグインをロードする
        self.load_plugins();
        eprintln!("[SEED INIT] load_plugins done  count={}", self.plugin_registry.len());

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
