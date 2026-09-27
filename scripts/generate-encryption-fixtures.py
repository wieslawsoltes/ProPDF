"""Generate synthetic interoperability inputs, not production encrypted documents.

Run with pypdf[crypto]==5.9.0. pypdf is BSD-3-Clause and is a fixture
producer only; no Python/PDF runtime dependency is added to ProPDF packages.
The intentionally obsolete ciphers are reader-compatibility inputs, not
recommended encryption algorithms. New ProPDF encryption uses AES-256 R6.
"""
from __future__ import annotations
import base64
import io
import json
from pathlib import Path
import pypdf
from pypdf import PdfWriter
from pypdf.generic import DictionaryObject, NameObject, DecodedStreamObject

result = {"producer": f"pypdf {pypdf.__version__}", "fixtures": {}}
for algorithm in ("RC4-40", "RC4-128", "AES-128", "AES-256-R5", "AES-256"):
    writer = PdfWriter()
    page = writer.add_blank_page(width=200, height=300)
    font = DictionaryObject({NameObject("/Type"): NameObject("/Font"), NameObject("/Subtype"): NameObject("/Type1"), NameObject("/BaseFont"): NameObject("/Helvetica")})
    page[NameObject("/Resources")] = DictionaryObject({NameObject("/Font"): DictionaryObject({NameObject("/F1"): font})})
    stream = DecodedStreamObject()
    stream.set_data(b"BT /F1 16 Tf 20 250 Td (Independent cipher fixture) Tj ET")
    page.replace_contents(stream)
    writer.add_metadata({"/Title": "Independent encryption fixture"})
    writer.encrypt("reader-password", "owner-password", algorithm=algorithm)
    output = io.BytesIO()
    writer.write(output)
    result["fixtures"][algorithm] = base64.b64encode(output.getvalue()).decode("ascii")
output = Path(__file__).resolve().parents[1] / "tests/ProPDF.Tests/Fixtures/encryption.json"
output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
print(result["producer"], output)
