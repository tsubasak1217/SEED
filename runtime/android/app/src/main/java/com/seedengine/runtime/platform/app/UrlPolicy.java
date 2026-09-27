// ============================================================
//  UrlPolicy.java — app.open_url で開いてよい URL かの判定（メインプロセス。W1-6）
//
//  【規則】（Rust の runtime/src/engine/platform/bridge/app/url_rules.rs と同じ。変えるときは両方）
//    1. 空でない・MAX_URL_LENGTH 文字（Unicode の符号位置）以下・制御文字（U+0000〜U+001F・U+007F）を含まない  … 外れたら INVALID
//    2. 先頭に scheme があり（最初の ':' より前）、形が RFC 3986 §3.1 の scheme（英字で始まり、英数字と + - . が続く）… 外れたら INVALID
//    3. scheme（小文字にそろえて比べる。scheme は大文字小文字を区別しない）が断る一覧（file・content・javascript）なら SCHEME_NOT_ALLOWED
//    4. それ以外（http・https・mailto・tel・アプリ独自の scheme など）は ALLOWED（開けるアプリが無いかは開くときに分かる）
//  断る理由: file: は端末のファイルを他のアプリへ開かせる、content: はアプリの ContentProvider の中身を渡しうる、javascript: は
//  ブラウザにスクリプトを走らせる。URL がアプリの外（サーバの文面・ディープリンク）から来ても、これらは通さない。
//
//  Android の API を使わない（java.* だけ）ので、JVM でもそのまま確かめられる。
// ============================================================

package com.seedengine.runtime.platform.app;

import com.seedengine.runtime.platform.PlatformContract;

import java.util.Arrays;
import java.util.Collections;
import java.util.HashSet;
import java.util.Locale;
import java.util.Set;

/**
 * URL の判定（static のみ。どのスレッドから呼んでもよい）。
 */
public final class UrlPolicy {

    private UrlPolicy() {
    }

    /** scheme とその後ろを分ける文字。 */
    private static final char SCHEME_SEPARATOR = ':';

    /** これより小さい文字は制御文字（C0。U+0000〜U+001F）。 */
    private static final char FIRST_PRINTABLE = ' ';

    /** 制御文字の DEL（U+007F）。 */
    private static final char DELETE = '\u007f';

    /** scheme の 2 文字目以降に使える記号（RFC 3986 §3.1: ALPHA *( ALPHA / DIGIT / "+" / "-" / "." )）。 */
    private static final String SCHEME_SYMBOLS = "+-.";

    /** 断る scheme（小文字）。 */
    private static final Set<String> DENIED_SCHEMES = Collections.unmodifiableSet(new HashSet<>(Arrays.asList(
            PlatformContract.URL_SCHEME_FILE,
            PlatformContract.URL_SCHEME_CONTENT,
            PlatformContract.URL_SCHEME_JAVASCRIPT)));

    /** 判定の種類。 */
    public enum Verdict {
        /** 開いてよい。 */
        ALLOWED,
        /** URL の形が約束に合わない（invalid_argument）。 */
        INVALID,
        /** 断る scheme（scheme_not_allowed）。 */
        SCHEME_NOT_ALLOWED,
    }

    /** 判定の結果（不変）。 */
    public static final class Result {
        /** 判定。 */
        public final Verdict verdict;
        /** 小文字にそろえた scheme（INVALID なら空）。 */
        public final String scheme;
        /** 断った理由の説明（ALLOWED なら空。返答の detail）。 */
        public final String detail;

        private Result(Verdict verdict, String scheme, String detail) {
            this.verdict = verdict;
            this.scheme = scheme;
            this.detail = detail;
        }
    }

    /**
     * URL を判定する。
     *
     * @param url 開きたい URL（null 可）
     * @return 判定の結果
     */
    public static Result check(String url) {
        if (url == null || url.isEmpty()) {
            return invalid(PlatformContract.KEY_APP_URL + " が空です");
        }
        if (url.codePointCount(0, url.length()) > PlatformContract.MAX_URL_LENGTH) {
            return invalid(PlatformContract.KEY_APP_URL + " が長すぎます（" + PlatformContract.MAX_URL_LENGTH + " 文字まで）");
        }
        for (int i = 0; i < url.length(); i++) {
            char c = url.charAt(i);
            if (c < FIRST_PRINTABLE || c == DELETE) {
                return invalid(PlatformContract.KEY_APP_URL + " に制御文字が入っています");
            }
        }
        int separator = url.indexOf(SCHEME_SEPARATOR);
        if (separator <= 0) {
            return invalid(PlatformContract.KEY_APP_URL + " に scheme がありません（例 https://…）");
        }
        String scheme = url.substring(0, separator);
        if (!isScheme(scheme)) {
            return invalid(PlatformContract.KEY_APP_URL + " の scheme の形が正しくありません（英字で始まり、英数字と + - . が続く）");
        }
        String normalized = scheme.toLowerCase(Locale.ROOT);
        if (DENIED_SCHEMES.contains(normalized)) {
            return new Result(Verdict.SCHEME_NOT_ALLOWED, normalized, normalized + ": の URL は開けません（file / content / javascript は断る）");
        }
        return new Result(Verdict.ALLOWED, normalized, "");
    }

    /** 形の誤り。 */
    private static Result invalid(String detail) {
        return new Result(Verdict.INVALID, "", detail);
    }

    /**
     * RFC 3986 §3.1 の scheme の形か（英字で始まり、英数字と + - . が続く）。
     *
     * @param scheme 調べる文字列（空でないこと）
     * @return 形が合えば true
     */
    private static boolean isScheme(String scheme) {
        if (!isAsciiLetter(scheme.charAt(0))) {
            return false;
        }
        for (int i = 1; i < scheme.length(); i++) {
            char c = scheme.charAt(i);
            if (!isAsciiLetter(c) && !isAsciiDigit(c) && SCHEME_SYMBOLS.indexOf(c) < 0) {
                return false;
            }
        }
        return true;
    }

    /** ASCII の英字か。 */
    private static boolean isAsciiLetter(char c) {
        return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
    }

    /** ASCII の数字か。 */
    private static boolean isAsciiDigit(char c) {
        return c >= '0' && c <= '9';
    }
}
