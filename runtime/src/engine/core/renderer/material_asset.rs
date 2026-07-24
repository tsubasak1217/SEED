// ============================================================
//  material_asset.rs — .mat マテリアルアセット（Phase R7）
//
//  【役割】
//  .mat ファイル（JSON）のデータ型定義・ロード・パスキャッシュを提供する。
//  glTF に埋め込まれたマテリアルとは独立した「差し替え用マテリアル定義」であり、
//  ModelComponent の MaterialOverride（MatAsset 種別）から参照される。
//
//  【設計方針】
//  - 全フィールド `#[serde(default)]` を付け、欠落キーがあってもロードが失敗しないようにする
//    （エディタが未対応フィールドを持つ古い .mat を開いても壊れない）。
//  - パスをキーにしたグローバルキャッシュを持ち、同一 .mat の再ロードコストを避ける。
//    エディタからの明示リロード要求（IPC）に応じて `reload` / `clear_cache` で無効化する。
// ============================================================

use crate::engine::core::loader::model::{AlphaMode, CullFace};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::sync::{Arc, Mutex, OnceLock};

// ─── デフォルト値関数（マジックナンバー禁止のため名前付きで定義） ─────────────

/// base_color の既定値（不透明・白）
fn def_white() -> [f32; 4] {
    [1.0, 1.0, 1.0, 1.0]
}
/// metallic / roughness の既定値（glTF PBR の慣例に合わせフルスケール）
fn def_one() -> f32 {
    1.0
}
/// transmission（透過率）の既定値（0.0＝透過なし＝従来動作）。
fn def_zero() -> f32 {
    0.0
}
/// alpha_mode の既定値
fn def_opaque() -> String {
    "opaque".to_string()
}
/// alpha_cutoff（Mask モード時の閾値）の既定値
fn def_cutoff() -> f32 {
    0.5
}
/// cull_face の既定値（背面カリング）。`CullFace::default()` と一致させること。
fn def_cull_face() -> String {
    CullFace::default().as_str().to_ascii_lowercase()
}

// ============================================================
//  MaterialAsset — .mat ファイルの JSON スキーマ
// ============================================================

/// `.mat` ファイル（JSON）に対応するマテリアル定義。
///
/// glTF の `Material`（core::loader::model::Material）と似た構造だが、
/// - テクスチャはインデックスではなく「パス」で参照する（アセット単体で完結させるため）
/// - `alpha_mode` は文字列表現（"opaque"|"mask"|"blend"）で保持する（JSON 上の可読性のため）
#[derive(Clone, Serialize, Deserialize)]
pub struct MaterialAsset {
    #[serde(default)]
    pub name: String,
    #[serde(default = "def_white")]
    pub base_color: [f32; 4],
    #[serde(default = "def_one")]
    pub metallic: f32,
    #[serde(default = "def_one")]
    pub roughness: f32,
    #[serde(default)]
    pub emissive: [f32; 3],
    /// "opaque" | "mask" | "blend"（大文字小文字は問わない。不明値は opaque 扱い）
    #[serde(default = "def_opaque")]
    pub alpha_mode: String,
    /// alpha_mode == "mask" のときのカットオフ閾値
    #[serde(default = "def_cutoff")]
    pub alpha_cutoff: f32,
    /// 屈折率（IOR, Phase RT-Translucency）。既定 1.0（屈折なし）。ガラス≈1.5、水≈1.33。
    /// RT-Translucency 有効・alpha_mode=="blend" のときスクリーンスペース屈折に使う。
    #[serde(default = "def_one")]
    pub ior: f32,
    /// 透過率（transmission, 0..1。ガラス表現）。既定 0.0（透過なし＝従来動作）。
    /// アルファと分離した「向こうがどれだけ透けるか」。RT-Translucency の合成でフレネル配分に使う。
    #[serde(default = "def_zero")]
    pub transmission: f32,
    /// 拡散透過（diffuse_transmission, 0..1。葉・布・紙の逆光透け）。既定 0.0（拡散透過なし＝従来動作）。
    /// ガラスの鏡面透過（上の transmission）とは別物で、屈折を伴わない内部散乱の逆光透け。
    /// `#[serde(default = "def_zero")]` により、本キーを持たない既存 .mat は従来どおり不透過扱いになる。
    #[serde(default = "def_zero")]
    pub diffuse_transmission: f32,
    /// MR テクスチャ無視トグル（既定 false）。true で metallic/roughness テクスチャの乗算を
    /// スキップし、metallic/roughness factor をそのまま最終値にする（glTF 標準の乗算は既定で維持）。
    /// `#[serde(default)]` により、本キーを持たない既存 .mat は従来どおり乗算する。
    #[serde(default)]
    pub mr_tex_ignore: bool,
    /// カリング面 "back" | "front" | "none"（大文字小文字は問わない。不明値は back 扱い）。
    /// `#[serde(default)]` により、本キーを持たない既存 .mat は従来どおり背面カリングになる。
    #[serde(default = "def_cull_face")]
    pub cull_face: String,
    #[serde(default)]
    pub textures: MatTextures,
}

/// `.mat` が参照するテクスチャパス一式。空文字列 = 「指定なし」。
#[derive(Clone, Default, Serialize, Deserialize)]
pub struct MatTextures {
    #[serde(default)]
    pub albedo: String,
    #[serde(default)]
    pub normal: String,
    #[serde(default)]
    pub metallic_roughness: String,
    #[serde(default)]
    pub occlusion: String,
    #[serde(default)]
    pub emissive: String,
}

/// 新規 .mat 作成時のデフォルト JSON 文字列を返す。
///
/// ProjectPanel（C# 側）が「新規マテリアル」作成時にこの内容を書き出す想定だが、
/// Rust 側の他処理（テスト等）からも参照できるよう公開しておく。
pub fn default_mat_json() -> String {
    let asset = MaterialAsset {
        name: "NewMaterial".to_string(),
        base_color: def_white(),
        metallic: def_one(),
        roughness: def_one(),
        emissive: [0.0; 3],
        alpha_mode: def_opaque(),
        alpha_cutoff: def_cutoff(),
        ior: def_one(),
        transmission: def_zero(),
        diffuse_transmission: def_zero(),
        mr_tex_ignore: false,
        cull_face: def_cull_face(),
        textures: MatTextures::default(),
    };
    // pretty JSON（人手編集・diff レビューのしやすさを優先）
    serde_json::to_string_pretty(&asset).unwrap_or_default()
}

/// `.mat` の alpha_mode 文字列を `AlphaMode` へ変換する。
///
/// 大文字小文字を無視し、"mask"→Mask, "blend"→Blend、それ以外（"opaque" 含む不明値）は
/// Opaque にフォールバックする（安全側デフォルト）。
pub fn parse_alpha_mode(s: &str) -> AlphaMode {
    match s.to_ascii_lowercase().as_str() {
        "mask" => AlphaMode::Mask,
        "blend" => AlphaMode::Blend,
        _ => AlphaMode::Opaque,
    }
}

/// カリング面の文字列表現（.mat / IPC 共通）を `CullFace` へ変換する。
///
/// 大文字小文字を無視し、"front"→Front, "none"→None、それ以外（"back" 含む不明値）は
/// Back にフォールバックする（安全側デフォルト＝従来の背面カリング）。
pub fn parse_cull_face(s: &str) -> CullFace {
    match s.to_ascii_lowercase().as_str() {
        "front" => CullFace::Front,
        "none" => CullFace::None,
        _ => CullFace::Back,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// cull_face 文字列 → CullFace の変換（大文字小文字非依存・不明値は Back）。
    #[test]
    fn parse_cull_face_maps_all_values() {
        assert_eq!(parse_cull_face("back"), CullFace::Back);
        assert_eq!(parse_cull_face("Front"), CullFace::Front);
        assert_eq!(parse_cull_face("NONE"), CullFace::None);
        assert_eq!(parse_cull_face("bogus"), CullFace::Back);
        assert_eq!(parse_cull_face(""), CullFace::Back);
    }

    /// cull_face キーを持たない既存 .mat が壊れず、既定（back）で読めること。
    #[test]
    fn legacy_mat_without_cull_face_defaults_to_back() {
        let json = r#"{"name":"Old","base_color":[1,1,1,1],"metallic":1,"roughness":1}"#;
        let asset: MaterialAsset = serde_json::from_str(json).expect("旧 .mat のパースに失敗");
        assert_eq!(parse_cull_face(&asset.cull_face), CullFace::Back);
    }

    /// 新規 .mat の既定 JSON に cull_face が含まれ、back として解釈されること。
    #[test]
    fn default_mat_json_contains_cull_face() {
        let asset: MaterialAsset =
            serde_json::from_str(&default_mat_json()).expect("既定 .mat のパースに失敗");
        assert_eq!(parse_cull_face(&asset.cull_face), CullFace::Back);
    }

    /// mr_tex_ignore キーを持たない既存 .mat が壊れず、既定（false＝乗算）で読めること（serde 互換）。
    #[test]
    fn legacy_mat_without_mr_tex_ignore_defaults_to_false() {
        let json = r#"{"name":"Old","base_color":[1,1,1,1],"metallic":1,"roughness":1}"#;
        let asset: MaterialAsset = serde_json::from_str(json).expect("旧 .mat のパースに失敗");
        assert!(!asset.mr_tex_ignore, "キー欠落時は false（従来の乗算）");
    }

    /// mr_tex_ignore=true を明示した .mat が正しく読めること。
    #[test]
    fn mat_with_mr_tex_ignore_true_parses() {
        let json = r#"{"name":"NoMR","base_color":[1,1,1,1],"metallic":1,"roughness":1,"mr_tex_ignore":true}"#;
        let asset: MaterialAsset =
            serde_json::from_str(json).expect("mr_tex_ignore 付き .mat のパースに失敗");
        assert!(asset.mr_tex_ignore, "true を指定したら true");
    }

    /// 新規 .mat の既定 JSON に mr_tex_ignore が含まれ、false として読めること。
    #[test]
    fn default_mat_json_contains_mr_tex_ignore_false() {
        let asset: MaterialAsset =
            serde_json::from_str(&default_mat_json()).expect("既定 .mat のパースに失敗");
        assert!(!asset.mr_tex_ignore, "既定 .mat は false");
    }
}

// ============================================================
//  パスキャッシュ
// ============================================================

/// パス（正規化なし・呼び出し元が渡した文字列そのまま）→ ロード済み `MaterialAsset` のキャッシュ。
/// スレッド安全にするため `Mutex` で包む（レンダースレッド・IPC スレッド両方から触られ得る）。
static CACHE: OnceLock<Mutex<HashMap<String, Arc<MaterialAsset>>>> = OnceLock::new();

fn cache() -> &'static Mutex<HashMap<String, Arc<MaterialAsset>>> {
    CACHE.get_or_init(|| Mutex::new(HashMap::new()))
}

/// `.mat` ファイルをロードする（キャッシュ優先）。
///
/// - キャッシュにあればそれを返す。
/// - 無ければ `asset_fs` 経由で読み込み（仮想パス assets:// / PAK 対応）、
///   JSON パースに成功したらキャッシュへ登録して返す。
/// - 読み込み・パース失敗時は `None`（呼び出し元はオーバーライド適用をスキップする）。
pub fn load(path: &str) -> Option<Arc<MaterialAsset>> {
    if let Some(cached) = cache().lock().ok().and_then(|c| c.get(path).cloned()) {
        return Some(cached);
    }

    // asset_fs::read_string は仮想パス(assets://)・PAK・絶対パスのいずれにも対応する。
    let text = match crate::engine::asset_fs::read_string(path) {
        Ok(t) => t,
        Err(e) => {
            eprintln!("[SEED material_asset] .mat 読み込み失敗: path={path:?} err={e}");
            return None;
        }
    };

    let asset: MaterialAsset = match serde_json::from_str(&text) {
        Ok(a) => a,
        Err(e) => {
            eprintln!("[SEED material_asset] .mat パース失敗: path={path:?} err={e}");
            return None;
        }
    };

    let arc = Arc::new(asset);
    if let Ok(mut c) = cache().lock() {
        c.insert(path.to_string(), Arc::clone(&arc));
    }
    Some(arc)
}

/// 指定パスのキャッシュを破棄して再ロードする（エディタからの明示リロード IPC 用）。
pub fn reload(path: &str) -> Option<Arc<MaterialAsset>> {
    if let Ok(mut c) = cache().lock() {
        c.remove(path);
    }
    load(path)
}

/// キャッシュ全体を破棄する。
pub fn clear_cache() {
    if let Ok(mut c) = cache().lock() {
        c.clear();
    }
}
