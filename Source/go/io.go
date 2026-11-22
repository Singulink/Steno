package steno

import (
	"io"
)

// chunk is the number of input bytes that Writer and Reader convert at a time.
const chunk = 4096

// A Writer is an io.Writer that takes UTF-8 text and writes the Steno bytes of it, as one value, to an underlying writer.
//
// Write may end in the middle of a UTF-8 sequence: the start of the sequence is held back until the next Write. Close checks that the text
// ended at the end of a sequence; it does not close the underlying writer. A Writer does not buffer the Steno bytes: each Write writes them to
// the underlying writer before it returns. Wrap the underlying writer in a bufio.Writer for many small writes.
type Writer struct {
	w   io.Writer
	enc Encoder
	buf []byte
	err error
}

// NewWriter returns a Writer that writes the Steno bytes of one value to w.
func NewWriter(w io.Writer) *Writer {
	return &Writer{w: w}
}

// Reset makes z write a new value to w, and drops what it holds back.
func (z *Writer) Reset(w io.Writer) {
	z.w = w
	z.enc.Reset()
	z.err = nil
}

// Write encodes the UTF-8 text p as the next piece of the value and writes its Steno bytes to the underlying writer.
//
// If p is not valid UTF-8, Write encodes and writes the text before the bad sequence, and returns the number of bytes of p before it and an
// *EncodeError whose offset is the position of the sequence in p. The bad sequence is not consumed, and the Writer can be used again. If the
// sequence began in an earlier Write, the bytes held from that call are dropped, the offset is negative (-1 is the last byte held), and Write
// returns 0. An error of the underlying writer is returned, and returned again by every later call.
func (z *Writer) Write(p []byte) (int, error) {
	return writePiece(z, p)
}

// WriteString is Write for a string.
func (z *Writer) WriteString(s string) (int, error) {
	return writePiece(z, s)
}

func writePiece[T string | []byte](z *Writer, p T) (int, error) {
	if z.err != nil {
		return 0, z.err
	}
	done := 0
	for done < len(p) {
		end := min(done+chunk, len(p))
		var err error
		z.buf, err = appendUTF8Piece(&z.enc, z.buf[:0], p[done:end])
		if err != nil {
			// The encoder is as it was before this chunk. Encode the text of the chunk before the bad sequence, which ends at the end of a
			// code point, and drop the start of the sequence if the encoder holds it from an earlier chunk or call.
			at := done + err.(*EncodeError).Offset
			if at > done {
				z.buf, _ = appendUTF8Piece(&z.enc, z.buf[:0], p[done:at])
				if _, werr := z.w.Write(z.buf); werr != nil {
					z.err = werr
					return done, werr
				}
			}
			z.enc.dropUTF8()
			return max(at, 0), &EncodeError{Reason: InvalidUTF8, Offset: at}
		}
		if _, err := z.w.Write(z.buf); err != nil {
			z.err = err
			return done, err
		}
		done = end
	}
	return done, nil
}

// Close ends the text. If the text ended in the middle of a UTF-8 sequence, Close returns an *EncodeError with the reason InvalidUTF8. Close does
// not close the underlying writer, and it does not start a new value: call Reset for that.
func (z *Writer) Close() error {
	if z.err != nil {
		return z.err
	}
	_, err := z.enc.Flush(nil)
	return err
}

// A Reader is an io.Reader that reads the Steno bytes of one value from an underlying reader and returns its text as UTF-8.
//
// The value ends where the underlying reader ends. If the bytes are not valid Steno data, or the value has a surrogate code point (which has no
// UTF-8 form), Read returns the text before the bad code point and then a *DecodeError, whose offset counts from the first byte of the value.
type Reader struct {
	r   io.Reader
	dec Decoder
	in  []byte
	out []byte
	off int
	err error
}

// NewReader returns a Reader that decodes the Steno bytes of one value from r.
func NewReader(r io.Reader) *Reader {
	return &Reader{r: r}
}

// Reset makes z read a new value from r, and drops what it holds.
func (z *Reader) Reset(r io.Reader) {
	z.r = r
	z.dec.Reset()
	z.out = z.out[:0]
	z.off = 0
	z.err = nil
}

// maxEmptyReads is how many reads in a row of the underlying reader may return no bytes and no error before Read gives up with
// io.ErrNoProgress, as bufio.Reader does.
const maxEmptyReads = 100

// Read reads UTF-8 text into p. It returns io.EOF at the end of a valid value. With an empty p it returns 0 and no error at once.
func (z *Reader) Read(p []byte) (int, error) {
	if len(p) == 0 {
		return 0, nil
	}
	for empty := 0; z.off == len(z.out); {
		if z.err != nil {
			return 0, z.err
		}
		if z.in == nil {
			z.in = make([]byte, chunk)
		}
		n, err := z.r.Read(z.in)
		if n == 0 && err == nil {
			if empty++; empty >= maxEmptyReads {
				return 0, io.ErrNoProgress
			}
			continue
		}
		empty = 0
		z.out, z.off = z.out[:0], 0
		if n > 0 {
			z.decode(z.in[:n])
		}
		if z.err == nil && err != nil {
			if err == io.EOF {
				err = z.dec.Flush()
				if err == nil {
					err = io.EOF
				}
			}
			z.err = err
		}
	}
	n := copy(p, z.out[z.off:])
	z.off += n
	return n, nil
}

// decode decodes in into z.out. On an error it decodes the bytes before the bad code point, and keeps the error for the next Read.
func (z *Reader) decode(in []byte) {
	start := z.dec.pos
	out, err := z.dec.Append(z.out, in)
	if err == nil {
		z.out = out
		return
	}
	if de, ok := err.(*DecodeError); ok && de.Offset > start {
		z.out, _ = z.dec.Append(z.out, in[:de.Offset-start])
	}
	z.err = err
}
