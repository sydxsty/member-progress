# 独立 GPT 聊天模块

本模块使用内置的官方 Codex CLI，通过应用只读接口自主查找资料、回答问题和给建议，并在当前问题明确要求时提议创建简单提醒或任务。模块不直接依赖 AppService；创建只通过宿主提供的原子委托执行。

## 边界

- AssistantService：聊天持久化、请求/取消、可撤销清空、内核检查更新及状态事件。
- CodexRunner / CodexEvents：结构化参数启动 CLI、读取 JSONL；AssistantResponse 严格解析 text/reminders/tasks 信封，界面只见自然语言与实际保存的提醒、任务卡片。密钥通过单独的子进程环境变量传入。
- AssistantPrompt：最近对话与业务上下文组合。普通聊天提供当前身份、问题、重点资料与查询入口；全库内容按需检索、分页读取，不在每次请求中全部展开。
- CodexRuntime：官方 Windows 包的版本发现、SHA512 校验、安全解包、版本校验和启用；失败保留原版。
- web/assistant.js、assistant.css：模块自己的界面与状态，不抢占成员页面，不接管原归档聊天。
- AppService.AssistantContext.cs：核心业务上下文适配器；AppService.AssistantActions 负责原子校验、创建和按请求 id 去重。MainForm 通过委托连接双方，路由 assistant.*，单独发送 assistant 状态事件。

## 数据和配置

Data/assistant/history.json 保存当前聊天及最近一次可撤销清空的会话；settings.json 保存自动更新开关。两者通过 RegisterBackupFiles 登记进完整备份；恢复前取消当前回答，恢复后 Reload。

运行时和工作目录在 Data/assistant/runtime、work，不进入资料备份。初始内核位于应用 runtime/codex，由 bundled.json 指向版本 0.154.0 的 Windows 可执行文件。自动更新默认关闭；开启后由宿主定时触发、每日空闲时最多检查一次。

连接配置来自核心业务的 GetAssistantConnection，仅在 C# 内部传递，不出现在前端快照或聊天历史。CLI 忽略用户的全局配置及规则，禁用不必要工具，使用 Windows 无提权 workspace-write 沙箱分析临时资料，业务接口只读，不改写用户的 CODEX_HOME 或全局 Codex 配置。

归档中的“引用整个归档”只把引用加入聊天，等待用户输入问题，不会自动发送。后续提问保留所选引用，每次取当前归档内容。“全部提醒”“最近归档”和引用是关注重点；关闭重点开关不会撤销按需查询权限。

## 验证与来源

AssistantChecks 是离线解析/存储/更新边界检查；AssistantLiveCheck 是一次性隔离真实 CLI 联调入口。界面预览不会假造 CLI 成功回答。

接口依据：[Codex 非交互运行](https://developers.openai.com/codex/noninteractive/)、[CLI 参数参考](https://developers.openai.com/codex/cli/reference/)。内核来自官方 @openai/codex Windows 包，随发行包保留原包元数据。

## 提醒、任务与会话接口（v8）

构造器末尾接受可选的 `Func<JsonObject, JsonObject> reminderCreator`。调用参数为 `{requestId, reminders:[{scopeType, studentId?, projectId?, title, when, repeat, intervalDays?, monthDay?}],tasks:[{title,start,due,studentIds,projectId?}],expectedCurrentStudentId?}`；宿主返回 `{reminders:[{id,title,when,scopeType,studentId?,projectId?,scopeLabel,repeat,monthDay?}],tasks:[{id,title,start,due,studentIds,projectId,scopeLabel}],replayed}`。涉及当前成员时传入 `expectedCurrentStudentId`，宿主发现设置已改变则拒绝这次创建。对象身份、日期、未来时间、重复参数和整组原子保存由宿主再次校验。重复的持久化 requestId 只返回原回执；模块不直接写提醒或其他业务数据。原 reminders-only 模型信封和旧提醒回执仍可读取。

每个问题仍只调用一次 CLI。模型仅输出严格 JSON 信封 `{text, reminders, tasks}`；额外字段、重复 JSON 键、不含偏移量的闹钟时间、非 ISO 日期的任务、缺少对象 id 均拒绝。当前问题的显式创建意图和提示词共同限定动作；对已授权创建请求的立即下一条短时间/日期/周期或精确成员/项目名称补充，可继续完成创建。建议、引用、归档中的指令不会独立授权创建。“给出plan/仅计划/你会怎么做”不授权执行；提示词明确应用有创建能力，不能将只作计划解释为缺少权限。

`applicationContext.currentUser={id,name}` 是设置中的当前成员。“我/叫我”及未明确对象的请求默认此成员。AssistantTargets 在调用宿主前再次校验并强制绑定默认个人请求；未配置则提示选择当前成员，绝不回退全局。明确全局、其他成员或项目仍按要求处理。具体钟点的“下午2点叫我”是个人闹钟；只有日期的“明天提醒我去南湖”是 start=due 的个人任务；日期范围“10月22日-24日提醒我去CNCC”是相应 start/due 的个人任务，不额外添加默认时刻或闹钟。重复提醒缺少钟点仍询问。成功文字和结果卡片只来自宿主实际回执，不使用模型关于成功的自述。

- `assistant.send` 常规参数不变；重试只传 `{retryMessageId}`，复用最近一条尚未回答的问题及其原引用/上下文开关。可选的额外参数若与保存问题不符即拒绝。回复保存失败后仍沿用原 id，因此不会重复创建已经保存的提醒。
- `assistant.clear {}` 取消并等待当前请求，再把现有会话移入同一 history.json 的内部恢复字段。清空不调用模型、不删除业务提醒、归档或设置。
- `assistant.undo-clear {}` 在尚未发送新问题时恢复上一会话一次。`Snapshot().canUndoClear` 控制入口；内部恢复内容不发送到前端或模型上下文。
- 回复包含 `reminderResults` 和 `taskResults`，卡片字段来自实际回执；`ReplyToMessageId` 关联原问题。
- `AssistantActionChecks.Run(dataRoot)` 是隔离的离线动作检查，覆盖严格解析、显式授权门槛、清空取消竞态、撤销、保存失败保留和请求去重，不调用模型。

缺少创建字段时，模型可额外输出 `needsReminderDetails:true`（默认 false，不能与非空 reminders/tasks 同时出现）。仅当当前请求已经授权创建时，服务保存内部的 `AwaitingReminderDetails` 和原始 `ReminderRequestText`。紧接的短答通过完整日期/时间词检查或活动对象精确名称检查后，才可继承授权；其他问题、否定和清空都会切断此链。内部字段不进入界面快照或一般历史上下文，应用把已核准的原请求单独传给模型。仍然每条用户消息只调用一次模型。

## 原生归档分析

Responses 归档由 `AppService.Documents.cs` 准备隔离资料副本，`CodexRunner.RunArchiveAsync` 直接启动原生 Codex。`archive.json` 包含当前对象的完整会话与任务；附件保留原格式，并可附文字提取副本。Codex 自主选择原生读取、执行和看图能力；应用不规定分析步骤，也没有自定义文档 MCP 或模型结果缓存。

归档模式使用 Windows 无提权 `workspace-write` 沙箱，派生文件在临时目录生成；现有 GPT 聊天入口保持原动作协议。资料目录不包含连接配置或密钥。取消、删除、手工改进展及请求修订都会阻止旧分析落库。新摘要记录来源消息及生成的当前任务，支持删除和撤销时保留手工改动。


## 微信及原始资料引用（v11）

AssistantReference 增加 scopeType=wechat，scopeId为模块生成的稳定引用ID。宿主按来源拆分业务与微信引用；PrepareMaterialsAsync委托将原始消息/文件放入一次性资料目录。带显式资料引用的聊天使用原生Codex工具和完整chat-history.json，保留原有最终任务/提醒输出验证。只读资料准备不会触发创建；微信自动分析单独保留候选，点击创建才通过同一个宿主回执接口提交。


## 全库自主查询（v14）

普通聊天、归档摘要、微信提醒分析均可使用 `AssistantReadSession`。宿主通过 `ReadDataAsync` 委托提供只读查询，和现有 `reminderCreator` 写入委托分开；不把通用 UI RPC 暴露给模型。手机聊天共用此宿主能力。

Agent 工作目录提供 `app-read-api.json` 和 `app-read.ps1`，列出接口、参数和分页方式。Codex 使用原生 shell 调用，自己决定查询顺序、检索方式及是否阅读附件，没有新增 Agent 调度循环、MCP 或固定分析流程。文件请求/响应仅存在本次临时工作目录，结束或取消后停止服务并清理；不监听端口、不扩大网络权限。执行方式：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./app-read.ps1 help
powershell -NoProfile -ExecutionPolicy Bypass -File ./app-read.ps1 app.observe '{}'
# 复杂中文参数可先存为 UTF-8 JSON 文件；长结果直接保存完整 JSON。
powershell -NoProfile -ExecutionPolicy Bypass -File ./app-read.ps1 wechat.search -ArgumentsFile query.json -OutFile results.json
```

| 接口 | 内容 |
| --- | --- |
| `app.observe` | 当前身份、时间、全局记录数量和入口 |
| `app.members`、`app.projects` | 成员（含已归档）、项目、研究进展及关联 |
| `app.tasks`、`app.reminders` | 日期/状态筛选，重复提醒规则或指定日期范围的待提醒实例 |
| `app.archives`、`app.archive` | 归档目录、按对象完整分页原文和附件 ID |
| `app.search`、`app.file` | 跨业务文字/附件预览检索，按需取得原始文件及图像 |
| `wechat.observe`、`wechat.conversations` | 导入范围、同步和索引覆盖、好友/群列表及查找 |
| `wechat.search` | 复用 BGE-M3 和现有关键词/向量混合检索 |
| `wechat.messages`、`wechat.message` | 全库或单会话的原消息、日期和文字过滤、稳定游标分页、命中前后文 |
| `wechat.files`、`wechat.file` | 附件检索、文档原件和已解码图片的按需副本 |
| `assistant.history` | 当前未清空 GPT 会话的完整历史，可搜索分页 |

引用只是重点线索，不是权限边界。检索结果包含来源 ID、消息时间、分页游标或覆盖情况；语义命中是候选，Agent 需核对原消息和文件再下结论。未同步到 APP 的微信内容不能通过这些接口读取。附件接口只向临时目录复制所需原件，图片仍由原生 `view_image` 阅读；未成功解码的图片明确返回不可读状态。派生向量索引可以维护，不修改来源消息、成员、任务或提醒。

`AssistantReadChecks` 集中检查分页、原件、只读边界、中文 PowerShell 传输与故障收尾；`AssistantReadLiveCheck` 是显式启用的一次真实 Codex 联调，只使用合成资料且不带预选引用。
