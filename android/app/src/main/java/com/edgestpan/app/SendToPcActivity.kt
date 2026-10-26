package com.edgestpan.app

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.content.pm.ResolveInfo
import android.graphics.Color
import android.media.projection.MediaProjectionManager
import android.os.Bundle
import android.provider.Settings
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.BaseAdapter
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ListView
import android.widget.Switch
import android.widget.TextView
import android.widget.Toast

/**
 * "Send to PC" app picker. Picking an app opens it in its own window on Windows: through the shell server's
 * virtual display when it runs (USB/adb), otherwise by mirroring the screen with MediaProjection.
 */
class SendToPcActivity : Activity() {
    private var pending: Pair<String, String>? = null
    private lateinit var strings: Map<String, String>

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        strings = AppSender.strings(this)
        val preset = intent?.getStringExtra("component")
        if (preset != null) {
            begin(preset, intent?.getStringExtra("title") ?: preset)
            return
        }
        val d = resources.displayMetrics.density
        val root = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setBackgroundColor(Color.parseColor("#12141C")); setPadding((16 * d).toInt(), (16 * d).toInt(), (16 * d).toInt(), 0) }
        root.addView(TextView(this).apply {
            text = strings["sendPcTitle"] ?: "Send an app to your PC"
            setTextColor(Color.WHITE)
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 20f)
        })
        root.addView(TextView(this).apply {
            text = strings["sendPcHint"] ?: "It opens in its own window on Windows. Drag that window back to this side to return it."
            setTextColor(Color.parseColor("#B0B6C8"))
            setPadding(0, (6 * d).toInt(), 0, (10 * d).toInt())
        })
        val prefs = getSharedPreferences("edgestpan", Context.MODE_PRIVATE)
        root.addView(Switch(this).apply {
            text = strings["sendPcHandle"] ?: "Show the send handle on the shared edge"
            setTextColor(Color.WHITE)
            isChecked = prefs.getBoolean("sendHandle", false)
            setOnCheckedChangeListener { _, on ->
                if (on && !Settings.canDrawOverlays(this@SendToPcActivity)) {
                    isChecked = false
                    Toast.makeText(this@SendToPcActivity, strings["overlayNeeded"] ?: "Allow \"display over other apps\" first.", Toast.LENGTH_LONG).show()
                    return@setOnCheckedChangeListener
                }
                prefs.edit().putBoolean("sendHandle", on).apply()
                SendHandle.refresh(applicationContext)
            }
        })
        val apps = launcherApps(this)
        val list = ListView(this)
        list.adapter = object : BaseAdapter() {
            override fun getCount() = apps.size
            override fun getItem(position: Int) = apps[position]
            override fun getItemId(position: Int) = position.toLong()
            override fun getView(position: Int, convertView: View?, parent: ViewGroup?): View {
                val info = apps[position]
                val row = LinearLayout(this@SendToPcActivity).apply {
                    orientation = LinearLayout.HORIZONTAL
                    gravity = Gravity.CENTER_VERTICAL
                    setPadding(0, (8 * d).toInt(), 0, (8 * d).toInt())
                }
                row.addView(ImageView(this@SendToPcActivity).apply { setImageDrawable(info.loadIcon(packageManager)) }, LinearLayout.LayoutParams((40 * d).toInt(), (40 * d).toInt()))
                row.addView(TextView(this@SendToPcActivity).apply {
                    text = info.loadLabel(packageManager)
                    setTextColor(Color.WHITE)
                    setTextSize(TypedValue.COMPLEX_UNIT_SP, 16f)
                    setPadding((14 * d).toInt(), 0, 0, 0)
                })
                return row
            }
        }
        list.setOnItemClickListener { _, _, position, _ ->
            val info = apps[position].activityInfo
            begin("${info.packageName}/${info.name}", apps[position].loadLabel(packageManager).toString())
        }
        root.addView(list, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f))
        setContentView(root)
    }

    private fun begin(component: String, title: String) {
        if (AppLink.engine == null) {
            Toast.makeText(this, strings["sendPcNoLink"] ?: "Connect to the PC first (USB or Wi-Fi).", Toast.LENGTH_LONG).show()
            finish()
            return
        }
        Thread({
            DisplaySize.ensure(this)
            val dpi = resources.displayMetrics.densityDpi
            // The virtual display matches the phone's current shape so the app lays out the way it does here.
            val shell = AppSender.shellSource(component, DisplaySize.width, DisplaySize.height, dpi)
            runOnUiThread {
                if (shell != null) {
                    AppSender.send(applicationContext, component, title) { shell }
                    finish()
                } else {
                    // No shell server: ask for screen capture and mirror the phone while the app is open.
                    pending = component to title
                    val manager = getSystemService(MediaProjectionManager::class.java)
                    @Suppress("DEPRECATION")
                    startActivityForResult(manager.createScreenCaptureIntent(), REQUEST)
                }
            }
        }, "edgestpan-sendpc-open").start()
    }

    @Deprecated("Activity result API is not used in this module")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        @Suppress("DEPRECATION")
        super.onActivityResult(requestCode, resultCode, data)
        val p = pending
        if (requestCode == REQUEST && p != null && resultCode == RESULT_OK && data != null) ProjectionService.start(this, resultCode, data, p.first, p.second)
        else if (requestCode == REQUEST) Toast.makeText(this, strings["sendPcNoSource"] ?: "Could not capture the app.", Toast.LENGTH_LONG).show()
        finish()
    }

    companion object {
        private const val REQUEST = 41

        fun launcherApps(context: Context): List<ResolveInfo> {
            val intent = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)
            return context.packageManager.queryIntentActivities(intent, 0)
                .filter { it.activityInfo.packageName != context.packageName }
                .sortedBy { it.loadLabel(context.packageManager).toString().lowercase() }
        }

        fun launchOnPhone(context: Context, component: String) {
            val parts = component.split('/')
            if (parts.size != 2) return
            val cls = if (parts[1].startsWith(".")) parts[0] + parts[1] else parts[1]
            runCatching {
                context.startActivity(Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)
                    .setClassName(parts[0], cls).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            }
        }
    }
}
