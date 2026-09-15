package windows

import (
	"bytes"
	"context"
	"encoding/binary"
	"encoding/hex"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"unsafe"

	"github.com/rs/zerolog/log"
	"golang.org/x/sys/windows"

	"github.com/sjzar/chatlog/internal/errors"
	"github.com/sjzar/chatlog/internal/wechat/model"
)

const (
	MEM_PRIVATE       = 0x20000
	MaxWorkers        = 16
	v4MemoryChunkSize = 4 * 1024 * 1024
)

var v4KeyPattern = []byte{
	0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
	0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
	0x2F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
}

type v4MemoryChunk struct {
	handle windows.Handle
	pid    uint32
	data   []byte
}

type v4CandidateID struct {
	pid  uint32
	addr uint64
}

func (e *V4Extractor) Extract(ctx context.Context, proc *model.Process) (string, string, error) {
	if proc.Status == model.StatusOffline {
		return "", "", errors.ErrWeChatOffline
	}

	pids := relatedWeixinPIDs(proc.PID)
	internalMasks := loadInternalDBMasks(proc.ExePath, proc.FullVersion)
	handles := make(map[uint32]windows.Handle, len(pids))
	for _, pid := range pids {
		handle, err := windows.OpenProcess(windows.PROCESS_VM_READ|windows.PROCESS_QUERY_INFORMATION, false, pid)
		if err != nil {
			log.Debug().Err(err).Uint32("pid", pid).Msg("Unable to open related Weixin process")
			continue
		}
		handles[pid] = handle
	}
	defer func() {
		for _, handle := range handles {
			windows.CloseHandle(handle)
		}
	}()
	if len(handles) == 0 {
		return "", "", errors.ErrNoValidKey
	}

	searchCtx, cancel := context.WithCancel(ctx)
	defer cancel()
	memoryChannel := make(chan v4MemoryChunk, 32)
	resultChannel := make(chan [2]string, 4)
	workerCount := runtime.NumCPU()
	if workerCount < 2 {
		workerCount = 2
	}
	if workerCount > MaxWorkers {
		workerCount = MaxWorkers
	}

	var seen sync.Map
	var producerWaitGroup sync.WaitGroup
	producerWaitGroup.Add(1)
	var workerWaitGroup sync.WaitGroup
	workerWaitGroup.Add(workerCount)
	for index := 0; index < workerCount; index++ {
		go func() {
			defer workerWaitGroup.Done()
			e.worker(searchCtx, memoryChannel, resultChannel, &seen, internalMasks)
		}()
	}

	defer func() { cancel(); producerWaitGroup.Wait(); workerWaitGroup.Wait() }()
	go func() {
		defer producerWaitGroup.Done()
		defer close(memoryChannel)
		for _, pid := range pids {
			handle, ok := handles[pid]
			if !ok {
				continue
			}
			if err := e.findMemory(searchCtx, pid, handle, memoryChannel); err != nil {
				log.Debug().Err(err).Uint32("pid", pid).Msg("Failed to scan related Weixin process")
			}
		}
	}()
	go func() {
		workerWaitGroup.Wait()
		close(resultChannel)
	}()

	var dataKey, imgKey string
	for {
		select {
		case <-ctx.Done():
			return "", "", ctx.Err()
		case result, ok := <-resultChannel:
			if !ok {
				if dataKey != "" || imgKey != "" {
					return dataKey, imgKey, nil
				}
				return "", "", errors.ErrNoValidKey
			}
			if result[0] != "" {
				dataKey = result[0]
			}
			if result[1] != "" {
				imgKey = result[1]
			}
			// Current Weixin 4.x keeps the database key behind an internal mask, while
			// the image key is not consistently resident in the same process. Return
			// as soon as the database key validates; callers can keep or supply an
			// independently discovered image key.
			if dataKey != "" {
				cancel()
				return dataKey, imgKey, nil
			}
		}
	}
}

func relatedWeixinPIDs(rootPID uint32) []uint32 {
	type processInfo struct {
		pid    uint32
		parent uint32
		name   string
	}

	snapshot, err := windows.CreateToolhelp32Snapshot(windows.TH32CS_SNAPPROCESS, 0)
	if err != nil {
		return []uint32{rootPID}
	}
	defer windows.CloseHandle(snapshot)

	var entries []processInfo
	entry := windows.ProcessEntry32{Size: uint32(unsafe.Sizeof(windows.ProcessEntry32{}))}
	if err = windows.Process32First(snapshot, &entry); err == nil {
		for {
			entries = append(entries, processInfo{
				pid: entry.ProcessID, parent: entry.ParentProcessID,
				name: strings.TrimSuffix(strings.ToLower(windows.UTF16ToString(entry.ExeFile[:])), ".exe"),
			})
			if err = windows.Process32Next(snapshot, &entry); err != nil {
				break
			}
		}
	}

	// Account discovery may select a database worker. Start at the owning
	// Weixin ancestor so image keys in its siblings remain visible.
	selected := map[uint32]bool{rootPID: true}
	for current := rootPID; ; {
		parent := uint32(0)
		for _, item := range entries {
			if item.pid == current {
				parent = item.parent
				break
			}
		}
		own := false
		for _, item := range entries {
			if item.pid == parent && item.name == "weixin" {
				own = true
				break
			}
		}
		if !own || selected[parent] {
			break
		}
		selected[parent] = true
		current = parent
	}
	for changed := true; changed; {
		changed = false
		for _, item := range entries {
			if item.name == "weixin" && selected[item.parent] && !selected[item.pid] {
				selected[item.pid] = true
				changed = true
			}
		}
	}

	result := []uint32{rootPID}
	for _, item := range entries {
		if item.pid != rootPID && selected[item.pid] {
			result = append(result, item.pid)
		}
	}
	log.Debug().Msgf("Scanning related Weixin processes: %v", result)
	return result
}

func (e *V4Extractor) findMemory(ctx context.Context, pid uint32, handle windows.Handle, memoryChannel chan<- v4MemoryChunk) error {
	minAddr := uintptr(0x10000)
	maxAddr := uintptr(0x7FFFFFFFFFFF)
	currentAddr := minAddr

	for currentAddr < maxAddr {
		var memInfo windows.MemoryBasicInformation
		if err := windows.VirtualQueryEx(handle, currentAddr, &memInfo, unsafe.Sizeof(memInfo)); err != nil {
			break
		}
		nextAddr := uintptr(memInfo.BaseAddress) + uintptr(memInfo.RegionSize)
		if nextAddr <= currentAddr {
			break
		}
		if memInfo.State == windows.MEM_COMMIT && memInfo.Type == MEM_PRIVATE && (memInfo.Protect&windows.PAGE_READWRITE) != 0 {
			regionStart := uintptr(memInfo.BaseAddress)
			regionEnd := nextAddr
			for chunkStart := regionStart; chunkStart < regionEnd; {
				chunkSize := uintptr(v4MemoryChunkSize)
				if remaining := regionEnd - chunkStart; remaining < chunkSize {
					chunkSize = remaining
				}
				memory := make([]byte, chunkSize)
				if err := windows.ReadProcessMemory(handle, chunkStart, &memory[0], chunkSize, nil); err == nil {
					select {
					case memoryChannel <- v4MemoryChunk{handle: handle, pid: pid, data: memory}:
					case <-ctx.Done():
						return nil
					}
				}
				advance := chunkSize
				if chunkStart+chunkSize < regionEnd && advance > uintptr(len(v4KeyPattern)+8) {
					advance -= uintptr(len(v4KeyPattern) + 8)
				}
				chunkStart += advance
			}
		}
		currentAddr = nextAddr
	}
	return nil
}

func (e *V4Extractor) worker(ctx context.Context, memoryChannel <-chan v4MemoryChunk, resultChannel chan<- [2]string, seen *sync.Map, internalMasks [][32]byte) {
	for {
		select {
		case <-ctx.Done():
			return
		case chunk, ok := <-memoryChannel:
			if !ok {
				return
			}
			index := len(chunk.data)
			for {
				index = bytes.LastIndex(chunk.data[:index], v4KeyPattern)
				if index < 8 {
					break
				}
				ptrValue := binary.LittleEndian.Uint64(chunk.data[index-8 : index])
				if ptrValue > 0x10000 && ptrValue < 0x7FFFFFFFFFFF {
					candidateID := v4CandidateID{pid: chunk.pid, addr: ptrValue}
					if _, loaded := seen.LoadOrStore(candidateID, struct{}{}); !loaded {
						if keyData := readV4Candidate(chunk.handle, ptrValue); plausibleV4Candidate(keyData) {
							key, isImgKey := e.validateKeyData(keyData, internalMasks)
							if key == "" {
								index--
								continue
							}
							result := [2]string{}
							if isImgKey {
								result[1] = key
								log.Debug().Msg("Image key found (redacted)")
							} else {
								result[0] = key
								log.Debug().Msg("Data key found (redacted)")
							}
							select {
							case resultChannel <- result:
							case <-ctx.Done():
								return
							}
						}
					}
				}
				index--
			}
		}
	}
}

func readV4Candidate(handle windows.Handle, addr uint64) []byte {
	keyData := make([]byte, 0x20)
	if err := windows.ReadProcessMemory(handle, uintptr(addr), &keyData[0], uintptr(len(keyData)), nil); err != nil {
		return nil
	}
	return keyData
}

func plausibleV4Candidate(keyData []byte) bool {
	if len(keyData) != 32 {
		return false
	}
	unique := make(map[byte]struct{}, 32)
	printable := 0
	for _, value := range keyData {
		unique[value] = struct{}{}
		if value >= 0x20 && value <= 0x7e {
			printable++
		}
	}
	return len(unique) >= 15 && printable <= 24
}

func (e *V4Extractor) validateKeyData(keyData []byte, internalMasks [][32]byte) (string, bool) {
	if e.validator.Validate(keyData) {
		return hex.EncodeToString(keyData), false
	}
	if e.validator.ValidateImgKey(keyData) {
		return hex.EncodeToString(keyData[:16]), true
	}
	for _, mask := range internalMasks {
		transformed := make([]byte, 32)
		for index := range transformed {
			transformed[index] = keyData[index] ^ mask[index]
		}
		if e.validator.Validate(transformed) {
			return hex.EncodeToString(transformed), false
		}
		if e.validator.ValidateImgKey(transformed) {
			return hex.EncodeToString(transformed[:16]), true
		}
	}
	return "", false
}

func loadInternalDBMasks(exePath, fullVersion string) [][32]byte {
	baseDir := filepath.Dir(exePath)
	paths := []string{
		filepath.Join(baseDir, fullVersion, "Weixin.dll"),
		filepath.Join(baseDir, "Weixin.dll"),
		exePath,
	}
	seen := make(map[[32]byte]struct{})
	var result [][32]byte
	for _, path := range paths {
		data, err := os.ReadFile(path)
		if err != nil {
			continue
		}
		for _, mask := range scanInternalDBMasks(data) {
			if _, exists := seen[mask]; exists {
				continue
			}
			seen[mask] = struct{}{}
			result = append(result, mask)
		}
	}
	log.Debug().Msgf("Loaded %d internal database masks", len(result))
	return result
}

func scanInternalDBMasks(data []byte) [][32]byte {
	movMask := []byte{0x48, 0xBA}
	testRAX := []byte{0x48, 0x85, 0xC0}
	var result [][32]byte
	for searchStart := 0; searchStart+10 <= len(data); {
		relative := bytes.Index(data[searchStart:], movMask)
		if relative < 0 {
			break
		}
		instruction := searchStart + relative
		var mask [32]byte
		valid := true
		for part := 0; part < 4; part++ {
			if instruction+10 > len(data) || !bytes.Equal(data[instruction:instruction+2], movMask) {
				valid = false
				break
			}
			copy(mask[part*8:(part+1)*8], data[instruction+2:instruction+10])
			nextPattern := movMask
			if part == 3 {
				nextPattern = testRAX
			}
			next := -1
			for gap := 3; gap <= 8; gap++ {
				candidate := instruction + 10 + gap
				if candidate+len(nextPattern) <= len(data) && bytes.Equal(data[candidate:candidate+len(nextPattern)], nextPattern) {
					next = candidate
					break
				}
			}
			if next < 0 {
				valid = false
				break
			}
			instruction = next
		}
		if valid {
			result = append(result, mask)
		}
		searchStart += relative + 1
	}
	return result
}
