namespace SEED.UI;

// ============================================================
//  ScreenStack.BackPreview.cs — 画面のスタックの予測型の戻る（受けるかの問い・プレビューの相手。W2 の手直し 3b）
//
//  - 受けるか（WouldHandleBack）: HandleBack と同じ決め方を副作用なしで答える。上の画面が ScreenOptions.IgnoreBack・UiScreen.IgnoreBack・
//    OnBackPressed を上書き（中身は問えないので受けるとみなす）なら受ける、でなければ根より上なら受ける（1 つ下ろす）。
//    出入りの途中も同じ規則（HandleBack は今の動きを終えてから決めるが、並びは積み下ろしの時点で変わっているので答えは同じ）。
//  - プレビューの相手（BackPreviewTarget）: 根より上のとき（戻るを無視する画面・出入りの途中・できあがっていない画面は無し）、
//    いちばん上の画面の枠。OnBackPressed を上書きした画面も縮める（下ろさなければ確定の後に BackPreview が元へ戻す）。
//    姿勢は枠の VisualScale（真ん中の周り）と Translate（指の向きへのずらし）。
//    下の画面の実体が隠れていればプレビューの間だけ見せる（KeepState = false で手放した画面は無い＝背景のまま）。
//    確定して下ろすときは、見せていた下の画面を視差の位置へ跳ばさず 0 のまま見せる（Run.FromPreview）。取り消したら隠し直す。
// ============================================================

public sealed partial class ScreenStack
{
    /// <summary>プレビューの無い段の番号（段の番号は 0 以上）。</summary>
    private const int NoPreviewEntry = -1;

    /// <summary>1 つ下の段の添字のずれ（いちばん上の段の 1 つ下）。</summary>
    private const int BelowTopOffset = 2;

    /// <summary>プレビュー中（確定して下ろしている途中を含む）の段の番号（Enqueue が Run.FromPreview を決める）。</summary>
    private int _previewEntryId = NoPreviewEntry;

    /// <summary>部品が消えたか（プレビューの相手を無効にする）。</summary>
    private bool _destroyed;

    /// <inheritdoc />
    bool INavigator.WouldHandleBack()
    {
        if (_model.Top is not { } top) return false;
        if (top.Options.IgnoreBack) return true;
        if (Get(top)?.Screen is { } screen && screen.MayConsumeBack()) return true;
        return _model.CanPop;
    }

    /// <inheritdoc />
    IBackPreviewTarget? INavigator.BackPreviewTarget
    {
        get
        {
            if (_destroyed || IsTransitioning || !_model.CanPop || _model.Top is not { } top || top.Options.IgnoreBack) return null;
            if (Get(top) is not { Phase: BuildPhase.Ready } instance || !instance.Frame.IsValid) return null;
            // 戻るを無視する画面は縮めない（押しても何も起きない）。OnBackPressed を上書きした画面は縮める
            // （未保存の確認などで下ろさなければ、BackPreview が確定の後に元の姿勢へ戻す）
            if (instance.Screen is { IgnoreBack: true }) return null;
            var below = _model.Entries[_model.Count - BelowTopOffset];
            return new StackPreview(this, top, instance.Frame, Get(below)?.Frame ?? new GameObject(Entity.None));
        }
    }

    /// <summary>出入りが、プレビュー中の段を下ろす Pop か（見せていた下の画面を動かさない）。</summary>
    private bool IsPreviewedPop(ScreenChange change) =>
        _previewEntryId != NoPreviewEntry && change.Kind == ScreenOpKind.Pop && change.Outgoing?.Id == _previewEntryId;

    /// <summary>画面のスタックのプレビューの相手（いちばん上の段 1 つ。手ぶりごとに作る）。</summary>
    private sealed class StackPreview : IBackPreviewTarget
    {
        /// <summary>持ち主のスタック。</summary>
        private readonly ScreenStack _stack;
        /// <summary>縮める段。</summary>
        private readonly ScreenEntry _entry;
        /// <summary>縮める段の枠。</summary>
        private readonly GameObject _frame;
        /// <summary>1 つ下の段の枠（実体が無ければ無効）。</summary>
        private readonly GameObject _below;
        /// <summary>プレビューのために下の枠を見せたか。</summary>
        private bool _shownBelow;

        public StackPreview(ScreenStack stack, ScreenEntry entry, GameObject frame, GameObject below)
        {
            _stack = stack;
            _entry = entry;
            _frame = frame;
            _below = below;
        }

        /// <inheritdoc />
        /// <remarks>同じ段がいちばん上のまま・出入りの途中でない・スタックが見えている（選んでいないタブ・覆われた画面の中ではない）。</remarks>
        public bool IsBackPreviewValid =>
            !_stack._destroyed
            && !_entry.Removed
            && ReferenceEquals(_stack._model.Top, _entry)
            && !_stack.IsTransitioning
            && _frame.IsValid
            && NavigatorRegistry.IsActiveNode(_stack.Owner);

        /// <inheritdoc />
        /// <remarks>下ろされて（並びから外れて）、実体を消すまでの出入りの途中。</remarks>
        public bool IsBackPreviewExiting => !_stack._destroyed && _entry.Removed && _stack._instances.ContainsKey(_entry.Id);

        /// <inheritdoc />
        public void ApplyBackPreview(BackPreviewPose pose)
        {
            _stack._previewEntryId = _entry.Id;
            NavNode.SetVisualScale(_frame, new Vector2(pose.Scale, pose.Scale));
            NavNode.SetTranslate(_frame, new Vector2(pose.ShiftX, 0f));
            ShowBelow();
        }

        /// <inheritdoc />
        public void ClearBackPreview()
        {
            NavNode.SetVisualScale(_frame, Vector2.One);
            NavNode.SetTranslate(_frame, Vector2.Zero);
            if (_stack._previewEntryId == _entry.Id) _stack._previewEntryId = NoPreviewEntry;
            HideBelow();
        }

        /// <summary>下の画面の実体が隠れていれば見せる（1 度だけ）。</summary>
        private void ShowBelow()
        {
            if (_shownBelow || !_below.IsValid || _below.Visible) return;
            _shownBelow = true;
            NavNode.SetVisible(_below, true);
        }

        /// <summary>見せた下の画面を隠し直す（下ろした・並びが変わったなら、落ち着いた状態〈Settle〉に任せて触らない）。</summary>
        private void HideBelow()
        {
            if (!_shownBelow) return;
            _shownBelow = false;
            if (_entry.Removed || _stack.IsTransitioning || !ReferenceEquals(_stack._model.Top, _entry)) return;
            NavNode.SetVisible(_below, false);
        }
    }
}
