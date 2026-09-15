# 验证记录

2026-09-15，Windows 4.1.12.26 实际本机只读连接通过。未改写微信目录，未发送/注入/终止微信，没有向控制台输出密钥或聊天正文。

- 精确版本发现与 contacts-only 通过；只读取会话列表。
- 仅使用文件传输助手验证实际消息表、发送人标识、原始正文与原文件定位；重复同步稳定且不重复，第二次没有重解密数据库。
- 源 WAL 包含 4 MiB 预分配文件，因此每次比较 WAL 内容哈希，不能仅看大小/mtime。
- 隔离 TestCommittedWALSnapshot 覆盖源 DB 检查点后仅 WAL 新增消息、缓存复用及继续新增消息。
- 隔离 TestWALTornTailRetainsCommit 覆盖不完整尾部不影响最后完整提交。
- 两项专项测试通过。实际好友/群没有为测试额外导出，也没有触发 GPT 调用。

限制：微信 4.x 加密图片原件可定位并保留，但本版本未承诺全部解码，输出 encrypted 状态；语音保留原始 SILK。本地已被清理或未下载的原件记录为 missing，之后同步时补齐。

私有 QA 位于工作区 03_过程缓存/tmp/member-progress-v11/private-reader，不属于源码或发布包。发布二进制校验见 release.json。

v12：contacts-only读取增加sourceLastMessageAt，使用SessionTable.last_timestamp而非置顶排序字段。实际869条联系人中340条有源消息时间，逐项与只读数据库核对一致，输出按最近时间倒序；未导出消息正文。
