# Pinned passive source

Chatlog: https://github.com/imldy/chatlog/tree/eb9572ee028cbaa72a50749b9dfe6630b317228f (Apache-2.0).
Masked-key patch: https://github.com/Sherlock488/wechat-chatlog-plus/tree/398f128b7f14365b146a312127d3fbce742d9e20 (MIT).
Only process discovery, read-only key extraction, SQLCipher decryption and message parsers are retained. No upstream HTTP server, commands, sending, hook, injection or config code is linked.
Local adaptations remove unused Gin error handler and V3 extractor, normalize device paths, and add stable snapshot + committed WAL replay. Extracted keys are never stdout, command arguments or plaintext files.
