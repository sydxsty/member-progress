package main

import (
	"bytes"
	"context"
	"crypto/sha256"
	"crypto/sha512"
	"database/sql"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"time"

	"github.com/sjzar/chatlog/internal/wechat/decrypt/common"
	"golang.org/x/crypto/pbkdf2"
	_ "modernc.org/sqlite"
)

const pageSize = 4096

type stamp struct {
	Size     int64
	Modified int64
}
type dbStamp struct {
	DB      stamp
	WAL     stamp
	WALHash string
}
type snapshotReceipt struct {
	Source  dbStamp
	Hash    string
	Created int64
}

func stat(path string) stamp {
	f, e := os.Stat(path)
	if e != nil {
		return stamp{}
	}
	return stamp{f.Size(), f.ModTime().UnixNano()}
}
func stamps(path string) dbStamp {
	h, _ := hashFile(path + "-wal")
	return dbStamp{stat(path), stat(path + "-wal"), h}
}
func copyFile(src, dst string) (string, error) {
	in, e := os.Open(src)
	if e != nil {
		return "", e
	}
	defer in.Close()
	out, e := os.Create(dst)
	if e != nil {
		return "", e
	}
	h := sha256.New()
	_, e = io.Copy(io.MultiWriter(out, h), in)
	ce := out.Close()
	if e != nil {
		return "", e
	}
	if ce != nil {
		return "", ce
	}
	return hex.EncodeToString(h.Sum(nil)), nil
}
func hashFile(path string) (string, error) {
	f, e := os.Open(path)
	if e != nil {
		return "", e
	}
	defer f.Close()
	h := sha256.New()
	_, e = io.Copy(h, f)
	return hex.EncodeToString(h.Sum(nil)), e
}
func openDB(path string) (*sql.DB, error) {
	d, e := sql.Open("sqlite", "file:"+filepath.ToSlash(path)+"?mode=ro&immutable=1")
	if e != nil {
		return nil, e
	}
	d.SetMaxOpenConns(1)
	return d, nil
}

// Copy encrypted DB and WAL, then verify both byte-for-byte against the source.
// A live writer or checkpoint causes retry, never a falsely complete snapshot.
// Existing source files are opened with read access only. SQLite only sees APP copies.
func snapshot(ctx context.Context, src, cache string, key []byte) (string, bool, error) {
	tag := sha256.Sum256([]byte(src))
	base := filepath.Join(cache, hex.EncodeToString(tag[:12]))
	dst := base + ".sqlite"
	receiptPath := base + ".json"
	before := stamps(src)
	var prior snapshotReceipt
	if b, e := os.ReadFile(receiptPath); e == nil {
		_ = json.Unmarshal(b, &prior)
	}
	// Periodic deep refresh catches timestamp-preserving restoration/history backfill.
	if prior.Source == before && prior.Hash != "" && time.Now().Unix()-prior.Created < 1800 {
		if _, e := os.Stat(dst); e == nil {
			return dst, false, nil
		}
	}
	for attempt := 0; attempt < 4; attempt++ {
		select {
		case <-ctx.Done():
			return "", false, ctx.Err()
		default:
		}
		before = stamps(src)
		if before.DB.Size == 0 {
			return "", false, errors.New("source database unavailable")
		}
		raw := base + ".raw.tmp"
		wal := base + ".wal.tmp"
		tmp := base + ".plain.tmp"
		dbhash, e := copyFile(src, raw)
		if e != nil {
			return "", false, e
		}
		walhash := ""
		if before.WAL.Size > 0 {
			walhash, e = copyFile(src+"-wal", wal)
		} else {
			_ = os.Remove(wal)
		}
		if e != nil {
			continue
		}
		checkDB, e := hashFile(src)
		if e != nil {
			continue
		}
		checkWAL := ""
		if before.WAL.Size > 0 {
			checkWAL, e = hashFile(src + "-wal")
		}
		if e != nil || stamps(src) != before || dbhash != checkDB || walhash != checkWAL {
			continue
		}
		if prior.Hash == dbhash+walhash {
			if _, exists := os.Stat(dst); exists == nil {
				_ = os.Remove(raw)
				_ = os.Remove(wal)
				prior.Source = before
				prior.Created = time.Now().Unix()
				encoded, _ := json.Marshal(prior)
				if e = os.WriteFile(receiptPath, encoded, 0600); e != nil {
					return "", false, e
				}
				return dst, false, nil
			}
		}
		e = decryptSnapshot(ctx, raw, wal, tmp, key)
		_ = os.Remove(raw)
		_ = os.Remove(wal)
		if e != nil {
			_ = os.Remove(tmp)
			return "", false, e
		}
		d, e := openDB(tmp)
		if e != nil {
			return "", false, e
		}
		var check string
		e = d.QueryRow("PRAGMA quick_check").Scan(&check)
		d.Close()
		if e != nil || check != "ok" {
			_ = os.Remove(tmp)
			continue
		}
		_ = os.Remove(dst)
		if e = os.Rename(tmp, dst); e != nil {
			return "", false, e
		}
		encoded, _ := json.Marshal(snapshotReceipt{before, dbhash + walhash, time.Now().Unix()})
		if e = os.WriteFile(receiptPath, encoded, 0600); e != nil {
			return "", false, e
		}
		return dst, true, nil
	}
	return "", false, errors.New("database is changing; retry after current writes finish")
}

func decryptSnapshot(ctx context.Context, raw, wal, out string, key []byte) error {
	f, e := os.Open(raw)
	if e != nil {
		return e
	}
	defer f.Close()
	first := make([]byte, pageSize)
	if _, e = io.ReadFull(f, first); e != nil {
		return e
	}
	plain := bytes.HasPrefix(first, []byte(common.SQLiteHeader))
	salt := first[:16]
	enc := pbkdf2.Key(key, salt, 256000, 32, sha512.New)
	mac := pbkdf2.Key(enc, common.XorBytes(salt, 0x3a), 2, 32, sha512.New)
	output, e := os.Create(out)
	if e != nil {
		return e
	}
	defer output.Close()
	_, _ = f.Seek(0, 0)
	page := make([]byte, pageSize)
	decode := func(p []byte, n uint32) ([]byte, error) {
		if plain {
			return p, nil
		}
		if bytes.Equal(p, make([]byte, pageSize)) {
			return p, nil
		}
		b, e := common.DecryptPage(p, enc, mac, int64(n)-1, sha512.New, 64, 80, pageSize)
		if e != nil {
			return nil, e
		}
		if n == 1 {
			b = append([]byte(common.SQLiteHeader), b...)
		}
		return b, nil
	}
	for n := uint32(1); ; n++ {
		select {
		case <-ctx.Done():
			return ctx.Err()
		default:
		}
		_, e = io.ReadFull(f, page)
		if e == io.EOF {
			break
		}
		if e != nil {
			return e
		}
		b, e := decode(page, n)
		if e != nil {
			return e
		}
		if _, e = output.Write(b); e != nil {
			return e
		}
	}
	if _, e = os.Stat(wal); os.IsNotExist(e) {
		return nil
	}
	frames, dbPages, e := committedWAL(wal)
	if e != nil {
		return e
	}
	if len(frames) == 0 {
		return nil
	}
	wf, e := os.Open(wal)
	if e != nil {
		return e
	}
	defer wf.Close()
	for n, pos := range frames {
		if n > dbPages {
			continue
		}
		if _, e = wf.ReadAt(page, pos); e != nil {
			return e
		}
		b, e := decode(page, n)
		if e != nil {
			return e
		}
		if _, e = output.WriteAt(b, int64(n-1)*pageSize); e != nil {
			return e
		}
	}
	return output.Truncate(int64(dbPages) * pageSize)
}

// Validate native SQLite WAL rolling checksums/salts and replay only the last
// committed transaction. Uncommitted/torn tails never become exported messages.
func committedWAL(path string) (map[uint32]int64, uint32, error) {
	f, e := os.Open(path)
	if e != nil {
		return nil, 0, e
	}
	defer f.Close()
	h := make([]byte, 32)
	if _, e = io.ReadFull(f, h); e != nil {
		return nil, 0, errors.New("incomplete WAL header")
	}
	magic := binary.BigEndian.Uint32(h[:4])
	if magic != 0x377f0682 && magic != 0x377f0683 {
		return nil, 0, errors.New("unsupported WAL header")
	}
	if binary.BigEndian.Uint32(h[8:12]) != pageSize {
		return nil, 0, errors.New("unsupported WAL page size")
	}
	var order binary.ByteOrder = binary.LittleEndian
	if magic == 0x377f0683 {
		order = binary.BigEndian
	}
	checksum := func(data []byte, s0, s1 uint32) (uint32, uint32) {
		for i := 0; i < len(data); i += 8 {
			s0 += order.Uint32(data[i:i+4]) + s1
			s1 += order.Uint32(data[i+4:i+8]) + s0
		}
		return s0, s1
	}
	s0, s1 := checksum(h[:24], 0, 0)
	if s0 != binary.BigEndian.Uint32(h[24:28]) || s1 != binary.BigEndian.Uint32(h[28:]) {
		return nil, 0, errors.New("WAL header checksum mismatch")
	}
	pending := map[uint32]int64{}
	committed := map[uint32]int64{}
	dbPages := uint32(0)
	buf := make([]byte, 24+pageSize)
	for pos := int64(32); ; pos += int64(len(buf)) {
		_, e = io.ReadFull(f, buf)
		if e == io.EOF || e == io.ErrUnexpectedEOF {
			break
		}
		if e != nil {
			return nil, 0, e
		}
		if !bytes.Equal(buf[8:16], h[16:24]) {
			break
		}
		a, b := checksum(buf[:8], s0, s1)
		a, b = checksum(buf[24:], a, b)
		if a != binary.BigEndian.Uint32(buf[16:20]) || b != binary.BigEndian.Uint32(buf[20:24]) {
			if pos == 32 {
				return nil, 0, errors.New("WAL first frame checksum mismatch")
			}
			break
		}
		s0, s1 = a, b
		n := binary.BigEndian.Uint32(buf[:4])
		if n == 0 {
			return nil, 0, fmt.Errorf("invalid WAL page")
		}
		pending[n] = pos + 24
		if size := binary.BigEndian.Uint32(buf[4:8]); size != 0 {
			for k, v := range pending {
				committed[k] = v
			}
			clear(pending)
			dbPages = size
		}
	}
	return committed, dbPages, nil
}
