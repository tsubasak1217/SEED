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
        let window = Arc::new(create_window(event_loop, &WindowConfig {
            parent_hwnd: self.parent_hwnd,
            ..WindowConfig::default()
        }));

        // スタンドアロンモードでは GPU 初期化前にウィンドウを即表示する。
        // Renderer::new()（wgpu DX12 デバイス作成）は数秒かかることがあり、
        // 初期化完了まで画面に何も出ない問題を防ぐ。
        // 埋め込みモードはエディタ側コンテナが背景を描くため従来どおり後で表示。
        if !self.is_embedded() {
            window.set_visible(true);
        }

        let renderer = Renderer::new(window.clone());

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

        let scene = crate::engine::core::app_base::scene::Scene::new("Untitled");
        let camera_buf = ctx.create_camera_buffer();
        let id_buffer  = IdBuffer::new(&ctx.device, size.width, size.height);
        let line_model_buf = ctx.create_identity_model_bg_for_unlit();

        if self.is_embedded() {
            // 非表示ウィンドウへの request_redraw は WM_PAINT が配送されず
            // RedrawRequested が発火しないため、常に可視化してから redraw を要求する。
            // 起動中の白フラッシュはエディタ側コンテナの WM_ERASEBKGND 黒塗りで対処する。
            window.set_visible(true);
            window.request_redraw();
        } else {
            // スタンドアロンモードは resumed() 冒頭で set_visible 済み
            window.set_visible(true); // 冪等（すでに表示中）
        }

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
        }

        self.renderer = Some(renderer);
        self.window   = Some(window);
        self.clock    = crate::engine::core::clock::Clock::new();

        self.sync_anim_seeds();

        // asset_fs を初期化する（全モード共通）
        self.init_asset_fs();

        let hwnd = self.window_hwnd();
        if let Some(ipc) = &self.ipc {
            ipc.send(&format!("READY:{hwnd}"));
        }
        self.send_hierarchy();

        // Play モードでは指定シーン（または start_scene）を自動ロードする
        if self.mode == RuntimeMode::Play {
            self.load_play_scene();
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
                self.scene = Some(new_scene);
            }
            Some(Err(_)) => {}
            None => {}
        }
    }
}
