namespace SEED.Localization;

// ============================================================
//  MissingKeyPolicy.cs — どの言語の表にも無いキーを引いたときに返す文の方針（純粋）
//
//  探す順（今の言語 → fallback → 既定の言語）のどこにも無いキーを Get / Plural で引いたとき:
//    Key    … キーをそのまま返す（"menu.start"。配布用のビルドの既定＝画面が空にならない）
//    Marked … キーを [ ] で囲んで返す（"[menu.start]"。開発中の既定＝欠けが一目で分かる）
//    Empty  … 空文字を返す
//  欠けはキーごとに 1 回だけ警告のログを出す（LocaleCatalog）。既定の選び方は MissingKeyText.DefaultPolicy。
// ============================================================

/// <summary>どの言語の表にも無いキーを引いたときに返す文の方針。</summary>
public enum MissingKeyPolicy
{
    /// <summary>キーをそのまま返す（配布用のビルドの既定）。</summary>
    Key,
    /// <summary>キーを [ ] で囲んで返す（開発中の既定）。</summary>
    Marked,
    /// <summary>空文字を返す。</summary>
    Empty,
}
