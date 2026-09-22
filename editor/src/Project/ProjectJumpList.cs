// ============================================================
//  ProjectJumpList.cs — タスクバーのジャンプリスト（アイコン右クリックのメニュー）を更新する
//
//  「最近」欄に最近開いたプロジェクトの .seedproj を並べる（Visual Studio の「最近使ったもの」相当）。
//   - 項目は JumpPath（ファイルそのもの）。クリックすると .seedproj の関連付けで SEEDEditor.exe が
//     起動し、右クリックには Windows 標準の「フォルダーの場所を開く」（プロジェクトのフォルダ）が付く。
//   - アプリ自身の項目（"SEED"）は Windows が自動で出す。その右クリックは Windows 標準の
//     「開く／管理者として実行／ピン留め／プロパティ」だけで、アプリからは項目を足せない
//     （2026-09-23 に「SEED 本体のフォルダーを開く項目が欲しい」と指摘され、実機で確認）。
//     そこで「タスク」欄に「SEED のフォルダーを開く」（explorer.exe でエンジンのフォルダを開く）を置く。
//     開く先は EngineFolderLocator が決める（開発配置＝リポジトリ、配布配置＝exe のフォルダ）。
//
//  【前提】
//   JumpPath は「そのアプリがその拡張子の登録ハンドラである」ときだけ表示される（未登録なら黙って落ちる）。
//   そのため更新前に .seedproj の関連付け（HKCU、管理者権限不要）を確認し、無ければ登録する。
//
//  【仕組み】
//   WPF の JumpList を Application に設定すると、OS が
//   %APPDATA%\Microsoft\Windows\Recent\CustomDestinations に書き込み、
//   タスクバーのボタン（起動中でもピン留めでも）に反映する。項目の識別は exe のパス
//   （AppUserModelID 既定値）なので、同じ exe を指すショートカットから起動しても同じ一覧になる。
//
//  【更新タイミング】
//   プロジェクトを開いたとき／最近の一覧から外したとき／スタート画面を出したとき。
//   ヘッドレス起動（エージェント用）では OS のジャンプリストも関連付けも触らない。
// ============================================================
using System;
using System.IO;
using System.Windows;
using System.Windows.Shell;

namespace SEEDEditor.Project;

/// <summary>
/// 最近のプロジェクト一覧をタスクバーのジャンプリストへ反映する。
/// </summary>
public static class ProjectJumpList
{
    /// <summary>「SEED のフォルダーを開く」タスクの表示名。</summary>
    private const string OPEN_ENGINE_FOLDER_TITLE = "SEED のフォルダーを開く";

    /// <summary>「SEED のフォルダーを開く」タスクの説明（ホバーで出る）。</summary>
    private const string OPEN_ENGINE_FOLDER_DESCRIPTION = "SEED 本体（エンジン）のフォルダーをエクスプローラーで開きます";

    /// <summary>フォルダーを開くのに使うプログラム。</summary>
    private const string EXPLORER_EXE = "explorer.exe";

    /// <summary>
    /// タスク項目のアイコンに使う shell32.dll のフォルダーアイコンの番号。
    /// 負の値は「リソース ID」指定（正の値は並び順の指定になる）。ID 4 は標準の閉じたフォルダー。
    /// </summary>
    private const int SHELL32_FOLDER_ICON_RESOURCE = -4;

    /// <summary>アイコンを取るシステム DLL。</summary>
    private const string SHELL32_DLL = "shell32.dll";

    /// <summary>
    /// 最近の一覧を読み直してジャンプリストを作り直す。
    /// 失敗しても（OS 側の制限・権限など）エディタの動作には影響させない。
    /// </summary>
    /// <param name="store">最近のプロジェクトの保存場所。</param>
    public static void Refresh(RecentProjectsStore store)
    {
        if (Headless.EditorStartupOptions.IsHeadless) return;

        try
        {
            var exePath = FileAssociation.CurrentExePath;
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                EditorLog.Write("ジャンプリスト: 実行ファイルのパスが取れないため更新しません");
                return;
            }

            // JumpPath はこの exe が .seedproj の登録ハンドラでないと表示されない。
            // 未登録（別の場所の exe を指している場合を含む）なら HKCU に登録する。
            EnsureAssociation(exePath);

            var specs = ProjectJumpListBuilder.Build(store.Load(), File.Exists);

            var jumpList = new JumpList
            {
                // OS 管理の「最近使ったもの」「よく使うもの」は使わない（自前の「最近」欄だけ出す）。
                ShowRecentCategory   = false,
                ShowFrequentCategory = false,
            };

            foreach (var spec in specs)
            {
                jumpList.JumpItems.Add(new JumpPath
                {
                    Path           = spec.ProjectFilePath,
                    CustomCategory = ProjectJumpListBuilder.RECENT_CATEGORY,
                });
            }

            // 「タスク」欄: SEED 本体のフォルダーをエクスプローラーで開く。
            // Windows が出す "SEED" の項目の右クリックには足せないので、ここに置く。
            var engineFolder = EngineFolderLocator.Resolve(
                Path.GetDirectoryName(exePath), Directory.Exists);
            if (engineFolder.Length > 0)
            {
                jumpList.JumpItems.Add(new JumpTask
                {
                    Title             = OPEN_ENGINE_FOLDER_TITLE,
                    Description       = OPEN_ENGINE_FOLDER_DESCRIPTION,
                    ApplicationPath   = EXPLORER_EXE,
                    // パスに空白があっても 1 引数として渡るように引用符で囲む。
                    Arguments         = $"\"{engineFolder}\"",
                    IconResourcePath  = SHELL32_DLL,
                    IconResourceIndex = SHELL32_FOLDER_ICON_RESOURCE,
                    WorkingDirectory  = engineFolder,
                });
            }

            JumpList.SetJumpList(Application.Current, jumpList);
            EditorLog.Write(
                $"ジャンプリストを更新しました: 最近 {specs.Count} 件 / 本体のフォルダー={engineFolder}");
        }
        catch (Exception ex)
        {
            EditorLog.Write($"ジャンプリストの更新に失敗しました: {ex.Message}");
        }
    }

    /// <summary>
    /// .seedproj がこの exe に関連付いていなければ登録する（JumpPath の表示条件）。
    /// 登録に失敗してもジャンプリストの更新自体は続ける（項目が出ないだけ）。
    /// </summary>
    private static void EnsureAssociation(string exePath)
    {
        if (FileAssociation.IsRegistered(exePath)) return;
        if (!FileAssociation.Register(exePath, out var error))
            EditorLog.Write($"ジャンプリスト: 関連付けを登録できなかったため「最近」欄は出ません — {error}");
    }
}
