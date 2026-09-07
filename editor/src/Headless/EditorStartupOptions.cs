using System;
using System.IO;
using System.Linq;

namespace SEEDEditor.Headless;

/// <summary>
/// エディタ起動時のコマンドライン引数・環境変数を 1 か所で解釈して保持する。
///
/// <para>
/// 目的は「ヘッドレス運用」。AI エージェント（MCP サーバー経由）がエディタと
/// ランタイムを自分で起動し、画面に何も出さないまま操作してスクリーンショットまで
/// 撮れるようにする。Blender の <c>--background</c> に相当する。
/// </para>
///
/// <para>受け付ける指定（引数と環境変数はどちらでもよい。両方指定なら OR）:</para>
/// <list type="bullet">
///   <item><c>--headless</c> / 環境変数 <c>SEED_HEADLESS=1</c> … 画面外配置・モーダル抑止</item>
///   <item><c>--scene &lt;path&gt;</c>（<c>--scene=&lt;path&gt;</c> 形式も可）… 起動時に開く .scene</item>
/// </list>
///
/// <para>
/// 静的クラスにしているのは、WPF の <c>StartupUri</c> 経由で生成される
/// <c>MainWindow</c> のコンストラクタからも、パネル・RuntimeManager からも
/// 引数を渡さずに参照する必要があるため（DI 経路が無い）。
/// 値は <see cref="Parse"/> を最初に 1 回呼んだ時点で確定し、以後変化しない。
/// </para>
/// </summary>
internal static class EditorStartupOptions
{
    /// <summary>ヘッドレス起動を指示するコマンドライン引数。</summary>
    private const string ARG_HEADLESS = "--headless";
    /// <summary>起動時に開くシーンを指示するコマンドライン引数。</summary>
    private const string ARG_SCENE = "--scene";
    /// <summary>ヘッドレス起動を指示する環境変数名。</summary>
    private const string ENV_HEADLESS = "SEED_HEADLESS";
    /// <summary>環境変数でヘッドレスを有効とみなす値。</summary>
    private const string ENV_HEADLESS_ENABLED = "1";
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
    /// コマンドライン引数と環境変数を解析する。アプリ起動時に 1 回だけ呼ぶ。
    /// 2 回目以降の呼び出しは何もしない（値は不変）。
    /// </summary>
    /// <param name="args">
    /// 解析対象の引数。null なら <c>Environment.GetCommandLineArgs()</c> から
    /// 実行ファイル名を除いたものを使う（WPF の StartupUri 経路でも動くようにするため）。
    /// </param>
    public static void Parse(string[]? args = null)
    {
        if (_parsed) return;
        _parsed = true;

        args ??= Environment.GetCommandLineArgs().Skip(1).ToArray();

        // 環境変数によるヘッドレス指定（MCP サーバーが exe を起動する際の保険）。
        IsHeadless = string.Equals(
            Environment.GetEnvironmentVariable(ENV_HEADLESS),
            ENV_HEADLESS_ENABLED,
            StringComparison.Ordinal);

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (string.Equals(arg, ARG_HEADLESS, StringComparison.OrdinalIgnoreCase))
            {
                IsHeadless = true;
                continue;
            }

            // --scene=path 形式
            if (arg.StartsWith(ARG_SCENE + ARG_INLINE_SEPARATOR, StringComparison.OrdinalIgnoreCase))
            {
                StartupScenePath = NormalizeScenePath(
                    arg[(ARG_SCENE.Length + 1)..]);
                continue;
            }

            // --scene path 形式（次の引数を値として食う）
            if (string.Equals(arg, ARG_SCENE, StringComparison.OrdinalIgnoreCase)
                && i + 1 < args.Length)
            {
                StartupScenePath = NormalizeScenePath(args[++i]);
                continue;
            }

            // --ai-port N / --ai-port=N
            var portValue = TakeValue(args, ref i, ARG_AI_PORT);
            if (portValue is not null)
            {
                AiPort = ParsePort(portValue);
                continue;
            }

            // --ai-token T / --ai-token=T
            var tokenValue = TakeValue(args, ref i, ARG_AI_TOKEN);
            if (tokenValue is not null)
            {
                AiToken = tokenValue.Length == 0 ? null : tokenValue;
            }
        }

        // 引数で指定が無ければ環境変数を見る（プロセス起動方法に依らず届くようにする保険）。
        AiPort  ??= ParsePort(Environment.GetEnvironmentVariable(ENV_AI_PORT));
        AiToken ??= NullIfEmpty(Environment.GetEnvironmentVariable(ENV_AI_TOKEN));
    }

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
