// ============================================================
//  EngineVersionGate.cs — engine_version 不一致の通知・更新（プロジェクトを開く直前のゲート）
//
//  【役割】
//  EngineVersionCheck が出した判定（Same / ProjectOlder / ProjectNewer / Unknown）に応じて、
//  実際に利用者へ知らせる・.seedproj を更新して保存し直す・ログへ残す、という
//  「通知」側の責務をここへ閉じ込める。判定ロジック（EngineVersionCheck）とは
//  ファイルを分けることで、判定は WPF 非依存のまま単体テストできるようにしてある。
//
//  【呼び出し箇所】
//  プロジェクトを開く経路は 2 つあり、どちらも ProjectContext.OpenFromFile の直前で
//  CheckBeforeOpen を呼ぶ:
//    ・editor/src/App.xaml.cs                  … OpenProjectAndShowEditor（起動時・ヘッドレス含む）
//    ・editor/src/Startup/StartWindow.xaml.cs  … OpenProject（スタート画面からの手動オープン）
//
//  【ダイアログとヘッドレス】
//  ヘッドレス起動（AI エージェント運用など）では「常にこのまま開き、engine_version は
//  更新しない」と決めている。EditorDialogs のヘッドレス既定値（ボタン構成ごとに固定）に
//  頼ると、ダイアログの Yes/No の意味をその既定値に合わせてねじる必要が出るため、
//  このクラスで IsHeadless を先に判定してダイアログ自体を出さない。
//  対話時のダイアログは SEEDEditor.Headless.EditorDialogs.Show 経由（表示の一元化）で、
//  Yes/No はどちらも「はい = 前向きな操作（更新する／開く）」の自然な割り当てにする。
// ============================================================

using System;
using System.IO;
using System.Windows;

namespace SEEDEditor.Project;

/// <summary>
/// engine_version の不一致を検知し、必要ならダイアログで利用者に確認してから
/// プロジェクトを開いてよいかどうかを決める。WPF（ダイアログ）に依存するため
/// editor/tests/ProjectSystemTests へはリンクしない（判定ロジック本体は
/// WPF 非依存の <see cref="EngineVersionCheck"/> 側でテストする）。
/// </summary>
internal static class EngineVersionGate
{
    // ── ログ・文言の定数（マジック文字列にしない） ──────────────

    /// <summary>ログ行の接頭辞（grep しやすくするため）。</summary>
    private const string LOG_PREFIX = "[エンジン版チェック]";

    /// <summary>ログでバージョンが空だったことを示す表示。</summary>
    private const string EMPTY_VERSION_LOG_PLACEHOLDER = "(空)";

    /// <summary>ProjectOlder のダイアログタイトル。</summary>
    private const string CAPTION_OLDER = "エンジンバージョンの確認";

    /// <summary>ProjectNewer のダイアログタイトル。</summary>
    private const string CAPTION_NEWER = "エンジンバージョンの警告";

    /// <summary>
    /// 利用者が ProjectNewer のダイアログで「開かない」を選んだときに、
    /// 呼び出し側（スタート画面）が表示するステータス文言。
    /// 2 か所の呼び出し元で文言を重複させないために公開する。
    /// </summary>
    public const string DeclinedStatusMessage =
        "新しいエンジンで作られたプロジェクトのため、開くのをやめました。";

    /// <summary>
    /// プロジェクトを開く直前に engine_version を確認し、必要なら通知・更新を行う。
    /// </summary>
    /// <param name="projectFilePath">開こうとしている .seedproj の絶対パス。</param>
    /// <returns>
    /// true ならこのまま開いてよい（Same / Unknown / 利用者が続行を選んだ場合を含む）。
    /// false は利用者が明示的に「開かない」を選んだ場合のみ
    /// （ProjectNewer の警告ダイアログでのみ起こり得る。ヘッドレスではダイアログを出さず常に true）。
    /// </returns>
    public static bool CheckBeforeOpen(string projectFilePath)
    {
        SeedProjectFile file;
        try
        {
            file = SeedProjectFile.Load(projectFilePath);
        }
        catch
        {
            // ここでの読み込み失敗は無視して「続行してよい」を返す。
            // この直後に呼び出し側が行う本来の読み込み（ProjectContext.OpenFromFile）で
            // 同じ例外が発生し、そちらの既存のエラー表示（スタート画面 / ヘッドレス終了）に
            // 処理を譲る。ここで二重にエラーを扱わない。
            return true;
        }

        var result = EngineVersionCheck.Compare(file.EngineVersion, EditorVersion.Current);

        // 判定結果は Same を含めて必ず 1 行ログへ残す。
        EditorLog.Write(
            $"{LOG_PREFIX} 判定={result.Comparison} "
          + $"プロジェクト={DisplayForLog(result.ProjectVersionRaw)} "
          + $"エディタ={DisplayForLog(result.EditorVersionRaw)} "
          + $"({Path.GetFileName(projectFilePath)})");

        // 一致・判定不能はそのまま開く。
        if (result.Comparison is EngineVersionComparison.Same or EngineVersionComparison.Unknown)
        {
            return true;
        }

        // ヘッドレス起動では利用者に聞けないので、ダイアログを出さず「このまま開く」で続行し、
        // .seedproj は書き換えない（自動運用が黙ってプロジェクトのメタデータを変えないため）。
        if (SEEDEditor.Headless.EditorStartupOptions.IsHeadless)
        {
            EditorLog.Write($"{LOG_PREFIX} ヘッドレス起動のため確認せずにこのまま開きます（engine_version は更新しません）");
            return true;
        }

        return result.Comparison switch
        {
            EngineVersionComparison.ProjectOlder => HandleProjectOlder(file, projectFilePath, result),
            EngineVersionComparison.ProjectNewer => HandleProjectNewer(result),
            _ => true,
        };
    }

    /// <summary>
    /// プロジェクトの方が古い版で作られていた場合の通知。
    /// 「更新して開く」を選ぶと .seedproj の engine_version をこのエディタの版へ書き換えて保存する。
    /// </summary>
    /// <param name="file">読み込み済みの .seedproj 内容（更新して保存し直す対象）。</param>
    /// <param name="projectFilePath">.seedproj の絶対パス。</param>
    /// <param name="result">比較結果（文言組み立てに使う）。</param>
    /// <returns>常に true（どちらを選んでも開く）。</returns>
    private static bool HandleProjectOlder(SeedProjectFile file, string projectFilePath, EngineVersionCheckResult result)
    {
        var message =
            $"プロジェクトはエンジン {result.ProjectVersionRaw} で作られ、このエディタは {result.EditorVersionRaw} です。\n\n"
          + $"「はい」　— engine_version を {result.EditorVersionRaw} に更新して開く\n"
          +  "「いいえ」— 更新せずこのまま開く";

        // Yes=更新して開く / No=そのまま開く（ヘッドレス時はここへ来ない。CheckBeforeOpen 参照）。
        var choice = SEEDEditor.Headless.EditorDialogs.Show(
            message, CAPTION_OLDER, MessageBoxButton.YesNo, MessageBoxImage.Information);

        if (choice == MessageBoxResult.Yes)
        {
            try
            {
                file.EngineVersion = result.EditorVersionRaw;
                file.Save(projectFilePath);
                EditorLog.Write($"{LOG_PREFIX} engine_version を \"{result.EditorVersionRaw}\" に更新して保存しました: {projectFilePath}");
            }
            catch (Exception ex)
            {
                // 更新保存に失敗しても「開く」こと自体は続行する
                // （メタデータの書き戻しに失敗しただけで、プロジェクトを開けなくする理由にはならない）。
                EditorLog.Write($"{LOG_PREFIX} engine_version の更新保存に失敗しました: {ex.Message}");
            }
        }

        return true;
    }

    /// <summary>
    /// プロジェクトの方が新しい版で作られていた場合の通知。
    /// 正しく読み込めない可能性があることを強めに警告し、開くかどうかを選ばせる。
    /// </summary>
    /// <param name="result">比較結果（文言組み立てに使う）。</param>
    /// <returns>true ならこのまま開く。false なら利用者が「開かない」を選んだ。</returns>
    private static bool HandleProjectNewer(EngineVersionCheckResult result)
    {
        var message =
            $"プロジェクトはエンジン {result.ProjectVersionRaw} で作られ、このエディタは {result.EditorVersionRaw} です。\n"
          +  "新しいエンジンで作られたプロジェクトです。正しく読み込めない可能性があります。\n\n"
          +  "「はい」　— このまま開く（正しく読めない可能性を承知のうえで続行）\n"
          +  "「いいえ」— 開かない（スタート画面へ戻る）";

        // Yes=このまま開く / No=開かない（ヘッドレス時はここへ来ない。CheckBeforeOpen 参照）。
        var choice = SEEDEditor.Headless.EditorDialogs.Show(
            message, CAPTION_NEWER, MessageBoxButton.YesNo, MessageBoxImage.Warning);

        var openAnyway = choice == MessageBoxResult.Yes;

        // プロジェクトの方が新しい版＝データが壊れるリスクがある選択なので、
        // どちらを選んだかを明示的に記録する（あとから「なぜ開けた／開けなかったか」を
        // 調べられるようにするため。判定結果のログだけでは利用者の選択までは分からない）。
        EditorLog.Write(openAnyway
            ? $"{LOG_PREFIX} 警告を確認のうえ、このまま開く選択をしました。"
            : $"{LOG_PREFIX} 警告を確認のうえ、開かない選択をしました。");

        return openAnyway;
    }

    /// <summary>ログ表示用に、空文字を分かりやすいプレースホルダへ置き換える。</summary>
    /// <param name="raw">生のバージョン文字列。</param>
    private static string DisplayForLog(string raw) =>
        string.IsNullOrEmpty(raw) ? EMPTY_VERSION_LOG_PLACEHOLDER : raw;
}
