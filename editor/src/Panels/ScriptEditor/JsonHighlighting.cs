using System;
using System.Xml;
using SEEDEditor;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace SEEDEditor.Panels.ScriptEditor;

/// <summary>
/// JSON 用のシンタックスハイライト定義を提供する。
///
/// <para>
/// AvalonEdit には JSON の定義が同梱されていないため、埋め込みリソースの .xshd を
/// 読み込んで <see cref="HighlightingManager"/> へ登録する（WGSL と同じ流儀）。
/// 定義は全エディタで共有するため、初回のみ生成してキャッシュする。
/// </para>
/// <para>
/// 対象は .json だけでなく、中身が JSON の独自拡張子（.icons / .inputmap / .anim）も含む。
/// どの拡張子をこの定義で開くかは <see cref="TextEditableCatalog"/> が決める。
/// </para>
/// </summary>
public static class JsonHighlighting
{
    /// <summary>HighlightingManager に登録する定義名。</summary>
    private const string DefinitionName = "JSON";

    /// <summary>埋め込みリソース名（SEEDEditor.csproj の LogicalName と一致させること）。</summary>
    private const string ResourceName = "SEEDEditor.Json.xshd";

    /// <summary>定義に紐付ける代表拡張子（GetDefinitionByExtension 用）。</summary>
    private static readonly string[] Extensions = { ".json" };

    /// <summary>読み込み済みの定義（失敗した場合も null をキャッシュして再試行しない）。</summary>
    private static IHighlightingDefinition? _definition;
    private static bool _loaded;

    /// <summary>
    /// JSON のハイライト定義を返す。読み込みに失敗した場合は null
    /// （呼び出し側はハイライトなしのプレーン表示にフォールバックする）。
    /// </summary>
    public static IHighlightingDefinition? Get()
    {
        if (_loaded) return _definition;
        _loaded = true;

        // 既に登録済み（多重ロード時など）ならそれを再利用する
        var registered = HighlightingManager.Instance.GetDefinition(DefinitionName);
        if (registered is not null) return _definition = registered;

        try
        {
            using var stream = typeof(JsonHighlighting).Assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException($"埋め込みリソースが見つかりません: {ResourceName}");
            using var reader = XmlReader.Create(stream);

            var def = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            HighlightingManager.Instance.RegisterHighlighting(DefinitionName, Extensions, def);
            _definition = def;
        }
        catch (Exception ex)
        {
            EditorLog.Write($"JSON シンタックスハイライト定義の読み込みに失敗しました: {ex.Message}");
            _definition = null;
        }

        return _definition;
    }

    /// <summary>
    /// AvalonEdit 同梱の定義を名前で取得する（無ければ null）。
    ///
    /// Markdown のように「同梱されていれば使いたいが、無くても困らない」種別のための入口。
    /// バージョンによって同梱定義の有無が変わるため、必ず null を許容して呼ぶこと。
    /// </summary>
    /// <param name="name">定義名（例: "MarkDown"）。</param>
    public static IHighlightingDefinition? GetBuiltIn(string name)
    {
        try { return HighlightingManager.Instance.GetDefinition(name); }
        catch { return null; }
    }
}
