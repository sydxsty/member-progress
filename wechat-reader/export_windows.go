package main

import (
	"bufio"
	"context"
	"crypto/md5"
	"crypto/sha256"
	"database/sql"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	messageModel "github.com/sjzar/chatlog/internal/model"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"time"
)

type Contact struct {
	ID             string `json:"id"`
	ConversationID string `json:"conversationId"`
	AccountID      string `json:"accountId"`
	Name           string `json:"name"`
	Remark         string `json:"remark"`
	Nickname       string `json:"nickname"`
	Alias          string `json:"alias"`
	Type           string `json:"type"`
	IsFriend       bool   `json:"isFriend"`
}
type Attachment struct {
	ID     string `json:"id"`
	Name   string `json:"name"`
	Path   string `json:"path,omitempty"`
	Size   int64  `json:"size"`
	Type   string `json:"type"`
	Status string `json:"status"`
}
type Message struct {
	ID               string         `json:"id"`
	AccountID        string         `json:"accountId"`
	ConversationID   string         `json:"conversationId"`
	ConversationName string         `json:"conversationName"`
	SenderID         string         `json:"senderId"`
	SenderName       string         `json:"senderName"`
	IsSelf           bool           `json:"isSelf"`
	Time             string         `json:"time"`
	Timestamp        int64          `json:"timestamp"`
	Type             string         `json:"type"`
	Text             string         `json:"text"`
	RawContent       string         `json:"rawContent,omitempty"`
	Attachments      []Attachment   `json:"attachments"`
	Source           map[string]any `json:"source"`
}
type exportResult struct {
	OK               bool     `json:"ok"`
	AccountID        string   `json:"accountId"`
	Contacts         int      `json:"contacts"`
	Messages         int      `json:"messages"`
	Files            int      `json:"files"`
	ChangedDatabases int      `json:"changedDatabases"`
	Warnings         []string `json:"warnings"`
	Complete         bool     `json:"complete"`
	SnapshotAt       string   `json:"snapshotAt"`
}

func atomicJSON(path string, v any) error {
	b, e := json.Marshal(v)
	if e != nil {
		return e
	}
	if e = os.WriteFile(path+".tmp", b, 0600); e != nil {
		return e
	}
	_ = os.Remove(path)
	return os.Rename(path+".tmp", path)
}
func contactName(c Contact) string {
	for _, s := range []string{c.Remark, c.Nickname, c.Alias, c.ID} {
		if s != "" {
			return s
		}
	}
	return "未命名"
}

var messageFile = regexp.MustCompile(`^message_[0-9]+\.db$`)

func export(ctx context.Context, a *Account, out, cache string, key []byte, selected []string, contactsOnly, all bool) (exportResult, error) {
	r := exportResult{OK: true, AccountID: a.ID, Warnings: []string{}, Complete: true, SnapshotAt: time.Now().Format(time.RFC3339)}
	sources := []string{}
	voiceSources := []string{}
	voiceLoaded := false
	e := filepath.WalkDir(filepath.Join(a.DataPath, "db_storage"), func(path string, d os.DirEntry, e error) error {
		if e != nil {
			return e
		}
		if !contactsOnly && !d.IsDir() && regexp.MustCompile(`^media_[0-9]+\.db$`).MatchString(d.Name()) {
			voiceSources = append(voiceSources, path)
		}
		if !d.IsDir() && (d.Name() == "contact.db" || d.Name() == "session.db" || (!contactsOnly && (messageFile.MatchString(d.Name()) || d.Name() == "hardlink.db"))) {
			sources = append(sources, path)
		}
		return nil
	})
	if e != nil {
		return r, e
	}
	dbs := map[string]*sql.DB{}
	defer func() {
		for _, d := range dbs {
			d.Close()
		}
	}()
	contacts := map[string]Contact{}
	talkerHashes := map[string]string{}
	for _, src := range sources {
		p, changed, e := snapshot(ctx, src, cache, key)
		if e != nil {
			return r, e
		}
		if changed {
			r.ChangedDatabases++
		}
		d, e := openDB(p)
		if e != nil {
			return r, e
		}
		dbs[filepath.Base(src)] = d
	}
	if d := dbs["contact.db"]; d != nil {
		rows, e := d.Query("SELECT username, IFNULL(remark,''), IFNULL(nick_name,''), IFNULL(alias,''), IFNULL(local_type,0) FROM contact")
		if e != nil {
			return r, e
		}
		for rows.Next() {
			var c Contact
			var typ int
			if e = rows.Scan(&c.ID, &c.Remark, &c.Nickname, &c.Alias, &typ); e != nil {
				rows.Close()
				return r, e
			}
			c.Type = "friend"
			if strings.HasSuffix(c.ID, "@chatroom") {
				c.Type = "group"
			}
			c.IsFriend = typ != 3
			c.AccountID = a.ID
			c.ConversationID = c.ID
			c.Name = contactName(c)
			contacts[c.ID] = c
		}
		e = rows.Err()
		rows.Close()
		if e != nil {
			return r, e
		}
	} else {
		return r, errors.New("contact database missing")
	}
	if d := dbs["session.db"]; d != nil {
		rows, e := d.Query("SELECT username FROM SessionTable")
		if e != nil {
			rows, e = d.Query("SELECT username FROM session")
		}
		if e == nil {
			for rows.Next() {
				var id string
				if rows.Scan(&id) == nil && id != "" {
					if _, ok := contacts[id]; !ok {
						typ := "friend"
						if strings.HasSuffix(id, "@chatroom") {
							typ = "group"
						}
						contacts[id] = Contact{ID: id, ConversationID: id, AccountID: a.ID, Name: id, Type: typ, IsFriend: true}
					}
				}
			}
			rows.Close()
		}
	}
	for id := range contacts {
		h := md5.Sum([]byte(id))
		talkerHashes["Msg_"+hex.EncodeToString(h[:])] = id
	}
	list := make([]Contact, 0, len(contacts))
	for _, c := range contacts {
		if c.Type == "group" || c.IsFriend {
			list = append(list, c)
		}
	}
	sort.Slice(list, func(i, j int) bool { return list[i].Name < list[j].Name })
	r.Contacts = len(list)
	if e = atomicJSON(filepath.Join(out, "contacts.json"), list); e != nil {
		return r, e
	}
	temp := filepath.Join(out, "messages.jsonl.tmp")
	f, e := os.Create(temp)
	if e != nil {
		return r, e
	}
	buffer := bufio.NewWriterSize(f, 1024*1024)
	enc := json.NewEncoder(buffer)
	done := false
	defer func() {
		f.Close()
		if !done {
			_ = os.Remove(temp)
		}
	}()
	if !contactsOnly {
		wanted := map[string]bool{}
		for _, s := range selected {
			wanted[s] = true
			h := md5.Sum([]byte(s))
			talkerHashes["Msg_"+hex.EncodeToString(h[:])] = s
		}
		media := dbs["hardlink.db"]
		self := a.ID
		if pos := strings.LastIndex(self, "_"); pos > len("wxid_") {
			self = self[:pos]
		}
		for _, src := range sources {
			base := filepath.Base(src)
			if !messageFile.MatchString(base) {
				continue
			}
			d := dbs[base]
			nr, e := d.Query("SELECT user_name FROM Name2Id")
			if e == nil {
				for nr.Next() {
					var id string
					if nr.Scan(&id) == nil && id != "" {
						h := md5.Sum([]byte(id))
						talkerHashes["Msg_"+hex.EncodeToString(h[:])] = id
					}
				}
				nr.Close()
			}
			tables := []string{}
			rows, e := d.Query("SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'Msg_%'")
			if e != nil {
				return r, e
			}
			for rows.Next() {
				var t string
				if rows.Scan(&t) == nil {
					tables = append(tables, t)
				}
			}
			rows.Close()
			sort.Strings(tables)
			for _, table := range tables {
				talker := talkerHashes[table]
				if talker == "" {
					if all {
						r.Complete = false
						r.Warnings = append(r.Warnings, "有无法识别归属的历史会话表")
					}
					continue
				}
				if !all && !wanted[talker] {
					continue
				}
				q := fmt.Sprintf(`SELECT m.local_id, IFNULL(m.server_id,0), IFNULL(m.sort_seq,0), IFNULL(m.local_type,0), IFNULL(n.user_name,''), IFNULL(m.create_time,0), m.message_content, m.packed_info_data, IFNULL(m.status,0) FROM "%s" m LEFT JOIN Name2Id n ON m.real_sender_id=n.rowid ORDER BY m.local_id`, strings.ReplaceAll(table, `"`, `""`))
				mr, e := d.QueryContext(ctx, q)
				if e != nil {
					return r, e
				}
				for mr.Next() {
					var local int64
					var v messageModel.MessageV4
					if e = mr.Scan(&local, &v.ServerID, &v.SortSeq, &v.LocalType, &v.UserName, &v.CreateTime, &v.MessageContent, &v.PackedInfoData, &v.Status); e != nil {
						mr.Close()
						return r, e
					}
					m := v.Wrap(talker)
					m.IsSelf = m.Sender == self || m.Sender == a.ID
					sid := fmt.Sprintf("local:%s:%s:%d", base, table, local)
					cn := contacts[talker].Name
					if cn == "" {
						cn = talker
					}
					sn := contacts[m.Sender].Name
					if sn == "" {
						sn = m.Sender
					}
					attachments := attachmentFor(a, media, m, sid)
					if m.Type == 34 && len(attachments) == 1 {
						if !voiceLoaded {
							for _, vs := range voiceSources {
								vp, vc, ve := snapshot(ctx, vs, cache, key)
								if ve != nil {
									mr.Close()
									return r, ve
								}
								if vc {
									r.ChangedDatabases++
								}
								vd, ve := openDB(vp)
								if ve != nil {
									mr.Close()
									return r, ve
								}
								dbs[filepath.Base(vs)] = vd
							}
							voiceLoaded = true
						}
						for _, vs := range voiceSources {
							var data []byte
							if dbs[filepath.Base(vs)].QueryRow("SELECT voice_data FROM VoiceInfo WHERE svr_id=?", v.ServerID).Scan(&data) == nil && len(data) > 0 {
								dir := filepath.Join(cache, "media")
								if e = os.MkdirAll(dir, 0700); e != nil {
									mr.Close()
									return r, e
								}
								path := filepath.Join(dir, attachments[0].ID+".silk")
								if e = os.WriteFile(path, data, 0600); e != nil {
									mr.Close()
									return r, e
								}
								attachments[0].Path = path
								attachments[0].Name = "语音-" + strconv.FormatInt(v.ServerID, 10) + ".silk"
								attachments[0].Size = int64(len(data))
								attachments[0].Status = "stored"
								break
							}
						}
					}
					item := Message{sid, a.ID, talker, cn, m.Sender, sn, m.IsSelf, m.Time.Format(time.RFC3339), v.CreateTime, messageType(m), messageText(m), rawText(v.MessageContent), attachments, map[string]any{"database": base, "table": table, "localId": strconv.FormatInt(local, 10), "serverId": strconv.FormatInt(v.ServerID, 10), "sortSeq": strconv.FormatInt(v.SortSeq, 10)}}
					if e = enc.Encode(item); e != nil {
						mr.Close()
						return r, e
					}
					r.Messages++
					r.Files += len(attachments)
				}
				e = mr.Err()
				mr.Close()
				if e != nil {
					return r, e
				}
			}
		}
	}
	if e = buffer.Flush(); e != nil {
		return r, e
	}
	if e = f.Close(); e != nil {
		return r, e
	}
	_ = os.Remove(filepath.Join(out, "messages.jsonl"))
	if e = os.Rename(temp, filepath.Join(out, "messages.jsonl")); e != nil {
		return r, e
	}
	done = true
	return r, nil
}
func messageType(m *messageModel.Message) string {
	switch m.Type {
	case 1:
		return "text"
	case 3:
		return "image"
	case 34:
		return "voice"
	case 43:
		return "video"
	case 49:
		if m.SubType == 6 {
			return "file"
		}
		return "share"
	case 10000:
		return "system"
	default:
		return "other"
	}
}
func messageText(m *messageModel.Message) string {
	if m.Content != "" {
		return m.Content
	}
	parts := []string{}
	for _, key := range []string{"title", "desc", "url", "label", "cityname"} {
		if s, ok := m.Contents[key].(string); ok && s != "" {
			parts = append(parts, s)
		}
	}
	if len(parts) > 0 {
		return strings.Join(parts, "\n")
	}
	return "[" + map[string]string{"image": "图片", "voice": "语音", "video": "视频", "share": "分享", "file": "文件", "other": "消息", "system": "系统消息"}[messageType(m)] + "]"
}
func attachmentFor(a *Account, d *sql.DB, m *messageModel.Message, sid string) []Attachment {
	typ := messageType(m)
	if typ != "file" && typ != "image" && typ != "video" && typ != "voice" {
		return []Attachment{}
	}
	key, _ := m.Contents["md5"].(string)
	name, _ := m.Contents["title"].(string)
	if name == "" {
		name = messageText(m)
	}
	h := sha256.Sum256([]byte(sid + "\x00" + key))
	att := Attachment{ID: hex.EncodeToString(h[:12]), Name: name, Type: typ, Status: "missing"}
	if d != nil && key != "" && typ != "voice" {
		for _, version := range []string{"v4", "v3"} {
			table := typ + "_hardlink_info_" + version
			query := fmt.Sprintf(`SELECT f.file_name, IFNULL(f.file_size,0), IFNULL(d1.username,''), IFNULL(d2.username,'') FROM %s f LEFT JOIN dir2id d1 ON d1.rowid=f.dir1 LEFT JOIN dir2id d2 ON d2.rowid=f.dir2 WHERE f.md5=? OR f.file_name LIKE ? || '%%'`, table)
			rows, e := d.Query(query, key, key)
			if e != nil {
				continue
			}
			for rows.Next() {
				var filename, dir1, dir2 string
				var size int64
				if rows.Scan(&filename, &size, &dir1, &dir2) != nil {
					continue
				}
				rel := filepath.Join("msg", "file", dir1, filename)
				if typ == "image" {
					rel = filepath.Join("msg", "attach", dir1, dir2, "Img", filename)
				}
				if typ == "video" {
					rel = filepath.Join("msg", "video", dir1, filename)
				}
				path := filepath.Join(a.DataPath, rel)
				if !inside(a.DataPath, path) {
					continue
				}
				if st, e := os.Stat(path); e == nil && !st.IsDir() {
					att.Path = path
					att.Size = st.Size()
					att.Status = "stored"
					if name == messageText(m) {
						att.Name = filename
					}
					if typ == "image" && strings.EqualFold(filepath.Ext(path), ".dat") {
						att.Status = "encrypted"
					}
					break
				}
			}
			rows.Close()
			if att.Path != "" {
				break
			}
		}
	}
	if att.Path == "" {
		if p, ok := m.Contents["path"].(string); ok && p != "" {
			path := filepath.Join(a.DataPath, p)
			if inside(a.DataPath, path) {
				matches, _ := filepath.Glob(path + "*")
				for _, match := range matches {
					if st, e := os.Stat(match); e == nil && !st.IsDir() {
						att.Path = match
						att.Size = st.Size()
						att.Status = "stored"
						att.Name = filepath.Base(match)
						if strings.EqualFold(filepath.Ext(match), ".dat") {
							att.Status = "encrypted"
						}
						break
					}
				}
			}
		}
	}
	return []Attachment{att}
}
