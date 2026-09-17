// ============================================================
//  AccountSettings.cs — SEED アカウントまわりの数値・固定文字列の唯一の置き場
//
//  【役割】
//  ポート番号・タイムアウト・鍵導出の反復回数・ファイル名・プロトコル文字列といった
//  「契約で決まっている値」と「調整しうる値」を 1 か所へ集める。
//  実装ファイルの中に数値や文字列リテラルを直接書かない（マジックナンバー禁止）。
//
//  【契約の正典】
//  docs/seed_accounts.md。ここの定数はその表を写したものなので、
//  契約が変わったら **このファイルだけ** を直せば全体が追随する。
//
//  【なぜ定数とインスタンスに分かれているのか】
//  定数（const）… 契約で決まっていて実行時に変えないもの（署名の接頭辞・既定ポート）。
//  インスタンス … テストや利用者設定で差し替えたいもの（タイムアウト・更新猶予）。
//  静的定数だけにすると、単体テストで短いタイムアウトへ差し替えられない。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;

namespace SEEDEditor.Accounts;

/// <summary>
/// アカウント機能の動作パラメータ（不変）と、契約で固定された定数。
/// </summary>
public sealed class AccountSettings
{
    // ── 契約で固定された値（docs/seed_accounts.md）──────────────

    /// <summary>発行窓口（seed-auth）の既定ポート。契約 3 章。</summary>
    public const int DEFAULT_AUTH_PORT = 41350;

    /// <summary>Lore 本体（loreserver）の既定ポート。クローン先 URL の組み立てに使う。</summary>
    public const int DEFAULT_LORE_PORT = 41337;

    /// <summary>Lore のリモート URL のスキーム（`lore://host:port/<project>`）。</summary>
    public const string LORE_URL_SCHEME = "lore";

    /// <summary>発行窓口の URL スキーム（平文 HTTP。契約 7 章の既知の制約）。</summary>
    public const string AUTH_URL_SCHEME = "http";

    /// <summary>
    /// ログインチャレンジに署名する文字列の接頭辞。
    /// 実際に署名するのは <c>seed-auth-login:v1:&lt;challenge_id&gt;:&lt;nonce&gt;</c>（契約 2 章）。
    /// </summary>
    public const string LOGIN_SIGNATURE_PREFIX = "seed-auth-login:v1";

    /// <summary>署名対象文字列の区切り文字。</summary>
    public const char LOGIN_SIGNATURE_SEPARATOR = ':';

    /// <summary>名前の最小文字数（契約 2 章）。</summary>
    public const int NAME_MIN_LENGTH = 1;

    /// <summary>名前の最大文字数（契約 2 章。数えるのは Unicode コードポイント）。</summary>
    public const int NAME_MAX_LENGTH = 32;

    /// <summary>公開鍵（SEC1 非圧縮点）のバイト数（`0x04 ‖ X ‖ Y`）。</summary>
    public const int PUBLIC_KEY_BYTE_LENGTH = 65;

    /// <summary>SEC1 非圧縮点の先頭バイト。</summary>
    public const byte PUBLIC_KEY_UNCOMPRESSED_PREFIX = 0x04;

    /// <summary>P-256 の座標 1 つ分のバイト数。</summary>
    public const int P256_COORDINATE_BYTE_LENGTH = 32;

    /// <summary>署名（IEEE P1363 固定長 r ‖ s）のバイト数。</summary>
    public const int SIGNATURE_BYTE_LENGTH = 64;

    /// <summary>参加者の役割: オーナー。</summary>
    public const string ROLE_OWNER = "owner";

    /// <summary>参加者の役割: 一般参加者。</summary>
    public const string ROLE_MEMBER = "member";

    /// <summary>参加者の状態: 有効。</summary>
    public const string MEMBER_STATUS_ACTIVE = "active";

    /// <summary>参加者の状態: 失効済み。</summary>
    public const string MEMBER_STATUS_REVOKED = "revoked";

    // ── 保管（ローカル）──────────────────────────────────────

    /// <summary>アカウントの保管フォルダを差し替える環境変数（テスト用）。</summary>
    public const string ENV_ACCOUNT_DIR = "SEED_ACCOUNT_DIR";

    /// <summary>`%APPDATA%` 直下に作るアプリフォルダ名。</summary>
    public const string APPDATA_APP_DIR_NAME = "SEED";

    /// <summary>アプリフォルダ直下のアカウントフォルダ名。</summary>
    public const string ACCOUNT_DIR_NAME = "account";

    /// <summary>アカウントファイル名。</summary>
    public const string ACCOUNT_FILE_NAME = "account.json";

    /// <summary>原子的な書き込みに使う一時ファイルの拡張子（tmp → rename）。</summary>
    public const string TEMP_FILE_SUFFIX = ".tmp";

    /// <summary>書き出しファイルの既定拡張子。</summary>
    public const string EXPORT_FILE_EXTENSION = ".seedaccount";

    // ── 書き出しの暗号（パスフレーズ）────────────────────────

    /// <summary>書き出しファイルの形式識別子。</summary>
    public const string EXPORT_FORMAT_ID = "seed-account-export";

    /// <summary>書き出しファイルの形式版。</summary>
    public const int EXPORT_FORMAT_VERSION = 1;

    /// <summary>書き出しに使う鍵導出関数の名前（ファイルへ書いて将来の移行に備える）。</summary>
    public const string EXPORT_KDF_NAME = "PBKDF2-HMAC-SHA256";

    /// <summary>
    /// PBKDF2 の反復回数。
    /// 書き出し・読み込みは稀にしか行わないので、体感より安全側に倒す。
    /// </summary>
    public const int EXPORT_KDF_ITERATIONS = 600_000;

    /// <summary>PBKDF2 のソルト長 [byte]。</summary>
    public const int EXPORT_SALT_BYTE_LENGTH = 16;

    /// <summary>AES-GCM の鍵長 [byte]（AES-256）。</summary>
    public const int EXPORT_KEY_BYTE_LENGTH = 32;

    /// <summary>AES-GCM のノンス長 [byte]（GCM の推奨値）。</summary>
    public const int EXPORT_NONCE_BYTE_LENGTH = 12;

    /// <summary>AES-GCM の認証タグ長 [byte]。</summary>
    public const int EXPORT_TAG_BYTE_LENGTH = 16;

    /// <summary>パスフレーズの最小文字数（空のまま書き出させないための下限）。</summary>
    public const int EXPORT_PASSPHRASE_MIN_LENGTH = 8;

    /// <summary>
    /// DPAPI（CurrentUser）へ渡す追加エントロピーの元文字列。
    /// 同じ PC の別アプリが偶然復号できてしまわないよう、用途を混ぜる。
    /// **変更するとそれ以前に保存したアカウントが読めなくなる。**
    /// </summary>
    public const string DPAPI_ENTROPY_SEED = "seed-account:v1";

    // ── 既定値（インスタンス側）──────────────────────────────

    /// <summary>発行窓口への HTTP 要求の既定タイムアウト [ms]。</summary>
    public const int DEFAULT_HTTP_TIMEOUT_MS = 15_000;

    /// <summary>
    /// 窓口があるかを確かめる <c>GET /v1/health</c> の既定タイムアウト [ms]。
    /// プロジェクトを開くたびに走るので、無い環境で待たせないよう短くする。
    /// </summary>
    public const int DEFAULT_HEALTH_TIMEOUT_MS = 3_000;

    /// <summary>
    /// トークンの期限の何ミリ秒手前で取り直すか（既定 10 分）。
    /// 契約 7 章のとおり Lore の応答から期限切れを判別できないため、先回りで更新する。
    /// </summary>
    public const int DEFAULT_TOKEN_REFRESH_MARGIN_MS = 10 * 60 * 1_000;

    /// <summary>
    /// 自動更新に失敗したときの再試行間隔 [ms]（既定 1 分）。
    /// 期限まではまだ猶予があるので、諦めずに一定間隔で試し続ける。
    /// </summary>
    public const int DEFAULT_TOKEN_RETRY_INTERVAL_MS = 60 * 1_000;

    /// <summary>
    /// 自動更新をかける最短の待ち時間 [ms]。
    /// サーバが極端に短い期限のトークンを返したときに、
    /// 更新が連続実行され続ける（事実上の無限ループ）のを防ぐ下限。
    /// </summary>
    public const int DEFAULT_MIN_TOKEN_REFRESH_DELAY_MS = 1_000;

    /// <summary>招待コードの既定有効時間 [hour]（契約 3 章の既定値）。</summary>
    public const int DEFAULT_INVITE_EXPIRES_IN_HOURS = 72;

    /// <summary>招待コードの有効時間の下限 [hour]（契約 3 章。これ未満は 400 で断られる）。</summary>
    public const int MIN_INVITE_EXPIRES_IN_HOURS = 1;

    /// <summary>招待コードの有効時間の上限 [hour]（契約 3 章。30 日）。</summary>
    public const int MAX_INVITE_EXPIRES_IN_HOURS = 720;

    // ── プロパティ ──────────────────────────────────────────

    /// <summary>発行窓口への HTTP 要求のタイムアウト。</summary>
    public TimeSpan HttpTimeout { get; }

    /// <summary>窓口の有無を確かめる要求のタイムアウト。</summary>
    public TimeSpan HealthTimeout { get; }

    /// <summary>トークンの期限の何秒手前で取り直すか。</summary>
    public TimeSpan TokenRefreshMargin { get; }

    /// <summary>自動更新に失敗したときの再試行間隔。</summary>
    public TimeSpan TokenRetryInterval { get; }

    /// <summary>自動更新をかける最短の待ち時間。</summary>
    public TimeSpan MinTokenRefreshDelay { get; }

    /// <summary>招待コードの既定有効時間。</summary>
    public int InviteExpiresInHours { get; }

    // ── 生成 ────────────────────────────────────────────────

    /// <summary>
    /// 設定値を指定して生成する。省略した項目は既定値になる。
    /// </summary>
    /// <param name="httpTimeout">発行窓口への要求のタイムアウト。</param>
    /// <param name="healthTimeout">窓口の有無を確かめる要求のタイムアウト。</param>
    /// <param name="tokenRefreshMargin">トークンを取り直す期限手前の猶予。</param>
    /// <param name="tokenRetryInterval">自動更新失敗時の再試行間隔。</param>
    /// <param name="inviteExpiresInHours">招待コードの既定有効時間。</param>
    /// <param name="minTokenRefreshDelay">自動更新をかける最短の待ち時間。</param>
    public AccountSettings(
        TimeSpan? httpTimeout          = null,
        TimeSpan? healthTimeout        = null,
        TimeSpan? tokenRefreshMargin   = null,
        TimeSpan? tokenRetryInterval   = null,
        int?      inviteExpiresInHours = null,
        TimeSpan? minTokenRefreshDelay = null)
    {
        HttpTimeout          = httpTimeout
            ?? TimeSpan.FromMilliseconds(DEFAULT_HTTP_TIMEOUT_MS);
        HealthTimeout        = healthTimeout
            ?? TimeSpan.FromMilliseconds(DEFAULT_HEALTH_TIMEOUT_MS);
        TokenRefreshMargin   = tokenRefreshMargin
            ?? TimeSpan.FromMilliseconds(DEFAULT_TOKEN_REFRESH_MARGIN_MS);
        TokenRetryInterval   = tokenRetryInterval
            ?? TimeSpan.FromMilliseconds(DEFAULT_TOKEN_RETRY_INTERVAL_MS);
        MinTokenRefreshDelay = minTokenRefreshDelay
            ?? TimeSpan.FromMilliseconds(DEFAULT_MIN_TOKEN_REFRESH_DELAY_MS);

        // 契約の範囲（1〜720）へ丸める。範囲外を送るとサーバが 400 で断るので、
        // 設定の書き間違いを「招待コードが発行できない」にしない。
        InviteExpiresInHours = Math.Clamp(
            inviteExpiresInHours ?? DEFAULT_INVITE_EXPIRES_IN_HOURS,
            MIN_INVITE_EXPIRES_IN_HOURS, MAX_INVITE_EXPIRES_IN_HOURS);
    }

    /// <summary>既定値だけで構成した設定。</summary>
    public static AccountSettings Default { get; } = new();
}
