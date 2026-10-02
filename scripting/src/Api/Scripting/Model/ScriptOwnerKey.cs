namespace SEED.Scripting;

// ============================================================
//  ScriptOwnerKey.cs — スクリプトの登録簿の「持ち主」の鍵（アクタのルートのエンティティの index と generation）
//
//  登録簿（ScriptInstanceRegistry）はエンジンの型（SEED.Entity・SEEDScript）に依存させず、単体テスト
//  （editor/tests/ScriptRegistryTests）でそのまま確かめられるようにしている。そのため持ち主は SEED.Entity ではなく
//  数の組で持ち、エンジン側の窓口（ScriptRegistry）が SEED.Entity との詰め替えを受け持つ。
// ============================================================

/// <summary>
/// スクリプトの登録簿の持ち主の鍵: スクリプトが乗るアクタのルートのエンティティ（index と generation の組）。
///
/// generation まで鍵に含めるので、破棄されたアクタの index が別のアクタに使い回されても
/// （ランタイムは同じ index の generation を進めて使い回す）古いアクタのスクリプトと混ざらない。
/// </summary>
/// <param name="Index">エンティティの index（SparseSet の位置）。</param>
/// <param name="Generation">エンティティの generation（使い回しの検出用の世代）。</param>
internal readonly record struct ScriptOwnerKey(uint Index, uint Generation);
