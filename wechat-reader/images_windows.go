package main

import (
	"bytes"
	"context"
	"crypto/md5"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"image"
	_ "image/gif"
	_ "image/jpeg"
	_ "image/png"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"time"

	keywindows "github.com/sjzar/chatlog/internal/wechat/key/windows"
	"github.com/sjzar/chatlog/pkg/util/dat2img"
)

type imageDecoder struct {
	ctx           context.Context
	account       *Account
	cache         string
	keys          []imageKeyRecord
	derived       []imageKeyRecord
	derivedLoaded bool
	loaded        bool
	attempted     bool
	xorAttempted  bool
	refresh       bool
}

func (d *imageDecoder) close() {
	for _, k := range d.keys {
		clear(k.Key)
	}
	for _, k := range d.derived {
		clear(k.Key)
	}
	clear(dat2img.V4Format2.AesKey)
}

type imageKeyRecord struct {
	Key []byte `json:"key"`
	Xor *byte  `json:"xor,omitempty"`
}

func (d *imageDecoder) useKey(record imageKeyRecord) []byte {
	if record.Xor != nil {
		dat2img.V4XorKey = *record.Xor
		d.xorAttempted = true
	}
	return record.Key
}
func (d *imageDecoder) imageKey(source string) []byte {
	validator := dat2img.NewImgKeyValidator(source)
	if validator == nil {
		return nil
	}
	digest := sha256.Sum256([]byte(d.account.DataPath))
	base := filepath.Join(d.cache, hex.EncodeToString(digest[:12]))
	keyPath := base + ".image-keys.dpapi"
	if !d.loaded {
		d.loaded = true
		if protected, e := os.ReadFile(keyPath); e == nil {
			if b, e := unprotect(protected); e == nil {
				if json.Unmarshal(b, &d.keys) != nil {
					var legacy [][]byte
					if json.Unmarshal(b, &legacy) == nil {
						for _, k := range legacy {
							d.keys = append(d.keys, imageKeyRecord{Key: k})
						}
					}
				}
				clear(b)
			}
		}
		if protected, e := os.ReadFile(base + ".image.dpapi"); e == nil {
			if b, e := unprotect(protected); e == nil {
				d.keys = append(d.keys, imageKeyRecord{Key: b})
			}
		}
	}
	persist := func(record imageKeyRecord) {
		exists := false
		for i, r := range d.keys {
			if bytes.Equal(r.Key, record.Key) {
				if (r.Xor == nil && record.Xor == nil) || (r.Xor != nil && record.Xor != nil && *r.Xor == *record.Xor) {
					return
				}
				d.keys[i] = record
				exists = true
				break
			}
		}
		if !exists {
			d.keys = append(d.keys, record)
		}
		if b, e := json.Marshal(d.keys); e == nil {
			if protected, e := protect(b); e == nil {
				_ = os.WriteFile(keyPath, protected, 0600)
			}
			clear(b)
		}
	}
	// Current Weixin stores the per-account image-code in kvcomm filenames.
	// Derivation reads filenames only, and accepts a result solely after testing
	// this account's real image. No key value or source filename is logged.
	if !d.derivedLoaded {
		d.derivedLoaded = true
		d.derived = deriveLocalImageKeys(d.account.ID)
	}
	for _, record := range d.derived {
		if validator.Validate(record.Key) {
			persist(record)
			return d.useKey(record)
		}
	}
	for _, record := range d.keys {
		if validator.Validate(record.Key) {
			return d.useKey(record)
		}
	}
	// Unavailable memory keys must not turn the one-minute background sync into
	// repeated memory scans. Only an explicit sync enables the passive fallback.
	if !d.refresh || d.attempted {
		return nil
	}
	d.attempted = true
	samples := []*dat2img.AesKeyValidator{validator}
	conversationDir := filepath.Dir(filepath.Dir(filepath.Dir(source)))
	if inside(filepath.Join(d.account.DataPath, "msg", "attach"), conversationDir) {
		paths, _ := filepath.Glob(filepath.Join(conversationDir, "*", "Img", "*_t.dat"))
		sort.Slice(paths, func(i, j int) bool {
			a, ea := os.Stat(paths[i])
			b, eb := os.Stat(paths[j])
			return ea == nil && (eb != nil || a.ModTime().After(b.ModTime()))
		})
		for _, path := range paths {
			if len(samples) >= 4 {
				break
			}
			v := dat2img.NewImgKeyValidator(path)
			if v == nil {
				continue
			}
			known := false
			for _, r := range d.keys {
				if v.Validate(r.Key) {
					known = true
					break
				}
			}
			if known {
				continue
			}
			duplicate := false
			for _, other := range samples {
				if bytes.Equal(v.EncryptedData, other.EncryptedData) {
					duplicate = true
					break
				}
			}
			if !duplicate {
				samples = append(samples, v)
			}
		}
	}
	scan, cancel := context.WithTimeout(d.ctx, 45*time.Second)
	defer cancel()
	k, e := keywindows.ExtractImage(scan, d.account.proc, &dat2img.AesKeyValidator{Samples: samples})
	if report, err := json.Marshal(keywindows.LastImageScan); err == nil {
		_ = os.WriteFile(base+".image-last-scan.json", report, 0600)
	}
	if e != nil || len(k) == 0 {
		return nil
	}
	record := imageKeyRecord{Key: k}
	persist(record)
	if validator.Validate(k) {
		return d.useKey(record)
	}
	return nil
}
func deriveLocalImageKeys(account string) []imageKeyRecord {
	root := filepath.Join(os.Getenv("APPDATA"), "Tencent")
	patterns := []string{filepath.Join(root, "xwechat", "net", "kvcomm", "key_*.statistic"), filepath.Join(root, "xwechat", "ilink", "kvcomm", "key_*.statistic"), filepath.Join(root, "WeChat", "*", "kvcomm", "key_*.statistic"), filepath.Join(root, "WeChat", "*", "*", "kvcomm", "key_*.statistic")}
	accounts := []string{account}
	if strings.HasPrefix(account, "wxid_") {
		if i := strings.LastIndex(account, "_"); i > 4 {
			accounts = append(accounts, account[:i])
		}
	}
	seen := map[string]bool{}
	var result []imageKeyRecord
	for _, pattern := range patterns {
		paths, _ := filepath.Glob(pattern)
		for _, path := range paths {
			parts := strings.Split(filepath.Base(path), "_")
			if len(parts) < 3 {
				continue
			}
			code, e := strconv.ParseUint(parts[1], 10, 32)
			if e != nil {
				continue
			}
			for _, id := range accounts {
				input := parts[1] + id
				if seen[input] {
					continue
				}
				seen[input] = true
				sum := md5.Sum([]byte(input))
				k := []byte(hex.EncodeToString(sum[:])[:16])
				xor := byte(code)
				result = append(result, imageKeyRecord{Key: k, Xor: &xor})
			}
		}
	}
	return result
}

func (d *imageDecoder) decode(att *Attachment) {
	if att.Type != "image" || att.Path == "" {
		return
	}
	paths := []string{att.Path}
	if strings.EqualFold(filepath.Ext(att.Path), ".dat") {
		stem := strings.TrimSuffix(att.Path, filepath.Ext(att.Path))
		stem = strings.TrimSuffix(strings.TrimSuffix(stem, "_t"), "_h")
		paths = []string{stem + "_h.dat", stem + ".dat", stem + "_t.dat", att.Path}
	}
	seen := map[string]bool{}
	var best *Attachment
	for _, path := range paths {
		if seen[path] {
			continue
		}
		seen[path] = true
		st, err := os.Stat(path)
		if err != nil || st.IsDir() {
			continue
		}
		candidate := *att
		candidate.Path = path
		candidate.Size = st.Size()
		d.decodeCandidate(&candidate)
		if best == nil {
			b := candidate
			best = &b
		}
		if candidate.Status == "ready" {
			candidate.OriginalPath = best.OriginalPath
			candidate.OriginalName = best.OriginalName
			candidate.OriginalSize = best.OriginalSize
			*att = candidate
			return
		}
	}
	if best != nil {
		*att = *best
	}
}
func (d *imageDecoder) decodeCandidate(att *Attachment) {
	if att.Type != "image" || att.Path == "" {
		return
	}
	source := att.Path
	data, e := os.ReadFile(source)
	if e != nil {
		return
	}
	att.OriginalPath = source
	att.OriginalName = filepath.Base(source)
	att.OriginalSize = int64(len(data))
	sourceHash := sha256.Sum256(data)
	cacheDir := filepath.Join(d.cache, "images")
	_ = os.MkdirAll(cacheDir, 0700)
	cacheBase := filepath.Join(cacheDir, hex.EncodeToString(sourceHash[:])+"-v13")
	for _, ext := range []string{"jpg", "png", "gif", "webp", "bmp", "tiff"} {
		path := cacheBase + "." + ext
		if st, e := os.Stat(path); e == nil && st.Size() > 0 {
			d.ready(att, path, ext, st.Size())
			return
		}
	}
	if bytes.HasPrefix(data, dat2img.V4Format2.Header) {
		k := d.imageKey(source)
		if len(k) == 0 {
			att.Status = "encrypted"
			att.DecodeStatus = "key_unavailable"
			return
		}
		dat2img.SetAesKey(hex.EncodeToString(k))
	}
	if !d.xorAttempted && (bytes.HasPrefix(data, dat2img.V4Format1.Header) || bytes.HasPrefix(data, dat2img.V4Format2.Header)) {
		d.xorAttempted = true
		_, _ = dat2img.ScanAndSetXorKey(filepath.Join(d.account.DataPath, "msg", "attach"))
	}
	decoded, ext, e := dat2img.Dat2Image(data)
	if e != nil {
		att.Status = "encrypted"
		att.DecodeStatus = "decode_failed"
		return
	}
	if ext == "tiff" || ext == "bmp" || ext == "webp" {
		decoded, e = dat2img.Convert2PNG(decoded)
		ext = "png"
		if e != nil {
			att.Status = "encrypted"
			att.DecodeStatus = "converter_unavailable"
			return
		}
	}
	if ext == "mp4" || ext == "wxgf" {
		att.Status = "encrypted"
		att.DecodeStatus = "converter_unavailable"
		return
	}
	if ext == "jpg" || ext == "png" || ext == "gif" {
		if _, _, e = image.Decode(bytes.NewReader(decoded)); e != nil {
			att.Status = "encrypted"
			att.DecodeStatus = "decode_failed"
			return
		}
	}
	path := cacheBase + "." + ext
	if e = os.WriteFile(path+".tmp", decoded, 0600); e != nil {
		return
	}
	if e = os.Rename(path+".tmp", path); e != nil {
		_ = os.Remove(path + ".tmp")
		return
	}
	d.ready(att, path, ext, int64(len(decoded)))
}
func (d *imageDecoder) ready(att *Attachment, path, ext string, size int64) {
	att.Path = path
	att.Size = size
	att.Status = "ready"
	att.DecodeStatus = "decoded"
	base := strings.TrimSuffix(filepath.Base(att.OriginalName), filepath.Ext(att.OriginalName))
	att.Name = base + "." + ext
	att.Mime = map[string]string{"jpg": "image/jpeg", "png": "image/png", "gif": "image/gif", "webp": "image/webp", "bmp": "image/bmp", "tiff": "image/tiff"}[ext]
	att.IsThumbnail = strings.HasSuffix(base, "_t")
	if att.Mime == "" {
		att.Mime = fmt.Sprintf("image/%s", ext)
	}
}
