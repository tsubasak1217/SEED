// ============================================================
//  platform/launch_options.rs — OS が起動のときに渡す起動オプション（Android の Intent の extras。段階C-3）
//
//  【流れ】
//    エディタ／SeedAndroid … am start … --es seed.scene '<アセットルートからの相対パス>'
//                            （editor/src/Android/Steps/LaunchStep.cs。キーは AndroidRuntimeContract）
//    MainActivity（Java）   … 「seed.」で始まる文字列の extra を JSON 1 つにまとめ（接頭辞を外した名前がキー）、
//                            onCreate の最初（ネイティブのスレッドが立つ前）に JNI で渡す
//    Android の糊           … runtime/android/native の jni_exports.rs（受け取り）→ launch_options.rs（預かり）→
//                            launch.rs（起動するシーンを決めて LaunchArgs.scene_path へ）
//  ここに置くのは、JSON の書式（キーの名前）・シーンのパスの読み替え・「どのシーンで起動するか」の判断・
//  IPC のポートの読み方（どれも純粋な処理。ホストの cargo test で確かめる）。シーンがあるかの確かめ方
//  （pak・APK・アプリ専用フォルダ）は呼び出し側が関数で渡す。デスクトップは使わない（エディタは --scene= で渡す。main.rs）。
//
//  【JSON】{"scene": "scenes/Main.scene", "ipc_port": "52735"}。値は文字列（Java は文字列の extra だけを渡す）。
//  知らないキーは読み飛ばす（Java は seed.* をすべて渡すので、先の版で足したオプションが古いネイティブへ届いても
//  起動を止めない）。
//
//  【ipc_port（段階D-1）】エディタとの IPC を TCP で待ち受けるポート（127.0.0.1 だけ）。エディタ／SeedAndroid が
//  am start の extra seed.ipc_port で渡し、launch.rs が parse_ipc_port で確かめて LaunchArgs.ipc_port へ入れる
//  （無い・読めなければ待ち受けない。読めない値はシーンの指定を巻き込まず、その項目だけ警告して無視する）。
//  【ipc_token（段階D-1 の追加）】起動ごとの使い捨ての接続トークン（seed.ipc_token）。TCP の接続の最初の行
//  HELLO:<トークン> と照合する（ipc_transport/auth.rs）。無ければ ipc_port があっても待ち受けない。
//  logcat へ起動オプションを出すときは値を伏せる（masked_json_for_log）。
//  【quality / quality_overrides / gpu_timing（段階D-2。計測・検証用）】描画品質のプリセット名（seed.quality）・
//  つまみの上書き（seed.quality_overrides。`キー=値,キー=値`）・パスごとの GPU 時間の計測（seed.gpu_timing=1）。
//  エディタ・SeedAndroid は渡さない（手で am start するときだけ）。project_settings.json の render_quality より優先する
//  （renderer/quality/resolve.rs）。読み方は launch.rs が quality_launch / gpu_timing_enabled で LaunchArgs へ入れる。
// ============================================================

use serde::{Deserialize, Serialize};

use crate::engine::asset_fs::ASSETS_SCHEME;

/// 起動するシーンの JSON のキー（C# の AndroidRuntimeContract.LaunchOptionSceneKey と一致させる。
/// フィールド名 `scene` がそのままキーになる。一致はテストで確かめる）。
pub const SCENE_KEY: &str = "scene";

/// IPC のポートの JSON のキー（C# の AndroidRuntimeContract.LaunchOptionIpcPortKey と一致させる。
/// フィールド名 `ipc_port` がそのままキーになる。一致はテストで確かめる。段階D-1）。
pub const IPC_PORT_KEY: &str = "ipc_port";

/// IPC の接続トークンの JSON のキー（C# の AndroidRuntimeContract.LaunchOptionIpcTokenKey と一致させる。
/// フィールド名 `ipc_token` がそのままキーになる。一致はテストで確かめる。段階D-1）。
pub const IPC_TOKEN_KEY: &str = "ipc_token";

/// 描画品質のプリセット名の JSON のキー（am start の extra seed.quality。段階D-2・計測用）。
pub const QUALITY_KEY: &str = "quality";

/// 描画品質のつまみの上書きの JSON のキー（am start の extra seed.quality_overrides。段階D-2・計測用）。
pub const QUALITY_OVERRIDES_KEY: &str = "quality_overrides";

/// パスごとの GPU 時間の計測の JSON のキー（am start の extra seed.gpu_timing。段階D-2・計測用）。
pub const GPU_TIMING_KEY: &str = "gpu_timing";

/// gpu_timing を有効とみなす値（Java は文字列の extra だけを渡す）。
const GPU_TIMING_ON: &str = "1";

/// 待ち受けに使えるポートの最小値（0 は「OS に選ばせる」なので、エディタが forward できず使えない）。
const MIN_IPC_PORT: u16 = 1;

/// 接続トークンの最短の長さ（C# の AndroidIpcToken.MinLength と一致させる。当て推量で当たらない長さ）。
pub const MIN_IPC_TOKEN_LENGTH: usize = 16;

/// 接続トークンの最長の長さ（C# の AndroidIpcToken.MaxLength と一致させる。HELLO の行の上限に収まる長さ）。
pub const MAX_IPC_TOKEN_LENGTH: usize = 128;

/// ログに出すときに伏せた値の代わりに書く文字列。
pub const MASKED_VALUE: &str = "***";

/// 相対パスの区切り（pak のエントリ・仮想パスと同じ）。
const SEPARATOR: char = '/';

/// Windows の区切り（受け取ったら / にする）。
const BACKSLASH: char = '\\';

/// 親フォルダ（アセットルートの外へ出るので受け付けない）。
const PARENT_SEGMENT: &str = "..";

/// 今のフォルダ（読み飛ばす）。
const CURRENT_SEGMENT: &str = ".";

/// ドライブ名などの区切り（相対パスには現れない。絶対パスの取り違えを弾く）。
const DRIVE_SEPARATOR: char = ':';

/// 起動オプション。
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct LaunchOptions {
    /// 起動するシーン（アセットルートからの相対パス。無ければ project_settings.json の開始シーン）。
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub scene: Option<String>,
    /// エディタとの IPC を TCP で待ち受けるポート（受け取ったままの文字列。確かめ方は `parse_ipc_port`。段階D-1）。
    /// 無ければ待ち受けない。
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub ipc_port: Option<String>,
    /// TCP の IPC の接続トークン（受け取ったままの文字列。確かめ方は `parse_ipc_token`。段階D-1）。
    /// 無ければ ipc_port があっても待ち受けない。
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub ipc_token: Option<String>,
    /// 描画品質のプリセット名（段階D-2・計測用。無ければ project_settings.json の指定かプラットフォームの既定）。
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub quality: Option<String>,
    /// 描画品質のつまみの上書き（`キー=値,キー=値`。段階D-2・計測用）。
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub quality_overrides: Option<String>,
    /// パスごとの GPU 時間を測るか（"1" で有効。段階D-2・計測用）。
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub gpu_timing: Option<String>,
}

impl LaunchOptions {
    /// JSON から読む（空の文字列は「オプションなし」。空白だけのシーン・ポートは指定なしとみなす）。
    ///
    /// # 戻り値
    /// 読めなければ理由（JSON の誤り・オブジェクトでない・型の違い）。
    pub fn from_json(json: &str) -> Result<Self, String> {
        if json.trim().is_empty() {
            return Ok(Self::default());
        }
        // serde は構造体を配列からも読めてしまう（["x"] → 1 つ目のフィールド）ので、オブジェクトだけを受け付ける
        let value: serde_json::Value = serde_json::from_str(json).map_err(|err| err.to_string())?;
        if !value.is_object() {
            return Err("起動オプションは JSON のオブジェクト（{\"scene\": …}）にしてください".to_string());
        }
        let mut options: Self = serde_json::from_value(value).map_err(|err| err.to_string())?;
        if options.scene.as_deref().is_some_and(|scene| scene.trim().is_empty()) {
            options.scene = None;
        }
        if options.ipc_port.as_deref().is_some_and(|port| port.trim().is_empty()) {
            options.ipc_port = None;
        }
        if options.ipc_token.as_deref().is_some_and(|token| token.trim().is_empty()) {
            options.ipc_token = None;
        }
        for text in [&mut options.quality, &mut options.quality_overrides, &mut options.gpu_timing] {
            if text.as_deref().is_some_and(|value| value.trim().is_empty()) {
                *text = None;
            }
        }
        Ok(options)
    }

    /// 描画品質の指定（プリセット名とつまみの上書き）を LaunchArgs の形にする（段階D-2）。
    pub fn quality_launch(&self) -> crate::engine::core::renderer::quality::QualityLaunchOverrides {
        crate::engine::core::renderer::quality::QualityLaunchOverrides {
            preset: self.quality.clone(),
            knobs: self.quality_overrides.clone(),
        }
    }

    /// パスごとの GPU 時間を測るか（"1" のときだけ。段階D-2）。
    pub fn gpu_timing_enabled(&self) -> bool {
        self.gpu_timing.as_deref().is_some_and(|value| value.trim() == GPU_TIMING_ON)
    }

    /// JSON にする（Java 側が作る形と同じ。テストの往復と記録用）。
    pub fn to_json(&self) -> String {
        // 文字列のフィールドだけなので書き出しは失敗しない
        serde_json::to_string(self).unwrap_or_default()
    }

    /// IPC のポート（指定が無ければ None、あれば確かめた結果）。
    ///
    /// # 戻り値
    /// None = 指定なし（待ち受けない）／Some(Ok(ポート))／Some(Err(読めない理由))。
    pub fn ipc_port(&self) -> Option<Result<u16, String>> {
        self.ipc_port.as_deref().map(parse_ipc_port)
    }

    /// IPC の接続トークン（指定が無ければ None、あれば確かめた結果）。
    ///
    /// # 戻り値
    /// None = 指定なし／Some(Ok(トークン))／Some(Err(使えない理由。値そのものは含めない))。
    pub fn ipc_token(&self) -> Option<Result<String, String>> {
        self.ipc_token.as_deref().map(parse_ipc_token)
    }

    /// ログへ出すための JSON（接続トークンの値を伏せる）【純関数】。
    ///
    /// # 引数
    /// * `json` - 受け取った起動オプションの JSON
    ///
    /// # 戻り値
    /// ipc_token の値を `***` にした JSON。オブジェクトとして読めなければ、中身を出さずに大きさだけを書く
    /// （読めない JSON にトークンが入っていても logcat へ出さないため）。
    pub fn masked_json_for_log(json: &str) -> String {
        match serde_json::from_str::<serde_json::Value>(json) {
            Ok(serde_json::Value::Object(mut map)) => {
                if let Some(token) = map.get_mut(IPC_TOKEN_KEY) {
                    *token = serde_json::Value::String(MASKED_VALUE.to_string());
                }
                serde_json::Value::Object(map).to_string()
            }
            _ => format!("<JSON として読めません: {} バイト>", json.len()),
        }
    }
}

/// IPC の接続トークンの文字列を確かめる【純関数】（前後の空白は許す）。
///
/// 16〜128 文字の英数字・`_`・`-` だけ（C# の AndroidIpcToken が作るのは 32 文字の 16 進）。
///
/// # 戻り値
/// トークンか、使えない理由（値そのものは含めない。ログへ出るため）。
pub fn parse_ipc_token(text: &str) -> Result<String, String> {
    let trimmed = text.trim();
    let allowed = trimmed.chars().all(|c| c.is_ascii_alphanumeric() || c == '_' || c == '-');
    if allowed && (MIN_IPC_TOKEN_LENGTH..=MAX_IPC_TOKEN_LENGTH).contains(&trimmed.len()) {
        Ok(trimmed.to_string())
    } else {
        Err(format!(
            "IPC の接続トークンの書式が違います（{MIN_IPC_TOKEN_LENGTH}〜{MAX_IPC_TOKEN_LENGTH} 文字の英数字・_・-。{} 文字でした）",
            trimmed.chars().count()
        ))
    }
}

/// IPC のポートの文字列を確かめる【純関数】（1〜65535 の 10 進整数。前後の空白は許す）。
///
/// # 戻り値
/// ポートか、使えない理由。
pub fn parse_ipc_port(text: &str) -> Result<u16, String> {
    let trimmed = text.trim();
    match trimmed.parse::<u16>() {
        Ok(port) if port >= MIN_IPC_PORT => Ok(port),
        _ => Err(format!(
            "IPC のポート {trimmed} は使えません（{MIN_IPC_PORT}〜{} の整数で指定してください）",
            u16::MAX
        )),
    }
}

/// 起動するシーンの判断。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SceneChoice {
    /// 指定なし: 開始シーン（project_settings.json の start_scene）で起動する。
    StartScene,
    /// 指定のシーンで起動する（エンジンの仮想パス assets://…）。
    Requested {
        /// LaunchArgs.scene_path へ入れる仮想パス。
        virtual_path: String,
    },
    /// 指定のシーンが無い（pak の収録から外れた・名前の誤り）: 警告して開始シーンで起動する。
    Missing {
        /// アセットルートからの相対パス。
        relative: String,
    },
    /// 指定を読めない（アセットルートの外・空）: 警告して開始シーンで起動する。
    Invalid {
        /// 受け取った指定。
        requested: String,
        /// 読めない理由。
        reason: String,
    },
}

impl SceneChoice {
    /// LaunchArgs.scene_path へ入れる値（開始シーンで起動するなら None）。
    pub fn scene_path(&self) -> Option<String> {
        match self {
            Self::Requested { virtual_path } => Some(virtual_path.clone()),
            _ => None,
        }
    }
}

/// シーンの指定をアセットルートからの相対パス（/ 区切り）へ揃える【純関数】。
///
/// `assets://` の仮想パスも受け付ける。`\` は `/` に、空の区切りと `.` は落とす。
/// `..`・ドライブ名（`C:`）・空は受け付けない（アセットルートの外は APK の pak に入らない）。
///
/// # 戻り値
/// 揃えた相対パスか、受け付けない理由。
pub fn scene_relative_path(requested: &str) -> Result<String, String> {
    let trimmed = requested.trim();
    let without_scheme = trimmed.strip_prefix(ASSETS_SCHEME).unwrap_or(trimmed);
    let normalized = without_scheme.replace(BACKSLASH, &SEPARATOR.to_string());
    let segments: Vec<&str> = normalized
        .split(SEPARATOR)
        .map(str::trim)
        .filter(|segment| !segment.is_empty() && *segment != CURRENT_SEGMENT)
        .collect();
    if segments.iter().any(|segment| *segment == PARENT_SEGMENT) {
        return Err(format!("シーンのパスに {PARENT_SEGMENT} は使えません（アセットフォルダの中のパスで指定してください）"));
    }
    if segments.iter().any(|segment| segment.contains(DRIVE_SEPARATOR)) {
        return Err("シーンは絶対パスではなく、アセットフォルダからの相対パスで指定してください".to_string());
    }
    if segments.is_empty() {
        return Err("シーンのパスが空です".to_string());
    }
    Ok(segments.join(&SEPARATOR.to_string()))
}

/// 起動するシーンを決める【純関数】。
///
/// # 引数
/// * `options` - 起動オプション
/// * `exists`  - アセットルートからの相対パスのシーンがあるか（pak・APK・アプリ専用フォルダを見る処理を呼び出し側が渡す。
///               指定があって読めたときだけ 1 回呼ばれる）
pub fn choose_scene(options: &LaunchOptions, exists: impl Fn(&str) -> bool) -> SceneChoice {
    let Some(requested) = options.scene.as_deref() else {
        return SceneChoice::StartScene;
    };
    match scene_relative_path(requested) {
        Ok(relative) if exists(&relative) => SceneChoice::Requested {
            virtual_path: format!("{ASSETS_SCHEME}{relative}"),
        },
        Ok(relative) => SceneChoice::Missing { relative },
        Err(reason) => SceneChoice::Invalid {
            requested: requested.to_string(),
            reason,
        },
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cell::RefCell;

    #[test]
    fn json_round_trip_keeps_scene_and_uses_scene_key() {
        let options = LaunchOptions {
            scene: Some("シーン/森 の 2.scene".to_string()),
            ..Default::default()
        };
        let json = options.to_json();
        assert!(json.contains(&format!("\"{SCENE_KEY}\"")), "キーは SCENE_KEY: {json}");
        assert_eq!(LaunchOptions::from_json(&json).unwrap(), options);
        // Java（org.json）が書く形（/ を \/ にエスケープする）も読める
        let from_java = LaunchOptions::from_json("{\"scene\":\"scenes\\/Second.scene\"}").unwrap();
        assert_eq!(from_java.scene.as_deref(), Some("scenes/Second.scene"));
    }

    /// IPC のポートは IPC_PORT_KEY の文字列で届き（Java は文字列の extra だけを渡す）、往復しても変わらない。
    #[test]
    fn ipc_port_uses_its_key_and_round_trips() {
        let options = LaunchOptions {
            scene: Some("scenes/Main.scene".to_string()),
            ipc_port: Some("52735".to_string()),
            ..Default::default()
        };
        let json = options.to_json();
        assert!(json.contains(&format!("\"{IPC_PORT_KEY}\"")), "キーは IPC_PORT_KEY: {json}");
        assert_eq!(LaunchOptions::from_json(&json).unwrap(), options);
        let from_java = LaunchOptions::from_json("{\"scene\":\"scenes\\/Main.scene\",\"ipc_port\":\"52735\"}").unwrap();
        assert_eq!(from_java.ipc_port(), Some(Ok(52735)));
        assert_eq!(LaunchOptions::default().ipc_port(), None, "指定が無ければ待ち受けない");
        assert_eq!(LaunchOptions::from_json("{\"ipc_port\":\"  \"}").unwrap().ipc_port, None, "空白だけは指定なし");
    }

    /// 接続トークンは IPC_TOKEN_KEY の文字列で届き、16〜128 文字の英数字・_・- だけを受け付ける。
    /// 使えない理由には値そのものを含めない（ログへ出るため）。
    #[test]
    fn ipc_token_uses_its_key_and_is_validated() {
        let token = "0123456789abcdef0123456789abcdef";
        let options = LaunchOptions::from_json(&format!("{{\"ipc_port\":\"52735\",\"ipc_token\":\"{token}\"}}")).unwrap();
        assert_eq!(options.ipc_token(), Some(Ok(token.to_string())));
        assert!(options.to_json().contains(&format!("\"{IPC_TOKEN_KEY}\"")), "キーは IPC_TOKEN_KEY");
        assert_eq!(LaunchOptions::default().ipc_token(), None, "指定が無ければ None");
        assert_eq!(LaunchOptions::from_json("{\"ipc_token\":\" \"}").unwrap().ipc_token, None, "空白だけは指定なし");

        assert_eq!(parse_ipc_token(&format!(" {token} ")), Ok(token.to_string()), "前後の空白は許す");
        assert!(parse_ipc_token(&"a".repeat(MIN_IPC_TOKEN_LENGTH)).is_ok());
        assert!(parse_ipc_token(&"a".repeat(MAX_IPC_TOKEN_LENGTH)).is_ok());
        for bad in ["short", "0123456789abcdef 0123", "0123456789abcdef;rm -rf", "トークントークントークントークン"] {
            let error = parse_ipc_token(bad).unwrap_err();
            assert!(!error.contains(bad), "理由に値を含めない: {error}");
        }
        assert!(parse_ipc_token(&"a".repeat(MAX_IPC_TOKEN_LENGTH + 1)).is_err(), "長すぎる");
    }

    /// ログへ出す JSON はトークンの値だけを伏せる（読めない JSON は中身を出さない）。
    #[test]
    fn masked_json_hides_only_the_token() {
        let masked = LaunchOptions::masked_json_for_log(
            "{\"scene\":\"scenes\\/Main.scene\",\"ipc_port\":\"52735\",\"ipc_token\":\"0123456789abcdef0123456789abcdef\"}",
        );
        assert!(!masked.contains("0123456789abcdef"), "トークンを出さない: {masked}");
        assert!(masked.contains(MASKED_VALUE) && masked.contains("scenes/Main.scene") && masked.contains("52735"), "他の項目は出す: {masked}");
        assert_eq!(LaunchOptions::masked_json_for_log("{\"scene\":\"a.scene\"}"), "{\"scene\":\"a.scene\"}", "トークンが無ければそのまま");
        let broken = LaunchOptions::masked_json_for_log("{\"ipc_token\":\"0123456789abcdef0123456789abcdef\"");
        assert!(!broken.contains("0123456789abcdef"), "読めない JSON は中身を出さない: {broken}");
    }

    /// ポートは 1〜65535 の整数だけ。読めない値はその項目だけの誤りで、シーンの指定は巻き込まない。
    #[test]
    fn ipc_port_is_validated_without_breaking_scene() {
        assert_eq!(parse_ipc_port(" 52735 "), Ok(52735));
        assert_eq!(parse_ipc_port("1"), Ok(1));
        assert_eq!(parse_ipc_port("65535"), Ok(65535));
        for bad in ["0", "65536", "-1", "abc", "52735x", ""] {
            assert!(parse_ipc_port(bad).is_err(), "{bad} は使えない");
        }
        let options = LaunchOptions::from_json("{\"scene\":\"scenes/Main.scene\",\"ipc_port\":\"abc\"}").unwrap();
        assert_eq!(options.scene.as_deref(), Some("scenes/Main.scene"), "シーンはそのまま使える");
        assert!(matches!(options.ipc_port(), Some(Err(_))), "ポートだけが誤り");
    }

    /// 描画品質の指定（段階D-2）: キーの名前・空白だけは指定なし・GPU 計測は "1" だけ有効。
    #[test]
    fn quality_options_use_their_keys() {
        let options = LaunchOptions::from_json(
            "{\"quality\":\"mobile\",\"quality_overrides\":\"render_scale=0.75,deferred=true\",\"gpu_timing\":\"1\"}",
        )
        .unwrap();
        let launch = options.quality_launch();
        assert_eq!(launch.preset.as_deref(), Some("mobile"));
        assert_eq!(launch.knobs.as_deref(), Some("render_scale=0.75,deferred=true"));
        assert!(options.gpu_timing_enabled());
        let json = options.to_json();
        for key in [QUALITY_KEY, QUALITY_OVERRIDES_KEY, GPU_TIMING_KEY] {
            assert!(json.contains(&format!("\"{key}\"")), "キーは {key}: {json}");
        }
        assert_eq!(LaunchOptions::from_json(&json).unwrap(), options);

        let blank = LaunchOptions::from_json("{\"quality\":\" \",\"quality_overrides\":\"\",\"gpu_timing\":\" \"}").unwrap();
        assert_eq!(blank, LaunchOptions::default(), "空白だけは指定なし");
        assert!(!LaunchOptions::default().gpu_timing_enabled());
        assert!(!LaunchOptions::from_json("{\"gpu_timing\":\"0\"}").unwrap().gpu_timing_enabled());
        assert_eq!(LaunchOptions::default().quality_launch(), Default::default(), "指定が無ければ上書きなし");
    }

    #[test]
    fn empty_and_unknown_keys_are_tolerated() {
        assert_eq!(LaunchOptions::from_json("").unwrap(), LaunchOptions::default());
        assert_eq!(LaunchOptions::from_json("  ").unwrap(), LaunchOptions::default());
        assert_eq!(LaunchOptions::from_json("{}").unwrap(), LaunchOptions::default());
        assert_eq!(LaunchOptions::from_json("{\"scene\":\"  \"}").unwrap().scene, None);
        let with_unknown = LaunchOptions::from_json("{\"scene\":\"a.scene\",\"debug\":\"1\"}").unwrap();
        assert_eq!(with_unknown.scene.as_deref(), Some("a.scene"), "知らないキーは読み飛ばす");
        assert_eq!(LaunchOptions::default().to_json(), "{}", "指定が無ければ空のオブジェクト");
    }

    #[test]
    fn broken_json_is_an_error() {
        assert!(LaunchOptions::from_json("{\"scene\":").is_err());
        assert!(LaunchOptions::from_json("{\"scene\":3}").is_err(), "型の違いは誤り");
        assert!(LaunchOptions::from_json("[]").is_err(), "オブジェクトでないものは誤り");
        assert!(LaunchOptions::from_json("[\"scenes/a.scene\"]").is_err(), "配列の位置で読まない");
        assert!(LaunchOptions::from_json("\"scenes/a.scene\"").is_err());
    }

    #[test]
    fn scene_paths_are_normalized_to_asset_relative() {
        assert_eq!(scene_relative_path("scenes/Main.scene").unwrap(), "scenes/Main.scene");
        assert_eq!(scene_relative_path(" assets://scenes/Main.scene ").unwrap(), "scenes/Main.scene");
        assert_eq!(scene_relative_path("scenes\\Sub\\Main.scene").unwrap(), "scenes/Sub/Main.scene");
        assert_eq!(scene_relative_path("./scenes//Main.scene").unwrap(), "scenes/Main.scene");
        assert_eq!(scene_relative_path("/scenes/Main.scene").unwrap(), "scenes/Main.scene");
        assert_eq!(scene_relative_path("シーン/森 の 2.scene").unwrap(), "シーン/森 の 2.scene");
        assert!(scene_relative_path("../x.scene").is_err());
        assert!(scene_relative_path("scenes/../../x.scene").is_err());
        assert!(scene_relative_path("C:/Game/assets/x.scene").is_err());
        assert!(scene_relative_path("assets://").is_err());
        assert!(scene_relative_path("   ").is_err());
    }

    #[test]
    fn choose_scene_uses_requested_existing_scene() {
        let asked = RefCell::new(Vec::new());
        let options = LaunchOptions {
            scene: Some("assets://scenes\\Second.scene".to_string()),
            ..Default::default()
        };
        let choice = choose_scene(&options, |relative| {
            asked.borrow_mut().push(relative.to_string());
            true
        });
        assert_eq!(
            choice,
            SceneChoice::Requested {
                virtual_path: "assets://scenes/Second.scene".to_string()
            }
        );
        assert_eq!(choice.scene_path().as_deref(), Some("assets://scenes/Second.scene"));
        assert_eq!(asked.into_inner(), vec!["scenes/Second.scene".to_string()], "あるかは揃えた相対パスで 1 回だけ聞く");
    }

    #[test]
    fn choose_scene_falls_back_to_start_scene() {
        assert_eq!(choose_scene(&LaunchOptions::default(), |_| panic!("指定なしなら聞かない")), SceneChoice::StartScene);

        let missing = choose_scene(
            &LaunchOptions {
                scene: Some("scenes/NoSuch.scene".to_string()),
                ..Default::default()
            },
            |_| false,
        );
        assert_eq!(
            missing,
            SceneChoice::Missing {
                relative: "scenes/NoSuch.scene".to_string()
            }
        );
        assert_eq!(missing.scene_path(), None, "無ければ開始シーン");

        let invalid = choose_scene(
            &LaunchOptions {
                scene: Some("../outside.scene".to_string()),
                ..Default::default()
            },
            |_| panic!("読めない指定では聞かない"),
        );
        assert!(matches!(invalid, SceneChoice::Invalid { ref requested, .. } if requested == "../outside.scene"));
        assert_eq!(invalid.scene_path(), None);
    }
}
