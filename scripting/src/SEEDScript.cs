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

    /// <summary>このスクリプトがアタッチされた GameObject の Transform（短縮）。</summary>
    protected SEED.Transform transform => new(_entity);

    public virtual void BeginFrame(ref NativeFrameContext ctx)    {}
    public virtual void EarlyUpdate(ref NativeFrameContext ctx)   {}
    public virtual void Update(ref NativeFrameContext ctx)        {}
    public virtual void ConstantUpdate(ref NativeFrameContext ctx) {}
    public virtual void LateUpdate(ref NativeFrameContext ctx)    {}
    public virtual void Render(ref NativeFrameContext ctx)        {}
    public virtual void EndFrame(ref NativeFrameContext ctx)      {}
}
