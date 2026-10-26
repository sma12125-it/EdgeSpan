package com.edgestpan.app

import android.content.Intent
import android.net.Uri
import android.provider.Settings
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Checkbox
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.NavigationBarItemDefaults
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.foundation.layout.RowScope
import androidx.compose.material3.ButtonColors
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import kotlin.math.PI
import kotlin.math.cos
import kotlin.math.sin
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

private val Night = Color(0xFF202020)
private val NightCard = Color(0xFF2C2C2C)
private val Paper = Color(0xFFF3F3F3)
private val Ink = Color(0xFF1A1A1A)
private val MutedDark = Color(0xFFC8C8C8)
private val MutedLight = Color(0xFF5D5D5D)
private val Blue = Color(0xFF0067C0)
private val BlueLift = Color(0xFF60CDFF)
private val Mint = Color(0xFF3DDC84)
private val Gold = Color(0xFFF0B429)
private val LocalReduce = staticCompositionLocalOf { false }

@Composable
private fun Muted(): Color =
    if (MaterialTheme.colorScheme.background == Night) MutedDark else MutedLight

private val Jakarta = FontFamily(
    Font(R.font.plus_jakarta_sans_regular, FontWeight.Normal),
    Font(R.font.plus_jakarta_sans_semibold, FontWeight.SemiBold)
)
private val Vazir = FontFamily(
    Font(R.font.vazirmatn_regular, FontWeight.Normal),
    Font(R.font.vazirmatn_medium, FontWeight.Medium)
)

@Composable
fun EdgeSpanApp(
    strings: Map<String, String>,
    rtl: Boolean,
    dark: Boolean,
    link: String,
    name: String,
    backend: String,
    status: String,
    message: String,
    pairState: String,
    relay: String,
    stun: String,
    onboarded: Boolean,
    onLanguage: (String) -> Unit,
    onTheme: (Boolean) -> Unit,
    onSave: (String, String, String) -> Unit,
    onPair: (String) -> Unit,
    onRelay: (String) -> Unit,
    onStun: (String) -> Unit,
    onStart: () -> Unit,
    onStop: () -> Unit,
    onScan: () -> Unit,
    onFinishOnboarding: () -> Unit,
    method: String = "",
    latencyMs: Int = -1,
    peers: String = "",
    onChooseMethod: (String) -> Unit = {},
    onRetry: () -> Unit = {},
    onSwitch: () -> Unit = {},
    direct: String = "",
    onDirect: (String) -> Unit = {},
    diagnostics: Boolean = false,
    edgeGlow: Boolean = true,
    clipboard: Boolean = true,
    files: Boolean = true,
    handoffPc: Boolean = false,
    onFlag: (String, Boolean) -> Unit = { _, _ -> },
    onPaste: () -> Unit = {},
    path: String = "",
    licenseCode: Int = 0,
    licenseDays: Int = 0,
    handoffNote: String = "",
    onExportLog: () -> Unit = {},
    breakthrough: Boolean = true,
    reduceMotion: Boolean = false,
    sound: Boolean = false
) {
    val scheme = if (dark) {
        darkColorScheme(primary = BlueLift, secondary = Mint, background = Night, surface = NightCard, onPrimary = Night, onSurface = Color(0xFFF4F7FF), onBackground = Color(0xFFF4F7FF))
    } else {
        lightColorScheme(primary = Blue, secondary = Mint, background = Paper, surface = Color.White, onPrimary = Color.White, onSurface = Ink, onBackground = Ink)
    }
    val font = if (rtl) Vazir else Jakarta
    val systemReduce = android.provider.Settings.Global.getFloat(LocalContext.current.contentResolver, android.provider.Settings.Global.ANIMATOR_DURATION_SCALE, 1f) == 0f
    val reduce = reduceMotion || systemReduce
    MaterialTheme(colorScheme = scheme, typography = MaterialTheme.typography.copy(
        headlineMedium = MaterialTheme.typography.headlineMedium.copy(fontFamily = font, fontWeight = FontWeight.SemiBold),
        bodyLarge = MaterialTheme.typography.bodyLarge.copy(fontFamily = font),
        bodyMedium = MaterialTheme.typography.bodyMedium.copy(fontFamily = font),
        labelLarge = MaterialTheme.typography.labelLarge.copy(fontFamily = font)
    )) {
        CompositionLocalProvider(LocalLayoutDirection provides if (rtl) LayoutDirection.Rtl else LayoutDirection.Ltr, LocalReduce provides reduce) {
            Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
                var setup by remember { mutableStateOf(false) }
                if (setup) {
                    Onboarding(strings) { setup = false; onFinishOnboarding() }
                } else {
                    var tab by remember { mutableIntStateOf(0) }
                    Column(Modifier.fillMaxSize()) {
                        Box(Modifier.weight(1f)) {
                            AnimatedContent(tab, transitionSpec = {
                                if (reduce) fadeIn(tween(0)) togetherWith fadeOut(tween(0))
                                else fadeIn(tween(220)) togetherWith fadeOut(tween(160))
                            }, label = "page") { page ->
                                when (page) {
                                    0 -> Connect(
                                        strings, link, message, pairState, method, relay, stun, direct, latencyMs, peers, !onboarded, path,
                                        onChooseMethod, onPair, onRelay, onStun, onDirect, onRetry, onSwitch, onScan, { setup = true }, onPaste,
                                        handoffPc, onFlag, handoffNote
                                    )
                                    1 -> Pair(strings, link, message, pairState, onScan, onPair)
                                    else -> Settings(strings, rtl, dark, relay, stun, diagnostics, edgeGlow, clipboard, files, handoffPc, breakthrough, reduceMotion, sound, onLanguage, onTheme, onRelay, onStun, onFlag, licenseCode, licenseDays, onExportLog)
                                }
                            }
                        }
                        Text(
                            (strings["versionLabel"] ?: "Version") + " " + BuildConfig.VERSION_NAME,
                            modifier = Modifier.padding(start = 16.dp, end = 16.dp, bottom = 4.dp),
                            color = Muted(),
                            fontSize = 11.sp
                        )
                        NavigationBar(containerColor = MaterialTheme.colorScheme.surface) {
                            listOf(0 to (strings["navConnect"] ?: "Connect"), 1 to (strings["pairing"] ?: "Pair"), 2 to (strings["settings"] ?: "Settings")).forEach { (index, label) ->
                                NavigationBarItem(
                                    selected = tab == index,
                                    onClick = { tab = index },
                                    label = { Text(label) },
                                    icon = { StrokeIcon(index, if (tab == index) MaterialTheme.colorScheme.primary else Muted()) },
                                    colors = NavigationBarItemDefaults.colors(indicatorColor = MaterialTheme.colorScheme.primary.copy(alpha = 0.22f))
                                )
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun Connect(
    strings: Map<String, String>,
    link0: String,
    message: String,
    pairState: String,
    method: String,
    relay0: String,
    stun0: String,
    direct0: String,
    latencyMs: Int,
    peers: String,
    showSetup: Boolean,
    path: String,
    onChooseMethod: (String) -> Unit,
    onPair: (String) -> Unit,
    onRelay: (String) -> Unit,
    onStun: (String) -> Unit,
    onDirect: (String) -> Unit,
    onRetry: () -> Unit,
    onSwitch: () -> Unit,
    onScan: () -> Unit,
    onSetup: () -> Unit,
    onPaste: () -> Unit,
    handoffPc: Boolean,
    onFlag: (String, Boolean) -> Unit,
    handoffNote: String
) {
    val connected = pairState == "ok"
    val compact = LocalConfiguration.current.screenHeightDp < 520 || LocalConfiguration.current.screenWidthDp < 420
    val working = method.isNotBlank() && !connected
    val shift = rememberInfiniteTransition(label = "link").animateFloat(
        initialValue = 0f,
        targetValue = 1f,
        animationSpec = infiniteRepeatable(tween(1600, easing = LinearEasing)),
        label = "shift"
    ).value
    var pin by remember { mutableStateOf(link0) }
    var relay by remember { mutableStateOf(relay0) }
    var stun by remember { mutableStateOf(stun0) }
    var direct by remember { mutableStateOf(direct0) }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(if (compact) 12.dp else 22.dp), verticalArrangement = Arrangement.spacedBy(if (compact) 8.dp else 14.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Mark()
            Column {
                Text(strings["brandKicker"] ?: "ACROSS THE EDGE", color = MaterialTheme.colorScheme.primary, fontWeight = FontWeight.SemiBold, fontSize = 12.sp)
                Text(strings["connectTitle"] ?: "Connect your device", style = MaterialTheme.typography.headlineMedium)
            }
        }
        if (!working && !connected) {
            Text(strings["connectLead"] ?: "", color = Muted())
            MethodCard(strings["methodUsb"] ?: "USB", strings["methodUsbBody"] ?: "", "usb", shift) { onChooseMethod("usb") }
            MethodCard(strings["methodLan"] ?: "Wi-Fi", strings["methodLanBody"] ?: "", "lan", shift) { onChooseMethod("lan") }
            MethodCard(strings["methodRelay"] ?: "Internet", strings["methodRelayBody"] ?: "", "relay", shift) { onChooseMethod("relay") }
            if (showSetup) EOutlined(onClick = onSetup) { Text(strings["openSetup"] ?: "Permissions") }
        } else {
            Card {
                LinkCanvas(if (method.isBlank()) "lan" else method, if (connected) "connected" else pairState.ifBlank { "searching" }, shift, Modifier.fillMaxWidth().height(if (compact) 88.dp else 180.dp))
            }
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                if (connected) CheckMark()
                Text(
                    when {
                        connected -> strings["connectedTitle"] ?: "Connected"
                        message.isNotBlank() -> message
                        method == "usb" -> strings["usbSearching"] ?: ""
                        method == "relay" -> strings["waitingRelay"] ?: ""
                        else -> strings["waitingPhone"] ?: ""
                    },
                    fontSize = 22.sp,
                    fontWeight = FontWeight.SemiBold,
                    color = if (pairState == "error") Gold else MaterialTheme.colorScheme.onBackground
                )
            }
            if (connected && message.isNotBlank()) {
                Text(message, color = MaterialTheme.colorScheme.primary, fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
            }
            if (connected) {
                HandoffCard(strings, handoffPc, onFlag)
                if (handoffNote.isNotBlank()) {
                    Text(handoffNote, color = Gold, fontWeight = FontWeight.SemiBold)
                }
            }
            if (connected) {
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    Stat(strings["latency"] ?: "Latency", if (latencyMs >= 0) "$latencyMs ${strings["ms"] ?: "ms"}" else "—", Modifier.weight(1f))
                    Stat(
                        strings["connectionType"] ?: "Connection",
                        when {
                            path == "direct" -> strings["typeDirect"] ?: "Direct"
                            method == "usb" -> strings["typeUsb"] ?: "USB"
                            method == "relay" -> strings["typeRelay"] ?: "Internet"
                            else -> strings["typeLan"] ?: "Wi-Fi"
                        },
                        Modifier.weight(1f)
                    )
                }
            }
            if (working && method == "lan") {
                OutlinedTextField(pin, { pin = it }, label = { Text(strings["codeLabel"] ?: strings["pinLabel"] ?: "PIN") }, modifier = Modifier.fillMaxWidth())
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    EButton(onClick = { onPair(pin) }, colors = ButtonDefaults.buttonColors(containerColor = Blue)) { Text(strings["pairButton"] ?: "Pair") }
                    EOutlined(onClick = onScan) { Text(strings["qrScan"] ?: "Scan QR") }
                }
                val rows = peers.lineSequence().map { it.split('|') }.filter { it.size >= 2 && it[0].isNotBlank() }.toList()
                Text(if (rows.isEmpty()) strings["noComputersYet"] ?: "" else strings["discoveredComputers"] ?: "", color = Muted())
                rows.forEach { row ->
                    Text(row[0] + "  ·  " + row[1], fontWeight = FontWeight.SemiBold)
                }
            }
            if (working && method == "relay") {
                OutlinedTextField(relay, { relay = it; onRelay(it) }, label = { Text(strings["relayUrl"] ?: "Relay") }, modifier = Modifier.fillMaxWidth())
                OutlinedTextField(stun, { stun = it; onStun(it) }, label = { Text(strings["stunServer"] ?: "STUN") }, modifier = Modifier.fillMaxWidth())
                Text(strings["stunHint"] ?: "", color = Muted())
                OutlinedTextField(direct, { direct = it; onDirect(it) }, label = { Text(strings["directAddress"] ?: "PC") }, modifier = Modifier.fillMaxWidth())
                Text(strings["directHint"] ?: "", color = Muted())
                OutlinedTextField(pin, { pin = it }, label = { Text(strings["codeLabel"] ?: "PIN") }, modifier = Modifier.fillMaxWidth())
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    EButton(onClick = { onRelay(relay); onStun(stun); onDirect(direct); onPair(pin) }, colors = ButtonDefaults.buttonColors(containerColor = Blue)) { Text(strings["pairButton"] ?: "Pair") }
                    EOutlined(onClick = onScan) { Text(strings["qrScan"] ?: "Scan QR") }
                }
            }
            if (connected) {
                EOutlined(onClick = onPaste) { Text(strings["pasteHere"] ?: "Paste here") }
            }
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                if (pairState == "error") EButton(onClick = onRetry, colors = ButtonDefaults.buttonColors(containerColor = Blue)) { Text(strings["connectRetry"] ?: "Try again") }
                EOutlined(onClick = onSwitch) { Text(strings["switchMethod"] ?: "Switch method") }
            }
        }
    }
}

@Composable
private fun MethodCard(title: String, body: String, method: String, shift: Float, onClick: () -> Unit) {
    val source = remember { MutableInteractionSource() }
    val pressed by source.collectIsPressedAsState()
    val scale by animateFloatAsState(if (pressed && !LocalReduce.current) 0.97f else 1f, spring(dampingRatio = 0.92f, stiffness = 1540f), label = "card")
    Column(
        Modifier.fillMaxWidth().graphicsLayer { scaleX = scale; scaleY = scale }.clip(RoundedCornerShape(16.dp)).border(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.35f), RoundedCornerShape(16.dp)).background(if (pressed) MaterialTheme.colorScheme.primary.copy(alpha = 0.08f) else MaterialTheme.colorScheme.surface).clickable(interactionSource = source, indication = null, onClick = onClick).padding(14.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp)
    ) {
        LinkCanvas(method, "idle", shift, Modifier.fillMaxWidth().height(92.dp))
        Text(title, fontSize = 18.sp, fontWeight = FontWeight.SemiBold)
        Text(body, color = Muted())
    }
}

@Composable
private fun LinkCanvas(method: String, phase: String, shift: Float, modifier: Modifier) {
    val failed = phase == "error"
    val connected = phase == "connected"
    val accent = when {
        connected -> Mint
        failed -> Gold
        else -> BlueLift
    }
    Canvas(modifier) {
        val w = size.width
        val h = size.height
        val pulse = if (phase == "searching" || phase == "working" || phase == "idle") 0.55f + 0.45f * (0.5f + 0.5f * sin(shift * PI.toFloat() * 2f)) else 1f
        val link = accent.copy(alpha = pulse)
        val pc = androidx.compose.ui.geometry.Rect(w * 0.05f, h * 0.22f, w * 0.34f, h * 0.72f)
        val phoneLeft = w * 0.72f
        drawRoundRect(Blue, Offset(pc.left, pc.top), Size(pc.width, pc.height), CornerRadius(14f, 14f))
        drawRoundRect(Color.White, Offset(pc.left, pc.top), Size(pc.width, pc.height), CornerRadius(14f, 14f), style = Stroke(3f))
        drawRoundRect(Blue, Offset(phoneLeft, h * 0.12f), Size(w * 0.16f, h * 0.74f), CornerRadius(16f, 16f))
        drawRoundRect(Color.White, Offset(phoneLeft, h * 0.12f), Size(w * 0.16f, h * 0.74f), CornerRadius(16f, 16f), style = Stroke(3f))
        val y = h * 0.48f
        val start = pc.right
        val end = phoneLeft
        val stroke = Stroke(width = if (connected) 6f else 4f, cap = StrokeCap.Round)
        when (method) {
            "relay" -> {
                val cx = (start + end) / 2f
                val r = h * 0.22f
                drawCircle(link, r, Offset(cx, y), style = stroke)
                if (!failed) {
                    drawOval(link, Offset(cx - r * 0.45f, y - r), Size(r * 0.9f, r * 2f), style = stroke)
                    drawLine(link, Offset(cx - r, y), Offset(cx + r, y), strokeWidth = 3f)
                }
            }
            "lan" -> {
                val cx = (start + end) / 2f
                val arcs = if (failed) 1 else 3
                for (i in 1..arcs) {
                    val radius = 8f + i * 14f
                    val path = Path().apply {
                        moveTo(cx - radius, y)
                        quadraticTo(cx, y - radius * 0.85f + 4f * sin(shift * PI.toFloat() * 2f + i), cx + radius, y)
                    }
                    drawPath(path, link, style = stroke)
                }
            }
            else -> {
                val mid = (start + end) / 2f
                if (failed) {
                    drawLine(link, Offset(start, y), Offset(mid - 18f, y), strokeWidth = 5f, cap = StrokeCap.Round)
                    drawLine(link, Offset(mid + 18f, y), Offset(end, y), strokeWidth = 5f, cap = StrokeCap.Round)
                } else {
                    val path = Path().apply {
                        moveTo(start, y)
                        cubicTo(mid - 8f, y - 28f, mid + 8f, y + 24f, end, y)
                    }
                    drawPath(path, link, style = stroke)
                    val t = shift
                    val u = 1f - t
                    val x = u * u * u * start + 3 * u * u * t * (mid - 8f) + 3 * u * t * t * (mid + 8f) + t * t * t * end
                    val yy = u * u * u * y + 3 * u * u * t * (y - 28f) + 3 * u * t * t * (y + 24f) + t * t * t * y
                    drawCircle(link, 6f, Offset(x, yy))
                    drawCircle(link, 7f, Offset(end, y), style = Stroke(3f))
                }
            }
        }
    }
}

@Composable
private fun Onboarding(strings: Map<String, String>, onDone: () -> Unit) {
    val pages = listOf(
        Triple(strings["onboardTitle"] ?: "", strings["onboardBody"] ?: "", null),
        Triple(strings["onboardAccessTitle"] ?: "", strings["onboardAccessBody"] ?: "", "access"),
        Triple(strings["onboardOverlayTitle"] ?: "", strings["onboardOverlayBody"] ?: "", "overlay"),
        Triple(strings["onboardKeyboardTitle"] ?: "", strings["onboardKeyboardBody"] ?: "", "keyboard"),
        Triple(strings["onboardAdbTitle"] ?: "", strings["onboardAdbBody"] ?: "", "adb")
    )
    var index by remember { mutableIntStateOf(0) }
    val page = pages[index]
    val context = androidx.compose.ui.platform.LocalContext.current
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(24.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
        Mark()
        Text(page.first, style = MaterialTheme.typography.headlineMedium, color = MaterialTheme.colorScheme.onBackground)
        Text(page.second, color = Muted(), fontSize = 16.sp)
        Illustration(index)
        if (page.third != null) {
            EOutlined(onClick = {
                val intent = when (page.third) {
                    "access" -> Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)
                    "overlay" -> Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:${context.packageName}"))
                    "keyboard" -> Intent(Settings.ACTION_INPUT_METHOD_SETTINGS)
                    else -> Intent(Settings.ACTION_APPLICATION_DEVELOPMENT_SETTINGS)
                }
                context.startActivity(intent)
            }) { Text(when (page.third) {
                "access" -> strings["openAccess"] ?: ""
                "keyboard" -> strings["openKeyboard"] ?: ""
                "adb" -> strings["openDeveloper"] ?: ""
                else -> strings["grantOverlay"] ?: ""
            }) }
        }
        Spacer(Modifier.height(8.dp))
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            if (index > 0) EOutlined(onClick = { index -= 1 }) { Text(strings["back"] ?: "Back") }
            EButton(onClick = { if (index == pages.lastIndex) onDone() else index += 1 }, colors = ButtonDefaults.buttonColors(containerColor = Blue)) {
                Text(if (index == pages.lastIndex) strings["finish"] ?: "Finish" else strings["next"] ?: "Next")
            }
            if (index < pages.lastIndex) EOutlined(onClick = onDone) { Text(strings["skip"] ?: "Skip") }
        }
    }
}

@Composable
private fun Home(strings: Map<String, String>, name: String, backend: String, status: String, link: String, message: String, onStart: () -> Unit, onStop: () -> Unit, onSave: (String, String, String) -> Unit) {
    var mode by remember { mutableStateOf(backend) }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(24.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
        Text(strings["brandKicker"] ?: "ACROSS THE EDGE", color = MaterialTheme.colorScheme.primary, fontWeight = FontWeight.SemiBold, fontSize = 12.sp)
        Text(strings["home"] ?: "Home", style = MaterialTheme.typography.headlineMedium)
        Text(strings["tagline"] ?: "", color = Muted())
        Card {
            Text(
                when {
                    message.isNotBlank() -> message
                    status == "has" -> strings["hasControl"] ?: ""
                    else -> strings["statusIdle"] ?: strings["waitingForHost"] ?: ""
                },
                fontSize = 22.sp,
                fontWeight = FontWeight.SemiBold
            )
            Text(if (link.isBlank()) strings["dashboardHint"] ?: "" else strings["serviceRunning"] ?: "", color = Muted())
        }
        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            Stat(strings["modeLabel"] ?: "", if (mode == "adb") strings["backendAdb"] ?: "" else strings["backendAccess"] ?: "", Modifier.weight(1f))
            Stat(strings["connectionPath"] ?: "", if (status.isBlank()) strings["waiting"] ?: "" else strings["pathLan"] ?: "LAN", Modifier.weight(1f))
        }
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Choice(strings["backendAccess"] ?: "", mode == "access") { mode = "access" }
            Choice(strings["backendAdb"] ?: "", mode == "adb") { mode = "adb" }
        }
        Text(if (mode == "adb") strings["adbSteps"] ?: "" else strings["gestureNote"] ?: "", color = Muted())
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            EButton(onClick = { onSave(name, link, mode); onStart() }, colors = ButtonDefaults.buttonColors(containerColor = Blue)) { Text(strings["startService"] ?: "Start") }
            EOutlined(onClick = onStop) { Text(strings["stop"] ?: "Stop") }
        }
    }
}

@Composable
private fun Pair(strings: Map<String, String>, link0: String, message: String, pairState: String, onScan: () -> Unit, onPair: (String) -> Unit) {
    var link by remember { mutableStateOf(link0) }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(24.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
        Text(strings["pairing"] ?: "Pairing", style = MaterialTheme.typography.headlineMedium)
        Text(strings["qrHint"] ?: "", color = Muted())
        if (message.isNotBlank()) {
            Text(message, color = if (pairState == "error") Gold else Mint, fontSize = 18.sp, fontWeight = FontWeight.SemiBold)
        }
        EButton(onClick = onScan, colors = ButtonDefaults.buttonColors(containerColor = Blue)) { Text(strings["qrScan"] ?: "Scan QR") }
        OutlinedTextField(link, { link = it }, label = { Text(strings["codeLabel"] ?: strings["pasteLink"] ?: "") }, modifier = Modifier.fillMaxWidth(), minLines = 2)
        EButton(onClick = { onPair(link) }, colors = ButtonDefaults.buttonColors(containerColor = Blue)) { Text(strings["pairButton"] ?: "Pair") }
        Text(strings["privacyNote"] ?: "", color = Gold)
    }
}

@Composable
private fun Settings(strings: Map<String, String>, rtl: Boolean, dark: Boolean, relay0: String, stun0: String, diagnostics: Boolean, edgeGlow: Boolean, clipboard: Boolean, files: Boolean, handoffPc: Boolean, breakthrough: Boolean, reduceMotion: Boolean, sound: Boolean, onLanguage: (String) -> Unit, onTheme: (Boolean) -> Unit, onRelay: (String) -> Unit, onStun: (String) -> Unit, onFlag: (String, Boolean) -> Unit, licenseCode: Int, licenseDays: Int, onExportLog: () -> Unit) {
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(24.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
        Text(strings["settings"] ?: "Settings", style = MaterialTheme.typography.headlineMedium)
        HandoffCard(strings, handoffPc, onFlag)
        AboutBlock(strings, licenseCode, licenseDays, onExportLog)
        Text(strings["languageLabel"] ?: "", color = Muted())
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Choice(strings["english"] ?: "English", !rtl) { onLanguage("en") }
            Choice(strings["persian"] ?: "فارسی", rtl) { onLanguage("fa") }
        }
        Text(strings["appearance"] ?: "", color = Muted())
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Choice(strings["themeDark"] ?: "Dark", dark) { onTheme(true) }
            Choice(strings["themeLight"] ?: "Light", !dark) { onTheme(false) }
        }
        var relay by remember { mutableStateOf(relay0) }
        var stun by remember { mutableStateOf(stun0) }
        Text(strings["relayHint"] ?: "", color = Muted())
        OutlinedTextField(relay, { relay = it; onRelay(it) }, label = { Text(strings["relayUrl"] ?: "Relay") }, modifier = Modifier.fillMaxWidth())
        Text(strings["stunHint"] ?: "", color = Muted())
        OutlinedTextField(stun, { stun = it; onStun(it) }, label = { Text(strings["stunServer"] ?: "STUN") }, modifier = Modifier.fillMaxWidth())
        Flag(strings["clipboardShare"] ?: "Clipboard", clipboard) { onFlag("clipboard", it) }
        Flag(strings["fileTransfer"] ?: "Files", files) { onFlag("files", it) }
        Flag(strings["edgeGlowPhone"] ?: "Glow", edgeGlow) { onFlag("edgeGlow", it) }
        Flag(strings["breakthrough"] ?: "Breakthrough", breakthrough) { onFlag("breakthrough", it) }
        Flag(strings["reduceMotion"] ?: "Reduce motion", reduceMotion) { onFlag("reduceMotion", it) }
        Flag(strings["soundEnabled"] ?: "Sound", sound) { onFlag("sound", it) }
        Text(strings["soundHint"] ?: "", color = Muted(), fontSize = 12.sp)
        Flag(strings["diagnostics"] ?: "Diagnostics", diagnostics) { onFlag("diagnostics", it) }
        Text(strings["diagnosticsHint"] ?: "", color = Muted())
        Text(strings["diagPathHint"] ?: "", color = Muted())
        EOutlined(onClick = onExportLog) { Text(strings["diagExport"] ?: "Export diagnostics log") }
        Text(strings["imeNote"] ?: "", color = Muted())
        Text(strings["eulaShort"] ?: "", color = Muted())
    }
}

@Composable
private fun HandoffCard(strings: Map<String, String>, on: Boolean, onFlag: (String, Boolean) -> Unit) {
    Column(Modifier.fillMaxWidth().clip(RoundedCornerShape(8.dp)).border(1.dp, MaterialTheme.colorScheme.primary.copy(alpha = 0.45f), RoundedCornerShape(8.dp)).background(MaterialTheme.colorScheme.surface).padding(16.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Text(strings["handoffTitle"] ?: "Window handoff", fontWeight = FontWeight.SemiBold, fontSize = 18.sp)
        Flag(strings["handoffPc"] ?: "PC windows on the tablet", on) { onFlag("handoffPc", it) }
        Text(strings["handoffPcHint"] ?: "", color = Muted())
    }
}

@Composable
private fun AboutBlock(strings: Map<String, String>, licenseCode: Int, licenseDays: Int, onExportLog: () -> Unit) {
    Column(Modifier.fillMaxWidth().clip(RoundedCornerShape(8.dp)).border(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.35f), RoundedCornerShape(8.dp)).background(MaterialTheme.colorScheme.surface).padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text(strings["about"] ?: "About", fontWeight = FontWeight.SemiBold, fontSize = 18.sp)
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Image(painterResource(R.drawable.ic_brand), contentDescription = "EdgeSpan", modifier = Modifier.size(64.dp))
            Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text("EdgeSpan", fontWeight = FontWeight.SemiBold, fontSize = 20.sp)
                Text((strings["versionLabel"] ?: "Version") + " " + BuildConfig.VERSION_NAME + "  ·  " + (strings["versionBuild"] ?: "Build") + " " + BuildConfig.VERSION_CODE, color = MaterialTheme.colorScheme.primary)
                Text((strings["buildDateLabel"] ?: "Built") + " " + BuildConfig.BUILD_DATE + "  ·  " + (strings["protocolVersion"] ?: "Protocol") + " " + BuildConfig.PROTOCOL, color = Muted(), fontSize = 12.sp)
            }
        }
        Text(strings["aboutBody"] ?: "", color = Muted())
        Text(licenseLine(strings, licenseCode, licenseDays), fontWeight = FontWeight.SemiBold)
        Text(strings["diagPathHint"] ?: "", color = Muted(), fontSize = 12.sp)
        EOutlined(onClick = onExportLog) { Text(strings["diagExport"] ?: "Export diagnostics log") }
        Text(strings["eulaShort"] ?: "", color = Muted(), fontSize = 12.sp)
    }
}

private fun licenseLine(strings: Map<String, String>, code: Int, days: Int): String = when (code) {
    1 -> (strings["trialActive"] ?: "Trial active, {0} days left").replace("{0}", days.toString())
    2 -> strings["editionPro"] ?: "Pro"
    3 -> strings["editionFamily"] ?: "Family"
    4 -> strings["trialExpired"] ?: "Trial ended"
    5 -> strings["invalidKey"] ?: "That key is not valid."
    else -> strings["licenseOnPc"] ?: "License and trial status come from the computer once it is connected."
}

@Composable
private fun Card(content: @Composable () -> Unit) {
    Column(Modifier.fillMaxWidth().clip(RoundedCornerShape(8.dp)).border(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.35f), RoundedCornerShape(8.dp)).background(MaterialTheme.colorScheme.surface).padding(16.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) { content() }
}

@Composable
private fun Stat(label: String, value: String, modifier: Modifier) {
    Column(modifier.clip(RoundedCornerShape(8.dp)).border(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.35f), RoundedCornerShape(8.dp)).background(MaterialTheme.colorScheme.surface).padding(14.dp)) {
        Text(label, color = Muted(), fontSize = 12.sp)
        Text(value, fontWeight = FontWeight.SemiBold)
    }
}

@Composable
private fun Flag(label: String, checked: Boolean, onChange: (Boolean) -> Unit) {
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        Switch(checked, onChange)
        Text(label)
    }
}

@Composable
private fun EButton(onClick: () -> Unit, modifier: Modifier = Modifier, colors: ButtonColors = ButtonDefaults.buttonColors(), content: @Composable RowScope.() -> Unit) {
    val source = remember { MutableInteractionSource() }
    val pressed by source.collectIsPressedAsState()
    val scale by animateFloatAsState(if (pressed && !LocalReduce.current) 0.97f else 1f, spring(dampingRatio = 0.92f, stiffness = 1540f), label = "press")
    Button(onClick = onClick, modifier = modifier.graphicsLayer { scaleX = scale; scaleY = scale }, colors = colors, interactionSource = source, content = content)
}

@Composable
private fun EOutlined(onClick: () -> Unit, modifier: Modifier = Modifier, content: @Composable RowScope.() -> Unit) {
    val source = remember { MutableInteractionSource() }
    val pressed by source.collectIsPressedAsState()
    val scale by animateFloatAsState(if (pressed && !LocalReduce.current) 0.97f else 1f, spring(dampingRatio = 0.92f, stiffness = 1540f), label = "press")
    OutlinedButton(onClick = onClick, modifier = modifier.graphicsLayer { scaleX = scale; scaleY = scale }, interactionSource = source, content = content)
}

@Composable
private fun CheckMark() {
    val reveal by animateFloatAsState(1f, if (LocalReduce.current) tween(0) else spring(dampingRatio = 0.86f, stiffness = 500f), label = "check")
    Canvas(Modifier.size(22.dp).graphicsLayer { scaleX = reveal; scaleY = reveal }) {
        val path = Path().apply {
            moveTo(4f, size.height * 0.55f)
            lineTo(size.width * 0.42f, size.height * 0.78f)
            lineTo(size.width - 3f, size.height * 0.28f)
        }
        drawPath(path, Mint, style = Stroke(width = 3f, cap = StrokeCap.Round, join = StrokeJoin.Round))
    }
}

@Composable
private fun StrokeIcon(kind: Int, tint: Color) {
    Canvas(Modifier.size(22.dp)) {
        val stroke = Stroke(width = 1.7f * density, cap = StrokeCap.Round, join = StrokeJoin.Round)
        when (kind) {
            0 -> {
                drawCircle(tint, 3.2f * density, Offset(5f * density, size.height / 2f), style = stroke)
                drawCircle(tint, 3.2f * density, Offset(size.width - 5f * density, size.height / 2f), style = stroke)
                drawLine(tint, Offset(8.5f * density, size.height / 2f), Offset(size.width - 8.5f * density, size.height / 2f), strokeWidth = stroke.width, cap = StrokeCap.Round)
            }
            1 -> drawRoundRect(tint, Offset(4f * density, 4f * density), Size(size.width - 8f * density, size.height - 8f * density), CornerRadius(3f * density, 3f * density), style = stroke)
            else -> {
                drawLine(tint, Offset(4f * density, 7f * density), Offset(size.width - 4f * density, 7f * density), strokeWidth = stroke.width, cap = StrokeCap.Round)
                drawLine(tint, Offset(4f * density, size.height / 2f), Offset(size.width - 4f * density, size.height / 2f), strokeWidth = stroke.width, cap = StrokeCap.Round)
                drawLine(tint, Offset(4f * density, size.height - 7f * density), Offset(size.width - 4f * density, size.height - 7f * density), strokeWidth = stroke.width, cap = StrokeCap.Round)
            }
        }
    }
}

@Composable
private fun Choice(label: String, selected: Boolean, onClick: () -> Unit) {
    Box(Modifier.clip(RoundedCornerShape(8.dp)).background(if (selected) Blue else MaterialTheme.colorScheme.surface).clickable(onClick = onClick).padding(horizontal = 14.dp, vertical = 10.dp)) {
        Text(label, color = if (selected) Color.White else MaterialTheme.colorScheme.onSurface)
    }
}

@Composable
private fun Mark() {
    Image(
        painterResource(R.drawable.ic_brand),
        contentDescription = "EdgeSpan",
        modifier = Modifier.size(84.dp)
    )
}

@Composable
private fun Illustration(index: Int) {
    Canvas(Modifier.fillMaxWidth().height(150.dp)) {
        val w = size.width
        val h = size.height
        drawRoundRect(Blue, Offset(w * 0.08f, h * 0.28f), Size(w * 0.40f, h * 0.42f), CornerRadius(16f, 16f))
        drawRoundRect(Blue, Offset(w * 0.56f, h * 0.18f), Size(w * 0.22f, h * 0.58f), CornerRadius(14f, 14f))
        val path = Path().apply {
            moveTo(w * 0.30f, h * 0.58f)
            quadraticTo(w * 0.46f, h * 0.36f, w * 0.64f, h * 0.42f)
        }
        drawPath(path, BlueLift, style = Stroke(width = 5f, cap = StrokeCap.Round))
        drawCircle(BlueLift, 7f, Offset(w * 0.66f, h * 0.44f))
    }
}
