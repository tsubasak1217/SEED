// ============================================================
//  tonemap_ops.wgsl — トーンマップ演算子（純関数・バインディングなし）
//
//  HDR リニア色 → 表示リニア色（[0,1] 近傍）へ圧縮する純関数群。
//  バインディングを一切宣言しないため、post_tonemap.wgsl（フルスクリーン
//  トーンマップパス）とカメラプレビューブリット（camera_preview_blit.wgsl）の
//  双方から連結（include）して共用できる。
//
//  演算子は uniform / 定数で切り替え可能な設計にしてある（将来 ACES・
//  Uncharted2 等を追加する土台）。現状は輝度ベース Reinhard のみで、
//  従来 shader_fragment.wgsl 内にあった実装と同一式（見た目維持）。
// ============================================================

// ─── 演算子 ID（CPU 側 TonemapOperator と一致させること）───────────
/// 輝度ベース Reinhard（現行の見た目を維持する既定演算子）。
const TONEMAP_REINHARD_LUMA: u32 = 0u;
/// 素通し（トーンマップ無し）。デバッグ表示（G-Buffer 可視化）で
/// 「G-Buffer に入っている値そのもの」を画面へ出すために使う。
/// 通常の描画では使わない（Reinhard が既定）。
const TONEMAP_NONE: u32 = 1u;
// 将来追加予定: TONEMAP_ACES = 2u, TONEMAP_UNCHARTED2 = 3u ...（R4 以降）

// ─── 演算子実装 ────────────────────────────────────────────────

/// 輝度ベース Reinhard。
/// チャンネル毎 Reinhard では高輝度時に彩度が失われるため、輝度で Reinhard した
/// スケールを元色に乗算して色相を保持する（旧 shader_fragment.wgsl と同一）。
///   luma   = dot(hdr, (0.2126, 0.7152, 0.0722))
///   mapped = hdr * (1 / (luma + 1))
fn tonemap_reinhard_luma(hdr: vec3<f32>) -> vec3<f32> {
    let luma = dot(hdr, vec3<f32>(0.2126, 0.7152, 0.0722));
    return hdr * (1.0 / (luma + 1.0));
}

/// 演算子ディスパッチ。op に応じてトーンマップを適用する。
/// 未知の op は既定（Reinhard）にフォールバックする。
fn tonemap_apply(hdr: vec3<f32>, op: u32) -> vec3<f32> {
    // TONEMAP_NONE は入力をそのまま返す（デバッグ表示用の素通し）。
    // それ以外（未知の op を含む）は既定の Reinhard へ倒す。
    if (op == TONEMAP_NONE) {
        return hdr;
    }
    return tonemap_reinhard_luma(hdr);
}
