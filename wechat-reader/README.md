# 成员进度 · 微信只读读取组件

这是单独的 Windows x64 命令行组件，不启动服务器、不写微信目录、不发送消息，不需要 Python、数据库服务或额外安装程序。程序由固定版本公开源码构建，来源和本地改动见 UPSTREAM.md。

## 宿主契约

程序路径：`app/runtime/wechat-reader/member-wechat-reader.exe`。

- `discover` 输出 `{ok,accounts:[{id,name,dataPath,version}]}`，不会读取消息正文。只列出当前登录且能够找到数据目录的 Windows 微信 4.x 账号。
- `read --account <id或数据目录> --output <本次输出目录> --cache <持久缓存目录> --contacts-only` 只输出联系人/会话列表。首次连接默认使用此命令。
- `read --account ... --output ... --cache ... --conversation <会话微信ID>` 可重复指定 `--conversation`，输出所有选定会话在电脑上的记录。无会话选择时拒绝导出；只有显式 `--all` 才导出所有可识别会话。
- stdout 始终为一个 JSON 对象；成功返回 `ok,accountId,contacts,messages,files,changedDatabases,warnings,complete,snapshotAt`。失败为 `ok:false,code,message` 并以非零状态结束；不输出密钥、聊天正文或底层查询参数。

`contacts.json` 为数组：`{id,conversationId,accountId,name,remark,nickname,alias,type,isFriend}`。`type` 为 `friend` 或 `group`，ID 均为微信原始稳定标识。

`messages.jsonl` 每行为 `{id,accountId,conversationId,conversationName,senderId,senderName,isSelf,time,timestamp,type,text,rawContent,attachments,source}`。时间为 ISO8601 和 Unix 秒，`rawContent` 保存完整解压后的原文/XML供核对。`source` 提供 database、table、localId、serverId、sortSeq（数字标识保存为字符串，避免前端精度丢失）。`id` 使用 database/table/localId，发送中消息获得 serverId 后仍能更新原行；跨分片可按非零 serverId 再去重。

附件结构：`{id,name,path?,size,type,status}`，path 为宿主侧原文件位置，不可直接暴露给 WebView 或手机。状态为 stored / missing / encrypted。encrypted 表示已保留微信加密图片原件，需要解码才能预览；没有原文件的记录仍保留消息和附件信息，后续全量重扫选定会话时补齐。

## 一致性与增量

源数据库和 WAL 仅用只读文件句柄打开。复制完成后再次计算源文件内容哈希并核对前后文件状态；发生写入/检查点会重试，不发布不一致副本。副本通过 SQLite WAL 盐值和滚动校验，只回放最后已提交事务，再逐页验证 SQLCipher HMAC 并解密，最后执行 quick_check。

解密副本缓存在 APP 内，大小/修改时间未变时复用；30 分钟深刷新避免保留时间戳的历史恢复遗漏。每次导出重新扫描已选会话全部本地记录，因此历史回填和晚到附件可被发现。宿主按稳定 ID 增量合并；返回失败或 `complete:false` 不应标记同步完整，更不能删除此前保留的消息。缓存中的数据库副本含完整源分片（其中可能包含未选定会话），只能供读取器内部使用；不能进入安装包、微信导出、通用备份、GPT资料或网页投影。正式资料库只合并经过会话筛选的 messages.jsonl。

数据库访问密钥只保存在当前 Windows 用户 DPAPI 加密文件中，不放入程序参数、stdout、日志或安装包。源码调用只使用进程查询、ReadProcessMemory、VirtualQueryEx，不使用注入、钩子、写进程、暂停或终止微信。

## 构建与验证

使用 Go 1.24.7 或更高版本运行 build.ps1；构建机需要 Go，终端用户不需要。`CGO_ENABLED=0`，SQLite 随单个可执行文件编译。关键验证 `go test . -run 'TestCommittedWALSnapshot|TestWALTornTailRetainsCommit'` 使用隔离样例，不访问真实微信。
