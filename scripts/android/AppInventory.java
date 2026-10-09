package com.logpro;

import android.content.Context;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.os.Looper;
import java.io.OutputStreamWriter;
import java.nio.charset.StandardCharsets;
import org.json.JSONArray;
import org.json.JSONObject;

/** Read-only, short-lived shell helper. No APK installation, network, or persistent service. */
public final class AppInventory {
    public static void main(String[] args) throws Exception {
        if (Looper.myLooper() == null) Looper.prepareMainLooper();
        Class<?> activityThread = Class.forName("android.app.ActivityThread");
        Object thread = activityThread.getMethod("systemMain").invoke(null);
        Context system = (Context) activityThread.getMethod("getSystemContext").invoke(thread);
        Context shell = system.createPackageContext("com.android.shell", 0);
        PackageManager manager = shell.getPackageManager();
        JSONArray result = new JSONArray();
        for (PackageInfo pkg : manager.getInstalledPackages(0)) {
            ApplicationInfo app = pkg.applicationInfo;
            if (app == null) continue;
            JSONObject item = new JSONObject();
            item.put("package", pkg.packageName);
            item.put("version", pkg.versionName == null ? "" : pkg.versionName);
            try { item.put("label", manager.getApplicationLabel(app).toString()); }
            catch (RuntimeException unavailable) { item.put("label", pkg.packageName); }
            result.put(item);
        }
        OutputStreamWriter output = new OutputStreamWriter(System.out, StandardCharsets.UTF_8);
        output.write(result.toString());
        output.write('\n');
        output.flush();
    }
}
