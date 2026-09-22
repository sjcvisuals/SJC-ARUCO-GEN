using System.IO.Compression;
using System.Globalization;

if (args.Length == 1 && (args[0] is "-h" or "--help" or "/?" or "help"))
{
    PrintHelp();
    return 0;
}

try
{
    var opts = args.Length == 0 ? Prompt() : Parse(args);
    Generate(opts);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

static Options Parse(string[] args)
{
    int? count = null, width = null, height = null;
    string? output = null;
    int grey = 128;
    string dict = "4x4";

    var positional = new List<string>();
    for (int i = 0; i < args.Length; i++)
    {
        string a = args[i];
        if (a is "-o" or "--out" or "--output")
            output = Need(args, ref i, a);
        else if (a is "--grey" or "--gray")
            grey = ParseByte(Need(args, ref i, a), a);
        else if (a is "--dict" or "--dictionary")
            dict = NormalizeDict(Need(args, ref i, a));
        else if (a.StartsWith('-'))
            throw new Exception($"Unknown option '{a}'. Try --help.");
        else
            positional.Add(a);
    }

    // 64 5632x1408 [out.png]
    // 64 5632 1408 [out.png]
    if (positional.Count >= 1)
        count = ParsePositive(positional[0], "count");

    if (positional.Count >= 2 && TryParseSize(positional[1], out int w, out int h))
    {
        width = w;
        height = h;
        if (positional.Count >= 3)
            output = positional[2];
        if (positional.Count > 3)
            throw new Exception("Too many arguments. Try --help.");
    }
    else if (positional.Count >= 3)
    {
        width = ParsePositive(positional[1], "width");
        height = ParsePositive(positional[2], "height");
        if (positional.Count >= 4)
            output = positional[3];
        if (positional.Count > 4)
            throw new Exception("Too many arguments. Try --help.");
    }
    else if (positional.Count == 2)
        throw new Exception("Canvas size should look like 5632x1408.");
    else if (positional.Count == 0 && (count is null || width is null))
        throw new Exception("Need a marker count and canvas size. Try --help.");

    if (count is null || width is null || height is null)
        throw new Exception("Need a marker count and canvas size. Try --help.");

    return new Options(count.Value, width.Value, height.Value, grey, dict, output);
}

static Options Prompt()
{
    Console.WriteLine("SJC-ARUCO-GEN");
    Console.Write("How many markers? ");
    int count = ParsePositive(ReadRequired(), "count");

    Console.Write("Canvas size (e.g. 5632x1408 or 5632 1408)? ");
    string size = ReadRequired();
    int width, height;
    var parts = size.Split(['x', 'X', '*', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 2)
    {
        width = ParsePositive(parts[0], "width");
        height = ParsePositive(parts[1], "height");
    }
    else if (!TryParseSize(size, out width, out height))
        throw new Exception("Canvas size should look like 5632x1408.");

    Console.Write("Output PNG path (blank = Desktop)? ");
    string? output = Console.ReadLine()?.Trim();
    if (string.IsNullOrWhiteSpace(output))
        output = null;

    return new Options(count, width, height, 128, "4x4", output);
}

static string ReadRequired()
{
    string? line = Console.ReadLine()?.Trim();
    if (string.IsNullOrWhiteSpace(line))
        throw new Exception("That value is required.");
    return line;
}

static string Need(string[] args, ref int i, string name)
{
    if (i + 1 >= args.Length)
        throw new Exception($"Missing value for {name}.");
    return args[++i];
}

static int ParsePositive(string s, string name)
{
    if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n <= 0)
        throw new Exception($"{name} must be a positive integer.");
    return n;
}

static int ParseByte(string s, string name)
{
    if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 0 || n > 255)
        throw new Exception($"{name} must be 0-255.");
    return n;
}

static bool TryParseSize(string s, out int width, out int height)
{
    width = height = 0;
    var parts = s.Split(['x', 'X', '*'], StringSplitOptions.RemoveEmptyEntries);
    return parts.Length == 2
        && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out width) && width > 0
        && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height) && height > 0;
}

static string NormalizeDict(string s) => s.Trim().ToLowerInvariant() switch
{
    "4" or "4x4" or "dict_4x4" or "dict_4x4_1000" => "4x4",
    "5" or "5x5" or "dict_5x5" or "dict_5x5_1000" => "5x5",
    "6" or "6x6" or "dict_6x6" or "dict_6x6_1000" => "6x6",
    _ => throw new Exception("Dictionary must be 4x4, 5x5, or 6x6.")
};

static void Generate(Options opts)
{
    if (opts.Count > 1000)
        throw new Exception("This tool supports up to 1000 markers (OpenCV 4x4/5x5/6x6 1000 dictionaries).");

    int bits = opts.Dict switch { "5x5" => 5, "6x6" => 6, _ => 4 };
    int nbytes = (bits * bits + 7) / 8;
    byte[] dictBytes = LoadDict(opts.Dict, nbytes);
    if (opts.Count * nbytes > dictBytes.Length)
        throw new Exception($"Dictionary {opts.Dict} only has {dictBytes.Length / nbytes} markers.");

    var (cols, rows) = BestGrid(opts.Count, opts.Width, opts.Height);
    int cellW = opts.Width / cols;
    int cellH = opts.Height / rows;
    int originX = (opts.Width - cellW * cols) / 2;
    int originY = (opts.Height - cellH * rows) / 2;

    const int borderBits = 1;
    int modules = bits + 2 * borderBits;
    int maxSide = Math.Min(cellW, cellH);
    int gap = Math.Max(4, maxSide / 16);
    int usable = maxSide - 2 * gap;
    int modulePx = Math.Max(1, usable / modules);
    int markerPx = modulePx * modules;
    if (markerPx > maxSide)
    {
        modulePx = Math.Max(1, maxSide / modules);
        markerPx = modulePx * modules;
    }

    byte[] pixels = new byte[opts.Width * opts.Height];
    int lastRowCount = opts.Count - (rows - 1) * cols;

    for (int id = 0; id < opts.Count; id++)
    {
        int row = id / cols;
        int col = id % cols;
        int rowCount = row == rows - 1 ? lastRowCount : cols;
        int rowPadX = (cols - rowCount) * cellW / 2;

        int cellX = originX + rowPadX + col * cellW;
        int cellY = originY + row * cellH;
        int x0 = cellX + (cellW - markerPx) / 2;
        int y0 = cellY + (cellH - markerPx) / 2;

        bool[,] inner = UnpackBits(dictBytes, id, bits, nbytes);
        DrawMarker(pixels, opts.Width, opts.Height, x0, y0, inner, bits, borderBits, modulePx, (byte)opts.Grey);
    }

    string outPath = opts.Output is { Length: > 0 }
        ? Path.GetFullPath(opts.Output)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            $"aruco_{opts.Count}_{opts.Width}x{opts.Height}.png");

    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
    WritePng(outPath, pixels, opts.Width, opts.Height);

    Console.WriteLine($"Wrote {outPath}");
    Console.WriteLine($"  {opts.Count} {opts.Dict} markers  |  canvas {opts.Width}x{opts.Height}");
    Console.WriteLine($"  grid {cols}x{rows}  |  cell {cellW}x{cellH}  |  marker {markerPx}px  |  grey {opts.Grey}");
}

static (int cols, int rows) BestGrid(int n, int width, int height)
{
    double best = double.MaxValue;
    int bestCols = n, bestRows = 1;
    for (int rows = 1; rows <= n; rows++)
    {
        int cols = (int)Math.Ceiling(n / (double)rows);
        double cellW = width / (double)cols;
        double cellH = height / (double)rows;
        if (cellW < 8 || cellH < 8)
            continue;
        double squareness = Math.Max(cellW, cellH) / Math.Min(cellW, cellH);
        int unused = cols * rows - n;
        double score = squareness + unused * 0.05;
        if (score < best)
        {
            best = score;
            bestCols = cols;
            bestRows = rows;
        }
    }
    return (bestCols, bestRows);
}

static bool[,] UnpackBits(byte[] dict, int id, int markerSize, int nbytes)
{
    var bits = new bool[markerSize, markerSize];
    int offset = id * nbytes;
    int i = 0;
    for (int row = 0; row < markerSize; row++)
    {
        for (int col = 0; col < markerSize; col++)
        {
            byte b = dict[offset + i / 8];
            bits[row, col] = (b & (1 << (7 - (i % 8)))) != 0;
            i++;
        }
    }
    return bits;
}

static void DrawMarker(byte[] pixels, int width, int height, int originX, int originY,
    bool[,] bits, int markerSize, int borderBits, int modulePx, byte grey)
{
    int modules = markerSize + 2 * borderBits;
    for (int my = 0; my < modules; my++)
    {
        for (int mx = 0; mx < modules; mx++)
        {
            bool inner = my >= borderBits && my < borderBits + markerSize
                      && mx >= borderBits && mx < borderBits + markerSize;
            byte value = (byte)(inner && bits[my - borderBits, mx - borderBits] ? grey : 0);
            int x0 = originX + mx * modulePx;
            int y0 = originY + my * modulePx;
            for (int py = 0; py < modulePx; py++)
            {
                int y = y0 + py;
                if ((uint)y >= (uint)height) continue;
                int dst = y * width + x0;
                for (int px = 0; px < modulePx; px++)
                {
                    int x = x0 + px;
                    if ((uint)x < (uint)width)
                        pixels[dst + px] = value;
                }
            }
        }
    }
}

static byte[] LoadDict(string dict, int nbytes)
{
    string file = $"{dict}.bin";
    string[] candidates =
    [
        Path.Combine(AppContext.BaseDirectory, "dicts", file),
        Path.Combine(AppContext.BaseDirectory, file),
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "dicts", file),
    ];
    string? path = candidates.FirstOrDefault(File.Exists);
    if (path is null)
        throw new Exception($"Missing dictionary file dicts/{file}.");
    byte[] data = File.ReadAllBytes(path);
    if (data.Length % nbytes != 0)
        throw new Exception($"Dictionary {dict} is the wrong size.");
    return data;
}

static void WritePng(string path, byte[] gray, int w, int h)
{
    using var fs = File.Create(path);
    fs.Write([137, 80, 78, 71, 13, 10, 26, 10]);

    byte[] ihdr = new byte[13];
    WriteBe32(ihdr, 0, w);
    WriteBe32(ihdr, 4, h);
    ihdr[8] = 8;
    ihdr[9] = 0;
    WriteChunk(fs, "IHDR"u8, ihdr);

    byte[] raw = new byte[h * (w + 1)];
    for (int y = 0; y < h; y++)
    {
        raw[y * (w + 1)] = 0;
        Buffer.BlockCopy(gray, y * w, raw, y * (w + 1) + 1, w);
    }

    using var compressed = new MemoryStream();
    using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        zlib.Write(raw);
    WriteChunk(fs, "IDAT"u8, compressed.ToArray());
    WriteChunk(fs, "IEND"u8, []);
}

static void WriteChunk(Stream s, ReadOnlySpan<byte> type, byte[] data)
{
    Span<byte> len = stackalloc byte[4];
    WriteBe32(len, 0, data.Length);
    s.Write(len);
    s.Write(type);
    s.Write(data);
    byte[] crcBuf = new byte[type.Length + data.Length];
    type.CopyTo(crcBuf);
    Buffer.BlockCopy(data, 0, crcBuf, type.Length, data.Length);
    Span<byte> crc = stackalloc byte[4];
    WriteBe32(crc, 0, unchecked((int)Crc32(crcBuf)));
    s.Write(crc);
}

static void WriteBe32(Span<byte> buf, int offset, int value)
{
    buf[offset] = (byte)(value >> 24);
    buf[offset + 1] = (byte)(value >> 16);
    buf[offset + 2] = (byte)(value >> 8);
    buf[offset + 3] = (byte)value;
}

static uint Crc32(byte[] data)
{
    uint c = 0xFFFFFFFF;
    foreach (byte b in data)
    {
        c ^= b;
        for (int k = 0; k < 8; k++)
            c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
    }
    return c ^ 0xFFFFFFFF;
}

static void PrintHelp()
{
    Console.WriteLine("""
        SJC-ARUCO-GEN — black-background ArUco marker sheet

        Usage:
          generate.cmd <count> <width>x<height> [output.png]
          generate.cmd <count> <width> <height> [output.png]
          generate.cmd                 (prompts for count and size)

        Options:
          -o, --out <path>     Output PNG (default: Desktop\aruco_<n>_<w>x<h>.png)
          --grey <0-255>       Light color of marker bits (default 128)
          --dict 4x4|5x5|6x6  OpenCV dictionary (default 4x4)

        Examples:
          generate.cmd 64 5632x1408
          generate.cmd 36 2048 2048 --dict 6x6 --grey 160
          generate.cmd 10 1920x1080 -o markers.png
        """);
}

readonly record struct Options(int Count, int Width, int Height, int Grey, string Dict, string? Output);
