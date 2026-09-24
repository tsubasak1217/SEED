// ============================================================
//  scripting/unsupported_platform.rs — CLR を持たないプラットフォーム用の ScriptingHost::load
//
//  【対象】Android（段階0）。docs/android.md のロードマップ参照。
//  段階B で ScriptPackager の事前コンパイル DLL と linux-bionic 向け CoreCLR ランタイムを
//  APK に同梱し、既存の hostfxr 経路を `Hostfxr::load_from_path` で使う予定。
//  それまでは「未対応」を明確なエラーとして返し、呼び出し側（App::new）は
//  スクリプト無しで起動を続ける。
// ============================================================

use std::sync::Arc;

use super::{ScriptingHost, ScriptingHostLocation};

/// 未対応の理由として返すメッセージ（ログにそのまま出る）。
const UNSUPPORTED_MESSAGE: &str =
    "このプラットフォームでは C# スクリプトは未対応です（Android は段階B で CoreCLR を同梱して対応予定）";

impl ScriptingHost {
    /// CLR を持たないプラットフォームでは常に「未対応」エラーを返す。
    ///
    /// シグネチャはデスクトップ版の `load` と同一にしてあり、呼び出し側を cfg で分けずに済む。
    /// 通常は `platform::CURRENT.scripting_supported == false` によって呼ばれる前に
    /// 分岐するが、万一呼ばれても panic せずエラーで返す。
    pub fn load(
        _location: &ScriptingHostLocation,
    ) -> Result<Arc<Self>, Box<dyn std::error::Error>> {
        Err(UNSUPPORTED_MESSAGE.into())
    }
}
