using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;

namespace SEEDEditor.Panels.ScriptEditor;

/// <summary>
/// スクリプト全体（アセットルート配下の全 .cs）を 1 つの Roslyn プロジェクトとして
/// 保持するワークスペース。IntelliSense 補完と F12 定義ジャンプ（ファイルまたぎ）に
/// 必要な意味解析を提供する。
///
/// エディタでテキストが変わるたびに該当ドキュメントを更新し、
/// 常に最新のソースで補完・シンボル解決ができるようにする。
/// </summary>
public sealed class ScriptWorkspace
{
    private readonly AdhocWorkspace _workspace;
    private ProjectId _projectId;
    // フルパス（小文字正規化）→ DocumentId
    private readonly Dictionary<string, DocumentId> _docByPath = new();

    public ScriptWorkspace(string assetsRoot)
    {
        // C# の各種サービス（補完・整形など）を含む MEF ホストで構成する
        var host = MefHostServices.Create(MefHostServices.DefaultAssemblies);
        _workspace = new AdhocWorkspace(host);

        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location) && File.Exists(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .ToList();

        var projInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            name: "SEEDUserScripts",
            assemblyName: "SEEDUserScripts",
            language: LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
            metadataReferences: refs);

        var proj = _workspace.AddProject(projInfo);
        _projectId = proj.Id;

        LoadAllScripts(assetsRoot);
    }

    /// <summary>アセットルート配下の全 .cs をドキュメントとして登録する。</summary>
    private void LoadAllScripts(string assetsRoot)
    {
        if (!Directory.Exists(assetsRoot)) return;
        foreach (var file in Directory.EnumerateFiles(assetsRoot, "*.cs", SearchOption.AllDirectories))
        {
            try { UpsertText(file, File.ReadAllText(file)); }
            catch { /* 読めないファイルはスキップ */ }
        }
    }

    /// <summary>指定ファイルのドキュメントテキストを更新する（無ければ追加する）。</summary>
    public void UpsertText(string filePath, string text)
    {
        var key = Normalize(filePath);
        if (_docByPath.TryGetValue(key, out var id))
        {
            var sol = _workspace.CurrentSolution.WithDocumentText(id, SourceText.From(text));
            _workspace.TryApplyChanges(sol);
            return;
        }

        var docId = DocumentId.CreateNewId(_projectId);
        var info = DocumentInfo.Create(
            docId,
            name: Path.GetFileName(filePath),
            filePath: filePath,
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(text), VersionStamp.Create(), filePath)));
        var sol2 = _workspace.CurrentSolution.AddDocument(info);
        _workspace.TryApplyChanges(sol2);
        _docByPath[key] = docId;
    }

    /// <summary>指定ファイルの現在の Roslyn Document を返す。</summary>
    public Document? GetDocument(string filePath)
    {
        var key = Normalize(filePath);
        return _docByPath.TryGetValue(key, out var id)
            ? _workspace.CurrentSolution.GetDocument(id)
            : null;
    }

    public Solution CurrentSolution => _workspace.CurrentSolution;

    private static string Normalize(string path)
    {
        try { path = Path.GetFullPath(path); } catch { }
        return path.Replace('/', '\\').ToLowerInvariant();
    }
}
