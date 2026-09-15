package main

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"flag"
	"io"
	"os"
	"path/filepath"
	"strings"
	"time"
	"unsafe"

	"github.com/rs/zerolog"
	"github.com/rs/zerolog/log"
	"github.com/sjzar/chatlog/internal/wechat/decrypt"
	keywindows "github.com/sjzar/chatlog/internal/wechat/key/windows"
	"github.com/sjzar/chatlog/internal/wechat/model"
	processwindows "github.com/sjzar/chatlog/internal/wechat/process/windows"
	"golang.org/x/sys/windows"
)

type Account struct {
	ID       string `json:"id"`
	Name     string `json:"name"`
	DataPath string `json:"dataPath"`
	Version  string `json:"version"`
	proc     *model.Process
}
type repeated []string

func (s *repeated) String() string     { return strings.Join(*s, ",") }
func (s *repeated) Set(v string) error { *s = append(*s, v); return nil }
func result(v any)                     { _ = json.NewEncoder(os.Stdout).Encode(v) }
func fail(code, message string) {
	result(map[string]any{"ok": false, "code": code, "message": message})
	os.Exit(1)
}
func accounts() ([]Account, error) {
	processes, e := processwindows.NewDetector().FindProcesses()
	if e != nil {
		return nil, e
	}
	out := []Account{}
	seen := map[string]bool{}
	for _, p := range processes {
		if p.Version != 4 || p.DataDir == "" || p.Status != model.StatusOnline {
			continue
		}
		id := p.AccountName
		if seen[id] {
			continue
		}
		seen[id] = true
		out = append(out, Account{id, id, p.DataDir, p.FullVersion, p})
	}
	return out, nil
}
func main() {
	zerolog.SetGlobalLevel(zerolog.Disabled)
	log.Logger = zerolog.New(io.Discard)
	if len(os.Args) < 2 {
		fail("usage", "请指定 discover 或 read")
	}
	as, e := accounts()
	if e != nil {
		fail("discovery_failed", "暂时无法识别本机微信，请确认微信已登录")
	}
	if os.Args[1] == "discover" {
		result(map[string]any{"ok": true, "accounts": as})
		return
	}
	if os.Args[1] != "read" {
		fail("usage", "不支持此命令")
	}
	fs := flag.NewFlagSet("read", flag.ContinueOnError)
	fs.SetOutput(io.Discard)
	account := fs.String("account", "", "")
	output := fs.String("output", "", "")
	cache := fs.String("cache", "", "")
	contactsOnly := fs.Bool("contacts-only", false, "")
	all := fs.Bool("all", false, "")
	var conversations repeated
	fs.Var(&conversations, "conversation", "")
	if fs.Parse(os.Args[2:]) != nil {
		fail("usage", "读取参数有误")
	}
	if *account == "" || *output == "" {
		fail("usage", "请选择微信账号和输出目录")
	}
	if !*contactsOnly && !*all && len(conversations) == 0 {
		fail("selection_required", "请先选择需要同步的会话")
	}
	var selected *Account
	for i := range as {
		if strings.EqualFold(as[i].ID, *account) || strings.EqualFold(filepath.Clean(as[i].DataPath), filepath.Clean(*account)) {
			selected = &as[i]
			break
		}
	}
	if selected == nil {
		fail("account_offline", "所选微信账号未登录，已有资料可以继续查看")
	}
	abs, e := filepath.Abs(*output)
	if e != nil {
		fail("output_invalid", "输出目录不可用")
	}
	*output = abs
	src, _ := filepath.Abs(selected.DataPath)
	if inside(src, abs) {
		fail("output_invalid", "输出必须保存在 APP 目录，不能写入微信目录")
	}
	if *cache == "" {
		*cache = filepath.Join(abs, "reader-cache")
	}
	*cache, _ = filepath.Abs(*cache)
	if inside(src, *cache) {
		fail("output_invalid", "缓存不能写入微信目录")
	}
	if os.MkdirAll(*output, 0700) != nil || os.MkdirAll(*cache, 0700) != nil {
		fail("output_invalid", "无法创建微信资料缓存")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Minute)
	defer cancel()
	key, e := readKey(ctx, selected, *cache)
	if e != nil {
		if diag := os.Getenv("MEMBER_WECHAT_DIAGNOSTIC"); diag != "" {
			_ = os.WriteFile(diag, []byte(e.Error()), 0600)
		}
		fail("key_unavailable", "当前微信版本的只读连接暂未成功；请保持微信登录后重试")
	}
	defer clear(key)
	counts, e := export(ctx, selected, *output, *cache, key, conversations, *contactsOnly, *all)
	if e != nil {
		if diag := os.Getenv("MEMBER_WECHAT_DIAGNOSTIC"); diag != "" {
			_ = os.WriteFile(diag, []byte(e.Error()), 0600)
		}
		fail("snapshot_unavailable", "微信资料正在变化或读取格式暂不兼容；保留上次同步，稍后重试")
	}
	result(counts)
}
func inside(parent, path string) bool {
	r, e := filepath.Rel(parent, path)
	return e == nil && r != ".." && !strings.HasPrefix(r, ".."+string(filepath.Separator))
}
func readKey(ctx context.Context, a *Account, cache string) ([]byte, error) {
	validator, e := decrypt.NewValidator("windows", 4, a.DataPath)
	if e != nil {
		return nil, e
	}
	digest := sha256.Sum256([]byte(a.DataPath))
	path := filepath.Join(cache, hex.EncodeToString(digest[:12])+".dpapi")
	if protected, e := os.ReadFile(path); e == nil {
		if b, e := unprotect(protected); e == nil {
			if validator.Validate(b) {
				return b, nil
			}
			clear(b)
		}
	}
	extractor := keywindows.NewV4Extractor()
	extractor.SetValidate(validator)
	scan, stop := context.WithTimeout(ctx, 3*time.Minute)
	defer stop()
	raw, _, e := extractor.Extract(scan, a.proc)
	if e != nil {
		return nil, e
	}
	b, e := hex.DecodeString(raw)
	raw = ""
	if e != nil || !validator.Validate(b) {
		return nil, errors.New("key not validated")
	}
	p, e := protect(b)
	if e == nil {
		e = os.WriteFile(path, p, 0600)
	}
	if e != nil {
		clear(b)
		return nil, e
	}
	return b, nil
}
func protect(input []byte) ([]byte, error) {
	if len(input) == 0 {
		return nil, errors.New("empty")
	}
	in := windows.DataBlob{Size: uint32(len(input)), Data: &input[0]}
	var out windows.DataBlob
	if e := windows.CryptProtectData(&in, nil, nil, 0, nil, windows.CRYPTPROTECT_UI_FORBIDDEN, &out); e != nil {
		return nil, e
	}
	defer windows.LocalFree(windows.Handle(unsafe.Pointer(out.Data)))
	return append([]byte{}, unsafe.Slice(out.Data, out.Size)...), nil
}
func unprotect(input []byte) ([]byte, error) {
	if len(input) == 0 {
		return nil, errors.New("empty")
	}
	in := windows.DataBlob{Size: uint32(len(input)), Data: &input[0]}
	var out windows.DataBlob
	if e := windows.CryptUnprotectData(&in, nil, nil, 0, nil, windows.CRYPTPROTECT_UI_FORBIDDEN, &out); e != nil {
		return nil, e
	}
	defer windows.LocalFree(windows.Handle(unsafe.Pointer(out.Data)))
	return append([]byte{}, unsafe.Slice(out.Data, out.Size)...), nil
}
