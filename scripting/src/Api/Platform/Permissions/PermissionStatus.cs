namespace SEED.Platform;

/// <summary>権限の状態（<see cref="Permissions.Check"/>・<see cref="PermissionResultEvent"/>・<see cref="PermissionChangedEvent"/>。W1-5）。</summary>
public enum PermissionStatus
{
    /// <summary>分からない（<see cref="Permissions.Check"/> が失敗した〈<see cref="Platform.LastError"/> に理由〉・知らない状態の文字列）。</summary>
    Unknown,

    /// <summary>"granted": 許可されている。</summary>
    Granted,

    /// <summary>"denied": 許可されていない（<see cref="Permissions.Request"/> でもう一度求めれば確認の画面が出る見込み）。</summary>
    Denied,

    /// <summary>"denied_permanently": 許可されず、求めても確認の画面が出ない（<see cref="Permissions.OpenSettings"/> で設定の画面へ案内する）。</summary>
    DeniedPermanently,

    /// <summary>"needs_settings": 設定の画面で利用者が切り替える種類で、今は切られている（<see cref="Permissions.Request"/> は設定の画面を開く）。</summary>
    NeedsSettings,

    /// <summary>"not_applicable": この OS の版・この段階では要らない（扱わない）。</summary>
    NotApplicable,
}
