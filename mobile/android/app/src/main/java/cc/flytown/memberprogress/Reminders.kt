package cc.flytown.memberprogress

import android.Manifest
import android.app.*
import android.content.*
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.util.AtomicFile
import androidx.work.*
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.time.Instant
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

internal object Reminders {
    private const val CHANNEL = "member-reminders-silent"
    private const val WORK = "member-reminder-plan"
    private val lock = Any()
    private fun prefs(c: Context) = c.getSharedPreferences("reminder-status", Context.MODE_PRIVATE)
    private fun file(c: Context) = AtomicFile(File(c.filesDir, "reminder-plan.json"))
    private fun plan(c: Context): JSONObject = try { JSONObject(file(c).readFully().toString(Charsets.UTF_8)) } catch (_: Exception) { JSONObject().put("occurrences", JSONArray()) }
    private fun occurrences(c: Context): List<JSONObject> { val a = plan(c).optJSONArray("occurrences") ?: JSONArray(); return (0 until a.length()).mapNotNull { a.optJSONObject(it) } }
    private fun timestampMillis(value: String): Long = try { Instant.parse(value).toEpochMilli() } catch (_: Exception) { try { java.time.OffsetDateTime.parse(value).toInstant().toEpochMilli() } catch (_: Exception) { Long.MAX_VALUE } }
    private fun whenMillis(o: JSONObject): Long = timestampMillis(o.optString("when"))
    private fun alarm(c: Context) = c.getSystemService(AlarmManager::class.java)
    private fun notificationManager(c: Context) = c.getSystemService(NotificationManager::class.java)
    fun canNotify(c: Context) = notificationManager(c).areNotificationsEnabled() && (Build.VERSION.SDK_INT < 33 || c.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED)
    fun canExact(c: Context) = Build.VERSION.SDK_INT < 31 || alarm(c).canScheduleExactAlarms()
    fun status(c: Context): String = synchronized(lock) { JSONObject().put("notificationPermission", canNotify(c)).put("exactAlarmPermission", canExact(c)).put("cachedCount", occurrences(c).size).put("lastSync", prefs(c).getString("lastSync", "")).put("lastError", prefs(c).getString("lastError", "")).toString() }
    fun start(c: Context) {
        val req = PeriodicWorkRequestBuilder<ReminderSyncWorker>(15, TimeUnit.MINUTES)
            .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build()).build()
        WorkManager.getInstance(c).enqueueUniquePeriodicWork(WORK, ExistingPeriodicWorkPolicy.KEEP, req)
        ensureChannel(c)
    }
    private fun ensureChannel(c: Context) {
        val ch = NotificationChannel(CHANNEL, "成员与项目提醒", NotificationManager.IMPORTANCE_DEFAULT)
        ch.description = "按电脑端安排提醒，默认静音"
        ch.setSound(null, null); ch.enableVibration(false)
        notificationManager(c).createNotificationChannel(ch)
    }
    fun sync(c: Context): Boolean {
        val connection = Connection(c)
        val token = connection.token
        if (token.isEmpty()) return true
        try {
            val result = connection.request("GET", "/api/mobile/reminder-plan")
            require(result.status in 200..299) { if (result.status == 401) "登录已失效，请重新登录" else "暂时无法同步提醒" }
            val incoming = JSONObject(result.body)
            require(incoming.has("occurrences")) { "提醒数据格式不正确" }
            val serverTime = incoming.optString("serverTime")
            val serverMillis = timestampMillis(serverTime)
            require(serverMillis != Long.MAX_VALUE) { "提醒同步时间不正确" }
            synchronized(lock) {
                if (Connection(c).token != token) return true
                val previousServerMillis = timestampMillis(plan(c).optString("serverTime")).takeIf { it != Long.MAX_VALUE }
                // A later foreground/background response must not restore an older plan.
                if (previousServerMillis != null && serverMillis < previousServerMillis) return true
                val lastSuccessfulSync = previousServerMillis
                    ?: timestampMillis(prefs(c).getString("lastSync", "")!!).takeIf { it != Long.MAX_VALUE }
                val known = occurrences(c).map { it.optString("id") }.toSet()
                val a = incoming.getJSONArray("occurrences")
                require(a.length() <= 50000) { "提醒计划过大" }
                val now = System.currentTimeMillis()
                val retained = JSONArray()
                val seen = HashSet<String>()
                for (i in 0 until a.length()) {
                    val o = a.getJSONObject(i)
                    val id = o.optString("id")
                    val whenMs = whenMillis(o)
                    require(id.isNotEmpty() && o.optString("reminderId").isNotEmpty() && whenMs != Long.MAX_VALUE) { "提醒数据不完整" }
                    val missedSinceSync = lastSuccessfulSync != null && whenMs > lastSuccessfulSync
                    if (seen.add(id) && (whenMs > now || ((id in known || missedSinceSync) && whenMs > now - TimeUnit.HOURS.toMillis(24)))) retained.put(o)
                }
                incoming.put("occurrences", retained)
                val atomic = file(c); val output = atomic.startWrite()
                try { output.write(incoming.toString().toByteArray(Charsets.UTF_8)); atomic.finishWrite(output) } catch (e: Exception) { atomic.failWrite(output); throw e }
                prefs(c).edit().putString("lastSync", serverTime).putString("lastError", "").commit()
                schedule(c)
            }
            return true
        } catch (e: Exception) {
            if (Connection(c).token == token) prefs(c).edit().putString("lastError", e.message ?: "暂时无法连接电脑").commit()
            return false
        }
    }
    private fun pending(c: Context, id: String, flags: Int = PendingIntent.FLAG_UPDATE_CURRENT): PendingIntent? {
        val intent = Intent(c, ReminderReceiver::class.java).setData(Uri.Builder().scheme("member-reminder").authority("occurrence").appendPath(id).build()).putExtra("eventId", id)
        return PendingIntent.getBroadcast(c, 0, intent, flags or PendingIntent.FLAG_IMMUTABLE)
    }
    fun schedule(c: Context) = synchronized(lock) {
        val p = prefs(c)
        for (id in p.getStringSet("scheduled", emptySet())!!) pending(c, id, PendingIntent.FLAG_NO_CREATE)?.let { alarm(c).cancel(it); it.cancel() }
        if (!Connection(c).signedIn) { p.edit().remove("scheduled").commit(); return@synchronized }
        val fired = p.getStringSet("fired", emptySet())!!.toSet()
        val now = System.currentTimeMillis()
        val next = occurrences(c).filter { it.optString("id") !in fired && whenMillis(it) > now - TimeUnit.HOURS.toMillis(24) }
            .groupBy { it.optString("reminderId") }.values.mapNotNull { group -> group.minByOrNull { whenMillis(it) } }.sortedBy { whenMillis(it) }
        val scheduled = HashSet<String>()
        for (o in next) {
            val id = o.getString("id"); val pi = pending(c, id)!!; val due = maxOf(whenMillis(o), now + 500)
            try {
                if (canExact(c)) alarm(c).setExactAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, due, pi)
                else alarm(c).setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, due, pi)
                scheduled.add(id)
            } catch (_: SecurityException) { alarm(c).setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, due, pi); scheduled.add(id) }
            catch (e: RuntimeException) { prefs(c).edit().putString("lastError", "部分提醒暂未安排：${e.message}").commit(); break }
        }
        p.edit().putStringSet("scheduled", scheduled).commit()
    }
    fun fire(c: Context, eventId: String) = synchronized(lock) {
        if (!Connection(c).signedIn) return@synchronized
        val o = occurrences(c).firstOrNull { it.optString("id") == eventId } ?: return@synchronized
        val p = prefs(c); val fired = p.getStringSet("fired", emptySet())!!.toMutableSet()
        if (eventId in fired) return@synchronized
        if (canNotify(c)) {
            ensureChannel(c)
            val intent = Intent(c, MainActivity::class.java).setData(Uri.Builder().scheme("member-open").authority("reminder").appendPath(eventId).build())
                .putExtra("scopeType", o.optString("scopeType", "global")).putExtra("scopeId", o.optString("scopeId")).addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP)
            val open = PendingIntent.getActivity(c, 0, intent, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
            val title = o.optString("title", "提醒")
            val notification = Notification.Builder(c, CHANNEL).setSmallIcon(R.drawable.ic_notification).setContentTitle(title)
                .setContentText(o.optString("scopeLabel", "全局提醒")).setStyle(Notification.BigTextStyle().bigText(title))
                .setContentIntent(open).setAutoCancel(true).setOnlyAlertOnce(true).setDefaults(0).setCategory(Notification.CATEGORY_REMINDER).build()
            notificationManager(c).notify(eventId, 0, notification)
        } else p.edit().putString("lastError", "通知权限未开启，提醒未能显示").commit()
        fired.add(eventId)
        val keep = occurrences(c).map { it.optString("id") }.toSet()
        p.edit().putStringSet("fired", fired.intersect(keep)).commit()
        schedule(c)
    }
    fun clear(c: Context) = synchronized(lock) {
        WorkManager.getInstance(c).cancelUniqueWork(WORK)
        for (id in prefs(c).getStringSet("scheduled", emptySet())!!) pending(c, id, PendingIntent.FLAG_NO_CREATE)?.let { alarm(c).cancel(it); it.cancel() }
        notificationManager(c).cancelAll(); file(c).delete(); prefs(c).edit().clear().commit()
    }
}

class ReminderReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val id = intent.getStringExtra("eventId") ?: return
        val result = goAsync()
        Executors.newSingleThreadExecutor().let { executor -> executor.execute { try { Reminders.fire(context.applicationContext, id) } finally { result.finish(); executor.shutdown() } } }
    }
}

class RestoreReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action !in setOf(Intent.ACTION_BOOT_COMPLETED, Intent.ACTION_MY_PACKAGE_REPLACED, Intent.ACTION_TIME_CHANGED, Intent.ACTION_TIMEZONE_CHANGED, AlarmManager.ACTION_SCHEDULE_EXACT_ALARM_PERMISSION_STATE_CHANGED)) return
        if (!Connection(context).signedIn) return
        val result = goAsync()
        Executors.newSingleThreadExecutor().let { executor -> executor.execute { try { Reminders.start(context); Reminders.schedule(context) } finally { result.finish(); executor.shutdown() } } }
    }
}

class ReminderSyncWorker(context: Context, params: WorkerParameters) : Worker(context, params) {
    override fun doWork(): Result = if (Reminders.sync(applicationContext)) Result.success() else Result.retry()
}
