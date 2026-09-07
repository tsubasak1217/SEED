using System;

namespace SEEDEditor.Scripting;

/// <summary>
/// <c>string</c> フィールドを「アセットファイルへの参照」としてインスペクタに出す属性。
///
/// 行の見た目は Text コンポーネントの「フォント」行と同じ
/// **［パス表示（読み取り専用）］＋［参照］＋［×］** になり、
/// Project パネル／エクスプローラーからのドラッグ＆ドロップでも設定できる。
/// 保存される値は <c>assets://</c> 仮想パス（アセットルート外のファイルは絶対パスのまま）。
///
/// ## 使い方
/// <c>[SerializeField]</c> との併用が前提である。受け付ける拡張子を並べて指定する。
/// <code>
/// [SerializeField(Label = "フォント"), AssetReference("ttf", "otf")]
/// public string fontPath = "";
/// </code>
/// 拡張子は**ドット無し・大文字小文字どちらでも**書ける（内部で「小文字・ドット無し」へ正規化する）。
/// 1 つも指定しなかった場合はこの属性の意味が無いため、通常の 1 行テキストボックスへ戻る。
///
/// ## 効果があるのは string だけ
/// <c>string</c> 以外のフィールドに付けても無視される。
/// <c>[System.Serializable]</c> 構造体リストの <c>string</c> メンバにも付けられる。
/// </summary>
/// <example>[SerializeField, AssetReference("wav", "ogg")] public string sePath = "";</example>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public sealed class AssetReferenceAttribute : Attribute
{
    /// <summary>
    /// 受け付ける拡張子（小文字・ドット無しへ正規化済み。空要素は落とす）。
    /// 正規化の規約は <see cref="SEED.ScriptAssetReference"/> が正典。
    /// </summary>
    public string[] Extensions { get; }

    /// <summary>受け付ける拡張子を並べて指定する。</summary>
    /// <param name="extensions">拡張子（<c>"ttf"</c> / <c>".TTF"</c> のどちらの書き方でもよい）。</param>
    public AssetReferenceAttribute(params string[] extensions)
    {
        Extensions = SEED.ScriptAssetReference.Normalize(extensions);
    }
}
