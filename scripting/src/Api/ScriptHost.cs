using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SEED;

/// <summary>
/// Rust ランタイムが渡してくる関数ポインタ表（<see cref="ScriptHostApi"/>）を保持し、
/// スクリプト側のコンポーネントアクセス（Transform / Sprite など）を FFI 経由で仲介する。
///
/// データ表現は Rust 側 host_api.rs と対をなす:
/// - 数値フィールドはすべて float 配列（1〜4 要素）。f32=1 / Vector2=2 / Vector3=3 / RGBA=4。
///   bool は 0/1、整数は float に変換して受け渡す。
/// - 文字列フィールドは UTF-8 バイト列（取得は必要長を返す 2 段階プロトコル）。
///
/// Rust 側は起動時に一度だけ ScriptBridge.RegisterHostApi を呼び、
/// このクラスへアクセサ関数ポインタを登録する。登録前・非対応フィールドは失敗（既定値）扱い。
/// </summary>
public static unsafe class ScriptHost
{
    /// <summary>float フィールドの最大要素数（RGBA カラーの 4 要素、Rust 側と一致させる）。</summary>
    private const int MaxFloatFieldLen = 4;

    /// <summary>文字列取得の初期バッファサイズ（バイト）。パス程度なら 1 回で収まる長さ。</summary>
    private const int InitialStringBufferSize = 260;

    /// <summary>Rust から登録されたアクセサ関数ポインタ表。</summary>
    private static ScriptHostApi _api;
    /// <summary>ホスト API が登録済みか（未登録なら全アクセスが失敗する）。</summary>
    private static bool _available;

    /// <summary>Rust から関数ポインタ表を登録する（ScriptBridge.RegisterHostApi 経由）。</summary>
    internal static void Register(ScriptHostApi* api)
    {
        if (api == null) return;
        _api = *api;
        _available = true;
    }

    // ── 低レベル: float 配列アクセス ─────────────────────────────

    /// <summary>
    /// 指定コンポーネントの数値フィールドを読み、buf に書かれた要素数を返す。失敗時は 0。
    /// buf は最低でもフィールドの要素数（最大 <see cref="MaxFloatFieldLen"/>）を確保しておくこと。
    /// </summary>
    public static int TryGetFloats(Entity e, string component, string field, Span<float> buf)
    {
        if (!_available || _api.GetFloats == null || !e.IsValid) return 0;

        int cl = Encoding.UTF8.GetByteCount(component);
        int fl = Encoding.UTF8.GetByteCount(field);
        Span<byte> cb = stackalloc byte[cl];
        Span<byte> fb = stackalloc byte[fl];
        Encoding.UTF8.GetBytes(component, cb);
        Encoding.UTF8.GetBytes(field, fb);

        fixed (byte* cp = cb)
        fixed (byte* fp = fb)
        fixed (float* bp = buf)
            return _api.GetFloats(e.Index, e.Generation, cp, cl, fp, fl, bp, buf.Length);
    }

    /// <summary>指定コンポーネントの数値フィールドへ値（1〜4 要素）を書き込む。失敗時は false。</summary>
    public static bool TrySetFloats(Entity e, string component, string field, ReadOnlySpan<float> values)
    {
        if (!_available || _api.SetFloats == null || !e.IsValid) return false;
        if (values.Length is <= 0 or > MaxFloatFieldLen) return false;

        int cl = Encoding.UTF8.GetByteCount(component);
        int fl = Encoding.UTF8.GetByteCount(field);
        Span<byte> cb = stackalloc byte[cl];
        Span<byte> fb = stackalloc byte[fl];
        Encoding.UTF8.GetBytes(component, cb);
        Encoding.UTF8.GetBytes(field, fb);

        fixed (byte* cp = cb)
        fixed (byte* fp = fb)
        fixed (float* vp = values)
            return _api.SetFloats(e.Index, e.Generation, cp, cl, fp, fl, vp, values.Length) != 0;
    }

    // ── 型付きヘルパ: float / bool / Vector2 / Vector3 ──────────

    /// <summary>float フィールドを読む。失敗時は false。</summary>
    public static bool TryGetFloat(Entity e, string component, string field, out float value)
    {
        Span<float> buf = stackalloc float[1];
        value = 0f;
        if (TryGetFloats(e, component, field, buf) != 1) return false;
        value = buf[0];
        return true;
    }

    /// <summary>float フィールドへ書き込む。失敗時は false。</summary>
    public static bool TrySetFloat(Entity e, string component, string field, float value)
    {
        Span<float> buf = stackalloc float[1];
        buf[0] = value;
        return TrySetFloats(e, component, field, buf);
    }

    /// <summary>bool フィールド（0/1 の float 表現）を読む。失敗時は false。</summary>
    public static bool TryGetBool(Entity e, string component, string field, out bool value)
    {
        var ok = TryGetFloat(e, component, field, out var f);
        value = f != 0f;
        return ok;
    }

    /// <summary>bool フィールド（0/1 の float 表現）へ書き込む。失敗時は false。</summary>
    public static bool TrySetBool(Entity e, string component, string field, bool value)
        => TrySetFloat(e, component, field, value ? 1f : 0f);

    /// <summary>Vector2 フィールドを読む。失敗時は false。</summary>
    public static bool TryGetVec2(Entity e, string component, string field, out Vector2 value)
    {
        Span<float> buf = stackalloc float[2];
        value = Vector2.Zero;
        if (TryGetFloats(e, component, field, buf) != 2) return false;
        value = new Vector2(buf[0], buf[1]);
        return true;
    }

    /// <summary>Vector2 フィールドへ書き込む。失敗時は false。</summary>
    public static bool TrySetVec2(Entity e, string component, string field, Vector2 value)
    {
        Span<float> buf = stackalloc float[2];
        buf[0] = value.x; buf[1] = value.y;
        return TrySetFloats(e, component, field, buf);
    }

    /// <summary>Vector3 フィールドを読む。失敗時は false。</summary>
    public static bool TryGetVec3(Entity e, string component, string field, out Vector3 value)
    {
        Span<float> buf = stackalloc float[3];
        value = Vector3.Zero;
        if (TryGetFloats(e, component, field, buf) != 3) return false;
        value = new Vector3(buf[0], buf[1], buf[2]);
        return true;
    }

    /// <summary>Vector3 フィールドへ書き込む。失敗時は false。</summary>
    public static bool TrySetVec3(Entity e, string component, string field, Vector3 value)
    {
        Span<float> buf = stackalloc float[3];
        buf[0] = value.x; buf[1] = value.y; buf[2] = value.z;
        return TrySetFloats(e, component, field, buf);
    }

    /// <summary>Color（RGBA 4 要素）フィールドを読む。失敗時は false。</summary>
    public static bool TryGetColor(Entity e, string component, string field, out Color value)
    {
        Span<float> buf = stackalloc float[4];
        value = Color.White;
        if (TryGetFloats(e, component, field, buf) != 4) return false;
        value = new Color(buf[0], buf[1], buf[2], buf[3]);
        return true;
    }

    /// <summary>Color（RGBA 4 要素）フィールドへ書き込む。失敗時は false。</summary>
    public static bool TrySetColor(Entity e, string component, string field, Color value)
    {
        Span<float> buf = stackalloc float[4];
        buf[0] = value.r; buf[1] = value.g; buf[2] = value.b; buf[3] = value.a;
        return TrySetFloats(e, component, field, buf);
    }

    // ── 文字列アクセス ───────────────────────────────────────────

    /// <summary>
    /// 文字列フィールドを読む。失敗時は false。
    /// Rust 側は「必要バイト長」を返すため、初期バッファで足りなければ広げて再取得する。
    /// </summary>
    public static bool TryGetString(Entity e, string component, string field, out string value)
    {
        value = "";
        if (!_available || _api.GetString == null || !e.IsValid) return false;

        int cl = Encoding.UTF8.GetByteCount(component);
        int fl = Encoding.UTF8.GetByteCount(field);
        Span<byte> cb = stackalloc byte[cl];
        Span<byte> fb = stackalloc byte[fl];
        Encoding.UTF8.GetBytes(component, cb);
        Encoding.UTF8.GetBytes(field, fb);

        // 1 回目: 初期バッファで試す（パス程度なら通常ここで完了する）
        Span<byte> stack = stackalloc byte[InitialStringBufferSize];
        int needed;
        fixed (byte* cp = cb)
        fixed (byte* fp = fb)
        fixed (byte* sp = stack)
            needed = _api.GetString(e.Index, e.Generation, cp, cl, fp, fl, sp, stack.Length);

        if (needed < 0) return false;                       // フィールド未対応
        if (needed <= stack.Length)
        {
            value = Encoding.UTF8.GetString(stack[..needed]);
            return true;
        }

        // 2 回目: 必要長ちょうどのヒープバッファで再取得する
        var heap = new byte[needed];
        int written;
        fixed (byte* cp = cb)
        fixed (byte* fp = fb)
        fixed (byte* hp = heap)
            written = _api.GetString(e.Index, e.Generation, cp, cl, fp, fl, hp, heap.Length);

        if (written < 0 || written > heap.Length) return false;
        value = Encoding.UTF8.GetString(heap, 0, written);
        return true;
    }

    /// <summary>文字列フィールドへ書き込む。失敗時は false。</summary>
    public static bool TrySetString(Entity e, string component, string field, string value)
    {
        if (!_available || _api.SetString == null || !e.IsValid) return false;
        value ??= "";

        int cl = Encoding.UTF8.GetByteCount(component);
        int fl = Encoding.UTF8.GetByteCount(field);
        int vl = Encoding.UTF8.GetByteCount(value);
        Span<byte> cb = stackalloc byte[cl];
        Span<byte> fb = stackalloc byte[fl];
        // 値は長くなり得るため、長い場合だけヒープを使う
        Span<byte> vb = vl <= InitialStringBufferSize ? stackalloc byte[vl] : new byte[vl];
        Encoding.UTF8.GetBytes(component, cb);
        Encoding.UTF8.GetBytes(field, fb);
        Encoding.UTF8.GetBytes(value, vb);

        fixed (byte* cp = cb)
        fixed (byte* fp = fb)
        fixed (byte* vp = vb)
            return _api.SetString(e.Index, e.Generation, cp, cl, fp, fl, vp, vl) != 0;
    }

    // ── コンポーネント保持判定 ───────────────────────────────────

    /// <summary>エンティティが指定コンポーネントを持つか。</summary>
    public static bool HasComponent(Entity e, string component)
    {
        if (!_available || _api.HasComponent == null || !e.IsValid) return false;

        int cl = Encoding.UTF8.GetByteCount(component);
        Span<byte> cb = stackalloc byte[cl];
        Encoding.UTF8.GetBytes(component, cb);
        fixed (byte* cp = cb)
            return _api.HasComponent(e.Index, e.Generation, cp, cl) != 0;
    }

    // ── GetComponent<T> スロット解決 ─────────────────────────────

    /// <summary>
    /// アクタールート entity から、指定種別コンポーネントのスロット entity を解決する。失敗時は false。
    /// name 指定時（非 null・非空）はスロット名一致、そうでなければ index 番目（index&lt;0 は 0）を選ぶ。
    /// Transform / CanvasTransform はルート直付けのためルート entity 自身を返す。
    /// </summary>
    public static bool TryResolveComponentSlot(
        Entity actor, string kind, string? name, int index, out Entity slot)
    {
        slot = Entity.None;
        if (!_available || _api.ResolveComponentSlot == null || !actor.IsValid) return false;

        int kl = Encoding.UTF8.GetByteCount(kind);
        Span<byte> kb = stackalloc byte[kl];
        Encoding.UTF8.GetBytes(kind, kb);

        // 名前指定なしは長さ 0 で渡す（Rust 側は name_len<=0 を index 選択とみなす）
        int nl = string.IsNullOrEmpty(name) ? 0 : Encoding.UTF8.GetByteCount(name);
        Span<byte> nb = nl > 0 ? stackalloc byte[nl] : default;
        if (nl > 0) Encoding.UTF8.GetBytes(name!, nb);

        uint* outBuf = stackalloc uint[2];
        int ok;
        fixed (byte* kp = kb)
        fixed (byte* np = nb)
            ok = _api.ResolveComponentSlot(actor.Index, actor.Generation, kp, kl, np, nl, index, outBuf);

        if (ok == 0) return false;
        slot = new Entity(outBuf[0], outBuf[1]);
        return true;
    }

    // ── InputMap アクション評価 ───────────────────────────────────

    /// <summary>
    /// InputMap のアクションを評価する（kind: 0=action（条件適用後）/1=start（成立の瞬間）/2=end（終了の瞬間））。
    /// kind は Rust 側 host_api の INPUT_ACTION_KIND_* と一致させること。
    /// slot は InputMapComponent のスロット entity。
    /// </summary>
    public static bool InputAction(Entity slot, int kind, string name)
    {
        if (!_available || _api.InputAction == null || !slot.IsValid) return false;
        name ??= "";
        int nl = Encoding.UTF8.GetByteCount(name);
        Span<byte> nb = nl > 0 ? stackalloc byte[nl] : default;
        if (nl > 0) Encoding.UTF8.GetBytes(name, nb);
        fixed (byte* np = nb)
            return _api.InputAction(slot.Index, slot.Generation, kind, np, nl) != 0;
    }

    /// <summary>InputMap の Axis1D アクションを評価する（[-1,1]）。失敗時は 0。</summary>
    public static float InputActionAxis1D(Entity slot, string name)
    {
        if (!_available || _api.InputActionAxis == null || !slot.IsValid) return 0f;
        name ??= "";
        int nl = Encoding.UTF8.GetByteCount(name);
        Span<byte> nb = nl > 0 ? stackalloc byte[nl] : default;
        if (nl > 0) Encoding.UTF8.GetBytes(name, nb);
        float* buf = stackalloc float[1];
        fixed (byte* np = nb)
            return _api.InputActionAxis(slot.Index, slot.Generation, np, nl, buf, 1) == 1 ? buf[0] : 0f;
    }

    /// <summary>InputMap の Vector2 アクションを評価する。失敗時は Zero。</summary>
    public static Vector2 InputActionVector2(Entity slot, string name)
    {
        if (!_available || _api.InputActionAxis == null || !slot.IsValid) return Vector2.Zero;
        name ??= "";
        int nl = Encoding.UTF8.GetByteCount(name);
        Span<byte> nb = nl > 0 ? stackalloc byte[nl] : default;
        if (nl > 0) Encoding.UTF8.GetBytes(name, nb);
        float* buf = stackalloc float[2];
        fixed (byte* np = nb)
            return _api.InputActionAxis(slot.Index, slot.Generation, np, nl, buf, 2) == 2
                ? new Vector2(buf[0], buf[1])
                : Vector2.Zero;
    }

    // ── シーン操作（Instantiate / Destroy / Find）────────────────

    /// <summary>
    /// .actor ファイルからアクターを生成し、ルートエンティティを返す。失敗時は false。
    /// ルートは即座に予約される（同フレーム中に Transform を設定できる）が、
    /// アクター本体の構築はフレーム末尾に遅延適用される。
    /// </summary>
    public static bool TryInstantiate(string actorPath, out Entity entity)
    {
        entity = Entity.None;
        if (!_available || _api.Instantiate == null || string.IsNullOrEmpty(actorPath)) return false;

        int pl = Encoding.UTF8.GetByteCount(actorPath);
        Span<byte> pb = stackalloc byte[pl];
        Encoding.UTF8.GetBytes(actorPath, pb);

        uint* outBuf = stackalloc uint[2];
        int ok;
        fixed (byte* pp = pb)
            ok = _api.Instantiate(pp, pl, outBuf);

        if (ok == 0) return false;
        entity = new Entity(outBuf[0], outBuf[1]);
        return true;
    }

    /// <summary>
    /// 指定ルートエンティティのアクターを破棄する（フレーム末尾に遅延適用）。
    /// 受理されたら true。
    /// </summary>
    public static bool TryDestroy(Entity e)
    {
        if (!_available || _api.Destroy == null || !e.IsValid) return false;
        return _api.Destroy(e.Index, e.Generation) != 0;
    }

    // ── 入力（キー・マウス）─────────────────────────────────────

    /// <summary>キー入力判定（kind: 0=押下中/1=押した瞬間/2=離した瞬間）。</summary>
    public static bool InputKey(int kind, uint keyId)
    {
        if (!_available || _api.InputKey == null) return false;
        return _api.InputKey(kind, keyId) != 0;
    }

    /// <summary>マウスボタン入力判定（kind は InputKey と同じ体系）。</summary>
    public static bool InputMouse(int kind, uint buttonId)
    {
        if (!_available || _api.InputMouse == null) return false;
        return _api.InputMouse(kind, buttonId) != 0;
    }

    /// <summary>マウスの 2 要素状態（座標・移動量）を取得する。失敗時は Zero。</summary>
    public static Vector2 InputMouseVec2(int kind)
    {
        if (!_available || _api.InputMouseState == null) return Vector2.Zero;
        float* buf = stackalloc float[2];
        return _api.InputMouseState(kind, buf) == 2 ? new Vector2(buf[0], buf[1]) : Vector2.Zero;
    }

    /// <summary>マウスの 1 要素状態（ホイール量）を取得する。失敗時は 0。</summary>
    public static float InputMouseScroll(int kind)
    {
        if (!_available || _api.InputMouseState == null) return 0f;
        float* buf = stackalloc float[2];
        return _api.InputMouseState(kind, buf) == 1 ? buf[0] : 0f;
    }

    // ── オーディオ ───────────────────────────────────────────────

    /// <summary>
    /// オーディオコマンドを発行する（kind: 0=SE再生/1=BGM再生/2=BGM停止/3=BGM音量）。
    /// 受理されたら true。
    /// </summary>
    public static bool AudioCommand(int kind, string path, float volume, int flag)
    {
        if (!_available || _api.Audio == null) return false;
        path ??= "";

        int pl = Encoding.UTF8.GetByteCount(path);
        Span<byte> pb = pl > 0 ? stackalloc byte[pl] : default;
        if (pl > 0) Encoding.UTF8.GetBytes(path, pb);
        fixed (byte* pp = pb)
            return _api.Audio(kind, pp, pl, volume, flag) != 0;
    }

    /// <summary>
    /// AudioComponent を操作する（action: 0=Play/1=Stop/2=IsPlaying）。
    /// 成功（IsPlaying の場合は再生中）なら true。
    /// </summary>
    public static bool AudioComponentAction(int action, Entity e)
    {
        if (!_available || _api.AudioComponent == null || !e.IsValid) return false;
        return _api.AudioComponent(action, e.Index, e.Generation) != 0;
    }

    // ── パーティクルエミッタ ─────────────────────────────────────

    /// <summary>
    /// ParticleEmitterComponent を操作する（action: 0=Play/1=Stop/2=Burst）。
    /// count は Burst 時のみ使用（放出個数。0 以下は無視）。他 action では無視される。
    /// 成功なら true（エミッタを持たない・未登録なら false）。
    /// </summary>
    public static bool ParticleComponentAction(int action, Entity e, int count)
    {
        if (!_available || _api.ParticleComponent == null || !e.IsValid) return false;
        return _api.ParticleComponent(action, e.Index, e.Generation, count) != 0;
    }

    // ── アニメーター ─────────────────────────────────────────────

    /// <summary>
    /// AnimatorComponent を操作する（action: 0=Play/1=Stop/2=Pause/3=Resume）。
    /// clipName は Play 時のみ使用（他 action では無視されるので null 可）。
    /// speed は Play 時の再生速度指定（<see cref="float.NaN"/> なら既存の speed を変更しない）。
    /// 成功なら true（Play はクリップ未登録・未ロードだと false）。
    /// </summary>
    public static bool AnimatorComponentAction(int action, Entity e, string clipName, float speed)
    {
        if (!_available || _api.AnimatorComponent == null || !e.IsValid) return false;
        clipName ??= "";

        int nl = Encoding.UTF8.GetByteCount(clipName);
        Span<byte> nb = nl > 0 ? stackalloc byte[nl] : default;
        if (nl > 0) Encoding.UTF8.GetBytes(clipName, nb);
        fixed (byte* np = nb)
            return _api.AnimatorComponent(action, e.Index, e.Generation, np, nl, speed) != 0;
    }

    /// <summary>
    /// 2D アクターのスクリーン座標（ウィンドウ左上原点・ピクセル）を取得する。
    /// 2D アクターでない場合は false。
    /// </summary>
    public static bool TryGetScreenPosition(Entity e, out Vector2 position)
    {
        position = Vector2.Zero;
        if (!_available || _api.ScreenPosition == null || !e.IsValid) return false;
        float* buf = stackalloc float[2];
        if (_api.ScreenPosition(e.Index, e.Generation, buf) == 0) return false;
        position = new Vector2(buf[0], buf[1]);
        return true;
    }

    // ── シーン遷移 ───────────────────────────────────────────────

    /// <summary>
    /// シーンコマンドを発行する（kind: 0=事前読み込み / 1=遷移。フレーム末尾に遅延適用）。
    /// 受理されたら true。
    /// </summary>
    public static bool SceneCommand(int kind, string sceneName)
    {
        if (!_available || _api.Scene == null || string.IsNullOrEmpty(sceneName)) return false;

        int pl = Encoding.UTF8.GetByteCount(sceneName);
        Span<byte> pb = stackalloc byte[pl];
        Encoding.UTF8.GetBytes(sceneName, pb);
        fixed (byte* pp = pb)
            return _api.Scene(kind, pp, pl) != 0;
    }

    // ── プロファイラ（手動スコープ計測）────────────────────────────

    /// <summary>
    /// プロファイラの手動スコープを開始／終了する。計測されたら true。
    /// kind: 0=Begin（name を使う）/ 1=End（name は無視）。
    /// エディタの「プロファイラ」パネルが閉じている間は常に false（計測しない）。
    /// </summary>
    public static bool ProfilerScope(int kind, string? name)
    {
        if (!_available || _api.Profiler == null) return false;

        // End（name 不要）は文字列変換をせずそのまま呼ぶ。
        if (string.IsNullOrEmpty(name))
            return _api.Profiler(kind, null, 0) != 0;

        int nl = Encoding.UTF8.GetByteCount(name);
        Span<byte> nb = stackalloc byte[nl];
        Encoding.UTF8.GetBytes(name, nb);
        fixed (byte* np = nb)
            return _api.Profiler(kind, np, nl) != 0;
    }

    // ── 物理（Raycast）──────────────────────────────────────────

    /// <summary>
    /// レイキャストを実行する。ヒットしなければ false。
    /// 物理スレッドへの同期問い合わせ（数 ms 以内に応答）。
    /// </summary>
    public static bool TryRaycast(Vector3 origin, Vector3 direction, float maxDistance, out RaycastHit hit)
    {
        hit = default;
        if (!_available || _api.Raycast == null) return false;

        float* o = stackalloc float[3];
        float* d = stackalloc float[3];
        o[0] = origin.x;    o[1] = origin.y;    o[2] = origin.z;
        d[0] = direction.x; d[1] = direction.y; d[2] = direction.z;
        // ヒット情報: [point.xyz, normal.xyz, distance] の 7 要素
        float* h = stackalloc float[7];
        uint*  e = stackalloc uint[2];

        if (_api.Raycast(o, d, maxDistance, h, e) == 0) return false;

        hit = new RaycastHit(
            new GameObject(new Entity(e[0], e[1])),
            new Vector3(h[0], h[1], h[2]),
            new Vector3(h[3], h[4], h[5]),
            h[6]);
        return true;
    }

    /// <summary>
    /// キャラクターコントローラーを瞬間移動させる（衝突無視）。成功したら true。
    ///
    /// ECS Transform 位置を pos に設定し（子アクタも追従）、物理側の「前回位置」も pos に
    /// リセットするため、瞬間移動先で自動押し戻しが発生しない。
    /// is_character_controller でないアクターでは単に位置設定のみ行われる。
    /// </summary>
    public static bool TryTeleport(Entity e, Vector3 pos)
    {
        if (!_available || _api.Teleport == null || !e.IsValid) return false;

        float* p = stackalloc float[3];
        p[0] = pos.x; p[1] = pos.y; p[2] = pos.z;
        return _api.Teleport(e.Index, e.Generation, p) != 0;
    }

    /// <summary>
    /// キャラクターコントローラーが接地しているかを返す。
    /// 物理ステップ同期で更新された最新の接地状態を参照する（非接地・未登録は false）。
    /// </summary>
    public static bool IsGrounded(Entity e)
    {
        if (!_available || _api.IsGrounded == null || !e.IsValid) return false;
        return _api.IsGrounded(e.Index, e.Generation) != 0;
    }

    /// <summary>アクターを名前で検索する（DFS 順の最初の一致）。見つからなければ false。</summary>
    public static bool TryFindActor(string name, out Entity entity)
    {
        entity = Entity.None;
        if (!_available || _api.FindActor == null || string.IsNullOrEmpty(name)) return false;

        int nl = Encoding.UTF8.GetByteCount(name);
        Span<byte> nb = stackalloc byte[nl];
        Encoding.UTF8.GetBytes(name, nb);

        uint* outBuf = stackalloc uint[2];
        int ok;
        fixed (byte* np = nb)
            ok = _api.FindActor(np, nl, outBuf);

        if (ok == 0) return false;
        entity = new Entity(outBuf[0], outBuf[1]);
        return true;
    }
}

/// <summary>
/// Rust の #[repr(C)] ScriptHostApi と同じレイアウトの関数ポインタ表。
/// フィールド順・シグネチャを Rust 側 host_api.rs と必ず一致させること。
/// すべて cdecl（Win64 では system == C == cdecl）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ScriptHostApi
{
    /// <summary>(idx, gen, comp, compLen, field, fieldLen, out float*, cap) → 書き込んだ要素数（失敗=0）</summary>
    public delegate* unmanaged[Cdecl]<uint, uint, byte*, int, byte*, int, float*, int, int> GetFloats;
    /// <summary>(idx, gen, comp, compLen, field, fieldLen, in float*, count) → 1/0</summary>
    public delegate* unmanaged[Cdecl]<uint, uint, byte*, int, byte*, int, float*, int, int> SetFloats;
    /// <summary>(idx, gen, comp, compLen, field, fieldLen, out byte*, cap) → 必要バイト長（失敗=-1）</summary>
    public delegate* unmanaged[Cdecl]<uint, uint, byte*, int, byte*, int, byte*, int, int> GetString;
    /// <summary>(idx, gen, comp, compLen, field, fieldLen, in byte*, len) → 1/0</summary>
    public delegate* unmanaged[Cdecl]<uint, uint, byte*, int, byte*, int, byte*, int, int> SetString;
    /// <summary>(idx, gen, comp, compLen) → 1/0</summary>
    public delegate* unmanaged[Cdecl]<uint, uint, byte*, int, int> HasComponent;
    /// <summary>(path, pathLen, out uint[2] entity) → 1/0</summary>
    public delegate* unmanaged[Cdecl]<byte*, int, uint*, int> Instantiate;
    /// <summary>(idx, gen) → 1/0</summary>
    public delegate* unmanaged[Cdecl]<uint, uint, int> Destroy;
    /// <summary>(name, nameLen, out uint[2] entity) → 1/0</summary>
    public delegate* unmanaged[Cdecl]<byte*, int, uint*, int> FindActor;
    /// <summary>(kind, keyId) → 1/0</summary>
    public delegate* unmanaged[Cdecl]<int, uint, int> InputKey;
    /// <summary>(kind, buttonId) → 1/0</summary>
    public delegate* unmanaged[Cdecl]<int, uint, int> InputMouse;
    /// <summary>(kind, out float*) → 書き込んだ要素数（失敗=0）</summary>
    public delegate* unmanaged[Cdecl]<int, float*, int> InputMouseState;
    /// <summary>(origin float[3], dir float[3], maxDist, out float[7] hit, out uint[2] entity) → 1/0</summary>
    public delegate* unmanaged[Cdecl]<float*, float*, float, float*, uint*, int> Raycast;
    /// <summary>(kind, name, nameLen) → 1/0（kind: 0=事前読み込み / 1=遷移）</summary>
    public delegate* unmanaged[Cdecl]<int, byte*, int, int> Scene;
    /// <summary>(kind, path, pathLen, volume, flag) → 1/0</summary>
    public delegate* unmanaged[Cdecl]<int, byte*, int, float, int, int> Audio;
    /// <summary>(action, idx, gen) → 1/0（action: 0=Play/1=Stop/2=IsPlaying）</summary>
    public delegate* unmanaged[Cdecl]<int, uint, uint, int> AudioComponent;
    /// <summary>(idx, gen, out float[2]) → 1/0（2D アクターのスクリーン座標）</summary>
    public delegate* unmanaged[Cdecl]<uint, uint, float*, int> ScreenPosition;
    /// <summary>(action, idx, gen, clipName, clipNameLen, speed) → 1/0（action: 0=Play/1=Stop/2=Pause/3=Resume）</summary>
    public delegate* unmanaged[Cdecl]<int, uint, uint, byte*, int, float, int> AnimatorComponent;
    /// <summary>(action, idx, gen, count) → 1/0（action: 0=Play/1=Stop/2=Burst。count は Burst の放出個数）</summary>
    public delegate* unmanaged[Cdecl]<int, uint, uint, int, int> ParticleComponent;
    /// <summary>(idx, gen, pos float[3]) → 1/0（キャラクターを衝突無視で瞬間移動。Transform.Teleport）</summary>
    public delegate* unmanaged[Cdecl]<uint, uint, float*, int> Teleport;
    /// <summary>(idx, gen) → 1/0（キャラクターの接地状態。Physics.IsGrounded）</summary>
    public delegate* unmanaged[Cdecl]<uint, uint, int> IsGrounded;
    /// <summary>(actorIdx, actorGen, kind, kindLen, name, nameLen, index, out uint[2] slot) → 1/0（GetComponent&lt;T&gt; スロット解決）</summary>
    public delegate* unmanaged[Cdecl]<uint, uint, byte*, int, byte*, int, int, uint*, int> ResolveComponentSlot;
    /// <summary>(slotIdx, slotGen, kind, name, nameLen) → 1/0（InputMap Bool アクション。kind: 0=press/1=down/2=up）</summary>
    public delegate* unmanaged[Cdecl]<uint, uint, int, byte*, int, int> InputAction;
    /// <summary>(slotIdx, slotGen, name, nameLen, out float*, outLen) → 書き込んだ要素数（InputMap 軸アクション。outLen>=2 で Vector2）</summary>
    public delegate* unmanaged[Cdecl]<uint, uint, byte*, int, float*, int, int> InputActionAxis;
    /// <summary>(kind, name, nameLen) → 1/0（プロファイラ手動計測。kind: 0=Begin/1=End。End は name を無視）</summary>
    public delegate* unmanaged[Cdecl]<int, byte*, int, int> Profiler;
}
