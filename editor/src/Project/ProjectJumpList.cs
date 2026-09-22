// ============================================================
//  ProjectJumpList.cs — タスクバーのジャンプリスト（アイコン右クリックのメニュー）を更新する
//
//  「最近」欄に最近開いたプロジェクトの .seedproj を並べる（Visual Studio の「最近使ったもの」相当）。
//   - 項目は JumpPath（ファイルそのもの）。クリックすると .seedproj の関連付けで SEEDEditor.exe が
//     起動し、右クリックには Windows 標準の「フォルダーの場所を開く」（プロジェクトのフォルダ）が付く。
//   - アプリ自身の項目（"SEED"）は Windows が自動で出す。右クリックの「ファイルの場所を開く」で
//     エンジン（exe）のフォルダが開く。以前あった「スタート画面を開く」タスクは同じ動作の重複なので廃止。
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

            JumpList.SetJumpList(Application.Current, jumpList);
            EditorLog.Write($"ジャンプリストを更新しました: 最近 {specs.Count} 件");
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
