// ============================================================
//  ThumbnailInputs.cs — 入力（ライブラリ・ランタイム・作業フォルダ）の確定
//
//  引数で指定が無ければリポジトリの決まった場所から探す:
//    ライブラリ   … 実行ファイルの場所と作業フォルダから上へ、カタログ（templates/<フォルダ>/template_actors.json）を
//                   持つ templates/ を探す（環境変数 SEED_TEMPLATE_LIBRARY があればそれ。エディタと同じ上書き）
//    ランタイム   … <リポジトリ>/runtime/target/debug/SEED.exe（リポジトリ = ライブラリの親）
//    作業フォルダ … <リポジトリ>/runtime（ランタイムはここから ../scripting/bin/Debug/net10.0/SEEDScripting.dll を読む）
//    作業の置き場 … %TEMP%\seed_template_thumbnails\run_<プロセス ID>_<乱数>（実行ごとの下位フォルダ。同時に動かしても重ならない。
//                   成功したら消す。ThumbnailWorkFolder）
//  作業の置き場を使ってよいか（プロジェクト・印の無いフォルダを拒む）はここでは調べない（Program が ThumbnailWorkFolder で借りる）。
// ============================================================

using SEEDEditor.Templates;
using SEEDEditor.Tools.SeedTemplateThumbnails.Work;

namespace SEEDEditor.Tools.SeedTemplateThumbnails;

/// <summary>確定した入力。</summary>
/// <param name="LibraryRoot">テンプレートライブラリの絶対パス。</param>
/// <param name="RuntimeExe">SEED.exe の絶対パス。</param>
/// <param name="RuntimeWorkingDirectory">ランタイムの作業フォルダの絶対パス。</param>
/// <param name="WorkRoot">作業の置き場の絶対パス。</param>
/// <param name="WorkIsTemporary">作業の置き場が既定の実行ごとの下位フォルダ（成功したら消す）か。--work で指定したら false。</param>
public sealed record ThumbnailInputs(
    string LibraryRoot, string RuntimeExe, string RuntimeWorkingDirectory, string WorkRoot, bool WorkIsTemporary)
{
    /// <summary>上へ探す段数の上限。</summary>
    private const int SearchDepth = 12;

    /// <summary>リポジトリから見たランタイムの debug の実行ファイル。</summary>
    private static readonly string[] DefaultRuntimeRelative = ["runtime", "target", "debug", "SEED.exe"];

    /// <summary>リポジトリから見たランタイムの作業フォルダ。</summary>
    private const string RuntimeFolderName = "runtime";

    /// <summary>
    /// 引数と既定から入力を確定する。
    /// </summary>
    /// <param name="options">引数。</param>
    /// <param name="error">確定できなかった理由。</param>
    /// <returns>入力（確定できなければ null）。</returns>
    public static ThumbnailInputs? Resolve(ThumbnailOptions options, out string error)
    {
        error = "";
        var library = options.Library is { } given ? Path.GetFullPath(given) : FindLibrary();
        if (library is null || !HasCatalog(library))
        {
            error = $"テンプレートライブラリ（カタログ付きの templates/）が見つかりません: {library ?? "（探せませんでした）"}。--library で指定してください";
            return null;
        }
        string repository = Path.GetDirectoryName(library) ?? library;

        var exe = Path.GetFullPath(options.Runtime ?? Path.Combine([repository, .. DefaultRuntimeRelative]));
        if (!File.Exists(exe))
        {
            error = $"ランタイム（SEED.exe）が見つかりません: {exe}。--runtime で指定してください";
            return null;
        }
        var cwd = Path.GetFullPath(options.RuntimeWorkingDirectory ?? Path.Combine(repository, RuntimeFolderName));
        if (!Directory.Exists(cwd))
        {
            error = $"ランタイムの作業フォルダが見つかりません: {cwd}。--runtime-cwd で指定してください";
            return null;
        }
        // 作業の置き場: 指定が無ければ実行ごとの下位フォルダ（固定名だと同時に動かした実行どうしが中身を消し合う）
        bool temporary = options.Work is null;
        var work = Path.GetFullPath(options.Work ?? ThumbnailWorkFolder.NewDefaultRunFolder());
        return new ThumbnailInputs(library, exe, cwd, work, temporary);
    }

    /// <summary>環境変数・実行ファイルの場所・作業フォルダから上へ、カタログを持つ templates/ を探す。</summary>
    private static string? FindLibrary()
    {
        if (Environment.GetEnvironmentVariable(TemplateLibraryLocator.OverrideEnvVar) is { Length: > 0 } env && HasCatalog(env))
            return Path.GetFullPath(env);
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = new DirectoryInfo(start);
            for (int i = 0; i < SearchDepth && dir is not null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, TemplateLibraryLocator.LibraryFolderName);
                if (HasCatalog(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>トップレベルのどこかのフォルダにカタログがあるか。</summary>
    private static bool HasCatalog(string library) =>
        Directory.Exists(library)
        && Directory.EnumerateDirectories(library)
            .Any(d => File.Exists(Path.Combine(d, TemplateLibraryMetadata.TemplateActorCatalogFileName)));
}
