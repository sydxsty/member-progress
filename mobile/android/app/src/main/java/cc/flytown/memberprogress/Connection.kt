package cc.flytown.memberprogress

import android.content.Context
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URI
import java.net.URL
import java.util.concurrent.atomic.AtomicBoolean

internal data class HttpResult(val status: Int, val body: String)

internal class Connection(context: Context) {
    private val prefs = context.getSharedPreferences("connection", Context.MODE_PRIVATE)
    val baseUrl: String get() = prefs.getString("url", "http://nas.flytown.cc:27521")!!
    val token: String get() = prefs.getString("token", "")!!
    val signedIn: Boolean get() = token.isNotEmpty()
    fun save(url: String, token: String) { prefs.edit().putString("url", url).putString("token", token).commit() }
    fun clear() { prefs.edit().remove("token").commit(); connected.set(false) }
    fun permissionAsked(): Boolean = prefs.getBoolean("permissionAsked", false)
    fun markPermissionAsked() { prefs.edit().putBoolean("permissionAsked", true).apply() }

    fun request(method: String, path: String, body: String = "", overrideUrl: String? = null): HttpResult {
        require(method in setOf("GET", "POST")) { "不支持的请求" }
        val parsed = URI(path)
        val allowed = if (method == "GET") setOf("/api/mobile/state", "/api/mobile/assistant", "/api/mobile/reminder-plan", "/api/mobile/changes")
                      else setOf("/api/mobile/login", "/api/mobile/action", "/api/mobile/logout")
        require(parsed.path in allowed && parsed.scheme == null && parsed.rawAuthority == null && parsed.fragment == null) { "不支持的地址" }
        if (path != "/api/mobile/login") require(signedIn) { "请重新登录" }
        val target = (overrideUrl ?: baseUrl) + path
        val c = URL(target).openConnection() as HttpURLConnection
        try {
            c.requestMethod = method
            c.connectTimeout = 12000
            c.readTimeout = if (parsed.path == "/api/mobile/changes") 33000 else 120000
            c.instanceFollowRedirects = false
            c.useCaches = false
            c.setRequestProperty("Accept", "application/json")
            c.setRequestProperty("Cache-Control", "no-store")
            if (path != "/api/mobile/login") c.setRequestProperty("Authorization", "Bearer $token")
            if (method == "POST") {
                c.doOutput = true
                c.setRequestProperty("Content-Type", "application/json; charset=utf-8")
                val bytes = body.ifEmpty { "{}" }.toByteArray(Charsets.UTF_8)
                c.setFixedLengthStreamingMode(bytes.size)
                c.outputStream.use { it.write(bytes) }
            }
            val status = c.responseCode
            val stream = if (status in 200..299) c.inputStream else c.errorStream
            val bytes = stream?.use { it.readBytesLimited(12 * 1024 * 1024) } ?: ByteArray(0)
            connected.set(status in 200..499 && status != 401)
            return HttpResult(status, bytes.toString(Charsets.UTF_8))
        } catch (e: Exception) { connected.set(false); throw e }
        finally { c.disconnect() }
    }

    companion object {
        val connected = AtomicBoolean(false)
        fun normalizeUrl(input: String): String {
            val value = input.trim().let { if (it.contains("://")) it else "http://$it" }.trimEnd('/')
            val u = URI(value)
            require(u.scheme in setOf("http", "https") && !u.host.isNullOrEmpty() && u.userInfo == null && u.query == null && u.fragment == null && u.path.isNullOrEmpty()) { "请输入服务器地址，例如 http://nas.flytown.cc:27521" }
            require(u.port == -1 || u.port in 1..65535) { "端口不正确" }
            return value
        }
        fun errorJson(message: String): String = JSONObject().put("error", message).toString()
        private fun java.io.InputStream.readBytesLimited(limit: Int): ByteArray {
            val out = java.io.ByteArrayOutputStream()
            val b = ByteArray(8192)
            while (true) { val n = read(b); if (n < 0) break; require(out.size() + n <= limit) { "服务器返回内容过大" }; out.write(b, 0, n) }
            return out.toByteArray()
        }
    }
}
