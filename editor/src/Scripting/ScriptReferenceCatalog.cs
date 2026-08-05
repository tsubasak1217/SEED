using SEEDEditor.Controls;

namespace SEEDEditor.Scripting;

/// <summary>
/// スクリプトの参照フィールド種別（SEED.ScriptReference の Kind）と、
/// エディタ側の表現（ACTOR_COMPONENTS の型名・表示名）との対応表 —— への入口。
///
/// 実体（対応表そのもの）は <see cref="ReferenceKindCatalog"/> に移した。
/// スクリプト参照だけでなく、水・リンク・キャンバスの参照フィールドも同じ表を引くため、
/// 表は参照ピッカーの共通基盤側（editor/src/Controls/）に置くのが正しいからである。
///
/// 新しい参照型を足すときは <see cref="ReferenceKindCatalog"/> の辞書を増やすこと。
/// このクラスはスクリプト側の呼び出し名を保つためだけの薄い転送である。
/// </summary>
internal static class ScriptReferenceCatalog
{
    /// <summary>
    /// この種別がアクター内の「スロット」に格納されるか（＝スロット選択が必要か）。
    /// false の場合はアクター名だけで参照が確定する（GameObject / Transform 系）。
    /// </summary>
    public static bool NeedsSlotSelection(string kind) => ReferenceKindCatalog.NeedsSlotSelection(kind);

    /// <summary>
    /// 種別に対応する ACTOR_COMPONENTS の "type" 文字列。スロット型でなければ null。
    /// </summary>
    public static string? SlotComponentType(string kind) => ReferenceKindCatalog.SlotComponentType(kind);

    /// <summary>種別の表示名（未登録なら種別名そのもの）。</summary>
    public static string DisplayName(string kind) => ReferenceKindCatalog.DisplayName(kind);
}
