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
/// 物理演算への問い合わせ・操作 API。
/// Raycast（レイキャスト）と IsGrounded（キャラクターの接地判定）を提供する。
/// </summary>
public static class Physics
{
    /// <summary>
    /// キャラクターコントローラー（Collider の「キャラクターコントローラー」を有効にしたアクター）が
    /// 接地しているかを返す。
    ///
    /// 接地判定は物理ステップ同期（60Hz）で自動更新される。キャラクターは Transform.Position を
    /// 書き換えるだけで地形に衝突解決されて押し戻され、その際の接地状態がここに反映される。
    /// </summary>
    /// <param name="actor">キャラクターコントローラーを有効にしたアクター</param>
    /// <returns>接地していれば true（未設定・非接地は false）</returns>
    public static bool IsGrounded(GameObject actor)
        => ScriptHost.IsGrounded(actor.Entity);

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
