using System;
using System.Collections.Generic;
using System.Xml;
using SEEDEditor;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace SEEDEditor.Panels.ScriptEditor;

/// <summary>
/// WGSL（WebGPU Shading Language）用のシンタックスハイライト定義を提供する。
///
/// C# は AvalonEdit に定義が同梱されている（HighlightingManager から取得できる）が、
/// WGSL は同梱されていないため、埋め込みリソースの .xshd を読み込んで
/// <see cref="HighlightingManager"/> へ登録する。
/// 定義は全エディタで共有するため、初回のみ生成してキャッシュする。
/// </summary>
public static class WgslHighlighting
{
    /// <summary>HighlightingManager に登録する定義名。</summary>
    private const string DefinitionName = "WGSL";

    /// <summary>埋め込みリソース名（SEEDEditor.csproj の LogicalName と一致させること）。</summary>
    private const string ResourceName = "SEEDEditor.Wgsl.xshd";

    /// <summary>読み込み済みの定義（失敗した場合も null をキャッシュして再試行しない）。</summary>
    private static IHighlightingDefinition? _definition;
    private static bool _loaded;

    /// <summary>
    /// xshd に書かれていた「元の」前景色のスナップショット（Color 名 → "#RRGGBB"）。
    ///
    /// 定義中の <see cref="HighlightingColor"/> はユーザー設定色の適用で上書きされるため、
    /// 上書き前に一度だけ控えておく。これが配色設定の既定値（＝リセット先）になる。
    /// </summary>
    private static readonly Dictionary<string, string> _xshdDefaultForegrounds = new(StringComparer.Ordinal);

    /// <summary>
    /// WGSL のハイライト定義を返す。読み込みに失敗した場合は null
    /// （呼び出し側はハイライトなしのプレーン表示にフォールバックする）。
    /// </summary>
    public static IHighlightingDefinition? Get()
    {
        if (_loaded) return _definition;
        _loaded = true;

        // 既に登録済み（多重ロード時など）ならそれを再利用する
        var registered = HighlightingManager.Instance.GetDefinition(DefinitionName);
        if (registered is not null)
        {
            _definition = registered;
            CaptureDefaultForegrounds(registered);
            return _definition;
        }

        try
        {
            using var stream = typeof(WgslHighlighting).Assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException($"埋め込みリソースが見つかりません: {ResourceName}");
            using var reader = XmlReader.Create(stream);

            // xshd を読み込み、名前と拡張子を指定して HighlightingManager へ登録する。
            // 登録しておくことで、将来 GetDefinitionByExtension(".wgsl") でも取得できる。
            var def = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            HighlightingManager.Instance.RegisterHighlighting(
                DefinitionName,
                new[] { EditorLanguages.WgslExtension },
                def);
            _definition = def;
            // ユーザー設定色で上書きされる前に、xshd 本来の色を控えておく
            CaptureDefaultForegrounds(def);
        }
        catch (Exception ex)
        {
            EditorLog.Write($"WGSL シンタックスハイライト定義の読み込みに失敗しました: {ex.Message}");
            _definition = null;
        }

        return _definition;
    }

    /// <summary>
    /// xshd 由来の既定前景色（Color 名 → "#RRGGBB"）を返す。
    /// 未読み込みならこの中で読み込みを行う。読み込みに失敗している場合は空。
    /// </summary>
    public static IReadOnlyDictionary<string, string> XshdDefaultForegrounds()
    {
        Get();
        return _xshdDefaultForegrounds;
    }

    /// <summary>
    /// 定義中の名前付き色の前景色を "#RRGGBB" 文字列として控える。
    /// 既に控えてある名前は上書きしない（常に「最初に読んだ＝xshd 本来の色」を保つ）。
    /// </summary>
    private static void CaptureDefaultForegrounds(IHighlightingDefinition def)
    {
        foreach (var c in def.NamedHighlightingColors)
        {
            if (c.Name is null || _xshdDefaultForegrounds.ContainsKey(c.Name)) continue;
            // SimpleHighlightingBrush は文脈を参照しないため null を渡してよい。
            // 文脈依存ブラシ（テーマ色参照など）の場合は色を取得できず null が返る。
            var color = c.Foreground?.GetColor(null!);
            if (color is { } col)
                _xshdDefaultForegrounds[c.Name] = $"#{col.R:X2}{col.G:X2}{col.B:X2}";
        }
    }
}
