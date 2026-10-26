package com.edgestpan.app

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.res.Configuration
import android.content.IntentFilter
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.PowerManager
import android.provider.Settings
import android.util.Log
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.core.splashscreen.SplashScreen.Companion.installSplashScreen
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import org.json.JSONObject

class MainActivity : ComponentActivity() {
    private val scanner = registerForActivityResult(ScanContract()) { result ->
        val text = result.contents ?: return@registerForActivityResult
        prefs().edit().putString("link", text).apply()
        render()
    }

    private val statusReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) = render()
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        try {
            installSplashScreen()
        } catch (thrown: Throwable) {
            Log.e("EdgeSpan", "splash", thrown)
        }
        super.onCreate(savedInstanceState)
        Haptics.bind(window.decorView)
        applyIntent(intent)
        val filter = IntentFilter(SessionService.ACTION_STATUS)
        if (Build.VERSION.SDK_INT >= 33) registerReceiver(statusReceiver, filter, RECEIVER_NOT_EXPORTED) else registerReceiver(statusReceiver, filter)
        render()
        window.decorView.viewTreeObserver.addOnGlobalLayoutListener { DisplaySize.updateFrom(this) }
        DisplaySize.updateFrom(this)
        maybeBattery()
        maybeOverlay()
        maybeStart(intent, restart = false)
    }

    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        DisplaySize.updateFrom(this)
        render()
    }

    private fun maybeOverlay() {
        if (Build.VERSION.SDK_INT < 23) return
        if (Settings.canDrawOverlays(this)) return
        if (prefs().getBoolean("overlayAsked", false)) return
        prefs().edit().putBoolean("overlayAsked", true).apply()
        runCatching {
            startActivity(Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:$packageName")))
        }
    }

    private fun maybeBattery() {
        if (Build.VERSION.SDK_INT < 23) return
        val pm = getSystemService(PowerManager::class.java) ?: return
        if (pm.isIgnoringBatteryOptimizations(packageName)) return
        if (prefs().getBoolean("batteryAsked", false)) return
        prefs().edit().putBoolean("batteryAsked", true).apply()
        runCatching {
            startActivity(Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:$packageName")))
        }
    }

    override fun onDestroy() {
        Haptics.bind(null)
        runCatching { unregisterReceiver(statusReceiver) }
        super.onDestroy()
    }

    override fun onResume() {
        super.onResume()
        ClipHub.pendingRead = true
        ClipHub.readIfAllowed(this)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        applyIntent(intent)
        render()
        if (intent.getBooleanExtra("auto", false) || !intent.getStringExtra("code").isNullOrBlank() || !intent.getStringExtra("method").isNullOrBlank())
            maybeStart(intent, restart = true)
    }

    private fun applyIntent(intent: Intent?) {
        val edit = prefs().edit()
        val data = intent?.dataString
        if (!data.isNullOrBlank()) edit.putString("link", data)
        val code = intent?.getStringExtra("code")
        if (!code.isNullOrBlank()) edit.putString("link", code)
        val relay = intent?.getStringExtra("relay")
        if (relay != null) edit.putString("relay", relay)
        val stun = intent?.getStringExtra("stun")
        if (stun != null) edit.putString("stun", stun)
        val method = intent?.getStringExtra("method")
        if (!method.isNullOrBlank()) {
            edit.putString("method", method)
            if (method == "usb") edit.putString("backend", "adb")
        }
        if (intent?.getBooleanExtra("auto", false) == true || intent?.getBooleanExtra("onboard", false) == true)
            edit.putBoolean("onboarded", true)
        edit.apply()
    }

    private fun maybeStart(intent: Intent?, restart: Boolean) {
        val auto = intent?.getBooleanExtra("auto", false) == true
        val method = prefs().getString("method", "")
        if (auto || !method.isNullOrBlank()) startPairing(restart)
    }

    private fun startPairing(restart: Boolean = false) {
        val launch = Intent(this, SessionService::class.java)
        if (restart) launch.putExtra("restart", true)
        startForegroundService(launch)
    }

    private fun prefs() = getSharedPreferences("edgestpan", MODE_PRIVATE)

    private fun render() {
        val prefs = prefs()
        val lang = prefs.getString("lang", "en") ?: "en"
        val strings = loadStrings(lang)
        setContent {
            EdgeSpanApp(
                strings = strings,
                rtl = lang == "fa",
                dark = prefs.getString("theme", "dark") != "light",
                link = prefs.getString("link", "") ?: "",
                name = prefs.getString("name", "Android") ?: "Android",
                backend = prefs.getString("backend", "access") ?: "access",
                status = prefs.getString("status", "") ?: "",
                message = prefs.getString("pairDetail", "") ?: "",
                pairState = prefs.getString("pairState", "") ?: "",
                relay = prefs.getString("relay", "") ?: "",
                stun = prefs.getString("stun", "") ?: "",
                onboarded = prefs.getBoolean("onboarded", false),
                method = prefs.getString("method", "") ?: "",
                latencyMs = prefs.getInt("latencyMs", -1),
                peers = prefs.getString("peers", "") ?: "",
                onLanguage = {
                    prefs.edit().putString("lang", it).apply()
                    render()
                },
                onTheme = {
                    prefs.edit().putString("theme", if (it) "dark" else "light").apply()
                    render()
                },
                onSave = { name, link, backend ->
                    prefs.edit().putString("name", name).putString("link", link).putString("backend", backend).apply()
                },
                onPair = { code ->
                    val trimmed = code.trim()
                    val missing = trimmed.isBlank()
                    prefs.edit()
                        .putString("link", trimmed)
                        .putString("pairState", if (missing) "error" else "working")
                        .putString("pairDetail", if (missing) strings["pairNeedCode"] ?: "Enter the code from the computer." else strings["pairWorking"] ?: "Looking for the computer…")
                        .apply()
                    render()
                    if (!missing) startPairing(restart = true)
                },
                onRelay = { prefs.edit().putString("relay", it.trim()).apply() },
                onStun = { prefs.edit().putString("stun", it.trim()).apply() },
                onStart = {
                    if (prefs.getString("backend", "access") == "access" && !android.provider.Settings.canDrawOverlays(this)) {
                        startActivity(Intent(android.provider.Settings.ACTION_MANAGE_OVERLAY_PERMISSION, android.net.Uri.parse("package:$packageName")))
                    }
                    startPairing(restart = true)
                },
                onStop = { stopService(Intent(this, SessionService::class.java)) },
                onScan = {
                    scanner.launch(ScanOptions().setDesiredBarcodeFormats(ScanOptions.QR_CODE).setBeepEnabled(false).setPrompt(strings["qrScan"] ?: "EdgeSpan"))
                },
                onFinishOnboarding = {
                    prefs.edit().putBoolean("onboarded", true).apply()
                    render()
                },
                onChooseMethod = { choose(it) },
                direct = prefs.getString("direct", "") ?: "",
                onDirect = { prefs.edit().putString("direct", it.trim()).apply() },
                diagnostics = prefs.getBoolean("diagnostics", false),
                edgeGlow = prefs.getBoolean("edgeGlow", true),
                clipboard = prefs.getBoolean("clipboard", true),
                files = prefs.getBoolean("files", true),
                handoffPc = !prefs.contains("handoffPc") || prefs.getBoolean("handoffPc", true),
                licenseCode = prefs.getInt("hostLicense", 0),
                licenseDays = prefs.getInt("hostLicenseDays", 0),
                path = prefs.getString("path", "") ?: "",
                handoffNote = prefs.getString("handoffError", "") ?: "",
                breakthrough = !prefs.contains("breakthrough") || prefs.getBoolean("breakthrough", true),
                reduceMotion = prefs.getBoolean("reduceMotion", false),
                sound = prefs.getBoolean("sound", false),
                onExportLog = { shareDiagnostics() },
                onFlag = { key, value -> prefs.edit().putBoolean(key, value).apply(); render() },
                onPaste = {
                    ClipHub.pendingRead = true
                    ClipHub.readIfAllowed(this)
                    val dir = ClipHub.receivedDir(this)
                    startActivity(Intent(Intent.ACTION_VIEW).setDataAndType(android.net.Uri.fromFile(dir), "resource/folder").addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                },
                onRetry = {
                    render()
                    startPairing(restart = true)
                },
                onSwitch = {
                    stopService(Intent(this, SessionService::class.java))
                    prefs.edit().putString("method", "").putString("pairState", "").putString("pairDetail", "").apply()
                    render()
                }
            )
        }
    }

    private fun shareDiagnostics() {
        val body = DiagFile.read(this).ifBlank { "EdgeSpan log is empty." }
        val send = Intent(Intent.ACTION_SEND)
            .setType("text/plain")
            .putExtra(Intent.EXTRA_SUBJECT, "EdgeSpan diagnostics")
            .putExtra(Intent.EXTRA_TEXT, body)
        startActivity(Intent.createChooser(send, "EdgeSpan"))
    }

    private fun choose(method: String) {
        val strings = loadStrings(prefs().getString("lang", "en") ?: "en")
        val detail = when (method) {
            "usb" -> strings["usbSearching"]
            "relay" -> strings["waitingRelay"]
            else -> strings["waitingPhone"]
        } ?: ""
        val edit = prefs().edit()
            .putString("method", method)
            .putBoolean("onboarded", true)
            .putString("pairState", "searching")
            .putString("pairDetail", detail)
        if (method == "usb") edit.putString("backend", "adb")
        edit.apply()
        render()
        startPairing(restart = true)
    }

    private fun loadStrings(lang: String): Map<String, String> {
        val json = assets.open("strings.json").bufferedReader().readText()
        val root = JSONObject(json)
        val map = linkedMapOf<String, String>()
        val keys = root.keys()
        while (keys.hasNext()) {
            val key = keys.next()
            val item = root.getJSONObject(key)
            map[key] = if (item.has(lang)) item.getString(lang) else item.optString("en", key)
        }
        return map
    }
}
