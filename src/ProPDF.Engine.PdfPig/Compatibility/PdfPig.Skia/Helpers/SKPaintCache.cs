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
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Graphics.Core;

namespace ProPDF.Engine.PdfPig.Compatibility.Helpers
{
    internal sealed class SKPaintCache : IDisposable
    {
        private readonly bool _isAntialias;

        private readonly Dictionary<PaintKey, SKPaint> _cache = new();
        private readonly Dictionary<(bool, BlendMode), SKPaint> _imagePaintCache = new();

#if PROPDF_RENDER_DIAGNOSTICS
        private readonly SKPaint _imageDebugPaint;
#endif

        public SKPaintCache(bool isAntialias, float minimumLineWidth)
        {
            _isAntialias = isAntialias;
            // minimumLineWidth not in use

#if PROPDF_RENDER_DIAGNOSTICS
            _imageDebugPaint = new SKPaint()
            {
                Style = SKPaintStyle.StrokeAndFill,
                Color = new SKColor(SKColors.IndianRed.Red, SKColors.IndianRed.Green, SKColors.IndianRed.Blue, 150),
                IsAntialias = _isAntialias,
                StrokeWidth = 2
            };
#endif
        }

        // Hash codes only select a dictionary bucket. They are never paint identity:
        // different PDF colors and dash sequences can have identical 32-bit hashes.
        private readonly record struct PaintKey(IColor Color, double Alpha, bool Stroke, float? Width,
            LineJoinStyle? Join, LineCapStyle? Cap, DashKey Dashes, SKBlendMode Blend);

        private readonly struct DashKey : IEquatable<DashKey>
        {
            private readonly int _phase;
            private readonly IReadOnlyList<double>? _lengths;
            public DashKey(LineDashPattern? pattern) { _phase = pattern?.Phase ?? 0; _lengths = pattern?.Array; }
            private DashKey(int phase, double[]? lengths) { _phase = phase; _lengths = lengths; }
            public DashKey Freeze() => new(_phase, _lengths?.ToArray());
            public bool Equals(DashKey other)
            {
                if (_phase != other._phase || (_lengths?.Count ?? 0) != (other._lengths?.Count ?? 0)) return false;
                for (var i = 0; i < (_lengths?.Count ?? 0); i++)
                    if (!_lengths![i].Equals(other._lengths![i])) return false;
                return true;
            }
            public override bool Equals(object? other) => other is DashKey key && Equals(key);
            public override int GetHashCode()
            {
                var hash = new HashCode(); hash.Add(_phase);
                if (_lengths is not null) foreach (var length in _lengths) hash.Add(length);
                return hash.ToHashCode();
            }
        }

        public SKPaint GetPaint(IColor? color, double alpha, bool stroke, float? strokeWidth, LineJoinStyle? joinStyle,
            LineCapStyle? capStyle, LineDashPattern? dashPattern, BlendMode blendMode, SKBlendMode? skBlendModeOverride = null)
        {
            if (stroke && (!strokeWidth.HasValue || !joinStyle.HasValue || !capStyle.HasValue || !dashPattern.HasValue))
                throw new ArgumentException("A stroke requires width, join, cap and dash parameters.");
            color ??= RGBColor.Black;
            var key = new PaintKey(color, alpha, stroke, strokeWidth, joinStyle, capStyle,
                new DashKey(dashPattern), skBlendModeOverride ?? blendMode.ToSKBlendMode());

            if (_cache.TryGetValue(key, out var paint))
            {
                return paint;
            }

            paint = new SKPaint()
            {
                IsAntialias = _isAntialias,
                Color = color.ToSKColor(alpha),
                Style = stroke ? SKPaintStyle.Stroke : SKPaintStyle.Fill,
                BlendMode = skBlendModeOverride ?? blendMode.ToSKBlendMode()
            };
            
            if (stroke)
            {
                // ProPDF: make the upstream stroke-parameter invariant explicit under nullable checks.
                paint.StrokeWidth = strokeWidth.GetValueOrDefault();
                paint.StrokeJoin = joinStyle.GetValueOrDefault().ToSKStrokeJoin();
                paint.StrokeCap = capStyle.GetValueOrDefault().ToSKStrokeCap();
                paint.PathEffect = dashPattern.GetValueOrDefault().ToSKPathEffect();
            }

            // Copy a possibly caller-owned dash array only on a miss, not every glyph/path lookup.
            _cache[key with { Dashes = key.Dashes.Freeze() }] = paint;

            return paint;
        }

        public SKPaint GetPaint(IPdfImage pdfImage, BlendMode blendMode)
        {
            // For non-Normal blend modes, use general cache with ValueTuple key
            var key = (pdfImage.Interpolate, blendMode);

            if (_imagePaintCache.TryGetValue(key, out var paint))
            {
                return paint;
            }

            paint = new SKPaint
            {
                IsAntialias = pdfImage.Interpolate,
                BlendMode = blendMode.ToSKBlendMode()
            };
            
            _imagePaintCache[key] = paint;

            return paint;
        }

#if PROPDF_RENDER_DIAGNOSTICS
        public SKPaint GetImageDebug()
        {
            return _imageDebugPaint;
        }
#endif

        public void Dispose()
        {
#if PROPDF_RENDER_DIAGNOSTICS
            _imageDebugPaint.Dispose();
#endif
            foreach (var pair in _cache)
            {
                pair.Value.PathEffect?.Dispose();
                pair.Value.Dispose();
            }
            _cache.Clear();

            foreach (var pair in _imagePaintCache)
            {
                pair.Value.Dispose();
            }
            _imagePaintCache.Clear();
        }
    }
}
