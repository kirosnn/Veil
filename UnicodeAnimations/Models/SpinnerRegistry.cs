namespace UnicodeAnimations.Models;

/// <summary>
/// Registry of all 18 braille spinners, faithfully ported from
/// https://github.com/gunnargray-dev/unicode-animations (MIT).
/// </summary>
public static class SpinnerRegistry
{
    // Ordered list preserving the original grouping.
    public static IReadOnlyList<(string Name, Spinner Spinner)> All { get; } = Build();

    // -------------------------------------------------------------------------
    // Registry builder
    // -------------------------------------------------------------------------
    private static List<(string, Spinner)> Build() =>
    [
        // Classic single-char braille
        ("braille",
            new(["⠋","⠙","⠹","⠸","⠼","⠴","⠦","⠧","⠇","⠏"], 80)),

        ("braillewave",
            new(["⠁⠂⠄⡀","⠂⠄⡀⢀","⠄⡀⢀⠠","⡀⢀⠠⠐","⢀⠠⠐⠈","⠠⠐⠈⠁","⠐⠈⠁⠂","⠈⠁⠂⠄"], 100)),

        ("dna",
            new(["⠋⠉⠙⠚","⠉⠙⠚⠒","⠙⠚⠒⠂","⠚⠒⠂⠂",
                 "⠒⠂⠂⠒","⠂⠂⠒⠲","⠂⠒⠲⠴","⠒⠲⠴⠤",
                 "⠲⠴⠤⠄","⠴⠤⠄⠋","⠤⠄⠋⠉","⠄⠋⠉⠙"], 80)),

        // Generated grid animations
        ("scan",         new(GenScan(),          70)),
        ("rain",         new(GenRain(),         100)),
        ("scanline",     new(GenScanLine(),      120)),
        ("pulse",        new(GenPulse(),         180)),
        ("snake",        new(GenSnake(),          80)),
        ("sparkle",      new(GenSparkle(),       150)),
        ("cascade",      new(GenCascade(),        60)),
        ("columns",      new(GenColumns(),        60)),
        ("orbit",        new(GenOrbit(),         100)),
        ("breathe",      new(GenBreathe(),       100)),
        ("waverows",     new(GenWaveRows(),       90)),
        ("checkerboard", new(GenCheckerboard(),  250)),
        ("helix",        new(GenHelix(),          80)),
        ("fillsweep",    new(GenFillSweep(),     100)),
        ("diagswipe",    new(GenDiagonalSwipe(),  60)),
    ];

    // -------------------------------------------------------------------------
    // Frame generators – direct port of the TypeScript originals
    // -------------------------------------------------------------------------

    private static string[] GenScan()
    {
        const int W = 8, H = 4;
        var frames = new List<string>();
        for (int pos = -1; pos < W + 1; pos++)
        {
            var g = BrailleUtils.MakeGrid(H, W);
            for (int r = 0; r < H; r++)
                for (int c = 0; c < W; c++)
                    if (c == pos || c == pos - 1) g[r][c] = true;
            frames.Add(BrailleUtils.GridToBraille(g));
        }
        return [.. frames];
    }

    private static string[] GenRain()
    {
        const int W = 8, H = 4, TotalFrames = 12;
        int[] offsets = [0, 3, 1, 5, 2, 7, 4, 6];
        var frames = new List<string>();
        for (int f = 0; f < TotalFrames; f++)
        {
            var g = BrailleUtils.MakeGrid(H, W);
            for (int c = 0; c < W; c++)
            {
                int row = (f + offsets[c]) % (H + 2);
                if (row < H) g[row][c] = true;
            }
            frames.Add(BrailleUtils.GridToBraille(g));
        }
        return [.. frames];
    }

    private static string[] GenScanLine()
    {
        const int W = 6, H = 4;
        int[] positions = [0, 1, 2, 3, 2, 1];
        var frames = new List<string>();
        foreach (int row in positions)
        {
            var g = BrailleUtils.MakeGrid(H, W);
            for (int c = 0; c < W; c++)
            {
                g[row][c] = true;
                if (row > 0) g[row - 1][c] = c % 2 == 0;
            }
            frames.Add(BrailleUtils.GridToBraille(g));
        }
        return [.. frames];
    }

    private static string[] GenPulse()
    {
        const int W = 6, H = 4;
        double cx = W / 2.0 - 0.5, cy = H / 2.0 - 0.5;
        double[] radii = [0.5, 1.2, 2.0, 3.0, 3.5];
        var frames = new List<string>();
        foreach (double radius in radii)
        {
            var g = BrailleUtils.MakeGrid(H, W);
            for (int r = 0; r < H; r++)
                for (int c = 0; c < W; c++)
                {
                    double dist = Math.Sqrt(Math.Pow(c - cx, 2) + Math.Pow(r - cy, 2));
                    if (Math.Abs(dist - radius) < 0.9) g[r][c] = true;
                }
            frames.Add(BrailleUtils.GridToBraille(g));
        }
        return [.. frames];
    }

    private static string[] GenSnake()
    {
        const int W = 4, H = 4;
        var path = new List<(int Row, int Col)>();
        for (int r = 0; r < H; r++)
        {
            if (r % 2 == 0)
                for (int c = 0; c < W; c++) path.Add((r, c));
            else
                for (int c = W - 1; c >= 0; c--) path.Add((r, c));
        }
        var frames = new List<string>();
        for (int i = 0; i < path.Count; i++)
        {
            var g = BrailleUtils.MakeGrid(H, W);
            for (int t = 0; t < 4; t++)
            {
                int idx = (i - t + path.Count) % path.Count;
                g[path[idx].Row][path[idx].Col] = true;
            }
            frames.Add(BrailleUtils.GridToBraille(g));
        }
        return [.. frames];
    }

    private static string[] GenSparkle()
    {
        const int W = 8, H = 4;
        int[][] patterns =
        [
            [1,0,0,1,0,0,1,0, 0,0,1,0,0,1,0,0, 0,1,0,0,1,0,0,1, 1,0,0,0,0,1,0,0],
            [0,1,0,0,1,0,0,1, 1,0,0,1,0,0,0,1, 0,0,0,1,0,1,0,0, 0,0,1,0,1,0,1,0],
            [0,0,1,0,0,1,0,0, 0,1,0,0,0,0,1,0, 1,0,1,0,0,0,0,1, 0,1,0,1,0,0,0,1],
            [1,0,0,0,0,0,1,1, 0,0,1,0,1,0,0,0, 0,0,0,0,1,0,1,0, 1,0,0,1,0,0,1,0],
            [0,0,0,1,1,0,0,0, 0,1,0,0,0,1,0,1, 1,0,0,1,0,0,0,0, 0,1,0,0,0,1,0,1],
            [0,1,1,0,0,0,0,1, 0,0,0,1,0,0,1,0, 0,1,0,0,0,1,0,0, 0,0,1,0,1,0,0,0],
        ];
        var frames = new List<string>();
        foreach (var pat in patterns)
        {
            var g = BrailleUtils.MakeGrid(H, W);
            for (int r = 0; r < H; r++)
                for (int c = 0; c < W; c++)
                    g[r][c] = pat[r * W + c] != 0;
            frames.Add(BrailleUtils.GridToBraille(g));
        }
        return [.. frames];
    }

    private static string[] GenCascade()
    {
        const int W = 8, H = 4;
        var frames = new List<string>();
        for (int offset = -2; offset < W + H; offset++)
        {
            var g = BrailleUtils.MakeGrid(H, W);
            for (int r = 0; r < H; r++)
                for (int c = 0; c < W; c++)
                {
                    int diag = c + r;
                    if (diag == offset || diag == offset - 1) g[r][c] = true;
                }
            frames.Add(BrailleUtils.GridToBraille(g));
        }
        return [.. frames];
    }

    private static string[] GenColumns()
    {
        const int W = 6, H = 4;
        var frames = new List<string>();
        for (int col = 0; col < W; col++)
        {
            for (int fillTo = H - 1; fillTo >= 0; fillTo--)
            {
                var g = BrailleUtils.MakeGrid(H, W);
                for (int pc = 0; pc < col; pc++)
                    for (int r = 0; r < H; r++) g[r][pc] = true;
                for (int r = fillTo; r < H; r++) g[r][col] = true;
                frames.Add(BrailleUtils.GridToBraille(g));
            }
        }
        var full = BrailleUtils.MakeGrid(H, W);
        for (int r = 0; r < H; r++)
            for (int c = 0; c < W; c++) full[r][c] = true;
        frames.Add(BrailleUtils.GridToBraille(full));
        frames.Add(BrailleUtils.GridToBraille(BrailleUtils.MakeGrid(H, W)));
        return [.. frames];
    }

    private static string[] GenOrbit()
    {
        const int W = 2, H = 4;
        (int R, int C)[] path =
        [
            (0, 0), (0, 1),
            (1, 1), (2, 1), (3, 1),
            (3, 0),
            (2, 0), (1, 0),
        ];
        var frames = new List<string>();
        for (int i = 0; i < path.Length; i++)
        {
            var g = BrailleUtils.MakeGrid(H, W);
            g[path[i].R][path[i].C] = true;
            int t1 = (i - 1 + path.Length) % path.Length;
            g[path[t1].R][path[t1].C] = true;
            frames.Add(BrailleUtils.GridToBraille(g));
        }
        return [.. frames];
    }

    private static string[] GenBreathe()
    {
        (int R, int C)[][] stages =
        [
            [],
            [(1, 0)],
            [(0, 1), (2, 0)],
            [(0, 0), (1, 1), (3, 0)],
            [(0, 0), (1, 1), (2, 0), (3, 1)],
            [(0, 0), (0, 1), (1, 1), (2, 0), (3, 1)],
            [(0, 0), (0, 1), (1, 0), (2, 1), (3, 0), (3, 1)],
            [(0, 0), (0, 1), (1, 0), (1, 1), (2, 0), (3, 0), (3, 1)],
            [(0, 0), (0, 1), (1, 0), (1, 1), (2, 0), (2, 1), (3, 0), (3, 1)],
        ];
        var frames = new List<string>();
        var sequence = stages.Concat(stages.Reverse().Skip(1));
        foreach (var dots in sequence)
        {
            var g = BrailleUtils.MakeGrid(4, 2);
            foreach (var (r, c) in dots) g[r][c] = true;
            frames.Add(BrailleUtils.GridToBraille(g));
        }
        return [.. frames];
    }

    private static string[] GenWaveRows()
    {
        const int W = 8, H = 4, TotalFrames = 16;
        var frames = new List<string>();
        for (int f = 0; f < TotalFrames; f++)
        {
            var g = BrailleUtils.MakeGrid(H, W);
            for (int c = 0; c < W; c++)
            {
                double phase = f - c * 0.5;
                int row = (int)Math.Round((Math.Sin(phase * 0.8) + 1) / 2.0 * (H - 1));
                g[row][c] = true;
                if (row > 0) g[row - 1][c] = (f + c) % 3 == 0;
            }
            frames.Add(BrailleUtils.GridToBraille(g));
        }
        return [.. frames];
    }

    private static string[] GenCheckerboard()
    {
        const int W = 6, H = 4;
        var frames = new List<string>();
        for (int phase = 0; phase < 4; phase++)
        {
            var g = BrailleUtils.MakeGrid(H, W);
            for (int r = 0; r < H; r++)
                for (int c = 0; c < W; c++)
                    g[r][c] = phase < 2
                        ? (r + c + phase) % 2 == 0
                        : (r + c + phase) % 3 == 0;
            frames.Add(BrailleUtils.GridToBraille(g));
        }
        return [.. frames];
    }

    private static string[] GenHelix()
    {
        const int W = 8, H = 4, TotalFrames = 16;
        var frames = new List<string>();
        for (int f = 0; f < TotalFrames; f++)
        {
            var g = BrailleUtils.MakeGrid(H, W);
            for (int c = 0; c < W; c++)
            {
                double phase = (f + c) * (Math.PI / 4);
                int y1 = (int)Math.Round((Math.Sin(phase) + 1) / 2.0 * (H - 1));
                int y2 = (int)Math.Round((Math.Sin(phase + Math.PI) + 1) / 2.0 * (H - 1));
                g[y1][c] = true;
                g[y2][c] = true;
            }
            frames.Add(BrailleUtils.GridToBraille(g));
        }
        return [.. frames];
    }

    private static string[] GenFillSweep()
    {
        const int W = 4, H = 4;
        var full = BrailleUtils.MakeGrid(H, W);
        for (int r = 0; r < H; r++)
            for (int c = 0; c < W; c++) full[r][c] = true;

        var frames = new List<string>();
        // Fill bottom-up
        for (int row = H - 1; row >= 0; row--)
        {
            var g = BrailleUtils.MakeGrid(H, W);
            for (int r = row; r < H; r++)
                for (int c = 0; c < W; c++) g[r][c] = true;
            frames.Add(BrailleUtils.GridToBraille(g));
        }
        // Hold full twice
        frames.Add(BrailleUtils.GridToBraille(full));
        frames.Add(BrailleUtils.GridToBraille(full));
        // Empty top-down
        for (int row = 0; row < H; row++)
        {
            var g = BrailleUtils.MakeGrid(H, W);
            for (int r = row + 1; r < H; r++)
                for (int c = 0; c < W; c++) g[r][c] = true;
            frames.Add(BrailleUtils.GridToBraille(g));
        }
        frames.Add(BrailleUtils.GridToBraille(BrailleUtils.MakeGrid(H, W)));
        return [.. frames];
    }

    private static string[] GenDiagonalSwipe()
    {
        const int W = 4, H = 4;
        int maxDiag = W + H - 2;
        var full = BrailleUtils.MakeGrid(H, W);
        for (int r = 0; r < H; r++)
            for (int c = 0; c < W; c++) full[r][c] = true;

        var frames = new List<string>();
        // Sweep in
        for (int d = 0; d <= maxDiag; d++)
        {
            var g = BrailleUtils.MakeGrid(H, W);
            for (int r = 0; r < H; r++)
                for (int c = 0; c < W; c++)
                    if (r + c <= d) g[r][c] = true;
            frames.Add(BrailleUtils.GridToBraille(g));
        }
        frames.Add(BrailleUtils.GridToBraille(full));
        // Sweep out
        for (int d = 0; d <= maxDiag; d++)
        {
            var g = BrailleUtils.MakeGrid(H, W);
            for (int r = 0; r < H; r++)
                for (int c = 0; c < W; c++)
                    if (r + c > d) g[r][c] = true;
            frames.Add(BrailleUtils.GridToBraille(g));
        }
        frames.Add(BrailleUtils.GridToBraille(BrailleUtils.MakeGrid(H, W)));
        return [.. frames];
    }
}
