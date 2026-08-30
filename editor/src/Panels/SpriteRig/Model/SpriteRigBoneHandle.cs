namespace SEEDEditor.Panels.SpriteRig.Model;

/// <summary>
/// ボーン編集で掴める 1 点への参照（あるボーンの「根元」か「先端」か）。
///
/// ボーンの編集表現は「根元 + 先端」の 2 点なので、この 2 値でキャンバス上の
/// すべてのボーンハンドルを一意に指せる。保存表現（親ローカル TRS）への変換は
/// <see cref="Mesh.SpriteRigSkeleton"/> が受け持つ。
/// </summary>
/// <param name="BoneIndex">対象ボーンの添字。</param>
/// <param name="IsTip">true = 先端（tip）／false = 根元（head）。</param>
public readonly record struct SpriteRigBoneHandle(int BoneIndex, bool IsTip)
{
    /// <summary>根元ハンドルへの参照を作る。</summary>
    /// <param name="boneIndex">対象ボーンの添字。</param>
    public static SpriteRigBoneHandle Head(int boneIndex) => new(boneIndex, false);

    /// <summary>先端ハンドルへの参照を作る。</summary>
    /// <param name="boneIndex">対象ボーンの添字。</param>
    public static SpriteRigBoneHandle Tip(int boneIndex) => new(boneIndex, true);
}
