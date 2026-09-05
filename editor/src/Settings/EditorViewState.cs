// ============================================================
//  EditorViewState.cs — 「シーンごとの見た目の状態」をエディタ再起動をまたいで保持するストア
//
//  【何を持つか】
//    ・Hierarchy パネルのツリー展開状態（展開されているノードの安定キー集合）
//    ・シーンパネル上部のトグル（World/Local・軸・ボーン・グリッド）の ON/OFF
//
//  【なぜ .scene ではなくエディタ側に持つのか】
//    どちらも「どう見えているか」であってシーンの内容ではない。.scene へ書くと
//    トグルを触るだけでシーンがダーティになり、チーム作業では無意味な差分になる。
//    そのため editor/settings/ 配下のエディタ専用 JSON へ逃がす。
//
//  【なぜシーン単位か】
//    ツリーの展開状態はシーンのアクタ構成に強く結び付いており、別シーンへ持ち越すと
//    無関係なノードが開く。トグルも「このシーンではグリッドを消して作業する」といった
//    使い分けがあるため、同じくシーン単位で覚える。
//
//  【保存タイミング】
//    変更のたびに即書き込みするとツリー操作中にファイル I/O が頻発するため、
//    RequestSave() でデバウンス（最後の変更から一定時間後に 1 回）する。
//    エディタ終了時は Flush() で確実に書き出す。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace SEEDEditor.Settings;

/// <summary>
/// シーンパネル上部トグルの ON/OFF 状態（シーン単位で保存する）。
/// 既定値は各トグルの従来初期状態（すべて ON、ギズモ座標系は World）に合わせる。
/// </summary>
public sealed class ToolbarViewState
{
    /// <summary>ギズモ座標系が World（ワールド軸）なら true。false は Local。</summary>
    [JsonPropertyName("world")]
    public bool World { get; set; } = true;

    /// <summary>画面隅の XYZ 軸ガイドを表示するなら true。</summary>
    [JsonPropertyName("axis")]
    public bool Axis { get; set; } = true;

    /// <summary>選択中スキンスプライトのボーンを表示するなら true。</summary>
    [JsonPropertyName("bone")]
    public bool Bone { get; set; } = true;

    /// <summary>地面グリッドを表示するなら true。</summary>
    [JsonPropertyName("grid")]
    public bool Grid { get; set; } = true;
}

/// <summary>
/// 1 シーン分のビュー状態。JSON の "scenes" 辞書の値に対応する。
/// </summary>
public sealed class SceneViewState
{
    /// <summary>
    /// Hierarchy で「展開されている」ノードの安定キー一覧。
    /// ここに無いノードは折りたたみ扱い（＝未知のノードは閉じた状態で現れる）。
    /// </summary>
    [JsonPropertyName("hierarchyExpanded")]
    public List<string> HierarchyExpanded { get; set; } = new();

    /// <summary>
    /// トグルの状態。null は「このシーンではまだ一度も保存していない」＝既定値を使う、の意味。
    /// </summary>
    [JsonPropertyName("toolbar")]
    public ToolbarViewState? Toolbar { get; set; }
}

/// <summary>
/// view_state.json のルート。シーンキー → そのシーンのビュー状態。
/// </summary>
public sealed class EditorViewStateDocument
{
    /// <summary>シーンキー（正規化した .scene の絶対パス）→ ビュー状態。</summary>
    [JsonPropertyName("scenes")]
    public Dictionary<string, SceneViewState> Scenes { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// <see cref="EditorViewStateDocument"/> の読み書きを担う静的ストア。
/// 起動時に <see cref="Init"/>、変更時に <see cref="RequestSave"/>、終了時に <see cref="Flush"/> を呼ぶ。
/// </summary>
public static class EditorViewState
{
    // ── 定数 ──────────────────────────────────────────────────

    /// <summary>設定ディレクトリ内のファイル名。</summary>
    private const string FileName = "view_state.json";

    /// <summary>
    /// 保存デバウンス時間（ms）。ツリーを連続開閉しても書き込みは最後の 1 回だけにする。
    /// </summary>
    private const int SaveDebounceMs = 500;

    // ── 状態 ──────────────────────────────────────────────────

    /// <summary>読み込み済みの内容（Init 前は空）。</summary>
    private static EditorViewStateDocument _document = new();

    /// <summary>保存先の絶対パス（Init で決定。未 Init なら null＝保存しない）。</summary>
    private static string? _filePath;

    /// <summary>デバウンス用タイマ（初回の RequestSave で UI スレッド上に作る）。</summary>
    private static DispatcherTimer? _saveTimer;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // ── 初期化・保存 ──────────────────────────────────────────

    /// <summary>設定ディレクトリを指定して読み込む（起動時に一度呼ぶ）。</summary>
    /// <param name="settingsDir">editor/settings の絶対パス。</param>
    public static void Init(string settingsDir)
    {
        _filePath = Path.Combine(settingsDir, FileName);
        try
        {
            if (File.Exists(_filePath))
            {
                var loaded = JsonSerializer.Deserialize<EditorViewStateDocument>(
                    File.ReadAllText(_filePath), JsonOpts);
                if (loaded is not null) _document = loaded;
            }
        }
        catch
        {
            // 壊れたファイルは空状態で継続する（次回保存で上書きされる）
            _document = new EditorViewStateDocument();
        }
        _document.Scenes ??= new Dictionary<string, SceneViewState>(StringComparer.Ordinal);
    }

    /// <summary>保存を予約する（デバウンス）。UI スレッドから呼ぶこと。</summary>
    public static void RequestSave()
    {
        if (_filePath is null) return;
        if (_saveTimer is null)
        {
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SaveDebounceMs) };
            _saveTimer.Tick += (_, _) => Flush();
        }
        // Stop→Start で「最後の変更から SaveDebounceMs 後」に倒す
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>予約中の保存を打ち切って即座に書き出す（エディタ終了時など）。</summary>
    public static void Flush()
    {
        _saveTimer?.Stop();
        if (_filePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_document, JsonOpts));
        }
        catch (Exception ex)
        {
            EditorLog.Write($"ビュー状態の保存に失敗しました: {ex.Message}");
        }
    }

    // ── シーンキー ────────────────────────────────────────────

    /// <summary>
    /// シーンファイルパスから保存キーを作る。
    /// 大文字小文字・相対表記の揺れで別エントリにならないよう、絶対パス化して小文字に揃える。
    /// </summary>
    /// <param name="scenePath">.scene のパス。null / 空（未保存の新規シーン）なら null を返す。</param>
    /// <returns>保存キー。保存対象外なら null。</returns>
    public static string? MakeSceneKey(string? scenePath)
    {
        if (string.IsNullOrWhiteSpace(scenePath)) return null;
        try
        {
            return Path.GetFullPath(scenePath).Replace('\\', '/').ToLowerInvariant();
        }
        catch
        {
            // 不正なパス文字などで正規化できない場合は素の文字列で妥協する
            return scenePath.Replace('\\', '/').ToLowerInvariant();
        }
    }

    // ── シーンエントリの取得 ──────────────────────────────────

    /// <summary>
    /// 保存済みのシーンエントリを返す。無ければ null（＝既定値を使うべき、の意味）。
    /// </summary>
    public static SceneViewState? TryGetScene(string? sceneKey)
        => sceneKey is not null && _document.Scenes.TryGetValue(sceneKey, out var s) ? s : null;

    /// <summary>
    /// 書き込み用にシーンエントリを取得する（無ければ作る）。
    /// sceneKey が null（未保存シーン）のときは null を返し、呼び出し側は保存を諦める。
    /// </summary>
    public static SceneViewState? GetOrCreateScene(string? sceneKey)
    {
        if (sceneKey is null) return null;
        if (!_document.Scenes.TryGetValue(sceneKey, out var s))
        {
            s = new SceneViewState();
            _document.Scenes[sceneKey] = s;
        }
        return s;
    }
}
