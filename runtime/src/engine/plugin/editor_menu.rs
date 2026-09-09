// ============================================================
//  plugin/editor_menu.rs — plugin.json の editor_menus 定義
//
//  【役割】
//  プラグインがエディタのメニューバーへ追加する項目を、コードではなく
//  plugin.json（データ）で宣言するための型を定義する。
//  ここは「宣言の器」だけを持ち、実行（on_editor_action の呼び出し）は
//  registry.rs、UI 生成はエディタ側（C#）が担当する（単一責任）。
//
//  【plugin.json の記述例】
//  ```json
//  "editor_menus": [
//    {
//      "menu": "Game",
//      "items": [
//        { "id": "delete_save",
//          "label": "ユーザーデータ削除",
//          "confirm": "セーブデータ（save.json）を削除します。よろしいですか？" },
//        { "separator": true },
//        { "id": "dump_save", "label": "セーブ内容をログ出力" }
//      ]
//    }
//  ]
//  ```
//
//  【マージ規則】
//  - `menu` はトップレベルメニュー名。複数プラグインが同じ名前を宣言した
//    場合はエディタ側で 1 つのメニューへマージされる。
//  - エディタに同名の静的メニュー（「表示」など）が既にある場合は、
//    新規作成せずそのメニューへ項目が追加される。
//  - `separator: true` の項目は区切り線として描画され、`id` / `label` は無視する。
//  - `confirm` が空でなければ、実行前に Yes/No の確認ダイアログを表示する。
// ============================================================

use serde::{Deserialize, Serialize};

// ============================================================
//  EditorMenuItemDef — メニュー項目 1 件
// ============================================================

/// プラグインが宣言するメニュー項目 1 件。
///
/// すべてのフィールドが `serde(default)` なので、
/// `{ "separator": true }` だけの項目も、`id` と `label` だけの項目も書ける。
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct EditorMenuItemDef {
    /// アクション識別子。`Plugin::on_editor_action` にそのまま渡される。
    /// 区切り線の場合は空でよい。
    #[serde(default)]
    pub id: String,

    /// メニューに表示するラベル。空の場合は `id` が表示される。
    #[serde(default)]
    pub label: String,

    /// 実行前の確認ダイアログ本文。空文字列なら確認なしで即実行する。
    #[serde(default)]
    pub confirm: String,

    /// true の場合、この項目は区切り線として描画される（クリック不可）。
    #[serde(default)]
    pub separator: bool,
}

impl EditorMenuItemDef {
    /// 実際に表示するラベルを返す（`label` 未指定なら `id` で代用する）。
    pub fn display_label(&self) -> &str {
        if self.label.is_empty() {
            self.id.as_str()
        } else {
            self.label.as_str()
        }
    }

    /// クリックして実行できる項目か（区切り線でなく、id を持つ）。
    pub fn is_actionable(&self) -> bool {
        !self.separator && !self.id.is_empty()
    }
}

// ============================================================
//  EditorMenuDef — トップレベルメニュー 1 つ分
// ============================================================

/// プラグインが宣言するトップレベルメニュー 1 つ分の定義。
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct EditorMenuDef {
    /// トップレベルメニュー名（例: "Game"）。同名は複数プラグイン間でマージされる。
    #[serde(default)]
    pub menu: String,

    /// このメニューへ追加する項目の一覧。
    #[serde(default)]
    pub items: Vec<EditorMenuItemDef>,
}

impl EditorMenuDef {
    /// エディタへ送る価値がある定義か（メニュー名と項目が揃っている）。
    pub fn is_valid(&self) -> bool {
        !self.menu.is_empty() && !self.items.is_empty()
    }
}
