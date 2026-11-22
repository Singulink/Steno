# Encoded Size

**Steno is the smallest of the 13 codecs over 8 of the 13 groups of values: every group of values of up to 16 characters, the mixed lines and the names and UI strings. Over the others (UDHR, paragraphs; UDHR, documents; Mixed content, documents; Wikipedia, paragraphs; Wikipedia, articles), the stronger compressors are smaller.**

Bytes per character, counting characters as UTF-16 code units (an emoji counts as 2); smaller is better. Each value is encoded on its own, as a database stores a field. In parentheses is the size against Steno: + is larger than Steno, − is smaller. The compressors compress the UTF-8 of each value by itself, with nothing shared between values. Steno, UTF-8, UTF-16 and BOCU-1 give one encoding per text, so their sizes are the same in every correct implementation; SCSU leaves choices to the encoder, and its sizes are those of ICU's encoder.

## Built-in Corpus

The UDHR in 47 languages, 23 files of mixed content, and names, places, titles and UI strings.

| Codec | 4 chars | 8 chars | 16 chars | Paragraphs | Documents | Mixed, lines | Mixed, documents | Names and UI strings |
|---|--:|--:|--:|--:|--:|--:|--:|--:|
| **Steno** | 1.26 | 1.16 | 1.11 | 1.06 | 1.05 | 1.17 | 1.16 | 1.15 |
| UTF-8 | 1.97 (+56%) | 1.94 (+67%) | 1.92 (+73%) | 1.91 (+80%) | 1.90 (+81%) | 1.66 (+42%) | 1.65 (+42%) | 1.67 (+45%) |
| UTF-16 | 2.00 (+58%) | 2.00 (+72%) | 2.00 (+80%) | 2.00 (+89%) | 2.00 (+90%) | 2.00 (+70%) | 2.00 (+72%) | 2.00 (+74%) |
| SCSU | 1.36 (+7%) | 1.21 (+5%) | 1.14 (+3%) | 1.07 (+0.9%) | 1.06 (+0.6%) | 1.24 (+6%) | 1.22 (+6%) | 1.18 (+2%) |
| BOCU-1 | 1.28 (+1%) | 1.20 (+4%) | 1.17 (+5%) | 1.13 (+7%) | 1.13 (+8%) | 1.30 (+11%) | 1.30 (+12%) | 1.25 (+9%) |
| Deflate fast | 2.60 (+106%) | 2.25 (+94%) | 2.02 (+82%) | 1.36 (+28%) | 0.81 (−23%) | 1.65 (+41%) | 1.35 (+16%) | 1.76 (+53%) |
| Deflate max | 2.61 (+106%) | 2.26 (+95%) | 1.96 (+77%) | 1.02 (−4%) | 0.47 (−56%) | 1.55 (+32%) | 1.04 (−10%) | 1.70 (+47%) |
| Brotli 1 | 3.05 (+141%) | 2.47 (+113%) | 2.19 (+98%) | 1.20 (+14%) | 0.62 (−41%) | 1.71 (+46%) | 1.17 (+0.6%) | 1.88 (+63%) |
| Brotli 11 | 3.04 (+141%) | 2.45 (+111%) | 2.04 (+84%) | 0.96 (−9%) | 0.42 (−60%) | 1.48 (+26%) | 0.89 (−24%) | 1.75 (+52%) |
| LZ4 fast | 2.24 (+77%) | 2.11 (+82%) | 1.98 (+79%) | 1.44 (+36%) | 0.90 (−14%) | 1.63 (+39%) | 1.39 (+20%) | 1.71 (+49%) |
| LZ4 max | 2.24 (+77%) | 2.11 (+82%) | 1.98 (+79%) | 1.36 (+29%) | 0.62 (−42%) | 1.63 (+39%) | 1.35 (+16%) | 1.71 (+49%) |
| Zstd 1 | 4.40 (+248%) | 3.14 (+170%) | 2.52 (+127%) | 1.16 (+10%) | 0.56 (−47%) | 1.72 (+47%) | 1.14 (−2%) | 2.11 (+83%) |
| Zstd 19 | 4.40 (+248%) | 3.14 (+170%) | 2.41 (+118%) | 1.10 (+4%) | 0.44 (−58%) | 1.68 (+43%) | 1.08 (−7%) | 2.06 (+78%) |

The groups of values:

| Group | Values | Characters |
|---|--:|--:|
| UDHR, 4-char values | 121,285 | 448,581 |
| UDHR, 8-char values | 61,793 | 463,755 |
| UDHR, 16-char values | 31,947 | 471,559 |
| UDHR, paragraphs | 4,306 | 478,823 |
| UDHR, documents | 47 | 483,082 |
| Mixed content, lines | 282 | 14,140 |
| Mixed content, documents | 23 | 14,399 |
| Names and UI strings | 151 | 2,777 |

## Wikipedia

388 articles in 56 languages, fetched on 2026-11-20.

| Codec | 4 chars | 8 chars | 16 chars | Paragraphs | Articles |
|---|--:|--:|--:|--:|--:|
| **Steno** | 1.18 | 1.11 | 1.08 | 1.04 | 1.04 |
| UTF-8 | 1.63 (+38%) | 1.61 (+45%) | 1.60 (+48%) | 1.59 (+52%) | 1.58 (+52%) |
| UTF-16 | 2.00 (+69%) | 2.00 (+80%) | 2.00 (+86%) | 2.00 (+92%) | 2.00 (+92%) |
| SCSU | 1.24 (+5%) | 1.15 (+3%) | 1.10 (+2%) | 1.05 (+1%) | 1.05 (+1%) |
| BOCU-1 | 1.23 (+4%) | 1.17 (+5%) | 1.14 (+6%) | 1.12 (+7%) | 1.12 (+8%) |
| Deflate fast | 2.23 (+89%) | 1.92 (+73%) | 1.74 (+62%) | 1.13 (+8%) | 0.80 (−23%) |
| Deflate max | 2.23 (+89%) | 1.93 (+73%) | 1.70 (+58%) | 0.83 (−21%) | 0.49 (−53%) |
| Brotli 1 | 2.71 (+130%) | 2.13 (+92%) | 1.86 (+73%) | 0.96 (−8%) | 0.60 (−42%) |
| Brotli 11 | 2.71 (+130%) | 2.12 (+91%) | 1.80 (+67%) | 0.77 (−26%) | 0.44 (−58%) |
| LZ4 fast | 1.90 (+61%) | 1.77 (+60%) | 1.70 (+58%) | 1.19 (+14%) | 0.86 (−17%) |
| LZ4 max | 1.90 (+61%) | 1.77 (+60%) | 1.70 (+58%) | 1.12 (+8%) | 0.63 (−40%) |
| Zstd 1 | 4.07 (+244%) | 2.79 (+151%) | 2.19 (+103%) | 0.92 (−12%) | 0.57 (−46%) |
| Zstd 19 | 4.07 (+244%) | 2.79 (+151%) | 2.14 (+99%) | 0.88 (−16%) | 0.47 (−55%) |

The groups of values:

| Group | Values | Characters |
|---|--:|--:|
| Wikipedia, 4-char values | 1,465,669 | 5,416,728 |
| Wikipedia, 8-char values | 739,701 | 5,621,282 |
| Wikipedia, 16-char values | 376,774 | 5,723,076 |
| Wikipedia, paragraphs | 27,744 | 5,821,370 |
| Wikipedia, articles | 388 | 5,848,726 |

