// ============================================================
//  terrain/cover/trample.rs — 轍・足跡のスタンプ（純粋関数。Phase I3.2）
//
//  【責務】
//    「動く物が接地した」という 1 回の出来事を、カバー場の踏み固めチャネル
//    （`CoverField::stamp_trample`）へ押し当てる。
//    ECS も GPU も知らない。エンジン層（app/terrain_cover_ops.rs）が
//    `InteractionSourceComponent` ＋ Transform から `CoverStampSpec` を組み立て、
//    接地しうるチャンクにだけ本ファイルの関数を呼ぶ。
//
//  【なぜ積算（accumulate.rs）と別ファイルなのか】
//    駆動する事象がまったく違う。積算は「時間が経つと降る」＝ dt 駆動で全チャンク
//    一様に走るのに対し、スタンプは「物が動いた瞬間」＝ イベント駆動で
//    接地点の周囲 1〜2 チャンクにしか触らない。頻度もコストも別物なので、
//    早期棄却の作りも別に持たせるほうが素直である。
//
//  【Y 照合（この機能の要）】
//    カバー場は XZ の 2 次元なので、真上から押すだけでは
//    「洞窟の床を歩いた轍が、頭上の地表にも付く」ことになる。
//    そこでスタンプ側は接地点のワールド Y を持ち、その高さの近傍にある面
//    （`CoverSurface::surface_y_at` ＝ その面のワールド高さ）にだけ適用する。
//
//  【Y 照合の窓は上下で非対称（接地スナップ。I3.2 の不具合修正）】
//    当初は「|面の Y − ソースの Y| ≤ 半径」という **上下対称**の窓だった。
//    しかしソースの Y は *アクタの原点* であり、足裏とは限らない。
//    キャラクターのモデル原点が腰にある・当たり判定カプセルの中心にある、
//    といったごく普通の構成では原点が地面より 1m 前後高くなるため、
//    対称窓では照合に落ちて **轍が 1 テクセルも押されない**（実機で報告された症状）。
//
//    そこで窓を次の非対称なものへ変えた:
//      ・上方向（面がソースより上）… `y_tolerance()` まで
//        ＝ 少し埋まっている足元を許すぶんだけ。ここを広げると
//           「洞窟の床を歩いたら頭上の地表にも付く」が復活するので広げない。
//      ・下方向（面がソースより下）… `y_tolerance() + COVER_STAMP_GROUND_SNAP_DROP`
//        ＝ 浮いた原点から地面を探しに行く距離。
//    「真上の面には押さない」という Y 照合の目的はそのまま保たれる。
//
//  【形状（Circle / Texture）】
//    Circle  … 半径ベースの等方な窪み（既定）。人の足跡程度ならこれで足りる。
//    Texture … グレースケール画像を「進行方向へ回転させて」押し当てる。
//              タイヤのトレッド・ブーツの靴底など、向きを持つ痕を作るための形状。
// ============================================================

use super::emit::CoverMask;
use super::field::{
    slope_scale, texel_center_uv, CoverField, CoverSurface, COVER_FIELD_RESOLUTION,
};
use super::material::CoverMaterialSet;

// ─── 定数（マジックナンバー禁止）─────────────────────────────────────────────

/// 進行方向として採用できる速度の下限（m/s）。
///
/// これ未満の動きはノイズ（数値誤差・微振動）とみなし、向きを更新しない
/// ＝直前の向きを保つ。止まった瞬間に痕の向きがくるくる回るのを防ぐ。
pub const COVER_STAMP_DIRECTION_MIN_SPEED: f32 = 0.05;

/// 向きが未確定のときの既定の進行方向（ワールド XZ）。
///
/// 一度も動いていないソース（Play 開始直後にその場で踏んだ場合）に使う。
/// エンジンの前方が +Z なので、モデルの正面と痕の向きが自然に揃う。
pub const COVER_STAMP_DEFAULT_FORWARD: [f32; 2] = [0.0, 1.0];

/// Y 照合の許容差の下限（メートル）。
///
/// 許容差はソース半径から作る（大きな獣ほど上下に余裕を持って接地判定する）が、
/// 半径が極端に小さいソースでも「1 テクセルの起伏」ぶんは許さないと
/// 平地を歩いても一切痕が付かなくなる。
pub const COVER_STAMP_Y_TOLERANCE_MIN: f32 = 0.25;

/// 接地スナップで下方向へ余分に面を探す距離（メートル）。
///
/// 【なぜ必要か】
///   ソースのワールド Y は「アクタの原点」であって足裏ではない。人型モデルの
///   原点が腰にある、当たり判定カプセルの中心に置いてある、といった構成では
///   原点が地面より 1m 前後高い位置に来る。上下対称の許容差だけでは
///   そこで照合に落ち、轍が一切押されなくなる。
///
/// 【なぜ「下方向だけ」広げるのか】
///   上方向を同じだけ広げると「洞窟の床を歩いた轍が頭上の地表に付く」という、
///   Y 照合がそもそも防ぎたかった漏れが復活する。物は必ず**面の上に載る**ので、
///   探す向きは下だけでよい。
///
/// 【なぜ 2m なのか】
///   人型（腰の高さ 1m 前後）・小型車両（車軸中心 0.5m 前後）の原点ずれを
///   余裕をもって飲み込みつつ、「2m 上の桟橋を歩いたら下の雪に轍が付く」という
///   誤爆までは届かない距離として選んだ。これを超える浮きを許したい場合は
///   ソースの半径を大きくする（許容差が半径に比例して伸びる）。
pub const COVER_STAMP_GROUND_SNAP_DROP: f32 = 2.0;

/// 縁の盛り上がり（リム）が張り出す倍率。
///
/// 痕の形状をこの倍率で拡大したものと、等倍のものとの **差** が縁の帯になる
/// （円なら半径 r と r×倍率 の間のリング、テクスチャ痕なら輪郭に沿った縁取り）。
/// 形状に依らず 1 つの規則で縁を作れるのがこの方式の利点であり、
/// タイヤのトレッド痕でも「踏んだ形の外側だけ」が正しく盛れる。
///
/// 【なぜ 1.4 なのか】
///   1.0 に近いと縁が痕の輪郭と重なって潰れ、大きすぎると足跡から離れた場所が
///   盛り上がって「土手」に見える。痕の半径の 4 割ぶん外へ広がる幅は、
///   カバー場のテクセル 0.5m に対しても 1 テクセル前後を確保できる最小値である。
pub const COVER_STAMP_RIM_SCALE: f32 = 1.4;

/// 踏み固めが効き始める傾斜（`slope_scale` の値）の下限。
///
/// 崖には積もらない（＝カバーが無い）ので踏む対象も無い。積算と同じ規則を
/// 使うことで「積もる面 ⇔ 踏める面」が常に一致する。
const COVER_STAMP_SLOPE_MIN: f32 = 0.0;

// ============================================================
//  CoverStampShape — 痕の形状
// ============================================================

/// 轍スタンプの形状（どんな形に踏み固めるか）。
#[derive(Clone, Debug, PartialEq)]
pub enum CoverStampShape {
    /// 等方な円（既定）。中心で最大・縁で 0 の滑らかな窪み。
    Circle,
    /// グレースケール画像を進行方向へ回転させて押し当てる。
    ///
    /// 白 = 最大の深さ、黒 = 踏まない。画像の **上方向（V=0 側）が進行方向**。
    Texture {
        /// 痕のローカルサイズ（メートル）。`[進行方向に直交する幅, 進行方向の長さ]`。
        size: [f32; 2],
        /// 進行方向（ワールド XZ の単位ベクトル）。
        forward_xz: [f32; 2],
        /// グレースケールマスク（`CoverMask` はエミッタのマスクと同じ型を使い回す）。
        mask: CoverMask,
    },
}

// ============================================================
//  CoverStampSpec — スタンプ 1 回ぶんの仕様
// ============================================================

/// 「動く物が 1 点で接地した」ことを表すスタンプ仕様（ワールド解決済み）。
#[derive(Clone, Debug, PartialEq)]
pub struct CoverStampSpec {
    /// 接地点のワールド座標。Y は **接地面の高さ**として Y 照合に使う。
    pub contact: [f32; 3],
    /// 影響半径（メートル）。`Circle` の半径であり、`Texture` でも AABB の余裕に使う。
    pub radius: f32,
    /// 踏み込みの強さ（0..1）。素材の「足跡の残りやすさ」と掛け合わせる。
    pub strength: f32,
    /// 形状。
    pub shape: CoverStampShape,
}

impl CoverStampSpec {
    /// Y 照合の許容差（メートル）。半径に比例させ、下限で底上げする。
    ///
    /// これは **上方向**（面がソースより上にある場合）の許容量であり、
    /// 下方向は `ground_reach_down()` を使う（上下非対称。ファイル冒頭参照）。
    ///
    /// 【なぜ半径から作るのか】
    ///   影響半径は「その物がどれだけの範囲に触れているか」の宣言なので、
    ///   上下方向の許容にもそのまま使うのがデータ 1 個で意図を表現できて素直である。
    pub fn y_tolerance(&self) -> f32 {
        self.radius.max(COVER_STAMP_Y_TOLERANCE_MIN)
    }

    /// 接地スナップで **下方向**へ面を探しに行く距離（メートル）。
    ///
    /// 上方向の許容差に固定の探索距離を足したもの。アクタの原点が地面より
    /// 高い位置にあっても轍が押されるようにするための窓であり、
    /// 「真上の面には押さない」性質は上側の許容差が守る。
    pub fn ground_reach_down(&self) -> f32 {
        self.y_tolerance() + COVER_STAMP_GROUND_SNAP_DROP
    }

    /// このスタンプが、ワールド高さ `surface_y` の面に届くか（Y 照合の本体）。
    ///
    /// 窓は上下非対称:
    ///   `contact_y - ground_reach_down() ≤ surface_y ≤ contact_y + y_tolerance()`
    ///
    /// 非有限な面の高さ（壊れた派生データ）は常に false（触らない）。
    pub fn matches_surface_y(&self, surface_y: f32) -> bool {
        if !surface_y.is_finite() {
            return false;
        }
        let delta = surface_y - self.contact[1];
        delta <= self.y_tolerance() && delta >= -self.ground_reach_down()
    }

    /// このスタンプが XZ 方向に届く最大距離（メートル）。
    ///
    /// `Texture` はサイズの対角線の半分まで届きうる（どの向きに回転しても
    /// はみ出さない保守的な上界）。
    /// **縁の盛り上がり（リム）まで含めた**距離であることに注意。リムは形状を
    /// `COVER_STAMP_RIM_SCALE` 倍したところまで届くので、ここを等倍のままにすると
    /// 縁だけが載るチャンクが選別で落ちて盛り上がりが切れる。
    pub fn reach_xz(&self) -> f32 {
        let base = match &self.shape {
            CoverStampShape::Circle => self.radius.max(0.0),
            CoverStampShape::Texture { size, .. } => {
                let half_diagonal = (size[0] * size[0] + size[1] * size[1]).sqrt() * 0.5;
                half_diagonal.max(self.radius.max(0.0))
            }
        };
        base * COVER_STAMP_RIM_SCALE
    }

    /// このスタンプが指定 AABB（チャンクなど）に一切かからないか。
    ///
    /// Y は `matches_surface_y` と **同じ非対称の窓**
    /// （`[接地点 - ground_reach_down, 接地点 + y_tolerance]`）で判定する。
    /// ここを対称のままにすると、テクセル単位では届く面を持つチャンクが
    /// チャンク選別の段で先に落ちてしまい、修正が効かない。
    pub fn is_outside_aabb(&self, min: [f32; 3], max: [f32; 3]) -> bool {
        let reach = self.reach_xz();
        let up = self.y_tolerance();
        let down = self.ground_reach_down();
        self.contact[0] + reach < min[0]
            || self.contact[0] - reach > max[0]
            || self.contact[2] + reach < min[2]
            || self.contact[2] - reach > max[2]
            || self.contact[1] + up < min[1]
            || self.contact[1] - down > max[1]
    }

    /// 指定ワールド XZ 位置での踏み込み比率（0..1）を返す。
    ///
    /// 形状ごとの落ち方だけを決める純粋関数であり、素材の残りやすさ・強さは
    /// 呼び出し側が掛ける（責務の分離：`CoverEmitSpec::coverage_at` と同じ流儀）。
    pub fn footprint_at(&self, world_x: f32, world_z: f32) -> f32 {
        // 等倍＝痕そのもの。倍率を掛けたものは縁（`rim_at`）の計算に使う。
        self.footprint_at_scaled(world_x, world_z, 1.0)
    }

    /// 縁の盛り上がり比（0..1）を返す。
    ///
    /// 「形状を `COVER_STAMP_RIM_SCALE` 倍した痕」と「等倍の痕」の差である。
    /// 痕の内側では両者がほぼ等しいので 0 に近づき、輪郭のすぐ外側で最大になり、
    /// さらに外では両方 0 になって消える。つまり **痕の周りを囲む帯**が得られる。
    ///
    /// 形状の種類を知らずに縁を作れるので、円でもタイヤのトレッド痕でも
    /// 同じ 1 行で「踏んだ形の外側だけが盛れる」ようになる。
    pub fn rim_at(&self, world_x: f32, world_z: f32) -> f32 {
        let outer = self.footprint_at_scaled(world_x, world_z, COVER_STAMP_RIM_SCALE);
        let inner = self.footprint_at(world_x, world_z);
        (outer - inner).max(0.0)
    }

    /// 形状を `scale` 倍したときの踏み込み比率（0..1）。
    ///
    /// `scale = 1.0` のとき `footprint_at` と **bit 単位で同じ**値を返す
    /// （半径・サイズへの 1.0 倍は浮動小数として恒等変換であるため）。
    fn footprint_at_scaled(&self, world_x: f32, world_z: f32, scale: f32) -> f32 {
        let dx = world_x - self.contact[0];
        let dz = world_z - self.contact[2];
        match &self.shape {
            // ─── 円: 中心 1.0・縁 0.0 の smoothstep ───
            CoverStampShape::Circle => {
                let r = self.radius * scale;
                if !(r > 0.0) {
                    return 0.0;
                }
                let d = (dx * dx + dz * dz).sqrt();
                if d >= r {
                    return 0.0;
                }
                let t = 1.0 - d / r;
                // 両端で傾き 0 になる smoothstep（縁に硬い線が出ない）。
                t * t * (3.0 - 2.0 * t)
            }

            // ─── テクスチャ: 進行方向へ回した局所座標で画像を読む ───
            CoverStampShape::Texture { size, forward_xz, mask } => {
                let (sw, sl) = (size[0] * scale, size[1] * scale);
                if !(sw > 0.0) || !(sl > 0.0) || !mask.is_valid() {
                    return 0.0;
                }
                // 進行方向（前方）と、その右手側（横方向）の 2 軸を作る。
                //   前方 f=(fx,fz) に対する右手は (fz, -fx)（左手系 Y-up の +X が右）。
                let (fx, fz) = (forward_xz[0], forward_xz[1]);
                let len = (fx * fx + fz * fz).sqrt();
                if !(len > 0.0) {
                    return 0.0;
                }
                let (fx, fz) = (fx / len, fz / len);
                let (rx, rz) = (fz, -fx);

                // 局所座標（横 = 右方向の成分・縦 = 前方向の成分）。
                let local_right = dx * rx + dz * rz;
                let local_forward = dx * fx + dz * fz;

                // 画像 UV へ。U は右方向、V は **前方が上（V=0）** になるよう反転する。
                let u = local_right / sw + 0.5;
                let v = 0.5 - local_forward / sl;
                mask.sample(u, v)
            }
        }
    }
}

// ============================================================
//  スタンプの適用（1 チャンク分）
// ============================================================

/// 1 チャンク分のカバー場へスタンプ列を押し当てる。
///
/// - `chunk_origin`: チャンク最小コーナーのワールド座標（メートル）
/// - `chunk_extent`: チャンク 1 辺のワールド長（メートル）
/// - `materials`: 素材定義（「足跡の残りやすさ」を引くために要る）
///
/// 戻り値は「カバー場が実際に変化したか」。false のときチャンクはダーティにならず、
/// 頂点の焼き直しも保存も走らない。
///
/// どのスタンプが効いたかを知りたい場合は `stamp_chunk_tracked` を使う
/// （実装は 1 本きり。こちらはヒット列を捨てるだけの薄い包み）。
pub fn stamp_chunk(
    field: &mut CoverField,
    surface: &CoverSurface,
    chunk_origin: [f32; 3],
    chunk_extent: f32,
    stamps: &[CoverStampSpec],
    materials: &CoverMaterialSet,
) -> bool {
    let mut hits = vec![false; stamps.len()];
    stamp_chunk_tracked(field, surface, chunk_origin, chunk_extent, stamps, materials, &mut hits)
}

/// `stamp_chunk` に「どのスタンプが実際にカバー場を変えたか」の記録を足したもの。
///
/// - `hits`: `stamps` と同順の記録先。**スタンプ `i` が最深として採用され、かつ
///   そのテクセルの踏み固めが実際に深くなった**とき `hits[i] = true` になる。
///   関数は false を書き戻さない（呼び出し側が複数チャンク分を累積できる）。
///   長さが `stamps` より短い場合、はみ出す添字は単に記録されない。
///
/// 【何のための記録か（デバッグ描画）】
///   「轍が付かない」類の不具合は、収集・チャンク選別・Y 照合・素材・量の
///   どこで落ちたのかが実機では見えない。選択中ソースのギズモを
///   「今まさに押した」瞬間だけ色替えするために、押したという事実を
///   ソース単位で返す必要がある。値の意味は真偽 1 ビットだけなので、
///   計算コストは実質ゼロである。
pub fn stamp_chunk_tracked(
    field: &mut CoverField,
    surface: &CoverSurface,
    chunk_origin: [f32; 3],
    chunk_extent: f32,
    stamps: &[CoverStampSpec],
    materials: &CoverMaterialSet,
    hits: &mut [bool],
) -> bool {
    if stamps.is_empty() || !(chunk_extent > 0.0) {
        return false;
    }

    // ─── 早期棄却: このチャンクの AABB にかかるスタンプだけを残す ───
    let aabb_min = chunk_origin;
    let aabb_max = [
        chunk_origin[0] + chunk_extent,
        chunk_origin[1] + chunk_extent,
        chunk_origin[2] + chunk_extent,
    ];
    // ヒット記録のため **元の添字を持ったまま**残す。
    let active: Vec<(usize, &CoverStampSpec)> = stamps
        .iter()
        .enumerate()
        .filter(|(_, s)| s.strength > 0.0 && !s.is_outside_aabb(aabb_min, aabb_max))
        .collect();
    if active.is_empty() {
        return false;
    }

    let mut changed = false;
    for iz in 0..COVER_FIELD_RESOLUTION {
        for ix in 0..COVER_FIELD_RESOLUTION {
            // ─── 面が無い／積もらない急斜面のテクセルは踏めない ───
            if !surface.has_surface(ix, iz) {
                continue;
            }
            if slope_scale(surface.up_at(ix, iz)) <= COVER_STAMP_SLOPE_MIN {
                continue;
            }

            // ─── カバーが無いテクセルには轍が残らない（素の地面は変形しない）───
            let amount = field.amount_at(ix, iz);
            if amount <= 0.0 {
                continue;
            }
            let material_index = field.material_at(ix, iz);
            let Some(material) = materials.get(material_index as usize) else {
                continue;
            };
            // 素材が「足跡の残りやすさ」を持たない（濡れ・水膜など）なら何も起きない。
            if !(material.footprint_persistence > 0.0) {
                continue;
            }

            // ─── テクセル中心の面のワールド座標 ───
            let (u, v) = texel_center_uv(ix, iz);
            let world_x = chunk_origin[0] + u * chunk_extent;
            let world_z = chunk_origin[2] + v * chunk_extent;
            let surface_y = surface.surface_y_at(ix, iz);

            // ─── 各スタンプを評価して、最も深い踏み込み／最も強い縁を採る ───
            //   踏み込みと縁は同じテクセルで同時に立ちうる（痕の輪郭付近）。
            //   どちらも「最大値を採る」ことで、複数ソースが重なっても結果が
            //   順序に依存しない（`stamp_trample` が最大値更新なのと同じ流儀）。
            let mut depth = 0.0f32;
            let mut deepest: Option<usize> = None;
            let mut rim = 0.0f32;
            for (index, s) in &active {
                // Y 照合: この面がスタンプの上下窓の内側にあるか
                // （洞窟の床を歩いた轍が頭上の地表へ写らない ＋ 浮いた原点でも接地する）。
                if !s.matches_surface_y(surface_y) {
                    continue;
                }
                // 縁の盛り上がり = 縁の帯 × 強さ × 素材の残りやすさ。
                //   「形を保てない素材（濡れ）では縁も立たない」を残りやすさで表す。
                let rim_f = s.rim_at(world_x, world_z);
                if rim_f > 0.0 {
                    rim = rim.max(rim_f * s.strength * material.footprint_persistence);
                }

                let f = s.footprint_at(world_x, world_z);
                if f <= 0.0 {
                    continue;
                }
                // 踏み込み深さ = 形状 × 強さ × 素材の残りやすさ。
                //   量を超える踏み込みも許す（下地が完全に露出する）。
                let d = f * s.strength * material.footprint_persistence;
                if d > depth {
                    depth = d;
                    deepest = Some(*index);
                }
            }

            // ─── 縁の盛り上がり: 押しのけられた素材を量チャネルへ足す ───
            //   踏み固めチャネルではなく **量** を増やすので、変位・色・法線は
            //   既存の 1 本の経路（`rebuild_terrain_model_with_cover`）がそのまま扱う。
            //   足す素材は必ず「そのテクセルに今ある素材」なので、`deposit` の
            //   異素材置き換え規則（削ってから積む）には決して入らない
            //   ＝踏むたびに縁が確実に高くなる（上限は量 1.0）。
            let rim_gain = rim * material.rim_ratio;
            if rim_gain > 0.0 {
                let before = field.amount_at(ix, iz);
                field.deposit(ix, iz, material_index, rim_gain);
                if field.amount_at(ix, iz) != before {
                    changed = true;
                }
            }

            if depth <= 0.0 {
                continue;
            }

            if field.stamp_trample(ix, iz, depth) {
                changed = true;
                // 実際に場を変えたスタンプだけを「作用中」として記録する
                // （既に同じ深さまで踏まれていたテクセルは記録しない＝
                //   ギズモの色替えが「今まさに押した」だけを意味するようになる）。
                if let Some(hit) = deepest.and_then(|i| hits.get_mut(i)) {
                    *hit = true;
                }
            }
        }
    }
    changed
}

// ============================================================
//  進行方向の解決
// ============================================================

/// 移動量から進行方向（ワールド XZ の単位ベクトル）を決める。
///
/// 動きが `COVER_STAMP_DIRECTION_MIN_SPEED × dt` 未満なら `previous` を返す
/// （止まった瞬間に痕がくるくる回るのを防ぐ）。`previous` が無ければ既定の +Z。
pub fn resolve_forward_xz(
    delta_xz: [f32; 2],
    dt: f32,
    previous: Option<[f32; 2]>,
) -> [f32; 2] {
    let min_move = COVER_STAMP_DIRECTION_MIN_SPEED * dt.max(0.0);
    let len = (delta_xz[0] * delta_xz[0] + delta_xz[1] * delta_xz[1]).sqrt();
    if len.is_finite() && len > min_move && len > 0.0 {
        return [delta_xz[0] / len, delta_xz[1] / len];
    }
    previous.unwrap_or(COVER_STAMP_DEFAULT_FORWARD)
}
