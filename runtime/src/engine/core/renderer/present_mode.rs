// ============================================================
//  present_mode.rs — 垂直同期（VSync）モードとプレゼントモード選択
//
//  【役割】
//  project_settings.json の `vsync` を解釈し、
//  「スワップチェーンをどの PresentMode で構成するか」を 1 か所で決める。
//
//  【なぜ独立ファイルなのか】
//  プレゼントモードの選択は「エディタ埋め込みかどうか」「利用可能なモードは何か」
//  という 2 つの外部条件に依存する分岐であり、Renderer::new の中に埋め込むと
//  GPU 初期化を伴わずに検証できない（＝単体テストが書けない）。
//  純関数として切り出し、Renderer::new からは呼ぶだけにする。
//
//  【背景: なぜ既定が「埋め込みは Mailbox 優先／単体ウィンドウは Fifo 優先」なのか】
//  エディタ埋め込み時、このランタイムは WPF の子ウィンドウとして DWM に合成される。
//  DWM 自体が OS レベルの VSync を担当するため、ここでさらに Fifo（Vulkan 側の VSync）を
//  重ねると「二重 VSync 待ち」になり、DWM のタイミングとズレるたびに 1 サイクル余計に
//  待ってカクつく。よって埋め込み時は Mailbox / Immediate を優先する（従来動作）。
//
//  一方、単体ウィンドウ（別ウィンドウ Play・パッケージ版）では DWM の合成に頼れる
//  保証がなく、Mailbox / Immediate は「描けるだけ描く」挙動になるため GPU が常時全開に
//  なり、発熱・ファン全開・ノート PC のバッテリー消費を招く。こちらは Fifo（VSync あり）
//  を既定にするのが正しい。
// ============================================================

/// project_settings.json 内のキー名（垂直同期モード）。
const KEY_VSYNC: &str = "vsync";

/// VsyncMode::Auto の文字列表現。
const MODE_STR_AUTO: &str = "auto";
/// VsyncMode::On の文字列表現。
const MODE_STR_ON: &str = "on";
/// VsyncMode::Off の文字列表現。
const MODE_STR_OFF: &str = "off";

/// 垂直同期（VSync）の切り替え設定。
///
/// - `Auto`（既定）: 実行形態で自動的に選ぶ。
///   エディタ埋め込み（親 HWND あり）なら DWM に VSync を任せて `Off` と同じ扱い、
///   単体ウィンドウ（パッケージ版・別ウィンドウ Play）なら `On` と同じ扱い。
/// - `On`  : 常に Fifo（垂直同期あり）。ティアリングが出ず、フレームレートは
///           モニタのリフレッシュレートに張り付く。GPU 負荷・発熱を抑えたいときはこれ。
/// - `Off` : 常に Mailbox → Immediate（垂直同期なし）。入力遅延を最小にしたいとき用。
///           暴走を防ぐ CPU 側のフレーム制限（`target_fps`）は別途効く。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
pub enum VsyncMode {
    /// 実行形態から自動判定する（既定）。
    #[default]
    Auto,
    /// 常に垂直同期あり（Fifo）。
    On,
    /// 常に垂直同期なし（Mailbox / Immediate）。
    Off,
}

impl VsyncMode {
    /// 設定ファイル／ログで使用する文字列表現を返す。
    pub fn as_str(&self) -> &'static str {
        match self {
            Self::Auto => MODE_STR_AUTO,
            Self::On   => MODE_STR_ON,
            Self::Off  => MODE_STR_OFF,
        }
    }

    /// 文字列表現から変換する。
    ///
    /// 未知の値・空文字は既定の `Auto` へフォールバックする
    /// （設定ミスで起動できなくなるより、自動判定へ倒すほうが安全なため）。
    /// 前後の空白は落とし、大文字小文字は区別しない。
    pub fn from_str(s: &str) -> Self {
        match s.trim().to_ascii_lowercase().as_str() {
            MODE_STR_ON  => Self::On,
            MODE_STR_OFF => Self::Off,
            _            => Self::Auto,
        }
    }

    /// `Auto` を実行形態に応じて `On` / `Off` のどちらかへ解決する。
    ///
    /// `embedded` はエディタ埋め込み（起動引数 --parent-hwnd あり）かどうか。
    /// 判定源を引数で受け取ることで、App の状態に触れずに単体テストできる。
    pub fn resolve(self, embedded: bool) -> ResolvedVsync {
        match self {
            Self::On  => ResolvedVsync::On,
            Self::Off => ResolvedVsync::Off,
            // 埋め込みは DWM が VSync を担当するので二重待ちを避ける（従来動作）。
            // 単体ウィンドウは自前で VSync を掛けないと GPU が全力で回り続ける。
            Self::Auto => {
                if embedded { ResolvedVsync::Off } else { ResolvedVsync::On }
            }
        }
    }
}

/// `VsyncMode::Auto` を解決した後の 2 値。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ResolvedVsync {
    /// 垂直同期あり（Fifo 優先）。
    On,
    /// 垂直同期なし（Mailbox → Immediate 優先）。
    Off,
}

// ── 解決後 VSync のグローバル公開 ────────────────────────────
//
// スクリプト（SEED.Application.VsyncEnabled）は「結局 VSync が効いているのか」を
// 知りたい。auto の解決結果は Renderer::new でしか分からないため、決まった時点で
// ここへ写し取る。実行中に変化しない（スワップチェーン再構成を伴うため）。

/// 解決後の VSync が有効か（1 = 有効 / 0 = 無効）。既定は無効側。
static EFFECTIVE_VSYNC_ON: std::sync::atomic::AtomicU32 =
    std::sync::atomic::AtomicU32::new(0);

/// 解決結果を公開する（`Renderer::new` が 1 回だけ呼ぶ）。
pub fn publish_effective_vsync(resolved: ResolvedVsync) {
    let v = if resolved == ResolvedVsync::On { 1 } else { 0 };
    EFFECTIVE_VSYNC_ON.store(v, std::sync::atomic::Ordering::Relaxed);
}

/// 解決後の VSync が有効かを読む（スクリプト公開用）。
pub fn effective_vsync_on() -> bool {
    EFFECTIVE_VSYNC_ON.load(std::sync::atomic::Ordering::Relaxed) != 0
}

/// project_settings.json のテキストから垂直同期モードを読む純関数。
///
/// JSON が壊れている・キーが無い・値が文字列でない・未知の文字列 —— いずれも
/// 既定の `Auto` を返す（parse_render_resolution_mode と同じ方針）。
pub fn parse_vsync_mode(json: &str) -> VsyncMode {
    let Ok(v) = serde_json::from_str::<serde_json::Value>(json) else {
        return VsyncMode::Auto;
    };
    match v[KEY_VSYNC].as_str() {
        Some(s) => VsyncMode::from_str(s),
        None    => VsyncMode::Auto,
    }
}

/// 設定・実行形態・GPU が対応しているモード一覧から、実際に使う
/// `wgpu::PresentMode` を決める純関数。
///
/// `available` はサーフェス（SurfaceCapabilities::present_modes）が報告する対応モード。
/// wgpu の仕様上 Fifo は必ず対応しているが、ここでは仮定せず、どの候補も取れなければ
/// 最後の保険として Fifo を返す（対応外の値を渡して configure で落とさない）。
pub fn select_present_mode(
    vsync:     VsyncMode,
    embedded:  bool,
    available: &[wgpu::PresentMode],
) -> wgpu::PresentMode {
    // 優先順を「解決後の VSync 有無」だけで決める。
    //   On  : Fifo → FifoRelaxed → Mailbox → Immediate
    //         （FifoRelaxed はフレーム落ち時だけティアリングを許す準 VSync。
    //           Fifo が無い環境での次善策として On 側に置く）
    //   Off : Mailbox → Immediate → Fifo（従来の埋め込み時と同じ優先順）
    let preferred: &[wgpu::PresentMode] = match vsync.resolve(embedded) {
        ResolvedVsync::On => &[
            wgpu::PresentMode::Fifo,
            wgpu::PresentMode::FifoRelaxed,
            wgpu::PresentMode::Mailbox,
            wgpu::PresentMode::Immediate,
        ],
        ResolvedVsync::Off => &[
            wgpu::PresentMode::Mailbox,
            wgpu::PresentMode::Immediate,
            wgpu::PresentMode::Fifo,
        ],
    };

    preferred
        .iter()
        .copied()
        .find(|m| available.contains(m))
        // 対応モードが 1 つも報告されない異常時の保険。
        // Fifo は wgpu が常時保証する唯一のモード。
        .unwrap_or(wgpu::PresentMode::Fifo)
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 全モードが使える一般的な環境を模した対応モード一覧。
    const ALL: &[wgpu::PresentMode] = &[
        wgpu::PresentMode::Fifo,
        wgpu::PresentMode::FifoRelaxed,
        wgpu::PresentMode::Mailbox,
        wgpu::PresentMode::Immediate,
    ];

    /// キーが無い・壊れた JSON は既定（Auto）。
    #[test]
    fn missing_or_broken_json_is_auto() {
        assert_eq!(parse_vsync_mode("{}"), VsyncMode::Auto);
        assert_eq!(parse_vsync_mode(""), VsyncMode::Auto);
        assert_eq!(parse_vsync_mode("not json"), VsyncMode::Auto);
        assert_eq!(parse_vsync_mode(r#"{"window_width": 1280}"#), VsyncMode::Auto);
    }

    /// 明示値のパース（大文字小文字・前後空白を無視）。
    #[test]
    fn explicit_values_are_parsed() {
        assert_eq!(parse_vsync_mode(r#"{"vsync": "on"}"#),   VsyncMode::On);
        assert_eq!(parse_vsync_mode(r#"{"vsync": "off"}"#),  VsyncMode::Off);
        assert_eq!(parse_vsync_mode(r#"{"vsync": "auto"}"#), VsyncMode::Auto);
        assert_eq!(parse_vsync_mode(r#"{"vsync": " ON "}"#), VsyncMode::On);
    }

    /// 未知文字列・型違いは既定（Auto）へフォールバック。
    #[test]
    fn unknown_or_wrong_type_is_auto() {
        assert_eq!(parse_vsync_mode(r#"{"vsync": "yes"}"#), VsyncMode::Auto);
        assert_eq!(parse_vsync_mode(r#"{"vsync": 1}"#),     VsyncMode::Auto);
        assert_eq!(parse_vsync_mode(r#"{"vsync": null}"#),  VsyncMode::Auto);
    }

    /// Auto は埋め込みかどうかで解決先が変わる（この分岐が本機能の核心）。
    #[test]
    fn auto_resolves_by_embedding() {
        assert_eq!(VsyncMode::Auto.resolve(true),  ResolvedVsync::Off);
        assert_eq!(VsyncMode::Auto.resolve(false), ResolvedVsync::On);
        // 明示指定は埋め込みの有無に影響されない。
        assert_eq!(VsyncMode::On.resolve(true),   ResolvedVsync::On);
        assert_eq!(VsyncMode::Off.resolve(false), ResolvedVsync::Off);
    }

    /// 全モードが使える環境での選択結果。
    #[test]
    fn selects_expected_mode_when_all_available() {
        // auto + 埋め込み → 従来どおり Mailbox
        assert_eq!(select_present_mode(VsyncMode::Auto, true, ALL),  wgpu::PresentMode::Mailbox);
        // auto + 単体ウィンドウ → Fifo（VSync あり）
        assert_eq!(select_present_mode(VsyncMode::Auto, false, ALL), wgpu::PresentMode::Fifo);
        // 明示 on / off は形態によらず固定
        assert_eq!(select_present_mode(VsyncMode::On,  true,  ALL), wgpu::PresentMode::Fifo);
        assert_eq!(select_present_mode(VsyncMode::Off, false, ALL), wgpu::PresentMode::Mailbox);
    }

    /// Mailbox 非対応の環境では Immediate へ落ちる（Off 側の優先順）。
    #[test]
    fn falls_back_to_immediate_without_mailbox() {
        let caps = &[wgpu::PresentMode::Fifo, wgpu::PresentMode::Immediate];
        assert_eq!(select_present_mode(VsyncMode::Off, true, caps), wgpu::PresentMode::Immediate);
    }

    /// Fifo しか無い環境では Off 指定でも Fifo になる（対応外を返さない）。
    #[test]
    fn falls_back_to_fifo_when_only_fifo() {
        let caps = &[wgpu::PresentMode::Fifo];
        assert_eq!(select_present_mode(VsyncMode::Off, true, caps), wgpu::PresentMode::Fifo);
    }

    /// 対応モードが 1 つも報告されない異常時も Fifo を返してパニックさせない。
    #[test]
    fn empty_capabilities_fall_back_to_fifo() {
        assert_eq!(select_present_mode(VsyncMode::Off, true,  &[]), wgpu::PresentMode::Fifo);
        assert_eq!(select_present_mode(VsyncMode::On,  false, &[]), wgpu::PresentMode::Fifo);
    }

    /// 文字列表現の往復（エディタ側が書く JSON 値と一致していること）。
    #[test]
    fn string_round_trip() {
        for m in [VsyncMode::Auto, VsyncMode::On, VsyncMode::Off] {
            assert_eq!(VsyncMode::from_str(m.as_str()), m);
        }
    }
}
