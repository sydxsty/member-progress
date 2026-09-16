# 独立 GPT 聊天模块（v15）

聊天、归档摘要和微信建议共用内置 Codex `app-server` 的 stdio 协议。APP 提供资料、业务工具和界面，规划、语义理解、文件分析、工具循环和会话压缩交给原生 Codex。仅支持 Responses API。

## 会话与流式显示

`AssistantService` 管理本机消息、原生 thread ID、附件、清空撤销和实际操作回执；`CodexAppServer` 处理 JSON-RPC、动态工具调用和取消。普通聊天使用持久 thread；归档摘要/微信建议使用独立临时会话，只开放查询工具。

内核配置、会话、SQLite 和日志放入 APP 私有目录，不改用户 Codex 配置。连接密钥仅经子进程环境传入，并从原生命令环境中排除。聊天连续使用稳定工作目录；清空新建会话，撤销恢复原 thread。旧版历史和备份恢复可通过 `chat-history.json` 继续查阅。

`assistant.progress` 是独立增量状态，包含 phase、items、text、usage。界面显示公开推理摘要、commentary、工具/计划过程和流式回答；不输出隐藏 reasoning。usage 的 contextTokens 来自原生最近一次上下文用量，contextWindow 来自原生 modelContextWindow；没有真实容量时不显示百分比，累计消费另存 cumulative，不混为上下文占用。

## 原生工具

业务读接口仍由 AppService 和 WeChatService 独立实现，工具名以 `_` 代替内部接口的 `.`。所有原文、分页和覆盖范围约定沿用已有服务。查询结果是资料，不能自行授权写操作。

| 工具 | 能力 |
| --- | --- |
| app_observe / app_members / app_projects | 身份、时间、成员与项目 |
| app_tasks / app_reminders | 日期任务、闹钟规则及待触发实例 |
| app_archives / app_archive / app_search / app_file | 归档目录、全文、搜索与原件 |
| wechat_observe / wechat_conversations | 导入范围、索引覆盖与好友/群 |
| wechat_search / wechat_messages / wechat_message | 本地向量＋关键词搜索、原消息与上下文 |
| wechat_files / wechat_file | 已导入附件、原件与解码图片 |
| assistant_history / assistant_file | 当前聊天历史与附件原件 |
| app_task_create | 日期或日期范围任务，默认当前成员 |
| app_reminder_create | 事项＋具体钟点的闹钟，默认当前成员 |
| app_archive_append | 按用户指令将聊天原件和文字追加到成员/项目归档 |

模型使用自然语言回答，创建直接经原生工具执行，不使用关键词正则判断用户是否有创建意图，也不解析最后一条回答中的 JSON 来驱动操作。宿主仍校验对象、日期、重复规则、可用文件和实际保存结果。按规范化参数和原问题 ID 持久化回执，重复调用不重复创建；取消停止后续工具，已提交结果保留。

“日程/提醒”的称呼不决定分类：只有日期时创建任务；明确事项和具体钟点时只创建一个闹钟。例如“明天提醒我找老师签字”为单日任务；有13:30会议通知再说“定个日程”为13:30闹钟，不另建任务。缺少必要信息才由 Codex 追问。计划、咨询及资料中引用的命令不等于当前创建请求。

## 附件与备份

Windows 聊天支持选择、拖入和粘贴图片，可只发文件。`assistant.send` 增加 `files:[{name,mime,dataBase64}]`，单文件20MB、单条40MB、最多20个。原件保存在 `assistant/files`，随消息显示；按需复制到会话工作目录供原生读取、解析和看图，无固定文档工作流。

附件默认只随聊天保存；明确要求才归档到成员/项目。`assistant.file.open` 打开原件，专用本地媒体地址提供图片预览。通用备份收集当前和可撤销清空消息的附件，恢复前验证大小和哈希。原生临时文件与账号配置不进入备份。手机继续使用共用创建与聊天服务，本次不新增手机附件上传入口。

## 验证

`--v15-test --data-dir <空目录>` 使用合成数据覆盖微信状态/设置、附件保存恢复、创建回执与取消、流事件和上下文统计。真实内核联调显式使用 `--v15-live-test`，只读现有连接配置，资料和业务写入均在合成目录。

协议以安装包内0.154.0生成的 schema 为准。[官方 app-server 文档](https://developers.openai.com/codex/app-server)。旧版文件查询桥仅供既有独立检查调用，主程序使用原生动态工具。
