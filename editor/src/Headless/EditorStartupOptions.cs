using System;
using System.IO;
using System.Linq;

namespace SEEDEditor.Headless;

/// <summary>
/// 起動引数・環境変数の解析結果（不変）。
///
/// <para>
/// <see cref="EditorStartupOptions.ParseArgs"/> が返す純粋な値。副作用を持たないので
/// 単体テストから引数の組み合わせをそのまま検証できる。
/// 静的プロパティ（<see cref="EditorStartupOptions"/>）はこの結果を適用するだけ。
/// </para>
/// </summary>
/// <param name="IsHeadless">ヘッドレス起動か。</param>
/// <param name="ScenePath">起動時に開く .scene の絶対パス（実在するもののみ）。</param>
/// <param name="ProjectFilePath">開くプロジェクトの .seedproj 絶対パス（実在は問わない）。</param>
/// <param name="AiPort">AI ブリッジの待ち受けポート。</param>
/// <param name="AiToken">AI ブリッジのインスタンストークン。</param>
internal readonly record struct EditorStartupArgs(
    bool    IsHeadless,
    string? ScenePath,
    string? ProjectFilePath,
    int?    AiPort,
    string? AiToken);

/// <summary>
/// エディタ起動時のコマンドライン引数・環境変数を 1 か所で解釈して保持する。
///
/// <para>
/// 目的は 2 つ。1 つは「どのプロジェクトを開くか」の決定
/// （<c>.seedproj</c> のダブルクリック / <c>--project</c> / 環境変数）。
/// もう 1 つは「ヘッドレス運用」で、AI エージェント（MCP サーバー経由）がエディタと
/// ランタイムを自分で起動し、画面に何も出さないまま操作してスクリーンショットまで
/// 撮れるようにする。Blender の <c>--background</c> に相当する。
/// </para>
///
/// <para>受け付ける指定（引数と環境変数はどちらでもよい）:</para>
/// <list type="bullet">
///   <item><c>--headless</c> / 環境変数 <c>SEED_HEADLESS=1</c> … 画面外配置・モーダル抑止</item>
///   <item><c>--project &lt;path&gt;</c>（<c>=</c> 形式も可）… 開くプロジェクト。
///         フォルダでも .seedproj でも良い</item>
///   <item>拡張子 <c>.seedproj</c> の位置引数 … エクスプローラーのダブルクリック経路</item>
///   <item>環境変数 <c>SEED_PROJECT</c> … 引数を渡せない起動経路の保険</item>
///   <item><c>--scene &lt;path&gt;</c>（<c>=</c> 形式も可）… 起動時に開く .scene</item>
/// </list>
///
/// <para>
/// 静的クラスにしているのは、<c>MainWindow</c> のコンストラクタからも、
/// パネル・RuntimeManager からも引数を渡さずに参照する必要があるため（DI 経路が無い）。
/// 値は <see cref="Parse"/> を最初に 1 回呼んだ時点で確定し、以後変化しない。
/// </para>
/// </summary>
internal static class EditorStartupOptions
{
    /// <summary>ヘッドレス起動を指示するコマンドライン引数。</summary>
    private const string ARG_HEADLESS = "--headless";
    /// <summary>起動時に開くシーンを指示するコマンドライン引数。</summary>
    private const string ARG_SCENE = "--scene";
    /// <summary>開くプロジェクトを指示するコマンドライン引数。</summary>
    private const string ARG_PROJECT = "--project";
    /// <summary>ヘッドレス起動を指示する環境変数名。</summary>
    private const string ENV_HEADLESS = "SEED_HEADLESS";
    /// <summary>環境変数でヘッドレスを有効とみなす値。</summary>
    private const string ENV_HEADLESS_ENABLED = "1";
    /// <summary>開くプロジェクトを指示する環境変数名（引数の保険）。</summary>
    private const string ENV_PROJECT = "SEED_PROJECT";
    /// <summary><c>--scene=path</c> 形式で使う区切り文字。</summary>
    private const char ARG_INLINE_SEPARATOR = '=';
    /// <summary>シーンファイルの拡張子（<c>--scene</c> の妥当性チェックに使う）。</summary>
    private const string SCENE_EXTENSION = ".scene";

    /// <summary>AI ブリッジの待ち受けポートを指示するコマンドライン引数。</summary>
    private const string ARG_AI_PORT = "--ai-port";
    /// <summary>AI ブリッジのインスタンストークンを指示するコマンドライン引数。</summary>
    private const string ARG_AI_TOKEN = "--ai-token";
    /// <summary>AI ブリッジのポートを指示する環境変数名（引数の保険）。</summary>
    private const string ENV_AI_PORT = "SEED_AI_PORT";
    /// <summary>AI ブリッジのトークンを指示する環境変数名（引数の保険）。</summary>
    private const string ENV_AI_TOKEN = "SEED_AI_TOKEN";
    /// <summary>ポート番号として受け付ける最小値（0 番と特権ポートは対象外）。</summary>
    private const int AI_PORT_MIN = 1024;
    /// <summary>ポート番号として受け付ける最大値。</summary>
    private const int AI_PORT_MAX = 65535;

    /// <summary>解析済みかどうか（多重解析を避ける）。</summary>
    private static bool _parsed;

    /// <summary>ヘッドレス起動か。</summary>
    public static bool IsHeadless { get; private set; }

    /// <summary>起動時に開くシーンの絶対パス。指定が無ければ null。</summary>
    public static string? StartupScenePath { get; private set; }

    /// <summary>
    /// 起動時に開くプロジェクト（.seedproj）の絶対パス。指定が無ければ null。
    ///
    /// <para>
    /// <b>実在するとは限らない</b>。存在しないファイルを指していても値を返すのは、
    /// 「指定されたが開けなかった」ことを起動処理がエラーとして扱えるようにするため
    /// （黙って別のプロジェクトへ倒すと、利用者は間違ったプロジェクトを編集してしまう）。
    /// </para>
    /// </summary>
    public static string? ProjectFilePath { get; private set; }

    /// <summary>
    /// AI ブリッジが待ち受けるポート。指定が無ければ null（＝既定ポートを使う）。
    ///
    /// 指定されている場合、そのポートを掴めなかったら**エディタは起動を中止する**。
    /// 別のインスタンスへ黙って乗り移らせないための、意図的に厳しい仕様
    /// （docs/editor_mcp.md のポストモーテム節を参照）。
    /// </summary>
    public static int? AiPort { get; private set; }

    /// <summary>
    /// AI ブリッジのインスタンストークン。指定が無ければ null（＝トークン検証なし）。
    /// MCP サーバーは自分が起動したインスタンスのトークンだけを知っている。
    /// </summary>
    public static string? AiToken { get; private set; }

    /// <summary>
    /// コマンドライン引数と環境変数を解析して静的プロパティへ反映する。
    /// アプリ起動時に 1 回だけ呼ぶ。2 回目以降の呼び出しは何もしない（値は不変）。
    /// </summary>
    /// <param name="args">
    /// 解析対象の引数。null なら <c>Environment.GetCommandLineArgs()</c> から
    /// 実行ファイル名を除いたものを使う。
    /// </param>
    public static void Parse(string[]? args = null)
    {
        if (_parsed) return;
        _parsed = true;

        args ??= Environment.GetCommandLineArgs().Skip(1).ToArray();

        var result = ParseArgs(args, Environment.GetEnvironmentVariable);

        IsHeadless       = result.IsHeadless;
        StartupScenePath = result.ScenePath;
        ProjectFilePath  = result.ProjectFilePath;
        AiPort           = result.AiPort;
        AiToken          = result.AiToken;
    }

    /// <summary>
    /// 引数と環境変数から起動オプションを組み立てる純関数（副作用なし）。
    /// </summary>
    /// <param name="args">コマンドライン引数（実行ファイル名を含まない）。</param>
    /// <param name="env">
    /// 環境変数の読み取り関数。null なら環境変数を参照しない
    /// （単体テストが実行環境の環境変数に引きずられないようにするため）。
    /// </param>
    /// <returns>解析結果。</returns>
    public static EditorStartupArgs ParseArgs(string[]? args, Func<string, string?>? env = null)
    {
        args ??= Array.Empty<string>();
        env  ??= static _ => null;

        // ── 環境変数（引数が無い場合の既定値）─────────────────
        var isHeadless = string.Equals(
            env(ENV_HEADLESS), ENV_HEADLESS_ENABLED, StringComparison.Ordinal);

        string? scenePath        = null;
        string? explicitProject  = null;   // --project で明示されたもの（最優先）
        string? positionalProject = null;  // 位置引数の .seedproj（ダブルクリック経路）
        int?    aiPort           = null;
        string? aiToken          = null;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (string.Equals(arg, ARG_HEADLESS, StringComparison.OrdinalIgnoreCase))
            {
                isHeadless = true;
                continue;
            }

            // --project <path> / --project=<path>
            var projectValue = TakeValue(args, ref i, ARG_PROJECT);
            if (projectValue is not null)
            {
                explicitProject = NormalizeProjectPath(projectValue) ?? explicitProject;
                continue;
            }

            // --scene <path> / --scene=<path>
            var sceneValue = TakeValue(args, ref i, ARG_SCENE);
            if (sceneValue is not null)
            {
                scenePath = NormalizeScenePath(sceneValue);
                continue;
            }

            // --ai-port N / --ai-port=N
            var portValue = TakeValue(args, ref i, ARG_AI_PORT);
            if (portValue is not null)
            {
                aiPort = ParsePort(portValue);
                continue;
            }

            // --ai-token T / --ai-token=T
            var tokenValue = TakeValue(args, ref i, ARG_AI_TOKEN);
            if (tokenValue is not null)
            {
                aiToken = tokenValue.Length == 0 ? null : tokenValue;
                continue;
            }

            // 位置引数の .seedproj（エクスプローラーのダブルクリックはこの形で来る）。
            // オプション（先頭が "-"）は対象外。
            if (!arg.StartsWith('-')
                && arg.Trim().Trim('"').EndsWith(SeedProjectFileExtension, StringComparison.OrdinalIgnoreCase))
            {
                positionalProject ??= NormalizeProjectPath(arg);
            }
        }

        // 引数で指定が無ければ環境変数を見る（プロセス起動方法に依らず届くようにする保険）。
        aiPort  ??= ParsePort(env(ENV_AI_PORT));
        aiToken ??= NullIfEmpty(env(ENV_AI_TOKEN));

        // プロジェクトの優先順位: --project > 位置引数 > 環境変数
        var projectPath = explicitProject
                       ?? positionalProject
                       ?? NormalizeProjectPath(env(ENV_PROJECT));

        return new EditorStartupArgs(isHeadless, scenePath, projectPath, aiPort, aiToken);
    }

    /// <summary>
    /// プロジェクトファイルの拡張子。値の正典は
    /// <see cref="SEEDEditor.Project.SeedProjectFile.EXTENSION"/> で、
    /// このクラス内での参照を 1 か所に集めるためのローカル別名。
    /// </summary>
    private const string SeedProjectFileExtension = SEEDEditor.Project.SeedProjectFile.EXTENSION;

    /// <summary>
    /// <c>--name value</c> / <c>--name=value</c> の両形式から値を取り出す。
    /// 一致しなければ null を返し、<paramref name="i"/> は動かさない。
    /// </summary>
    private static string? TakeValue(string[] args, ref int i, string name)
    {
        var arg = args[i];
        if (arg.StartsWith(name + ARG_INLINE_SEPARATOR, StringComparison.OrdinalIgnoreCase))
            return arg[(name.Length + 1)..].Trim().Trim('"');

        if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            return args[++i].Trim().Trim('"');

        return null;
    }

    /// <summary>ポート文字列を検証して返す。範囲外・数値でない場合は null。</summary>
    private static int? ParsePort(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!int.TryParse(raw.Trim(), out var port)) return null;
        return port is >= AI_PORT_MIN and <= AI_PORT_MAX ? port : null;
    }

    /// <summary>空文字を null へ畳む。</summary>
    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// プロジェクト指定を .seedproj の絶対パスへ正規化する。
    ///
    /// <list type="bullet">
    ///   <item>.seedproj ファイルを指していれば、その絶対パス</item>
    ///   <item>フォルダを指していれば、その中の .seedproj（無ければ
    ///         &lt;フォルダ&gt;/&lt;フォルダ名&gt;.seedproj を候補として返す）</item>
    ///   <item>それ以外（空・不正なパス）は null</item>
    /// </list>
    ///
    /// <b>実在確認はしない。</b> 存在しないパスをそのまま返すことで、
    /// 呼び出し側が「指定されたが開けなかった」とエラー表示できる。
    /// </summary>
    /// <param name="raw">引数または環境変数で渡された文字列。</param>
    /// <returns>正規化した .seedproj の絶対パス。判定不能なら null。</returns>
    internal static string? NormalizeProjectPath(string? raw)
    {
        var value = raw?.Trim().Trim('"');
        if (string.IsNullOrEmpty(value)) return null;

        try
        {
            var full = Path.GetFullPath(value);

            // .seedproj を直接指している
            if (full.EndsWith(SeedProjectFileExtension, StringComparison.OrdinalIgnoreCase))
                return full;

            // フォルダ指定: 中の .seedproj を探す
            var found = SEEDEditor.Project.SeedProjectFile.FindInDirectory(full);
            if (found is not null) return found;

            // 見つからなくても「そのフォルダのプロジェクト」を指しているものとして
            // 候補パスを返す（呼び出し側が「見つかりません」と出せる形にする）。
            var folderName = new DirectoryInfo(full).Name;
            if (string.IsNullOrEmpty(folderName)) return null;
            return Path.Combine(full, folderName + SeedProjectFileExtension);
        }
        catch
        {
            // パスとして不正（不正文字など）。指定なしとして扱う。
            return null;
        }
    }

    /// <summary>
    /// <c>--scene</c> の値を絶対パスへ正規化する。
    /// 実在しない・拡張子が <c>.scene</c> でない場合は null（＝指定なし扱い）にする。
    /// 起動直後にダイアログを出せないヘッドレスでは、黙って既定シーンへ倒す方が安全なため。
    /// </summary>
    private static string? NormalizeScenePath(string raw)
    {
        var value = raw.Trim().Trim('"');
        if (string.IsNullOrEmpty(value)) return null;

        try
        {
            var full = Path.GetFullPath(value);
            if (!full.EndsWith(SCENE_EXTENSION, StringComparison.OrdinalIgnoreCase)) return null;
            return File.Exists(full) ? full : null;
        }
        catch
        {
            // パスとして不正（不正文字など）。指定なしとして扱う。
            return null;
        }
    }
}
