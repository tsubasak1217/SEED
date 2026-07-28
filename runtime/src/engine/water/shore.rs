// ============================================================
//  water/shore.rs — ショアフィールド（岸波の場）のベイク（Phase W1.5）
//
//  ## 役割（単一責任）
//  水域ごとに「俯瞰 2D の岸情報」を CPU でベイクし、水面シェーダが 1 サンプルで
//  岸波を作れる形へ落とす。ここでは **流体シミュレーションを一切行わない**。
//  岸波は「岸までの距離・岸の方向・水深」という静的な場から作るプロシージャル波帯であり、
//  本モジュールはその場（= ショアフィールド）だけを担当する。
//
//  ## チャネル設計（1 テクセル = 4 成分。GPU では Rgba16Float の 1 レイヤ）
//  | 成分 | 意味                                                            |
//  |------|-----------------------------------------------------------------|
//  | x    | 水深 [m]（水面 Y − 地形 Y）。**負は陸**（地形が水面より高い）    |
//  | y    | 符号付き岸距離 [m]。**正 = 沖（水側）／負 = 陸側**               |
//  | z,w  | 岸方向（単位ベクトル XZ）。**そのテクセルから最寄りの岸を指す**   |
//
//  z,w が (0,0) のテクセルは「窓内に岸が 1 つも無い」を意味し、
//  シェーダ側は岸波の振幅 0 として扱う（＝岸の無い外洋で波帯が湧かない）。
//  ビット詰めは行わない。4 成分がちょうど 4 チャネルに収まり、
//  Rgba16Float は水深・距離のレンジ（数百 m）と近岸の精度（岸近傍で mm 級）を
//  同時に満たすため、パック／アンパックのコードを持つ理由が無い。
//
//  ## 地形高さの取得方式
//  地形は ECS ではなくボクセル SDF（`TerrainState.chunks`）が単一の真実源なので、
//  **CPU のカラム走査**で高さを求める。`terrain::scatter::generate::surface_hit_down`
//  （散布プロップの接地判定と同一関数）を使い、テクセル中心の XZ について
//  「水面より上の余白から下方向へ降り、最初に現れる AIR→SOLID 遷移」を採る。
//  レイキャスト（物理）は使わない（Play 中しかコライダーが無く、コスト もはるかに高い）。
//
//  **洞窟の割り切り**: 上から降りて最初に当たった面をその XZ の地表とみなす。
//  したがって水面より上に天井がある洞窟の内部は「天井が地表」と解釈される。
//  岸波は水際の見た目のための場であり、洞窟内の水面に岸波を出す要求は無いため
//  この割り切りで十分と判断した（散布プロップの接地判定と同じ規約でもある）。
//
//  ## 岸距離の求め方
//  水深 0 の等高線からの距離変換。解像度が 256² なので **CPU の 8SSEDT**
//  （Danielsson のベクタ距離変換・2 パス）で十分速く、GPU 化する理由が無い。
//  8SSEDT は「最寄りシードへのオフセットベクタ」を持ち回るため、
//  **距離と岸方向が同時に**得られる（岸方向のために別の勾配計算をしなくてよい）。
//  シードは水／陸の隣接ペアの間で水深を線形補間した **サブテクセル位置**に置く。
//  こうしないと距離がテクセル幅（外洋窓で 2m）刻みに量子化され、
//  岸波の位相が縞状に段付く。
//
//  ## 再ベイクのタイミング
//  毎フレームは焼かない。`update()` が「地形編集バージョン・水パラメータ・
//  （Ocean は）カメラ位置」から署名を作り、変化してから
//  `SHORE_BAKE_DEBOUNCE_SECS` 経過した時点で焼く。ブラシのドラッグ中は
//  署名が毎フレーム変わり続けるので、実際に焼かれるのは手を止めた後 1 回だけになる。
// ============================================================

use std::collections::HashMap;
use std::time::Instant;

use crate::engine::components::water_volume_component::WaterVolumeKind;
use crate::engine::terrain::scatter::generate::{ScatterField, surface_hit_down};

use super::resolved::ResolvedWaterVolume;

// ─── 場の形（エンジン定数。ユーザには露出しない）─────────────

/// ショアフィールド 1 枚の解像度（一辺のテクセル数）。
///
/// 256² は「外洋窓 512m で 2m/テクセル・池窓 64m で 0.25m/テクセル」に相当する。
/// 岸波の波長は数 m〜数十 m なので、位相の連続性にはこれで足りる
/// （距離場はサブテクセルシードとバイリニアで滑らかに補間される）。
/// 1 レイヤ = 256×256×4ch×2byte = 512KB。
pub const SHORE_FIELD_RESOLUTION: usize = 256;

/// 同時にベイクできる水域の数（＝ GPU 配列テクスチャのレイヤ数）。
///
/// これを超えた水域は岸波を持たない（従来どおりの W1/I2 の水面になる）。
/// 512KB × 8 = 4MB。水域を何十個も置くシーンで数十 MB を焼かないための上限。
pub const SHORE_FIELD_MAX_LAYERS: usize = 8;

/// Region（直方体の水塊）のベイク窓が AABB の外へ広げるマージン（m）。
///
/// 岸（水深 0 の等高線）は AABB の**外**にあることが多い（水面は AABB で切られるが
/// 地形の斜面は続いている）。マージン無しだと岸が窓に入らず距離場が作れない。
pub const SHORE_FIELD_REGION_MARGIN_M: f32 = 16.0;

/// Region のベイク窓の一辺の上限（m）。
///
/// 巨大な AABB をそのまま窓にすると 1 テクセルが粗くなりすぎ、岸線がギザつく。
/// 上限を超える水域は窓が水域より小さくなるが、岸波は水際の表現なので
/// 「中央の沖は窓外＝岸波なし」で実害が無い。
pub const SHORE_FIELD_REGION_MAX_WINDOW_M: f32 = 512.0;

/// Ocean（XZ 無限の大洋）のベイク窓の一辺（m）。カメラ追従。
pub const SHORE_FIELD_OCEAN_WINDOW_M: f32 = 512.0;

/// Ocean 窓を焼き直すカメラ移動しきい値（窓一辺に対する比）。
///
/// カメラが窓中心からこの比率×一辺だけ離れたら窓を置き直す。
/// 毎フレーム追従させるとベイクが毎フレーム走ってしまうため、
/// 「窓の 1/4 動いたら焼き直す」で間引く。
pub const SHORE_FIELD_OCEAN_RECENTER_RATIO: f32 = 0.25;

// ─── 地形カラム走査の範囲 ────────────────────────────────────

/// カラム走査の上端（水面からの高さ。m）。
///
/// これより高い陸は「水深 = −SHORE_PROBE_UP_M」に飽和する。岸波に必要なのは
/// 水深 0 の等高線の位置だけなので、高い崖の正確な標高は要らない。
pub const SHORE_PROBE_UP_M: f32 = 8.0;

/// カラム走査の下端（水面からの深さ。m）。
///
/// これより深い水は「水深 = SHORE_PROBE_DOWN_M」に飽和する。岸波の振幅は
/// 深水で 0 へ落ちるので、外洋の正確な水深も要らない。
pub const SHORE_PROBE_DOWN_M: f32 = 32.0;

// ─── 再ベイクのデバウンス ────────────────────────────────────

/// 署名が変化してから実際にベイクするまでの待ち時間（秒）。
///
/// 地形ブラシのドラッグ中は毎フレーム署名が変わる。ここで待つことで、
/// 手を止めてから 1 回だけ焼かれる（ドラッグ中に数百 ms のベイクが挟まらない）。
pub const SHORE_BAKE_DEBOUNCE_SECS: f32 = 0.3;

// ─── 内部の数値定数 ──────────────────────────────────────────

/// 距離変換の「未確定」を表す初期オフセット（テクセル単位）。
/// 解像度より十分大きければよい。
const SHORE_DT_INFINITY: f32 = 1.0e9;

/// 岸方向を単位化するときのゼロ長判定しきい値（テクセル単位の二乗長）。
const SHORE_DIR_EPSILON_SQ: f32 = 1.0e-12;

/// 水／陸の線形補間で 0 除算を避けるための下限。
const SHORE_CROSSING_EPSILON: f32 = 1.0e-6;

/// テクセル中心のオフセット（テクセル座標 → 中心へ）。
const SHORE_TEXEL_CENTER: f32 = 0.5;

/// 窓の一辺から半径を得る係数（中心 ⇔ 原点の換算）。
const SHORE_WINDOW_HALF: f32 = 0.5;

// ─── 地形の存在範囲 ──────────────────────────────────────────

/// 地形チャンクが実在するワールド範囲（AABB）。
///
/// **ベイク時間の支配項はカラム走査**（256² = 65,536 本）なので、
/// 「そもそも地形が無い XZ」と「地形が存在しない Y 帯」を先に切り落とす。
/// 外洋の窓（512m 四方）に小さな島だけがあるような典型ケースでは、
/// 走査するカラムが数 % まで減る。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ShoreTerrainBounds {
    /// 地形チャンクが存在するワールド XZ の最小。
    pub min_xz: [f32; 2],
    /// 同 最大。
    pub max_xz: [f32; 2],
    /// 地形チャンクが存在するワールド Y の最小。
    pub min_y: f32,
    /// 同 最大。
    pub max_y: f32,
}

// ─── ベイク署名 ──────────────────────────────────────────────

/// 「この入力で焼いた」を表す署名。これが変わったら焼き直す。
///
/// f32 をそのまま比較するのは、値が 1 ビットでも変われば焼き直すべきだから
/// （近似比較にする理由が無い。NaN は入らない前提で `PartialEq` を使う）。
#[derive(Clone, Copy, PartialEq, Debug)]
struct ShoreBakeSignature {
    /// 水域の種別（Ocean はカメラ追従窓・Region は固定窓）。
    kind: WaterVolumeKind,
    /// 水面のワールド Y（これが動けば水深が全部変わる）。
    surface_y: f32,
    /// ベイク窓のワールド XZ 最小。
    origin_xz: [f32; 2],
    /// ベイク窓の一辺（m）。
    extent_m: f32,
    /// 地形編集バージョン（App が編集のたびに進める単調カウンタ）。
    terrain_version: u64,
}

// ─── ベイク結果 ──────────────────────────────────────────────

/// 水域 1 個ぶんのベイク済みショアフィールド。
pub struct ShoreFieldEntry {
    /// GPU 配列テクスチャのレイヤ番号（0..SHORE_FIELD_MAX_LAYERS）。
    pub layer: u32,
    /// ベイク窓のワールド XZ 最小。
    pub origin_xz: [f32; 2],
    /// ベイク窓の一辺（m）。
    pub extent_m: f32,
    /// 場の本体（row-major。長さ = SHORE_FIELD_RESOLUTION²）。
    /// 成分の意味はファイル冒頭のチャネル設計表を参照。
    pub texels: Vec<[f32; 4]>,
    /// 焼き直しのたびに進む単調カウンタ。
    /// レンダラは「前回アップロードした版と違うか」だけをこれで見る
    /// （テクセル配列の比較をしない）。
    pub revision: u64,
    /// この結果を焼いたときの入力署名。
    signature: ShoreBakeSignature,
}

/// シーン中の全水域のショアフィールドを保持するキャッシュ。
///
/// キーは水域アクタの DFS インデックス（`ResolvedWaterVolume::actor_dfs_id`）。
/// 描画で使う ID 採番と同じものなので、フレーム間で安定して同じ水域を指す。
pub struct ShoreFieldSet {
    /// actor_dfs_id → ベイク結果。
    entries: HashMap<u32, ShoreFieldEntry>,
    /// 次に配る revision。
    next_revision: u64,
    /// 「焼き直しが必要になった時刻」。デバウンスの起点。None = 待ちなし。
    pending_since: Option<Instant>,
}

impl Default for ShoreFieldSet {
    fn default() -> Self {
        Self { entries: HashMap::new(), next_revision: 1, pending_since: None }
    }
}

impl ShoreFieldSet {
    /// 空のキャッシュを作る。
    pub fn new() -> Self { Self::default() }

    /// 焼き済みのエントリを引く（描画側がパラメータとアップロードに使う）。
    pub fn get(&self, actor_dfs_id: u32) -> Option<&ShoreFieldEntry> {
        self.entries.get(&actor_dfs_id)
    }

    /// 焼き済みエントリの反復（レンダラのアップロード用）。
    pub fn iter(&self) -> impl Iterator<Item = (&u32, &ShoreFieldEntry)> {
        self.entries.iter()
    }

    /// 1 枚も焼かれていないか。
    pub fn is_empty(&self) -> bool { self.entries.is_empty() }

    /// シーンの状態に合わせてキャッシュを更新する（必要ならベイクする）。
    ///
    /// 毎フレーム呼んでよい。実際にベイクが走るのは
    /// 「署名が変わってから `SHORE_BAKE_DEBOUNCE_SECS` 経った最初の呼び出し」だけで、
    /// それ以外のフレームは HashMap の走査だけで終わる。
    ///
    /// - `volumes`: このフレームの解決済み水域（`collect_water_volumes` の結果）。
    /// - `camera_xz`: Ocean のカメラ追従窓に使うカメラのワールド XZ。
    /// - `field`: 地形密度場（`ScatterField`。散布の接地判定と同一のサンプラ）。
    /// - `bounds`: 地形チャンクの実在範囲。**`None`（地形なし）なら岸は定義できない**ので、
    ///   キャッシュを空にして即座に戻る（地形の無いシーンではベイクが 1 度も走らない）。
    /// - `terrain_version`: 地形編集の単調バージョン（App が編集のたびに進める）。
    /// - `now`: 現在時刻（デバウンス判定。壁時計なので Edit / Play を問わない）。
    pub fn update<F: ScatterField + Sync>(
        &mut self,
        volumes:         &[ResolvedWaterVolume],
        camera_xz:       [f32; 2],
        field:           &F,
        bounds:          Option<ShoreTerrainBounds>,
        terrain_version: u64,
        now:             Instant,
    ) {
        // ── ⓪ 地形が無ければ岸も無い。焼かずに捨てる ──
        let Some(bounds) = bounds else {
            self.entries.clear();
            self.pending_since = None;
            return;
        };

        // ── ① 岸波を使う水域だけを、レイヤ上限まで拾う ──
        //     strength <= 0 の水域は焼かない（＝ユーザが切れば CPU コストも 0）。
        //     川（Spline。W4）も焼かない。岸波は「岸へ寄せるうねり」であって
        //     川面に出すものではなく、川の窓は AABB では表せない（細長い折れ線）ため、
        //     Region 用の正方窓を当てると無関係な広域を焼くだけになる。
        let targets: Vec<&ResolvedWaterVolume> = volumes
            .iter()
            .filter(|v| v.visual.shore_wave_strength > 0.0
                && v.kind != WaterVolumeKind::Spline)
            .take(SHORE_FIELD_MAX_LAYERS)
            .collect();

        // ── ② 消えた水域のエントリを捨てる（レイヤを空ける）──
        self.entries.retain(|id, _| targets.iter().any(|v| v.actor_dfs_id == *id));

        // ── ③ 各水域の「今あるべき署名」を求め、焼き直しが要るかを判定する ──
        //     Ocean の窓中心は、既存エントリの窓からしきい値以上離れたときだけ動かす
        //     （毎フレーム追従させるとベイクが止まらなくなる）。
        let mut wanted: Vec<(u32, ShoreBakeSignature)> = Vec::with_capacity(targets.len());
        let mut needs_bake = false;
        for v in &targets {
            let current = self.entries.get(&v.actor_dfs_id);
            let sig = desired_signature(v, camera_xz, terrain_version, current);
            if current.map(|e| e.signature) != Some(sig) {
                needs_bake = true;
            }
            wanted.push((v.actor_dfs_id, sig));
        }

        // ── ④ デバウンス。要求が続いている間は焼かず、静まってから 1 回だけ焼く ──
        if !needs_bake {
            self.pending_since = None;
            return;
        }
        let since = *self.pending_since.get_or_insert(now);
        if now.duration_since(since).as_secs_f32() < SHORE_BAKE_DEBOUNCE_SECS {
            return;
        }
        self.pending_since = None;

        // ── ⑤ ベイク本体。レイヤ番号は「今回の対象列の並び順」で配り直す ──
        //     水域の増減でレイヤがずれるが、パラメータ側もこの直後に作り直されるため
        //     ずれたまま描かれるフレームは発生しない。
        for (layer, (id, sig)) in wanted.into_iter().enumerate() {
            // 署名が一致しているエントリはそのまま（レイヤ番号だけ付け替える）。
            if let Some(existing) = self.entries.get_mut(&id) {
                if existing.signature == sig {
                    if existing.layer != layer as u32 {
                        existing.layer = layer as u32;
                        // レイヤが変わったらアップロードし直す必要がある。
                        existing.revision = self.next_revision;
                        self.next_revision += 1;
                    }
                    continue;
                }
            }
            let texels = bake_shore_field(
                field, bounds, sig.origin_xz, sig.extent_m, sig.surface_y);
            let revision = self.next_revision;
            self.next_revision += 1;
            self.entries.insert(id, ShoreFieldEntry {
                layer: layer as u32,
                origin_xz: sig.origin_xz,
                extent_m:  sig.extent_m,
                texels,
                revision,
                signature: sig,
            });
        }
    }
}

// ─── 窓の決定 ────────────────────────────────────────────────

/// 水域 1 個の「今あるべきベイク署名（＝窓と入力）」を求める。
///
/// `current` は既存エントリ。Ocean のカメラ追従で「まだ動かさなくてよい」判定に使う。
fn desired_signature(
    v:               &ResolvedWaterVolume,
    camera_xz:       [f32; 2],
    terrain_version: u64,
    current:         Option<&ShoreFieldEntry>,
) -> ShoreBakeSignature {
    let (origin_xz, extent_m) = match v.kind {
        // Ocean: カメラ追従の固定サイズ窓。既存窓の中心からしきい値以内なら据え置く。
        WaterVolumeKind::Ocean => {
            let extent = SHORE_FIELD_OCEAN_WINDOW_M;
            let threshold = extent * SHORE_FIELD_OCEAN_RECENTER_RATIO;
            let keep = current.and_then(|e| {
                let cx = e.origin_xz[0] + e.extent_m * SHORE_WINDOW_HALF;
                let cz = e.origin_xz[1] + e.extent_m * SHORE_WINDOW_HALF;
                let moved = ((camera_xz[0] - cx).powi(2) + (camera_xz[1] - cz).powi(2)).sqrt();
                // 窓サイズが変わっていない かつ 移動がしきい値以内なら既存窓を維持。
                if e.extent_m == extent && moved <= threshold { Some(e.origin_xz) } else { None }
            });
            let origin = keep.unwrap_or_else(|| snap_window_origin(
                [camera_xz[0] - extent * SHORE_WINDOW_HALF,
                 camera_xz[1] - extent * SHORE_WINDOW_HALF],
                extent,
            ));
            (origin, extent)
        }
        // Region / Spline: AABB を覆う固定窓（マージン込み・正方）。
        _ => {
            let span = (v.half_extents[0].max(v.half_extents[2]) + SHORE_FIELD_REGION_MARGIN_M) * 2.0;
            let extent = span.min(SHORE_FIELD_REGION_MAX_WINDOW_M);
            let origin = [
                v.center[0] - extent * SHORE_WINDOW_HALF,
                v.center[2] - extent * SHORE_WINDOW_HALF,
            ];
            (origin, extent)
        }
    };
    ShoreBakeSignature {
        kind: v.kind,
        surface_y: v.surface_y,
        origin_xz,
        extent_m,
        terrain_version,
    }
}

/// 窓原点をテクセル境界へスナップする。
///
/// Ocean の窓はカメラ移動で置き直されるので、原点がテクセル格子に乗っていないと
/// 同じワールド地点のサンプル位置が焼き直しのたびに微妙にずれ、
/// 岸波の位相が「窓を置き直した瞬間」に跳ねて見える。
/// インタラクションフィールド（I1）の `snap_window_origin` と同じ考え方。
fn snap_window_origin(origin: [f32; 2], extent: f32) -> [f32; 2] {
    let texel = extent / SHORE_FIELD_RESOLUTION as f32;
    if !(texel > 0.0) { return origin; }
    [(origin[0] / texel).floor() * texel, (origin[1] / texel).floor() * texel]
}

// ─── ベイク本体 ──────────────────────────────────────────────

/// ショアフィールドを 1 枚焼く。
///
/// 手順は「① 水深グリッド（地形カラム走査）→ ② 水深 0 等高線のシード →
/// ③ 8SSEDT で距離＋方向 → ④ 4 成分へ詰める」。
///
/// 戻り値は row-major・長さ `SHORE_FIELD_RESOLUTION²` のテクセル配列。
pub fn bake_shore_field<F: ScatterField + Sync>(
    field:     &F,
    bounds:    ShoreTerrainBounds,
    origin_xz: [f32; 2],
    extent_m:  f32,
    surface_y: f32,
) -> Vec<[f32; 4]> {
    let res   = SHORE_FIELD_RESOLUTION;
    let texel = extent_m / res as f32;

    // ── ① 水深グリッド ──
    let depth = bake_depth_grid(field, bounds, origin_xz, texel, surface_y);

    // ── ②③ 符号付き距離と岸方向（8SSEDT）──
    let offsets = signed_distance_transform(&depth, res);

    // ── ④ 4 成分へ詰める ──
    let mut out = vec![[0.0f32; 4]; res * res];
    for i in 0..res * res {
        let d  = depth[i];
        let off = offsets[i];
        let len_sq = off[0] * off[0] + off[1] * off[1];
        if len_sq >= SHORE_DT_INFINITY {
            // 窓内に岸が 1 つも無い。方向 0 で「岸情報なし」を表す
            //（シェーダはこれを見て岸波の振幅を 0 にする）。
            out[i] = [d, SHORE_PROBE_DOWN_M, 0.0, 0.0];
            continue;
        }
        let len = len_sq.sqrt();
        // 距離はテクセル単位 → m。符号は「水側が正・陸側が負」。
        let signed = len * texel * if d >= 0.0 { 1.0 } else { -1.0 };
        // 方向はテクセルから最寄り岸へ向かう単位ベクトル。
        let (dx, dz) = if len_sq > SHORE_DIR_EPSILON_SQ {
            (off[0] / len, off[1] / len)
        } else {
            // 岸のテクセルそのもの。方向が縮退するので、
            // 「岸情報あり」を保ちつつ勾配が出ない値（0）にはできないため、
            // 距離 0 の点は振幅も 0 付近になることを利用して +X を仮置きする。
            (1.0, 0.0)
        };
        out[i] = [d, signed, dx, dz];
    }
    out
}

/// 水深グリッド（水面 Y − 地形 Y）を焼く。負は陸。
///
/// テクセルごとに地形のカラム走査を行うため、ここがベイク時間の支配項である
/// （256² = 65,536 カラム）。行単位で rayon 並列化する。
fn bake_depth_grid<F: ScatterField + Sync>(
    field:     &F,
    bounds:    ShoreTerrainBounds,
    origin_xz: [f32; 2],
    texel:     f32,
    surface_y: f32,
) -> Vec<f32> {
    use rayon::prelude::*;

    let res = SHORE_FIELD_RESOLUTION;
    let iso = field.settings().iso_level;

    // ── 走査 Y 範囲を「水面まわりの関心帯」と「地形が実在する帯」の共通部分に狭める ──
    //   地形の上端より上・下端より下は必ず AIR（`read_global` が density_clamp を返す）なので、
    //   走査しても無駄でしかない。カラムあたりのサンプル数がそのまま減る。
    let y_top    = (surface_y + SHORE_PROBE_UP_M).min(bounds.max_y);
    let y_bottom = (surface_y - SHORE_PROBE_DOWN_M).max(bounds.min_y);

    let mut depth = vec![0.0f32; res * res];

    // 走査帯が潰れている＝この水面の周りに地形が全く無い。
    //   ・地形が水面より完全に下 → 全面「深い水」
    //   ・地形が水面より完全に上 → 全面「陸」
    if !(y_top > y_bottom) {
        let all = if bounds.min_y >= surface_y { -SHORE_PROBE_UP_M } else { SHORE_PROBE_DOWN_M };
        depth.fill(all);
        return depth;
    }

    depth
        .par_chunks_mut(res)
        .enumerate()
        .for_each(|(row, out_row)| {
            let z = origin_xz[1] + (row as f32 + SHORE_TEXEL_CENTER) * texel;
            // 行ごと（Z 方向）の早期棄却。地形の外なら 1 行まるごと「深い水」。
            if z < bounds.min_xz[1] || z > bounds.max_xz[1] {
                out_row.fill(SHORE_PROBE_DOWN_M);
                return;
            }
            for (col, slot) in out_row.iter_mut().enumerate() {
                let x = origin_xz[0] + (col as f32 + SHORE_TEXEL_CENTER) * texel;
                // 列（X 方向）の早期棄却。ここを通る列だけがカラム走査を払う。
                if x < bounds.min_xz[0] || x > bounds.max_xz[0] {
                    *slot = SHORE_PROBE_DOWN_M;
                    continue;
                }
                *slot = column_depth(field, iso, x, z, y_top, y_bottom, surface_y);
            }
        });
    depth
}

/// 1 カラムぶんの水深を求める（水面 Y − 地形 Y。負は陸）。
///
/// 走査は `surface_hit_down`（散布の接地判定と同一関数）に任せる。
/// 別実装にすると「草は生えているのに岸波が陸に乗る」ようなずれが出る。
fn column_depth<F: ScatterField>(
    field:     &F,
    iso:       f32,
    x:         f32,
    z:         f32,
    y_top:     f32,
    y_bottom:  f32,
    surface_y: f32,
) -> f32 {
    if let Some((hit, _n)) = surface_hit_down(field, x, z, y_top, y_bottom) {
        return surface_y - hit[1];
    }
    // ヒット無しには 2 通りある。走査開始点の状態で区別する:
    //   ・開始点が既に SOLID … 走査範囲の全体が地中＝ y_top より高い陸
    //   ・開始点が AIR       … 走査範囲に地表が無い＝ y_bottom より深い水（or 地形なし）
    if field.density_at([x, y_top, z]) < iso {
        -SHORE_PROBE_UP_M
    } else {
        SHORE_PROBE_DOWN_M
    }
}

// ─── 8SSEDT（ベクタ距離変換）─────────────────────────────────

/// 水深グリッドから「各テクセル → 最寄りの岸（水深 0 等高線）」のオフセットベクタを求める。
///
/// 返すのはテクセル単位のオフセット `[dx, dz]`。岸が 1 つも無いテクセルは
/// 長さの二乗が `SHORE_DT_INFINITY` 以上になる。
///
/// アルゴリズムは Danielsson の 8SSEDT（2 パス・各パスで前方/後方の近傍を伝播）。
/// 厳密なユークリッド距離ではないが、誤差は数学的に「稀に 1 テクセル未満」であり、
/// 岸波の位相にとっては完全に無視できる。
fn signed_distance_transform(depth: &[f32], res: usize) -> Vec<[f32; 2]> {
    // ── 初期化: 岸に隣接するテクセルへサブテクセル位置のシードを置く ──
    let inf = [SHORE_DT_INFINITY, SHORE_DT_INFINITY];
    let mut grid = vec![inf; res * res];
    for row in 0..res {
        for col in 0..res {
            let i  = row * res + col;
            let d0 = depth[i];
            let mut best = inf;
            let mut best_len = SHORE_DT_INFINITY;
            // 4 近傍のうち「符号が反対」の相手との間に岸がある。
            let neighbors: [(i32, i32); 4] = [(-1, 0), (1, 0), (0, -1), (0, 1)];
            for (dc, dr) in neighbors {
                let c = col as i32 + dc;
                let r = row as i32 + dr;
                if c < 0 || r < 0 || c >= res as i32 || r >= res as i32 { continue; }
                let d1 = depth[r as usize * res + c as usize];
                // 同符号（両方水 or 両方陸）なら、この向きに岸は無い。
                if (d0 >= 0.0) == (d1 >= 0.0) { continue; }
                // 水深を線形補間して 0 になる位置（0..1 のテクセル比）。
                let denom = d0 - d1;
                let t = if denom.abs() < SHORE_CROSSING_EPSILON {
                    SHORE_TEXEL_CENTER
                } else {
                    (d0 / denom).clamp(0.0, 1.0)
                };
                let off = [dc as f32 * t, dr as f32 * t];
                let len = off[0] * off[0] + off[1] * off[1];
                if len < best_len { best_len = len; best = off; }
            }
            grid[i] = best;
        }
    }

    // ── 伝播パス ──
    // 前方パス: 上の行と左、後方パス: 下の行と右。
    // 「隣のテクセルが知っている最寄りシード」へ、自分からの相対オフセットで乗り換える。
    //
    // 【オフセットの向きの規約（ここを間違えると岸波が沖へ逃げる）】
    //   grid[i] は「テクセル i **から** 最寄りシードへ向かうベクタ」である。
    //   隣 `ni` の知識を借りるとき、
    //     (シード − 自分) = (シード − 隣) + (隣 − 自分) = grid[ni] + (隣 − 自分)
    //   なので、渡す差分は **「隣の座標 − 自分の座標」** でなければならない。
    //   例: ni が左隣（col−1）なら差分は (−1, 0)。
    //   符号を逆にしても距離の大きさは対称なので正しく見えるが、
    //   岸方向だけが 180° 反転する（＝波が岸から離れていく）。
    let compare = |grid: &mut Vec<[f32; 2]>, i: usize, ni: usize, dx: f32, dz: f32| {
        let cand = [grid[ni][0] + dx, grid[ni][1] + dz];
        let cl = cand[0] * cand[0] + cand[1] * cand[1];
        let cur = grid[i];
        if cl < cur[0] * cur[0] + cur[1] * cur[1] { grid[i] = cand; }
    };

    // 前方（row 昇順 / col 昇順 → col 降順の 2 段）
    for row in 0..res {
        for col in 0..res {
            let i = row * res + col;
            if row > 0 {
                compare(&mut grid, i, i - res, 0.0, -1.0);
                if col > 0       { compare(&mut grid, i, i - res - 1, -1.0, -1.0); }
                if col + 1 < res { compare(&mut grid, i, i - res + 1, 1.0, -1.0); }
            }
            if col > 0 { compare(&mut grid, i, i - 1, -1.0, 0.0); }
        }
        for col in (0..res.saturating_sub(1)).rev() {
            let i = row * res + col;
            compare(&mut grid, i, i + 1, 1.0, 0.0);
        }
    }
    // 後方（row 降順 / col 降順 → col 昇順の 2 段）
    for row in (0..res).rev() {
        for col in (0..res).rev() {
            let i = row * res + col;
            if row + 1 < res {
                compare(&mut grid, i, i + res, 0.0, 1.0);
                if col > 0       { compare(&mut grid, i, i + res - 1, -1.0, 1.0); }
                if col + 1 < res { compare(&mut grid, i, i + res + 1, 1.0, 1.0); }
            }
            if col + 1 < res { compare(&mut grid, i, i + 1, 1.0, 0.0); }
        }
        for col in 1..res {
            let i = row * res + col;
            compare(&mut grid, i, i - 1, -1.0, 0.0);
        }
    }
    grid
}

// ─── ユニットテスト ──────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::terrain::settings::TerrainSettings;

    /// テスト用の「地形はどこにでもある」バウンズ（早期棄却を無効化して走査経路を検証する）。
    fn test_bounds() -> ShoreTerrainBounds {
        ShoreTerrainBounds {
            min_xz: [-1.0e4, -1.0e4], max_xz: [1.0e4, 1.0e4],
            min_y:  -1.0e4,           max_y:  1.0e4,
        }
    }

    /// テスト用の解析地形。`height(x, z)` が返す高さの平面を地表とする。
    ///
    /// 密度規約は `density(p) = p.y − height` （density < iso = SOLID）。
    /// 実地形（マーチングキューブス）と同じ規約なので、走査コードの検証になる。
    struct PlaneField {
        settings: TerrainSettings,
        /// 地表高さ（x に比例する斜面を作る）。
        slope: f32,
    }

    impl ScatterField for PlaneField {
        fn settings(&self) -> &TerrainSettings { &self.settings }
        fn density_at(&self, p: [f32; 3]) -> f32 { p[1] - p[0] * self.slope }
        fn layer_weight_at(&self, _p: [f32; 3], _layer: &str) -> f32 { 0.0 }
    }

    /// 斜面地形（x が大きいほど高い）で、水深が x に対して単調減少すること。
    /// 水面 Y = 0・傾き 0.1 なら、地表 Y = 0.1x なので水深 = −0.1x。
    #[test]
    fn depth_follows_terrain_height() {
        let f = PlaneField { settings: TerrainSettings::default(), slope: 0.1 };
        // 窓: 原点 (-64, -64)・一辺 128m。テクセル 0.5m。
        let texels = bake_shore_field(&f, test_bounds(), [-64.0, -64.0], 128.0, 0.0);
        let res = SHORE_FIELD_RESOLUTION;
        // 中央行の左端（x ≒ -64・地表 -6.4）は水中、右端（x ≒ +64・地表 +6.4）は陸。
        let row = res / 2;
        let left  = texels[row * res];
        let right = texels[row * res + (res - 1)];
        assert!(left[0] > 0.0,  "左端は水中（水深 > 0）: {}", left[0]);
        assert!(right[0] < 0.0, "右端は陸（水深 < 0）: {}", right[0]);
        // 水深は走査範囲で飽和する（左端は SHORE_PROBE_DOWN_M 未満のはず）。
        assert!(left[0] <= SHORE_PROBE_DOWN_M + f32::EPSILON);
    }

    /// 岸距離は水側で正・陸側で負になり、水深 0 の位置（x = 0）付近で 0 を跨ぐこと。
    #[test]
    fn shore_distance_is_signed_and_zero_at_waterline() {
        let f = PlaneField { settings: TerrainSettings::default(), slope: 0.1 };
        let texels = bake_shore_field(&f, test_bounds(), [-64.0, -64.0], 128.0, 0.0);
        let res = SHORE_FIELD_RESOLUTION;
        let row = res / 2;
        // x = 0 は列 128 付近（origin -64・texel 0.5）。
        let mid = texels[row * res + res / 2];
        assert!(mid[1].abs() < 2.0, "水際の岸距離はほぼ 0 であること: {}", mid[1]);
        assert!(texels[row * res][1] > 0.0,             "沖側は正");
        assert!(texels[row * res + (res - 1)][1] < 0.0, "陸側は負");
    }

    /// 岸方向は「そのテクセルから岸へ向かう」単位ベクトルであること。
    /// x が大きいほど陸なので、沖（左）のテクセルの岸方向は +X を向く。
    #[test]
    fn shore_direction_points_toward_shore() {
        let f = PlaneField { settings: TerrainSettings::default(), slope: 0.1 };
        let texels = bake_shore_field(&f, test_bounds(), [-64.0, -64.0], 128.0, 0.0);
        let res = SHORE_FIELD_RESOLUTION;
        let row = res / 2;
        // 沖側（列 32 = x ≒ -48m）。
        let t = texels[row * res + 32];
        let len = (t[2] * t[2] + t[3] * t[3]).sqrt();
        assert!((len - 1.0).abs() < 1.0e-3, "単位ベクトルであること: {len}");
        assert!(t[2] > 0.9, "岸（+X 側）を向くこと: {}", t[2]);
    }

    /// 岸が窓内に無い（全面が水）とき、岸方向は 0 になること。
    /// シェーダはこれを見て岸波の振幅を 0 にするので、外洋で波帯が湧かない保証になる。
    #[test]
    fn no_shore_in_window_yields_zero_direction() {
        // 傾き 0 の平面地形を水面より十分下に置く（＝窓全体が深い水）。
        struct DeepField { settings: TerrainSettings }
        impl ScatterField for DeepField {
            fn settings(&self) -> &TerrainSettings { &self.settings }
            // 地表 Y = -1000 相当（走査範囲に地表が無い）。
            fn density_at(&self, p: [f32; 3]) -> f32 { p[1] + 1000.0 }
            fn layer_weight_at(&self, _p: [f32; 3], _l: &str) -> f32 { 0.0 }
        }
        let f = DeepField { settings: TerrainSettings::default() };
        let texels = bake_shore_field(&f, test_bounds(), [-64.0, -64.0], 128.0, 0.0);
        for t in texels.iter().take(64) {
            assert_eq!([t[2], t[3]], [0.0, 0.0], "岸が無ければ方向は 0");
        }
    }

    /// 地形の外側のカラムは **1 回も密度サンプルしない**こと（早期棄却の生存確認）。
    ///
    /// ベイク時間の支配項はカラム走査なので、ここが効かなくなると
    /// 「外洋の窓に小島がひとつ」でも 65,536 カラム全部を払うことになる。
    /// 実測（本テストの計測部）でもコストが桁で変わるため、性質としてテストで固定する。
    #[test]
    fn columns_outside_terrain_bounds_cost_no_samples() {
        use std::sync::atomic::{AtomicUsize, Ordering};

        /// 密度サンプル回数を数える地形。
        struct CountingField {
            settings: TerrainSettings,
            calls:    AtomicUsize,
        }
        impl ScatterField for CountingField {
            fn settings(&self) -> &TerrainSettings { &self.settings }
            fn density_at(&self, p: [f32; 3]) -> f32 {
                self.calls.fetch_add(1, Ordering::Relaxed);
                p[1] - p[0] * 0.1
            }
            fn layer_weight_at(&self, _p: [f32; 3], _l: &str) -> f32 { 0.0 }
        }

        // 窓は 128m 四方だが、地形は中央の 8m 四方だけに存在する。
        let f = CountingField {
            settings: TerrainSettings::default(),
            calls:    AtomicUsize::new(0),
        };
        let bounds = ShoreTerrainBounds {
            min_xz: [-4.0, -4.0], max_xz: [4.0, 4.0],
            min_y:  -8.0,          max_y:  8.0,
        };
        let _ = bake_shore_field(&f, bounds, [-64.0, -64.0], 128.0, 0.0);
        let counted = f.calls.load(Ordering::Relaxed);

        // 地形の内側は 8m/0.5m = 16 テクセル四方 ＝ 256 カラムしか走査しないはず。
        // 1 カラムあたりのサンプル数には上限があるので、全カラム走査（65,536 本）の
        // 場合とは 2 桁以上離れる。ここでは「全カラム走査していない」ことだけを固定する。
        let all_columns = SHORE_FIELD_RESOLUTION * SHORE_FIELD_RESOLUTION;
        assert!(counted < all_columns,
            "地形外カラムまで走査している（サンプル {counted} 回 ≥ カラム数 {all_columns}）");
    }


    /// 窓原点のテクセルスナップが「テクセル境界に乗る」こと。
    #[test]
    fn window_origin_snaps_to_texel_grid() {
        let extent = 512.0;
        let texel  = extent / SHORE_FIELD_RESOLUTION as f32; // 2.0m
        let o = snap_window_origin([-123.4, 77.7], extent);
        assert_eq!(o[0] % texel, 0.0, "X がテクセル境界に乗ること: {}", o[0]);
        assert_eq!(o[1] % texel, 0.0, "Z がテクセル境界に乗ること: {}", o[1]);
    }
}
