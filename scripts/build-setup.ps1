[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$projectRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectFile = Join-Path $projectRoot "apps\desktop\Veil\Veil.csproj"
$targetFramework = "net10.0-windows10.0.22621.0"
$appBuildDir = Join-Path $projectRoot "apps\desktop\Veil\bin\$Configuration\$targetFramework\$RuntimeIdentifier"
$artifactsRoot = Join-Path $projectRoot "artifacts\setup"
$publishDir = Join-Path $artifactsRoot "publish"
$packageDir = Join-Path $artifactsRoot "package"
$installerSrcDir = Join-Path $artifactsRoot "installer-src"
$installerPublishDir = Join-Path $artifactsRoot "installer-publish"
$payloadZipPath = Join-Path $packageDir "payload.zip"
$installerProjectPath = Join-Path $installerSrcDir "VeilSetup.csproj"
$installerProgramPath = Join-Path $installerSrcDir "Program.cs"
$installerPayloadPath = Join-Path $installerSrcDir "payload.zip"
$setupExePath = Join-Path $artifactsRoot "VeilSetup.exe"
$dotnetCliHome = Join-Path $projectRoot ".dotnet-cli"
$nugetPackages = Join-Path $dotnetCliHome ".nuget\packages"

$env:DOTNET_CLI_HOME = $dotnetCliHome
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:NUGET_PACKAGES = $nugetPackages

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null

foreach ($path in @($publishDir, $packageDir, $installerSrcDir, $installerPublishDir, $setupExePath)) {
    if (Test-Path $path) {
        Remove-Item $path -Recurse -Force -ErrorAction SilentlyContinue
    }
}

New-Item -ItemType Directory -Path $publishDir | Out-Null
New-Item -ItemType Directory -Path $packageDir | Out-Null
New-Item -ItemType Directory -Path $installerSrcDir | Out-Null
New-Item -ItemType Directory -Path $installerPublishDir | Out-Null
New-Item -ItemType Directory -Path $dotnetCliHome -Force | Out-Null
New-Item -ItemType Directory -Path $nugetPackages -Force | Out-Null

dotnet build $projectFile `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained false

if (-not (Test-Path $appBuildDir)) {
    throw "Application build output was not created at $appBuildDir"
}

Get-ChildItem -Path $appBuildDir -Force |
    Where-Object { $_.Name -ne "publish" } |
    Copy-Item -Destination $publishDir -Recurse -Force

Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $payloadZipPath -CompressionLevel Optimal
Copy-Item -Path $payloadZipPath -Destination $installerPayloadPath -Force

$installerProject = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>VeilSetup</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <EmbeddedResource Include="payload.zip" LogicalName="VeilSetup.payload.zip" />
  </ItemGroup>
</Project>
'@

$installerProgram = @'
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            ApplicationConfiguration.Initialize();
            return args.Length > 0 && string.Equals(args[0], "--uninstall", StringComparison.OrdinalIgnoreCase)
                ? Uninstall()
                : Install();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Veil Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static int Install()
    {
        if (MessageBox.Show(
                "Install Veil for the current user?",
                "Veil Setup",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return 0;
        }

        string installDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Veil");
        string appExePath = Path.Combine(installDir, "Veil.exe");
        string setupExePath = Path.Combine(installDir, "VeilSetup.exe");
        string shortcutPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "Windows",
            "Start Menu",
            "Programs",
            "Veil.lnk");
        string tempZipPath = Path.Combine(
            Path.GetTempPath(),
            "Veil." + Guid.NewGuid().ToString("N") + ".zip");

        try
        {
            StopVeilProcesses();

            if (Directory.Exists(installDir))
            {
                Directory.Delete(installDir, true);
            }

            Directory.CreateDirectory(installDir);
            ExtractPayload(tempZipPath, installDir);
            File.Copy(Environment.ProcessPath!, setupExePath, true);
            CreateShortcut(shortcutPath, appExePath, installDir);
            RegisterUninstallEntry(installDir, appExePath, setupExePath);
            RegisterWidgetShellNew(appExePath);

            Process.Start(new ProcessStartInfo
            {
                FileName = appExePath,
                WorkingDirectory = installDir,
                UseShellExecute = true
            });

            return 0;
        }
        finally
        {
            if (File.Exists(tempZipPath))
            {
                File.Delete(tempZipPath);
            }
        }
    }

    private static int Uninstall()
    {
        if (MessageBox.Show(
                "Remove Veil from this computer?",
                "Veil Setup",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return 0;
        }

        string installDir = Path.GetDirectoryName(Environment.ProcessPath!)!;
        string parentDir = Directory.GetParent(installDir)!.FullName;
        string shortcutPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "Windows",
            "Start Menu",
            "Programs",
            "Veil.lnk");

        StopVeilProcesses();

        if (File.Exists(shortcutPath))
        {
            File.Delete(shortcutPath);
        }

        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Veil", false);
        UnregisterWidgetShellNew();
        ScheduleDelete(installDir, parentDir);

        MessageBox.Show(
            "Veil will be removed in a moment.",
            "Veil Setup",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);

        return 0;
    }

    private static void ExtractPayload(string tempZipPath, string installDir)
    {
        using Stream resourceStream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("VeilSetup.payload.zip")
            ?? throw new InvalidOperationException("Embedded payload not found.");

        using (FileStream outputStream = File.Create(tempZipPath))
        {
            resourceStream.CopyTo(outputStream);
        }

        ZipFile.ExtractToDirectory(tempZipPath, installDir, true);
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDirectory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);

        Type shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell is unavailable.");
        object shellObject = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Failed to create WScript.Shell.");
        dynamic shell = shellObject;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetPath;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.IconLocation = targetPath + ",0";
        shortcut.Save();
    }

    private static void RegisterUninstallEntry(string installDir, string appExePath, string setupExePath)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Veil")
            ?? throw new InvalidOperationException("Failed to create uninstall registry key.");

        string version = FileVersionInfo.GetVersionInfo(appExePath).FileVersion ?? "1.0.0";

        key.SetValue("DisplayName", "Veil");
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "Veil");
        key.SetValue("InstallLocation", installDir);
        key.SetValue("DisplayIcon", appExePath);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("UninstallString", "\"" + setupExePath + "\" --uninstall");
    }

    private static void RegisterWidgetShellNew(string appExePath)
    {
        const string extension = ".veilwidget";
        const string progId = "Veil.Widget";

        using RegistryKey extensionKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{extension}")
            ?? throw new InvalidOperationException("Failed to create widget extension key.");
        extensionKey.SetValue(string.Empty, progId);

        using RegistryKey shellNewKey = extensionKey.CreateSubKey("ShellNew")
            ?? throw new InvalidOperationException("Failed to create widget ShellNew key.");
        shellNewKey.SetValue("Command", "\"" + appExePath + "\" --widget");
        shellNewKey.SetValue("ItemName", "Widget");

        using RegistryKey progIdKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}")
            ?? throw new InvalidOperationException("Failed to create widget ProgID key.");
        progIdKey.SetValue(string.Empty, "Widget");
        progIdKey.SetValue("FriendlyTypeName", "Widget");

        using RegistryKey defaultIconKey = progIdKey.CreateSubKey("DefaultIcon")
            ?? throw new InvalidOperationException("Failed to create widget icon key.");
        defaultIconKey.SetValue(string.Empty, "\"" + appExePath + "\",0");

        using RegistryKey openCommandKey = progIdKey.CreateSubKey(@"shell\open\command")
            ?? throw new InvalidOperationException("Failed to create widget open command key.");
        openCommandKey.SetValue(string.Empty, "\"" + appExePath + "\" --widget \"%1\"");
    }

    private static void UnregisterWidgetShellNew()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\.veilwidget", false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Veil.Widget", false);
    }

    private static void ScheduleDelete(string installDir, string parentDir)
    {
        string cleanupScriptPath = Path.Combine(
            Path.GetTempPath(),
            "Veil.Uninstall." + Guid.NewGuid().ToString("N") + ".cmd");
        string script = string.Join(
            "\r\n",
            "@echo off",
            "timeout /t 2 /nobreak >nul",
            "rmdir /s /q \"" + installDir + "\"",
            "rd \"" + parentDir + "\" 2>nul",
            "del \"%~f0\"");

        File.WriteAllText(cleanupScriptPath, script);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c \"" + cleanupScriptPath + "\"",
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }

    private static void StopVeilProcesses()
    {
        foreach (Process process in Process.GetProcessesByName("Veil"))
        {
            try
            {
                process.Kill(true);
                process.WaitForExit(5000);
            }
            catch
            {
            }
        }
    }
}
'@

[System.IO.File]::WriteAllText($installerProjectPath, $installerProject)
[System.IO.File]::WriteAllText($installerProgramPath, $installerProgram)

dotnet restore $installerProjectPath `
    -r $RuntimeIdentifier `
    --ignore-failed-sources `
    -p:RestoreIgnoreFailedSources=true

dotnet publish $installerProjectPath `
    -c Release `
    -r $RuntimeIdentifier `
    --self-contained true `
    --no-restore `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -o $installerPublishDir

$builtSetupExePath = Join-Path $installerPublishDir "VeilSetup.exe"

if (-not (Test-Path $builtSetupExePath)) {
    throw "Setup executable was not created."
}

Copy-Item -Path $builtSetupExePath -Destination $setupExePath -Force
Write-Host "Setup created at $setupExePath"
