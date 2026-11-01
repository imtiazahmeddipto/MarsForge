using System.IO.Compression;
using System.Text;

namespace MarsForge.Core;

/// <summary>One mesh for the FBX file. Positions and normals are (east, up, north) in metres.</summary>
public sealed class FbxMesh
{
    public required string Name { get; init; }
    public required float[] Positions { get; init; }
    public required float[] Normals { get; init; }
    public required int[] Triangles { get; init; }
    public float[]? Uvs { get; init; }
    public byte[]? TexturePng { get; init; }
    public string? TextureName { get; init; }
    public (double R, double G, double B) Color { get; init; } = (1, 1, 1);
    public Dictionary<string, object>? Properties { get; init; }
}

/// <summary>Binary FBX 7.4 writer for Blender: Y-up, 1 unit = 1 metre. Positions are written as
/// (east, up, -north), so after Blender's import +X = east, +Y = north, +Z = up. Textures are embedded.</summary>
public static class Fbx
{
    const int Version = 7400;
    static readonly byte[] Head = Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0\x1a\0");
    static readonly byte[] FileId = [0x28, 0xb3, 0x2a, 0xeb, 0xb6, 0x24, 0xcc, 0xc2, 0xbf, 0xc8, 0xb0, 0x2a, 0xa9, 0x2b, 0xfc, 0xf1];
    static readonly byte[] FootId = [0xfa, 0xbc, 0xab, 0x09, 0xd0, 0xc8, 0xd4, 0x66, 0xb1, 0x76, 0xfb, 0x83, 0x1c, 0xf7, 0x26, 0x7e];
    static readonly byte[] FootMagic = [0xf8, 0x5a, 0x8c, 0x6a, 0xde, 0xf5, 0xd9, 0x7e, 0xec, 0xe9, 0x0c, 0xe3, 0x75, 0x8f, 0x29, 0x0b];
    const string TimeId = "1970-01-01 10:00:00:000";
    const string Sep = "\0\x01";

    /// <summary>64-bit integer property.</summary>
    public readonly record struct L(long Value);

    sealed class Node(string name, params object[] props)
    {
        public readonly string Name = name;
        public readonly object[] Props = props;
        public readonly List<Node> Children = [];

        public Node Add(string name, params object[] props)
        {
            var n = new Node(name, props);
            Children.Add(n);
            return n;
        }

        public Node P(params object[] props) => Add("P", props);
    }

    /// <summary>The bytes of a binary FBX file with these meshes.</summary>
    public static byte[] Build(IReadOnlyList<FbxMesh> meshes)
    {
        using var output = new MemoryStream();
        var now = DateTime.Now;
        var root = new List<Node>();
        var hx = new Node("FBXHeaderExtension"); root.Add(hx);
        hx.Add("FBXHeaderVersion", 1003);
        hx.Add("FBXVersion", Version);
        hx.Add("EncryptionType", 0);
        var ts = hx.Add("CreationTimeStamp");
        foreach (var (k, v) in new[] { ("Version", 1000), ("Year", now.Year), ("Month", now.Month), ("Day", now.Day),
                                       ("Hour", now.Hour), ("Minute", now.Minute), ("Second", now.Second), ("Millisecond", 0) })
            ts.Add(k, v);
        hx.Add("Creator", "MarsForge FBX writer");
        var si = hx.Add("SceneInfo", "GlobalInfo" + Sep + "SceneInfo", "UserData");
        si.Add("Type", "UserData");
        si.Add("Version", 100);
        var md = si.Add("MetaData");
        md.Add("Version", 100);
        foreach (var k in new[] { "Title", "Subject", "Author", "Keywords", "Revision", "Comment" }) md.Add(k, k == "Title" ? "Mars terrain" : "");
        var sp = si.Add("Properties70");
        sp.P("DocumentUrl", "KString", "Url", "", "/MarsForge.fbx");
        sp.P("SrcDocumentUrl", "KString", "Url", "", "/MarsForge.fbx");
        sp.P("Original", "Compound", "", "");
        sp.P("Original|ApplicationVendor", "KString", "", "", "MarsForge");
        sp.P("Original|ApplicationName", "KString", "", "", "MarsForge");
        sp.P("Original|ApplicationVersion", "KString", "", "", "1.0");
        root.Add(new Node("FileId", FileId));
        root.Add(new Node("CreationTime", TimeId));
        root.Add(new Node("Creator", "MarsForge FBX writer"));

        var gs = new Node("GlobalSettings"); root.Add(gs);
        gs.Add("Version", 1000);
        var gp = gs.Add("Properties70");
        gp.P("UpAxis", "int", "Integer", "", 1);
        gp.P("UpAxisSign", "int", "Integer", "", 1);
        gp.P("FrontAxis", "int", "Integer", "", 2);
        gp.P("FrontAxisSign", "int", "Integer", "", 1);
        gp.P("CoordAxis", "int", "Integer", "", 0);
        gp.P("CoordAxisSign", "int", "Integer", "", 1);
        gp.P("OriginalUpAxis", "int", "Integer", "", 1);
        gp.P("OriginalUpAxisSign", "int", "Integer", "", 1);
        gp.P("UnitScaleFactor", "double", "Number", "", 100.0);
        gp.P("OriginalUnitScaleFactor", "double", "Number", "", 100.0);
        gp.P("AmbientColor", "ColorRGB", "Color", "", 0.0, 0.0, 0.0);
        gp.P("DefaultCamera", "KString", "", "", "Producer Perspective");
        gp.P("TimeMode", "enum", "", "", 11);
        gp.P("TimeSpanStart", "KTime", "Time", "", new L(0));
        gp.P("TimeSpanStop", "KTime", "Time", "", new L(46186158000));
        gp.P("CustomFrameRate", "double", "Number", "", 24.0);

        var docs = new Node("Documents"); root.Add(docs);
        docs.Add("Count", 1);
        var doc = docs.Add("Document", new L(999999), "Scene", "Scene");
        var dp = doc.Add("Properties70");
        dp.P("SourceObject", "object", "", "");
        dp.P("ActiveAnimStackName", "KString", "", "", "");
        doc.Add("RootNode", new L(0));
        root.Add(new Node("References"));

        int nTex = meshes.Count(m => m.TexturePng is not null);
        var counts = new List<(string, int)> { ("GlobalSettings", 1), ("Geometry", meshes.Count), ("Model", meshes.Count), ("Material", meshes.Count) };
        if (nTex > 0) { counts.Add(("Texture", nTex)); counts.Add(("Video", nTex)); }
        var defs = new Node("Definitions"); root.Add(defs);
        defs.Add("Version", 100);
        defs.Add("Count", counts.Sum(c => c.Item2));
        foreach (var (typ, c) in counts) defs.Add("ObjectType", typ).Add("Count", c);

        var objs = new Node("Objects"); root.Add(objs);
        var con = new Node("Connections");
        for (int k = 0; k < meshes.Count; k++) MeshObjects(objs, con, meshes[k], 1000000 + 10 * k);
        root.Add(con);
        var takes = new Node("Takes");
        takes.Add("Current", "");
        root.Add(takes);

        var bw = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        bw.Write(Head);
        bw.Write(Version);
        foreach (var node in root) Encode(bw, node, ReferenceEquals(node, root[^1]));
        bw.Write(new byte[13]);
        bw.Write(FootId);
        bw.Write(new byte[4]);
        long len = output.Position;
        int pad = (int)(((len + 15) & ~15L) - len);
        bw.Write(new byte[pad == 0 ? 16 : pad]);
        bw.Write(Version);
        bw.Write(new byte[120]);
        bw.Write(FootMagic);
        bw.Flush();
        return output.ToArray();
    }

    static void MeshObjects(Node objs, Node con, FbxMesh mesh, long baseId)
    {
        string name = mesh.Name;
        int nv = mesh.Positions.Length / 3;
        var verts = new double[nv * 3];
        var norms = new double[nv * 3];
        for (int i = 0; i < nv; i++)
        {
            // (east, up, north) -> (east, up, -north): a rotation of the real ground, no mirroring, so the winding stays
            verts[3 * i] = mesh.Positions[3 * i]; verts[3 * i + 1] = mesh.Positions[3 * i + 1]; verts[3 * i + 2] = -mesh.Positions[3 * i + 2];
            norms[3 * i] = mesh.Normals[3 * i]; norms[3 * i + 1] = mesh.Normals[3 * i + 1]; norms[3 * i + 2] = -mesh.Normals[3 * i + 2];
        }
        var pvi = (int[])mesh.Triangles.Clone();
        for (int i = 2; i < pvi.Length; i += 3) pvi[i] = ~pvi[i];      // last index of each polygon is negated - 1
        long model = baseId, geom = baseId + 1, mat = baseId + 2, tex = baseId + 3, vid = baseId + 4;

        var g = objs.Add("Geometry", new L(geom), name + Sep + "Geometry", "Mesh");
        g.Add("Properties70");
        g.Add("GeometryVersion", 124);
        g.Add("Vertices", verts);
        g.Add("PolygonVertexIndex", pvi);
        var ln = g.Add("LayerElementNormal", 0);
        ln.Add("Version", 101);
        ln.Add("Name", "");
        ln.Add("MappingInformationType", "ByVertice");
        ln.Add("ReferenceInformationType", "Direct");
        ln.Add("Normals", norms);
        var layers = new List<string> { "LayerElementNormal" };
        if (mesh.Uvs is not null)
        {
            var lu = g.Add("LayerElementUV", 0);
            lu.Add("Version", 101);
            lu.Add("Name", "UVMap");
            lu.Add("MappingInformationType", "ByPolygonVertex");
            lu.Add("ReferenceInformationType", "IndexToDirect");
            lu.Add("UV", mesh.Uvs.Select(v => (double)v).ToArray());
            lu.Add("UVIndex", (int[])mesh.Triangles.Clone());
            layers.Add("LayerElementUV");
        }
        var lm = g.Add("LayerElementMaterial", 0);
        lm.Add("Version", 101);
        lm.Add("Name", "");
        lm.Add("MappingInformationType", "AllSame");
        lm.Add("ReferenceInformationType", "IndexToDirect");
        lm.Add("Materials", new int[] { 0 });
        layers.Add("LayerElementMaterial");
        var layer = g.Add("Layer", 0);
        layer.Add("Version", 100);
        foreach (var t in layers)
        {
            var le = layer.Add("LayerElement");
            le.Add("Type", t);
            le.Add("TypedIndex", 0);
        }

        var m = objs.Add("Model", new L(model), name + Sep + "Model", "Mesh");
        m.Add("Version", 232);
        var mp = m.Add("Properties70");
        mp.P("Lcl Translation", "Lcl Translation", "", "A", 0.0, 0.0, 0.0);
        mp.P("Lcl Rotation", "Lcl Rotation", "", "A", 0.0, 0.0, 0.0);
        mp.P("Lcl Scaling", "Lcl Scaling", "", "A", 1.0, 1.0, 1.0);
        mp.P("DefaultAttributeIndex", "int", "Integer", "", 0);
        mp.P("InheritType", "enum", "", "", 1);
        foreach (var (k, v) in mesh.Properties ?? [])
        {
            if (v is string s) mp.P(k, "KString", "", "U", s);
            else mp.P(k, "double", "Number", "U", Convert.ToDouble(v));
        }
        m.Add("MultiLayer", 0);
        m.Add("MultiTake", 0);
        m.Add("Shading", true);
        m.Add("Culling", "CullingOff");

        var color = mesh.TexturePng is null ? mesh.Color : (1.0, 1.0, 1.0);
        var mt = objs.Add("Material", new L(mat), name + "Material" + Sep + "Material", "");
        mt.Add("Version", 102);
        mt.Add("ShadingModel", "Phong");
        mt.Add("MultiLayer", 0);
        var mtp = mt.Add("Properties70");
        mtp.P("DiffuseColor", "Color", "", "A", color.Item1, color.Item2, color.Item3);
        mtp.P("DiffuseFactor", "Number", "", "A", 1.0);
        mtp.P("SpecularColor", "Color", "", "A", 0.0, 0.0, 0.0);
        mtp.P("SpecularFactor", "Number", "", "A", 0.0);
        mtp.P("Shininess", "Number", "", "A", 2.0);
        mtp.P("ShininessExponent", "Number", "", "A", 2.0);
        mtp.P("ReflectionFactor", "Number", "", "A", 0.0);

        con.Add("C", "OO", new L(model), new L(0));
        con.Add("C", "OO", new L(geom), new L(model));
        con.Add("C", "OO", new L(mat), new L(model));
        if (mesh.TexturePng is not null)
        {
            string tn = mesh.TextureName ?? name + "_texture.png";
            var tx = objs.Add("Texture", new L(tex), name + "Texture" + Sep + "Texture", "");
            tx.Add("Type", "TextureVideoClip");
            tx.Add("Version", 202);
            tx.Add("TextureName", name + "Texture" + Sep + "Texture");
            tx.Add("Properties70").P("UseMaterial", "bool", "", "", 1);
            tx.Add("Media", name + "Video" + Sep + "Video");
            tx.Add("FileName", tn);
            tx.Add("RelativeFilename", tn);
            tx.Add("ModelUVTranslation", 0.0, 0.0);
            tx.Add("ModelUVScaling", 1.0, 1.0);
            tx.Add("Texture_Alpha_Source", "None");
            tx.Add("Cropping", 0, 0, 0, 0);
            var vd = objs.Add("Video", new L(vid), name + "Video" + Sep + "Video", "Clip");
            vd.Add("Type", "Clip");
            vd.Add("Properties70").P("Path", "KString", "XRefUrl", "", tn);
            vd.Add("UseMipMap", 0);
            vd.Add("Filename", tn);
            vd.Add("RelativeFilename", tn);
            vd.Add("Content", mesh.TexturePng);
            con.Add("C", "OP", new L(tex), new L(mat), "DiffuseColor");
            con.Add("C", "OO", new L(vid), new L(tex));
        }
    }

    static void Encode(BinaryWriter w, Node node, bool isLast)
    {
        var props = new MemoryStream();
        foreach (var p in node.Props) WriteProp(props, p);
        byte[] pb = props.ToArray();
        var name = Encoding.ASCII.GetBytes(node.Name);
        long start = w.BaseStream.Position;
        w.Write(new byte[12]);
        w.Write((byte)name.Length);
        w.Write(name);
        w.Write(pb);
        if (node.Children.Count > 0)
        {
            for (int i = 0; i < node.Children.Count; i++) Encode(w, node.Children[i], i == node.Children.Count - 1);
            w.Write(new byte[13]);
        }
        else if (node.Props.Length == 0 && !isLast)
            w.Write(new byte[13]);
        long end = w.BaseStream.Position;
        w.BaseStream.Position = start;
        w.Write((uint)end);
        w.Write((uint)node.Props.Length);
        w.Write((uint)pb.Length);
        w.BaseStream.Position = end;
    }

    static void WriteProp(Stream s, object v)
    {
        var w = new BinaryWriter(s, Encoding.UTF8, leaveOpen: true);
        switch (v)
        {
            case bool b: w.Write((byte)'C'); w.Write((byte)(b ? 1 : 0)); break;
            case L l: w.Write((byte)'L'); w.Write(l.Value); break;
            case int i: w.Write((byte)'I'); w.Write(i); break;
            case double d: w.Write((byte)'D'); w.Write(d); break;
            case string str:
                var sb = Encoding.UTF8.GetBytes(str);
                w.Write((byte)'S'); w.Write((uint)sb.Length); w.Write(sb);
                break;
            case byte[] raw: w.Write((byte)'R'); w.Write((uint)raw.Length); w.Write(raw); break;
            case double[] da: WriteArray(w, 'd', da.Length, System.Runtime.InteropServices.MemoryMarshal.AsBytes(da.AsSpan()).ToArray()); break;
            case int[] ia: WriteArray(w, 'i', ia.Length, System.Runtime.InteropServices.MemoryMarshal.AsBytes(ia.AsSpan()).ToArray()); break;
            default: throw new ArgumentException($"FBX property type {v.GetType()}");
        }
        w.Flush();
    }

    static void WriteArray(BinaryWriter w, char type, int count, byte[] raw)
    {
        byte[] data = raw;
        uint enc = 0;
        if (raw.Length > 128)
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);
            data = ms.ToArray();
            enc = 1;
        }
        w.Write((byte)type);
        w.Write((uint)count);
        w.Write(enc);
        w.Write((uint)data.Length);
        w.Write(data);
    }
}
