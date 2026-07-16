// ============================================================
//  clustered.rs — Clustered Lighting（3D フロクセル単位のライトカリング）
//
//  視錐台を「X×Y タイル × Z 深度スライス」の 3D フロクセル（クラスタ）へ分割し、
//  compute シェーダ（shaders/cluster_build.wgsl）でクラスタごとに影響ライトの
//  インデックスを集める。フラグメント（shaders/lighting_eval.wgsl）は
//  「自分のクラスタのライトリスト ＋ 全平行光」だけを走査する。
//
//  【本モジュールの責務（単一責任）】
//    - クラスタ分割数などの定数（WGSL 側 cluster_common.wgsl と一致）
//    - GPU バッファ一式（グリッド / インデックスリスト / カーソル / パラメータ）
//    - 毎フレームのパラメータ更新と compute ディスパッチ
//    - クラスタ幾何（Z スライス・クラスタ AABB・球 vs AABB）の **CPU ミラー**
//      → WGSL と同一ロジックをユニットテストで検証するために持つ（実行時には使わない）
//    - ライト配列の「平行光を先頭へ」安定分割（クラスタ対象＝局所ライトの範囲確定）
//
//  【カメラごとに固有であること（最重要）】
//  クラスタはカメラの near/far/fov/ビューポートに依存するため、**カメラごとに固有**である。
//  エディタはメインカメラの描画パスとカメラプレビューの描画パスを別々に回している
//  （app/frame_renderer.rs）。メインカメラ基準で作ったクラスタをプレビューへ適用すると
//  プレビューのライティングが壊れる。
//  本実装は方式 (b)「クラスタ無効モード」を採る:
//    - ClusterParams.enabled = 1（メインカメラ用パラメータ）→ クラスタのリストを走査
//    - ClusterParams.enabled = 0（プレビュー用パラメータ）  → 従来どおり全ライトを線形走査
//  ライトバッファ・クラスタバッファは共有したまま、**ClusterParams uniform が異なる**
//  2 つの group 4 BindGroup を持つ（LightBuffer::bind_group(LightingPass::…) で選ぶ）。
//  プレビューは無効側を bind するため、クラスタの内容に一切依存しない＝壊れない。
//  なお、同じく**カメラ固有**であるシャドウ資源（CSM）も同じ 2 本の BindGroup で
//  メイン用／プレビュー用に差し替わっている（lighting.rs / shadow.rs 参照）。
//  透視カメラ以外（2D オルソ・正射ゲームカメラ）でも enabled=0 にして線形走査へ落とす。
// ============================================================

use wgpu::util::DeviceExt;

use super::lighting::{GpuLight, LIGHT_KIND_DIRECTIONAL, MAX_LIGHTS};

// ─── クラスタ分割数（WGSL: cluster_common.wgsl と一致必須）─────
//
// 一致は本ファイル下部のユニットテスト（wgsl_constants_match_rust）が担保する。

/// 画面横方向のタイル数。
pub const CLUSTER_TILES_X: u32 = 16;
/// 画面縦方向のタイル数。
pub const CLUSTER_TILES_Y: u32 = 9;
/// 奥行き（ビュー空間 Z）のスライス数。指数分割（対数深度）。
pub const CLUSTER_SLICES_Z: u32 = 24;
/// クラスタ総数（16 × 9 × 24 = 3456。Doom 2016 準拠の分割）。
pub const CLUSTER_COUNT: u32 = CLUSTER_TILES_X * CLUSTER_TILES_Y * CLUSTER_SLICES_Z;

/// 1 クラスタが保持できるライト数の上限。
/// 超過分はそのクラスタで評価されない（＝暗くなる）。実用上十分な値を取る。
pub const MAX_LIGHTS_PER_CLUSTER: u32 = 256;

/// グローバルライトインデックスリストの容量（u32 個数）。
/// 全クラスタが上限まで詰めても収まる最悪ケース容量にすることで、
/// atomic カーソルがプールを溢れさせない（＝ライトが落ちない）ことを保証する。
/// 3456 × 256 × 4B = 約 3.5 MB。
pub const CLUSTER_LIGHT_POOL: u32 = CLUSTER_COUNT * MAX_LIGHTS_PER_CLUSTER;

/// ライトのバウンディング球に掛ける安全マージン（保守側へ倒す係数）。
pub const CLUSTER_LIGHT_BOUND_MARGIN: f32 = 1.05;

// ─── compute のワークグループ（WGSL の @workgroup_size と一致必須）──

/// クラスタ構築 compute のワークグループサイズ X。
pub const CLUSTER_WG_X: u32 = 4;
/// クラスタ構築 compute のワークグループサイズ Y。
pub const CLUSTER_WG_Y: u32 = 4;
/// クラスタ構築 compute のワークグループサイズ Z。
pub const CLUSTER_WG_Z: u32 = 4;

// ─── ClusterCell ─────────────────────────────────────────────

/// クラスタ 1 個のライトリスト参照（8 バイト）。
/// WGSL: cluster_common.wgsl の ClusterCell と一致させること。
#[repr(C)]
#[derive(Debug, Clone, Copy, Default, bytemuck::Pod, bytemuck::Zeroable)]
pub struct ClusterCell {
    /// グローバルインデックスリスト内の先頭位置。
    pub offset: u32,
    /// このクラスタに影響する局所ライト数。
    pub count:  u32,
}

// ─── ClusterParams ───────────────────────────────────────────

/// クラスタの構築（compute）と参照（fragment）が共有するカメラ／画面パラメータ（112 バイト）。
///
/// WGSL: cluster_common.wgsl の ClusterParams と厳密に一致させること。
///
/// | offset | field              | size |
/// |--------|--------------------|------|
/// |   0    | view (mat4x4)      |  64  |
/// |  64    | vp_origin (vec2)   |   8  |
/// |  72    | vp_size   (vec2)   |   8  |
/// |  80    | near               |   4  |
/// |  84    | far                |   4  |
/// |  88    | tan_half_x         |   4  |
/// |  92    | tan_half_y         |   4  |
/// |  96    | enabled            |   4  |
/// | 100    | local_light_offset |   4  |
/// | 104    | light_count        |   4  |
/// | 108    | _pad               |   4  |
/// 合計 112（16 の倍数）
#[repr(C)]
#[derive(Debug, Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
pub struct ClusterParams {
    /// ビュー行列（転置済み＝列優先アップロード。CameraUniform.view と同じ流儀）。
    pub view:               [[f32; 4]; 4],
    /// ビューポート左上（フレームバッファのピクセル座標）。
    pub vp_origin:          [f32; 2],
    /// ビューポートの幅・高さ（ピクセル）。
    pub vp_size:            [f32; 2],
    /// ニアクリップ（> 0）。
    pub near:               f32,
    /// ファークリップ（> near）。
    pub far:                f32,
    /// tan(fov_y/2) * aspect。
    pub tan_half_x:         f32,
    /// tan(fov_y/2)。
    pub tan_half_y:         f32,
    /// クラスタ有効フラグ（0 = 無効＝フラグメントは全ライト線形走査）。
    pub enabled:            u32,
    /// 局所ライトの開始インデックス（= 平行光の数）。
    pub local_light_offset: u32,
    /// 有効ライト総数。
    pub light_count:        u32,
    /// パディング。
    pub _pad:               u32,
}

impl ClusterParams {
    /// クラスタ無効（全ライト線形走査）のパラメータ。
    /// カメラプレビュー用の group 4 BindGroup が常にこれを使う。
    fn disabled() -> Self {
        Self {
            view:               [[0.0; 4]; 4],
            vp_origin:          [0.0; 2],
            vp_size:            [1.0; 2],
            // near/far は 0 除算・log(0) を避けるための無害な既定値（enabled=0 なので未使用）。
            near:               1.0,
            far:                2.0,
            tan_half_x:         1.0,
            tan_half_y:         1.0,
            enabled:            0,
            local_light_offset: 0,
            light_count:        0,
            _pad:               0,
        }
    }
}

// ─── ライト配列の分割（平行光を先頭へ）────────────────────────

/// ライト配列を「平行光 → 局所ライト」の順へ**安定分割**し、平行光の数を返す。
///
/// 平行光は視錐台全体に影響するためクラスタに入れない（＝常に全ピクセルで評価する
/// 別枠にする）。配列の先頭へ寄せておけば、フラグメントは [0, dir_count) を無条件に
/// 走査し、その後をクラスタのリストで走査するだけで済む（別バッファ不要）。
///
/// 安定（各グループ内の相対順序を保つ）であることが重要:
/// ShadowResources::prepare_frame は「先頭の影付き平行光を CSM に採用」「スポットは
/// 出現順にシャドウ配列レイヤを割り当て」といった順序依存の処理を行うため、
/// グループ内順序が変わると影の割り当てが変わってしまう。
pub fn partition_directional_first(lights: &mut Vec<GpuLight>) -> u32 {
    let mut dir:   Vec<GpuLight> = Vec::with_capacity(lights.len());
    let mut local: Vec<GpuLight> = Vec::with_capacity(lights.len());
    for l in lights.iter() {
        if l.kind == LIGHT_KIND_DIRECTIONAL { dir.push(*l); } else { local.push(*l); }
    }
    let dir_count = dir.len() as u32;
    lights.clear();
    lights.extend(dir);
    lights.extend(local);
    dir_count
}

// ─── ClusterResources ────────────────────────────────────────

/// クラスタリング用の GPU リソース一式。
///
/// - グリッド（クラスタごとの offset/count）
/// - グローバルライトインデックスリスト
/// - atomic カーソル（毎フレーム 0 クリア）
/// - パラメータ uniform ×2（メインカメラ用＝有効／プレビュー用＝無効固定）
///
/// バッファはすべて生成後不変（内容だけ更新）なので、BindGroup は起動時 1 回だけ作る。
pub struct ClusterResources {
    /// array<ClusterCell>（CLUSTER_COUNT 要素）。compute が書き、フラグメントが読む。
    grid_buffer:    wgpu::Buffer,
    /// array<u32>（CLUSTER_LIGHT_POOL 要素）。compute が書き、フラグメントが読む。
    indices_buffer: wgpu::Buffer,
    /// atomic<u32>（1 要素）。毎フレーム 0 クリアしてから compute が atomicAdd する。
    cursor_buffer:  wgpu::Buffer,
    /// メインカメラ用 ClusterParams（enabled はカメラが透視のときだけ 1）。
    params_buffer:  wgpu::Buffer,
    /// プレビュー／非対応カメラ用 ClusterParams（enabled = 0 固定・生成時に 1 回書くだけ）。
    params_disabled_buffer: wgpu::Buffer,
    /// クラスタ構築 compute の group 0 BindGroup。
    /// ライトバッファ（LightBuffer 所有）を参照するため、LightBuffer 生成後に
    /// `attach_lights` で構築する（生成順の循環を避けるための 2 段構え）。
    compute_bg:     Option<wgpu::BindGroup>,
}

impl ClusterResources {
    /// クラスタ用バッファ一式を生成する（BindGroup はまだ作らない）。
    ///
    /// ライトバッファへの依存を避けるため、compute の BindGroup は
    /// `attach_lights`（LightBuffer 生成後に呼ぶ）で作る。
    pub fn new(device: &wgpu::Device) -> Self {
        // グリッド: 全クラスタ count=0 で初期化する。
        // これにより「compute を 1 度も走らせていないフレーム」でも
        // フラグメントが読むのは count=0（＝局所ライト無し）で、未初期化メモリを
        // 参照しない。ただし enabled=0 のときフラグメントはそもそも読まない。
        let init_grid = vec![ClusterCell::default(); CLUSTER_COUNT as usize];
        let grid_buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Cluster Grid Buffer"),
            contents: bytemuck::cast_slice(&init_grid),
            usage:    wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
        });

        // インデックスリスト（最悪ケース容量。ゼロ初期化）。
        let indices_buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label:              Some("Cluster Light Index Buffer"),
            size:               (CLUSTER_LIGHT_POOL as u64) * std::mem::size_of::<u32>() as u64,
            usage:              wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        let cursor_buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Cluster Cursor Buffer"),
            contents: bytemuck::bytes_of(&0u32),
            usage:    wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
        });

        let params_buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Cluster Params Uniform (main camera)"),
            contents: bytemuck::bytes_of(&ClusterParams::disabled()),
            usage:    wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        });

        // プレビュー用は生成時に enabled=0 を書いたきり更新しない（COPY_DST も不要だが、
        // 将来カメラごとのクラスタ（方式 a）へ拡張するときのために付けておく）。
        let params_disabled_buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Cluster Params Uniform (disabled)"),
            contents: bytemuck::bytes_of(&ClusterParams::disabled()),
            usage:    wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        });

        Self {
            grid_buffer,
            indices_buffer,
            cursor_buffer,
            params_buffer,
            params_disabled_buffer,
            compute_bg: None,
        }
    }

    /// クラスタ構築 compute の group 0 BindGroup を構築する（LightBuffer 生成後に 1 回）。
    ///
    /// - `bgl`:           ClusterBuildPipeline の group 0 レイアウト。
    /// - `lights_buffer`: LightBuffer が持つライト storage buffer（フラグメントと共有）。
    pub fn attach_lights(
        &mut self,
        device:        &wgpu::Device,
        bgl:           &wgpu::BindGroupLayout,
        lights_buffer: &wgpu::Buffer,
    ) {
        self.compute_bg = Some(device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Cluster Build BG (group 0)"),
            layout:  bgl,
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: lights_buffer.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 1, resource: self.params_buffer.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 2, resource: self.grid_buffer.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 3, resource: self.indices_buffer.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 4, resource: self.cursor_buffer.as_entire_binding() },
            ],
        }));
    }

    /// group 4（ライト＋シャドウ複合）へ差し込む各バッファへの参照。
    /// LightBuffer が BindGroup を組み立てる際に使う。
    pub fn grid_buffer(&self)    -> &wgpu::Buffer { &self.grid_buffer }
    /// 同上（インデックスリスト）。
    pub fn indices_buffer(&self) -> &wgpu::Buffer { &self.indices_buffer }
    /// 同上（メインカメラ用パラメータ＝クラスタ有効）。
    pub fn params_buffer(&self)  -> &wgpu::Buffer { &self.params_buffer }
    /// 同上（無効固定パラメータ＝プレビュー用）。
    pub fn params_disabled_buffer(&self) -> &wgpu::Buffer { &self.params_disabled_buffer }

    /// このフレームのクラスタパラメータを書き込み、atomic カーソルを 0 クリアする。
    ///
    /// - `view`:        メインカメラのビュー行列（転置済み＝CameraUniform.view と同じもの）。
    /// - `near`/`far`:  カメラのクリップ面（透視カメラのもの）。
    /// - `fov_y_rad`:   垂直画角。`aspect`: 射影のアスペクト比。
    /// - `viewport`:    (x, y, w, h) 実際に set_viewport する矩形（ピクセル）。
    ///                  Play のレターボックス時はゲーム領域。それ以外はフレームバッファ全体。
    /// - `dir_count`:   平行光の数（= 局所ライトの開始インデックス）。
    /// - `light_count`: 有効ライト総数。
    ///
    /// 戻り値: このフレームでクラスタを使うか（false ならディスパッチ不要・
    /// フラグメントは全ライト線形走査へフォールバックする）。
    /// near/far/fov が不正（透視カメラでない等）なら自動的に false になる。
    #[allow(clippy::too_many_arguments)]
    pub fn update(
        &self,
        queue:       &wgpu::Queue,
        enable:      bool,
        view:        [[f32; 4]; 4],
        near:        f32,
        far:         f32,
        fov_y_rad:   f32,
        aspect:      f32,
        viewport:    (f32, f32, f32, f32),
        dir_count:   u32,
        light_count: u32,
    ) -> bool {
        // 透視カメラとして成立しない値ではクラスタを組めない（log(far/near) が破綻する）。
        // その場合は無効化して従来の線形走査へ落とす（暗転させないための安全弁）。
        let geometry_ok = near > 0.0
            && far > near
            && fov_y_rad > 0.0
            && aspect > 0.0
            && viewport.2 > 0.0
            && viewport.3 > 0.0;
        let active = enable && geometry_ok;

        let tan_half_y = (fov_y_rad * 0.5).tan();
        let params = ClusterParams {
            view,
            vp_origin:          [viewport.0, viewport.1],
            vp_size:            [viewport.2, viewport.3],
            // 無効時も log/除算が壊れない値を書いておく（シェーダは読まないが念のため）。
            near:               if geometry_ok { near } else { 1.0 },
            far:                if geometry_ok { far }  else { 2.0 },
            tan_half_x:         if geometry_ok { tan_half_y * aspect } else { 1.0 },
            tan_half_y:         if geometry_ok { tan_half_y } else { 1.0 },
            enabled:            if active { 1 } else { 0 },
            local_light_offset: dir_count,
            light_count:        light_count.min(MAX_LIGHTS as u32),
            _pad:               0,
        };
        queue.write_buffer(&self.params_buffer, 0, bytemuck::bytes_of(&params));
        // atomic カーソルを 0 へ戻す（前フレームの詰め込み位置をリセット）。
        if active {
            queue.write_buffer(&self.cursor_buffer, 0, bytemuck::bytes_of(&0u32));
        }
        active
    }

    /// クラスタ構築 compute をディスパッチする（`update` が true を返したフレームのみ呼ぶ）。
    pub fn dispatch(&self, pass: &mut wgpu::ComputePass<'_>, pipeline: &wgpu::ComputePipeline) {
        let bg = self.compute_bg.as_ref()
            .expect("ClusterResources::attach_lights が呼ばれていません（BindGroup 未構築）");
        pass.set_pipeline(pipeline);
        pass.set_bind_group(0, bg, &[]);
        pass.dispatch_workgroups(
            CLUSTER_TILES_X.div_ceil(CLUSTER_WG_X),
            CLUSTER_TILES_Y.div_ceil(CLUSTER_WG_Y),
            CLUSTER_SLICES_Z.div_ceil(CLUSTER_WG_Z),
        );
    }
}

// ============================================================
//  クラスタ幾何の CPU ミラー（WGSL と同一ロジック・テスト用）
//
//  実行時の描画経路では使わない。WGSL 版（cluster_common.wgsl）と同じ式を
//  Rust で持ち、境界ケース（球が AABB の角に接する等）をユニットテストで
//  検証するために存在する。式を変えるときは必ず両方を同時に直すこと。
// ============================================================

/// スライス境界 k（0..=CLUSTER_SLICES_Z）のビュー空間深度（指数分割）。
pub fn cluster_slice_depth(k: u32, near: f32, far: f32) -> f32 {
    let t = k as f32 / CLUSTER_SLICES_Z as f32;
    near * (far / near).powf(t)
}

/// ビュー空間深度 → スライス番号（0..CLUSTER_SLICES_Z-1 にクランプ）。
pub fn cluster_z_slice(view_z: f32, near: f32, far: f32) -> u32 {
    let z = view_z.max(near);
    let t = (z / near).ln() / (far / near).ln();
    let s = (t * CLUSTER_SLICES_Z as f32).floor() as i32;
    s.clamp(0, CLUSTER_SLICES_Z as i32 - 1) as u32
}

/// (x, y, z) → 一次元クラスタ番号（x が最内）。
pub fn cluster_flat_index(x: u32, y: u32, z: u32) -> u32 {
    x + y * CLUSTER_TILES_X + z * CLUSTER_TILES_X * CLUSTER_TILES_Y
}

/// クラスタ (x, y, z) のビュー空間 AABB（min, max）を返す。
pub fn cluster_view_aabb(
    x: u32, y: u32, z: u32,
    near: f32, far: f32, tan_half_x: f32, tan_half_y: f32,
) -> ([f32; 3], [f32; 3]) {
    let nx0 = x       as f32 / CLUSTER_TILES_X as f32 * 2.0 - 1.0;
    let nx1 = (x + 1) as f32 / CLUSTER_TILES_X as f32 * 2.0 - 1.0;
    let ny0 = 1.0 - y       as f32 / CLUSTER_TILES_Y as f32 * 2.0;
    let ny1 = 1.0 - (y + 1) as f32 / CLUSTER_TILES_Y as f32 * 2.0;

    let z0 = cluster_slice_depth(z,     near, far);
    let z1 = cluster_slice_depth(z + 1, near, far);

    let xs = [nx0 * tan_half_x * z0, nx0 * tan_half_x * z1,
              nx1 * tan_half_x * z0, nx1 * tan_half_x * z1];
    let ys = [ny0 * tan_half_y * z0, ny0 * tan_half_y * z1,
              ny1 * tan_half_y * z0, ny1 * tan_half_y * z1];

    let min_of = |v: [f32; 4]| v.iter().copied().fold(f32::INFINITY,     f32::min);
    let max_of = |v: [f32; 4]| v.iter().copied().fold(f32::NEG_INFINITY, f32::max);

    (
        [min_of(xs), min_of(ys), z0.min(z1)],
        [max_of(xs), max_of(ys), z0.max(z1)],
    )
}

/// ライトのバウンディング球（center, radius）。平行光は None（クラスタ対象外）。
///
/// WGSL の cluster_light_bounds と同一ロジック（保守側＝棄却しすぎない）。
pub fn light_bounding_sphere(light: &GpuLight) -> Option<([f32; 3], f32)> {
    use super::lighting::LIGHT_KIND_RECT;
    if light.kind == LIGHT_KIND_DIRECTIONAL {
        return None;
    }
    let radius = if light.kind == LIGHT_KIND_RECT {
        let half_diag = (light.rect_half_width  * light.rect_half_width
                       + light.rect_half_height * light.rect_half_height).sqrt();
        (light.range + half_diag) * CLUSTER_LIGHT_BOUND_MARGIN
    } else {
        // point / spot（および未知の種別）は位置中心・半径 range の球で包む。
        // spot の円錐上の全点は頂点（光源位置）から range 以内にあるため厳密に保守的。
        light.range * CLUSTER_LIGHT_BOUND_MARGIN
    };
    Some((light.position, radius))
}

/// 球 vs AABB の交差判定（接触も交差とみなす＝保守側）。
pub fn sphere_aabb_intersects(
    center: [f32; 3], radius: f32,
    aabb_min: [f32; 3], aabb_max: [f32; 3],
) -> bool {
    let mut d2 = 0.0f32;
    for i in 0..3 {
        let c = center[i].clamp(aabb_min[i], aabb_max[i]);
        let d = center[i] - c;
        d2 += d * d;
    }
    d2 <= radius * radius
}

// ============================================================
//  テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::renderer::lighting::{LIGHT_KIND_POINT, LIGHT_KIND_RECT, LIGHT_KIND_SPOT};

    /// WGSL ソースから `const NAME: type = value;` の右辺リテラルを取り出す。
    fn wgsl_const(src: &str, name: &str) -> String {
        let decl = src
            .lines()
            .map(str::trim)
            .find(|l| l.starts_with(&format!("const {name}:")))
            .unwrap_or_else(|| panic!("cluster_common.wgsl に const {name} の宣言が見つかりません"));
        decl.split('=')
            .nth(1)
            .unwrap_or_else(|| panic!("const {name} の宣言に '=' がありません"))
            .trim()
            .trim_end_matches(';')
            .trim()
            .trim_end_matches('u')     // WGSL の u32 サフィックス
            .to_string()
    }

    fn wgsl_const_u32(src: &str, name: &str) -> u32 {
        wgsl_const(src, name).parse::<u32>()
            .unwrap_or_else(|_| panic!("const {name} が u32 として解釈できません"))
    }

    /// WGSL（cluster_common.wgsl）と Rust の分割数・上限・マージンが一致すること。
    ///
    /// ここがズレると「構築側と参照側で別のクラスタを見る」状態になり、
    /// ライトが誤って落ちて画面が暗くなる（コンパイルでも実行時エラーでも検出できない）。
    #[test]
    fn wgsl_constants_match_rust() {
        let src = include_str!("shaders/cluster_common.wgsl");

        assert_eq!(wgsl_const_u32(src, "CLUSTER_TILES_X"),        CLUSTER_TILES_X);
        assert_eq!(wgsl_const_u32(src, "CLUSTER_TILES_Y"),        CLUSTER_TILES_Y);
        assert_eq!(wgsl_const_u32(src, "CLUSTER_SLICES_Z"),       CLUSTER_SLICES_Z);
        assert_eq!(wgsl_const_u32(src, "CLUSTER_COUNT"),          CLUSTER_COUNT);
        assert_eq!(wgsl_const_u32(src, "MAX_LIGHTS_PER_CLUSTER"), MAX_LIGHTS_PER_CLUSTER);

        // マージンは f32 リテラル。
        let margin: f32 = wgsl_const(src, "CLUSTER_LIGHT_BOUND_MARGIN").parse().unwrap();
        assert!((margin - CLUSTER_LIGHT_BOUND_MARGIN).abs() < 1e-6);

        // ライト種別コードも同一ファイルへ移設したので一致を確認する。
        assert_eq!(wgsl_const_u32(src, "LIGHT_KIND_DIRECTIONAL"), LIGHT_KIND_DIRECTIONAL);
        assert_eq!(wgsl_const_u32(src, "LIGHT_KIND_POINT"),       LIGHT_KIND_POINT);
        assert_eq!(wgsl_const_u32(src, "LIGHT_KIND_SPOT"),        LIGHT_KIND_SPOT);
        assert_eq!(wgsl_const_u32(src, "LIGHT_KIND_RECT"),        LIGHT_KIND_RECT);

        // CLUSTER_COUNT の定義が分割数の積であること（WGSL 側はリテラルなので取り違え防止）。
        assert_eq!(CLUSTER_COUNT, CLUSTER_TILES_X * CLUSTER_TILES_Y * CLUSTER_SLICES_Z);
    }

    /// compute の @workgroup_size が Rust の CLUSTER_WG_* と一致すること
    /// （ズレると一部クラスタが未構築のまま残り、そこだけライトが落ちる）。
    #[test]
    fn wgsl_workgroup_size_matches_rust() {
        let src = include_str!("shaders/cluster_build.wgsl");
        let decl = src.lines().map(str::trim)
            .find(|l| l.starts_with("@compute @workgroup_size"))
            .expect("cluster_build.wgsl に @workgroup_size の宣言が見つかりません");
        let inner = decl
            .split('(').nth(1).expect("workgroup_size の '(' がありません")
            .split(')').next().expect("workgroup_size の ')' がありません");
        let dims: Vec<u32> = inner.split(',')
            .map(|s| s.trim().parse::<u32>().expect("workgroup_size が整数ではありません"))
            .collect();
        assert_eq!(dims, vec![CLUSTER_WG_X, CLUSTER_WG_Y, CLUSTER_WG_Z]);
    }

    /// GpuLight 構造体が light_common.wgsl と cluster_build.wgsl で同一であること。
    ///
    /// compute は light_common.wgsl を連結できない（group 4 のバインディングが混入する）
    /// ため GpuLight をミラーしている。ズレると compute が読むライト位置・range が
    /// 別のフィールドになり、ライトが無言で落ちる（画面が暗くなる）。
    #[test]
    fn wgsl_gpu_light_struct_mirrors_light_common() {
        /// `struct GpuLight { ... }` の中身をフィールド行（コメント除去済み）として抽出する。
        fn fields(src: &str) -> Vec<String> {
            let start = src.find("struct GpuLight {")
                .expect("struct GpuLight の宣言が見つかりません");
            let body  = &src[start..];
            let end   = body.find('}').expect("struct GpuLight の '}' が見つかりません");
            body[..end]
                .lines()
                .skip(1) // "struct GpuLight {" 行
                .map(|l| l.split("//").next().unwrap_or("").trim().to_string())
                .filter(|l| !l.is_empty())
                .collect()
        }
        let common = fields(include_str!("shaders/light_common.wgsl"));
        let build  = fields(include_str!("shaders/cluster_build.wgsl"));
        assert!(!common.is_empty(), "light_common.wgsl の GpuLight が空です");
        assert_eq!(
            common, build,
            "light_common.wgsl と cluster_build.wgsl の GpuLight のフィールドが一致しません\
             （96 バイトのレイアウトがズレると compute が誤ったライトを読みます）"
        );
    }

    /// クラスタ構築 compute（cluster_common + cluster_build）が naga で parse + validate できること。
    #[test]
    fn cluster_build_shader_parses_and_validates() {
        let src = format!(
            "{}\n{}",
            include_str!("shaders/cluster_common.wgsl"),
            include_str!("shaders/cluster_build.wgsl"),
        );
        let module = naga::front::wgsl::parse_str(&src)
            .unwrap_or_else(|e| panic!("cluster_build WGSL parse 失敗: {e:?}"));
        let mut validator = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            naga::valid::Capabilities::empty(),
        );
        validator.validate(&module)
            .unwrap_or_else(|e| panic!("cluster_build WGSL validate 失敗: {e:?}"));
    }

    /// Z スライスの指数分割と逆写像が整合すること。
    ///
    /// slice(slice_depth(k)) == k（境界そのもの）であり、
    /// 各スライスの内部の点も同じ k に落ちる（構築と参照で同じ境界を見る）。
    #[test]
    fn z_slice_is_inverse_of_slice_depth() {
        let (near, far) = (0.1f32, 1000.0f32);
        for k in 0..CLUSTER_SLICES_Z {
            let d0 = cluster_slice_depth(k,     near, far);
            let d1 = cluster_slice_depth(k + 1, near, far);
            // スライス内部の代表点（幾何平均）は必ず k に落ちる。
            let mid = (d0 * d1).sqrt();
            assert_eq!(cluster_z_slice(mid, near, far), k, "スライス {k} の内部点が別スライスへ落ちた");
            // 深度は単調増加。
            assert!(d1 > d0, "スライス深度が単調増加でない: k={k}");
        }
        // 範囲外はクランプされる（near より手前 → 0、far より奥 → 最終スライス）。
        assert_eq!(cluster_z_slice(near * 0.5, near, far), 0);
        assert_eq!(cluster_z_slice(far * 2.0,  near, far), CLUSTER_SLICES_Z - 1);
        assert_eq!(cluster_z_slice(near,       near, far), 0);
    }

    /// クラスタ番号の一次元化が全域で一意（衝突なし・範囲内）であること。
    #[test]
    fn flat_index_is_bijective() {
        let mut seen = vec![false; CLUSTER_COUNT as usize];
        for z in 0..CLUSTER_SLICES_Z {
            for y in 0..CLUSTER_TILES_Y {
                for x in 0..CLUSTER_TILES_X {
                    let i = cluster_flat_index(x, y, z) as usize;
                    assert!(i < CLUSTER_COUNT as usize, "クラスタ番号が範囲外: {i}");
                    assert!(!seen[i], "クラスタ番号が衝突: {i}");
                    seen[i] = true;
                }
            }
        }
        assert!(seen.iter().all(|&b| b), "未使用のクラスタ番号がある");
    }

    /// 球 vs AABB の境界ケース（角・辺・面に接する／わずかに離れる）。
    ///
    /// 判定は `dot(d,d) <= r*r` なので「厳密に接する」は交差扱い（保守側）。
    /// ただし距離が無理数（角までの √3、辺までの √2）の場合、f32 では
    /// `sqrt(x)*sqrt(x)` が x をわずかに下回ることがあり、数学的に「ちょうど接する」
    /// 半径では判定が転ぶ（＝ライトが 1 クラスタ分落ちうる）。
    /// これは実機でも起こりうる現実の丸め挙動であり、まさにそのために
    /// CLUSTER_LIGHT_BOUND_MARGIN（半径を数 % 広げる）を掛けて保守側へ倒している。
    /// 本テストでは「厳密に表現できる整数距離での接触」で <= の意味論を確認し、
    /// 無理数距離ではマージン込み／マージン抜きの両側を確認する。
    #[test]
    fn sphere_aabb_corner_cases() {
        let mn = [-1.0, -1.0, -1.0];
        let mx = [1.0, 1.0, 1.0];

        // 中心が内部 → 半径 0 でも交差。
        assert!(sphere_aabb_intersects([0.0, 0.0, 0.0], 0.0, mn, mx));

        // 面に接する（軸方向・距離 2.0 は f32 で厳密）。接触は交差扱い。
        assert!(
            sphere_aabb_intersects([3.0, 0.0, 0.0], 2.0, mn, mx),
            "面にちょうど接する球は交差扱いにすること（保守側）"
        );
        assert!(
            !sphere_aabb_intersects([3.0, 0.0, 0.0], 1.999, mn, mx),
            "面に届かない球が交差扱いになっている（カリングが効いていない）"
        );

        // 角 (1,1,1) から (1,1,1) 方向へ距離 √3 の点。
        let s3     = 3.0f32.sqrt();
        let corner = [2.0, 2.0, 2.0];
        assert!(
            sphere_aabb_intersects(corner, s3 * CLUSTER_LIGHT_BOUND_MARGIN, mn, mx),
            "角に接する球がマージン込みでも交差しない（ライトが落ちる）"
        );
        assert!(
            !sphere_aabb_intersects(corner, s3 * 0.99, mn, mx),
            "角に届かない球が交差扱いになっている（カリングが効いていない）"
        );

        // 辺に接する（2 軸方向・距離 √2）。
        let s2   = 2.0f32.sqrt();
        let edge = [2.0, 2.0, 0.0];
        assert!(
            sphere_aabb_intersects(edge, s2 * CLUSTER_LIGHT_BOUND_MARGIN, mn, mx),
            "辺に接する球がマージン込みでも交差しない（ライトが落ちる）"
        );
        assert!(
            !sphere_aabb_intersects(edge, s2 * 0.99, mn, mx),
            "辺に届かない球が交差扱いになっている（カリングが効いていない）"
        );

        // 中心が AABB の内部にある巨大な球（内包）も当然交差。
        assert!(sphere_aabb_intersects([0.5, -0.5, 0.0], 100.0, mn, mx));
    }

    /// ライトのバウンディング球が保守的（実際の影響範囲を必ず包含する）であること。
    #[test]
    fn light_bounds_are_conservative() {
        // 平行光はクラスタ対象外（None）。
        let dir = GpuLight::directional([0.0, -1.0, 0.0], [1.0; 3], 1.0);
        assert!(light_bounding_sphere(&dir).is_none(), "平行光はクラスタに入れない");

        // 点光源: 半径は range 以上（減衰は range で厳密に 0 になるため range で十分・マージン付き）。
        let p = GpuLight::point([1.0, 2.0, 3.0], [1.0; 3], 5.0, 10.0);
        let (c, r) = light_bounding_sphere(&p).unwrap();
        assert_eq!(c, [1.0, 2.0, 3.0]);
        assert!(r >= 10.0, "点光源の半径が range を下回っている（影響範囲を切り落とす）");

        // スポット: 円錐の全点は頂点から range 以内 → 半径 range 以上であれば厳密に保守的。
        let s = GpuLight::spot([0.0; 3], [0.0, 0.0, 1.0], [1.0; 3], 1.0, 20.0, 10.0, 30.0);
        let (_, rs) = light_bounding_sphere(&s).unwrap();
        assert!(rs >= 20.0, "スポットの半径が range を下回っている");
        assert_eq!(s.kind, LIGHT_KIND_SPOT);

        // Rect: 半径 ≥ range + 半対角（矩形の端点から range 先まで届くため）。
        let rect = GpuLight::rect(
            [0.0; 3], [0.0, 0.0, 1.0], [1.0, 0.0, 0.0], [0.0, 1.0, 0.0],
            [1.0; 3], 1.0, 10.0, 6.0, 8.0,   // 幅 6 高 8 → 半幅 3 半高 4 → 半対角 5
        );
        let (_, rr) = light_bounding_sphere(&rect).unwrap();
        assert!(rr >= 15.0, "Rect の半径が range + 半対角（15）を下回っている: {rr}");
        assert_eq!(rect.kind, LIGHT_KIND_RECT);
        assert_eq!(p.kind, LIGHT_KIND_POINT);
    }

    /// クラスタ AABB が視錐台セルを包含していること（＝棄却しすぎない）。
    ///
    /// 各クラスタの錐台セルの 8 隅を厳密に計算し、AABB に含まれることを確認する。
    /// また全クラスタの AABB の和が視錐台全体（x/y は最大画角、z は near..far）を覆うこと。
    #[test]
    fn cluster_aabb_contains_frustum_cell() {
        let (near, far)   = (0.1f32, 500.0f32);
        let fov_y: f32    = 60f32.to_radians();
        let aspect        = 16.0 / 9.0;
        let tan_y         = (fov_y * 0.5).tan();
        let tan_x         = tan_y * aspect;

        for z in 0..CLUSTER_SLICES_Z {
            for y in 0..CLUSTER_TILES_Y {
                for x in 0..CLUSTER_TILES_X {
                    let (mn, mx) = cluster_view_aabb(x, y, z, near, far, tan_x, tan_y);
                    // 錐台セルの 8 隅（NDC タイル境界 × スライス深度）。
                    let nx0 = x       as f32 / CLUSTER_TILES_X as f32 * 2.0 - 1.0;
                    let nx1 = (x + 1) as f32 / CLUSTER_TILES_X as f32 * 2.0 - 1.0;
                    let ny0 = 1.0 - y       as f32 / CLUSTER_TILES_Y as f32 * 2.0;
                    let ny1 = 1.0 - (y + 1) as f32 / CLUSTER_TILES_Y as f32 * 2.0;
                    let z0  = cluster_slice_depth(z,     near, far);
                    let z1  = cluster_slice_depth(z + 1, near, far);
                    for &nx in &[nx0, nx1] {
                        for &ny in &[ny0, ny1] {
                            for &vz in &[z0, z1] {
                                let vx = nx * tan_x * vz;
                                let vy = ny * tan_y * vz;
                                // 浮動小数の丸めを許容する微小イプシロン。
                                let eps = 1e-3;
                                assert!(vx >= mn[0] - eps && vx <= mx[0] + eps, "隅 x が AABB 外");
                                assert!(vy >= mn[1] - eps && vy <= mx[1] + eps, "隅 y が AABB 外");
                                assert!(vz >= mn[2] - eps && vz <= mx[2] + eps, "隅 z が AABB 外");
                            }
                        }
                    }
                }
            }
        }
    }

    /// 視錐台の中心にある点光源が、対応するクラスタで確実に拾われること
    /// （＝典型ケースでライトが落ちない）。
    #[test]
    fn light_at_view_center_is_collected() {
        let (near, far) = (0.1f32, 100.0f32);
        let fov_y: f32  = 60f32.to_radians();
        let tan_y       = (fov_y * 0.5).tan();
        let tan_x       = tan_y * (16.0 / 9.0);

        // ビュー空間 (0, 0, 10) にある半径 3 の点光源（ビュー行列は単位＝ワールド==ビュー）。
        let light  = GpuLight::point([0.0, 0.0, 10.0], [1.0; 3], 1.0, 3.0);
        let (c, r) = light_bounding_sphere(&light).unwrap();

        // 光源中心が属するクラスタは必ずヒットしなければならない。
        let zs = cluster_z_slice(10.0, near, far);
        // 中心は NDC (0,0) → タイル境界（X=16 は偶数なので x=8, Y=9 は奇数なので y=4 の中央）。
        let (mn, mx) = cluster_view_aabb(CLUSTER_TILES_X / 2, CLUSTER_TILES_Y / 2, zs,
                                         near, far, tan_x, tan_y);
        assert!(
            sphere_aabb_intersects(c, r, mn, mx),
            "視錐台中央の点光源が自分のクラスタで拾われない（暗転バグ）"
        );

        // 数え上げ: 少なくとも 1 つのクラスタがこのライトを拾う。
        let mut hits = 0;
        for z in 0..CLUSTER_SLICES_Z {
            for y in 0..CLUSTER_TILES_Y {
                for x in 0..CLUSTER_TILES_X {
                    let (mn, mx) = cluster_view_aabb(x, y, z, near, far, tan_x, tan_y);
                    if sphere_aabb_intersects(c, r, mn, mx) { hits += 1; }
                }
            }
        }
        assert!(hits > 0, "点光源をどのクラスタも拾わなかった");
        assert!(hits < CLUSTER_COUNT, "点光源が全クラスタに入っている（カリングが機能していない）");
    }

    /// 平行光が先頭へ安定分割され、各グループ内の相対順序が保たれること。
    #[test]
    fn partition_keeps_stable_order() {
        let mk_dir = |i: f32| GpuLight::directional([0.0, -1.0, 0.0], [i, i, i], i);
        let mk_pt  = |i: f32| GpuLight::point([i, 0.0, 0.0], [1.0; 3], i, 1.0);

        let mut lights = vec![mk_pt(1.0), mk_dir(2.0), mk_pt(3.0), mk_dir(4.0), mk_pt(5.0)];
        let dir_count = partition_directional_first(&mut lights);

        assert_eq!(dir_count, 2);
        // 先頭 2 つが平行光（元の相対順序: intensity 2 → 4）。
        assert_eq!(lights[0].kind, LIGHT_KIND_DIRECTIONAL);
        assert_eq!(lights[1].kind, LIGHT_KIND_DIRECTIONAL);
        assert_eq!(lights[0].intensity, 2.0);
        assert_eq!(lights[1].intensity, 4.0);
        // 残りは局所ライト（元の相対順序: position.x 1 → 3 → 5）。
        assert_eq!(lights[2].position[0], 1.0);
        assert_eq!(lights[3].position[0], 3.0);
        assert_eq!(lights[4].position[0], 5.0);
        assert!(lights[2..].iter().all(|l| l.kind != LIGHT_KIND_DIRECTIONAL));

        // 空配列・全平行光・全局所ライトの縮退ケース。
        let mut empty: Vec<GpuLight> = vec![];
        assert_eq!(partition_directional_first(&mut empty), 0);
        let mut all_dir = vec![mk_dir(1.0), mk_dir(2.0)];
        assert_eq!(partition_directional_first(&mut all_dir), 2);
        let mut all_local = vec![mk_pt(1.0)];
        assert_eq!(partition_directional_first(&mut all_local), 0);
    }

    /// ClusterParams / ClusterCell の repr(C) レイアウトが WGSL と一致すること。
    #[test]
    fn params_layout() {
        use std::mem::{size_of, offset_of};
        assert_eq!(size_of::<ClusterCell>(), 8);
        assert_eq!(size_of::<ClusterParams>(), 112, "ClusterParams は 112 バイト（16 の倍数）");
        assert_eq!(offset_of!(ClusterParams, view),               0);
        assert_eq!(offset_of!(ClusterParams, vp_origin),          64);
        assert_eq!(offset_of!(ClusterParams, vp_size),            72);
        assert_eq!(offset_of!(ClusterParams, near),               80);
        assert_eq!(offset_of!(ClusterParams, far),                84);
        assert_eq!(offset_of!(ClusterParams, tan_half_x),         88);
        assert_eq!(offset_of!(ClusterParams, tan_half_y),         92);
        assert_eq!(offset_of!(ClusterParams, enabled),            96);
        assert_eq!(offset_of!(ClusterParams, local_light_offset), 100);
        assert_eq!(offset_of!(ClusterParams, light_count),        104);
    }

    /// インデックスプールが最悪ケース（全クラスタが上限まで詰める）でも溢れないこと。
    #[test]
    fn index_pool_cannot_overflow() {
        assert_eq!(CLUSTER_LIGHT_POOL, CLUSTER_COUNT * MAX_LIGHTS_PER_CLUSTER);
    }
}
