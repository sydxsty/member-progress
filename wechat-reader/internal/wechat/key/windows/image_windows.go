package windows

import (
	"context"
	"encoding/binary"
	"encoding/hex"
	"strings"
	"sync"
	"sync/atomic"
	"time"
	"unsafe"

	"github.com/sjzar/chatlog/internal/wechat/model"
	"github.com/sjzar/chatlog/pkg/util/dat2img"
	"golang.org/x/sys/windows"
)

// ExtractImage reads existing process memory only. It accepts a candidate only
// after decrypting a real local image header. It does not inject code, install a
// hook, request write access, or ask WeChat to download/open anything.
func ExtractImage(ctx context.Context, proc *model.Process, validator *dat2img.AesKeyValidator) ([]byte, error) {
	started := time.Now()
	LastImageScan = ImageScanReport{}
	imageBytes.Store(0)
	imageProcesses.Store(0)
	imageCandidates.Store(0)
	defer func() {
		LastImageScan.ElapsedSeconds = time.Since(started).Seconds()
		LastImageScan.ScannedBytes = imageBytes.Load()
		LastImageScan.ProcessPasses = imageProcesses.Load()
		LastImageScan.Candidates = imageCandidates.Load()
		LastImageScan.TimedOut = ctx.Err() != nil
	}()
	LastImageScan.Phase = "strings"
	if k, e := extractImage(ctx, proc, validator, true); e != nil || len(k) > 0 {
		return k, e
	}
	LastImageScan.Phase = "binary"
	k, e := extractImage(ctx, proc, validator, false)
	LastImageScan.Completed = e == nil
	return k, e
}
func extractImage(ctx context.Context, proc *model.Process, validator *dat2img.AesKeyValidator, fastOnly bool) ([]byte, error) {
	if validator == nil {
		return nil, nil
	}
	scanCtx, cancel := context.WithCancel(ctx)
	defer cancel()
	masks := loadInternalDBMasks(proc.ExePath, proc.FullVersion)
	chunks := make(chan v4MemoryChunk, 8)
	found := make(chan []byte, 1)
	handles := []windows.Handle{}
	defer func() {
		for _, h := range handles {
			windows.CloseHandle(h)
		}
	}()
	var producer sync.WaitGroup
	producer.Add(1)
	go func() {
		defer producer.Done()
		defer close(chunks)

		for _, pid := range imageProcessPIDs(proc.PID) {
			imageProcesses.Add(1)
			h, err := windows.OpenProcess(windows.PROCESS_VM_READ|windows.PROCESS_QUERY_INFORMATION, false, pid)
			if err != nil {
				continue
			}
			handles = append(handles, h)
			// Handles must stay alive until workers finish consuming their chunks.
			_ = findImageMemory(scanCtx, pid, h, chunks)
			// Handles are closed after workers finish.
			if scanCtx.Err() != nil {
				return
			}
		}
	}()
	var workers sync.WaitGroup
	for n := 0; n < 4; n++ {
		workers.Add(1)
		go func() {
			defer workers.Done()
			seen := map[[16]byte]bool{}
			validate := func(b []byte) bool {
				if len(b) < 16 {
					return false
				}
				var k [16]byte
				copy(k[:], b)
				if seen[k] {
					return false
				}
				if len(seen) > 65536 {
					clear(seen)
				}
				seen[k] = true
				imageCandidates.Add(1)
				if validator.Validate(k[:]) {
					select {
					case found <- append([]byte{}, k[:]...):
						cancel()
					default:
					}
					return true
				}
				return false
			}
			for c := range chunks {
				if scanCtx.Err() != nil {
					continue
				}
				b := c.data
				// Binary keys can be kept without a string owner. Check aligned
				// data in the already-read snapshot; no extra process reads.
				for i := 0; !fastOnly && i+16 <= len(b); i += 4 {
					if i&0xffff == 0 && scanCtx.Err() != nil {
						break
					}
					candidate := b[i : i+16]
					zeros := 0
					for _, v := range candidate {
						if v == 0 {
							zeros++
						}
					}
					if zeros > 2 {
						continue
					}
					imageCandidates.Add(1)
					if validator.Validate(candidate) {
						select {
						case found <- append([]byte{}, candidate...):
							cancel()
						default:
						}
						return
					}
				}
				// The image AES secret in current clients is commonly an inline ASCII
				// string. This scan deliberately does not reuse the database-key entropy
				// filter, which rejects that representation.
				for i := 0; i+16 <= len(b); i++ {
					if i+32 <= len(b) && b[i+1] == 0 && isImageKeyChar(b[i]) {
						var decoded [64]byte
						n := 0
						for n < 64 && i+2*n+1 < len(b) && b[i+2*n+1] == 0 && isImageKeyChar(b[i+2*n]) {
							decoded[n] = b[i+2*n]
							n++
						}
						if n >= 16 {
							if validate(decoded[:16]) {
								return
							}
							if n >= 32 {
								var raw [16]byte
								if _, err := hex.Decode(raw[:], decoded[:32]); err == nil && validate(raw[:]) {
									return
								}
							}
						}
					}
					if i&0xffff == 0 && scanCtx.Err() != nil {
						break
					}
					if (i == 0 || !isImageKeyChar(b[i-1])) && isImageKeyChar(b[i]) {
						end := i
						for end < len(b) && isImageKeyChar(b[end]) && end-i <= 128 {
							end++
						}
						length := end - i
						if length == 16 || length == 32 || length == 64 {
							for offset := i; offset+16 <= end; offset += 16 {
								if validate(b[offset : offset+16]) {
									return
								}
							}
							if length >= 32 {
								decoded := make([]byte, length/2)
								if _, err := hex.Decode(decoded, b[i:end]); err == nil && validate(decoded) {
									return
								}
							}
						}
						if end > i {
							i = end - 1
						}
					}
					// Inline std::string storage with a 16-byte length; the first 16 bytes
					// cover binary image keys too, and remain bounded to local copied memory.
					if i >= 16 && i+16 <= len(b) && (binary.LittleEndian.Uint64(b[i:i+8]) == 16 || binary.LittleEndian.Uint64(b[i:i+8]) == 32) {
						cap := binary.LittleEndian.Uint64(b[i+8 : i+16])
						if cap == 15 || cap == 16 || cap == 31 || cap == 32 || cap == 47 {
							if validate(b[i-16 : i]) {
								return
							}
							ptr := binary.LittleEndian.Uint64(b[i-16 : i-8])
							if ptr > 0x10000 && ptr < 0x7fffffffffff {
								raw := make([]byte, 32)
								if windows.ReadProcessMemory(c.handle, uintptr(ptr), &raw[0], uintptr(len(raw)), nil) == nil {
									if validate(raw) || validate(raw[16:]) {
										return
									}
									for _, mask := range masks {
										var transformed [32]byte
										for j := range transformed {
											transformed[j] = raw[j] ^ mask[j]
										}
										if validate(transformed[:]) || validate(transformed[16:]) {
											return
										}
									}
								}
							}
						}
					}
				}
			}
		}()
	}
	done := make(chan struct{})
	go func() { producer.Wait(); workers.Wait(); close(done) }()
	select {
	case k := <-found:
		cancel()
		<-done
		return k, nil
	case <-done:
		select {
		case k := <-found:
			return k, nil
		default:
			return nil, nil
		}
	case <-ctx.Done():
		cancel()
		<-done
		return nil, ctx.Err()
	}
}
func isImageKeyChar(c byte) bool {
	return c >= '0' && c <= '9' || c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z'
}

// Images may keep their decoder key in mapped or read-only memory, unlike the
// database-key layout. Read committed non-guard pages in bounded chunks.
func findImageMemory(ctx context.Context, pid uint32, h windows.Handle, chunks chan<- v4MemoryChunk) error {
	for address := uintptr(0x10000); address < 0x7fffffffffff; {
		var info windows.MemoryBasicInformation
		if windows.VirtualQueryEx(h, address, &info, unsafe.Sizeof(info)) != nil {
			break
		}
		next := info.BaseAddress + info.RegionSize
		if next <= address {
			break
		}
		if info.State == windows.MEM_COMMIT && info.Protect&(windows.PAGE_NOACCESS|windows.PAGE_GUARD) == 0 && info.Protect != 0 {
			for start := info.BaseAddress; start < next; {
				size := uintptr(v4MemoryChunkSize)
				if next-start < size {
					size = next - start
				}
				b := make([]byte, size)
				var read uintptr
				if windows.ReadProcessMemory(h, start, &b[0], size, &read) == nil && read > 0 {
					imageBytes.Add(uint64(read))
					select {
					case chunks <- v4MemoryChunk{handle: h, pid: pid, data: b[:read]}:
					case <-ctx.Done():
						return ctx.Err()
					}
				}
				advance := size
				if start+size < next && advance > 32 {
					advance -= 32
				}
				start += advance
				if ctx.Err() != nil {
					return ctx.Err()
				}
			}
		}
		address = next
	}
	return nil
}

func imageProcessPIDs(root uint32) []uint32 {
	result := relatedWeixinPIDs(root)
	selected := map[uint32]bool{}
	for _, pid := range result {
		selected[pid] = true
	}
	snapshot, err := windows.CreateToolhelp32Snapshot(windows.TH32CS_SNAPPROCESS, 0)
	if err != nil {
		return result
	}
	defer windows.CloseHandle(snapshot)
	type entry struct {
		pid, parent uint32
		name        string
	}
	entries := []entry{}
	p := windows.ProcessEntry32{Size: uint32(unsafe.Sizeof(windows.ProcessEntry32{}))}
	if windows.Process32First(snapshot, &p) == nil {
		for {
			entries = append(entries, entry{p.ProcessID, p.ParentProcessID, strings.ToLower(windows.UTF16ToString(p.ExeFile[:]))})
			if windows.Process32Next(snapshot, &p) != nil {
				break
			}
		}
	}
	for changed := true; changed; {
		changed = false
		for _, p := range entries {
			if selected[p.parent] && !selected[p.pid] && (p.name == "wechatappex.exe" || p.name == "weixinappex.exe" || p.name == "weixin.exe") {
				selected[p.pid] = true
				result = append(result, p.pid)
				changed = true
			}
		}
	}
	return result
}

type ImageScanReport struct {
	Phase          string  `json:"phase"`
	Completed      bool    `json:"completed"`
	TimedOut       bool    `json:"timedOut"`
	ScannedBytes   uint64  `json:"scannedBytes"`
	Candidates     uint64  `json:"candidates"`
	ProcessPasses  uint64  `json:"processPasses"`
	ElapsedSeconds float64 `json:"elapsedSeconds"`
}

var LastImageScan ImageScanReport
var imageBytes atomic.Uint64
var imageProcesses atomic.Uint64
var imageCandidates atomic.Uint64
