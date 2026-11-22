# Benchmark corpus

All files are UTF-8 text. The tool normalizes them to NFC, trims every line and drops blank lines before use.

- `udhr/`: translations of the Universal Declaration of Human Rights, the usual corpus for comparing Unicode compression schemes because the same
  content exists in hundreds of languages. The files come from the "UDHR in Unicode" collection (as redistributed in the NLTK `udhr2` corpus); file names are
  ISO 639-3 language codes. Each one is measured three ways: as a whole document, as independently encoded paragraphs, and as independently encoded 16-char
  snippets (to see how each encoding behaves on short field values, where start-up cost dominates).
- `mixed/`: hand-written samples of content that mixes scripts the way real data does: chat with emoji, technical prose with English identifiers, JSON, HTML,
  typographic punctuation. Measured as whole documents and as independently encoded lines.
- `short/`: names, addresses and titles, one per line; every line is encoded independently.
