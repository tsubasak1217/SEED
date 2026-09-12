using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SEEDEditor;

/// <summary>
/// エディタ全体の環境設定（特定パネルに属さない操作系の好み）。
/// editor/settings/editor_preferences.json に永続化される。
///
/// 起動時に MainWindow が <see cref="Init"/> を呼んで読み込み、
/// 以降は <see cref="Instance"/> から参照する。設定 UI からの変更は
/// Instance を書き換えて <see cref="Save"/> を呼ぶと即座に反映・保存される。
/// </summary>
public sealed class EditorPreferences
{
    // ── 設定値の範囲（クランプ用）─────────────────────────────
    /// <summary>タッチパッドスクロール係数の最小値。</summary>
    public const double ScrollScaleMin = 0.05;
    /// <summary>タッチパッドスクロール係数の最大値。</summary>
    public const double ScrollScaleMax = 5.0;

    // ── 設定値 ────────────────────────────────────────────────

    /// <summary>
    /// タッチパッド（精密スクロール）の縦・横スクロール係数（1.0 = 標準）。
    /// 小さくするほどスクロールが弱く（ゆっくりに）なる。物理ホイールには影響しない。
    /// </summary>
    [JsonPropertyName("touchpad_scroll_scale")]
    public double TouchpadScrollScale { get; set; } = 1.0;

    /// <summary>
    /// ヒエラルキーの選択アクターの所属（ワールド/ビューポート）に応じて、
    /// シーンタブ（ワールド/ビューポート）を自動で切り替えるかどうか。既定はオン。
    /// </summary>
    [JsonPropertyName("scene_tab_auto_switch")]
    public bool SceneTabAutoSwitch { get; set; } = true;

    /// <summary>
    /// Play 実行時に別プロセスのウィンドウを出してプレイするかどうか。
    /// 既定は false（＝埋め込みインプレース Play。別プロセスを起動せず、シーンパネルの
    /// Edit ランタイムをその場で Play 化するため即座に再生できる）。
    /// true にすると従来の別ウィンドウ Play（別プロセス）になる。
    /// UI 上の「ウィンドウを出してプレイ」チェックボックスと 1 対 1 に対応する。
    /// </summary>
    [JsonPropertyName("window_play")]
    public bool WindowPlay { get; set; } = false;

    /// <summary>
    /// Play 実行中もシェーディングアセット（.wgsl）のホットリロードを行うかどうか。既定はオン。
    ///
    /// オンのとき、Play 中に .wgsl を保存すると約 1 秒以内に再コンパイルされて反映される
    /// （保存したフレームだけパイプライン再構築のヒッチが出るが、再生を止めずに画作りを
    /// 詰められる）。あわせて Play 中も WGSL のライブ検証（赤下線）が有効になる。
    /// オフにすると従来どおり Play 開始時点のパイプラインを使い続け、検証も送らない。
    ///
    /// ランタイムへは <c>SET_PLAY_SHADER_HOT_RELOAD:{0|1}</c> で同期する。
    /// UI 上の「Play中もシェーダをホットリロード」チェックボックスと 1 対 1 に対応する。
    /// </summary>
    [JsonPropertyName("play_shader_hot_reload")]
    public bool PlayShaderHotReload { get; set; } = true;

    /// <summary>
    /// アセットルート配下の .cs が変更されたときに、自動でスクリプトを
    /// 再コンパイル・ホットリロードするかどうか。既定はオン。
    ///
    /// オンのとき、内蔵スクリプトエディタでの保存だけでなく、VS Code などの
    /// 外部エディタでの保存も FileSystemWatcher が検出して自動反映する
    /// （Unity と同じ「保存したら反映」の体験）。送信前にエディタ側で全 .cs を
    /// 一括コンパイル検証し、エラーがある間は送信しない（＝ランタイムは
    /// 直前の正常アセンブリのまま動き続ける）。
    ///
    /// オフにすると従来どおり、内蔵スクリプトエディタの保存時のみ再読込する。
    /// UI 上の「表示 > スクリプト > スクリプトを自動再読込」と 1 対 1 に対応する。
    /// </summary>
    [JsonPropertyName("auto_reload_scripts")]
    public bool AutoReloadScripts { get; set; } = true;

    /// <summary>
    /// Play 実行中に .cs の変更を検出したとき、その場でホットリロードするかどうか。既定はオフ。
    ///
    /// オフ（既定）のとき、Play 中の変更は保留され、Play を止めた直後に 1 回だけ反映される。
    /// これは Play 中のホットリロードがランタイム側で**全スクリプトインスタンスの作り直し**を
    /// 意味するためで、次の実害が出る:
    ///   - OnStart が再実行され、チュートリアルなどの進行が最初へ戻る
    ///   - OnStart で Instantiate している補助アクタ（ポーズメニュー・リザルト・
    ///     ビートアイコン等）が二重生成され、フレーム時間が伸びる
    ///   - static シングルトンが古いエンティティハンドルを持ち続け、一部が表示されなくなる
    ///
    /// オンにすると、上記を承知のうえで Play を止めずにスクリプトを反映できる
    /// （挙動だけを詰めたい場合向け）。シーンの自動再読込はこの設定の対象外で、
    /// Play 中は常に保留される（ワールドごと作り直すため）。
    /// UI 上の「表示 > スクリプト > Play 中もスクリプトを即時反映する」と 1 対 1 に対応する。
    /// </summary>
    [JsonPropertyName("play_script_hot_reload")]
    public bool PlayScriptHotReload { get; set; } = false;

    /// <summary>
    /// 今開いている .scene ファイルがディスク上で変更されたときに、
    /// エディタが自動でシーンを読み直すかどうか。既定はオン。
    ///
    /// 外部ツール・別プロセス・手動編集による変更を検出して、
    /// 「ファイルを開いたとき」と同じ経路で読み直す。
    /// エディタ自身の保存（SAVE_SCENE）は自己書き込みとして除外する。
    ///
    /// 未保存の編集があるときは（破棄になるため）読み直さず通知だけ行い、
    /// Play 中は Play 終了後に読み直す。
    /// UI 上の「表示 > シーン > シーンを自動再読込」と 1 対 1 に対応する。
    /// </summary>
    [JsonPropertyName("auto_reload_scene")]
    public bool AutoReloadScene { get; set; } = true;

    /// <summary>
    /// プレハブ（.actor / .actor2d）をアクタータブで保存したときに、
    /// 今開いているシーン内の同じプレハブのインスタンスへ自動で反映するかどうか。既定はオン。
    ///
    /// オンのとき、保存成功（SAVE_OK）に続けて IPC <c>PREFAB_REAPPLY_PATH</c> を送り、
    /// そのプレハブを参照するインスタンスだけを再展開する。反映は Undo 1 操作で戻せ、
    /// 反映件数はトーストで必ず知らせる。
    ///
    /// オフにすると従来どおり手動更新（ヒエラルキー右クリックの「プレハブから更新」／
    /// 「シーン内の全プレハブを更新」）だけになる。インスタンスごとに手を入れて
    /// 使い分けている場合は、その変更が上書きされないようオフにする。
    /// UI 上の「表示 > シーン > プレハブ保存時にシーンのインスタンスへ自動反映」と 1 対 1 に対応する。
    /// </summary>
    [JsonPropertyName("prefab_auto_propagate_on_save")]
    public bool PrefabAutoPropagateOnSave { get; set; } = true;

    /// <summary>
    /// ロジック配置ダイアログで最後に使ったパターン指定。
    ///
    /// 「円形に 12 個」「5×5 グリッド」といった指定は同じ設定を続けて使うことが多く、
    /// 毎回入れ直させるのは操作コストが高い。次に開いたときの初期値として復元する。
    /// null（＝一度も使っていない）なら既定値で開く。
    /// </summary>
    [JsonPropertyName("logic_placement")]
    public Placement.Patterns.PlacementSpec? LogicPlacement { get; set; }

    /// <summary>
    /// ロジック配置ダイアログの「地形に接地させる」の前回値。
    ///
    /// パターン指定（<see cref="LogicPlacement"/>）とは別に持つ。接地はパターンの
    /// 一部ではなく「置き方」の設定であり、ランタイムへも spec とは別フィールドで送るため。
    /// </summary>
    [JsonPropertyName("logic_placement_ground")]
    public bool LogicPlacementGround { get; set; }

    /// <summary>
    /// プロジェクトパネルで「隠しファイル」を表示するかどうか。既定はオフ。
    ///
    /// <para>
    /// 隠す対象は editor/config/project_panel_rules.json のルール
    /// （<see cref="SEEDEditor.Assets.ProjectPanelVisibilityRules"/>）で決まる。
    /// エディタ・OS・外部ツールの作業ファイル（.backup / *.lock / Thumbs.db …）と、
    /// エンジンが生成する地形の中間データ（.tvox / .tscatter / .tcover）が対象。
    /// </para>
    /// <para>
    /// オンにすると全ファイルが見えるようになり、隠し対象は薄く表示される
    /// （＝「普段は触らないもの」だと分かる）。ファイル操作そのものは制限しない。
    /// UI 上はプロジェクトパネルのツールバーの「隠しファイル」トグルと 1 対 1 に対応する。
    /// </para>
    /// </summary>
    [JsonPropertyName("show_hidden_project_files")]
    public bool ShowHiddenProjectFiles { get; set; } = false;

    /// <summary>
    /// エディタが起動するランタイム（SEED.exe）のビルド構成 id。
    ///
    /// <para>
    /// 値は editor/config/runtime_build_configs.json の "configs[].id"
    /// （"debug" / "develop" / "release"）。null＝未選択で、カタログの
    /// "default"（既定は develop）が使われる。
    /// </para>
    /// <para>
    /// カタログから消えた id が残っていても落ちないよう、参照側は必ず
    /// <see cref="Runtime.BuildConfig.RuntimeBuildConfigCatalog.Resolve"/> を
    /// 通して解決する（未知なら既定へ丸められる）。
    /// UI 上はツールバーの Play ボタン隣のコンボボックスと 1 対 1 に対応する。
    /// </para>
    /// </summary>
    [JsonPropertyName("runtime_build_config_id")]
    public string? RuntimeBuildConfigId { get; set; }

    // ── シングルトン・永続化 ──────────────────────────────────

    /// <summary>読み込み済みの環境設定（Init 前は既定値）。</summary>
    public static EditorPreferences Instance { get; private set; } = new();

    /// <summary>設定ファイルの絶対パス（Init で決定）。</summary>
    private static string? _filePath;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>設定ディレクトリを指定して環境設定を読み込む（起動時に一度呼ぶ）。</summary>
    public static void Init(string settingsDir)
    {
        _filePath = Path.Combine(settingsDir, "editor_preferences.json");
        try
        {
            if (File.Exists(_filePath))
            {
                var loaded = JsonSerializer.Deserialize<EditorPreferences>(File.ReadAllText(_filePath), JsonOpts);
                if (loaded is not null) Instance = loaded;
            }
        }
        catch
        {
            // 壊れたファイルは既定値で継続する（次回保存で上書きされる）
            Instance = new EditorPreferences();
        }
        // 範囲外の値（手編集等）をクランプして正規化する
        Instance.TouchpadScrollScale = Math.Clamp(Instance.TouchpadScrollScale, ScrollScaleMin, ScrollScaleMax);
    }

    /// <summary>現在の設定をファイルへ保存する。</summary>
    public static void Save()
    {
        if (_filePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(Instance, JsonOpts));
        }
        catch (Exception ex)
        {
            EditorLog.Write($"環境設定の保存に失敗: {ex.Message}");
        }
    }
}
