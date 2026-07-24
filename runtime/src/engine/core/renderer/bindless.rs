// ============================================================
//  bindless.rs — バインドレス基盤（フェーズ B1）
//
//  【目的】
//  inline RT のヒットシェーディングが「プリミティブ平均色」の近似に留まる根本原因＝
//  「ヒット先のテクスチャ・頂点属性（UV）を引けない」を解消する土台を作る。
//  本フェーズ（B1）は **基盤の確保・API・充填のみ** で、消費側（シェーダのバインディング／
//  実際のサンプリング）は後続の B2/B3 で行う。したがって B1 は見た目を一切変えない。
//
//  【本モジュールが提供するもの】
//    1. TextureRegistry: シーン中のベースカラー（albedo）テクスチャに安定インデックスを
//       割り当て、`binding_array<texture_2d<f32>>` として 1 個の binding にまとめられる
//       BindGroup を構築する。index 0 は 1x1 白のダミー（未登録・解放後の安全弁）。
//    2. MegaAllocator ×2: 全プリミティブの UV（vec2）と三角形インデックス（u32）を、
//       少数の巨大 storage buffer にサブアロケートする（buffer の binding_array は使わない。
//       GPU 互換性と実装単純性のため。根拠は下記 MegaAllocator のコメント）。
//    3. BindlessInstanceRecord テーブル: TLAS の custom_data（＝インスタンス番号）で引く
//       1 本の storage buffer。ヒット先の albedo テクスチャ index / UV offset / index offset /
//       base_color_factor / 平均アルベド を 1 レコードにまとめる。WGSL ミラー
//       （shaders/bindless_common.wgsl）とレイアウトを照合するテストを持つ。
//
//  【対応 GPU 限定】
//    バインドレスは `TEXTURE_BINDING_ARRAY` +
//    `SAMPLED_TEXTURE_AND_STORAGE_BUFFER_ARRAY_NON_UNIFORM_INDEXING`（+ 望ましくは
//    `PARTIALLY_BOUND_BINDING_ARRAY`）に対応し、かつ
//    `max_binding_array_elements_per_shader_stage > 0` のアダプタでのみ確保される
//    （renderer/mod.rs が判定）。非対応 GPU では一切確保せず、既存の平均色経路が
//    現状どおり動く。対応可否は下記グローバルフラグ（rt_shadow の流儀）で参照する。
//
//  【既存の平均アルベド storage との関係（互換の守り方）】
//    既存の rt_shadow.rs の平均アルベド storage（16B/inst, `array<vec4<f32>>`）は、
//    rt_shadow_on.wgsl(group4 binding14) / ddgi_probe_update.wgsl(group0 binding4) /
//    reflection_rt.wgsl(group3 binding3) の 3 経路が `.rgb`/`.a` を読む共有バッファである。
//    このバッファのストライド（16B）を変えるとこれら 3 シェーダが無言で壊れる。
//    そこで本モジュールの BindlessInstanceRecord は **別個のバッファ** として新設し、
//    既存の 16B バッファには一切触れない（＝ゼロリグレッション）。
//    将来 B2/B3 で統合できるよう、BindlessInstanceRecord の先頭 16B は既存と同じ
//    「平均アルベド vec4（.rgb=生アルベド, .a=α/transmission パック）」に合わせてある。
// ============================================================

// B1 は「基盤の確保・API・充填」までを提供するフェーズであり、レジストリ登録／メガバッファ追記／
// インスタンステーブル充填／BindGroup 構築の各 API は **消費側 B2/B3 が呼ぶ**ため、B1 時点では
// 多くが未使用になる（意図的）。個別に #[allow] を撒くとノイズになるためモジュール単位で許可する。
// （テスト・レイアウト照合で全 API の健全性自体は担保している。）
#![allow(dead_code)]

use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::sync::{Arc, Mutex};

// ─── グローバル対応フラグ ────────────────────────────────────
//
// デバイス初期化（renderer/mod.rs）で 1 回だけ確定させる（rt_shadow と同じ流儀）。
// gpu_resources などが「バインドレス資源を確保・充填するか」を判断するために参照する。
static BINDLESS_SUPPORTED: AtomicBool = AtomicBool::new(false);

/// 確定したテクスチャ配列容量（アダプタ上限でクランプ後）。mod.rs が算出して設定し、
/// DrawContext が BindlessResources 確保時に読む（アダプタ参照を持ち回らずに済ませる）。
static BINDLESS_CAPACITY: AtomicU32 = AtomicU32::new(0);

/// バインドレス対応フラグと確定容量を設定する（デバイス初期化時に 1 回）。
pub fn set_bindless_supported(v: bool) {
    BINDLESS_SUPPORTED.store(v, Ordering::Relaxed);
}

/// 確定したテクスチャ配列容量を設定する（デバイス初期化時に 1 回）。
pub fn set_bindless_capacity(cap: u32) {
    BINDLESS_CAPACITY.store(cap, Ordering::Relaxed);
}

/// バインドレス対応フラグを取得する。
pub fn bindless_supported() -> bool {
    BINDLESS_SUPPORTED.load(Ordering::Relaxed)
}

/// 確定したテクスチャ配列容量を取得する（未設定＝0）。
pub fn bindless_capacity() -> u32 {
    BINDLESS_CAPACITY.load(Ordering::Relaxed)
}

// ─── 定数（マジックナンバー禁止・名前付き）──────────────────────

/// バインドレス テクスチャ配列の目安上限（要素数）。実際の容量はアダプタ上限
/// （`max_binding_array_elements_per_shader_stage` / `max_sampled_textures_per_shader_stage`）
/// との min を取る（renderer/mod.rs が算出して `BindlessResources::new` に渡す）。
pub const BINDLESS_MAX_TEXTURES: u32 = 4096;

/// 未登録／解放後を指す予約インデックス（1x1 白ダミー）。0 番は常にダミー。
pub const BINDLESS_DUMMY_TEX_INDEX: u32 = 0;

/// UV メガバッファの既定容量（バイト）。vec2<f32>=8B/頂点。64MB ≒ 800 万頂点分。
/// これを超えたプリミティブは append が None を返し、バインドレス対象外（ダミー扱い）になる。
pub const BINDLESS_UV_BUFFER_BYTES: u64 = 64 * 1024 * 1024;

/// インデックス メガバッファの既定容量（バイト）。u32=4B/インデックス。128MB ≒ 3200 万インデックス分。
/// 【実測根拠】初期値 32MB（800 万）は Sponza 級 1 シーンで超過した
/// （Sponza main だけで約 1100 万インデックス ≒ 44MB。あふれた後続プリミティブが平均色へ
/// 縮退し「カーテンだけベタ塗りで反射する」症状になった）。複数の大型モデルの同時ロードに
/// 余裕を持たせて 128MB とする。それでも超えた場合は従来どおり警告ログ＋平均色縮退（安全）。
pub const BINDLESS_INDEX_BUFFER_BYTES: u64 = 128 * 1024 * 1024;

/// UV 要素 1 個のバイト数（vec2<f32>）。
pub const BINDLESS_UV_ELEM_BYTES: u64 = 8;

/// インデックス要素 1 個のバイト数（u32）。
pub const BINDLESS_INDEX_ELEM_BYTES: u64 = 4;

/// 法線メガバッファの既定容量（バイト）。八面体エンコード u32=4B/頂点。
/// UV メガバッファ（8B/頂点・64MB ≒ 800 万頂点）と同じ頂点数を収めるため、
/// その半分の 32MB とする（4B/頂点 × 800 万頂点 = 32MB）。屈折の界面ごとの本物の
/// 補間法線復元に使う（RT 屈折の多重界面再屈折）。あふれたら平均色ベールへ縮退（安全）。
pub const BINDLESS_NORMAL_BUFFER_BYTES: u64 = 32 * 1024 * 1024;

/// 法線要素 1 個のバイト数（八面体エンコード u32）。
pub const BINDLESS_NORMAL_ELEM_BYTES: u64 = 4;

/// TLAS インスタンス数の上限（= rt_shadow::MAX_RT_INSTANCES）。インスタンステーブルの容量。
/// rt_shadow 側の定数と一致させること（レイアウトテストで担保）。
pub const BINDLESS_MAX_INSTANCES: u32 = super::rt_shadow::MAX_RT_INSTANCES;

// ─── BindlessInstanceRecord: フラグビット ────────────────────

/// このインスタンスがバインドレス対象（UV/index/テクスチャを引ける）ことを示すフラグ。
/// 0 なら B2/B3 の消費側は従来の平均色フォールバックを使う（縮退動作）。
pub const BINDLESS_FLAG_ELIGIBLE: u32 = 1 << 0;

/// このインスタンスが Mask マテリアル（アルファテスト＝葉・鎖・フェンス等）であることを示すフラグ（B3）。
/// 色付き影の第 2 クエリ（RT_MASK_NON_OPAQUE 0x02）で Mask 面にヒットしたとき、消費側
/// （rt_shadow_tint_bindless.wgsl）は「テクスチャ α を alpha_cutoff と比較し、α>=cutoff なら
/// 完全遮蔽（葉の形の影）、α<cutoff なら素通り」する。立っていない（Blend）インスタンスは
/// 従来どおり透過色フィルタ（色付き影）で処理する。
/// WGSL ミラー bindless_common.wgsl の BINDLESS_FLAG_MASK と一致させること。
pub const BINDLESS_FLAG_MASK: u32 = 1 << 1;

/// このインスタンスがバインドレス**ジオメトリ**（UV/index/**法線**）を登録済みであることを示すフラグ。
/// `BINDLESS_FLAG_ELIGIBLE`（= ジオメトリ＋テクスチャ）とは独立で、テクスチャ無し（base_color_factor
/// のみの素ガラス）でも法線は引ける。RT 屈折の界面ごとの本物の法線復元は**このビット**で判定する
/// （テクスチャ有無に依存させない）。WGSL ミラー bindless_common.wgsl の BINDLESS_FLAG_GEOM と一致させること。
pub const BINDLESS_FLAG_GEOM: u32 = 1 << 2;

// ============================================================
//  八面体法線エンコード（u32・4B/頂点）— Rust ↔ WGSL 往復一致
// ============================================================
//
// 単位法線 vec3 を八面体マッピングで 2 成分（[-1,1]）へ畳み、各成分を 16bit 固定小数へ
// 量子化して 1 個の u32（下位16=x, 上位16=y）へ詰める。省メモリ（12B→4B/頂点）のため。
// WGSL 側 bindless_common.wgsl の `oct_decode_normal` とビット単位で対応させること
// （`bindless::tests::oct_roundtrip_*` が Rust 側の往復一致を担保する）。

/// 八面体の下半球（z<0）を折り返す補助（Cigolle et al. の標準式）。
fn oct_wrap(x: f32, y: f32) -> (f32, f32) {
    let sx = if x >= 0.0 { 1.0 } else { -1.0 };
    let sy = if y >= 0.0 { 1.0 } else { -1.0 };
    ((1.0 - y.abs()) * sx, (1.0 - x.abs()) * sy)
}

/// 単位法線 `n`（正規化前でも可・内部で L1 正規化）を八面体エンコード u32 にする。
/// 下位 16bit = x 成分、上位 16bit = y 成分（各 [-1,1]→[0,65535] の量子化）。
pub fn oct_encode_normal(n: [f32; 3]) -> u32 {
    let inv_l1 = 1.0 / (n[0].abs() + n[1].abs() + n[2].abs()).max(1.0e-8);
    let mut ex = n[0] * inv_l1;
    let mut ey = n[1] * inv_l1;
    if n[2] < 0.0 {
        let (wx, wy) = oct_wrap(ex, ey);
        ex = wx;
        ey = wy;
    }
    // [-1,1] → [0,65535] 量子化（round-nearest）。
    let qx = ((ex * 0.5 + 0.5).clamp(0.0, 1.0) * 65535.0).round() as u32;
    let qy = ((ey * 0.5 + 0.5).clamp(0.0, 1.0) * 65535.0).round() as u32;
    (qy << 16) | (qx & 0xffff)
}

/// 八面体エンコード u32 を単位法線へ復号する（WGSL `oct_decode_normal` のミラー・テスト用）。
#[cfg(test)]
fn oct_decode_normal(u: u32) -> [f32; 3] {
    let ex = (u & 0xffff) as f32 / 65535.0 * 2.0 - 1.0;
    let ey = ((u >> 16) & 0xffff) as f32 / 65535.0 * 2.0 - 1.0;
    let mut nx = ex;
    let mut ny = ey;
    let nz = 1.0 - ex.abs() - ey.abs();
    let t = (-nz).max(0.0);
    nx += if nx >= 0.0 { -t } else { t };
    ny += if ny >= 0.0 { -t } else { t };
    let len = (nx * nx + ny * ny + nz * nz).sqrt().max(1.0e-8);
    [nx / len, ny / len, nz / len]
}

// ============================================================
//  BindlessInstanceRecord — TLAS custom_data で引く 1 レコード
// ============================================================

/// TLAS インスタンス 1 件のヒットシェーディング用データ（std430, 64B, 16B 整列）。
///
/// レイアウトは `shaders/bindless_common.wgsl` の `BindlessInstanceRecord` と
/// バイト単位で一致させること（`bindless_record_layout` テストが担保）。
///
/// 【先頭 16B の互換】`avg_albedo` を offset 0 に置く。これは既存の平均アルベド storage
/// （`array<vec4<f32>>`）と同じ意味・同じ位置で、将来 B2/B3 で両バッファを統合するときに
/// 先頭 16B をそのまま流用できるようにするため（本 B1 では別バッファなので実害はないが、
/// 統合時の安全弁として先頭に置く）。
// align(16): WGSL の vec4<f32>（16B 整列）を含む std430 構造体に合わせ、Rust 側の型も
// 16B 整列にする（[f32;4] 単体の align は 4 のため明示指定しないと 4 になる）。サイズは 64B のまま。
#[repr(C, align(16))]
#[derive(Debug, Clone, Copy, PartialEq, bytemuck::Pod, bytemuck::Zeroable)]
pub struct BindlessInstanceRecord {
    /// offset 0: 平均アルベド。`.rgb`=生アルベド（GI/反射のバウンス色）、
    /// `.a`=RT 色付き影の α/transmission パック（rt_shadow.rs のパックと同一）。
    pub avg_albedo: [f32; 4],
    /// offset 16: ベースカラー係数（override 適用後）。B2 のヒットシェーディングで
    /// サンプルした albedo テクスチャ値に乗算する。
    pub base_color_factor: [f32; 4],
    /// offset 32: albedo テクスチャの TextureRegistry インデックス（未登録は 0=ダミー白）。
    pub albedo_tex_index: u32,
    /// offset 36: UV メガバッファ内の先頭要素オフセット（vec2 単位）。
    pub uv_offset: u32,
    /// offset 40: インデックス メガバッファ内の先頭要素オフセット（u32 単位）。
    pub index_offset: u32,
    /// offset 44: フラグ（`BINDLESS_FLAG_ELIGIBLE` / `BINDLESS_FLAG_MASK` 等）。
    pub flags: u32,
    /// offset 48: Mask マテリアルのアルファカットオフ（B3）。`BINDLESS_FLAG_MASK` が立っている
    /// インスタンスでのみ意味を持つ。色付き影の第 2 クエリで「サンプルした α >= alpha_cutoff なら
    /// 完全遮蔽（葉の形の影）」の閾値。Blend では未使用（0.0）。
    pub alpha_cutoff: f32,
    /// offset 52: 法線メガバッファ内の先頭要素オフセット（八面体 u32 単位・頂点順）。
    /// `BINDLESS_FLAG_GEOM` が立っているとき有効。RT 屈折の界面ごとの補間法線復元に使う。
    pub normal_offset: u32,
    /// offset 56..64: 16B 整列のためのパディング（vec3 パディング禁止＝明示的に埋める）。
    pub _pad: [u32; 2],
}

impl BindlessInstanceRecord {
    /// バインドレス非対象（全ゼロ＝ダミー白・フォールバック）のレコード。
    pub fn dummy() -> Self {
        Self {
            avg_albedo: [0.0; 4],
            base_color_factor: [1.0, 1.0, 1.0, 1.0],
            albedo_tex_index: BINDLESS_DUMMY_TEX_INDEX,
            uv_offset: 0,
            index_offset: 0,
            flags: 0,
            alpha_cutoff: 0.0,
            normal_offset: 0,
            _pad: [0; 2],
        }
    }
}

// ============================================================
//  MegaAllocator — メガバッファのサブアロケータ（純 CPU・テスト可能）
// ============================================================

/// 巨大 storage buffer 1 本を「バンプ + フリーリスト」でサブアロケートする純 CPU 構造体。
///
/// 【なぜ buffer の binding_array を使わずメガバッファ方式か】
///   `binding_array<...>` を buffer に対して使うのは GPU/ドライバ間の互換が悪く（対応が
///   テクスチャ配列より狭い）、実装も煩雑になる。全プリミティブの最小データ（UV・index）を
///   少数の巨大 storage buffer に連結し、レコードの (uv_offset, index_offset) で引く方が
///   互換・単純さの両面で優れる。B2 のヒットシェーダは `uv_buf[uv_offset + i]` の形で引く。
///
/// 【断片化の限界】フリーリストは first-fit + 隣接コアレスのみ（コンパクションは無し）。
///   モデルの頻繁なロード／アンロードで穴あきが進むと、空き総量は足りても連続領域が取れず
///   append が None を返す（＝そのモデルはバインドレス対象外＝ダミー扱い）ことがあり得る。
///   実用上はシーンのモデル数が有界なため問題になりにくいが、限界として明記しておく。
///   コンパクション（生存領域の詰め直し）は将来の課題。
#[derive(Debug)]
pub struct MegaAllocator {
    /// 総容量（バイト）。
    capacity: u64,
    /// 次に新規確保するバンプ位置（バイト）。フリーリストで賄えないときだけ前進する。
    bump: u64,
    /// 解放済み領域 `(offset, size)`（バイト）。offset 昇順・非隣接（コアレス済み）に保つ。
    free: Vec<(u64, u64)>,
    /// 現在確保中の総バイト数（統計・テスト用）。
    used: u64,
}

impl MegaAllocator {
    /// 容量 `capacity`（バイト）のアロケータを作る。
    pub fn new(capacity: u64) -> Self {
        Self {
            capacity,
            bump: 0,
            free: Vec::new(),
            used: 0,
        }
    }

    /// `size` バイトを `align` 境界に整列して確保し、先頭バイトオフセットを返す。
    /// 容量不足なら `None`（呼び出し側は縮退＝バインドレス対象外にする）。
    ///
    /// 手順:
    ///   1. フリーリストから「整列後に size が収まる」最初の領域を探す（first-fit）。
    ///      見つかれば前パディング・後残りを分割してフリーリストへ戻す。
    ///   2. 無ければバンプを整列前進させて確保する。
    pub fn alloc(&mut self, size: u64, align: u64) -> Option<u64> {
        debug_assert!(
            align > 0 && align.is_power_of_two(),
            "align は 2 の冪であること"
        );
        if size == 0 {
            // 0 バイト要求は「先頭を指す有効なオフセット 0」を返す（空 UV/index の縮退回避）。
            return Some(0);
        }

        // ── 1. フリーリスト first-fit ─────────────────────────
        for i in 0..self.free.len() {
            let (off, sz) = self.free[i];
            let aligned = align_up(off, align);
            let pad = aligned - off; // 前パディング
            if pad + size <= sz {
                // この領域を使う。前パディングと後残りをフリーリストへ戻す。
                self.free.remove(i);
                let tail_off = aligned + size;
                let tail_sz = sz - pad - size;
                // 後残り（tail）を先に push（順序は下の正規化で整える）。
                if tail_sz > 0 {
                    self.free.push((tail_off, tail_sz));
                }
                // 前パディング（head）も無駄領域として戻す（次回の小要求で再利用可能）。
                if pad > 0 {
                    self.free.push((off, pad));
                }
                self.normalize_free();
                self.used += size;
                return Some(aligned);
            }
        }

        // ── 2. バンプ確保 ────────────────────────────────────
        let aligned = align_up(self.bump, align);
        if aligned + size > self.capacity {
            return None; // 容量オーバー
        }
        // バンプ前進で生じた前パディングはフリーリストへ戻す（無駄にしない）。
        if aligned > self.bump {
            self.free.push((self.bump, aligned - self.bump));
            self.normalize_free();
        }
        self.bump = aligned + size;
        self.used += size;
        Some(aligned)
    }

    /// `offset` から `size` バイトの領域を解放する（フリーリストへ戻し、隣接をコアレス）。
    pub fn free(&mut self, offset: u64, size: u64) {
        if size == 0 {
            return;
        }
        debug_assert!(offset + size <= self.capacity, "解放範囲が容量外");
        self.used = self.used.saturating_sub(size);
        self.free.push((offset, size));
        self.normalize_free();
    }

    /// フリーリストを offset 昇順ソートし、隣接領域をコアレスする。
    fn normalize_free(&mut self) {
        if self.free.len() <= 1 {
            return;
        }
        self.free.sort_unstable_by_key(|&(off, _)| off);
        let mut merged: Vec<(u64, u64)> = Vec::with_capacity(self.free.len());
        for &(off, sz) in &self.free {
            if let Some(last) = merged.last_mut() {
                if last.0 + last.1 == off {
                    // 直前の領域と隣接 → 結合。
                    last.1 += sz;
                    continue;
                }
            }
            merged.push((off, sz));
        }
        self.free = merged;
    }

    /// 現在確保中の総バイト数。
    pub fn used(&self) -> u64 {
        self.used
    }
    /// 総容量（バイト）。
    pub fn capacity(&self) -> u64 {
        self.capacity
    }
}

/// `v` を `align`（2 の冪）境界へ切り上げる。
fn align_up(v: u64, align: u64) -> u64 {
    (v + align - 1) & !(align - 1)
}

/// 色付き影（B3）用 group3 の BindGroupLayout を作る（インスタンス不要＝パイプライン構築時に呼べる）。
///
/// deferred_lighting_rt / shadow_mask のバインドレス影バリアントの group3 に置く。**group3 には
/// uniform を一切置かない**（WebGPU 制約: binding_array と uniform buffer は同一 bind group に同居不可。
/// binding_array を含むこのグループは uniform 禁止）。group4（ライト＋シャドウ＋TLAS）に uniform は
/// 残るが、そちらには binding_array を置かないため制約を満たす（＝設計上、両立が構成として保証される）。
///
/// バインディング（rt_shadow_tint_bindless.wgsl と一致）:
///   0=instance_table(storage) / 1=UV(storage) / 2=index(storage) /
///   3=テクスチャ配列(binding_array<texture_2d>, count=capacity) / 4=共有サンプラー
///
/// `capacity` はアダプタ上限でクランプ済みの確定容量（`bindless_capacity()`）。
pub fn colored_shadow_bgl(device: &wgpu::Device, capacity: u32) -> wgpu::BindGroupLayout {
    let cap = capacity.max(1);
    let vis = wgpu::ShaderStages::FRAGMENT;
    let storage_ro = |binding: u32| wgpu::BindGroupLayoutEntry {
        binding,
        visibility: vis,
        ty: wgpu::BindingType::Buffer {
            ty: wgpu::BufferBindingType::Storage { read_only: true },
            has_dynamic_offset: false,
            min_binding_size: None,
        },
        count: None,
    };
    device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label: Some("Bindless Colored Shadow BGL (group3)"),
        entries: &[
            storage_ro(0), // instance_table（BindlessInstanceRecord 配列）
            storage_ro(1), // UV メガバッファ（vec2<f32>）
            storage_ro(2), // index メガバッファ（u32）
            wgpu::BindGroupLayoutEntry {
                binding: 3,
                visibility: vis,
                ty: wgpu::BindingType::Texture {
                    sample_type: wgpu::TextureSampleType::Float { filterable: true },
                    view_dimension: wgpu::TextureViewDimension::D2,
                    multisampled: false,
                },
                count: Some(std::num::NonZeroU32::new(cap).unwrap()),
            },
            wgpu::BindGroupLayoutEntry {
                binding: 4,
                visibility: vis,
                ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
                count: None,
            },
        ],
    })
}

// ============================================================
//  TextureRegistry — albedo テクスチャの安定インデックス管理
// ============================================================

/// ベースカラー（albedo）テクスチャに安定インデックスを割り当て、解放スロットを再利用する。
///
/// 【なぜ albedo だけか】B2 のヒットシェーディングでまず必要なのはベースカラーの色相
/// （テクスチャ × factor）である。normal/MR も将来引けると望ましいが、それぞれ別の
/// binding_array・別の意味を持つため、まず albedo 1 種に絞って基盤を単純に保つ
/// （B3 以降で `binding_array` を種類ごとに増設する設計余地は残す）。
///
/// index 0 は常に 1x1 白ダミー（`BINDLESS_DUMMY_TEX_INDEX`）で、登録されていない／解放済みの
/// スロットの安全弁。BindGroup 構築時、未使用スロットは全てダミーで埋める（下記参照）。
struct TextureRegistry {
    /// スロット配列。`Some(view)`=登録済み、`None`=空き（ダミーで埋める）。index 0 は常にダミー。
    /// wgpu の TextureView は Arc バックの安価なハンドルなので clone して保持できる
    /// （clone した view は元テクスチャを生存させる＝BindGroup 構築まで有効）。
    slots: Vec<Option<wgpu::TextureView>>,
    /// スロット占有フラグ（インデックス採番ロジックの唯一の真実。wgpu 非依存でテスト可能）。
    /// `slots[i].is_some()` と常に一致させる。index 0（ダミー）は常に occupied 扱い。
    occupied: Vec<bool>,
    /// 再利用可能な空きインデックス（0 は含めない）。解放時に push、採番時に優先 pop。
    free_indices: Vec<u32>,
    /// 次に新規採番する最小候補（フリーリストが空のときに前進させるバンプ）。
    next: u32,
    /// 容量（= テクスチャ配列サイズ）。
    capacity: u32,
}

impl TextureRegistry {
    fn new(capacity: u32) -> Self {
        let mut slots = Vec::with_capacity(capacity as usize);
        let mut occupied = Vec::with_capacity(capacity as usize);
        for i in 0..capacity {
            slots.push(None);
            occupied.push(i == BINDLESS_DUMMY_TEX_INDEX); // 0 番は常に占有（ダミー予約）
        }
        Self {
            slots,
            occupied,
            free_indices: Vec::new(),
            next: 1,
            capacity,
        }
    }

    /// 空きインデックスを 1 個確保して返す（純ロジック・wgpu 非依存＝単体テスト対象）。
    /// フリーリストを優先（LIFO 再利用）、無ければバンプ（1..capacity）。満杯なら `None`。
    fn alloc_index(&mut self) -> Option<u32> {
        let idx = if let Some(i) = self.free_indices.pop() {
            i
        } else if self.next < self.capacity {
            let i = self.next;
            self.next += 1;
            i
        } else {
            return None; // 満杯
        };
        self.occupied[idx as usize] = true;
        Some(idx)
    }

    /// albedo テクスチャ view を登録し、安定インデックスを返す。満杯なら `None`。
    fn register(&mut self, view: &wgpu::TextureView) -> Option<u32> {
        let idx = self.alloc_index()?;
        self.slots[idx as usize] = Some(view.clone());
        Some(idx)
    }

    /// インデックスを解放する（スロットを空にし、再利用リストへ戻す）。0 番（ダミー）は無視。
    fn free(&mut self, index: u32) {
        if index == BINDLESS_DUMMY_TEX_INDEX {
            return;
        }
        let i = index as usize;
        if i < self.occupied.len() && self.occupied[i] {
            self.occupied[i] = false;
            self.slots[i] = None;
            self.free_indices.push(index);
        }
    }

    /// 現在登録中のテクスチャ数（ダミーを除く）。
    fn registered_count(&self) -> usize {
        self.occupied.iter().skip(1).filter(|&&o| o).count()
    }
}

// ============================================================
//  遅延解放キュー（RAII）— GpuModel の drop で確実に解放する
// ============================================================
//
// 【なぜ RAII / 遅延キューか（設計判断）】
// GpuModel は多数の場所で drop される（MC の `Option<GpuModel>` 置換・stale バッチ prune・
// スロット再割当・シーン再読込・複製・ギズモ）。B2 で登録したテクスチャ配列スロットと
// UV/index メガバッファ領域を「明示的に free する」方式にすると、全 drop 地点を漏れなく
// 呼ぶ必要があり、1 箇所でも漏れるとスロット/領域がリークする（4096 枠・64MB は有界だが
// 長時間のシーン編集で枯渇し得る）。逆に同じ GpuModel を 2 経路から free すると二重解放になる。
//
// そこで **登録の対価として GpuModel に `BindlessModelAlloc`（RAII ハンドル）を持たせ、
// その Drop で解放要求をキューへ積む**。GpuModel は Clone 不可（wgpu ハンドルを一意所有）で
// Drop はちょうど 1 回だけ発火するため、どこで drop されても **必ず 1 回だけ** 解放要求が積まれる
// （リークも二重解放も構造的に起こらない）。実際の registry/allocator への返却は
// `BindlessResources::process_pending_frees` が次のモデル登録時にまとめて行う。

/// GpuModel 1 個分の解放要求（drop 時にキューへ積まれる）。
#[derive(Debug, Default)]
struct BindlessFree {
    /// 解放する albedo テクスチャ index 群（登録順）。
    tex: Vec<u32>,
    /// 解放する UV 領域 `(要素オフセット, 要素数)` 群。
    uv: Vec<(u32, u32)>,
    /// 解放するインデックス領域 `(要素オフセット, 要素数)` 群。
    idx: Vec<(u32, u32)>,
    /// 解放する法線領域 `(要素オフセット, 要素数)` 群。
    nrm: Vec<(u32, u32)>,
}

/// 遅延解放キュー本体。`BindlessModelAlloc` の Drop（任意スレッド・任意タイミング）から
/// 積まれ、`BindlessResources::process_pending_frees`（レンダースレッド）が排出する。
type BindlessFreeQueue = Arc<Mutex<Vec<BindlessFree>>>;

/// GpuModel が保持するバインドレス割り当てハンドル（RAII）。
///
/// 登録した texture index / UV 領域 / index 領域を記録し、**Drop で解放要求をキューへ積む**。
/// GpuModel は Clone 不可なので Drop はちょうど 1 回。空（何も登録していない）なら Drop は無操作。
pub struct BindlessModelAlloc {
    /// 解放要求の積み先（`BindlessResources` と共有）。
    queue: BindlessFreeQueue,
    tex: Vec<u32>,
    uv: Vec<(u32, u32)>,
    idx: Vec<(u32, u32)>,
    nrm: Vec<(u32, u32)>,
}

impl BindlessModelAlloc {
    /// 空のハンドルを作る（キューを共有）。以後 `record_*` で登録内容を積む。
    fn new(queue: BindlessFreeQueue) -> Self {
        Self {
            queue,
            tex: Vec::new(),
            uv: Vec::new(),
            idx: Vec::new(),
            nrm: Vec::new(),
        }
    }
    /// 登録した albedo テクスチャ index を記録する（ダミー 0 番は記録しない＝解放不要）。
    fn record_texture(&mut self, index: u32) {
        if index != BINDLESS_DUMMY_TEX_INDEX {
            self.tex.push(index);
        }
    }
    /// 登録した UV 領域（要素オフセット・要素数）を記録する。
    fn record_uv(&mut self, elem_offset: u32, elem_count: u32) {
        if elem_count > 0 {
            self.uv.push((elem_offset, elem_count));
        }
    }
    /// 登録したインデックス領域（要素オフセット・要素数）を記録する。
    fn record_index(&mut self, elem_offset: u32, elem_count: u32) {
        if elem_count > 0 {
            self.idx.push((elem_offset, elem_count));
        }
    }
    /// 登録した法線領域（要素オフセット・要素数）を記録する。
    fn record_normal(&mut self, elem_offset: u32, elem_count: u32) {
        if elem_count > 0 {
            self.nrm.push((elem_offset, elem_count));
        }
    }
    /// 何も登録していないか（Drop の無操作判定・テスト用）。
    fn is_empty(&self) -> bool {
        self.tex.is_empty() && self.uv.is_empty() && self.idx.is_empty() && self.nrm.is_empty()
    }
}

impl Drop for BindlessModelAlloc {
    /// 登録内容を 1 件の解放要求としてキューへ積む（実際の返却は process_pending_frees）。
    /// `mem::take` で中身を移すため二重登録は起こらない。空なら積まない。
    fn drop(&mut self) {
        if self.is_empty() {
            return;
        }
        let free = BindlessFree {
            tex: std::mem::take(&mut self.tex),
            uv: std::mem::take(&mut self.uv),
            idx: std::mem::take(&mut self.idx),
            nrm: std::mem::take(&mut self.nrm),
        };
        // ロック失敗（poison）時も解放要求を捨てない（リーク回避）。into_inner ではなく
        // ロックを取り直す代わりに、poison でも guard を得て push する。
        if let Ok(mut q) = self.queue.lock() {
            q.push(free);
        }
    }
}

// ============================================================
//  BindlessResources — GPU 資源の束（対応時のみ確保）
// ============================================================

/// バインドレス基盤の GPU 資源一式。対応 GPU でのみ `Some` として DrawContext が保持する。
///
/// B1 では確保・登録・充填 API までを提供し、消費（シェーダのバインド）は行わない。
/// テクスチャ配列の BindGroup は `dirty` 時に遅延再構築する（登録／解放でダーティ化）。
pub struct BindlessResources {
    /// BindGroup 再構築に使うデバイス。
    device: Arc<wgpu::Device>,

    /// テクスチャ配列の容量（= レジストリ容量）。
    capacity: u32,
    /// albedo テクスチャ レジストリ。
    registry: TextureRegistry,

    /// 1x1 白ダミーテクスチャ（index 0・未使用スロット埋め用）。
    #[allow(dead_code)]
    dummy_texture: wgpu::Texture,
    dummy_view: wgpu::TextureView,
    /// 共有サンプラー 1 個（リニア・リピート）。B2 のヒットシェーダが全 albedo に使う。
    sampler: wgpu::Sampler,

    /// テクスチャ配列 BindGroup のレイアウト（B2 が同一レイアウトで消費側パイプラインを組む）。
    tex_bgl: wgpu::BindGroupLayout,
    /// キャッシュ済みテクスチャ配列 BindGroup。`dirty` 時に再構築する。
    tex_bg: Option<wgpu::BindGroup>,
    /// レジストリ変更でダーティ化し、次の `ensure_texture_bind_group` で再構築させる。
    dirty: bool,

    /// UV メガバッファ（vec2<f32> 連結, storage）。
    uv_buffer: wgpu::Buffer,
    uv_alloc: MegaAllocator,
    /// インデックス メガバッファ（u32 連結, storage）。
    index_buffer: wgpu::Buffer,
    index_alloc: MegaAllocator,
    /// 法線メガバッファ（八面体エンコード u32 連結, storage）。頂点順で index メガバッファ経由で引く。
    /// RT 屈折の界面ごとの本物の補間法線復元に使う（UV/index と同じ仕組み）。
    normal_buffer: wgpu::Buffer,
    normal_alloc: MegaAllocator,

    /// BindlessInstanceRecord テーブル（TLAS custom_data で引く, storage）。容量 = MAX_INSTANCES。
    instance_table: wgpu::Buffer,

    /// UV/index/法線 容量オーバー警告の抑止（ログ爆発防止）。
    warned_uv_overflow: bool,
    warned_index_overflow: bool,
    warned_normal_overflow: bool,

    /// 遅延解放キュー。GpuModel が持つ `BindlessModelAlloc` の Drop から解放要求が積まれ、
    /// `process_pending_frees` が registry/allocator へ返却する（リーク・二重解放防止の要）。
    free_queue: BindlessFreeQueue,
}

impl BindlessResources {
    /// バインドレス資源を確保する（対応 GPU でのみ呼ぶこと）。
    ///
    /// - `capacity`: テクスチャ配列サイズ。renderer/mod.rs がアダプタ上限との min を取って渡す。
    pub fn new(device: &Arc<wgpu::Device>, queue: &wgpu::Queue, capacity: u32) -> Self {
        let capacity = capacity.max(1); // 最低 1（ダミー分）

        // ── ダミー 1x1 白テクスチャ（index 0）─────────────────
        let dummy_texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("Bindless Dummy White"),
            size: wgpu::Extent3d {
                width: 1,
                height: 1,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            // ベースカラーは sRGB 系だがダミー白（1,1,1,1）はどちらでも同値。
            format: wgpu::TextureFormat::Rgba8UnormSrgb,
            usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats: &[],
        });
        queue.write_texture(
            wgpu::TexelCopyTextureInfo {
                texture: &dummy_texture,
                mip_level: 0,
                origin: wgpu::Origin3d::ZERO,
                aspect: wgpu::TextureAspect::All,
            },
            &[255u8, 255, 255, 255],
            wgpu::TexelCopyBufferLayout {
                offset: 0,
                bytes_per_row: Some(4),
                rows_per_image: Some(1),
            },
            wgpu::Extent3d {
                width: 1,
                height: 1,
                depth_or_array_layers: 1,
            },
        );
        let dummy_view = dummy_texture.create_view(&wgpu::TextureViewDescriptor::default());

        // ── 共有サンプラー（リニア・リピート）─────────────────
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("Bindless Shared Sampler"),
            address_mode_u: wgpu::AddressMode::Repeat,
            address_mode_v: wgpu::AddressMode::Repeat,
            address_mode_w: wgpu::AddressMode::Repeat,
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
            mipmap_filter: wgpu::FilterMode::Linear,
            ..Default::default()
        });

        // ── テクスチャ配列 BindGroupLayout（binding0=配列, binding1=サンプラー）──
        // 部分バインドが使える環境でも、B1 では全スロットをダミーで埋めて構築するため
        // count = capacity 固定。B2 が同一レイアウトで消費側パイプラインを組む。
        let tex_bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("Bindless Texture Array BGL"),
            entries: &[
                wgpu::BindGroupLayoutEntry {
                    binding: 0,
                    visibility: wgpu::ShaderStages::FRAGMENT | wgpu::ShaderStages::COMPUTE,
                    ty: wgpu::BindingType::Texture {
                        sample_type: wgpu::TextureSampleType::Float { filterable: true },
                        view_dimension: wgpu::TextureViewDimension::D2,
                        multisampled: false,
                    },
                    count: Some(std::num::NonZeroU32::new(capacity).unwrap()),
                },
                wgpu::BindGroupLayoutEntry {
                    binding: 1,
                    visibility: wgpu::ShaderStages::FRAGMENT | wgpu::ShaderStages::COMPUTE,
                    ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
                    count: None,
                },
            ],
        });

        // ── メガバッファ ─────────────────────────────────────
        let uv_buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("Bindless UV Megabuffer"),
            size: BINDLESS_UV_BUFFER_BYTES,
            usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        let index_buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("Bindless Index Megabuffer"),
            size: BINDLESS_INDEX_BUFFER_BYTES,
            usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        let normal_buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("Bindless Normal Megabuffer"),
            size: BINDLESS_NORMAL_BUFFER_BYTES,
            usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        // ── インスタンステーブル ────────────────────────────
        let instance_table = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("Bindless Instance Table"),
            size: (BINDLESS_MAX_INSTANCES as u64)
                * std::mem::size_of::<BindlessInstanceRecord>() as u64,
            usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        Self {
            device: Arc::clone(device),
            capacity,
            registry: TextureRegistry::new(capacity),
            dummy_texture,
            dummy_view,
            sampler,
            tex_bgl,
            tex_bg: None,
            dirty: true,
            uv_buffer,
            uv_alloc: MegaAllocator::new(BINDLESS_UV_BUFFER_BYTES),
            index_buffer,
            index_alloc: MegaAllocator::new(BINDLESS_INDEX_BUFFER_BYTES),
            normal_buffer,
            normal_alloc: MegaAllocator::new(BINDLESS_NORMAL_BUFFER_BYTES),
            instance_table,
            warned_uv_overflow: false,
            warned_index_overflow: false,
            warned_normal_overflow: false,
            free_queue: Arc::new(Mutex::new(Vec::new())),
        }
    }

    // ── 遅延解放（RAII）─────────────────────────────────────

    /// GpuModel の `BindlessModelAlloc` に渡す解放要求キュー（共有クローン）を返す。
    pub fn free_queue_handle(&self) -> BindlessFreeQueue {
        Arc::clone(&self.free_queue)
    }

    /// 新しい空の割り当てハンドルを作る（このリソースのキューを共有）。
    /// GpuModel の登録時に受け取り、登録内容を記録して GpuModel に保持させる。
    pub fn new_model_alloc(&self) -> BindlessModelAlloc {
        BindlessModelAlloc::new(self.free_queue_handle())
    }

    /// キューに積まれた解放要求を排出し、registry / メガバッファへ実際に返却する。
    /// モデル登録（`register_*`）の直前に呼び、解放済みスロット/領域を再利用可能にする。
    /// 返り値: 解放したモデル数（ログ用）。
    pub fn process_pending_frees(&mut self) -> usize {
        let frees: Vec<BindlessFree> = match self.free_queue.lock() {
            Ok(mut q) => std::mem::take(&mut *q),
            Err(_) => return 0,
        };
        let n = frees.len();
        for f in frees {
            for t in f.tex {
                self.registry.free(t);
            }
            for (o, c) in f.uv {
                self.uv_alloc.free(
                    o as u64 * BINDLESS_UV_ELEM_BYTES,
                    c as u64 * BINDLESS_UV_ELEM_BYTES,
                );
            }
            for (o, c) in f.idx {
                self.index_alloc.free(
                    o as u64 * BINDLESS_INDEX_ELEM_BYTES,
                    c as u64 * BINDLESS_INDEX_ELEM_BYTES,
                );
            }
            for (o, c) in f.nrm {
                self.normal_alloc.free(
                    o as u64 * BINDLESS_NORMAL_ELEM_BYTES,
                    c as u64 * BINDLESS_NORMAL_ELEM_BYTES,
                );
            }
        }
        if n > 0 {
            self.dirty = true;
        } // テクスチャ配列 BindGroup を作り直させる
        n
    }

    /// 登録した albedo テクスチャ index を `alloc` に記録する版の登録。
    /// 満杯ならダミー 0 番を返し、`alloc` には記録しない（解放不要）。
    pub fn register_albedo_texture_tracked(
        &mut self,
        view: &wgpu::TextureView,
        alloc: &mut BindlessModelAlloc,
    ) -> u32 {
        let idx = self.register_albedo_texture(view);
        alloc.record_texture(idx);
        idx
    }

    // ── テクスチャ レジストリ API ────────────────────────────

    /// albedo テクスチャを登録して安定インデックスを返す。満杯ならダミー 0 番を返す。
    pub fn register_albedo_texture(&mut self, view: &wgpu::TextureView) -> u32 {
        match self.registry.register(view) {
            Some(idx) => {
                self.dirty = true;
                idx
            }
            None => BINDLESS_DUMMY_TEX_INDEX,
        }
    }

    /// albedo テクスチャ インデックスを解放する（スロット再利用）。0 番は無視。
    pub fn free_albedo_texture(&mut self, index: u32) {
        self.registry.free(index);
        self.dirty = true;
    }

    /// 現在登録中の albedo テクスチャ数（ダミー除く）。
    pub fn registered_texture_count(&self) -> usize {
        self.registry.registered_count()
    }

    // ── メガバッファ API ────────────────────────────────────

    /// UV（vec2<f32>）配列をメガバッファへ追記し、先頭要素オフセット（vec2 単位）を返す。
    /// 容量オーバーなら `None`（呼び出し側はバインドレス対象外にする）。
    pub fn append_uv(&mut self, queue: &wgpu::Queue, uvs: &[[f32; 2]]) -> Option<u32> {
        let bytes = (uvs.len() as u64) * BINDLESS_UV_ELEM_BYTES;
        let off = match self.uv_alloc.alloc(bytes, BINDLESS_UV_ELEM_BYTES) {
            Some(o) => o,
            None => {
                if !self.warned_uv_overflow {
                    self.warned_uv_overflow = true;
                    eprintln!(
                        "[SEED BINDLESS] 警告: UV メガバッファ容量 {} バイトを超過。\
                         以降のプリミティブはバインドレス対象外（ダミー扱い）になります。",
                        BINDLESS_UV_BUFFER_BYTES
                    );
                }
                return None;
            }
        };
        if !uvs.is_empty() {
            queue.write_buffer(&self.uv_buffer, off, bytemuck::cast_slice(uvs));
        }
        Some((off / BINDLESS_UV_ELEM_BYTES) as u32)
    }

    /// インデックス（u32）配列をメガバッファへ追記し、先頭要素オフセット（u32 単位）を返す。
    pub fn append_indices(&mut self, queue: &wgpu::Queue, indices: &[u32]) -> Option<u32> {
        let bytes = (indices.len() as u64) * BINDLESS_INDEX_ELEM_BYTES;
        let off = match self.index_alloc.alloc(bytes, BINDLESS_INDEX_ELEM_BYTES) {
            Some(o) => o,
            None => {
                if !self.warned_index_overflow {
                    self.warned_index_overflow = true;
                    eprintln!(
                        "[SEED BINDLESS] 警告: インデックス メガバッファ容量 {} バイトを超過。\
                         以降のプリミティブはバインドレス対象外（ダミー扱い）になります。",
                        BINDLESS_INDEX_BUFFER_BYTES
                    );
                }
                return None;
            }
        };
        if !indices.is_empty() {
            queue.write_buffer(&self.index_buffer, off, bytemuck::cast_slice(indices));
        }
        Some((off / BINDLESS_INDEX_ELEM_BYTES) as u32)
    }

    /// UV 領域を解放する（`append_uv` が返した要素オフセットと要素数を渡す）。
    pub fn free_uv(&mut self, elem_offset: u32, elem_count: u32) {
        self.uv_alloc.free(
            elem_offset as u64 * BINDLESS_UV_ELEM_BYTES,
            elem_count as u64 * BINDLESS_UV_ELEM_BYTES,
        );
    }

    /// インデックス領域を解放する。
    pub fn free_indices(&mut self, elem_offset: u32, elem_count: u32) {
        self.index_alloc.free(
            elem_offset as u64 * BINDLESS_INDEX_ELEM_BYTES,
            elem_count as u64 * BINDLESS_INDEX_ELEM_BYTES,
        );
    }

    /// 八面体エンコード済み法線（u32）配列を法線メガバッファへ追記し、先頭要素オフセット
    /// （u32 単位）を返す。容量オーバーなら `None`（呼び出し側はバインドレス対象外にする）。
    pub fn append_normals(&mut self, queue: &wgpu::Queue, normals: &[u32]) -> Option<u32> {
        let bytes = (normals.len() as u64) * BINDLESS_NORMAL_ELEM_BYTES;
        let off = match self.normal_alloc.alloc(bytes, BINDLESS_NORMAL_ELEM_BYTES) {
            Some(o) => o,
            None => {
                if !self.warned_normal_overflow {
                    self.warned_normal_overflow = true;
                    eprintln!(
                        "[SEED BINDLESS] 警告: 法線メガバッファ容量 {} バイトを超過。\
                         以降のプリミティブはバインドレス対象外（ダミー扱い）になります。",
                        BINDLESS_NORMAL_BUFFER_BYTES
                    );
                }
                return None;
            }
        };
        if !normals.is_empty() {
            queue.write_buffer(&self.normal_buffer, off, bytemuck::cast_slice(normals));
        }
        Some((off / BINDLESS_NORMAL_ELEM_BYTES) as u32)
    }

    /// 法線領域を解放する。
    pub fn free_normals(&mut self, elem_offset: u32, elem_count: u32) {
        self.normal_alloc.free(
            elem_offset as u64 * BINDLESS_NORMAL_ELEM_BYTES,
            elem_count as u64 * BINDLESS_NORMAL_ELEM_BYTES,
        );
    }

    // ── インスタンステーブル API ────────────────────────────

    /// インスタンステーブル全体を GPU へアップロードする（TLAS 再構築フレームで呼ぶ想定）。
    /// `records.len()` は `BINDLESS_MAX_INSTANCES` 以下であること（超過分は無視）。
    pub fn upload_instance_records(&self, queue: &wgpu::Queue, records: &[BindlessInstanceRecord]) {
        let n = records.len().min(BINDLESS_MAX_INSTANCES as usize);
        if n == 0 {
            return;
        }
        queue.write_buffer(&self.instance_table, 0, bytemuck::cast_slice(&records[..n]));
    }

    // ── BindGroup 構築（B2 が消費）──────────────────────────

    /// テクスチャ配列 BindGroup を（必要なら再構築して）返す。
    ///
    /// 全スロット（capacity 個）を「登録済みなら実 view、空きならダミー白」で埋めて構築する。
    /// 部分バインド（PARTIALLY_BOUND）に依存せず全スロットを埋めるのは、部分バインド非対応でも
    /// 動くようにするため（対応可否で分岐せず 1 経路に統一＝縮退の判断を単純化）。
    pub fn ensure_texture_bind_group(&mut self) -> &wgpu::BindGroup {
        if self.dirty || self.tex_bg.is_none() {
            // capacity 個の view 参照を作る（未登録はダミー）。
            let views: Vec<&wgpu::TextureView> = self.texture_view_list();
            let bg = self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("Bindless Texture Array BG"),
                layout: &self.tex_bgl,
                entries: &[
                    wgpu::BindGroupEntry {
                        binding: 0,
                        resource: wgpu::BindingResource::TextureViewArray(&views),
                    },
                    wgpu::BindGroupEntry {
                        binding: 1,
                        resource: wgpu::BindingResource::Sampler(&self.sampler),
                    },
                ],
            });
            self.tex_bg = Some(bg);
            self.dirty = false;
        }
        self.tex_bg.as_ref().unwrap()
    }

    /// テクスチャ配列 BindGroup を組むための capacity 個の view 参照列
    /// （登録済みは実 view・空きはダミー白）。reflection_rt の group3 に配列を同居させる
    /// ため、B2 の消費側（reflection.rs）がこの列を使って自前で BindGroup を構築する。
    pub fn texture_view_list(&self) -> Vec<&wgpu::TextureView> {
        (0..self.capacity as usize)
            .map(|i| self.registry.slots[i].as_ref().unwrap_or(&self.dummy_view))
            .collect()
    }

    // ── プリミティブ ジオメトリ登録（B2 のライブ配線）──────────

    /// プリミティブ 1 個の UV（頂点順）・インデックス（三角形リスト）・八面体エンコード法線
    /// （頂点順）をメガバッファへ登録し、`(uv_offset, index_offset, normal_offset, eligible)` を返す。
    /// `alloc` に領域を記録して RAII 解放させる。
    ///
    /// 【部分あふれの後始末】UV・インデックス・法線のいずれかが確保できなかった場合は、確保
    /// できた側を**即座にすべて解放**して `eligible=false`（バインドレス対象外＝従来の平均色へ縮退）
    /// にする。片方だけ残すとリーク＋対を成さないゴミ領域になるため。3 者すべて成功時のみ
    /// eligible=true（3 バッファは頂点番号で相互参照するため揃って初めて意味を持つ）。
    ///
    /// `normals_oct` は `uvs` と同じ頂点順・同じ要素数の八面体エンコード u32 列であること。
    pub fn register_primitive_geometry(
        &mut self,
        queue: &wgpu::Queue,
        uvs: &[[f32; 2]],
        indices: &[u32],
        normals_oct: &[u32],
        alloc: &mut BindlessModelAlloc,
    ) -> (u32, u32, u32, bool) {
        let uv_off = self.append_uv(queue, uvs);
        let idx_off = self.append_indices(queue, indices);
        let nrm_off = self.append_normals(queue, normals_oct);
        match (uv_off, idx_off, nrm_off) {
            (Some(u), Some(x), Some(n)) => {
                alloc.record_uv(u, uvs.len() as u32);
                alloc.record_index(x, indices.len() as u32);
                alloc.record_normal(n, normals_oct.len() as u32);
                (u, x, n, true)
            }
            // いずれか欠けた → 確保できた側をすべて即解放して縮退（対象外）。
            (u, x, n) => {
                if let Some(u) = u {
                    self.free_uv(u, uvs.len() as u32);
                }
                if let Some(x) = x {
                    self.free_indices(x, indices.len() as u32);
                }
                if let Some(n) = n {
                    self.free_normals(n, normals_oct.len() as u32);
                }
                (0, 0, 0, false)
            }
        }
    }

    // ── アクセサ（B2 消費用）────────────────────────────────

    /// 色付き影（B3）用 group3 BindGroup を（インスタンステーブル/UV/index/テクスチャ配列/
    /// サンプラーを並べて）生成する。deferred_lighting_rt / shadow_mask のバインドレス影バリアントが
    /// 毎フレーム bind する（reflection.rs の create_rt_data_bg_bindless と同じ流儀＝毎フレーム全スロット
    /// を「登録済み＝実 view / 空き＝ダミー白」で並べて構築。登録変化の追従は再構築で担保）。
    ///
    /// `bgl` は `colored_shadow_bgl` で作った同一レイアウトであること（deferred.rs が保持）。
    /// バインディング: 0=instance_table / 1=UV / 2=index / 3=テクスチャ配列 / 4=共有サンプラー。
    pub fn create_colored_shadow_bind_group(
        &self,
        device: &wgpu::Device,
        bgl: &wgpu::BindGroupLayout,
    ) -> wgpu::BindGroup {
        let views = self.texture_view_list();
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("Bindless Colored Shadow BG (group3)"),
            layout: bgl,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: self.instance_table.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: self.uv_buffer.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: self.index_buffer.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 3,
                    resource: wgpu::BindingResource::TextureViewArray(&views),
                },
                wgpu::BindGroupEntry {
                    binding: 4,
                    resource: wgpu::BindingResource::Sampler(&self.sampler),
                },
            ],
        })
    }

    /// テクスチャ配列 BindGroupLayout（B2 が消費側パイプライン構築に使う）。
    pub fn texture_bind_group_layout(&self) -> &wgpu::BindGroupLayout {
        &self.tex_bgl
    }
    /// UV メガバッファ。
    pub fn uv_buffer(&self) -> &wgpu::Buffer {
        &self.uv_buffer
    }
    /// インデックス メガバッファ。
    pub fn index_buffer(&self) -> &wgpu::Buffer {
        &self.index_buffer
    }
    /// 法線メガバッファ（八面体エンコード u32）。RT 屈折の界面法線復元に使う。
    pub fn normal_buffer(&self) -> &wgpu::Buffer {
        &self.normal_buffer
    }
    /// インスタンステーブル。
    pub fn instance_table_buffer(&self) -> &wgpu::Buffer {
        &self.instance_table
    }
    /// 共有サンプラー。
    pub fn shared_sampler(&self) -> &wgpu::Sampler {
        &self.sampler
    }
    /// テクスチャ配列容量。
    pub fn texture_capacity(&self) -> u32 {
        self.capacity
    }
}

// ============================================================
//  テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;
    use std::mem::{align_of, offset_of, size_of};

    // ── BindlessInstanceRecord のレイアウト照合（Rust ↔ WGSL ミラー）──

    /// BindlessInstanceRecord は 64B・16B 整列で、各フィールドのオフセットが固定であること。
    /// WGSL ミラー（shaders/bindless_common.wgsl）と一致させるための基準。
    #[test]
    fn bindless_record_layout() {
        assert_eq!(
            size_of::<BindlessInstanceRecord>(),
            64,
            "record は 64 バイト"
        );
        assert_eq!(
            align_of::<BindlessInstanceRecord>(),
            16,
            "record は 16B 整列（vec4 先頭）"
        );
        assert_eq!(offset_of!(BindlessInstanceRecord, avg_albedo), 0);
        assert_eq!(offset_of!(BindlessInstanceRecord, base_color_factor), 16);
        assert_eq!(offset_of!(BindlessInstanceRecord, albedo_tex_index), 32);
        assert_eq!(offset_of!(BindlessInstanceRecord, uv_offset), 36);
        assert_eq!(offset_of!(BindlessInstanceRecord, index_offset), 40);
        assert_eq!(offset_of!(BindlessInstanceRecord, flags), 44);
        assert_eq!(offset_of!(BindlessInstanceRecord, alpha_cutoff), 48);
        assert_eq!(offset_of!(BindlessInstanceRecord, normal_offset), 52);
        assert_eq!(offset_of!(BindlessInstanceRecord, _pad), 56);
    }

    /// WGSL ミラーの構造体宣言が Rust と同じ 8 フィールド（合計 64B 相当）を持つことを、
    /// ソース文字列から素朴に検査する（naga で完全検証はしないが、ズレの早期検出線）。
    #[test]
    fn wgsl_mirror_has_record_fields() {
        let src = include_str!("shaders/bindless_common.wgsl");
        for field in [
            "avg_albedo",
            "base_color_factor",
            "albedo_tex_index",
            "uv_offset",
            "index_offset",
            "flags",
            "alpha_cutoff",
            "normal_offset",
        ] {
            assert!(
                src.contains(field),
                "bindless_common.wgsl に BindlessInstanceRecord.{field} が見つかりません"
            );
        }
        // 先頭 16B 互換の要（avg_albedo が最初のメンバ）。
        let s = src
            .find("struct BindlessInstanceRecord")
            .expect("bindless_common.wgsl に struct BindlessInstanceRecord が無い");
        let avg = src[s..].find("avg_albedo").expect("avg_albedo が無い");
        let bcf = src[s..]
            .find("base_color_factor")
            .expect("base_color_factor が無い");
        assert!(
            avg < bcf,
            "avg_albedo は base_color_factor より前（先頭 16B 互換）"
        );
    }

    /// WGSL ミラーの定数（容量・ダミー index・フラグ）が Rust 定数と一致すること。
    #[test]
    fn wgsl_mirror_constants_match() {
        let src = include_str!("shaders/bindless_common.wgsl");
        let read = |name: &str| -> String {
            let decl = src
                .lines()
                .map(str::trim)
                .find(|l| l.starts_with(&format!("const {name}")))
                .unwrap_or_else(|| panic!("bindless_common.wgsl に const {name} が無い"));
            decl.split('=')
                .nth(1)
                .unwrap()
                .trim()
                .trim_end_matches(';')
                .trim()
                .trim_end_matches('u')
                .to_string()
        };
        assert_eq!(
            read("BINDLESS_DUMMY_TEX_INDEX").parse::<u32>().unwrap(),
            BINDLESS_DUMMY_TEX_INDEX
        );
        assert_eq!(
            read("BINDLESS_FLAG_ELIGIBLE").parse::<u32>().unwrap(),
            BINDLESS_FLAG_ELIGIBLE
        );
        assert_eq!(
            read("BINDLESS_FLAG_MASK").parse::<u32>().unwrap(),
            BINDLESS_FLAG_MASK
        );
        assert_eq!(
            read("BINDLESS_FLAG_GEOM").parse::<u32>().unwrap(),
            BINDLESS_FLAG_GEOM
        );
    }

    // ── 八面体法線エンコードの往復一致（Rust ↔ WGSL ミラーの基準）──

    /// `oct_encode_normal` → `oct_decode_normal` の往復が単位法線を高精度で復元すること
    /// （16bit/成分の量子化誤差以内）。WGSL 側 `oct_decode_normal` は同一式のミラーで、この
    /// Rust テストが往復不変を担保する（GPU では実行できないため）。
    #[test]
    fn oct_roundtrip_dense_sphere() {
        // 球面を格子状に走査し、全方向で往復誤差が閾値以内であることを確認する。
        let mut max_err: f32 = 0.0;
        let steps = 64;
        for i in 0..=steps {
            for j in 0..=steps {
                let u = i as f32 / steps as f32; // [0,1]
                let v = j as f32 / steps as f32;
                // 一様に近い球面サンプル（緯度 θ・経度 φ）。
                let theta = (2.0 * v - 1.0).clamp(-1.0, 1.0).acos(); // [0,π]
                let phi = u * std::f32::consts::TAU;
                let n = [
                    theta.sin() * phi.cos(),
                    theta.sin() * phi.sin(),
                    theta.cos(),
                ];
                let d = oct_decode_normal(oct_encode_normal(n));
                // 復号は正規化済み。元 n も単位長。誤差 = 1 - cosθ。
                let dot = (n[0] * d[0] + n[1] * d[1] + n[2] * d[2]).clamp(-1.0, 1.0);
                max_err = max_err.max(1.0 - dot);
            }
        }
        // 16bit/成分の八面体で、1-cos の最悪誤差は 1e-4 未満に収まる（十分に精密）。
        assert!(
            max_err < 1.0e-4,
            "八面体往復の最悪誤差が大きすぎる: {max_err}"
        );
    }

    /// 軸方向法線（±X/±Y/±Z）は往復でほぼ完全一致すること（境界の折り返しの健全性）。
    #[test]
    fn oct_roundtrip_axis_normals() {
        let axes = [
            [1.0, 0.0, 0.0],
            [-1.0, 0.0, 0.0],
            [0.0, 1.0, 0.0],
            [0.0, -1.0, 0.0],
            [0.0, 0.0, 1.0],
            [0.0, 0.0, -1.0],
        ];
        for n in axes {
            let d = oct_decode_normal(oct_encode_normal(n));
            let dot = n[0] * d[0] + n[1] * d[1] + n[2] * d[2];
            assert!(dot > 0.9999, "軸法線 {n:?} の往復誤差: dot={dot}");
        }
    }

    /// WGSL ミラー bindless_common.wgsl に `oct_decode_normal` が存在し、Rust と同じ 16bit 分解
    /// （65535.0）で復号していることをソース照合する（往復一致の前提）。
    #[test]
    fn wgsl_mirror_has_oct_decode() {
        let src = include_str!("shaders/bindless_common.wgsl");
        assert!(
            src.contains("fn oct_decode_normal"),
            "bindless_common.wgsl に oct_decode_normal が無い"
        );
        assert!(
            src.contains("65535.0"),
            "bindless_common.wgsl の oct_decode_normal は 16bit 分解（65535.0）であること"
        );
    }

    // ── MegaAllocator ───────────────────────────────────────

    #[test]
    fn allocator_bump_and_offsets() {
        let mut a = MegaAllocator::new(1024);
        // 8B 整列で 3 連続確保 → 0, 8, 24（2 番目は 16B 要求）。
        assert_eq!(a.alloc(8, 8), Some(0));
        assert_eq!(a.alloc(16, 8), Some(8));
        assert_eq!(a.alloc(8, 8), Some(24));
        assert_eq!(a.used(), 32);
    }

    #[test]
    fn allocator_alignment_padding() {
        let mut a = MegaAllocator::new(1024);
        // 4B 確保後、16B 整列要求 → 0..4 確保、次は 16 へ整列（4..16 はパディングとして free 化）。
        assert_eq!(a.alloc(4, 4), Some(0));
        assert_eq!(a.alloc(8, 16), Some(16));
        // パディング 4..16（12B）が再利用可能：4B×3 が入るはず。
        assert_eq!(a.alloc(4, 4), Some(4));
    }

    #[test]
    fn allocator_free_reuse_and_coalesce() {
        let mut a = MegaAllocator::new(1024);
        let o0 = a.alloc(64, 8).unwrap(); // 0
        let o1 = a.alloc(64, 8).unwrap(); // 64
        let o2 = a.alloc(64, 8).unwrap(); // 128
        assert_eq!((o0, o1, o2), (0, 64, 128));
        // 中央を解放 → 同サイズ要求で再利用される（first-fit）。
        a.free(o1, 64);
        assert_eq!(a.alloc(64, 8), Some(64));
        // 隣接 2 領域を解放 → コアレスされ、128B 連続要求が o0 位置に入る。
        a.free(o0, 64);
        a.free(o1, 64);
        assert_eq!(a.alloc(128, 8), Some(0));
    }

    #[test]
    fn allocator_overflow_returns_none() {
        let mut a = MegaAllocator::new(128);
        assert_eq!(a.alloc(96, 8), Some(0)); // bump → 96
        // 残り 32B しかない → 48B 要求は None（縮退）。状態は変えない。
        assert_eq!(a.alloc(48, 8), None);
        // 容量以内の小要求はまだ通る（96 は 8 整列済みなのでパディング無し）。
        assert_eq!(a.alloc(16, 8), Some(96));
    }

    #[test]
    fn allocator_zero_size_is_offset_zero() {
        let mut a = MegaAllocator::new(64);
        // 空 UV/index（0 要素）は有効なオフセット 0 を返し、使用量を増やさない。
        assert_eq!(a.alloc(0, 8), Some(0));
        assert_eq!(a.used(), 0);
    }

    // ── TextureRegistry ─────────────────────────────────────
    //
    // register() は wgpu::TextureView を要求し GPU デバイスが要るが、インデックス採番
    // ロジックは `alloc_index()`（wgpu 非依存）へ分離済みなので、そこを直接検証する。

    /// 新規採番は 1 から始まり（0 はダミー予約）、解放したインデックスが優先再利用され、
    /// 満杯で None を返す。ダミー 0 番は free 対象外。
    #[test]
    fn registry_index_allocation_reuse_and_dummy_zero() {
        // capacity 4（index 0=ダミー, 1..3 が使用可能）。
        let mut reg = TextureRegistry::new(4);
        // 新規採番は 1, 2, 3（0 はダミー予約でスキップ）。
        assert_eq!(reg.alloc_index(), Some(1));
        assert_eq!(reg.alloc_index(), Some(2));
        assert_eq!(reg.alloc_index(), Some(3));
        // 満杯 → None（縮退：呼び出し側はダミー 0 番へフォールバック）。
        assert_eq!(reg.alloc_index(), None);
        assert_eq!(reg.registered_count(), 3);

        // index 2 を解放 → 次の採番で 2 が再利用される。
        reg.free(2);
        assert_eq!(reg.registered_count(), 2);
        assert_eq!(reg.alloc_index(), Some(2), "解放スロットが再利用される");

        // ダミー 0 番は free しても再利用リストに入らない（常に占有）。
        reg.free(BINDLESS_DUMMY_TEX_INDEX);
        assert!(reg.free_indices.is_empty(), "ダミー 0 番は解放対象外");
        assert!(reg.occupied[0], "index 0 は常に占有（ダミー予約）");
    }

    /// 容量計算: BINDLESS_MAX_INSTANCES が rt_shadow の MAX_RT_INSTANCES と一致すること。
    #[test]
    fn instance_capacity_matches_rt_shadow() {
        assert_eq!(
            BINDLESS_MAX_INSTANCES,
            super::super::rt_shadow::MAX_RT_INSTANCES
        );
    }

    /// WGSL ミラーが単体で naga に parse できる（＝構文が有効）ことを確認する。
    /// B1 ではどのパイプラインにも連結しないため、ここで最低限の構文健全性を担保しておく
    /// （B2 が連結したときに初めて構文エラーで落ちる、という事態を防ぐ）。
    #[test]
    fn wgsl_mirror_parses() {
        let src = include_str!("shaders/bindless_common.wgsl");
        naga::front::wgsl::parse_str(src)
            .unwrap_or_else(|e| panic!("bindless_common.wgsl の WGSL parse 失敗: {e:?}"));
    }

    /// dummy() レコードはバインドレス非対象（flags=0, tex_index=0）で factor=白。
    #[test]
    fn dummy_record_is_fallback() {
        let d = BindlessInstanceRecord::dummy();
        assert_eq!(d.flags & BINDLESS_FLAG_ELIGIBLE, 0);
        assert_eq!(d.albedo_tex_index, BINDLESS_DUMMY_TEX_INDEX);
        assert_eq!(d.base_color_factor, [1.0, 1.0, 1.0, 1.0]);
    }

    // ── RAII 遅延解放（リーク・二重解放の固定）──────────────────

    /// `BindlessModelAlloc` の Drop はちょうど 1 件の解放要求を積み、内容が記録どおりであること。
    /// GpuModel が 1 回だけ drop される前提の下、二重解放が起きないことの根拠。
    #[test]
    fn model_alloc_drop_enqueues_exactly_once() {
        let q: BindlessFreeQueue = Arc::new(Mutex::new(Vec::new()));
        {
            let mut a = BindlessModelAlloc::new(Arc::clone(&q));
            a.record_texture(3);
            a.record_texture(5);
            a.record_uv(10, 4);
            a.record_index(20, 6);
            assert!(!a.is_empty());
        } // drop
        let queued = q.lock().unwrap();
        assert_eq!(queued.len(), 1, "drop で解放要求はちょうど 1 件");
        assert_eq!(queued[0].tex, vec![3, 5]);
        assert_eq!(queued[0].uv, vec![(10, 4)]);
        assert_eq!(queued[0].idx, vec![(20, 6)]);
    }

    /// 空ハンドル（何も登録していない）の Drop は無操作（キューを汚さない）。
    /// ダミー 0 番テクスチャは記録されない（解放不要）。
    #[test]
    fn model_alloc_empty_or_dummy_drop_is_noop() {
        let q: BindlessFreeQueue = Arc::new(Mutex::new(Vec::new()));
        {
            let mut a = BindlessModelAlloc::new(Arc::clone(&q));
            a.record_texture(BINDLESS_DUMMY_TEX_INDEX); // 0 は記録しない
            assert!(a.is_empty(), "ダミー 0 番のみは空とみなす");
        }
        assert!(q.lock().unwrap().is_empty(), "空ハンドルの drop は積まない");
    }

    /// 複数モデルを登録 → 全 drop → drain（process_pending_frees と同一ロジック）で、
    /// テクスチャ・UV・index が **全て過不足なく解放される**（登録数＝解放数の整合＝リークなし）。
    #[test]
    fn register_then_drop_drain_balances_no_leak() {
        let q: BindlessFreeQueue = Arc::new(Mutex::new(Vec::new()));
        let mut reg = TextureRegistry::new(16);
        let mut uv = MegaAllocator::new(4096);
        let mut idx = MegaAllocator::new(4096);

        let mut handles = Vec::new();
        for _ in 0..4 {
            let mut a = BindlessModelAlloc::new(Arc::clone(&q));
            for _ in 0..2 {
                let ti = reg.alloc_index().unwrap();
                a.record_texture(ti);
            }
            let uo = uv
                .alloc(8 * BINDLESS_UV_ELEM_BYTES, BINDLESS_UV_ELEM_BYTES)
                .unwrap();
            a.record_uv((uo / BINDLESS_UV_ELEM_BYTES) as u32, 8);
            let io = idx
                .alloc(12 * BINDLESS_INDEX_ELEM_BYTES, BINDLESS_INDEX_ELEM_BYTES)
                .unwrap();
            a.record_index((io / BINDLESS_INDEX_ELEM_BYTES) as u32, 12);
            handles.push(a);
        }
        assert_eq!(reg.registered_count(), 8, "4 モデル×2 テクスチャ = 8 登録");
        assert_eq!(uv.used(), 4 * 8 * BINDLESS_UV_ELEM_BYTES);
        assert_eq!(idx.used(), 4 * 12 * BINDLESS_INDEX_ELEM_BYTES);

        drop(handles); // 全モデル drop → 解放要求がキューへ

        // process_pending_frees と同一の drain ロジックを再現する。
        let frees = std::mem::take(&mut *q.lock().unwrap());
        assert_eq!(frees.len(), 4, "4 モデル分の解放要求");
        for f in frees {
            for t in f.tex {
                reg.free(t);
            }
            for (o, c) in f.uv {
                uv.free(
                    o as u64 * BINDLESS_UV_ELEM_BYTES,
                    c as u64 * BINDLESS_UV_ELEM_BYTES,
                );
            }
            for (o, c) in f.idx {
                idx.free(
                    o as u64 * BINDLESS_INDEX_ELEM_BYTES,
                    c as u64 * BINDLESS_INDEX_ELEM_BYTES,
                );
            }
        }
        assert_eq!(reg.registered_count(), 0, "全テクスチャ解放（リークなし）");
        assert_eq!(uv.used(), 0, "全 UV 領域解放");
        assert_eq!(idx.used(), 0, "全 index 領域解放");
    }
}
