// ============================================================
//  entity.rs — エンティティ型とレジストリ
//
//  Entity は (index, generation) の組み合わせで一意に識別される。
//  データは一切持たず、コンポーネントの検索キーとして機能する。
//  world_line やヒエラルキー情報は Actor 構造体が保持する。
// ============================================================

// ─── Entity ───────────────────────────────────────────────────────────────────

/// ECS エンティティ識別子。
///
/// index:      SparseSet へのインデックス。
/// generation: 同一インデックスの再利用を区別する世代カウンタ。
/// Copy であることが重要 — 引数として渡した後も元の変数が使える。
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug, Default)]
pub struct Entity {
    pub(crate) index:      u32,
    pub(crate) generation: u32,
}

impl Entity {
    /// インデックスを返す（SparseSet へのキー）。
    #[inline] pub fn index(self)      -> u32 { self.index }
    /// 世代を返す（再利用検出用）。
    #[inline] pub fn generation(self) -> u32 { self.generation }

    /// (index, generation) から Entity を復元する（FFI 境界からの再構築用）。
    ///
    /// C# スクリプト側が保持する index/generation から Entity を作り直すために使う。
    /// 生死・世代の整合は World 側（SparseSet::get の世代チェック）で担保されるため、
    /// 無効・再利用済みのエンティティを渡してもコンポーネント取得が None になるだけ。
    #[inline]
    pub(crate) fn from_raw(index: u32, generation: u32) -> Self {
        Self { index, generation }
    }
}

impl std::fmt::Display for Entity {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "Entity({}:{})", self.index, self.generation)
    }
}

// ─── EntityMeta ───────────────────────────────────────────────────────────────

/// Entity スロットの生死・世代を内部管理する。
#[derive(Clone)]
struct EntityMeta {
    generation: u32,
    alive:      bool,
}

// ─── Entities ─────────────────────────────────────────────────────────────────

/// 全 Entity の生死・世代を管理するレジストリ。
/// World が所有し、spawn / despawn のみがここを経由する。
pub struct Entities {
    meta:     Vec<EntityMeta>,
    free_ids: Vec<u32>,
}

impl Entities {
    pub fn new() -> Self {
        Self { meta: Vec::new(), free_ids: Vec::new() }
    }

    /// 新しい Entity を生成して返す。
    /// 可能な限り古い ID スロットを再利用し、世代をインクリメントして区別する。
    pub fn spawn(&mut self) -> Entity {
        if let Some(idx) = self.free_ids.pop() {
            self.meta[idx as usize].alive = true;
            Entity { index: idx, generation: self.meta[idx as usize].generation }
        } else {
            let idx = self.meta.len() as u32;
            self.meta.push(EntityMeta { generation: 0, alive: true });
            Entity { index: idx, generation: 0 }
        }
    }

    /// Entity を死亡状態にして ID スロットを解放する。
    /// 世代をインクリメントすることで、古いコピーが誤って使われるのを防ぐ。
    /// Entity が有効でなければ false を返す。
    pub fn despawn(&mut self, entity: Entity) -> bool {
        let Some(meta) = self.meta.get_mut(entity.index as usize) else { return false };
        if !meta.alive || meta.generation != entity.generation { return false; }
        meta.alive      = false;
        meta.generation = meta.generation.wrapping_add(1);
        self.free_ids.push(entity.index);
        true
    }

    /// Entity が現在有効（alive かつ世代一致）かどうかを返す。
    pub fn is_alive(&self, entity: Entity) -> bool {
        self.meta.get(entity.index as usize)
            .map(|m| m.alive && m.generation == entity.generation)
            .unwrap_or(false)
    }

    /// 現在 alive な全 Entity を列挙する（スナップショット）。
    pub fn alive_entities(&self) -> impl Iterator<Item = Entity> + '_ {
        self.meta.iter().enumerate()
            .filter(|(_, m)| m.alive)
            .map(|(i, m)| Entity { index: i as u32, generation: m.generation })
    }

    /// 生きているエンティティ数を返す。
    pub fn len(&self) -> usize { self.meta.iter().filter(|m| m.alive).count() }
}

impl Default for Entities {
    fn default() -> Self { Self::new() }
}
