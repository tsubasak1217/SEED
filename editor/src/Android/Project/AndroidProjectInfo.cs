// ============================================================
//  AndroidProjectInfo.cs — 今回の実行のプロジェクト（どこのアセットを、どう APK / 端末へ届けるか）
//
//  【2 つの形】（従来の build_and_run.ps1 の -ProjectDir / -AssetsDir と同じ）
//    Packaged          … --project。SeedPak で pak とスクリプトの bin/ を作って APK に入れる（パッケージ実行。配布版と同じ形）
//    DevelopmentAssets … --assets-dir。APK は pak の無い開発用で、アセットは run-as で端末の内部フォルダへ送る
//  どちらも、画面の向き・アプリの識別情報はそのアセットルートの project_settings.json から読む。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using SEEDEditor.Project;

namespace SEEDEditor.Android.Project;

/// <summary>プロジェクトの届け方。</summary>
public enum AndroidProjectMode
{
    /// <summary>APK に pak とスクリプトを入れる（--project）。</summary>
    Packaged,

    /// <summary>pak の無い開発用の APK と、run-as で送るアセット（--assets-dir）。</summary>
    DevelopmentAssets,
}

/// <summary>今回の実行のプロジェクト。</summary>
/// <param name="Folder">アセットルート・プロジェクトルート・プロジェクト名。</param>
/// <param name="Mode">届け方。</param>
/// <param name="SourceArgument">指定されたフォルダ（SeedPak へそのまま渡す）。</param>
/// <param name="Settings">project_settings.json から読んだ値。</param>
public sealed record AndroidProjectInfo(
    ResolvedProjectFolder Folder,
    AndroidProjectMode Mode,
    string SourceArgument,
    AndroidProjectSettings Settings);
