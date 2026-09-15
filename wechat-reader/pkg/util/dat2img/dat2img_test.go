package dat2img

import (
	"bytes"
	"crypto/aes"
	"encoding/binary"
	"encoding/hex"
	"image"
	"image/color"
	"image/jpeg"
	"image/png"
	"os"
	"path/filepath"
	"testing"
)

func testPicture(t *testing.T, ext string) []byte {
	t.Helper()
	im := image.NewRGBA(image.Rect(0, 0, 31, 23))
	for y := 0; y < 23; y++ {
		for x := 0; x < 31; x++ {
			im.Set(x, y, color.RGBA{byte(x * 7), byte(y * 9), 120, 255})
		}
	}
	var b bytes.Buffer
	var e error
	if ext == "png" {
		e = png.Encode(&b, im)
	} else {
		e = jpeg.Encode(&b, im, &jpeg.Options{Quality: 85})
	}
	if e != nil {
		t.Fatal(e)
	}
	return b.Bytes()
}
func testV4(t *testing.T, plain, key []byte, v byte, xor byte) []byte {
	t.Helper()
	n := 32
	span := (n/16 + 1) * 16
	tail := 16
	b := make([]byte, 15+span+len(plain)-n)
	copy(b, []byte{7, 8, 'V', v, 8, 7})
	binary.LittleEndian.PutUint32(b[6:], uint32(n))
	binary.LittleEndian.PutUint32(b[10:], uint32(tail))
	b[14] = 1
	padded := append(append([]byte{}, plain[:n]...), bytes.Repeat([]byte{16}, 16)...)
	c, e := aes.NewCipher(key)
	if e != nil {
		t.Fatal(e)
	}
	for i := 0; i < span; i += 16 {
		c.Encrypt(b[15+i:15+i+16], padded[i:i+16])
	}
	copy(b[15+span:], plain[n:])
	for i := len(b) - tail; i < len(b); i++ {
		b[i] ^= xor
	}
	return b
}
func TestPlainAndLegacyXor(t *testing.T) {
	for _, ext := range []string{"jpg", "png"} {
		for _, key := range []byte{0, 0x73} {
			plain := testPicture(t, ext)
			data := append([]byte{}, plain...)
			for i := range data {
				data[i] ^= key
			}
			got, format, e := Dat2Image(data)
			if e != nil || format != ext || !bytes.Equal(got, plain) {
				t.Fatalf("%s xor=%d decode mismatch: %v", ext, key, e)
			}
		}
	}
}
func TestV1AndV2DecodeAndValidation(t *testing.T) {
	old := V4XorKey
	defer func() { V4XorKey = old }()
	V4XorKey = 0x37
	for _, ext := range []string{"jpg", "png"} {
		for _, v := range []byte{'1', '2'} {
			key := []byte("local-fixture-16")
			if v == '1' {
				key = V4Format1.AesKey
			}
			plain := testPicture(t, ext)
			data := testV4(t, plain, key, v, 0x82)
			got, format, e := Dat2ImageV4(data, key)
			if e != nil || format != ext || !bytes.Equal(got, plain) {
				t.Fatalf("v%c %s: %v", v, ext, e)
			}
			if v == '2' {
				path := filepath.Join(t.TempDir(), "sample_t.dat")
				if e = os.WriteFile(path, data, 0600); e != nil {
					t.Fatal(e)
				}
				validator := NewImgKeyValidator(path)
				if validator == nil || !validator.Validate(key) || validator.Validate([]byte("wrong-fixture-16")) {
					t.Fatal("image key validation failed")
				}
				broken := append([]byte{}, data...)
				broken[15+47] ^= 1
				if e = os.WriteFile(path, broken, 0600); e != nil {
					t.Fatal(e)
				}
				if NewImgKeyValidator(path).Validate(key) {
					t.Fatal("corrupt padding accepted")
				}
			}
		}
	}
}
func TestMalformedImageLengths(t *testing.T) {
	plain := testPicture(t, "jpg")
	data := testV4(t, plain, []byte("local-fixture-16"), '2', 0x82)
	for _, offset := range []int{6, 10} {
		bad := append([]byte{}, data...)
		binary.LittleEndian.PutUint32(bad[offset:], 0xffffffff)
		if _, _, e := Dat2ImageV4(bad, []byte("local-fixture-16")); e == nil {
			t.Fatal("invalid length accepted")
		}
	}
	for n := 0; n < 16; n++ {
		_, _, _ = Dat2Image(data[:n])
	}
}
func TestMultipleHistoricalImageKeys(t *testing.T) {
	plain := testPicture(t, "jpg")
	var samples []*AesKeyValidator
	keys := [][]byte{[]byte("first-fixture-16"), []byte("other-fixture-16")}
	for i, key := range keys {
		path := filepath.Join(t.TempDir(), string(rune('a'+i))+".dat")
		if e := os.WriteFile(path, testV4(t, plain, key, '2', 0x82), 0600); e != nil {
			t.Fatal(e)
		}
		samples = append(samples, NewImgKeyValidator(path))
	}
	v := &AesKeyValidator{Samples: samples}
	for _, key := range keys {
		if !v.Validate(key) {
			t.Fatal("historical key missing")
		}
	}
}

// Black HEVC frame from github.com/Eyevinn/mp4ff v0.49.0, hevc/testdata/blackframe.265 (MIT).
func TestBundledWxgfToJpeg(t *testing.T) {
	path := os.Getenv("MEMBER_WECHAT_FFMPEG")
	if path == "" {
		t.Skip("bundled Windows converter integration")
	}
	ConfigureFFmpeg(path)
	payload, _ := hex.DecodeString("0000000140010c01ffff01600000030090000003000003001e9598090000000142010101600000030090000003000003001ea014207965959a4932bc05a02000000300200000030321000000014401c172b462400000012801af1d44c8f702357fff7639fb1c007f6304ab280000030019a00001041a90000000010201d0294be10c638950f98290a2e94d000000010001e024f55fa2c294988e8c00071c")
	data := make([]byte, 19)
	copy(data, []byte("wxgf"))
	data[4] = 19
	binary.BigEndian.PutUint32(data[15:19], uint32(len(payload)))
	data = append(data, payload...)
	got, ext, e := Dat2Image(data)
	if e != nil || ext != "jpg" {
		t.Fatalf("wxgf conversion failed: %v", e)
	}
	decoded, _, e := image.Decode(bytes.NewReader(got))
	if e != nil || decoded.Bounds().Dx() < 1 || decoded.Bounds().Dy() < 1 {
		t.Fatalf("decoded JPEG unusable: %v", e)
	}
	t.Logf("converted HEVC wxgf to valid JPEG %dx%d", decoded.Bounds().Dx(), decoded.Bounds().Dy())
}
