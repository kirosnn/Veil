namespace UnicodeAnimations.Models;

/// <summary>An animation sequence made of Unicode frames.</summary>
/// <param name="Frames">The ordered list of Unicode strings forming the animation.</param>
/// <param name="Interval">Time between frames, in milliseconds.</param>
public sealed record Spinner(string[] Frames, int Interval);
