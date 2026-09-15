package main

import (
	"bytes"
	"github.com/sjzar/chatlog/pkg/util/zstd"
)

func rawText(b []byte) string {
	if bytes.HasPrefix(b, []byte{0x28, 0xb5, 0x2f, 0xfd}) {
		if raw, e := zstd.Decompress(b); e == nil {
			return string(raw)
		}
	}
	return string(b)
}
