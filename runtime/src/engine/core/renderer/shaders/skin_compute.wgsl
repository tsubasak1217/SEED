// ============================================================
//  skin_compute.wgsl — GPU スキニング計算 コンピュートシェーダ
//
//  1 スレッド = 1 インスタンス。
//  各スレッドが独立して:
//    ① バインドポーズ TRS をロード
//    ② 現在クリップ B のチャンネルを補間して TRS をオーバーライド
//    ②' フェード中（weight < 1）なら フェード元クリップ A も同様に評価し、
//        TRS を per-node で補間（位置/スケール=lerp、回転=符号合わせ付き nlerp）
//    ③ BFS 順序でワールド行列を計算
//    ④ ジョイント行列 = world[node] * ibm を出力バッファへ書き込む
//
//  group 0: フレームごとのデータ（インスタンスごとの再生指定）
//  group 1: 静的アニメーションデータ（ロード時に 1 回だけ転送。全アニメ連結済み）
//  group 2: 出力（ジョイント行列バッファ）
//
//  【複数アニメのパッキング】チャンネル列 `channels` はモデル内の全アニメを連結した 1 本で、
//  どの範囲がどのアニメかは `anims[]`（chan_offset / chan_count）が持つ。インスタンスは
//  `anim_samples[]` でアニメ index を指定するため、同じバッチ内で 1 体ごとに別のアニメを
//  再生できる（従来は animations[0] 固定だった）。
// ============================================================

const MAX_NODES:  u32 = 64u;
const MAX_JOINTS: u32 = 128u;

// ── Group 0: フレームごとのデータ ────────────────────────────

/// インスタンス 1 体ぶんの再生指定（コンパクト順）。
/// A = フェード元 / B = 現在クリップ。最終ポーズ = mix(pose_A, pose_B, weight)。
struct AnimSample {
    anim_a: u32,
    anim_b: u32,
    time_a: f32,
    time_b: f32,
    weight: f32,   // 0 = A のみ / 1 = B のみ（フェード無し）
    _pad0:  u32,
    _pad1:  u32,
    _pad2:  u32,
}
@group(0) @binding(0) var<storage, read> anim_samples: array<AnimSample>;

struct SkinParams {
    n_nodes:    u32,   // ノード数（≤ MAX_NODES）
    n_joints:   u32,   // ジョイント数（≤ MAX_JOINTS）
    n_channels: u32,   // 連結チャンネル列の総数
    n_visible:  u32,   // このディスパッチで処理するインスタンス数
    n_anims:    u32,   // パッキング済みアニメ本数
    _pad0:      u32,
    _pad1:      u32,
    _pad2:      u32,
}
@group(0) @binding(1) var<uniform> params: SkinParams;

// ── Group 1: 静的アニメーションデータ ────────────────────────

/// アニメーションチャンネルの情報
struct ChannelInfo {
    target_node: u32,  // 対象ノードインデックス
    prop_type:   u32,  // 0=translation, 1=rotation, 2=scale
    ts_offset:   u32,  // timestamps バッファ内オフセット
    ts_count:    u32,  // キーフレーム数
    val_offset:  u32,  // 値バッファ内オフセット
    interp:      u32,  // 0=LINEAR, 1=STEP, 2=CUBICSPLINE
    _pad0:       u32,
    _pad1:       u32,
}

@group(1) @binding(0)  var<storage, read> bind_t:      array<vec4<f32>>;   // バインドポーズ平行移動 xyz+0
@group(1) @binding(1)  var<storage, read> bind_r:      array<vec4<f32>>;   // バインドポーズ回転 xyzw
@group(1) @binding(2)  var<storage, read> bind_s:      array<vec4<f32>>;   // バインドポーズスケール xyz+1
@group(1) @binding(3)  var<storage, read> channels:    array<ChannelInfo>;
@group(1) @binding(4)  var<storage, read> timestamps:  array<f32>;
@group(1) @binding(5)  var<storage, read> trans_vals:  array<vec4<f32>>;   // 平行移動キー値 xyz+0
@group(1) @binding(6)  var<storage, read> rot_vals:    array<vec4<f32>>;   // 回転キー値 xyzw
@group(1) @binding(7)  var<storage, read> scale_vals:  array<vec4<f32>>;   // スケールキー値 xyz+1
@group(1) @binding(8)  var<storage, read> bfs_order:   array<u32>;          // BFS ノード走査順
@group(1) @binding(9)  var<storage, read> parents:     array<i32>;          // 親ノードインデックス（-1=ルート）
@group(1) @binding(10) var<storage, read> joint_nodes: array<u32>;          // ジョイント→ノードインデックス
@group(1) @binding(11) var<storage, read> ibm:         array<mat4x4<f32>>;  // インバースバインド行列（列優先）

/// アニメーション 1 本ぶんのテーブルエントリ（連結チャンネル列の範囲）。
struct AnimInfo {
    chan_offset: u32,
    chan_count:  u32,
    duration:    f32,   // シェーダ側では未使用（時刻正規化は CPU 側で完了している）
    _pad:        u32,
}
// 固定長 uniform（Rust 側 MAX_ANIMS と一致させること）。有効長は params.n_anims。
@group(1) @binding(12) var<uniform> anims: array<AnimInfo, 64>;

// ── Group 2: 出力 ─────────────────────────────────────────────

/// ジョイント行列出力バッファ（列優先）
/// インデックス: inst_idx * MAX_JOINTS + joint_idx
@group(2) @binding(0) var<storage, read_write> joint_matrices: array<mat4x4<f32>>;

// ── Per-invocation 作業変数 ───────────────────────────────────

var<private> node_t: array<vec4<f32>, 64>;
var<private> node_r: array<vec4<f32>, 64>;
var<private> node_s: array<vec4<f32>, 64>;
var<private> world:  array<mat4x4<f32>, 64>;

// フェード元クリップ A のポーズ（weight < 1 のときだけ使う）。
// 【なぜ 2 セット必要か】ブレンドは per-node の TRS レベルで
//   pose = mix(pose_A[node], pose_B[node], w)
// と定義される。「B を書いた配列へ A を上書き混合」する 1 セット方式では、
// A だけが動かすノード（B がバインドポーズのままのノード）を
// バインドポーズ側へ戻せず、腕だけ元アニメに取り残される破綻が出る。
var<private> alt_t: array<vec4<f32>, 64>;
var<private> alt_r: array<vec4<f32>, 64>;
var<private> alt_s: array<vec4<f32>, 64>;

// ============================================================
//  補間ヘルパー関数
// ============================================================

/// タイムスタンプ配列から補間区間を二分探索で求める。
/// 戻り値: vec2(i0, i1) — 絶対インデックス
fn find_interval(ts_off: u32, ts_cnt: u32, t: f32) -> vec2<u32> {
    if ts_cnt == 0u { return vec2(ts_off, ts_off); }
    let last = ts_off + ts_cnt - 1u;
    if t <= timestamps[ts_off] { return vec2(ts_off, ts_off); }
    if t >= timestamps[last]   { return vec2(last, last); }
    var lo = ts_off;
    var hi = last;
    for (var iter = 0u; iter < 32u; iter++) {
        if hi - lo <= 1u { break; }
        let mid = (lo + hi) >> 1u;
        if timestamps[mid] <= t { lo = mid; } else { hi = mid; }
    }
    return vec2(lo, hi);
}

fn interp_alpha(i0: u32, i1: u32, t: f32) -> f32 {
    let dt = timestamps[i1] - timestamps[i0];
    if dt < 1e-7 { return 0.0; }
    return clamp((t - timestamps[i0]) / dt, 0.0, 1.0);
}

/// クォータニオンの正規化線形補間（最短経路補正付き）
fn nlerp(a: vec4<f32>, b: vec4<f32>, alpha: f32) -> vec4<f32> {
    let bb = select(b, -b, dot(a, b) < 0.0);
    return normalize(mix(a, bb, alpha));
}

/// translation / scale チャンネルをサンプリング（vec4 で返す）
fn sample_tvec(ts_off: u32, ts_cnt: u32, val_off: u32, interp: u32, t: f32) -> vec4<f32> {
    let ii    = find_interval(ts_off, ts_cnt, t);
    let i0    = ii.x; let i1 = ii.y;
    let alpha = interp_alpha(i0, i1, t);
    let is_cubic = interp == 2u;
    let ci0 = select(val_off + (i0 - ts_off),       val_off + (i0 - ts_off) * 3u + 1u, is_cubic);
    let ci1 = select(val_off + (i1 - ts_off),       val_off + (i1 - ts_off) * 3u + 1u, is_cubic);
    if interp == 1u { return trans_vals[ci0]; }  // Step
    return mix(trans_vals[ci0], trans_vals[ci1], alpha);
}

fn sample_svec(ts_off: u32, ts_cnt: u32, val_off: u32, interp: u32, t: f32) -> vec4<f32> {
    let ii    = find_interval(ts_off, ts_cnt, t);
    let i0    = ii.x; let i1 = ii.y;
    let alpha = interp_alpha(i0, i1, t);
    let is_cubic = interp == 2u;
    let ci0 = select(val_off + (i0 - ts_off),       val_off + (i0 - ts_off) * 3u + 1u, is_cubic);
    let ci1 = select(val_off + (i1 - ts_off),       val_off + (i1 - ts_off) * 3u + 1u, is_cubic);
    if interp == 1u { return scale_vals[ci0]; }  // Step
    return mix(scale_vals[ci0], scale_vals[ci1], alpha);
}

fn sample_rvec(ts_off: u32, ts_cnt: u32, val_off: u32, interp: u32, t: f32) -> vec4<f32> {
    let ii    = find_interval(ts_off, ts_cnt, t);
    let i0    = ii.x; let i1 = ii.y;
    let alpha = interp_alpha(i0, i1, t);
    let is_cubic = interp == 2u;
    let ci0 = select(val_off + (i0 - ts_off),       val_off + (i0 - ts_off) * 3u + 1u, is_cubic);
    let ci1 = select(val_off + (i1 - ts_off),       val_off + (i1 - ts_off) * 3u + 1u, is_cubic);
    if interp == 1u { return rot_vals[ci0]; }  // Step
    return nlerp(rot_vals[ci0], rot_vals[ci1], alpha);
}

// ============================================================
//  TRS → 列優先 mat4x4 変換
// ============================================================

/// クォータニオン [x,y,z,w] とスケール、平行移動から列優先行列を生成する。
fn trs_to_mat(t: vec3<f32>, r: vec4<f32>, s: vec3<f32>) -> mat4x4<f32> {
    let x = r.x; let y = r.y; let z = r.z; let w = r.w;
    let x2 = x+x; let y2 = y+y; let z2 = z+z;
    let xx = x*x2; let yy = y*y2; let zz = z*z2;
    let xy = x*y2; let xz = x*z2; let yz = y*z2;
    let wx = w*x2; let wy = w*y2; let wz = w*z2;

    // mat4x4<f32>(col0, col1, col2, col3)
    return mat4x4<f32>(
        vec4((1.0-yy-zz)*s.x, (xy+wz)*s.x,     (xz-wy)*s.x,     0.0),
        vec4((xy-wz)*s.y,     (1.0-xx-zz)*s.y, (yz+wx)*s.y,     0.0),
        vec4((xz+wy)*s.z,     (yz-wx)*s.z,     (1.0-xx-yy)*s.z, 0.0),
        vec4(t.x,             t.y,             t.z,             1.0),
    );
}

// ============================================================
//  1 クリップぶんのポーズ評価
// ============================================================

/// アニメ `anim_idx` を時刻 `t` で評価し、TRS 配列（primary=node_* / secondary=alt_*）へ書く。
///
/// バインドポーズで初期化してから、そのアニメが持つチャンネルだけを上書きする。
/// チャンネル範囲はアニメテーブル（anims[]）から引くので、連結された他アニメには触れない。
fn eval_pose(anim_idx: u32, t: f32, n_nodes: u32, secondary: bool) {
    // ── バインドポーズで初期化 ──
    for (var i = 0u; i < n_nodes; i++) {
        if secondary {
            alt_t[i] = bind_t[i];
            alt_r[i] = bind_r[i];
            alt_s[i] = bind_s[i];
        } else {
            node_t[i] = bind_t[i];
            node_r[i] = bind_r[i];
            node_s[i] = bind_s[i];
        }
    }

    // アニメ本数 0（パッキング失敗）や範囲外 index はチャンネル適用をスキップし、
    // バインドポーズのまま返す（描画が消えるより静止の方が安全）。
    if params.n_anims == 0u || anim_idx >= params.n_anims { return; }

    let info  = anims[anim_idx];
    let begin = info.chan_offset;
    // 連結列の総数でも頭打ちにして、テーブル不整合でも範囲外を読まないようにする。
    let end   = min(begin + info.chan_count, params.n_channels);

    for (var ci = begin; ci < end; ci++) {
        let ch = channels[ci];
        switch ch.prop_type {
            case 0u: {  // translation
                let v = sample_tvec(ch.ts_offset, ch.ts_count, ch.val_offset, ch.interp, t);
                if secondary { alt_t[ch.target_node] = v; } else { node_t[ch.target_node] = v; }
            }
            case 1u: {  // rotation
                let v = sample_rvec(ch.ts_offset, ch.ts_count, ch.val_offset, ch.interp, t);
                if secondary { alt_r[ch.target_node] = v; } else { node_r[ch.target_node] = v; }
            }
            case 2u: {  // scale
                let v = sample_svec(ch.ts_offset, ch.ts_count, ch.val_offset, ch.interp, t);
                if secondary { alt_s[ch.target_node] = v; } else { node_s[ch.target_node] = v; }
            }
            default: {}
        }
    }
}

// ============================================================
//  メインエントリポイント
// ============================================================

@compute @workgroup_size(64)
fn cs_main(@builtin(global_invocation_id) gid: vec3<u32>) {
    let inst_idx = gid.x;
    if inst_idx >= params.n_visible { return; }

    let smp      = anim_samples[inst_idx];
    let w        = clamp(smp.weight, 0.0, 1.0);
    let n_nodes  = params.n_nodes;
    let n_joints = params.n_joints;

    // ── ① 現在クリップ B のポーズ（バインドポーズ初期化 + チャンネル適用）──
    eval_pose(smp.anim_b, smp.time_b, n_nodes, false);

    // ── ②' クロスフェード: フェード元 A を評価して per-node で補間する ──
    //   weight = 1（フェード無し）ではこの分岐に入らないため、
    //   従来の単一クリップ再生とビット単位で同じ結果になる。
    if w < 1.0 {
        eval_pose(smp.anim_a, smp.time_a, n_nodes, true);
        for (var i = 0u; i < n_nodes; i++) {
            node_t[i] = mix(alt_t[i], node_t[i], w);
            node_s[i] = mix(alt_s[i], node_s[i], w);
            // 回転は符号合わせ付きの正規化線形補間（最短経路）
            node_r[i] = nlerp(alt_r[i], node_r[i], w);
        }
    }

    // ── ③ BFS 順序でワールド行列を計算 ──────────────────────────
    for (var bi = 0u; bi < n_nodes; bi++) {
        let ni  = bfs_order[bi];
        let lm  = trs_to_mat(node_t[ni].xyz, node_r[ni], node_s[ni].xyz);
        let pi  = parents[ni];
        if pi < 0 {
            world[ni] = lm;
        } else {
            world[ni] = world[u32(pi)] * lm;
        }
    }

    // ── ④ ジョイント行列 = world[node] * ibm を出力 ──────────────
    let base = inst_idx * MAX_JOINTS;
    for (var ji = 0u; ji < n_joints; ji++) {
        let ni = joint_nodes[ji];
        joint_matrices[base + ji] = world[ni] * ibm[ji];
    }
    // 余剰スロットを単位行列で埋める
    let id = mat4x4<f32>(
        vec4(1.0,0.0,0.0,0.0), vec4(0.0,1.0,0.0,0.0),
        vec4(0.0,0.0,1.0,0.0), vec4(0.0,0.0,0.0,1.0),
    );
    for (var ji = n_joints; ji < MAX_JOINTS; ji++) {
        joint_matrices[base + ji] = id;
    }
}
