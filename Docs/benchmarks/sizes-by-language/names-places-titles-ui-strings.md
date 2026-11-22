# Names, Places, Titles, UI Strings

Source: names of people and places, titles and UI strings, in the repository. Bytes per character, counting characters as UTF-16 code units (an emoji counts as 2); smaller is better. Each value is encoded on its own, as a database stores a field. The second table gives the size of each codec against Steno: + is larger than Steno, − is smaller. Steno, UTF-8, UTF-16 and BOCU-1 give one encoding per text, so their sizes are the same in every correct implementation; SCSU leaves choices to the encoder, and its sizes are those of ICU's encoder.

| Text | Values | Chars | **Steno** | UTF-8 | UTF-16 | SCSU | BOCU-1 | Deflate fast | Deflate max | Brotli 1 | Brotli 11 | LZ4 fast | LZ4 max | Zstd 1 | Zstd 19 |
|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| Names of people | 44 | 494 | 1.16 | 1.82 | 2.00 | 1.18 | 1.26 | 2.03 | 1.99 | 2.18 | 2.12 | 1.93 | 1.93 | 2.62 | 2.56 |
| Places and addresses | 37 | 806 | 1.16 | 1.76 | 2.00 | 1.19 | 1.28 | 1.82 | 1.75 | 1.92 | 1.77 | 1.77 | 1.77 | 2.12 | 2.06 |
| Titles | 34 | 1,009 | 1.11 | 1.64 | 2.00 | 1.14 | 1.21 | 1.69 | 1.59 | 1.76 | 1.57 | 1.65 | 1.65 | 1.85 | 1.79 |
| UI strings | 36 | 468 | 1.21 | 1.43 | 2.00 | 1.22 | 1.29 | 1.53 | 1.54 | 1.74 | 1.70 | 1.51 | 1.51 | 2.11 | 2.10 |
| **Total** | 151 | 2,777 | 1.15 | 1.67 | 2.00 | 1.18 | 1.25 | 1.76 | 1.70 | 1.88 | 1.75 | 1.71 | 1.71 | 2.11 | 2.06 |

Against Steno (+ is larger than Steno, − is smaller):

| Text | UTF-8 | UTF-16 | SCSU | BOCU-1 | Deflate fast | Deflate max | Brotli 1 | Brotli 11 | LZ4 fast | LZ4 max | Zstd 1 | Zstd 19 |
|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| Names of people | +57% | +72% | +2% | +9% | +75% | +71% | +88% | +83% | +66% | +66% | +125% | +120% |
| Places and addresses | +51% | +72% | +3% | +10% | +57% | +51% | +66% | +53% | +52% | +52% | +83% | +77% |
| Titles | +47% | +79% | +2% | +9% | +51% | +42% | +58% | +41% | +48% | +48% | +66% | +60% |
| UI strings | +19% | +66% | +1% | +7% | +26% | +27% | +44% | +41% | +25% | +25% | +75% | +74% |
| **Total** | +45% | +74% | +2% | +9% | +53% | +47% | +63% | +52% | +49% | +49% | +83% | +78% |
