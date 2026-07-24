// ============================================================
//  prefab/mod.rs — プレハブオーバーライド機構（データ層）
//
//  【背景】
//  プレハブインスタンス（Actor::prefab_source 付き）は、シーンロード時に
//  参照先 `.actor` ファイルの内容で子ツリー・コンポーネントを丸ごと再展開される
//  （prefab_ops.rs）。この仕様だけだと「シーン側でインスタンスに加えた変更」が
//  再展開で消える（＝データ損失）。
//
//  【方式】
//  Unity のプレハブオーバーライドに相当する差分を `.scene` 側に保存し、
//  再展開の直後に再適用することで、
//    - プレハブ本体の編集は全インスタンスへ反映される（従来の利点を維持）
//    - シーン側の個別変更は失われない
//  の両立を図る。
//
//  【差分の検出方式】
//  「編集操作時にフラグを立てる」方式は採らない。IPC の編集経路は多数あり、
//  どれか 1 本にマーク処理を通し忘れるだけで差分が失われるため。
//  代わりに **シーン保存時にプレハブ本体（.actor）と現在の状態を比較して
//  自動抽出する**（extract.rs）。編集経路が何本あっても構造的に漏れが起きない。
//
//  【差分の粒度】
//  コンポーネント単位。SEED のコンポーネントは serde で丸ごと直列化されるため、
//  フィールド単位（Unity の PropertyModification 相当）ではなくコンポーネント単位の
//  差分が素直に扱える。
//
//  【モジュール構成】
//  - overrides.rs : 差分のデータ構造（`.scene` へ保存される形）
//  - extract.rs   : 保存時の差分抽出（現在の状態 vs プレハブ本体）
//  - apply.rs     : ロード時の再適用（プレハブ本体データへのマージ）
//  抽出・再適用とも ActorData だけを扱う純粋なデータ処理であり、
//  ECS World / GPU に依存しない（＝ユニットテスト可能）。
//  実際に呼び出す側は Scene::save（抽出）と prefab_ops::reinstantiate_single（再適用）。
// ============================================================

pub mod overrides;
pub mod extract;
pub mod apply;

#[cfg(test)]
mod tests;

// 他モジュールから直接使うものだけ再エクスポートする
// （それ以外は `prefab::overrides::…` / `prefab::extract::…` で参照する）。
pub use overrides::{PrefabOverrides, SINGULAR_SCALE_EPS};
pub use extract::{compute_delta_for_root, refresh_prefab_overrides};
pub use apply::{apply_delta_to_subtree, merge_overrides_into};
