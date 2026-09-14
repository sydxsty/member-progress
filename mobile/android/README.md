# 成员进度 Android

Android 10 及以上。原生外壳加载 Windows 提供的 `/mobile/index.html`，日常页面调整无需重装 APK。Windows 是成员、任务、归档、提醒和 GPT 会话的唯一主数据来源。

首次打开输入电脑提供的 HTTP 地址和密码。登录后只保存设备令牌；不在 APK 中内置密码或 frp token。退出登录会清除令牌、提醒计划并取消手机上尚未触发的通知。电脑离线时无法保存修改或使用 GPT，已经同步的提醒仍按缓存安排通知。

## 提醒

打开手机应用立即尝试同步，后台由 WorkManager 每 15 分钟申请同步一次；Android 可能因省电限制延后同步。未来 90 天计划保存在应用私有目录，每个提醒只向系统安排下一次，触发后接续下一次。手机重启、应用覆盖升级及时间调整后重建系统安排。

首次登录会提示通知权限及“闹钟和提醒”权限。通知默认静音；未允许准时提醒时使用系统允许的普通定时提醒，可能延后。实际通知行为仍受手机系统设置影响。首次同步不会集中补发电脑过去 24 小时已经触发过的提醒。后续同步会补发上次成功同步之后到期、且仍在最近 24 小时内的新提醒；较旧的服务器计划不会覆盖已接收的新计划。

## 桥接接口

`window.MobileNative.request(id, method, path, bodyJson)` 异步返回 `window.MobileNativeResult(id, status, bodyString)`。网络失败的 status 为 0，body 为带 error 字段的 JSON。仅开放 `/api/mobile` 中指定接口，令牌由原生添加，不交给网页。

- `getConnection()` 返回 JSON `{baseUrl, connected}`。
- `getNotificationStatus()` 返回 JSON `{notificationPermission, exactAlarmPermission, cachedCount, lastSync, lastError}`。
- `nativeReady()` 在网页挂好回调后调用，投递待处理的通知跳转。
- `requestSync()` 尝试同步提醒缓存。
- `requestNotificationPermissions()` 打开相关系统设置。
- `reconnect()` 打开原生连接页；`logout()` 退出登录并清除手机提醒。
- 通知点击调用网页 `MobileOpenScope(scopeType, scopeId)`；状态变化调用可选 `MobileNotificationStatus(statusObject)`。
- Android 返回操作调用网页 `MobileBack()`，网页负责关闭详情或回到成员页。

WebView 只允许配置服务地址下的 `/mobile/*` 以及精确的 `/gantt.js`、`/logo.png` 资源，不允许本机文件、任意外站导航或下载。页面业务不使用 WebView 持久缓存、Cookie 或 DOM 存储。

## 构建

使用 JDK 17、Android SDK 36 / Build Tools 36.0.0、Gradle 8.11.1。Android Gradle Plugin 为 8.10.1，Kotlin 2.1.20，WorkManager 2.10.1。

在 `local.properties` 指定本机 `sdk.dir`。安装环境后运行 `./gradlew assembleRelease lintRelease`。正式签名从环境变量读取：`MEMBER_ANDROID_KEYSTORE`、`MEMBER_ANDROID_STORE_PASSWORD`、`MEMBER_ANDROID_KEY_PASSWORD`，别名固定为 `member-progress`。私有签名材料放在仓库外，后续升级必须继续使用同一签名。未提供签名变量时产生未签名产物，不能直接安装。

包名 `cc.flytown.memberprogress`，版本 `1.0.7`（versionCode 7）。APK 位于 `app/build/outputs/apk/release/`，构建目录、私钥与本机配置不进入 Git。

## 验证边界

本次执行发布构建、Android Lint 及 APK 签名/清单检查。没有下载模拟器；真实手机安装、锁屏通知和厂商省电行为需要在实际设备上验收，不能由编译结果替代。
