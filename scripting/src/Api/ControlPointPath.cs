namespace SEED;

/// <summary>
/// GameObject のコントロールポイント列（ControlPointComponent）へのアクセサ。
///
/// シーン上に置いた「順序付きの点列」を、時刻を与えて評価するための薄いラッパー。
/// 評価（補間・ワールド変換・閉ループの周回）はすべてエンジン側の
/// <c>PathEval</c> が行うため、見えているギズモの線と完全に同じ経路を辿る。
///
/// <b>座標系はすべてワールド空間</b>（制御点はアクタ相対で保存されているが、
/// 取得時にアクタの Transform が合成済み）。アクタを動かせば経路ごと動く。
///
/// <b>時刻</b>の単位・原点は制御点の <c>time</c> に従う（既定は「1 点 = 1 秒」）。
/// 閉ループ（Closed）では時刻が 1 周ぶんで周回するので、時刻を増やし続けるだけで
/// ぐるぐる回れる。開いた経路では両端でクランプされる（経路の外へは出ない）。
///
/// 取得は <c>gameObject.GetComponent&lt;ControlPointPath&gt;()</c>。
/// 別アクタの経路を使う場合は <c>[SerializeField] SEED.ControlPointPath? path;</c> で参照する。
/// <code>
/// if (gameObject.GetComponent&lt;ControlPointPath&gt;() is { } path)
/// {
///     transform.Position = path.SamplePosition(t);      // ワールド位置
///     var dir = path.SampleTangent(t);                  // 進行方向（単位ベクトル）
/// }
/// </code>
/// このコンポーネントは<b>読み取り専用</b>である（点列の編集はエディタで行う）。
/// </summary>
public readonly struct ControlPointPath : IComponentHandle<ControlPointPath>
{
    /// <summary>この経路が属するスロット entity。</summary>
    private readonly Entity _entity;

    /// <summary>
    /// コンポーネント種別名（Rust 側 host_api の KIND_CONTROL_POINT および
    /// エディタ側 ReferenceKindCatalog.ControlPointKind と一致必須）。
    /// </summary>
    private const string Comp = "ControlPoint";

    internal ControlPointPath(Entity entity) { _entity = entity; }

    // ── IComponentHandle 実装（GetComponent 経由でのみ使われる）──
    static string IComponentHandle<ControlPointPath>.ComponentKindName => Comp;
    static ControlPointPath IComponentHandle<ControlPointPath>.FromEntity(Entity slotEntity) => new(slotEntity);

    // ── 参照の生存判定 ─────────────────────────────────

    /// <summary>
    /// この参照が生存しているか（指すエンティティが実在し ControlPoint を保持しているか）。
    ///
    /// <b>null は「未設定」</b>（Nullable 宣言のみ）を意味し、
    /// <b>IsValid == false は「未解決または破棄済み」</b>を意味する。
    /// </summary>
    public bool IsValid => ScriptHost.HasComponent(_entity, Comp);

    // ── 形状の問い合わせ ───────────────────────────────

    /// <summary>制御点の数。取得できない場合は 0。</summary>
    public int PointCount
        => ScriptHost.TryGetFloat(_entity, Comp, "point_count", out var v) ? (int)v : 0;

    /// <summary>
    /// 閉ループ（始点と終点が接続されている）か。
    /// 制御点が 2 個未満のときは区間が作れないため、設定に関わらず false になる。
    /// </summary>
    public bool Closed
        => ScriptHost.TryGetBool(_entity, Comp, "closed", out var b) && b;

    /// <summary>
    /// 経路 1 周ぶんの所要時間（秒）。開いた経路では先頭点から末尾点までの時間。
    /// 閉ループでは「最後の点 → 最初の点」へ戻る区間ぶんも含む。点が無ければ 0。
    ///
    /// 閉ループの周回はこの値が周期になる（時刻 t と t + Duration が同じ位置）。
    /// </summary>
    public float Duration
        => ScriptHost.TryGetFloat(_entity, Comp, "duration", out var v) ? v : 0f;

    /// <summary>
    /// 経路の開始時刻（先頭の制御点の <c>time</c>）。点が無ければ 0。
    ///
    /// 時刻の原点は点列が決めるため、経路上の時刻を自前で保持する場合の
    /// 範囲の下端として使う（終端は <c>StartTime + Duration</c>）。
    /// </summary>
    public float StartTime
        => ScriptHost.TryGetFloat(_entity, Comp, "start_time", out var v) ? v : 0f;

    // ── 時刻サンプル ───────────────────────────────────

    /// <summary>
    /// 指定時刻における経路上の<b>ワールド位置</b>。
    ///
    /// 閉ループでは時刻が周回し、開いた経路では両端でクランプされる。
    /// 制御点が 1 つも無い場合は <see cref="Vector3.Zero"/>。
    /// </summary>
    /// <param name="time">経路上の時刻（制御点の time と同じ単位・原点）。</param>
    public Vector3 SamplePosition(float time)
        => ScriptHost.TryPathSample(_entity, ScriptHost.PathQueryPosition, time, out var p)
            ? p : Vector3.Zero;

    /// <summary>
    /// 指定時刻における経路の<b>進行方向</b>（ワールド空間の単位ベクトル）。
    ///
    /// 時刻が増える向きを正とする。逆走したい場合は呼び出し側で符号を反転する。
    /// 向きが定まらない場合（点が 1 個以下・Step 補間の区間内・同一座標が続く）は
    /// <see cref="Vector3.Zero"/> を返すので、呼び出し側は
    /// <c>SqrMagnitude</c> で有効性を確認してから使うこと。
    /// </summary>
    /// <param name="time">経路上の時刻（制御点の time と同じ単位・原点）。</param>
    public Vector3 SampleTangent(float time)
        => ScriptHost.TryPathSample(_entity, ScriptHost.PathQueryTangent, time, out var d)
            ? d : Vector3.Zero;
}
