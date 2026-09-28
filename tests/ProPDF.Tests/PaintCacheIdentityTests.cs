using ProPDF.Engine.PdfPig.Compatibility.Helpers;
using SkiaSharp;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Graphics.Core;
using Xunit;

namespace ProPDF.Tests;

public sealed class PaintCacheIdentityTests
{
    [Fact]
    public void CollidingColorHashesNeverReuseDifferentPaints()
    {
        using var cache=new SKPaintCache(true,.25f);
        var red=new CollisionColor(1,0,0);var blue=new CollisionColor(0,0,1);
        Assert.Equal(red.GetHashCode(),blue.GetHashCode());
        var a=cache.GetPaint(red,1,false,null,null,null,null,BlendMode.Normal);
        var b=cache.GetPaint(blue,1,false,null,null,null,null,BlendMode.Normal);
        Assert.NotSame(a,b);Assert.Equal(SKColors.Red,a.Color);Assert.Equal(SKColors.Blue,b.Color);
        using var surface=SKSurface.Create(new SKImageInfo(20,10));surface.Canvas.DrawRect(0,0,10,10,a);surface.Canvas.DrawRect(10,0,10,10,b);
        using var image=surface.Snapshot();using var pixels=SKBitmap.FromImage(image);
        Assert.Equal(SKColors.Red,pixels.GetPixel(5,5));Assert.Equal(SKColors.Blue,pixels.GetPixel(15,5));
    }

    [Fact]
    public void CollidingDashHashesUseStructuralEquality()
    {
        var one=1d;var other=BitConverter.Int64BitsToDouble(0x3ff0000100000001);
        Assert.Equal(one.GetHashCode(),other.GetHashCode());Assert.NotEqual(one,other);
        using var cache=new SKPaintCache(true,.25f);
        Assert.NotSame(Stroke(cache,[one,2]),Stroke(cache,[other,2]));
    }

    [Fact]
    public void StoredDashIdentityIsIndependentOfMutableInput()
    {
        using var cache=new SKPaintCache(true,.25f);var values=new[]{2d,3d};var first=Stroke(cache,values);
        values[0]=20;Assert.NotSame(first,Stroke(cache,values));
        Assert.Same(first,Stroke(cache,[2,3]));
    }

    [Fact]
    public void EqualDashValuesReuseOneNativePaintAcrossRepeatedRequests()
    {
        using var cache=new SKPaintCache(true,.25f);var first=Stroke(cache,[2,3]);
        for(var i=0;i<1000;i++)Assert.Same(first,Stroke(cache,[2,3]));
    }

    [Fact]
    public void OpacityAndBlendOverridesArePartOfPaintIdentity()
    {
        using var cache=new SKPaintCache(true,.25f);
        var normal=cache.GetPaint(RGBColor.Black,1,false,null,null,null,null,BlendMode.Normal);
        var half=cache.GetPaint(RGBColor.Black,.5,false,null,null,null,null,BlendMode.Normal);
        var source=cache.GetPaint(RGBColor.Black,1,false,null,null,null,null,BlendMode.Normal,SKBlendMode.Src);
        Assert.NotSame(normal,half);Assert.NotSame(normal,source);Assert.InRange((int)half.Color.Alpha,127,128);Assert.Equal(SKBlendMode.Src,source.BlendMode);
    }

    private static SKPaint Stroke(SKPaintCache cache,double[] lengths)
        =>cache.GetPaint(RGBColor.Black,1,true,2,LineJoinStyle.Miter,LineCapStyle.Butt,new LineDashPattern(0,lengths),BlendMode.Normal);
    private sealed class CollisionColor(double r,double g,double b):IColor
    {
        public ColorSpace ColorSpace=>ColorSpace.DeviceRGB;
        public (double r,double g,double b) ToRGBValues()=>(r,g,b);
        public override int GetHashCode()=>42;
    }
}
