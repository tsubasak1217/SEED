// ============================================================
//  AssetCollector.cs — 参照グラフによる収録ファイルの決定
//
//  【役割】
//  「起点（project_settings.json のシーン）から辿れるアセットだけを集める」処理。
//  アセットルート配下を無条件に全部詰める従来方式を置き換える。
//
//  【アルゴリズム】
//  1. ディスク上の全ファイルを 1 度だけ列挙してインデックス化する
//     （以降の実在チェックはハッシュ参照だけで済み、I/O が起きない）。
//  2. 起点を積む: project_settings.json / start_scene / scenes[].path /
//     ランタイムに焼き込まれた assets:// パス / 追加フォルダ / 常時同梱拡張子 /
//     除外ルールに当たらない全 .cs（走査専用。同梱はしない。下記【型参照対策】参照）。
//  3. ワークリスト方式で閉包を取る。テキスト系ファイルは中身を走査して
//     参照を取り出し（AssetReferenceScanner）、実在するものをキューへ積む。
//  4. 収録が決まったファイルには「暗黙の同伴ファイル」を足す
//     （.tvox → .tscatter / .tcover / terrain_meta.json など）。
//
//  【除外の位置づけ】
//  除外ルールは「未参照ファイルの掃除」であって、参照より優先しない。
//  参照されていれば除外設定に当たっていても同梱し、警告として報告する。
//
//  【2 つの入口】
//  ・Collect()     … パッケージ化用。上記の既定の起点（project_settings.json ほか）から辿る。
//  ・CollectFrom() … 任意の起点から辿る汎用版。呼び出し側が渡したパスだけを起点にし、
//                    project_settings.json も登録シーンもスクリプト全走査も行わない。
//                    テンプレートライブラリのインポート（editor/src/Templates/）が使う。
//                    閉包の探索・同伴ファイル・欠落検出は Collect() と完全に同じ処理を通る。
//
//  【型参照対策（.cs の走査専用起点）】
//  参照グラフは「パス文字列で参照されたファイルだけ」を辿るため、
//  C# の**型名**だけで使われるスクリプト（例: ファクトリの `new MoveMission()`、
//  静的クラスの `FishCatalog.ForLevel(...)`）は永遠に到達できない。
//  アセット配下の全 .cs は SEEDUserScripts.dll へ一括コンパイルされる方式
//  （どの .cs の文字列リテラルも実行時に使われ得る）なので、除外ルールに
//  当たらない全 .cs を「走査専用」の起点として積み、中の assets:// 参照だけを拾う
//  （AddScriptScanSeeds）。.cs 自体は同梱しない — 収録集合に足すのは
//  EnqueueScan ではなく Include を呼んだときだけ。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SEEDEditor.Packaging.Collect;

/// <summary>
/// アセットルートを走査し、参照グラフに基づいて PAK へ収録するファイルを決定する。
/// ファイルの書き出しは行わない（決定だけを担う）。
/// </summary>
public sealed class AssetCollector
{
    // ── 入力 ─────────────────────────────────────────────────

    /// <summary>アセットルートの絶対パス。</summary>
    private readonly string _assetsRoot;

    /// <summary>収録ルールのユーザー設定。</summary>
    private readonly AssetPackagingSettings _settings;

    /// <summary>
    /// ランタイムのソースルート（runtime/src）。
    /// エンジンに焼き込まれた assets:// パスを起点に加えるために走査する。null なら省略。
    /// </summary>
    private readonly string? _runtimeSourceRoot;

    /// <summary>進行状況の出力先（null なら出力しない）。</summary>
    private readonly Action<string>? _log;

    // ── ディスク上のインデックス ─────────────────────────────

    /// <summary>アセットルート相対パス → バイトサイズ。</summary>
    private readonly Dictionary<string, long> _filesOnDisk;

    /// <summary>アセットルート相対のフォルダパス集合（フォルダ参照の解決に使う）。</summary>
    private readonly HashSet<string> _dirsOnDisk;

    // ── 収集途中の状態 ───────────────────────────────────────

    /// <summary>収録が決まったファイル（相対パス）。</summary>
    private readonly HashSet<string> _included;

    /// <summary>参照抽出待ちのファイル（相対パス）。</summary>
    private readonly Queue<string> _scanQueue;

    /// <summary>
    /// 参照抽出の走査キューへ積み済みのファイル（多重登録防止用）。
    /// 収録集合（<see cref="_included"/>）とは別に持つ。「同梱される」と「走査される」は
    /// 別の概念で、型参照対策（<see cref="AddScriptScanSeeds"/>）が積む .cs は
    /// 走査はするが同梱はしないため、1 つの集合で両方を兼ねると意味が壊れる。
    /// </summary>
    private readonly HashSet<string> _scanQueued;

    /// <summary>丸ごと取り込み済みのフォルダ（同じフォルダを何度も舐めないため）。</summary>
    private readonly HashSet<string> _expandedFolders;

    /// <summary>参照されたが実体が無かったパス。</summary>
    private readonly List<MissingReference> _missing;

    /// <summary>欠落の重複報告を防ぐキー集合（参照先 + 参照元）。</summary>
    private readonly HashSet<string> _missingKeys;

    /// <summary>ランタイム側の Rust ソースから assets:// パスを拾う正規表現。</summary>
    private static readonly Regex RustAssetPathRegex =
        new("\"(assets://[^\"]*)\"", RegexOptions.Compiled);

    /// <summary>
    /// 拡張子の切れ目（ドット + 英数字）を見つける正規表現。
    /// 参照文字列の末尾に文章が続いている場合の切り詰め位置を探すのに使う。
    /// </summary>
    private static readonly Regex ExtensionBoundaryRegex =
        new("\\.[A-Za-z0-9]{1,10}", RegexOptions.Compiled);

    // ============================================================
    //  構築
    // ============================================================

    /// <summary>
    /// コレクタを構築し、アセットルート配下のファイル一覧を作る。
    /// </summary>
    /// <param name="assetsRoot">アセットルートの絶対パス。</param>
    /// <param name="settings">収録ルールのユーザー設定。</param>
    /// <param name="runtimeSourceRoot">runtime/src の絶対パス（省略可）。</param>
    /// <param name="log">進行状況の出力先（省略可）。</param>
    public AssetCollector(
        string assetsRoot,
        AssetPackagingSettings settings,
        string? runtimeSourceRoot = null,
        Action<string>? log = null)
    {
        _assetsRoot        = Path.GetFullPath(assetsRoot);
        _settings          = settings;
        _runtimeSourceRoot = runtimeSourceRoot;
        _log               = log;

        _filesOnDisk     = new Dictionary<string, long>(AssetPathUtil.PathComparer);
        _dirsOnDisk      = new HashSet<string>(AssetPathUtil.PathComparer);
        _included        = new HashSet<string>(AssetPathUtil.PathComparer);
        _scanQueue       = new Queue<string>();
        _scanQueued      = new HashSet<string>(AssetPathUtil.PathComparer);
        _expandedFolders = new HashSet<string>(AssetPathUtil.PathComparer);
        _missing         = [];
        _missingKeys     = new HashSet<string>(AssetPathUtil.PathComparer);

        BuildDiskIndex();
    }

    /// <summary>アセットルート配下の全ファイル・全フォルダを 1 度だけ列挙して索引を作る。</summary>
    private void BuildDiskIndex()
    {
        var rootInfo = new DirectoryInfo(_assetsRoot);
        if (!rootInfo.Exists) return;

        foreach (var fi in rootInfo.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var rel = AssetPathUtil.ToRelative(_assetsRoot, fi.FullName);
            if (rel is null || rel.Length == 0) continue;
            _filesOnDisk[rel] = fi.Length;

            // 親フォルダを順に登録する（フォルダ参照 assets://terrain/Scene1 の解決用）
            var dir = AssetPathUtil.GetDirectory(rel);
            while (dir.Length > 0 && _dirsOnDisk.Add(dir))
                dir = AssetPathUtil.GetDirectory(dir);
        }
    }

    // ============================================================
    //  収集
    // ============================================================

    /// <summary>参照グラフを辿って収録ファイルを決定する。</summary>
    /// <returns>収録一覧・欠落一覧・除外統計を含む結果。</returns>
    public AssetCollectionResult Collect()
    {
        var missingScenes = new List<string>();

        // ── 全ファイル同梱モード（従来の挙動） ──────────────────
        if (_settings.IncludeAllFiles)
        {
            long allBytes = _filesOnDisk.Values.Sum();
            Log($"収録モード: 全ファイル同梱（参照解決なし） {_filesOnDisk.Count} 件");
            var all = _filesOnDisk
                .Select(kv => new CollectedAsset(kv.Key, kv.Value))
                .OrderBy(a => a.RelPath, AssetPathUtil.PathComparer)
                .ToList();
            return new AssetCollectionResult
            {
                Included       = all,
                IncludedBytes  = allBytes,
                TotalFileCount = _filesOnDisk.Count,
                TotalBytes     = allBytes,
            };
        }

        // ── 起点を積む ─────────────────────────────────────────
        AddSeeds(missingScenes);

        // ── 閉包を取って結果にする ─────────────────────────────
        return RunClosureAndBuildResult(missingScenes);
    }

    /// <summary>
    /// 起点として渡されたパスが実在しなかったときに、欠落報告の「参照元」欄へ入れるラベル。
    /// 実ファイルの相対パスと取り違えようがない形にしてある。
    /// </summary>
    public const string SeedSourceLabel = "(指定された起点)";

    /// <summary>
    /// 呼び出し側が指定した起点だけから参照の閉包を取る汎用版。
    ///
    /// <para>
    /// <see cref="Collect"/> と違い、<c>project_settings.json</c>・登録シーン・
    /// ランタイム内蔵参照・追加同梱フォルダ・常時同梱拡張子・全 .cs の走査といった
    /// 「パッケージ化のための既定の起点」（<see cref="AddSeeds"/>）を **一切積まない**。
    /// 渡されたパスから辿れるものだけが結果に入る。
    /// </para>
    /// <para>
    /// 用途はテンプレートライブラリのインポート（editor/src/Templates/）。
    /// ライブラリはフォルダ構成がアセットルートと同じ形（<c>shaders/toon.wgsl</c> が
    /// プロジェクトの <c>assets/shaders/toon.wgsl</c> になる）なので、
    /// ライブラリのルートをアセットルートとみなしてこのメソッドを呼べば、
    /// 「選んだテンプレートが必要とするライブラリ内ファイル一式」がそのまま求まる。
    /// </para>
    /// <para>
    /// <see cref="AssetPackagingSettings.IncludeAllFiles"/> は参照しない
    /// （「起点から辿る」ことがこのメソッドの存在理由なので、全件モードには意味が無い）。
    /// </para>
    /// </summary>
    /// <param name="seedRelPaths">
    /// 起点のルート相対パス。ファイルならそれ自身を、フォルダならその配下の全ファイルを起点にする。
    /// 実在しないパスは欠落参照（参照元 = <see cref="SeedSourceLabel"/>）として報告する。
    /// </param>
    /// <returns>収録一覧・欠落一覧・除外統計を含む結果。</returns>
    public AssetCollectionResult CollectFrom(IEnumerable<string> seedRelPaths)
    {
        // ── 指定された起点を積む（ファイル / フォルダのどちらでも受ける） ──
        int seedCount = 0;
        foreach (var raw in seedRelPaths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var rel = AssetPathUtil.NormalizeRelative(raw);
            if (rel.Length == 0) continue;

            if (_filesOnDisk.ContainsKey(rel))      { Include(rel);        seedCount++; continue; }
            if (_dirsOnDisk.Contains(rel))          { IncludeFolder(rel);  seedCount++; continue; }

            // 実在しない起点は黙って捨てず欠落として残す（呼び出し側の指定ミスを可視化する）
            AddMissing(rel, raw, SeedSourceLabel);
            Log($"⚠ 起点の実体がありません（スキップ）: {rel}");
        }
        Log($"指定された起点: {seedCount} 件");

        // ── 閉包を取って結果にする（Collect と同じ処理を通る） ──
        return RunClosureAndBuildResult(missingScenes: []);
    }

    /// <summary>
    /// 積まれた起点から参照の閉包を取り、結果オブジェクトを組み立てる。
    /// <see cref="Collect"/> と <see cref="CollectFrom"/> の共通後半部分。
    /// </summary>
    /// <param name="missingScenes">実体の無い登録シーン（<see cref="CollectFrom"/> では常に空）。</param>
    /// <returns>収録一覧・欠落一覧・除外統計を含む結果。</returns>
    private AssetCollectionResult RunClosureAndBuildResult(List<string> missingScenes)
    {
        // ── 閉包を取る ─────────────────────────────────────────
        int scanned = 0;
        while (_scanQueue.Count > 0)
        {
            var rel = _scanQueue.Dequeue();
            ScanFile(rel);
            scanned++;
        }
        Log($"参照走査: {scanned} ファイルを解析");

        // ── 結果を組み立てる ───────────────────────────────────
        var included = _included
            .Select(rel => new CollectedAsset(rel, _filesOnDisk.TryGetValue(rel, out var s) ? s : 0))
            .OrderBy(a => a.RelPath, AssetPathUtil.PathComparer)
            .ToList();

        // 除外ルールに当たっているのに参照されたため入ったもの（設定見直しの材料）
        var despite = included
            .Select(a => a.RelPath)
            .Where(IsExcludedByRule)
            .OrderBy(p => p, AssetPathUtil.PathComparer)
            .ToList();

        return new AssetCollectionResult
        {
            Included                = included,
            IncludedBytes           = included.Sum(a => a.SizeBytes),
            TotalFileCount          = _filesOnDisk.Count,
            TotalBytes              = _filesOnDisk.Values.Sum(),
            MissingReferences       = _missing,
            IncludedDespiteExclusion = despite,
            MissingScenes           = missingScenes,
        };
    }

    // ============================================================
    //  起点
    // ============================================================

    /// <summary>参照グラフの起点となるファイルをキューへ積む。</summary>
    /// <param name="missingScenes">実体の無いシーン登録を書き戻す先。</param>
    private void AddSeeds(List<string> missingScenes)
    {
        // ── 1. project_settings.json 本体（ランタイムが必ず読む） ──
        const string projectSettingsName = "project_settings.json";
        if (_filesOnDisk.ContainsKey(projectSettingsName))
            Include(projectSettingsName);
        else
            Log($"⚠ {projectSettingsName} が見つかりません（シーン起点を取得できません）");

        // ── 2. start_scene と scenes[].path ────────────────────
        foreach (var scenePath in ReadSceneEntries(projectSettingsName))
        {
            var rel = ToRelativeReference(scenePath);
            if (rel.Length == 0) continue;
            if (_filesOnDisk.ContainsKey(rel)) { Include(rel); continue; }

            missingScenes.Add(scenePath);
            Log($"⚠ 登録シーンの実体がありません（スキップ）: {scenePath}");
        }

        // ── 3. ランタイムに焼き込まれた assets:// パス ──────────
        int builtin = 0;
        foreach (var rel in ReadRuntimeBuiltinReferences())
        {
            if (!_filesOnDisk.ContainsKey(rel)) continue;   // テスト用ダミーは実在しないので自然に落ちる
            if (_included.Contains(rel)) continue;
            Include(rel);
            builtin++;
        }
        if (builtin > 0) Log($"エンジン内蔵参照: {builtin} ファイルを追加");

        // ── 4. 追加同梱フォルダ（参照グラフで辿れないものの逃げ道） ──
        foreach (var folder in _settings.AdditionalFolders)
        {
            var dir = AssetPathUtil.NormalizeRelative(folder);
            if (dir.Length == 0) continue;
            int before = _included.Count;
            IncludeFolder(dir);
            Log($"追加同梱フォルダ: {dir} → {_included.Count - before} ファイル");
        }

        // ── 5. 常時同梱拡張子（除外ルールには従う） ──────────────
        var alwaysExts = new HashSet<string>(
            _settings.AlwaysIncludedExtensions
                .Select(PackagingRules.NormalizeExtension)
                .Where(e => e.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        if (alwaysExts.Count > 0)
        {
            int added = 0;
            foreach (var rel in _filesOnDisk.Keys.ToList())
            {
                if (!alwaysExts.Contains(AssetPathUtil.GetExtensionLower(rel))) continue;
                if (IsExcludedByRule(rel)) continue;
                if (_included.Contains(rel)) continue;
                Include(rel);
                added++;
            }
            if (added > 0)
                Log($"常時同梱拡張子（{string.Join(" ", alwaysExts)}）: {added} ファイルを追加");
        }

        // ── 6. 型参照対策（走査専用の起点） ──────────────────────
        AddScriptScanSeeds();
    }

    /// <summary>C# スクリプトの拡張子。走査専用の起点判定に使う。</summary>
    private const string ScriptExtension = ".cs";

    /// <summary>
    /// 除外ルールに当たらない全 .cs を「走査専用」の起点として積む。
    ///
    /// <para>
    /// 【なぜ要るか】アセット配下の全 .cs は <c>SEEDUserScripts.dll</c> へ一括コンパイルされる
    /// 方式なので、どの .cs の文字列リテラルも実行時に使われ得る。ところが C# の**型名**だけで
    /// 参照されるスクリプト（例: ファクトリの <c>new MoveMission()</c>、
    /// 静的クラスの <c>FishCatalog.ForLevel(...)</c>）はパスの参照グラフに一切現れない。
    /// 通常の閉包探索（<see cref="ScanFile"/> → <see cref="Resolve"/>）は
    /// 「パス文字列で参照されたファイルだけ」を辿るので、辿り着く手段が無いスクリプトは
    /// 中に書かれた assets:// 参照（自動生成された画像パスなど）ごと永遠に見つからず、
    /// パッケージ版でだけ読み込みに失敗する（実機でしか気付けない）。
    /// </para>
    /// <para>
    /// 【同梱はしない】ここで積むのは走査だけ。<see cref="EnqueueScan"/> は
    /// <see cref="Include"/> と違って収録集合（<see cref="_included"/>）に触らないので、
    /// .cs 自体が PAK に入ることはない。スクリプトのソースは事前コンパイル済み DLL
    /// （<c>ScriptPackager</c>）で配るので、PAK に入れる必要が無い
    /// （既定の常時同梱拡張子を空にした理由と同じ。<see cref="PackagingRules.DefaultAlwaysIncludedExtensions"/>）。
    /// パスで実際に参照されている .cs は、これとは別に通常の閉包経由で今も同梱される
    /// （docs/packaging.md §8 の既知の制限。型参照対策とは独立した挙動）。
    /// </para>
    /// <para>
    /// 【除外フォルダは走査もしない】<c>templates</c> のような「使われないスクリプト置き場」の
    /// .cs まで無条件に走査すると、サンプルコードに書いた assets:// 参照だけでそのフォルダの
    /// 資産が丸ごと収録される事故になる。除外ルール（フォルダ・拡張子・ファイル名。
    /// <see cref="IsExcludedByRule"/>）に当たる .cs は起点から外す。ただし「除外は参照より弱い」
    /// 原則は変えないので、そういう .cs が実際にパスで参照されていれば
    /// 通常の閉包経由で従来どおり同梱・走査される。
    /// </para>
    /// </summary>
    private void AddScriptScanSeeds()
    {
        int added = 0;
        foreach (var rel in _filesOnDisk.Keys.ToList())
        {
            if (!string.Equals(AssetPathUtil.GetExtensionLower(rel), ScriptExtension, StringComparison.OrdinalIgnoreCase))
                continue;
            if (IsExcludedByRule(rel)) continue;    // 使われないスクリプト置き場（templates 等）は走査しない
            if (_included.Contains(rel)) continue;  // 既に通常の経路で走査予約済み（二重走査防止）

            EnqueueScan(rel, ScriptExtension);
            added++;
        }
        if (added > 0) Log($"スクリプト走査（型参照対策）: {added} ファイル");
    }

    /// <summary>project_settings.json から start_scene と scenes[].path を読み出す。</summary>
    /// <param name="projectSettingsRel">project_settings.json のルート相対パス。</param>
    /// <returns>シーンパス文字列の一覧（重複あり得る）。</returns>
    private List<string> ReadSceneEntries(string projectSettingsRel)
    {
        var list = new List<string>();
        var abs = AssetPathUtil.ToAbsolute(_assetsRoot, projectSettingsRel);
        if (!File.Exists(abs)) return list;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(abs));
            var root = doc.RootElement;

            if (root.TryGetProperty("start_scene", out var start) &&
                start.ValueKind == JsonValueKind.String)
            {
                var s = start.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }

            if (root.TryGetProperty("scenes", out var scenes) &&
                scenes.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in scenes.EnumerateArray())
                {
                    if (!e.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String)
                        continue;
                    var s = p.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"⚠ project_settings.json の解析に失敗: {ex.Message}");
        }
        return list;
    }

    /// <summary>
    /// runtime/src の Rust ソースに書かれた assets:// パスを列挙する。
    /// エンジンが内蔵で読むアセット（terrain/layers.json など）を取りこぼさないため。
    /// 実在チェックは呼び出し側で行う（テスト用ダミーはここで落ちる）。
    /// </summary>
    /// <returns>アセットルート相対パスの一覧。</returns>
    private IEnumerable<string> ReadRuntimeBuiltinReferences()
    {
        if (string.IsNullOrEmpty(_runtimeSourceRoot) || !Directory.Exists(_runtimeSourceRoot))
            yield break;

        foreach (var rs in Directory.EnumerateFiles(_runtimeSourceRoot, "*.rs", SearchOption.AllDirectories))
        {
            string text;
            try { text = File.ReadAllText(rs); }
            catch { continue; }

            foreach (Match m in RustAssetPathRegex.Matches(text))
            {
                var rel = AssetPathUtil.NormalizeRelative(
                    m.Groups[1].Value[AssetPathUtil.AssetsScheme.Length..]);
                if (rel.Length > 0) yield return rel;
            }
        }
    }

    /// <summary>
    /// 設定ファイルに書かれた参照文字列（assets:// / 絶対パス / ルート相対）を
    /// アセットルート相対パスへ揃える。ルート外の絶対パスは空文字を返す。
    /// </summary>
    /// <param name="reference">project_settings.json などに書かれた参照文字列。</param>
    /// <returns>ルート相対パス（解決できなければ空文字）。</returns>
    private string ToRelativeReference(string reference)
    {
        var s = reference.Trim();
        if (s.Length == 0) return "";

        // ① 仮想パス
        if (s.StartsWith(AssetPathUtil.AssetsScheme, StringComparison.OrdinalIgnoreCase))
            return AssetPathUtil.NormalizeRelative(s[AssetPathUtil.AssetsScheme.Length..]);

        // ② 絶対パス（アセットルート外なら収録対象外）
        if (Path.IsPathRooted(s))
            return AssetPathUtil.ToRelative(_assetsRoot, s) ?? "";

        // ③ ルート相対
        return AssetPathUtil.NormalizeRelative(s);
    }

    // ============================================================
    //  グラフ探索
    // ============================================================

    /// <summary>ファイル 1 本を読んで参照を抽出し、解決できたものをキューへ積む。</summary>
    /// <param name="rel">走査対象のルート相対パス。</param>
    private void ScanFile(string rel)
    {
        var abs = AssetPathUtil.ToAbsolute(_assetsRoot, rel);
        string text;
        try
        {
            // 巨大ファイルの正規表現走査で固まらないようにサイズで足切りする
            if (_filesOnDisk.TryGetValue(rel, out var size) &&
                size > PackagingRules.MaxScanFileSizeBytes)
            {
                Log($"⚠ 参照走査をスキップ（サイズ上限超過）: {rel}");
                return;
            }
            text = File.ReadAllText(abs);
        }
        catch (Exception ex)
        {
            Log($"⚠ 読み込み失敗（参照を辿れません）: {rel} — {ex.Message}");
            return;
        }

        foreach (var candidate in AssetReferenceScanner.Scan(text, rel, _assetsRoot))
            Resolve(candidate, rel);
    }

    /// <summary>参照候補を実ファイル / フォルダへ解決し、収録集合へ足す。</summary>
    /// <param name="candidate">解決する参照候補。</param>
    /// <param name="sourceRel">参照元ファイルのルート相対パス。</param>
    private void Resolve(AssetReferenceCandidate candidate, string sourceRel)
    {
        // ① ファイルとして実在するか（候補の順に試す）
        foreach (var cand in candidate.Candidates)
        {
            if (!_filesOnDisk.ContainsKey(cand)) continue;
            Include(cand);
            return;
        }

        // 推測の参照は外れても黙って捨てる（誤検出を警告にしない）
        if (!candidate.IsExplicit) return;

        // ② 拡張子の切れ目まで詰めた前方部分が実在するなら、それを参照とみなす。
        //    コメント中の "assets://a/b.txt）から読む。" のように、
        //    終端文字が現れないまま日本語の文章が続くケースを救う。
        foreach (var cand in candidate.Candidates)
        {
            var trimmed = TrimToExistingFile(cand);
            if (trimmed is null) continue;
            Include(trimmed);
            return;
        }

        // ③ 拡張子の無い確実な参照はフォルダ参照とみなす
        //    （assets://terrain/Scene1 のような地形フォルダ参照、
        //      およびスクリプトが文字列連結で組み立てるパスの前半部分）
        foreach (var cand in candidate.Candidates)
        {
            if (AssetPathUtil.GetExtensionLower(cand).Length != 0) continue;
            if (!_dirsOnDisk.Contains(cand)) continue;
            IncludeFolder(cand);
            return;
        }

        // ④ 拡張子らしい末尾を持たない参照は「連結の断片」「文章の一部」とみなし、欠落報告しない
        var primary = candidate.Candidates[0];
        if (!AssetPathUtil.IsLikelyExtension(AssetPathUtil.GetExtensionLower(primary))) return;

        // ⑤ 拡張子付きなのに実体が無い = 本物の欠落
        AddMissing(primary, candidate.Raw, sourceRel);
    }

    /// <summary>
    /// 欠落参照を 1 件記録する（同じ「参照先 + 参照元」の重複報告は落とす）。
    /// </summary>
    /// <param name="referencePath">解決を試みたルート相対パス。</param>
    /// <param name="rawText">元テキストに書かれていた生の文字列。</param>
    /// <param name="sourceRel">参照元ファイルのルート相対パス（起点指定なら <see cref="SeedSourceLabel"/>）。</param>
    private void AddMissing(string referencePath, string rawText, string sourceRel)
    {
        var key = referencePath + "|" + sourceRel;
        if (!_missingKeys.Add(key)) return;
        _missing.Add(new MissingReference(referencePath, rawText, sourceRel));
    }

    /// <summary>
    /// 参照文字列の末尾に余計な文章が続いている場合に備え、
    /// 拡張子の切れ目で切り詰めた前方部分のうち実在するものを探す。
    /// </summary>
    /// <param name="candidate">解決できなかった参照候補（ルート相対）。</param>
    /// <returns>実在する切り詰め結果。見つからなければ null。</returns>
    private string? TrimToExistingFile(string candidate)
    {
        var matches = ExtensionBoundaryRegex.Matches(candidate);
        // 長い前方部分（＝より右にある拡張子）から順に試し、最長一致を採用する
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            var end = matches[i].Index + matches[i].Length;
            if (end == candidate.Length) continue;      // 切り詰め不要な形はここへ来ない
            var prefix = candidate[..end];
            if (_filesOnDisk.ContainsKey(prefix)) return prefix;
        }
        return null;
    }

    /// <summary>ファイルを収録集合へ足し、同伴ファイルとテキスト走査を予約する。</summary>
    /// <param name="rel">ルート相対パス。</param>
    private void Include(string rel)
    {
        if (!_filesOnDisk.ContainsKey(rel)) return;
        if (IsNeverIncluded(rel)) return;   // 配布物に入れてはいけないもの（参照より優先する唯一の規則）
        if (!_included.Add(rel)) return;

        var ext = AssetPathUtil.GetExtensionLower(rel);
        EnqueueScan(rel, ext);
        AddCompanions(rel, ext);
    }

    /// <summary>
    /// テキスト系ファイルを参照抽出の走査キューへ積む（<see cref="_scanQueued"/> で多重登録を防ぐ）。
    ///
    /// <para>
    /// 収録するかどうか（<see cref="_included"/>）とは独立に「走査を予約したか」だけを管理する。
    /// これにより、<see cref="AddScriptScanSeeds"/> が「走査専用」で積んだ .cs が
    /// あとから通常の参照経由で <see cref="Include"/> されても（またはその逆の順でも）、
    /// 同じファイルを 2 回走査することがない。
    /// </para>
    /// </summary>
    /// <param name="rel">走査対象候補のルート相対パス。</param>
    /// <param name="ext">そのファイルの小文字拡張子（呼び出し側で算出済みのものを渡す）。</param>
    private void EnqueueScan(string rel, string ext)
    {
        if (!PackagingRules.ScannableExtensions.Contains(ext)) return;
        if (!_scanQueued.Add(rel)) return;
        _scanQueue.Enqueue(rel);
    }

    /// <summary>
    /// 参照グラフには現れない同伴ファイルを足す。
    /// ランタイムが「拡張子を差し替えて隣を読む」種類の依存を取りこぼさないため。
    /// </summary>
    /// <param name="rel">収録が決まったファイルのルート相対パス。</param>
    /// <param name="ext">そのファイルの小文字拡張子。</param>
    private void AddCompanions(string rel, string ext)
    {
        // ① 同名・別拡張子の兄弟（.tvox → .tscatter / .tcover など）
        if (PackagingRules.SiblingExtensions.TryGetValue(ext, out var siblingExts))
        {
            var stem = rel[..^ext.Length];
            foreach (var se in siblingExts)
            {
                var sibling = stem + se;
                if (_filesOnDisk.ContainsKey(sibling)) Include(sibling);
            }
        }

        // ② 同じフォルダの付随ファイル（.tvox → terrain_meta.json）
        if (PackagingRules.FolderCompanions.TryGetValue(ext, out var companionNames))
        {
            var dir = AssetPathUtil.GetDirectory(rel);
            foreach (var name in companionNames)
            {
                var companion = dir.Length > 0 ? dir + "/" + name : name;
                if (_filesOnDisk.ContainsKey(companion)) Include(companion);
            }
        }
    }

    /// <summary>フォルダ配下のファイルを丸ごと収録集合へ足す。</summary>
    /// <param name="dirRel">フォルダのルート相対パス。</param>
    private void IncludeFolder(string dirRel)
    {
        if (!_expandedFolders.Add(dirRel)) return;

        var prefix = dirRel + "/";
        foreach (var rel in _filesOnDisk.Keys.ToList())
        {
            if (!rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            Include(rel);
        }
    }

    // ============================================================
    //  除外判定
    // ============================================================

    /// <summary>
    /// 除外ルールに当たるかを判定する。
    /// 参照されたファイルはこの判定に関わらず同梱される（除外は掃除用）。
    /// </summary>
    /// <param name="rel">ルート相対パス。</param>
    /// <returns>除外ルールに一致すれば true。</returns>
    public bool IsExcludedByRule(string rel)
    {
        var name = AssetPathUtil.GetFileName(rel);
        foreach (var pattern in _settings.ExcludedFileNames)
            if (PackagingRules.WildcardMatch(pattern.Trim(), name)) return true;

        var ext = AssetPathUtil.GetExtensionLower(rel);
        if (ext.Length > 0)
        {
            foreach (var excluded in _settings.ExcludedExtensions)
                if (string.Equals(PackagingRules.NormalizeExtension(excluded), ext,
                                  StringComparison.OrdinalIgnoreCase)) return true;
        }

        var dir = AssetPathUtil.GetDirectory(rel);
        if (dir.Length > 0)
        {
            foreach (var segment in dir.Split('/'))
                foreach (var pattern in _settings.ExcludedFolders)
                    if (PackagingRules.WildcardMatch(pattern.Trim(), segment)) return true;
        }

        return false;
    }

    /// <summary>
    /// 参照されていても決して同梱しないファイルかを判定する。
    /// 除外ルール（掃除用）と違い、これだけは参照より優先する。
    /// </summary>
    /// <param name="rel">ルート相対パス。</param>
    /// <returns>同梱禁止なら true。</returns>
    public static bool IsNeverIncluded(string rel)
    {
        foreach (var never in PackagingRules.NeverIncludedRelativePaths)
            if (AssetPathUtil.PathComparer.Equals(never, rel)) return true;
        return false;
    }

    // ============================================================
    //  ログ
    // ============================================================

    /// <summary>進行状況を出力する（出力先が無ければ何もしない）。</summary>
    /// <param name="message">出力する 1 行。</param>
    private void Log(string message) => _log?.Invoke(message);
}
