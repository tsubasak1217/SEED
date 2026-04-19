namespace SEED.Scripting;

/// <summary>
/// スクリプトコンポーネントが実装するインターフェース。
/// エンジンの 7 フェーズライフサイクルに対応する。
/// </summary>
public interface IScriptComponent
{
    void BeginFrame(ref NativeFrameContext ctx);
    void EarlyUpdate(ref NativeFrameContext ctx);
    void Update(ref NativeFrameContext ctx);
    void ConstantUpdate(ref NativeFrameContext ctx);
    void LateUpdate(ref NativeFrameContext ctx);
    void Render(ref NativeFrameContext ctx);
    void EndFrame(ref NativeFrameContext ctx);
}
