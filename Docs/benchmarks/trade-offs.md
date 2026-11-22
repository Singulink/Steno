# Trade-offs at a Glance

**Steno is smaller than UTF-8, UTF-16, SCSU and BOCU-1 over every one of the 13 groups of values measured, and smaller than every general compressor over each group of values of up to 16 characters.** The exceptions are single languages or texts at particular lengths of value. They have four causes, listed below with why the design accepts them. Each row links to the page that compares the two in full.

A set of values is one language or text at one length of value, such as Russian in 16-character UDHR values; a group of values is all the sets of one length in one corpus. Steno, UTF-8, UTF-16 and BOCU-1 give one encoding per text, so their sizes are the same in every correct implementation; SCSU leaves choices to the encoder, and its sizes are those of ICU's encoder. The speeds are those of the Rust reference implementation, Steno (Rust), against native implementations of the alternatives, and are indicative.

## Size Scoreboard

Over the 13 groups of values and the 565 sets of values measured; tied means within 0.5%:

| Alternative | Groups where Steno is smaller | Other's size against Steno, per group (+ is larger) | Sets where Steno is smaller | Tied | Sets where the other is smaller | Largest margin of the other |
|---|--:|--:|--:|--:|--:|---|
| [UTF-8](../articles/comparisons/utf-8.md) | 13 of 13 | +38% to +81% | 545 | 20 | 0 | none |
| [UTF-16](../articles/comparisons/utf-16.md) | 13 of 13 | +58% to +92% | 555 | 2 | 8 | −10%: Chinese (Traditional), UDHR, 4-char values |
| [SCSU](../articles/comparisons/scsu.md) | 13 of 13 | +0.6% to +7% | 271 | 281 | 13 | −9%: Amharic, UDHR, documents |
| [BOCU-1](../articles/comparisons/bocu-1.md) | 13 of 13 | +1% to +12% | 506 | 25 | 34 | −17%: Burmese, UDHR, 4-char values |
| [General compressors](../articles/comparisons/compressors.md) (the smallest of the eight) | 8 of 13 | −60% to +82% | 353 | 0 | 212 | Brotli 11 −74%: English, UDHR, documents |

## Speed Scoreboard

The figures appear here after the benchmark run.

## Why Another Encoding Is Sometimes Smaller

Every set of values where another encoding is smaller than Steno by 0.5% or more has one of these causes:

| Cause | Affects you if | Ahead | How many and by how much | Why the design accepts this |
|---|---|---|--:|---|
| The first letter of a script above U+0EFF takes 3 bytes | most of your values are under about 16 characters and in Georgian, Khmer, Burmese, Tibetan, Amharic or polytonic Greek | BOCU-1 and UTF-16 | 35 sets of values; up to 17% (BOCU-1, Burmese, UDHR, 4-char values) | Every value starts ready for ASCII and the Latin letters, which take 1 byte, and for the scripts up to U+0EFF, whose first letter takes 2: Greek, Cyrillic, Armenian, Hebrew, Arabic, the Indic scripts, Thai and Lao. There is no room left in that starting state to give a 2-byte first letter to the scripts above U+0EFF as well, such as Georgian, Khmer, Burmese, Tibetan and Ethiopic. The cost is one byte, once per value; from the second letter on, these scripts take 1 byte per letter too. ([§6.6](../articles/reference/specification.md#66-the-initial-layout), [§13.6](../articles/reference/specification.md#136-every-layout-fits)) |
| The script spans several windows (runs of up to 128 code points whose characters take 1 byte) | you store Amharic or Urdu paragraphs or documents | SCSU | 8 sets of values; up to 9% (SCSU, Amharic, UDHR, documents) | The Steno Encoding moves its window by itself as the text moves, with no tag bytes; that is what keeps its bytes in the order of the text, with one encoding per text. A script that spans more than one window, such as Ethiopic or the Arabic letters of Urdu, makes the window move often, and each move costs a byte or two. SCSU places tag bytes where its encoder chooses, which is how it wins here and why its bytes keep neither the order nor one encoding per text. ([§4](../articles/reference/specification.md#4-windows), [§7](../articles/reference/specification.md#7-state-update)) |
| The first ideograph, kana or syllable of a value takes 3 bytes | most of your values are very short Chinese, Japanese or Korean text, of about 4 characters | UTF-16 and SCSU | 8 sets of values; up to 10% (UTF-16, Chinese (Traditional), UDHR, 4-char values) | Every value starts ready for Latin text. The first ideograph, kana or Hangul syllable takes 3 bytes and switches the value to Chinese, Japanese or Korean text; after that, ideographs and syllables take 2 bytes and kana 1. That switch is what gives Japanese 1.58 bytes per character on 16-character UDHR values, against 2.00 in UTF-16 and 2.97 in UTF-8. ([§6.2](../articles/reference/specification.md#62-pieces), [§7](../articles/reference/specification.md#7-state-update)) |
| Punctuation and symbols outside the letters' window | your text is dense with typographic punctuation, mathematical symbols or emoji | SCSU | 4 sets of values; up to 1% (SCSU, Spanish chat with emoji, Mixed content, documents) | Dashes, quotation marks and currency signs take 2 bytes without moving the window away from the text's letters, so the text after them stays at 1 byte per letter; other symbols, such as arrows and emoji, take 3. ([§6.2](../articles/reference/specification.md#62-pieces)) |

## Speed

Transcoding does more work in Steno than copying UTF-8: the encoder and the decoder keep a small state and look up a table for it. The state is what makes the bytes small, and a store that compares, sorts, matches and hashes the Steno bytes pays the transcoding only when text enters or leaves it. Text that changes script often is the slowest to encode and decode.

## Long Documents

On paragraphs and whole documents a general compressor is the right tool: it finds repetition across a long text, which a per-value encoding cannot, and it is up to 60% smaller than Steno over a group of values. Its bytes keep none of the order, the prefixes or one encoding per text, which a document that is stored and read back whole does not need. See [Steno and General Compressors](../articles/comparisons/compressors.md).

## Weighted by Speakers

How many people speak a language where another encoding is smaller than Steno, at any length of value (often only at 4 characters) and at 16 characters. Of the 56 languages measured, 25 have 50 million speakers or more and are in the source: Wikipedia's list of languages by total number of speakers (first and second language, figures from Ethnologue, 2026), read on 2026-11-21. The smaller languages are left out, and a speaker of several languages is counted in each, so the shares are approximate.

| Alternative | Languages where it is smaller | Share of the speakers of those 25 languages, at any length | At 16 characters |
|---|---|--:|--:|
| UTF-8 | none | 0% | 0% |
| UTF-16 | Amharic, Chinese and Korean | 19% | 17% |
| SCSU | Urdu, Amharic and Japanese | 6% | 0% |
| BOCU-1 | Greek (polytonic), Georgian, Khmer, Burmese, Tibetan and Amharic (Greek (polytonic), Georgian, Khmer, Burmese and Tibetan not in the source) | 1% | 1% |

## Every Case

Each comparison page lists its exceptions with their cause. Every set of values where an encoding is smaller than Steno by 0.5% or more, every text where an encoding is faster than Steno (Rust) beyond the error margins, and every case where a compressor is smaller follow.

<details>
<summary>Every case, row by row</summary>

### Size Against UTF-8, UTF-16, SCSU and BOCU-1

Every set of values where one of these encodings is smaller than Steno by 0.5% or more (54 rows). 104 more cases, where one of them is smaller by less than 0.5%, are on the Size by Language pages.

| Group | Language or text | Smaller than Steno |
|---|---|---|
| UDHR, 4-char values | Greek (polytonic) | BOCU-1 −6% |
| UDHR, 4-char values | Georgian | BOCU-1 −16% |
| UDHR, 4-char values | Khmer | BOCU-1 −17% |
| UDHR, 4-char values | Burmese | BOCU-1 −17% |
| UDHR, 4-char values | Tibetan | BOCU-1 −16% |
| UDHR, 4-char values | Amharic | BOCU-1 −13%, UTF-16 −1% |
| UDHR, 4-char values | Chinese (Simplified) | UTF-16 −9% |
| UDHR, 4-char values | Chinese (Traditional) | UTF-16 −10% |
| UDHR, 4-char values | Korean | UTF-16 −5% |
| UDHR, 8-char values | Greek (polytonic) | BOCU-1 −3% |
| UDHR, 8-char values | Georgian | BOCU-1 −8% |
| UDHR, 8-char values | Khmer | BOCU-1 −10% |
| UDHR, 8-char values | Burmese | BOCU-1 −10% |
| UDHR, 8-char values | Tibetan | BOCU-1 −8% |
| UDHR, 8-char values | Amharic | BOCU-1 −7% |
| UDHR, 8-char values | Chinese (Simplified) | UTF-16 −3% |
| UDHR, 8-char values | Chinese (Traditional) | UTF-16 −4% |
| UDHR, 16-char values | Georgian | BOCU-1 −3% |
| UDHR, 16-char values | Khmer | BOCU-1 −6% |
| UDHR, 16-char values | Burmese | BOCU-1 −6% |
| UDHR, 16-char values | Tibetan | BOCU-1 −3% |
| UDHR, 16-char values | Amharic | BOCU-1 −3% |
| UDHR, 16-char values | Chinese (Traditional) | UTF-16 −0.8% |
| UDHR, paragraphs | Urdu | SCSU −2% |
| UDHR, paragraphs | Khmer | BOCU-1 −0.8% |
| UDHR, paragraphs | Burmese | BOCU-1 −0.5% |
| UDHR, paragraphs | Amharic | SCSU −6% |
| UDHR, documents | Urdu | SCSU −3% |
| UDHR, documents | Amharic | SCSU −9% |
| Mixed content, lines | English with typographic punctuation | SCSU −1% |
| Mixed content, documents | Spanish chat with emoji | SCSU −1% |
| Mixed content, documents | English with typographic punctuation | SCSU −1% |
| Mixed content, documents | Math and science notation | SCSU −0.6% |
| Wikipedia, 4-char values | Georgian | BOCU-1 −14% |
| Wikipedia, 4-char values | Khmer | BOCU-1 −12% |
| Wikipedia, 4-char values | Burmese | BOCU-1 −16% |
| Wikipedia, 4-char values | Tibetan | BOCU-1 −15% |
| Wikipedia, 4-char values | Amharic | BOCU-1 −12% |
| Wikipedia, 4-char values | Chinese | UTF-16 −1% |
| Wikipedia, 4-char values | Japanese | SCSU −0.5% |
| Wikipedia, 8-char values | Georgian | BOCU-1 −7% |
| Wikipedia, 8-char values | Khmer | BOCU-1 −6% |
| Wikipedia, 8-char values | Burmese | BOCU-1 −9% |
| Wikipedia, 8-char values | Tibetan | BOCU-1 −8% |
| Wikipedia, 8-char values | Amharic | BOCU-1 −6% |
| Wikipedia, 16-char values | Georgian | BOCU-1 −1% |
| Wikipedia, 16-char values | Khmer | BOCU-1 −3% |
| Wikipedia, 16-char values | Burmese | BOCU-1 −4% |
| Wikipedia, 16-char values | Tibetan | BOCU-1 −2% |
| Wikipedia, 16-char values | Amharic | BOCU-1 −2% |
| Wikipedia, paragraphs | Urdu | SCSU −3% |
| Wikipedia, paragraphs | Amharic | SCSU −3% |
| Wikipedia, articles | Urdu | SCSU −4% |
| Wikipedia, articles | Amharic | SCSU −5% |

### Speed

The figures appear here after the benchmark run.

### Size Against the Compressors

The compressors are smaller than Steno on paragraphs and whole documents, and their bytes keep none of the order, the prefixes or one encoding per text. Over each group of values:

| Group | Smaller than Steno |
|---|---|
| UDHR, 4-char values | none |
| UDHR, 8-char values | none |
| UDHR, 16-char values | none |
| UDHR, paragraphs | Brotli 11 −9%, Deflate max −4% |
| UDHR, documents | Brotli 11 −60%, Zstd 19 −58%, Deflate max −56%, Zstd 1 −47%, LZ4 max −42%, Brotli 1 −41%, Deflate fast −23%, LZ4 fast −14% |
| Mixed content, lines | none |
| Mixed content, documents | Brotli 11 −24%, Deflate max −10%, Zstd 19 −7%, Zstd 1 −2% |
| Names and UI strings | none |
| Wikipedia, 4-char values | none |
| Wikipedia, 8-char values | none |
| Wikipedia, 16-char values | none |
| Wikipedia, paragraphs | Brotli 11 −26%, Deflate max −21%, Zstd 19 −16%, Zstd 1 −12%, Brotli 1 −8% |
| Wikipedia, articles | Brotli 11 −58%, Zstd 19 −55%, Deflate max −53%, Zstd 1 −46%, Brotli 1 −42%, LZ4 max −40%, Deflate fast −23%, LZ4 fast −17% |

<details>
<summary>Every language and text where a compressor is smaller than Steno (212 rows)</summary>

| Group | Language or text | Smaller than Steno |
|---|---|---|
| UDHR, paragraphs | English | Brotli 11 −48%, Deflate max −31%, Zstd 19 −25%, Zstd 1 −23%, Brotli 1 −21%, Deflate fast −12%, LZ4 max −8%, LZ4 fast −7% |
| UDHR, paragraphs | French | Deflate max −28%, Brotli 11 −26%, Zstd 19 −22%, Zstd 1 −20%, Brotli 1 −18%, Deflate fast −9%, LZ4 max −5%, LZ4 fast −4% |
| UDHR, paragraphs | German | Deflate max −27%, Brotli 11 −24%, Zstd 19 −22%, Zstd 1 −20%, Brotli 1 −17%, Deflate fast −11%, LZ4 max −7%, LZ4 fast −6% |
| UDHR, paragraphs | Spanish | Brotli 11 −35%, Deflate max −30%, Zstd 19 −24%, Zstd 1 −23%, Brotli 1 −20%, Deflate fast −10%, LZ4 max −6%, LZ4 fast −5% |
| UDHR, paragraphs | Portuguese | Deflate max −27%, Brotli 11 −26%, Zstd 19 −21%, Zstd 1 −20%, Brotli 1 −17%, Deflate fast −7%, LZ4 max −3%, LZ4 fast −3% |
| UDHR, paragraphs | Swedish | Deflate max −26%, Brotli 11 −23%, Zstd 19 −19%, Zstd 1 −17%, Brotli 1 −15%, Deflate fast −6%, LZ4 max −3%, LZ4 fast −2% |
| UDHR, paragraphs | Finnish | Deflate max −29%, Brotli 11 −27%, Zstd 19 −24%, Zstd 1 −22%, Brotli 1 −19%, Deflate fast −8%, LZ4 max −5%, LZ4 fast −4% |
| UDHR, paragraphs | Icelandic | Deflate max −17%, Brotli 11 −17%, Zstd 19 −12%, Zstd 1 −10%, Brotli 1 −7% |
| UDHR, paragraphs | Polish | Deflate max −23%, Brotli 11 −20%, Zstd 19 −17%, Zstd 1 −16%, Brotli 1 −13%, Deflate fast −7%, LZ4 max −4%, LZ4 fast −4% |
| UDHR, paragraphs | Czech | Deflate max −14%, Brotli 11 −13%, Zstd 19 −9%, Zstd 1 −6%, Brotli 1 −4% |
| UDHR, paragraphs | Hungarian | Deflate max −22%, Brotli 11 −22%, Zstd 19 −18%, Zstd 1 −16%, Brotli 1 −12%, Deflate fast −3%, LZ4 max −1%, LZ4 fast −0.4% |
| UDHR, paragraphs | Romanian | Deflate max −26%, Brotli 11 −25%, Zstd 19 −21%, Zstd 1 −20%, Brotli 1 −16%, Deflate fast −8%, LZ4 max −5%, LZ4 fast −4% |
| UDHR, paragraphs | Lithuanian | Deflate max −23%, Brotli 11 −20%, Zstd 19 −18%, Zstd 1 −16%, Brotli 1 −13%, Deflate fast −5%, LZ4 max −3%, LZ4 fast −2% |
| UDHR, paragraphs | Serbian (Latin) | Deflate max −24%, Brotli 11 −21%, Zstd 19 −18%, Zstd 1 −16%, Brotli 1 −14%, Deflate fast −7%, LZ4 max −3%, LZ4 fast −3% |
| UDHR, paragraphs | Turkish | Deflate max −21%, Brotli 11 −19%, Zstd 19 −16%, Zstd 1 −14%, Brotli 1 −11%, Deflate fast −2% |
| UDHR, paragraphs | Vietnamese | Deflate max −14%, Brotli 11 −13%, Zstd 19 −8%, Zstd 1 −5% |
| UDHR, paragraphs | Russian | Brotli 11 −17%, Deflate max −0.8% |
| UDHR, paragraphs | Ukrainian | Brotli 11 −9% |
| UDHR, paragraphs | Bulgarian | Brotli 11 −16%, Deflate max −5% |
| UDHR, paragraphs | Serbian (Cyrillic) | Brotli 11 −10% |
| UDHR, paragraphs | Kazakh | Brotli 11 −16%, Deflate max −6% |
| UDHR, paragraphs | Greek | Brotli 11 −16%, Deflate max −3% |
| UDHR, paragraphs | Greek (polytonic) | Brotli 11 −11%, Deflate max −4% |
| UDHR, paragraphs | Armenian | Brotli 11 −12%, Deflate max −3% |
| UDHR, paragraphs | Georgian | Brotli 11 −1% |
| UDHR, paragraphs | Hebrew | Brotli 11 −11% |
| UDHR, paragraphs | Arabic | Brotli 11 −3% |
| UDHR, paragraphs | Persian | Brotli 11 −12%, Deflate max −1% |
| UDHR, paragraphs | Urdu | Brotli 11 −24%, Deflate max −17%, Zstd 19 −9%, Zstd 1 −6%, Brotli 1 −3% |
| UDHR, paragraphs | Tamil | Brotli 11 −1% |
| UDHR, paragraphs | Burmese | Brotli 11 −10%, Deflate max −5% |
| UDHR, paragraphs | Tibetan | Brotli 11 −2% |
| UDHR, paragraphs | Amharic | Brotli 11 −9%, Deflate max −3% |
| UDHR, documents | English | Brotli 11 −74%, Zstd 19 −65%, Deflate max −64%, Zstd 1 −62%, Brotli 1 −59%, LZ4 max −51%, Deflate fast −47%, LZ4 fast −43% |
| UDHR, documents | French | Brotli 11 −66%, Zstd 19 −64%, Deflate max −63%, Zstd 1 −60%, Brotli 1 −56%, LZ4 max −49%, Deflate fast −44%, LZ4 fast −39% |
| UDHR, documents | German | Brotli 11 −65%, Zstd 19 −64%, Deflate max −63%, Zstd 1 −60%, Brotli 1 −57%, LZ4 max −50%, Deflate fast −46%, LZ4 fast −42% |
| UDHR, documents | Spanish | Brotli 11 −69%, Zstd 19 −65%, Deflate max −64%, Zstd 1 −62%, Brotli 1 −58%, LZ4 max −51%, Deflate fast −46%, LZ4 fast −42% |
| UDHR, documents | Portuguese | Brotli 11 −66%, Zstd 19 −64%, Deflate max −62%, Zstd 1 −60%, Brotli 1 −56%, LZ4 max −49%, Deflate fast −44%, LZ4 fast −40% |
| UDHR, documents | Swedish | Brotli 11 −64%, Zstd 19 −62%, Deflate max −61%, Zstd 1 −58%, Brotli 1 −55%, LZ4 max −47%, Deflate fast −43%, LZ4 fast −38% |
| UDHR, documents | Finnish | Brotli 11 −63%, Zstd 19 −63%, Deflate max −61%, Zstd 1 −59%, Brotli 1 −55%, LZ4 max −47%, Deflate fast −41%, LZ4 fast −36% |
| UDHR, documents | Icelandic | Brotli 11 −60%, Zstd 19 −58%, Deflate max −57%, Zstd 1 −53%, Brotli 1 −50%, LZ4 max −40%, Deflate fast −36%, LZ4 fast −31% |
| UDHR, documents | Polish | Brotli 11 −63%, Zstd 19 −62%, Deflate max −61%, Zstd 1 −58%, Brotli 1 −55%, LZ4 max −47%, Deflate fast −44%, LZ4 fast −40% |
| UDHR, documents | Czech | Brotli 11 −59%, Zstd 19 −58%, Deflate max −56%, Zstd 1 −53%, Brotli 1 −50%, LZ4 max −41%, Deflate fast −36%, LZ4 fast −33% |
| UDHR, documents | Hungarian | Brotli 11 −64%, Zstd 19 −62%, Deflate max −61%, Zstd 1 −58%, Brotli 1 −54%, LZ4 max −48%, Deflate fast −42%, LZ4 fast −38% |
| UDHR, documents | Romanian | Brotli 11 −66%, Zstd 19 −64%, Deflate max −63%, Zstd 1 −60%, Brotli 1 −57%, LZ4 max −49%, Deflate fast −45%, LZ4 fast −41% |
| UDHR, documents | Lithuanian | Brotli 11 −63%, Zstd 19 −62%, Deflate max −61%, Zstd 1 −57%, Brotli 1 −54%, LZ4 max −46%, Deflate fast −41%, LZ4 fast −38% |
| UDHR, documents | Serbian (Latin) | Brotli 11 −63%, Zstd 19 −61%, Deflate max −60%, Zstd 1 −57%, Brotli 1 −53%, LZ4 max −44%, Deflate fast −40%, LZ4 fast −36% |
| UDHR, documents | Turkish | Brotli 11 −63%, Zstd 19 −61%, Deflate max −60%, Zstd 1 −57%, Brotli 1 −53%, LZ4 max −45%, Deflate fast −40%, LZ4 fast −36% |
| UDHR, documents | Vietnamese | Brotli 11 −67%, Zstd 19 −66%, Deflate max −65%, Zstd 1 −60%, Brotli 1 −54%, LZ4 max −54%, Deflate fast −45%, LZ4 fast −40% |
| UDHR, documents | Russian | Brotli 11 −61%, Zstd 19 −57%, Deflate max −55%, Zstd 1 −48%, LZ4 max −41%, Brotli 1 −39%, Deflate fast −16%, LZ4 fast −11% |
| UDHR, documents | Ukrainian | Brotli 11 −58%, Zstd 19 −54%, Deflate max −52%, Zstd 1 −45%, LZ4 max −38%, Brotli 1 −37%, Deflate fast −16%, LZ4 fast −8% |
| UDHR, documents | Bulgarian | Brotli 11 −62%, Zstd 19 −59%, Deflate max −57%, Zstd 1 −49%, LZ4 max −44%, Brotli 1 −41%, Deflate fast −20%, LZ4 fast −11% |
| UDHR, documents | Serbian (Cyrillic) | Brotli 11 −57%, Zstd 19 −54%, Deflate max −51%, Zstd 1 −44%, LZ4 max −36%, Brotli 1 −36%, Deflate fast −12%, LZ4 fast −4% |
| UDHR, documents | Kazakh | Brotli 11 −62%, Zstd 19 −60%, Deflate max −57%, Zstd 1 −50%, LZ4 max −45%, Brotli 1 −43%, Deflate fast −22%, LZ4 fast −16% |
| UDHR, documents | Greek | Brotli 11 −59%, Zstd 19 −56%, Deflate max −53%, Zstd 1 −45%, LZ4 max −40%, Brotli 1 −37%, Deflate fast −14%, LZ4 fast −7% |
| UDHR, documents | Greek (polytonic) | Brotli 11 −60%, Zstd 19 −58%, Deflate max −56%, Zstd 1 −48%, LZ4 max −44%, Brotli 1 −40%, Deflate fast −22%, LZ4 fast −14% |
| UDHR, documents | Armenian | Brotli 11 −62%, Zstd 19 −60%, Deflate max −58%, Zstd 1 −50%, LZ4 max −46%, Brotli 1 −42%, Deflate fast −23%, LZ4 fast −13% |
| UDHR, documents | Georgian | Brotli 11 −59%, Zstd 19 −58%, Deflate max −55%, LZ4 max −42%, Zstd 1 −36%, Brotli 1 −30%, Deflate fast −8% |
| UDHR, documents | Hebrew | Brotli 11 −53%, Zstd 19 −49%, Deflate max −47%, Zstd 1 −42%, Brotli 1 −34%, LZ4 max −27%, Deflate fast −8% |
| UDHR, documents | Arabic | Brotli 11 −52%, Zstd 19 −47%, Deflate max −45%, Zstd 1 −38%, Brotli 1 −31%, LZ4 max −27%, Deflate fast −8% |
| UDHR, documents | Persian | Brotli 11 −59%, Zstd 19 −56%, Deflate max −53%, Zstd 1 −47%, Brotli 1 −41%, LZ4 max −38%, Deflate fast −20%, LZ4 fast −11% |
| UDHR, documents | Urdu | Brotli 11 −65%, Zstd 19 −63%, Deflate max −61%, Zstd 1 −56%, Brotli 1 −50%, LZ4 max −49%, Deflate fast −33%, LZ4 fast −27% |
| UDHR, documents | Hindi | Brotli 11 −58%, Zstd 19 −55%, Deflate max −52%, LZ4 max −39%, Zstd 1 −37%, Brotli 1 −29%, Deflate fast −8% |
| UDHR, documents | Nepali | Brotli 11 −55%, Zstd 19 −53%, Deflate max −50%, Zstd 1 −35%, LZ4 max −35%, Brotli 1 −27%, Deflate fast −5% |
| UDHR, documents | Bengali | Brotli 11 −55%, Zstd 19 −54%, Deflate max −51%, LZ4 max −37%, Zstd 1 −35%, Brotli 1 −26%, Deflate fast −5% |
| UDHR, documents | Punjabi | Brotli 11 −55%, Zstd 19 −54%, Deflate max −51%, LZ4 max −38%, Zstd 1 −37%, Brotli 1 −29%, Deflate fast −9% |
| UDHR, documents | Gujarati | Brotli 11 −56%, Zstd 19 −55%, Deflate max −52%, LZ4 max −38%, Zstd 1 −36%, Brotli 1 −29%, Deflate fast −9% |
| UDHR, documents | Tamil | Brotli 11 −59%, Zstd 19 −59%, Deflate max −55%, LZ4 max −43%, Zstd 1 −36%, Brotli 1 −33%, Deflate fast −4% |
| UDHR, documents | Kannada | Brotli 11 −53%, Zstd 19 −52%, Deflate max −48%, LZ4 max −34%, Zstd 1 −32%, Brotli 1 −23% |
| UDHR, documents | Malayalam | Brotli 11 −60%, Zstd 19 −59%, Deflate max −57%, LZ4 max −45%, Zstd 1 −37%, Brotli 1 −29%, Deflate fast −9% |
| UDHR, documents | Thai | Brotli 11 −50%, Zstd 19 −49%, Deflate max −46%, LZ4 max −30%, Zstd 1 −30%, Brotli 1 −19% |
| UDHR, documents | Lao | Brotli 11 −50%, Zstd 19 −50%, Deflate max −46%, Zstd 1 −31%, LZ4 max −31%, Brotli 1 −21% |
| UDHR, documents | Khmer | Brotli 11 −53%, Zstd 19 −52%, Deflate max −48%, LZ4 max −33%, Zstd 1 −32%, Brotli 1 −23%, Deflate fast −1% |
| UDHR, documents | Burmese | Brotli 11 −62%, Zstd 19 −61%, Deflate max −58%, LZ4 max −46%, Zstd 1 −38%, Brotli 1 −35%, Deflate fast −14% |
| UDHR, documents | Tibetan | Zstd 19 −63%, Brotli 11 −63%, Deflate max −59%, LZ4 max −48%, Zstd 1 −42%, Brotli 1 −38%, Deflate fast −19%, LZ4 fast −4% |
| UDHR, documents | Amharic | Brotli 11 −61%, Zstd 19 −60%, Deflate max −57%, Zstd 1 −49%, LZ4 max −44%, Brotli 1 −43%, Deflate fast −26%, LZ4 fast −18% |
| UDHR, documents | Chinese (Simplified) | Brotli 11 −40%, Zstd 19 −33%, Deflate max −31%, Zstd 1 −24%, Brotli 1 −20%, LZ4 max −7% |
| UDHR, documents | Chinese (Traditional) | Brotli 11 −38%, Zstd 19 −33%, Deflate max −30%, Zstd 1 −22%, Brotli 1 −18%, LZ4 max −6% |
| UDHR, documents | Japanese | Brotli 11 −38%, Zstd 19 −33%, Deflate max −31%, Zstd 1 −23%, Brotli 1 −16%, LZ4 max −10% |
| UDHR, documents | Korean | Brotli 11 −56%, Zstd 19 −54%, Deflate max −52%, Zstd 1 −46%, Brotli 1 −42%, LZ4 max −37%, Deflate fast −26%, LZ4 fast −23% |
| Mixed content, lines | English with typographic punctuation | Brotli 11 −4% |
| Mixed content, lines | JSON catalog | Brotli 11 −8%, Deflate max −2% |
| Mixed content, lines | Math and science notation | Deflate max −3% |
| Mixed content, documents | Bilingual Arabic and English | Brotli 11 −25%, Deflate max −4%, Zstd 19 −0.2% |
| Mixed content, documents | Arabic chat with emoji | Brotli 11 −14%, Deflate max −7%, Zstd 19 −0.3% |
| Mixed content, documents | English chat with emoji | Brotli 11 −37%, Deflate max −28%, Zstd 19 −25%, Zstd 1 −23%, Brotli 1 −21%, Deflate fast −6%, LZ4 max −3%, LZ4 fast −1% |
| Mixed content, documents | Spanish chat with emoji | Brotli 11 −33%, Deflate max −29%, Zstd 19 −26%, Zstd 1 −22%, Brotli 1 −20%, Deflate fast −8%, LZ4 max −5%, LZ4 fast −4% |
| Mixed content, documents | Hindi chat with emoji | Brotli 11 −15%, Deflate max −7%, Zstd 19 −5% |
| Mixed content, documents | Korean chat with emoji | Brotli 11 −13%, Deflate max −9%, Zstd 19 −2% |
| Mixed content, documents | Russian chat with emoji | Brotli 11 −21%, Deflate max −10%, Zstd 19 −6%, Zstd 1 −0.7% |
| Mixed content, documents | English with typographic punctuation | Brotli 11 −42%, Deflate max −28%, Zstd 19 −26%, Zstd 1 −24%, Brotli 1 −22%, Deflate fast −6%, LZ4 max −3%, LZ4 fast −2% |
| Mixed content, documents | Hindi and English mixed | Brotli 11 −31%, Deflate max −1% |
| Mixed content, documents | Greek HTML | Brotli 11 −38%, Deflate max −26%, Zstd 19 −22%, Zstd 1 −17%, Brotli 1 −15%, Deflate fast −4%, LZ4 max −1% |
| Mixed content, documents | JSON catalog | Brotli 11 −41%, Deflate max −32%, Zstd 19 −31%, Zstd 1 −27%, Brotli 1 −24%, LZ4 max −18%, Deflate fast −17%, LZ4 fast −13% |
| Mixed content, documents | Math and science notation | Brotli 11 −31%, Deflate max −23%, Zstd 19 −20%, Zstd 1 −16%, Brotli 1 −14%, Deflate fast −6%, LZ4 max −1%, LZ4 fast −0.5% |
| Mixed content, documents | UI strings in many languages | Brotli 11 −23% |
| Mixed content, documents | Korean news | Brotli 11 −21%, Deflate max −16%, Zstd 19 −12%, Zstd 1 −8%, Brotli 1 −5% |
| Mixed content, documents | Chinese news | Brotli 11 −9% |
| Mixed content, documents | Social media posts | Brotli 11 −19%, Deflate max −1% |
| Mixed content, documents | Japanese technical text with English | Brotli 11 −16% |
| Mixed content, documents | Korean technical text with English | Brotli 11 −26%, Deflate max −14%, Zstd 19 −11%, Zstd 1 −7%, Brotli 1 −4% |
| Mixed content, documents | Russian technical text with English | Brotli 11 −29%, Deflate max −12%, Zstd 19 −9%, Zstd 1 −5%, Brotli 1 −2% |
| Mixed content, documents | Chinese technical text with English | Brotli 11 −25%, Deflate max −0.3% |
| Wikipedia, paragraphs | English | Brotli 11 −57%, Deflate max −42%, Zstd 19 −38%, Zstd 1 −37%, Brotli 1 −36%, Deflate fast −20%, LZ4 max −16%, LZ4 fast −14% |
| Wikipedia, paragraphs | French | Brotli 11 −36%, Deflate max −35%, Zstd 19 −31%, Zstd 1 −30%, Brotli 1 −28%, Deflate fast −14%, LZ4 max −11%, LZ4 fast −9% |
| Wikipedia, paragraphs | German | Deflate max −33%, Brotli 11 −32%, Zstd 19 −28%, Zstd 1 −27%, Brotli 1 −26%, Deflate fast −15%, LZ4 max −11%, LZ4 fast −10% |
| Wikipedia, paragraphs | Spanish | Brotli 11 −42%, Deflate max −37%, Zstd 19 −34%, Zstd 1 −33%, Brotli 1 −30%, Deflate fast −17%, LZ4 max −13%, LZ4 fast −12% |
| Wikipedia, paragraphs | Portuguese | Brotli 11 −41%, Deflate max −39%, Zstd 19 −36%, Zstd 1 −35%, Brotli 1 −33%, Deflate fast −18%, LZ4 max −14%, LZ4 fast −13% |
| Wikipedia, paragraphs | Italian | Deflate max −39%, Brotli 11 −39%, Zstd 19 −36%, Zstd 1 −34%, Brotli 1 −32%, Deflate fast −16%, LZ4 max −12%, LZ4 fast −11% |
| Wikipedia, paragraphs | Dutch | Deflate max −38%, Brotli 11 −37%, Zstd 19 −34%, Zstd 1 −32%, Brotli 1 −31%, Deflate fast −17%, LZ4 max −13%, LZ4 fast −12% |
| Wikipedia, paragraphs | Swedish | Deflate max −35%, Brotli 11 −35%, Zstd 19 −31%, Zstd 1 −30%, Brotli 1 −28%, Deflate fast −15%, LZ4 max −11%, LZ4 fast −10% |
| Wikipedia, paragraphs | Danish | Deflate max −32%, Brotli 11 −31%, Zstd 19 −27%, Zstd 1 −26%, Brotli 1 −24%, Deflate fast −13%, LZ4 max −9%, LZ4 fast −8% |
| Wikipedia, paragraphs | Finnish | Deflate max −34%, Brotli 11 −33%, Zstd 19 −30%, Zstd 1 −29%, Brotli 1 −27%, Deflate fast −13%, LZ4 max −10%, LZ4 fast −8% |
| Wikipedia, paragraphs | Icelandic | Brotli 11 −32%, Deflate max −32%, Zstd 19 −27%, Zstd 1 −25%, Brotli 1 −24%, Deflate fast −11%, LZ4 max −8%, LZ4 fast −6% |
| Wikipedia, paragraphs | Polish | Deflate max −28%, Brotli 11 −27%, Zstd 19 −23%, Zstd 1 −22%, Brotli 1 −20%, Deflate fast −9%, LZ4 max −6%, LZ4 fast −5% |
| Wikipedia, paragraphs | Czech | Brotli 11 −23%, Deflate max −23%, Zstd 19 −17%, Zstd 1 −15%, Brotli 1 −15%, Deflate fast −5%, LZ4 max −2%, LZ4 fast −1% |
| Wikipedia, paragraphs | Slovak | Brotli 11 −26%, Deflate max −25%, Zstd 19 −21%, Zstd 1 −19%, Brotli 1 −18%, Deflate fast −7%, LZ4 max −5%, LZ4 fast −4% |
| Wikipedia, paragraphs | Hungarian | Brotli 11 −24%, Deflate max −23%, Zstd 19 −18%, Zstd 1 −17%, Brotli 1 −15%, Deflate fast −3%, LZ4 max −1%, LZ4 fast −0.2% |
| Wikipedia, paragraphs | Romanian | Brotli 11 −31%, Deflate max −31%, Zstd 19 −27%, Zstd 1 −25%, Brotli 1 −23%, Deflate fast −13%, LZ4 max −9%, LZ4 fast −8% |
| Wikipedia, paragraphs | Lithuanian | Deflate max −32%, Brotli 11 −31%, Zstd 19 −28%, Zstd 1 −26%, Brotli 1 −24%, Deflate fast −13%, LZ4 max −10%, LZ4 fast −9% |
| Wikipedia, paragraphs | Latvian | Deflate max −29%, Brotli 11 −29%, Zstd 19 −24%, Zstd 1 −23%, Brotli 1 −21%, Deflate fast −10%, LZ4 max −7%, LZ4 fast −6% |
| Wikipedia, paragraphs | Croatian | Deflate max −32%, Brotli 11 −30%, Zstd 19 −27%, Zstd 1 −26%, Brotli 1 −25%, Deflate fast −12%, LZ4 max −8%, LZ4 fast −7% |
| Wikipedia, paragraphs | Slovenian | Deflate max −34%, Brotli 11 −33%, Zstd 19 −30%, Zstd 1 −28%, Brotli 1 −27%, Deflate fast −14%, LZ4 max −11%, LZ4 fast −10% |
| Wikipedia, paragraphs | Turkish | Deflate max −32%, Brotli 11 −32%, Zstd 19 −28%, Zstd 1 −26%, Brotli 1 −25%, Deflate fast −12%, LZ4 max −9%, LZ4 fast −7% |
| Wikipedia, paragraphs | Vietnamese | Brotli 11 −28%, Deflate max −26%, Zstd 19 −22%, Zstd 1 −18%, Brotli 1 −15%, Deflate fast −10%, LZ4 max −9%, LZ4 fast −7% |
| Wikipedia, paragraphs | Indonesian | Deflate max −42%, Brotli 11 −42%, Zstd 19 −39%, Zstd 1 −38%, Brotli 1 −36%, Deflate fast −22%, LZ4 max −18%, LZ4 fast −17% |
| Wikipedia, paragraphs | Russian | Brotli 11 −23%, Deflate max −9%, Zstd 19 −3% |
| Wikipedia, paragraphs | Ukrainian | Brotli 11 −17%, Deflate max −4% |
| Wikipedia, paragraphs | Bulgarian | Brotli 11 −24%, Deflate max −12%, Zstd 19 −7%, Zstd 1 −3% |
| Wikipedia, paragraphs | Serbian | Brotli 11 −24%, Deflate max −13%, Zstd 19 −8%, Zstd 1 −4%, Brotli 1 0.0% |
| Wikipedia, paragraphs | Kazakh | Brotli 11 −22%, Deflate max −13%, Zstd 19 −7%, Zstd 1 −2% |
| Wikipedia, paragraphs | Mongolian | Brotli 11 −26%, Deflate max −15%, Zstd 19 −10%, Zstd 1 −5%, Brotli 1 −2% |
| Wikipedia, paragraphs | Greek | Brotli 11 −21%, Deflate max −10%, Zstd 19 −5%, Zstd 1 −0.2% |
| Wikipedia, paragraphs | Armenian | Brotli 11 −27%, Deflate max −20%, Zstd 19 −15%, Zstd 1 −10%, Brotli 1 −5% |
| Wikipedia, paragraphs | Georgian | Brotli 11 −10%, Deflate max −5% |
| Wikipedia, paragraphs | Hebrew | Brotli 11 −22%, Deflate max −12%, Zstd 19 −7%, Zstd 1 −2% |
| Wikipedia, paragraphs | Arabic | Brotli 11 −24%, Deflate max −12%, Zstd 19 −6%, Zstd 1 −1% |
| Wikipedia, paragraphs | Persian | Brotli 11 −31%, Deflate max −25%, Zstd 19 −19%, Zstd 1 −15%, Brotli 1 −12% |
| Wikipedia, paragraphs | Urdu | Brotli 11 −40%, Deflate max −34%, Zstd 19 −30%, Zstd 1 −26%, Brotli 1 −23%, LZ4 max −7%, Deflate fast −6%, LZ4 fast −1% |
| Wikipedia, paragraphs | Hindi | Brotli 11 −17%, Deflate max −6%, Zstd 19 −2% |
| Wikipedia, paragraphs | Marathi | Brotli 11 −6% |
| Wikipedia, paragraphs | Nepali | Brotli 11 −9%, Deflate max −1% |
| Wikipedia, paragraphs | Bengali | Brotli 11 −12%, Deflate max −8%, Zstd 19 −5% |
| Wikipedia, paragraphs | Punjabi | Brotli 11 −13%, Deflate max −9%, Zstd 19 −6% |
| Wikipedia, paragraphs | Gujarati | Brotli 11 −8%, Deflate max −4%, Zstd 19 −0.4% |
| Wikipedia, paragraphs | Tamil | Brotli 11 −10%, Deflate max −6%, Zstd 19 −2% |
| Wikipedia, paragraphs | Telugu | Brotli 11 −12%, Deflate max −6%, Zstd 19 −4% |
| Wikipedia, paragraphs | Kannada | Brotli 11 −6%, Deflate max −2% |
| Wikipedia, paragraphs | Malayalam | Brotli 11 −12%, Deflate max −8%, Zstd 19 −5% |
| Wikipedia, paragraphs | Sinhala | Brotli 11 −2% |
| Wikipedia, paragraphs | Thai | Brotli 11 −1% |
| Wikipedia, paragraphs | Khmer | Brotli 11 −18%, Deflate max −7%, Zstd 19 −2% |
| Wikipedia, paragraphs | Burmese | Brotli 11 −18%, Deflate max −11%, Zstd 19 −9% |
| Wikipedia, paragraphs | Tibetan | Brotli 11 −11%, Deflate max −9%, Zstd 19 −4% |
| Wikipedia, paragraphs | Amharic | Brotli 11 −5% |
| Wikipedia, paragraphs | Korean | Brotli 11 −14%, Deflate max −9%, Zstd 19 −1% |
| Wikipedia, articles | English | Brotli 11 −69%, Zstd 19 −62%, Deflate max −60%, Zstd 1 −57%, Brotli 1 −54%, LZ4 max −49%, Deflate fast −43%, LZ4 fast −39% |
| Wikipedia, articles | French | Brotli 11 −64%, Zstd 19 −61%, Deflate max −60%, Zstd 1 −56%, Brotli 1 −53%, LZ4 max −48%, Deflate fast −40%, LZ4 fast −37% |
| Wikipedia, articles | German | Brotli 11 −62%, Zstd 19 −60%, Deflate max −59%, Zstd 1 −55%, Brotli 1 −53%, LZ4 max −47%, Deflate fast −41%, LZ4 fast −38% |
| Wikipedia, articles | Spanish | Brotli 11 −65%, Zstd 19 −62%, Deflate max −61%, Zstd 1 −57%, Brotli 1 −54%, LZ4 max −49%, Deflate fast −41%, LZ4 fast −38% |
| Wikipedia, articles | Portuguese | Brotli 11 −65%, Zstd 19 −63%, Deflate max −61%, Zstd 1 −58%, Brotli 1 −54%, LZ4 max −50%, Deflate fast −42%, LZ4 fast −39% |
| Wikipedia, articles | Italian | Brotli 11 −64%, Zstd 19 −62%, Deflate max −61%, Zstd 1 −58%, Brotli 1 −54%, LZ4 max −49%, Deflate fast −41%, LZ4 fast −38% |
| Wikipedia, articles | Dutch | Brotli 11 −63%, Zstd 19 −62%, Deflate max −60%, Zstd 1 −58%, Brotli 1 −54%, LZ4 max −48%, Deflate fast −42%, LZ4 fast −38% |
| Wikipedia, articles | Swedish | Brotli 11 −62%, Zstd 19 −60%, Deflate max −58%, Zstd 1 −55%, Brotli 1 −52%, LZ4 max −46%, Deflate fast −40%, LZ4 fast −36% |
| Wikipedia, articles | Danish | Brotli 11 −58%, Zstd 19 −55%, Deflate max −54%, Zstd 1 −52%, Brotli 1 −49%, LZ4 max −37%, Deflate fast −35%, LZ4 fast −30% |
| Wikipedia, articles | Finnish | Brotli 11 −61%, Zstd 19 −59%, Deflate max −58%, Zstd 1 −55%, Brotli 1 −52%, LZ4 max −44%, Deflate fast −38%, LZ4 fast −34% |
| Wikipedia, articles | Icelandic | Brotli 11 −53%, Zstd 19 −50%, Deflate max −49%, Zstd 1 −46%, Brotli 1 −43%, LZ4 max −29%, Deflate fast −28%, LZ4 fast −23% |
| Wikipedia, articles | Polish | Brotli 11 −59%, Zstd 19 −57%, Deflate max −56%, Zstd 1 −52%, Brotli 1 −49%, LZ4 max −42%, Deflate fast −36%, LZ4 fast −33% |
| Wikipedia, articles | Czech | Brotli 11 −56%, Zstd 19 −53%, Deflate max −52%, Zstd 1 −47%, Brotli 1 −45%, LZ4 max −37%, Deflate fast −32%, LZ4 fast −28% |
| Wikipedia, articles | Slovak | Brotli 11 −57%, Zstd 19 −55%, Deflate max −54%, Zstd 1 −50%, Brotli 1 −47%, LZ4 max −39%, Deflate fast −34%, LZ4 fast −30% |
| Wikipedia, articles | Hungarian | Brotli 11 −56%, Zstd 19 −54%, Deflate max −53%, Zstd 1 −48%, Brotli 1 −46%, LZ4 max −37%, Deflate fast −32%, LZ4 fast −28% |
| Wikipedia, articles | Romanian | Brotli 11 −62%, Zstd 19 −60%, Deflate max −58%, Zstd 1 −54%, Brotli 1 −51%, LZ4 max −45%, Deflate fast −39%, LZ4 fast −35% |
| Wikipedia, articles | Lithuanian | Brotli 11 −58%, Zstd 19 −56%, Deflate max −55%, Zstd 1 −51%, Brotli 1 −49%, LZ4 max −38%, Deflate fast −35%, LZ4 fast −31% |
| Wikipedia, articles | Latvian | Brotli 11 −59%, Zstd 19 −56%, Deflate max −55%, Zstd 1 −52%, Brotli 1 −49%, LZ4 max −40%, Deflate fast −35%, LZ4 fast −32% |
| Wikipedia, articles | Croatian | Brotli 11 −59%, Zstd 19 −56%, Deflate max −55%, Zstd 1 −52%, Brotli 1 −49%, LZ4 max −39%, Deflate fast −35%, LZ4 fast −31% |
| Wikipedia, articles | Slovenian | Brotli 11 −61%, Zstd 19 −59%, Deflate max −57%, Zstd 1 −54%, Brotli 1 −51%, LZ4 max −43%, Deflate fast −38%, LZ4 fast −34% |
| Wikipedia, articles | Turkish | Brotli 11 −60%, Zstd 19 −57%, Deflate max −56%, Zstd 1 −53%, Brotli 1 −49%, LZ4 max −41%, Deflate fast −36%, LZ4 fast −32% |
| Wikipedia, articles | Vietnamese | Brotli 11 −64%, Zstd 19 −62%, Deflate max −60%, Zstd 1 −55%, Brotli 1 −53%, LZ4 max −50%, Deflate fast −41%, LZ4 fast −37% |
| Wikipedia, articles | Indonesian | Brotli 11 −66%, Zstd 19 −64%, Deflate max −62%, Zstd 1 −60%, Brotli 1 −56%, LZ4 max −51%, Deflate fast −46%, LZ4 fast −41% |
| Wikipedia, articles | Russian | Brotli 11 −56%, Zstd 19 −53%, Deflate max −49%, Zstd 1 −41%, Brotli 1 −39%, LZ4 max −38%, Deflate fast −9%, LZ4 fast −2% |
| Wikipedia, articles | Ukrainian | Brotli 11 −55%, Zstd 19 −52%, Deflate max −49%, Zstd 1 −40%, Brotli 1 −38%, LZ4 max −37%, Deflate fast −9%, LZ4 fast −2% |
| Wikipedia, articles | Bulgarian | Brotli 11 −58%, Zstd 19 −55%, Deflate max −52%, Zstd 1 −43%, Brotli 1 −41%, LZ4 max −41%, Deflate fast −11%, LZ4 fast −3% |
| Wikipedia, articles | Serbian | Brotli 11 −54%, Zstd 19 −52%, Deflate max −48%, Zstd 1 −41%, Brotli 1 −38%, LZ4 max −37%, Deflate fast −9%, LZ4 fast −0.8% |
| Wikipedia, articles | Kazakh | Brotli 11 −59%, Zstd 19 −57%, Deflate max −54%, Zstd 1 −47%, Brotli 1 −43%, LZ4 max −43%, Deflate fast −20%, LZ4 fast −14% |
| Wikipedia, articles | Mongolian | Brotli 11 −54%, Zstd 19 −49%, Deflate max −47%, Zstd 1 −40%, Brotli 1 −34%, LZ4 max −31%, Deflate fast −14%, LZ4 fast −7% |
| Wikipedia, articles | Greek | Brotli 11 −55%, Zstd 19 −51%, Deflate max −48%, Zstd 1 −40%, LZ4 max −36%, Brotli 1 −36%, Deflate fast −10%, LZ4 fast −4% |
| Wikipedia, articles | Armenian | Brotli 11 −58%, Zstd 19 −56%, Deflate max −53%, Zstd 1 −44%, LZ4 max −43%, Brotli 1 −42%, Deflate fast −16%, LZ4 fast −8% |
| Wikipedia, articles | Georgian | Brotli 11 −44%, Zstd 19 −41%, Deflate max −39%, Zstd 1 −26%, Brotli 1 −21%, LZ4 max −19% |
| Wikipedia, articles | Hebrew | Brotli 11 −53%, Zstd 19 −50%, Deflate max −47%, Zstd 1 −41%, Brotli 1 −37%, LZ4 max −34%, Deflate fast −8% |
| Wikipedia, articles | Arabic | Brotli 11 −55%, Zstd 19 −52%, Deflate max −48%, Zstd 1 −41%, Brotli 1 −38%, LZ4 max −36%, Deflate fast −11%, LZ4 fast −3% |
| Wikipedia, articles | Persian | Brotli 11 −61%, Zstd 19 −60%, Deflate max −56%, Zstd 1 −50%, Brotli 1 −48%, LZ4 max −46%, Deflate fast −23%, LZ4 fast −16% |
| Wikipedia, articles | Urdu | Brotli 11 −64%, Zstd 19 −61%, Deflate max −59%, Zstd 1 −53%, Brotli 1 −50%, LZ4 max −48%, Deflate fast −31%, LZ4 fast −26% |
| Wikipedia, articles | Hindi | Brotli 11 −52%, Zstd 19 −49%, Deflate max −45%, LZ4 max −32%, Zstd 1 −30%, Brotli 1 −26% |
| Wikipedia, articles | Marathi | Brotli 11 −48%, Zstd 19 −45%, Deflate max −42%, Zstd 1 −27%, LZ4 max −26%, Brotli 1 −21% |
| Wikipedia, articles | Nepali | Brotli 11 −47%, Zstd 19 −44%, Deflate max −41%, Zstd 1 −26%, LZ4 max −23%, Brotli 1 −19% |
| Wikipedia, articles | Bengali | Brotli 11 −52%, Zstd 19 −51%, Deflate max −47%, LZ4 max −33%, Zstd 1 −31%, Brotli 1 −26%, Deflate fast −0.1% |
| Wikipedia, articles | Punjabi | Brotli 11 −50%, Zstd 19 −48%, Deflate max −45%, Zstd 1 −31%, LZ4 max −30%, Brotli 1 −26%, Deflate fast −2% |
| Wikipedia, articles | Gujarati | Brotli 11 −54%, Zstd 19 −53%, Deflate max −48%, LZ4 max −38%, Zstd 1 −31%, Brotli 1 −29% |
| Wikipedia, articles | Tamil | Brotli 11 −57%, Zstd 19 −56%, Deflate max −52%, LZ4 max −41%, Zstd 1 −33%, Brotli 1 −30%, Deflate fast −2% |
| Wikipedia, articles | Telugu | Brotli 11 −52%, Zstd 19 −50%, Deflate max −46%, LZ4 max −33%, Zstd 1 −30%, Brotli 1 −27% |
| Wikipedia, articles | Kannada | Brotli 11 −52%, Zstd 19 −51%, Deflate max −47%, LZ4 max −34%, Zstd 1 −30%, Brotli 1 −25% |
| Wikipedia, articles | Malayalam | Brotli 11 −51%, Zstd 19 −49%, Deflate max −45%, LZ4 max −33%, Zstd 1 −27%, Brotli 1 −22% |
| Wikipedia, articles | Sinhala | Brotli 11 −49%, Zstd 19 −45%, Deflate max −42%, Zstd 1 −28%, LZ4 max −27%, Brotli 1 −23% |
| Wikipedia, articles | Thai | Brotli 11 −47%, Zstd 19 −45%, Deflate max −41%, LZ4 max −27%, Zstd 1 −26%, Brotli 1 −20% |
| Wikipedia, articles | Lao | Brotli 11 −23%, Zstd 19 −16%, Deflate max −16%, Zstd 1 −5% |
| Wikipedia, articles | Khmer | Brotli 11 −51%, Zstd 19 −45%, Deflate max −42%, Zstd 1 −31%, Brotli 1 −27%, LZ4 max −26%, Deflate fast −7% |
| Wikipedia, articles | Burmese | Brotli 11 −56%, Zstd 19 −55%, Deflate max −50%, LZ4 max −39%, Zstd 1 −33%, Brotli 1 −29%, Deflate fast −9% |
| Wikipedia, articles | Tibetan | Brotli 11 −43%, Zstd 19 −41%, Deflate max −38%, Zstd 1 −26%, Brotli 1 −19%, LZ4 max −17%, Deflate fast −2% |
| Wikipedia, articles | Amharic | Brotli 11 −42%, Zstd 19 −36%, Deflate max −35%, Zstd 1 −30%, Brotli 1 −24%, LZ4 max −13%, Deflate fast −6% |
| Wikipedia, articles | Chinese | Brotli 11 −35%, Zstd 19 −29%, Deflate max −25%, Zstd 1 −15%, Brotli 1 −14%, LZ4 max −4% |
| Wikipedia, articles | Japanese | Brotli 11 −36%, Zstd 19 −33%, Deflate max −28%, Zstd 1 −17%, Brotli 1 −16%, LZ4 max −15% |
| Wikipedia, articles | Korean | Brotli 11 −50%, Zstd 19 −48%, Deflate max −46%, Zstd 1 −38%, Brotli 1 −37%, LZ4 max −32%, Deflate fast −19%, LZ4 fast −16% |

</details>

</details>
