// ============================================================
//  ProjectJumpList.cs — タスクバーのジャンプリスト（アイコン右クリックのメニュー）を更新する
//
//  「最近」欄に最近開いたプロジェクトを並べ、クリックで .seedproj を引数に SEEDEditor.exe を
//  起動する（Visual Studio の「最近使ったもの」相当）。
//  「タスク」欄には引数なし起動（スタート画面）を 1 つ置く。
//
//  【仕組み】
//   WPF の JumpList を Application に設定すると、OS が
//   %APPDATA%\Microsoft\Windows\Recent\CustomDestinations に書き込み、
//   タスクバーのボタン（起動中でもピン留めでも）に反映する。
//   項目の識別は exe のパス（AppUserModelID 既定値）なので、同じ exe を指すショートカット
//   （スタートメニュー／ピン留め）から起動した場合も同じ一覧が出る。
//
//  【更新タイミング】
//   プロジェクトを開いたとき／最近の一覧から外したとき／スタート画面を出したとき。
//   ヘッドレス起動（エージェント用）では OS のジャンプリストを触らない。
// ============================================================
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Shell;

namespace SEEDEditor.Project;

/// <summary>
/// 最近のプロジェクト一覧をタスクバーのジャンプリストへ反映する。
/// </summary>
public static class ProjectJumpList
{
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
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                EditorLog.Write("ジャンプリスト: 実行ファイルのパスが取れないため更新しません");
                return;
            }
            var workingDir = Path.GetDirectoryName(exePath) ?? string.Empty;

            var specs = ProjectJumpListBuilder.Build(store.Load(), File.Exists);

            var jumpList = new JumpList
            {
                // OS 管理の「最近使ったもの」「よく使うもの」は使わない
                // （ファイル関連付けが無い環境でも同じ見た目になるよう、自前の「最近」欄だけ出す）。
                ShowRecentCategory   = false,
                ShowFrequentCategory = false,
            };

            foreach (var spec in specs)
            {
                jumpList.JumpItems.Add(new JumpTask
                {
                    Title             = spec.Title,
                    Description       = spec.Description,
                    CustomCategory    = ProjectJumpListBuilder.RECENT_CATEGORY,
                    ApplicationPath   = exePath,
                    Arguments         = spec.Arguments,
                    WorkingDirectory  = workingDir,
                    IconResourcePath  = exePath,
                    IconResourceIndex = 0,
                });
            }

            // 「タスク」欄: 引数なし起動＝スタート画面。
            jumpList.JumpItems.Add(new JumpTask
            {
                Title             = ProjectJumpListBuilder.START_WINDOW_TASK_TITLE,
                Description       = ProjectJumpListBuilder.START_WINDOW_TASK_DESCRIPTION,
                ApplicationPath   = exePath,
                Arguments         = string.Empty,
                WorkingDirectory  = workingDir,
                IconResourcePath  = exePath,
                IconResourceIndex = 0,
            });

            JumpList.SetJumpList(Application.Current, jumpList);
            EditorLog.Write($"ジャンプリストを更新しました: 最近 {specs.Count} 件");
        }
        catch (Exception ex)
        {
            EditorLog.Write($"ジャンプリストの更新に失敗しました: {ex.Message}");
        }
    }
}
