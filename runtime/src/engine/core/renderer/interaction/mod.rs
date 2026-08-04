// ============================================================
//  renderer/interaction/mod.rs — 瞬発インタラクションフィールド（Phase I1）
//
//  正典: docs/water_interaction_roadmap.md §1.3 / §2 I1。
//
//  ## 役割（単一責任）
//  「ワールド空間俯瞰の瞬発場テクスチャを保持し、毎フレーム 1 ディスパッチで
//  更新する」ことだけを担う。**誰が場を書くか**（InteractionSource の収集と速度算出）は
//  `engine::interaction` の責務、**誰が場を読むか**（草・水・雪泥）は各シェーダの責務であり、
//  本モジュールはそのどちらも知らない。
//
//  ## 場の形（数値の根拠）
//  ・窓 = カメラ XZ 追従の一辺 64m 正方形（`INTERACTION_FIELD_EXTENT_M`）
//      根拠: 草の描画距離（terrain_scatter_ops::GRASS_CULL_DISTANCE）の内側で、
//      「プレイヤーの足元の草が反応する」ために必要十分な範囲。窓を広げるほど
//      同じ解像度ならテクセルが粗くなり、足跡の輪郭がぼける。
//  ・解像度 = 512×512（`INTERACTION_FIELD_RESOLUTION`）
//      根拠: 64m / 512 = 0.125m/テクセル。人の足元（半径 1m ≒ 8 テクセル）が
//      円として認識できる最小限の細かさ。Rgba16Float 512² = 2MB／枚、
//      ping-pong 2 枚で 4MB と常駐コストも許容範囲。
//
//  ## ping-pong（2 枚構成）である理由
//  更新は「前フレームの場を減衰 → ソースを合成」の read-modify-write だが、
//  core WebGPU の storage テクスチャは rgba16float の `read_write` を許さない。
//  そこで読み側（`texture_2d`）と書き側（`texture_storage_2d<..., write>`）を
//  別テクスチャにし、毎フレーム役割を入れ替える。バリアもクリアも不要になる。
//
//  ## カメラ追従はテクセル単位スナップ
//  窓の原点をテクセルサイズの整数倍へ丸めるため、前フレームとの差は必ず整数テクセル。
//  再マップがバイリニアでなく整数 `textureLoad` で済み、カメラ移動で場がにじまない
//  （＝草がカメラを動かすだけでちらつくのを構造的に防ぐ）。
//
//  ## 波の伝播（I2 で追加）
//  `.z` = 現在の波高 / `.w` = 1 フレーム前の波高 として、同じ 1 ディスパッチ内で
//  波動方程式（明示スキーム）を解く。**別 ping-pong は増やさない**（詳細は
//  `shaders/interaction_field.wgsl` の「波の伝播」節）。
//
//  ## 将来（I3）
//  ・I3（雪泥の轍）: **本テクスチャは使わない。**「永続変形」は地形チャンクに
//    紐づく別の蓄積テクスチャであり（寿命が違う＝場を分けるのが設計の要）、
//    本モジュールとは独立に追加する。
// ============================================================

use bytemuck::{Pod, Zeroable};

use crate::engine::interaction::{
    collect_water_physics_regions, MovingInteractionSource, WaterPhysicsRegion,
};
use crate::engine::water::ResolvedWaterVolume;

// ============================================================
//  形状・挙動の定数（マジックナンバー禁止）
// ============================================================

/// 場の窓の一辺（m）。カメラ XZ を中心に張る。
pub const INTERACTION_FIELD_EXTENT_M: f32 = 64.0;

/// 場の一辺の解像度（テクセル数）。1 テクセル = 64/512 = 0.125m。
pub const INTERACTION_FIELD_RESOLUTION: u32 = 512;

/// 場のテクスチャフォーマット。
///
/// XZ 速度だけなら Rg16Float で足りるが、rg16float は core WebGPU の
/// storage フォーマットに含まれない（imos_blur.rs の R16Float と同じ事情）。
/// Rgba16Float は storage 可・filterable（頂点段のバイリニアサンプルが必要）で、
/// 余る 2 チャンネルは I2 の波エネルギー用に予約できる。
pub const INTERACTION_FIELD_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rgba16Float;

/// 減衰の時定数 τ（秒）。1 フレームの減衰係数は exp(-dt/τ)。
///
/// τ=1.0s は「通り過ぎて約 3 秒（3τ ≒ 残り 5%）で完全に元へ戻る」挙動。
/// 草が踏み分けられてゆっくり起き上がる、という見た目の要求そのもの。
pub const INTERACTION_FIELD_DECAY_TAU_SECS: f32 = 1.0;

/// 波（`.z`/`.w`）の減衰の時定数 τ（秒）。1 フレームの減衰係数は exp(-dt/τ)。
///
/// 速度場（τ=1s）より長く残す。水面の波紋は「通り過ぎた後もしばらく輪が広がって
/// 消えていく」のが自然であり、草の踏み分けより寿命が長い現象のため。
pub const INTERACTION_WAVE_DECAY_TAU_SECS: f32 = 1.5;

/// 波の伝播速度（m/s）。波紋の輪が広がる速さそのもの。
///
/// 現実の重力波は波長依存だが、本実装は単一速度の波動方程式なので 1 値で表す。
/// 「人が歩く速さの約 3 倍で輪が広がる」＝池に石を落としたときの体感に近い値を狙う。
///
/// 3.75m/s という半端な値は **固定刻み 1/60 秒で k = 0.25 ちょうどになる**ように
/// 選んだもの（3.75 × (1/60) / 0.125 = 0.5、その 2 乗が 0.25）。
/// 2D の安定限界 k ≤ 1/2 のちょうど半分＝十分な余裕があり、
/// 見た目の伝播速度は旧値（4.0m/s）から 6% 遅くなるだけで体感差は無い。
pub const INTERACTION_WAVE_SPEED_MPS: f32 = 3.75;

/// 波を進める **固定タイムステップ**（秒）。
///
/// 波動方程式の陽解法は「刻みが一定」であることを前提にした 2 段階差分であり、
/// 可変 dt を dt 比で補正する方式は原理的に発散する（詳細は
/// `shaders/interaction_field.wgsl` の「時間刻みは固定である」節）。
/// 実経過時間はアキュムレータに積み、この刻み単位でサブステップとして消化する。
/// 1/60 秒は「標準的な表示リフレッシュで 1 フレーム＝1 サブステップ」になる値。
pub const INTERACTION_WAVE_FIXED_DT_SECS: f32 = 1.0 / 60.0;

/// 1 フレームに実行する波のサブステップ数の上限。
///
/// 低フレームレート時に「遅い → サブステップが増える → もっと遅い」の
/// スパイラルへ落ちるのを断ち切るための上限。超過ぶんの時間は**捨てる**。
/// 捨てた場合の挙動は「波が実時間よりゆっくり進む」であり、安全側に倒れる。
/// 4 回＝15fps までは実時間どおりに追従できる。
pub const INTERACTION_WAVE_MAX_SUBSTEPS: u32 = 4;

/// 明示スキームのクーラン数の 2 乗の安定限界。
///
/// 2 次元の波動方程式を陽解法で解く場合、(c·dt/dx)² ≤ 1/2 を超えると発散する。
/// 固定刻み化により `INTERACTION_WAVE_K` はコンパイル時定数になったので、
/// この定数は「飽和のための上限」ではなく**テストが検証する不変条件の基準値**である。
pub const INTERACTION_WAVE_MAX_COURANT_SQ: f32 = 0.5;

/// 波のクーラン数 c·dt_fixed/dx（無次元・コンパイル時定数）。
pub const INTERACTION_WAVE_COURANT: f32 =
    INTERACTION_WAVE_SPEED_MPS * INTERACTION_WAVE_FIXED_DT_SECS / INTERACTION_FIELD_TEXEL_SIZE;

/// 波の伝播係数 k = (c·dt_fixed/dx)²（無次元・コンパイル時定数）。
///
/// フレーム時間に一切依存しないため、**どんなフレームレートでも安定条件
/// k ≤ `INTERACTION_WAVE_MAX_COURANT_SQ` が保たれる**（テストで固定）。
pub const INTERACTION_WAVE_K: f32 = INTERACTION_WAVE_COURANT * INTERACTION_WAVE_COURANT;

/// ソースが 1 個も無い状態がこの秒数続いたら、場を完全に消してディスパッチを止める。
///
/// 5τ ＝ 残り 0.7% で目視不能。**波の τ（速度場より長い）を基準に取る**こと。
/// 速度場基準にすると、まだ見えている波紋の途中でディスパッチが止まり、
/// 波が凍りついたまま残る。ここで最後に 1 回「減衰係数 0」で書き潰し、
/// 以降はディスパッチ自体を行わない（＝ソースを置かないシーンの GPU コストは 0）。
pub const INTERACTION_FIELD_SETTLE_SECS: f32 = INTERACTION_WAVE_DECAY_TAU_SECS * 5.0;

/// 1 フレームに場へ焼けるソースの最大数。
///
/// 更新シェーダはテクセルごとにこの数だけループするため、上限は実測コストに直結する。
/// 「同時に草を踏み分ける動的オブジェクト」が 64 個を超える場面は想定しない
/// （超過分は収集順＝アクタ DFS 順で切り捨てる）。
pub const INTERACTION_MAX_SOURCES: usize = 64;

/// コンピュートのワークグループ 1 辺（WGSL と一致必須）。
pub const INTERACTION_FIELD_WORKGROUP_SIZE: u32 = 8;

/// 1 フレームに場へ渡せる「水域の物性矩形」の最大数（Phase I2.1）。
///
/// 更新シェーダはテクセルごとにこの配列を先頭から走査し、**最初に当たった矩形で
/// 打ち切る**。窓（64m）に重なる水域だけへ CPU 側で絞ってあるので、
/// 典型シーンでは 0〜2 個しか入らない。32 は「入り組んだ水場でも足りる」上限で、
/// 32 × 32 バイト = 1KB とバッファも極小。
/// 超過ぶんは**面積の大きい矩形から**捨てる（`collect_water_physics_regions`）。
pub const INTERACTION_MAX_WATER_REGIONS: usize = 32;

/// 速度 1 m/s あたりの草の曲げ角（rad·s/m）。
///
/// 人の歩行（約 1.4m/s）で 0.35rad ≒ 20 度、走り（約 5m/s）で上限に張り付く程度。
/// 「歩けば揺れ、走れば薙ぎ倒す」という体感になる値。
pub const INTERACTION_GRASS_BEND_PER_SPEED: f32 = 0.25;

/// 草がインタラクションで曲がる角度の上限（rad ≒ 80 度）。
///
/// 草シェーダ側の全体上限（`GRASS_MAX_BEND_ANGLE` ≒ 100 度）より小さくして、
/// 風と合成しても葉が地面へ潜り切らない余裕を残す。
pub const INTERACTION_GRASS_MAX_BEND: f32 = 1.396_263_4;

/// 1 テクセルのワールドサイズ（m）。
pub const INTERACTION_FIELD_TEXEL_SIZE: f32 =
    INTERACTION_FIELD_EXTENT_M / INTERACTION_FIELD_RESOLUTION as f32;

/// 波紋の減衰率の既定値（1/s）＝ 時定数 `INTERACTION_WAVE_DECAY_TAU_SECS` の逆数。
///
/// `WaterVolumeComponent::ripple_damping` の既定値（`default_ripple_damping`）と
/// **同じ値でなければならない**（テスト `default_ripple_damping_matches_engine_tau` が固定する）。
/// 水域が物性を指定しない／水が 1 つも無いテクセルは、この率で減衰する。
pub const INTERACTION_WAVE_DEFAULT_DAMPING_RATE: f32 = 1.0 / INTERACTION_WAVE_DECAY_TAU_SECS;

/// 1 サブステップぶんの波の減衰係数 exp(-dt_fixed × 減衰率)。
///
/// 固定刻みなので、率が同じなら値は常に一定（フレームレートに依存しない）。
/// `f32::exp` が const fn でないため定数ではなく関数として置く。
///
/// **水域ごとの物性（Phase I2.1）も同じ式で係数を作る**
/// （`interaction::water_physics`）。式を 1 本に揃えてあるので、
/// 「水域の外」と「既定の物性を持つ水域の中」で減衰が食い違うことがない。
pub fn interaction_wave_damp_for_rate(rate: f32) -> f32 {
    (-INTERACTION_WAVE_FIXED_DT_SECS * rate).exp()
}

/// 水域外テクセル（＝草の揺れなど非水用途）で使う既定の減衰係数。
pub fn interaction_wave_damp_per_substep() -> f32 {
    interaction_wave_damp_for_rate(INTERACTION_WAVE_DEFAULT_DAMPING_RATE)
}

/// 実経過時間から「今フレームに実行する波のサブステップ数」と
/// 「次フレームへ繰り越すアキュムレータ残量（秒）」を決める。
///
/// - `accum_secs`: 前フレームまでの繰越 ＋ 今フレームの経過時間（秒）。
/// - 戻り値: (サブステップ数, 繰越残量)。
///
/// 上限 `INTERACTION_WAVE_MAX_SUBSTEPS` を超える要求が来た場合は、
/// **超過ぶんの時間を捨てて繰越を 0 にする**（死のスパイラル防止。冒頭の定数コメント参照）。
/// GPU を触らない純粋関数なのでユニットテストで直接検証できる。
pub fn interaction_wave_substeps(accum_secs: f32) -> (u32, f32) {
    if accum_secs < INTERACTION_WAVE_FIXED_DT_SECS {
        // 1 サブステップに満たない ＝ 場を進めない（繰り越すだけ）。
        return (0, accum_secs.max(0.0));
    }
    let wanted = (accum_secs / INTERACTION_WAVE_FIXED_DT_SECS) as u32;
    if wanted > INTERACTION_WAVE_MAX_SUBSTEPS {
        (INTERACTION_WAVE_MAX_SUBSTEPS, 0.0)
    } else {
        (wanted, accum_secs - wanted as f32 * INTERACTION_WAVE_FIXED_DT_SECS)
    }
}

// ─── バインディング番号（WGSL @binding と一致必須）───────────────

/// 更新パス group0: パラメータ UBO。
const BINDING_UNIFORM: u32 = 0;
/// 更新パス group0: ソース配列（storage）。
const BINDING_SOURCES: u32 = 1;
/// 更新パス group0: 前フレームの場（読み）。
const BINDING_SRC_TEX: u32 = 2;
/// 更新パス group0: 今フレームの場（書き）。
const BINDING_DST_TEX: u32 = 3;
/// 更新パス group0: 水域の物性矩形配列（storage。Phase I2.1）。
const BINDING_WATER_REGIONS: u32 = 4;

/// 消費側 group: 場テクスチャ。
const BINDING_SAMPLE_TEX:     u32 = 0;
/// 消費側 group: サンプラー。
const BINDING_SAMPLE_SAMPLER: u32 = 1;
/// 消費側 group: パラメータ UBO（更新パスと同じバッファを共有する）。
const BINDING_SAMPLE_UNIFORM: u32 = 2;

// ============================================================
//  GPU データレイアウト（WGSL と一致必須）
// ============================================================

/// 場の更新／消費で共有するパラメータ UBO。
///
/// WGSL `InteractionFieldUniform` と一致必須（64 バイト）。
/// **同じ構造体を interaction_field.wgsl と grass_gbuffer.wgsl の 2 箇所が宣言する**
/// ため、並びを変えたら 3 箇所同時に直すこと（テストが文字列で照合する）。
#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable, Debug, Default)]
pub struct InteractionFieldUniformGpu {
    /// 今フレームの窓のワールド XZ 最小（テクセル単位にスナップ済み）。
    pub origin_xz:      [f32; 2],
    /// 前フレームの窓のワールド XZ 最小。
    pub prev_origin_xz: [f32; 2],
    /// 1 テクセルのワールドサイズ（m）。
    pub texel_size:     f32,
    /// 窓の一辺の逆数（1/m）。ワールド XZ → [0,1] UV に使う。
    pub inv_extent:     f32,
    /// このフレームの減衰係数 exp(-dt/τ)。0 で場を完全消去。
    pub decay:          f32,
    /// 場の一辺の解像度（テクセル数）。
    pub resolution:     u32,
    /// 有効なソース数。
    pub source_count:   u32,
    /// 速度 1 m/s あたりの草の曲げ角（rad·s/m）。消費側のみ使用。
    pub bend_per_speed: f32,
    /// 草の曲げ角の上限（rad）。消費側のみ使用。
    pub max_bend:       f32,
    /// 波の伝播係数 k = (c·dt_fixed/dx)²（無次元・**定数** `INTERACTION_WAVE_K`）。更新パスのみ使用。
    pub wave_k:         f32,
    /// 1 サブステップぶんの波の減衰係数 exp(-dt_fixed/τ_wave)（**定数**）。更新パスのみ使用。
    ///
    /// **水域の物性矩形に当たらなかったテクセルの既定値**でもある（Phase I2.1）。
    pub wave_damp:      f32,
    /// 有効な水域の物性矩形数（`u_water_regions` の先頭から何個読むか。Phase I2.1）。
    /// **0 なら走査ループが回らず、W5.2 以前と完全に同一の処理になる。** 更新パスのみ使用。
    pub water_region_count: u32,
    /// 16 バイト境界へ揃えるためのパディング（未使用）。
    pub _pad1:          f32,
    pub _pad2:          f32,
}

/// 「この XZ 矩形の中では波はこの係数で進む」1 件ぶん（Phase I2.1）。
///
/// WGSL `WaterPhysicsRegion` と一致必須（std430 / 32 バイト）。
/// **係数は CPU 側（`interaction::water_physics`）で完成済み**であり、
/// シェーダは矩形判定と代入しか行わない（安定性のクランプを 1 箇所に集約するため）。
#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable, Debug, Default)]
pub struct WaterPhysicsRegionGpu {
    /// 矩形のワールド XZ 最小（場の窓でクリップ済み）。
    pub min_xz:    [f32; 2],
    /// 矩形のワールド XZ 最大（場の窓でクリップ済み）。
    pub max_xz:    [f32; 2],
    /// この矩形内での波の伝播係数 k = (c·dt_fixed/dx)²。
    pub wave_k:    f32,
    /// この矩形内での 1 サブステップぶんの減衰係数。
    pub wave_damp: f32,
    /// 16 バイト境界へ揃えるためのパディング（未使用）。
    pub _pad0:     f32,
    pub _pad1:     f32,
}

impl From<WaterPhysicsRegion> for WaterPhysicsRegionGpu {
    /// エンジン層の物性矩形を GPU レイアウトへ詰め替える（値の加工は一切しない）。
    fn from(r: WaterPhysicsRegion) -> Self {
        Self {
            min_xz:    r.min_xz,
            max_xz:    r.max_xz,
            wave_k:    r.wave_k,
            wave_damp: r.wave_damp,
            _pad0:     0.0,
            _pad1:     0.0,
        }
    }
}

/// インタラクションソース 1 個（GPU）。WGSL `InteractionSourceGpu` と一致必須（32 バイト）。
#[repr(C)]
#[derive(Clone, Copy, Pod, Zeroable, Debug, Default)]
pub struct InteractionSourceGpu {
    /// ワールド XZ 位置。
    pub pos_xz:   [f32; 2],
    /// ワールド XZ 速度（m/s）。
    pub vel_xz:   [f32; 2],
    /// 影響半径（m）。
    pub radius:   f32,
    /// 書き込みの強さ（0..1）。
    pub strength: f32,
    /// 水面へ注入する波の振幅（m 相当。0 = 注入しない）。
    /// CPU 側（`engine::interaction::water_wave`）が水ボリュームと照合して決めた値。
    pub wave_amp: f32,
    /// 16 バイト境界へ揃えるためのパディング（未使用）。
    pub _pad:     f32,
}

// ============================================================
//  消費側 BindGroupLayout（草などが group2 で受け取る）
// ============================================================

/// 場を **読む側** の BindGroupLayout を作る。
///
/// 草パイプライン（`GrassGBufferPipeline::new`）と `InteractionFieldRenderer` の
/// 双方がこの関数を呼ぶ。両者が作る BGL は構造的に等価なので、wgpu の
/// BindGroupLayout 構造的等価性によりバインドできる
/// （gbuffer.rs がカメラ BGL を借用しているのと同じ既存慣例）。
///
/// `visibility` を引数にするのは、消費側が頂点段（草）とは限らないため
/// （I2 の水面はフラグメント段で読む）。
pub fn create_field_sample_bind_group_layout(
    device:     &wgpu::Device,
    visibility: wgpu::ShaderStages,
) -> wgpu::BindGroupLayout {
    device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label: Some("interaction_field_sample_bgl"),
        entries: &[
            // 場テクスチャ（Rgba16Float は filterable ＝ バイリニアで滑らかに読める）。
            wgpu::BindGroupLayoutEntry {
                binding:    BINDING_SAMPLE_TEX,
                visibility,
                ty: wgpu::BindingType::Texture {
                    sample_type:    wgpu::TextureSampleType::Float { filterable: true },
                    view_dimension: wgpu::TextureViewDimension::D2,
                    multisampled:   false,
                },
                count: None,
            },
            // サンプラー（線形・ClampToEdge）。
            wgpu::BindGroupLayoutEntry {
                binding:    BINDING_SAMPLE_SAMPLER,
                visibility,
                ty:    wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
                count: None,
            },
            // パラメータ UBO（窓原点・スケール・曲げ係数）。
            wgpu::BindGroupLayoutEntry {
                binding:    BINDING_SAMPLE_UNIFORM,
                visibility,
                ty: wgpu::BindingType::Buffer {
                    ty:                 wgpu::BufferBindingType::Uniform,
                    has_dynamic_offset: false,
                    min_binding_size:   None,
                },
                count: None,
            },
        ],
    })
}

// ============================================================
//  InteractionFieldRenderer
// ============================================================

/// 瞬発インタラクションフィールドの GPU リソースと更新手続き。
pub struct InteractionFieldRenderer {
    /// 1 回目のサブステップ用パイプライン
    /// （再マップ＋速度場の減衰＋波 1 ステップ＋ソースのスタンプ）。
    pipeline: wgpu::ComputePipeline,
    /// 2 回目以降のサブステップ用パイプライン（波 1 ステップのみ）。
    ///
    /// **なぜ uniform のフラグではなくエントリポイントを分けるのか**:
    /// `queue.write_buffer` はコマンドバッファの送信前にまとめて適用されるため、
    /// 同一フレーム内で 1 本の UBO を「サブステップごとに書き換える」ことはできない
    /// （最後の書き込みが全ディスパッチに効いてしまう）。
    /// パイプラインを分ければ UBO は 1 回書くだけで済む。
    substep_pipeline: wgpu::ComputePipeline,
    /// 更新パス group0 の BindGroup（添字 = **書き込み先**テクスチャの番号）。
    update_bind_groups: [wgpu::BindGroup; 2],
    /// 消費側 BindGroup（添字 = **最新の場**を保持するテクスチャの番号）。
    sample_bind_groups: [wgpu::BindGroup; 2],
    /// パラメータ UBO（更新パスと消費側で共有）。
    uniform_buf: wgpu::Buffer,
    /// ソース配列（storage。容量は `INTERACTION_MAX_SOURCES` 固定）。
    sources_buf: wgpu::Buffer,
    /// 水域の物性矩形配列（storage。容量は `INTERACTION_MAX_WATER_REGIONS` 固定。Phase I2.1）。
    water_regions_buf: wgpu::Buffer,
    /// 場テクスチャのビュー 2 枚（添字 = テクスチャ番号）。
    ///
    /// **消費側が自前の BindGroup を作れるように保持している。**
    /// 草は共有レイアウト（`create_field_sample_bind_group_layout`）で作った
    /// `sample_bind_groups` をそのまま使えるが、水面パスはパイプラインを
    /// WGSL リフレクション（`RenderPipelineBuilder`）で組むため、BGL の可視性が
    /// 共有レイアウトと構造的に一致しない（リフレクションは uniform を
    /// VERTEX_FRAGMENT 可視にする）。そこで水面側は**リフレクション由来の BGL で
    /// 自前の BindGroup を作る**方式にし、そのために生リソースを公開する。
    views: [wgpu::TextureView; 2],
    /// 場のサンプラー（線形・ClampToEdge）。消費側の BindGroup 生成に使う。
    sampler: wgpu::Sampler,
    /// 最新の場を保持しているテクスチャの番号（0 or 1）。
    current: usize,
    /// 前回ディスパッチ時の窓原点（再マップの基準）。
    prev_origin_xz: [f32; 2],
    /// ソースが 1 個も無い状態が続いた秒数。
    idle_secs: f32,
    /// 波の固定刻みアキュムレータ（秒）。実経過時間を積み、固定刻み単位で消化する。
    wave_accum_secs: f32,
    /// まだ減衰へ反映していない実経過時間（秒）。
    ///
    /// サブステップ 0 回のフレーム（dt が固定刻み未満）はディスパッチ自体を行わないため、
    /// その間の経過時間をここへ溜め、次にディスパッチするフレームでまとめて
    /// `exp(-Σdt/τ)` として掛ける。こうしないと「速度場が実時間より遅く減衰する」。
    pending_decay_secs: f32,
    /// 場を完全に消し終えてディスパッチを止めている状態か。
    settled: bool,
    /// ソース数が上限を超えた警告を出したか（ログ氾濫防止のため 1 回だけ）。
    warned_overflow: bool,
}

impl InteractionFieldRenderer {
    /// GPU リソースとパイプラインを構築する（テクスチャは固定サイズなのでここで確保する）。
    pub fn new(device: &wgpu::Device, cache: Option<&wgpu::PipelineCache>) -> Self {
        // ── 場テクスチャ 2 枚（ping-pong）──
        //   wgpu はテクスチャをゼロ初期化するため、初回フレームの読み側は 0（場が無い）。
        let make_tex = |label: &str| {
            device.create_texture(&wgpu::TextureDescriptor {
                label:           Some(label),
                size: wgpu::Extent3d {
                    width:                 INTERACTION_FIELD_RESOLUTION,
                    height:                INTERACTION_FIELD_RESOLUTION,
                    depth_or_array_layers: 1,
                },
                mip_level_count: 1,
                sample_count:    1,
                dimension:       wgpu::TextureDimension::D2,
                format:          INTERACTION_FIELD_FORMAT,
                // STORAGE = 更新パスの書き先 / TEXTURE = 更新パスの読み元＋消費側のサンプル。
                usage: wgpu::TextureUsages::STORAGE_BINDING | wgpu::TextureUsages::TEXTURE_BINDING,
                view_formats:    &[],
            })
        };
        let tex0 = make_tex("interaction_field_0");
        let tex1 = make_tex("interaction_field_1");
        let view0 = tex0.create_view(&wgpu::TextureViewDescriptor::default());
        let view1 = tex1.create_view(&wgpu::TextureViewDescriptor::default());

        // ── パラメータ UBO ──
        let uniform_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("interaction_field_uniform"),
            size:  std::mem::size_of::<InteractionFieldUniformGpu>() as wgpu::BufferAddress,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        // ── ソース配列（固定容量。毎フレームの再確保を避ける）──
        let sources_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("interaction_field_sources"),
            size:  (INTERACTION_MAX_SOURCES * std::mem::size_of::<InteractionSourceGpu>())
                       as wgpu::BufferAddress,
            usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        // ── 水域の物性矩形配列（固定容量。Phase I2.1）──
        //   水が 0 個のフレームでも中身を読まないだけでバインドは常に有効にしておく
        //   （storage バッファはサイズ 0 を作れず、毎フレームの BindGroup 再生成も避けたい）。
        let water_regions_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("interaction_field_water_regions"),
            size:  (INTERACTION_MAX_WATER_REGIONS
                        * std::mem::size_of::<WaterPhysicsRegionGpu>()) as wgpu::BufferAddress,
            usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        // ── 更新パイプライン ──
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label:  Some("interaction_field"),
            source: wgpu::ShaderSource::Wgsl(
                include_str!("../shaders/interaction_field.wgsl").into()),
        });
        let update_bgl = create_update_bind_group_layout(device);
        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label:                Some("interaction_field_layout"),
            bind_group_layouts:   &[&update_bgl],
            push_constant_ranges: &[],
        });
        // 1 回目（フル更新）と 2 回目以降（波のみ）で 2 本作る。
        // バインドグループのレイアウトは同一なので、BindGroup はそのまま使い回せる。
        let make_pipeline = |label: &str, entry: &str| {
            device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
                label:               Some(label),
                layout:              Some(&layout),
                module:              &shader,
                entry_point:         Some(entry),
                compilation_options: Default::default(),
                cache,
            })
        };
        let pipeline = make_pipeline("interaction_field_update", "cs_interaction_field");
        let substep_pipeline =
            make_pipeline("interaction_field_wave_substep", "cs_interaction_wave_substep");

        // ── 更新 BindGroup（添字 = 書き込み先。読み元はもう一方）──
        let make_update_bg = |dst: &wgpu::TextureView, src: &wgpu::TextureView, label: &str| {
            device.create_bind_group(&wgpu::BindGroupDescriptor {
                label:  Some(label),
                layout: &update_bgl,
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: BINDING_UNIFORM, resource: uniform_buf.as_entire_binding() },
                    wgpu::BindGroupEntry {
                        binding: BINDING_SOURCES, resource: sources_buf.as_entire_binding() },
                    wgpu::BindGroupEntry {
                        binding: BINDING_SRC_TEX, resource: wgpu::BindingResource::TextureView(src) },
                    wgpu::BindGroupEntry {
                        binding: BINDING_DST_TEX, resource: wgpu::BindingResource::TextureView(dst) },
                    wgpu::BindGroupEntry {
                        binding:  BINDING_WATER_REGIONS,
                        resource: water_regions_buf.as_entire_binding() },
                ],
            })
        };
        let update_bind_groups = [
            make_update_bg(&view0, &view1, "interaction_field_update_bg0"),
            make_update_bg(&view1, &view0, "interaction_field_update_bg1"),
        ];

        // ── 消費側 BindGroup（草の group2。頂点段で読む）──
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label:          Some("interaction_field_sampler"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            mag_filter:     wgpu::FilterMode::Linear,
            min_filter:     wgpu::FilterMode::Linear,
            mipmap_filter:  wgpu::FilterMode::Nearest,
            ..Default::default()
        });
        let sample_bgl = create_field_sample_bind_group_layout(device, wgpu::ShaderStages::VERTEX);
        let make_sample_bg = |view: &wgpu::TextureView, label: &str| {
            device.create_bind_group(&wgpu::BindGroupDescriptor {
                label:  Some(label),
                layout: &sample_bgl,
                entries: &[
                    wgpu::BindGroupEntry {
                        binding:  BINDING_SAMPLE_TEX,
                        resource: wgpu::BindingResource::TextureView(view) },
                    wgpu::BindGroupEntry {
                        binding:  BINDING_SAMPLE_SAMPLER,
                        resource: wgpu::BindingResource::Sampler(&sampler) },
                    wgpu::BindGroupEntry {
                        binding:  BINDING_SAMPLE_UNIFORM,
                        resource: uniform_buf.as_entire_binding() },
                ],
            })
        };
        let sample_bind_groups = [
            make_sample_bg(&view0, "interaction_field_sample_bg0"),
            make_sample_bg(&view1, "interaction_field_sample_bg1"),
        ];

        // 初期の窓原点は「まだ場が無い」ことを表す 0。初回ディスパッチでは
        // prev との差が大きくなるが、読み側テクスチャはゼロ初期化なので実害はない。
        Self {
            pipeline,
            substep_pipeline,
            update_bind_groups,
            sample_bind_groups,
            uniform_buf,
            sources_buf,
            water_regions_buf,
            views: [view0, view1],
            sampler,
            current: 0,
            prev_origin_xz: [0.0, 0.0],
            idle_secs: 0.0,
            wave_accum_secs: 0.0,
            pending_decay_secs: 0.0,
            settled: true,
            warned_overflow: false,
        }
    }

    /// 消費側（草など）が group へバインドする BindGroup。
    ///
    /// **常に有効**（ソースが 0 個でも、場が消えていても、ゼロで埋まった
    /// テクスチャが返る）。消費側は「場が無いフレーム」を分岐で扱わなくてよい。
    pub fn sample_bind_group(&self) -> &wgpu::BindGroup {
        &self.sample_bind_groups[self.current]
    }

    /// 最新の場テクスチャのビュー（消費側が自前 BindGroup を作るため）。
    ///
    /// **常に有効**（場が消えていてもゼロで埋まったテクスチャが返る）。
    pub fn field_view(&self) -> &wgpu::TextureView {
        &self.views[self.current]
    }

    /// 場のサンプラー（線形・ClampToEdge）。
    pub fn field_sampler(&self) -> &wgpu::Sampler {
        &self.sampler
    }

    /// 場のパラメータ UBO（窓原点・窓幅の逆数・テクセルサイズを消費側が読む）。
    pub fn field_uniform_buffer(&self) -> &wgpu::Buffer {
        &self.uniform_buf
    }

    /// このフレームの場を更新する（コマンドは `encoder` へ記録される）。
    ///
    /// - `sources`   : 速度確定済みのソース列（`InteractionSourceVelocityTracker::update` の結果）。
    /// - `volumes`   : 解決済み水ボリューム列（Phase I2.1。**水域ごとの物性**を場へ渡すため）。
    ///                 空スライスを渡せば全テクセルが従来どおりのエンジン定数で進む。
    /// - `camera_pos`: 窓の中心にするワールド座標（メインカメラ位置）。
    /// - `delta_secs`: 前フレームからの経過時間（**壁時計**。Edit 中も減衰が進むように）。
    ///
    /// 戻り値は「実際にディスパッチしたか」。ソースが無い状態が
    /// `INTERACTION_FIELD_SETTLE_SECS` 続いた後は false を返し、GPU コストが 0 になる。
    /// また、経過時間が波の固定刻みに満たないフレームも false を返す
    /// （場を進めない＝ ping-pong もしない。消費側は前フレームと同じテクスチャを読む）。
    pub fn update(
        &mut self,
        queue:      &wgpu::Queue,
        encoder:    &mut wgpu::CommandEncoder,
        sources:    &[MovingInteractionSource],
        volumes:    &[ResolvedWaterVolume],
        camera_pos: [f32; 3],
        delta_secs: f32,
    ) -> bool {
        // ── ① 休止判定（ソースが無い時間を数える）──
        if sources.is_empty() {
            self.idle_secs += delta_secs.max(0.0);
        } else {
            self.idle_secs = 0.0;
            self.settled   = false;
        }
        // すでに場を消し終えている＆ソースも無い → 何もしない（コスト 0）。
        if self.settled {
            return false;
        }

        // ── ② 経過時間を積む（波の固定刻み用と、速度場の減衰用）──
        let dt = delta_secs.max(0.0);
        self.wave_accum_secs    += dt;
        self.pending_decay_secs += dt;

        // ── ③ 今フレームに実行する波のサブステップ数を決める ──
        //   固定刻み単位で消化し、余りは次フレームへ繰り越す（上限超過ぶんは捨てる）。
        let (substeps, carry) = interaction_wave_substeps(self.wave_accum_secs);
        // 消化ぶんを引いた残量を確定させる（以降どの経路を通っても繰越は正しい）。
        self.wave_accum_secs  = carry;

        // ── ④ 場の消去フレームか判定する ──
        //   ソースが無いまま SETTLE を超えたら、最後に 1 回だけ「減衰 0」で書き潰し、
        //   以降はディスパッチしない。消去は波のサブステップ数と無関係に必ず 1 回実行する。
        let clearing = self.idle_secs > INTERACTION_FIELD_SETTLE_SECS;

        // ── ⑤ サブステップ 0 回のフレームは場を進めない ──
        //   経過時間が固定刻みに満たない（高フレームレート）ケース。
        //   ping-pong もしないため `current` は据え置きで、消費側は前フレームと
        //   同じテクスチャを読む。窓原点・減衰の未反映ぶんは繰り越されるので、
        //   次にディスパッチするフレームでまとめて正しく処理される。
        if !clearing && substeps == 0 {
            return false;
        }

        // ── ⑥ 窓の原点をテクセル単位へスナップする ──
        //   スナップしないと、カメラ移動のたびに場が半テクセルずれて再マップが
        //   バイリニアになり、草が微妙に揺れてちらつく。
        let origin_xz = snap_window_origin(camera_pos);

        // ── ⑦ 速度場の減衰係数 ──
        //   1 次減衰なので可変 dt でも安定＝**実経過時間ぶんをまとめて 1 回**掛ける
        //   （波と違い、サブステップに分ける必要がない）。
        let decay = if clearing {
            self.settled = true;
            0.0
        } else {
            (-self.pending_decay_secs / INTERACTION_FIELD_DECAY_TAU_SECS).exp()
        };
        self.pending_decay_secs = 0.0;

        // ── ⑧ ソース配列をアップロード（上限で切り捨て）──
        let count = sources.len().min(INTERACTION_MAX_SOURCES);
        if sources.len() > INTERACTION_MAX_SOURCES && !self.warned_overflow {
            self.warned_overflow = true;
            eprintln!(
                "[interaction] InteractionSource が上限 {} 個を超えたため {} 個を切り捨てます",
                INTERACTION_MAX_SOURCES,
                sources.len() - INTERACTION_MAX_SOURCES,
            );
        }
        if count > 0 {
            let gpu: Vec<InteractionSourceGpu> = sources[..count]
                .iter()
                .map(|s| InteractionSourceGpu {
                    pos_xz:   [s.world_pos[0], s.world_pos[2]],
                    vel_xz:   s.velocity_xz,
                    radius:   s.radius,
                    strength: s.strength,
                    wave_amp: s.wave_amplitude,
                    _pad:     0.0,
                })
                .collect();
            queue.write_buffer(&self.sources_buf, 0, bytemuck::cast_slice(&gpu));
        }

        // ── ⑧' 水域の物性矩形をアップロード（Phase I2.1）──
        //   窓（今フレームの origin_xz を基準）に重なる水域だけを矩形へ落とす。
        //   面積の昇順に並ぶので、シェーダは「最初に当たった矩形」で打ち切ってよい。
        //   消去フレーム（decay = 0）は場を 0 で書き潰すだけなので矩形は要らない。
        let regions = if clearing {
            Vec::new()
        } else {
            collect_water_physics_regions(
                volumes,
                origin_xz,
                INTERACTION_FIELD_EXTENT_M,
                INTERACTION_WAVE_K,
                INTERACTION_WAVE_FIXED_DT_SECS,
                INTERACTION_MAX_WATER_REGIONS,
            )
        };
        if !regions.is_empty() {
            let gpu: Vec<WaterPhysicsRegionGpu> =
                regions.iter().copied().map(WaterPhysicsRegionGpu::from).collect();
            queue.write_buffer(&self.water_regions_buf, 0, bytemuck::cast_slice(&gpu));
        }

        // ── ⑨ パラメータ UBO ──
        //   波の係数はどちらも固定刻み由来の定数＝フレーム時間に依存しない
        //   （＝どんな fps でも CFL 条件が破れない。これが発散事故の恒久対策）。
        let uniform = InteractionFieldUniformGpu {
            origin_xz,
            prev_origin_xz: self.prev_origin_xz,
            texel_size:     INTERACTION_FIELD_TEXEL_SIZE,
            inv_extent:     1.0 / INTERACTION_FIELD_EXTENT_M,
            decay,
            resolution:     INTERACTION_FIELD_RESOLUTION,
            source_count:   count as u32,
            bend_per_speed: INTERACTION_GRASS_BEND_PER_SPEED,
            max_bend:       INTERACTION_GRASS_MAX_BEND,
            wave_k:         INTERACTION_WAVE_K,
            wave_damp:      interaction_wave_damp_per_substep(),
            // 水域の物性矩形（Phase I2.1）。0 ならシェーダの走査ループは 1 周も回らず、
            // 全テクセルが上の既定係数で進む＝W5.2 以前と完全に同一の挙動になる。
            water_region_count: regions.len() as u32,
            _pad1:          0.0,
            _pad2:          0.0,
        };
        queue.write_buffer(&self.uniform_buf, 0, bytemuck::bytes_of(&uniform));

        // ── ⑩ ディスパッチ（読み = current / 書き = 反対側を ping-pong）──
        //   消去フレームは 1 回だけ（全チャンネル 0 で書き潰す）。
        //   通常フレームは「1 回目 = フル更新 → 2 回目以降 = 波のみ」を substeps 回。
        //   **サブステップごとに別のコンピュートパスへ分ける**のが要点:
        //   同一パス内で同じテクスチャを読み書きすると同期が保証されないため、
        //   パスの境界をバリアとして使う（wgpu がパス間に自動で挿入する）。
        let passes = if clearing { 1 } else { substeps };
        let groups = workgroup_count(INTERACTION_FIELD_RESOLUTION);
        for step in 0..passes {
            let dst = 1 - self.current;
            {
                let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
                    label:            Some("Interaction Field Update"),
                    timestamp_writes: None,
                });
                if step == 0 {
                    pass.set_pipeline(&self.pipeline);
                } else {
                    pass.set_pipeline(&self.substep_pipeline);
                }
                pass.set_bind_group(0, &self.update_bind_groups[dst], &[]);
                pass.dispatch_workgroups(groups, groups, 1);
            }
            // 次のサブステップは今書いた側を読む。
            self.current = dst;
        }

        // ── ⑪ 状態を進める（以降 sample_bind_group は最後に書いた側を返す）──
        self.prev_origin_xz = origin_xz;
        true
    }
}

/// 更新パス group0 の BindGroupLayout を作る。
fn create_update_bind_group_layout(device: &wgpu::Device) -> wgpu::BindGroupLayout {
    device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label: Some("interaction_field_update_bgl"),
        entries: &[
            // パラメータ UBO
            wgpu::BindGroupLayoutEntry {
                binding:    BINDING_UNIFORM,
                visibility: wgpu::ShaderStages::COMPUTE,
                ty: wgpu::BindingType::Buffer {
                    ty:                 wgpu::BufferBindingType::Uniform,
                    has_dynamic_offset: false,
                    min_binding_size:   None,
                },
                count: None,
            },
            // ソース配列（read-only storage）
            wgpu::BindGroupLayoutEntry {
                binding:    BINDING_SOURCES,
                visibility: wgpu::ShaderStages::COMPUTE,
                ty: wgpu::BindingType::Buffer {
                    ty:                 wgpu::BufferBindingType::Storage { read_only: true },
                    has_dynamic_offset: false,
                    min_binding_size:   None,
                },
                count: None,
            },
            // 前フレームの場（textureLoad で読む）
            wgpu::BindGroupLayoutEntry {
                binding:    BINDING_SRC_TEX,
                visibility: wgpu::ShaderStages::COMPUTE,
                ty: wgpu::BindingType::Texture {
                    sample_type:    wgpu::TextureSampleType::Float { filterable: true },
                    view_dimension: wgpu::TextureViewDimension::D2,
                    multisampled:   false,
                },
                count: None,
            },
            // 今フレームの場（storage write）
            wgpu::BindGroupLayoutEntry {
                binding:    BINDING_DST_TEX,
                visibility: wgpu::ShaderStages::COMPUTE,
                ty: wgpu::BindingType::StorageTexture {
                    access:         wgpu::StorageTextureAccess::WriteOnly,
                    format:         INTERACTION_FIELD_FORMAT,
                    view_dimension: wgpu::TextureViewDimension::D2,
                },
                count: None,
            },
            // 水域の物性矩形配列（read-only storage。Phase I2.1）
            wgpu::BindGroupLayoutEntry {
                binding:    BINDING_WATER_REGIONS,
                visibility: wgpu::ShaderStages::COMPUTE,
                ty: wgpu::BindingType::Buffer {
                    ty:                 wgpu::BufferBindingType::Storage { read_only: true },
                    has_dynamic_offset: false,
                    min_binding_size:   None,
                },
                count: None,
            },
        ],
    })
}

/// カメラ位置から、テクセル単位にスナップした窓原点（ワールド XZ 最小）を求める。
///
/// スナップは `floor(v / texel) * texel`。前フレームとの差が必ず整数テクセルになるため、
/// 場の再マップを整数 `textureLoad` で行える（＝カメラ移動でにじまない）。
pub fn snap_window_origin(camera_pos: [f32; 3]) -> [f32; 2] {
    let half = INTERACTION_FIELD_EXTENT_M * 0.5;
    let snap = |v: f32| (v / INTERACTION_FIELD_TEXEL_SIZE).floor() * INTERACTION_FIELD_TEXEL_SIZE;
    [snap(camera_pos[0] - half), snap(camera_pos[2] - half)]
}

/// 解像度からワークグループ数を求める（切り上げ）。
pub const fn workgroup_count(resolution: u32) -> u32 {
    // 切り上げ除算（const fn で使えるよう div_ceil ではなく手計算にする）。
    (resolution + INTERACTION_FIELD_WORKGROUP_SIZE - 1) / INTERACTION_FIELD_WORKGROUP_SIZE
}

// ============================================================
//  テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// 更新シェーダのソース（naga 検証・定数照合で使う）。
    fn field_shader() -> &'static str {
        include_str!("../shaders/interaction_field.wgsl")
    }

    /// 場更新 WGSL を naga で parse + validate する。
    /// GPU を回せない環境ではこれが唯一の実効的な検証手段であり、最重要のテスト。
    #[test]
    fn interaction_field_shader_parses_and_validates() {
        let module = naga::front::wgsl::parse_str(field_shader())
            .unwrap_or_else(|e| panic!("[interaction_field] WGSL parse 失敗: {e:?}"));
        let mut validator = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            naga::valid::Capabilities::empty(),
        );
        validator
            .validate(&module)
            .unwrap_or_else(|e| panic!("[interaction_field] WGSL validate 失敗: {e:?}"));
    }

    /// ワークグループサイズ定数が Rust と WGSL で一致すること
    /// （ズレるとディスパッチ数が合わず、場の一部が更新されないまま残る）。
    #[test]
    fn workgroup_size_matches_shader() {
        let expected = format!(
            "const INTERACTION_FIELD_WORKGROUP_SIZE: u32 = {INTERACTION_FIELD_WORKGROUP_SIZE}u;");
        assert!(
            field_shader().contains(&expected),
            "interaction_field.wgsl に `{expected}` が無い（Rust 側定数と不一致）"
        );
    }

    /// UBO 構造体は 16 バイト境界（uniform の要求）かつ想定サイズであること。
    #[test]
    fn uniform_is_expected_size() {
        let size = std::mem::size_of::<InteractionFieldUniformGpu>();
        assert_eq!(size, 64, "InteractionFieldUniformGpu は 64 バイト（WGSL と一致必須）");
        assert_eq!(size % 16, 0, "uniform は 16 の倍数バイトであること");
    }

    /// 波の伝播係数は **コンパイル時定数**であり、CFL 安定条件（k ≤ 1/2）に
    /// 十分な余裕を持って収まること（最重要の不変条件）。
    ///
    /// 固定刻み化により k は dt に依存しなくなったため、検証は 1 点で足りる。
    /// ここが破れると波が発散し、水面が一瞬で真っ白に壊れる。
    #[test]
    fn wave_k_is_constant_and_within_stability_limit() {
        // 安定限界そのもの。
        assert!(
            INTERACTION_WAVE_K < INTERACTION_WAVE_MAX_COURANT_SQ,
            "k={INTERACTION_WAVE_K} が CFL 上限 {INTERACTION_WAVE_MAX_COURANT_SQ} を超えた"
        );
        // 設計値（限界の半分 = 0.25）から外れていないこと。
        // ここを緩めるなら波速 c と固定刻みを再検討すること。
        assert!(
            (INTERACTION_WAVE_K - 0.25).abs() < 1e-4,
            "k は 0.25（安定限界の半分）である設計。実際は {INTERACTION_WAVE_K}"
        );
        // 減衰は 1 サブステップぶんの指数減衰＝ 0 < damp < 1。
        let damp = interaction_wave_damp_per_substep();
        assert!(damp > 0.0 && damp < 1.0, "damp={damp} が (0,1) の外");
        // 特性方程式 g² − damp·(2 − kλ)·g + damp = 0 の根の積は damp < 1 ＝
        // 全モードが減衰する（旧実装の inertia 方式ではここが damp·inertia > 1 になり得た）。
        assert!(damp < 1.0, "根の積 = damp が 1 以上だとエネルギーが増える");
    }

    /// アキュムレータは「固定刻み単位で消化し、余りを繰り越す」こと。
    /// ここが崩れると波が実時間とズレる（速すぎ／遅すぎ）。
    #[test]
    fn wave_substeps_consume_fixed_dt_and_carry_remainder() {
        let dt = INTERACTION_WAVE_FIXED_DT_SECS;
        // 刻み未満 → 0 回。時間は丸ごと繰り越す（捨てない）。
        let (n, carry) = interaction_wave_substeps(dt * 0.4);
        assert_eq!(n, 0);
        assert!((carry - dt * 0.4).abs() < 1e-7, "繰越が失われた: {carry}");
        // ちょうど 1 刻み → 1 回、繰越ほぼ 0。
        let (n, carry) = interaction_wave_substeps(dt);
        assert_eq!(n, 1);
        assert!(carry.abs() < 1e-6, "繰越={carry}");
        // 1.5 刻み → 1 回、0.5 刻みを繰り越す。
        let (n, carry) = interaction_wave_substeps(dt * 1.5);
        assert_eq!(n, 1);
        assert!((carry - dt * 0.5).abs() < 1e-6, "繰越={carry}");
        // 30fps 相当（2 刻み）→ 2 回。
        let (n, _) = interaction_wave_substeps(dt * 2.0);
        assert_eq!(n, 2);
    }

    /// 低フレームレートでもサブステップ数は上限で頭打ちになり、超過時間は捨てること
    /// （「遅い→サブステップ増→もっと遅い」のスパイラルを断つ）。
    #[test]
    fn wave_substeps_are_capped_and_drop_excess_time() {
        let dt = INTERACTION_WAVE_FIXED_DT_SECS;
        // 1fps 相当・ウィンドウドラッグ等の巨大 dt。
        for &secs in &[dt * 10.0, 1.0, 10.0] {
            let (n, carry) = interaction_wave_substeps(secs);
            assert_eq!(n, INTERACTION_WAVE_MAX_SUBSTEPS, "secs={secs} で上限を超えた");
            assert_eq!(carry, 0.0, "超過ぶんの時間は捨てること（secs={secs}）");
        }
        // ちょうど上限ぶんは捨てない。
        let (n, carry) = interaction_wave_substeps(dt * INTERACTION_WAVE_MAX_SUBSTEPS as f32);
        assert_eq!(n, INTERACTION_WAVE_MAX_SUBSTEPS);
        assert!(carry.abs() < 1e-5, "繰越={carry}");
    }

    /// 60fps で回している限り、実時間と波の進む時間が一致すること
    /// （1 フレーム = 1 サブステップ。累積ドリフトが無い）。
    #[test]
    fn wave_time_tracks_real_time_at_60fps() {
        let frame = 1.0 / 60.0_f32;
        let mut accum = 0.0_f32;
        let mut total_steps = 0_u32;
        const FRAMES: u32 = 600; // 10 秒ぶん
        for _ in 0..FRAMES {
            accum += frame;
            let (n, carry) = interaction_wave_substeps(accum);
            total_steps += n;
            accum = carry;
        }
        // 浮動小数の丸めで ±1 ステップはあり得るので許容する。
        let diff = (total_steps as i64 - FRAMES as i64).abs();
        assert!(diff <= 1, "10 秒で {total_steps} ステップ（期待 {FRAMES}±1）");
    }

    /// 更新シェーダに 2 つのエントリポイント（フル更新／波のみ）が存在すること。
    /// パイプライン生成はここで名前を取り違えても実行時まで気づけないため固定する。
    #[test]
    fn shader_declares_both_substep_entry_points() {
        let src = field_shader();
        assert!(src.contains("fn cs_interaction_field("), "1 回目用エントリが無い");
        assert!(src.contains("fn cs_interaction_wave_substep("), "サブステップ用エントリが無い");
        // 発散原因だった慣性項が完全に消えていること（再発防止）。
        assert!(!src.contains("wave_inertia"), "wave_inertia が残っている（発散の再発）");
    }

    /// 場の休止判定は「波の τ」基準であること（速度場の τ で切ると波紋が凍って残る）。
    #[test]
    fn settle_time_covers_wave_decay() {
        assert!(INTERACTION_FIELD_SETTLE_SECS >= INTERACTION_WAVE_DECAY_TAU_SECS * 5.0);
        assert!(INTERACTION_FIELD_SETTLE_SECS >= INTERACTION_FIELD_DECAY_TAU_SECS * 5.0);
    }

    // ── 水域ごとの物性（Phase I2.1）────────────────────────────

    /// 物性矩形の GPU 構造体は std430 の 32 バイトであること
    /// （ズレると GPU が隣の矩形のデータを読む＝静かな描画バグ）。
    #[test]
    fn water_physics_region_is_32_bytes() {
        assert_eq!(std::mem::size_of::<WaterPhysicsRegionGpu>(), 32);
        assert_eq!(std::mem::size_of::<WaterPhysicsRegionGpu>() % 16, 0,
            "std430 の配列ストライドは 16 の倍数であること");
    }

    /// 更新シェーダが物性矩形を **binding 4** で宣言し、走査していること。
    ///
    /// リファクタでバインドや走査関数が落ちても、静かに「粘度が効かない」に
    /// なるだけで誰も気づかないため、文字列で押さえる。
    #[test]
    fn shader_declares_and_consumes_water_regions() {
        let src = field_shader();
        assert!(src.contains("@group(0) @binding(4) var<storage, read> u_water_regions:"),
            "物性矩形の storage バインド（binding 4）が無い");
        assert!(src.contains("fn interaction_wave_coeffs("),
            "テクセルごとの係数引き（水域走査）が消えている");
        // 1 回目・2 回目以降の**両方**のサブステップが係数を引くこと。
        // 片方だけだと「1 サブステップ目だけ粘度が効く」という気づきにくい不具合になる。
        assert_eq!(src.matches("interaction_wave_coeffs(").count(), 3,
            "係数引きは 定義1 + 2 エントリからの呼び出し2 = 3 箇所であること");
    }

    /// Rust の GPU 構造体と WGSL の `WaterPhysicsRegion` でフィールド並びが一致すること。
    #[test]
    fn water_region_fields_match_shader() {
        let body = field_shader()
            .split_once("struct WaterPhysicsRegion {")
            .expect("struct WaterPhysicsRegion が見つからない").1
            .split_once('}').expect("struct の終端が見つからない").0;
        let names: Vec<String> = body.lines()
            .map(|l| l.trim())
            .filter(|l| !l.is_empty() && !l.starts_with("//"))
            .filter_map(|l| l.split_once(':').map(|(n, _)| n.trim().to_string()))
            .collect();
        assert_eq!(names, vec!["min_xz", "max_xz", "wave_k", "wave_damp", "_pad0", "_pad1"],
            "WGSL 側 WaterPhysicsRegion の並びが Rust 側 WaterPhysicsRegionGpu と不一致");
    }

    /// **コンポーネントの既定減衰率がエンジンの時定数と等価**であること。
    ///
    /// 片方だけ変えると「既定の水なのに減衰だけ変わっている」という
    /// 気づきにくい退行になるため、2 つの独立した既定値をここで縛る。
    #[test]
    fn default_ripple_damping_matches_engine_tau() {
        use crate::engine::components::water_volume_component::WaterVolumeComponentData;
        let d = WaterVolumeComponentData::default();
        assert!((d.ripple_damping - INTERACTION_WAVE_DEFAULT_DAMPING_RATE).abs() < 1e-6,
            "コンポーネント既定 {} とエンジン既定 {} が食い違う",
            d.ripple_damping, INTERACTION_WAVE_DEFAULT_DAMPING_RATE);
        // 率で計算した既定の減衰係数が、旧来の exp(-dt/τ) と一致すること。
        let legacy = (-INTERACTION_WAVE_FIXED_DT_SECS / INTERACTION_WAVE_DECAY_TAU_SECS).exp();
        assert!((interaction_wave_damp_per_substep() - legacy).abs() < 1e-7,
            "既定の減衰係数が I2 実装時の値から動いている");
    }

    /// 物性矩形の GPU 変換は値を一切加工しないこと（クランプは CPU 側で完了済み）。
    #[test]
    fn region_to_gpu_is_a_verbatim_copy() {
        let r = WaterPhysicsRegion {
            min_xz: [-1.0, -2.0], max_xz: [3.0, 4.0], wave_k: 0.09, wave_damp: 0.5,
        };
        let g = WaterPhysicsRegionGpu::from(r);
        assert_eq!(g.min_xz, r.min_xz);
        assert_eq!(g.max_xz, r.max_xz);
        assert_eq!(g.wave_k, r.wave_k);
        assert_eq!(g.wave_damp, r.wave_damp);
    }

    /// ソース構造体は std430 の 32 バイトであること（波振幅を足しても変わらない）
    /// （ズレると GPU が隣のソースのデータを読む＝静かな描画バグ）。
    #[test]
    fn source_is_32_bytes() {
        assert_eq!(std::mem::size_of::<InteractionSourceGpu>(), 32);
    }

    /// UBO のフィールド並びが「Rust / 更新シェーダ / 草シェーダ / 水面シェーダ」の
    /// 4 箇所で一致すること。
    /// ここがズレると、草が窓原点を誤読して場をまったく別の場所からサンプルする。
    #[test]
    fn interaction_uniform_fields_match_grass_shader() {
        /// WGSL ソースから `struct InteractionFieldUniform { ... }` のフィールド名列を抜き出す。
        fn field_names(src: &str) -> Vec<String> {
            let body = src
                .split_once("struct InteractionFieldUniform {")
                .expect("struct InteractionFieldUniform が見つからない")
                .1
                .split_once('}')
                .expect("struct の終端が見つからない")
                .0;
            body.lines()
                .map(|l| l.trim())
                .filter(|l| !l.is_empty() && !l.starts_with("//") && !l.starts_with("///"))
                .filter_map(|l| l.split_once(':').map(|(n, _)| n.trim().to_string()))
                .collect()
        }
        let update = field_names(field_shader());
        let grass  = field_names(include_str!("../shaders/grass_gbuffer.wgsl"));
        // 水面側の宣言は Phase W5.1 で共有モジュール（頂点段からも読む）へ移った。
        let water  = field_names(include_str!("../shaders/water_height_field.wgsl"));
        assert_eq!(update, grass,
            "interaction_field.wgsl と grass_gbuffer.wgsl の InteractionFieldUniform が不一致");
        assert_eq!(update, water,
            "interaction_field.wgsl と water_surface.wgsl の InteractionFieldUniform が不一致");
        assert_eq!(update.len(), 14, "Rust 側 InteractionFieldUniformGpu と本数を揃えること");
    }

    /// 窓原点はテクセル単位にスナップされ、カメラ中心を包むこと。
    #[test]
    fn window_origin_is_snapped_and_centered() {
        let origin = snap_window_origin([10.03, 0.0, -7.77]);
        // テクセル格子に載っている（= texel の整数倍）。
        for v in origin {
            let ratio = v / INTERACTION_FIELD_TEXEL_SIZE;
            assert!((ratio - ratio.round()).abs() < 1e-3, "{v} がテクセル格子に載っていない");
        }
        // カメラ XZ が窓の内側にある。
        assert!(origin[0] <= 10.03 && 10.03 < origin[0] + INTERACTION_FIELD_EXTENT_M);
        assert!(origin[1] <= -7.77 && -7.77 < origin[1] + INTERACTION_FIELD_EXTENT_M);
    }

    /// カメラが 1 テクセル未満だけ動いても窓原点は動かない（＝場がにじまない条件）。
    #[test]
    fn window_origin_is_stable_under_subtexel_motion() {
        let a = snap_window_origin([0.0, 0.0, 0.0]);
        let b = snap_window_origin([INTERACTION_FIELD_TEXEL_SIZE * 0.4, 0.0, 0.0]);
        assert_eq!(a, b);
        // 1 テクセル動けば、ちょうど 1 テクセルぶんだけずれる。
        let c = snap_window_origin([INTERACTION_FIELD_TEXEL_SIZE, 0.0, 0.0]);
        assert!((c[0] - a[0] - INTERACTION_FIELD_TEXEL_SIZE).abs() < 1e-4);
    }

    /// テクセルサイズと解像度・窓幅の関係（数値の根拠）が崩れていないこと。
    #[test]
    fn texel_size_matches_extent_and_resolution() {
        assert!((INTERACTION_FIELD_TEXEL_SIZE - 0.125).abs() < 1e-6, "64m / 512 = 0.125m");
        assert_eq!(workgroup_count(INTERACTION_FIELD_RESOLUTION), 64, "512 / 8 = 64");
        // 端数のある解像度でも切り上げになること。
        assert_eq!(workgroup_count(9), 2);
    }
}
