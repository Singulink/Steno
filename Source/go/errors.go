package steno

import "strconv"

// Reason is why decoding failed. The first six values are the rejection rules 1 to 6 of FORMAT.md §10.1, by their names in the test vectors.
// LoneSurrogate is not a rule of the format: it is returned only by the functions that decode to UTF-8.
type Reason uint8

const (
	// ZeroByte: a byte 00, as a lead byte or a trail byte (rule 1).
	ZeroByte Reason = iota + 1

	// UnusedLead: a lead byte that the layout of the current state does not use (rule 2).
	UnusedLead

	// Truncated: the bytes end in the middle of the encoding of a code point (rule 3).
	Truncated

	// PastRange: a code point beyond the last code point of its range, which includes every value above U+10FFFF (rule 4).
	PastRange

	// SurrogatePair: a low surrogate code point directly after a high surrogate code point (rule 5).
	SurrogatePair

	// InvalidTrail: a byte 01..07 where a trail byte is expected; trail bytes are 08..FF (rule 6). A byte 00 there is ZeroByte.
	InvalidTrail

	// LoneSurrogate: the bytes are valid so far, but the value has a surrogate code point (U+D800..U+DFFF), which has no UTF-8 form and so cannot
	// be part of a Go string. Decode the bytes with DecodeRunes or DecodeUTF16 to get such a value.
	LoneSurrogate
)

// String returns the name of the reason as the test vectors write it, for example "zero-byte", or "lone-surrogate".
func (r Reason) String() string {
	switch r {
	case ZeroByte:
		return "zero-byte"
	case UnusedLead:
		return "unused-lead"
	case Truncated:
		return "truncated"
	case PastRange:
		return "past-range"
	case SurrogatePair:
		return "surrogate-pair"
	case InvalidTrail:
		return "invalid-trail"
	case LoneSurrogate:
		return "lone-surrogate"
	default:
		return "Reason(" + strconv.Itoa(int(r)) + ")"
	}
}

// A DecodeError is returned when bytes cannot be decoded.
type DecodeError struct {
	// Reason is why the bytes were rejected.
	Reason Reason

	// Offset is the position in the bytes of the value where the encoding of the code point that broke the rule starts. For a Decoder it counts
	// from the first byte given since the last Reset.
	Offset int
}

func (e *DecodeError) Error() string {
	if e.Reason == LoneSurrogate {
		return "steno: surrogate code point at offset " + strconv.Itoa(e.Offset) + " has no UTF-8 form"
	}
	return "steno: invalid data: " + e.Reason.String() + " at offset " + strconv.Itoa(e.Offset)
}

// EncodeReason is why encoding failed. A value is never rejected: only input that is not a value, or not valid in its text form, is.
type EncodeReason uint8

const (
	// InvalidUTF8: a string or byte slice is not valid UTF-8. This includes the UTF-8 form of a surrogate (ED A0 80, for example), which is not
	// valid UTF-8.
	InvalidUTF8 EncodeReason = iota + 1

	// InvalidRune: a rune is negative or above U+10FFFF, so it is not a code point.
	InvalidRune

	// NotAValue: a high surrogate code point is directly followed by a low surrogate code point, which is not a value (FORMAT.md §3).
	NotAValue
)

// String returns the name of the reason: "invalid-utf8", "invalid-rune" or "not-a-value".
func (r EncodeReason) String() string {
	switch r {
	case InvalidUTF8:
		return "invalid-utf8"
	case InvalidRune:
		return "invalid-rune"
	case NotAValue:
		return "not-a-value"
	default:
		return "EncodeReason(" + strconv.Itoa(int(r)) + ")"
	}
}

// An EncodeError is returned when the input of an encoder is not a value.
type EncodeError struct {
	// Reason is why the input was rejected.
	Reason EncodeReason

	// Offset is the index in the input of the call (a byte for UTF-8, an element for runes and UTF-16) where the rejected sequence starts. For
	// NotAValue it is the index of the low surrogate. If the sequence started in an earlier call of an Encoder or Writer and was held, Offset is
	// negative: -1 is the last unit held.
	Offset int
}

func (e *EncodeError) Error() string {
	return "steno: cannot encode: " + e.Reason.String() + " at offset " + strconv.Itoa(e.Offset)
}
