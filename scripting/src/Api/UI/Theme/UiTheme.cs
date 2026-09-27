using System;
using System.IO;

namespace SEED.UI;

// ============================================================
//  UiTheme.cs — 今のテーマ（部品が見た目を決めるときに読む。W2-4）
//
//  既定のテーマ（Theme/default_theme.json。SEEDScripting に埋め込み）を最初に使う。
//  プロジェクトのテーマは <see cref="LoadAsset"/>（assets:// の JSON）か <see cref="Use"/> で差し替える
//  （書いていないトークンは既定のテーマの値）。差し替えると <see cref="Version"/> が増え、部品（UiWidget）は
//  次のフレームで見た目を作り直す。実行中のテーマ交換・ギャラリーでの切り替えの本格化は W2-9。
// ============================================================

/// <summary>今のテーマ。</summary>
public static class UiTheme
{
    /// <summary>既定のテーマの埋め込みの名前（SEEDScripting.csproj の LogicalName と一致）。</summary>
    internal const string DefaultResourceName = "SEED.UI.default_theme.json";

    /// <summary>ログの接頭辞。</summary>
    private const string LogPrefix = "[SEED.UI]";

    /// <summary>既定のテーマ（読み込み済みの値）。</summary>
    private static UiThemeData? _default;
    /// <summary>今のテーマ（null の間は既定のテーマ）。</summary>
    private static UiThemeData? _current;

    /// <summary>テーマを差し替えるたびに増える番号（部品が見た目を作り直す合図）。</summary>
    public static int Version { get; private set; }

    /// <summary>既定のテーマ（SEEDScripting に埋め込んだ default_theme.json）。</summary>
    public static UiThemeData Default => _default ??= LoadDefault();

    /// <summary>今のテーマ。</summary>
    public static UiThemeData Current => _current ?? Default;

    /// <summary>今のテーマの色のトークン。</summary>
    public static Color Color(string token) => Current.Color(token);

    /// <summary>今のテーマの数のトークン。</summary>
    public static float Number(string token, float fallback = 0f) => Current.Number(token, fallback);

    /// <summary>テーマを差し替える（null で既定へ戻す）。</summary>
    public static void Use(UiThemeData? theme)
    {
        _current = theme;
        Version++;
    }

    /// <summary>
    /// アセットのテーマの JSON を読み込んで差し替える（書いていないトークンは既定のテーマ）。
    /// </summary>
    /// <param name="assetPath">assets:// のパス。</param>
    /// <returns>読めて差し替えたら true（読めなければ今のテーマのまま）。</returns>
    public static bool LoadAsset(string assetPath)
    {
        if (!Assets.TryReadText(assetPath, out var json))
        {
            Debug.LogWarning($"{LogPrefix} テーマを読めません: {assetPath}");
            return false;
        }
        var theme = UiThemeData.Parse(json, Default, out var error);
        if (error.Length > 0)
        {
            Debug.LogWarning($"{LogPrefix} テーマの JSON が壊れています: {assetPath}: {error}");
            return false;
        }
        Use(theme);
        return true;
    }

    /// <summary>埋め込みの既定のテーマを読む（読めなければ空＝部品の最後の既定値）。</summary>
    private static UiThemeData LoadDefault()
    {
        try
        {
            using var stream = typeof(UiTheme).Assembly.GetManifestResourceStream(DefaultResourceName);
            if (stream is null)
            {
                Console.Error.WriteLine($"{LogPrefix} 既定のテーマが埋め込まれていません: {DefaultResourceName}");
                return UiThemeData.Empty();
            }
            using var reader = new StreamReader(stream);
            var data = UiThemeData.Parse(reader.ReadToEnd(), null, out var error);
            if (error.Length > 0) Console.Error.WriteLine($"{LogPrefix} 既定のテーマの JSON が壊れています: {error}");
            return data;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{LogPrefix} 既定のテーマを読めません: {ex.Message}");
            return UiThemeData.Empty();
        }
    }
}
