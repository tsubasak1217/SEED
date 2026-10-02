namespace SEED.Localization;

// ============================================================
//  LocaleSwitchResult.cs — 言語の切り替えの結果（LocaleCatalog.SetLanguage の戻り値。純粋）
//
//  L10n は Changed のときだけ L10n.Changed（SEED.Events）を知らせる。
// ============================================================

/// <summary>言語の切り替えの結果。</summary>
public enum LocaleSwitchResult
{
    /// <summary>一覧に無い言語（切り替えず、保存もしない）。</summary>
    Unknown,
    /// <summary>今の言語と同じ（表は読み直さない。保存はする）。</summary>
    Unchanged,
    /// <summary>切り替えた（表を読み替えた）。</summary>
    Changed,
}
