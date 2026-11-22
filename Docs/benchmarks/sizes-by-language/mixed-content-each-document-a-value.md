# Mixed Content, Documents

Source: 23 files of mixed content, in the repository. Bytes per character, counting characters as UTF-16 code units (an emoji counts as 2); smaller is better. Each value is encoded on its own, as a database stores a field. The second table gives the size of each codec against Steno: + is larger than Steno, − is smaller. Steno, UTF-8, UTF-16 and BOCU-1 give one encoding per text, so their sizes are the same in every correct implementation; SCSU leaves choices to the encoder, and its sizes are those of ICU's encoder.

| Text | Values | Chars | **Steno** | UTF-8 | UTF-16 | SCSU | BOCU-1 | Deflate fast | Deflate max | Brotli 1 | Brotli 11 | LZ4 fast | LZ4 max | Zstd 1 | Zstd 19 |
|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| Bilingual Arabic and English | 1 | 511 | 1.01 | 1.53 | 2.00 | 1.01 | 1.09 | 1.24 | 0.97 | 1.08 | 0.75 | 1.27 | 1.25 | 1.05 | 1.00 |
| Arabic chat with emoji | 1 | 547 | 1.05 | 1.79 | 2.00 | 1.07 | 1.12 | 1.43 | 0.98 | 1.12 | 0.91 | 1.48 | 1.42 | 1.11 | 1.05 |
| English chat with emoji | 1 | 618 | 1.02 | 1.09 | 2.00 | 1.03 | 1.06 | 0.96 | 0.74 | 0.81 | 0.64 | 1.01 | 0.99 | 0.79 | 0.77 |
| Spanish chat with emoji | 1 | 730 | 1.06 | 1.11 | 2.00 | 1.05 | 1.12 | 0.97 | 0.75 | 0.84 | 0.71 | 1.02 | 1.01 | 0.83 | 0.79 |
| Hindi chat with emoji | 1 | 662 | 1.04 | 2.42 | 2.00 | 1.06 | 1.14 | 1.43 | 0.97 | 1.17 | 0.88 | 1.57 | 1.34 | 1.08 | 0.99 |
| Japanese chat with emoji | 1 | 276 | 1.37 | 2.45 | 2.00 | 1.61 | 1.80 | 2.26 | 1.83 | 2.00 | 1.65 | 2.25 | 2.21 | 1.96 | 1.91 |
| Korean chat with emoji | 1 | 373 | 1.59 | 2.18 | 2.00 | 1.97 | 1.79 | 2.00 | 1.45 | 1.61 | 1.38 | 1.99 | 1.97 | 1.60 | 1.55 |
| Russian chat with emoji | 1 | 652 | 1.04 | 1.78 | 2.00 | 1.06 | 1.15 | 1.35 | 0.94 | 1.04 | 0.83 | 1.38 | 1.33 | 1.04 | 0.98 |
| Chinese chat with emoji | 1 | 261 | 1.74 | 2.66 | 2.00 | 1.90 | 2.03 | 2.52 | 1.87 | 2.16 | 1.76 | 2.49 | 2.48 | 2.16 | 1.98 |
| English with typographic punctuation | 1 | 778 | 1.05 | 1.10 | 2.00 | 1.04 | 1.12 | 0.99 | 0.76 | 0.82 | 0.61 | 1.03 | 1.02 | 0.80 | 0.78 |
| Hindi and English mixed | 1 | 544 | 1.00 | 1.82 | 2.00 | 1.00 | 1.13 | 1.29 | 0.99 | 1.16 | 0.69 | 1.38 | 1.28 | 1.08 | 1.03 |
| Greek HTML | 1 | 820 | 1.02 | 1.42 | 2.00 | 1.03 | 1.09 | 0.98 | 0.75 | 0.87 | 0.64 | 1.04 | 1.01 | 0.84 | 0.79 |
| JSON catalog | 1 | 1,326 | 1.06 | 1.24 | 2.00 | 1.07 | 1.08 | 0.87 | 0.71 | 0.80 | 0.63 | 0.91 | 0.86 | 0.77 | 0.72 |
| Math and science notation | 1 | 715 | 1.10 | 1.20 | 2.00 | 1.09 | 1.22 | 1.03 | 0.85 | 0.94 | 0.75 | 1.09 | 1.08 | 0.92 | 0.87 |
| UI strings in many languages | 1 | 877 | 1.15 | 1.74 | 2.00 | 1.19 | 1.29 | 1.52 | 1.20 | 1.35 | 0.89 | 1.55 | 1.54 | 1.35 | 1.23 |
| Japanese news | 1 | 402 | 1.46 | 2.67 | 2.00 | 1.79 | 2.14 | 2.23 | 1.67 | 1.86 | 1.52 | 2.27 | 2.21 | 1.87 | 1.76 |
| Korean news | 1 | 512 | 1.56 | 2.12 | 2.00 | 1.92 | 1.85 | 1.79 | 1.31 | 1.48 | 1.23 | 1.81 | 1.77 | 1.45 | 1.38 |
| Chinese news | 1 | 350 | 1.73 | 2.64 | 2.00 | 1.95 | 2.23 | 2.45 | 1.83 | 2.04 | 1.57 | 2.40 | 2.37 | 2.06 | 1.95 |
| Social media posts | 1 | 767 | 1.17 | 1.51 | 2.00 | 1.20 | 1.25 | 1.42 | 1.16 | 1.27 | 0.94 | 1.43 | 1.42 | 1.26 | 1.17 |
| Japanese technical text with English | 1 | 647 | 1.19 | 1.79 | 2.00 | 1.22 | 1.35 | 1.43 | 1.19 | 1.33 | 1.00 | 1.50 | 1.47 | 1.28 | 1.24 |
| Korean technical text with English | 1 | 634 | 1.31 | 1.61 | 2.00 | 1.48 | 1.43 | 1.39 | 1.12 | 1.26 | 0.97 | 1.42 | 1.40 | 1.21 | 1.17 |
| Russian technical text with English | 1 | 858 | 1.00 | 1.47 | 2.00 | 1.01 | 1.06 | 1.17 | 0.88 | 0.99 | 0.72 | 1.22 | 1.16 | 0.96 | 0.92 |
| Chinese technical text with English | 1 | 539 | 1.24 | 1.55 | 2.00 | 1.37 | 1.42 | 1.43 | 1.24 | 1.37 | 0.93 | 1.44 | 1.43 | 1.33 | 1.27 |
| **Total** | 23 | 14,399 | 1.16 | 1.65 | 2.00 | 1.22 | 1.30 | 1.35 | 1.04 | 1.17 | 0.89 | 1.39 | 1.35 | 1.14 | 1.08 |

Against Steno (+ is larger than Steno, − is smaller):

| Text | UTF-8 | UTF-16 | SCSU | BOCU-1 | Deflate fast | Deflate max | Brotli 1 | Brotli 11 | LZ4 fast | LZ4 max | Zstd 1 | Zstd 19 |
|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| Bilingual Arabic and English | +52% | +99% | 0% | +9% | +23% | −4% | +7% | −25% | +27% | +24% | +4% | −0.2% |
| Arabic chat with emoji | +71% | +91% | +2% | +6% | +37% | −7% | +6% | −14% | +41% | +35% | +6% | −0.3% |
| English chat with emoji | +7% | +95% | +0.2% | +3% | −6% | −28% | −21% | −37% | −1% | −3% | −23% | −25% |
| Spanish chat with emoji | +5% | +89% | −1% | +6% | −8% | −29% | −20% | −33% | −4% | −5% | −22% | −26% |
| Hindi chat with emoji | +133% | +92% | +2% | +9% | +37% | −7% | +13% | −15% | +51% | +29% | +4% | −5% |
| Japanese chat with emoji | +79% | +46% | +18% | +32% | +66% | +34% | +46% | +21% | +65% | +62% | +44% | +40% |
| Korean chat with emoji | +37% | +26% | +23% | +12% | +26% | −9% | +1% | −13% | +25% | +24% | +0.7% | −2% |
| Russian chat with emoji | +71% | +92% | +2% | +11% | +29% | −10% | +0.1% | −21% | +32% | +28% | −0.7% | −6% |
| Chinese chat with emoji | +53% | +15% | +9% | +17% | +45% | +7% | +24% | +1% | +43% | +42% | +24% | +14% |
| English with typographic punctuation | +5% | +90% | −1% | +6% | −6% | −28% | −22% | −42% | −2% | −3% | −24% | −26% |
| Hindi and English mixed | +81% | +99% | 0% | +13% | +29% | −1% | +15% | −31% | +38% | +28% | +8% | +3% |
| Greek HTML | +39% | +96% | +0.7% | +7% | −4% | −26% | −15% | −38% | +2% | −1% | −17% | −22% |
| JSON catalog | +18% | +90% | +1% | +2% | −17% | −32% | −24% | −41% | −13% | −18% | −27% | −31% |
| Math and science notation | +10% | +83% | −0.6% | +11% | −6% | −23% | −14% | −31% | −0.5% | −1% | −16% | −20% |
| UI strings in many languages | +52% | +74% | +3% | +13% | +32% | +5% | +18% | −23% | +35% | +34% | +17% | +7% |
| Japanese news | +83% | +37% | +23% | +47% | +53% | +15% | +28% | +5% | +56% | +52% | +28% | +21% |
| Korean news | +36% | +28% | +23% | +18% | +15% | −16% | −5% | −21% | +16% | +13% | −8% | −12% |
| Chinese news | +53% | +16% | +13% | +29% | +42% | +6% | +18% | −9% | +39% | +38% | +19% | +13% |
| Social media posts | +29% | +71% | +2% | +7% | +22% | −1% | +8% | −19% | +22% | +21% | +7% | +0.2% |
| Japanese technical text with English | +51% | +69% | +3% | +14% | +20% | 0% | +12% | −16% | +26% | +24% | +8% | +5% |
| Korean technical text with English | +23% | +53% | +13% | +9% | +6% | −14% | −4% | −26% | +9% | +7% | −7% | −11% |
| Russian technical text with English | +46% | +99% | +0.1% | +5% | +16% | −12% | −2% | −29% | +21% | +16% | −5% | −9% |
| Chinese technical text with English | +25% | +61% | +10% | +14% | +15% | −0.3% | +11% | −25% | +16% | +16% | +7% | +3% |
| **Total** | +42% | +72% | +6% | +12% | +16% | −10% | +0.6% | −24% | +20% | +16% | −2% | −7% |
