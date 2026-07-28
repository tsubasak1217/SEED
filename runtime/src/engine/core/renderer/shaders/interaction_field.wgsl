// ============================================================
//  interaction_field.wgsl — 瞬発インタラクションフィールドの更新（Phase I1）
//
//  ## 役割（単一責任）
//  ワールド空間俯瞰の「瞬発場」テクスチャを 1 ディスパッチで更新する。
//  1 回の実行で以下を **同時に** 行う（テクセルごとに独立・原子操作なし）:
//    ① 前フレームの場を読み、カメラ追従による窓のズレを再マップする
//    ② 指数減衰させる（数秒で消える）
//    ③ 各インタラクションソースの「今フレームの速度」を半径内へ焼き込む
//
//  ## なぜ 1 パスなのか（設計判断）
//  「減衰パス → スタンプパス」の 2 本に分けると、スタンプ側が
//  read-modify-write（同一テクスチャの読み書き）を要求する。
//  core WebGPU の storage テクスチャは rgba16float の `read_write` を許さないため、
//  結局 ping-pong が必要になる。ならば **前フレーム=読み / 今フレーム=書き** の
//  ping-pong を 1 本で済ませ、テクセルごとに「減衰値 → 全ソースを合成」まで
//  やり切るのが最も単純かつ高速である（バリアもクリアも不要）。
//  ソース数は高々 INTERACTION_MAX_SOURCES 個なので、テクセル内ループの
//  実コストは無視できる（典型は 1〜数個）。
//
//  ## チャンネル割り当て（I2 への拡張前提）
//    .xy … ワールド XZ 速度ベクトル場（m/s）。**I1 で使用。**
//    .z  … 予約: 波エネルギー（I2 の水面波紋。ソースが立てた波の強さ）
//    .w  … 予約: 種類マスク／将来拡張
//  I1 では .zw は「減衰されるだけで誰も書かない」。I2 は本シェーダの
//  スタンプ節に `.z` への書き込みを足し、水面シェーダが読むだけで済む
//  （フォーマット・パス構成・バインドは一切変えなくてよい）。
//
//  ## 座標系
//  場は「カメラ XZ に追従する一辺 INTERACTION_FIELD_EXTENT_M の正方形の窓」。
//  窓の原点（ワールド XZ 最小）は**テクセル単位にスナップ**されているため、
//  前フレームの窓との差は必ず整数テクセル数になる。よって再マップは
//  バイリニア補間ではなく **整数オフセットの textureLoad** で行える
//  ＝ カメラが動いても場がにじまず、ちらつかない（スナップ移動の要点）。
// ============================================================

// ─── Rust 側と一致必須の定数（interaction/mod.rs のテストが文字列一致で検証する）───

/// コンピュートのワークグループ 1 辺（8×8 = 64 スレッド）。
/// Rust `INTERACTION_FIELD_WORKGROUP_SIZE` と一致必須。
const INTERACTION_FIELD_WORKGROUP_SIZE: u32 = 8u;

// ─── 調整定数（マジックナンバー禁止）─────────────────────────

/// スタンプの減衰カーブ指数。
/// 1 = 円錐状（縁が直線的）、2 = 中心に寄った滑らかな山。
/// 足元がしっかり倒れ、縁は自然にぼける 2 を採用する。
const INTERACTION_STAMP_FALLOFF_POW: f32 = 2.0;

/// 場に書き込める速度の絶対上限（m/s）。
/// CPU 側（velocity.rs::INTERACTION_MAX_SPEED）でも飽和させているが、
/// 複数ソースの重なりで合成後に超えることがあるためシェーダ側でも締める。
const INTERACTION_FIELD_MAX_SPEED: f32 = 20.0;

/// 前フレーム値をこの絶対値未満まで減衰したら 0 に落とす閾値（m/s）。
/// 指数減衰は厳密には 0 にならないため、非正規化数の温床を断ち、
/// 「もう誰も見えないほど弱い場」を確実に消す。
const INTERACTION_FIELD_EPSILON: f32 = 1.0e-3;

// ============================================================
//  バインディング（group 0 のみ。Rust interaction/mod.rs と一致必須）
// ============================================================

/// 場の更新パラメータ。
/// Rust `InteractionFieldUniformGpu` と一致必須（48 バイト）。
///
/// **草シェーダ（grass_gbuffer.wgsl）の group2 も同じ構造体を宣言する。**
/// フィールドの順序・型を変えたら 3 箇所（Rust / 本ファイル / grass_gbuffer.wgsl）を
/// 同時に直すこと（テスト `interaction_uniform_fields_match_grass_shader` が照合する）。
struct InteractionFieldUniform {
    /// 今フレームの窓のワールド XZ 最小（テクセル単位にスナップ済み）。
    origin_xz:      vec2<f32>,
    /// 前フレームの窓のワールド XZ 最小（再マップに使う）。
    prev_origin_xz: vec2<f32>,
    /// 1 テクセルのワールドサイズ（m）。
    texel_size:     f32,
    /// 窓の一辺の逆数（1/m）。ワールド XZ → [0,1] UV 変換に使う（消費側で使用）。
    inv_extent:     f32,
    /// このフレームの減衰係数 exp(-dt/tau)。0 を渡すと場を完全に消去する。
    decay:          f32,
    /// 場の一辺の解像度（テクセル数）。
    resolution:     u32,
    /// 有効なソース数（`u_sources` の先頭から何個読むか）。
    source_count:   u32,
    /// 速度 1 m/s あたりの草の曲げ角（rad·s/m）。**消費側（草）のみが使う。**
    bend_per_speed: f32,
    /// 草の曲げ角の上限（rad）。**消費側（草）のみが使う。**
    max_bend:       f32,
    /// 16 バイト境界へ揃えるためのパディング（未使用）。
    _pad:           f32,
}
@group(0) @binding(0) var<uniform> u_field: InteractionFieldUniform;

/// インタラクションソース 1 個（速度確定済み）。
/// Rust `InteractionSourceGpu` と一致必須（std430 / 32 バイト）。
struct InteractionSourceGpu {
    /// ワールド XZ 位置。
    pos_xz:   vec2<f32>,
    /// ワールド XZ 速度（m/s）。
    vel_xz:   vec2<f32>,
    /// 影響半径（m。> 0 が保証されている）。
    radius:   f32,
    /// 書き込みの強さ（0..1）。
    strength: f32,
    _pad0:    f32,
    _pad1:    f32,
}
@group(0) @binding(1) var<storage, read> u_sources: array<InteractionSourceGpu>;

/// 前フレームの場（ping-pong の読み側）。
@group(0) @binding(2) var src_field: texture_2d<f32>;

/// 今フレームの場（ping-pong の書き側）。
/// rgba16float を使う理由: 単一/2 チャンネルの r16float・rg16float は
/// core WebGPU の storage フォーマットに含まれない（imos_blur.rs と同じ事情）。
/// 余ったチャンネルは上記「チャンネル割り当て」で I2 用に予約する。
@group(0) @binding(3) var dst_field: texture_storage_2d<rgba16float, write>;

// ============================================================
//  ユーティリティ
// ============================================================

/// 前フレームの場から、今フレームのテクセルに対応する値を読む（窓ズレの再マップ）。
///
/// 窓の原点は両フレームともテクセル単位にスナップされているため、差は整数テクセル。
/// 窓の外（前フレームには存在しなかった領域）は 0 を返す＝新しく入ってきた帯は
/// 「場が無かった」ことになり、草が突然倒れることがない。
fn interaction_load_previous(coord: vec2<i32>, res: i32) -> vec4<f32> {
    // 窓原点の差を整数テクセル数へ（スナップ済みなので丸め誤差は ±0.5 テクセル未満）。
    let shift_f = (u_field.origin_xz - u_field.prev_origin_xz) / u_field.texel_size;
    let shift   = vec2<i32>(i32(round(shift_f.x)), i32(round(shift_f.y)));
    let prev    = coord + shift;
    if (prev.x < 0 || prev.y < 0 || prev.x >= res || prev.y >= res) {
        return vec4<f32>(0.0);
    }
    return textureLoad(src_field, prev, 0);
}

/// ソース 1 個の影響の重み（0..1）を返す。半径外は 0。
fn interaction_stamp_weight(world_xz: vec2<f32>, src: InteractionSourceGpu) -> f32 {
    let d = length(world_xz - src.pos_xz);
    if (d >= src.radius) {
        return 0.0;
    }
    // 中心 1 → 縁 0 の正規化距離を指数カーブへ通す。
    let t = 1.0 - d / src.radius;
    return clamp(pow(t, INTERACTION_STAMP_FALLOFF_POW) * src.strength, 0.0, 1.0);
}

// ============================================================
//  コンピュートエントリ
// ============================================================

/// 場を 1 ディスパッチで更新する（再マップ → 減衰 → 全ソースのスタンプ）。
@compute @workgroup_size(INTERACTION_FIELD_WORKGROUP_SIZE, INTERACTION_FIELD_WORKGROUP_SIZE, 1)
fn cs_interaction_field(@builtin(global_invocation_id) gid: vec3<u32>) {
    let res = u_field.resolution;
    // 解像度がワークグループ幅の倍数でない場合の範囲外スレッドを弾く。
    if (gid.x >= res || gid.y >= res) {
        return;
    }
    let coord = vec2<i32>(i32(gid.x), i32(gid.y));

    // ── ① このテクセルのワールド XZ（テクセル中心）──
    let world_xz = u_field.origin_xz
                 + (vec2<f32>(f32(gid.x), f32(gid.y)) + vec2<f32>(0.5)) * u_field.texel_size;

    // ── ② 前フレームの値を再マップして読み、指数減衰させる ──
    var value = interaction_load_previous(coord, i32(res)) * u_field.decay;
    // 減衰しきった微小値は 0 に落とす（非正規化数を残さない）。
    if (length(value.xy) < INTERACTION_FIELD_EPSILON) {
        value = vec4<f32>(0.0, 0.0, value.z, value.w);
    }

    // ── ③ 各ソースの速度を焼き込む ──
    //   加算ではなく「重み付きの上書き（mix）」であることが重要。
    //   加算だと、同じ場所に留まって動き続けるソースの寄与が毎フレーム
    //   積み上がり、1/(1-decay) 倍（60fps で数十倍）に発散する。
    //   mix なら場は「そのソースの速度」へ収束し、離れれば減衰して消える。
    for (var i: u32 = 0u; i < u_field.source_count; i = i + 1u) {
        let src = u_sources[i];
        let w   = interaction_stamp_weight(world_xz, src);
        if (w > 0.0) {
            value = vec4<f32>(mix(value.xy, src.vel_xz, w), value.z, value.w);
        }
    }

    // ── ④ 速度の大きさを飽和させて書き出す ──
    //   複数ソースの重なりでも上限を超えないようにする（草が地面へ潜るのを防ぐ）。
    let speed = length(value.xy);
    if (speed > INTERACTION_FIELD_MAX_SPEED) {
        value = vec4<f32>(value.xy * (INTERACTION_FIELD_MAX_SPEED / speed), value.z, value.w);
    }
    textureStore(dst_field, coord, value);
}
