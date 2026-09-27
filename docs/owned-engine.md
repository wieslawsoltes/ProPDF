# Owned PDF engine

`ProPDF.Kernel` and `ProPDF.Editing` are ProPDF-authored MIT libraries. They replace the previous restricted-license PDF editing backend rather than wrapping it under a new name. SkiaSharp supplies image decoding/drawing; .NET cryptography supplies cryptographic primitives and CMS. No third-party PDF implementation is required by the owned loader/editor. The independently licensed PdfPig adapter remains optional for rendering, extraction and reopen validation.

## Byte model and parser

`PdfObject`, `PdfName`, `PdfNumber`, `PdfString`, `PdfArray`, `PdfDictionary`, `PdfStream` and `PdfReference` represent the native graph. Numbers retain parsed lexical precision, strings retain bytes and decode Unicode/PDFDocEncoding, and unknown names/dictionaries are preserved.

The tokenizer handles comments, whitespace, escaped/nested literal strings, hexadecimal strings, escaped names, numbers, arrays, dictionaries and indirect references. Duplicate dictionary keys, unterminated syntax, invalid numbers and exceeded token/depth budgets fail explicitly. Streams use their declared length; binary data containing `endstream`, `obj` or `%%EOF` is not mistaken for structure.

The reader follows classic cross-reference tables, cross-reference streams, hybrid cross-references, compressed object streams and previous revisions. Newer free entries override older live objects. Object-number/generation and offset mismatches, overlapping indexes, cycles and excessive revision chains are rejected. Cross-reference repair by guessing byte patterns is deliberately not performed.

## Filters and serialization

Owned decoders implement Flate through platform zlib, ASCIIHex, ASCII85, RunLength and LZW with EarlyChange 0/1, plus TIFF and PNG predictors. Per-stream and cumulative decoded-byte budgets prevent unchecked expansion in these paths. DCT/JPX/JBIG2 and other opaque streams can be retained unchanged; decoding them through the generic lossless-filter API is not falsely advertised.

Saving walks the reachable graph, preserves unknown values and opaque encoded streams, writes valid offsets/free lists and discards unreachable objects. A full rewrite does not preserve source bytes or original signatures. Incremental serialization retains the original prefix and adds a new cross-reference revision. `PdfFile` is mutable and single-owner; `PdfSnapshot`/`PdfSession` remain the immutable application boundary.

## Editing and fonts

Owned edit implementations preserve the existing Core operation contracts for pages, native text/images/vectors, annotations, AcroForms, attachments, metadata and navigation. Page transforms account for cropped/rotated geometry and UserUnit. Imported fields receive collision-safe names. Tagged-page import and unqualified signature-field operations fail instead of producing misleading results.

The font implementation reads TrueType cmap formats 4/12 and horizontal metrics, enforces embedding flags, embeds complete font programs and writes Type0/CIDFontType2, widths, CIDToGIDMap and ToUnicode. Distinct Unicode characters mapped to the same glyph remain semantically distinct CIDs. Tests generate geometric glyph programs in memory; no font binaries are bundled. CFF, font collections, vertical writing, shaping, advanced fallback/subsetting and paragraph reflow remain work.

## Standard security and signatures

The owned Standard security handler reads revisions R2–R6 using owner/user authentication, object-specific RC4/AES where required, crypt filters and encrypted permissions. New encryption writes AES-256 R6 with a PDF 1.7 extension-level-8 declaration (or retains PDF 2.0 input version). Legacy algorithms are supported for reading interoperability, not offered as recommended new encryption choices. Printable-ASCII passwords are currently supported; complete Unicode/SASLprep behavior is explicitly unsupported rather than silently approximated.

Signing uses an owned PDF incremental writer, byte-range validation and signature reservation, together with .NET CMS and host-owned RSA/ECDSA providers. The implementation emits detached signatures with SHA-256 and ESS signing-certificate binding. Verification checks cryptographic integrity, range/Contents consistency, current-document coverage and revision counts. It does not establish certificate trust, revocation, TSA validity or PAdES-LTV. Certified/locked forms need further policy qualification and are rejected.

## Redaction boundary

The native redactor interprets the supported content operator subset. It removes whole intersecting text objects, painted paths and image/form invocations, prunes unused resources and writes an opaque fill afterward. It preserves text-state mutations needed by subsequent objects. This is actual removal, but is **conservative object/group removal**, not exact glyph-level or partial-image editing; unaffected portions of an intersecting object can also disappear.

Tagged/ActualText content, inline images, soft masks, patterns, unsupported font encodings and other unqualified operators fail explicitly. These checks and tests are not a hostile-document security certification. Metadata, attachments, other pages, alternate information, source files and undo history need separate review. Sensitive-production sanitization requires broader independent qualification.

## Validation and remaining qualification

Tests cover native output through an independent parser/renderer, handcrafted classic/stream/hybrid cross-references and compressed objects, malformed/cyclic/bounded syntax, predictors/LZW, generated embedded fonts, independently produced Standard-security files, native form flattening, image/text redaction and RSA/ECDSA signature tampering/append histories.

These are focused regression and interoperability checks, not complete ISO 32000, PDF/A or PDF/UA coverage. Missing areas include damaged-file repair, public-key encryption, international password preparation, exhaustive native-content editing, all redaction semantics, complex typography, OCR/conversion, accessibility/tagging, production printing and collaboration. In-process resource checks are not an operating-system sandbox.

Primary design references: [PDF Association specification errata](https://pdf-issues.pdfa.org/32000-2-2020/), [OpenType cmap](https://learn.microsoft.com/en-us/typography/opentype/spec/cmap), [OpenType embedding permissions](https://learn.microsoft.com/en-us/typography/opentype/spec/os2#fstype), [.NET SignedCms](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.pkcs.signedcms).

The kernel recognizes PDF 2.0 syntax. The pinned optional PdfPig rendering adapter has a strict-header limitation for PDF 2.0 files; full PDF 2.0 rendering is not claimed. New AES-256 output therefore declares its extension level on a PDF 1.7 base header rather than silently relabelling features.
