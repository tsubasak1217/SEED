namespace SEEDEditor.Panels.SpriteRig.Model;

/// <summary>
/// スプライトリグパネルの編集モード（大分類）。
///
/// Phase B1a では <see cref="Mesh"/> のみ UI を持つ。
/// <see cref="Bone"/> / <see cref="Weight"/> は Phase B1b（ボーン配置・ウェイトペイント）で
/// 実装するための枠だけを先に用意してあり、選択しても編集操作は受け付けない。
/// </summary>
public enum SpriteRigEditMode
{
    /// <summary>メッシュ編集（輪郭・頂点・三角形）。</summary>
    Mesh,

    /// <summary>ボーン編集（B1b で実装）。</summary>
    Bone,

    /// <summary>ウェイトペイント（B1b で実装）。</summary>
    Weight,
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
