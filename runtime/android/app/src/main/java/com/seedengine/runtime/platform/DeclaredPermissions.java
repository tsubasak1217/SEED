// ============================================================
//  DeclaredPermissions.java — APK のマニフェストが宣言している権限（<uses-permission>）を調べる（両プロセス共通。W1-5）
//
//  【使い道】機能（project_settings.json の android.features）の有無を実行時に知る。機能 notifications は権限
//  POST_NOTIFICATIONS だけを入れる（部品が無い）ので、目覚まし（AlarmModule）のように受信機の有無では調べられない。
//  そこで「その機能の権限が宣言されているか」で判定する:
//    notification.*（:seed_platform の NotificationModule）… POST_NOTIFICATIONS
//    permission.*（メインプロセス）… 種類ごとの権限（permission/PermissionKind）
//  注意: 機能 alarm も POST_NOTIFICATIONS を入れる（鳴動の通知のため）ので、alarm だけの APK でも通知の命令は使える
//  （Android 12 以前の端末では権限が無くても通知を出せるので、「版によって動いたり動かなかったり」を防ぐのが目的）。
//
//  【確かめたこと】宣言の一覧は PackageInfo.requestedPermissions。AOSP（main）の ParsingPackageUtils.parseUsesPermission は、
//  端末の版が maxSdkVersion を超える宣言（SCHEDULE_EXACT_ALARM の maxSdkVersion 32 など）だけを落とし、その版に無い権限の名前
//  （Android 12 以前の POST_NOTIFICATIONS など）も一覧に残す（ソースを読んで確かめた。Android 10〜12 の旧パーサは読んでいない）。
//  マニフェストはプロセスの間に変わらないので、最初の 1 回だけ読んで持つ。
// ============================================================

package com.seedengine.runtime.platform;

import android.content.Context;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.os.Build;
import android.util.Log;

import java.util.Arrays;
import java.util.Collections;
import java.util.HashSet;
import java.util.Set;

/**
 * 宣言している権限の一覧（static のみ。どのスレッドから呼んでもよい）。
 */
public final class DeclaredPermissions {

    private DeclaredPermissions() {
    }

    /** 宣言している権限の名前（最初に読んだときに作る。null = まだ）。 */
    private static volatile Set<String> declared;

    /**
     * その権限が APK のマニフェストで宣言されているか。
     *
     * @param context    どの Context でもよい（パッケージ名と PackageManager を使う）
     * @param permission 権限の名前（例 android.Manifest.permission.POST_NOTIFICATIONS）
     * @return 宣言されていれば true
     */
    public static boolean isDeclared(Context context, String permission) {
        return load(context).contains(permission);
    }

    /**
     * 宣言の一覧を読む（1 回だけ。読めなければ空＝どの機能も無い扱い）。
     *
     * @param context どの Context でもよい
     * @return 変えられない一覧
     */
    private static Set<String> load(Context context) {
        Set<String> cached = declared;
        if (cached != null) {
            return cached;
        }
        Set<String> names;
        try {
            PackageInfo info = packageInfo(context.getPackageManager(), context.getPackageName());
            String[] requested = info.requestedPermissions;
            names = requested != null
                    ? Collections.unmodifiableSet(new HashSet<>(Arrays.asList(requested)))
                    : Collections.<String>emptySet();
        } catch (PackageManager.NameNotFoundException e) {
            // 自分のパッケージが見つからないことは起きない見込み。起きたら機能は無い扱い（命令は feature_not_enabled）
            Log.w(PlatformContract.LOG_TAG, "宣言している権限を読めませんでした: " + PlatformJson.describe(e));
            names = Collections.emptySet();
        }
        declared = names;
        return names;
    }

    /**
     * 権限つきの PackageInfo（API 33 からは flags を PackageInfoFlags で渡す。それより前は int の flags）。
     *
     * @param packages    PackageManager
     * @param packageName 自分のパッケージ名
     * @return PackageInfo
     * @throws PackageManager.NameNotFoundException パッケージが無い
     */
    private static PackageInfo packageInfo(PackageManager packages, String packageName) throws PackageManager.NameNotFoundException {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            return packages.getPackageInfo(packageName, PackageManager.PackageInfoFlags.of(PackageManager.GET_PERMISSIONS));
        }
        return packages.getPackageInfo(packageName, PackageManager.GET_PERMISSIONS);
    }
}
