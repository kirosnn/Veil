namespace UnicodeAnimations.Models;

/// <summary>
/// Utility functions for the braille dot grid.
///
/// Each braille character (U+2800 block) is a 2-col × 4-row dot grid.
/// Dot bit layout:
///   Row 0 :  dot1 (0x01)  dot4 (0x08)
///   Row 1 :  dot2 (0x02)  dot5 (0x10)
///   Row 2 :  dot3 (0x04)  dot6 (0x20)
///   Row 3 :  dot7 (0x40)  dot8 (0x80)
/// Base codepoint : U+2800
/// </summary>
public static class BrailleUtils
{
    private static readonly int[][] DotMap =
    [
        [0x01, 0x08], // row 0
        [0x02, 0x10], // row 1
        [0x04, 0x20], // row 2
        [0x40, 0x80], // row 3
    ];

    /// <summary>
    /// Converts a 2-D boolean grid to a braille string.
    /// grid[row][col] = true means the dot is raised.
    /// Column count should be even (2 dot-columns per character).
    /// </summary>
    public static string GridToBraille(bool[][] grid)
    {
        int rows = grid.Length;
        int cols = rows > 0 ? grid[0].Length : 0;
        int charCount = (int)Math.Ceiling(cols / 2.0);
        var sb = new System.Text.StringBuilder(charCount);

        for (int c = 0; c < charCount; c++)
        {
            int code = 0x2800;
            for (int r = 0; r < 4 && r < rows; r++)
            {
                for (int d = 0; d < 2; d++)
                {
                    int col = c * 2 + d;
                    if (col < cols && grid[r][col])
                        code |= DotMap[r][d];
                }
            }
            sb.Append(char.ConvertFromUtf32(code));
        }

        return sb.ToString();
    }

    /// <summary>Creates an empty boolean grid of the given size.</summary>
    public static bool[][] MakeGrid(int rows, int cols)
    {
        if (rows <= 0 || cols <= 0) return [];
        var grid = new bool[rows][];
        for (int i = 0; i < rows; i++)
            grid[i] = new bool[cols];
        return grid;
    }
}
