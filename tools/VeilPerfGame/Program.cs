using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text.Json;
using System.Windows.Forms;

var options = BenchmarkOptions.Parse(args);
ApplicationConfiguration.Initialize();
Application.Run(new BenchmarkForm(options));

internal sealed record BenchmarkOptions(
    string Mode,
    int DurationSeconds,
    int CpuLoad,
    string? ReportPath)
{
    internal static BenchmarkOptions Parse(string[] args)
    {
        string mode = "desktop";
        int durationSeconds = 45;
        int cpuLoad = 18000;
        string? reportPath = null;

        for (int index = 0; index < args.Length; index++)
        {
            string current = args[index];
            switch (current)
            {
                case "--mode":
                    if (index + 1 < args.Length)
                    {
                        mode = args[++index].Trim().ToLowerInvariant();
                    }
                    break;
                case "--duration":
                    if (index + 1 < args.Length && int.TryParse(args[++index], out int parsedDuration))
                    {
                        durationSeconds = Math.Max(10, parsedDuration);
                    }
                    break;
                case "--cpu-load":
                    if (index + 1 < args.Length && int.TryParse(args[++index], out int parsedCpuLoad))
                    {
                        cpuLoad = Math.Clamp(parsedCpuLoad, 1000, 500000);
                    }
                    break;
                case "--report-path":
                    if (index + 1 < args.Length)
                    {
                        reportPath = args[++index];
                    }
                    break;
            }
        }

        if (mode is not "desktop" and not "game")
        {
            mode = "desktop";
        }

        return new BenchmarkOptions(mode, durationSeconds, cpuLoad, reportPath);
    }
}

internal sealed class BenchmarkForm : Form
{
    private readonly BenchmarkOptions _options;
    private readonly Stopwatch _sessionStopwatch = Stopwatch.StartNew();
    private readonly Stopwatch _frameStopwatch = Stopwatch.StartNew();
    private readonly List<double> _frameTimesMs = [];
    private readonly System.Windows.Forms.Timer _renderTimer;
    private readonly System.Windows.Forms.Timer _shutdownTimer;
    private readonly Font _titleFont = new("Segoe UI", 18, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _bodyFont = new("Segoe UI", 10, FontStyle.Regular, GraphicsUnit.Point);
    private double _lastFrameTimestampMs;
    private int _frameCount;
    private float _phase;
    private bool _reportWritten;

    internal BenchmarkForm(BenchmarkOptions options)
    {
        _options = options;
        DoubleBuffered = true;
        Text = options.Mode == "game" ? "VeilPerfGame Fullscreen" : "VeilPerfGame Desktop";
        BackColor = Color.FromArgb(10, 14, 22);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        if (options.Mode == "game")
        {
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            TopMost = true;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            ClientSize = new Size(1600, 900);
        }

        _renderTimer = new System.Windows.Forms.Timer
        {
            Interval = 16
        };
        _renderTimer.Tick += (_, _) => Invalidate();

        _shutdownTimer = new System.Windows.Forms.Timer
        {
            Interval = Math.Max(1000, options.DurationSeconds * 1000)
        };
        _shutdownTimer.Tick += (_, _) => Close();

        Shown += (_, _) =>
        {
            Activate();
            _renderTimer.Start();
            _shutdownTimer.Start();
        };

        FormClosed += (_, _) => WriteReport();
        KeyDown += OnKeyDown;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        double nowMs = _frameStopwatch.Elapsed.TotalMilliseconds;
        if (_frameCount > 0)
        {
            _frameTimesMs.Add(nowMs - _lastFrameTimestampMs);
        }

        _lastFrameTimestampMs = nowMs;
        _frameCount++;
        _phase += 0.022f;

        SimulateCpuLoad();
        DrawScene(e.Graphics);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            Close();
        }
    }

    private void DrawScene(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(BackColor);

        int width = ClientSize.Width;
        int height = ClientSize.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        using var gradient = new LinearGradientBrush(
            new Rectangle(0, 0, width, height),
            Color.FromArgb(18, 38, 70),
            Color.FromArgb(6, 12, 20),
            35f);
        graphics.FillRectangle(gradient, 0, 0, width, height);

        for (int index = 0; index < 24; index++)
        {
            float orbit = _phase + index * 0.27f;
            float circleWidth = 60 + (index % 5) * 24;
            float x = (float)((Math.Sin(orbit * 1.7f) * 0.38 + 0.5) * Math.Max(1, width - circleWidth));
            float y = (float)((Math.Cos(orbit * 1.2f) * 0.36 + 0.5) * Math.Max(1, height - circleWidth));
            int alpha = 90 + (index % 4) * 18;
            using var brush = new SolidBrush(Color.FromArgb(alpha, 72 + index * 4, 140 + index * 3, 220));
            graphics.FillEllipse(brush, x, y, circleWidth, circleWidth);
        }

        using var panelBrush = new SolidBrush(Color.FromArgb(150, 8, 10, 14));
        using var panelPen = new Pen(Color.FromArgb(90, 255, 255, 255), 1f);
        var panel = new RectangleF(32, 24, Math.Min(520, width - 64), 140);
        graphics.FillRoundedRectangle(panelBrush, panel, 18);
        graphics.DrawRoundedRectangle(panelPen, panel, 18);

        double elapsedSeconds = _sessionStopwatch.Elapsed.TotalSeconds;
        double averageFps = elapsedSeconds <= 0.001 ? 0 : _frameCount / elapsedSeconds;
        double p95 = CalculatePercentile(_frameTimesMs, 0.95);
        string title = _options.Mode == "game" ? "Game workload benchmark" : "Desktop workload benchmark";
        string subtitle = $"FPS {averageFps:F1}   P95 frame {p95:F2} ms   Frames {_frameCount}";
        string body = $"CPU load {_options.CpuLoad}   Duration {_options.DurationSeconds}s   Process {Environment.ProcessId}";

        graphics.DrawString(title, _titleFont, Brushes.White, new PointF(52, 42));
        graphics.DrawString(subtitle, _bodyFont, Brushes.Gainsboro, new PointF(54, 84));
        graphics.DrawString(body, _bodyFont, Brushes.Silver, new PointF(54, 108));
    }

    private void SimulateCpuLoad()
    {
        double accumulator = 0;
        for (int index = 0; index < _options.CpuLoad; index++)
        {
            accumulator += Math.Sqrt(index + 1) * Math.Sin(_phase + index * 0.0012f);
        }

        if (accumulator > double.MaxValue)
        {
            Text = accumulator.ToString("F0");
        }
    }

    private void WriteReport()
    {
        if (_reportWritten)
        {
            return;
        }

        _reportWritten = true;
        _renderTimer.Stop();
        _shutdownTimer.Stop();

        if (string.IsNullOrWhiteSpace(_options.ReportPath))
        {
            return;
        }

        try
        {
            string reportPath = Path.GetFullPath(_options.ReportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);

            double durationSeconds = Math.Max(0.001, _sessionStopwatch.Elapsed.TotalSeconds);
            double averageFps = _frameCount / durationSeconds;
            double averageFrameMs = _frameTimesMs.Count == 0 ? 0 : _frameTimesMs.Average();
            double p95FrameMs = CalculatePercentile(_frameTimesMs, 0.95);
            int lateFrames = _frameTimesMs.Count(static value => value > 20);

            var payload = new
            {
                mode = _options.Mode,
                durationSeconds,
                cpuLoad = _options.CpuLoad,
                frameCount = _frameCount,
                averageFps,
                averageFrameMs,
                p95FrameMs,
                maxFrameMs = _frameTimesMs.Count == 0 ? 0 : _frameTimesMs.Max(),
                lateFrames
            };

            File.WriteAllText(reportPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch
        {
        }
    }

    private static double CalculatePercentile(List<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        double[] ordered = [.. values.Order()];
        int index = (int)Math.Clamp(Math.Ceiling(percentile * ordered.Length) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }
}

internal static class GraphicsExtensions
{
    internal static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF bounds, int radius)
    {
        using GraphicsPath path = CreateRoundedRectangle(bounds, radius);
        graphics.FillPath(brush, path);
    }

    internal static void DrawRoundedRectangle(this Graphics graphics, Pen pen, RectangleF bounds, int radius)
    {
        using GraphicsPath path = CreateRoundedRectangle(bounds, radius);
        graphics.DrawPath(pen, path);
    }

    private static GraphicsPath CreateRoundedRectangle(RectangleF bounds, int radius)
    {
        float diameter = radius * 2f;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
