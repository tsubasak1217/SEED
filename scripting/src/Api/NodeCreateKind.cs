namespace SEED;

/// <summary>
/// 空のアクタを作るときの 2D / 3D の決め方（<see cref="GameObject.Create(string, GameObject)"/> 系の内部用）。
/// 値は Rust 側 runtime/src/engine/core/scripting/host_api/nodes.rs の NODE_CREATE_* と一致させること。
/// </summary>
internal enum NodeCreateKind
{
    /// <summary>親から推定する（2D の親・Canvas を持つ 3D の親の下は 2D、それ以外と親なしは 3D）。</summary>
    Auto = 0,

    /// <summary>3D（Transform）。</summary>
    ThreeD = 1,

    /// <summary>2D（CanvasTransform。親相対・pivot 0・anchor 0・大きさ 0）。</summary>
    TwoD = 2,
}
