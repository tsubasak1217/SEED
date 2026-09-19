// ============================================================
//  ShellOpenLauncher.cs — OS の関連付けでファイル・フォルダを開く唯一の入口
//
//  【役割】
//  「エディタでは開かず、OS に任せる」処理をここへ集約する。
//  対象の選別は ShellOpenCatalog（データ）、実際の起動はここ（実行）と分けてある。
//
//  【落とさないこと】
//  関連付けが無い（ERROR_NO_ASSOCIATION）・実行ファイルが壊れている・
//  ユーザーが UAC を拒否した、のいずれも Process.Start は例外で知らせてくる。
//  ダブルクリック 1 回でエディタが落ちるのは許容できないので、
//  例外はすべてここで捕まえ、成否と理由を戻り値で返す。
//  画面への出し方（トースト・ログ）は呼び出し側の責務。
//
//  【WPF 非依存】
//  System.Diagnostics だけを使う。UI へは触らない。
// ============================================================

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace SEEDEditor.Assets;

/// <summary>
/// 関連付けで開いた結果（例外を投げない代わりにこれを返す）。
/// </summary>
/// <param name="Success">起動できたか。</param>
/// <param name="ErrorMessage">失敗した理由（成功時は null）。画面にそのまま出せる日本語。</param>
public readonly record struct ShellOpenResult(bool Success, string? ErrorMessage)
{
    /// <summary>成功を表す結果を作る。</summary>
    public static ShellOpenResult Ok() => new(true, null);

    /// <summary>失敗を表す結果を作る。</summary>
    /// <param name="message">画面に出す理由。</param>
    public static ShellOpenResult Fail(string message) => new(false, message);
}

/// <summary>
/// ファイルやフォルダを OS の既定のアプリで開く。状態を持たない静的クラス。
/// </summary>
public static class ShellOpenLauncher
{
    // ── 定数 ─────────────────────────────────────────────────────

    /// <summary>
    /// 関連付けが 1 つも無いときに Windows が返すエラーコード（ERROR_NO_ASSOCIATION）。
    /// これだけは「失敗」ではなく「開くアプリが無い」と言い分けたいので区別する。
    /// </summary>
    private const int ErrorNoAssociation = 1155;

    /// <summary>関連付けが無いときの文面（{0}=ファイル名）。</summary>
    private const string NoAssociationFormat =
        "{0} を開けるアプリが Windows に登録されていません。"
      + "対応アプリをインストールするか、エクスプローラで「プログラムから開く」を一度設定してください。";

    /// <summary>ファイルが存在しないときの文面（{0}=パス）。</summary>
    private const string MissingFileFormat = "ファイルが見つかりません: {0}";

    /// <summary>その他の失敗の文面（{0}=ファイル名 {1}=例外メッセージ）。</summary>
    private const string GenericFailureFormat = "{0} を開けませんでした — {1}";

    // ── 本体 ─────────────────────────────────────────────────────

    /// <summary>
    /// ファイル（またはフォルダ）を OS の既定のアプリで開く。
    ///
    /// <para>
    /// <c>UseShellExecute = true</c> にすることで、拡張子の関連付け（.blend → Blender、
    /// .png → フォトビューア など）を Windows に解決させる。
    /// エディタ側で「どのアプリか」を知る必要はなく、利用者が普段使っているアプリが開く。
    /// </para>
    /// </summary>
    /// <param name="path">開く対象の絶対パス。</param>
    /// <returns>成否と、失敗したときの理由。例外は投げない。</returns>
    public static ShellOpenResult Open(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ShellOpenResult.Fail(string.Format(MissingFileFormat, path ?? "(未指定)"));

        // 存在確認を先に行う。無いファイルを Process.Start へ渡すと
        // 「関連付けが無い」と区別できない例外になり、理由を取り違えるため。
        if (!File.Exists(path) && !Directory.Exists(path))
            return ShellOpenResult.Fail(string.Format(MissingFileFormat, path));

        var displayName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar,
                                                        Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(displayName)) displayName = path;

        try
        {
            var info = new ProcessStartInfo
            {
                FileName        = path,
                UseShellExecute = true,   // 関連付けを Windows に解決させる
            };
            Process.Start(info);
            return ShellOpenResult.Ok();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorNoAssociation)
        {
            return ShellOpenResult.Fail(string.Format(NoAssociationFormat, displayName));
        }
        catch (Exception ex)
        {
            return ShellOpenResult.Fail(string.Format(GenericFailureFormat, displayName, ex.Message));
        }
    }
}
