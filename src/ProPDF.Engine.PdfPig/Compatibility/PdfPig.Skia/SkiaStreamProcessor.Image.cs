// Modified by ProPDF: isolated compatibility namespace; see PROVENANCE.json and PATCHES.md.
// Copyright 2024 BobLd
//
// Licensed under the Apache License, Version 2.0 (the "License").
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using SkiaSharp;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Graphics.Core;
using ProPDF.Engine.PdfPig.Compatibility.Helpers;
using UglyToad.PdfPig.XObjects;

namespace ProPDF.Engine.PdfPig.Compatibility
{
    internal partial class SkiaStreamProcessor
    {
        protected override void RenderXObjectImage(XObjectContentRecord xObjectContentRecord)
        {
            RenderImage(XObjectFactory.ReadImage(xObjectContentRecord, PdfScanner, FilterProvider, ResourceStore));
        }

        protected override void RenderInlineImage(InlineImage inlineImage)
        {
            RenderImage(inlineImage);
        }

        private void RenderImage(IPdfImage pdfImage)
        {
            if (pdfImage.WidthInSamples == 0 || pdfImage.HeightInSamples == 0)
            {
                return;
            }

            if (pdfImage.BoundingBox.Width == 0 || pdfImage.BoundingBox.Height == 0)
            {
                return;
            }

            try
            {
                using SKAutoCanvasRestore skAutoCanvasRestore = new SKAutoCanvasRestore(_canvas, true);
                using var bitmap = pdfImage.GetSKBitmap(ParsingOptions.Logger);

                if (bitmap is null)
                {
                    throw new NullReferenceException("Got a null image.");
                }

                // Images are upside down in PDF
                _canvas.Scale(1, -1, 0, 0.5f);

                var currentState = GetCurrentState();
                // ProPDF correction: sampling is controlled by /Interpolate, not SKPaint.IsAntialias.
                var sampling = new SKSamplingOptions(pdfImage.Interpolate ? SKFilterMode.Linear : SKFilterMode.Nearest,
                    SKMipmapMode.None);
                // ProPDF correction: use an invocation-owned paint. A cached paint must never be changed
                // for /ca because a subsequent invocation can share the image but have different opacity.
                var alpha = (byte)Math.Round(Math.Clamp(currentState.AlphaConstantNonStroking, 0, 1) * 255);


                if (!pdfImage.IsImageMask)
                {
                    bitmap.SetImmutable();
                    using SKImage image = SKImage.FromBitmap(bitmap);
                    if (TryGetActiveSoftMask(out var softMask))
                    {
                        using var innerPaint = _paintCache.GetPaint(pdfImage, BlendMode.Normal).Clone();
                        innerPaint.Color = SKColors.White.WithAlpha(alpha);
                        DrawWithSoftMask(softMask!, currentState.BlendMode,
                            () => _canvas.DrawImage(image, new SKRect(0, 0, 1, 1), sampling, innerPaint));
                    }
                    else
                    {
                        using var imagePaint = _paintCache.GetPaint(pdfImage, currentState.BlendMode).Clone();
                        imagePaint.Color = SKColors.White.WithAlpha(alpha);
                        _canvas.DrawImage(image, new SKRect(0, 0, 1, 1), sampling, imagePaint);
                    }
                }
                else
                {
                    // Image mask: 1-bit stencil. The source bitmap is Gray8 in canonical PDF
                    // convention (0 = paint, 255 = transparent), so invert into an Alpha8 image
                    // (Alpha8 is set in GetSKBitmap) and let Skia composite the current
                    // non-stroking colour through it
                    System.Diagnostics.Debug.Assert(bitmap.ColorType == SKColorType.Alpha8);
                    System.Diagnostics.Debug.Assert(bitmap.AlphaType == SKAlphaType.Premul);

                    Span<byte> src = bitmap.GetPixelSpan();
                    for (int i = 0; i < src.Length; i++)
                    {
                        src[i] = (byte)~src[i];
                    }
                    bitmap.SetImmutable();

                    using SKImage image = SKImage.FromBitmap(bitmap);
                    if (TryGetActiveSoftMask(out var softMask))
                    {
                        var innerMaskPaint = _paintCache.GetPaint(currentState.CurrentNonStrokingColor,
                            currentState.AlphaConstantNonStroking, false, null, null, null, null, BlendMode.Normal);
                        DrawWithSoftMask(softMask!, currentState.BlendMode,
                            () => _canvas.DrawImage(image, new SKRect(0, 0, 1, 1), sampling, innerMaskPaint));
                    }
                    else
                    {
                        var maskPaint = _paintCache.GetPaint(currentState.CurrentNonStrokingColor,
                            currentState.AlphaConstantNonStroking, false, null, null, null, null,
                            currentState.BlendMode);
                        _canvas.DrawImage(image, new SKRect(0, 0, 1, 1), sampling, maskPaint);
                    }
                }
            }
            catch (Exception ex)
            {
                // We have no way so far to know if skia will be able to draw the picture
                ParsingOptions.Logger.Error($"Failed to render image: {ex}");
            }

#if PROPDF_RENDER_DIAGNOSTICS
            _canvas.DrawRect(new SKRect(0, 0, 1, 1), _paintCache.GetImageDebug());
#endif
        }
    }
}
