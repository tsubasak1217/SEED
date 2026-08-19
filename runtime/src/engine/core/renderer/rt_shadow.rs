// ============================================================
//  rt_shadow.rs — インラインレイトレ影の加速構造管理（Phase R8）
//
//  「品質オプション」としてのインラインレイトレ影を提供する。RT 対応 GPU
//  （EXPERIMENTAL_RAY_QUERY + EXPERIMENTAL_RAY_TRACING_ACCELERATION_STRUCTURE）
//  でのみ有効化され、非対応 GPU では一切のリソースを生成せず従来の
//  シャドウマップ経路（renderer/shadow.rs）が完全に無変更で動作する。
//
//  【役割分担（ECS/単一責任）】
//    - 本モジュール: BLAS（メッシュプリミティブ単位）と TLAS（フレーム単位）の
//      構築・キャッシュ・更新、および RT 影サンプリング用の group 4 複合
//      BindGroup（既存ライト/シャドウ binding 0〜5 ＋ TLAS binding 6）の保持。
//    - frame_renderer 側は「キャスター（GpuModel・バッチ）を渡して
//      prepare_and_build を呼ぶ」だけに留める。
//
//  【設計方針】
//    - シェーダバリアントは GPU 能力で静的に選ぶ（RT 対応時は常に RT パイプライン）。
//      設定 rt_shadows のオン/オフは LightMeta.rt_shadows フラグで実行時切替する。
//      → 設定変更でパイプラインを差し替える必要がない（再起動不要）。
//    - BLAS は「source_path + メッシュ index + プリミティブ index」粒度で一度だけ構築し
//      キャッシュする（非スキンのみ）。TLAS は cast_shadows=true の全インスタンス
//      （カメラカリング前）から毎フレーム再構築する（画面外キャスターも影を落とせる）。
//
//  【スキンメッシュ（Phase RT-Skin）】
//    スキンメッシュも TLAS へ登録する。BLAS は「source_path+mesh+prim」ではなく
//    **インスタンスまで含めた粒度**（ポーズが異なれば頂点も異なるため共有できない）で
//    毎フレーム再構築する。変形後頂点の書き出しと BLAS の管理は `rt_skin_blas.rs` が担い、
//    本モジュールは「TLAS への詰め込み・マスク決定・bindless レコード生成」だけを
//    非スキンとまったく同じ流儀で行う。詳細は rt_skin_blas.rs のヘッダを参照。
//
//  【v1 の割り切り（TODO）】
//    - rect/point のソフトシャドウ（複数サンプル面光源）は未対応。v1 はハード 1 本。
// ============================================================

use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, Ordering};

use wgpu::{
    AccelerationStructureFlags, AccelerationStructureGeometryFlags,
    AccelerationStructureUpdateMode, BlasBuildEntry, BlasGeometries,
    BlasGeometrySizeDescriptors, BlasTriangleGeometry, BlasTriangleGeometrySizeDescriptor,
    CreateBlasDescriptor, CreateTlasDescriptor, TlasInstance, TlasPackage,
};

use super::bindless::{BindlessResources, BindlessInstanceRecord, BINDLESS_FLAG_ELIGIBLE, BINDLESS_FLAG_MASK, BINDLESS_FLAG_GEOM, BINDLESS_DUMMY_TEX_INDEX};
use super::gpu_resources::{GpuModel, GpuPrimitive, InstancedModelBatch, RtSkinnedPrimRef};
use super::lighting::LightBuffer;
use super::pipeline::SkinDeformPipeline;
use super::rt_skin_blas::{
    skin_blas_size_desc, RtSkinBlasManager, SkinBlasKey, SkinPrimInput, SKIN_DEFORM_VERTEX_STRIDE,
};
use super::shadow::ShadowResources;
use crate::engine::core::loader::model::AlphaMode;

// ─── グローバル対応フラグ ────────────────────────────────────
//
// デバイス初期化（renderer/mod.rs）で 1 回だけ確定させる。gpu_resources が
// 頂点/インデックスバッファへ BLAS_INPUT 用途を付与するか判断するために参照し、
// DrawContext が RT パイプライン/リソースを生成するか判断するためにも参照する。
static RT_SHADOWS_SUPPORTED: AtomicBool = AtomicBool::new(false);

/// RT 対応フラグを設定する（デバイス初期化時に 1 回）。
pub fn set_rt_shadows_supported(v: bool) {
    RT_SHADOWS_SUPPORTED.store(v, Ordering::Relaxed);
}

/// RT 対応フラグを取得する。
pub fn rt_shadows_supported() -> bool {
    RT_SHADOWS_SUPPORTED.load(Ordering::Relaxed)
}

// ─── 定数 ────────────────────────────────────────────────────

/// TLAS に格納できるインスタンス（キャスター×メッシュノードプリミティブ）の上限。
/// これを超えるキャスターは影を落とさない（オーバーフロー時に 1 回だけ警告）。
pub const MAX_RT_INSTANCES: u32 = 4096;

/// マテリアルレコード（平均アルベド storage ／ bindless インスタンステーブル）の上限。
///
/// 【なぜ TLAS インスタンス数と別なのか（Phase RT-Skin の BLAS 統合）】
/// スキンメッシュは「1 体 = 1 BLAS（複数ジオメトリ）= 1 TLAS インスタンス」だが、
/// マテリアルはプリミティブ（＝ジオメトリ）ごとに異なる。したがって
/// **1 TLAS インスタンスが複数レコードを消費する**。シェーダは
/// `record = instance_custom_data + geometry_index` でレコードを引く
/// （非スキンは geometry_index が常に 0 なので従来どおり 1:1）。
/// 詳細は本ファイル末尾の「レコード索引規約」を参照。
///
/// 値は当面 TLAS 上限と同じ 4096（レコード 1 件 = 平均アルベド 16B ＋ bindless 64B）。
pub const MAX_RT_RECORDS: u32 = MAX_RT_INSTANCES;

/// 1 フレームで新規構築する BLAS の最大数（分割ビルドの上限）。
///
/// 【なぜ必要か（実機クラッシュの直接原因）】
///   地形を作り直す／ハイトマップを読み込む／シーンを再ロードすると、
///   `invalidate_geometry_caches` で地形全チャンク（実測 ~72 個）の BLAS キャッシュが
///   一斉に空になる。次に RT 影が走るフレームで、それら全部（1 個あたり頂点 ~9000／
///   インデックス ~53000）を **1 回の command encoder + submit** でまとめて
///   `build_acceleration_structures` すると、GPU が長時間ビジーになり TDR
///   （タイムアウト検出＆復帰）→ デバイスロスト → wgpu の遅延破棄が submit の
///   snatch read lock 下で走って「snatch lock を再帰取得」パニック、という連鎖で落ちる。
///
/// 【対策】新規 BLAS のビルドを 1 フレームこの数までに制限し、残りは後続フレームへ
///   繰り越す。未構築のキャスターはその間 TLAS に登録されない（`prepare_and_build` の
///   TLAS ループが `blas_cache.get()==None` を素通りする既存挙動）ため、影が数フレーム
///   遅れて出るだけで描画は破綻しない。バックログがある間は `new_blas_built=true` の
///   ままなので毎フレーム TLAS を組み直して確実に消化し、消化し切れば静止スキップへ戻る。
///
/// 8 個 ≒ 地形 1.4 万三角形 ×8 の BLAS ビルド／フレーム。72 チャンクでも 9 フレーム
///（60fps で約 0.15 秒）で消化でき、1 submit あたりの GPU 占有を TDR しきい値より十分
/// 低く保てる大きさとして選んだ。
///
/// 【公開している理由】地形側（`terrain_ops::flush_rt_blas_prune`）が「1 フレームに
///   何チャンクぶんの BLAS を捨ててよいか」の予算をこの値と一致させるため。
///   ここより多く捨てても再構築が追いつかず、影が抜けている時間が伸びるだけで得が無い。
///   予算の根拠を 2 か所に書かないための単一情報源である。
pub const MAX_BLAS_BUILDS_PER_FRAME: usize = 8;

/// メッシュ頂点 1 個のバイトストライド（Vertex 構造体サイズ）。
/// 位置は各頂点の先頭（offset 0）の Float32x3。BLAS はこのストライドで位置のみを読む。
const VERTEX_STRIDE: wgpu::BufferAddress =
    std::mem::size_of::<crate::engine::core::loader::model::Vertex>() as wgpu::BufferAddress;

// ─── TLAS インスタンスマスク ─────────────────────────────────
//
// TLAS の各インスタンスへ「どの用途のレイに見えるか」を 8bit マスクで持たせ、
// レイ側の `cull_mask`（rt_shadow_on.wgsl の RT_SHADOW_CULL_MASK）との AND が 0 の
// インスタンスはトラバースから除外される（レイは素通りする）。
//
// これを使い、影のオクルーダを「不透明マテリアルのプリミティブ」だけに限定する。
// BLAS ジオメトリは常に OPAQUE フラグ付き（＝ヒットが即確定）なので、マスクを分けない限り
// Blend マテリアル（例: Sponza の dirt_decal＝汚れデカールの板ポリ）が完全不透明の遮蔽物として
// 影を落としてしまう。ラスタでは半透明にしか見えないものが真っ黒な影を落とす、という不整合が
// 発生していた。
//
// 【拡張余地】Mask（アルファテスト: 葉・鎖・フェンス等）を影に落としたい場合:
//   - 正しく落とすには rayQuery の candidate 段階でヒット三角形のマテリアル／UV／
//     ベースカラーテクスチャを引き、アルファテストしてから ConfirmIntersection する
//     必要がある（naga 25 は rayQueryGetCandidateIntersection / rayQueryConfirmIntersection を
//     サポートしており、WGSL 構文上の障害は無い）。
//   - しかし「ヒットした三角形からマテリアルとテクスチャを引く」には bindless
//     （binding_array + 頂点/インデックスバッファの storage 公開）が必要で、本エンジンの
//     現在のバインドモデル（マテリアルごとの BindGroup 差し替え）とは根本的に噛み合わない。
//     現状は非現実的と判断し、Mask も非オクルーダ（RT_MASK_NON_OPAQUE）として扱う。
//   - 妥協案として「Mask だけ不透明扱いで影に含める」なら、下の mask 決定を
//     `AlphaMode::Blend` のみ非不透明にすればよい（1 行の変更で切り替えられる）。

/// 不透明（`AlphaMode::Opaque`）プリミティブのインスタンスマスク。影のオクルーダ。
/// rt_shadow_on.wgsl の `RT_SHADOW_CULL_MASK` と値を一致させること（ユニットテストで担保）。
pub const RT_MASK_OPAQUE: u8 = 0x01;

/// 非不透明（`AlphaMode::Blend` / `AlphaMode::Mask`）プリミティブのインスタンスマスク。
/// TLAS には登録するが影のレイからは見えない（将来 candidate 段階のアルファテストを
/// 実装するときのために、ジオメトリ自体は TLAS に残しておく）。
pub const RT_MASK_NON_OPAQUE: u8 = 0x02;

/// プリミティブの alpha_mode から TLAS インスタンスマスクを決める。
/// 「どのマテリアルが影を落とすか」の唯一の判断箇所（単一責任）。
fn instance_mask_for(alpha_mode: AlphaMode) -> u8 {
    match alpha_mode {
        AlphaMode::Opaque => RT_MASK_OPAQUE,
        // Blend: 半透明。不透明オクルーダにすると実物より濃い影が出る（症状の直接原因）。
        // Mask : アルファテスト。正しく落とすには上記 bindless が必要なため v1 では落とさない。
        AlphaMode::Blend | AlphaMode::Mask => RT_MASK_NON_OPAQUE,
    }
}

// ─── 色付き影（RT-Translucency）の α×transmission パッキング ──────────
//
// 平均アルベド storage の .rgb は DDGI（ddgi_probe_update.wgsl:`gi_albedo[ai].rgb`）と
// RT リフレクション（reflection_rt.wgsl:`rt_albedo[ai].rgb`）が「生のアルベド」として読む共有バッファ。
// **.rgb の意味は変えてはならない**。色付き影が必要とする 2 値（α＝base_color_factor.a と
// transmission）は、GI/反射が読まない .a に固定小数でビットパックして相乗りさせる。
// こうすることで第 2 storage を増やさずに済む（binding14/GI binding4/反射 binding3 の
// 三経路の bind group 配線を触らない）。
//
//   量子化: a_q = round(α * SHADOW_PACK_QUANT), t_q = round(tr * SHADOW_PACK_QUANT)（各 0..255）
//   パック: .a = a_q * SHADOW_PACK_RADIX + t_q（最大 255*256+255 = 65535）
//   → 65535 は f32 の整数完全表現域（2^24 = 16_777_216）内なので往復で誤差 0。
//   デコードは WGSL 側 rt_shadow_on.wgsl の rt_trace_translucent_tint と対（同名定数を共有）。
//
// 【透過モデル（1 枚の Blend 面を通る光の RGB 透過率 T）】
//   T = (1-α) + α·transmission·albedo.rgb
//   - α=1, tr=1 → T = albedo（色の付いた影＝色ガラスは baseColor で透過光を濾過する）
//   - α=1, tr=0 → T = 0    （透過しない被覆面は光を通さない＝暗い影）
//   - α=0        → T = 1    （影なし）

/// 影の透過率パックの 1 チャンネル量子化段数（8bit）。WGSL の SHADOW_PACK_QUANT と一致させること。
const SHADOW_PACK_QUANT: f32 = 255.0;
/// α を上位バイトへ寄せる基数。WGSL の SHADOW_PACK_RADIX と一致させること。
const SHADOW_PACK_RADIX: f32 = 256.0;

/// α（base_color_factor.a）と transmission を平均アルベド storage の .a へ詰める固定小数パック値を返す。
/// WGSL 側 rt_shadow_on.wgsl のデコードと必ず対にすること（往復テスト shadow_pack_roundtrip_and_transmittance が担保）。
fn pack_shadow_alpha_transmission(alpha: f32, transmission: f32) -> f32 {
    // round-to-nearest（+0.5 して floor）で 0..255 に量子化。clamp で範囲外入力も安全に丸める。
    let a_q = (alpha.clamp(0.0, 1.0) * SHADOW_PACK_QUANT + 0.5).floor();
    let t_q = (transmission.clamp(0.0, 1.0) * SHADOW_PACK_QUANT + 0.5).floor();
    a_q * SHADOW_PACK_RADIX + t_q
}

// ─── BLAS キャッシュキー ─────────────────────────────────────

/// BLAS を一意に識別するキー（共有モデルパス＋メッシュ index＋プリミティブ index）。
#[derive(Clone, PartialEq, Eq, Hash)]
struct BlasKey {
    source_path: String,
    mesh_idx:    usize,
    prim_idx:    usize,
}

impl BlasKey {
    fn new(source_path: &str, mesh_idx: usize, prim_idx: usize) -> Self {
        Self { source_path: source_path.to_string(), mesh_idx, prim_idx }
    }
}

// ─── RtShadowResources ───────────────────────────────────────

/// RT 影の加速構造一式（RT 対応 GPU でのみ生成される）。DrawContext が Option で保持。
pub struct RtShadowResources {
    /// TLAS（安全版パッケージ）。毎フレーム instances を書き換えて再ビルドする。
    /// TLAS 本体は生成後不変（同一リソースを再ビルドで更新）のため、下記 bind group は
    /// 起動時 1 回生成で使い回せる。
    tlas_package: TlasPackage,
    /// BLAS キャッシュ（キー = source_path+mesh+prim, 非スキンのみ）。初回のみ構築。
    blas_cache:   HashMap<BlasKey, wgpu::Blas>,
    /// group 4 複合 BindGroup（ライト binding0/1 ＋ シャドウ binding2〜5 ＋ TLAS binding6）。
    /// mesh_rt / skinned_mesh_rt パイプラインの描画で bind する。
    /// この BindGroup を実際に bind するのは RT 影オン時のみ。オン時は毎フレーム
    /// メインパス直前に TLAS を（再）ビルドするため、bind 時点で必ずビルド済みが保証される。
    pub bind_group: wgpu::BindGroup,
    /// インスタンス上限超過の警告を出したか（ログ爆発防止）。
    warned_overflow: bool,
    /// BLAS_INPUT 用途不足の警告を出したプリミティブ（毎フレーム同一警告のログ爆発防止）。
    warned_usage: std::collections::HashSet<BlasKey>,

    // ── 静止シーンの TLAS 再構築スキップ用（性能最適化）───────────────
    /// 直前フレームで TLAS を構築した際のインスタンス内容シグネチャ（ハッシュ）。
    /// `None` = 未構築（初回は必ず構築する）。次フレームで同一シグネチャなら
    /// GPU 上の TLAS は前回内容と完全一致するため build_acceleration_structures を省く。
    /// シグネチャは「キャスターパス＋(mesh,prim)＋ワールド変換ビット列」を全インスタンス
    /// 順序どおりにハッシュしたもの。変換・追加削除・cast_shadows 変化で必ず変わる。
    last_tlas_sig: Option<u64>,
    /// 直近に TLAS へ登録したインスタンス数（[PERF] 表示用）。
    last_inst_count: u32,
    /// 直近に TLAS へ登録したスキン由来インスタンス数（[PERF] 表示用）。
    last_skin_inst_count: u32,
    /// 【診断】直近にログ出力した (0x02 総数<<16 | GEOM 数) のパック値。変化時のみ再ログ（ログ爆発防止）。
    /// RT 屈折の界面再屈折可否（GEOM 付与率）の裏取り用（毎フレームではなく内容変化時のみ 1 行）。
    last_geom_log: u32,

    /// TLAS インスタンス順（custom_data 順）のプリミティブ平均アルベド storage（Phase RT-GI）。
    /// DDGI のヒット点近似シェーディングで、ヒットした instance_custom_data で引く。
    /// 容量 MAX_RT_INSTANCES。TLAS を（再）構築したフレームにのみ詰め直してアップロードする。
    albedo_buffer: wgpu::Buffer,

    /// スキンメッシュの RT 加速構造マネージャ（Phase RT-Skin）。
    /// 変形後頂点バッファ・専用 BLAS・変形 compute の BindGroup を保持する。
    skin_blas: RtSkinBlasManager,
}

/// `prepare_and_build` の結果統計（[PERF] ログ用）。
pub struct RtBuildStat {
    /// このフレームで実際に TLAS を（再）構築したか。false = 静止スキップ。
    pub built: bool,
    /// TLAS に登録されているインスタンス数（構築時は今回値、スキップ時は前回値）。
    pub instances: u32,
    /// そのうちスキンメッシュ由来のインスタンス数（＝毎フレーム再構築したスキン BLAS 数）。
    /// 構築時は今回値、静止スキップ時は前回値。
    pub skin_instances: u32,
    /// スキン RT（エントリ確保・params 更新・変形 compute 記録）に費やした **CPU 側** 時間 [ms]。
    /// GPU 実行時間ではなくコマンド記録＋リソース管理のコスト。静止スキップ時は 0。
    pub skin_build_ms: f64,
}

impl RtShadowResources {
    /// RT 影リソースを生成する（RT 対応時のみ呼ぶこと）。
    ///
    /// - `rt_lights_bgl`: mesh_rt パイプラインの group 4 レイアウト
    ///   （ライト＋シャドウ＋TLAS の複合。binding 6 に acceleration_structure を含む）。
    /// - `shadow`:        シャドウ資源（binding 2〜5 を供給）。
    /// - `light_buffer`:  ライトバッファ（binding 0/1 を供給）。
    /// - `clusters`:      クラスタ資源（binding 7〜9 を供給。Phase C1）。
    pub fn new(
        device:        &wgpu::Device,
        rt_lights_bgl: &wgpu::BindGroupLayout,
        shadow:        &ShadowResources,
        light_buffer:  &LightBuffer,
        clusters:      &super::clustered::ClusterResources,
        gi:            &super::ddgi::GiResources,
    ) -> Self {
        // TLAS を生成する。フラグは高速ビルド優先（毎フレーム再構築のため）。
        let tlas = device.create_tlas(&CreateTlasDescriptor {
            label:         Some("RT Shadow TLAS"),
            max_instances: MAX_RT_INSTANCES,
            flags:         AccelerationStructureFlags::PREFER_FAST_BUILD,
            update_mode:   AccelerationStructureUpdateMode::Build,
        });

        // 平均アルベド storage（Phase RT-GI）。容量 = MAX_RT_INSTANCES × vec4<f32>（16B）。
        // TLAS インスタンス順（custom_data）に平均アルベド（.rgb）＋不透明度（.a=base_color_factor.a）を
        // 詰める。GI compute の binding4 と、色付き影（rt_shadow_on.wgsl の group4 binding14）の両方が読む。
        // BindGroup 生成前に作る（create_rt_bind_group が binding14 として参照するため）。
        let albedo_buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label:              Some("RT Instance Avg Albedo"),
            size:               (MAX_RT_INSTANCES as u64) * 16,
            usage:              wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        // TLAS を参照する group 4 複合 BindGroup を生成する。
        // as_binding() は &tlas を借用するため、TlasPackage へ move する前に生成する。
        // 生成された bind group は内部で TLAS リソース（Arc）を保持するため、以後
        // TlasPackage へ move しても有効であり、再ビルドで内容が更新されても使い回せる。
        // 平均アルベド storage（binding14）も同時に bind し、半透明レイヤーの色付き影に使う。
        let bind_group = light_buffer.create_rt_bind_group(
            device, rt_lights_bgl, shadow, clusters, gi, &tlas, &albedo_buffer,
        );

        let tlas_package = TlasPackage::new(tlas);

        Self {
            tlas_package,
            blas_cache: HashMap::new(),
            bind_group,
            warned_overflow: false,
            warned_usage: std::collections::HashSet::new(),
            last_tlas_sig: None,
            last_inst_count: 0,
            last_geom_log: u32::MAX, // 初回は必ずログを出す（0 と衝突しない番兵）。
            albedo_buffer,
            skin_blas: RtSkinBlasManager::new(),
            last_skin_inst_count: 0,
        }
    }

    /// GI compute 用の TLAS 参照（RtShadowResources と共有する加速構造）。
    pub fn tlas(&self) -> &wgpu::Tlas { self.tlas_package.tlas() }

    /// TLAS インスタンス順の平均アルベド storage への参照（GI compute の binding4 に使う）。
    pub fn albedo_buffer(&self) -> &wgpu::Buffer { &self.albedo_buffer }

    /// 解放された統合バッチキー（batch_key）に紐づく BLAS キャッシュと警告集合を追従解放する。
    ///
    /// BLAS は「source_path（＝ frame_renderer が渡す batch_key）＋mesh＋prim」粒度でキャッシュされ、
    /// これまで削除経路が無かった。マテリアルのインライン編集で署名付きの batch_key が量産されると、
    /// 巨大な BLAS が無制限に蓄積して VRAM が枯渇する（本修正の主因）。frame_renderer 側で stale と
    /// 判定された batch_key を受け取り、`BlasKey.source_path` が一致するエントリを解放する。
    ///
    /// - キー一致は「完全一致」（`source_path == freed_key`）。`source_path` は連結済みの batch_key
    ///   そのものなので前方一致は不要で、部分一致による誤解放も避けられる。
    /// - TLAS は毎フレーム casters から詰め直すため、BLAS がキャッシュから消えれば登録されなくなる
    ///   （`prepare_and_build` は `blas_cache.get(&key)` が None のインスタンスをスキップする）。
    ///
    /// 返り値: 解放した BLAS エントリ数（ログ表示用）。
    pub fn prune_source_paths(&mut self, freed_keys: &[String]) -> usize {
        if freed_keys.is_empty() { return 0; }
        let before = self.blas_cache.len();
        self.blas_cache.retain(|k, _| !freed_keys.iter().any(|f| k.source_path == *f));
        // 警告集合（BLAS_INPUT 用途不足）も同じキーで掃除し、無限成長を防ぐ。
        self.warned_usage.retain(|k| !freed_keys.iter().any(|f| k.source_path == *f));
        // スキン側（Phase RT-Skin）も同じキー体系（batch_key）なので同時に掃除する。
        // スキンエントリは「インスタンスごとの変形後頂点バッファ」を持つぶん 1 件が重く、
        // 取りこぼすと VRAM の蓄積が非スキンより速い。
        let skin_freed = self.skin_blas.prune_batch_keys(freed_keys);
        before - self.blas_cache.len() + skin_freed
    }

    /// BLAS（新規のみ）と TLAS を command encoder へ記録する。
    ///
    /// - `casters`: (source_path, GpuModel, バッチ) の並び。cast_shadows=true で
    ///   事前フィルタ済み。TLAS へは各バッチの「カメラカリング前・全インスタンス」×
    ///   「非スキンのメッシュノードプリミティブ」を登録する。
    ///
    /// フレーム先頭（シャドウパスの位置）で呼ぶこと。build_acceleration_structures は
    /// 「BLAS をビルドしてから同一呼び出し内で TLAS が参照する」ことを許すため、
    /// 新規 BLAS のビルドと TLAS ビルドを 1 回の呼び出しにまとめる。
    pub fn prepare_and_build(
        &mut self,
        device:  &wgpu::Device,
        queue:   &wgpu::Queue,
        encoder: &mut wgpu::CommandEncoder,
        casters: &[(&str, &GpuModel, &InstancedModelBatch)],
        // バインドレス（B2）: 対応 GPU でのみ Some。TLAS 詰め直しと同順（custom_data 順）で
        // `BindlessInstanceRecord` テーブルを埋め、RT 反射のヒットシェーディングが引く。
        // 既存の平均アルベド storage（albedo_buffer）は当面併存（DDGI/色付き影の読み手を壊さない）。
        bindless: Option<&BindlessResources>,
        // スキンメッシュの変形 compute パイプライン（Phase RT-Skin）。
        // None を渡すとスキンメッシュは一切 TLAS に載らない（従来挙動へ縮退）。
        // 実運用では常に Some だが、パイプライン非依存のテストを書けるよう Option にしている。
        skin_deform: Option<&SkinDeformPipeline>,
    ) -> RtBuildStat {
        // ── 1. 新規 BLAS の作成対象を収集（キャッシュ未登録の非スキンプリミティブ）──
        // 借用衝突を避けるため、先に「作成すべきキー＋対象プリミティブ参照」を集める。
        let mut to_build: Vec<(BlasKey, &GpuPrimitive)> = Vec::new();
        for (path, gpu, _batch) in casters {
            for (mesh_idx, mesh) in gpu.meshes.iter().enumerate() {
                for (prim_idx, prim) in mesh.primitives.iter().enumerate() {
                    // スキン用頂点を持つプリミティブは v1 対象外
                    // （静止時姿勢の頂点で BLAS を作っても変形後と一致しないため）。
                    if prim.skin_vertex_buffer.is_some() { continue; }
                    let key = BlasKey::new(path, mesh_idx, prim_idx);
                    if self.blas_cache.contains_key(&key) { continue; }
                    // 同一フレーム内で同一キーが複数キャスターに現れる場合の重複追加も防ぐ。
                    if to_build.iter().any(|(k, _)| *k == key) { continue; }
                    // ── 防御チェック: BLAS 入力バッファの用途検証 ──────────
                    // 頂点/インデックスバッファに BLAS_INPUT 用途が無いまま
                    // build_acceleration_structures へ渡すと wgpu の検証パニックで
                    // アプリ全体が落ちる（実機で発生済み: 'Index Buffer' の用途漏れ）。
                    // 生成経路の見落とし・将来の新経路追加に備えてここで検証し、
                    // 不足時は警告ログ＋そのプリミティブをスキップ（RT 影を落とさない
                    // だけの縮退動作）にする。パニックはさせない。
                    let vb_ok = prim.vertex_buffer.usage().contains(wgpu::BufferUsages::BLAS_INPUT);
                    let ib_ok = prim.index_buffer.usage().contains(wgpu::BufferUsages::BLAS_INPUT);
                    if !vb_ok || !ib_ok {
                        // 同一プリミティブの警告は 1 回だけ（毎フレーム呼ばれるため）。
                        if self.warned_usage.insert(key.clone()) {
                            eprintln!(
                                "[SEED RT] 警告: {} mesh#{} prim#{} のバッファに BLAS_INPUT 用途が\
                                 ありません（vertex={vb_ok}, index={ib_ok}）。このプリミティブは\
                                 RT 影を落としません（gpu_resources.rs の生成経路を確認してください）",
                                key.source_path, key.mesh_idx, key.prim_idx
                            );
                        }
                        continue;
                    }
                    to_build.push((key, prim));
                }
            }
        }

        // ── 1.5. 新規 BLAS ビルドを 1 フレームあたりの上限で分割する ─────────
        // 地形再構築等でキャッシュが一斉に空になると to_build が数十件（全チャンク）に
        // 膨らむ。それを 1 submit でまとめてビルドすると GPU が長時間ビジーになり
        // TDR → デバイスロスト → snatch 再帰パニックへ連鎖する（本修正の直接対象）。
        // 上限を超えた分はここで切り落とし、後続フレームで再収集→ビルドさせる
        //（キャッシュ未登録のままなので次フレームも to_build に再度挙がる）。
        // 未ビルドのキャスターは下の TLAS ループが素通りするため、影が数フレーム遅れて
        // 出るだけで描画は壊れない。バックログが残る間は new_blas_built=true のまま
        // 毎フレーム TLAS を組み直して確実に消化する。
        let blas_backlog = to_build.len().saturating_sub(MAX_BLAS_BUILDS_PER_FRAME);
        if blas_backlog > 0 {
            to_build.truncate(MAX_BLAS_BUILDS_PER_FRAME);
            eprintln!(
                "[SEED RT] BLAS 分割ビルド: このフレーム {} 件を構築、残り {} 件は後続フレームへ繰り越し\
                （一斉再構築による GPU 過負荷／デバイスロストの回避）",
                to_build.len(), blas_backlog
            );
        }

        // ── 2. BLAS を create_blas してキャッシュへ挿入（初回のみ・ログ）──
        for (key, prim) in &to_build {
            let blas = create_blas_for_prim(device, prim);
            self.blas_cache.insert(key.clone(), blas);
            eprintln!(
                "[SEED RT] BLAS 構築: {} mesh#{} prim#{}（頂点 {} / インデックス {}）",
                key.source_path, key.mesh_idx, key.prim_idx, prim.vertex_count, prim.index_count
            );
        }

        // ── 2.2. スキンエントリの収集（Phase RT-Skin）─────────────────
        // 「スキンプリミティブ × インスタンス」を今フレームぶん列挙する。ここでは CPU 側の
        // 情報だけを集め、GPU リソースの生成（＝上限判定を含む）は静止スキップ判定の後で行う。
        // スキップするフレームで ensure_entry を呼ぶと、変形 compute を積まないのに
        // live_keys/pending だけが更新されて状態がズレるため、順序は必ずこの通りにする。
        // 【Phase RT-Skin の BLAS 統合】列挙の単位は「メッシュノード × インスタンス」＝1 体。
        // さらに TLAS インスタンスマスク（不透明 / 非不透明）ごとに分割する。マスクは
        // TLAS インスタンス単位の属性であり、1 BLAS = 1 TLAS インスタンス = 1 マスクだからである
        //（混ぜると半透明部位が不透明の影を落とす退行になる）。
        let mut skin_cands: Vec<SkinEntryInfo> = Vec::new();
        if skin_deform.is_some() {
            for (caster_i, (_path, gpu, batch)) in casters.iter().enumerate() {
                batch.rt_enumerate_skinned(|g| {
                    // ポーズ依存値（再生時刻）。Animator 非駆動（None）は静止扱いなので
                    // 実値と衝突しない番兵ビットを使う。
                    let anim_bits = batch.anim_time_overrides.get(g.inst_idx)
                        .and_then(|o| *o)
                        .map(f32::to_bits)
                        .unwrap_or(ANIM_TIME_NONE_BITS);
                    // マスク別に振り分ける（並び順は列挙順を保つ＝ジオメトリ順の決定性）。
                    for mask in [RT_MASK_OPAQUE, RT_MASK_NON_OPAQUE] {
                        let prims: Vec<RtSkinnedPrimRef> = g.prims.iter().copied()
                            .filter(|p| instance_mask_for(gpu.primitive_alpha_mode(p.material_idx)) == mask)
                            .collect();
                        if prims.is_empty() { continue; }
                        skin_cands.push(SkinEntryInfo {
                            caster_i,
                            node_idx:    g.node_idx,
                            inst_idx:    g.inst_idx,
                            mask,
                            transform:   g.transform,
                            lod:         g.lod,
                            compact_idx: g.compact_idx,
                            anim_bits,
                            prims,
                        });
                    }
                });
            }
        }

        // ── 2.5. 静止シーン判定（TLAS 再構築スキップ）─────────────────
        // TLAS の内容は「キャスターパス × (mesh,prim) × ワールド変換」で完全に決まる。
        // これらを順序どおりにハッシュしたシグネチャが前フレームと一致し、かつ今回
        // 新規 BLAS を作っていなければ、GPU 上の TLAS は前回内容と完全一致するため
        // build_acceleration_structures（数百インスタンスの GPU 再構築）を丸ごと省ける。
        // エディタで何も動かしていない間はほぼ毎フレームここでスキップされる。
        //
        // 【正しさ】シグネチャはキャッシュ有無に関わらず全列挙インスタンスを対象にする
        // （過剰無効化は許容＝安全側、見逃しは不可）。変換変更／アクタ追加削除／
        // cast_shadows 変化（casters 集合が変わる）／モデル差替（新規 BLAS→強制再構築）で
        // 必ずシグネチャが変わるか new_blas_built=true になり、確実に再構築される。
        let new_blas_built = !to_build.is_empty();
        let new_sig = {
            use std::hash::Hasher;
            let mut hasher = std::collections::hash_map::DefaultHasher::new();
            for (path, gpu, batch) in casters {
                // パスはキャスター単位で 1 回だけ混ぜる（列挙順がグループを保つ）。
                hasher.write(path.as_bytes());
                hasher.write_u8(0xff); // 区切り
                batch.rt_enumerate(|mesh_idx, prim_idx, material_idx, transform| {
                    hasher.write_usize(mesh_idx);
                    hasher.write_usize(prim_idx);
                    // インスタンスマスク（alpha_mode 由来）も内容の一部。マテリアル差し替えで
                    // Opaque⇔Blend が変わったときに TLAS 再構築を確実に発火させる。
                    hasher.write_u8(instance_mask_for(gpu.primitive_alpha_mode(material_idx)));
                    // 色付き影（RT-Translucency）の平均アルベド storage は TLAS 再構築フレームでしか
                    // 再アップロードされない（下の queue.write_buffer は build 経路のみ）。よって
                    // パックの入力そのもの＝平均アルベド（.rgb 色相・.a=被覆 α）と透過率 transmission を
                    // シグネチャに含める（パック値ではなく生の入力を混ぜるので、pack 方式が変わっても
                    // 追従は保たれる）。含めないと、alpha_mode/変換を変えないマテリアルの in-place 編集
                    //（アルファ・色・透過率のスライダー）で shadow 用の .a パックが変わっても
                    // シグネチャが不変のまま静止スキップが発火し、GPU 上の albedo_buffer が古い値で
                    // 固定され、色付き影に編集が反映されない（＝実行時のアルファ/色/透過率変更が影に出ない）。
                    let alb = gpu.primitive_avg_albedo(material_idx);
                    for v in &alb { hasher.write_u32(v.to_bits()); }
                    hasher.write_u32(gpu.primitive_transmission(material_idx).to_bits());
                    // バインドレス（B2）: instance_table は build 経路でしか再アップロードされない
                    // ため、レコードを左右する値（base_color_factor・albedo tex index・UV/index
                    // オフセット）もシグネチャに含める。含めないと、変換/alpha_mode を変えない
                    // マテリアルの in-place 編集（factor）や再登録でのオフセット変化が反映されない。
                    if bindless.is_some() {
                        let bcf = gpu.primitive_base_color_factor(material_idx);
                        for v in &bcf { hasher.write_u32(v.to_bits()); }
                        hasher.write_u32(gpu.primitive_bindless_albedo_tex_index(material_idx));
                        // B3: Mask 判別（instance_mask_for は Blend/Mask を同一 0x02 に潰すため、
                        // Mask フラグと alpha_cutoff はここで別途混ぜる。含めないと Blend⇔Mask 切替や
                        // カットオフ編集で instance_table が古いまま固定され、葉の形の影に反映されない）。
                        let is_mask = matches!(gpu.primitive_alpha_mode(material_idx), AlphaMode::Mask);
                        hasher.write_u8(is_mask as u8);
                        hasher.write_u32(gpu.primitive_alpha_cutoff(material_idx).to_bits());
                        if let Some(gp) = gpu.meshes.get(mesh_idx)
                            .and_then(|m| m.primitives.get(prim_idx)) {
                            hasher.write_u32(gp.bindless_uv_offset);
                            hasher.write_u32(gp.bindless_index_offset);
                            hasher.write_u8(gp.bindless_eligible as u8);
                        }
                    }
                    for v in &transform { hasher.write_u32(v.to_bits()); }
                });
            }

            // ── スキンエントリ（Phase RT-Skin）をシグネチャに含める ──────────
            // 【これが無いと何が壊れるか】アニメーション再生中は「変換行列もマテリアルも
            // 変わらないのにポーズだけが変わる」フレームが普通に発生する。ポーズ依存値を
            // 混ぜないとシグネチャが不変のまま静止スキップが発火し、変形 compute も
            // BLAS 再構築も走らず、**RT 上のキャラだけが姿勢を固めたまま止まる**。
            // よって再生時刻（anim_time_overrides）と、行列位置を決める lod/compact_idx を
            // 必ず含める。
            hasher.write_u8(SKIN_SIG_MARKER); // 非スキン部との境界（連結の曖昧さを消す）
            for c in &skin_cands {
                // キャスターの識別（batch_key）。同一 (node,inst) が別バッチにあり得る。
                hasher.write(casters[c.caster_i].0.as_bytes());
                hasher.write_u8(SKIN_SIG_MARKER);
                hasher.write_usize(c.node_idx);
                hasher.write_usize(c.inst_idx);
                hasher.write_u8(c.mask);
                hasher.write_usize(c.lod);
                hasher.write_u32(c.compact_idx);
                hasher.write_u32(c.anim_bits);
                for v in &c.transform { hasher.write_u32(v.to_bits()); }
                // プリミティブ列（＝BLAS のジオメトリ順・レコードの並び順）。件数も混ぜて
                // 連結の曖昧さを消す。順序が変わればレコード対応が変わるので必ず再構築させる。
                hasher.write_usize(c.prims.len());
                let gpu = casters[c.caster_i].1;
                for p in &c.prims {
                    hasher.write_usize(p.mesh_idx);
                    hasher.write_usize(p.prim_idx);
                    // マテリアル由来（色付き影・bindless レコード）は非スキンと同じ理由で含める。
                    let alb = gpu.primitive_avg_albedo(p.material_idx);
                    for v in &alb { hasher.write_u32(v.to_bits()); }
                    hasher.write_u32(gpu.primitive_transmission(p.material_idx).to_bits());
                    if bindless.is_some() {
                        let bcf = gpu.primitive_base_color_factor(p.material_idx);
                        for v in &bcf { hasher.write_u32(v.to_bits()); }
                        hasher.write_u32(gpu.primitive_bindless_albedo_tex_index(p.material_idx));
                        let is_mask = matches!(gpu.primitive_alpha_mode(p.material_idx), AlphaMode::Mask);
                        hasher.write_u8(is_mask as u8);
                        hasher.write_u32(gpu.primitive_alpha_cutoff(p.material_idx).to_bits());
                    }
                }
            }
            hasher.finish()
        };

        // 初回（last_tlas_sig=None）は必ず構築する。新規 BLAS があるフレームも必ず構築する。
        if !new_blas_built && self.last_tlas_sig == Some(new_sig) {
            // 静止フレーム: GPU 上の TLAS は前回のまま有効。CPU 詰め直し・GPU ビルドを共に省く。
            //
            // 【スキンも一緒にスキップする理由（整合性）】ここで抜けると
            // `skin_blas.record_deform` を積まないため、変形後頂点バッファは前フレームの
            // 内容のまま据え置かれる。同時にスキン BLAS も再構築しないので、
            // 「バッファの中身」と「BLAS が畳み込んだ形状」は完全に一致したままになる。
            // 片方だけ更新すると両者が食い違い、RT 上でメッシュが壊れる。
            // シグネチャにポーズ依存値を含めてあるので、動いているフレームはここを通らない。
            return RtBuildStat {
                built:          false,
                instances:      self.last_inst_count,
                skin_instances: self.last_skin_inst_count,
                skin_build_ms:  0.0,
            };
        }
        self.last_tlas_sig = Some(new_sig);

        // ── 2.7. スキンエントリの GPU リソース確保＋変形 compute の記録 ──────
        // ここから先は必ず TLAS を再構築する経路なので、変形出力と BLAS を同じフレームで
        // 揃えて更新できる。
        // (キー, 列挙情報, このフレームに BLAS を構築し直す必要があるか)
        let mut skin_accepted: Vec<(SkinBlasKey, &SkinEntryInfo, bool)> = Vec::new();
        let skin_t0 = std::time::Instant::now();
        if let Some(deform) = skin_deform {
            self.skin_blas.begin_frame();
            // 【体単位の原子的な予約】1 体は分類（マスク）ごとに最大 2 エントリへ割れる。
            // 1 件ずつ上限判定すると「不透明部位だけ載って半透明部位が弾かれる」＝
            // 体の途中で切れる部分受理が起きうるため、同一 (caster, node, inst) の
            // 連続区間をまとめて枠を確保できるときだけ登録する。
            // （`skin_cands` は列挙時に同一体の分類を隣接させて push している）
            let mut i = 0usize;
            while i < skin_cands.len() {
                let head = &skin_cands[i];
                let body_id = (head.caster_i, head.node_idx, head.inst_idx);
                let mut j = i;
                while j < skin_cands.len() {
                    let c = &skin_cands[j];
                    if (c.caster_i, c.node_idx, c.inst_idx) != body_id { break; }
                    j += 1;
                }
                let group = &skin_cands[i..j];
                i = j;

                // 枠が足りなければこの体は 1 件も登録しない（部分受理の禁止）。
                if !self.skin_blas.can_accept_group(group.len()) { continue; }

                for c in group {
                    let (path, gpu, batch) = &casters[c.caster_i];
                    let Some(skin) = batch.skin.as_ref() else { continue };
                    // このエントリを構成するプリミティブ実体を解決する。
                    // 解決できないものは落とす（モデル実体との不整合＝安全側）。
                    let prims: Vec<SkinPrimInput<'_>> = c.prims.iter()
                        .filter_map(|p| {
                            gpu.meshes.get(p.mesh_idx)
                                .and_then(|m| m.primitives.get(p.prim_idx))
                                .map(|prim| SkinPrimInput {
                                    mesh_idx: p.mesh_idx, prim_idx: p.prim_idx, prim,
                                })
                        })
                        .collect();
                    if prims.is_empty() { continue; }
                    // ポーズ署名（anim_bits）を渡し、前回構築時と完全一致するエントリは
                    // 変形 compute も BLAS 再構築も省かせる（per-actor の静止スキップ）。
                    let status = self.skin_blas.ensure_entry(
                        device, queue, deform, path, c.node_idx, c.inst_idx, c.mask as u32,
                        &prims, skin, c.lod, c.compact_idx, c.anim_bits,
                    );
                    if status.accepted() {
                        skin_accepted.push((
                            SkinBlasKey::new(path, c.node_idx, c.inst_idx, c.mask as u32),
                            c,
                            status.needs_rebuild(),
                        ));
                    }
                }
            }
            // 今フレームに現れなかったエントリ（アクタ削除・上限超過）を解放して VRAM 蓄積を防ぐ。
            self.skin_blas.prune_unused();
            // 【依存の根拠】変形 compute を **BLAS ビルドより前** に、同じ encoder へ積む。
            // wgpu のコマンドは 1 本のキュー上で記録順に実行され、compute pass の書き込みは
            // 後続の加速構造ビルドから可視になる（パス境界に暗黙のバリアが入る）。
            // 逆順に積むと BLAS は前フレームの頂点を読む（1 フレーム遅れの姿勢になる）。
            self.skin_blas.record_deform(encoder, deform);
        }
        let skin_build_ms = skin_t0.elapsed().as_secs_f64() * 1000.0;

        // ── 3. 新規 BLAS のビルドエントリを構築 ──────────────────
        // サイズ記述子はビルド呼び出しまで生存させる必要があるため Vec に確保する。
        let size_descs: Vec<BlasTriangleGeometrySizeDescriptor> =
            to_build.iter().map(|(_, p)| blas_size_desc(p)).collect();
        let mut blas_entries: Vec<BlasBuildEntry> = to_build.iter().enumerate()
            .map(|(i, (key, prim))| {
                let blas = self.blas_cache.get(key).unwrap();
                BlasBuildEntry {
                    blas,
                    geometry: BlasGeometries::TriangleGeometries(vec![BlasTriangleGeometry {
                        size:                    &size_descs[i],
                        vertex_buffer:           &prim.vertex_buffer,
                        first_vertex:            0,
                        vertex_stride:           VERTEX_STRIDE,
                        index_buffer:            Some(&prim.index_buffer),
                        first_index:             Some(0),
                        transform_buffer:        None,
                        transform_buffer_offset: None,
                    }]),
                }
            })
            .collect();

        // ── 3.5. スキン BLAS のビルドエントリ（Phase RT-Skin）──────────────────
        // 非スキンと違い「新規のみ」では足りない。ポーズが変わればジオメトリが変わるため、
        // **ポーズが動いたエントリは毎フレーム**積み直す必要がある。逆に言えば、ポーズが
        // 前回構築時と 1 ビット違わないエントリ（アニメ停止中・非駆動）は積む必要がなく、
        // `ensure_entry` が返した `needs_rebuild` で選り分ける（`skin_targets` の
        // `needs_rebuild` フィールド）。スキップしたエントリは変形 compute も走らないので、
        // 変形出力バッファと BLAS は前フレームの内容で整合したまま据え置かれる。
        // 頂点入力は変形 compute の出力バッファ（ストライド 16B = vec4<f32>）。
        // インデックスは元プリミティブのものをそのまま使う（トポロジは変形で不変）。
        //
        // 添字ズレを構造的に防ぐため、まず「ビルド対象」を 1 パスで確定させてから
        // サイズ記述子とビルドエントリを **同じ Vec から順に** 作る
        //（記述子は build 呼び出しまで生存させる必要があるので別 Vec に確保する）。
        //
        // 【重要】この `skin_targets` が **TLAS 登録の対象集合でもある**（下の 4.5 が同じ Vec を回す）。
        // ビルド対象と TLAS 登録対象を別々に絞り込むと、片方だけ通ったエントリが
        // 「ビルドされていない BLAS を参照する TLAS インスタンス」になり wgpu の検証で落ちる。
        // 集合を 1 本に統一することで、その食い違いを構造的に不可能にしている。
        // ポーズ静止によるビルド省略は「集合を絞る」のではなく **同じ集合の中の
        // `needs_rebuild` フラグ**で表す。省略されたエントリの BLAS は過去フレームで
        // 必ず構築済み（`ensure_entry` が未構築なら必ず `NeedsRebuild` を返す）なので、
        // TLAS へ登録しても未構築参照にはならない。
        /// 統合 BLAS 内の 1 ジオメトリ分のビルド入力＋レコード生成情報。
        /// `Vec` の添字がそのまま `geometry_index` であり、bindless レコードの相対位置になる。
        struct SkinGeomTarget<'a> {
            /// 変形 compute の出力（BLAS の頂点入力）。
            out_buf:      &'a wgpu::Buffer,
            /// 元プリミティブのインデックスバッファ（トポロジは変形で不変）。
            index_bu:     &'a wgpu::Buffer,
            vertex_count: u32,
            index_count:  u32,
            /// このジオメトリのマテリアル（レコード生成に使う。`None` = デフォルト）。
            material_idx: Option<usize>,
            /// bindless メガバッファのオフセットを引くためのメッシュ／プリミティブ番号。
            mesh_idx:     usize,
            prim_idx:     usize,
        }
        struct SkinBuildTarget<'a> {
            blas:  &'a wgpu::Blas,
            /// ジオメトリ順（＝`geometry_index` 順）のビルド入力。
            geoms: Vec<SkinGeomTarget<'a>>,
            /// このフレームに BLAS を構築し直すか（false = ポーズ静止で据え置き）。
            needs_rebuild: bool,
            /// ビルド確定（`mark_built`）に使うキー。
            key:   SkinBlasKey,
            /// TLAS 詰め込みで使う列挙情報（マスク・変換）。
            info:  &'a SkinEntryInfo,
        }
        let skin_targets: Vec<SkinBuildTarget> = skin_accepted.iter()
            .filter_map(|(key, c, needs_rebuild)| {
                let (blas, slots) = self.skin_blas.blas_geometries(key)?;
                let gpu = casters[c.caster_i].1;
                let mut geoms: Vec<SkinGeomTarget> = Vec::with_capacity(slots.len());
                for slot in slots {
                    let prim = gpu.meshes.get(slot.mesh_idx)
                        .and_then(|m| m.primitives.get(slot.prim_idx))?;
                    // 防御チェック: インデックスバッファの BLAS_INPUT 用途（非スキンと同じ流儀）。
                    // ensure_entry でも検証済みだが、ここは wgpu の検証パニックへ直結する最終境界
                    // なので二重に確認する。1 ジオメトリでも欠ければ **エントリごと** 見送る
                    //（ジオメトリを抜くと BLAS のサイズ記述子と食い違い、さらに geometry_index と
                    //  レコードの対応もズレるため、部分ビルドは許されない）。
                    if !prim.index_buffer.usage().contains(wgpu::BufferUsages::BLAS_INPUT) {
                        return None;
                    }
                    // マテリアルは列挙情報（`info.prims`）側が正典。スロットと同じ
                    // (mesh, prim) を引いて対応付ける。
                    let material_idx = c.prims.iter()
                        .find(|p| p.mesh_idx == slot.mesh_idx && p.prim_idx == slot.prim_idx)
                        .and_then(|p| p.material_idx);
                    geoms.push(SkinGeomTarget {
                        out_buf:      &slot.out_buffer,
                        index_bu:     &prim.index_buffer,
                        vertex_count: slot.vertex_count,
                        index_count:  slot.index_count,
                        material_idx,
                        mesh_idx:     slot.mesh_idx,
                        prim_idx:     slot.prim_idx,
                    });
                }
                if geoms.is_empty() { return None; }
                Some(SkinBuildTarget {
                    blas, geoms, needs_rebuild: *needs_rebuild, key: key.clone(), info: c,
                })
            })
            .collect();
        // 実際にビルドへ積んだキー（ビルド後に `mark_built` で署名を確定させる）。
        let skin_built_keys: Vec<SkinBlasKey> = skin_targets.iter()
            .filter(|t| t.needs_rebuild)
            .map(|t| t.key.clone())
            .collect();
        // サイズ記述子はビルド呼び出しまで生存させる必要があるため、エントリごとに確保する
        //（`skin_targets` と同順・同長）。
        let skin_size_descs: Vec<Vec<BlasTriangleGeometrySizeDescriptor>> = skin_targets.iter()
            .map(|t| t.geoms.iter()
                .map(|g| skin_blas_size_desc(g.vertex_count, g.index_count))
                .collect())
            .collect();
        for (t, descs) in skin_targets.iter().zip(skin_size_descs.iter()) {
            // ポーズが動いていないエントリはビルドを積まない（BLAS は前フレームのまま有効）。
            if !t.needs_rebuild { continue; }
            // 1 体（1 メッシュノードインスタンス）の全プリミティブを
            // **1 つの BLAS のジオメトリ配列**として積む（本修正の中核）。
            let geometries: Vec<BlasTriangleGeometry> = t.geoms.iter().zip(descs.iter())
                .map(|(g, size)| BlasTriangleGeometry {
                    size,
                    vertex_buffer:           g.out_buf,
                    first_vertex:            0,
                    // 変形出力は vec4<f32> 配列。BLAS は先頭 12B（Float32x3）を読む。
                    vertex_stride:           SKIN_DEFORM_VERTEX_STRIDE,
                    index_buffer:            Some(g.index_bu),
                    first_index:             Some(0),
                    transform_buffer:        None,
                    transform_buffer_offset: None,
                })
                .collect();
            blas_entries.push(BlasBuildEntry {
                blas:     t.blas,
                geometry: BlasGeometries::TriangleGeometries(geometries),
            });
        }

        // ── 4. TLAS インスタンスを詰め直す（全 None → キャスター順に登録）──
        let mut inst_count: usize = 0;
        // 【診断】RT 屈折の界面再屈折可否の裏取り。0x02（半透明）インスタンス総数と、そのうち
        // 法線ジオメトリ登録済み（BINDLESS_FLAG_GEOM 付与＝界面で実屈折できる）数を数える。
        // GEOM 付与率が低い＝法線メガバッファ枯渇等で glass が直進近似へ縮退している証跡になる。
        let mut translucent_total: usize = 0;
        let mut translucent_geom:  usize = 0;
        // 平均アルベド（Phase RT-GI）を **レコード順**で詰める。
        let mut albedos: Vec<[f32; 4]> = vec![[0.0f32; 4]; MAX_RT_RECORDS as usize];
        // バインドレス（B2）: インスタンステーブルを同順で詰める（対応 GPU のみ確保）。
        let mut records: Vec<BindlessInstanceRecord> = if bindless.is_some() {
            vec![BindlessInstanceRecord::dummy(); MAX_RT_RECORDS as usize]
        } else {
            Vec::new()
        };
        // 【レコード索引規約】マテリアルレコードの通し番号。TLAS インスタンスの
        // custom_data には **そのインスタンスの先頭レコード番号** を入れ、シェーダは
        // `record = instance_custom_data + geometry_index` で引く。
        // 非スキンは 1 インスタンス = 1 ジオメトリなので geometry_index が常に 0 で
        // 従来と同一の 1:1 対応になり、スキン統合 BLAS だけが 2 件以上を消費する。
        let mut record_count: usize = 0;
        // スキン由来のインスタンス数（[PERF] 表示用）。
        let mut skin_inst_count: u32 = 0;
        {
            // disjoint フィールド借用: blas_cache（不変）と tlas_package（可変）。
            // スキン側は上で確定させた skin_targets（&self.skin_blas 由来の参照）を使う。
            let cache = &self.blas_cache;
            let instances = self.tlas_package
                .get_mut_slice(0..MAX_RT_INSTANCES as usize)
                .expect("TLAS スライス範囲は max_instances 以内");
            for slot in instances.iter_mut() { *slot = None; }

            let mut overflow = false;
            for (path, gpu, batch) in casters {
                batch.rt_enumerate(|mesh_idx, prim_idx, material_idx, transform| {
                    // TLAS スロットとレコードの両方に空きが要る（非スキンは 1 件ずつ消費）。
                    if inst_count >= MAX_RT_INSTANCES as usize
                        || record_count >= MAX_RT_RECORDS as usize { overflow = true; return; }
                    let key = BlasKey::new(path, mesh_idx, prim_idx);
                    if let Some(blas) = cache.get(&key) {
                        // custom_data はインスタンス番号（GI が平均アルベドを引くキーでもある）。
                        // mask は alpha_mode 由来（不透明のみ影レイから見える）。
                        let mask = instance_mask_for(gpu.primitive_alpha_mode(material_idx));
                        // 【診断】0x02（半透明）インスタンス総数を数える（GEOM 付与率の分母）。
                        if mask == RT_MASK_NON_OPAQUE { translucent_total += 1; }
                        // 平均アルベド（Phase RT-GI）を同じ index に詰める（custom_data と一致）。
                        // .rgb=平均アルベド（GI/反射のバウンス色。ddgi_probe_update / reflection_rt は .rgb のみ参照）。
                        // .a =RT 色付き影専用。α（base_color_factor.a）と transmission を固定小数でパックして相乗り。
                        //   シェーダ側は透過率 T = (1-α) + α·transmission·albedo.rgb を評価する:
                        //     α=1,tr=1 → 影 = アルベド色（色ガラスは baseColor で透過光を濾過）
                        //     α=1,tr=0 → 影 = 0（透過しない被覆面は光を通さない＝暗い影）
                        //     α=0      → 影なし。
                        // GI/反射は .a を読まないため、色付き影のためだけの .a パックで GI/反射は不変。
                        // ── レコード生成（平均アルベド＋bindless）は非スキンと共通の関数で行う ──
                        // 非スキンは法線メガバッファが実ジオメトリと一致するので GEOM を許可する。
                        let (alb, rec, geom_registered) = build_hit_record(
                            gpu, material_idx, mesh_idx, prim_idx, /* allow_geom_flag */ true,
                        );
                        albedos[record_count] = alb;
                        if !records.is_empty() {
                            records[record_count] = rec;
                            // 【診断】0x02 のうち GEOM 付与済み（界面で実屈折できる）数を数える。
                            if mask == RT_MASK_NON_OPAQUE && geom_registered { translucent_geom += 1; }
                        }
                        // custom_data は **先頭レコード番号**（非スキンは 1 ジオメトリなので
                        // シェーダ側の `custom_data + geometry_index` が従来の 1:1 と一致する）。
                        instances[inst_count] =
                            Some(TlasInstance::new(blas, transform, record_count as u32, mask));
                        inst_count   += 1;
                        record_count += 1;
                    }
                });
                if overflow { break; }
            }

            // ── 4.5. スキンインスタンスを同じスロット列へ続けて詰める（Phase RT-Skin）──
            // `inst_count` を非スキンと共有するため、custom_data（＝albedos / records の添字）
            // の一意性と、MAX_RT_INSTANCES のオーバーフロー判定はそのまま引き継がれる。
            // マスク・平均アルベド・α×transmission パック・bindless レコードの詰め方は
            // 非スキンとまったく同じ（唯一の違いは BINDLESS_FLAG_GEOM を立てないこと）。
            // 【統合 BLAS のレコード配置】1 エントリ = 1 TLAS インスタンスだが、
            // マテリアルはジオメトリ（＝元プリミティブ）ごとに異なる。そこで
            // **ジオメトリ順にレコードを連続配置**し、custom_data には先頭レコード番号を入れる。
            // シェーダは `record = instance_custom_data + geometry_index` で自分のレコードを引く。
            for t in &skin_targets {
                let n_geom = t.geoms.len();
                // TLAS スロット 1 件＋レコード n_geom 件が **まとめて** 入る空きが要る。
                // 途中で切れるとジオメトリとレコードの対応が壊れるため、入らなければ
                // このインスタンスは 1 件も登録しない。
                if inst_count >= MAX_RT_INSTANCES as usize
                    || record_count + n_geom > MAX_RT_RECORDS as usize {
                    overflow = true;
                    break;
                }
                let c    = t.info;
                let gpu  = casters[c.caster_i].1;
                // マスクはエントリ（＝分類）単位で一意。列挙時にマスクごとへ分けてある。
                let mask = c.mask;
                let record_base = record_count;

                for g in &t.geoms {
                    if mask == RT_MASK_NON_OPAQUE { translucent_total += 1; }
                    // 【重要】スキンでは BINDLESS_FLAG_GEOM を **立てない**（allow_geom_flag=false）。
                    //   このビットは「メガバッファの法線でヒット点の界面法線を復元してよい」
                    //   ことを意味するが、メガバッファへ登録されているスキンの法線は
                    //   **バインドポーズのまま**（変形 compute は位置しか書き出さない）で、
                    //   実際の変形後法線とは食い違う。立てると RT 屈折が誤った界面法線で
                    //   再屈折し、ガラス越しのスキンキャラが歪んで見える。
                    //   UV とインデックスは変形で不変なので ELIGIBLE（テクスチャ引き）は有効。
                    let (alb, rec, _) = build_hit_record(
                        gpu, g.material_idx, g.mesh_idx, g.prim_idx, /* allow_geom_flag */ false,
                    );
                    albedos[record_count] = alb;
                    if !records.is_empty() { records[record_count] = rec; }
                    record_count += 1;
                }

                instances[inst_count] =
                    Some(TlasInstance::new(t.blas, c.transform, record_base as u32, mask));
                inst_count += 1;
                skin_inst_count += 1;
            }

            if overflow && !self.warned_overflow {
                self.warned_overflow = true;
                eprintln!(
                    "[SEED RT] 警告: RT 影キャスターが上限（インスタンス {} / マテリアルレコード {}）を\
                     超過しました。超過分は影を落としません\
                     （MAX_RT_INSTANCES / MAX_RT_RECORDS を増やすか対象を減らしてください）",
                    MAX_RT_INSTANCES, MAX_RT_RECORDS
                );
            }
        }

        // 【診断】0x02（半透明）インスタンス総数と GEOM 付与数を、値が変化したときだけ 1 行ログ。
        // 毎フレームではなく TLAS 再構築フレームのうち内容が変わったときのみ（ログ爆発防止）。
        // GEOM 付与率が低いと RT 屈折の界面再屈折が直進近似へ縮退している証跡になる（法線メガバッファ枯渇等）。
        {
            let packed = ((translucent_total as u32) << 16) | (translucent_geom as u32 & 0xffff);
            if self.last_geom_log != packed {
                self.last_geom_log = packed;
                eprintln!(
                    "[SEED BINDLESS] 0x02(半透明) TLAS インスタンス総数={} / うち GEOM 付き={}（RT屈折の界面再屈折可能数）",
                    translucent_total, translucent_geom
                );
            }
        }

        // ── 5. BLAS（新規）と TLAS を 1 回の呼び出しでビルド ──────
        // 平均アルベド storage を（再構築フレームのみ）アップロードする（TLAS と同じ更新周期）。
        queue.write_buffer(&self.albedo_buffer, 0, bytemuck::cast_slice(&albedos));
        // バインドレス（B2）: インスタンステーブルを同じ更新周期でアップロードする
        // （custom_data 順で albedos と一致）。対応 GPU のみ（records 非空）。
        if let Some(b) = bindless {
            if !records.is_empty() {
                b.upload_instance_records(queue, &records[..record_count]);
            }
        }

        encoder.build_acceleration_structures(blas_entries.iter(), std::iter::once(&self.tlas_package));

        // ビルドへ積み終えたスキンエントリのポーズ署名を「構築済み」として確定させる。
        // ここまで来て初めて BLAS の中身が今フレームの署名と一致するため、確定は
        // ビルド記録の後に行う（`blas_entries` は上で drop 済み＝可変借用が取れる）。
        drop(blas_entries);
        if !skin_built_keys.is_empty() {
            self.skin_blas.mark_built(&skin_built_keys);
        }

        self.last_inst_count = inst_count as u32;
        self.last_skin_inst_count = skin_inst_count;
        RtBuildStat {
            built:          true,
            instances:      inst_count as u32,
            skin_instances: skin_inst_count,
            skin_build_ms,
        }
    }
}

// ─── スキンエントリの中間表現（Phase RT-Skin）────────────────
//
// `rt_enumerate_skinned` の列挙結果を、シグネチャ計算・GPU リソース確保・BLAS ビルド・
// TLAS 詰め込みの 4 箇所で使い回すための構造体。列挙を 1 回に抑え、かつ各段階が
// 同じ集合・同じ順序を見ることを保証する（順序がズレると custom_data と albedos/records の
// 対応が壊れる）。

/// 「スキンメッシュノード × インスタンス × インスタンスマスク」1 エントリ分の列挙結果。
///
/// これが **RT スキン BLAS 1 個の単位**である（複数プリミティブ = 複数ジオメトリ）。
struct SkinEntryInfo {
    /// `casters` 内での添字（batch_key / GpuModel / バッチを引くため）。
    caster_i:    usize,
    /// メッシュノード番号（ワールド変換の単位）。
    node_idx:    usize,
    /// バッチ内のインスタンス番号。
    inst_idx:    usize,
    /// TLAS インスタンスマスク（不透明 / 非不透明）。1 BLAS = 1 マスク。
    mask:        u8,
    /// TLAS インスタンス変換（3x4 行優先）。VS の `u_model.model` と同一行列。
    transform:   [f32; 12],
    /// ジョイント行列バッファの LOD 番号。
    lod:         usize,
    /// LOD 内のコンパクト添字（`joint_base = compact_idx * MAX_JOINTS`）。
    compact_idx: u32,
    /// 再生時刻のビット表現（静止シーン判定のシグネチャに含める）。
    anim_bits:   u32,
    /// このエントリを構成するプリミティブ列（BLAS のジオメトリ順＝レコードの並び順）。
    prims:       Vec<RtSkinnedPrimRef>,
}

// ─── ヒットレコードの生成（非スキン／スキン共通）──────────────────
//
/// TLAS ジオメトリ 1 件分の「平均アルベド（色付き影のパック込み）」と
/// bindless インスタンスレコードを組み立てる。
///
/// 非スキン経路とスキン経路で **まったく同じ規則**を使うために関数化してある
/// （唯一の違いは `allow_geom_flag`）。両者で別々に書くと、片方だけパック方式や
/// フラグ条件が変わったときに RT 反射・屈折・色付き影が経路ごとに食い違う。
///
/// - `allow_geom_flag`: `BINDLESS_FLAG_GEOM`（メガバッファ法線で界面法線を復元してよい）を
///   立てることを許すか。スキンは変形後法線をメガバッファへ書いていない（バインドポーズのまま）
///   ため必ず `false` を渡す。
///
/// 返り値: `(平均アルベド[.a=α×transmission パック], レコード, ジオメトリ登録済みか)`。
/// 第 3 要素は診断ログ（GEOM 付与率）専用。
fn build_hit_record(
    gpu:             &GpuModel,
    material_idx:    Option<usize>,
    mesh_idx:        usize,
    prim_idx:        usize,
    allow_geom_flag: bool,
) -> ([f32; 4], BindlessInstanceRecord, bool) {
    // 平均アルベド（Phase RT-GI）。
    // .rgb=平均アルベド（GI/反射のバウンス色。ddgi_probe_update / reflection_rt は .rgb のみ参照）。
    // .a =RT 色付き影専用。α（base_color_factor.a）と transmission を固定小数でパックして相乗り。
    //   シェーダ側は透過率 T = (1-α) + α·transmission·albedo.rgb を評価する:
    //     α=1,tr=1 → 影 = アルベド色（色ガラスは baseColor で透過光を濾過）
    //     α=1,tr=0 → 影 = 0（透過しない被覆面は光を通さない＝暗い影）
    //     α=0      → 影なし。
    // GI/反射は .a を読まないため、色付き影のためだけの .a パックで GI/反射は不変。
    let mut alb = gpu.primitive_avg_albedo(material_idx); // [r,g,b,a]（.a=base_color_factor.a）
    let alpha = alb[3].clamp(0.0, 1.0);
    let trans = gpu.primitive_transmission(material_idx).clamp(0.0, 1.0);
    alb[3] = pack_shadow_alpha_transmission(alpha, trans); // .rgb は生アルベドのまま

    // ── バインドレス（B2）: レコードを組み立てる ──
    // eligible = UV/index 登録済み かつ albedo テクスチャ登録済み（tex≠0）。
    // どちらか欠ければ flags=0 で、ヒットシェーダは平均色（avg_albedo）へ縮退する。
    let tex_index = gpu.primitive_bindless_albedo_tex_index(material_idx);
    let geom = gpu.meshes.get(mesh_idx)
        .and_then(|m| m.primitives.get(prim_idx))
        .map(|gp| (gp.bindless_eligible, gp.bindless_uv_offset,
                   gp.bindless_index_offset, gp.bindless_normal_offset))
        .unwrap_or((false, 0, 0, 0));
    let geom_registered = geom.0; // UV/index/法線をメガバッファへ登録済みか。
    let elig = geom_registered && tex_index != BINDLESS_DUMMY_TEX_INDEX;
    // Mask マテリアル（アルファテスト）は色付き影の第 2 クエリでテクスチャ α を
    // alpha_cutoff と比較して葉の形の影を落とす（B3）。flag を立て cutoff を積む。
    let is_mask = matches!(gpu.primitive_alpha_mode(material_idx), AlphaMode::Mask);
    let mut flags = if elig { BINDLESS_FLAG_ELIGIBLE } else { 0 };
    if is_mask { flags |= BINDLESS_FLAG_MASK; }
    if geom_registered && allow_geom_flag { flags |= BINDLESS_FLAG_GEOM; }

    let rec = BindlessInstanceRecord {
        avg_albedo:        alb, // 先頭 16B は既存 storage と同一（.a=パック済み）
        base_color_factor: gpu.primitive_base_color_factor(material_idx),
        albedo_tex_index:  tex_index,
        uv_offset:         geom.1,
        index_offset:      geom.2,
        flags,
        // Mask のときだけ有効な閾値（Blend では 0.0）。BINDLESS_FLAG_MASK が
        // 立っているインスタンスでのみシェーダが参照する。
        alpha_cutoff:      if is_mask { gpu.primitive_alpha_cutoff(material_idx) } else { 0.0 },
        // 法線オフセットは GEOM を立てない経路では参照されないが、
        // レコードの意味を壊さないよう登録値をそのまま入れておく。
        normal_offset:     geom.3,
        _pad:              [0; 2],
    };
    (alb, rec, geom_registered)
}

/// シグネチャ内でスキン部の開始／各エントリの区切りを示すマーカーバイト。
/// 非スキン部の区切り（0xff）と同じ役割で、可変長要素の連結が偶然一致するのを防ぐ。
const SKIN_SIG_MARKER: u8 = 0xfe;

/// `anim_time_overrides` が `None`（Animator 非駆動＝静止）のときにシグネチャへ混ぜる番兵。
/// f32 の NaN ビットパターンなので、実際の再生時刻（有限値）と衝突しない。
const ANIM_TIME_NONE_BITS: u32 = 0x7fc0_0000;

// ─── BLAS 構築ヘルパー ───────────────────────────────────────

/// プリミティブ 1 個分の BLAS サイズ記述子を作る。
///
/// 位置フォーマットは Float32x3（EXPERIMENTAL_RAY_TRACING_ACCELERATION_STRUCTURE の
/// 標準対応形式）、インデックスは Uint32。
///
/// 【OPAQUE フラグについて】全ジオメトリを OPAQUE で作る（＝ヒットが candidate 段階を経ずに
/// 即確定する）。非 OPAQUE にすると committed intersection を得るために WGSL 側で
/// candidate ループ＋`rayQueryConfirmIntersection` を書く必要がある。
/// naga 25 は `rayQueryGetCandidateIntersection` / `rayQueryConfirmIntersection` を
/// サポートしているため WGSL 構文上は書けるが、confirm の判断（アルファテスト）に必要な
/// 「ヒット三角形のマテリアル・UV・テクスチャ」を引くには bindless が必要で現状は非現実的。
/// 代わりに半透明・アルファテストのプリミティブは TLAS インスタンスマスク
/// （RT_MASK_NON_OPAQUE）で影レイから除外している（上記マスク定数のコメント参照）。
fn blas_size_desc(prim: &GpuPrimitive) -> BlasTriangleGeometrySizeDescriptor {
    BlasTriangleGeometrySizeDescriptor {
        vertex_format: wgpu::VertexFormat::Float32x3,
        vertex_count:  prim.vertex_count,
        index_format:  Some(wgpu::IndexFormat::Uint32),
        index_count:   Some(prim.index_count),
        flags:         AccelerationStructureGeometryFlags::OPAQUE,
    }
}

/// プリミティブ 1 個分の BLAS を生成する（ビルドは呼び出し側の
/// build_acceleration_structures で行う）。
fn create_blas_for_prim(device: &wgpu::Device, prim: &GpuPrimitive) -> wgpu::Blas {
    let size = blas_size_desc(prim);
    device.create_blas(
        &CreateBlasDescriptor {
            label:       Some("RT Shadow BLAS"),
            // 静的シーンジオメトリ向けに高速トレース優先（構築は初回のみ）。
            flags:       AccelerationStructureFlags::PREFER_FAST_TRACE,
            update_mode: AccelerationStructureUpdateMode::Build,
        },
        BlasGeometrySizeDescriptors::Triangles { descriptors: vec![size] },
    )
}

// ─── シェーダバリアントの静的検証（naga parse + validate）────────
//
// RT バリアント（mesh_rt / skinned_mesh_rt）は RT 対応 GPU の実行時にのみパイプラインが
// 構築されるため、cargo build だけでは WGSL が検証されない。ここで全 4 バリアントを
// naga で parse + validate（RAY_QUERY ケイパビリティ付き）し、rayQuery 構文や
// acceleration_structure 宣言・共有フラグメントの整合性を CI/ローカルビルドで担保する。
#[cfg(test)]
mod tests {
    use super::*;

    /// 【レコード索引規約】全 WGSL が `instance_custom_data` を **単体で使っていない**こと。
    ///
    /// Phase RT-Skin の BLAS 統合以降、TLAS の custom_data は「そのインスタンスの
    /// 先頭レコード番号」であり、実レコードは `custom_data + geometry_index` で引く。
    /// 非スキンは geometry_index が常に 0 なので従来と同じ値になるが、スキン統合 BLAS では
    /// 加算を忘れると **1 体の全プリミティブが先頭プリミティブのマテリアルで着色される**
    /// （影の色・反射のテクスチャ・アルファテストが全部おかしくなる）。
    /// この間違いはコンパイルでも実行時エラーでも検出できないので、ソースレベルで縛る。
    #[test]
    fn wgsl_reads_record_index_with_geometry_index() {
        /// 検査対象（`instance_custom_data` を読む可能性のある全 WGSL）。
        const SOURCES: &[(&str, &str)] = &[
            ("ddgi_probe_update.wgsl",      include_str!("shaders/ddgi_probe_update.wgsl")),
            ("reflection_rt.wgsl",          include_str!("shaders/reflection_rt.wgsl")),
            ("refract_rt.wgsl",             include_str!("shaders/refract_rt.wgsl")),
            ("rt_shadow_on.wgsl",           include_str!("shaders/rt_shadow_on.wgsl")),
            ("rt_shadow_tint_avg.wgsl",     include_str!("shaders/rt_shadow_tint_avg.wgsl")),
            ("rt_shadow_tint_bindless.wgsl",include_str!("shaders/rt_shadow_tint_bindless.wgsl")),
            ("water_reflection_rt.wgsl",    include_str!("shaders/water_reflection_rt.wgsl")),
            ("reflection_rt_hit_on.wgsl",   include_str!("shaders/reflection_rt_hit_on.wgsl")),
            ("reflection_rt_hit_off.wgsl",  include_str!("shaders/reflection_rt_hit_off.wgsl")),
            ("water_reflection_hit_on.wgsl",include_str!("shaders/water_reflection_hit_on.wgsl")),
            ("water_reflection_hit_off.wgsl",include_str!("shaders/water_reflection_hit_off.wgsl")),
        ];
        /// 読み出し箇所の直後に必ず続くべき文字列（geometry_index の加算）。
        const REQUIRED_SUFFIX: &str = " + hit.geometry_index";
        const FIELD: &str = ".instance_custom_data";

        for (name, src) in SOURCES {
            for (lineno, line) in src.lines().enumerate() {
                let code = line.trim_start();
                // コメント行は規約の説明を含むので対象外。
                if code.starts_with("//") { continue; }
                let Some(pos) = line.find(FIELD) else { continue };
                let rest = &line[pos + FIELD.len()..];
                assert!(
                    rest.starts_with(REQUIRED_SUFFIX),
                    "{name}:{} が instance_custom_data を単体で読んでいます。\
                     レコードは `instance_custom_data + hit.geometry_index` で引くこと\
                     （スキン統合 BLAS では 1 インスタンスが複数レコードを持つ）: {line}",
                    lineno + 1
                );
            }
        }
    }

    /// レコード容量が TLAS インスタンス容量以上であること。
    ///
    /// スキン統合 BLAS は 1 TLAS インスタンスで複数レコードを消費するため、
    /// レコード側が小さいと先に枯渇して「体の一部だけレコードが無い」状態を作りうる。
    /// 実装は「まとめて入らなければ登録しない」で防いでいるが、容量関係そのものを固定する。
    #[test]
    fn record_capacity_covers_instances() {
        assert!(
            MAX_RT_RECORDS >= MAX_RT_INSTANCES,
            "MAX_RT_RECORDS({MAX_RT_RECORDS}) は MAX_RT_INSTANCES({MAX_RT_INSTANCES}) 以上であること"
        );
    }

    /// WGSL の `RT_SHADOW_CULL_MASK` と Rust の `RT_MASK_OPAQUE` が同じ値であることを検証する。
    ///
    /// この 2 つがズレると「影が一切出ない（AND=0）」「Blend が再び影を落とす（0xFF に戻る）」
    /// といった無言の破綻になり、コンパイルでも実行時エラーでも検出できない。
    /// WGSL ソースを include_str! して定数宣言を直接パースし、値の一致を保証する。
    #[test]
    fn wgsl_cull_mask_matches_rust_mask() {
        let src = include_str!("shaders/rt_shadow_on.wgsl");

        // `const RT_SHADOW_CULL_MASK: u32 = 0x01u;` の右辺リテラルを取り出す。
        let decl = src
            .lines()
            .map(str::trim)
            .find(|l| l.starts_with("const RT_SHADOW_CULL_MASK"))
            .expect("rt_shadow_on.wgsl に const RT_SHADOW_CULL_MASK の宣言が見つかりません");
        let rhs = decl
            .split('=')
            .nth(1)
            .expect("RT_SHADOW_CULL_MASK の宣言に '=' がありません")
            .trim()
            .trim_end_matches(';')
            .trim()
            .trim_end_matches('u'); // WGSL の u32 サフィックス

        // 16 進（0x..）／10 進の双方を受け付ける。
        let value = if let Some(hex) = rhs.strip_prefix("0x").or_else(|| rhs.strip_prefix("0X")) {
            u8::from_str_radix(hex, 16).expect("RT_SHADOW_CULL_MASK が u8 の 16 進として解釈できません")
        } else {
            rhs.parse::<u8>().expect("RT_SHADOW_CULL_MASK が u8 として解釈できません")
        };

        assert_eq!(
            value, RT_MASK_OPAQUE,
            "WGSL の RT_SHADOW_CULL_MASK({value:#04x}) と Rust の RT_MASK_OPAQUE({RT_MASK_OPAQUE:#04x}) が\
             一致していません。影レイのカリングマスクと TLAS インスタンスマスクは必ず対応させること"
        );
        // 不透明ビットと非不透明ビットが重なっていない（AND=0）ことも保証する。
        assert_eq!(
            RT_MASK_OPAQUE & RT_MASK_NON_OPAQUE, 0,
            "RT_MASK_OPAQUE と RT_MASK_NON_OPAQUE のビットが重複しています（マスク分離が機能しません）"
        );

        // 色付き影（Phase RT-Translucency）: 第 2 クエリの cull_mask（RT_TRANSLUCENT_CULL_MASK）が
        // Rust の RT_MASK_NON_OPAQUE と一致すること。ズレると半透明レイヤーの透過色を拾えない。
        let tdecl = src
            .lines()
            .map(str::trim)
            .find(|l| l.starts_with("const RT_TRANSLUCENT_CULL_MASK"))
            .expect("rt_shadow_on.wgsl に const RT_TRANSLUCENT_CULL_MASK の宣言が見つかりません");
        let trhs = tdecl
            .split('=').nth(1).expect("RT_TRANSLUCENT_CULL_MASK の宣言に '=' がありません")
            .trim().trim_end_matches(';').trim().trim_end_matches('u');
        let tvalue = if let Some(hex) = trhs.strip_prefix("0x").or_else(|| trhs.strip_prefix("0X")) {
            u8::from_str_radix(hex, 16).expect("RT_TRANSLUCENT_CULL_MASK が u8 の 16 進として解釈できません")
        } else {
            trhs.parse::<u8>().expect("RT_TRANSLUCENT_CULL_MASK が u8 として解釈できません")
        };
        assert_eq!(
            tvalue, RT_MASK_NON_OPAQUE,
            "WGSL の RT_TRANSLUCENT_CULL_MASK({tvalue:#04x}) と Rust の RT_MASK_NON_OPAQUE({RT_MASK_NON_OPAQUE:#04x}) が一致していません"
        );
    }

    /// WGSL ソースから `const NAME: TYPE = VALUE;` の右辺（数値リテラル）を文字列で取り出す。
    /// 型サフィックス（u32 の `u` / f32 の `f`）は落とす。定数の値をシェーダ側の 1 箇所に保ち、
    /// Rust 側テストからは「読み取って検証するだけ」にするためのヘルパー。
    fn wgsl_const_literal(src: &str, name: &str) -> String {
        let decl = src
            .lines()
            .map(str::trim)
            .find(|l| l.starts_with(&format!("const {name}")))
            .unwrap_or_else(|| panic!("WGSL に const {name} の宣言が見つかりません"));
        decl.split('=')
            .nth(1)
            .unwrap_or_else(|| panic!("const {name} の宣言に '=' がありません"))
            .trim()
            .trim_end_matches(';')
            .trim()
            .trim_end_matches(['u', 'f'])
            .to_string()
    }

    /// ソフト影の定数群（クランプ上限・サンプル数・適応の傾き・ターミネータ帯・各種バイアス）の
    /// 妥当性を固定する。
    ///
    /// これらは 3 つの退行の再発防止線である:
    ///
    /// 1. 「ライト近傍の面がディザ状のまだらノイズになり真っ黒に潰れる」
    ///   - cone_radius に上限が無いと、ライトに近い面ほど円錐が発散してノイズと偽遮蔽を生む。
    ///   - サンプル数が固定 4 本だと、広い円錐で遮蔽率が 5 段階に量子化されディザになる。
    ///   3 定数（上限 cone_radius / 最大サンプル数 / 1 サンプルが受け持つ幅）が整合していないと
    ///   「上限まで広げてもサンプルが増えない」等の噛み合わせ崩れが無言で起きるため、ここで縛る。
    ///
    /// 2. 「三角形の面境界に沿った硬い黒帯（ファセット状の影）」
    ///   影の減衰判定にフラットな幾何法線 Ng を使うと面境界で判定が飛ぶ。現実装は
    ///   補間法線 Nv による smoothstep ランプ（RT_SHADOW_TERMINATOR_BAND_COS）で滑らかに落とし、
    ///   Ng はレイ原点の最小クリアランス（RT_SHADOW_GEO_CLEARANCE）だけに使う。
    ///   これらの定数が壊れる（0 になる・過大になる）と黒帯か光漏れのどちらかが再発するため縛る。
    ///
    /// 3. 「一枚布（カーテン）の、光が当たっていないはずの面に点描状の光漏れが出る」
    ///   地平線より下を向くサンプル方向を Ng 方向へ「起こす（リフトする）」実装が原因だった。
    ///   薄い面の裏側へ抜けるべきレイが手前側へ折り返され、遮蔽なし＝照射と誤判定される。
    ///   現実装はリフトせず「地平線より下は 0（遮蔽）として母数に数える」。
    ///   リフト機構が復活していないことをソースレベルで縛る（下の assert 参照）。
    #[test]
    fn wgsl_soft_shadow_constants_are_consistent() {
        let rt_on    = include_str!("shaders/rt_shadow_on.wgsl");
        // RT_SHADOW_MAX_CONE_RADIUS は light_common.wgsl へ移設（Phase RT-Shadow-Denoise。
        // インライン影とマスク生成が共有するため）。ここも移設先から読む。
        let light_c  = include_str!("shaders/light_common.wgsl");

        let max_cone: f32 = wgsl_const_literal(light_c, "RT_SHADOW_MAX_CONE_RADIUS")
            .parse()
            .expect("RT_SHADOW_MAX_CONE_RADIUS が f32 として解釈できません");
        let samples_min: u32 = wgsl_const_literal(rt_on, "RT_SHADOW_SAMPLES_MIN")
            .parse()
            .expect("RT_SHADOW_SAMPLES_MIN が u32 として解釈できません");
        let samples_max: u32 = wgsl_const_literal(rt_on, "RT_SHADOW_SAMPLES_MAX")
            .parse()
            .expect("RT_SHADOW_SAMPLES_MAX が u32 として解釈できません");
        let per_sample: f32 = wgsl_const_literal(rt_on, "RT_SHADOW_CONE_RADIUS_PER_SAMPLE")
            .parse()
            .expect("RT_SHADOW_CONE_RADIUS_PER_SAMPLE が f32 として解釈できません");
        let band: f32 = wgsl_const_literal(rt_on, "RT_SHADOW_TERMINATOR_BAND_COS")
            .parse()
            .expect("RT_SHADOW_TERMINATOR_BAND_COS が f32 として解釈できません");
        let normal_bias: f32 = wgsl_const_literal(rt_on, "RT_SHADOW_NORMAL_BIAS")
            .parse()
            .expect("RT_SHADOW_NORMAL_BIAS が f32 として解釈できません");
        let geo_clearance: f32 = wgsl_const_literal(rt_on, "RT_SHADOW_GEO_CLEARANCE")
            .parse()
            .expect("RT_SHADOW_GEO_CLEARANCE が f32 として解釈できません");
        let tmin: f32 = wgsl_const_literal(rt_on, "RT_SHADOW_TMIN")
            .parse()
            .expect("RT_SHADOW_TMIN が f32 として解釈できません");

        // 見込み半径の上限は「有限かつ実用的な広さ」に収まっていること。
        // tan(半角) = 1.0 は見込み半角 45°＝半球の大半を覆う。これを超えるとペナンブラの
        // 概念が破綻し、サンプル数を増やしてもノイズが残るだけになる。
        assert!(
            max_cone > 0.0 && max_cone <= 1.0,
            "RT_SHADOW_MAX_CONE_RADIUS({max_cone}) は (0, 1] の範囲であること（発散防止）"
        );

        // サンプル数の上下限。1 本未満は無意味、上限は 1 ピクセルあたりの最悪レイ本数を決める。
        assert!(
            samples_min >= 1 && samples_min <= samples_max,
            "RT_SHADOW_SAMPLES_MIN({samples_min}) は 1 以上かつ MAX({samples_max}) 以下であること"
        );
        assert!(
            samples_max <= 32,
            "RT_SHADOW_SAMPLES_MAX({samples_max}) が過大です（1 ライトあたりのレイ本数がコストに直結する）"
        );

        // 適応サンプルの傾きは「上限 cone_radius でちょうど最大サンプル数に到達する」よう決める。
        // ズレると、上限まで広がった円錐でも本数が増えない（＝ディザが残る）／
        // 小さな円錐で即座に最大本数へ張り付く（＝無駄なコスト）といった破綻になる。
        let expected_per_sample = max_cone / samples_max as f32;
        assert!(
            (per_sample - expected_per_sample).abs() <= f32::EPSILON * 8.0,
            "RT_SHADOW_CONE_RADIUS_PER_SAMPLE({per_sample}) は \
             RT_SHADOW_MAX_CONE_RADIUS({max_cone}) / RT_SHADOW_SAMPLES_MAX({samples_max}) \
             = {expected_per_sample} と一致させること"
        );

        // ターミネータランプの帯幅は「正、かつ狭い」こと。
        //   0 以下 → 硬いカットオフに逆戻り（面境界での不連続＝黒帯の再発）。
        //   広すぎ → 正当に照らされている領域まで影の係数で減光し、全体が暗く沈む。
        //   上限 0.5（入射角 60°）は「BRDF の ndl が半分以上ある領域を影で減光しない」線。
        assert!(
            band > 0.0 && band <= 0.5,
            "RT_SHADOW_TERMINATOR_BAND_COS({band}) は (0, 0.5] の範囲であること\
             （0 以下は硬いカットオフ＝ファセット状の黒帯の再発、広すぎは全体の沈み込み）"
        );
        // 地平線「リフト」機構が復活していないこと（光漏れの再発防止線）。
        // サンプル方向を幾何法線 Ng 側へ起こすと、厚みのない面（カーテン）の裏にライトがある
        // とき、面を貫くはずのレイが手前へ折り返されて空へ逃げ、「照らされている」と誤判定される
        // （＝点描状の光漏れ）。地平線より下のサンプルは起こさず 0（遮蔽）として数えること。
        assert!(
            !rt_on.contains("RT_SHADOW_GEO_HORIZON_MIN_COS") && !rt_on.contains("lift_above_horizon"),
            "rt_shadow_on.wgsl に地平線リフト機構が復活しています。\
             サンプル方向を Ng で起こすと薄い面で光漏れが再発します。\
             地平線より下（dot(nv,dir) <= 0）のサンプルは 0（遮蔽）として母数に数えること"
        );

        // バイアスの大小関係。
        //   geo_clearance > tmin : 平面からのクリアランスが tmin の打ち切りに埋もれない。
        //   geo_clearance < normal_bias : 主防御はあくまで補間法線方向のオフセット。
        //     幾何法線方向は「必ず平面から浮いている」ことを保証する最小量に留め、
        //     大きくしすぎると接地点の影（コンタクトシャドウ）が痩せて浮いて見える。
        assert!(
            geo_clearance > tmin && geo_clearance < normal_bias,
            "RT_SHADOW_GEO_CLEARANCE({geo_clearance}) は RT_SHADOW_TMIN({tmin}) より大きく、\
             RT_SHADOW_NORMAL_BIAS({normal_bias}) より小さいこと"
        );
        // レイ原点の総オフセットが「定数の和」で静的に抑えられていること（光漏れの再発防止線）。
        // スロープスケール（NORMAL_BIAS を dot(nv,l) で除算してグレージングで押し出しを増やす機構）が
        // 復活すると、下限 0.1 で最大 0.2 ワールド単位（Sponza=20cm）まで膨らみ、薄い布のヒダ間隔
        // （数 cm）を越えてレイ原点が隣のヒダを突き抜け、遮蔽が外れてリム状の光漏れが再発する。
        // よって (a) スロープ下限定数が復活していないこと、(b) レイ原点式に除算が現れないことを
        // ソースレベルで縛る。総オフセットは NORMAL_BIAS + GEO_CLEARANCE の固定和に限定される。
        assert!(
            !rt_on.contains("RT_SHADOW_SLOPE_MIN_COS"),
            "rt_shadow_on.wgsl にスロープスケールの下限定数 RT_SHADOW_SLOPE_MIN_COS が復活しています。             グレージングでレイ原点オフセットが最大 20cm まで膨らみ薄い布で光漏れが再発します。             レイ原点は固定量（NORMAL_BIAS + GEO_CLEARANCE）だけで押し出すこと"
        );
        // レイ原点式（`let o = origin ...;`）にスロープスケール由来の除算が無いこと。
        // 原点計算のブロック（`let o = origin` から次の ';' まで）を抽出し、除算 '/' を含まないと縛る。
        let o_expr = {
            let start = rt_on
                .find("let o = origin")
                .expect("rt_shadow_on.wgsl にレイ原点式 `let o = origin` が見つかりません");
            let rel_end = rt_on[start..]
                .find(';')
                .expect("レイ原点式が ';' で終わっていません");
            &rt_on[start..start + rel_end]
        };
        assert!(
            !o_expr.contains('/'),
            "レイ原点式に除算が含まれています（スロープスケール復活の疑い）: {o_expr:?}。             オフセットは NORMAL_BIAS と GEO_CLEARANCE の固定量の和だけにすること"
        );

        // 色付き影の貫通枚数定数の分離（CENTER=4 / SAMPLE=2）。半影品質とコストの綱引きの前提。
        // ソフト影はサンプルごとに tint を評価する（半影に色の減衰を付ける）ため、最悪レイ数を
        // 有界に保つ目的で SAMPLE 経路の貫通枚数を CENTER より小さくする。ここが崩れると
        //   ・SAMPLE >= CENTER: ソフト影の最悪コスト（samples×hits）が跳ね上がる
        //   ・どちらか 0     : 貫通ループが回らず色付き影が無効化される
        // といった噛み合わせ崩れが無言で起きるため縛る。ループの静的上限は必ず CENTER 側を使う。
        let hits_center: u32 = wgsl_const_literal(rt_on, "RT_TRANSLUCENT_MAX_HITS_CENTER")
            .parse()
            .expect("RT_TRANSLUCENT_MAX_HITS_CENTER が u32 として解釈できません");
        let hits_sample: u32 = wgsl_const_literal(rt_on, "RT_TRANSLUCENT_MAX_HITS_SAMPLE")
            .parse()
            .expect("RT_TRANSLUCENT_MAX_HITS_SAMPLE が u32 として解釈できません");
        assert_eq!(
            hits_center, 4,
            "RT_TRANSLUCENT_MAX_HITS_CENTER({hits_center}) は 4（ハード影の中心 1 本用）であること"
        );
        assert_eq!(
            hits_sample, 2,
            "RT_TRANSLUCENT_MAX_HITS_SAMPLE({hits_sample}) は 2（ソフト影のサンプルごと・コスト有界化）であること"
        );
        assert!(
            hits_sample >= 1 && hits_sample < hits_center,
            "RT_TRANSLUCENT_MAX_HITS_SAMPLE({hits_sample}) は 1 以上かつ CENTER({hits_center}) 未満であること\
             （サンプルごと評価の最悪コストを中心 1 本用より小さく抑える前提）"
        );
        // 貫通ループの静的上限は両者の大きい方（CENTER）で縛られていること
        //（SAMPLE 経路は早期 break で締める）。上限を SAMPLE 側にすると CENTER 経路が途中で切れる。
        // B3 で rt_trace_translucent_tint はコア（rt_shadow_on.wgsl）から tint バリアント 2 本へ分離した
        // （平均アルベド版／バインドレス版）。両方でループ静的上限が CENTER で縛られていること。
        let tint_avg = include_str!("shaders/rt_shadow_tint_avg.wgsl");
        let tint_bl  = include_str!("shaders/rt_shadow_tint_bindless.wgsl");
        assert!(
            tint_avg.contains("i < RT_TRANSLUCENT_MAX_HITS_CENTER"),
            "rt_shadow_tint_avg.wgsl の rt_trace_translucent_tint のループ静的上限は\
             RT_TRANSLUCENT_MAX_HITS_CENTER で縛ること（両経路の最大枚数を包含する静的上限）"
        );
        assert!(
            tint_bl.contains("i < RT_TRANSLUCENT_MAX_HITS_CENTER"),
            "rt_shadow_tint_bindless.wgsl の rt_trace_translucent_tint のループ静的上限は\
             RT_TRANSLUCENT_MAX_HITS_CENTER で縛ること（両経路の最大枚数を包含する静的上限）"
        );
    }

    /// alpha_mode → TLAS インスタンスマスクの割り当て（不透明のみ影を落とす）。
    #[test]
    fn only_opaque_is_shadow_occluder() {
        assert_eq!(instance_mask_for(AlphaMode::Opaque), RT_MASK_OPAQUE);
        assert_eq!(instance_mask_for(AlphaMode::Blend),  RT_MASK_NON_OPAQUE);
        assert_eq!(instance_mask_for(AlphaMode::Mask),   RT_MASK_NON_OPAQUE);
        // 影レイ（cull_mask = RT_MASK_OPAQUE）から見えるのは不透明だけ。
        assert_ne!(instance_mask_for(AlphaMode::Opaque) & RT_MASK_OPAQUE, 0);
        assert_eq!(instance_mask_for(AlphaMode::Blend)  & RT_MASK_OPAQUE, 0);
        assert_eq!(instance_mask_for(AlphaMode::Mask)   & RT_MASK_OPAQUE, 0);
    }

    /// 色付き影の α×transmission パッキングの往復と、透過率 T の式の真理値表を固定する。
    ///
    /// 検証する不変条件:
    ///  1. WGSL の SHADOW_PACK_QUANT / SHADOW_PACK_RADIX が Rust 定数と一致（デコードが対になる根拠）。
    ///  2. 往復: Rust パック → WGSL 相当デコードで α/transmission を量子化精度（1/255）内に復元できる。
    ///     かつパック値は f32 の整数完全表現域（≤65535）に収まる（往復で桁落ちしない根拠）。
    ///  3. 透過率 T = (1-α) + α·transmission·albedo の真理値表:
    ///     α=1,tr=1 → albedo（色ガラス） / α=1,tr=0 → 0（暗い影） / α=0 → 1（影なし） / 中間値。
    #[test]
    fn shadow_pack_roundtrip_and_transmittance() {
        let rt_on = include_str!("shaders/rt_shadow_on.wgsl");

        // ── 1. WGSL 定数と Rust 定数の一致（ズレるとデコードが破綻し色付き影が壊れる）──
        let wq: f32 = wgsl_const_literal(rt_on, "SHADOW_PACK_QUANT")
            .parse().expect("SHADOW_PACK_QUANT が f32 として解釈できません");
        let wr: f32 = wgsl_const_literal(rt_on, "SHADOW_PACK_RADIX")
            .parse().expect("SHADOW_PACK_RADIX が f32 として解釈できません");
        assert_eq!(wq, SHADOW_PACK_QUANT,
            "WGSL の SHADOW_PACK_QUANT({wq}) と Rust の {SHADOW_PACK_QUANT} が一致していません");
        assert_eq!(wr, SHADOW_PACK_RADIX,
            "WGSL の SHADOW_PACK_RADIX({wr}) と Rust の {SHADOW_PACK_RADIX} が一致していません");

        // WGSL の rt_trace_translucent_tint と同じ算術でデコードする（floor(.a/RADIX) / 残余）。
        let unpack = |packed: f32| -> (f32, f32) {
            let a_q = (packed / wr).floor();
            let t_q = packed - a_q * wr;
            ((a_q / wq).clamp(0.0, 1.0), (t_q / wq).clamp(0.0, 1.0))
        };
        // WGSL の layer_t = (1-α) + α·tr·albedo と同じ式。
        let transmittance = |albedo: [f32; 3], a: f32, tr: f32| -> [f32; 3] {
            [
                (1.0 - a) + a * tr * albedo[0],
                (1.0 - a) + a * tr * albedo[1],
                (1.0 - a) + a * tr * albedo[2],
            ]
        };
        let approx3 = |got: [f32; 3], want: [f32; 3]| {
            for k in 0..3 {
                assert!((got[k] - want[k]).abs() <= 1e-5,
                    "T の成分[{k}] が想定と一致しません: got={got:?} want={want:?}");
            }
        };

        // ── 2. 往復（量子化精度内で復元・整数域内）──
        let tol = 1.0 / SHADOW_PACK_QUANT + 1e-6; // 量子化 1 段ぶんの許容
        for &alpha in &[0.0f32, 0.2, 0.5, 0.6, 0.999, 1.0] {
            for &tr in &[0.0f32, 0.1, 0.5, 0.75, 1.0] {
                let packed = pack_shadow_alpha_transmission(alpha, tr);
                assert!(
                    packed >= 0.0 && packed <= 65535.0 && packed.fract() == 0.0,
                    "パック値 {packed} が f32 整数域（0..=65535）外です（往復桁落ちの恐れ）"
                );
                let (da, dt) = unpack(packed);
                assert!((da - alpha).abs() <= tol, "α の往復誤差過大: in={alpha} out={da}");
                assert!((dt - tr).abs() <= tol, "transmission の往復誤差過大: in={tr} out={dt}");
            }
        }

        // ── 3. 透過率 T の真理値表（albedo=赤 [1,0,0]。色ガラスの代表）──
        let red = [1.0f32, 0.0, 0.0];
        // 式そのもの（連続値）。
        approx3(transmittance(red, 1.0, 1.0), red);              // α=1,tr=1 → アルベド色（色ガラス）
        approx3(transmittance(red, 1.0, 0.0), [0.0, 0.0, 0.0]);  // α=1,tr=0 → 0（暗い影・挙動変更点）
        approx3(transmittance(red, 0.0, 0.5), [1.0, 1.0, 1.0]);  // α=0 → 1（影なし・tr 無関係）
        approx3(transmittance(red, 0.5, 1.0), [1.0, 0.5, 0.5]);  // 中間: 0.5 + 0.5·red
        approx3(transmittance(red, 1.0, 0.5), [0.5, 0.0, 0.0]);  // 中間: 0.5·red

        // 実運用パス（pack → decode → T）の end-to-end 真理値表（量子化後も端点が保たれる）。
        for &(alpha, tr, want) in &[
            (1.0f32, 1.0f32, red),
            (1.0,    0.0,    [0.0, 0.0, 0.0]),
            (0.0,    0.5,    [1.0, 1.0, 1.0]),
        ] {
            let (a, t) = unpack(pack_shadow_alpha_transmission(alpha, tr));
            approx3(transmittance(red, a, t), want);
        }
    }

    /// mesh / skinned × RT オン/オフ の 4 バリアントを結合し、naga で parse + validate する。
    /// 連結順は pipelines/*.toml の shader_sources と一致させること。
    #[test]
    fn rt_shader_variants_parse_and_validate() {
        // クラスタ共有定義（定数・構造体・索引関数）。shader_common.wgsl の group 4
        // binding 7〜9 が ClusterCell / ClusterParams を参照するため、**必ず先に**連結する
        // （pipelines/*.toml の shader_sources と同じ順序）。
        let cluster  = include_str!("shaders/cluster_common.wgsl");
        let common   = include_str!("shaders/shader_common.wgsl");
        let pbr_c    = include_str!("shaders/pbr_common.wgsl");
        let ddgi_c   = include_str!("shaders/ddgi_common.wgsl");
        let light_c  = include_str!("shaders/light_common.wgsl");
        let shadow   = include_str!("shaders/shadow.wgsl");
        let rt_on    = include_str!("shaders/rt_shadow_on.wgsl");
        let rt_off   = include_str!("shaders/rt_shadow_off.wgsl");
        // 色付き影の透過色 tint（B3）: rt_shadow_on コアが呼ぶ rt_trace_translucent_tint を供給。
        // rt_on を含む連結には必ず tint バリアントを 1 本並べること（未定義参照で validate が落ちる）。
        let tint_avg = include_str!("shaders/rt_shadow_tint_avg.wgsl");
        let static_v = include_str!("shaders/shader_static_vertex.wgsl");
        let skin_v   = include_str!("shaders/shader_skinned_vertex.wgsl");
        // PBR シェーディングの 3 段分割（Surface / マテリアル採取 / ライト評価）。
        let surf     = include_str!("shaders/surface.wgsl");
        let gather   = include_str!("shaders/surface_gather.wgsl");
        // シェーディング契約 v1（型・標準ライブラリ）＋ 既定ディスパッチ（shade_surface）。
        let sc       = include_str!("shaders/shading_contract.wgsl");
        let sd       = include_str!("shaders/shading_dispatch.wgsl");
        let light_ev = include_str!("shaders/lighting_eval.wgsl");
        let frag     = include_str!("shaders/shader_fragment.wgsl");

        // WBOIT（半透明）バリアントも同じライト評価を共有するため併せて検証する
        // （transparency.rs が同じ連結でパイプラインを構築している）。
        // RT-Translucency: WBOIT は屈折ヘルパ refract_common.wgsl（group4 binding15/16・glass_composite）と、
        // 背景取得の SS 実装 refract_ss.wgsl（glass_composite が呼ぶ refract_sample_bg を供給）を含む。
        // 本物の RT 屈折（refract_rt.wgsl）バリアントは transparency.rs 側の naga テストが検証する。
        let wboit      = include_str!("shaders/shader_wboit.wgsl");
        let refract    = include_str!("shaders/refract_common.wgsl");
        let refract_ss = include_str!("shaders/refract_ss.wgsl");

        let variants: [(&str, Vec<&str>); 6] = [
            ("mesh_rt",         vec![cluster, pbr_c, common, ddgi_c, light_c, shadow, rt_on, tint_avg,  static_v, surf, gather, sc, sd, light_ev, frag]),
            ("skinned_mesh_rt", vec![cluster, pbr_c, common, ddgi_c, light_c, shadow, rt_on, tint_avg,  skin_v,   surf, gather, sc, sd, light_ev, frag]),
            ("mesh",            vec![cluster, pbr_c, common, ddgi_c, light_c, shadow, rt_off, static_v, surf, gather, sc, sd, light_ev, frag]),
            ("skinned_mesh",    vec![cluster, pbr_c, common, ddgi_c, light_c, shadow, rt_off, skin_v,   surf, gather, sc, sd, light_ev, frag]),
            ("wboit_mesh",      vec![cluster, pbr_c, common, ddgi_c, light_c, shadow, rt_off, static_v, surf, gather, sc, sd, light_ev, frag, refract, refract_ss, wboit]),
            ("wboit_skinned",   vec![cluster, pbr_c, common, ddgi_c, light_c, shadow, rt_off, skin_v,   surf, gather, sc, sd, light_ev, frag, refract, refract_ss, wboit]),
        ];

        for (name, parts) in variants {
            let src = parts.join("\n");
            let module = naga::front::wgsl::parse_str(&src)
                .unwrap_or_else(|e| panic!("[{name}] WGSL parse 失敗: {e:?}"));
            let mut validator = naga::valid::Validator::new(
                naga::valid::ValidationFlags::all(),
                // 加速構造/レイクエリを使う RT バリアントの検証に RAY_QUERY が必須。
                naga::valid::Capabilities::RAY_QUERY,
            );
            validator
                .validate(&module)
                .unwrap_or_else(|e| panic!("[{name}] WGSL validate 失敗: {e:?}"));
        }
    }
}
