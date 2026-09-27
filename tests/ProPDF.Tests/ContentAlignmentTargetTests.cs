using System.Text;
using ProPDF.Core;
using ProPDF.Editing;
using ProPDF.Engine.PdfPig;
using ProPDF.Kernel;
using Xunit;
using static ProPDF.Kernel.PdfValues;

namespace ProPDF.Tests;

public sealed class ContentAlignmentTargetTests
{
    [Theory]
    [InlineData(PdfSelectionAlignment.Left, 30, 0)]
    [InlineData(PdfSelectionAlignment.HorizontalCenter, 70, 0)]
    [InlineData(PdfSelectionAlignment.Right, 110, 0)]
    [InlineData(PdfSelectionAlignment.Top, 0, 55)]
    [InlineData(PdfSelectionAlignment.VerticalCenter, 0, 112.5)]
    [InlineData(PdfSelectionAlignment.Bottom, 0, 170)]
    public void ExplicitTargetTranslatesEveryMemberEqually(PdfSelectionAlignment alignment, double dx, double dy)
    {
        var items = Objects(new(10, 20, 20, 30), new(80, 65, 50, 40));
        // Original union (10,20,120,85); explicit region (40,75,200,200).
        var edit = PdfContentSelection.AlignToBounds(items, new(40, 75, 200, 200), alignment);
        for (var i = 0; i < items.Length; i++)
        {
            var member = Assert.IsType<TransformContentObject>(edit.Edits[i]);
            Assert.Equal(items[i].Reference, member.Object);
            Assert.Equal(PdfAffineTransform.Translation(dx, dy), member.Transform);
            var mapped = member.Transform.Map(items[i].Bounds);
            Assert.Equal(items[i].Bounds.Width, mapped.Width); Assert.Equal(items[i].Bounds.Height, mapped.Height);
        }
    }

    [Fact]
    public void SingleObjectAndOversizedSelectionsAreTranslatedNotResized()
    {
        var items = Objects(new PdfRect(-30, -40, 300, 250));
        var edit = PdfContentSelection.AlignToBounds(items, new(0, 0, 100, 100), PdfSelectionAlignment.HorizontalCenter);
        var transform = Assert.IsType<TransformContentObject>(Assert.Single(edit.Edits)).Transform;
        Assert.Equal(new PdfRect(-100, -40, 300, 250), transform.Map(items[0].Bounds));
    }

    [Fact]
    public void InvalidTargetsSelectionsAndUnboundedTranslationsFailBeforeEditing()
    {
        var items = Objects(new(20, 20, 30, 30), new(60, 60, 40, 40));
        Assert.Throws<ArgumentException>(() => PdfContentSelection.AlignToBounds(items, default, PdfSelectionAlignment.Left));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfContentSelection.AlignToBounds(items, new(0, 0, 100, 100), (PdfSelectionAlignment)999));
        Assert.Throws<ArgumentException>(() => PdfContentSelection.AlignToBounds([items[0], items[0]], new(0, 0, 100, 100), PdfSelectionAlignment.Left));
        Assert.Throws<PdfRevisionConflictException>(() => PdfContentSelection.AlignToBounds([items[0], items[1] with { Reference = items[1].Reference with { PageNumber = 2 } }], new(0, 0, 100, 100), PdfSelectionAlignment.Left));
        Assert.Throws<PdfRevisionConflictException>(() => PdfContentSelection.AlignToBounds([items[0], items[1] with { Reference = items[1].Reference with { Revision = Guid.NewGuid() } }], new(0, 0, 100, 100), PdfSelectionAlignment.Left));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfContentSelection.AlignToBounds(items, new(1e12, 0, 100, 100), PdfSelectionAlignment.Left));
        var enumerated = 0;
        IEnumerable<PdfContentObject> TooMany() { while (true) { enumerated++; yield return items[0]; } }
        Assert.Throws<ArgumentException>(() => PdfContentSelection.AlignToBounds(TooMany(), new(0, 0, 100, 100), PdfSelectionAlignment.Left));
        Assert.Equal(EditContentObjects.MaximumSelection + 1, enumerated);
    }

    [Theory]
    [InlineData(0)] [InlineData(90)] [InlineData(180)] [InlineData(270)]
    public async Task CroppedRotatedPageAlignmentKeepsSpacingOtherContentAndOneUndo(int rotation)
    {
        var backend = new PdfPigBackend(); var editor = new ManagedPdfEditor(backend);
        var source = await editor.CreateAsync(size: new(400, 500));
        using var input = source.OpenRead(); using var buffer = new MemoryStream(); input.CopyTo(buffer);
        var file = PdfFile.Open(buffer.ToArray());
        var page = file.Dictionary(file.Array(file.Dictionary(file.Catalog["Pages"])["Kids"])[0]);
        page["CropBox"] = Numbers(20, 30, 350, 450); page["Rotate"] = new PdfNumber(rotation); page["UserUnit"] = new PdfNumber(1.25);
        page["Contents"] = file.Add(PdfStream.FromDecoded(Encoding.ASCII.GetBytes("1 0 0 rg 60 250 40 35 re f 0 1 0 rg 130 170 40 60 re f 0 0 1 rg 240 100 25 25 re f")));
        var session = new PdfSession(backend, editor); using var stream = new MemoryStream(file.Save()); await session.OpenAsync(stream);
        var before = session.Current!; var original = (await editor.ReadPageContentAsync(before, 1)).Objects;
        var size = before.GetPage(1).Size; var target = new PdfRect(0, 0, size.Width, size.Height);
        var operation = PdfContentSelection.AlignToBounds(original.Take(2), target, PdfSelectionAlignment.HorizontalCenter);
        await session.ApplyAsync(operation, before.Id);
        var result = (await editor.ReadPageContentAsync(session.Current!, 1)).Objects;
        var bounds = PdfContentSelection.Bounds(result.Take(2));
        Assert.Equal(size.Width / 2, bounds.X + bounds.Width / 2, 3);
        Assert.Equal(original[1].Bounds.X - original[0].Bounds.X, result[1].Bounds.X - result[0].Bounds.X, 3);
        Assert.Equal(original[1].Bounds.Y - original[0].Bounds.Y, result[1].Bounds.Y - result[0].Bounds.Y, 3);
        Assert.Equal(original[2].Bounds, result[2].Bounds);
        await Assert.ThrowsAsync<PdfRevisionConflictException>(() => session.ApplyAsync(operation, before.Id));
        Assert.True(await session.UndoAsync()); Assert.Same(before, session.Current); Assert.False(await session.UndoAsync());
    }

    private static PdfContentObject[] Objects(params PdfRect[] bounds)
    {
        var revision = Guid.NewGuid();
        return bounds.Select((bounds, index) => new PdfContentObject(new(revision, 1, "test", index), PdfContentObjectKind.Path, bounds)).ToArray();
    }
}
