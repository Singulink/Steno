# Mixed Content, Lines

Source: 23 files of mixed content, in the repository. Bytes per character, counting characters as UTF-16 code units (an emoji counts as 2); smaller is better. Each value is encoded on its own, as a database stores a field. The second table gives the size of each codec against Steno: + is larger than Steno, − is smaller. Steno, UTF-8, UTF-16 and BOCU-1 give one encoding per text, so their sizes are the same in every correct implementation; SCSU leaves choices to the encoder, and its sizes are those of ICU's encoder.

| Text | Values | Chars | **Steno** | UTF-8 | UTF-16 | SCSU | BOCU-1 | Deflate fast | Deflate max | Brotli 1 | Brotli 11 | LZ4 fast | LZ4 max | Zstd 1 | Zstd 19 |
|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| Bilingual Arabic and English | 7 | 505 | 1.02 | 1.53 | 2.00 | 1.02 | 1.10 | 1.50 | 1.39 | 1.58 | 1.23 | 1.47 | 1.47 | 1.51 | 1.45 |
| Arabic chat with emoji | 17 | 531 | 1.08 | 1.82 | 2.00 | 1.12 | 1.12 | 1.91 | 1.75 | 1.94 | 1.70 | 1.84 | 1.84 | 1.99 | 1.93 |
| English chat with emoji | 17 | 602 | 1.04 | 1.10 | 2.00 | 1.07 | 1.06 | 1.12 | 1.12 | 1.21 | 1.18 | 1.13 | 1.13 | 1.34 | 1.34 |
| Spanish chat with emoji | 18 | 713 | 1.06 | 1.12 | 2.00 | 1.07 | 1.12 | 1.15 | 1.13 | 1.22 | 1.21 | 1.15 | 1.15 | 1.34 | 1.33 |
| Hindi chat with emoji | 17 | 646 | 1.07 | 2.46 | 2.00 | 1.10 | 1.14 | 2.17 | 1.93 | 2.33 | 1.77 | 2.18 | 2.17 | 2.17 | 2.08 |
| Japanese chat with emoji | 14 | 263 | 1.47 | 2.52 | 2.00 | 1.66 | 1.84 | 2.77 | 2.76 | 2.73 | 2.70 | 2.62 | 2.62 | 3.00 | 3.00 |
| Korean chat with emoji | 17 | 357 | 1.67 | 2.24 | 2.00 | 2.03 | 1.83 | 2.45 | 2.41 | 2.43 | 2.39 | 2.30 | 2.30 | 2.65 | 2.65 |
| Russian chat with emoji | 17 | 636 | 1.07 | 1.80 | 2.00 | 1.10 | 1.16 | 1.87 | 1.69 | 1.87 | 1.63 | 1.80 | 1.80 | 1.88 | 1.82 |
| Chinese chat with emoji | 17 | 245 | 1.87 | 2.77 | 2.00 | 2.04 | 2.10 | 3.07 | 3.02 | 3.05 | 3.02 | 2.87 | 2.87 | 3.40 | 3.40 |
| English with typographic punctuation | 9 | 770 | 1.05 | 1.11 | 2.00 | 1.04 | 1.12 | 1.10 | 1.05 | 1.15 | 1.01 | 1.12 | 1.12 | 1.17 | 1.15 |
| Hindi and English mixed | 8 | 537 | 1.02 | 1.83 | 2.00 | 1.02 | 1.13 | 1.73 | 1.56 | 1.87 | 1.23 | 1.76 | 1.75 | 1.68 | 1.64 |
| Greek HTML | 12 | 809 | 1.03 | 1.43 | 2.00 | 1.04 | 1.09 | 1.38 | 1.25 | 1.39 | 1.12 | 1.37 | 1.37 | 1.38 | 1.35 |
| JSON catalog | 7 | 1,320 | 1.05 | 1.24 | 2.00 | 1.07 | 1.08 | 1.10 | 1.04 | 1.20 | 0.97 | 1.15 | 1.14 | 1.16 | 1.06 |
| Math and science notation | 10 | 706 | 1.11 | 1.20 | 2.00 | 1.11 | 1.22 | 1.11 | 1.08 | 1.26 | 1.22 | 1.17 | 1.17 | 1.32 | 1.26 |
| UI strings in many languages | 24 | 854 | 1.14 | 1.76 | 2.00 | 1.19 | 1.30 | 1.71 | 1.61 | 1.85 | 1.55 | 1.72 | 1.72 | 1.87 | 1.81 |
| Japanese news | 8 | 395 | 1.50 | 2.70 | 2.00 | 1.81 | 2.16 | 2.77 | 2.49 | 2.78 | 2.26 | 2.68 | 2.67 | 2.74 | 2.66 |
| Korean news | 8 | 505 | 1.59 | 2.14 | 2.00 | 1.93 | 1.86 | 2.21 | 1.99 | 2.20 | 1.78 | 2.11 | 2.11 | 2.11 | 2.05 |
| Chinese news | 8 | 343 | 1.76 | 2.67 | 2.00 | 1.99 | 2.26 | 2.84 | 2.70 | 2.77 | 2.42 | 2.68 | 2.68 | 2.88 | 2.85 |
| Social media posts | 15 | 753 | 1.16 | 1.52 | 2.00 | 1.22 | 1.26 | 1.51 | 1.44 | 1.59 | 1.45 | 1.51 | 1.51 | 1.64 | 1.60 |
| Japanese technical text with English | 8 | 640 | 1.19 | 1.80 | 2.00 | 1.23 | 1.36 | 1.74 | 1.69 | 1.85 | 1.59 | 1.73 | 1.72 | 1.83 | 1.77 |
| Korean technical text with English | 8 | 627 | 1.32 | 1.62 | 2.00 | 1.48 | 1.43 | 1.65 | 1.60 | 1.67 | 1.55 | 1.60 | 1.60 | 1.71 | 1.66 |
| Russian technical text with English | 8 | 851 | 1.01 | 1.47 | 2.00 | 1.01 | 1.06 | 1.43 | 1.29 | 1.48 | 1.16 | 1.43 | 1.43 | 1.35 | 1.30 |
| Chinese technical text with English | 8 | 532 | 1.26 | 1.55 | 2.00 | 1.38 | 1.42 | 1.62 | 1.60 | 1.61 | 1.53 | 1.56 | 1.56 | 1.67 | 1.67 |
| **Total** | 282 | 14,140 | 1.17 | 1.66 | 2.00 | 1.24 | 1.30 | 1.65 | 1.55 | 1.71 | 1.48 | 1.63 | 1.63 | 1.72 | 1.68 |

Against Steno (+ is larger than Steno, − is smaller):

| Text | UTF-8 | UTF-16 | SCSU | BOCU-1 | Deflate fast | Deflate max | Brotli 1 | Brotli 11 | LZ4 fast | LZ4 max | Zstd 1 | Zstd 19 |
|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| Bilingual Arabic and English | +51% | +96% | 0% | +8% | +47% | +36% | +55% | +21% | +44% | +44% | +48% | +42% |
| Arabic chat with emoji | +68% | +85% | +3% | +3% | +77% | +61% | +79% | +57% | +70% | +70% | +84% | +78% |
| English chat with emoji | +6% | +93% | +3% | +2% | +8% | +8% | +16% | +14% | +9% | +9% | +29% | +29% |
| Spanish chat with emoji | +6% | +89% | +0.9% | +6% | +9% | +7% | +15% | +14% | +9% | +9% | +27% | +26% |
| Hindi chat with emoji | +130% | +87% | +3% | +7% | +103% | +80% | +119% | +66% | +104% | +103% | +103% | +94% |
| Japanese chat with emoji | +71% | +36% | +13% | +25% | +88% | +88% | +86% | +84% | +78% | +78% | +104% | +104% |
| Korean chat with emoji | +34% | +20% | +22% | +10% | +47% | +45% | +46% | +43% | +38% | +38% | +59% | +59% |
| Russian chat with emoji | +68% | +87% | +3% | +8% | +74% | +58% | +74% | +52% | +68% | +68% | +75% | +70% |
| Chinese chat with emoji | +48% | +7% | +9% | +12% | +64% | +61% | +63% | +61% | +53% | +53% | +81% | +81% |
| English with typographic punctuation | +5% | +90% | −1% | +6% | +4% | 0% | +9% | −4% | +6% | +6% | +11% | +10% |
| Hindi and English mixed | +80% | +97% | 0% | +11% | +71% | +54% | +84% | +21% | +73% | +72% | +65% | +62% |
| Greek HTML | +39% | +95% | +1% | +6% | +34% | +21% | +35% | +9% | +34% | +34% | +34% | +32% |
| JSON catalog | +18% | +90% | +1% | +2% | +5% | −2% | +14% | −8% | +9% | +8% | +10% | +0.4% |
| Math and science notation | +8% | +79% | −0.3% | +10% | 0% | −3% | +13% | +10% | +5% | +5% | +18% | +13% |
| UI strings in many languages | +55% | +76% | +5% | +15% | +50% | +42% | +62% | +37% | +51% | +51% | +64% | +59% |
| Japanese news | +80% | +33% | +20% | +44% | +85% | +66% | +85% | +51% | +79% | +78% | +83% | +78% |
| Korean news | +35% | +26% | +22% | +17% | +39% | +25% | +39% | +12% | +33% | +33% | +33% | +29% |
| Chinese news | +52% | +14% | +13% | +28% | +61% | +53% | +57% | +38% | +52% | +52% | +64% | +62% |
| Social media posts | +31% | +73% | +6% | +9% | +30% | +24% | +38% | +25% | +30% | +30% | +42% | +38% |
| Japanese technical text with English | +51% | +68% | +3% | +14% | +46% | +41% | +55% | +34% | +45% | +44% | +53% | +49% |
| Korean technical text with English | +22% | +51% | +12% | +8% | +25% | +21% | +26% | +17% | +21% | +21% | +30% | +26% |
| Russian technical text with English | +46% | +97% | 0% | +5% | +42% | +27% | +46% | +15% | +41% | +41% | +33% | +28% |
| Chinese technical text with English | +23% | +59% | +10% | +13% | +29% | +27% | +28% | +22% | +24% | +24% | +33% | +33% |
| **Total** | +42% | +70% | +6% | +11% | +41% | +32% | +46% | +26% | +39% | +39% | +47% | +43% |
