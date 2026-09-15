package dat2img

import (
	"bytes"
	"crypto/aes"
	"encoding/binary"
	"os"
	"path/filepath"
	"strings"
)

type AesKeyValidator struct {
	Path          string
	EncryptedData []byte
	PaddingBlock  []byte
	Padding       byte
	Samples       []*AesKeyValidator
}

func NewImgKeyValidator(path string) *AesKeyValidator {
	validator := &AesKeyValidator{
		Path: path,
	}

	// Both full-size images and thumbnails can validate the image secret.
	filepath.Walk(path, func(filePath string, info os.FileInfo, err error) error {
		if err != nil {
			return err
		}

		// Skip directories
		if info.IsDir() {
			return nil
		}

		// Only process *.dat files.
		if !strings.HasSuffix(info.Name(), ".dat") {
			return nil
		}

		// Read file content
		data, err := os.ReadFile(filePath)
		if err != nil {
			return nil
		}

		// Check if header matches V4Format2.Header
		// Get aes.BlockSize (16) bytes starting from position 15
		if len(data) >= 31 && bytes.Equal(data[:4], V4Format2.Header) {
			plain := uint64(binary.LittleEndian.Uint32(data[6:10]))
			span := (plain/16 + 1) * 16
			if span+15 > uint64(len(data)) {
				return nil
			}
			validator.EncryptedData = append([]byte{}, data[15:31]...)
			validator.PaddingBlock = append([]byte{}, data[15+int(span)-16:15+int(span)]...)
			validator.Padding = byte(16 - plain%16)
			return filepath.SkipAll
		}

		return nil
	})

	// image data not found
	if len(validator.EncryptedData) == 0 {
		return nil
	}

	return validator
}

func (v *AesKeyValidator) Validate(key []byte) bool {
	for _, sample := range v.Samples {
		if sample.Validate(key) {
			return true
		}
	}
	if len(v.EncryptedData) == 0 {
		return false
	}
	if len(key) < 16 {
		return false
	}
	aesKey := key[:16]

	cipher, err := aes.NewCipher(aesKey)
	if err != nil {
		return false
	}

	decrypted := make([]byte, len(v.EncryptedData))
	cipher.Decrypt(decrypted, v.EncryptedData)

	valid := false
	for _, format := range Formats {
		if bytes.HasPrefix(decrypted, format.Header) {
			valid = true
			break
		}
	}
	if !valid {
		return false
	}
	// Validate the actual final AES padding block, too. A short format prefix
	// alone produces false positives when millions of memory candidates are read.
	if len(v.PaddingBlock) == 16 && v.Padding > 0 && v.Padding <= 16 {
		var tail [16]byte
		cipher.Decrypt(tail[:], v.PaddingBlock)
		for _, b := range tail[16-int(v.Padding):] {
			if b != v.Padding {
				return false
			}
		}
	}
	return true
}
