// Package steno is the Go reference implementation of the Steno Encoding, version 1.
//
// The Steno Encoding (Steno for short) is a compact encoding of Unicode text whose bytes sort like the text. It was designed as the per-value
// encoding for databases and key-value stores: two encodings compared as unsigned bytes (bytes.Compare) are in the code point order of their
// text, which is the order of Go's string comparison. The encoding of a prefix is a prefix, a value has exactly one encoding, and the byte 00
// never occurs. No byte below 08 occurs inside a code point, so line feeds (03), tabs (02), carriage returns (05) and spaces (07) can be found at
// the byte level. The format is specified in FORMAT.md in the repository:
// https://github.com/Singulink/Singulink.Steno/blob/main/FORMAT.md
//
// # Text model
//
// A value of the format is a sequence of code points U+0000..U+10FFFF, surrogate code points included, but never a high surrogate code point
// directly followed by a low one. The package offers three forms of text:
//
//   - Go strings (Encode, Decode and the Append forms). A string must be valid UTF-8: Encode returns an *EncodeError with the reason InvalidUTF8
//     for anything else, and never replaces a bad sequence with U+FFFD. A string cannot hold a surrogate code point, so Decode fails with the
//     reason LoneSurrogate on a value that has one.
//   - Runes (EncodeRunes, DecodeRunes). A rune in U+D800..U+DFFF is a surrogate code point of its own. A high surrogate rune directly followed by
//     a low one is not a value, and EncodeRunes rejects it with the reason NotAValue; combine the pair into one rune first.
//   - UTF-16 code units (EncodeUTF16, DecodeUTF16), read and written as FORMAT.md §3 says. Every sequence of code units is a value, so
//     EncodeUTF16 never fails, and DecodeUTF16 gives back exactly the code units that were encoded.
//
// Runes and UTF-16 have full fidelity: every valid byte string decodes in these forms.
//
// # Errors
//
// Bytes that are not valid Steno data give a *DecodeError, with the Reason of FORMAT.md §10.1 (ZeroByte, UnusedLead, Truncated, PastRange,
// SurrogatePair or InvalidTrail) and the Offset where the encoding of the code point that broke the rule starts. Use errors.As to get it. Encoding a value never
// fails; only input that is not a value, or not valid UTF-8, gives an *EncodeError.
//
// # Streaming
//
// An Encoder and a Decoder take one value in pieces and give the same bytes or text as the one-shot functions for any split, also inside a UTF-8
// sequence, a surrogate pair or the bytes of one code point. Their zero values are ready to use. Flush does not start a new value; Reset does.
// Writer and Reader adapt them to io.Writer and io.Reader, with UTF-8 text on the text side.
//
// # Things to know before storing data
//
// The bytes carry no header or version marker: store the name in the constant Name ("x-steno") next to the data or in its schema. The encoding
// is stateful, so two encoded values cannot be joined by joining their bytes, and decoding must start at the first byte of a value. To keep
// several values in one buffer, end each with a 00 byte or record its length. The order is code point order, not the order of any language.
//
// # Concurrency
//
// The functions of the package are safe to call from many goroutines at once. They share a cache of layouts with a fixed number of slots, which
// they read and write without a lock. An Encoder, Decoder, Writer or Reader must not be used by several goroutines at once.
package steno
