// ============================================================
//  renderer/surface_lifecycle.rs — 描画サーフェスの破棄と再生成（Renderer 側）
//
//  【背景】
//  Android はアプリがバックグラウンドへ回るとネイティブウィンドウを破棄し、前面へ戻ると
//  別のウィンドウを作り直す。wgpu::Surface は生成時のウィンドウに結び付いているため、
//    ・suspended で手放し（release_surface）
//    ・resumed で同じ winit Window から作り直す（recreate_surface）
//  必要がある。デバイス・キュー・パイプライン・深度以外のレンダーターゲットは作り直さない。
//
//  【作り直しの制約】
//  全パイプラインは初回生成時のスワップチェーン形式（config.format）でビルド済みのため、
//  新しいサーフェスが同じ形式に対応していなければ使えない（その場合は作り直しを諦める）。
//  同じ端末・同じアダプタでは形式は変わらないので、実際に問題になることは想定していない。
//
//  呼び出し元は app/surface_lifecycle.rs（App 側のライフサイクル処理）。
// ============================================================

use std::sync::Arc;

use winit::window::Window;

use super::{DepthTexture, Renderer};

/// どの環境でも必ず対応している提示モード（WebGPU 仕様上 Fifo は全実装が対応する）。
/// 作り直したサーフェスが従来の提示モードに対応しなかったときの退避先。
const ALWAYS_SUPPORTED_PRESENT_MODE: wgpu::PresentMode = wgpu::PresentMode::Fifo;

impl Renderer {
    /// 描画サーフェスを保持しているか。
    ///
    /// false の間（Android のバックグラウンド中）はフレームを描いてはならない。
    pub fn has_surface(&self) -> bool {
        self.surface.is_some()
    }

    /// 描画サーフェスを手放す（ApplicationHandler::suspended から呼ぶ）。
    ///
    /// ネイティブウィンドウの破棄より前に必ず手放すこと。破棄済みウィンドウを指す
    /// サーフェスへ present するとドライバがクラッシュする。既に無ければ何もしない。
    pub fn release_surface(&mut self) {
        if self.surface.take().is_some() {
            eprintln!("[SEED SURFACE] released（ウィンドウ破棄に備えてサーフェスを手放しました）");
        }
    }

    /// 同じ winit Window から描画サーフェスを作り直し、構成する（2 回目以降の resumed から呼ぶ）。
    ///
    /// 大きさはウィンドウの現在の実サイズ（0 のときは直前の大きさ）を使う。
    /// 深度テクスチャは描画解像度が変わった場合だけ作り直す。
    ///
    /// # 戻り値
    /// 作り直して構成できたら true。サーフェス生成に失敗した・アダプタが非対応・
    /// 形式が従来と合わないときは false（描画は止めたままにする）。
    pub fn recreate_surface(&mut self, window: Arc<Window>) -> bool {
        let window_size = window.inner_size();

        let surface = match self.instance.create_surface(window) {
            Ok(surface) => surface,
            Err(err) => {
                eprintln!("[SEED SURFACE][ERROR] サーフェスの再生成に失敗しました: {err}");
                return false;
            }
        };
        if !self.adapter.is_surface_supported(&surface) {
            eprintln!("[SEED SURFACE][ERROR] 選択済みアダプタが新しいサーフェスに対応していません");
            return false;
        }

        let caps = surface.get_capabilities(&self.adapter);
        if !caps.formats.contains(&self.config.format) {
            eprintln!(
                "[SEED SURFACE][ERROR] 新しいサーフェスが従来の形式 {:?} に対応していません（対応: {:?}）",
                self.config.format, caps.formats
            );
            return false;
        }
        if !caps.present_modes.contains(&self.config.present_mode) {
            eprintln!(
                "[SEED SURFACE][WARN] 提示モード {:?} が非対応になったため {:?} へ切り替えます",
                self.config.present_mode, ALWAYS_SUPPORTED_PRESENT_MODE
            );
            self.config.present_mode = ALWAYS_SUPPORTED_PRESENT_MODE;
        }
        if !caps.alpha_modes.contains(&self.config.alpha_mode) {
            if let Some(&alpha_mode) = caps.alpha_modes.first() {
                self.config.alpha_mode = alpha_mode;
            }
        }

        // 大きさが取れたときだけ更新する（0 のままでは構成できないので直前の値で構成する）。
        if window_size.width > 0 && window_size.height > 0 {
            self.size = window_size;
            self.config.width = window_size.width;
            self.config.height = window_size.height;
        }
        surface.configure(&self.device, &self.config);
        self.surface = Some(surface);

        // 深度は「描画解像度」に追従させる（resize / begin_frame と同じ規則）。
        let render_size = self.render_size();
        if render_size.width != self.depth_texture.width
            || render_size.height != self.depth_texture.height
        {
            self.depth_texture =
                DepthTexture::new(&self.device, render_size.width, render_size.height);
        }

        eprintln!(
            "[SEED SURFACE] recreated {}x{} format={:?} present_mode={:?}",
            self.config.width, self.config.height, self.config.format, self.config.present_mode,
        );
        true
    }
}
