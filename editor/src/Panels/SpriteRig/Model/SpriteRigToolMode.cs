namespace SEEDEditor.Panels.SpriteRig.Model;

/// <summary>
/// スプライトリグパネルの編集モード（大分類）。
///
/// モードごとにキャンバスの左クリックの意味と、左パネルに出る道具立てが切り替わる。
/// メッシュ（Phase B1a）・ボーン／ウェイト（Phase B1b）のすべてが編集可能。
/// </summary>
public enum SpriteRigEditMode
{
    /// <summary>メッシュ編集（輪郭・頂点・三角形）。</summary>
    Mesh,

    /// <summary>ボーン編集（作成・選択／移動・親子付け）。</summary>
    Bone,

    /// <summary>ウェイトペイント（自動割り当て・ブラシ・数値編集）。</summary>
    Weight,
}

/// <summary>
/// ボーン編集モードにおけるツール（小分類）。
/// </summary>
public enum SpriteRigBoneTool
{
    /// <summary>
    /// 選択 / 移動。関節（根元・先端）をクリックで選択、ドラッグで移動する。
    /// </summary>
    Select,

    /// <summary>
    /// ボーン作成。押した位置が根元、離した位置が先端になる。
    /// 作成直後は<b>その先端が次のボーンの根元候補</b>になり、続けて骨を生やせる（Esc で連鎖終了）。
    /// </summary>
    Create,
}

/// <summary>
/// メッシュ編集モードにおけるツール（小分類）。
/// キャンバスの左クリックの意味がこれで決まる。
/// </summary>
public enum SpriteRigMeshTool
{
    /// <summary>選択のみ（クリックで頂点を選ぶ。形は変えない）。</summary>
    Select,

    /// <summary>ポリゴン描画（クリックで頂点を足し、始点クリックまたは Enter で閉じる）。</summary>
    DrawPolygon,

    /// <summary>頂点追加（輪郭辺の上なら分割、領域内なら内部点として追加）。</summary>
    AddVertex,

    /// <summary>頂点移動（ドラッグで動かす）。</summary>
    MoveVertex,

    /// <summary>頂点削除（クリックで消す）。</summary>
    DeleteVertex,
}
