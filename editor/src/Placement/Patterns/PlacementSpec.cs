using System;
using System.Text.Json.Serialization;

namespace SEEDEditor.Placement.Patterns;

/// <summary>
/// 配置パターンの種別。
///
/// JSON 表現は<b>名前そのままの文字列</b>（"Circle" 等）で、Rust 側
/// <c>PlacementPattern</c> の serde 表現と一致させてある。
/// </summary>
public enum PlacementPattern
{
    /// <summary>円形・円弧（中心から半径 r・角度範囲 span に等間隔）。</summary>
    Circle,
    /// <summary>グリッド（行 × 列 × 段）。</summary>
    Grid,
    /// <summary>直線（方向角と間隔で等間隔）。</summary>
    Line,
    /// <summary>ランダム散布（拒否サンプリングで最小間隔を保証する）。</summary>
    Random,
}

/// <summary>
/// 生成された配置点 1 個（基準点を原点とするローカル座標）。
///
/// 座標系はパターン共通で <b>XZ 平面 + Y は段</b>。2D 配置では (X, Z) を
/// キャンバスの (X, Y) へ写す（写す側はランタイム）。
/// </summary>
public struct PlacementPoint
{
    /// <summary>位置（基準点相対）。</summary>
    public float X, Y, Z;
    /// <summary>ヨー（Y 軸回り・度）。生成器はヨーだけを設定する。</summary>
    public float Yaw;
    /// <summary>拡縮倍率（均一）。</summary>
    public float Scale;

    /// <summary>既定値（原点・無回転・等倍）の点を作る。</summary>
    public static PlacementPoint Default() => new() { X = 0, Y = 0, Z = 0, Yaw = 0, Scale = 1f };
}

/// <summary>
/// 生成結果。点列と、要求を満たせなかったときの警告文を持つ。
/// </summary>
public sealed class PlacementResult
{
    /// <summary>生成された点列（先頭から配置順）。</summary>
    public List<PlacementPoint> Points { get; } = new();

    /// <summary>
    /// 要求を満たせなかった場合の警告文（無ければ null）。
    /// 例: 最小間隔が厳しすぎて要求個数を置けなかった。
    /// </summary>
    public string? Warning { get; set; }
}

/// <summary>
/// 配置パターンとそのパラメータ一式。
///
/// <para>
/// <b>Rust 側 <c>PlacementSpec</c>（runtime/src/engine/placement/spec.rs）と
/// 1 対 1 に対応する。</b>JSON のプロパティ名は serde のフィールド名
/// （snake_case）に合わせてあり、そのまま <c>LOGIC_PLACE</c> の
/// <c>spec</c> として送れる。フィールドを増やすときは必ず両方へ足すこと。
/// </para>
///
/// <para>
/// パターンごとに型を分けず 1 枚のフラットな構造にしているのは、
/// ダイアログでパターンを行き来しながら値を詰める使い方
/// （プレビューを見ながら決める）で入力値を失わないため。
/// 未使用のフィールドは単に読まれないので生成結果へ影響しない。
/// </para>
/// </summary>
public sealed class PlacementSpec
{
    // ── アンカーの定数（Rust 側 PlacementSpec と一致させること）──────

    /// <summary>基準位置アンカーの既定値（0.5 = 中心揃え）。</summary>
    public const float DefaultAnchor = 0.5f;

    /// <summary>基準位置アンカーの下限。</summary>
    public const float AnchorMin = 0f;

    /// <summary>基準位置アンカーの上限。</summary>
    public const float AnchorMax = 1f;

    /// <summary>
    /// アンカー値を 0..1 に丸める（NaN は 0.5 ＝ 中心揃えへ倒す）。
    /// Rust 側 <c>clamp_anchor</c> と同じ規則。
    /// </summary>
    public static float ClampAnchor(float v)
        => float.IsNaN(v) ? DefaultAnchor : Math.Clamp(v, AnchorMin, AnchorMax);

    // ── パターン種別 ────────────────────────────────────────

    /// <summary>使用するパターン。</summary>
    [JsonPropertyName("pattern")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PlacementPattern Pattern { get; set; } = PlacementPattern.Circle;

    // ── 共通 ────────────────────────────────────────────────

    /// <summary>生成個数（Grid は行×列×段で決まるため使わない）。</summary>
    [JsonPropertyName("count")]
    public uint Count { get; set; } = 8;

    /// <summary>乱数シード。同じ値なら常に同じ結果になる。</summary>
    [JsonPropertyName("seed")]
    public ulong Seed { get; set; }

    /// <summary>位置ジッターの振れ幅 [m]（各軸 ±この値）。0 でジッター無し。</summary>
    [JsonPropertyName("jitter_pos")]
    public float JitterPos { get; set; }

    /// <summary>回転ジッターの振れ幅 [度]（ヨー ±この値）。0 でジッター無し。</summary>
    [JsonPropertyName("jitter_rot")]
    public float JitterRot { get; set; }

    /// <summary>進行方向（点列の進む向き）を向かせるか。</summary>
    [JsonPropertyName("face_forward")]
    public bool FaceForward { get; set; }

    // ── 円形／円弧 ──────────────────────────────────────────

    /// <summary>半径 [m]。</summary>
    [JsonPropertyName("radius")]
    public float Radius { get; set; } = 5f;

    /// <summary>開始角 [度]。</summary>
    [JsonPropertyName("start_angle")]
    public float StartAngle { get; set; }

    /// <summary>角度範囲 [度]（360 で全周）。</summary>
    [JsonPropertyName("angle_span")]
    public float AngleSpan { get; set; } = 360f;

    /// <summary>中心を向かせるか（<see cref="FaceForward"/> より優先される）。</summary>
    [JsonPropertyName("face_center")]
    public bool FaceCenter { get; set; }

    // ── グリッド ────────────────────────────────────────────

    /// <summary>行数（Z 方向の個数）。</summary>
    [JsonPropertyName("rows")]
    public uint Rows { get; set; } = 3;

    /// <summary>列数（X 方向の個数）。</summary>
    [JsonPropertyName("cols")]
    public uint Cols { get; set; } = 3;

    /// <summary>段数（Y 方向の個数。2D 配置では 1）。</summary>
    [JsonPropertyName("layers")]
    public uint Layers { get; set; } = 1;

    /// <summary>X 方向の間隔 [m]。</summary>
    [JsonPropertyName("spacing_x")]
    public float SpacingX { get; set; } = 2f;

    /// <summary>Z 方向の間隔 [m]。</summary>
    [JsonPropertyName("spacing_z")]
    public float SpacingZ { get; set; } = 2f;

    /// <summary>Y 方向（段）の間隔 [m]。</summary>
    [JsonPropertyName("spacing_y")]
    public float SpacingY { get; set; } = 2f;

    /// <summary>
    /// 基準位置アンカー X（0..1）。パターンの X 方向のどこを基準点に合わせるか。
    ///
    /// <para><b>座標規約</b>: 0 = グリッドの -X 側の辺が基準点に一致、
    /// 1 = +X 側の辺が基準点に一致、0.5 = 中心揃え。
    /// 直線パターンでは「線に沿った方向」のアンカーとして共用する
    /// （0 = 始点が基準点 / 1 = 終点が基準点 / 0.5 = 線の中心）。</para>
    /// </summary>
    [JsonPropertyName("anchor_x")]
    public float AnchorX { get; set; } = DefaultAnchor;

    /// <summary>
    /// 基準位置アンカー Y（0..1）。パターン平面の<b>第 2 軸</b>のアンカー。
    ///
    /// <para><b>座標規約</b>: 3D では Z 軸に対応し、0 = -Z 側の辺、1 = +Z 側の辺、
    /// 0.5 = 中心。2D ではキャンバス Y（下向き正）に写るので、0 = 上辺、1 = 下辺。
    /// つまり (0,0) はグリッドの<b>左上</b>、(1,1) は<b>右下</b>が基準点に来る。</para>
    ///
    /// <para>直線パターンでは使わない（線は 1 次元なので <see cref="AnchorX"/> だけで決まる）。</para>
    /// </summary>
    [JsonPropertyName("anchor_y")]
    public float AnchorY { get; set; } = DefaultAnchor;

    /// <summary>
    /// 旧「中心揃え」チェックの読み込み専用ブリッジ（後方互換）。
    ///
    /// <para>
    /// アンカー導入前に <see cref="EditorPreferences"/> へ保存された前回値は
    /// <c>center_align</c>（bool）を持つ。読み込み時だけそれをアンカーへ翻訳する
    /// （true → 0.5 = 中心揃え / false → 0 = 手前の隅）。
    /// getter は常に null なので、<b>書き出しにも IPC にも現れない</b>
    /// （<see cref="JsonIgnoreCondition.WhenWritingNull"/>）。
    /// </para>
    /// </summary>
    [JsonPropertyName("center_align")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyCenterAlign
    {
        get => null;
        set
        {
            if (value is null) return;
            float a = value.Value ? DefaultAnchor : 0f;
            AnchorX = a;
            AnchorY = a;
        }
    }

    /// <summary>市松オフセット（奇数行を X 方向へ半間隔ずらす）。</summary>
    [JsonPropertyName("checker_offset")]
    public bool CheckerOffset { get; set; }

    // ── 直線 ────────────────────────────────────────────────

    /// <summary>直線の方向角 [度]（<c>yaw = atan2(dir.x, dir.z)</c> 規約）。</summary>
    [JsonPropertyName("line_angle")]
    public float LineAngle { get; set; }

    /// <summary>直線上の点間隔 [m]。</summary>
    [JsonPropertyName("line_spacing")]
    public float LineSpacing { get; set; } = 2f;

    // ── ランダム散布 ────────────────────────────────────────

    /// <summary>範囲の形状。true = 円、false = 矩形。</summary>
    [JsonPropertyName("area_circle")]
    public bool AreaCircle { get; set; } = true;

    /// <summary>円範囲の半径 [m]。</summary>
    [JsonPropertyName("area_radius")]
    public float AreaRadius { get; set; } = 5f;

    /// <summary>矩形範囲の X 幅 [m]。</summary>
    [JsonPropertyName("area_size_x")]
    public float AreaSizeX { get; set; } = 10f;

    /// <summary>矩形範囲の Z 幅 [m]。</summary>
    [JsonPropertyName("area_size_z")]
    public float AreaSizeZ { get; set; } = 10f;

    /// <summary>点同士の最小間隔 [m]（XZ 距離）。0 で無制限。</summary>
    [JsonPropertyName("min_spacing")]
    public float MinSpacing { get; set; }

    /// <summary>ヨーを 0..360 度でランダム化するか。</summary>
    [JsonPropertyName("random_rotation")]
    public bool RandomRotation { get; set; }

    /// <summary>スケールのばらつき（±この割合。0.2 なら 0.8〜1.2 倍）。</summary>
    [JsonPropertyName("scale_variance")]
    public float ScaleVariance { get; set; }

    /// <summary>
    /// パターン種別に応じた既定のグループ名・アクタ名の基（日本語表示名）。
    /// Rust 側 <c>PlacementPattern::display_name</c> と一致させること。
    /// </summary>
    public string PatternDisplayName => Pattern switch
    {
        PlacementPattern.Circle => "円形配置",
        PlacementPattern.Grid   => "グリッド配置",
        PlacementPattern.Line   => "直線配置",
        PlacementPattern.Random => "ランダム配置",
        _                       => "配置",
    };

    /// <summary>現在の値をそのまま複製する（前回値の保存・復元に使う）。</summary>
    public PlacementSpec Clone() => (PlacementSpec)MemberwiseClone();
}
