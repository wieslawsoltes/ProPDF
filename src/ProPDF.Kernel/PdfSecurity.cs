using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ProPDF.Kernel;

/// <summary>ProPDF standard security handler. Platform cryptography supplies hashes and AES; no PDF-vendor code is used.</summary>
internal sealed class PdfSecurity
{
    private static readonly byte[] Padding = [0x28,0xbf,0x4e,0x5e,0x4e,0x75,0x8a,0x41,0x64,0x00,0x4e,0x56,0xff,0xfa,0x01,0x08,0x2e,0x2e,0x00,0xb6,0xd0,0x68,0x3e,0x80,0x2f,0x0c,0xa9,0xfe,0x64,0x53,0x69,0x7a];
    private readonly byte[] _key;
    private readonly string _stringMethod;
    private readonly string _streamMethod;
    private readonly bool _encryptMetadata;
    private PdfSecurity(PdfDictionary dictionary, byte[] key, bool owner, string strings, string streams, bool metadata)
    { Dictionary = dictionary; _key = key; OwnerAuthorized = owner; _stringMethod = strings; _streamMethod = streams; _encryptMetadata = metadata; }
    public PdfDictionary Dictionary { get; }
    public bool OwnerAuthorized { get; }
    private static int ReadPermissions(PdfDictionary dictionary)
    {
        if (dictionary["P"] is not PdfNumber number) throw new InvalidDataException("Missing PDF permissions.");
        // Some producers serialize the signed permission mask as an unsigned decimal.
        // Preserve exactly the low 32 bits, but never truncate a wider value.
        var bits = number.Integer;
        if (bits < int.MinValue || bits > uint.MaxValue) throw new InvalidDataException("PDF permissions exceed 32 bits.");
        return unchecked((int)bits);
    }
    public static PdfSecurity Open(PdfDictionary dictionary, byte[] id, string password, CancellationToken token)
    {
        if (dictionary.Name("Filter") != "Standard") throw new NotSupportedException("Only the PDF Standard password security handler is implemented.");
        var version = (int)dictionary.Number("V"); var revision = (int)dictionary.Number("R");
        var metadata = dictionary["EncryptMetadata"] is not PdfBoolean { Value: false };
        if (revision is < 2 or > 6 || version is not (1 or 2 or 4 or 5)) throw new NotSupportedException("Unsupported PDF encryption revision.");
        string Method(string selector)
        {
            if (version < 4) return "V2";
            var name = dictionary.Name(selector) ?? "Identity"; if (name == "Identity") return "Identity";
            if (dictionary["CF"] is not PdfDictionary filters || filters[name] is not PdfDictionary filter) throw new InvalidDataException("Unknown PDF crypt filter.");
            var method = filter.Name("CFM") ?? "None";
            if (method == "None") return "Identity";
            if (method is not ("V2" or "AESV2" or "AESV3") || revision >= 5 && method != "AESV3" || revision < 5 && method == "AESV3")
                throw new NotSupportedException("Unsupported PDF crypt-filter method.");
            return method;
        }
        var strings = Method("StrF"); var streams = Method("StmF");
        if (dictionary.Name("EFF") is { } embedded && embedded != dictionary.Name("StmF")) throw new NotSupportedException("Separate embedded-file crypt filters are not implemented.");
        var user = Bytes(dictionary, "U"); var owner = Bytes(dictionary, "O"); byte[] key; bool authorized;
        if (revision >= 5)
        {
            if (version != 5 || user.Length != 48 || owner.Length != 48) throw new InvalidDataException("Invalid AES-256 security dictionary.");
            var p = PasswordBytes(password, modern: true);
            if (Equal(Hash(p, owner[32..40], user, revision, token), owner[..32]))
            {
                key = AesCbc(Hash(p, owner[40..48], user, revision, token), new byte[16], Bytes(dictionary, "OE"), false, false); authorized = true;
            }
            else if (Equal(Hash(p, user[32..40], [], revision, token), user[..32]))
            {
                key = AesCbc(Hash(p, user[40..48], [], revision, token), new byte[16], Bytes(dictionary, "UE"), false, false); authorized = false;
            }
            else throw new UnauthorizedAccessException("Invalid PDF password.");
            if (key.Length != 32) throw new InvalidDataException("Invalid encrypted file key.");
            using var aes = Aes.Create(); aes.Key = key;
            var permissions = aes.DecryptEcb(Bytes(dictionary, "Perms"), PaddingMode.None);
            var expectedPermissions = ReadPermissions(dictionary);
            if (permissions.Length != 16 || BinaryPrimitives.ReadInt32LittleEndian(permissions) != expectedPermissions ||
                !permissions.AsSpan(4, 4).SequenceEqual(new byte[] { 255,255,255,255 }) || permissions[8] != (metadata ? (byte)'T' : (byte)'F') ||
                !permissions.AsSpan(9, 3).SequenceEqual("adb"u8)) throw new InvalidDataException("PDF encrypted permissions do not match the security dictionary.");
        }
        else
        {
            if (user.Length < 32 || owner.Length != 32 || id.Length == 0) throw new InvalidDataException("Invalid legacy PDF security data.");
            var length = revision == 2 ? 5 : (int)dictionary.Number("Length", 40) / 8;
            if (length is < 5 or > 16) throw new InvalidDataException("Unsupported legacy PDF key length.");
            var permissions = ReadPermissions(dictionary);
            byte[] FileKey(byte[] padded)
            {
                var p = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(p, permissions);
                var digest = MD5.HashData([.. padded, .. owner, .. p, .. id, .. revision >= 4 && !metadata ? new byte[] { 255,255,255,255 } : Array.Empty<byte>()]);
                if (revision >= 3) for (var round = 0; round < 50; round++) digest = MD5.HashData(digest.AsSpan(0, length));
                return digest[..length];
            }
            bool Matches(byte[] candidate)
            {
                if (revision == 2) return Equal(Rc4(candidate, Padding), user);
                var check = Rc4(candidate, MD5.HashData([.. Padding, .. id]));
                for (var round = 1; round <= 19; round++) check = Rc4(candidate.Select(b => (byte)(b ^ round)).ToArray(), check);
                return Equal(check, user[..16]);
            }
            var passwordPadding = Pad(PasswordBytes(password, modern: false)); var digest = MD5.HashData(passwordPadding);
            if (revision >= 3) for (var round = 0; round < 50; round++) digest = MD5.HashData(digest);
            var ownerKey = digest[..length]; var recovered = owner.ToArray();
            if (revision == 2) recovered = Rc4(ownerKey, recovered);
            else for (var round = 19; round >= 0; round--) recovered = Rc4(ownerKey.Select(b => (byte)(b ^ round)).ToArray(), recovered);
            key = FileKey(recovered); authorized = Matches(key);
            if (!authorized) { key = FileKey(passwordPadding); if (!Matches(key)) throw new UnauthorizedAccessException("Invalid PDF password."); }
        }
        return new PdfSecurity(dictionary, key, authorized, strings, streams, metadata);
    }
    public static PdfSecurity Create(string userPassword, string ownerPassword, bool printing, bool copy, CancellationToken token)
    {
        if (ownerPassword.Length < 8) throw new ArgumentException("Owner passwords require at least eight characters.", nameof(ownerPassword));
        var user = PasswordBytes(userPassword, true); var owner = PasswordBytes(ownerPassword, true); var key = RandomNumberGenerator.GetBytes(32);
        var userSalt = RandomNumberGenerator.GetBytes(16); var ownerSalt = RandomNumberGenerator.GetBytes(16);
        var u = Hash(user, userSalt[..8], [], 6, token).Concat(userSalt).ToArray();
        var o = Hash(owner, ownerSalt[..8], u, 6, token).Concat(ownerSalt).ToArray();
        var ue = AesCbc(Hash(user, userSalt[8..], [], 6, token), new byte[16], key, true, false);
        var oe = AesCbc(Hash(owner, ownerSalt[8..], u, 6, token), new byte[16], key, true, false);
        var p = unchecked((int)0xfffff0c0) | (printing ? 4 | 2048 : 0) | (copy ? 16 : 0);
        var permissions = RandomNumberGenerator.GetBytes(16); BinaryPrimitives.WriteInt32LittleEndian(permissions, p);
        permissions.AsSpan(4, 4).Fill(255); permissions[8] = (byte)'T'; "adb"u8.CopyTo(permissions.AsSpan(9));
        using var aes = Aes.Create(); aes.Key = key;
        var dictionary = PdfValues.Dictionary(("Filter", new PdfName("Standard")), ("V", new PdfNumber(5L)), ("R", new PdfNumber(6L)),
            ("Length", new PdfNumber(256L)), ("P", new PdfNumber(p)), ("O", new PdfString(o)), ("U", new PdfString(u)),
            ("OE", new PdfString(oe)), ("UE", new PdfString(ue)), ("Perms", new PdfString(aes.EncryptEcb(permissions, PaddingMode.None))),
            ("EncryptMetadata", new PdfBoolean(true)), ("StmF", new PdfName("StdCF")), ("StrF", new PdfName("StdCF")),
            ("CF", PdfValues.Dictionary(("StdCF", PdfValues.Dictionary(("CFM", new PdfName("AESV3")), ("Length", new PdfNumber(32L)), ("AuthEvent", new PdfName("DocOpen")))))));
        return new PdfSecurity(dictionary, key, true, "AESV3", "AESV3", true);
    }
    public PdfObject Transform(PdfObject value, PdfObjectId id, bool encrypt, int maximumDepth, int depth = 0)
    {
        if (depth > maximumDepth) throw new InvalidDataException("Security object nesting exceeded.");
        switch (value)
        {
            case PdfString text: return new PdfString(Crypt(text.ToArray(), id, _stringMethod, encrypt));
            case PdfArray array: return new PdfArray(array.Select(item => Transform(item, id, encrypt, maximumDepth, depth + 1)));
            case PdfDictionary dictionary:
                var result = new PdfDictionary(); var signature = dictionary.Name("Type") == "Sig" || dictionary.Contains("ByteRange");
                foreach (var item in dictionary) result[item.Key] = signature && item.Key == "Contents" ? item.Value : Transform(item.Value, id, encrypt, maximumDepth, depth + 1);
                return result;
            case PdfStream stream:
                var filters = stream.Dictionary["Filter"];
                if (filters is PdfName { Value: "Crypt" } || filters is PdfArray filterArray && filterArray.Any(f => f is PdfName { Value: "Crypt" }))
                    throw new NotSupportedException("Explicit stream Crypt filters are not yet supported.");
                var dict = (PdfDictionary)Transform(stream.Dictionary, id, encrypt, maximumDepth, depth + 1);
                return new PdfStream(dict, !_encryptMetadata && dict.Name("Type") == "Metadata" ? stream.EncodedBytes : Crypt(stream.EncodedBytes, id, _streamMethod, encrypt));
            default: return value;
        }
    }
    private byte[] Crypt(byte[] input, PdfObjectId id, string method, bool encrypt)
    {
        if (method == "Identity") return input;
        byte[] key;
        if (method == "AESV3") key = _key;
        else
        {
            var suffix = new byte[] { (byte)id.Number, (byte)(id.Number >> 8), (byte)(id.Number >> 16), (byte)id.Generation, (byte)(id.Generation >> 8) };
            key = MD5.HashData([.. _key, .. suffix, .. method == "AESV2" ? "sAlT"u8.ToArray() : Array.Empty<byte>()])[..Math.Min(_key.Length + 5, 16)];
        }
        if (method == "V2") return Rc4(key, input);
        if (encrypt)
        {
            var iv = RandomNumberGenerator.GetBytes(16); return [.. iv, .. AesCbc(key, iv, input, true, true)];
        }
        if (input.Length < 32 || input.Length % 16 != 0) throw new InvalidDataException("Invalid encrypted PDF string or stream.");
        return AesCbc(key, input[..16], input[16..], false, true);
    }
    private static byte[] PasswordBytes(string password, bool modern)
    {
        // Full SASLprep/bidirectional international password handling is not silently approximated.
        if (password.Any(c => c is < ' ' or > '~')) throw new NotSupportedException("The owned security handler currently supports printable ASCII passwords only.");
        var bytes = modern ? Encoding.UTF8.GetBytes(password) : Encoding.Latin1.GetBytes(password);
        return bytes[..Math.Min(bytes.Length, modern ? 127 : 32)];
    }
    private static byte[] Pad(byte[] password) => [.. password.Take(32), .. Padding.Take(Math.Max(0, 32 - password.Length))];
    private static byte[] Hash(byte[] password, byte[] salt, byte[] user, int revision, CancellationToken token)
    {
        var k = SHA256.HashData([.. password, .. salt, .. user]); if (revision == 5) return k;
        var iteration = 0; byte[] e;
        do
        {
            token.ThrowIfCancellationRequested();
            var block = new byte[password.Length + k.Length + user.Length]; password.CopyTo(block, 0); k.CopyTo(block, password.Length); user.CopyTo(block, password.Length + k.Length);
            var repeated = new byte[block.Length * 64]; for (var i = 0; i < 64; i++) block.CopyTo(repeated, block.Length * i);
            e = AesCbc(k[..16], k[16..32], repeated, true, false);
            var selector = 0; for (var i = 0; i < 16; i++) selector = (selector + e[i]) % 3;
            k = selector switch { 0 => SHA256.HashData(e), 1 => SHA384.HashData(e), _ => SHA512.HashData(e) };
            iteration++;
        } while (iteration < 64 || e[^1] > iteration - 32);
        return k[..32];
    }
    private static byte[] Bytes(PdfDictionary dictionary, string key) => dictionary[key] is PdfString value ? value.ToArray() : throw new InvalidDataException($"Missing security entry /{key}.");
    private static bool Equal(byte[] a, byte[] b) => a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    private static byte[] AesCbc(byte[] key, byte[] iv, byte[] data, bool encrypt, bool padding)
    {
        using var aes = Aes.Create(); aes.Key = key;
        return encrypt ? aes.EncryptCbc(data, iv, padding ? PaddingMode.PKCS7 : PaddingMode.None) : aes.DecryptCbc(data, iv, padding ? PaddingMode.PKCS7 : PaddingMode.None);
    }
    private static byte[] Rc4(byte[] key, byte[] bytes)
    {
        var s = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(); var j = 0;
        for (var i = 0; i < 256; i++) { j = (j + s[i] + key[i % key.Length]) & 255; (s[i], s[j]) = (s[j], s[i]); }
        var result = new byte[bytes.Length]; var x = 0; j = 0;
        for (var i = 0; i < bytes.Length; i++) { x = (x + 1) & 255; j = (j + s[x]) & 255; (s[x], s[j]) = (s[j], s[x]); result[i] = (byte)(bytes[i] ^ s[(s[x] + s[j]) & 255]); }
        return result;
    }
}
