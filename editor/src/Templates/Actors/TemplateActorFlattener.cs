// ============================================================
//  TemplateActorFlattener.cs — テンプレートを「まっさらな 1 本の木」にする
//
//  【役割】
//  テンプレート（.actor の JSON）を、プレハブとのつながりを一切持たない普通のアクタの木へ直す。
//   1. 入れ子のプレハブの参照（子のノードの prefab_source）を展開する
//   2. すべてのノードから prefab_source / prefab_hash を取り除く
//  これで追加したアクタはプレハブのインスタンスにならない（ヒエラルキーで水色にならず、
//  「プレハブから更新」の対象にもならない）。プロジェクトへ .actor を作る必要も無い。
//
//  【展開の規則】（ランタイムの「プレハブから更新」と同じ考え方。docs/editor_prefab.md）
//  SEED の .actor は、入れ子のインスタンスの中身（子の木とコンポーネント）も丸ごと保存している。
//  ランタイムは「保存された内容を正とする」方針なので、ここでも**保存された中身をそのまま使う**。
//  ただし中身が空（components も children も空）で参照だけが残ったノード（手書きの参照など）は、
//  参照先のファイルをライブラリから読んで中身を差し込む。このとき残す値は
//  ルートの name / transform / canvas_transform / active / visible（再展開で維持される値と同じ）。
//  - 参照先の版（format_version）がテンプレートと違うときは差し込まない
//    （版の変換はランタイムが読み込みの入口で木全体へ 1 度だけ行うので、版が混ざると変換を誤る）。
//  - 循環参照・深すぎる入れ子（MaxNestedExpansionDepth 超）は差し込まない。
//  いずれも警告を返し、そのノードは「参照を外しただけ」の状態で残す。
//
//  【スクリプトの欄にあるプレハブのパス】
//  RowPrefab = "assets://ui/prefabs/wheel_row.actor" のように、スクリプトが**実行中に**
//  Instantiate するパスは木の一部ではないので展開しない（展開すると行を何個作るかが決まらない）。
//  それらは「動かすのに要るファイル」として TemplateActorDependencyPlanner がコピーする。
// ============================================================

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace SEEDEditor.Templates.Actors;

/// <summary>
/// <see cref="TemplateActorFlattener.Flatten"/> の結果。
/// </summary>
public sealed class TemplateActorFlattenResult
{
    /// <summary>まっさらにしたアクタの木（入力とは別の複製）。</summary>
    public required JsonObject Root { get; init; }

    /// <summary>中身を差し込んだ入れ子のプレハブのノード数。</summary>
    public int ExpandedCount { get; init; }

    /// <summary>prefab_source / prefab_hash を取り除いたノード数。</summary>
    public int StrippedCount { get; init; }

    /// <summary>差し込めなかった参照など、利用者へ知らせる文。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>ルートの表示名（name 欄。無ければ空）。</summary>
    public string RootName =>
        Root[TemplateActorFlattener.NameKey] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    /// <summary>ルートが 2D アクタか。</summary>
    public bool Is2D => TemplateActorFlattener.IsActor2D(Root);
}

/// <summary>
/// テンプレートの JSON からプレハブのつながりを取り除き、入れ子を展開する。状態を持たない。
/// </summary>
public static class TemplateActorFlattener
{
    // ============================================================
    //  欄名（ランタイム ActorData の直列化名と同じ）
    // ============================================================

    /// <summary>プレハブの参照元パスの欄名。</summary>
    public const string PrefabSourceKey = "prefab_source";

    /// <summary>取り込んだプレハブの版（内容ハッシュ）の欄名。</summary>
    public const string PrefabHashKey = "prefab_hash";

    /// <summary>名前の欄名。</summary>
    public const string NameKey = "name";

    /// <summary>子アクタの配列の欄名。</summary>
    public const string ChildrenKey = "children";

    /// <summary>コンポーネントの配列の欄名。</summary>
    public const string ComponentsKey = "components";

    /// <summary>3D の変換の欄名。</summary>
    private const string TransformKey = "transform";

    /// <summary>2D の変換の欄名。</summary>
    private const string CanvasTransformKey = "canvas_transform";

    /// <summary>アクティブの欄名。</summary>
    private const string ActiveKey = "active";

    /// <summary>表示の欄名。</summary>
    private const string VisibleKey = "visible";

    /// <summary>DFS 番号の欄名（保存物に残っていることがある。展開では持ち越さない）。</summary>
    private const string DfsIdKey = "dfs_id";

    /// <summary>フォルダノードの欄名。</summary>
    private const string IsFolderKey = "is_folder";

    /// <summary>版の欄名（.actor のトップレベルにだけある）。</summary>
    private const string FormatVersionKey = TemplateActorCatalogFormat.FormatVersionKey;

    /// <summary>版の欄が無い .actor の版（版の仕組みより前のファイル = 1 版目）。</summary>
    private const int ImplicitFormatVersion = 1;

    /// <summary>入れ子の差し込みを何段まで辿るか（循環の検出とは別の、暴走の歯止め）。</summary>
    public const int MaxNestedExpansionDepth = 8;

    /// <summary>
    /// 差し込みのときにインスタンス側の値を残す欄（再展開で維持される値と同じ。docs/editor_prefab.md §2）。
    /// 版とプレハブの印も持ち越さない。
    /// </summary>
    private static readonly HashSet<string> InstanceOwnedKeys = new(StringComparer.Ordinal)
    {
        NameKey, TransformKey, CanvasTransformKey, ActiveKey, VisibleKey,
        PrefabSourceKey, PrefabHashKey, DfsIdKey, FormatVersionKey,
    };

    /// <summary>
    /// 差し込みのときに参照先と完全に一致させる欄（参照先に無ければインスタンス側からも消す）。
    /// 種別やフォルダ属性が食い違ったまま残ると、ランタイムの読み込みで木の不変条件が崩れる。
    /// </summary>
    private static readonly string[] StructuralKeys =
        [TemplateActorCatalogFormat.ActorKindKey, IsFolderKey, ComponentsKey, ChildrenKey];

    // ============================================================
    //  公開 API
    // ============================================================

    /// <summary>
    /// テンプレートをまっさらな 1 本の木にする。入力は変更しない。
    /// </summary>
    /// <param name="template">テンプレート（.actor）のルートの JSON。</param>
    /// <param name="loadNested">
    /// 入れ子のプレハブの参照（prefab_source の生の値）から、その .actor のルートを読む関数。
    /// 読めなければ null を返す（ライブラリの外・ファイルが無い等）。
    /// </param>
    /// <returns>まっさらにした木と、展開・除去の件数、警告。</returns>
    public static TemplateActorFlattenResult Flatten(
        JsonObject template, Func<string, JsonObject?> loadNested)
    {
        var root  = (JsonObject)template.DeepClone();
        var state = new WalkState(loadNested, VersionOf(root));

        Visit(root, expandDepth: 0, state);

        return new TemplateActorFlattenResult
        {
            Root          = root,
            ExpandedCount = state.Expanded,
            StrippedCount = state.Stripped,
            Warnings      = state.Warnings,
        };
    }

    /// <summary>ノードが 2D アクタ（actor_kind = Actor2D）かを判定する。</summary>
    /// <param name="node">アクタのノード。</param>
    /// <returns>2D なら true。</returns>
    public static bool IsActor2D(JsonObject node) =>
        node[TemplateActorCatalogFormat.ActorKindKey] is JsonValue v
        && v.TryGetValue<string>(out var kind)
        && string.Equals(kind, TemplateActorCatalogFormat.ActorKind2D, StringComparison.Ordinal);

    /// <summary>
    /// 木のどこかに prefab_source / prefab_hash が残っているかを調べる（検算用）。
    /// </summary>
    /// <param name="node">調べる木のルート。</param>
    /// <returns>1 つでも残っていれば true。</returns>
    public static bool ContainsPrefabLink(JsonObject node)
    {
        if (node.ContainsKey(PrefabSourceKey) || node.ContainsKey(PrefabHashKey)) return true;
        if (node[ChildrenKey] is JsonArray children)
            foreach (var child in children)
                if (child is JsonObject o && ContainsPrefabLink(o)) return true;
        return false;
    }

    // ============================================================
    //  走査
    // ============================================================

    /// <summary>走査中の状態（件数・警告・展開中の参照の並び）。</summary>
    private sealed class WalkState(Func<string, JsonObject?> loadNested, int rootVersion)
    {
        /// <summary>入れ子の参照を読む関数。</summary>
        public Func<string, JsonObject?> LoadNested { get; } = loadNested;

        /// <summary>テンプレートのルートの版。</summary>
        public int RootVersion { get; } = rootVersion;

        /// <summary>いま展開している参照の並び（循環の検出用。大文字小文字は区別しない）。</summary>
        public List<string> ExpandingStack { get; } = [];

        /// <summary>差し込んだノード数。</summary>
        public int Expanded { get; set; }

        /// <summary>印を取り除いたノード数。</summary>
        public int Stripped { get; set; }

        /// <summary>警告。</summary>
        public List<string> Warnings { get; } = [];
    }

    /// <summary>
    /// ノード 1 つを処理し、子へ降りる。
    /// </summary>
    /// <param name="node">処理するノード。</param>
    /// <param name="expandDepth">いま何段の差し込みの内側にいるか。</param>
    /// <param name="state">走査の状態。</param>
    private static void Visit(JsonObject node, int expandDepth, WalkState state)
    {
        // ── 入れ子のプレハブ（参照だけのノード）なら中身を差し込む ─────
        bool pushed = false;
        if (TryGetString(node, PrefabSourceKey, out var source) && IsStub(node))
            pushed = TryExpand(node, source, expandDepth, state);

        // ── プレハブの印を取り除く（まっさらなアクタにする）──────────
        bool hadSource = node.Remove(PrefabSourceKey);
        bool hadHash   = node.Remove(PrefabHashKey);
        if (hadSource || hadHash) state.Stripped++;

        // ── 子へ降りる（差し込んだノードの下は 1 段深い扱い）───────────
        if (node[ChildrenKey] is JsonArray children)
        {
            foreach (var child in children)
                if (child is JsonObject childObj)
                    Visit(childObj, pushed ? expandDepth + 1 : expandDepth, state);
        }

        if (pushed) state.ExpandingStack.RemoveAt(state.ExpandingStack.Count - 1);
    }

    /// <summary>
    /// 参照だけのノードへ参照先の中身を差し込む。
    /// </summary>
    /// <param name="node">参照だけのノード。</param>
    /// <param name="source">prefab_source の値。</param>
    /// <param name="expandDepth">いま何段の差し込みの内側にいるか。</param>
    /// <param name="state">走査の状態。</param>
    /// <returns>差し込んで、展開中の並びへ積んだなら true（呼び出し側が降り終えたら外す）。</returns>
    private static bool TryExpand(JsonObject node, string source, int expandDepth, WalkState state)
    {
        var label = NodeLabel(node);
        if (expandDepth >= MaxNestedExpansionDepth)
        {
            state.Warnings.Add($"入れ子のプレハブが深すぎるので展開しませんでした（{label} → {source}）");
            return false;
        }
        if (state.ExpandingStack.Exists(s => string.Equals(s, source, StringComparison.OrdinalIgnoreCase)))
        {
            state.Warnings.Add($"入れ子のプレハブが循環しているので展開しませんでした（{label} → {source}）");
            return false;
        }

        var nested = state.LoadNested(source);
        if (nested is null)
        {
            state.Warnings.Add($"入れ子のプレハブが見つからないので中身の無いアクタのままです（{label} → {source}）");
            return false;
        }
        if (VersionOf(nested) != state.RootVersion)
        {
            state.Warnings.Add(
                $"入れ子のプレハブの版がテンプレートと違うので展開しませんでした（{label} → {source}）");
            return false;
        }

        ApplyNested(node, nested);
        state.Expanded++;
        state.ExpandingStack.Add(source);
        return true;
    }

    /// <summary>
    /// 参照先の中身をノードへ写す（インスタンス側の値は残す）。
    /// </summary>
    /// <param name="node">写し先（参照だけのノード）。</param>
    /// <param name="nested">参照先の .actor のルート。</param>
    private static void ApplyNested(JsonObject node, JsonObject nested)
    {
        // 種別・フォルダ属性・コンポーネント・子は参照先と完全に一致させる
        foreach (var key in StructuralKeys)
        {
            if (nested[key] is { } value) node[key] = value.DeepClone();
            else node.Remove(key);
        }

        // それ以外の欄は、インスタンスが持つ値（名前・変換・表示）を除いて写す
        foreach (var (key, value) in nested)
        {
            if (InstanceOwnedKeys.Contains(key)) continue;
            if (Array.IndexOf(StructuralKeys, key) >= 0) continue;
            node[key] = value?.DeepClone();
        }

        // 種別が変わって変換の欄が欠けたら、参照先の既定の変換を借りる
        if (!node.ContainsKey(TransformKey) && nested[TransformKey] is { } tf)
            node[TransformKey] = tf.DeepClone();
        if (!node.ContainsKey(CanvasTransformKey) && nested[CanvasTransformKey] is { } ct)
            node[CanvasTransformKey] = ct.DeepClone();
    }

    // ============================================================
    //  小道具
    // ============================================================

    /// <summary>
    /// 参照だけのノード（components も children も無いか空）かを判定する。
    /// 中身が保存されているノードは、保存された内容を正とする（展開しない）。
    /// </summary>
    private static bool IsStub(JsonObject node) =>
        IsMissingOrEmptyArray(node[ComponentsKey]) && IsMissingOrEmptyArray(node[ChildrenKey]);

    /// <summary>欄が無い・null・空配列なら true。</summary>
    private static bool IsMissingOrEmptyArray(JsonNode? value) =>
        value is null || (value is JsonArray a && a.Count == 0);

    /// <summary>文字列の欄を読む（空文字は無い扱い）。</summary>
    private static bool TryGetString(JsonObject node, string key, out string value)
    {
        value = "";
        if (node[key] is not JsonValue v || !v.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s))
            return false;
        value = s;
        return true;
    }

    /// <summary>.actor のルートの版（欄が無ければ 1 版目）。</summary>
    private static int VersionOf(JsonObject root) =>
        root[FormatVersionKey] is JsonValue v && v.TryGetValue<int>(out var version)
            ? version
            : ImplicitFormatVersion;

    /// <summary>警告に出すノードの名前。</summary>
    private static string NodeLabel(JsonObject node) =>
        TryGetString(node, NameKey, out var name) ? name : "(名前なし)";
}
