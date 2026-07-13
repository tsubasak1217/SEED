// ============================================================
// cluster_build.wgsl  —  クラスタ光源リスト構築 compute（Phase C1）
//
// ## 役割（単一責任）
// クラスタ（X×Y タイル × Z 深度スライスの 3D フロクセル）ごとに
// 「そのクラスタに影響しうる局所ライト（point/spot/rect）」を集め、
//   - グローバルなライトインデックスリスト（cb_indices）へ atomic で詰め、
//   - クラスタごとの (offset, count)（cb_grid）を書く。
// フラグメント（lighting_eval.wgsl）はこのリストだけを走査する。
//
// 平行光（directional）は視錐台全体に影響するためクラスタに入れない。
// ライト配列は CPU 側で「平行光を先頭へ」安定分割済みで、
// [0, local_light_offset) が平行光。本 compute は local_light_offset 以降だけを見る。
//
// ## 保守側（棄却しすぎない）の原則
// ライトが誤って落ちると画面が暗くなり、原因の追跡が極めて困難になる。
// そのため:
//   - クラスタ体積は錐台セルではなく、それを包む**ビュー空間 AABB**（広い側）。
//   - ライト体積は**バウンディング球**（spot の円錐も球で包む＝広い側）。
//   - 半径には安全マージン（CLUSTER_LIGHT_BOUND_MARGIN）を掛ける。
//   - 球と AABB が「接する」ケースも交差扱いにする。
// 過剰に含めた場合の代償は減衰計算 1 回ぶんの無駄（結果は 0 寄与）だけである。
//
// ## 連結
//   cluster_common.wgsl（定数・構造体・純関数。バインディングなし）＋ 本ファイル
// で 1 モジュールになる（renderer/pipeline.rs の ClusterBuildPipeline）。
// shader_common.wgsl は連結しない（group 2/4 のバインディングが混入し、
// compute の手動パイプラインレイアウトと食い違うため）。
// そのため GpuLight 構造体は本ファイルへミラーする（下記）。
// ============================================================

/// shader_common.wgsl の GpuLight の**ミラー**（96 バイト・同一レイアウト）。
///
/// compute は shader_common.wgsl を連結できない（上記理由）ため、やむを得ず二重定義する。
/// 両者のフィールド並び・型が一致していることは clustered.rs のユニットテスト
/// （wgsl_gpu_light_struct_mirrors_shader_common）が担保する。
/// フィールドを足すときは shader_common.wgsl / lighting.rs（Rust）と 3 箇所同時に直すこと。
struct GpuLight {
    color:            vec3<f32>,
    intensity:        f32,
    position:         vec3<f32>,
    range:            f32,
    direction:        vec3<f32>,
    kind:             u32,
    inner_cos:        f32,
    outer_cos:        f32,
    rect_half_width:  f32,
    rect_half_height: f32,
    rect_right:       vec3<f32>,
    shadow_index:     f32,
    rect_up:          vec3<f32>,
    soft_radius:      f32,
}

// ─── バインディング（group 0・compute 専用の手動レイアウト）─────
//
// Rust: pipeline.rs の ClusterBuildPipeline::new が同じ並びで BGL を作る。

/// ライト配列（フラグメントと同一の storage buffer を共有）。
@group(0) @binding(0) var<storage, read>       cb_lights:  array<GpuLight>;
/// クラスタパラメータ（メインカメラぶん。フラグメント側 group4 binding9 と同一バッファ）。
@group(0) @binding(1) var<uniform>             cb_params:  ClusterParams;
/// クラスタごとの (offset, count)。CLUSTER_COUNT 要素。
@group(0) @binding(2) var<storage, read_write> cb_grid:    array<ClusterCell>;
/// グローバルライトインデックスリスト。容量 = CLUSTER_COUNT * MAX_LIGHTS_PER_CLUSTER。
@group(0) @binding(3) var<storage, read_write> cb_indices: array<u32>;
/// リスト詰め込み位置のアトミックカーソル（毎フレーム CPU が 0 クリアする）。
@group(0) @binding(4) var<storage, read_write> cb_cursor:  atomic<u32>;

// ─── ワークグループ ──────────────────────────────────────────
//
// 1 スレッド = 1 クラスタ。4×4×4 = 64 スレッド／ワークグループ。
// Rust の CLUSTER_WG_X/Y/Z（dispatch のグループ数計算に使う）と一致させること。

/// 局所ライト i がクラスタ AABB に影響するか（保守的なバウンディング球 vs AABB）。
fn light_affects_cluster(i: u32, aabb: ClusterAabb) -> bool {
    let light  = cb_lights[i];
    let bounds = cluster_light_bounds(
        light.kind, light.position, light.range,
        light.rect_half_width, light.rect_half_height,
    );
    // w <= 0 = 平行光（クラスタ対象外）。CPU 側の分割で本来ここには来ないが、
    // 二重の安全網として弾く（平行光は別枠で常時評価されるため落ちない）。
    if bounds.w <= 0.0 { return false; }
    // AABB はビュー空間なので、ライト中心もビュー空間へ移す。
    let center_view = (cb_params.view * vec4<f32>(bounds.xyz, 1.0)).xyz;
    return sphere_aabb_intersects(center_view, bounds.w, aabb.min, aabb.max);
}

@compute @workgroup_size(4, 4, 4)
fn cs_main(@builtin(global_invocation_id) gid: vec3<u32>) {
    // グリッド外スレッド（分割数がワークグループサイズの倍数でない場合）は何もしない。
    if gid.x >= CLUSTER_TILES_X || gid.y >= CLUSTER_TILES_Y || gid.z >= CLUSTER_SLICES_Z {
        return;
    }
    let ci   = cluster_flat_index(gid.x, gid.y, gid.z);
    let aabb = cluster_view_aabb(gid.x, gid.y, gid.z, cb_params);

    // 走査範囲: [local_light_offset, light_count)（＝局所ライトのみ）。
    let light_end   = min(cb_params.light_count, arrayLength(&cb_lights));
    let light_begin = min(cb_params.local_light_offset, light_end);

    // ── パス 1: 該当ライト数を数える ──────────────────────────
    // 1 スレッドに 256 要素のローカル配列を持たせると private メモリが溢れる
    // （64 スレッド × 1KB）ため、2 パス走査でローカルバッファを持たない設計にする。
    var count: u32 = 0u;
    for (var i: u32 = light_begin; i < light_end; i = i + 1u) {
        if light_affects_cluster(i, aabb) { count = count + 1u; }
    }
    count = min(count, MAX_LIGHTS_PER_CLUSTER);

    // ── 予約: グローバルリストへ count 個ぶんの領域を atomic で確保 ──
    // 総和は CLUSTER_COUNT * MAX_LIGHTS_PER_CLUSTER = プール容量以下なので溢れない。
    // それでも念のため容量チェックし、超えるなら 0 件（＝そのクラスタは局所ライト無し）
    // ではなく「入る分だけ」に切り詰める（暗転を最小化する）。
    var offset: u32 = 0u;
    if count > 0u {
        offset = atomicAdd(&cb_cursor, count);
        let capacity = arrayLength(&cb_indices);
        if offset >= capacity {
            count = 0u;
        } else if offset + count > capacity {
            count = capacity - offset;
        }
    }
    cb_grid[ci] = ClusterCell(offset, count);

    // ── パス 2: 実際にインデックスを書き込む ────────────────────
    var written: u32 = 0u;
    for (var i: u32 = light_begin; i < light_end && written < count; i = i + 1u) {
        if light_affects_cluster(i, aabb) {
            cb_indices[offset + written] = i;
            written = written + 1u;
        }
    }
}
