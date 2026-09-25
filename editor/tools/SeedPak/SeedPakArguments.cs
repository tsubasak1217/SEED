// ============================================================
//  SeedPakArguments.cs — SeedPak のコマンドライン引数の解釈（純粋な処理）
//
//  【役割】
//  文字列の配列を SeedPakOptions に変換するだけ。ファイルシステムには触らない
//  （フォルダの実在確認やアセットルートの決定は PakInputResolver の責務）。
// ============================================================

using System;
using System.Collections.Generic;

namespace SEEDEditor.Tools.SeedPak;

/// <summary>何を作るか（--scripts / --scripts-only で決まる）。</summary>
public enum SeedPakContent
{
    /// <summary>assets.pak だけ（従来どおり・既定）。</summary>
    PakOnly,

    /// <summary>assets.pak と bin/（スクリプトの事前コンパイル DLL とスクリプトホスト一式）。</summary>
    PakAndScripts,

    /// <summary>bin/ だけ（PAK は作らない。Android の -PushScripts でスクリプトだけ差し替えるとき）。</summary>
    ScriptsOnly,
}

/// <summary>解釈済みのコマンドライン引数。</summary>
/// <param name="ProjectDir">--project の値（プロジェクトフォルダ）。</param>
/// <param name="AssetsDir">--assets の値（アセットルートの直接指定）。</param>
/// <param name="OutDir">--out の値（assets.pak・bin/ を書くフォルダ）。</param>
/// <param name="RuntimeSourceDir">--runtime-src の値（runtime/src。省略可）。</param>
/// <param name="Content">何を作るか（--scripts / --scripts-only）。</param>
public sealed record SeedPakOptions(
    string? ProjectDir,
    string? AssetsDir,
    string OutDir,
    string? RuntimeSourceDir,
    SeedPakContent Content = SeedPakContent.PakOnly)
{
    /// <summary>
    /// --extra-scene の値（指定の順。無ければ空）。project_settings.json の登録シーンに加えて収録の起点にするシーン
    /// （Android の実行で、シーンマネージャに未登録の開いているシーンを pak に入れるため）。
    /// </summary>
    public IReadOnlyList<string> ExtraScenes { get; init; } = Array.Empty<string>();

    /// <summary>assets.pak を作るか。</summary>
    public bool WritesPak => Content != SeedPakContent.ScriptsOnly;

    /// <summary>bin/（スクリプト）を作るか。</summary>
    public bool WritesScripts => Content != SeedPakContent.PakOnly;
}

/// <summary>引数を解釈した結果（成功・ヘルプ要求・エラーのどれか）。</summary>
/// <param name="Options">成功したときの値。</param>
/// <param name="ShowHelp">--help が指定された。</param>
/// <param name="Error">エラーの説明（成功時は null）。</param>
public sealed record SeedPakParseResult(SeedPakOptions? Options, bool ShowHelp, string? Error);

/// <summary>SeedPak のコマンドライン引数の解釈。</summary>
public static class SeedPakArguments
{
    // ── オプション名 ─────────────────────────────────────────

    /// <summary>プロジェクトフォルダ（その下の assets/ をアセットルートにする）。</summary>
    public const string ProjectOption = "--project";

    /// <summary>アセットルートを直接指定する。</summary>
    public const string AssetsOption = "--assets";

    /// <summary>出力フォルダ（assets.pak をここへ書く）。</summary>
    public const string OutOption = "--out";

    /// <summary>エンジンのソース（runtime/src）。組み込み参照（assets://）を収録の起点に加えるのに使う。</summary>
    public const string RuntimeSourceOption = "--runtime-src";

    /// <summary>assets.pak に加えて bin/（スクリプトの事前コンパイル DLL とスクリプトホスト一式）も作る（値を取らない）。</summary>
    public const string ScriptsOption = "--scripts";

    /// <summary>bin/ だけを作る（PAK は作らない。値を取らない）。</summary>
    public const string ScriptsOnlyOption = "--scripts-only";

    /// <summary>
    /// 登録シーンに加えて収録の起点にするシーン（値を 1 つ取る。繰り返し指定できる）。
    /// Android の実行（SeedAndroid・エディタ）が、シーンマネージャに未登録の開いているシーンを pak に入れるのに使う。
    /// </summary>
    public const string ExtraSceneOption = "--extra-scene";

    /// <summary>使い方を表示する。</summary>
    public const string HelpOption = "--help";

    /// <summary>使い方を表示する（短縮形）。</summary>
    public const string HelpShortOption = "-h";

    /// <summary>使い方の説明文。</summary>
    public const string Usage = """
        SeedPak — エディタを起動せずに assets.pak を作る（パッケージ化ウィンドウと同じ収録規則・パス書き換え・PAK 形式）

        使い方:
          dotnet run --project editor/tools/SeedPak -- --project <プロジェクトフォルダ> --out <出力フォルダ>
          dotnet run --project editor/tools/SeedPak -- --assets <アセットルート> --out <出力フォルダ>
          dotnet run --project editor/tools/SeedPak -- --project <プロジェクトフォルダ> --out <出力フォルダ> --scripts
          dotnet run --project editor/tools/SeedPak -- --project <プロジェクトフォルダ> --out <出力フォルダ> --extra-scene scenes/Stage2.scene

        オプション:
          --project <フォルダ>      プロジェクトフォルダ。<フォルダ>/assets をアセットルートにする
                                    （<フォルダ> 自体に project_settings.json があればそこをアセットルートにする）
          --assets <フォルダ>       アセットルートを直接指定する（--project と排他）
          --out <フォルダ>          出力フォルダ。<フォルダ>/assets.pak を書く（無ければ作る）
          --runtime-src <フォルダ>  エンジンのソース runtime/src（既定: このツールのあるリポジトリから探す）
          --scripts                 加えて <フォルダ>/bin/ にスクリプトを作る（パッケージ化ウィンドウと同じ ScriptPackager:
                                    アセット配下の .cs を事前コンパイルした SEEDUserScripts.dll と、スクリプトホスト
                                    SEEDScripting.dll・依存 DLL・runtimeconfig。ホストは scripting/bin/Debug/net10.0 から写す）
          --scripts-only            bin/ だけを作る（PAK は作らない。Android の DLL の差し替え用）
          --extra-scene <シーン>    project_settings.json の登録シーンに加えて収録の起点にするシーン（繰り返し指定できる）。
                                    アセットルートからの相対パス・assets://…・アセットルート内の絶対パス。そのシーンと
                                    そこから参照をたどれるものを PAK に入れる（Android の実行で、シーンマネージャに未登録の
                                    開いているシーンから起動するため）。無いシーンは警告して飛ばす。--scripts-only とは併用できない
          --help, -h                この説明を表示する

        収録ルールはパッケージ化ウィンドウと同じく <アセットルート>/packaging_settings.json を読む（無ければ既定値）。
        アセットルートはエディタが使うパスと同じ表記で渡すこと（シーン内の絶対パス参照の照合・書き換えがこの表記を基準にする）。
        bin/ は上書きで書き足す（古いファイルは消さない。置き場を作り直すのは呼び出し側）。

        終了コード: 0 成功 / 1 引数・入力の誤り / 2 収録対象 0 件 / 3 書き出し失敗 / 4 スクリプトのコンパイル・同梱の失敗
        """;

    /// <summary>
    /// コマンドライン引数を解釈する。
    /// </summary>
    /// <param name="args">コマンドライン引数。</param>
    /// <returns>解釈結果。</returns>
    public static SeedPakParseResult Parse(IReadOnlyList<string> args)
    {
        string? project = null, assets = null, output = null, runtimeSource = null;
        var content = SeedPakContent.PakOnly;
        var extraScenes = new List<string>();

        for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == HelpOption || arg == HelpShortOption)
                return new SeedPakParseResult(null, ShowHelp: true, Error: null);

            // 値を取らないフラグ（何を作るか）
            if (arg is ScriptsOption or ScriptsOnlyOption)
            {
                var requested = arg == ScriptsOption ? SeedPakContent.PakAndScripts : SeedPakContent.ScriptsOnly;
                if (content != SeedPakContent.PakOnly && content != requested)
                    return Fail($"{ScriptsOption} と {ScriptsOnlyOption} は同時に指定できません");
                content = requested;
                continue;
            }

            // 以降のオプションはすべて値を 1 つ取る
            if (arg is not (ProjectOption or AssetsOption or OutOption or RuntimeSourceOption or ExtraSceneOption))
                return Fail($"不明な引数です: {arg}");
            if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]))
                return Fail($"{arg} には値が必要です");

            var value = args[++i];
            switch (arg)
            {
                case ProjectOption:       project       = value; break;
                case AssetsOption:        assets        = value; break;
                case OutOption:           output        = value; break;
                case RuntimeSourceOption: runtimeSource = value; break;
                case ExtraSceneOption:    extraScenes.Add(value); break;   // 繰り返し指定できる（指定の順に積む）
            }
        }

        if (project is null && assets is null)
            return Fail($"{ProjectOption} か {AssetsOption} のどちらかを指定してください");
        if (project is not null && assets is not null)
            return Fail($"{ProjectOption} と {AssetsOption} は同時に指定できません");
        if (output is null)
            return Fail($"{OutOption} を指定してください");
        // 追加の起点は PAK の収録にだけ効く。PAK を作らない指定と一緒なら、指定の食い違いとして知らせる
        if (extraScenes.Count > 0 && content == SeedPakContent.ScriptsOnly)
            return Fail($"{ExtraSceneOption} は {ScriptsOnlyOption} と同時に指定できません（PAK を作らないため）");

        return new SeedPakParseResult(
            new SeedPakOptions(project, assets, output, runtimeSource, content) { ExtraScenes = extraScenes },
            ShowHelp: false, Error: null);
    }

    /// <summary>エラーの解釈結果を作る。</summary>
    /// <param name="message">エラーの説明。</param>
    /// <returns>解釈結果。</returns>
    private static SeedPakParseResult Fail(string message) =>
        new(null, ShowHelp: false, Error: message);
}
