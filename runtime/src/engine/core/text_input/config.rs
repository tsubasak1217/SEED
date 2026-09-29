// ============================================================
//  text_input/config.rs — 入力欄の設定（入力の種類・完了のアクション・最大の長さ・貼り付けとコピーの許可）【純データ】
//
//  スクリプト（C# の SEED.TextInputOptions）が入力欄にフォーカスを当てるとき（TextInput.Begin）に渡す。
//  番号はスクリプトとの約束（C# の SEED.TextInputKind・TextInputAction と一致させる）。完了のアクションの番号は
//  Android の EditorInfo.IME_ACTION_* と同じ値にしてあり、IME から届いた番号（MainActivity.onEditorAction）をそのまま読める。
// ============================================================

/// 入力の種類（キーボードの種類と、受け付ける文字の絞り込み）。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
#[repr(i32)]
pub enum TextInputKind {
    /// 文字（日本語の変換を含む。Android は TYPE_CLASS_TEXT、Windows は IME を許可する）。
    #[default]
    Text = 0,
    /// 数字だけ（0〜9。全角の数字は半角へ直し、それ以外は捨てる。Android は TYPE_CLASS_NUMBER、
    /// Windows は IME を許可しない＝直接の文字で入る）。
    Number = 1,
}

impl TextInputKind {
    /// すべての種類（番号から読むときの表）。
    pub const ALL: [Self; 2] = [Self::Text, Self::Number];

    /// スクリプトの番号から読む（知らない番号は None）。
    pub fn from_code(code: i32) -> Option<Self> {
        Self::ALL.into_iter().find(|kind| kind.code() == code)
    }

    /// スクリプトとの約束の番号。
    pub fn code(self) -> i32 {
        self as i32
    }

    /// ログ・IPC の応答に出す名前。
    pub fn name(self) -> &'static str {
        match self {
            Self::Text => "text",
            Self::Number => "number",
        }
    }
}

/// 完了などのアクション（ソフトキーボードのアクションのボタン・PC の Enter）。
/// 値は Android の EditorInfo.IME_ACTION_* と同じ。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
#[repr(i32)]
pub enum TextInputAction {
    /// 決めない（IME に任せる）。
    Unspecified = 0,
    /// アクションなし（Enter は何もしない）。
    None = 1,
    /// 移動（Go）。
    Go = 2,
    /// 検索。
    Search = 3,
    /// 送信。
    Send = 4,
    /// 次へ。
    Next = 5,
    /// 完了（既定）。
    #[default]
    Done = 6,
    /// 前へ。
    Previous = 7,
}

impl TextInputAction {
    /// すべてのアクション（番号・名前から読むときの表）。
    pub const ALL: [Self; 8] = [
        Self::Unspecified,
        Self::None,
        Self::Go,
        Self::Search,
        Self::Send,
        Self::Next,
        Self::Done,
        Self::Previous,
    ];

    /// 番号から読む（スクリプトの番号・Android の IME_ACTION_*。知らない番号は None）。
    pub fn from_code(code: i32) -> Option<Self> {
        Self::ALL.into_iter().find(|action| action.code() == code)
    }

    /// 番号（スクリプト・Android の IME_ACTION_* と同じ）。
    pub fn code(self) -> i32 {
        self as i32
    }

    /// ログ・IPC に出す名前（IPC の `INPUT_TEXT:action,<名前>` もこの名前で読む）。
    pub fn name(self) -> &'static str {
        match self {
            Self::Unspecified => "unspecified",
            Self::None => "none",
            Self::Go => "go",
            Self::Search => "search",
            Self::Send => "send",
            Self::Next => "next",
            Self::Done => "done",
            Self::Previous => "previous",
        }
    }

    /// 名前から読む（IPC の注入用。知らない名前は None）。
    pub fn from_name(name: &str) -> Option<Self> {
        Self::ALL.into_iter().find(|action| action.name() == name)
    }

    /// Enter（1 行の入力の確定）でスクリプトへ知らせるアクションか（None なら Enter は何もしない）。
    pub fn fires_on_enter(self) -> bool {
        self != Self::None
    }
}

/// 旗: 貼り付けを禁止する（PC の Ctrl+V・Shift+Insert を捨て、Android は貼り付けに見える一度の大きな挿入を戻す）。
pub const FLAG_DISALLOW_PASTE: i32 = 1;
/// 旗: コピー・切り取りを禁止する（PC の Ctrl+C・Ctrl+X・Ctrl+Insert・Shift+Delete を捨てる）。
pub const FLAG_DISALLOW_COPY: i32 = 2;

/// 最大の長さの「制限なし」を表す FFI の値（0 以下）。
pub const NO_MAX_LENGTH: i32 = 0;

/// 入力欄の設定。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct TextInputConfig {
    /// 入力の種類。
    pub kind: TextInputKind,
    /// 完了などのアクション。
    pub action: TextInputAction,
    /// 最大の長さ（書記素の数。None = 制限なし）。変換中は超えてよく、確定したときに切り詰める（Flutter の
    /// MaxLengthEnforcement.truncateAfterCompositionEnds と同じ）。
    pub max_length: Option<usize>,
    /// 貼り付けを許すか。
    pub allow_paste: bool,
    /// コピー・切り取りを許すか。
    pub allow_copy: bool,
}

impl Default for TextInputConfig {
    fn default() -> Self {
        Self {
            kind: TextInputKind::Text,
            action: TextInputAction::Done,
            max_length: None,
            allow_paste: true,
            allow_copy: true,
        }
    }
}

impl TextInputConfig {
    /// FFI の値から作る（知らない種類・アクションは既定へ落とす。最大の長さは 0 以下なら制限なし）。
    ///
    /// # 引数
    /// * `kind`       - `TextInputKind` の番号
    /// * `action`     - `TextInputAction` の番号
    /// * `max_length` - 最大の長さ（書記素の数。0 以下 = 制限なし）
    /// * `flags`      - `FLAG_*` の組み合わせ
    pub fn from_ffi(kind: i32, action: i32, max_length: i32, flags: i32) -> Self {
        Self {
            kind: TextInputKind::from_code(kind).unwrap_or_default(),
            action: TextInputAction::from_code(action).unwrap_or_default(),
            max_length: usize::try_from(max_length).ok().filter(|&n| n > 0),
            allow_paste: flags & FLAG_DISALLOW_PASTE == 0,
            allow_copy: flags & FLAG_DISALLOW_COPY == 0,
        }
    }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// FFI の値の読み取り（知らない番号は既定・最大の長さは正のときだけ・旗は禁止の向き）。
    #[test]
    fn from_ffi_reads_codes_and_flags() {
        let config = TextInputConfig::from_ffi(1, 5, 8, FLAG_DISALLOW_PASTE);
        assert_eq!(config.kind, TextInputKind::Number);
        assert_eq!(config.action, TextInputAction::Next);
        assert_eq!(config.max_length, Some(8));
        assert!(!config.allow_paste);
        assert!(config.allow_copy);

        let fallback = TextInputConfig::from_ffi(99, 99, NO_MAX_LENGTH, 0);
        assert_eq!(fallback, TextInputConfig::default());
        assert_eq!(TextInputConfig::from_ffi(0, 6, -3, 0).max_length, None);
    }

    /// アクションの番号は Android の IME_ACTION_* と同じ（完了 6・次へ 5）。名前でも往復できる。
    #[test]
    fn action_codes_match_android() {
        assert_eq!(TextInputAction::Done.code(), 6);
        assert_eq!(TextInputAction::Next.code(), 5);
        for action in TextInputAction::ALL {
            assert_eq!(TextInputAction::from_code(action.code()), Some(action));
            assert_eq!(TextInputAction::from_name(action.name()), Some(action));
        }
        assert_eq!(TextInputAction::from_code(99), None);
        assert!(!TextInputAction::None.fires_on_enter());
        assert!(TextInputAction::Done.fires_on_enter());
    }
}
