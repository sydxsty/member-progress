package main

import (
	"google.golang.org/protobuf/encoding/protowire"
	"os"
	"path/filepath"
	"testing"
	"time"
)

func packedVoice(state uint64, text []byte) []byte {
	child := protowire.AppendTag(nil, 1, protowire.VarintType)
	child = protowire.AppendVarint(child, state)
	child = protowire.AppendTag(child, 2, protowire.BytesType)
	child = protowire.AppendBytes(child, text)
	outer := protowire.AppendTag(nil, 1, protowire.VarintType)
	outer = protowire.AppendVarint(outer, 106)
	outer = protowire.AppendTag(outer, 5, protowire.BytesType)
	return protowire.AppendBytes(outer, child)
}
func TestVoiceLocalTranscript(t *testing.T) {
	good := "明天下午讨论项目进展。"
	for _, tt := range []struct {
		name         string
		raw          []byte
		text, status string
	}{
		{"completed", packedVoice(2, []byte(good)), good, "completed"},
		{"empty_state", packedVoice(0, nil), "", "no_text"},
		{"unknown_state_has_text", packedVoice(3, []byte(good)), "", "unknown"},
		{"missing", nil, "", "no_text"},
		{"truncated", []byte{0x2a, 0xff}, "", "unknown"},
		{"invalid_utf8", packedVoice(2, []byte{0xff, 0xfe}), "", "unknown"},
		{"completed_short_text", packedVoice(2, []byte("语音转文字失败")), "语音转文字失败", "completed"},
		{"placeholder", packedVoice(2, []byte("[语音]")), "", "no_text"},
	} {
		t.Run(tt.name, func(t *testing.T) {
			text, status := readVoiceTranscript(tt.raw)
			if text != tt.text || status != tt.status {
				t.Fatalf("got status %q, text match %v", status, text == tt.text)
			}
		})
	}
}
func TestStableVoiceFile(t *testing.T) {
	path := filepath.Join(t.TempDir(), "test.silk")
	data := []byte("synthetic-silk-original")
	if err := writeStableVoice(path, data); err != nil {
		t.Fatal(err)
	}
	old := time.Date(2020, 1, 2, 3, 4, 5, 0, time.UTC)
	if err := os.Chtimes(path, old, old); err != nil {
		t.Fatal(err)
	}
	if err := writeStableVoice(path, data); err != nil {
		t.Fatal(err)
	}
	info, _ := os.Stat(path)
	if !info.ModTime().Equal(old) {
		t.Fatal("identical voice rewritten")
	}
	if err := writeStableVoice(path, []byte("changed-voice")); err != nil {
		t.Fatal(err)
	}
	value, _ := os.ReadFile(path)
	if string(value) != "changed-voice" {
		t.Fatal("updated voice not saved")
	}
}
