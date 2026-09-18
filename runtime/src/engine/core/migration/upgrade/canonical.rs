// ============================================================
//  upgrade/canonical.rs — 変換後の値を「エンジンが保存するのと同じテキスト」にする
//
//  【なぜ必要か】
//  一括アップグレードは変換後の値をファイルへ書き戻す。`serde_json::Value` を
//  そのまま `to_string_pretty` すると欄がアルファベット順へ並び替わり、
//  中身が 1 つも変わらないファイルでも全行が差分になる。
//  本体の型（`SceneData` / `ActorData`）を経由すれば欄の並びは宣言順のまま
//  ＝**普通に保存したときと同じテキスト**になる。
//
//  【書き手がエディタ（C#）の形式】
//  `.anim` / 地形 JSON / `project_settings.json` / `.inputmap` などは
//  ランタイムに「保存する側の型」が無い（読むための型しか無い）。
//  それらは正準テキストを作れないので、**読めるかどうかの検証だけ**行う。
//  検証に通らないファイルは `failed` として**書き換えない**（安全弁）。
//
//  【この 1 か所に集約する理由】
//  形式ごとの本体の型を知っているのはここだけにする。形式を足したときに
//  網羅 match が埋まらずビルドが落ちるので、検証の付け忘れが起きない。
// ============================================================

use serde_json::Value;

use crate::engine::core::migration::kind::FormatKind;

// ── 定数 ─────────────────────────────────────────────────────────

/// 検証時に各ローダへ渡す識別子（エラーメッセージに出る）。
const VALIDATION_LABEL: &str = "(upgrade)";

// ── 結果の型 ─────────────────────────────────────────────────────

/// 変換後の値をテキストにできたか、検証だけ済んだか。
pub enum CanonicalText {
    /// 本体の型を経由した正準テキスト（欄の並びが普通の保存と同じ）。
    Rendered(String),
    /// 正準の書き手がランタイムに無い形式。読めることの検証だけ済んでいる。
    ValidatedOnly,
}

impl CanonicalText {
    /// 正準テキストがあれば返す。
    pub fn text(self) -> Option<String> {
        match self {
            CanonicalText::Rendered(t) => Some(t),
            CanonicalText::ValidatedOnly => None,
        }
    }
}

// ── 振り分け ─────────────────────────────────────────────────────

/// 変換後の `value` を正準テキストにする。作れない形式は検証だけ行う。
///
/// エラーは「このファイルをエンジンが読めない」という意味で、呼び出し側は
/// そのファイルを `failed` にして**書き換えない**。
pub fn render_or_validate(kind: FormatKind, value: Value) -> Result<CanonicalText, String> {
    match kind {
        // ── ランタイムが保存する形式（正準テキストを作れる）──
        FormatKind::Scene => crate::engine::core::app_base::scene::scene_text_from_value(value)
            .map(CanonicalText::Rendered)
            .map_err(|e| format!("シーンとして書き出せません: {e}")),
        FormatKind::Actor => crate::engine::core::app_base::actor_file::text_from_value(value)
            .map(CanonicalText::Rendered)
            .map_err(|e| format!("アクタとして書き出せません: {e}")),

        // ── エディタ（C#）が保存する形式（読めることだけ検証する）──
        FormatKind::Anim => validate_with_text(value, "アニメーションクリップ", |text| {
            crate::engine::animation::AnimationClip::from_json(VALIDATION_LABEL, text)
                .map(|_| ())
        }),
        FormatKind::Material => serde_json::from_value::<
            crate::engine::core::renderer::material_asset::MaterialAsset,
        >(value)
        .map(|_| CanonicalText::ValidatedOnly)
        .map_err(|e| format!("マテリアルとして読めません: {e}")),
        FormatKind::Postfx => validate_with_text(value, "ポストエフェクト", |text| {
            crate::engine::core::renderer::postfx::asset::PostfxAsset::from_json(text)
                .map(|_| ())
                .map_err(|e| e.to_string())
        }),
        FormatKind::InputMap => validate_with_text(value, "入力アクションマップ", |text| {
            crate::engine::core::input::action_map::ActionMap::validate_json(text)
        }),
        FormatKind::SpriteMesh => validate_with_text(value, "スプライトメッシュ", |text| {
            crate::engine::core::loader::sprite_mesh::SpriteMesh::from_json(text)
                .map(|_| ())
                .map_err(|e| e.to_string())
        }),
        FormatKind::TerrainLayers => validate_with_text(value, "地形レイヤ定義", |text| {
            crate::engine::terrain::TerrainLayerSet::from_json_str(text).map(|_| ())
        }),
        FormatKind::TerrainProps => validate_with_text(value, "地形プロップ定義", |text| {
            crate::engine::terrain::scatter::TerrainPropSet::from_json_str(text).map(|_| ())
        }),
        FormatKind::TerrainCoverMaterials => {
            validate_with_text(value, "地形カバー材質定義", |text| {
                crate::engine::terrain::cover::CoverMaterialSet::from_json_str(text).map(|_| ())
            })
        }
        // プロジェクト設定はランタイム側に型付きスキーマが無い（キーを個別に拾って読む）。
        // トップレベルがオブジェクトであることは版の読み取りで既に確かめてあるので、
        // ここで追加の検証は行わない。
        FormatKind::ProjectSettings => Ok(CanonicalText::ValidatedOnly),
    }
}

// ── ヘルパー ─────────────────────────────────────────────────────

/// `Value` をテキストへ戻してから、テキストを受け取るローダで検証する。
///
/// * `what` … 失敗メッセージに出す日本語の形式名
///
/// ランタイム側のローダはどれもテキスト入口しか持たないため、ここで 1 回だけ
/// 直列化して渡す（一括アップグレードは 1 ファイル 1 回しか通らない処理なので、
/// この往復のコストは問題にならない）。
fn validate_with_text<F>(value: Value, what: &str, load: F) -> Result<CanonicalText, String>
where
    F: FnOnce(&str) -> Result<(), String>,
{
    let text = serde_json::to_string(&value)
        .map_err(|e| format!("{what}の JSON 化に失敗しました: {e}"))?;
    load(&text).map_err(|e| format!("{what}として読めません: {e}"))?;
    Ok(CanonicalText::ValidatedOnly)
}

// ── テスト ───────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    /// ランタイムが保存する形式は正準テキストを返すこと。
    #[test]
    fn runtime_written_formats_return_text() {
        let actor = json!({
            "format_version": FormatKind::Actor.current_version(),
            "name": "A", "components": [], "children": []
        });
        let rendered = render_or_validate(FormatKind::Actor, actor).expect("読めること");
        let text = rendered.text().expect("正準テキストが返るはず");
        assert!(text.contains("\"name\": \"A\""), "{text}");
    }

    /// エディタが保存する形式は検証だけ行い、テキストは返さないこと。
    #[test]
    fn editor_written_formats_only_validate() {
        let anim = json!({ "name": "Swim", "duration": 1.0, "tracks": [] });
        let result = render_or_validate(FormatKind::Anim, anim).expect("読めること");
        assert!(result.text().is_none(), "検証だけの形式がテキストを返している");
    }

    /// エンジンが読めない中身はエラーになること（＝ファイルを書き換えない安全弁）。
    #[test]
    fn unreadable_content_is_rejected() {
        // アクタとして必須の構造が無い
        let broken = json!({ "name": 12345 });
        assert!(render_or_validate(FormatKind::Actor, broken).is_err());

        // スプライトメッシュは中身の整合まで検証する（頂点が空）
        let broken_mesh = json!({
            "version": 1, "vertices": [], "uvs": [], "triangles": [], "bones": [], "weights": []
        });
        assert!(render_or_validate(FormatKind::SpriteMesh, broken_mesh).is_err());
    }

    /// 全形式について、最小の妥当な中身なら `render_or_validate` が通ること。
    ///
    /// 形式を足したときに検証の腕を書き忘れる（＝常に失敗する）のを防ぐ。
    #[test]
    fn every_format_accepts_a_minimal_document() {
        for kind in FormatKind::ALL.iter().copied() {
            let value = minimal_document(kind);
            render_or_validate(kind, value)
                .unwrap_or_else(|e| panic!("{kind}: 最小の中身が読めない: {e}"));
        }
    }

    /// 形式ごとの「最小の妥当な中身」。
    fn minimal_document(kind: FormatKind) -> Value {
        match kind {
            FormatKind::Scene => json!({ "name": "S", "actors": [] }),
            FormatKind::Actor => json!({ "name": "A", "components": [], "children": [] }),
            FormatKind::Anim => json!({ "name": "Swim", "duration": 1.0, "tracks": [] }),
            FormatKind::Material => json!({ "name": "M" }),
            FormatKind::Postfx => json!({ "every_frame": false, "effects": [] }),
            FormatKind::InputMap => json!({ "version": 2, "actions": [] }),
            FormatKind::SpriteMesh => minimal_sprite_mesh(),
            FormatKind::TerrainLayers => json!({ "layers": [ { "name": "L" } ] }),
            FormatKind::TerrainProps => json!({ "props": [] }),
            FormatKind::TerrainCoverMaterials => json!({ "materials": [] }),
            FormatKind::ProjectSettings => json!({ "game_name": "G" }),
        }
    }

    /// 検証を通る最小の `.sprite_mesh`（三角形 1 枚・ボーン 1 本）。
    fn minimal_sprite_mesh() -> Value {
        json!({
            "version": 1,
            "vertices": [[0.0, 0.0], [1.0, 0.0], [0.0, 1.0]],
            "uvs": [[0.0, 0.0], [1.0, 0.0], [0.0, 1.0]],
            "triangles": [0, 1, 2],
            "bones": [ { "name": "root" } ],
            "weights": [
                [ { "bone": 0, "weight": 1.0 } ],
                [ { "bone": 0, "weight": 1.0 } ],
                [ { "bone": 0, "weight": 1.0 } ]
            ]
        })
    }
}
