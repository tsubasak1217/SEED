// ============================================================
//  terrain/cover/brush.rs — カバー場の手編集ブラシ（純粋関数）
//
//  【責務】
//    地形編集モードの「カバー」ブラシ 1 発ぶんを、1 チャンクのカバー場へ適用する。
//    ECS も GPU も IPC も知らない。エンジン層（app/terrain_cover_brush_ops.rs）が
//    レイマーチの着弾点から `CoverBrushSpec` を組み立て、ブラシ球に触れる
//    チャンクにだけ本ファイルの関数を呼ぶ。
//
//  【積算（accumulate.rs）・スタンプ（trample.rs）と別ファイルにした理由】
//    駆動する事象が三者三様である。
//      積算   … 時間（dt）駆動。エミッタが届く全チャンクへ一様に効く
//      スタンプ … 「物が動いた」イベント駆動。接地点の周りだけに効く
//      ブラシ … 「人がドラッグした」イベント駆動。着弾点の周りだけに効く。
//               さらに **減らす方向（消しゴム）** を持つのはブラシだけである
//    早期棄却の作りも、時間の扱い（dt を掛けない）も別物なので、
//    同じファイルに同居させると条件分岐だらけの 1 本になる。
//
//  【dt を掛けない理由】
//    ブラシは「1 回押すたびに一定量」効く道具である。dt を掛けると、
//    フレームレートによって同じドラッグの結果が変わってしまう
//    （密度ブラシ・レイヤペイントブラシも同じ理由で dt を掛けていない）。
//    連続適用の速さはエディタ側の送信スロットル（40ms）が決める。
//
//  【Y 照合（上下対称でよい理由）】
//    ブラシの着弾点は地形へのレイマーチ交点であり、**必ず面の上にある**。
//    轍スタンプのように「アクタ原点が地面より上にある」ことを考えなくてよいので、
//    窓は上下対称でよい（`trample.rs` の接地スナップに相当するものは要らない）。
//    窓そのものは必要である——これが無いと、地表をなぞったブラシが
//    真下の洞窟の床に積もった雪まで消してしまう。
// ============================================================

use super::emit::CoverMask;
use super::field::{
    slope_scale, texel_center_uv, CoverField, CoverSurface, COVER_FIELD_RESOLUTION,
};
use crate::engine::terrain::brush_mask::brush_shape_factor;

// ─── 定数（マジックナンバー禁止）─────────────────────────────────────────────

/// ブラシ 1 発が動かせる量の上限（正規化量。強さ 1.0・中心テクセルでの値）。
///
/// 【なぜ強さをそのまま量にしないのか】
///   エディタは 40ms 間隔でブラシを送るため、ドラッグ 1 秒で 25 発届く。
///   強さ 1.0 をそのまま量 1.0 にすると、1 発（＝クリックした瞬間）で
///   カバーが最大量まで飛び、強度スライダーが実質「効くか効かないか」の
///   2 値になってしまう。4 発ぶん（≒0.16 秒）で満量に達する値にすると、
///   軽く撫でる／押し当て続ける、の差が手触りとして出る。
pub const COVER_BRUSH_MAX_DELTA_PER_APPLY: f32 = 0.25;

/// Y 照合の許容差（メートル）の下限。
///
/// 許容差はブラシ半径から作る（大きなブラシほど上下方向にも余裕を持たせる）が、
/// 極端に小さい半径でも「テクセル 1 個ぶんの起伏（既定 0.5m）」は許さないと、
/// でこぼこした地面でブラシが虫食いに効いてしまう。
pub const COVER_BRUSH_Y_TOLERANCE_MIN: f32 = 0.5;

/// 塗りモードで「積もらない」と判断する傾斜スケールの下限。
///
/// 積算（`accumulate_chunk`）と同じ規則を使い、
/// 「エミッタで積もる面 ⇔ 手で塗れる面」を常に一致させる
/// （手で塗ったときだけ崖に雪が張り付く、という不整合を作らない）。
const COVER_BRUSH_SLOPE_MIN: f32 = 0.0;

// ============================================================
//  CoverBrushMode — ブラシの向き（減らす / 目標量へ寄せる）
// ============================================================

/// カバーブラシの動作モード。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CoverBrushMode {
    /// 消しゴム。ブラシ範囲の量と踏み固めを減らし、消え切ったテクセルの素材を空へ戻す。
    Erase,
    /// 塗り。ブラシ範囲を指定素材の指定量（目標量）へ近づける。
    Paint,
}

// ============================================================
//  CoverBrushSpec — ブラシ 1 発ぶんの仕様
// ============================================================

/// 「地形編集モードでカバーブラシを 1 回当てた」ことを表す仕様（ワールド解決済み）。
#[derive(Clone, Debug, PartialEq)]
pub struct CoverBrushSpec {
    /// 着弾点のワールド座標（地形へのレイマーチ交点）。Y は Y 照合に使う。
    pub center: [f32; 3],
    /// ブラシ半径（メートル）。
    pub radius: f32,
    /// 強さ（0..1）。中心テクセルで `COVER_BRUSH_MAX_DELTA_PER_APPLY` 倍される。
    pub strength: f32,
    /// 動作モード（消す / 塗る）。
    pub mode: CoverBrushMode,
    /// 塗る素材の添字（`CoverMaterialSet` の添字）。消しゴムでは使わない。
    pub material_index: u8,
    /// 塗りの目標量（0..1）。消しゴムでは使わない。
    pub target_amount: f32,
}

impl CoverBrushSpec {
    /// Y 照合の許容差（メートル）。半径に比例させ、下限で底上げする。
    pub fn y_tolerance(&self) -> f32 {
        self.radius.max(COVER_BRUSH_Y_TOLERANCE_MIN)
    }

    /// このブラシが、ワールド高さ `surface_y` の面に届くか（Y 照合の本体）。
    ///
    /// 着弾点は必ず面の上なので窓は上下対称でよい（ファイル冒頭参照）。
    /// 非有限な面の高さ（壊れた派生データ）は常に false（触らない）。
    pub fn matches_surface_y(&self, surface_y: f32) -> bool {
        if !surface_y.is_finite() {
            return false;
        }
        (surface_y - self.center[1]).abs() <= self.y_tolerance()
    }

    /// このブラシが指定 AABB（チャンクなど）に一切かからないか（早期棄却）。
    pub fn is_outside_aabb(&self, min: [f32; 3], max: [f32; 3]) -> bool {
        let reach = self.radius.max(0.0);
        let y = self.y_tolerance();
        self.center[0] + reach < min[0]
            || self.center[0] - reach > max[0]
            || self.center[2] + reach < min[2]
            || self.center[2] - reach > max[2]
            || self.center[1] + y < min[1]
            || self.center[1] - y > max[1]
    }

    /// 指定ワールド XZ 位置での効き具合（0..1）。中心 1.0・縁 0.0 の smoothstep。
    ///
    /// 落ち方は轍スタンプの円形状（`CoverStampSpec::footprint_at` の `Circle`）と
    /// まったく同じ式にしてある。カバーへ作用する道具の縁の出方を揃えるためである。
    ///
    /// 形状マスクを使う場合は `falloff_at_masked` を呼ぶこと（本関数はマスク無しと等価）。
    pub fn falloff_at(&self, world_x: f32, world_z: f32) -> f32 {
        self.falloff_at_masked(world_x, world_z, None)
    }

    /// 形状マスクを考慮した効き具合（0..1）。
    ///
    /// `mask` が `None`（未指定）または無効（読み込み失敗）なら、
    /// 従来どおりの円形 smoothstep へ縮退する（`brush_mask.rs` の規約）。
    /// マスクが有効なら、ブラシ球の XZ バウンディング正方形へ貼った画像の値を返す。
    pub fn falloff_at_masked(
        &self,
        world_x: f32,
        world_z: f32,
        mask: Option<&CoverMask>,
    ) -> f32 {
        // 円形フォールオフに使う距離は従来どおり **XZ 平面上の距離**
        // （カバーは面の性質なので、Y 方向の距離は Y 照合が別途担当する）。
        let dx = world_x - self.center[0];
        let dz = world_z - self.center[2];
        let d = (dx * dx + dz * dz).sqrt();
        brush_shape_factor(
            mask,
            [self.center[0], self.center[2]],
            self.radius,
            [world_x, world_z],
            d,
        )
    }
}

// ============================================================
//  ブラシの適用（1 チャンク分）
// ============================================================

/// 1 チャンク分のカバー場へブラシを 1 発当てる。
///
/// - `chunk_origin`: チャンク最小コーナーのワールド座標（メートル）
/// - `chunk_extent`: チャンク 1 辺のワールド長（メートル）
///
/// 戻り値は「カバー場が実際に変化したか」。false のときチャンクはダーティにならず、
/// 頂点の焼き直しも保存も走らない。
///
/// 【面が無いテクセルを触らない理由】
///   カバーは「面の性質」なので、面が無ければ消すものも塗る先も無い。
///   ここで弾いておくと、空中や地中のテクセルへ量を書き込んで
///   「どこにも描かれないのに `.tcover` だけ太る」状態を作らずに済む。
pub fn brush_chunk(
    field: &mut CoverField,
    surface: &CoverSurface,
    chunk_origin: [f32; 3],
    chunk_extent: f32,
    spec: &CoverBrushSpec,
) -> bool {
    brush_chunk_with_mask(field, surface, chunk_origin, chunk_extent, spec, None)
}

/// 形状マスク付きで 1 チャンク分のカバー場へブラシを 1 発当てる。
///
/// `mask` 以外の引数と戻り値の意味は `brush_chunk` と同一。
/// `mask` が `None`（未指定）または無効（読み込み失敗）なら、
/// **`brush_chunk` とビット単位で同じ結果**になる（`brush_mask.rs` の縮退規約）。
///
/// 【マスクを `CoverBrushSpec` へ持たせず引数で渡す理由】
///   エンジン層はマスクを `TerrainState` のキャッシュ（`HashMap`）から借りる。
///   `CoverBrushSpec` に参照を持たせるとライフタイム引数が全呼び出し側へ波及し、
///   逆に所有させるとブラシ 1 発ごとに画像を複製することになる。
///   「仕様（数値）」と「素材（画像）」を分けて渡せば、どちらも避けられる。
pub fn brush_chunk_with_mask(
    field: &mut CoverField,
    surface: &CoverSurface,
    chunk_origin: [f32; 3],
    chunk_extent: f32,
    spec: &CoverBrushSpec,
    mask: Option<&CoverMask>,
) -> bool {
    // ─── 早期棄却: 効かないブラシ・関係ないチャンク ───
    if !(chunk_extent > 0.0) || !(spec.strength > 0.0) || !(spec.radius > 0.0) {
        return false;
    }
    let aabb_min = chunk_origin;
    let aabb_max = [
        chunk_origin[0] + chunk_extent,
        chunk_origin[1] + chunk_extent,
        chunk_origin[2] + chunk_extent,
    ];
    if spec.is_outside_aabb(aabb_min, aabb_max) {
        return false;
    }

    let mut changed = false;
    for iz in 0..COVER_FIELD_RESOLUTION {
        for ix in 0..COVER_FIELD_RESOLUTION {
            // ─── 面が無いテクセルは対象外 ───
            if !surface.has_surface(ix, iz) {
                continue;
            }
            // ─── Y 照合: 着弾点と同じ段の面だけを触る（真下の洞窟へ効かせない）───
            let surface_y = surface.surface_y_at(ix, iz);
            if !spec.matches_surface_y(surface_y) {
                continue;
            }

            // ─── テクセル中心のワールド XZ と、ブラシの効き具合 ───
            let (u, v) = texel_center_uv(ix, iz);
            let world_x = chunk_origin[0] + u * chunk_extent;
            let world_z = chunk_origin[2] + v * chunk_extent;
            // 形状マスクがあればマスク値、無ければ従来の円形フォールオフ。
            // ブラシ球の XZ 正方形（一辺 = 2 × 半径）の外はマスク側が 0 を返す。
            let falloff = spec.falloff_at_masked(world_x, world_z, mask);
            if falloff <= 0.0 {
                continue;
            }
            let delta = falloff * spec.strength * COVER_BRUSH_MAX_DELTA_PER_APPLY;
            if delta <= 0.0 {
                continue;
            }

            match spec.mode {
                // ─── 消しゴム: 傾斜を見ない ───
                //   「積もりにくい斜面」は積もる側の規則であって、既にそこにある
                //   カバーを消せない理由にはならない（消せない場所があるほうが不便）。
                CoverBrushMode::Erase => {
                    if field.erase(ix, iz, delta) {
                        changed = true;
                    }
                }

                // ─── 塗り: 積算と同じ傾斜ルールを通す ───
                CoverBrushMode::Paint => {
                    let slope = slope_scale(surface.up_at(ix, iz));
                    if slope <= COVER_BRUSH_SLOPE_MIN {
                        continue;
                    }
                    // 「量を持つテクセルの基準 Y は必ず有限」の不変条件を、
                    // 積算（`accumulate_chunk`）と同じく **塗る前に**満たしておく。
                    field.set_base_y(ix, iz, surface_y);
                    if field.paint(ix, iz, spec.material_index, spec.target_amount, delta * slope) {
                        changed = true;
                    }
                }
            }
        }
    }
    changed
}
