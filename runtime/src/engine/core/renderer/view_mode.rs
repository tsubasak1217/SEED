// ============================================================
//  view_mode.rs — エディタのシーンビュー表示モード
//
//  エディタのシーンビュー（デバッグカメラ）専用の描画モードを表す。
//  ゲームカメラのプレビュー小窓・Play 時の見た目には一切影響させない
//  （分離の実装根拠は lighting.rs の LightMeta 二重化と frame_renderer の
//   Edit モードゲート・LightingPass 選択を参照）。
//
//  3 モード:
//    - Lit       : 現行どおりのフル PBR ライティング（既定）。
//    - Unlit     : ライティング計算なし。アルベド（頂点カラー・ベースカラー畳み込み済み）
//                  ＋エミッシブのフラット表示。lighting_eval.wgsl の分岐が実体。
//    - Wireframe : メッシュを線で表示（PolygonMode::Line）。色はアンリット。
//                  POLYGON_MODE_LINE 非対応 GPU では Unlit へフォールバックする。
//
//  データドリブン: エディタからは文字列（"lit"|"unlit"|"wireframe"）で受け取り、
//  シェーダへは u32 コード（Lit=0 / Unlit=1 / Wireframe=2）で渡す。
//  シェーダ側の分岐は「0 以外はアンリット」なので、Unlit と Wireframe の
//  シェーディングは同一（違いはパイプラインの PolygonMode だけ）。
// ============================================================

use std::sync::atomic::{AtomicBool, Ordering};

// ─── SceneViewMode ───────────────────────────────────────────

/// エディタのシーンビュー（デバッグカメラ）の表示モード。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum SceneViewMode {
    /// フル PBR ライティング（既定）。
    #[default]
    Lit,
    /// アンリット（ライティングなし・アルベド＋エミッシブのフラット表示）。
    Unlit,
    /// ワイヤーフレーム（線描画）。色はアンリット。
    Wireframe,
}

impl SceneViewMode {
    /// シェーダ（LightMeta.view_mode）へ渡す u32 コード。
    ///
    /// lighting_eval.wgsl は「0 以外ならアンリット」で分岐するため、
    /// Unlit / Wireframe はともに 0 以外にする。値そのものは順序のみ意味を持つ。
    pub fn to_code(self) -> u32 {
        match self {
            SceneViewMode::Lit       => 0,
            SceneViewMode::Unlit     => 1,
            SceneViewMode::Wireframe => 2,
        }
    }

    /// エディタから受け取る文字列（IPC の SET_POST_FX JSON）を解釈する。
    /// 未知・欠落時は安全側（Lit）へフォールバックする。
    pub fn from_str(s: &str) -> Self {
        match s {
            "unlit"     => SceneViewMode::Unlit,
            "wireframe" => SceneViewMode::Wireframe,
            _           => SceneViewMode::Lit,
        }
    }

    /// このモードがワイヤーフレーム描画（PolygonMode::Line）を要求するか。
    pub fn is_wireframe(self) -> bool {
        matches!(self, SceneViewMode::Wireframe)
    }

    /// このモードがフル PBR ライティング（Lit）かどうか。
    ///
    /// デファード（G-Buffer + フルスクリーン・ライティング）は Lit 専用パスであり、
    /// Unlit／Wireframe はフォワードへフォールバックする（frame_renderer.rs の
    /// deferred_active 判定を参照）。
    pub fn is_lit(self) -> bool {
        matches!(self, SceneViewMode::Lit)
    }
}

// ─── ワイヤーフレーム対応フラグ（GPU フィーチャー依存）──────────

/// POLYGON_MODE_LINE に対応した GPU かどうか（起動時に一度だけ確定）。
///
/// renderer::mod.rs のデバイス生成時に `set_wireframe_supported` で設定する。
/// 非対応時はワイヤ用パイプラインを生成せず、ワイヤ選択時も Unlit 表示へ
/// フォールバックする（クラッシュさせない）。
static WIREFRAME_SUPPORTED: AtomicBool = AtomicBool::new(false);

/// ワイヤーフレーム（PolygonMode::Line）対応可否を設定する（起動時 1 回）。
pub fn set_wireframe_supported(v: bool) {
    WIREFRAME_SUPPORTED.store(v, Ordering::Relaxed);
}

/// ワイヤーフレーム（PolygonMode::Line）対応可否を取得する。
pub fn wireframe_supported() -> bool {
    WIREFRAME_SUPPORTED.load(Ordering::Relaxed)
}
