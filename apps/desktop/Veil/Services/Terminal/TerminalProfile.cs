namespace Veil.Services.Terminal;

internal sealed record TerminalProfile(
    string Id,
    string DisplayName,
    string ExecutablePath,
    string Arguments,
    string? WorkingDirectory,
    string? IconPath,
    bool IsVerified);
