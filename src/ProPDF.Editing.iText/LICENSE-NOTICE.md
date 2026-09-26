# Optional editing backend licensing

ProPDF-authored adapter code is MIT licensed. This package depends on iText and pdfSweep, which are separately licensed under the AGPL or a commercial agreement with iText. The MIT license on this adapter does not grant permission to ignore the licenses of its dependencies.

Use of the combined editor requires compliance with the applicable AGPL terms or appropriate commercial licenses for **both iText and pdfSweep**, including any additional iText products used by the application. Obtain licensing advice for your distribution and deployment model. ProPDF includes no commercial license, license key, or license bypass.

The Core, Rendering.Skia, Engine.PdfPig, Presentation and UI adapter packages must not reference this package transitively. An application opts into the editing backend at its composition root.

Official terms: https://itextpdf.com/how-buy/AGPLv3-license
