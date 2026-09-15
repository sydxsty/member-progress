package main

import (
	"context"
	"database/sql"
	"os"
	"path/filepath"
	"testing"
)

func TestCommittedWALSnapshot(t *testing.T) {
	root := t.TempDir()
	source := filepath.Join(root, "source.db")
	db, e := sql.Open("sqlite", source)
	if e != nil {
		t.Fatal(e)
	}
	defer db.Close()
	db.SetMaxOpenConns(1)
	for _, q := range []string{"PRAGMA journal_mode=WAL", "PRAGMA wal_autocheckpoint=0", "CREATE TABLE messages(id INTEGER PRIMARY KEY,text TEXT)", "INSERT INTO messages VALUES(1,'before checkpoint')", "PRAGMA wal_checkpoint(TRUNCATE)", "INSERT INTO messages VALUES(2,'committed in WAL')"} {
		if _, e = db.Exec(q); e != nil {
			t.Fatal(e)
		}
	}
	cache := filepath.Join(root, "cache")
	_ = os.Mkdir(cache, 0700)
	copy, changed, e := snapshot(context.Background(), source, cache, nil)
	if e != nil {
		t.Fatal(e)
	}
	if !changed {
		t.Fatal("first snapshot must be new")
	}
	read, e := openDB(copy)
	if e != nil {
		t.Fatal(e)
	}
	var count int
	if e = read.QueryRow("SELECT count(*) FROM messages").Scan(&count); e != nil || count != 2 {
		t.Fatalf("WAL commit absent: count=%d error=%v", count, e)
	}
	read.Close()
	_, changed, e = snapshot(context.Background(), source, cache, nil)
	if e != nil || changed {
		t.Fatalf("unchanged source should reuse validated copy %v", e)
	}
	if _, e = db.Exec("INSERT INTO messages VALUES(3,'later message')"); e != nil {
		t.Fatal(e)
	}
	copy, changed, e = snapshot(context.Background(), source, cache, nil)
	if e != nil || !changed {
		t.Fatalf("WAL-only append not noticed: %v", e)
	}
	read, _ = openDB(copy)
	defer read.Close()
	_ = read.QueryRow("SELECT count(*) FROM messages").Scan(&count)
	if count != 3 {
		t.Fatalf("expected third message, got %d", count)
	}
}
func TestWALTornTailRetainsCommit(t *testing.T) {
	root := t.TempDir()
	source := filepath.Join(root, "source.db")
	db, e := sql.Open("sqlite", source)
	if e != nil {
		t.Fatal(e)
	}
	defer db.Close()
	_, _ = db.Exec("PRAGMA journal_mode=WAL")
	_, _ = db.Exec("CREATE TABLE t(id INTEGER)")
	_, _ = db.Exec("INSERT INTO t VALUES(1)")
	dst := filepath.Join(root, "wal-copy")
	_, e = copyFile(source+"-wal", dst)
	if e != nil {
		t.Fatal(e)
	}
	before, pages, e := committedWAL(dst)
	if e != nil || len(before) == 0 {
		t.Fatal("missing committed frames", e)
	}
	f, _ := os.OpenFile(dst, os.O_APPEND|os.O_WRONLY, 0600)
	_, _ = f.Write([]byte{1, 2, 3, 4, 5})
	f.Close()
	after, pages2, e := committedWAL(dst)
	if e != nil || pages != pages2 || len(after) != len(before) {
		t.Fatal("torn tail changed committed snapshot", e)
	}
}
