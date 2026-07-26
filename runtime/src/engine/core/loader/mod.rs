pub mod model;
pub mod asset_cache;
mod gltf_loader;
mod obj_loader;

pub use model::*;

use std::path::Path;
use std::fmt;
use std::time::Instant;

// ============================================================
//  生成時間の内訳計測（LOD 簡略化 / メッシュレット分割）
//
//  初回ロード（キャッシュミス時）のホットスポットを特定できるよう、
//  各ローダーが LOD 生成・メッシュレット分割の所要時間を累積し、
//  `load_model` が [SEED cache] 初回ロード行に内訳として出力する。
// ============================================================
pub(crate) mod gen_timing {
    use std::sync::atomic::{AtomicU64, Ordering};

    /// LOD インデックス生成（meshopt simplify）の累積ナノ秒。
    static LOD_NANOS:     AtomicU64 = AtomicU64::new(0);
    /// メッシュレット分割＋境界計算（meshopt build/bounds）の累積ナノ秒。
    static MESHLET_NANOS: AtomicU64 = AtomicU64::new(0);

    /// LOD 生成時間を累積する（各プリミティブのロードから呼ぶ）。
    pub fn add_lod(d: std::time::Duration) {
        LOD_NANOS.fetch_add(d.as_nanos() as u64, Ordering::Relaxed);
    }
    /// メッシュレット分割時間を累積する（各プリミティブのロードから呼ぶ）。
    pub fn add_meshlet(d: std::time::Duration) {
        MESHLET_NANOS.fetch_add(d.as_nanos() as u64, Ordering::Relaxed);
    }
    /// 累積値を (lod_ms, meshlet_ms) で取り出してリセットする（モデル 1 体分の内訳）。
    pub fn take_ms() -> (f64, f64) {
        let lod = LOD_NANOS.swap(0, Ordering::Relaxed) as f64 / 1.0e6;
        let ml  = MESHLET_NANOS.swap(0, Ordering::Relaxed) as f64 / 1.0e6;
        (lod, ml)
    }
}

// ============================================================
//  エラー型
// ============================================================

/// `load_model` が返すエラー種別。
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
    // parse 内で累積された LOD 生成・メッシュレット分割の内訳（このモデル 1 体分）。
    let (lod_ms, meshlet_ms) = gen_timing::take_ms();

    // ── ③ テクスチャをミップ生成 + BC 圧縮して Ready 形式へ変換 ────────
    // （初回のみのコスト。BC7 圧縮は重いため高速プリセットを使用）
    let t_tex = Instant::now();
    // プリミティブ平均アルベド（Phase RT-GI）を、テクスチャを Ready へ変換する前に算出して焼く。
    // （process_model_textures がデコード元を破棄するため、その前に一度だけデコードして平均を取る）
    asset_cache::compute_material_avg_albedo(&mut model);
    let src_bytes = asset_cache::process_model_textures(&mut model);
    let tex_ms = t_tex.elapsed().as_secs_f64() * 1000.0;

    // ── ④ キャッシュ書き出し（ベストエフォート）────────────────────
    // ブロブ分離のため &mut を渡すが、書き出し後に内容は元通り復元される。
    let t_store = Instant::now();
    asset_cache::store_model(path, &mut model);
    let store_ms = t_store.elapsed().as_secs_f64() * 1000.0;

    eprintln!(
        "[SEED cache] 初回ロード: {} | parse {:.1}ms (内 lod={:.1}ms meshlet={:.1}ms) + tex処理 {:.1}ms ({} KiB, bc={}) + 書出 {:.1}ms",
        path.display(), parse_ms, lod_ms, meshlet_ms, tex_ms, src_bytes / 1024,
        asset_cache::bc_supported(), store_ms,
    );

    Ok(model)
}
