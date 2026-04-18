pub mod model;
mod gltf_loader;
mod obj_loader;

pub use model::*;

use std::path::Path;
use std::fmt;

// ============================================================
//  エラー型
// ============================================================

#[derive(Debug)]
pub enum LoadError {
    /// ファイルが見つからない・読めない
    Io(String),
    /// パースに失敗した
    Parse(String),
    /// 対応していない拡張子
    UnsupportedFormat(String),
}

impl fmt::Display for LoadError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            LoadError::Io(s)                => write!(f, "IO error: {}", s),
            LoadError::Parse(s)             => write!(f, "Parse error: {}", s),
            LoadError::UnsupportedFormat(s) => write!(f, "Unsupported format: {}", s),
        }
    }
}

// ============================================================
//  ファサード
// ============================================================

/// パスの拡張子からローダーを選択してモデルを読み込む。
///
/// # 対応形式
/// | 拡張子          | ローダー | 備考                          |
/// |-----------------|----------|-------------------------------|
/// | `.gltf` / `.glb`| gltf     | PBR・アニメ・スキン対応       |
/// | `.obj`          | tobj     | Phong→PBR 近似、アニメ非対応  |
/// | `.fbx`          | -        | 未対応（glTF へ変換を推奨）   |
pub fn load_model(path: &Path) -> Result<Model, LoadError> {
    let ext = path
        .extension()
        .and_then(|e| e.to_str())
        .unwrap_or("")
        .to_lowercase();

    match ext.as_str() {
        "gltf" | "glb" => gltf_loader::load(path),
        "obj"           => obj_loader::load(path),
        "fbx" => Err(LoadError::UnsupportedFormat(
            "FBX は未対応です。Blender で glTF 形式にエクスポートしてください。".to_string(),
        )),
        other => Err(LoadError::UnsupportedFormat(format!(
            "`.{}` は対応していない形式です。gltf / glb / obj を使用してください。",
            other,
        ))),
    }
}
