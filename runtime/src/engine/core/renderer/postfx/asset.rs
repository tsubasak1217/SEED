// ============================================================
//  postfx/asset.rs — .postfx ポストエフェクトアセット（JSON）
//
//  【役割】
//  .postfx ファイル（JSON）のデータ型定義・ロード・パスキャッシュを提供する。
//  material_asset.rs と同じ流儀（全 serde default・OnceLock<Mutex<HashMap>> キャッシュ・
//  load/reload/clear_cache）で実装する。
//
//  【スキーマ】
//  {
//    "every_frame": false,
//    "effects": [
//      { "type": "blur",     "radius": 8 },
//      { "type": "vignette", "strength": 0.3, "mask": "assets://..." },
//      { "type": "tint",     "color": [1, 0.9, 0.8, 1] }
//    ]
//  }
//
//  - effects は先頭から順に適用するチェーン。
//  - `type` が未知のエフェクトは警告してスキップする（前方互換）。
//  - every_frame=true はキャッシュを毎フレーム焼き直す（既定 false = 1 回焼いてキャッシュ）。
// ============================================================

use serde::Deserialize;
use std::collections::HashMap;
use std::sync::{Arc, Mutex, OnceLock};

// ─── デフォルト値関数（マジックナンバー禁止のため名前付き）─────────────

/// blur 半径の既定（画素）。
pub const DEFAULT_BLUR_RADIUS: f32 = 4.0;
/// vignette 強度の既定。
pub const DEFAULT_VIGNETTE_STRENGTH: f32 = 0.5;
/// tint 色の既定（恒等＝白）。
fn def_white4() -> [f32; 4] {
    [1.0, 1.0, 1.0, 1.0]
}

// ============================================================
//  PostfxEffect — 個々のエフェクト（解決済み）
// ============================================================

/// .postfx の 1 エフェクト（`type` 判別済みの解決型）。
///
/// JSON からの読み込みは `PostfxAsset::from_json` が `type` 文字列を見て
/// 手動ディスパッチする（未知 type を警告スキップするため serde の tagged enum は使わない）。
#[derive(Clone, Debug)]
pub enum PostfxEffect {
    /// ボックス 3 回近似ガウシアンブラー（いもす法）。`radius` は画素。
    Blur { radius: f32 },
    /// ビネット（周辺減光）。`strength` は強度、`mask` は任意のマスクテクスチャパス（空=全面）。
    Vignette { strength: f32, mask: String },
    /// 乗算色（tint）。`color` は RGBA。
    Tint { color: [f32; 4] },
}

// ============================================================
//  PostfxAsset — .postfx ファイルの内容（解決済み）
// ============================================================

/// `.postfx` ファイル（JSON）に対応するポストエフェクトチェーン定義。
#[derive(Clone, Debug)]
pub struct PostfxAsset {
    /// 毎フレーム焼き直すか（既定 false = 1 回焼いてキャッシュ）。
    pub every_frame: bool,
    /// 適用エフェクトのチェーン（先頭から順に適用）。
    pub effects: Vec<PostfxEffect>,
}

// ─── serde 生スキーマ（JSON パース用の中間表現）─────────────────────────

/// JSON パース用の生アセット。effects は type 判別前の Value として受ける。
#[derive(Deserialize)]
struct PostfxAssetRaw {
    #[serde(default)]
    every_frame: bool,
    #[serde(default)]
    effects: Vec<serde_json::Value>,
}

impl PostfxAsset {
    /// JSON テキストからアセットを構築する。
    ///
    /// 各エフェクトの `type` を見て手動でディスパッチし、未知 type は警告してスキップする。
    /// 各フィールドは欠落時にデフォルト値を使う（全 serde default 相当）。
    pub fn from_json(text: &str) -> Result<Self, serde_json::Error> {
        let raw: PostfxAssetRaw = serde_json::from_str(text)?;
        let mut effects = Vec::with_capacity(raw.effects.len());
        for v in &raw.effects {
            // "type" 文字列を取り出す（無ければスキップ）。
            let ty = v.get("type").and_then(|t| t.as_str()).unwrap_or("");
            match ty.to_ascii_lowercase().as_str() {
                "blur" => {
                    let radius = v
                        .get("radius")
                        .and_then(|x| x.as_f64())
                        .map(|x| x as f32)
                        .unwrap_or(DEFAULT_BLUR_RADIUS);
                    effects.push(PostfxEffect::Blur {
                        radius: radius.max(0.0),
                    });
                }
                "vignette" => {
                    let strength = v
                        .get("strength")
                        .and_then(|x| x.as_f64())
                        .map(|x| x as f32)
                        .unwrap_or(DEFAULT_VIGNETTE_STRENGTH);
                    let mask = v
                        .get("mask")
                        .and_then(|x| x.as_str())
                        .unwrap_or("")
                        .to_string();
                    effects.push(PostfxEffect::Vignette { strength, mask });
                }
                "tint" => {
                    let color = v
                        .get("color")
                        .and_then(|c| serde_json::from_value::<[f32; 4]>(c.clone()).ok())
                        .unwrap_or_else(def_white4);
                    effects.push(PostfxEffect::Tint { color });
                }
                other => {
                    eprintln!("[SEED postfx] 未知のエフェクト type=\"{other}\" をスキップします");
                }
            }
        }
        Ok(Self {
            every_frame: raw.every_frame,
            effects,
        })
    }
}

/// 新規 .postfx 作成時のデフォルト JSON 文字列を返す（雛形の正典・schema ドキュメント）。
///
/// 現状はエディタ（C#）側が同等の雛形を書き出すため crate 内では未使用だが、
/// スキーマの単一情報源として保持する。
#[allow(dead_code)]
pub fn default_postfx_json() -> String {
    // blur → vignette → tint の代表的な 3 エフェクトを雛形として並べる。
    let out = serde_json::json!({
        "every_frame": false,
        "effects": [
            { "type": "blur",     "radius": 4 },
            { "type": "vignette", "strength": 0.3, "mask": "" },
            { "type": "tint",     "color": [1.0, 0.95, 0.9, 1.0] }
        ]
    });
    serde_json::to_string_pretty(&out).unwrap_or_default()
}

// ============================================================
//  パスキャッシュ（material_asset.rs と同流儀）
// ============================================================

/// パス → ロード済み `PostfxAsset` のキャッシュ。
static CACHE: OnceLock<Mutex<HashMap<String, Arc<PostfxAsset>>>> = OnceLock::new();

fn cache() -> &'static Mutex<HashMap<String, Arc<PostfxAsset>>> {
    CACHE.get_or_init(|| Mutex::new(HashMap::new()))
}

/// `.postfx` ファイルをロードする（キャッシュ優先）。
///
/// 読み込み・パース失敗時は `None`（呼び出し元はポスト適用をスキップし元テクスチャを使う）。
pub fn load(path: &str) -> Option<Arc<PostfxAsset>> {
    if let Some(cached) = cache().lock().ok().and_then(|c| c.get(path).cloned()) {
        return Some(cached);
    }

    let text = match crate::engine::asset_fs::read_string(path) {
        Ok(t) => t,
        Err(e) => {
            eprintln!("[SEED postfx] .postfx 読み込み失敗: path={path:?} err={e}");
            return None;
        }
    };
    let asset = match PostfxAsset::from_json(&text) {
        Ok(a) => a,
        Err(e) => {
            eprintln!("[SEED postfx] .postfx パース失敗: path={path:?} err={e}");
            return None;
        }
    };

    let arc = Arc::new(asset);
    if let Ok(mut c) = cache().lock() {
        c.insert(path.to_string(), Arc::clone(&arc));
    }
    Some(arc)
}

/// 指定パスのキャッシュを破棄して再ロードする（ライブ編集反映用）。
pub fn reload(path: &str) -> Option<Arc<PostfxAsset>> {
    if let Ok(mut c) = cache().lock() {
        c.remove(path);
    }
    load(path)
}

/// キャッシュ全体を破棄する。
#[allow(dead_code)]
pub fn clear_cache() {
    if let Ok(mut c) = cache().lock() {
        c.clear();
    }
}
