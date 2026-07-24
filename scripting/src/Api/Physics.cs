namespace SEED;

/// <summary>
/// レイキャストのヒット情報。<see cref="Physics.Raycast"/> の結果として返される。
/// </summary>
public readonly struct RaycastHit
{
    /// <summary>ヒットしたアクターの GameObject（逆引きできない場合は IsValid=false）。</summary>
    public readonly GameObject GameObject;
    /// <summary>ヒット点のワールド座標。</summary>
    public readonly Vector3 Point;
    /// <summary>ヒット点の法線ベクトル。</summary>
    public readonly Vector3 Normal;
    /// <summary>レイ始点からヒット点までの距離。</summary>
    public readonly float Distance;

    internal RaycastHit(GameObject gameObject, Vector3 point, Vector3 normal, float distance)
    {
        GameObject = gameObject;
        Point      = point;
        Normal     = normal;
        Distance   = distance;
    }
}

/// <summary>
/// <see cref="Physics.MoveActor"/> の結果。衝突解決後の実効移動量と接地状態を返す。
/// </summary>
public readonly struct MoveResult
{
    /// <summary>実際に移動した量（ワールド空間）。壁ずり・段差・スロープ解決後の補正済み移動量。</summary>
    public readonly Vector3 Motion;
    /// <summary>移動適用後に接地しているか。</summary>
    public readonly bool Grounded;

    internal MoveResult(Vector3 motion, bool grounded)
    {
        Motion   = motion;
        Grounded = grounded;
    }
}

/// <summary>
/// 物理演算への問い合わせ・操作 API。
/// Raycast（問い合わせ）と MoveActor（キャラクターコントローラー移動）を提供する。
/// </summary>
public static class Physics
{
    /// <summary>
    /// アクターをキャラクターコントローラー的に移動する（Unity の CharacterController.Move 相当）。
    ///
    /// 希望移動量 <paramref name="motion"/>（ワールド空間）を地形・静的コライダーとの衝突で
    /// 解決し、補正済みの移動量ぶんだけアクターを動かす。壁に沿って滑る・段差の乗り越え・
    /// スロープ登坂は内部のキネマティックキャラクターコントローラーが処理する。
    ///
    /// 重力はエンジンが自動適用しない。落下させたい場合は <paramref name="motion"/> に
    /// 重力ぶん（例: 下向きの速度 × Time.DeltaTime）を含めること。
    ///
    /// 対象アクターは Collider コンポーネント（カプセル推奨）を持つ必要がある。
    /// 物理スレッドへの同期問い合わせのため、毎フレーム大量に呼ぶとフレーム時間を消費する。
    /// </summary>
    /// <param name="actor">移動するアクター（Collider 必須）</param>
    /// <param name="motion">このフレームの希望移動量（ワールド空間・重力分を含める）</param>
    /// <returns>補正済み移動量と接地状態（移動できなかった場合は Motion=Zero, Grounded=false）</returns>
    public static MoveResult MoveActor(GameObject actor, Vector3 motion)
        => ScriptHost.TryMoveActor(actor.Entity, motion, out var moved, out var grounded)
            ? new MoveResult(moved, grounded)
            : new MoveResult(Vector3.Zero, false);

    /// <summary>
    /// レイキャストを実行し、最初にヒットしたコライダーの情報を返す。
    /// ヒットしなければ false（hit は既定値）。
    ///
    /// 物理スレッドへの同期問い合わせのため、毎フレーム大量に呼ぶと
    /// フレーム時間を消費する点に注意（1 回あたり数 ms 以内）。
    /// </summary>
    /// <param name="origin">レイの始点（ワールド座標）</param>
    /// <param name="direction">レイの方向（正規化推奨）</param>
    /// <param name="maxDistance">最大距離</param>
    /// <param name="hit">ヒット情報（ヒット時のみ有効）</param>
    public static bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RaycastHit hit)
        => ScriptHost.TryRaycast(origin, direction, maxDistance, out hit);

    /// <summary>ヒット情報が不要な場合の簡易版レイキャスト。</summary>
    public static bool Raycast(Vector3 origin, Vector3 direction, float maxDistance)
        => ScriptHost.TryRaycast(origin, direction, maxDistance, out _);
}
