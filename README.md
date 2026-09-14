# 成员进度 · Member Progress

<img src="app/web/logo.png" alt="成员进度 Logo" width="64">

用于跟进成员与项目进度的 Windows 本地桌面应用。记一条进展、拖动任务日期或勾选完成，软件负责保存归档、生成摘要和到点提醒。

当前版本：**1.0.7**。不需要应用账号，首次打开为空白列表。

## 功能

- **成员总览**：姓名、可选年级与 meta 标签、bio；当前进展可直接编辑，成员可拖动排序。
- **甘特任务**：拖动日期创建任务、移动与调整期限，一键完成或提交归档；时间不冲突的任务共用一排。
- **追加式归档**：发送文本、文件并自动生成 GPT 摘要，保留历史记录。
- **项目协作**：按成员组成项目，共享任务与归档，成员行显示参与项目。
- **提醒**：全局、成员、项目提醒及每天、每周、每月、间隔重复；甘特图标注，静音 Windows 通知。
- **GPT 聊天**：参考提醒和最近归档给建议，一键引用整个归档；自然语言创建简单提醒，支持清空与撤销清空会话。
- **本地资料**：自动保存、附件复制、完整备份与恢复；密钥由 Windows 加密保存。

## Android 手机端

手机端与 Windows 共用成员、任务、提醒、归档和 GPT 会话。成员页可横向滑动或点击切换到甘特图；任务和提醒使用现有 Windows 规则。手机只保留轻量编辑、文字归档及聊天，设置与完整资料仍在电脑管理。

Windows 在 **设置 → 手机连接** 开启访问并设置密码，可配置已有 frps；Windows 内置的是 frpc。手机首次输入地址和密码后记住登录。连接采用 HTTP，密码按个人使用要求明文保存在电脑；连接设置、设备令牌和 frp 口令不进入 Git 或发行包。

手机后台采用省电同步，已缓存提醒由 Android 系统通知；电脑关闭后无法提交修改和使用 GPT。详细操作与通知边界见 [手机连接说明](docs/mobile.md)，Android 源码与构建方法见 [mobile/android](mobile/android/README.md)。

## 界面

以下为虚构成员的界面预览，应用不会预置这些记录。

![成员总览](docs/images/members.png)

![项目](docs/images/projects.png)

## 从源码运行

在 **Windows x64** 上安装 .NET 8 SDK 和 Microsoft Edge WebView2 Runtime，然后在仓库根目录执行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/prepare-frp.ps1
dotnet publish app/StudentProgress.csproj -c Release -r win-x64 --self-contained false -o artifacts/portable
.\artifacts\portable\StudentProgress.exe
```

前端是原生 HTML/CSS/JavaScript，无需 Node 或前端构建步骤。发布结果依赖 .NET 8 Desktop Runtime；完整 .NET 8 SDK 已提供相应开发与运行支持。其他电脑运行时需要 .NET 8 Desktop Runtime 和 WebView2。

源码仓库不存放 Codex CLI 二进制。首次从源码启动后，在 **设置 → GPT 聊天内核 → 更新内核** 下载官方稳定版；应用会校验 SHA512 后启用。自动更新默认关闭。已交付便携版随附的初始版本为 **0.154.0**，对应版本及完整性信息保存在 `app/runtime/codex/bundled.json`。

GPT 摘要和聊天需要在设置中配置可用的模型接口。不开启 GPT 也可管理成员、任务、归档与提醒；使用 GPT 时，所选上下文会发送到配置的接口。

资料默认保存在可执行文件旁的 `Data` 目录。开发时可使用独立目录：

```powershell
.\artifacts\portable\StudentProgress.exe --data-dir .\artifacts\dev-data
```

## 代码结构

| 路径 | 内容 |
| --- | --- |
| `app/AppService*.cs` | 成员、项目、任务、归档、提醒、GPT 摘要和备份 |
| `app/MainForm.cs` | WebView2 宿主及前后端消息桥接 |
| `app/Notifications.cs` | Windows 静音通知及点击唤起 |
| `app/Assistant/` | 独立 GPT 聊天、Codex CLI 调用及更新模块 |
| `app/web/` | 电脑页面、样式、甘特图和聊天界面 |
| `app/web/mobile/` | 随 Windows 更新的手机页面 |
| `app/Remote/` | 手机 HTTP 接口、连接设置与 frpc 管理 |
| `mobile/android/` | Android 外壳、登录及静音定时提醒 |
| `app/*Checks.cs` | 使用隔离目录的定向检查 |

界面使用“成员”，内部 `StudentProgress`、`student` 数据键和接口名保留，以兼容已有资料。

[使用说明](docs/usage.md) · [设计方案](docs/design.md) · [聊天模块说明](app/Assistant/README.md)

## 检查

下面两项离线检查不调用 GPT。它们会创建检查记录，务必使用独立目录：

```powershell
Start-Process .\artifacts\portable\StudentProgress.exe -ArgumentList '--assistant-reminders-test --data-dir artifacts/checks/reminders' -Wait
Start-Process .\artifacts\portable\StudentProgress.exe -ArgumentList '--assistant-actions-test --data-dir artifacts/checks/assistant' -Wait
```

结果分别写入指定目录中的 `reminder-result.json` 与 `assistant-action-result.json`。带 `live-test` 的入口会调用真实模型，需要单独配置，不作为默认检查。

仓库仅保存源码、文档与选定的图片；个人资料、API 密钥、编译缓存、Codex 二进制及本机交付包均被忽略。
