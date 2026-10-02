namespace SEED.UI;

// ============================================================
//  Dialog.Input.cs — ダイアログの 1 行の入力欄（W2-6b）とキーボードを避ける持ち上げ
//
//  DialogOptions.Input があれば、札の Input の枠へ入力欄（text_field.actor）を作り、部品のスクリプトが始まったら中身を当てて
//  フォーカスする。入力欄の大きさは札の中の幅 × size.field_height（2026-10-02 の直し: 以前は札を割り付けるときに作ったばかりの
//  入力欄〈まだできあがっていない〉へ書いていたので効かず、入力欄がプレハブの 280 のまま枠の 264 から右へ 16 はみ出した。
//  Wake or Pay の W3-3 (1)。今は入力欄のスクリプトが始まったとき〈BindInput〉と割り付けるたびに当てる。入力欄は自分の大きさの変化を
//  見て中身を置き直す〈TextField の WatchFieldSize〉）。
// ============================================================

public sealed partial class Dialog
{
    /// <summary>入力欄の枠の子の名前（W2-6b）。</summary>
    private const string InputChild = "Card/Input";
    /// <summary>入力欄のプレハブ（W2-6b）。</summary>
    private const string TextFieldPrefab = "assets://ui/prefabs/text_field.actor";

    /// <summary>入力欄のノード（W2-6b。入力が無ければ無効）。</summary>
    private GameObject _inputNode = new(Entity.None);
    /// <summary>つないだ入力欄（部品の OnStart の後に登録簿から引く）。</summary>
    private TextField? _input;
    /// <summary>札の持ち上げ（キャンバスの単位。キーボードを避ける。0 = 持ち上げない）。</summary>
    private float _liftUnits;
    /// <summary>入力欄に当てる大きさ（札の中の幅 × 欄の高さ。最後の割り付けの値）。</summary>
    private Vector2 _inputSize;

    /// <summary>入力があれば入力欄を札の Input の枠に作る（W2-6b。枠を見せる。中身は部品の OnStart の後に Bind で当てる）。</summary>
    private void CreateInput()
    {
        var slot = gameObject.FindChild(InputChild);
        bool wanted = _options.Input is not null && slot.IsValid;
        if (slot.IsValid) NavNode.SetVisible(slot, wanted);
        if (!wanted) return;
        _inputNode = GameObject.Instantiate(TextFieldPrefab, slot);
        if (!_inputNode.IsValid) Debug.LogWarning($"{LogPrefix} 入力欄のプレハブを作れませんでした（{TextFieldPrefab}）");
    }

    /// <summary>
    /// 入力欄の大きさ（札の中の幅 × 欄の高さ）を覚えて当てる（割り付けのたび。入力欄ができあがる前は BindInput が当てる）。
    /// </summary>
    /// <param name="inner">札の中の幅。</param>
    /// <param name="height">欄の高さ（入力が無ければ 0）。</param>
    private void ApplyInputSize(float inner, float height)
    {
        _inputSize = new Vector2(inner, height);
        FitInputNode();
    }

    /// <summary>入力欄の背景（枠）の Sprite を覚えた大きさにする（できあがっていなければ何もしない。同じなら書かない）。</summary>
    private void FitInputNode()
    {
        if (!_inputNode.IsValid || !(_inputSize.y > 0f) || _inputNode.GetComponent<Sprite>() is not { } background) return;
        if (background.Size != _inputSize) background.Size = _inputSize;
    }

    /// <summary>入力欄をつなぐ（大きさを当て、中身を当て、完了で Positive、開いたらフォーカス）。</summary>
    private void BindInput()
    {
        if (_input is not null || !_inputNode.IsValid || _options.Input is not { } spec) return;
        if (Of<TextField>(_inputNode) is not { } field) return;
        _input = field;
        // 入力欄の幅を札の中の幅へ（プレハブの 280 のままにしない。2026-10-02）
        FitInputNode();
        field.Placeholder = spec.Placeholder;
        field.Kind = spec.Kind;
        field.MaxLength = spec.MaxLength;
        field.AllowPaste = spec.AllowPaste;
        field.UnfocusOnDone = false;
        field.SetText(spec.Text);
        field.Submitted += (_, action) =>
        {
            if (spec.SubmitOnDone && action == TextInputAction.Done) Choose(DialogResult.Positive);
        };
        field.Focus();
    }

    /// <inheritdoc />
    /// <remarks>
    /// 札の下端 ＋ 余白がキーボードの上端を越えた分だけ札を上へずらす（CanvasLayoutItem.Translate。持ち上げる前の矩形で測る）。
    /// 札の上端は安全領域の上端 ＋ 余白より上へは行かない（KeyboardInsetMath.LiftPx）。
    /// </remarks>
    public void ApplyKeyboardLift(float keyboardTopPx, float gapPx)
    {
        if (!_card.IsValid || _card.GetComponent<CanvasTransform>() is not { } t || !t.HasLayout) return;
        var rect = t.LayoutRect;
        float pxPerUnit = KeyboardInsetMath.PxPerUnit(rect.height, t.LayoutSize.y);
        // 今の持ち上げを戻した矩形（画面の画素）で測る（持ち上げた矩形で測ると、持ち上げの分だけ少なく見える）
        var resting = new Rect(rect.x, rect.y + _liftUnits * pxPerUnit, rect.width, rect.height);
        float lift = KeyboardInsetMath.LiftPx(resting, keyboardTopPx, gapPx, Screen.SafeArea.y + gapPx);
        _liftUnits = KeyboardInsetMath.PxToUnits(lift, pxPerUnit);
        NavNode.SetTranslate(_card, new Vector2(0f, -_liftUnits));
        Redraw.Request();
    }

    /// <inheritdoc />
    public void ClearKeyboardLift()
    {
        if (_liftUnits == 0f) return;
        _liftUnits = 0f;
        if (_card.IsValid) NavNode.SetTranslate(_card, Vector2.Zero);
        Redraw.Request();
    }
}
