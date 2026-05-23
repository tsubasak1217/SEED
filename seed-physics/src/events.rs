// ============================================================
//  events.rs — 衝突・トリガーイベント型定義
//
//  物理スレッドが生成し、mpsc チャンネルでメインスレッドへ送信する。
//  メインスレッドは C# スクリプトの OnCollision*/OnTrigger* に変換する。
// ============================================================

// ─── ContactInfo ─────────────────────────────────────────────────────────────

/// 1 接触点の詳細情報。1 回の衝突で最大 4 点まで生成される。
#[derive(Debug, Clone)]
pub struct ContactInfo {
    /// ワールド空間の接触点座標（オブジェクト A 上の点）
    pub point:  [f32; 3],
    /// A から B 方向を向く接触法線（正規化済み）
    pub normal: [f32; 3],
    /// 貫通深度（正の値）
    pub depth:  f32,
}

// ─── CollisionEvent ──────────────────────────────────────────────────────────

/// 衝突フェーズ。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum CollisionPhase {
    /// このステップで初めて接触した
    Enter,
    /// 前ステップも接触していた
    Stay,
    /// このステップで離れた
    Exit,
}

/// 衝突イベント。
///
/// is_trigger=false のコライダー同士が接触したときに発火する。
/// C# の OnCollisionEnter / OnCollisionStay / OnCollisionExit に対応。
#[derive(Debug, Clone)]
pub struct CollisionEvent {
    /// 衝突オブジェクト A の entity_id
    pub entity_a: u64,
    /// 衝突オブジェクト B の entity_id
    pub entity_b: u64,
    /// 接触点リスト
    pub contacts: Vec<ContactInfo>,
    /// Enter / Stay / Exit
    pub phase:    CollisionPhase,
}

// ─── TriggerEvent ────────────────────────────────────────────────────────────

/// トリガーフェーズ（Stay はトリガーでは不要なため省略）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum TriggerPhase {
    Enter,
    Exit,
}

/// トリガーイベント。
///
/// is_trigger=true のコライダーに他のコライダーが侵入・離脱した際に発火する。
/// 衝突応答（速度変化）は発生しない。
/// C# の OnTriggerEnter / OnTriggerExit に対応。
#[derive(Debug, Clone)]
pub struct TriggerEvent {
    /// トリガーコライダーの entity_id
    pub trigger_entity: u64,
    /// 侵入・離脱したオブジェクトの entity_id
    pub other_entity:   u64,
    pub phase:          TriggerPhase,
}
