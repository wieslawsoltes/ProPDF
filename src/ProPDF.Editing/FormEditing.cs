using ProPDF.Core;
using ProPDF.Kernel;
using static ProPDF.Editing.PdfGraph;
using static ProPDF.Kernel.PdfValues;

namespace ProPDF.Editing;

public sealed partial class ManagedPdfEditor
{
    internal sealed record NativeWidget(NativePage Page, PdfObject Raw, PdfDictionary Dictionary);
    internal sealed record NativeField(string Name, PdfObject Raw, PdfDictionary Dictionary, PdfDictionary Effective, IReadOnlyList<NativeWidget> Widgets);
    internal static List<NativeField> Fields(PdfGraph graph)
    {
        var result = new List<NativeField>(); var form = graph.OptionalDictionary(graph.File.Catalog["AcroForm"]);
        var widgets = new Dictionary<PdfDictionary,NativeWidget>(ReferenceEqualityComparer.Instance);
        foreach (var page in graph.Pages) foreach (var item in graph.Annotations(page)) if (item.Dictionary.Name("Subtype") == "Widget")
        {
            if (!widgets.TryAdd(item.Dictionary,new NativeWidget(page,item.Raw,item.Dictionary))) throw new InvalidDataException("A widget belongs to multiple pages.");
            if (widgets.Count > 100_000) throw new InvalidDataException("Widget count exceeded.");
        }
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        void Read(PdfObject raw, string prefix, PdfDictionary inherited, int depth)
        {
            var d = graph.Dictionary(raw);
            if (depth > graph.File.Limits.MaximumDepth || !visited.Add(d) || visited.Count > 100_000) throw new InvalidDataException("Cyclic or excessive form-field tree.");
            var name = d["T"] is PdfString part ? (prefix.Length == 0 ? part.Text : prefix+"."+part.Text) : prefix;
            var effective = inherited.Copy(); foreach (var key in new[]{"FT","V","DV","Ff","DA","Opt","Q","MaxLen"}) if (d.Contains(key)) effective[key] = graph.Resolve(d[key]);
            var ownWidgets = new List<NativeWidget>(); if (widgets.TryGetValue(d,out var self)) ownWidgets.Add(self);
            var children = new List<PdfObject>();
            foreach (var child in graph.OptionalArray(d["Kids"]))
            {
                var childDictionary = graph.Dictionary(child);
                if (childDictionary.Name("Subtype") == "Widget" && !childDictionary.Contains("T"))
                { if (widgets.TryGetValue(childDictionary,out var widget)) ownWidgets.Add(widget); }
                else children.Add(child);
            }
            if (effective.Name("FT") is not null && (ownWidgets.Count > 0 || children.Count == 0)) result.Add(new NativeField(name,raw,d,effective,ownWidgets));
            foreach (var child in children) Read(child,name,effective,depth+1);
        }
        foreach (var field in graph.OptionalArray(form["Fields"])) Read(field,"",form,0);
        if (result.Select(f=>f.Name).Distinct(StringComparer.Ordinal).Count()!=result.Count) throw new InvalidDataException("Duplicate fully qualified field names.");
        return result;
    }
    private static PdfDictionary EditableForm(PdfGraph graph, bool create = false)
    {
        if (graph.Resolve(graph.File.Catalog["AcroForm"]) is not PdfDictionary form)
        {
            if (!create) throw new InvalidOperationException("This PDF has no form.");
            form = Dictionary(("Fields",new PdfArray())); graph.File.Catalog["AcroForm"] = graph.File.Add(form);
        }
        if (form.Contains("XFA")) throw new NotSupportedException("XFA editing is not implemented.");
        return form;
    }
    private static void InsertField(PdfGraph graph, AddFormField field)
    {
        if (string.IsNullOrWhiteSpace(field.Name) || field.Name.Length > 512 || field.Name.Contains('.')) throw new ArgumentException("New field names must be nonempty flat names.");
        if (Fields(graph).Any(f=>f.Name==field.Name)) throw new ArgumentException("Field name already exists.");
        var page = graph.Page(field.PageNumber); page.Validate(field.Bounds); var b = page.Transform.ToPdf(field.Bounds);
        var form = EditableForm(graph,true); var flags = (field.ReadOnly?1:0)|(field.Required?2:0);
        var type = field.Kind switch { PdfFormFieldKind.Text=>"Tx", PdfFormFieldKind.CheckBox=>"Btn", PdfFormFieldKind.ComboBox or PdfFormFieldKind.ListBox=>"Ch", _=>throw new ArgumentOutOfRangeException(nameof(field.Kind)) };
        if (field.Kind == PdfFormFieldKind.ComboBox) flags |= 1 << 17;
        var d = Dictionary(("Subtype",new PdfName("Widget")), ("FT",new PdfName(type)), ("T",new PdfString(field.Name)), ("Rect",Numbers(b.X,b.Y,b.Right,b.Bottom)), ("F",new PdfNumber(4L)), ("Ff",new PdfNumber(flags)));
        if (type=="Ch")
        {
            if (field.Choices.IsEmpty) throw new ArgumentException("Choice fields require options.");
            d["Opt"] = new PdfArray(field.Choices.Select(value=>(PdfObject)new PdfString(value)));
        }
        var reference = graph.AddAnnotation(page,d); var array = graph.OptionalArray(form["Fields"]); array.Items.Add(reference); form["Fields"] = array;
        var native = new NativeField(field.Name,reference,d,d,[new NativeWidget(page,reference,d)]);
        SetField(graph,native,field.Value,allowReadOnly:true);
    }
    private static void FillField(PdfGraph graph, SetFormValue value)
    {
        _ = EditableForm(graph);
        var field = Fields(graph).SingleOrDefault(f=>f.Name==value.Name) ?? throw new KeyNotFoundException(value.Name);
        SetField(graph,field,value.Value,allowReadOnly:false);
    }
    private static void SetField(PdfGraph graph, NativeField field, string value, bool allowReadOnly)
    {
        if (value.Length > 1_000_000) throw new ArgumentOutOfRangeException(nameof(value));
        var flags = (int)field.Effective.Number("Ff"); if ((flags&1)!=0 && !allowReadOnly) throw new InvalidOperationException("The form field is read-only.");
        var type = field.Effective.Name("FT"); if (type=="Sig") throw new NotSupportedException("Signature fields require the signing service.");
        if (type=="Btn")
        {
            if ((flags&(1<<16))!=0) throw new NotSupportedException("Push buttons do not accept a field value.");
            var states = field.Widgets.SelectMany(widget=>graph.OptionalDictionary(graph.OptionalDictionary(widget.Dictionary["AP"])["N"]).Select(p=>p.Key)).Distinct().ToArray();
            var selected = value.Equals("true",StringComparison.OrdinalIgnoreCase) ? states.FirstOrDefault(s=>s!="Off")??"Yes" :
                value.Length==0 || value.Equals("false",StringComparison.OrdinalIgnoreCase)?"Off":value;
            if (selected!="Off" && states.Length>0 && !states.Contains(selected,StringComparer.Ordinal)) throw new ArgumentException("Unknown checkbox/radio export state.");
            field.Dictionary["V"] = new PdfName(selected);
            foreach (var widget in field.Widgets)
            {
                var normal = graph.OptionalDictionary(graph.OptionalDictionary(widget.Dictionary["AP"])["N"]);
                if (normal.Count==0)
                {
                    var b = Rectangle(graph.File,widget.Dictionary["Rect"]);
                    var border=$"q 1 g 0 0 {F(b.Width)} {F(b.Height)} re f 0 G 1 w .5 .5 {F(Math.Max(0,b.Width-1))} {F(Math.Max(0,b.Height-1))} re S\n";
                    var on = border+$"2 2 m {F(b.Width-2)} {F(b.Height-2)} l 2 {F(b.Height-2)} m {F(b.Width-2)} 2 l S Q\n";
                    normal=Dictionary(("Off",Appearance(graph,b,border+"Q\n")),("Yes",Appearance(graph,b,on)));
                    widget.Dictionary["AP"]=Dictionary(("N",normal));
                }
                widget.Dictionary["AS"] = new PdfName(normal.Contains(selected)?selected:"Off");
            }
        }
        else if (type is "Tx" or "Ch")
        {
            var maximum=(int)field.Effective.Number("MaxLen",int.MaxValue); if (value.Length>maximum) throw new ArgumentException("Field value exceeds MaxLen.");
            var display=value;
            if (type=="Ch")
            {
                var options=graph.OptionalArray(field.Effective["Opt"]);
                var matches=options.Select(item=>graph.Resolve(item) is PdfArray pair && pair.Count==2 ?
                    (Export:((PdfString)graph.Resolve(pair[0])).Text,Display:((PdfString)graph.Resolve(pair[1])).Text) :
                    (Export:((PdfString)graph.Resolve(item)).Text,Display:((PdfString)graph.Resolve(item)).Text)).ToArray();
                var index=System.Array.FindIndex(matches,item=>item.Export==value);
                if (value.Length>0 && index<0 && (flags&(1<<18))==0) throw new ArgumentException("Value is not one of the field's export options.");
                if (index>=0) { display=matches[index].Display; field.Dictionary["I"]=Numbers(index); } else field.Dictionary.Remove("I");
            }
            field.Dictionary["V"]=new PdfString(value);
            var font=PdfFonts.Create(graph,"Helvetica",null,display); var resources=Dictionary(("Font",Dictionary(("PPForm",font.Reference))));
            field.Dictionary["DA"]=new PdfString("/PPForm 12 Tf 0 g");
            var form=EditableForm(graph); var dr=graph.EnsureDictionary(form,"DR"); graph.EnsureDictionary(dr,"Font")["PPForm"]=font.Reference;
            foreach (var widget in field.Widgets)
            {
                var b=Rectangle(graph.File,widget.Dictionary["Rect"]);
                var fontSize=Math.Clamp((b.Width-6)/Math.Max(1,display.Length*.65),4,12);
                var content=$"q 1 g 0 0 {F(b.Width)} {F(b.Height)} re f .65 G .7 w .4 .4 {F(Math.Max(0,b.Width-.8))} {F(Math.Max(0,b.Height-.8))} re S\n"+
                    $"1 1 {F(Math.Max(0,b.Width-2))} {F(Math.Max(0,b.Height-2))} re W n 0 g\n";
                var lines=(flags&(1<<12))!=0?display.Replace("\r\n","\n",StringComparison.Ordinal).Split('\n'):[display.Replace('\n',' ').Replace('\r',' ')];
                for (var i=0;i<lines.Length;i++)
                {
                    var y=lines.Length>1?b.Height-3-fontSize-i*fontSize*1.2:Math.Max(3,(b.Height-fontSize)/2+2);
                    if (y<0) break;
                    content+=$"BT /PPForm {F(fontSize)} Tf 1 0 0 1 3 {F(y)} Tm {Hex(font.Encode(lines[i]))} Tj ET\n";
                }
                widget.Dictionary["AP"]=Dictionary(("N",Appearance(graph,b,content+"Q\n",resources)));
            }
        }
        else throw new NotSupportedException("Unsupported form field type.");
        EditableForm(graph)["NeedAppearances"]=new PdfBoolean(false);
    }
    private static void DeleteField(PdfGraph graph,string name)
    {
        var form=EditableForm(graph); var field=Fields(graph).SingleOrDefault(f=>f.Name==name)??throw new KeyNotFoundException(name);
        foreach (var widget in field.Widgets) graph.OptionalArray(widget.Page.Dictionary["Annots"]).Items.Remove(widget.Raw);
        var parent=graph.Resolve(field.Dictionary["Parent"]) as PdfDictionary;
        if (parent is null) graph.OptionalArray(form["Fields"]).Items.Remove(field.Raw);
        else graph.OptionalArray(parent["Kids"]).Items.Remove(field.Raw);
    }
    private static void RemoveWidget(PdfGraph graph,NativePage page,PdfDictionary widget)
    {
        var field=Fields(graph).FirstOrDefault(f=>f.Widgets.Any(w=>ReferenceEquals(w.Dictionary,widget)));
        if (field is null) return;
        if (field.Widgets.Count<=1) { DeleteField(graph,field.Name); return; }
        var raw=field.Widgets.First(w=>ReferenceEquals(w.Dictionary,widget)).Raw;
        graph.OptionalArray(page.Dictionary["Annots"]).Items.Remove(raw); graph.OptionalArray(field.Dictionary["Kids"]).Items.Remove(raw);
    }
    private static void Flatten(PdfGraph graph,string? name)
    {
        _=EditableForm(graph); var all=Fields(graph); var selected=name is null?all:all.Where(f=>f.Name==name).ToList();
        if (name is not null && selected.Count==0) throw new KeyNotFoundException(name);
        foreach (var field in selected)
        {
            if (field.Effective.Name("FT")=="Sig") throw new NotSupportedException("Signature fields cannot be flattened through ordinary editing.");
            foreach (var widget in field.Widgets)
            {
                var normal=graph.OptionalDictionary(widget.Dictionary["AP"])["N"];
                if (graph.Resolve(normal) is PdfDictionary states) normal=states[widget.Dictionary.Name("AS")??field.Dictionary.Name("V")??"Off"];
                if (graph.Resolve(normal) is not PdfStream stream) throw new NotSupportedException("Flattening requires a valid native appearance; fill/regenerate this field first.");
                var box=Rectangle(graph.File,stream.Dictionary["BBox"]); var rectangle=Rectangle(graph.File,widget.Dictionary["Rect"]);
                var matrix=graph.OptionalArray(stream.Dictionary["Matrix"]);
                if (matrix.Count>0)
                {
                    if (matrix.Count!=6) throw new InvalidDataException("Invalid appearance matrix.");
                    var m=matrix.Select(v=>graph.Resolve(v) is PdfNumber n?n.Value:throw new InvalidDataException("Invalid appearance matrix.")).ToArray();
                    var corners=new[]{new PdfPoint(box.X,box.Y),new PdfPoint(box.Right,box.Y),new PdfPoint(box.X,box.Bottom),new PdfPoint(box.Right,box.Bottom)}
                        .Select(p=>new PdfPoint(m[0]*p.X+m[2]*p.Y+m[4],m[1]*p.X+m[3]*p.Y+m[5])).ToArray();
                    box=new PdfRect(corners.Min(p=>p.X),corners.Min(p=>p.Y),corners.Max(p=>p.X)-corners.Min(p=>p.X),corners.Max(p=>p.Y)-corners.Min(p=>p.Y));
                }
                if (box.IsEmpty) throw new InvalidDataException("Empty form appearance bounds.");
                var resource=graph.Resource(widget.Page,"XObject",normal); var sx=rectangle.Width/box.Width; var sy=rectangle.Height/box.Height;
                graph.Append(widget.Page,$"q {F(sx)} 0 0 {F(sy)} {F(rectangle.X-box.X*sx)} {F(rectangle.Y-box.Y*sy)} cm /{resource} Do Q\n");
            }
            DeleteField(graph,field.Name);
        }
    }
}
