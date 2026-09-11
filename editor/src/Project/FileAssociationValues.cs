// ============================================================
//  FileAssociationValues.cs — .seedproj 関連付けでレジストリへ書く値の組み立て
//
//  【なぜレジストリ操作と分けるのか】
//  「どのキーへ何を書くか」は間違えると利用者の環境を壊す一方、
//  レジストリ API を呼ぶ部分は単体テストから触れない（テストが環境を汚す）。
//  そこで **値の組み立てだけを純関数** として切り出し、ここをテストで固定する。
//  実際の書き込み・削除・判定は FileAssociation.cs が担当する。
//
//  WPF にもレジストリ API にも依存しない（単体テストからそのままリンクできる）。
// ============================================================

using System;

namespace SEEDEditor.Project;

/// <summary>
/// .seedproj をこのエディタに関連付けるためにレジストリへ書く値一式（不変）。
///
/// <para>書き込み先はすべて <c>HKEY_CURRENT_USER</c> 配下なので管理者権限は要らない。</para>
/// <list type="bullet">
///   <item><c>Software\Classes\.seedproj</c>（既定値）= ProgID</item>
///   <item><c>Software\Classes\SEED.Project</c>（既定値）= 種類の説明</item>
///   <item><c>Software\Classes\SEED.Project\DefaultIcon</c>（既定値）= "exe",0</item>
///   <item><c>Software\Classes\SEED.Project\shell\open\command</c>（既定値）= "exe" "%1"</item>
/// </list>
/// </summary>
/// <param name="Extension">関連付ける拡張子（ドット付き）。</param>
/// <param name="ProgId">ProgID（ファイル種別の識別子）。</param>
/// <param name="ExtensionKeyPath">拡張子キーのパス（HKCU 相対）。</param>
/// <param name="ProgIdKeyPath">ProgID キーのパス（HKCU 相対）。</param>
/// <param name="Description">エクスプローラーの「種類」欄に出る説明。</param>
/// <param name="DefaultIconKeyPath">DefaultIcon キーのパス（HKCU 相対）。</param>
/// <param name="DefaultIconValue">DefaultIcon の既定値。</param>
/// <param name="OpenCommandKeyPath">shell\open\command キーのパス（HKCU 相対）。</param>
/// <param name="OpenCommandValue">shell\open\command の既定値。</param>
public sealed record FileAssociationValues(
    string Extension,
    string ProgId,
    string ExtensionKeyPath,
    string ProgIdKeyPath,
    string Description,
    string DefaultIconKeyPath,
    string DefaultIconValue,
    string OpenCommandKeyPath,
    string OpenCommandValue)
{
    // ── 定数（レジストリの構造）────────────────────────────

    /// <summary>ファイル種別を登録する ProgID。</summary>
    public const string PROG_ID = "SEED.Project";

    /// <summary>エクスプローラーの「種類」欄に出る説明。</summary>
    public const string DESCRIPTION = "SEED プロジェクト";

    /// <summary>HKCU 配下のクラス登録ルート。</summary>
    public const string CLASSES_ROOT = @"Software\Classes";

    /// <summary>ProgID 配下の「開く」コマンドのサブキー。</summary>
    public const string OPEN_COMMAND_SUBKEY = @"shell\open\command";

    /// <summary>ProgID 配下のアイコン指定サブキー。</summary>
    public const string DEFAULT_ICON_SUBKEY = "DefaultIcon";

    /// <summary>exe 内のアイコンインデックス（0 = 既定のアプリケーションアイコン）。</summary>
    private const int ICON_INDEX = 0;

    /// <summary>シェルが渡すファイルパスのプレースホルダ。</summary>
    private const string SHELL_PATH_PLACEHOLDER = "%1";

    // ── 組み立て ────────────────────────────────────────────

    /// <summary>
    /// エディタ exe のパスから、レジストリへ書く値一式を組み立てる（純関数）。
    /// </summary>
    /// <param name="editorExePath">SEEDEditor.exe の絶対パス。</param>
    /// <returns>書き込むキーと値の一式。</returns>
    /// <exception cref="ArgumentException">exe パスが空のとき。</exception>
    public static FileAssociationValues Build(string editorExePath)
    {
        if (string.IsNullOrWhiteSpace(editorExePath))
            throw new ArgumentException("エディタ exe のパスが空です。", nameof(editorExePath));

        // 引用符で囲むのは、パスに空白（"Program Files" 等）が含まれても
        // シェルが 1 つの引数として解釈できるようにするため。
        var quotedExe = $"\"{editorExePath}\"";

        return new FileAssociationValues(
            Extension:          SeedProjectFile.EXTENSION,
            ProgId:             PROG_ID,
            ExtensionKeyPath:   $@"{CLASSES_ROOT}\{SeedProjectFile.EXTENSION}",
            ProgIdKeyPath:      $@"{CLASSES_ROOT}\{PROG_ID}",
            Description:        DESCRIPTION,
            DefaultIconKeyPath: $@"{CLASSES_ROOT}\{PROG_ID}\{DEFAULT_ICON_SUBKEY}",
            DefaultIconValue:   $"{quotedExe},{ICON_INDEX}",
            OpenCommandKeyPath: $@"{CLASSES_ROOT}\{PROG_ID}\{OPEN_COMMAND_SUBKEY}",
            OpenCommandValue:   $"{quotedExe} \"{SHELL_PATH_PLACEHOLDER}\"");
    }
}
