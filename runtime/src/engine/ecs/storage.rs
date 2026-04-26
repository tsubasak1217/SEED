// ============================================================
//  storage.rs — コンポーネントのスパースセットストレージ
//
//  【設計】
//  SparseSet<T> は ECS の中核データ構造。
//  - sparse 配列 : entity.index → dense スロット番号（高速 O(1) ルックアップ）
//  - dense 配列  : (Entity, T) の連続メモリ（イテレーションがキャッシュフレンドリー）
//
//  Component トレイトは「コンポーネントとして使える型」を示すマーカー。
//  ライフサイクルロジックは持たない — それは System の責務。
// ============================================================

use std::any::{Any, TypeId};
use super::entity::Entity;

// ─── Component マーカートレイト ────────────────────────────────────────────────

/// ECS コンポーネントとして World に格納できる型のマーカートレイト。
///
/// 実装要件:
///   - Any + Send + Sync + 'static
///   - ライフサイクルメソッドを持たない（System が担う）
///   - 純粋なデータ構造であること
pub trait Component: Any + Send + Sync + 'static {}

// ─── AnyStorage トレイト ────────────────────────────────────────────────────────

/// 型消去されたコンポーネントストレージ。
/// World が `HashMap<TypeId, Box<dyn AnyStorage>>` として保持する。
pub trait AnyStorage: Any + Send + Sync {
    fn as_any    (&self)     -> &dyn Any;
    fn as_any_mut(&mut self) -> &mut dyn Any;
    /// エンティティのコンポーネントを削除する（despawn 時に全ストレージへ伝播）。
    fn remove_entity(&mut self, entity: Entity);
    /// エンティティがコンポーネントを持つか確認する。
    fn has(&self, entity: Entity) -> bool;
}

// ─── SparseSet ────────────────────────────────────────────────────────────────

/// スパースセット実装のコンポーネントストレージ。
///
/// パフォーマンス特性:
///   - insert / get / remove : O(1)
///   - 全体イテレーション    : dense 配列の連続走査（キャッシュフレンドリー）
///
/// `ABSENT = u32::MAX` を sentinel として使うことで
/// `Option<u32>` のタグを省略しメモリ効率を高める。
pub struct SparseSet<T: Component> {
    /// entity.index → dense スロット番号（ABSENT = 不在）
    sparse: Vec<u32>,
    /// (Entity, コンポーネントデータ) の連続配列
    dense:  Vec<(Entity, T)>,
}

const ABSENT: u32 = u32::MAX;

impl<T: Component> Default for SparseSet<T> {
    fn default() -> Self { Self { sparse: Vec::new(), dense: Vec::new() } }
}

impl<T: Component> SparseSet<T> {
    pub fn new() -> Self { Self::default() }

    // ─── 書き込み ─────────────────────────────────────────────

    /// コンポーネントを挿入する。既存エントリがある場合は上書きする。
    pub fn insert(&mut self, entity: Entity, component: T) {
        let idx = entity.index as usize;
        if idx >= self.sparse.len() {
            self.sparse.resize(idx + 1, ABSENT);
        }
        let slot = self.sparse[idx];
        if slot != ABSENT {
            // 既存スロットを上書き（世代確認なし: World.insert で管理済み）
            self.dense[slot as usize] = (entity, component);
        } else {
            let new_slot = self.dense.len() as u32;
            self.sparse[idx] = new_slot;
            self.dense.push((entity, component));
        }
    }

    /// コンポーネントを削除する。
    /// swap_remove で O(1)を保証: 末尾要素を削除スロットへ移動し sparse を更新する。
    pub fn remove(&mut self, entity: Entity) -> bool {
        let idx = entity.index as usize;
        if idx >= self.sparse.len() { return false; }
        let slot = self.sparse[idx];
        if slot == ABSENT { return false; }

        self.sparse[idx] = ABSENT;

        let last_idx = self.dense.len() - 1;
        if (slot as usize) != last_idx {
            // 末尾を削除スロットに移動し、末尾エントリの sparse を更新する
            self.dense.swap(slot as usize, last_idx);
            let moved_entity_idx = self.dense[slot as usize].0.index as usize;
            self.sparse[moved_entity_idx] = slot;
        }
        self.dense.pop();
        true
    }

    // ─── 読み込み ─────────────────────────────────────────────

    /// コンポーネントへの不変参照を返す。世代が一致しない場合は None。
    pub fn get(&self, entity: Entity) -> Option<&T> {
        let slot = *self.sparse.get(entity.index as usize).unwrap_or(&ABSENT);
        if slot == ABSENT { return None; }
        let (e, c) = &self.dense[slot as usize];
        if e.generation == entity.generation { Some(c) } else { None }
    }

    /// コンポーネントへの可変参照を返す。
    pub fn get_mut(&mut self, entity: Entity) -> Option<&mut T> {
        let slot = *self.sparse.get(entity.index as usize).unwrap_or(&ABSENT);
        if slot == ABSENT { return None; }
        let (e, c) = &mut self.dense[slot as usize];
        if e.generation == entity.generation { Some(c) } else { None }
    }

    /// エンティティがコンポーネントを持つか確認する（世代チェック込み）。
    pub fn contains(&self, entity: Entity) -> bool {
        let slot = *self.sparse.get(entity.index as usize).unwrap_or(&ABSENT);
        if slot == ABSENT { return false; }
        self.dense[slot as usize].0.generation == entity.generation
    }

    // ─── イテレーション ───────────────────────────────────────

    /// 全コンポーネントを (Entity, &T) でイテレートする。
    pub fn iter(&self) -> impl Iterator<Item = (Entity, &T)> {
        self.dense.iter().map(|(e, c)| (*e, c))
    }

    /// 全コンポーネントを (Entity, &mut T) でイテレートする。
    pub fn iter_mut(&mut self) -> impl Iterator<Item = (Entity, &mut T)> {
        self.dense.iter_mut().map(|(e, c)| (*e, c))
    }

    pub fn len(&self)      -> usize { self.dense.len() }
    pub fn is_empty(&self) -> bool  { self.dense.is_empty() }
}

// ─── AnyStorage 実装 ─────────────────────────────────────────────────────────

impl<T: Component + 'static> AnyStorage for SparseSet<T> {
    fn as_any    (&self)     -> &dyn Any     { self }
    fn as_any_mut(&mut self) -> &mut dyn Any { self }
    fn remove_entity(&mut self, entity: Entity) { self.remove(entity); }
    fn has(&self, entity: Entity) -> bool { self.contains(entity) }
}
