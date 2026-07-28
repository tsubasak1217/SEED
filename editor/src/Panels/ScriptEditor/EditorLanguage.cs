using System;
using System.IO;

namespace SEEDEditor.Panels.ScriptEditor;

/// <summary>
/// スクリプトエディタで開けるファイルの種別（言語）。
///
/// 種別ごとに「補完・診断・デバッグ（ブレークポイント）・保存後処理」の有無が変わるため、
/// タブ生成時に確定させ、以降の機能分岐はすべてこの値を見て行う。
/// </summary>
public enum EditorLanguage
{
    /// <summary>C# ユーザースクリプト（.cs）。IntelliSense・診断・デバッグの全機能が有効。</summary>
    CSharp,

    /// <summary>WGSL シェーディングアセット（.wgsl）。編集と構文着色のみ（デバッグ機能なし）。</summary>
    Wgsl,
}

/// <summary>
/// <see cref="EditorLanguage"/> に関するユーティリティ（拡張子判定など）。
///
/// 対応拡張子はここに一元化する。新しい種別を足すときはこのクラスだけを更新すれば、
/// 「開く動線（ProjectPanel）」と「タブ生成（ScriptEditorPanel）」の判定が同時に揃う。
/// </summary>
public static class EditorLanguages
{
    /// <summary>C# ユーザースクリプトの拡張子。</summary>
    public const string CSharpExtension = ".cs";

    /// <summary>WGSL シェーディングアセットの拡張子。</summary>
    public const string WgslExtension = ".wgsl";

    /// <summary>
    /// ファイルパスの拡張子から言語種別を判定する。
    /// 未知の拡張子は C# として扱う（従来どおりの挙動を維持するための既定値）。
    /// </summary>
    public static EditorLanguage FromPath(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return ext.Equals(WgslExtension, StringComparison.OrdinalIgnoreCase)
            ? EditorLanguage.Wgsl
            : EditorLanguage.CSharp;
    }

    /// <summary>
    /// スクリプトエディタで開ける拡張子か（ProjectPanel のダブルクリック判定用）。
    /// </summary>
    public static bool IsEditableExtension(string extension)
        => extension.Equals(CSharpExtension, StringComparison.OrdinalIgnoreCase)
        || extension.Equals(WgslExtension,   StringComparison.OrdinalIgnoreCase);
}
