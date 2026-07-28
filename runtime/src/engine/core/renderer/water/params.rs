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
/// （1 インスタンス = `WaterParams` 224 バイトなので 1024 個でも 224KB）。
pub const WATER_MAX_INSTANCES: usize = 1024;

/// インスタンス種別（`WaterParams::center.w`）: 軸平行クアッド（Ocean / Region）。
pub const WATER_INSTANCE_QUAD: f32 = 0.0;

/// インスタンス種別（`WaterParams::center.w`）: 川リボンの 1 分割（Phase W4）。
///
/// 頂点シェーダはこの値を見て、`center ± half_extent` の矩形ではなく
/// `river_p0/river_p1/river_normal` から作る四角形（＝リボンの 1 コマ）を生成する。
/// **`water_surface.wgsl` / `water_id.wgsl` の `WATER_INSTANCE_RIVER` と一致必須。**
pub const WATER_INSTANCE_RIVER: f32 = 1.0;

/// 水面クアッド 1 枚を描くための頂点数（三角形 2 枚 = 6 頂点）。
/// 頂点バッファを持たず `draw(0..この値, 0..N)` で描くため、WGSL 側の
/// `WATER_QUAD_VERTEX_COUNT`（water_surface.wgsl / water_id.wgsl）と一致させること。
pub const WATER_QUAD_VERTEX_COUNT: u32 = 6;

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
    /// x,z = クアッドの片側半径（m）／y,w = 未使用
    pub half_extent: [f32; 4],
    /// rgb = 浅場の色／a = 吸収距離（m）
    pub shallow_color: [f32; 4],
    /// rgb = 深場の色／a = 深場での最大不透明度
    pub deep_color: [f32; 4],
    /// rgb = 岸フォームの色／a = フォーム幅（m）
    pub foam_color: [f32; 4],
    /// rgb = 簡易反射色／a = フォーム強度
    pub reflection_color: [f32; 4],
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
        Self {
            center:      [cx, v.surface_y, cz, WATER_INSTANCE_QUAD],
            half_extent: [hx, 0.0, hz, 0.0],
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
            reflection_color: [
                vis.reflection_color[0], vis.reflection_color[1], vis.reflection_color[2],
                vis.foam_intensity,
            ],
            wave: [
                vis.wave_amplitude, vis.wave_scale, vis.wave_speed, vis.refraction_distortion,
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
            // クアッド（Ocean / Region）は川のフィールドを使わない。
            river_p0:     [0.0; 4],
            river_p1:     [0.0; 4],
            river_normal: [0.0; 4],
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
        p.half_extent   = [half_width, 0.0, half_width, 0.0];
        p.river_p0      = [a.pos[0], a.pos[1], a.pos[2], half_width];
        p.river_p1      = [b.pos[0], b.pos[1], b.pos[2], flow_speed];
        p.river_normal  = [a.normal[0], a.normal[1], b.normal[0], b.normal[1]];
        p
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
        assert_eq!(std::mem::size_of::<WaterParams>(), 14 * 16,
            "WaterParams は vec4 14 本ぶん（224 バイト）であること。\
             WGSL 側 struct WaterParams（water_surface.wgsl / water_id.wgsl）と同期すること");
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
            wave_amplitude: 1.0, wave_scale: 1.0, wave_speed: 1.0, fresnel_power: 1.0,
            fresnel_strength: 1.0, reflection_color: [0.0; 3], refraction_distortion: 0.0,
            ripple_strength: 1.0, ripple_foam_threshold: 0.1,
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
        assert_eq!(p.half_extent, [500.0, 0.0, 500.0, 0.0]);

        let region = ResolvedWaterVolume {
            kind: WaterVolumeKind::Region, surface_y: 2.0,
            center: [1.0, 0.0, 2.0], half_extents: [4.0, 1.0, 6.0], ocean_extent: 500.0, visual,
            actor_dfs_id: 0, river: None,
        };
        let q = WaterParams::from_resolved(&region, [10.0, 5.0, -20.0], 0, None);
        assert_eq!(q.center, [1.0, 2.0, 2.0, 0.0]);
        assert_eq!(q.half_extent, [4.0, 0.0, 6.0, 0.0]);
    }

    /// 波紋パラメータは fresnel.zw へ詰められ、危険な値は下限で切られること（Phase I2）。
    /// ここがズレると水面が真っ白（しきい値 0）または無反応（負スケール）になる。
    #[test]
    fn ripple_params_are_packed_into_fresnel_zw_with_guards() {
        use crate::engine::water::WaterVisualParams;
        let visual = WaterVisualParams {
            shallow_color: [0.0; 3], deep_color: [0.0; 3], absorption_distance: 1.0,
            surface_opacity: 1.0, foam_color: [0.0; 3], foam_width: 1.0, foam_intensity: 1.0,
            wave_amplitude: 1.0, wave_scale: 1.0, wave_speed: 1.0, fresnel_power: 2.0,
            fresnel_strength: 0.5, reflection_color: [0.0; 3], refraction_distortion: 0.0,
            ripple_strength: 1.25, ripple_foam_threshold: 0.08,
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

    /// 水面シェーダが波紋の場を実際に消費していること（Phase I2 の消費経路の生存確認）。
    /// リファクタで group2 の宣言やサンプル関数が落ちても、静かに「波紋が出ない」に
    /// なるだけで誰も気づかないため、文字列で押さえる。
    #[test]
    fn water_shader_consumes_interaction_field() {
        let src = include_str!("../shaders/water_surface.wgsl");
        assert!(src.contains("@group(2) @binding(0) var  t_interaction:"),
            "水面シェーダが波紋の場（group2）を宣言していない");
        assert!(src.contains("fn water_ripple_gradient("),
            "波紋の勾配（法線摂動）が消えている");
        assert!(src.contains("ripple_foam"),
            "航跡フォームが消えている");
    }

    /// ピッキング用 raw ID は `id_base + DFS + 1`（0 = 背景を空ける共通規約）。
    #[test]
    fn actor_id_follows_id_pass_convention() {
        use crate::engine::water::WaterVisualParams;
        let visual = WaterVisualParams {
            shallow_color: [0.0; 3], deep_color: [0.0; 3], absorption_distance: 1.0,
            surface_opacity: 1.0, foam_color: [0.0; 3], foam_width: 1.0, foam_intensity: 1.0,
            wave_amplitude: 1.0, wave_scale: 1.0, wave_speed: 1.0, fresnel_power: 1.0,
            fresnel_strength: 1.0, reflection_color: [0.0; 3], refraction_distortion: 0.0,
            ripple_strength: 1.0, ripple_foam_threshold: 0.1,
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
