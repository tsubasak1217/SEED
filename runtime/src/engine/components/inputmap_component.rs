// ============================================================
//  inputmap_component.rs — 入力マップコンポーネント
//
//  アクターに .inputmap アセットファイルへの参照をアタッチする。
//  ランタイムはこのパスから InputMap データをロードし、
//  アクション名 → 物理入力のマッピングを解決する（将来実装）。
//
//  【スクリプトからの利用例（将来）】
//      let val = input_map.get_action_bool("Jump");
//      let dir = input_map.get_action_vector2("Move");
// ============================================================

use crate::engine::ecs::Component;
use serde::{Deserialize, Serialize};

// ─── InputMapComponentData ────────────────────────────────────────────────────

/// InputMapComponent のシリアライズ用データ。
/// シーンファイル保存・Undo スナップショットに使用する。
#[derive(Clone, Serialize, Deserialize)]
pub struct InputMapComponentData {
    /// .inputmap ファイルの絶対パス（空文字列 = 未設定）
    pub asset_path: String,
}

// ─── InputMapComponent ───────────────────────────────────────────────────────

/// 入力マップコンポーネント。
///
/// アクターに .inputmap アセットファイルへの参照をアタッチする。
/// 複数のアクターが同じ .inputmap を参照してもよく、
/// アクターごとに異なる入力マップを持つことも可能（例: 通常操作 / 乗り物操作）。
///
/// # .inputmap ファイル形式
/// JSON 形式。エディタの InputMap エディタウィンドウで編集する。
/// アクション名・値の型・プラットフォームごとのバインディングを定義する。
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct InputMapComponent {
    /// .inputmap ファイルの絶対パス（空文字列 = 未設定）
    pub asset_path: String,
}

impl InputMapComponent {
    /// シリアライズ用データから復元する（to_data の逆）。
    /// シーン読込・Undo/Redo の両方から使う唯一の復元経路。
    pub fn from_data(data: InputMapComponentData) -> Self {
        Self { asset_path: data.asset_path }
    }

    /// シリアライズ用データに変換する。
    pub fn to_data(&self) -> InputMapComponentData {
        InputMapComponentData {
            asset_path: self.asset_path.clone(),
        }
    }
}

impl Default for InputMapComponent {
    /// デフォルトは未設定（アセットパスなし）。
    fn default() -> Self {
        Self {
            asset_path: String::new(),
        }
    }
}

impl Component for InputMapComponent {}
