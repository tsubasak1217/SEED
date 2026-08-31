using System;

namespace SEED;

/// <summary>
/// GameObject の 3D ポリライン（LineRendererComponent）へのアクセサ。
/// Rust ランタイムのコンポーネントを FFI 経由で読み書きする薄いラッパー（値はエンジンが保持）。
///
/// 釣り糸・ロープ・軌跡・照準線など「毎フレーム形が変わる線」を描くための API。
/// 典型的な使い方は Update で <see cref="SetPoints(ReadOnlySpan{Vector3})"/> を呼び、
/// 点列を丸ごと差し替えること。
///
/// LineRenderer を持たないエンティティに対する読み取りは既定値、書き込みは無視される。
/// </summary>
public readonly struct LineRenderer : IComponentHandle<LineRenderer>
{
    /// <summary>この LineRenderer が属するエンティティ（スロット entity）。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント名（Rust 側レジストリのキーと一致必須）。</summary>
    private const string Comp = "LineRenderer";

    /// <summary>点 1 個あたりの float 要素数（Vector3 = x,y,z）。</summary>
    private const int FloatsPerPoint = 3;

    /// <summary>
    /// 1 本の線が持てる点の最大数（Rust 側 <c>MAX_LINE_POINTS</c> と一致させること）。
    /// これを超える点列を <see cref="SetPoints(ReadOnlySpan{Vector3})"/> に渡すと失敗する。
    /// </summary>
    public const int MaxPoints = ScriptHost.MaxFloatWriteLen / FloatsPerPoint;

    /// <summary>
    /// スタック上に確保する点列バッファの上限要素数（float 個数）。
    /// これを超える長さはヒープ配列にフォールバックする（stackalloc のスタック溢れ回避）。
    /// 256 float = 約 1KB で、実用的な釣り糸（数十点）は全てスタックで収まる。
    /// </summary>
    private const int StackallocFloatLimit = 256;

    internal LineRenderer(Entity entity) { _entity = entity; }

    // ── IComponentHandle 実装（GetComponent 経由でのみ使われる）──
    static string IComponentHandle<LineRenderer>.ComponentKindName => Comp;
    static LineRenderer IComponentHandle<LineRenderer>.FromEntity(Entity slotEntity) => new(slotEntity);

    /// <summary>
    /// この参照が生存しているか（指すエンティティが実在し LineRenderer を保持しているか）。
    /// [SerializeField] 参照フィールドの生存判定に使う。
    /// </summary>
    public bool IsValid => ScriptHost.HasComponent(_entity, Comp);

    // ── プロパティ ─────────────────────────────────────

    /// <summary>線の太さ（ワールド単位＝メートル）。負値は 0 にクランプされる。</summary>
    public float Width
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "width", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "width", value);
    }

    /// <summary>線の色（RGBA）。アルファ &lt; 1 で半透明合成される。</summary>
    public Color Color
    {
        get => ScriptHost.TryGetColor(_entity, Comp, "color", out var c) ? c : Color.White;
        set => ScriptHost.TrySetColor(_entity, Comp, "color", value);
    }

    /// <summary>描画するか。false でスロットを消さずに一時的に隠せる。</summary>
    public bool Visible
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "visible", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "visible", value);
    }

    /// <summary>
    /// 点列をアクターローカル座標として扱うか（false = ワールド座標）。
    /// 竿先とウキのように「別々のアクターの位置を結ぶ」場合は false にして
    /// ワールド座標を直接渡すのが素直。
    /// </summary>
    public bool LocalSpace
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "local_space", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "local_space", value);
    }

    /// <summary>
    /// 深度テストを行うか（true = 手前の不透明物に隠れる / false = 常に最前面）。
    /// </summary>
    public bool DepthTest
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "depth_test", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "depth_test", value);
    }

    /// <summary>
    /// 現在の点の数。点列そのものは読み出せない（FFI の読み取りは 4 要素までのため）。
    /// </summary>
    public int PointCount =>
        ScriptHost.TryGetFloat(_entity, Comp, "point_count", out var v) ? (int)v : 0;

    // ── 点列の更新 ─────────────────────────────────────

    /// <summary>
    /// 点列を丸ごと差し替える。毎フレーム呼ぶ想定の主 API。
    /// </summary>
    /// <param name="points">
    /// 新しい点列。<see cref="LocalSpace"/> の設定に従いローカル／ワールド座標として解釈される。
    /// 2 点未満なら線は描かれない。空配列を渡すと線が消える。
    /// </param>
    /// <returns>
    /// 反映できたら true。点数が <see cref="MaxPoints"/> を超える場合や
    /// LineRenderer を持たないエンティティでは false。
    /// </returns>
    public bool SetPoints(ReadOnlySpan<Vector3> points)
    {
        // 空指定は「線を消す」。点数 0 への切り詰めとして送る
        // （FFI の float 配列書き込みは 1 要素以上が必要なため、専用フィールドを使う）。
        if (points.Length == 0)
        {
            return ScriptHost.TrySetFloat(_entity, Comp, "point_count", 0f);
        }
        if (points.Length > MaxPoints) return false;

        int count = points.Length * FloatsPerPoint;
        // 短い点列はスタック、長い点列はヒープ（stackalloc のスタック溢れを避ける）。
        Span<float> buf = count <= StackallocFloatLimit
            ? stackalloc float[count]
            : new float[count];

        for (int i = 0; i < points.Length; i++)
        {
            int o = i * FloatsPerPoint;
            buf[o]     = points[i].x;
            buf[o + 1] = points[i].y;
            buf[o + 2] = points[i].z;
        }
        return ScriptHost.TrySetFloats(_entity, Comp, "points", buf);
    }

    /// <summary>配列版の <see cref="SetPoints(ReadOnlySpan{Vector3})"/>。null は空扱い。</summary>
    public bool SetPoints(Vector3[] points) =>
        SetPoints(points is null ? ReadOnlySpan<Vector3>.Empty : points.AsSpan());

    /// <summary>点列を空にして線を消す（<c>SetPoints(空)</c> と同じ）。</summary>
    public bool Clear() => SetPoints(ReadOnlySpan<Vector3>.Empty);
}
