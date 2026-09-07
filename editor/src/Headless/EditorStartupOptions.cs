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

    /// <summary>解析済みかどうか（多重解析を避ける）。</summary>
    private static bool _parsed;

    /// <summary>ヘッドレス起動か。</summary>
    public static bool IsHeadless { get; private set; }

    /// <summary>起動時に開くシーンの絶対パス。指定が無ければ null。</summary>
    public static string? StartupScenePath { get; private set; }

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
            }
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
