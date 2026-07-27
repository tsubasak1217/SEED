// ============================================================
//  view_mode.rs — エディタのシーンビュー表示モード
//
//  エディタのシーンビュー（デバッグカメラ）専用の描画モードを表す。
//  ゲームカメラのプレビュー小窓・Play 時の見た目には一切影響させない
//  （分離の実装根拠は lighting.rs の LightMeta 二重化と frame_renderer の
//   Edit モードゲート・LightingPass 選択を参照）。
//
//  モード:
//    - Lit       : 現行どおりのフル PBR ライティング（既定）。
//    - Unlit     : ライティング計算なし。アルベド（頂点カラー・ベースカラー畳み込み済み）
//                  ＋エミッシブのフラット表示。lighting_eval.wgsl の分岐が実体。
//    - Wireframe : メッシュを線で表示（PolygonMode::Line）。色はアンリット。
//                  POLYGON_MODE_LINE 非対応 GPU では Unlit へフォールバックする。
//    - GBuffer(ch): G-Buffer の任意チャンネルをそのまま画面へ出すデバッグ表示。
//                  **デファードを維持したまま**、ライティングのフルスクリーンパスだけを
//                  可視化パス（gbuffer_debug.wgsl）へ差し替えて実現する（後述）。
//
//  データドリブン: エディタからは文字列（"lit"|"unlit"|"wireframe"|"gbuffer_*"）で受け取り、
//  シェーダへは u32 コード（Lit=0 / Unlit=1 / Wireframe=2）で渡す。
//  シェーダ側の分岐は「0 以外はアンリット」なので、Unlit と Wireframe の
//  シェーディングは同一（違いはパイプラインの PolygonMode だけ）。
//
//  ## G-Buffer デバッグ表示を Unlit/Wireframe と同じ仕組みに乗せない理由（重要）
//  `is_lit()` が false になると frame_renderer の `deferred_active` が落ち、
//  フォワード経路になって **G-Buffer 自体が生成されなくなる**。G-Buffer を見たいのに
//  G-Buffer が焼かれない、という自己矛盾になる（velocity デバッグ実装時に確認済み）。
//  そのため G-Buffer 系モードは `is_lit() == true` を返してデファードを維持し、
//  デファード・ライティングのフルスクリーンパスだけを可視化パスへ差し替える。
// ============================================================

use std::sync::atomic::{AtomicBool, Ordering};

// ─── GBufferDebugChannel ─────────────────────────────────────

/// G-Buffer デバッグ表示で可視化するチャンネル。
///
/// 値（u32 キャスト結果）は `shaders/gbuffer_debug.wgsl` の `GB_DEBUG_*` 定数と
/// **厳密に一致させること**（uniform で渡す enum 値そのもの）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u32)]
pub enum GBufferDebugChannel {
    /// ベースカラー（RT0.rgb）。
    BaseColor    = 0,
    /// オクルージョン（RT0.a）。グレースケール。
    Occlusion    = 1,
    /// ワールド法線（RT1.xyz）。0..1 へ再マップして表示。
    Normal       = 2,
    /// ラフネス（RT2.g）。グレースケール。
    Roughness    = 3,
    /// メタリック（RT2.r）。グレースケール。
    Metallic     = 4,
    /// 拡散透過（RT2.b）。グレースケール。
    Transmission = 5,
    /// エミッシブ（RT3.rgb, HDR）。
    Emissive     = 6,
    /// 深度。カメラの near/far で線形化したグレースケール。
    Depth        = 7,
    /// 速度（モーションベクタ RT4）。velocity_debug.wgsl と同じ疑似カラー規約。
    Velocity     = 8,
    /// surface_id のレンダータグ（RT3.a の下位 4bit）。カラーパレット表示。
    RenderTag    = 9,
    /// ユーザーデータ（RT2.a）。グレースケール。
    UserData     = 10,
}

impl GBufferDebugChannel {
    /// シェーダ uniform へ渡す u32 コード。
    pub fn to_code(self) -> u32 {
        self as u32
    }
}

/// エディタ文字列 → G-Buffer デバッグチャンネルの対応表（データドリブンの単一正典）。
///
/// エディタ側 `MainWindow.xaml` の `CmbViewMode` の `Tag` 文字列と 1:1 で対応させること。
/// 文字列は SET_POST_FX の `view_mode` フィールドにそのまま載る。
const GBUFFER_DEBUG_CHANNEL_TABLE: &[(&str, GBufferDebugChannel)] = &[
    ("gbuffer_base_color",   GBufferDebugChannel::BaseColor),
    ("gbuffer_occlusion",    GBufferDebugChannel::Occlusion),
    ("gbuffer_normal",       GBufferDebugChannel::Normal),
    ("gbuffer_roughness",    GBufferDebugChannel::Roughness),
    ("gbuffer_metallic",     GBufferDebugChannel::Metallic),
    ("gbuffer_transmission", GBufferDebugChannel::Transmission),
    ("gbuffer_emissive",     GBufferDebugChannel::Emissive),
    ("gbuffer_depth",        GBufferDebugChannel::Depth),
    ("gbuffer_velocity",     GBufferDebugChannel::Velocity),
    ("gbuffer_render_tag",   GBufferDebugChannel::RenderTag),
    ("gbuffer_user_data",    GBufferDebugChannel::UserData),
];

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
    /// G-Buffer の指定チャンネルをそのまま表示するデバッグモード。
    /// デファードは維持され、ライティングパスだけが可視化パスへ差し替わる。
    GBuffer(GBufferDebugChannel),
}

impl SceneViewMode {
    /// シェーダ（LightMeta.view_mode）へ渡す u32 コード。
    ///
    /// lighting_eval.wgsl は「0 以外ならアンリット」で分岐するため、
    /// Unlit / Wireframe はともに 0 以外にする。値そのものは順序のみ意味を持つ。
    ///
    /// G-Buffer デバッグは **0（Lit）** を返す。可視化はライティングパスの差し替えで
    /// 行うため、G-Buffer 書き込みやフォワードのシェーディング分岐へは一切影響させない
    /// （＝通常の Lit と完全に同じ G-Buffer が焼かれる＝見ている値が本番と同一である保証）。
    pub fn to_code(self) -> u32 {
        match self {
            SceneViewMode::Lit         => 0,
            SceneViewMode::Unlit       => 1,
            SceneViewMode::Wireframe   => 2,
            SceneViewMode::GBuffer(_)  => 0,
        }
    }

    /// エディタから受け取る文字列（IPC の SET_POST_FX JSON）を解釈する。
    /// 未知・欠落時は安全側（Lit）へフォールバックする。
    pub fn from_str(s: &str) -> Self {
        match s {
            "unlit"     => SceneViewMode::Unlit,
            "wireframe" => SceneViewMode::Wireframe,
            other => {
                // G-Buffer デバッグ表示（対応表に載っていれば採用）。
                for (name, ch) in GBUFFER_DEBUG_CHANNEL_TABLE {
                    if *name == other {
                        return SceneViewMode::GBuffer(*ch);
                    }
                }
                SceneViewMode::Lit
            }
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
    ///
    /// **G-Buffer デバッグ表示は true を返す**。デファードを維持しないと
    /// 可視化対象の G-Buffer 自体が生成されないため（モジュール冒頭コメント参照）。
    pub fn is_lit(self) -> bool {
        matches!(self, SceneViewMode::Lit | SceneViewMode::GBuffer(_))
    }

    /// G-Buffer デバッグ表示なら可視化チャンネルを返す（それ以外は None）。
    ///
    /// frame_renderer はこれが Some のとき、デファード・ライティングのフルスクリーンパスを
    /// 可視化パスへ差し替え、後段のポストエフェクト群をスキップする。
    pub fn gbuffer_debug_channel(self) -> Option<GBufferDebugChannel> {
        match self {
            SceneViewMode::GBuffer(ch) => Some(ch),
            _ => None,
        }
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

// ============================================================
//  テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// 既存 3 モードの文字列解釈が変わっていないこと（G-Buffer 追加の巻き添え防止）。
    #[test]
    fn legacy_view_modes_still_parse() {
        assert_eq!(SceneViewMode::from_str("lit"),       SceneViewMode::Lit);
        assert_eq!(SceneViewMode::from_str("unlit"),     SceneViewMode::Unlit);
        assert_eq!(SceneViewMode::from_str("wireframe"), SceneViewMode::Wireframe);
        // 未知文字列・空文字は安全側（Lit）へ倒れる。
        assert_eq!(SceneViewMode::from_str(""),          SceneViewMode::Lit);
        assert_eq!(SceneViewMode::from_str("gbuffer"),   SceneViewMode::Lit);
    }

    /// 対応表の全エントリが from_str で往復し、チャンネルを取り出せること。
    #[test]
    fn gbuffer_debug_modes_parse_from_table() {
        for (name, ch) in GBUFFER_DEBUG_CHANNEL_TABLE {
            let mode = SceneViewMode::from_str(name);
            assert_eq!(mode, SceneViewMode::GBuffer(*ch), "view_mode 文字列 `{name}` の解釈が不正");
            assert_eq!(mode.gbuffer_debug_channel(), Some(*ch));
        }
    }

    /// G-Buffer デバッグ表示はデファードを維持する（is_lit()==true）こと。
    /// ここが false になると deferred_active が落ち、可視化対象の G-Buffer が
    /// そもそも生成されなくなる（モジュール冒頭コメント参照）。
    #[test]
    fn gbuffer_debug_modes_keep_deferred_active() {
        for (_, ch) in GBUFFER_DEBUG_CHANNEL_TABLE {
            let mode = SceneViewMode::GBuffer(*ch);
            assert!(mode.is_lit(),        "G-Buffer デバッグ表示で is_lit() が false になっている");
            assert!(!mode.is_wireframe(), "G-Buffer デバッグ表示がワイヤーフレーム扱いになっている");
            // シェーダ（LightMeta.view_mode）へは Lit（0）として渡す。
            assert_eq!(mode.to_code(), 0);
        }
    }

    /// 既存 3 モードは G-Buffer デバッグ扱いにならないこと。
    #[test]
    fn legacy_view_modes_are_not_gbuffer_debug() {
        for mode in [SceneViewMode::Lit, SceneViewMode::Unlit, SceneViewMode::Wireframe] {
            assert_eq!(mode.gbuffer_debug_channel(), None);
        }
    }

    /// 対応表のチャンネルコードが重複していないこと（コピペ由来の取り違え検出）。
    #[test]
    fn gbuffer_debug_channel_codes_are_unique() {
        let mut codes: Vec<u32> = GBUFFER_DEBUG_CHANNEL_TABLE.iter().map(|(_, c)| c.to_code()).collect();
        codes.sort_unstable();
        let len = codes.len();
        codes.dedup();
        assert_eq!(codes.len(), len, "GBufferDebugChannel のコードが重複している");
    }
}
