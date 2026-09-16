package main

import (
	"bytes"
	"os"
	"strings"
	"unicode"
	"unicode/utf8"

	"google.golang.org/protobuf/encoding/protowire"
)

// WeChat 4.x stores a completed local voice transcription in packed_info_data:
// field 5 (message), child 1 == 2 (completed), child 2 (UTF-8 text).
// Unknown states/formats are deliberately left unindexed; this never initiates ASR.
func readVoiceTranscript(packed []byte) (string, string) {
	if len(packed) == 0 {
		return "", "no_text"
	}
	if len(packed) > 65536 {
		return "", "unknown"
	}
	var voice []byte
	for len(packed) > 0 {
		number, kind, used := protowire.ConsumeTag(packed)
		if used < 0 {
			return "", "unknown"
		}
		packed = packed[used:]
		if number == 5 {
			if kind != protowire.BytesType || voice != nil {
				return "", "unknown"
			}
			value, n := protowire.ConsumeBytes(packed)
			if n < 0 {
				return "", "unknown"
			}
			voice = value
			packed = packed[n:]
		} else {
			n := protowire.ConsumeFieldValue(number, kind, packed)
			if n < 0 {
				return "", "unknown"
			}
			packed = packed[n:]
		}
	}
	if voice == nil {
		return "", "no_text"
	}
	var state uint64
	var content []byte
	seenState, seenText := false, false
	for len(voice) > 0 {
		number, kind, n := protowire.ConsumeTag(voice)
		if n < 0 {
			return "", "unknown"
		}
		voice = voice[n:]
		switch number {
		case 1:
			if kind != protowire.VarintType || seenState {
				return "", "unknown"
			}
			value, used := protowire.ConsumeVarint(voice)
			if used < 0 {
				return "", "unknown"
			}
			state = value
			seenState = true
			voice = voice[used:]
		case 2:
			if kind != protowire.BytesType || seenText {
				return "", "unknown"
			}
			value, used := protowire.ConsumeBytes(voice)
			if used < 0 {
				return "", "unknown"
			}
			content = value
			seenText = true
			voice = voice[used:]
		default:
			used := protowire.ConsumeFieldValue(number, kind, voice)
			if used < 0 {
				return "", "unknown"
			}
			voice = voice[used:]
		}
	}
	if !seenState || state != 2 {
		if state == 0 && len(content) == 0 {
			return "", "no_text"
		}
		return "", "unknown"
	}
	if !utf8.Valid(content) {
		return "", "unknown"
	}
	text := strings.TrimSpace(string(content))
	if text == "" {
		return "", "no_text"
	}
	if len(text) > 32768 {
		return "", "unknown"
	}
	for _, r := range text {
		if unicode.IsControl(r) && r != '\r' && r != '\n' && r != '\t' {
			return "", "unknown"
		}
	}
	// Only the explicit message placeholder is excluded. Completed speech may itself
	// say "cannot recognize"; do not infer state from the user's words.
	if text == "[语音]" {
		return "", "no_text"
	}
	return text, "completed"
}

// Re-exporting the same SILK bytes must not change mtime and invalidate the APP's cache.
func writeStableVoice(path string, content []byte) error {
	if info, err := os.Stat(path); err == nil && info.Size() == int64(len(content)) {
		if old, err := os.ReadFile(path); err == nil && bytes.Equal(old, content) {
			return nil
		}
	}
	return os.WriteFile(path, content, 0600)
}
