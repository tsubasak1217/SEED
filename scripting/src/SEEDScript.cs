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
    {
        _entity   = new SEED.Entity(index, generation);
        // transform はフィールドとして束縛し直す（下記コメント参照）。
        transform = new SEED.Transform(_entity);
    }

    /// <summary>このスクリプトがアタッチされた GameObject。</summary>
    protected SEED.GameObject gameObject => new(_entity);

    /// <summary>
    /// このスクリプトがアタッチされた GameObject の Transform（短縮）。
    ///
    /// **プロパティではなくフィールド**であることが重要: C# は値型を返すプロパティの
    /// 戻り値へのメンバ代入（<c>transform.Position += ...</c>）を CS1612 で禁止するが、
    /// フィールドは「変数」なので合法になる（Position の setter は FFI 呼び出しで
    /// 構造体自体を変更しないため、コピーで動作しても意味は変わらない）。
    /// Transform はアクタールートの entity を包むだけのハンドルで、BindEntity で
    /// エンティティと同時に束縛される。
    /// </summary>
    protected SEED.Transform transform;

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
