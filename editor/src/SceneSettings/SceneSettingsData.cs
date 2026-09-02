// ============================================================
//  SceneSettingsData.cs — シーン単位のビューポート／レンダリング設定データ
//
//  .scene ファイルの "settings" 節に保存される設定のエディタ側モデル。
//  ランタイム（runtime/src/engine/core/app_base/scene_settings.rs）の
//  SceneSettingsData と JSON キー名・既定値を厳密に一致させること。
//  キー名を変えるとランタイム側の serde デシリアライズが既定値へ落ちる。
// ============================================================

using System;
using System.IO;
using System.Text.Json.Nodes;

namespace SEEDEditor.SceneSettings;

/// <summary>
/// シーンビュー（Edit モードのデバッグカメラ）の表示・投影設定。
/// カメラの「位置・向き」は .scene の別ノード（debug_camera 節）が持つため、ここには含めない。
/// </summary>
public sealed class DebugCameraSettings
{
    // ── 既定値（マジックナンバー禁止のため名前付き定数で保持する）──

    /// <summary>垂直画角（度）の既定値。</summary>
    public const double DefaultFov = 45.0;
    /// <summary>far クリップ距離の既定値。</summary>
    public const double DefaultFar = 1000.0;
    /// <summary>カメラ移動速度の既定値。</summary>
    public const double DefaultSpeed = 5.0;
    /// <summary>2D（正射投影）モードの既定値。</summary>
    public const bool DefaultOrtho2d = false;

    /// <summary>垂直画角（度）。IPC VIEWPORT_FOV に対応する。</summary>
    public double Fov { get; set; } = DefaultFov;
    /// <summary>far クリップ距離。IPC VIEWPORT_FAR に対応する。</summary>
    public double Far { get; set; } = DefaultFar;
    /// <summary>カメラ移動速度。IPC CAM_SPEED に対応する。</summary>
    public double Speed { get; set; } = DefaultSpeed;
    // グリッド描画 / 軸ギズモ表示（SHOW_GRID / SHOW_AXIS_GIZMO）はこの節に含めない。
    // シーンパネル上部のトグルが持つセッション限りの非永続項目であり、
    // .scene の settings 節にもランタイム側スキーマにも保存しない（起動時は常に ON）。

    /// <summary>2D（正射投影）モードかどうか。IPC EDITOR_CAM_ORTHO に対応する。</summary>
    public bool Ortho2d { get; set; } = DefaultOrtho2d;

    /// <summary>この節を既定値へ戻す。</summary>
    public void ResetToDefault()
    {
        Fov           = DefaultFov;
        Far           = DefaultFar;
        Speed         = DefaultSpeed;
        Ortho2d       = DefaultOrtho2d;
    }

    /// <summary>同じ値を持つ新しいインスタンスを返す（変更前の値を退避する用途）。</summary>
    public DebugCameraSettings Clone() => new()
    {
        Fov = Fov, Far = Far, Speed = Speed, Ortho2d = Ortho2d,
    };

    /// <summary>JSON ノードから値を読み込む（キーが無い項目は現在値を維持する）。</summary>
    public void ReadFrom(JsonObject node)
    {
        Fov           = SceneSettingsJson.ReadDouble(node, "fov",             Fov);
        Far           = SceneSettingsJson.ReadDouble(node, "far",             Far);
        Speed         = SceneSettingsJson.ReadDouble(node, "speed",           Speed);
        Ortho2d       = SceneSettingsJson.ReadBool  (node, "ortho_2d",        Ortho2d);
    }

    /// <summary>ランタイムのスキーマに一致する JsonObject を生成する。</summary>
    public JsonObject ToJson() => new()
    {
        ["fov"]             = Fov,
        ["far"]             = Far,
        ["speed"]           = Speed,
        ["ortho_2d"]        = Ortho2d,
    };
}

/// <summary>
/// レンダリング機能マトリクス（影 / GI / 反射 / AO / 半透明の各方式）。
/// 値はランタイムの RenderFeatures が解釈する小文字モード文字列。
/// RT 非対応 GPU ではランタイム側が自動的に代替方式へ降格する。
/// </summary>
public sealed class RenderFeatureSettings
{
    /// <summary>影の既定方式（シャドウマップ）。</summary>
    public const string DefaultShadow = "shadowmap";
    /// <summary>GI の既定方式（レイトレ DDGI。従来動作の維持）。</summary>
    public const string DefaultGi = "rt";
    /// <summary>反射の既定方式（なし）。</summary>
    public const string DefaultReflection = "off";
    /// <summary>AO の既定方式（なし）。</summary>
    public const string DefaultAo = "off";
    /// <summary>半透明の既定方式（ラスタ）。</summary>
    public const string DefaultTranslucency = "raster";

    /// <summary>影の方式（"shadowmap" / "rt"）。</summary>
    public string Shadow { get; set; } = DefaultShadow;
    /// <summary>GI の方式（"flat" / "ssgi" / "rt"）。</summary>
    public string Gi { get; set; } = DefaultGi;
    /// <summary>反射の方式（"off" / "ssr" / "rt"）。</summary>
    public string Reflection { get; set; } = DefaultReflection;
    /// <summary>AO の方式（"off" / "ssao" / "rt"）。</summary>
    public string Ao { get; set; } = DefaultAo;
    /// <summary>半透明の方式（"raster" / "rt"）。</summary>
    public string Translucency { get; set; } = DefaultTranslucency;

    /// <summary>この節を既定値へ戻す。</summary>
    public void ResetToDefault()
    {
        Shadow       = DefaultShadow;
        Gi           = DefaultGi;
        Reflection   = DefaultReflection;
        Ao           = DefaultAo;
        Translucency = DefaultTranslucency;
    }

    /// <summary>JSON ノードから値を読み込む（キーが無い項目は現在値を維持する）。</summary>
    public void ReadFrom(JsonObject node)
    {
        Shadow       = SceneSettingsJson.ReadString(node, "shadow",       Shadow);
        Gi           = SceneSettingsJson.ReadString(node, "gi",           Gi);
        Reflection   = SceneSettingsJson.ReadString(node, "reflection",   Reflection);
        Ao           = SceneSettingsJson.ReadString(node, "ao",           Ao);
        Translucency = SceneSettingsJson.ReadString(node, "translucency", Translucency);
    }

    /// <summary>ランタイムのスキーマに一致する JsonObject を生成する。</summary>
    public JsonObject ToJson() => new()
    {
        ["shadow"]       = Shadow,
        ["gi"]           = Gi,
        ["reflection"]   = Reflection,
        ["ao"]           = Ao,
        ["translucency"] = Translucency,
    };
}

/// <summary>
/// シーンのレンダリング設定（ポストエフェクト・機能マトリクス・環境光）。
/// キー名は移行元である project_settings.json の既存キーと意図的に同一である。
///
/// なお view_mode（シーンビュー表示モード: Lit / Unlit / G-Buffer デバッグ等）は
/// 本スキーマに含めない。G-Buffer デバッグ表示のままシーンへ保存されると
/// 「シーンを開いたら法線バッファ表示だった」といった事故になるため、
/// セッション限りの非永続設定として MainWindow のフィールドで保持する。
/// </summary>
public sealed class RenderingSettings
{
    // ── 既定値 ────────────────────────────────────────────────

    /// <summary>ブルームの既定値（無効）。</summary>
    public const bool DefaultBloom = false;
    /// <summary>ブルーム強度の既定値。</summary>
    public const double DefaultBloomIntensity = 0.6;
    /// <summary>FXAA の既定値（無効）。</summary>
    public const bool DefaultFxaa = false;
    /// <summary>透明描画方式の既定値（距離ソート）。</summary>
    public const string DefaultTransparency = "sort";
    /// <summary>Deferred レンダリングの既定値（有効）。</summary>
    public const bool DefaultDeferred = true;
    /// <summary>屈折の逐次グラブの既定値（無効。重量オプションのため）。</summary>
    public const bool DefaultRefractSequentialGrab = false;
    /// <summary>GI 強度の既定値。</summary>
    public const double DefaultGiIntensity = 1.0;
    /// <summary>反射強度の既定値。</summary>
    public const double DefaultReflectionIntensity = 1.0;
    /// <summary>AO 強度の既定値。</summary>
    public const double DefaultAoIntensity = 1.0;
    /// <summary>環境光カラーの既定値（白・リニア RGB）。</summary>
    public static readonly float[] DefaultAmbientColor = { 1.0f, 1.0f, 1.0f };
    /// <summary>環境光強度の既定値（従来の見た目を維持する値）。</summary>
    public const double DefaultAmbientIntensity = 0.05;

    /// <summary>ブルーム有効フラグ。</summary>
    public bool Bloom { get; set; } = DefaultBloom;
    /// <summary>ブルーム合成強度。</summary>
    public double BloomIntensity { get; set; } = DefaultBloomIntensity;
    /// <summary>FXAA 有効フラグ。</summary>
    public bool Fxaa { get; set; } = DefaultFxaa;
    /// <summary>透明描画方式（"sort" = 距離ソート / "wboit" = Weighted Blended OIT）。</summary>
    public string Transparency { get; set; } = DefaultTransparency;
    /// <summary>Deferred（G-Buffer）レンダリング有効フラグ。</summary>
    public bool Deferred { get; set; } = DefaultDeferred;
    /// <summary>RT 屈折の逐次グラブ（ガラス越しガラスの多重屈折）。</summary>
    public bool RefractSequentialGrab { get; set; } = DefaultRefractSequentialGrab;
    /// <summary>GI（間接光）の強度倍率。</summary>
    public double GiIntensity { get; set; } = DefaultGiIntensity;
    /// <summary>反射（SSR / RT）の強度倍率。</summary>
    public double ReflectionIntensity { get; set; } = DefaultReflectionIntensity;
    /// <summary>AO（SSAO / RT-AO）の強度倍率。</summary>
    public double AoIntensity { get; set; } = DefaultAoIntensity;
    /// <summary>描画機能マトリクス（影 / GI / 反射 / AO / 半透明）。</summary>
    public RenderFeatureSettings Features { get; set; } = new();
    /// <summary>環境光カラー（リニア RGB）。</summary>
    public float[] AmbientColor { get; set; } = (float[])DefaultAmbientColor.Clone();
    /// <summary>環境光強度（0 で完全な暗闇）。</summary>
    public double AmbientIntensity { get; set; } = DefaultAmbientIntensity;

    /// <summary>この節を既定値へ戻す。</summary>
    public void ResetToDefault()
    {
        Bloom                 = DefaultBloom;
        BloomIntensity        = DefaultBloomIntensity;
        Fxaa                  = DefaultFxaa;
        Transparency          = DefaultTransparency;
        Deferred              = DefaultDeferred;
        RefractSequentialGrab = DefaultRefractSequentialGrab;
        GiIntensity           = DefaultGiIntensity;
        ReflectionIntensity   = DefaultReflectionIntensity;
        AoIntensity           = DefaultAoIntensity;
        Features.ResetToDefault();
        AmbientColor          = (float[])DefaultAmbientColor.Clone();
        AmbientIntensity      = DefaultAmbientIntensity;
    }

    /// <summary>JSON ノードから値を読み込む（キーが無い項目は現在値を維持する）。</summary>
    public void ReadFrom(JsonObject node)
    {
        Bloom                 = SceneSettingsJson.ReadBool  (node, "bloom",                   Bloom);
        BloomIntensity        = SceneSettingsJson.ReadDouble(node, "bloom_intensity",         BloomIntensity);
        Fxaa                  = SceneSettingsJson.ReadBool  (node, "fxaa",                    Fxaa);
        Transparency          = SceneSettingsJson.ReadString(node, "transparency",            Transparency);
        Deferred              = SceneSettingsJson.ReadBool  (node, "deferred",                Deferred);
        RefractSequentialGrab = SceneSettingsJson.ReadBool  (node, "refract_sequential_grab", RefractSequentialGrab);
        GiIntensity           = SceneSettingsJson.ReadDouble(node, "gi_intensity",            GiIntensity);
        ReflectionIntensity   = SceneSettingsJson.ReadDouble(node, "reflection_intensity",    ReflectionIntensity);
        AoIntensity           = SceneSettingsJson.ReadDouble(node, "ao_intensity",            AoIntensity);
        AmbientIntensity      = SceneSettingsJson.ReadDouble(node, "ambient_intensity",       AmbientIntensity);

        if (node["features"] is JsonObject features)
            Features.ReadFrom(features);

        // 環境光カラーは [r, g, b] の配列。要素数が足りない壊れたデータは無視する。
        if (node["ambient_color"] is JsonArray colorArray && colorArray.Count >= AmbientColor.Length)
        {
            var parsed = new float[AmbientColor.Length];
            for (int i = 0; i < parsed.Length; i++)
                parsed[i] = (float)SceneSettingsJson.ToDouble(colorArray[i], AmbientColor[i]);
            AmbientColor = parsed;
        }
    }

    /// <summary>ランタイムのスキーマに一致する JsonObject を生成する。</summary>
    public JsonObject ToJson()
    {
        var color = new JsonArray();
        foreach (var c in AmbientColor) color.Add(c);

        return new JsonObject
        {
            ["bloom"]                   = Bloom,
            ["bloom_intensity"]         = BloomIntensity,
            ["fxaa"]                    = Fxaa,
            ["transparency"]            = Transparency,
            ["deferred"]                = Deferred,
            ["refract_sequential_grab"] = RefractSequentialGrab,
            ["gi_intensity"]            = GiIntensity,
            ["reflection_intensity"]    = ReflectionIntensity,
            ["ao_intensity"]            = AoIntensity,
            ["features"]                = Features.ToJson(),
            ["ambient_color"]           = color,
            ["ambient_intensity"]       = AmbientIntensity,
        };
    }
}

/// <summary>
/// モデル LOD（距離による簡略メッシュ切替）の設定。
/// ランタイムの scene_settings::LodSettings と JSON キー名・既定値を厳密に一致させること。
///
/// Distances[i] 未満の距離が LOD i、最後の要素以上が最終 LOD になる。
/// 既定値はランタイムの旧ハードコード値（10 / 30 / 60）と同一で、
/// この節を持たない旧 .scene は従来と同じ LOD 振り分けになる。
/// </summary>
public sealed class LodSettings
{
    /// <summary>LOD 切替境界の本数（ランタイムの LOD_DISTANCE_COUNT = 段数 4 - 1）。</summary>
    public const int DistanceCount = 3;
    /// <summary>切替距離として許す最小値（ランタイムの LOD_DISTANCE_MIN と一致）。</summary>
    public const double DistanceMin = 0.01;
    /// <summary>切替距離として許す最大値（ランタイムの LOD_DISTANCE_MAX と一致）。</summary>
    public const double DistanceMax = 1.0e6;
    /// <summary>切替距離の既定値（ランタイムの DEFAULT_LOD_DISTANCES と一致）。</summary>
    public static readonly double[] DefaultDistances = { 10.0, 30.0, 60.0 };

    /// <summary>LOD 切替距離（ワールド単位・昇順）。要素数は常に DistanceCount。</summary>
    public double[] Distances { get; set; } = (double[])DefaultDistances.Clone();

    /// <summary>この節を既定値へ戻す。</summary>
    public void ResetToDefault() => Distances = (double[])DefaultDistances.Clone();

    /// <summary>
    /// 現在の距離列が昇順（非減少）かどうか。
    /// 崩れている場合は呼び出し側が警告のうえ <see cref="SortAscending"/> で修復する。
    /// </summary>
    public bool IsAscending()
    {
        for (int i = 1; i < Distances.Length; i++)
            if (Distances[i] < Distances[i - 1]) return false;
        return true;
    }

    /// <summary>距離列を昇順へ並べ替える（昇順バリデーションの自動修復）。</summary>
    public void SortAscending() => Array.Sort(Distances);

    /// <summary>1 要素を範囲内へクランプして設定する。</summary>
    public void SetDistance(int index, double value)
    {
        if (index < 0 || index >= Distances.Length) return;
        Distances[index] = Math.Clamp(value, DistanceMin, DistanceMax);
    }

    /// <summary>JSON ノードから値を読み込む（キーが無い項目は現在値を維持する）。</summary>
    public void ReadFrom(JsonObject node)
    {
        if (node["distances"] is not JsonArray array) return;
        var parsed = (double[])DefaultDistances.Clone();
        for (int i = 0; i < parsed.Length && i < array.Count; i++)
            parsed[i] = Math.Clamp(
                SceneSettingsJson.ToDouble(array[i], parsed[i]), DistanceMin, DistanceMax);
        Distances = parsed;
    }

    /// <summary>ランタイムのスキーマに一致する JsonObject を生成する。</summary>
    public JsonObject ToJson()
    {
        var array = new JsonArray();
        foreach (var d in Distances) array.Add(d);
        return new JsonObject { ["distances"] = array };
    }
}

/// <summary>
/// 編集時物理（Edit モードで物理シミュレーションを走らせる機能）の設定。
/// エディタ専用の設定であり、ランタイムは .scene への保存・復元のみを行う。
/// </summary>
public sealed class PhysicsSettings
{
    /// <summary>編集時物理の既定値（無効）。</summary>
    public const bool DefaultEditPhysics = false;
    /// <summary>編集時物理の RigidBody サブオプションの既定値（無効）。</summary>
    public const bool DefaultEditPhysicsRigidbody = false;

    /// <summary>編集時物理を有効にするか。</summary>
    public bool EditPhysics { get; set; } = DefaultEditPhysics;
    /// <summary>編集時物理で RigidBody（重力・ダイナミクス）を有効にするか。</summary>
    public bool EditPhysicsRigidbody { get; set; } = DefaultEditPhysicsRigidbody;

    /// <summary>この節を既定値へ戻す。</summary>
    public void ResetToDefault()
    {
        EditPhysics          = DefaultEditPhysics;
        EditPhysicsRigidbody = DefaultEditPhysicsRigidbody;
    }

    /// <summary>JSON ノードから値を読み込む（キーが無い項目は現在値を維持する）。</summary>
    public void ReadFrom(JsonObject node)
    {
        EditPhysics          = SceneSettingsJson.ReadBool(node, "edit_physics",           EditPhysics);
        EditPhysicsRigidbody = SceneSettingsJson.ReadBool(node, "edit_physics_rigidbody", EditPhysicsRigidbody);
    }

    /// <summary>ランタイムのスキーマに一致する JsonObject を生成する。</summary>
    public JsonObject ToJson() => new()
    {
        ["edit_physics"]           = EditPhysics,
        ["edit_physics_rigidbody"] = EditPhysicsRigidbody,
    };
}

/// <summary>
/// .scene の "settings" 節ルート。シーンごとのビューポート／レンダリング／編集時物理設定を保持する。
///
/// 読み込みは <see cref="LoadForScene"/> が担当し、
/// ・.scene に settings 節があればそれを採用
/// ・無ければ旧保存先である project_settings.json のルートキーからフォールバック生成
/// する。書き込みはエディタ側では行わず、IPC SET_SCENE_SETTINGS でランタイムへ渡して
/// ランタイムが .scene へ保存する（シーンの保存経路を 1 本に保つため）。
/// </summary>
public sealed class SceneSettingsData
{
    /// <summary>.scene 内でこの設定群が格納されるキー名。</summary>
    public const string SceneSettingsKey = "settings";

    /// <summary>シーンビューのデバッグカメラ設定。</summary>
    public DebugCameraSettings DebugCamera { get; set; } = new();
    /// <summary>レンダリング設定。</summary>
    public RenderingSettings Rendering { get; set; } = new();
    /// <summary>モデル LOD の切替距離設定。</summary>
    public LodSettings Lod { get; set; } = new();
    /// <summary>編集時物理設定。</summary>
    public PhysicsSettings Physics { get; set; } = new();

    // ── 読み込み ──────────────────────────────────────────────

    /// <summary>
    /// 指定シーンのシーン設定をロードする。例外は投げず、失敗時は既定値を返す。
    /// </summary>
    /// <param name="scenePath">.scene ファイルの絶対パス。null / 未保存シーンなら project_settings.json のみを見る。</param>
    /// <param name="projectSettingsPath">project_settings.json の絶対パス（旧シーン互換フォールバック元）。</param>
    public static SceneSettingsData LoadForScene(string? scenePath, string projectSettingsPath)
    {
        var data = new SceneSettingsData();

        // ── 1) .scene の settings 節を最優先で採用する ──
        var sceneRoot = SceneSettingsJson.TryParseFile(scenePath, "LoadForScene(.scene)");
        if (sceneRoot?[SceneSettingsKey] is JsonObject settings)
        {
            data.ReadFrom(settings);
            return data;
        }

        // ── 2) 旧シーン互換: project_settings.json のルートキーから rendering 節を組み立てる ──
        //      debug_camera / physics は旧保存先を持たないため既定値のままとする。
        var projectRoot = SceneSettingsJson.TryParseFile(projectSettingsPath, "LoadForScene(project_settings.json)");
        if (projectRoot is not null)
            data.Rendering.ReadFrom(projectRoot);

        // 旧キー rt_shadows（bool）は features 節が無い旧プロジェクト用の影方式指定。
        // features がある場合は上の ReadFrom で読めているため、無い場合だけ変換する。
        if (projectRoot is not null && projectRoot["features"] is not JsonObject &&
            projectRoot["rt_shadows"] is JsonNode rtShadows)
        {
            data.Rendering.Features.Shadow =
                SceneSettingsJson.ToBool(rtShadows, false) ? "rt" : RenderFeatureSettings.DefaultShadow;
        }

        return data;
    }

    /// <summary>settings 節の JSON ノードから全項目を読み込む。</summary>
    public void ReadFrom(JsonObject node)
    {
        if (node["debug_camera"] is JsonObject debugCamera) DebugCamera.ReadFrom(debugCamera);
        if (node["rendering"]    is JsonObject rendering)   Rendering.ReadFrom(rendering);
        if (node["lod"]          is JsonObject lod)         Lod.ReadFrom(lod);
        if (node["physics"]      is JsonObject physics)     Physics.ReadFrom(physics);
    }

    /// <summary>
    /// 指定シーンの .scene からトップレベルの "shading_asset"（シーン既定シェーディングアセット）を読む。
    /// settings 節ではなくトップレベルに保存されるため、この設定データには含めない。
    /// </summary>
    /// <returns>未設定・読み取り失敗時は null。</returns>
    public static string? LoadSceneShadingAsset(string? scenePath)
    {
        var root = SceneSettingsJson.TryParseFile(scenePath, "LoadSceneShadingAsset");
        var value = root?["shading_asset"]?.GetValue<string?>();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    // ── 書き出し ──────────────────────────────────────────────

    /// <summary>ランタイムのスキーマに一致する JsonObject を生成する（IPC 送信・比較に使う）。</summary>
    public JsonObject ToJson() => new()
    {
        ["debug_camera"] = DebugCamera.ToJson(),
        ["rendering"]    = Rendering.ToJson(),
        ["lod"]          = Lod.ToJson(),
        ["physics"]      = Physics.ToJson(),
    };

    /// <summary>IPC 送信用に改行を含まない圧縮 JSON 文字列を生成する。</summary>
    public string ToCompactJsonString() => ToJson().ToJsonString();
}

/// <summary>
/// JsonNode からの型安全な値取り出しヘルパ。
/// シーン設定は外部ファイル由来のため、型不一致・欠落キーで例外を投げず
/// 既定値へフォールバックする必要がある（壊れた .scene でエディタが落ちないようにする）。
/// </summary>
internal static class SceneSettingsJson
{
    /// <summary>指定パスの JSON ファイルをオブジェクトとしてパースする。失敗時は null（ログのみ）。</summary>
    public static JsonObject? TryParseFile(string? path, string context)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception ex)
        {
            EditorLog.Write($"SceneSettings — {context} のパースに失敗: {ex.Message}");
            return null;
        }
    }

    /// <summary>JsonNode を double として読む（数値以外・null は既定値）。</summary>
    public static double ToDouble(JsonNode? node, double fallback)
    {
        try { return node?.GetValue<double>() ?? fallback; }
        catch { return fallback; }
    }

    /// <summary>JsonNode を bool として読む（bool 以外・null は既定値）。</summary>
    public static bool ToBool(JsonNode? node, bool fallback)
    {
        try { return node?.GetValue<bool>() ?? fallback; }
        catch { return fallback; }
    }

    /// <summary>オブジェクトのキーを double として読む。</summary>
    public static double ReadDouble(JsonObject node, string key, double fallback)
        => ToDouble(node[key], fallback);

    /// <summary>オブジェクトのキーを bool として読む。</summary>
    public static bool ReadBool(JsonObject node, string key, bool fallback)
        => ToBool(node[key], fallback);

    /// <summary>オブジェクトのキーを string として読む（空文字は既定値扱い）。</summary>
    public static string ReadString(JsonObject node, string key, string fallback)
    {
        try
        {
            var value = node[key]?.GetValue<string>();
            return string.IsNullOrEmpty(value) ? fallback : value;
        }
        catch { return fallback; }
    }
}
