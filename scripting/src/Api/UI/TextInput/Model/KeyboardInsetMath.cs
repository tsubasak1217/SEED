using System;

namespace SEED.UI;

// ============================================================
//  KeyboardInsetMath.cs — ソフトキーボードを避けるための計算（W2-6b。純粋な計算。editor/tests/UiComponentsTests で検算）
//
//  座標は画面の画素（左上が原点・Y 下向き。CanvasTransform.LayoutRect・Screen.Height・TextInput.KeyboardHeight と同じ）。
//  キーボードは描画面の上に重なる（描画面は縮まない。Android の GameTextInput は窓を setDecorFitsSystemWindows(false) にする）ので、
//  フォーカスのある欄がキーボードの上に見えるように、部品の側で
//    - スクロールの中の欄: 窓のうちキーボードに隠れる高さを中身の末尾の余白（CanvasScroll.EndInset）にして、欄がキーボードの上の
//      余白の位置に来るまでスクロールする（中身の最後までキーボードの上へスクロールできる）
//    - ダイアログの中の欄: 札をキーボードの上へ持ち上げる（ただし札の上端が画面の上の限り〈安全領域の上端 ＋ 余白〉を越えない）
//  を行う（KeyboardAvoidance.cs）。ここはその量の計算だけ。
// ============================================================

/// <summary>キーボードを避ける量の計算。</summary>
public static class KeyboardInsetMath
{
    /// <summary>キーボードの上端（画面の画素。キーボードが無ければ画面の下端）。</summary>
    public static float KeyboardTop(float screenHeight, float keyboardHeight)
        => screenHeight - Math.Max(0f, float.IsFinite(keyboardHeight) ? keyboardHeight : 0f);

    /// <summary>
    /// 欄をキーボードの上の余白の位置へ出すのに要る上へのずらし（画素。0 以上）。欄の下端 ＋ 余白がキーボードの上端を越えた分。
    /// </summary>
    public static float OverlapPx(Rect field, float keyboardTop, float gapPx)
        => Math.Max(0f, field.YMax + Math.Max(0f, gapPx) - keyboardTop);

    /// <summary>窓（スクロールの窓の画面の矩形）のうちキーボードに隠れる高さ（画素。0〜窓の高さ）。</summary>
    public static float HiddenPx(Rect viewport, float keyboardTop)
        => Math.Clamp(viewport.YMax - keyboardTop, 0f, Math.Max(0f, viewport.height));

    /// <summary>画素 → キャンバスの単位（1 単位の画素数が使えなければ画素のまま）。</summary>
    public static float PxToUnits(float px, float pxPerUnit)
        => pxPerUnit > 0f && float.IsFinite(pxPerUnit) ? px / pxPerUnit : px;

    /// <summary>1 単位の画素数（画面の矩形の長さ ÷ レイアウトの長さ。使えなければ 1）。</summary>
    public static float PxPerUnit(float screenLength, float layoutLength)
        => layoutLength > 0f && screenLength > 0f && float.IsFinite(screenLength / layoutLength) ? screenLength / layoutLength : 1f;

    /// <summary>
    /// ダイアログの札を持ち上げる量（画素。0 以上）。札の下端 ＋ 余白がキーボードの上端を越えた分だけ上げるが、
    /// 札の上端が <paramref name="topLimit"/> より上へは行かない（札がキーボードの上に収まらなければ上端に留める）。
    /// </summary>
    /// <param name="card">持ち上げる前の札の画面の矩形。</param>
    /// <param name="keyboardTop">キーボードの上端。</param>
    /// <param name="gapPx">キーボードとの余白。</param>
    /// <param name="topLimit">札の上端の限り（安全領域の上端 ＋ 余白）。</param>
    public static float LiftPx(Rect card, float keyboardTop, float gapPx, float topLimit)
    {
        float needed = OverlapPx(card, keyboardTop, gapPx);
        float room = Math.Max(0f, card.y - topLimit);
        return Math.Min(needed, room);
    }
}
