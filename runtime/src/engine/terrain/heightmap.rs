// ============================================================
//  terrain/heightmap.rs — ハイトマップ画像 → 密度場サンプラ
//
//  【責務】
//    グレースケール画像（0..1 に正規化した輝度配列）を「ワールド座標 → 高さ」の
//    連続関数として扱い、SDF 密度（density = worldY - height）へ変換する。
//    このファイルは image クレートに依存しない（テストで image 抜きに組める）。
//    画像デコード（image::open → to_luma8）は terrain_ops.rs（エンジン層）が担い、
//    ここへは正規化済みの luma01 配列として渡す。
//
//  【マッピング規約】
//    画像を初期平地フットプリント（world x,z ∈ [0, footprint_w/d]）へ張る。
//    ワールド (wx, wz) → uv=(wx/footprint_w, wz/footprint_d) をバイリニア補間で
//    サンプルし、高さ h = luma01 * height_scale を得る。
//    density(wx, wy, wz) = wy - h（規約: density<iso(0)=SOLID なので wy<h で solid）。
// ============================================================

/// ハイトマップ画像から密度場をサンプリングするフィールド。
///
/// `luma01` は row-major（index = z*w + x）で正規化済み輝度（0.0=黒〜1.0=白）を持つ。
pub struct HeightmapField {
    /// 正規化済み輝度配列（row-major, 長さ = w*h）。
    pub luma01: Vec<f32>,
    /// 画像の横幅（ピクセル数）。
    pub w: usize,
    /// 画像の縦幅（ピクセル数）。
    pub h: usize,
    /// 画像を張り付けるワールド空間フットプリントの X 方向の広がり（メートル）。
    pub footprint_w: f32,
    /// 画像を張り付けるワールド空間フットプリントの Z 方向の広がり（メートル）。
    pub footprint_d: f32,
    /// 輝度 1.0（白）が対応する高さ（メートル）。
    pub height_scale: f32,
}

impl HeightmapField {
    /// ワールド座標 (wx, wz) における高さをバイリニア補間で返す。
    ///
    /// フットプリント範囲外の座標は端のピクセルへクランプする（画像の外側は
    /// エッジを引き延ばした扱いになる。地形は footprint 内にしか存在しないため実害はない）。
    pub fn height_at(&self, wx: f32, wz: f32) -> f32 {
        // ── ワールド座標 → uv ∈ [0,1] ──（footprint が 0 の異常値は 0 割りを避けて 0 扱い）
        let u = if self.footprint_w > 0.0 {
            (wx / self.footprint_w).clamp(0.0, 1.0)
        } else {
            0.0
        };
        let v = if self.footprint_d > 0.0 {
            (wz / self.footprint_d).clamp(0.0, 1.0)
        } else {
            0.0
        };

        // ── uv → 連続ピクセル座標（端は w-1 / h-1 にクランプ済みなので安全） ──
        let fx = u * (self.w.saturating_sub(1)) as f32;
        let fz = v * (self.h.saturating_sub(1)) as f32;
        let x0 = fx.floor() as usize;
        let z0 = fz.floor() as usize;
        let x1 = (x0 + 1).min(self.w.saturating_sub(1));
        let z1 = (z0 + 1).min(self.h.saturating_sub(1));
        let tx = fx - x0 as f32;
        let tz = fz - z0 as f32;

        let sample = |x: usize, z: usize| self.luma01[z * self.w + x];
        let c00 = sample(x0, z0);
        let c10 = sample(x1, z0);
        let c01 = sample(x0, z1);
        let c11 = sample(x1, z1);
        // x → z の順で線形補間（2 段バイリニア）。
        let c0 = c00 + (c10 - c00) * tx;
        let c1 = c01 + (c11 - c01) * tx;
        let g01 = c0 + (c1 - c0) * tz;

        g01 * self.height_scale
    }

    /// ワールド座標 (wx, wy, wz) における密度を返す（density = wy - height_at(wx,wz)）。
    ///
    /// 規約どおり wy < height（地表より下）で負（SOLID）、wy > height で正（AIR）になる。
    pub fn density_at(&self, wx: f32, wy: f32, wz: f32) -> f32 {
        wy - self.height_at(wx, wz)
    }
}
