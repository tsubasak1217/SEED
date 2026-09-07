namespace SEED;

/// <summary>
/// 名前付きイベント（<see cref="Events"/>）が運ぶ引数の種別。
///
/// イベントは「引数 0 個、または 1 個」だけを扱う。1 個の場合に許す型が本列挙で、
/// インスペクタ結線用の <see cref="ScriptEventArgKind"/> と意図的に揃えている
/// （ScriptEvent と Events で「渡せる引数の種類」がズレると利用者が混乱するため）。
///
/// 【重要】発火側と購読側の種別は完全一致でなければハンドラは呼ばれない
/// （float で購読しているところへ string で Raise しても呼ばない。暗黙変換はしない）。
/// 文字列 "3" が float 3 として届くような曖昧な伝播を防ぐための仕様。
///
/// 【拡張時の注意】
/// 値を足すときは <see cref="Events"/> の Raise / Subscribe のオーバーロード、
/// <see cref="EventBus"/> の呼び出し switch、docs の Events 節を同時に更新すること。
/// 種別と C# 型は 1 対 1 に対応させる（多対 1 にすると呼び出し時の型が一意に決まらない）。
/// </summary>
internal enum EventArgKind
{
    /// <summary>引数なし（<c>Action</c> で購読・<c>Raise(name)</c> で発火）。</summary>
    None = 0,

    /// <summary><c>string</c> 引数（<c>Action&lt;string&gt;</c>）。</summary>
    String,

    /// <summary><c>float</c> 引数（<c>Action&lt;float&gt;</c>）。</summary>
    Float,

    /// <summary><see cref="SEED.GameObject"/> 引数（<c>Action&lt;GameObject&gt;</c>）。</summary>
    GameObject,
}
