// ============================================================
//  terrain/scatter/generate.rs — 決定的なルール自動散布・ブラシ散布・再接地
//
//  【責務】
//    密度場（SDF）の表面へプロップを散布するアルゴリズム層。
//    ECS / GPU / ファイル IO への依存を持たないため、密度場とレイヤ重みへの
//    アクセスは `ScatterField` トレイト越しに抽象化してある。
//
//  【決定性が最優先である理由】
//    散布結果はチャンク単位で .tscatter へ保存されるが、未保存のチャンクは
//    実行時に再生成される。もし生成が非決定的だと「保存済みチャンクと
//    未保存チャンクで草の生え方が違う」「起動のたびに草原が変わる」という
//    再現不能な見た目バグになる。
//    そのため本モジュールは以下を厳守する:
//      * 乱数は必ず `hash_seed(...)` で決まる per-セルの独立ストリーム
//        （セル同士に順序依存が無いので、将来並列化しても結果が変わらない）
//      * 走査順は固定の入れ子ループ（z 外側・x 内側）
//      * std のハッシャは使わない（`DefaultHasher` はリリース間で安定しない）
//
//  【密度の規約（settings.rs より）】
//    density < iso_level ⇒ SOLID（地中）
//    density > iso_level ⇒ AIR  （空中）
//
//  【法線の符号規約 — 検証済み】
//    密度は「外へ行くほど増える」ので、勾配 ∇density は **外向き** を指す。
//    したがって外向き法線は `+normalize(∇density)` である（負勾配ではない）。
//    検証: 平坦地面 density(p) = p.y に対し ∇density = (0, 1, 0) となり、
//    +勾配 = +Y が正しい外向き（上向き）法線になる。負勾配だと -Y＝地中向き
//    になってしまう。これは marching_cubes.rs の `gradient_normal`
//    （「勾配は密度増加方向 = 外向き」）とも一致する規約である。
// ============================================================

use std::f32::consts::TAU;

use super::super::chunk_coord::ChunkCoord;
use super::super::settings::TerrainSettings;
use super::props::TerrainPropSet;
use super::tscatter::ScatterInstance;

// ─── 表面探索のパラメータ（マジックナンバー禁止のため定数化）───────────────

/// 表面探索の 1 ステップ幅（voxel_size に対する比率）。
///
/// 1.0（＝1 ボクセル刻み）だと、1 ボクセル未満の薄い床や庇を跨いで
/// 見落とすことがある。半ボクセル刻みにして取りこぼしを減らす。
const SURFACE_MARCH_STEP_FRACTION: f32 = 0.5;

/// AIR→SOLID 区間を挟み込んだ後の二分探索回数。
///
/// 8 回で区間は 1/256 に縮む。step = 0.25m（voxel 0.5m）なら
/// 約 1mm 精度となり、草の接地ズレは目視できない。
const SURFACE_BISECT_ITERS: u32 = 8;

/// 法線を求める中心差分の刻み幅（voxel_size に対する比率）。
///
/// 小さすぎると f32 の桁落ちで法線が荒れ、大きすぎると細部が丸まる。
/// 半ボクセルが両者のバランス点。
const NORMAL_GRADIENT_EPS_FRACTION: f32 = 0.5;

/// 勾配長がこの値以下なら「勾配なし」とみなして真上を向く。
///
/// 完全に平坦な密度（空洞の中など）で 0 除算しないための保険。
const NORMAL_GRADIENT_EPSILON: f32 = 1.0e-12;

/// 勾配が縮退したときのフォールバック法線（真上）。
const NORMAL_FALLBACK_UP: [f32; 3] = [0.0, 1.0, 0.0];

// ─── 散布グリッドのパラメータ ───────────────────────────────────────────────

/// 1 チャンク 1 軸あたりの散布グリッド分割数の上限。
///
/// 候補点は分割数の 2 乗で増え、1 点ごとに表面探索（数十回の密度サンプル）が
/// 走る。128 分割 = 16,384 候補/チャンク/プロップが 1 チャンク生成の実用上限。
/// props.json に極端な density を書かれてもここで頭打ちになる
/// （＝密度指定が上限に張り付くと実効密度は指定より低くなる。仕様として許容）。
pub const MAX_SCATTER_GRID_PER_AXIS: u32 = 128;

/// ブラシ散布で「重なりすぎ」と判定する最小間隔の係数。
///
/// 目標密度 D の平均点間隔は 1/√D なので、その `MIN_INSTANCE_SPACING_FACTOR`
/// 倍より近い同種インスタンスがあれば候補を捨てる。
/// 1.0 にすると格子状に整列して不自然なので、平均間隔よりかなり短く取る。
pub const MIN_INSTANCE_SPACING_FACTOR: f32 = 0.45;

/// 密度が 0 以下のときに使う最小密度（1 m² あたり）。
///
/// density=0 だと 1/√D が発散するため、下限を切って安全側に倒す。
const MIN_EFFECTIVE_DENSITY: f32 = 1.0e-4;

// ─── splitmix64 の混合定数（出典: Steele et al. "Fast Splittable PRNGs"）────

/// splitmix64 の状態増分（黄金比 φ の 64bit 固定小数表現）。
const SPLITMIX_GAMMA: u64 = 0x9E37_79B9_7F4A_7C15;
/// splitmix64 の第 1 乗算定数。
const SPLITMIX_MUL1: u64 = 0xBF58_476D_1CE4_E5B9;
/// splitmix64 の第 2 乗算定数。
const SPLITMIX_MUL2: u64 = 0x94D0_49BB_1331_11EB;
/// splitmix64 の第 1 シフト量。
const SPLITMIX_SHIFT1: u32 = 30;
/// splitmix64 の第 2 シフト量。
const SPLITMIX_SHIFT2: u32 = 27;
/// splitmix64 の最終シフト量。
const SPLITMIX_SHIFT3: u32 = 31;

// ─── hash_seed の混合定数（座標の各成分を別の奇数で撹拌する）────────────────

/// チャンク座標 x に掛ける奇数。
const SEED_MIX_COORD_X: u64 = 0x9E37_79B1_85EB_CA87;
/// チャンク座標 y に掛ける奇数。
const SEED_MIX_COORD_Y: u64 = 0xC2B2_AE3D_27D4_EB4F;
/// チャンク座標 z に掛ける奇数。
const SEED_MIX_COORD_Z: u64 = 0x1656_67B1_9E37_79F9;
/// プロップ添字に掛ける奇数。
const SEED_MIX_PROP: u64 = 0xD6E8_FEB8_6659_FD93;
/// セル添字に掛ける奇数。
const SEED_MIX_CELL: u64 = 0xA076_1D64_78BD_642F;

/// u32 乱数を [0,1) の f32 へ写すときの仮数ビット数。
///
/// f32 の仮数は 24 bit なので、上位 24 bit だけを使えば
/// 「取り得る値がすべて等確率」かつ 1.0 を絶対に含まない写像になる。
const F32_MANTISSA_BITS: u32 = 24;
/// 上記に対応する除数（2^24）。
const F32_MANTISSA_SCALE: f32 = (1u32 << F32_MANTISSA_BITS) as f32;

// ============================================================
//  ScatterField — 密度場とレイヤ重みへの読み取り専用アクセス
// ============================================================

/// 密度場（SDF）への読み取り専用アクセス。
///
/// エンジン層が `HashMap<ChunkCoord, TerrainChunkData>` の上に実装することで、
/// 本モジュールはチャンク管理・ファイル IO を一切知らずに済む。
pub trait ScatterField {
    /// 地形設定（ボクセルサイズ・チャンク分割・iso_level）を返す。
    fn settings(&self) -> &TerrainSettings;

    /// ワールド座標の密度（トライリニア補間）。density < iso_level = SOLID。
    ///
    /// チャンクが存在しない領域は「空（AIR）」相当の値を返すこと
    /// （そうしないと未生成領域の境界に草が生えてしまう）。
    fn density_at(&self, p: [f32; 3]) -> f32;

    /// ワールド座標におけるレイヤ名 → 重み（0..1）。ルール判定に使う。
    ///
    /// 未知のレイヤ名には 0 を返すこと（条件不成立になる）。
    fn layer_weight_at(&self, p: [f32; 3], layer: &str) -> f32;
}

// ============================================================
//  ScatterRng — 決定的な擬似乱数（splitmix64）
// ============================================================

/// splitmix64 ベースの決定的な擬似乱数生成器。
///
/// `std::collections::hash_map::DefaultHasher` を使わないのは、その出力が
/// **Rust のリリース間で安定することを保証されていない** ためである。
/// コンパイラを上げただけで既存セーブと草の生え方が変わるのは許容できない。
/// splitmix64 は仕様が固定された 8 行のアルゴリズムなので、この実装が
/// 存在する限り出力は未来永劫同一である。
#[derive(Clone, Debug)]
pub struct ScatterRng {
    /// 内部状態。next のたびに SPLITMIX_GAMMA だけ進む。
    state: u64,
}

impl ScatterRng {
    /// 任意の 64bit 値から生成器を作る。
    ///
    /// シードが 0 でも壊れない（splitmix64 は状態 0 から正常に動く）。
    pub fn new(seed: u64) -> Self {
        Self { state: seed }
    }

    /// 次の 64bit 乱数を返す（splitmix64 本体）。
    pub fn next_u64(&mut self) -> u64 {
        self.state = self.state.wrapping_add(SPLITMIX_GAMMA);
        let mut z = self.state;
        z = (z ^ (z >> SPLITMIX_SHIFT1)).wrapping_mul(SPLITMIX_MUL1);
        z = (z ^ (z >> SPLITMIX_SHIFT2)).wrapping_mul(SPLITMIX_MUL2);
        z ^ (z >> SPLITMIX_SHIFT3)
    }

    /// 次の 32bit 乱数を返す（64bit の上位半分を使う＝下位ビットの偏りを避ける）。
    pub fn next_u32(&mut self) -> u32 {
        (self.next_u64() >> 32) as u32
    }

    /// 次の乱数を [0, 1) の f32 で返す（1.0 は決して返さない）。
    pub fn next_f32(&mut self) -> f32 {
        // 上位 24 bit（f32 の仮数幅）だけを使う。
        let bits = self.next_u32() >> (32 - F32_MANTISSA_BITS);
        bits as f32 / F32_MANTISSA_SCALE
    }

    /// 次の乱数を [lo, hi) の f32 で返す（lo > hi のときは lo を返す）。
    pub fn next_range(&mut self, lo: f32, hi: f32) -> f32 {
        if hi <= lo {
            // 乱数ストリームの消費数を分岐で変えない（決定性のため必ず 1 回引く）。
            let _ = self.next_f32();
            return lo;
        }
        lo + (hi - lo) * self.next_f32()
    }
}

/// 「グローバルシード × チャンク座標 × プロップ添字 × セル添字」から
/// そのセル専用の乱数シードを作る整数ミキサ。
///
/// 各成分を別々の奇数で乗算してから XOR し、最後に splitmix64 の
/// finalizer（雪崩関数）を 1 回通す。奇数乗算は 2^64 上の全単射なので
/// 情報が失われず、finalizer が下位ビットの偏りを潰す。
///
/// セルごとに独立したストリームを作るのが要点で、これにより
/// 「どの順でセルを処理しても各セルの乱数列が変わらない」＝
/// 並列化しても結果がビット単位で同一、という決定性が保証される。
pub fn hash_seed(global_seed: u64, coord: ChunkCoord, prop_index: usize, cell_index: usize) -> u64 {
    // i32 → u64 は符号拡張（as i64 経由）してから撹拌する。
    // 直接 `as u64` すると負座標が巨大値に化けるが全単射性は保たれるので
    // どちらでもよい。可読性のため符号拡張を明示する。
    let mut h = global_seed;
    h ^= (coord.x as i64 as u64).wrapping_mul(SEED_MIX_COORD_X);
    h ^= (coord.y as i64 as u64).wrapping_mul(SEED_MIX_COORD_Y);
    h ^= (coord.z as i64 as u64).wrapping_mul(SEED_MIX_COORD_Z);
    h ^= (prop_index as u64).wrapping_mul(SEED_MIX_PROP);
    h ^= (cell_index as u64).wrapping_mul(SEED_MIX_CELL);

    // splitmix64 の finalizer で雪崩させる。
    let mut z = h;
    z = (z ^ (z >> SPLITMIX_SHIFT1)).wrapping_mul(SPLITMIX_MUL1);
    z = (z ^ (z >> SPLITMIX_SHIFT2)).wrapping_mul(SPLITMIX_MUL2);
    z ^ (z >> SPLITMIX_SHIFT3)
}

// ============================================================
//  表面探索
// ============================================================

/// 指定 XZ の柱を上から下へ辿り、最初の AIR→SOLID 遷移（＝地表）を返す。
///
/// - `y_top` / `y_bottom`: 探索する Y 範囲（y_top > y_bottom であること）。
/// - 戻り値: `Some((ワールド接地点, 外向き単位法線))`。地表が無ければ `None`。
///
/// 「上から下へ」なのは、洞窟のように上下に複数の面がある地形で
/// **一番上の床** に生やしたいため（下から探すと洞窟の底が選ばれる）。
///
/// 探索開始点がすでに SOLID（地中）の場合、そこは AIR→SOLID 遷移ではないので
/// 採用しない。地中から抜けて再び地面に入る面が下にあればそれを拾う。
pub fn surface_hit_down(
    field: &impl ScatterField,
    x: f32,
    z: f32,
    y_top: f32,
    y_bottom: f32,
) -> Option<([f32; 3], [f32; 3])> {
    let settings = field.settings();
    let iso = settings.iso_level;
    let step = SURFACE_MARCH_STEP_FRACTION * settings.voxel_size;

    // ─── 刻み幅が非正なら無限ループになるので防御（設定破損時の保険）───
    if !(step > 0.0) || !(y_top > y_bottom) {
        return None;
    }

    // ─── 開始点の状態を取る（solid = density < iso）───
    let mut prev_y = y_top;
    let mut prev_solid = field.density_at([x, prev_y, z]) < iso;

    // ─── 半ボクセル刻みで下降し、AIR→SOLID の跨ぎを探す ───
    let mut y = y_top - step;
    loop {
        // 最下端を跨いだら y_bottom に丸めて最後の 1 回だけ評価する。
        let cur_y = if y < y_bottom { y_bottom } else { y };
        let cur_solid = field.density_at([x, cur_y, z]) < iso;

        if !prev_solid && cur_solid {
            // ─── 挟み込み成功。二分探索で iso 交差点へ寄せる ───
            //   不変条件: air_y は AIR 側、solid_y は SOLID 側。
            let mut air_y = prev_y;
            let mut solid_y = cur_y;
            for _ in 0..SURFACE_BISECT_ITERS {
                let mid = 0.5 * (air_y + solid_y);
                if field.density_at([x, mid, z]) < iso {
                    solid_y = mid;
                } else {
                    air_y = mid;
                }
            }
            // 交差点は挟み込んだ区間の中点とする（誤差は区間幅の半分以下）。
            let hit = [x, 0.5 * (air_y + solid_y), z];
            return Some((hit, surface_normal(field, hit)));
        }

        // 最下端まで見終わったら終了。
        if cur_y <= y_bottom {
            return None;
        }
        prev_y = cur_y;
        prev_solid = cur_solid;
        y -= step;
    }
}

/// ワールド座標 `p` における密度場の外向き単位法線。
///
/// 中心差分で勾配を取り、**そのまま**（符号反転せずに）正規化する。
/// 密度は外側ほど大きいので勾配自体が外向きだからである
/// （ファイル冒頭の「法線の符号規約」を参照）。
fn surface_normal(field: &impl ScatterField, p: [f32; 3]) -> [f32; 3] {
    let h = NORMAL_GRADIENT_EPS_FRACTION * field.settings().voxel_size;

    // ─── 各軸の偏微分（中心差分）───
    let dx = field.density_at([p[0] + h, p[1], p[2]]) - field.density_at([p[0] - h, p[1], p[2]]);
    let dy = field.density_at([p[0], p[1] + h, p[2]]) - field.density_at([p[0], p[1] - h, p[2]]);
    let dz = field.density_at([p[0], p[1], p[2] + h]) - field.density_at([p[0], p[1], p[2] - h]);

    // ─── 正規化。勾配が縮退していたら真上を向く ───
    let len_sq = dx * dx + dy * dy + dz * dz;
    if len_sq <= NORMAL_GRADIENT_EPSILON {
        return NORMAL_FALLBACK_UP;
    }
    let inv = 1.0 / len_sq.sqrt();
    [dx * inv, dy * inv, dz * inv]
}

/// 法線（単位ベクトル）から斜度（度）を求める。
///
/// 上向き = 0 度、水平方向 = 90 度。`|n.y|` を使うことで天井（下向き法線）も
/// 同じ斜度として扱う（layers.rs の `rule_weights_all_into` と同じ流儀）。
fn slope_degrees(normal: [f32; 3]) -> f32 {
    normal[1].abs().clamp(0.0, 1.0).acos().to_degrees()
}

// ============================================================
//  ルール自動散布
// ============================================================

/// 1 チャンク分のルール自動散布を行う。
///
/// - `prop_indices`: 散布対象のプロップ添字（props.props の添字）。範囲外は無視。
/// - `global_seed` : シーン全体の散布シード。これを変えると草原全体が生え変わる。
///
/// アルゴリズムは「ジッタ付きグリッドサンプリング」。チャンクの XZ 面を
/// 等分割し、各セルにランダム位置の候補を 1 点だけ置く。純粋な一様乱数だと
/// 点が固まって禿げとダマができるが、この方式なら偏りなく散る。
///
/// 【決定性】同じ (global_seed, coord, prop 定義, 密度場) なら、返る Vec は
/// 要素順まで含めてビット単位で同一になる（走査は prop → z → x の固定順）。
pub fn scatter_chunk_by_rules(
    field: &impl ScatterField,
    props: &TerrainPropSet,
    prop_indices: &[usize],
    coord: ChunkCoord,
    global_seed: u64,
) -> Vec<ScatterInstance> {
    let settings = field.settings();
    let extent = settings.chunk_extent();
    let origin = coord.world_origin(settings);
    let mut out: Vec<ScatterInstance> = Vec::new();

    // ─── チャンクの Y 探索範囲（このチャンクが占める高さぶん）───
    let y_top = origin[1] + extent;
    let y_bottom = origin[1];

    for &prop_index in prop_indices {
        // 範囲外の添字は黙って飛ばす（エンジン層の指定ミスで落とさない）。
        let Some(prop) = props.props.get(prop_index) else {
            continue;
        };

        // ─── 密度からグリッド分割数を決める（上限でクランプ）───
        let cells = grid_cells_per_axis(prop.scatter.density, extent);
        let cell_size = extent / cells as f32;

        // ─── z 外側・x 内側の固定順で走査する（並べ替えない＝順序が決定的）───
        for cz in 0..cells {
            for cx in 0..cells {
                let cell_index = (cz * cells + cx) as usize;
                let mut rng =
                    ScatterRng::new(hash_seed(global_seed, coord, prop_index, cell_index));

                // ─── セル内のジッタ位置に候補点を置く ───
                let jx = rng.next_f32();
                let jz = rng.next_f32();
                let x = origin[0] + (cx as f32 + jx) * cell_size;
                let z = origin[2] + (cz as f32 + jz) * cell_size;

                // ─── 柱を上から辿って接地点を探す ───
                let Some((hit, normal)) = surface_hit_down(field, x, z, y_top, y_bottom) else {
                    continue;
                };

                // ─── ルール評価（斜度・高度・レイヤ重み）───
                let slope = slope_degrees(normal);
                let probability = prop
                    .rule
                    .evaluate(slope, hit[1], &|layer| field.layer_weight_at(hit, layer));

                // ─── 確率判定。next_f32 は [0,1) なので probability=1 は必ず通る ───
                if !(rng.next_f32() < probability) {
                    continue;
                }

                out.push(make_instance(&mut rng, prop, prop_index, hit, normal));
            }
        }
    }

    out
}

/// 目標密度（1 m² あたり）とチャンク幅から、1 軸あたりのグリッド分割数を求める。
///
/// セル総数 = cells² が目標本数 density * extent² 以上になるよう
/// `ceil(sqrt(density) * extent)` を取り、上限でクランプする。
fn grid_cells_per_axis(density: f32, extent: f32) -> u32 {
    // NaN / 負値は最小密度に落とす（clamp は NaN でパニックするため先に潰す）。
    let d = if density.is_finite() && density > 0.0 {
        density
    } else {
        MIN_EFFECTIVE_DENSITY
    };
    let raw = (d.sqrt() * extent).ceil();
    // f32 → u32 の飽和変換（負値は 0、巨大値は u32::MAX）。
    (raw as u32).clamp(1, MAX_SCATTER_GRID_PER_AXIS)
}

/// 接地点と法線から 1 インスタンスを組み立てる（姿勢の乱数はここで消費する）。
///
/// 乱数の消費順は yaw → scale → tilt角 → tilt方位 → seed で固定する。
/// この順序を変えると既存シーンの草が全て生え変わるため、変更禁止。
fn make_instance(
    rng: &mut ScatterRng,
    prop: &super::props::TerrainProp,
    prop_index: usize,
    hit: [f32; 3],
    surface_normal: [f32; 3],
) -> ScatterInstance {
    let sp = &prop.scatter;

    // ─── Y 軸まわりのランダム回転 ───
    //   random_yaw=false でも乱数は 1 回引く（消費数を分岐させない）。
    let yaw_raw = rng.next_f32();
    let yaw = if sp.random_yaw { yaw_raw * TAU } else { 0.0 };

    // ─── スケール ───
    let scale = rng.next_range(sp.scale_min, sp.scale_max);

    // ─── 基準軸: 地表法線に沿わせるか、常に真上か ───
    let base = if sp.align_to_normal {
        surface_normal
    } else {
        NORMAL_FALLBACK_UP
    };

    // ─── ランダム傾き（基準軸から tilt_max_deg までランダムに倒す）───
    let tilt_amount = rng.next_f32();
    let tilt_azimuth = rng.next_f32() * TAU;
    let tilt_rad = (sp.tilt_max_deg.max(0.0) * tilt_amount).to_radians();
    let normal = tilt_axis(base, tilt_rad, tilt_azimuth);

    ScatterInstance {
        pos: hit,
        normal,
        yaw,
        scale,
        prop_id: prop_index as u32,
        seed: rng.next_u32(),
    }
}

/// 単位ベクトル `axis` を、それに直交する方向へ `angle` ラジアンだけ倒す。
///
/// `azimuth` は「どちら向きに倒すか」を axis まわりの角度で指定する。
/// axis に直交する正規直交基底 (t, b) を作り、
/// `axis*cos(angle) + (t*cos(azimuth) + b*sin(azimuth))*sin(angle)` を返す。
fn tilt_axis(axis: [f32; 3], angle: f32, azimuth: f32) -> [f32; 3] {
    if angle == 0.0 {
        return axis;
    }
    // ─── axis と平行でない参照ベクトルを選ぶ（|y| が大きいなら X 軸を使う）───
    //   平行なベクトルと外積を取ると 0 ベクトルになり基底が作れないため。
    let reference = if axis[1].abs() > AXIS_PARALLEL_THRESHOLD {
        [1.0, 0.0, 0.0]
    } else {
        [0.0, 1.0, 0.0]
    };
    let t = normalize_or(cross(reference, axis), [1.0, 0.0, 0.0]);
    let b = normalize_or(cross(axis, t), [0.0, 0.0, 1.0]);

    let (sa, ca) = angle.sin_cos();
    let (sz, cz) = azimuth.sin_cos();
    let dir = [
        t[0] * cz + b[0] * sz,
        t[1] * cz + b[1] * sz,
        t[2] * cz + b[2] * sz,
    ];
    normalize_or(
        [
            axis[0] * ca + dir[0] * sa,
            axis[1] * ca + dir[1] * sa,
            axis[2] * ca + dir[2] * sa,
        ],
        NORMAL_FALLBACK_UP,
    )
}

/// 基底構築で「軸が Y と平行すぎる」と判定する |y| のしきい値。
///
/// これを超えたら参照ベクトルを X 軸へ切り替える（外積の縮退回避）。
const AXIS_PARALLEL_THRESHOLD: f32 = 0.9;

/// 3 次元外積。
#[inline]
fn cross(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    ]
}

/// 正規化。長さが縮退していたら `fallback` を返す。
#[inline]
fn normalize_or(v: [f32; 3], fallback: [f32; 3]) -> [f32; 3] {
    let len_sq = v[0] * v[0] + v[1] * v[1] + v[2] * v[2];
    if len_sq <= NORMAL_GRADIENT_EPSILON {
        return fallback;
    }
    let inv = 1.0 / len_sq.sqrt();
    [v[0] * inv, v[1] * inv, v[2] * inv]
}

// ============================================================
//  ブラシ散布
// ============================================================

/// ワールド空間の球ブラシで散布インスタンスを追加／消去する。
///
/// - `center` / `radius`: ブラシの中心と半径（XZ 距離で判定する）。
/// - `density`          : 追加時の目標密度（1 m² あたり）。
/// - `erase`            : true なら消去、false なら追加。
/// - `seed`             : ブラシストロークのシード。
/// - 戻り値             : `existing` が変化したか。
///
/// 【消去の意味論（設計判断）】
///   erase=true は **半径内のすべてのプロップ** を消す（prop_index で絞らない）。
///   消しゴムは「見えているものが消える」のが直感に沿うためで、特定種だけを
///   残したいケースより、消したのに別種が残る驚きを避けることを優先した。
///
/// 【追加はルールを見ない（設計判断）】
///   ブラシはユーザーの明示的な意思なので、`ScatterRule` の斜度／高度／レイヤ
///   条件は適用しない。ルールで生えない場所にあえて生やせるのが手描きの価値。
///   ただし接地は必須なので、表面が見つからない候補は捨てる。
///
/// 【重なり防止】
///   目標密度 D の平均点間隔 1/√D の `MIN_INSTANCE_SPACING_FACTOR` 倍より近くに
///   **同種の** インスタンスがあれば候補を捨てる。同じ場所を何度もなぞっても
///   草が無限に積み上がらないようにするため（別種は共存させたいので同種のみ）。
pub fn scatter_brush(
    field: &impl ScatterField,
    props: &TerrainPropSet,
    prop_index: usize,
    existing: &mut Vec<ScatterInstance>,
    center: [f32; 3],
    radius: f32,
    density: f32,
    erase: bool,
    seed: u64,
) -> bool {
    // ─── 半径が非正なら何もしない ───
    if !(radius > 0.0) {
        return false;
    }
    let radius_sq = radius * radius;

    // ─── 消去: 半径内の全プロップを取り除く ───
    if erase {
        let before = existing.len();
        existing.retain(|inst| xz_distance_sq(inst.pos, center) >= radius_sq);
        return existing.len() != before;
    }

    // ─── 追加: 対象プロップが存在しなければ何もしない ───
    let Some(prop) = props.props.get(prop_index) else {
        return false;
    };

    // ─── ブラシの外接正方形（一辺 2r）を等分割したグリッドで候補を撒く ───
    let side = radius * 2.0;
    let cells = grid_cells_per_axis(density, side);
    let cell_size = side / cells as f32;
    let origin_x = center[0] - radius;
    let origin_z = center[2] - radius;

    // ─── Y 探索範囲。球の上端から下端まで見る ───
    let y_top = center[1] + radius;
    let y_bottom = center[1] - radius;

    // ─── 最小間隔（目標密度の平均点間隔 × 係数）───
    let effective_density = if density.is_finite() && density > 0.0 {
        density
    } else {
        MIN_EFFECTIVE_DENSITY
    };
    let min_spacing = MIN_INSTANCE_SPACING_FACTOR / effective_density.sqrt();
    let min_spacing_sq = min_spacing * min_spacing;

    // ─── ブラシ中心を 1m 格子に量子化してセルシードの空間バリエーションを作る ───
    //   ストローク中に中心が動けばシードも変わるので、同じ場所を塗り直しても
    //   同じ模様が繰り返されない。量子化なのでサブメートルの手ブレでは変わらない。
    let brush_coord = ChunkCoord::new(
        center[0].floor() as i32,
        center[1].floor() as i32,
        center[2].floor() as i32,
    );

    let mut changed = false;
    for cz in 0..cells {
        for cx in 0..cells {
            let cell_index = (cz * cells + cx) as usize;
            let mut rng = ScatterRng::new(hash_seed(seed, brush_coord, prop_index, cell_index));

            // ─── セル内のジッタ位置 ───
            let x = origin_x + (cx as f32 + rng.next_f32()) * cell_size;
            let z = origin_z + (cz as f32 + rng.next_f32()) * cell_size;

            // ─── 正方形グリッドなので、円の外へ出た候補を落とす ───
            if xz_distance_sq([x, 0.0, z], center) >= radius_sq {
                continue;
            }

            // ─── 接地点を探す ───
            let Some((hit, normal)) = surface_hit_down(field, x, z, y_top, y_bottom) else {
                continue;
            };

            // ─── 同種インスタンスとの最小間隔チェック（積み上がり防止）───
            //   直前に追加したぶんも見るため、判定は existing に対して毎回行う。
            let too_close = existing.iter().any(|inst| {
                inst.prop_id as usize == prop_index
                    && xz_distance_sq(inst.pos, hit) < min_spacing_sq
            });
            if too_close {
                continue;
            }

            existing.push(make_instance(&mut rng, prop, prop_index, hit, normal));
            changed = true;
        }
    }

    changed
}

/// 2 点間の XZ 平面上の距離の 2 乗（Y は無視する）。
#[inline]
fn xz_distance_sq(a: [f32; 3], b: [f32; 3]) -> f32 {
    let dx = a[0] - b[0];
    let dz = a[2] - b[2];
    dx * dx + dz * dz
}

// ============================================================
//  地形編集後の再接地
// ============================================================

/// 地形編集後に、既存インスタンスを新しい地表へ貼り直す。
///
/// 各インスタンスについて `pos.y + y_search` から `pos.y - y_search` まで
/// 柱を辿り直す。
///   * 見つかれば pos と normal を更新（＝盛土／掘削に追従する）
///     法線は各プロップの `align_to_normal` に従って
///     「新しい地表法線」か「真上」のどちらかへ張り替える。
///     生成時に掛かっていた **ランダム傾きは復元しない**（傾き量は
///     インスタンスに保存しておらず乱数ストリームからしか出せないため）。
///     再接地は地形編集という明示操作の直後にしか走らないので、
///     そこで傾きが揃うことは許容範囲と判断した。
///   * 見つからなければ **インスタンスを削除**（＝足元の地面が消えたので
///     宙に浮かせるより消すのが正しい）
///   * `prop_id` が props.json の範囲外になっていた場合も削除
///     （プロップ定義を減らしたときの孤児インスタンス掃除）
///
/// 生き残ったインスタンスの相対順序は保たれる（`retain_mut` を使うため）。
/// 戻り値は削除した件数。
pub fn restick_instances(
    field: &impl ScatterField,
    props: &TerrainPropSet,
    instances: &mut Vec<ScatterInstance>,
    y_search: f32,
) -> usize {
    let before = instances.len();

    instances.retain_mut(|inst| {
        // ─── 孤児インスタンス（プロップ定義が消えた）は捨てる ───
        let Some(prop) = props.props.get(inst.prop_id as usize) else {
            return false;
        };
        let align = prop.scatter.align_to_normal;
        // ─── 元の高さの上下 y_search を探索し直す ───
        match surface_hit_down(
            field,
            inst.pos[0],
            inst.pos[2],
            inst.pos[1] + y_search,
            inst.pos[1] - y_search,
        ) {
            Some((hit, normal)) => {
                inst.pos = hit;
                inst.normal = if align { normal } else { NORMAL_FALLBACK_UP };
                true
            }
            // 足元の地面が消えた → 削除。
            None => false,
        }
    });

    before - instances.len()
}
