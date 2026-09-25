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
//  ここに置くのは、JSON の書式（キーの名前）・シーンのパスの読み替え・「どのシーンで起動するか」の判断
//  （どれも純粋な処理。ホストの cargo test で確かめる）。シーンがあるかの確かめ方（pak・APK・アプリ専用フォルダ）は
//  呼び出し側が関数で渡す。デスクトップは使わない（エディタは --scene= で渡す。main.rs）。
//
//  【JSON】{"scene": "scenes/Main.scene"}。知らないキーは読み飛ばす（Java は seed.* をすべて渡すので、先の版で
//  足したオプションが古いネイティブへ届いても起動を止めない）。
// ============================================================

use serde::{Deserialize, Serialize};

use crate::engine::asset_fs::ASSETS_SCHEME;

/// 起動するシーンの JSON のキー（C# の AndroidRuntimeContract.LaunchOptionSceneKey と一致させる。
/// フィールド名 `scene` がそのままキーになる。一致はテストで確かめる）。
pub const SCENE_KEY: &str = "scene";

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
}

impl LaunchOptions {
    /// JSON から読む（空の文字列は「オプションなし」。空白だけのシーンは指定なしとみなす）。
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
        Ok(options)
    }

    /// JSON にする（Java 側が作る形と同じ。テストの往復と記録用）。
    pub fn to_json(&self) -> String {
        // 文字列のフィールドだけなので書き出しは失敗しない
        serde_json::to_string(self).unwrap_or_default()
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
        };
        let json = options.to_json();
        assert!(json.contains(&format!("\"{SCENE_KEY}\"")), "キーは SCENE_KEY: {json}");
        assert_eq!(LaunchOptions::from_json(&json).unwrap(), options);
        // Java（org.json）が書く形（/ を \/ にエスケープする）も読める
        let from_java = LaunchOptions::from_json("{\"scene\":\"scenes\\/Second.scene\"}").unwrap();
        assert_eq!(from_java.scene.as_deref(), Some("scenes/Second.scene"));
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
            },
            |_| panic!("読めない指定では聞かない"),
        );
        assert!(matches!(invalid, SceneChoice::Invalid { ref requested, .. } if requested == "../outside.scene"));
        assert_eq!(invalid.scene_path(), None);
    }
}
