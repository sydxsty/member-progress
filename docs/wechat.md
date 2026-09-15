# 微信只读模块

Windows 独立模块通过宿主委托读取业务日程、创建用户确认的任务/提醒及导入归档，不直接写核心 state.json。前端模块使用 wechat.* RPC 和独立状态推送；手机只兼容引用展示，不暴露微信资料库。

## 资料边界

Data/wechat/records.sqlite 保存选定会话、原始消息、附件元数据、引用、增量位置和检索文本。files 保存内容散列命名的原件。config.json（同步范围、成员/项目关联、全局归档起始日期、检查频率）及 suggestions.json（候选与忽略/创建回执）随通用备份。bulk 导出仅 records.sqlite、files 和导出清单；reader-cache、账号本机路径、密钥、模型与向量索引均排除。进入业务归档的文件有独立副本，删除归档后同步重放不复活该条归档。

## 只读来源

自带 member-wechat-reader.exe 被动读取当前用户的本机微信进程和数据。数据库/WAL 复制到模块缓存，校验两次源内容、合并已提交事务并验证数据库；不注入、不写进程、不强退微信。读取器输出稳定原始标识，服务以服务端标识/本地标识别名处理补齐和跨分片重复。周期重扫选定会话可接收历史回填与晚到文件。适配器来源、构建和真实兼容验证见 ../wechat-reader。

## 检索和 Codex

本地 BGE-M3 FP32（官方固定版本，1024维 CLS/L2）经 ONNX Runtime CPU运行，分词与官方XLM-R/SentencePiece编号核对。SQLite全文检索覆盖中文短词；USearch/HNSW FP32索引负责全库语义召回，过滤查询用过滤后的向量候选避免全库top-k过滤漏召回。索引可由原始数据重建，模型与分词文件由runtime/embedding/bundled.json固定SHA256。

微信引用会将完整原始JSONL和文件副本写入一次性资料目录，原生Codex自主读取、搜索和解析；没有固定分析流水线或模型预摘要。普通GPT带资料引用时复用这个原生模式，业务归档同样可读取原文件。自动建议按新增导入记录合并，首轮只自动检查最近7天；每条候选必须能关联原消息。模型只提出候选，点击创建通过已有幂等宿主接口提交，默认当前用户；没有日期或时刻时由用户补齐。

## 构建与检查

先按scripts/prepare-embedding.ps1准备固定模型，按wechat-reader/build.ps1构建读取器，再正常dotnet publish。便携版随附依赖，终端用户不需安装Go、Python、向量数据库或模型服务。

隔离检查入口：--wechat-test（可加--embedding运行一次真实本地检索）、--wechat-host-test。--wechat-agent-live-test需要显式--connection-from，使用合成资料且只允许一次真实Codex调用。所有检查都必须用新的空--data-dir，绝不能指向生产Data。

## 已知边界

电脑未保存的记录/文件无法凭空取得；微信加密图片的加密原件会保留并标注，当前不声称全部图像可解码。微信版本变化可能需要更新读取适配器，不影响已经导入的资料。后台索引未完成时继续提供关键词检索及完成状态。

## v12 设置与会话更新

config.archiveFrom 是统一的文件自动归档起始日期（yyyy-MM-dd 或 all），bindings 只保存成员/项目关联。升级旧配置取最早日期，任意旧配置为全部历史则沿用全部历史；原配置保留为 config.pre-global-date.json。零消息会话仅在聊天列表隐藏，同步选择仍保留。读取器从 SessionTable.last_timestamp 提供 sourceLastMessageAt，供尚未导入的会话排序。

修复检查入口 --wechat-revision-test 验证日期迁移、跨会话归档边界及源时间排序，不调用模型。已有v11完整模型的电脑可用 SkipBundledEmbedding=true 发布更新包，更新程序保留目标目录的既有模型；新安装仍使用默认完整发布。
