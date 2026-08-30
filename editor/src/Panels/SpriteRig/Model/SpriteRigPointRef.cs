namespace SEEDEditor.Panels.SpriteRig.Model;

/// <summary>
/// 編集対象となる 1 点への参照（輪郭ポリゴンの頂点か、内部点か）。
///
/// 編集モデルではユーザーが触れる点が「輪郭ポリゴンの頂点」と「内部点」の 2 種類しか無いので、
/// この 1 つの型でどちらも一意に指せる。三角形の頂点配列は派生データなので参照しない。
/// </summary>
/// <param name="PolygonIndex">
/// 輪郭ポリゴンの添字。<see cref="InteriorPolygonIndex"/>（-1）なら内部点を指す。
/// </param>
/// <param name="PointIndex">
/// ポリゴン内の頂点添字、または内部点リストの添字。
/// </param>
public readonly record struct SpriteRigPointRef(int PolygonIndex, int PointIndex)
{
    /// <summary>内部点を指すときに <see cref="PolygonIndex"/> へ入れる番兵値。</summary>
    public const int InteriorPolygonIndex = -1;

    /// <summary>この参照が内部点を指しているか。</summary>
    public bool IsInterior => PolygonIndex == InteriorPolygonIndex;

    /// <summary>内部点への参照を作る。</summary>
    /// <param name="pointIndex">内部点リストの添字。</param>
    public static SpriteRigPointRef Interior(int pointIndex) => new(InteriorPolygonIndex, pointIndex);
}
