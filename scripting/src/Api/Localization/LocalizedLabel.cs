using SEED.UI;

namespace SEED.Localization;

// ============================================================
//  LocalizedLabel.cs — 部品（SEED.UI の Button・Toggle・Checkbox・SegmentedControl など）か Text の文字へ多言語の文字を当てるスクリプト
//  （docs/localization.md §7.2）
//
//  【付け方】部品のアクタ（Button なら Button のスクリプトが付いたアクタ）に ScriptComponent
//  （型名 "SEED.Localization.LocalizedLabel"）を付け、「キー」を書く。部品には手を入れない（公開の API で当てる）。
//  当てる先は LocalizedTargetTable の表（Button → 選択のグループ → Toggle → Checkbox → Text）の上から決める。
//  選択のグループは項目ごとに「キー.番号」（番号は SelectItem.Index）を当てる。
//  【部品の登録を待つ】部品は OnStart で登録簿（UiRegistry）に載るまで引けない（スクリプトの OnStart の順は決まっていない）。
//  登録簿が変わるたびに当てる先を探し直し、種類が替われば当て直す（選択のグループは項目が増えるので毎回当て直す）。
//  しばらく（MissingTargetGraceFrames フレーム）待っても当てる先が無ければ 1 度だけ警告する。
// ============================================================

/// <summary>部品か Text の文字へ多言語の文字を当てる。</summary>
public sealed class LocalizedLabel : LocalizedBinding
{
    /// <summary>当てる先が無いまま待つフレームの数（これを過ぎたら 1 度だけ警告）。</summary>
    public const int MissingTargetGraceFrames = 30;

    /// <summary>最後に見た登録簿の版（-1 = まだ）。</summary>
    private int _registryVersion = -1;

    /// <summary>当てる先が無いまま過ぎたフレームの数。</summary>
    private int _framesWithoutTarget;

    /// <summary>当てる先が無いことを警告したか。</summary>
    private bool _warnedNoTarget;

    /// <inheritdoc />
    protected override ILocalizedTarget? FindTarget(GameObject actor) => LocalizedTargetTable.Find(actor);

    /// <inheritdoc />
    protected override void OnBindingStart() => _registryVersion = UiRegistry.Version;

    /// <inheritdoc />
    protected override void OnBindingUpdate()
    {
        // ── 登録簿が変わった: 部品が後から載った・選択の項目が増えた → 探し直す ──
        if (_registryVersion != UiRegistry.Version)
        {
            _registryVersion = UiRegistry.Version;
            Retarget(reapplySameKind: Target is SelectionLabelTarget);
        }

        // ── 当てる先が無いまま待ちすぎたら 1 度だけ警告 ──
        if (Target is not null || string.IsNullOrEmpty(Key) || _warnedNoTarget) return;
        _framesWithoutTarget++;
        if (_framesWithoutTarget < MissingTargetGraceFrames) return;
        _warnedNoTarget = true;
        Debug.LogWarning($"{LogPrefix} LocalizedLabel: アクタ {Owner.Name} に文字を当てる先がありません（キー {Key}。" +
                         $"当てられる種類: {string.Join("・", LocalizedTargetTable.KindNames)}）");
    }
}
