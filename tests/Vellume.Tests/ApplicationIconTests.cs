using System.Buffers.Binary;
using System.Reflection;
using System.Resources;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Vellume.Tests;

public sealed class ApplicationIconTests
{
    private static readonly int[] ExpectedSizes =
        [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(40)]
    [InlineData(48)]
    [InlineData(64)]
    [InlineData(96)]
    [InlineData(128)]
    [InlineData(256)]
    public void IconFile_DecodesEvery32BitSizeWithSmoothTransparency(int size)
    {
        var bytes = ReadSourceIcon();
        _ = ReadIconFrames(bytes);
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        Assert.Equal(ExpectedSizes.Length, decoder.Frames.Count);
        var frame = Assert.Single(decoder.Frames, candidate =>
            candidate.PixelWidth == size && candidate.PixelHeight == size);
        Assert.Equal(32, frame.Format.BitsPerPixel);

        var bitmap = new FormatConvertedBitmap(
            frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[size * size * 4];
        bitmap.CopyPixels(pixels, size * 4, 0);
        AssertAlphaCoverage(Enumerable.Range(0, size * size)
            .Select(index => pixels[index * 4 + 3]));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void TrayIconClone_RemainsUsableAfterSourceAndStreamAreDisposed(int size)
    {
        System.Drawing.Icon detached;
        using (var stream = new MemoryStream(ReadSourceIcon(), writable: false))
        using (var source = new System.Drawing.Icon(stream, size, size))
        {
            detached = (System.Drawing.Icon)source.Clone();
        }

        using (detached)
        using (var bitmap = detached.ToBitmap())
        {
            Assert.Equal(size, bitmap.Width);
            Assert.Equal(size, bitmap.Height);
            AssertAlphaCoverage(
                from y in Enumerable.Range(0, size)
                from x in Enumerable.Range(0, size)
                select bitmap.GetPixel(x, y).A);
        }
    }

    [Fact]
    [Trait("Category", "LocalIntegration")]
    public void BuiltExecutable_ContainsTheExactBrandedNativeIconFrames()
    {
        var executable = GetOptionalBuiltExecutable();
        if (executable is null)
        {
            return;
        }

        var sourceFrames = ReadIconFrames(ReadSourceIcon());
        // DATAFILE maps resources only: it never starts the app or its entry point.
        var module = LoadLibraryExW(executable, nint.Zero, 0x00000002);
        Assert.True(module != nint.Zero,
            $"LoadLibraryExW failed: {Marshal.GetLastWin32Error()}");
        try
        {
            var names = new List<nint>();
            EnumResourceNameCallback callback = (_, _, name, _) =>
            {
                names.Add(name);
                return true;
            };
            Assert.True(EnumResourceNamesW(module, (nint)14, callback, nint.Zero),
                $"RT_GROUP_ICON was not found: {Marshal.GetLastWin32Error()}");
            GC.KeepAlive(callback);

            var groups = names.Select(name => ReadResource(module, name, 14))
                .Where(bytes => bytes.Length >= 6 &&
                    ReadUInt16(bytes, 0) == 0 &&
                    ReadUInt16(bytes, 2) == 1 &&
                    ReadUInt16(bytes, 4) == ExpectedSizes.Length)
                .ToArray();
            var group = Assert.Single(groups);
            Assert.Equal(6 + ExpectedSizes.Length * 14, group.Length);
            var foundSizes = new List<int>();
            for (var index = 0; index < ExpectedSizes.Length; index++)
            {
                var offset = 6 + index * 14;
                var size = DecodeDimension(group[offset]);
                Assert.Equal(size, DecodeDimension(group[offset + 1]));
                Assert.Equal(1, ReadUInt16(group, offset + 4));
                Assert.Equal(32, ReadUInt16(group, offset + 6));
                var resourceId = ReadUInt16(group, offset + 12);
                var actual = ReadResource(module, (nint)resourceId, 3);
                Assert.Equal(actual.Length, ReadUInt32(group, offset + 8));
                // Comparing payloads rules out a successful extraction of the
                // generic .NET apphost icon or an outdated branded icon.
                Assert.Equal(sourceFrames[size], actual);
                foundSizes.Add(size);
            }

            Assert.Equal(ExpectedSizes, foundSizes.Order().ToArray());
        }
        finally
        {
            FreeLibrary(module);
        }
    }

    [Fact]
    [Trait("Category", "LocalIntegration")]
    public void BuiltWpfAssembly_EmbedsTheSameIconWithoutALooseFileDependency()
    {
        var executable = GetOptionalBuiltExecutable();
        if (executable is null)
        {
            return;
        }

        var assemblyPath = Path.Combine(
            Path.GetDirectoryName(executable)!, "Vellume.App.dll");
        Assert.True(File.Exists(assemblyPath), assemblyPath);
        // Loading metadata/resources does not instantiate Application or run Main.
        var assembly = Assembly.LoadFrom(assemblyPath);
        using var stream = assembly.GetManifestResourceStream("Vellume.App.g.resources");
        Assert.NotNull(stream);
        using var resources = new ResourceReader(stream);
        var entries = resources.GetEnumerator();
        byte[]? actual = null;
        while (entries.MoveNext())
        {
            if (entries.Key is not string key ||
                key != "assets/brand/vellume-app.ico")
            {
                continue;
            }

            using var iconStream = Assert.IsAssignableFrom<Stream>(entries.Value);
            using var copy = new MemoryStream();
            iconStream.CopyTo(copy);
            actual = copy.ToArray();
            break;
        }

        Assert.NotNull(actual);
        Assert.Equal(ReadSourceIcon(), actual);
    }

    private static void AssertAlphaCoverage(IEnumerable<byte> pixels)
    {
        var alphas = pixels.ToArray();
        Assert.Contains((byte)0, alphas);
        Assert.Contains((byte)255, alphas);
        Assert.Contains(alphas, alpha => alpha is > 0 and < 255);
    }

    private static Dictionary<int, byte[]> ReadIconFrames(byte[] bytes)
    {
        Assert.True(bytes.Length >= 6);
        Assert.Equal(0, ReadUInt16(bytes, 0));
        Assert.Equal(1, ReadUInt16(bytes, 2));
        Assert.Equal(ExpectedSizes.Length, ReadUInt16(bytes, 4));
        var frames = new Dictionary<int, byte[]>();
        var directoryEnd = 6 + ExpectedSizes.Length * 16;
        Assert.True(bytes.Length >= directoryEnd);
        for (var index = 0; index < ExpectedSizes.Length; index++)
        {
            var offset = 6 + index * 16;
            var size = DecodeDimension(bytes[offset]);
            Assert.Equal(size, DecodeDimension(bytes[offset + 1]));
            Assert.Equal(1, ReadUInt16(bytes, offset + 4));
            Assert.Equal(32, ReadUInt16(bytes, offset + 6));
            var length = ReadUInt32(bytes, offset + 8);
            var start = ReadUInt32(bytes, offset + 12);
            Assert.InRange(start, directoryEnd, bytes.Length);
            Assert.InRange(length, 1, bytes.Length - start);
            Assert.True(frames.TryAdd(size, bytes.AsSpan(start, length).ToArray()));
        }

        Assert.Equal(ExpectedSizes, frames.Keys.Order().ToArray());
        return frames;
    }

    private static int DecodeDimension(byte size) => size == 0 ? 256 : size;

    private static int ReadUInt16(byte[] bytes, int offset)
        => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));

    private static int ReadUInt32(byte[] bytes, int offset)
        => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)));

    private static byte[] ReadSourceIcon()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName,
                "src", "Vellume.App", "Assets", "Brand", "vellume-app.ico");
            if (File.Exists(path))
            {
                return File.ReadAllBytes(path);
            }
        }

        throw new FileNotFoundException("Vellume 앱 아이콘을 찾을 수 없습니다.");
    }

    private static string? GetOptionalBuiltExecutable()
    {
        var path = Environment.GetEnvironmentVariable("VELLUME_APP_ICON_TEST_EXE");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        path = Path.GetFullPath(path);
        Assert.True(File.Exists(path), path);
        return path;
    }

    private static byte[] ReadResource(nint module, nint name, int type)
    {
        var resource = FindResourceW(module, name, (nint)type);
        Assert.NotEqual(nint.Zero, resource);
        var length = checked((int)SizeofResource(module, resource));
        Assert.InRange(length, 1, 16 * 1024 * 1024);
        var loaded = LoadResource(module, resource);
        Assert.NotEqual(nint.Zero, loaded);
        var address = LockResource(loaded);
        Assert.NotEqual(nint.Zero, address);
        var bytes = new byte[length];
        Marshal.Copy(address, bytes, 0, length);
        return bytes;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool EnumResourceNameCallback(
        nint module, nint type, nint name, nint parameter);

    // Maps PE resources for inspection without executing the application.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true,
        SetLastError = true)]
    private static extern nint LoadLibraryExW(string path, nint file, uint flags);

    // Enumerates icon groups, including compiler-assigned numeric resource IDs.
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumResourceNamesW(nint module, nint type,
        EnumResourceNameCallback callback, nint parameter);

    // Locates an icon group or its individual frame by resource ID.
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint FindResourceW(nint module, nint name, nint type);

    // Obtains the bounded byte length of a mapped icon resource.
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint SizeofResource(nint module, nint resource);

    // Accesses resource data in the already mapped PE module.
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint LoadResource(nint module, nint resource);

    // Returns the address of loaded resource bytes for an immutable copy.
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint LockResource(nint resource);

    // Releases the DATAFILE mapping after all icon bytes have been inspected.
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(nint module);
}
