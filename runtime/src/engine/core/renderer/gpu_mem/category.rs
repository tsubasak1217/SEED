// ============================================================
//  gpu_mem/category.rs — GPU 資源の分類と、分類を決める規則の表
//
//  【決め方】
//  資源を作った呼び出し元のソースの場所（`#[track_caller]` の file）とラベルを、下の表
//  `CATEGORY_RULES` の上から順に照らし、最初に合った行の分類にする（どれにも合わなければ「その他」）。
//    - ラベルの規則を先に置く … 名前付きのレンダーターゲットのプール（post/rt_pool.rs）は 1 か所で
//      HDR の中間・G-Buffer・ブルーム…を作るので、場所では分けられない。
//    - 場所の規則 … モジュールごとに役割が分かれている（shadow.rs は影、ddgi/ は GI…）。
//  照合は小文字にして「含むか」で見る（Windows の `\` は `/` に揃えてから比べる）。
//  分類を増やす・振り分けを直すときは、この表（データ）だけを書き換える。
// ============================================================

/// GPU 資源の分類（内訳の表の 1 行）。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, PartialOrd, Ord, serde::Serialize)]
pub enum GpuMemCategory {
    /// メインの深度（3D 用・UI のオーバーレイ用）と Hi-Z（遮蔽カリング）。
    Depth,
    /// シーンの色の中間（HDR の scene_hdr・UI を重ねる post_ldr・ビネットの post_inter）。
    SceneColor,
    /// G-Buffer（デファードの MRT）とデファードのライティングの資源。
    GBuffer,
    /// シャドウマップ（カスケード・スポット）と影マスク。
    Shadow,
    /// GI（DDGI のプローブ・SSGI）。
    Gi,
    /// クラスタ（ライトの割り当て）。
    Cluster,
    /// ライトの一覧などライティングの共有資源。
    Lighting,
    /// 後処理（トーンマップ・ブルーム・FXAA など）。
    PostProcess,
    /// AO（SSAO / RT-AO とぼかし）。
    Ao,
    /// 反射（SSR / RT 反射）。
    Reflection,
    /// 半透明（WBOIT・屈折の背景）。
    Transparency,
    /// 水面・コースティクス・インタラクションのフィールド。
    Water,
    /// パーティクル。
    Particle,
    /// スカイボックス（天球）。
    Skybox,
    /// 地形・草・散布。
    Terrain,
    /// バインドレスのテクスチャ配列・メガバッファ。
    Bindless,
    /// レイトレーシング（BLAS / TLAS・RT 影）。
    RayTracing,
    /// 3D モデル（頂点・索引・マテリアル・スキニング・インスタンス）とスクリプトの 3D 図形。
    Mesh,
    /// スプライト（画像・バッチ・スキン・テクスチャ単位の後処理）。
    Sprite,
    /// UI の文字（グリフのアトラス・文字の頂点）。
    UiText,
    /// UI の図形（SEED.Draw の 2D 図形・角丸などの形）。
    UiShape,
    /// ピッキング用の ID バッファ。
    Picking,
    /// エディタ専用（ギズモ・アイコン・カメラのプレビュー）。
    Editor,
    /// 撮影（スクリーンショット・サムネイル）。
    Capture,
    /// GPU 時間の計測。
    Timing,
    /// どの規則にも合わなかったもの。
    Other,
}

impl GpuMemCategory {
    /// ログ・文書に出す日本語の名前。
    pub fn name(self) -> &'static str {
        match self {
            Self::Depth => "深度・Hi-Z",
            Self::SceneColor => "シーンの色の中間（HDR・LDR）",
            Self::GBuffer => "G-Buffer・デファード",
            Self::Shadow => "影",
            Self::Gi => "GI",
            Self::Cluster => "クラスタ",
            Self::Lighting => "ライト",
            Self::PostProcess => "後処理",
            Self::Ao => "AO",
            Self::Reflection => "反射",
            Self::Transparency => "半透明",
            Self::Water => "水・インタラクション",
            Self::Particle => "パーティクル",
            Self::Skybox => "スカイボックス",
            Self::Terrain => "地形",
            Self::Bindless => "bindless",
            Self::RayTracing => "レイトレーシング",
            Self::Mesh => "3D モデル",
            Self::Sprite => "スプライト",
            Self::UiText => "UI の文字",
            Self::UiShape => "UI の図形",
            Self::Picking => "ピッキング（ID）",
            Self::Editor => "エディタ",
            Self::Capture => "撮影",
            Self::Timing => "計測",
            Self::Other => "その他",
        }
    }

    /// JSON・スクリプトで使う英字のキー。
    pub fn key(self) -> &'static str {
        match self {
            Self::Depth => "depth",
            Self::SceneColor => "scene_color",
            Self::GBuffer => "gbuffer",
            Self::Shadow => "shadow",
            Self::Gi => "gi",
            Self::Cluster => "cluster",
            Self::Lighting => "lighting",
            Self::PostProcess => "post",
            Self::Ao => "ao",
            Self::Reflection => "reflection",
            Self::Transparency => "transparency",
            Self::Water => "water",
            Self::Particle => "particle",
            Self::Skybox => "skybox",
            Self::Terrain => "terrain",
            Self::Bindless => "bindless",
            Self::RayTracing => "ray_tracing",
            Self::Mesh => "mesh",
            Self::Sprite => "sprite",
            Self::UiText => "ui_text",
            Self::UiShape => "ui_shape",
            Self::Picking => "picking",
            Self::Editor => "editor",
            Self::Capture => "capture",
            Self::Timing => "timing",
            Self::Other => "other",
        }
    }
}

/// 規則が何に照らすか。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RuleTarget {
    /// 資源のラベル（小文字にして「含むか」）。
    Label,
    /// 作った呼び出し元のソースの場所（`/` に揃えて小文字にし「含むか」）。
    Site,
}

/// 分類の規則の 1 行。
#[derive(Clone, Copy, Debug)]
pub struct CategoryRule {
    /// 照らす対象。
    pub target: RuleTarget,
    /// 含まれていれば合う文字列（小文字で書く）。
    pub pattern: &'static str,
    /// 合ったときの分類。
    pub category: GpuMemCategory,
}

/// 規則の 1 行を作る（表を短く書くための補助）。
const fn label(pattern: &'static str, category: GpuMemCategory) -> CategoryRule {
    CategoryRule { target: RuleTarget::Label, pattern, category }
}

/// 規則の 1 行を作る（表を短く書くための補助）。
const fn site(pattern: &'static str, category: GpuMemCategory) -> CategoryRule {
    CategoryRule { target: RuleTarget::Site, pattern, category }
}

/// 分類の規則の表（上から順に照らし、最初に合った行で決める）。
///
/// ラベルの規則（名前付きのプール・汎用の生成箇所を名前で分ける）→ 場所の規則（モジュール単位）の順。
pub const CATEGORY_RULES: &[CategoryRule] = &[
    // ── ラベル（名前付きのレンダーターゲットのプール post/rt_pool.rs と、汎用の生成箇所） ──
    // エディタのカメラのプレビュー（"Camera Preview GBuffer" など）は G-Buffer より先に拾う。
    label("camera preview", GpuMemCategory::Editor),
    label("scene_hdr", GpuMemCategory::SceneColor),
    label("post_ldr", GpuMemCategory::SceneColor),
    label("post_inter", GpuMemCategory::SceneColor),
    label("water_reflection", GpuMemCategory::Water),
    label("gbuffer", GpuMemCategory::GBuffer),
    label("bloom", GpuMemCategory::PostProcess),
    label("wboit", GpuMemCategory::Transparency),
    label("reflection", GpuMemCategory::Reflection),
    label("depth texture", GpuMemCategory::Depth),
    label("overlay depth", GpuMemCategory::Depth),
    // ── 場所（モジュールごとの役割） ──
    site("renderer/gpu_mem/", GpuMemCategory::Other),
    site("renderer/rt_shadow.rs", GpuMemCategory::RayTracing),
    site("renderer/rt_skin_blas.rs", GpuMemCategory::RayTracing),
    site("renderer/shadow", GpuMemCategory::Shadow),
    site("renderer/ddgi/", GpuMemCategory::Gi),
    site("renderer/ssgi.rs", GpuMemCategory::Gi),
    site("renderer/clustered.rs", GpuMemCategory::Cluster),
    site("renderer/lighting.rs", GpuMemCategory::Lighting),
    site("renderer/gbuffer", GpuMemCategory::GBuffer),
    site("renderer/deferred.rs", GpuMemCategory::GBuffer),
    site("renderer/grass_gbuffer.rs", GpuMemCategory::Terrain),
    site("renderer/terrain", GpuMemCategory::Terrain),
    site("renderer/hiz.rs", GpuMemCategory::Depth),
    site("renderer/ao.rs", GpuMemCategory::Ao),
    site("renderer/imos_blur.rs", GpuMemCategory::Ao),
    site("renderer/water_reflection.rs", GpuMemCategory::Water),
    site("renderer/reflection", GpuMemCategory::Reflection),
    site("renderer/refract_pyramid.rs", GpuMemCategory::Transparency),
    site("renderer/transparency.rs", GpuMemCategory::Transparency),
    site("renderer/water/", GpuMemCategory::Water),
    site("renderer/caustics.rs", GpuMemCategory::Water),
    site("renderer/interaction/", GpuMemCategory::Water),
    site("renderer/particle", GpuMemCategory::Particle),
    site("renderer/skybox.rs", GpuMemCategory::Skybox),
    site("renderer/bindless.rs", GpuMemCategory::Bindless),
    site("renderer/post/", GpuMemCategory::PostProcess),
    site("renderer/postfx/", GpuMemCategory::Sprite),
    site("renderer/batch2d.rs", GpuMemCategory::Sprite),
    site("renderer/sprite_skin.rs", GpuMemCategory::Sprite),
    site("drawer/sprite_drawer.rs", GpuMemCategory::Sprite),
    site("renderer/primitive2d/", GpuMemCategory::UiShape),
    site("renderer/ui_shape/", GpuMemCategory::UiShape),
    site("font/axis_gizmo.rs", GpuMemCategory::Editor),
    site("font/icon_overlay.rs", GpuMemCategory::Editor),
    site("font/hint_plate.rs", GpuMemCategory::Editor),
    site("font/screen_hint.rs", GpuMemCategory::Editor),
    site("core/font/", GpuMemCategory::UiText),
    site("drawer/id_pass.rs", GpuMemCategory::Picking),
    site("drawer/primitive_drawer.rs", GpuMemCategory::Editor),
    site("renderer/screenshot.rs", GpuMemCategory::Capture),
    site("renderer/thumbnail/", GpuMemCategory::Capture),
    site("renderer/gpu_timing/", GpuMemCategory::Timing),
    site("renderer/primitive3d/", GpuMemCategory::Mesh),
    site("renderer/gpu_resources.rs", GpuMemCategory::Mesh),
    site("renderer/skin_system.rs", GpuMemCategory::Mesh),
];

/// 照合のためにソースの場所を揃える（`\` → `/`・小文字）。
pub fn normalize_site(file: &str) -> String {
    file.replace('\\', "/").to_ascii_lowercase()
}

/// ラベルとソースの場所から分類を決める（表 `rules` を上から照らす。合わなければ「その他」）。
///
/// * `label` … 資源のラベル（無ければ空文字列）
/// * `site`  … `normalize_site` で揃えたソースの場所
pub fn classify_with(rules: &[CategoryRule], label: &str, site: &str) -> GpuMemCategory {
    let label_lower = label.to_ascii_lowercase();
    rules
        .iter()
        .find(|rule| match rule.target {
            RuleTarget::Label => label_lower.contains(rule.pattern),
            RuleTarget::Site => site.contains(rule.pattern),
        })
        .map(|rule| rule.category)
        .unwrap_or(GpuMemCategory::Other)
}

/// 組み込みの表 `CATEGORY_RULES` で分類を決める。
pub fn classify(label: &str, site: &str) -> GpuMemCategory {
    classify_with(CATEGORY_RULES, label, site)
}

// ============================================================
//  資産ごとに作る場所（積み増しとして数える）
// ============================================================

/// 「同じ場所・同じラベルでも、作るたびに別の資産として積み増す」場所の表（ソースの場所に含まれる文字列。小文字）。
///
/// ここに無い場所は「後のフレームで同じ場所・同じラベルで作り直したら、前のものは捨てた」とみなす
/// （レンダーターゲットの作り直し・伸びるバッファ・毎フレームの使い捨てのバッファ）。
/// スプライトの画像（ラベルが全部 "SpriteTexture"）やモデルの頂点のように、資産ごとに同じラベルで
/// 別々に作る場所は、作り直しとみなすと 1 つしか数えないので、ここに書いて積み増す（解放は追えない）。
pub const ACCUMULATING_SITES: &[&str] = &[
    "drawer/sprite_drawer.rs",
    "renderer/gpu_resources.rs",
    "renderer/sprite_skin.rs",
    "renderer/postfx/bake.rs",
    "renderer/skin_system.rs",
    "renderer/particle_shapes.rs",
];

/// この場所で作った資源は積み増しとして数えるか（`ACCUMULATING_SITES` に含まれるか）。
pub fn is_accumulating_site(site: &str) -> bool {
    ACCUMULATING_SITES.iter().any(|pattern| site.contains(pattern))
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 名前付きのプールで作るものはラベルで分かれる（同じ場所 rt_pool.rs でも別の分類）。
    #[test]
    fn pool_targets_are_split_by_label() {
        let pool = normalize_site("src\\engine\\core\\renderer\\post\\rt_pool.rs");
        assert_eq!(classify("scene_hdr", &pool), GpuMemCategory::SceneColor);
        assert_eq!(classify("post_ldr", &pool), GpuMemCategory::SceneColor);
        assert_eq!(classify("gbuffer0", &pool), GpuMemCategory::GBuffer);
        assert_eq!(classify("bloom_mip_2", &pool), GpuMemCategory::PostProcess);
        // ラベルの規則に無い名前はプールの場所（post/）の分類＝後処理。
        assert_eq!(classify("fxaa_tmp", &pool), GpuMemCategory::PostProcess);
        // エディタのカメラのプレビューの G-Buffer は G-Buffer より先にエディタへ振る。
        let app = normalize_site("src/engine/core/app_base/app/mod.rs");
        assert_eq!(classify("Camera Preview GBuffer", &app), GpuMemCategory::Editor);
    }

    /// 場所の規則はモジュール単位で効く（Windows の区切りでも同じ）。
    #[test]
    fn sites_map_to_module_roles() {
        let shadow = normalize_site("src\\engine\\core\\renderer\\shadow.rs");
        assert_eq!(classify("Shadow Depth Array", &shadow), GpuMemCategory::Shadow);
        let ddgi = normalize_site("src/engine/core/renderer/ddgi/resources.rs");
        assert_eq!(classify("GI Atlas", &ddgi), GpuMemCategory::Gi);
        let font = normalize_site("src/engine/core/font/atlas.rs");
        assert_eq!(classify("Glyph Atlas", &font), GpuMemCategory::UiText);
        let gizmo = normalize_site("src/engine/core/font/axis_gizmo.rs");
        assert_eq!(classify("Axis Gizmo Geo Buffer", &gizmo), GpuMemCategory::Editor);
        let rt = normalize_site("src/engine/core/renderer/rt_shadow.rs");
        assert_eq!(classify("TLAS", &rt), GpuMemCategory::RayTracing);
    }

    /// どの規則にも合わなければ「その他」。
    #[test]
    fn unknown_is_other() {
        assert_eq!(classify("whatever", "src/somewhere/else.rs"), GpuMemCategory::Other);
    }

    /// 資産ごとに作る場所だけが積み増しになる。
    #[test]
    fn accumulating_sites_are_asset_loaders() {
        assert!(is_accumulating_site(&normalize_site("src\\engine\\methods\\drawer\\sprite_drawer.rs")));
        assert!(!is_accumulating_site(&normalize_site("src\\engine\\core\\renderer\\post\\rt_pool.rs")));
    }

    /// 分類の名前とキーはすべて空でなく、キーは重複しない（JSON の鍵になるため）。
    #[test]
    fn names_and_keys_are_unique() {
        let all = [
            GpuMemCategory::Depth, GpuMemCategory::SceneColor, GpuMemCategory::GBuffer,
            GpuMemCategory::Shadow, GpuMemCategory::Gi, GpuMemCategory::Cluster,
            GpuMemCategory::Lighting, GpuMemCategory::PostProcess, GpuMemCategory::Ao,
            GpuMemCategory::Reflection, GpuMemCategory::Transparency, GpuMemCategory::Water,
            GpuMemCategory::Particle, GpuMemCategory::Skybox, GpuMemCategory::Terrain,
            GpuMemCategory::Bindless, GpuMemCategory::RayTracing, GpuMemCategory::Mesh,
            GpuMemCategory::Sprite, GpuMemCategory::UiText, GpuMemCategory::UiShape,
            GpuMemCategory::Picking, GpuMemCategory::Editor, GpuMemCategory::Capture,
            GpuMemCategory::Timing, GpuMemCategory::Other,
        ];
        let mut keys = std::collections::HashSet::new();
        for c in all {
            assert!(!c.name().is_empty());
            assert!(keys.insert(c.key()), "キーが重複: {}", c.key());
        }
    }
}
