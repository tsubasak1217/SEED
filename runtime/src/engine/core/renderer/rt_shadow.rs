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
//  【v1 の割り切り（TODO）】
//    - スキンメッシュは対象外（TLAS に入れない）。スキン済み頂点からの BLAS 毎フレーム
//      再構築が必要（TODO）。
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

use super::gpu_resources::{GpuModel, GpuPrimitive, InstancedModelBatch};
use super::lighting::LightBuffer;
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
}

/// `prepare_and_build` の結果統計（[PERF] ログ用）。
pub struct RtBuildStat {
    /// このフレームで実際に TLAS を（再）構築したか。false = 静止スキップ。
    pub built: bool,
    /// TLAS に登録されているインスタンス数（構築時は今回値、スキップ時は前回値）。
    pub instances: u32,
}

impl RtShadowResources {
    /// RT 影リソースを生成する（RT 対応時のみ呼ぶこと）。
    ///
    /// - `rt_lights_bgl`: mesh_rt パイプラインの group 4 レイアウト
    ///   （ライト＋シャドウ＋TLAS の複合。binding 6 に acceleration_structure を含む）。
    /// - `shadow`:        シャドウ資源（binding 2〜5 を供給）。
    /// - `light_buffer`:  ライトバッファ（binding 0/1 を供給）。
    pub fn new(
        device:        &wgpu::Device,
        rt_lights_bgl: &wgpu::BindGroupLayout,
        shadow:        &ShadowResources,
        light_buffer:  &LightBuffer,
    ) -> Self {
        // TLAS を生成する。フラグは高速ビルド優先（毎フレーム再構築のため）。
        let tlas = device.create_tlas(&CreateTlasDescriptor {
            label:         Some("RT Shadow TLAS"),
            max_instances: MAX_RT_INSTANCES,
            flags:         AccelerationStructureFlags::PREFER_FAST_BUILD,
            update_mode:   AccelerationStructureUpdateMode::Build,
        });

        // TLAS を参照する group 4 複合 BindGroup を生成する。
        // as_binding() は &tlas を借用するため、TlasPackage へ move する前に生成する。
        // 生成された bind group は内部で TLAS リソース（Arc）を保持するため、以後
        // TlasPackage へ move しても有効であり、再ビルドで内容が更新されても使い回せる。
        let bind_group = light_buffer.create_rt_bind_group(device, rt_lights_bgl, shadow, &tlas);

        let tlas_package = TlasPackage::new(tlas);

        Self {
            tlas_package,
            blas_cache: HashMap::new(),
            bind_group,
            warned_overflow: false,
            warned_usage: std::collections::HashSet::new(),
            last_tlas_sig: None,
            last_inst_count: 0,
        }
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
        encoder: &mut wgpu::CommandEncoder,
        casters: &[(&str, &GpuModel, &InstancedModelBatch)],
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

        // ── 2. BLAS を create_blas してキャッシュへ挿入（初回のみ・ログ）──
        for (key, prim) in &to_build {
            let blas = create_blas_for_prim(device, prim);
            self.blas_cache.insert(key.clone(), blas);
            eprintln!(
                "[SEED RT] BLAS 構築: {} mesh#{} prim#{}（頂点 {} / インデックス {}）",
                key.source_path, key.mesh_idx, key.prim_idx, prim.vertex_count, prim.index_count
            );
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
                    for v in &transform { hasher.write_u32(v.to_bits()); }
                });
            }
            hasher.finish()
        };

        // 初回（last_tlas_sig=None）は必ず構築する。新規 BLAS があるフレームも必ず構築する。
        if !new_blas_built && self.last_tlas_sig == Some(new_sig) {
            // 静止フレーム: GPU 上の TLAS は前回のまま有効。CPU 詰め直し・GPU ビルドを共に省く。
            return RtBuildStat { built: false, instances: self.last_inst_count };
        }
        self.last_tlas_sig = Some(new_sig);

        // ── 3. 新規 BLAS のビルドエントリを構築 ──────────────────
        // サイズ記述子はビルド呼び出しまで生存させる必要があるため Vec に確保する。
        let size_descs: Vec<BlasTriangleGeometrySizeDescriptor> =
            to_build.iter().map(|(_, p)| blas_size_desc(p)).collect();
        let blas_entries: Vec<BlasBuildEntry> = to_build.iter().enumerate()
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

        // ── 4. TLAS インスタンスを詰め直す（全 None → キャスター順に登録）──
        let mut inst_count: usize = 0;
        {
            // disjoint フィールド借用: blas_cache（不変）と tlas_package（可変）。
            let cache = &self.blas_cache;
            let instances = self.tlas_package
                .get_mut_slice(0..MAX_RT_INSTANCES as usize)
                .expect("TLAS スライス範囲は max_instances 以内");
            for slot in instances.iter_mut() { *slot = None; }

            let mut overflow = false;
            for (path, gpu, batch) in casters {
                batch.rt_enumerate(|mesh_idx, prim_idx, material_idx, transform| {
                    if inst_count >= MAX_RT_INSTANCES as usize { overflow = true; return; }
                    let key = BlasKey::new(path, mesh_idx, prim_idx);
                    if let Some(blas) = cache.get(&key) {
                        // custom_data はデバッグ用にインスタンス番号。
                        // mask は alpha_mode 由来（不透明のみ影レイから見える）。
                        let mask = instance_mask_for(gpu.primitive_alpha_mode(material_idx));
                        instances[inst_count] = Some(TlasInstance::new(blas, transform, inst_count as u32, mask));
                        inst_count += 1;
                    }
                });
                if overflow { break; }
            }

            if overflow && !self.warned_overflow {
                self.warned_overflow = true;
                eprintln!(
                    "[SEED RT] 警告: RT 影キャスターのインスタンス数が上限 {} を超過しました。\
                     超過分は影を落としません（MAX_RT_INSTANCES を増やすか対象を減らしてください）",
                    MAX_RT_INSTANCES
                );
            }
        }

        // ── 5. BLAS（新規）と TLAS を 1 回の呼び出しでビルド ──────
        encoder.build_acceleration_structures(blas_entries.iter(), std::iter::once(&self.tlas_package));

        self.last_inst_count = inst_count as u32;
        RtBuildStat { built: true, instances: inst_count as u32 }
    }
}

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

    /// mesh / skinned × RT オン/オフ の 4 バリアントを結合し、naga で parse + validate する。
    /// 連結順は pipelines/*.toml の shader_sources と一致させること。
    #[test]
    fn rt_shader_variants_parse_and_validate() {
        let common   = include_str!("shaders/shader_common.wgsl");
        let shadow   = include_str!("shaders/shadow.wgsl");
        let rt_on    = include_str!("shaders/rt_shadow_on.wgsl");
        let rt_off   = include_str!("shaders/rt_shadow_off.wgsl");
        let static_v = include_str!("shaders/shader_static_vertex.wgsl");
        let skin_v   = include_str!("shaders/shader_skinned_vertex.wgsl");
        let frag     = include_str!("shaders/shader_fragment.wgsl");

        let variants: [(&str, Vec<&str>); 4] = [
            ("mesh_rt",         vec![common, shadow, rt_on,  static_v, frag]),
            ("skinned_mesh_rt", vec![common, shadow, rt_on,  skin_v,   frag]),
            ("mesh",            vec![common, shadow, rt_off, static_v, frag]),
            ("skinned_mesh",    vec![common, shadow, rt_off, skin_v,   frag]),
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
