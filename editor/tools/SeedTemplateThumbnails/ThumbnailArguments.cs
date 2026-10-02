// ============================================================
//  ThumbnailArguments.cs — SeedTemplateThumbnails の引数の解釈
//
//  【書式】（Usage と同じ。docs/template_library.md §9.10）
//    dotnet run --project editor/tools/SeedTemplateThumbnails -- [オプション]
//      --only <名前>        撮るテンプレートを絞る（ファイル名の拡張子なし。例 button。',' 区切り・繰り返し可）
//      --size <一辺>        書き出す画像の一辺（px。既定 192 = 規約の推奨）
//      --library <フォルダ>  テンプレートライブラリ（既定: 実行ファイル・作業フォルダから上へ探した templates/）
//      --runtime <SEED.exe> 撮影に使うランタイム（既定: <リポジトリ>/runtime/target/debug/SEED.exe）
//      --runtime-cwd <フォルダ> ランタイムの作業フォルダ（既定: <リポジトリ>/runtime。
//                            ランタイムはここから ../scripting/bin/Debug/net10.0/SEEDScripting.dll を読む）
//      --work <フォルダ>     一時のプロジェクト・撮った画像・ランタイムのログの置き場（既定: %TEMP%\seed_template_thumbnails。
//                            一時のプロジェクト・撮った元の画像・ログは起動のたびに作り直し、終わっても消さない
//                            ＝後から見られる。モデルのキャッシュ cache/ は残して次の読み込みを速くする）
//      --port <番号>         IPC（TCP）の最初のポート（既定 47770。使われていれば次の番号を試す）
//      --sheet <PNG>         書き出した見本を名前付きで 1 枚に並べた確認用の画像も書く
//      --help                使い方を出す
//
//  解釈だけを行い、ファイルの有無などは確かめない（確かめるのは Program の入力の確定）。
// ============================================================

using System.Globalization;

namespace SEEDEditor.Tools.SeedTemplateThumbnails;

/// <summary>解釈した引数（既定値の入った状態）。</summary>
public sealed class ThumbnailOptions
{
    /// <summary>撮るテンプレートのファイル名（拡張子なし。空なら全件）。大文字小文字は区別しない。</summary>
    public HashSet<string> Only { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>書き出す画像の一辺（px）。</summary>
    public int Size { get; set; } = ThumbnailArguments.DefaultSize;

    /// <summary>テンプレートライブラリ（null なら探す）。</summary>
    public string? Library { get; set; }

    /// <summary>ランタイムの実行ファイル（null ならリポジトリの既定）。</summary>
    public string? Runtime { get; set; }

    /// <summary>ランタイムの作業フォルダ（null ならリポジトリの runtime/）。</summary>
    public string? RuntimeWorkingDirectory { get; set; }

    /// <summary>作業フォルダ（null なら %TEMP% の下）。</summary>
    public string? Work { get; set; }

    /// <summary>IPC の最初のポート。</summary>
    public int Port { get; set; } = ThumbnailArguments.DefaultPort;

    /// <summary>確認用の一覧の画像の書き出し先（null なら書かない）。</summary>
    public string? Sheet { get; set; }
}

/// <summary>引数の解釈の結果。</summary>
/// <param name="Options">解釈できたときのオプション。</param>
/// <param name="Error">誤りの説明（無ければ null）。</param>
/// <param name="ShowHelp">使い方を出すだけでよいか。</param>
public sealed record ThumbnailParseResult(ThumbnailOptions? Options, string? Error, bool ShowHelp);

/// <summary>SeedTemplateThumbnails の引数の解釈。</summary>
public static class ThumbnailArguments
{
    /// <summary>書き出す画像の一辺の既定（規約の推奨。TemplateActorCatalogFormat と同じ値）。</summary>
    public const int DefaultSize = SEEDEditor.Templates.Actors.TemplateActorCatalogFormat.RecommendedThumbnailSizePx;

    /// <summary>書き出す画像の一辺の下限（これより小さいと一覧の 64 px の枠より粗くなる）。</summary>
    public const int MinSize = 64;

    /// <summary>書き出す画像の一辺の上限（撮る窓より大きくしても細かくならない）。</summary>
    public const int MaxSize = 1024;

    /// <summary>IPC の最初のポートの既定（検証で使う範囲 47770〜47779 の先頭）。</summary>
    public const int DefaultPort = 47770;

    /// <summary>ポートの下限（特権のポートを避ける）。</summary>
    private const int MinPort = 1024;

    /// <summary>ポートの上限。</summary>
    private const int MaxPort = 65535;

    /// <summary>--only の値の区切り。</summary>
    private const char OnlySeparator = ',';

    /// <summary>使い方（既定値は定数から埋める）。</summary>
    public static readonly string Usage =
        "使い方: dotnet run --project editor/tools/SeedTemplateThumbnails -- [オプション]\n" +
        "  --only <名前>          撮るテンプレートを絞る（ファイル名の拡張子なし。例 button。',' 区切り・繰り返し可）\n" +
        $"  --size <一辺>          書き出す画像の一辺（px。既定 {DefaultSize}・{MinSize}〜{MaxSize}）\n" +
        "  --library <フォルダ>   テンプレートライブラリ（既定: 上へ探した templates/）\n" +
        "  --runtime <SEED.exe>   撮影に使うランタイム（既定: <リポジトリ>/runtime/target/debug/SEED.exe）\n" +
        "  --runtime-cwd <フォルダ> ランタイムの作業フォルダ（既定: <リポジトリ>/runtime。../scripting の DLL を読む）\n" +
        "  --work <フォルダ>      一時のプロジェクト・撮った画像・ログの置き場（既定: %TEMP%\\seed_template_thumbnails）\n" +
        $"  --port <番号>          IPC（TCP）の最初のポート（既定 {DefaultPort}。使用中なら次を試す）\n" +
        "  --sheet <PNG>          書き出した見本を 1 枚に並べた確認用の画像も書く\n" +
        "  --help                 この説明\n" +
        "終了コード: 0 = すべて書けた（飛ばした件を含む）/ 1 = 引数・入力の誤り / 2 = 撮れなかった件がある / 3 = ランタイムを使えなかった";

    /// <summary>
    /// 引数を解釈する。
    /// </summary>
    /// <param name="args">コマンドライン引数。</param>
    /// <returns>解釈の結果。</returns>
    public static ThumbnailParseResult Parse(IReadOnlyList<string> args)
    {
        var options = new ThumbnailOptions();
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            // 値を取るオプションの値（無ければ誤り）
            string? Next() => i + 1 < args.Count ? args[++i] : null;

            switch (arg)
            {
                case "--help":
                case "-h":
                case "/?":
                    return new ThumbnailParseResult(null, null, ShowHelp: true);

                case "--only":
                    if (Next() is not { } only) return Missing(arg);
                    foreach (var name in only.Split(OnlySeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        options.Only.Add(name);
                    break;

                case "--size":
                    if (Next() is not { } sizeText) return Missing(arg);
                    if (!int.TryParse(sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
                        || size < MinSize || size > MaxSize)
                        return Invalid($"--size は {MinSize}〜{MaxSize} の整数で指定してください: {sizeText}");
                    options.Size = size;
                    break;

                case "--library":
                    if (Next() is not { } library) return Missing(arg);
                    options.Library = library;
                    break;

                case "--runtime":
                    if (Next() is not { } runtime) return Missing(arg);
                    options.Runtime = runtime;
                    break;

                case "--runtime-cwd":
                    if (Next() is not { } cwd) return Missing(arg);
                    options.RuntimeWorkingDirectory = cwd;
                    break;

                case "--work":
                    if (Next() is not { } work) return Missing(arg);
                    options.Work = work;
                    break;

                case "--port":
                    if (Next() is not { } portText) return Missing(arg);
                    if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
                        || port < MinPort || port > MaxPort)
                        return Invalid($"--port は {MinPort}〜{MaxPort} の整数で指定してください: {portText}");
                    options.Port = port;
                    break;

                case "--sheet":
                    if (Next() is not { } sheet) return Missing(arg);
                    options.Sheet = sheet;
                    break;

                default:
                    return Invalid($"知らないオプションです: {arg}");
            }
        }
        return new ThumbnailParseResult(options, null, ShowHelp: false);
    }

    /// <summary>値の無いオプションの誤り。</summary>
    private static ThumbnailParseResult Missing(string option) => Invalid($"{option} の値がありません");

    /// <summary>誤りの結果。</summary>
    private static ThumbnailParseResult Invalid(string message) => new(null, message, ShowHelp: false);
}
