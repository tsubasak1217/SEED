// ============================================================
//  world.rs — ECS 中央ストア
//
//  World はすべてのコンポーネントデータを型別の SparseSet に格納し、
//  Entity をキーとした O(1) アクセスを提供する。
//
//  【責務】
//  - Entity の生成・破棄（Entities に委譲）
//  - コンポーネントの挿入・削除・読み取り
//  - 型別クエリ (query / query_mut / query2)
//  - Resources（グローバルデータ）の保持
//
//  【スレッド安全性】
//  World 自体は Sync でないが、System は &mut World を受け取り
//  シングルスレッドで実行されることを想定している。
// ============================================================

use std::any::{Any, TypeId};
use std::collections::HashMap;
use super::entity::{Entities, Entity};
use super::storage::{AnyStorage, Component, SparseSet};

// ─── World ────────────────────────────────────────────────────────────────────

pub struct World {
    entities:  Entities,
    /// TypeId → 型消去されたコンポーネントストレージ
    storages:  HashMap<TypeId, Box<dyn AnyStorage>>,
    /// グローバルリソース（システムが共有するシングルトンデータ）
    resources: HashMap<TypeId, Box<dyn Any + Send + Sync>>,
}

impl World {
    pub fn new() -> Self {
        Self {
            entities:  Entities::new(),
            storages:  HashMap::new(),
            resources: HashMap::new(),
        }
    }

    // ─── Entity ───────────────────────────────────────────────

    /// 新しい Entity を生成して返す。コンポーネントは別途 insert() で追加する。
    pub fn spawn(&mut self) -> Entity { self.entities.spawn() }

    /// Entity とその全コンポーネントを削除する。
    pub fn despawn(&mut self, entity: Entity) {
        if self.entities.despawn(entity) {
            for storage in self.storages.values_mut() {
                storage.remove_entity(entity);
            }
        }
    }

    /// Entity が有効かどうかを確認する。
    pub fn is_alive(&self, entity: Entity) -> bool { self.entities.is_alive(entity) }

    // ─── コンポーネント書き込み ────────────────────────────────

    /// コンポーネントを挿入する。同型のコンポーネントが既にあれば上書きする。
    /// Entity 1 つにつき各型 1 インスタンス — ECS の基本制約。
    pub fn insert<T: Component>(&mut self, entity: Entity, component: T) {
        self.storage_or_create::<T>().insert(entity, component);
    }

    /// コンポーネントを削除する。存在しなければ false を返す。
    pub fn remove<T: Component>(&mut self, entity: Entity) -> bool {
        self.storages
            .get_mut(&TypeId::of::<T>())
            .and_then(|s| s.as_any_mut().downcast_mut::<SparseSet<T>>())
            .map(|s| s.remove(entity))
            .unwrap_or(false)
    }

    // ─── コンポーネント読み取り ────────────────────────────────

    /// コンポーネントへの不変参照を返す。
    pub fn get<T: Component>(&self, entity: Entity) -> Option<&T> {
        self.storages
            .get(&TypeId::of::<T>())?
            .as_any()
            .downcast_ref::<SparseSet<T>>()?
            .get(entity)
    }

    /// コンポーネントへの可変参照を返す。
    pub fn get_mut<T: Component>(&mut self, entity: Entity) -> Option<&mut T> {
        self.storages
            .get_mut(&TypeId::of::<T>())?
            .as_any_mut()
            .downcast_mut::<SparseSet<T>>()?
            .get_mut(entity)
    }

    /// エンティティがコンポーネントを持つか確認する。
    pub fn contains<T: Component>(&self, entity: Entity) -> bool {
        self.storages
            .get(&TypeId::of::<T>())
            .and_then(|s| s.as_any().downcast_ref::<SparseSet<T>>())
            .map(|s| s.contains(entity))
            .unwrap_or(false)
    }

    // ─── クエリ ───────────────────────────────────────────────

    /// 指定型のコンポーネントを持つ全 Entity をイテレートする。
    pub fn query<T: Component>(&self) -> impl Iterator<Item = (Entity, &T)> {
        self.storages
            .get(&TypeId::of::<T>())
            .and_then(|s| s.as_any().downcast_ref::<SparseSet<T>>())
            .into_iter()
            .flat_map(|s| s.iter())
    }

    /// 指定型のコンポーネントを持つ全 Entity を可変でイテレートする。
    pub fn query_mut<T: Component>(&mut self) -> impl Iterator<Item = (Entity, &mut T)> {
        self.storages
            .get_mut(&TypeId::of::<T>())
            .and_then(|s| s.as_any_mut().downcast_mut::<SparseSet<T>>())
            .into_iter()
            .flat_map(|s| s.iter_mut())
    }

    /// A と B 両方のコンポーネントを持つ Entity を (Entity, &A, &B) でまとめて返す。
    /// 内部で小さい方のストレージを走査してフィルタリングする。
    pub fn query2<A: Component, B: Component>(&self) -> Vec<(Entity, &A, &B)> {
        let Some(sa) = self.storage_ref::<A>() else { return Vec::new() };
        let Some(sb) = self.storage_ref::<B>() else { return Vec::new() };
        sa.iter()
            .filter_map(|(e, a)| sb.get(e).map(|b| (e, a, b)))
            .collect()
    }

    // ─── Resources ────────────────────────────────────────────

    /// リソース（シングルトンデータ）を挿入する。
    pub fn insert_resource<R: Any + Send + Sync + 'static>(&mut self, resource: R) {
        self.resources.insert(TypeId::of::<R>(), Box::new(resource));
    }

    /// リソースへの不変参照を返す。
    pub fn resource<R: Any + Send + Sync + 'static>(&self) -> Option<&R> {
        self.resources.get(&TypeId::of::<R>())?
            .downcast_ref::<R>()
    }

    /// リソースへの可変参照を返す。
    pub fn resource_mut<R: Any + Send + Sync + 'static>(&mut self) -> Option<&mut R> {
        self.resources.get_mut(&TypeId::of::<R>())?
            .downcast_mut::<R>()
    }

    // ─── 内部ヘルパー ─────────────────────────────────────────

    fn storage_or_create<T: Component>(&mut self) -> &mut SparseSet<T> {
        self.storages
            .entry(TypeId::of::<T>())
            .or_insert_with(|| Box::new(SparseSet::<T>::new()))
            .as_any_mut()
            .downcast_mut::<SparseSet<T>>()
            .unwrap()
    }

    fn storage_ref<T: Component>(&self) -> Option<&SparseSet<T>> {
        self.storages.get(&TypeId::of::<T>())?
            .as_any()
            .downcast_ref::<SparseSet<T>>()
    }
}

impl Default for World {
    fn default() -> Self { Self::new() }
}
