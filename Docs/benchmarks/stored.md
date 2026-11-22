# Stored Values

**Over the whole UDHR in 47 languages, Steno is the smallest of the five per-value encodings at every length of value up to 16 characters. By script, it is the smallest in 12 of the 18 script groups on 16-character values and 10 on 4-character values; where another is smaller, it is BOCU-1 or UTF-16, by the margins in the scorecard below.**

The size of short values, such as names, titles, keys and fields, in Steno and in the other per-value encodings. Bytes per character, counting characters as UTF-16 code units (an emoji counts as 2); smaller is better. Each value is encoded on its own, as a database stores a field. In parentheses is the size against Steno: + is larger than Steno, − is smaller. Steno, UTF-8, UTF-16 and BOCU-1 give one encoding per text, so their sizes are the same in every correct implementation; SCSU leaves choices to the encoder, and its sizes are those of ICU's encoder.

## Size by Script, 16-Character Values (UDHR in 47 languages)

| Script | **Steno** | UTF-8 | UTF-16 | SCSU | BOCU-1 |
|---|--:|--:|--:|--:|--:|
| Latin | 1.01 | 1.06 (+4%) | 2.00 (+98%) | 1.02 (+0.9%) | 1.10 (+9%) |
| Vietnamese | 1.22 | 1.38 (+13%) | 2.00 (+64%) | 1.24 (+2%) | 1.44 (+18%) |
| Cyrillic | 1.09 | 1.85 (+70%) | 2.00 (+83%) | 1.11 (+1%) | 1.13 (+4%) |
| Greek | 1.07 | 1.84 (+73%) | 2.00 (+87%) | 1.13 (+6%) | 1.10 (+3%) |
| Greek (polytonic) | 1.27 | 1.97 (+55%) | 2.00 (+58%) | 1.38 (+9%) | 1.29 (+2%) |
| Armenian | 1.07 | 1.88 (+76%) | 2.00 (+87%) | 1.14 (+6%) | 1.37 (+28%) |
| Georgian | 1.13 | 2.75 (+143%) | 2.00 (+76%) | 1.13 (0%) | 1.09 (−3%) |
| Hebrew | 1.07 | 1.82 (+70%) | 2.00 (+87%) | 1.14 (+6%) | 1.11 (+4%) |
| Arabic script | 1.21 | 1.82 (+50%) | 2.00 (+65%) | 1.25 (+3%) | 1.24 (+3%) |
| Indic scripts | 1.07 | 2.73 (+155%) | 2.00 (+86%) | 1.12 (+5%) | 1.09 (+2%) |
| Thai and Lao | 1.07 | 2.92 (+174%) | 2.00 (+87%) | 1.13 (+6%) | 1.08 (+1%) |
| Khmer | 1.13 | 2.92 (+157%) | 2.00 (+76%) | 1.13 (0%) | 1.07 (−6%) |
| Burmese | 1.13 | 2.87 (+154%) | 2.00 (+77%) | 1.13 (0%) | 1.07 (−6%) |
| Tibetan | 1.21 | 2.97 (+146%) | 2.00 (+65%) | 1.27 (+5%) | 1.17 (−3%) |
| Ethiopic (Amharic) | 1.76 | 3.00 (+71%) | 2.00 (+14%) | 1.80 (+2%) | 1.71 (−3%) |
| Chinese | 2.01 | 2.96 (+47%) | 2.00 (−0.6%) | 2.09 (+4%) | 2.17 (+8%) |
| Japanese | 1.58 | 2.97 (+88%) | 2.00 (+26%) | 1.86 (+18%) | 2.14 (+35%) |
| Korean | 1.82 | 2.48 (+37%) | 2.00 (+10%) | 2.05 (+13%) | 1.91 (+5%) |

## Size by Script, 4-Character Values (UDHR in 47 languages)

| Script | **Steno** | UTF-8 | UTF-16 | SCSU | BOCU-1 |
|---|--:|--:|--:|--:|--:|
| Latin | 1.01 | 1.06 (+5%) | 2.00 (+98%) | 1.02 (+1%) | 1.09 (+8%) |
| Vietnamese | 1.32 | 1.42 (+8%) | 2.00 (+52%) | 1.36 (+3%) | 1.43 (+9%) |
| Cyrillic | 1.29 | 1.90 (+47%) | 2.00 (+55%) | 1.31 (+2%) | 1.32 (+2%) |
| Greek | 1.27 | 1.90 (+49%) | 2.00 (+57%) | 1.54 (+21%) | 1.29 (+2%) |
| Greek (polytonic) | 1.53 | 2.03 (+32%) | 2.00 (+31%) | 1.81 (+18%) | 1.44 (−6%) |
| Armenian | 1.27 | 1.92 (+52%) | 2.00 (+58%) | 1.54 (+21%) | 1.51 (+19%) |
| Georgian | 1.53 | 2.83 (+85%) | 2.00 (+31%) | 1.53 (0%) | 1.28 (−16%) |
| Hebrew | 1.27 | 1.88 (+47%) | 2.00 (+57%) | 1.55 (+21%) | 1.30 (+2%) |
| Arabic script | 1.41 | 1.88 (+34%) | 2.00 (+42%) | 1.51 (+7%) | 1.42 (+0.7%) |
| Indic scripts | 1.28 | 2.83 (+122%) | 2.00 (+57%) | 1.48 (+16%) | 1.29 (+1%) |
| Thai and Lao | 1.25 | 2.94 (+135%) | 2.00 (+59%) | 1.51 (+20%) | 1.26 (+0.5%) |
| Khmer | 1.52 | 2.95 (+94%) | 2.00 (+32%) | 1.52 (0%) | 1.26 (−17%) |
| Burmese | 1.52 | 2.92 (+92%) | 2.00 (+31%) | 1.52 (0%) | 1.26 (−17%) |
| Tibetan | 1.59 | 2.98 (+87%) | 2.00 (+26%) | 1.66 (+4%) | 1.34 (−16%) |
| Ethiopic (Amharic) | 2.02 | 3.00 (+49%) | 2.00 (−1%) | 2.38 (+18%) | 1.77 (−13%) |
| Chinese | 2.21 | 2.96 (+34%) | 2.00 (−9%) | 2.27 (+3%) | 2.32 (+5%) |
| Japanese | 1.94 | 2.97 (+53%) | 2.00 (+3%) | 2.04 (+5%) | 2.30 (+18%) |
| Korean | 2.10 | 2.63 (+25%) | 2.00 (−5%) | 2.25 (+7%) | 2.17 (+3%) |

## Size by Script, 16-Character Values (Wikipedia in 56 languages)

| Script | **Steno** | UTF-8 | UTF-16 | SCSU | BOCU-1 |
|---|--:|--:|--:|--:|--:|
| Latin | 1.01 | 1.04 (+3%) | 2.00 (+98%) | 1.01 (+0.5%) | 1.07 (+6%) |
| Vietnamese | 1.17 | 1.31 (+11%) | 2.00 (+70%) | 1.20 (+2%) | 1.37 (+16%) |
| Cyrillic | 1.08 | 1.81 (+68%) | 2.00 (+85%) | 1.09 (+1%) | 1.13 (+5%) |
| Greek | 1.06 | 1.79 (+68%) | 2.00 (+88%) | 1.13 (+6%) | 1.10 (+3%) |
| Armenian | 1.07 | 1.84 (+72%) | 2.00 (+87%) | 1.13 (+6%) | 1.35 (+26%) |
| Georgian | 1.13 | 2.55 (+126%) | 2.00 (+77%) | 1.13 (0.0%) | 1.12 (−1%) |
| Hebrew | 1.07 | 1.79 (+68%) | 2.00 (+87%) | 1.13 (+6%) | 1.12 (+5%) |
| Arabic script | 1.18 | 1.81 (+53%) | 2.00 (+70%) | 1.22 (+3%) | 1.23 (+4%) |
| Indic scripts | 1.07 | 2.64 (+147%) | 2.00 (+87%) | 1.12 (+5%) | 1.10 (+3%) |
| Thai and Lao | 1.06 | 2.78 (+161%) | 2.00 (+88%) | 1.13 (+6%) | 1.08 (+1%) |
| Khmer | 1.09 | 2.18 (+99%) | 2.00 (+83%) | 1.09 (0.0%) | 1.06 (−3%) |
| Burmese | 1.13 | 2.72 (+141%) | 2.00 (+77%) | 1.13 (0.0%) | 1.08 (−4%) |
| Tibetan | 1.21 | 2.91 (+141%) | 2.00 (+66%) | 1.27 (+5%) | 1.18 (−2%) |
| Ethiopic (Amharic) | 1.50 | 2.38 (+58%) | 2.00 (+33%) | 1.57 (+5%) | 1.48 (−2%) |
| Chinese | 1.84 | 2.67 (+45%) | 2.00 (+9%) | 1.94 (+5%) | 2.06 (+12%) |
| Japanese | 1.48 | 2.77 (+87%) | 2.00 (+35%) | 1.65 (+11%) | 1.86 (+25%) |
| Korean | 1.74 | 2.34 (+34%) | 2.00 (+15%) | 1.98 (+13%) | 1.85 (+6%) |

## Size by Script, 4-Character Values (Wikipedia in 56 languages)

| Script | **Steno** | UTF-8 | UTF-16 | SCSU | BOCU-1 |
|---|--:|--:|--:|--:|--:|
| Latin | 1.01 | 1.04 (+3%) | 2.00 (+98%) | 1.02 (+0.7%) | 1.06 (+5%) |
| Vietnamese | 1.24 | 1.34 (+8%) | 2.00 (+62%) | 1.28 (+3%) | 1.36 (+10%) |
| Cyrillic | 1.28 | 1.86 (+46%) | 2.00 (+57%) | 1.29 (+0.8%) | 1.30 (+2%) |
| Greek | 1.25 | 1.84 (+46%) | 2.00 (+59%) | 1.51 (+20%) | 1.27 (+2%) |
| Armenian | 1.26 | 1.88 (+49%) | 2.00 (+59%) | 1.52 (+20%) | 1.48 (+17%) |
| Georgian | 1.49 | 2.62 (+76%) | 2.00 (+35%) | 1.49 (0.0%) | 1.27 (−14%) |
| Hebrew | 1.27 | 1.84 (+46%) | 2.00 (+58%) | 1.53 (+21%) | 1.30 (+2%) |
| Arabic script | 1.37 | 1.87 (+36%) | 2.00 (+45%) | 1.45 (+6%) | 1.39 (+1%) |
| Indic scripts | 1.26 | 2.73 (+116%) | 2.00 (+58%) | 1.47 (+16%) | 1.28 (+2%) |
| Thai and Lao | 1.24 | 2.81 (+127%) | 2.00 (+61%) | 1.48 (+19%) | 1.25 (+0.6%) |
| Khmer | 1.33 | 2.22 (+66%) | 2.00 (+50%) | 1.33 (0.0%) | 1.18 (−12%) |
| Burmese | 1.49 | 2.77 (+86%) | 2.00 (+34%) | 1.49 (0.0%) | 1.25 (−16%) |
| Tibetan | 1.58 | 2.92 (+85%) | 2.00 (+27%) | 1.64 (+4%) | 1.34 (−15%) |
| Ethiopic (Amharic) | 1.79 | 2.48 (+38%) | 2.00 (+11%) | 2.02 (+13%) | 1.57 (−12%) |
| Chinese | 2.03 | 2.68 (+32%) | 2.00 (−1%) | 2.09 (+3%) | 2.18 (+8%) |
| Japanese | 1.80 | 2.78 (+54%) | 2.00 (+11%) | 1.79 (−0.5%) | 2.04 (+13%) |
| Korean | 2.00 | 2.47 (+24%) | 2.00 (+0.1%) | 2.12 (+6%) | 2.07 (+4%) |

## Scorecard by Script

Where Steno is the smallest of the five per-value encodings, tied with another (within 0.5%), or behind one, over the UDHR in 47 languages:

| Script | 4-character values | 16-character values |
|---|---|---|
| Latin | Smallest | Smallest |
| Vietnamese | Smallest | Smallest |
| Cyrillic | Smallest | Smallest |
| Greek | Smallest | Smallest |
| Greek (polytonic) | Behind BOCU-1 (−6%) | Smallest |
| Armenian | Smallest | Smallest |
| Georgian | Behind BOCU-1 (−16%) | Behind BOCU-1 (−3%) |
| Hebrew | Smallest | Smallest |
| Arabic script | Smallest | Smallest |
| Indic scripts | Smallest | Smallest |
| Thai and Lao | Smallest | Smallest |
| Khmer | Behind BOCU-1 (−17%) | Behind BOCU-1 (−6%) |
| Burmese | Behind BOCU-1 (−17%) | Behind BOCU-1 (−6%) |
| Tibetan | Behind BOCU-1 (−16%) | Behind BOCU-1 (−3%) |
| Ethiopic (Amharic) | Behind BOCU-1 (−13%), UTF-16 (−1%) | Behind BOCU-1 (−3%) |
| Chinese | Behind UTF-16 (−9%) | Behind UTF-16 (−0.6%) |
| Japanese | Smallest | Smallest |
| Korean | Behind UTF-16 (−5%) | Smallest |

## Values per 8 KB Page

How many 16-character values fit in 8 KB, by script, over the UDHR in 47 languages; more is better. Calculated from the measured sizes ([how](../articles/benchmarks/machine.md#derived-figures)).

| Script | **Steno** | UTF-8 | UTF-16 | SCSU | BOCU-1 |
|---|--:|--:|--:|--:|--:|
| Latin | 544 | 522 | 275 | 539 | 500 |
| Vietnamese | 461 | 408 | 281 | 452 | 391 |
| Cyrillic | 507 | 299 | 277 | 501 | 489 |
| Greek | 518 | 299 | 276 | 487 | 504 |
| Greek (polytonic) | 434 | 280 | 275 | 399 | 427 |
| Armenian | 518 | 293 | 276 | 487 | 404 |
| Georgian | 483 | 199 | 273 | 483 | 500 |
| Hebrew | 536 | 314 | 286 | 504 | 517 |
| Arabic script | 465 | 310 | 281 | 451 | 453 |
| Indic scripts | 513 | 201 | 275 | 490 | 503 |
| Thai and Lao | 515 | 188 | 274 | 485 | 510 |
| Khmer | 483 | 188 | 274 | 483 | 514 |
| Burmese | 476 | 187 | 269 | 476 | 505 |
| Tibetan | 448 | 182 | 271 | 427 | 463 |
| Ethiopic (Amharic) | 328 | 192 | 288 | 321 | 338 |
| Chinese | 332 | 226 | 334 | 319 | 307 |
| Japanese | 387 | 206 | 307 | 329 | 287 |
| Korean | 338 | 247 | 307 | 299 | 321 |
