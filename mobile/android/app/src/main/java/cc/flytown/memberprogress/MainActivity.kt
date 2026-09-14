package cc.flytown.memberprogress

import android.Manifest
import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.graphics.Color
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.text.InputType
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.view.WindowInsets
import android.webkit.*
import android.widget.*
import org.json.JSONObject
import java.io.ByteArrayInputStream
import java.net.URI
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

class MainActivity : Activity() {
    private lateinit var root: LinearLayout
    private lateinit var connection: Connection
    private var web: WebView? = null
    private val io = Executors.newFixedThreadPool(5)
    private var pendingScope: Pair<String, String>? = null
    private var pageReady = false
    private var webGeneration = 0
    private val syncing = AtomicBoolean(false)
    private var wasInSettings = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        connection = Connection(this)
        root = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setBackgroundColor(Color.rgb(247,249,252)) }
        setContentView(root)
        root.setOnApplyWindowInsetsListener { v, insets ->
            if (Build.VERSION.SDK_INT >= 30) {
                val i = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.ime())
                v.setPadding(i.left, i.top, i.right, i.bottom)
            } else { @Suppress("DEPRECATION") v.setPadding(insets.systemWindowInsetLeft, insets.systemWindowInsetTop, insets.systemWindowInsetRight, insets.systemWindowInsetBottom) }
            insets
        }
        if (Build.VERSION.SDK_INT >= 33) onBackInvokedDispatcher.registerOnBackInvokedCallback(android.window.OnBackInvokedDispatcher.PRIORITY_DEFAULT) { handleBack() }
        captureScope(intent)
        if (connection.signedIn) { Reminders.start(this); showWeb(); sync() } else showConnection()
    }
    override fun onNewIntent(intent: Intent) { super.onNewIntent(intent); setIntent(intent); captureScope(intent); deliverScope() }
    private fun captureScope(intent: Intent) {
        val type = intent.getStringExtra("scopeType") ?: return
        if (type in setOf("student", "project", "global")) pendingScope = type to (intent.getStringExtra("scopeId") ?: "")
    }
    override fun onResume() {
        super.onResume()
        web?.onResume(); web?.resumeTimers()
        if (::connection.isInitialized && connection.signedIn) {
            Reminders.schedule(this)
            if (wasInSettings) { wasInSettings = false; notifyStatus() }
            sync()
        }
    }
    override fun onPause() { web?.onPause(); web?.pauseTimers(); super.onPause() }
    override fun onRestart() { super.onRestart(); web?.onResume() }
    override fun onDestroy() { webGeneration++; web?.removeJavascriptInterface("MobileNative"); web?.destroy(); web = null; io.shutdownNow(); super.onDestroy() }
    @Deprecated("Android system navigation")
    override fun onBackPressed() { handleBack() }
    private fun handleBack() {
        val current = web
        if (current == null) { finish(); return }
        current.evaluateJavascript("typeof window.MobileBack==='function'?window.MobileBack():false") { result -> if (result == "false") finish() }
    }
    private fun dp(value: Int) = (resources.displayMetrics.density * value).toInt()
    private fun text(value: String, size: Float = 15f) = TextView(this).apply { text = value; textSize = size; setTextColor(Color.rgb(35,47,66)) }

    private fun showConnection(message: String = "") {
        pageReady = false; webGeneration++
        web?.removeJavascriptInterface("MobileNative"); web?.stopLoading(); web?.destroy(); web = null
        root.removeAllViews()
        val scroll = ScrollView(this)
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(26), dp(42), dp(26), dp(24)) }
        scroll.addView(box); root.addView(scroll, LinearLayout.LayoutParams(-1,-1))
        val logo = ImageView(this).apply { setImageResource(R.drawable.logo) }
        box.addView(logo, LinearLayout.LayoutParams(dp(62),dp(62)))
        box.addView(text("成员进度", 27f).apply { setPadding(0,dp(18),0,dp(8)) })
        box.addView(text("连接电脑，继续查看与更新进展").apply { setPadding(0,0,0,dp(24)) })
        box.addView(text("电脑连接地址",13f))
        val address = EditText(this).apply { setSingleLine(); inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI; setText(connection.baseUrl); textSize = 16f }
        box.addView(address)
        box.addView(text("连接密码",13f).apply { setPadding(0,dp(15),0,0) })
        val password = EditText(this).apply { setSingleLine(); inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD; hint = if (connection.signedIn) "已记住登录；重新登录时填写" else "与电脑中设置的密码一致"; textSize=16f }
        box.addView(password)
        val status = text(message).apply { setTextColor(Color.rgb(156,70,48)); setPadding(0,dp(12),0,dp(12)) }
        box.addView(status)
        val connect = Button(this).apply { text = if (connection.signedIn) "重新连接" else "连接"; isAllCaps = false }
        box.addView(connect, LinearLayout.LayoutParams(-1,dp(52)))
        connect.setOnClickListener {
            val url = try { Connection.normalizeUrl(address.text.toString()) } catch (e: Exception) { status.text = e.message; return@setOnClickListener }
            val pass = password.text.toString()
            val useToken = connection.signedIn && url == connection.baseUrl && pass.isEmpty()
            if (!useToken && pass.isBlank()) { status.text = "请输入电脑端设置的密码"; return@setOnClickListener }
            connect.isEnabled = false; status.text = "正在连接…"
            io.execute {
                try {
                    val result = if (useToken) connection.request("GET","/api/mobile/state")
                    else connection.request("POST", "/api/mobile/login", JSONObject().put("password",pass).put("deviceName", "${Build.MANUFACTURER} ${Build.MODEL}").toString(), url)
                    if (result.status !in 200..299) throw IllegalStateException(if (result.status==401) "密码不正确或登录已失效" else try { JSONObject(result.body).optString("error", "连接失败（${result.status}）") } catch (_: Exception) { "连接失败（${result.status}）" })
                    if (!useToken) {
                        val response = JSONObject(result.body)
                        require(response.optInt("protocolVersion") == 1 && response.optString("token").isNotBlank()) { "电脑版本不兼容，请更新电脑端" }
                        if (connection.baseUrl != url || connection.token != response.getString("token")) { connection.clear(); Reminders.clear(applicationContext) }
                        connection.save(url,response.getString("token"))
                    }
                    runOnUiThread { if (!isDestroyed) { password.text.clear(); Reminders.start(this); showWeb(); sync(); guidePermissions() } }
                } catch (e: Exception) { runOnUiThread { if (!isDestroyed) { status.text = e.message ?: "暂时无法连接电脑，请确认电脑已开启手机连接"; connect.isEnabled = true } } }
            }
        }
        if (connection.signedIn) {
            box.addView(Button(this).apply { text="退出登录"; setOnClickListener { logout() } })
            val cached = JSONObject(Reminders.status(this)).optInt("cachedCount")
            if (cached > 0) box.addView(text("已缓存 $cached 次提醒，电脑离线时仍按缓存安排提醒。",13f).apply { setPadding(0,dp(12),0,0) })
        }
        box.addView(text("电脑需保持运行。首次连接后自动记住登录。",13f).apply { setPadding(0,dp(22),0,0) })
    }

    @Suppress("SetJavaScriptEnabled")
    private fun showWeb() {
        pageReady = false; webGeneration++
        root.removeAllViews()
        val view = WebView(this); web = view
        view.setBackgroundColor(Color.rgb(247,249,252))
        with(view.settings) {
            javaScriptEnabled = true; domStorageEnabled = false; databaseEnabled = false
            cacheMode = WebSettings.LOAD_NO_CACHE; allowFileAccess = false; allowContentAccess = false
            setSupportMultipleWindows(false); javaScriptCanOpenWindowsAutomatically = false
            mixedContentMode = WebSettings.MIXED_CONTENT_NEVER_ALLOW
            mediaPlaybackRequiresUserGesture = true
        }
        CookieManager.getInstance().setAcceptCookie(false)
        CookieManager.getInstance().setAcceptThirdPartyCookies(view, false)
        ServiceWorkerController.getInstance().serviceWorkerWebSettings.apply { allowFileAccess=false; allowContentAccess=false; blockNetworkLoads=true; cacheMode=WebSettings.LOAD_NO_CACHE }
        view.clearCache(true)
        view.addJavascriptInterface(NativeBridge(), "MobileNative")
        view.webViewClient = object : WebViewClient() {
            override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean = !trusted(request.url.toString())
            override fun shouldInterceptRequest(view: WebView, request: WebResourceRequest): WebResourceResponse? {
                return if (trusted(request.url.toString(), resource = true)) null else WebResourceResponse("text/plain", "utf-8",403,"Blocked",emptyMap(),ByteArrayInputStream(ByteArray(0)))
            }
            override fun onPageFinished(view: WebView, url: String) { if (trusted(url)) view.clearHistory() }
            override fun onReceivedError(view: WebView, request: WebResourceRequest, error: WebResourceError) {
                if (request.isForMainFrame) runOnUiThread { if (web === view) showConnection("暂时无法连接电脑，请确认电脑正在运行。") }
            }
            override fun onReceivedHttpError(view: WebView, request: WebResourceRequest, response: WebResourceResponse) {
                if (request.isForMainFrame && response.statusCode >= 400) runOnUiThread { if (web === view) showConnection("手机页面暂不可用，请确认电脑端已更新并开启手机连接。") }
            }
        }
        view.setDownloadListener { _,_,_,_,_ -> Toast.makeText(this,"请在电脑端查看附件",Toast.LENGTH_SHORT).show() }
        root.addView(view, LinearLayout.LayoutParams(-1,-1))
        view.loadUrl(connection.baseUrl + "/mobile/index.html", mapOf("Cache-Control" to "no-cache"))
    }
    private fun trusted(value: String, resource: Boolean = false): Boolean = try {
        val a = URI(connection.baseUrl); val b = URI(value)
        a.scheme == b.scheme && a.host.equals(b.host, true) && a.port == b.port && b.userInfo == null && (b.path.startsWith("/mobile/") || (resource && b.path in setOf("/gantt.js", "/logo.png"))) && !b.path.split('/').contains("..")
    } catch (_: Exception) { false }
    private fun sync() {
        if (!connection.signedIn || !syncing.compareAndSet(false,true) || io.isShutdown) return
        io.execute { try { Reminders.sync(applicationContext) } finally { syncing.set(false); runOnUiThread { notifyStatus() } } }
    }
    private fun notifyStatus() { if (pageReady) web?.evaluateJavascript("window.MobileNotificationStatus&&window.MobileNotificationStatus(${Reminders.status(this)})",null) }
    private fun deliverScope() {
        if (!pageReady) return
        val scope = pendingScope ?: return
        pendingScope = null
        web?.evaluateJavascript("window.MobileOpenScope&&window.MobileOpenScope(${JSONObject.quote(scope.first)},${JSONObject.quote(scope.second)})",null)
    }
    private fun logout() {
        connection.clear(); Reminders.clear(this); pendingScope=null
        CookieManager.getInstance().removeAllCookies(null); WebStorage.getInstance().deleteAllData(); web?.clearCache(true)
        showConnection()
    }
    private fun guidePermissions(force: Boolean = false) {
        if (!force && connection.permissionAsked()) return
        connection.markPermissionAsked()
        if (Build.VERSION.SDK_INT >= 33 && !Reminders.canNotify(this)) requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 20)
        else guideExact()
    }
    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) { super.onRequestPermissionsResult(requestCode,permissions,grantResults); if (requestCode==20) { guideExact(); notifyStatus() } }
    private fun guideExact() {
        if (Build.VERSION.SDK_INT >= 31 && !Reminders.canExact(this)) AlertDialog.Builder(this).setTitle("准时提醒")
            .setMessage("允许“闹钟和提醒”后，手机可按指定时间静音通知。未允许时，系统可能延后提醒。")
            .setPositiveButton("去设置") { _,_ -> try { wasInSettings=true; startActivity(Intent(Settings.ACTION_REQUEST_SCHEDULE_EXACT_ALARM,Uri.parse("package:$packageName"))) } catch (_: Exception) { Toast.makeText(this,"请在系统设置中允许闹钟和提醒",Toast.LENGTH_LONG).show() } }
            .setNegativeButton("稍后", null).show()
    }
    private inner class NativeBridge {
        @JavascriptInterface fun getConnection(): String = JSONObject().put("baseUrl",connection.baseUrl).put("connected",Connection.connected.get()).toString()
        @JavascriptInterface fun getNotificationStatus(): String = Reminders.status(applicationContext)
        @JavascriptInterface fun nativeReady() { runOnUiThread { pageReady=true; deliverScope(); notifyStatus() } }
        @JavascriptInterface fun requestSync() { runOnUiThread { sync() } }
        @JavascriptInterface fun logout() { runOnUiThread { this@MainActivity.logout() } }
        @JavascriptInterface fun reconnect() { runOnUiThread { showConnection() } }
        @JavascriptInterface fun requestNotificationPermissions() { runOnUiThread { if (!Reminders.canNotify(this@MainActivity) && connection.permissionAsked()) { wasInSettings=true; startActivity(Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).putExtra(Settings.EXTRA_APP_PACKAGE,packageName)) } else guidePermissions(true) } }
        @JavascriptInterface fun request(id: String, method: String, path: String, bodyJson: String) {
            if (id.length > 128 || bodyJson.length > 4*1024*1024 || io.isShutdown) return
            val generation = webGeneration; val token = connection.token
            io.execute {
                val response = try {
                    require(path != "/api/mobile/login") { "请在连接页登录" }
                    connection.request(method,path,bodyJson)
                } catch (e: Exception) { HttpResult(0,Connection.errorJson(e.message ?: "暂时无法连接电脑")) }
                runOnUiThread { if (generation == webGeneration && connection.token == token) web?.evaluateJavascript("window.MobileNativeResult&&window.MobileNativeResult(${JSONObject.quote(id)},${response.status},${JSONObject.quote(response.body)})",null) }
                if (method=="POST" && response.status in 200..299 && path=="/api/mobile/action") sync()
            }
        }
    }
}
