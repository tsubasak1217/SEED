using System;
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
        }
        catch (Exception ex)
        {
            EditorLog.Write($"WGSL シンタックスハイライト定義の読み込みに失敗しました: {ex.Message}");
            _definition = null;
        }

        return _definition;
    }
}
