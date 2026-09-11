// ============================================================
//  ProjectContext.cs — 「いま開いているプロジェクト」の唯一の保持場所
//
//  【役割】
//  起動時に 1 回だけ確定させ、以後エディタ全体がここを参照する。
//  MainWindow.AssetsPath もこの値を返すだけになっており、
//  「アセットルートはどこか」の答えが 2 か所に存在しない状態を保つ。
//
//  【なぜ static なのか】
//  MainWindow / 各パネル / RuntimeManager は DI 経路を持たず、
//  従来から MainWindow の static フィールド（AssetsPath）を直接参照していた。
//  プロジェクトは 1 プロセスに 1 つ（別プロジェクトは別プロセスで開く）なので、
//  プロセス全体で 1 つの値を持つ static がそのまま正しいモデルになる。
//
//  【切り替えについて】
//  プロジェクトの切り替えは「新しいプロセスを起動して現在のプロセスを閉じる」で行う。
//  開いたあとに Open を呼び直すことは想定していない（パネルが保持した旧パスが
//  残ったままになるため）。Open は 2 回目以降を拒否せず上書きするが、
//  呼び出し側は起動時のみ呼ぶこと。
// ============================================================

using System;

namespace SEEDEditor.Project;

/// <summary>
/// 現在開いているプロジェクト（プロセス全体で 1 つ）。
/// </summary>
public static class ProjectContext
{
    /// <summary>プロジェクト未確定のときに表示する名前（スタート画面経由では通常起きない）。</summary>
    public const string NO_PROJECT_DISPLAY_NAME = "（プロジェクト未設定）";

    /// <summary>現在のプロジェクトのパス一式。未確定なら null。</summary>
    public static ProjectPaths? Paths { get; private set; }

    /// <summary>現在のプロジェクトファイルの内容。未確定なら null。</summary>
    public static SeedProjectFile? File { get; private set; }

    /// <summary>プロジェクトが確定しているか。</summary>
    public static bool IsOpen => Paths is not null;

    /// <summary>
    /// アセットルートの絶対パス。未確定なら空文字。
    ///
    /// 空文字は <see cref="SEEDEditor.Assets.AssetsRootProbe"/> が
    /// Invalid と判定するため、エディタは「アセットフォルダが使えない」表示で
    /// 起動を続行できる（例外で落ちない）。
    /// </summary>
    public static string AssetsDir => Paths?.AssetsDir ?? string.Empty;

    /// <summary>プロジェクトルート。未確定なら空文字。</summary>
    public static string RootDir => Paths?.RootDir ?? string.Empty;

    /// <summary>プラグインフォルダ。未確定なら空文字。</summary>
    public static string PluginsDir => Paths?.PluginsDir ?? string.Empty;

    /// <summary>ウィンドウタイトル等に使う表示名。</summary>
    public static string DisplayName => Paths?.DisplayName ?? NO_PROJECT_DISPLAY_NAME;

    /// <summary>
    /// 読み込み済みの内容でプロジェクトを確定させる。
    /// </summary>
    /// <param name="paths">パス一式。</param>
    /// <param name="file">プロジェクトファイルの内容。</param>
    public static void Open(ProjectPaths paths, SeedProjectFile file)
    {
        Paths = paths ?? throw new ArgumentNullException(nameof(paths));
        File  = file  ?? throw new ArgumentNullException(nameof(file));
        EditorLog.Write($"プロジェクトを開きました: {paths}");
    }

    /// <summary>
    /// .seedproj を読み込んでプロジェクトを確定させる。
    /// assets / plugins フォルダが無ければ作る（再クローン直後などを救う）。
    /// </summary>
    /// <param name="projectFilePath">.seedproj の絶対パス。</param>
    /// <returns>確定したパス一式。</returns>
    /// <exception cref="SeedProjectFileException">読み込みに失敗したとき。</exception>
    public static ProjectPaths OpenFromFile(string projectFilePath)
    {
        var file  = SeedProjectFile.Load(projectFilePath);
        var paths = ProjectPaths.FromProjectFile(projectFilePath, file);

        // 空のプロジェクトフォルダを開いた場合でも assets/ が無いだけで
        // 何もできなくなるのは不便なので、最低限の骨組みは用意する。
        try { paths.EnsureDirectories(); }
        catch (Exception ex)
        {
            // 作成に失敗しても開くこと自体は続行する（読み取り専用メディア等）。
            EditorLog.Write($"プロジェクトのフォルダ作成に失敗しました: {ex.Message}");
        }

        Open(paths, file);
        return paths;
    }
}
