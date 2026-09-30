// ============================================================
//  TemplateActorPickerContext.cs — テンプレートアクタの窓が外へ頼むこと
//
//  【役割】
//  窓（TemplateActorPickerWindow）は MainWindow・ヒエラルキー・ランタイムを直接知らない。
//  必要なことは MainWindow がこの入れ物に関数として詰めて渡す
//  （テンプレートのインポート画面が MainWindow を知らないのと同じ流儀）。
// ============================================================

using System;

namespace SEEDEditor.Templates.Actors;

/// <summary>
/// 追加先を引き直した結果。
/// </summary>
/// <param name="Target">いまのツリーで引き直した追加先（見失ったら null）。</param>
/// <param name="Reason">見失った理由（利用者向けの文。引き直せたら null）。</param>
public readonly record struct TemplateActorTargetRefresh(TemplateActorTarget? Target, string? Reason);

/// <summary>
/// テンプレートアクタの窓が使う外部の機能一式。
/// </summary>
public sealed class TemplateActorPickerContext
{
    /// <summary>テンプレートライブラリ（templates/）の絶対パス。</summary>
    public required string LibraryRoot { get; init; }

    /// <summary>いまのプロジェクトのアセットルート（依存ファイルのコピー先）を返す。</summary>
    public required Func<string> AssetsRoot { get; init; }

    /// <summary>
    /// 追加の直前に、追加先をいまのヒエラルキーで引き直す
    /// （DFS 番号はツリーの編集でずれるため。HierarchyPanel.TryRefreshTemplateActorTarget）。
    /// </summary>
    public required Func<TemplateActorTarget, TemplateActorTargetRefresh> RefreshTarget { get; init; }

    /// <summary>いま編集できない理由（閲覧専用の表示中など）。編集できるなら null。</summary>
    public required Func<string?> ReadOnlyReason { get; init; }

    /// <summary>ランタイムへ 1 行の命令を送る。</summary>
    public required Action<string> SendToRuntime { get; init; }

    /// <summary>依存ファイルをプロジェクトへコピーした（プロジェクトパネルを読み直す合図）。</summary>
    public required Action<TemplateActorCopyResult> FilesCopied { get; init; }

    /// <summary>エディタのログ（Output パネル）へ書く。省略可。</summary>
    public Action<string>? Log { get; init; }
}
