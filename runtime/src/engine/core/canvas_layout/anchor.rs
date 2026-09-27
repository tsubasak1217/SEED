// ============================================================
//  canvas_layout/anchor.rs — アンカーの基準サイズとオフセットの規則
//
//  2026-09-07 の入れ子アンカーの修正で app/canvas_collect.rs に切り出した共通ヘルパーを、
//  W2-1a でレイアウトの一本化に合わせてここへ移した（中身は変えていない。
//  canvas_collect.rs は従来の呼び出し元のために再公開している）。
//
//  規則:
//    - 最上位ノード（親が居ない）… ビューポートを仮想親として原点位置を決める
//    - 子ノード                   … 親の CanvasComponent 領域 × anchor（親の累積スケールが掛かる）
//    - CanvasComponent を持たない親（Sprite だけのノード等）の子 … anchor は効かない（基準 0）
// ============================================================

/// ルート（トップレベル）キャンバスのアンカーオフセットを計算する共通ヘルパー。
///
/// スプライト描画・キャンバス枠・GPU ピッキング・2D 物理/ドロップ配置のすべてが
/// この関数を共有することで、ルートキャンバスの原点位置を完全に一致させる。
///
/// # 引数
/// - `anchor`: ルートキャンバスの CanvasTransform.anchor（正規化 [0,1]）
/// - `vw` / `vh`: 基準ビューポートサイズ（実効解像度・カメラ参照サイズ等）
/// - `design_space`: ビューポートタブの設計空間表示中か（= edit_view_is_2d）
///
/// # 挙動
/// - `design_space=false`（Play・SS オーバーレイ = 実ゲーム合成）:
///   ortho 原点が画面中央のため、anchor=(0,0) を画面左上へ寄せる目的で `-vp/2` する。
///   `anchor*vp - vp/2` により anchor=0→画面左上・0.5→中央・1→右下となる。
/// - `design_space=true`（ビューポートタブの設計空間編集）:
///   「キャンバスを編集」モードと同様に**キャンバス左上をワールド原点**へ一致させる。
///   センタリング（`-vp/2`）を行わず、anchor=(0,0) のルートキャンバス左上が原点になる。
#[inline]
pub fn root_anchor_offset(anchor: [f32; 2], vw: f32, vh: f32, design_space: bool) -> [f32; 2] {
    if design_space {
        [vw * anchor[0], vh * anchor[1]]
    } else {
        [vw * anchor[0] - vw / 2.0, vh * anchor[1] - vh / 2.0]
    }
}

/// CanvasComponent を持たないノードが子へ渡すアンカー基準サイズ。
///
/// Sprite / Text / SkinnedSprite だけを持つノードは「キャンバス領域」を定義しない。
/// そのため配下のノードの `anchor` は掛ける相手が無く、オフセット 0 ＝ 無効になる。
/// （anchor を親スプライトの寸法基準にはしない。スロットは複数持てるうえ、
///   スプライトの寸法は描画物のサイズであってレイアウト領域ではないため。）
pub const NO_ANCHOR_BASIS: [f32; 2] = [0.0, 0.0];

/// 子ノードへ渡す「アンカー基準サイズ」を決める共通ヘルパー。
///
/// # なぜ関数にするか
/// アンカー基準サイズの `None` は **「最上位ノード（親が居ない）＝ビューポートを
/// 仮想親とする」** という特別な意味を持つ。ここを素直に
/// `my_canvas.map(|cc| [cc.width, cc.height])` と書くと、CanvasComponent を持たない
/// ノード（Sprite など）の**子**にまで `None` が伝播し、その子が「最上位」と
/// 誤判定される。この関数を通すことで「`None` は最上位専用」という不変条件を
/// 呼び出し規約として 1 か所に固定する。
///
/// # 引数
/// - `my_canvas_size`: 自ノードの CanvasComponent の基準サイズ（無ければ None）
///
/// # 戻り値
/// 常に `Some`。CanvasComponent が無いノードでは `Some(NO_ANCHOR_BASIS)` ＝
/// 「子の anchor は効かない」を意味する。
#[inline]
pub fn child_anchor_basis(my_canvas_size: Option<[f32; 2]>) -> Option<[f32; 2]> {
    Some(my_canvas_size.unwrap_or(NO_ANCHOR_BASIS))
}

/// ノード 1 つぶんのアンカーオフセット（親ローカル px）を求める共通ヘルパー。
///
/// # 引数
/// - `parent_basis`: 親から渡されたアンカー基準サイズ。
///   `None` = 最上位ノード（ビューポートが仮想親）／
///   `Some([w,h])` = 親のキャンバス領域（`NO_ANCHOR_BASIS` なら anchor 無効）
/// - `anchor`: 自ノードの正規化アンカー
/// - `parent_cumul_scale`: 親までの累積スケール（子レベルのみ乗算する）
/// - `eff_viewport`: 最上位ノードの基準ビューポートサイズ（無ければオフセット 0）
/// - `design_space`: ビューポートタブの設計空間表示中か
#[inline]
pub fn node_anchor_offset(
    parent_basis: Option<[f32; 2]>,
    anchor: [f32; 2],
    parent_cumul_scale: [f32; 2],
    eff_viewport: Option<[f32; 2]>,
    design_space: bool,
) -> [f32; 2] {
    match parent_basis {
        // 最上位: ビューポートを仮想親として原点位置を決める
        None => eff_viewport.map_or([0.0, 0.0], |[vw, vh]| {
            root_anchor_offset(anchor, vw, vh, design_space)
        }),
        // 子レベル: 親のキャンバス領域 × anchor（位置と同じく親の累積スケールが掛かる）
        Some([pw, ph]) => [
            pw * anchor[0] * parent_cumul_scale[0],
            ph * anchor[1] * parent_cumul_scale[1],
        ],
    }
}
