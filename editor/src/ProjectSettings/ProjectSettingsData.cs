using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SEEDEditor.Migration;

namespace SEEDEditor.ProjectSettings;

/// <summary>
/// プロジェクト設定のプラグインエントリ。
/// project_settings.json の "plugins" 配列の各要素に対応する。
/// Rust 側の manifest::PluginEntry と同一構造を維持すること。
/// </summary>
public class PluginEntry
{
    /// <summary>プラグイン識別名（plugin.json の name フィールドと一致させること）。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>プラグインが有効化されているかどうか。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// シーンマネージャに登録されたシーンエントリ。
/// project_settings.json の "scenes" 配列の各要素に対応する。
/// スクリプトからは Name を引数に SEED.Scene.Transition("name") で遷移できる。
/// Rust 側（app_init.rs のシーンレジストリ読み込み）と同一構造を維持すること。
/// </summary>
public class SceneEntry
{
    /// <summary>シーン識別名（既定はファイル名の拡張子なし。レジストリ内で一意）。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>シーンファイルの仮想パス（assets://...）。</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;
}

/// <summary>
/// シャドウマップ（CSM）の品質パラメータ一式。
/// project_settings.json の "shadow" ブロックに対応する。
/// ランタイム側 runtime/src/engine/core/renderer/shadow_settings.rs の ShadowQuality と
/// 既定値・値域・JSON キー名を 1 ビットも違わず一致させること
/// （ここでズレると、保存した瞬間にランタイムの見た目が変わってしまう）。
/// features.shadow = "rt"（レイトレ影）のときはランタイムから一切参照されない。
/// </summary>
public class ShadowQualitySettings
{
    // ── 既定値（shadow_settings.rs の DEFAULT_* と同一の値）─────────

    /// <summary>CSM 1 カスケードの解像度の既定値 [px]。</summary>
    public const int DefaultResolution = 2048;

    /// <summary>影を描画する最大距離の既定値 [m]。</summary>
    public const double DefaultDistance = 150.0;

    /// <summary>カスケード分割係数（0=均等, 1=対数）の既定値。</summary>
    public const double DefaultSplitLambda = 0.8;

    /// <summary>法線オフセット [テクセル] の既定値。</summary>
    public const double DefaultNormalOffsetTexels = 1.5;

    /// <summary>定数深度バイアス [テクセル] の既定値。</summary>
    public const double DefaultDepthBiasTexels = 1.0;

    /// <summary>slope-scaled 深度バイアスの既定値。</summary>
    public const double DefaultSlopeBias = 2.0;

    /// <summary>PCF フィルタ半径 [テクセル] の既定値。</summary>
    public const double DefaultPcfRadiusTexels = 1.5;

    /// <summary>PCF タップ数の既定値。</summary>
    public const int DefaultPcfTaps = 12;

    // ── 値域（shadow_settings.rs の sanitize() と同一の範囲）────────
    // ランタイム側でも範囲外は丸められるが、「保存した瞬間に丸められて見た目が変わる」
    // 事態を避けるため、UI 側の保存直前でもこの範囲へクランプする。

    /// <summary>解像度として選択できる値（コンボボックスの選択肢と同一。正方のみ）。</summary>
    public static readonly int[] ResolutionChoices = { 1024, 2048, 4096 };

    /// <summary>影距離の下限 [m]。</summary>
    public const double DistanceMin = 1.0;
    /// <summary>影距離の上限 [m]。</summary>
    public const double DistanceMax = 100000.0;

    /// <summary>カスケード分割係数の下限（0=均等分割）。</summary>
    public const double SplitLambdaMin = 0.0;
    /// <summary>カスケード分割係数の上限（1=対数分割）。</summary>
    public const double SplitLambdaMax = 1.0;

    /// <summary>
    /// 法線オフセット・定数深度バイアス・PCF 半径 [テクセル] に共通の下限。
    /// この 3 つは単位が同じ（ワールド テクセル幅の倍数）なので値域も共通にする。
    /// </summary>
    public const double TexelScaleMin = 0.0;
    /// <summary>
    /// 同上の上限。4 テクセル以上はピーターパン（影の浮き）が誰の目にも分かる領域なので、
    /// 事故防止に頭を押さえる（ランタイム側 TEXEL_SCALE_MAX と同値）。
    /// </summary>
    public const double TexelScaleMax = 8.0;

    /// <summary>slope-scaled バイアスの下限。</summary>
    public const double SlopeBiasMin = 0.0;
    /// <summary>slope-scaled バイアスの上限。</summary>
    public const double SlopeBiasMax = 16.0;

    /// <summary>PCF タップ数の下限。</summary>
    public const int PcfTapsMin = 1;
    /// <summary>PCF タップ数の上限（shadow.wgsl のポアソンディスク表の要素数と一致させること）。</summary>
    public const int PcfTapsMax = 16;

    // ── 値本体 ───────────────────────────────────────────────

    /// <summary>
    /// CSM 1 カスケードあたりの解像度（正方・px）。1024 / 2048 / 4096 のいずれか。
    /// 深度テクスチャの実体サイズに直結するため、変更はランタイムの<b>次回起動から</b>反映される
    /// （実行中に変えても既存のテクスチャ・BindGroup は再確保されない）。
    /// 大きくするほど影の輪郭のギザギザ・アクネが減るが、VRAM 消費と描画コストが増える。
    /// </summary>
    [JsonPropertyName("resolution")]
    public int Resolution { get; set; } = DefaultResolution;

    /// <summary>
    /// 影を描画する最大距離 [m]（カメラの far クリップ距離との小さい方が実際に使われる）。
    /// CSM の全カスケードはこの距離までを分割してカバーするため、これが実質的に
    /// シャドウマップの解像度密度を決める最大の要因になる。小さくするほど手前の影が
    /// 精細になり、大きくすると遠くまで影が届く代わりに手前の影が粗くなる。
    /// </summary>
    [JsonPropertyName("distance")]
    public double Distance { get; set; } = DefaultDistance;

    /// <summary>
    /// カスケード分割の log/uniform ブレンド係数。0=均等分割、1=対数分割。
    /// 大きくする（対数寄りにする）ほど近距離側のカスケードが小さく取られ、
    /// カメラ手前の影が精細になる代わりに遠景側カスケードの負担が増える。
    /// </summary>
    [JsonPropertyName("split_lambda")]
    public double SplitLambda { get; set; } = DefaultSplitLambda;

    /// <summary>
    /// 法線オフセット量 [テクセル]（そのカスケードのワールド テクセル幅の倍数）。
    /// シャドウ空間へ投影する前に幾何法線方向へワールド位置をずらし、深度の量子化誤差
    /// （シャドウアクネ＝縞模様）を防ぐ。大きくするとアクネは消えるが、薄い物体の影が
    /// 本体から浮いて見える「ピーターパン」現象が目立つようになる。
    /// </summary>
    [JsonPropertyName("normal_offset")]
    public double NormalOffsetTexels { get; set; } = DefaultNormalOffsetTexels;

    /// <summary>
    /// 定数深度バイアス [テクセル]（そのカスケードのワールド テクセル幅の倍数）。
    /// 法線オフセットだけでは吸収しきれない残差（法線がほぼ光源方向を向く面の量子化誤差）
    /// を潰すための最終保険。大きくするとアクネは消えるが、法線オフセットと同様に
    /// ピーターパンが強まる。
    /// </summary>
    [JsonPropertyName("depth_bias")]
    public double DepthBiasTexels { get; set; } = DefaultDepthBiasTexels;

    /// <summary>
    /// ラスタライザの slope-scaled 深度バイアス（シャドウ深度パスの書き込み時に適用）。
    /// 面が光源方向に対して傾くほど強く掛かる、法線オフセットとは別経路のバイアス。
    /// 両方揃って初めて全ての角度のシャドウアクネを抑えられる。
    /// </summary>
    [JsonPropertyName("slope_bias")]
    public double SlopeBias { get; set; } = DefaultSlopeBias;

    /// <summary>
    /// PCF（Percentage Closer Filtering）のフィルタ半径 [テクセル]。
    /// この半径の円板にポアソンディスクでタップを撒いて影の輪郭を柔らかくする。
    /// 大きくするほど輪郭が滑らかになる代わりに、接地部の影が薄く（光漏れ気味に）見える。
    /// </summary>
    [JsonPropertyName("pcf_radius_texels")]
    public double PcfRadiusTexels { get; set; } = DefaultPcfRadiusTexels;

    /// <summary>
    /// PCF のタップ数（1〜16）。多いほど影の輪郭が滑らかになるが、シャドウマップの
    /// サンプリング回数が増えるためピクセルシェーダの負荷が上がる。
    /// </summary>
    [JsonPropertyName("pcf_taps")]
    public int PcfTaps { get; set; } = DefaultPcfTaps;
}

/// <summary>
/// プロジェクト全体の設定データ。
/// {assetsPath}/project_settings.json に JSON 形式で永続化される。
/// 今後カテゴリが増えるたびにプロパティを追加していく。
/// </summary>
public class ProjectSettingsData
{
    // ── 版 ───────────────────────────────────────────────────

    /// <summary>
    /// 版の欄を JSON の**先頭**へ出すための並び順。
    /// 既定（属性なし）の並び順は 0 なので、それより小さい値にすれば必ず先頭に来る。
    /// 先頭に置くのは、差分を見たときに版がすぐ分かるようにするため
    /// （docs/asset_migration.md 6.5 (1)）。
    /// </summary>
    private const int FORMAT_VERSION_PROPERTY_ORDER = -1;

    /// <summary>
    /// アセット形式のバージョン。保存時は常に現行版
    /// （<see cref="AssetFormats.ProjectSettings"/> の表）を書く。
    ///
    /// <para>
    /// 欄が無い古いファイルは 1 版として読まれる（ランタイムと同じ規約）。
    /// 値をここへ直書きせず表から取ることで、版を上げたときに直す場所を 1 か所に保つ。
    /// </para>
    /// </summary>
    [JsonPropertyName(AssetFormats.FORMAT_VERSION_KEY)]
    [JsonPropertyOrder(FORMAT_VERSION_PROPERTY_ORDER)]
    public int FormatVersion { get; set; } = AssetFormats.ProjectSettings.CurrentVersion;

    // ── 必須設定 ─────────────────────────────────────────────

    /// <summary>ゲームの名前（パッケージフォルダ名・ウィンドウタイトルなどに使用）。</summary>
    [JsonPropertyName("game_name")]
    public string GameName { get; set; } = "MyGame";

    /// <summary>ゲーム起動時に最初にロードするシーンの仮想パス（assets://...）。</summary>
    [JsonPropertyName("start_scene")]
    public string StartScene { get; set; } = string.Empty;

    // ── グラフィックス設定 ────────────────────────────────────

    /// <summary>ゲームウィンドウの初期解像度（横・物理ピクセル）。Play・パッケージ版で使用。</summary>
    [JsonPropertyName("window_width")]
    public int WindowWidth { get; set; } = 1920;

    /// <summary>ゲームウィンドウの初期解像度（縦・物理ピクセル）。Play・パッケージ版で使用。</summary>
    [JsonPropertyName("window_height")]
    public int WindowHeight { get; set; } = 1080;

    /// <summary>
    /// ゲーム画面の描画解像度の決め方。
    /// "window"（既定）= ウィンドウの実ピクセルで描画する（従来動作）。
    /// "fixed"        = 常に window_width × window_height の内部解像度で描画し、
    ///                  最終出力でウィンドウへアスペクト比を保ったまま拡大縮小する（余白は黒帯）。
    /// Rust 側 `runtime/src/engine/core/app_base/app/render_resolution.rs` の
    /// RenderResolutionMode と文字列表現を一致させること。
    /// </summary>
    [JsonPropertyName("render_resolution_mode")]
    public string RenderResolutionMode { get; set; } = "window";

    /// <summary>
    /// 目標フレームレート（フレーム／秒）。0 = 無制限。
    ///
    /// <para>
    /// Play・パッケージ版では、フォーカスの有無にかかわらずフレーム間隔を
    /// 1/target_fps 以上に保つ（＝上限を掛けて CPU・GPU の空回りを止める）。
    /// エディタ埋め込みのシーンビューには影響しない。
    /// </para>
    /// <para>
    /// Rust 側 <c>runtime/src/engine/core/app_base/app/frame_pacing.rs</c> の
    /// DEFAULT_TARGET_FPS / TARGET_FPS_MIN / TARGET_FPS_MAX と値域を一致させること。
    /// </para>
    /// </summary>
    [JsonPropertyName("target_fps")]
    public int TargetFps { get; set; } = 60;

    /// <summary>
    /// 垂直同期（VSync）の切り替え。
    /// "auto"（既定）= エディタ埋め込みなら VSync なし（DWM に任せる）、
    ///                 単体ウィンドウ（パッケージ版・別ウィンドウ Play）なら VSync あり。
    /// "on"          = 常に VSync あり（Fifo）。
    /// "off"         = 常に VSync なし（Mailbox / Immediate）。
    /// Rust 側 <c>runtime/src/engine/core/renderer/present_mode.rs</c> の
    /// VsyncMode と文字列表現を一致させること。
    /// </summary>
    [JsonPropertyName("vsync")]
    public string Vsync { get; set; } = "auto";

    /// <summary>
    /// 画面の向き（モバイル＝Android の APK だけに効く）。
    /// "both"（既定）= 縦横どちらも（端末の向きに追従）、"portrait" = 縦に固定、"landscape" = 横に固定。
    /// APK を作るときにマニフェストの screenOrientation へ焼き込まれる（値と表示名は
    /// <see cref="ScreenOrientationSetting"/>、マニフェストの値への変換表は runtime/android/app/build.gradle.kts）。
    /// デスクトップの実行には影響しない。
    /// </summary>
    [JsonPropertyName("screen_orientation")]
    public string ScreenOrientation { get; set; } = ScreenOrientationSetting.Default;

    // ── シーンマネージャ ─────────────────────────────────────

    /// <summary>
    /// シーンマネージャに登録されたシーン一覧。
    /// スクリプトの SEED.Scene.Load / Transition が名前で参照する。
    /// </summary>
    [JsonPropertyName("scenes")]
    public List<SceneEntry> Scenes { get; set; } = new();

    // ── プラグイン設定 ────────────────────────────────────────

    /// <summary>
    /// インポート済みプラグインの有効/無効リスト。
    /// リストに存在しないプラグインはデフォルトで有効（Rust 側と同一ルール）。
    /// </summary>
    [JsonPropertyName("plugins")]
    public List<PluginEntry> Plugins { get; set; } = new();

    // ── グラフィックス設定 ────────────────────────────────────

    /// <summary>
    /// インラインレイトレ影の有効フラグ（RT対応GPUのみ効果あり）。
    /// ランタイム起動時に App.rt_shadows へ反映され、エディタからは IPC の
    /// RT_SHADOWS:1 / RT_SHADOWS:0 でライブ切替できる。
    /// </summary>
    [JsonPropertyName("rt_shadows")]
    public bool RtShadows { get; set; } = false;

    /// <summary>
    /// シャドウマップ（CSM）の品質設定。features.shadow = "shadowmap"（既定）のときに
    /// ランタイムが参照する（"rt" のときは参照されない）。
    /// project_settings.json の "shadow" ブロックに対応する。
    /// </summary>
    [JsonPropertyName("shadow")]
    public ShadowQualitySettings Shadow { get; set; } = new();

    /// <summary>
    /// このクラスがモデル化していない未知キーを保持するバケット。
    /// ビューポートツールバーが書き込むグラフィックスキー（features / bloom / gi_intensity など）や
    /// ランタイム専用キー（ambient_* / post_vignette 等）は ProjectSettingsData に無いため、
    /// これが無いと ProjectSettingsWindow の保存（強い型付きシリアライズ）でそれらが消えてしまう。
    /// JsonExtensionData で LoadFrom→SaveTo の往復に未知キーを保全する。
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtraData { get; set; } = new();

    // ── オーディオ設定（将来実装） ──────────────────────────────
    // ── 物理設定（将来実装） ────────────────────────────────────
    // ── 入力設定（将来実装） ────────────────────────────────────
    // ── ビルド設定（将来実装） ──────────────────────────────────
    // ── タグ＆レイヤー設定（将来実装） ──────────────────────────

    // ── 永続化 ──────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// 読み込めなかったファイルの代わりに作られた既定値か。
    ///
    /// <para>
    /// 未来版（新しいエンジンで保存された）・変換失敗のときに真になる。
    /// **真のまま保存してはいけない**（既定値でプロジェクト設定を丸ごと上書きしてしまう）。
    /// 呼び出し側は編集させずに閉じること。
    /// </para>
    /// </summary>
    [JsonIgnore]
    public bool IsUnreadable { get; private set; }

    /// <summary>
    /// 指定パスから設定ファイルをロードする。
    ///
    /// <para>
    /// 古い形式ならランタイムの変換を通してから解釈する（メモリ上だけ。ファイルは書き換えない）。
    /// ファイルが存在しない場合や JSON パースに失敗した場合はデフォルト値を返す。
    /// 未来版・変換失敗のときは <see cref="IsUnreadable"/> を立てた既定値を返す。
    /// </para>
    /// </summary>
    /// <param name="path">読み込む project_settings.json の絶対パス。</param>
    public static ProjectSettingsData LoadFrom(string path)
    {
        var read = AssetMigrationGateway.ReadFile(path, AssetFormats.ProjectSettings);
        if (read.Status == AssetReadStatus.Missing) return new ProjectSettingsData();
        if (read.IsBlocked) return new ProjectSettingsData { IsUnreadable = true };

        try
        {
            var data = JsonSerializer.Deserialize<ProjectSettingsData>(read.Text)
                       ?? new ProjectSettingsData();
            // 変換済みのテキストを読んだので、ここでは現行版を名乗ってよい。
            data.FormatVersion = AssetFormats.ProjectSettings.CurrentVersion;
            return data;
        }
        catch
        {
            // ファイルが破損していた場合はデフォルト値で継続する
            return new ProjectSettingsData();
        }
    }

    /// <summary>
    /// 現在の設定を指定パスに JSON として保存する（常に現行版を先頭へ刻む）。
    ///
    /// <para>
    /// 書き込みは <see cref="SEEDEditor.Assets.SafeFileWriter"/> 経由の原子的置換
    /// （旧版を .backup/ へ退避 → .tmp へ書き切って rename）で行う。
    /// project_settings.json はアセットルート直下にあるので、
    /// バックアップの基準もそのフォルダ（＝アセットルート）でよい。
    /// </para>
    /// </summary>
    /// <param name="path">保存先の絶対パス。</param>
    public void SaveTo(string path)
    {
        FormatVersion = AssetFormats.ProjectSettings.CurrentVersion;
        var json = JsonSerializer.Serialize(this, JsonOptions);
        SEEDEditor.Assets.SafeFileWriter.WriteAllTextAtomic(
            path, json, Path.GetDirectoryName(Path.GetFullPath(path)));
    }
}
