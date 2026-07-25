namespace SEEDEditor.Scripting;

/// <summary>
/// ユーザースクリプトの基底クラス。
/// 必要なライフサイクルメソッドだけオーバーライドする。
///
/// 実行中は <see cref="gameObject"/> / <see cref="transform"/> で、自分がアタッチ
/// された GameObject とその Transform（位置・回転・スケール）へアクセスできる。
/// </summary>
public abstract class SEEDScript : IScriptComponent
{
    /// <summary>このスクリプトが乗る GameObject の所有エンティティ（毎フレーム束縛される）。</summary>
    private SEED.Entity _entity = SEED.Entity.None;

    /// <summary>
    /// エンジン内部用: 現在フレームの所有エンティティを束縛する。
    /// ScriptBridge が各ライフサイクル呼び出しの直前に呼ぶ。ユーザーは使わない。
    /// </summary>
    internal void BindEntity(uint index, uint generation)
        => _entity = new SEED.Entity(index, generation);

    /// <summary>このスクリプトがアタッチされた GameObject。</summary>
    protected SEED.GameObject gameObject => new(_entity);

    /// <summary>
    /// このスクリプトがアタッチされた GameObject の Transform（短縮）。
    /// <c>gameObject.GetComponent&lt;Transform&gt;()</c> 経由で解決する（Transform は
    /// アクタールート直付けのため、通常は必ず取得できる。万一未解決なら既定ハンドル）。
    /// </summary>
    protected SEED.Transform transform => gameObject.GetComponent<SEED.Transform>() ?? new(_entity);

    public virtual void BeginFrame(ref NativeFrameContext ctx)    {}
    public virtual void EarlyUpdate(ref NativeFrameContext ctx)   {}
    public virtual void Update(ref NativeFrameContext ctx)        {}
    public virtual void ConstantUpdate(ref NativeFrameContext ctx) {}
    public virtual void LateUpdate(ref NativeFrameContext ctx)    {}
    public virtual void Render(ref NativeFrameContext ctx)        {}
    public virtual void EndFrame(ref NativeFrameContext ctx)      {}

    // ── 物理イベントコールバック ──────────────────────────────
    // 自分のアクターのコライダーが他のコライダーと衝突・接触したときに
    // エンジンから呼ばれる。other は相手アクターの GameObject
    //（相手が特定できない場合は IsValid=false）。

    /// <summary>衝突が始まったフレームに呼ばれる。</summary>
    public virtual void OnCollisionEnter(SEED.GameObject other) {}
    /// <summary>衝突が継続している間、毎物理ステップ呼ばれる。</summary>
    public virtual void OnCollisionStay(SEED.GameObject other)  {}
    /// <summary>衝突が終わったフレームに呼ばれる。</summary>
    public virtual void OnCollisionExit(SEED.GameObject other)  {}
    /// <summary>トリガーコライダーへの進入時に呼ばれる（トリガー側・相手側の両方）。</summary>
    public virtual void OnTriggerEnter(SEED.GameObject other)   {}
    /// <summary>トリガーコライダーからの退出時に呼ばれる（トリガー側・相手側の両方）。</summary>
    public virtual void OnTriggerExit(SEED.GameObject other)    {}
}
