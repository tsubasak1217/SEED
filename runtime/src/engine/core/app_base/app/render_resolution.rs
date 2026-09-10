// ============================================================
//  render_resolution.rs — ゲーム画面の描画解像度モード
//
//  【役割】
//  project_settings.json の `render_resolution_mode` を解釈し、
//  「このフレームをどの解像度で描くか」を 1 か所で決める。
//
//  【なぜ独立ファイルなのか】
//  描画解像度は、描画（RT 確保・ビューポート）だけでなく
//  入力（カーソル座標）・UI（キャンバス基準サイズ）・スクリプト公開値
//  （Camera.WorldToScreen の基準）にも一斉に効く横断的な設定である。
//  判定式が散らばると「クリック位置だけズレる」「UI だけ伸びる」といった
//  部分崩れを生むため、モードの定義・パース・有効判定をこのファイルへ集約する。
// ============================================================

use super::{App, RuntimeMode};

/// project_settings.json 内のキー名（描画解像度モード）。
const KEY_RENDER_RESOLUTION_MODE: &str = "render_resolution_mode";

/// `RenderResolutionMode::Window` の文字列表現。
const MODE_STR_WINDOW: &str = "window";
/// `RenderResolutionMode::Fixed` の文字列表現。
const MODE_STR_FIXED: &str = "fixed";

/// ゲーム画面の描画解像度の決め方。
///
/// - `Window`（既定・従来動作）:
///   ウィンドウ（または埋め込み先の親クライアント領域）の実サイズでそのまま描く。
///   ウィンドウを広げれば見える範囲・UI の実ピクセル寸法も変わる。
///
/// - `Fixed`（内部解像度固定）:
///   project_settings.json の `window_width` × `window_height` を **内部解像度** とし、
///   3D も UI もすべてその解像度で描いてから、最終段でウィンドウへアスペクト維持で
///   拡大縮小する（余りは黒帯）。ウィンドウサイズを変えても見た目が変わらない。
///
/// 【`CameraComponent` の `ScalingMode` との関係】
/// `ScalingMode`（VertMinus / LetterBox / PillarBox …）は「**描画ターゲットの**アスペクトに
/// 対してカメラをどう収めるか」を決める設定であり、`Fixed` ではその基準が
/// ウィンドウのアスペクトではなく **内部解像度のアスペクト** になる。
/// したがって内部解像度とカメラの `target_width` / `target_height` を一致させれば、
/// `ScalingMode` 側の帯は一切出ず（描画ターゲットと target が同アスペクトのため）、
/// ウィンドウ側のレターボックス帯だけが出る、という関係になる。
/// 逆に両者をわざと食い違わせると帯が二重に出るので、通常は一致させること。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
pub enum RenderResolutionMode {
    /// ウィンドウ実サイズで描く（既定・従来動作）。
    #[default]
    Window,
    /// 固定の内部解像度で描き、最終段でウィンドウへレターボックス表示する。
    Fixed,
}

impl RenderResolutionMode {
    /// 設定ファイル／IPC で使用する文字列表現を返す。
    pub fn as_str(&self) -> &'static str {
        match self {
            Self::Window => MODE_STR_WINDOW,
            Self::Fixed => MODE_STR_FIXED,
        }
    }

    /// 文字列表現から変換する。
    ///
    /// 未知の値・空文字は既定の `Window` へフォールバックする
    /// （設定ミスで起動できなくなるより、従来動作へ倒すほうが安全なため）。
    /// 前後の空白は落とし、大文字小文字は区別しない。
    pub fn from_str(s: &str) -> Self {
        match s.trim().to_ascii_lowercase().as_str() {
            MODE_STR_FIXED => Self::Fixed,
            _ => Self::Window,
        }
    }
}

/// project_settings.json のテキストから描画解像度モードを読む純関数。
///
/// JSON が壊れている・キーが無い・値が文字列でない・未知の文字列 —— いずれも
/// 既定の `Window`（従来動作）を返す。ファイル I/O を含まないので単体テストできる
/// （`parse_window_size` / `parse_game_name` と同じ方針。本ファイル末尾の tests）。
pub fn parse_render_resolution_mode(json: &str) -> RenderResolutionMode {
    let Ok(v) = serde_json::from_str::<serde_json::Value>(json) else {
        return RenderResolutionMode::Window;
    };
    match v[KEY_RENDER_RESOLUTION_MODE].as_str() {
        Some(s) => RenderResolutionMode::from_str(s),
        None => RenderResolutionMode::Window,
    }
}

impl App {
    /// fixed（内部解像度固定）が実際に効く条件と、そのときの内部解像度を返す。
    ///
    /// `None` を返すフレームは完全に従来動作（描画解像度＝ウィンドウ実サイズ）。
    ///
    /// 【有効条件】
    /// 1. 設定が `Fixed` であること。
    /// 2. `RuntimeMode::Play` であること（Edit のシーンビューはウィンドウ自由リサイズが前提）。
    /// 3. 埋め込み（親 HWND あり）でないこと。
    ///    エディタの Play ボタンは `self.mode` を `Play` へ倒すが、その実体は WPF の
    ///    子ウィンドウであり、サイズは WPF が支配する。さらにエディタは
    ///    カーソル座標を**自分のビューポート座標系のまま注入**してくる（注入座標は
    ///    変換してはならない規約）ため、ここで内部解像度へ倒すと座標系が二重になる。
    ///    よって埋め込み時は常に従来動作にする。
    ///
    /// 内部解像度は `project_resolution`（project_settings.json の
    /// `window_width` × `window_height`。`handle_resumed` で 1 回だけ読む）。
    pub(super) fn fixed_render_resolution(&self) -> Option<(u32, u32)> {
        if self.render_resolution_mode != RenderResolutionMode::Fixed {
            return None;
        }
        if self.mode != RuntimeMode::Play || self.is_embedded() {
            return None;
        }
        Some(self.project_resolution)
    }

    /// 入力（ウィンドウ座標）→ 描画解像度座標 の写像を `Input` へ設定し直す。
    ///
    /// ウィンドウ実サイズが変わるたびに呼ぶ必要がある（初期化直後と `on_resize`）。
    /// 写像そのものは `Input` が唯一の所有者で、App 側には持たない
    /// （二重に持つと「片方だけ更新し忘れてクリック位置がズレる」不具合を招くため）。
    pub(super) fn sync_input_view_map(&mut self) {
        use crate::engine::core::input::ViewMap;
        // ウィンドウ座標の基準は、CursorMoved が返す座標系＝自ウィンドウのクライアント領域。
        // fixed は非埋め込み Play 限定なので、親クライアント領域を考慮する必要はない。
        let map = match (self.fixed_render_resolution(), self.window.as_ref()) {
            (Some(internal), Some(win)) => {
                let s = win.inner_size();
                Some(ViewMap { window: (s.width, s.height), internal })
            }
            // window モード（既定）は等倍。None を渡すと Input 側は変換自体を行わない。
            _ => None,
        };
        self.input.set_view_map(map);
    }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// キーが無いときは既定（Window）。
    #[test]
    fn missing_key_is_window() {
        assert_eq!(parse_render_resolution_mode("{}"), RenderResolutionMode::Window);
        assert_eq!(
            parse_render_resolution_mode(r#"{"window_width": 1280}"#),
            RenderResolutionMode::Window
        );
    }

    /// 明示的な "window" / "fixed" がそれぞれ解釈されること。
    #[test]
    fn explicit_values_are_parsed() {
        assert_eq!(
            parse_render_resolution_mode(r#"{"render_resolution_mode": "window"}"#),
            RenderResolutionMode::Window
        );
        assert_eq!(
            parse_render_resolution_mode(r#"{"render_resolution_mode": "fixed"}"#),
            RenderResolutionMode::Fixed
        );
    }

    /// 未知の文字列・値の型違いは既定（Window）へフォールバックすること。
    #[test]
    fn unknown_value_falls_back_to_window() {
        assert_eq!(
            parse_render_resolution_mode(r#"{"render_resolution_mode": "stretch"}"#),
            RenderResolutionMode::Window
        );
        assert_eq!(
            parse_render_resolution_mode(r#"{"render_resolution_mode": ""}"#),
            RenderResolutionMode::Window
        );
        // 数値・真偽値・null（as_str が None）も既定へ。
        assert_eq!(
            parse_render_resolution_mode(r#"{"render_resolution_mode": 1}"#),
            RenderResolutionMode::Window
        );
        assert_eq!(
            parse_render_resolution_mode(r#"{"render_resolution_mode": null}"#),
            RenderResolutionMode::Window
        );
    }

    /// JSON 自体が壊れている（空文字列含む）ときは既定（Window）。
    /// `read_project_settings_json` は読み込み失敗時に空文字列を渡すため、その経路の下支え。
    #[test]
    fn broken_json_is_window() {
        assert_eq!(parse_render_resolution_mode(""), RenderResolutionMode::Window);
        assert_eq!(parse_render_resolution_mode("not json"), RenderResolutionMode::Window);
        assert_eq!(
            parse_render_resolution_mode(r#"{"render_resolution_mode": "fixed""#),
            RenderResolutionMode::Window
        );
    }

    /// 大文字小文字・前後空白を吸収すること（手書き設定への耐性）。
    #[test]
    fn case_and_whitespace_are_normalized() {
        assert_eq!(
            parse_render_resolution_mode(r#"{"render_resolution_mode": " Fixed "}"#),
            RenderResolutionMode::Fixed
        );
        assert_eq!(
            parse_render_resolution_mode(r#"{"render_resolution_mode": "FIXED"}"#),
            RenderResolutionMode::Fixed
        );
        assert_eq!(
            parse_render_resolution_mode(r#"{"render_resolution_mode": " WINDOW "}"#),
            RenderResolutionMode::Window
        );
    }

    /// `as_str` と `from_str` が往復すること（設定の保存・読み込みで壊れない）。
    #[test]
    fn as_str_and_from_str_round_trip() {
        for m in [RenderResolutionMode::Window, RenderResolutionMode::Fixed] {
            assert_eq!(RenderResolutionMode::from_str(m.as_str()), m);
        }
    }
}
