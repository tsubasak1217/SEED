namespace SEED;

/// <summary>
/// GameObject のアニメーター（AnimatorComponent）へのアクセサ。
/// Rust ランタイムのキーフレームアニメーション再生状態を FFI 経由で読み書きする薄いラッパー
/// （評価・トラック適用はエンジン側の AnimationSystem が毎フレーム行う。値はエンジンが保持）。
///
/// クリップはインスペクタで登録した <c>clips</c> 一覧からロードされ、Play 時点で
/// 既にロード済みでなければならない（フレーム先頭でエンジンが自動ロードするため、
/// Update 等のスクリプトライフサイクル内から呼ぶ限り通常は問題にならない）。
/// 未登録のクリップ名を指定した場合は警告ログを出して無視する。
/// </summary>
public readonly struct Animator
{
    /// <summary>この Animator が属するエンティティ。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント名（Rust 側レジストリのキー）。</summary>
    private const string Comp = "Animator";

    // ── 操作種別（Rust 側 host_api.rs の ANIMATOR_COMPONENT_* と一致させる）──
    private const int ActionPlay   = 0;
    private const int ActionStop   = 1;
    private const int ActionPause  = 2;
    private const int ActionResume = 3;

    internal Animator(Entity entity) { _entity = entity; }

    // ── 再生操作 ─────────────────────────────────────────────

    /// <summary>指定クリップを先頭（time=0）から再生する。再生速度は変更しない。</summary>
    public void Play(string clipName)
        => ScriptHost.AnimatorComponentAction(ActionPlay, _entity, clipName, float.NaN);

    /// <summary>指定クリップを先頭（time=0）から再生し、再生速度も同時に設定する。</summary>
    public void Play(string clipName, float speed)
        => ScriptHost.AnimatorComponentAction(ActionPlay, _entity, clipName, speed);

    /// <summary>再生を停止し、再生位置を先頭（time=0）へ戻す。</summary>
    public void Stop() => ScriptHost.AnimatorComponentAction(ActionStop, _entity, "", 0f);

    /// <summary>現在の再生位置を保持したまま一時停止する。</summary>
    public void Pause() => ScriptHost.AnimatorComponentAction(ActionPause, _entity, "", 0f);

    /// <summary>一時停止していた再生を再開する（再生対象クリップが無ければ何もしない）。</summary>
    public void Resume() => ScriptHost.AnimatorComponentAction(ActionResume, _entity, "", 0f);

    // ── 状態プロパティ ───────────────────────────────────────

    /// <summary>再生中か。</summary>
    public bool IsPlaying
        => ScriptHost.TryGetBool(_entity, Comp, "playing", out var v) && v;

    /// <summary>現在再生中のクリップ名（未再生なら空文字）。</summary>
    public string CurrentClip
        => ScriptHost.TryGetString(_entity, Comp, "current_clip", out var s) ? s : "";

    /// <summary>再生位置（秒）。書き込みでシーク可能。</summary>
    public float Time
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "time", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "time", value);
    }

    /// <summary>再生速度倍率（1.0 = 等倍。負値で逆再生）。</summary>
    public float Speed
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "speed", out var v) ? v : 1f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "speed", value);
    }
}
