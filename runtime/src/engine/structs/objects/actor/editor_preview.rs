// ============================================================
//  actor/editor_preview.rs — エディタのプレビューの印（保存されない表示用のアクタ）
//
//  【何のためにあるか】
//  画面をすべて実行時にスクリプトがプレハブから積むアプリでは、Edit 上で画面が見えない。
//  そこでエディタが任意のプレハブ（.actor）を任意のノードの下へ「保存されないプレビュー」として
//  差し込めるようにした（docs/editor_screen_preview.md。本体は app/editor_preview/）。
//  プレビューの根のアクタだけがこの印（＋作り直しの材料）を持つ。
//
//  【この印の行き先（守っていること）】
//  ・.scene / .actor のファイル・コピー・写し（SNAPSHOT_SCENE）には入れない。
//    保存系の経路がそれぞれ `strip_editor_previews` で取り除く（濾過の場所の一覧は app/editor_preview/mod.rs）。
//  ・Undo の写し・Play 開始時の写しなど「メモリ上の写し」には残す（Undo/Play の後に印ごと戻るため）。
//
//  ここは純粋なデータとその走査だけを持つ（App や World に依存しない）。
// ============================================================

use serde::{Deserialize, Serialize};

use super::ActorData;

/// エディタのプレビューの根の印と、作り直すための材料（docs/editor_screen_preview.md）。
///
/// プレビューの根のアクタ（`Actor::editor_preview` / `ActorData::editor_preview`）だけが Some を持つ。
/// 「プレハブから作り直す」（PREVIEW_REFRESH_PATH）はこの材料だけで同じ形を組み立て直す。
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct EditorPreviewInfo {
    /// 中身のプレハブ（エディタが渡した assets:// 仮想パス or 絶対パス）
    /// （ファイルへは書かない印だが、シリアライズ用データ型の規約どおり欄の欠落でも読めるよう既定値を付ける）
    #[serde(default)]
    pub prefab: String,
    /// 枠のプレハブ（ScreenStack への差し込みで使う screen_frame.actor 等。無ければ None）
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub frame: Option<String>,
    /// 枠の中で中身を入れる子のパス（'/' 区切りの名前。空なら枠の直下）
    #[serde(default, skip_serializing_if = "String::is_empty")]
    pub frame_body: String,
    /// 根のレイヤーの底上げ（根の最初の CanvasLayoutItem の `layer_bias` へ書く。0 = 付けない）。
    ///
    /// 実行時は ScreenStack・ModalHost が `layer_bias`（保存されない実行中だけの欄）を付けて画面を前後に並べるが、
    /// Edit ではスクリプトが動かないので、プレビューの根に付けて「実行時の見た目」に近づける（値はエディタが決めて送る）。
    /// `layer_bias` 自体は保存されないので、作り直しの材料としてここに持つ（Undo・Play のメモリ上の写しに残り、
    /// 組み直し〈build_actor〉と作り直し〈PREVIEW_REFRESH_PATH〉で同じ値が付く。適用は editor_preview_bias.rs）。
    #[serde(default, skip_serializing_if = "is_no_layer_bias")]
    pub layer_bias: i32,
}

/// 底上げなし（`layer_bias` の既定）。
pub const NO_LAYER_BIAS: i32 = 0;

/// serde 用: 底上げなし（0）なら書き出さない（印の形を最小に保つ）。
fn is_no_layer_bias(bias: &i32) -> bool {
    *bias == NO_LAYER_BIAS
}

/// 子孫のうち editor_preview を持つノードを部分木ごと取り除く（自分自身は見ない）。
///
/// 保存系の経路（.scene の保存・.actor の書き出し・コピー・写し）が、書き出す直前のデータに使う。
/// 自分自身を見ないのは、呼び出し側が「根がプレビューなら書かない（飛ばす・拒否する）」を
/// 経路ごとに決めるため（.scene は飛ばす・.actor は拒否する）。
///
/// # 戻り値
/// 取り除いた根の数（取り除いた部分木の中にあった入れ子のプレビューは数えない）。
pub fn strip_editor_previews(data: &mut ActorData) -> usize {
    // 直下の子のうちプレビューの根を部分木ごと外す
    let before = data.children.len();
    data.children.retain(|child| child.editor_preview.is_none());
    let mut removed = before - data.children.len();
    // 残った子の中へ降りて、さらに内側のプレビューを外す
    for child in &mut data.children {
        removed += strip_editor_previews(child);
    }
    removed
}

/// 自分か子孫のどれかが editor_preview を持つか。
///
/// `.actor` の書き出しで「含まなければ複製せずにそのまま書く」を判断するのに使う。
pub fn contains_editor_preview(data: &ActorData) -> bool {
    data.editor_preview.is_some() || data.children.iter().any(contains_editor_preview)
}

// ============================================================
//  テスト — 印の serde 往復と、保存系の濾過（取り除く・含むかの判定）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用: 名前と子だけのアクタデータを JSON から作る（ActorData は欄が多いので JSON の既定値に任せる）。
    fn node(name: &str, children: Vec<ActorData>) -> ActorData {
        let mut data: ActorData = serde_json::from_str(&format!(
            r#"{{"name":{},"components":[],"children":[]}}"#,
            serde_json::to_string(name).unwrap()
        ))
        .expect("最小のアクタデータが読めること");
        data.children = children;
        data
    }

    /// テスト用: プレビューの根の印を付ける。
    fn mark(mut data: ActorData, prefab: &str) -> ActorData {
        data.editor_preview = Some(EditorPreviewInfo {
            prefab: prefab.to_string(),
            frame: None,
            frame_body: String::new(),
            layer_bias: NO_LAYER_BIAS,
        });
        data
    }

    /// Some は書かれて往復し、None は書かれず、欄の無い旧 JSON は None として読めること。
    #[test]
    fn editor_preview_serde_roundtrip_and_backward_compat() {
        // Some（枠つき）は書かれ、往復で同じ値に戻る
        let mut data = node("Screen", Vec::new());
        data.editor_preview = Some(EditorPreviewInfo {
            prefab: "assets://ui/screens/Home.actor".to_string(),
            frame: Some("assets://ui/prefabs/screen_frame.actor".to_string()),
            frame_body: "Body".to_string(),
            layer_bias: NO_LAYER_BIAS,
        });
        let json = serde_json::to_string(&data).unwrap();
        assert!(json.contains("\"editor_preview\""), "Some は書かれること: {json}");
        let back: ActorData = serde_json::from_str(&json).unwrap();
        assert_eq!(back.editor_preview, data.editor_preview, "往復で同じ値に戻ること");

        // 枠なし・本体の空は省かれる（frame / frame_body の欄を出さない）
        let plain = mark(node("Screen", Vec::new()), "assets://ui/a.actor");
        let json = serde_json::to_string(&plain).unwrap();
        assert!(!json.contains("frame"), "枠なしは frame の欄を出さないこと: {json}");

        // None は書かれない（既存の .scene / .actor とバイト互換）
        let none = node("Plain", Vec::new());
        let json = serde_json::to_string(&none).unwrap();
        assert!(!json.contains("editor_preview"), "None は書かれないこと: {json}");

        // 欄の無い旧 JSON は None として読める
        let legacy: ActorData = serde_json::from_str(r#"{"name":"old","components":[],"children":[]}"#).unwrap();
        assert!(legacy.editor_preview.is_none(), "欄の無い旧 JSON は None");
    }

    /// レイヤーの底上げ: 0 は書かず、0 でない値は書かれて往復し、欄の無い（底上げを足す前の）印は 0 として読めること。
    #[test]
    fn layer_bias_serde_omits_zero_and_defaults_to_zero() {
        // 0（底上げなし）は書かない
        let plain = mark(node("Screen", Vec::new()), "assets://ui/a.actor");
        let json = serde_json::to_string(&plain).unwrap();
        assert!(!json.contains("layer_bias"), "0 は書かないこと: {json}");

        // 0 でない値は書かれ、往復で同じ値に戻る（負の値も）
        for bias in [10_000, 3_000_000, -5] {
            let mut data = mark(node("Screen", Vec::new()), "assets://ui/a.actor");
            data.editor_preview.as_mut().unwrap().layer_bias = bias;
            let json = serde_json::to_string(&data).unwrap();
            assert!(json.contains(&format!("\"layer_bias\":{bias}")), "0 でない値は書かれること: {json}");
            let back: ActorData = serde_json::from_str(&json).unwrap();
            assert_eq!(back.editor_preview.unwrap().layer_bias, bias, "往復で同じ値に戻ること");
        }

        // 欄の無い印（底上げを足す前の形）は 0 として読める
        let old: EditorPreviewInfo = serde_json::from_str(r#"{"prefab":"assets://ui/a.actor"}"#).unwrap();
        assert_eq!(old.layer_bias, NO_LAYER_BIAS, "欄が無ければ底上げなし");
    }

    /// 入れ子のプレビューだけが部分木ごと消え、ほかは残り、取り除いた根の数が合うこと。
    #[test]
    fn strip_removes_only_preview_subtrees_and_counts_roots() {
        // Root
        // ├─ Keep
        // │   └─ PreviewA（根）
        // │        └─ PreviewInner（根。外側ごと消えるので数えない）
        // ├─ PreviewB（根）
        // │   └─ UnderB
        // └─ Keep2
        //     └─ Leaf
        let inner = mark(node("PreviewInner", Vec::new()), "assets://inner.actor");
        let preview_a = mark(node("PreviewA", vec![inner]), "assets://a.actor");
        let preview_b = mark(node("PreviewB", vec![node("UnderB", Vec::new())]), "assets://b.actor");
        let mut root = node(
            "Root",
            vec![
                node("Keep", vec![preview_a]),
                preview_b,
                node("Keep2", vec![node("Leaf", Vec::new())]),
            ],
        );

        assert_eq!(strip_editor_previews(&mut root), 2, "外側の根 PreviewA・PreviewB の 2 つ");
        let names: Vec<&str> = root.children.iter().map(|c| c.name.as_str()).collect();
        assert_eq!(names, vec!["Keep", "Keep2"], "プレビューでない子は並びごと残ること");
        assert!(root.children[0].children.is_empty(), "Keep の下のプレビューは部分木ごと消えること");
        assert_eq!(root.children[1].children[0].name, "Leaf", "プレビューの無い枝は手を付けないこと");
        assert!(!contains_editor_preview(&root), "取り除いた後はプレビューを含まないこと");
        assert_eq!(strip_editor_previews(&mut root), 0, "2 度目は何も外さないこと");
    }

    /// 自分自身の印は strip では見ない（呼び出し側が根を飛ばす・拒否する）。contains は自分も見ること。
    #[test]
    fn strip_ignores_self_and_contains_sees_self_and_descendants() {
        let mut root = mark(node("PreviewRoot", vec![node("Child", Vec::new())]), "assets://r.actor");
        assert_eq!(strip_editor_previews(&mut root), 0, "自分自身は外さないこと");
        assert!(root.editor_preview.is_some(), "根の印はそのまま");
        assert!(contains_editor_preview(&root), "自分の印を見ること");

        let nested = node("Outer", vec![node("Mid", vec![mark(node("Deep", Vec::new()), "assets://d.actor")])]);
        assert!(contains_editor_preview(&nested), "深い子孫の印を見ること");
        assert!(!contains_editor_preview(&node("Plain", vec![node("Child", Vec::new())])), "印が無ければ false");
    }
}
