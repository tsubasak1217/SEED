pub mod model;
pub mod asset_cache;
mod gltf_loader;
mod obj_loader;

pub use model::*;

use std::path::Path;
use std::fmt;
use std::time::Instant;

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

    // ── ① 派生データキャッシュ（ヒットすれば即返す）─────────────────
    // 元ファイルの mtime + サイズ + フォーマットバージョンが一致する
    // キャッシュがあれば、パース・画像デコード・LOD 生成・BC 圧縮を全てスキップする。
    let t_cache = Instant::now();
    if let Some(model) = asset_cache::try_load_model(path) {
        eprintln!(
            "[SEED cache] キャッシュヒット: {} ({:.1} ms)",
            path.display(),
            t_cache.elapsed().as_secs_f64() * 1000.0,
        );
        return Ok(model);
    }

    // ── ② キャッシュミス: 元ファイルからパース ──────────────────────
    let t_parse = Instant::now();
    let mut model = match ext.as_str() {
        "gltf" | "glb" => gltf_loader::load(path)?,
        "obj"           => obj_loader::load(path)?,
        "fbx" => return Err(LoadError::UnsupportedFormat(
            "FBX は未対応です。Blender で glTF 形式にエクスポートしてください。".to_string(),
        )),
        other => return Err(LoadError::UnsupportedFormat(format!(
            "`.{}` は対応していない形式です。gltf / glb / obj を使用してください。",
            other,
        ))),
    };
    let parse_ms = t_parse.elapsed().as_secs_f64() * 1000.0;

    // ── ③ テクスチャをミップ生成 + BC 圧縮して Ready 形式へ変換 ────────
    // （初回のみのコスト。BC7 圧縮は重いため高速プリセットを使用）
    let t_tex = Instant::now();
    let src_bytes = asset_cache::process_model_textures(&mut model);
    let tex_ms = t_tex.elapsed().as_secs_f64() * 1000.0;

    // ── ④ キャッシュ書き出し（ベストエフォート）────────────────────
    // ブロブ分離のため &mut を渡すが、書き出し後に内容は元通り復元される。
    let t_store = Instant::now();
    asset_cache::store_model(path, &mut model);
    let store_ms = t_store.elapsed().as_secs_f64() * 1000.0;

    eprintln!(
        "[SEED cache] 初回ロード: {} | parse {:.1}ms + tex処理 {:.1}ms ({} KiB, bc={}) + 書出 {:.1}ms",
        path.display(), parse_ms, tex_ms, src_bytes / 1024, asset_cache::bc_supported(), store_ms,
    );

    Ok(model)
}
