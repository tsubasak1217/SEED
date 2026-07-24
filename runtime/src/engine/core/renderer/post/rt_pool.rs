// ============================================================
//  rt_pool.rs — 名前付きレンダーターゲットプール（Phase R3 土台）
//
//  ポストプロセスで使うオフスクリーンターゲット（シーン HDR・中間バッファ等）を
//  「名前」で確保・再利用する。サイズ／フォーマットが変わったら自動で再生成する
//  （ウィンドウリサイズ追従）。
//
//  R4 のブルーム（しきい値→ダウンサンプルチェーン→合成）など、複数の一時 RT を
//  要するパスも同じプールから名前で確保する前提の設計。
// ============================================================

use std::collections::HashMap;

/// プールが保持する 1 枚のレンダーターゲット。
struct RtEntry {
    /// GPU テクスチャ本体（RENDER_ATTACHMENT | TEXTURE_BINDING | COPY_SRC | COPY_DST）。
    texture: wgpu::Texture,
    /// 描画・サンプリング両用のビュー。
    view: wgpu::TextureView,
    width: u32,
    height: u32,
    format: wgpu::TextureFormat,
}

/// 名前付きレンダーターゲットプール。
///
/// `ensure` で確保（既存が同サイズ・同フォーマットなら再利用）し、`view` でビューを得る。
/// フレームをまたいでテクスチャを保持するため、毎フレームの再生成コストが無い。
pub struct RtPool {
    entries: HashMap<String, RtEntry>,
}

impl Default for RtPool {
    fn default() -> Self {
        Self::new()
    }
}

impl RtPool {
    /// 空のプールを生成する。
    pub fn new() -> Self {
        Self {
            entries: HashMap::new(),
        }
    }

    /// 名前付きターゲットを確保する。
    ///
    /// 既存エントリが同じ (幅, 高さ, フォーマット) なら何もしない（再利用）。
    /// 異なる場合・未確保の場合は新規生成して差し替える。
    /// 用途は RENDER_ATTACHMENT | TEXTURE_BINDING 固定（ポスト入出力の両用）。
    pub fn ensure(
        &mut self,
        device: &wgpu::Device,
        name: &str,
        width: u32,
        height: u32,
        format: wgpu::TextureFormat,
    ) {
        // 0 サイズはテクスチャ生成が不正になるため 1 にクランプする。
        let width = width.max(1);
        let height = height.max(1);

        // 既存が一致していれば再利用（早期リターン）。
        if let Some(e) = self.entries.get(name) {
            if e.width == width && e.height == height && e.format == format {
                return;
            }
        }

        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some(name),
            size: wgpu::Extent3d {
                width,
                height,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format,
            // RENDER_ATTACHMENT | TEXTURE_BINDING（ポスト入出力の両用）に加え、
            // COPY_SRC | COPY_DST を許可する。RT-Translucency の屈折で scene_hdr を
            // refract_bg へ copy_texture_to_texture するために必要（両テクスチャとも同プール）。
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT
                | wgpu::TextureUsages::TEXTURE_BINDING
                | wgpu::TextureUsages::COPY_SRC
                | wgpu::TextureUsages::COPY_DST,
            view_formats: &[],
        });
        let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
        self.entries.insert(
            name.to_string(),
            RtEntry {
                texture,
                view,
                width,
                height,
                format,
            },
        );
    }

    /// 確保済みターゲットのビューを返す（`ensure` 済みであること）。
    ///
    /// # panics
    /// 未確保の名前を指定した場合はパニックする（呼び出し側の確保漏れバグを早期発見するため）。
    pub fn view(&self, name: &str) -> &wgpu::TextureView {
        &self
            .entries
            .get(name)
            .unwrap_or_else(|| {
                panic!("RtPool: ターゲット '{name}' が未確保（ensure を先に呼ぶこと）")
            })
            .view
    }

    /// 確保済みターゲットのテクスチャ本体を返す（`ensure` 済みであること）。
    /// copy_texture_to_texture（屈折の scene_hdr→refract_bg コピー）で使う。
    ///
    /// # panics
    /// 未確保の名前を指定した場合はパニックする。
    pub fn texture(&self, name: &str) -> &wgpu::Texture {
        &self
            .entries
            .get(name)
            .unwrap_or_else(|| {
                panic!("RtPool: ターゲット '{name}' が未確保（ensure を先に呼ぶこと）")
            })
            .texture
    }
}
