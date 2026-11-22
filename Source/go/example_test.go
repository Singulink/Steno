package steno_test

import (
	"bytes"
	"errors"
	"fmt"
	"io"

	steno "github.com/Singulink/Steno/Source/go"
)

func Example() {
	b, err := steno.Encode("Москва")
	if err != nil {
		panic(err)
	}
	s, err := steno.Decode(b)
	if err != nil {
		panic(err)
	}
	fmt.Println(len(b), len("Москва"))
	fmt.Println(s)

	// The bytes sort like the text, so a store can compare them without decoding.
	zug, _ := steno.Encode("Zug")
	zurich, _ := steno.Encode("Zürich")
	fmt.Println(bytes.Compare(zug, zurich) < 0, "Zug" < "Zürich")
	// Output:
	// 7 12
	// Москва
	// true true
}

func ExampleAppendEncode() {
	// Append to a buffer that is always large enough, so that it is not grown.
	text := "Grüße"
	buf := make([]byte, 0, steno.MaxEncodedLen(len(text)))
	buf, err := steno.AppendEncode(buf, text)
	fmt.Printf("% X %v\n", buf, err)
	// Output:
	// 2E 59 A3 86 4C <nil>
}

func ExampleEncodeRunes() {
	// A lone surrogate is a code point of its own. A Go string cannot hold it, but a []rune can.
	b, err := steno.EncodeRunes([]rune{'a', 0xD800, 'b'})
	fmt.Printf("% X %v\n", b, err)

	r, err := steno.DecodeRunes(b)
	fmt.Printf("%U %v\n", r, err)

	// A high surrogate code point directly followed by a low one is not a value.
	_, err = steno.EncodeRunes([]rune{0xD83D, 0xDE00})
	fmt.Println(err)

	// Decoding a surrogate code point to a string is an error, never a replacement.
	_, err = steno.Decode(b)
	fmt.Println(err)
	// Output:
	// 48 F8 C5 20 49 <nil>
	// [U+0061 U+D800 U+0062] <nil>
	// steno: cannot encode: not-a-value at offset 1
	// steno: surrogate code point at offset 1 has no UTF-8 form
}

func ExampleEncodeUTF16() {
	// UTF-16 from another system, with a surrogate pair (U+1F600) and an unpaired surrogate.
	u := []uint16{0x0048, 0xD83D, 0xDE00, 0xDC00}
	b := steno.EncodeUTF16(u)
	back, err := steno.DecodeUTF16(b)
	fmt.Printf("%X %v\n", back, err)
	// Output:
	// [48 D83D DE00 DC00] <nil>
}

func ExampleDecodeError() {
	_, err := steno.Decode([]byte{0x2F, 0x50, 0x00})

	var de *steno.DecodeError
	if errors.As(err, &de) {
		fmt.Println(de.Reason, de.Offset, de.Reason == steno.ZeroByte)
	}
	fmt.Println(err)
	// Output:
	// zero-byte 2 true
	// steno: invalid data: zero-byte at offset 2
}

func ExampleEncoder() {
	// The pieces may split a UTF-8 sequence: "é" is C3 A9.
	var enc steno.Encoder
	var b []byte
	for _, piece := range []string{"caf\xc3", "\xa9 crème"} {
		var err error
		if b, err = enc.AppendString(b, piece); err != nil {
			panic(err)
		}
	}
	b, err := enc.Flush(b)

	whole, _ := steno.Encode("café crème")
	fmt.Println(bytes.Equal(b, whole), err)

	// Reset before the next value.
	enc.Reset()
	// Output:
	// true <nil>
}

func ExampleDecoder() {
	b, _ := steno.Encode("東京")

	// Bytes that arrive one at a time.
	var dec steno.Decoder
	var text []byte
	for i := range b {
		var err error
		if text, err = dec.Append(text, b[i:i+1]); err != nil {
			panic(err)
		}
	}
	// Flush reports bytes of an unfinished code point.
	fmt.Println(string(text), dec.Flush())
	// Output:
	// 東京 <nil>
}

func ExampleNewWriter() {
	var buf bytes.Buffer
	w := steno.NewWriter(&buf)
	fmt.Fprintf(w, "%s, %s!", "Привет", "мир")
	if err := w.Close(); err != nil {
		panic(err)
	}

	text, err := io.ReadAll(steno.NewReader(&buf))
	fmt.Println(string(text), err)
	// Output:
	// Привет, мир! <nil>
}

func ExampleIsASCIIText() {
	a, _ := steno.Encode("Hello\tworld\n")
	b, _ := steno.Encode("Grüße")
	fmt.Println(steno.IsASCIIText(a), steno.IsASCIIText(b))
	// Output:
	// true false
}

func ExampleReason() {
	for _, b := range [][]byte{{0xFF}, {0xE9}, {0x01, 0xFF}, {0x01, 0x03}} {
		_, err := steno.DecodeRunes(b)
		var de *steno.DecodeError
		errors.As(err, &de)
		fmt.Println(de.Reason)
	}
	// Output:
	// unused-lead
	// truncated
	// past-range
	// invalid-trail
}
