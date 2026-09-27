// ============================================================
//  redraw/policy.rs — 描き方の方針（render_policy）と、止める判定の数（render_idle_frames）【純関数・単体テスト付き】
//
//  【project_settings.json のキー】（正典は docs/redraw_policy.md §2・docs/project_system.md）
//    "render_policy"      : "continuous"（既定。今までどおり毎フレーム描く）| "on_demand"（描く理由が無ければ止める）
//    "render_idle_frames" : on_demand で、描く理由の無いフレームがこの回数だけ続いたら止める（既定 10・1〜600）
//  どちらも無ければ既定＝今までどおり毎フレーム描く（既存のゲームの動きを変えない）。
//
//  【読めない値】起動を止めない。既定へ倒し、警告の文を返す（呼び出し側が起動ログへ出す）。
//  文字列は前後の空白を落とし、大文字小文字を区別しない（render_resolution_mode と同じ流儀）。
// ============================================================

use serde_json::Value;

/// project_settings.json のキー（描き方の方針）。
pub const KEY_RENDER_POLICY: &str = "render_policy";

/// project_settings.json のキー（止めるまでの理由の無いフレームの数）。
pub const KEY_RENDER_IDLE_FRAMES: &str = "render_idle_frames";

/// `RenderPolicy::Continuous` の文字列表現。
pub const POLICY_NAME_CONTINUOUS: &str = "continuous";

/// `RenderPolicy::OnDemand` の文字列表現。
pub const POLICY_NAME_ON_DEMAND: &str = "on_demand";

/// 止めるまでの理由の無いフレームの数の既定値。
///
/// 描く理由が消えた後も少しだけ描き続けるのは、理由の申告の漏れ（1〜2 フレーム遅れて見た目に出る変化・
/// 生成したスクリプトの Start が次のフレームで走る等）で最後の見た目が古いまま止まるのを避けるため。
/// 60 fps で約 0.17 秒。W2-0 の試作は 30（0.5 秒）で試した（roadmap §3.8.7 の R-12）。数値は W2-10 で詰める。
pub const DEFAULT_IDLE_AFTER_FRAMES: u32 = 10;

/// 止めるまでのフレームの数の下限（0 は「描いたそばから止める」になり、理由の無いフレームを 1 枚も描けないため認めない）。
pub const MIN_IDLE_AFTER_FRAMES: u32 = 1;

/// 止めるまでのフレームの数の上限（60 fps で 10 秒。これより長く待つなら continuous と変わらない）。
pub const MAX_IDLE_AFTER_FRAMES: u32 = 600;

/// FFI の番号（C# の `SEED.RedrawPolicy` の値と一致させる）: 常に描く。
pub const POLICY_CODE_CONTINUOUS: i32 = 0;

/// FFI の番号（C# の `SEED.RedrawPolicy` の値と一致させる）: 描く理由があるときだけ描く。
pub const POLICY_CODE_ON_DEMAND: i32 = 1;

/// 描き方の方針。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum RenderPolicy {
    /// 毎フレーム描く（既定・今までの動き。ゲーム向け）。
    #[default]
    Continuous,
    /// 描く理由があるときだけ描く（止まっている画面の多いアプリ向け。W2-10a）。
    OnDemand,
}

impl RenderPolicy {
    /// 設定ファイル・ログで使う文字列表現。
    pub const fn as_str(self) -> &'static str {
        match self {
            RenderPolicy::Continuous => POLICY_NAME_CONTINUOUS,
            RenderPolicy::OnDemand => POLICY_NAME_ON_DEMAND,
        }
    }

    /// 文字列表現から変換する（前後の空白を落とし、大文字小文字を区別しない）。知らない値は None。
    pub fn parse_name(text: &str) -> Option<Self> {
        match text.trim().to_ascii_lowercase().as_str() {
            POLICY_NAME_CONTINUOUS => Some(RenderPolicy::Continuous),
            POLICY_NAME_ON_DEMAND => Some(RenderPolicy::OnDemand),
            _ => None,
        }
    }

    /// FFI の番号（C# の `SEED.RedrawPolicy`）。
    pub const fn to_code(self) -> i32 {
        match self {
            RenderPolicy::Continuous => POLICY_CODE_CONTINUOUS,
            RenderPolicy::OnDemand => POLICY_CODE_ON_DEMAND,
        }
    }

    /// FFI の番号から変換する。知らない番号は None。
    pub const fn from_code(code: i32) -> Option<Self> {
        match code {
            POLICY_CODE_CONTINUOUS => Some(RenderPolicy::Continuous),
            POLICY_CODE_ON_DEMAND => Some(RenderPolicy::OnDemand),
            _ => None,
        }
    }
}

/// 描き方の設定（project_settings.json から起動時に 1 回読む）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RedrawSettings {
    /// 描き方の方針（スクリプトの `SEED.Redraw.Policy` で実行中に上書きできる）。
    pub policy: RenderPolicy,
    /// on_demand で、描く理由の無いフレームがこの回数だけ続いたら止める。
    pub idle_after_frames: u32,
}

impl Default for RedrawSettings {
    fn default() -> Self {
        Self { policy: RenderPolicy::default(), idle_after_frames: DEFAULT_IDLE_AFTER_FRAMES }
    }
}

/// 設定の読み取りの結果（読めなかった項目の警告つき）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ParsedRedrawSettings {
    /// 読めた設定（読めない項目は既定値）。
    pub settings: RedrawSettings,
    /// 読めなかった項目の警告の文（呼び出し側が起動ログへ出す）。
    pub warnings: Vec<String>,
}

/// project_settings.json のテキストから描き方の設定を読む【純関数】。
///
/// JSON が壊れている・キーが無いときは既定（continuous）で、警告も出さない（壊れた JSON は他の設定の読み取りが知らせる）。
/// キーはあるが値が読めないときは、その項目を既定にして警告の文を返す。
pub fn parse_redraw_settings(settings_json: &str) -> ParsedRedrawSettings {
    let mut parsed = ParsedRedrawSettings { settings: RedrawSettings::default(), warnings: Vec::new() };
    let Ok(root) = serde_json::from_str::<Value>(settings_json) else {
        return parsed;
    };
    match root.get(KEY_RENDER_POLICY) {
        None | Some(Value::Null) => {}
        Some(Value::String(name)) => match RenderPolicy::parse_name(name) {
            Some(policy) => parsed.settings.policy = policy,
            None => parsed.warnings.push(format!(
                "{KEY_RENDER_POLICY} は \"{POLICY_NAME_CONTINUOUS}\" か \"{POLICY_NAME_ON_DEMAND}\" にしてください（受け取った値: {name:?}。{POLICY_NAME_CONTINUOUS} として動きます）"
            )),
        },
        Some(other) => parsed.warnings.push(format!(
            "{KEY_RENDER_POLICY} は文字列にしてください（受け取った値: {other}。{POLICY_NAME_CONTINUOUS} として動きます）"
        )),
    }
    match root.get(KEY_RENDER_IDLE_FRAMES) {
        None | Some(Value::Null) => {}
        Some(value) => match value.as_u64().and_then(|n| u32::try_from(n).ok()) {
            Some(frames) if (MIN_IDLE_AFTER_FRAMES..=MAX_IDLE_AFTER_FRAMES).contains(&frames) => {
                parsed.settings.idle_after_frames = frames;
            }
            _ => parsed.warnings.push(format!(
                "{KEY_RENDER_IDLE_FRAMES} は {MIN_IDLE_AFTER_FRAMES}〜{MAX_IDLE_AFTER_FRAMES} の整数にしてください（受け取った値: {value}。{DEFAULT_IDLE_AFTER_FRAMES} として動きます）"
            )),
        },
    }
    parsed
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// キーが無い・壊れた JSON は既定（continuous・10 フレーム）で、警告も無い＝既存のプロジェクトの動きは変わらない。
    #[test]
    fn missing_or_broken_json_is_continuous_without_warnings() {
        for json in ["", "not json", "{}", r#"{"target_fps":60}"#, r#"{"render_policy":null}"#] {
            let parsed = parse_redraw_settings(json);
            assert_eq!(parsed.settings, RedrawSettings::default(), "json={json:?}");
            assert_eq!(parsed.settings.policy, RenderPolicy::Continuous);
            assert!(parsed.warnings.is_empty(), "json={json:?} warnings={:?}", parsed.warnings);
        }
    }

    /// 方針と数を読む（空白・大文字小文字は問わない）。
    #[test]
    fn reads_policy_and_idle_frames() {
        let parsed = parse_redraw_settings(r#"{"render_policy":" On_Demand ","render_idle_frames":30}"#);
        assert!(parsed.warnings.is_empty(), "{:?}", parsed.warnings);
        assert_eq!(parsed.settings.policy, RenderPolicy::OnDemand);
        assert_eq!(parsed.settings.idle_after_frames, 30);
        let parsed = parse_redraw_settings(r#"{"render_policy":"continuous"}"#);
        assert_eq!(parsed.settings.policy, RenderPolicy::Continuous);
        assert_eq!(parsed.settings.idle_after_frames, DEFAULT_IDLE_AFTER_FRAMES);
    }

    /// 読めない値は既定へ倒して警告（他の項目は活かす）。
    #[test]
    fn bad_values_fall_back_with_warnings() {
        let parsed = parse_redraw_settings(r#"{"render_policy":"sometimes","render_idle_frames":0}"#);
        assert_eq!(parsed.settings, RedrawSettings::default());
        assert_eq!(parsed.warnings.len(), 2, "{:?}", parsed.warnings);
        let parsed = parse_redraw_settings(r#"{"render_policy":1,"render_idle_frames":601}"#);
        assert_eq!(parsed.settings, RedrawSettings::default());
        assert_eq!(parsed.warnings.len(), 2, "{:?}", parsed.warnings);
        let parsed = parse_redraw_settings(r#"{"render_policy":"on_demand","render_idle_frames":"30"}"#);
        assert_eq!(parsed.settings.policy, RenderPolicy::OnDemand, "読めた項目は有効");
        assert_eq!(parsed.settings.idle_after_frames, DEFAULT_IDLE_AFTER_FRAMES);
        assert_eq!(parsed.warnings.len(), 1, "{:?}", parsed.warnings);
        // 下限・上限はちょうどなら受け付ける
        let parsed = parse_redraw_settings(r#"{"render_idle_frames":1}"#);
        assert_eq!(parsed.settings.idle_after_frames, MIN_IDLE_AFTER_FRAMES);
        let parsed = parse_redraw_settings(r#"{"render_idle_frames":600}"#);
        assert_eq!(parsed.settings.idle_after_frames, MAX_IDLE_AFTER_FRAMES);
    }

    /// 名前・FFI の番号の往復（知らない番号・名前は None）。
    #[test]
    fn names_and_codes_round_trip() {
        for policy in [RenderPolicy::Continuous, RenderPolicy::OnDemand] {
            assert_eq!(RenderPolicy::parse_name(policy.as_str()), Some(policy));
            assert_eq!(RenderPolicy::from_code(policy.to_code()), Some(policy));
        }
        assert_eq!(RenderPolicy::parse_name("ondemand"), None);
        assert_eq!(RenderPolicy::from_code(2), None);
        assert_eq!(RenderPolicy::from_code(-1), None);
    }
}
