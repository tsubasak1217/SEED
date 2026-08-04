// ============================================================
//  water/params.rs — 水面描画の GPU 側パラメータ
//
//  `ResolvedWaterVolume`（エンジン層の中間表現）を、シェーダが読む
//  ストレージバッファ要素へ詰め替えるだけの層。
//  「エンジンの水表現」と「描画都合のレイアウト」を型で分離しておくことで、
//  シェーダのレイアウト変更がエンジン層へ波及しない。
// ============================================================

use crate::engine::water::ResolvedWaterVolume;
use crate::engine::components::water_volume_component::WaterVolumeKind;
// 粘度による波速の低下係数は「波紋の伝播」と同じ関数を共有する（Phase I2.1）。
// 二重定義すると「模様のスクロールと波紋の広がりで粘度の効きが違う」ずれが出る。
use crate::engine::interaction::viscosity_wave_speed_scale;
use super::tessellation::{WATER_GRID_WARP_OFF, WATER_GRID_WARP_ON};

/// 1 ドローで描ける水ボリュームの最大数。
/// これを超えた分は切り捨てる（描画順の先頭から採用）。
/// ストレージバッファはこの容量まで自動で伸びる。
pub const WATER_MAX_VOLUMES: usize = 64;

/// 1 ドローで描ける最大**インスタンス**数（Phase W4）。
///
/// W1〜W1.5 では「1 水ボリューム = 1 インスタンス」だったが、川（Spline）は
/// 折れ線の 1 分割ごとに 1 インスタンス（リボンのクアッド 1 枚）を消費するため、
/// ボリューム数とインスタンス数が一致しなくなった。
/// 上限は「水域 64 個 ＋ 川の分割（最大 256）を数本」を賄える値にしてある
/// （1 インスタンス = `WaterParams` 256 バイトなので 1024 個でも 256KB）。
pub const WATER_MAX_INSTANCES: usize = 1024;

/// インスタンス種別（`WaterParams::center.w`）: 軸平行クアッド（Ocean / Region）。
pub const WATER_INSTANCE_QUAD: f32 = 0.0;

/// インスタンス種別（`WaterParams::center.w`）: 川リボンの 1 分割（Phase W4）。
///
/// 頂点シェーダはこの値を見て、`center ± half_extent` の矩形ではなく
/// `river_p0/river_p1/river_normal` から作る四角形（＝リボンの 1 コマ）を生成する。
/// **`water_surface.wgsl` / `water_id.wgsl` の `WATER_INSTANCE_RIVER` と一致必須。**
pub const WATER_INSTANCE_RIVER: f32 = 1.0;

/// 水面の格子セル 1 枚を描くための頂点数（三角形 2 枚 = 6 頂点）。
///
/// Phase W5.1 以前は「1 インスタンス = 1 クアッド = 6 頂点」だったが、頂点変位のために
/// 1 インスタンスを格子へ分割したので、この値は**セル 1 枚あたり**の意味になった。
/// 実際の描画頂点数は `tessellation::grid_vertex_count` が返す。
pub use super::tessellation::WATER_CELL_VERTEX_COUNT;

/// 波紋フォームしきい値の下限（m 相当）。
///
/// 0 を許すと「波高 0 の静水面まで泡だらけ」になり、ユーザが値を 0 にした瞬間に
/// 水面が真っ白になる。目視できないほど小さい正値で下限を切る。
pub const RIPPLE_FOAM_THRESHOLD_MIN: f32 = 1.0e-4;

/// 岸波の波長の下限（m）。
///
/// 位相は「岸距離 / 波長」なので 0 を許すと位相が発散し、
/// 1 テクセル内で無限に振動する（＝画面がノイズで埋まる）。
/// 目視で 1 波長を識別できる最小値として 10cm を下限にする。
pub const SHORE_WAVE_LENGTH_MIN: f32 = 0.1;

/// 岸波の周期の下限（秒）。
///
/// 0 だと時間項が発散する。フレーム時間より短い周期は
/// エイリアシングにしかならないので 1/60 秒相当を下限にする。
pub const SHORE_WAVE_PERIOD_MIN: f32 = 1.0 / 60.0;

/// 波形ランダマイズのノイズ空間周波数倍率の下限（Phase W6.4）。
///
/// 0 以下だとノイズの空間周波数が 0 になり、「ワープの波長 = ∞」＝
/// 変位量が発散する（ワープ量をノイズ自身の波長比で決めているため）。
/// 目視で意味を持つ最小の細かさとして 1/100 を下限にする
/// （既定 `wave_scale`=0.12 に対して波長 5km 相当＝実質「歪みなし」に見える）。
pub const WAVE_NOISE_SCALE_MIN: f32 = 0.01;

/// 水ボリューム 1 個ぶんの GPU パラメータ。
///
/// **全フィールドを vec4 相当（`[f32; 4]`）で構成している**。
/// std430 のアラインメント規則では vec3 が 16 バイト境界へ寄せられて暗黙のパディングが
/// 生じるため、そもそも vec3 を持たせないことでレイアウト事故を構造的に排除する。
/// WGSL 側 `water_surface.wgsl` の `struct WaterParams` とフィールド順を厳密一致させること。
#[repr(C)]
#[derive(Debug, Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
pub struct WaterParams {
    /// xyz = 水面クアッド中心のワールド座標（y = 水面 Y）／
    /// **w = インスタンス種別**（`WATER_INSTANCE_QUAD` / `WATER_INSTANCE_RIVER`。Phase W4）
    pub center: [f32; 4],
    /// x,z = クアッドの片側半径（m）／
    /// **y,w = 格子の分割数（Phase W5.1。y = X 方向／w = Z 方向。川では y = 幅方向／w = 長さ方向）**
    ///
    /// W5.1 以前は y,w が未使用の余剰スロットだったので、分割数をここへ同居させることで
    /// 配列ストライド（vec4 16 本）も WGSL 側の struct 宣言も変えずに済んでいる。
    /// 値は `set_grid_divisions` で描画直前に書き込む（バケット全体の頂点予算に
    /// 依存するため、`from_resolved` の時点では確定しない）。
    pub half_extent: [f32; 4],
    /// rgb = 浅場の色／a = 吸収距離（m）
    pub shallow_color: [f32; 4],
    /// rgb = 深場の色／a = 深場での最大不透明度
    pub deep_color: [f32; 4],
    /// rgb = 岸フォームの色／a = フォーム幅（m）
    pub foam_color: [f32; 4],
    /// 反射（Phase W5.2）。
    /// x = 反射の全体強度（0 で無効）／y = 波による反射のぼけ（粗さ 0..1）／
    /// z = 予約（0）／**w = フォーム強度（0..1）**
    ///
    /// x,y を消費するのは**水面反射パス**（`water_reflection_*.wgsl`）だけで、
    /// 水面パスは w（フォーム強度）しか読まない。
    /// W5.2 以前はここが「簡易反射色 rgb ＋ フォーム強度 a」だったが、固定色の反射は
    /// 本物の反射（水面反射 RT）へ置き換わったので rgb を反射の調整値に転用した
    /// （vec4 の本数もストライドも変わらない＝WGSL 側の struct 宣言も 1 行の差し替えで済む）。
    pub reflection: [f32; 4],
    /// x = 波振幅／y = 波の空間周波数／z = 波速度／w = 屈折歪み
    pub wave: [f32; 4],
    /// x = フレネル指数／y = フレネル寄与率／
    /// z = 波紋の法線摂動スケール（Phase I2）／w = 波紋フォームの波高しきい値（Phase I2）
    ///
    /// **z,w は W1 では未使用の余剰スロットだった。**I2 の 2 パラメータをここへ同居させることで、
    /// I2 時点では配列ストライドも WGSL 側の struct 宣言も一切変えずに済んだ
    /// （W1.5 の岸波は空きスロットが尽きたため、末尾へ vec4 を 2 本追加している）。
    pub fresnel: [f32; 4],
    /// x = ピッキング用の raw アクタ ID（`id_base + DFS + 1`。0 = 背景）／y,z,w = 未使用
    ///
    /// ID パス（`water_id.wgsl`）だけが読む。水面描画本体（`water_surface.wgsl`）は
    /// 使わないが、パラメータ配列を 1 本に保つ（収集もアップロードも 1 回で済む）ため
    /// 同じ構造体に持たせている。**WGSL 側の両シェーダと順序を同期すること。**
    pub actor_id: [u32; 4],
    /// 岸波の調整値（Phase W1.5）。
    /// x = 強さ（0 で完全無効）／y = うねりの波長（m）／z = うねりの周期（秒）／w = 泡量（0..1）
    pub shore: [f32; 4],
    /// 岸波のショアフィールド窓（Phase W1.5）。
    ///
    /// x,y = 窓のワールド XZ 最小／z = 窓一辺の逆数（1/m。ワールド XZ → [0,1] UV）／
    /// **w = 配列テクスチャのレイヤ番号。負値は「この水域にショアフィールドが無い」**
    /// （＝岸波を描かない）。f32 に入れているのは vec4 を分割せずに済ませるためで、
    /// シェーダ側は `w < 0` の判定と `i32(w)` のレイヤ添字化だけを行う。
    pub shore_field: [f32; 4],
    /// 川リボン 1 分割の上流側ノード（Phase W4）。
    /// xyz = ワールド座標（y = その地点の水面 Y）／w = リボンの半幅（m）
    pub river_p0: [f32; 4],
    /// 川リボン 1 分割の下流側ノード（Phase W4）。
    /// xyz = ワールド座標／w = 流速（m/s。水面模様を下流へ流す速さ）
    pub river_p1: [f32; 4],
    /// 川リボンの断面法線（Phase W4）。
    /// x,y = 上流ノードの法線（XZ。**マイター補正込みなので長さは 1 とは限らない**）／
    /// z,w = 下流ノードの法線（同上）。
    /// リボンの 4 隅は `p0 ± n0 * 半幅` と `p1 ± n1 * 半幅` で決まる。
    pub river_normal: [f32; 4],
    /// 解析波の**全体回転**（Phase W6.3）＋ 格子の放射状ワープフラグ（Phase W5.1）。
    /// x = cos(方位角)／y = sin(方位角)／
    /// **z = 放射状ワープの有効フラグ（`WATER_GRID_WARP_ON` = Ocean のみ）**／w = 予約（0）。
    ///
    /// `wave_direction_deg` をここで cos/sin へ焼いておくのは、
    /// 毎フラグメントで sin/cos を回さないため（値は水域単位の定数）。
    pub wave_axis: [f32; 4],
    /// 川リボン 1 分割の**関節タンジェント**（Phase W6.2）。
    /// x,y = 上流ノード／z,w = 下流ノードの進行方向（XZ 単位ベクトル）。
    ///
    /// 区間の弦ではなく `RiverNode::tangent`（中央差分で平滑化済み）を渡すのが要点。
    /// 隣接インスタンスは共有する関節で同一値を持つため、頂点間で補間すると
    /// 流れの向きが区間境界で連続になり、リボンの継ぎ目が消える。
    pub river_tangent: [f32; 4],
    /// 水中コースティクス（Phase W5.3）。
    /// x = 強度（0 で完全無効）／y = パターンの細かさ倍率／z = 深度フェード距離(m)／
    /// **w = 影の屈折ゆらぎ誇張倍率（1.0 = 物理どおり／0 で無効）**
    ///
    /// 消費するのは**コースティクス生成パスだけ**（`caustics.wgsl`）で、水面パス・ID パスは
    /// 読まない。それでも同じ配列に持たせているのは、水域パラメータの収集・アップロードを
    /// 1 本に保つため（`actor_id` を ID パス専用に同居させているのと同じ理由）。
    pub caustics: [f32; 4],
    /// 波形のプロシージャルランダマイズ（Phase W6.4）。
    /// x = 強さ（**0 で完全無効＝W6.3 以前と同一出力**）／y = ノイズの空間周波数倍率／
    /// z,w = 予約（0）。
    ///
    /// 空きスロットが尽きていた（`wave_axis.w` の 1 枠だけでは 2 パラメータを置けない）ため、
    /// W1.5・W5.3 と同じく末尾へ vec4 を 1 本追加している。
    /// **`water_height_field.wgsl` の `struct WaterParams` と順序・意味を同期すること。**
    pub wave_noise: [f32; 4],
}

/// ショアフィールドを持たない水域を表すレイヤ番号（負値）。
///
/// シェーダはこの値を見て岸波の計算そのものをスキップする
/// （テクスチャサンプルも行わないので、岸波を使わない水は W1/I2 と完全に同じコスト）。
pub const SHORE_LAYER_NONE: f32 = -1.0;

impl WaterParams {
    /// `ResolvedWaterVolume` から GPU パラメータを作る。
    ///
    /// `camera_pos` は Ocean のカメラ追従に使う（Ocean は XZ 無限の想定なので、
    /// カメラ位置を中心とした `ocean_extent` 半径のクアッドを毎フレーム置き直す）。
    /// Region は AABB の中心 XZ・半径 XZ をそのまま使い、Y は解決済みの水面 Y。
    ///
    /// `id_base` はピッキング ID 空間のベースオフセット（エディタの `canvas_id_offset`）。
    /// 書き込む raw ID は他のピック対象と同じ規約 `id_base + DFS + 1`（0 = 背景）とし、
    /// デコード側の「キャンバスアクター選択」分岐（`global - canvas_id_offset` を
    /// DFS インデックスとして解決する経路）にそのまま乗る。
    ///
    /// `shore` はこの水域に対して焼かれたショアフィールド（Phase W1.5）。
    /// `None`（＝まだ焼けていない・岸波を切っている）なら
    /// レイヤ番号 `SHORE_LAYER_NONE` を入れ、シェーダは岸波を完全に無視する。
    pub fn from_resolved(
        v:          &ResolvedWaterVolume,
        camera_pos: [f32; 3],
        id_base:    u32,
        shore:      Option<&crate::engine::water::ShoreFieldEntry>,
    ) -> Self {
        // Ocean は「カメラ追従の巨大クアッド」、Region は「AABB 上面の矩形」。
        let (cx, cz, hx, hz) = match v.kind {
            WaterVolumeKind::Ocean => (
                camera_pos[0], camera_pos[2],
                v.ocean_extent, v.ocean_extent,
            ),
            _ => (
                v.center[0], v.center[2],
                v.half_extents[0], v.half_extents[2],
            ),
        };
        let vis = &v.visual;
        // 格子分割数（half_extent.y/.w）は「バケット全体の頂点予算」で決まるため、
        // ここでは 1 分割（＝W5.1 以前と同じ 1 枚クアッド）を入れておき、
        // 描画直前に `set_grid_divisions` で上書きする。
        Self {
            center:      [cx, v.surface_y, cz, WATER_INSTANCE_QUAD],
            half_extent: [hx, 1.0, hz, 1.0],
            shallow_color: [
                vis.shallow_color[0], vis.shallow_color[1], vis.shallow_color[2],
                vis.absorption_distance,
            ],
            deep_color: [
                vis.deep_color[0], vis.deep_color[1], vis.deep_color[2],
                vis.surface_opacity,
            ],
            foam_color: [
                vis.foam_color[0], vis.foam_color[1], vis.foam_color[2],
                vis.foam_width,
            ],
            // 反射（Phase W5.2）。
            //   x = 強度（負値は「反射を反転する」という意味を持たないので 0 で下限を切る＝無効）
            //   y = 粗さ（0..1 の外はジッタ量が発散／反転するのでクランプ）
            //   z = 予約（0）
            //   w = フォーム強度（W5.2 以前と同じスロット）
            reflection: [
                vis.reflection_intensity.max(0.0),
                vis.reflection_roughness.clamp(0.0, 1.0),
                0.0,
                vis.foam_intensity,
            ],
            // 解析波（水面模様のスクロール）。
            //   z = 波速には **粘度の低下係数**を掛ける（Phase I2.1）。
            //   波紋（インタラクションフィールド）の伝播だけを遅くして解析波を
            //   そのままにすると、「模様はさらさら流れているのに輪だけ止まっている」
            //   ちぐはぐな見え方になるため、同じ係数で一緒になまらせる。
            //   **粘度 0（既定）では係数が厳密に 1.0** なので、既存シーンは変わらない。
            wave: [
                vis.wave_amplitude,
                vis.wave_scale,
                vis.wave_speed * viscosity_wave_speed_scale(vis.viscosity),
                vis.refraction_distortion,
            ],
            fresnel: [
                vis.fresnel_power, vis.fresnel_strength,
                // 負の摂動スケールは法線を逆向きに歪めるだけで意味を持たないため 0 で下限を切る。
                vis.ripple_strength.max(0.0),
                // しきい値 0 は「常にフォーム全開」を意味してしまうので下限を切る。
                vis.ripple_foam_threshold.max(RIPPLE_FOAM_THRESHOLD_MIN),
            ],
            // raw ID = ベース + DFS + 1（+1 は「0 = 背景」を空けるための ID パス共通規約）
            actor_id: [id_base + v.actor_dfs_id + 1, 0, 0, 0],
            shore: [
                // 負の強さは波を逆位相にするだけで意味を持たないため 0 で下限を切る。
                vis.shore_wave_strength.max(0.0),
                // 波長・周期 0 は位相が発散するので下限を切る（＝実質無効になる小ささ）。
                vis.shore_wave_length.max(SHORE_WAVE_LENGTH_MIN),
                vis.shore_wave_period.max(SHORE_WAVE_PERIOD_MIN),
                vis.shore_wave_foam.clamp(0.0, 1.0),
            ],
            shore_field: match shore {
                Some(f) => [
                    f.origin_xz[0], f.origin_xz[1],
                    // 一辺の逆数。0 除算は焼き側で起きない前提だが念のため守る。
                    if f.extent_m > 0.0 { 1.0 / f.extent_m } else { 0.0 },
                    f.layer as f32,
                ],
                None => [0.0, 0.0, 0.0, SHORE_LAYER_NONE],
            },
            // 解析波の全体回転（Phase W6.3）。度 → ラジアン → cos/sin を CPU で焼く。
            // z = 格子の放射状ワープフラグ（Phase W5.1）。**Ocean だけ有効**にする:
            //   Ocean は「カメラ追従の巨大クアッド」なので一様分割では近傍が粗すぎるが、
            //   Region / 川は面積相応の一様分割で足りるうえ、ワープを掛けると
            //   水域の中央だけ異様に細かくなって頂点が無駄になる。
            // w は予約（0）。
            wave_axis: {
                let rad = vis.wave_direction_deg.to_radians();
                let warp = match v.kind {
                    WaterVolumeKind::Ocean => WATER_GRID_WARP_ON,
                    _                      => WATER_GRID_WARP_OFF,
                };
                [rad.cos(), rad.sin(), warp, 0.0]
            },
            // クアッド（Ocean / Region）は川のフィールドを使わない。
            river_p0:      [0.0; 4],
            river_p1:      [0.0; 4],
            river_normal:  [0.0; 4],
            river_tangent: [0.0; 4],
            // 水中コースティクス（Phase W5.3）。コンポーネントの値をそのまま写す。
            //   x = 強度（負値は「逆位相の集光」という意味を持たないので 0 で下限を切る）
            //   y = 細かさ倍率（0 以下だと差分ステップが発散するのでシェーダ側でも下限を切るが、
            //       ここでも負値を潰しておく）
            //   z = 深度フェード距離（同上。0 は「即座に消える」ではなく 0 除算になるため）
            //   w = 影の屈折ゆらぎ誇張倍率（0 で従来と同一の影。負値は「逆向きにずらす」という
            //       意味を持たず、影が波の谷へ吸い寄せられる不自然な絵になるので 0 で下限を切る）
            caustics: [
                vis.caustics_intensity.max(0.0),
                vis.caustics_scale.max(0.0),
                vis.caustics_depth_fade.max(0.0),
                vis.shadow_refraction_strength.max(0.0),
            ],
            // 波形ランダマイズ（Phase W6.4）。
            //   x = 強さ（負値は「逆向きにばらす」という意味を持たない。0 で完全無効なので
            //       そこへ倒す＝シェーダ側が早期リターンして W6.3 以前と同一出力になる）
            //   y = ノイズの空間周波数倍率（0 以下はワープ量が発散するので下限を切る）
            //   z,w = 予約（0）
            wave_noise: [
                vis.wave_noise_strength.max(0.0),
                vis.wave_noise_scale.max(WAVE_NOISE_SCALE_MIN),
                0.0,
                0.0,
            ],
        }
    }

    /// 川リボンの 1 分割ぶんの GPU パラメータを作る（Phase W4）。
    ///
    /// 見た目パラメータ・ピッキング ID は水域全体で共通なので `from_resolved` を
    /// そのまま使い、形状（クアッド → リボンの 1 コマ）と流れの情報だけを差し替える。
    /// こうしておくと「川だけ色の扱いが違う」といった分岐が生まれない。
    ///
    /// `a` / `b` は折れ線の隣り合う 2 ノード（上流 → 下流）。
    ///
    /// ショアフィールド（岸波）は川では焼かれないため常に無効化する
    /// （`engine::water::shore` 側でも川は対象外にしてある）。
    pub fn from_river_segment(
        v:          &ResolvedWaterVolume,
        a:          &crate::engine::water::RiverNode,
        b:          &crate::engine::water::RiverNode,
        half_width: f32,
        flow_speed: f32,
        id_base:    u32,
    ) -> Self {
        // カメラ位置は Ocean のクアッド追従にしか使われないので、川では任意でよい。
        let mut p = Self::from_resolved(v, [0.0; 3], id_base, None);
        // 中心は「分割の中点」。フラグメントでは使わないが、デバッグ表示や
        // 将来のソートで意味のある値が入っている方が安全。
        p.center = [
            (a.pos[0] + b.pos[0]) * 0.5,
            (a.pos[1] + b.pos[1]) * 0.5,
            (a.pos[2] + b.pos[2]) * 0.5,
            WATER_INSTANCE_RIVER,
        ];
        // y,w（格子分割数）は `set_grid_divisions` が後から上書きする。
        p.half_extent   = [half_width, 1.0, half_width, 1.0];
        p.river_p0      = [a.pos[0], a.pos[1], a.pos[2], half_width];
        p.river_p1      = [b.pos[0], b.pos[1], b.pos[2], flow_speed];
        p.river_normal  = [a.normal[0], a.normal[1], b.normal[0], b.normal[1]];
        // 関節タンジェント（Phase W6.2）。**弦ではなくノードの平滑化タンジェント**を渡す。
        // 隣接する分割は共有する関節で同じ値を受け取るため、頂点間で補間した流れの向きが
        // 区間境界で連続になり、水面模様の継ぎ目が消える。
        p.river_tangent = [a.tangent[0], a.tangent[1], b.tangent[0], b.tangent[1]];
        p
    }

    /// 格子の分割数を書き込む（Phase W5.1）。
    ///
    /// クアッドなら (X 方向, Z 方向)、川なら (幅方向, 長さ方向)。
    /// 同じバケット（1 ドロー）のインスタンスには**必ず同じ値**を入れること。
    /// 1 ドローの頂点数は 1 つしか指定できないので、値が食い違うと
    /// 「頂点が足りないインスタンス」＝格子の一部が描かれない、が起きる。
    pub fn set_grid_divisions(&mut self, div_x: u32, div_z: u32) {
        self.half_extent[1] = div_x.max(1) as f32;
        self.half_extent[3] = div_z.max(1) as f32;
    }

    /// このインスタンスの格子分割数（`set_grid_divisions` で書いた値）。
    pub fn grid_divisions(&self) -> (u32, u32) {
        (self.half_extent[1] as u32, self.half_extent[3] as u32)
    }
}

// ============================================================
//  テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// std430 のストレージバッファ配列要素として安全なサイズ・アラインメントであること。
    /// vec4 のみで構成しているので 16 の倍数・16 アラインになるはず（暗黙パディング無し）。
    #[test]
    fn water_params_layout_is_std430_safe() {
        assert_eq!(std::mem::size_of::<WaterParams>() % 16, 0,
            "WaterParams のサイズは 16 の倍数であること（std430 の配列ストライド）");
        // Phase W5.3（水中コースティクス）＋ W6.4（波形ランダマイズ）で
        // vec4 を 1 本ずつ足したので 18 本＝288 バイト。
        assert_eq!(std::mem::size_of::<WaterParams>(), 16 * 18,
            "WaterParams は vec4 17 本ぶん（272 バイト）であること。\
             WGSL 側 struct WaterParams（water_height_field.wgsl）と同期すること");
        assert_eq!(std::mem::align_of::<WaterParams>(), 4,
            "repr(C) の [f32;4] 配列なので Rust 側アラインは 4（バイト列は 16 の倍数長で連続する）");
    }

    /// Ocean はカメラ XZ 追従、Region は AABB 中心を使うこと。
    #[test]
    fn ocean_follows_camera_region_uses_center() {
        use crate::engine::water::WaterVisualParams;
        let visual = WaterVisualParams {
            shallow_color: [0.0; 3], deep_color: [0.0; 3], absorption_distance: 1.0,
            surface_opacity: 1.0, foam_color: [0.0; 3], foam_width: 1.0, foam_intensity: 1.0,
            wave_amplitude: 1.0, wave_scale: 1.0, wave_speed: 1.0, wave_direction_deg: 0.0,
            // 波形ランダマイズ（Phase W6.4）。既定相当の値を入れておく。
            wave_noise_strength: 0.35, wave_noise_scale: 1.0, fresnel_power: 1.0,
            fresnel_strength: 1.0, reflection_intensity: 1.0, reflection_roughness: 0.15,
            refraction_distortion: 0.0,
            ripple_strength: 1.0, ripple_foam_threshold: 0.1,
            // 水域ごとの物性（Phase I2.1）。既定相当（＝現行の水）の値を入れておく。
            viscosity: 0.0, ripple_damping: 1.0 / 1.5,
            // 水中コースティクス（Phase W5.3）。既定相当の値を入れておく。
            caustics_intensity: 0.6, caustics_scale: 1.0, caustics_depth_fade: 6.0,
            shadow_refraction_strength: 1.0,
            shore_wave_strength: 0.0, shore_wave_length: 12.0,
            shore_wave_period: 4.0, shore_wave_foam: 0.8,
        };
        let ocean = ResolvedWaterVolume {
            kind: WaterVolumeKind::Ocean, surface_y: 3.0,
            center: [0.0; 3], half_extents: [0.0; 3], ocean_extent: 500.0, visual,
            actor_dfs_id: 0, river: None,
        };
        let p = WaterParams::from_resolved(&ocean, [10.0, 5.0, -20.0], 0, None);
        assert_eq!(p.center, [10.0, 3.0, -20.0, 0.0]);
        // y,w は格子分割数のスロット（Phase W5.1。既定は 1 分割で、描画直前に上書きされる）。
        assert_eq!(p.half_extent, [500.0, 1.0, 500.0, 1.0]);
        // Ocean だけ放射状ワープが有効になる（カメラ追従の巨大クアッドのため）。
        assert_eq!(p.wave_axis[2], WATER_GRID_WARP_ON, "Ocean はワープ有効");

        let region = ResolvedWaterVolume {
            kind: WaterVolumeKind::Region, surface_y: 2.0,
            center: [1.0, 0.0, 2.0], half_extents: [4.0, 1.0, 6.0], ocean_extent: 500.0, visual,
            actor_dfs_id: 0, river: None,
        };
        let q = WaterParams::from_resolved(&region, [10.0, 5.0, -20.0], 0, None);
        assert_eq!(q.center, [1.0, 2.0, 2.0, 0.0]);
        assert_eq!(q.half_extent, [4.0, 1.0, 6.0, 1.0]);
        assert_eq!(q.wave_axis[2], WATER_GRID_WARP_OFF, "Region はワープ無効（一様分割）");
    }

    /// 波紋パラメータは fresnel.zw へ詰められ、危険な値は下限で切られること（Phase I2）。
    /// ここがズレると水面が真っ白（しきい値 0）または無反応（負スケール）になる。
    #[test]
    fn ripple_params_are_packed_into_fresnel_zw_with_guards() {
        use crate::engine::water::WaterVisualParams;
        let visual = WaterVisualParams {
            shallow_color: [0.0; 3], deep_color: [0.0; 3], absorption_distance: 1.0,
            surface_opacity: 1.0, foam_color: [0.0; 3], foam_width: 1.0, foam_intensity: 1.0,
            wave_amplitude: 1.0, wave_scale: 1.0, wave_speed: 1.0, wave_direction_deg: 0.0,
            // 波形ランダマイズ（Phase W6.4）。既定相当の値を入れておく。
            wave_noise_strength: 0.35, wave_noise_scale: 1.0, fresnel_power: 2.0,
            fresnel_strength: 0.5, reflection_intensity: 1.0, reflection_roughness: 0.15,
            refraction_distortion: 0.0,
            ripple_strength: 1.25, ripple_foam_threshold: 0.08,
            // 水域ごとの物性（Phase I2.1）。既定相当（＝現行の水）の値を入れておく。
            viscosity: 0.0, ripple_damping: 1.0 / 1.5,
            // 水中コースティクス（Phase W5.3）。既定相当の値を入れておく。
            caustics_intensity: 0.6, caustics_scale: 1.0, caustics_depth_fade: 6.0,
            shadow_refraction_strength: 1.0,
            shore_wave_strength: 0.0, shore_wave_length: 12.0,
            shore_wave_period: 4.0, shore_wave_foam: 0.8,
        };
        let v = ResolvedWaterVolume {
            kind: WaterVolumeKind::Region, surface_y: 0.0,
            center: [0.0; 3], half_extents: [1.0; 3], ocean_extent: 1.0, visual,
            actor_dfs_id: 0, river: None,
        };
        let p = WaterParams::from_resolved(&v, [0.0; 3], 0, None);
        assert_eq!(p.fresnel, [2.0, 0.5, 1.25, 0.08], "x,y=フレネル / z,w=波紋");

        // 負のスケールと 0 のしきい値は下限で切られる。
        let mut bad = visual;
        bad.ripple_strength = -3.0;
        bad.ripple_foam_threshold = 0.0;
        let vb = ResolvedWaterVolume { visual: bad, ..v };
        let q = WaterParams::from_resolved(&vb, [0.0; 3], 0, None);
        assert_eq!(q.fresnel[2], 0.0, "負の摂動スケールは 0 に切る");
        assert_eq!(q.fresnel[3], RIPPLE_FOAM_THRESHOLD_MIN, "しきい値 0 は下限へ");
    }

    /// 波の方位角は cos/sin へ焼かれて `wave_axis` に入ること（Phase W6.3）。
    ///
    /// 規約（0 = +Z へ進む／正で +X 側へ回る）はシェーダ側の回転式と対で意味を持つ。
    /// ここが壊れると「波の向きだけ 90 度ずれる」という気づきにくい不具合になる。
    #[test]
    fn wave_direction_is_baked_into_cos_sin_axis() {
        use crate::engine::water::WaterVisualParams;
        let visual = WaterVisualParams {
            shallow_color: [0.0; 3], deep_color: [0.0; 3], absorption_distance: 1.0,
            surface_opacity: 1.0, foam_color: [0.0; 3], foam_width: 1.0, foam_intensity: 1.0,
            wave_amplitude: 1.0, wave_scale: 1.0, wave_speed: 1.0, wave_direction_deg: 90.0,
            // 波形ランダマイズ（Phase W6.4）。既定相当の値を入れておく。
            wave_noise_strength: 0.35, wave_noise_scale: 1.0,
            fresnel_power: 1.0,
            fresnel_strength: 1.0, reflection_intensity: 1.0, reflection_roughness: 0.15,
            refraction_distortion: 0.0,
            ripple_strength: 1.0, ripple_foam_threshold: 0.1,
            // 水域ごとの物性（Phase I2.1）。既定相当（＝現行の水）の値を入れておく。
            viscosity: 0.0, ripple_damping: 1.0 / 1.5,
            // 水中コースティクス（Phase W5.3）。既定相当の値を入れておく。
            caustics_intensity: 0.6, caustics_scale: 1.0, caustics_depth_fade: 6.0,
            shadow_refraction_strength: 1.0,
            shore_wave_strength: 0.0, shore_wave_length: 12.0,
            shore_wave_period: 4.0, shore_wave_foam: 0.8,
        };
        let v = ResolvedWaterVolume {
            kind: WaterVolumeKind::Region, surface_y: 0.0,
            center: [0.0; 3], half_extents: [1.0; 3], ocean_extent: 1.0, visual,
            actor_dfs_id: 0, river: None,
        };
        let p = WaterParams::from_resolved(&v, [0.0; 3], 0, None);
        assert!(p.wave_axis[0].abs() < 1e-6, "cos(90°) ≒ 0（実際 {}）", p.wave_axis[0]);
        assert!((p.wave_axis[1] - 1.0).abs() < 1e-6, "sin(90°) = 1（実際 {}）", p.wave_axis[1]);
        // z = 放射状ワープフラグ（Region なので無効）／w は予約（0）。
        assert_eq!([p.wave_axis[2], p.wave_axis[3]], [WATER_GRID_WARP_OFF, 0.0]);

        // 既定 0 度は無回転（cos=1, sin=0）＝旧シーンの見た目がそのまま保たれる。
        let mut zero = visual;
        zero.wave_direction_deg = 0.0;
        let q = WaterParams::from_resolved(&ResolvedWaterVolume { visual: zero, ..v }, [0.0; 3], 0, None);
        assert_eq!([q.wave_axis[0], q.wave_axis[1]], [1.0, 0.0], "0 度は無回転");
    }

    /// 川リボンの関節タンジェントは**隣接インスタンスで一致**すること（Phase W6.2）。
    ///
    /// ここが弦（p1 − p0）に戻ると区間境界で流れの向きが跳び、
    /// ポリゴンの継ぎ目が水面模様として見えるようになる。継ぎ目が消える根拠そのものなので、
    /// 「共有する関節で同じ値が入る」を契約として固定する。
    #[test]
    fn river_tangents_are_shared_between_adjacent_segments() {
        use crate::engine::water::{RiverPath, WaterVisualParams};
        let visual = WaterVisualParams {
            shallow_color: [0.0; 3], deep_color: [0.0; 3], absorption_distance: 1.0,
            surface_opacity: 1.0, foam_color: [0.0; 3], foam_width: 1.0, foam_intensity: 1.0,
            wave_amplitude: 1.0, wave_scale: 1.0, wave_speed: 1.0, wave_direction_deg: 0.0,
            // 波形ランダマイズ（Phase W6.4）。既定相当の値を入れておく。
            wave_noise_strength: 0.35, wave_noise_scale: 1.0,
            fresnel_power: 1.0,
            fresnel_strength: 1.0, reflection_intensity: 1.0, reflection_roughness: 0.15,
            refraction_distortion: 0.0,
            ripple_strength: 1.0, ripple_foam_threshold: 0.1,
            // 水域ごとの物性（Phase I2.1）。既定相当（＝現行の水）の値を入れておく。
            viscosity: 0.0, ripple_damping: 1.0 / 1.5,
            // 水中コースティクス（Phase W5.3）。既定相当の値を入れておく。
            caustics_intensity: 0.6, caustics_scale: 1.0, caustics_depth_fade: 6.0,
            shadow_refraction_strength: 1.0,
            shore_wave_strength: 0.0, shore_wave_length: 12.0,
            shore_wave_period: 4.0, shore_wave_foam: 0.8,
        };
        // わざと曲がって下る川（＝弦の向きが区間ごとに変わる形）。
        let path = RiverPath::build(
            &[[0.0, 4.0, 0.0], [10.0, 2.0, 4.0], [16.0, 0.0, 14.0]],
            4.0, 1.5, 2.0, 2.0,
        ).expect("川が成立すること");
        let v = ResolvedWaterVolume {
            kind: WaterVolumeKind::Spline, surface_y: 0.0,
            center: [0.0; 3], half_extents: [1.0; 3], ocean_extent: 1.0, visual,
            actor_dfs_id: 0, river: Some(path.clone()),
        };
        let segs: Vec<WaterParams> = path.nodes.windows(2)
            .map(|w| WaterParams::from_river_segment(
                &v, &w[0], &w[1], path.half_width, path.flow_speed, 0))
            .collect();
        assert!(segs.len() >= 3, "境界の検証には 3 分割以上ほしい（実際 {}）", segs.len());
        for i in 1..segs.len() {
            // 区間 i-1 の下流端タンジェント（zw）＝ 区間 i の上流端タンジェント（xy）。
            assert_eq!(
                [segs[i - 1].river_tangent[2], segs[i - 1].river_tangent[3]],
                [segs[i].river_tangent[0], segs[i].river_tangent[1]],
                "関節 {i} でタンジェントが不連続（＝区間境界に継ぎ目が出る）",
            );
        }
        // 弦ではなく平滑化タンジェントであること（曲がった川では両者は必ず食い違う）。
        let mid = &segs[segs.len() / 2];
        let chord = [
            mid.river_p1[0] - mid.river_p0[0],
            mid.river_p1[2] - mid.river_p0[2],
        ];
        let chord_len = (chord[0] * chord[0] + chord[1] * chord[1]).sqrt();
        let chord_dir = [chord[0] / chord_len, chord[1] / chord_len];
        assert!(
            (mid.river_tangent[0] - chord_dir[0]).abs() > 1e-4
                || (mid.river_tangent[1] - chord_dir[1]).abs() > 1e-4,
            "曲がった川なのに弦と一致している（＝弦を送ってしまっている）",
        );
    }

    /// 水面シェーダが波紋の場を実際に消費していること（Phase I2 の消費経路の生存確認）。
    /// リファクタで group2 の宣言やサンプル関数が落ちても、静かに「波紋が出ない」に
    /// なるだけで誰も気づかないため、文字列で押さえる。
    #[test]
    fn water_shader_consumes_interaction_field() {
        // Phase W5.1 で「場のサンプル」は共有モジュールへ移った
        // （頂点段からも読むため。連結後は水面パス・ID パスの両方に含まれる）。
        let src = include_str!("../shaders/water_height_field.wgsl");
        assert!(src.contains("@group(2) @binding(0) var  t_interaction:"),
            "水面シェーダが波紋の場（group2）を宣言していない");
        assert!(src.contains("fn water_ripple_gradient("),
            "波紋の勾配（法線摂動）が消えている");
        // 航跡フォームは「色の話」なので水面本体側に残っている。
        assert!(include_str!("../shaders/water_surface.wgsl").contains("ripple_foam"),
            "航跡フォームが消えている");
    }

    /// 粘度は解析波の速度（`wave.z`）を下げること。粘度 0 では**ビット単位で不変**（Phase I2.1）。
    ///
    /// 波紋（インタラクションフィールド）の伝播だけを遅くして解析波を据え置くと、
    /// 「模様はさらさら流れているのに輪だけ止まる」ちぐはぐな絵になる。
    #[test]
    fn viscosity_slows_the_analytic_wave_speed() {
        use crate::engine::water::WaterVisualParams;
        let visual = WaterVisualParams {
            shallow_color: [0.0; 3], deep_color: [0.0; 3], absorption_distance: 1.0,
            surface_opacity: 1.0, foam_color: [0.0; 3], foam_width: 1.0, foam_intensity: 1.0,
            wave_amplitude: 1.0, wave_scale: 1.0, wave_speed: 2.0, wave_direction_deg: 0.0,
            wave_noise_strength: 0.35, wave_noise_scale: 1.0, fresnel_power: 1.0,
            fresnel_strength: 1.0, reflection_intensity: 1.0, reflection_roughness: 0.15,
            refraction_distortion: 0.0,
            ripple_strength: 1.0, ripple_foam_threshold: 0.1,
            viscosity: 0.0, ripple_damping: 1.0 / 1.5,
            caustics_intensity: 0.6, caustics_scale: 1.0, caustics_depth_fade: 6.0,
            shadow_refraction_strength: 1.0,
            shore_wave_strength: 0.0, shore_wave_length: 12.0,
            shore_wave_period: 4.0, shore_wave_foam: 0.8,
        };
        let v = ResolvedWaterVolume {
            kind: WaterVolumeKind::Region, surface_y: 0.0,
            center: [0.0; 3], half_extents: [1.0; 3], ocean_extent: 1.0, visual,
            actor_dfs_id: 0, river: None,
        };
        // 粘度 0（既定）は素の `wave_speed` がそのまま入る（丸めも入らない）。
        let p = WaterParams::from_resolved(&v, [0.0; 3], 0, None);
        assert_eq!(p.wave[2], 2.0, "粘度 0 で波速が変わっている");

        // 粘度を上げると単調に遅くなり、上限（粘度 1）でも 0 にはならない。
        let mut prev = p.wave[2];
        for i in 1..=4 {
            let mut vis = visual;
            vis.viscosity = i as f32 / 4.0;
            let q = WaterParams::from_resolved(
                &ResolvedWaterVolume { visual: vis, ..v.clone() }, [0.0; 3], 0, None);
            assert!(q.wave[2] < prev, "粘度 {} で単調に遅くなっていない", vis.viscosity);
            assert!(q.wave[2] > 0.0, "粘度 {} で波が止まっている", vis.viscosity);
            prev = q.wave[2];
        }
    }

    /// ピッキング用 raw ID は `id_base + DFS + 1`（0 = 背景を空ける共通規約）。
    #[test]
    fn actor_id_follows_id_pass_convention() {
        use crate::engine::water::WaterVisualParams;
        let visual = WaterVisualParams {
            shallow_color: [0.0; 3], deep_color: [0.0; 3], absorption_distance: 1.0,
            surface_opacity: 1.0, foam_color: [0.0; 3], foam_width: 1.0, foam_intensity: 1.0,
            wave_amplitude: 1.0, wave_scale: 1.0, wave_speed: 1.0, wave_direction_deg: 0.0,
            // 波形ランダマイズ（Phase W6.4）。既定相当の値を入れておく。
            wave_noise_strength: 0.35, wave_noise_scale: 1.0, fresnel_power: 1.0,
            fresnel_strength: 1.0, reflection_intensity: 1.0, reflection_roughness: 0.15,
            refraction_distortion: 0.0,
            ripple_strength: 1.0, ripple_foam_threshold: 0.1,
            // 水域ごとの物性（Phase I2.1）。既定相当（＝現行の水）の値を入れておく。
            viscosity: 0.0, ripple_damping: 1.0 / 1.5,
            // 水中コースティクス（Phase W5.3）。既定相当の値を入れておく。
            caustics_intensity: 0.6, caustics_scale: 1.0, caustics_depth_fade: 6.0,
            shadow_refraction_strength: 1.0,
            shore_wave_strength: 0.0, shore_wave_length: 12.0,
            shore_wave_period: 4.0, shore_wave_foam: 0.8,
        };
        let v = ResolvedWaterVolume {
            kind: WaterVolumeKind::Region, surface_y: 0.0,
            center: [0.0; 3], half_extents: [1.0; 3], ocean_extent: 1.0, visual,
            actor_dfs_id: 7, river: None,
        };
        let p = WaterParams::from_resolved(&v, [0.0; 3], 100, None);
        assert_eq!(p.actor_id[0], 108, "id_base(100) + DFS(7) + 1");
    }
}
