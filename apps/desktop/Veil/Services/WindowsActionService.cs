namespace Veil.Services;

internal static class WindowsActionService
{
    private static FinderEntry[]? _actions;

    internal static FinderEntry[] GetActions()
    {
        return _actions ??= BuildActions();
    }

    private static FinderEntry[] BuildActions()
    {
        var actions = new List<FinderEntry>();

        AddSetting(actions, "Display Settings", "\uE7F8", "ms-settings:display");
        AddSetting(actions, "Sound Settings", "\uE767", "ms-settings:sound");
        AddSetting(actions, "Notifications", "\uEA8F", "ms-settings:notifications");
        AddSetting(actions, "Power & Battery", "\uEBA6", "ms-settings:powersleep");
        AddSetting(actions, "Storage", "\uEDA2", "ms-settings:storagesense");
        AddSetting(actions, "Multitasking", "\uE737", "ms-settings:multitasking");
        AddSetting(actions, "Wi-Fi Settings", "\uE701", "ms-settings:network-wifi");
        AddSetting(actions, "Bluetooth Settings", "\uE702", "ms-settings:bluetooth");
        AddSetting(actions, "VPN Settings", "\uE705", "ms-settings:network-vpn");
        AddSetting(actions, "Network & Internet", "\uE774", "ms-settings:network");
        AddSetting(actions, "Airplane Mode", "\uE709", "ms-settings:network-airplanemode");
        AddSetting(actions, "Proxy Settings", "\uE774", "ms-settings:network-proxy");
        AddSetting(actions, "Apps & Features", "\uE71D", "ms-settings:appsfeatures");
        AddSetting(actions, "Default Apps", "\uE737", "ms-settings:defaultapps");
        AddSetting(actions, "Startup Apps", "\uE7B5", "ms-settings:startupapps");
        AddSetting(actions, "Accounts", "\uE77B", "ms-settings:yourinfo");
        AddSetting(actions, "Sign-in Options", "\uE72E", "ms-settings:signinoptions");
        AddSetting(actions, "Time & Date", "\uE787", "ms-settings:dateandtime");
        AddSetting(actions, "Language & Region", "\uE774", "ms-settings:regionlanguage");
        AddSetting(actions, "Personalization", "\uE771", "ms-settings:personalization");
        AddSetting(actions, "Background", "\uE7B5", "ms-settings:personalization-background");
        AddSetting(actions, "Colors", "\uE790", "ms-settings:colors");
        AddSetting(actions, "Themes", "\uE771", "ms-settings:themes");
        AddSetting(actions, "Lock Screen", "\uE72E", "ms-settings:lockscreen");
        AddSetting(actions, "Taskbar Settings", "\uE700", "ms-settings:taskbar");
        AddSetting(actions, "Start Menu Settings", "\uE700", "ms-settings:personalization-start");
        AddSetting(actions, "Mouse Settings", "\uE962", "ms-settings:mousetouchpad");
        AddSetting(actions, "Keyboard Settings", "\uE765", "ms-settings:easeofaccess-keyboard");
        AddSetting(actions, "Touchpad Settings", "\uE962", "ms-settings:devices-touchpad");
        AddSetting(actions, "Printers & Scanners", "\uE772", "ms-settings:printers");
        AddSetting(actions, "Windows Update", "\uE777", "ms-settings:windowsupdate");
        AddSetting(actions, "Windows Security", "\uE72E", "ms-settings:windowsdefender");
        AddSetting(actions, "Privacy & Security", "\uE72E", "ms-settings:privacy");
        AddSetting(actions, "Accessibility", "\uE776", "ms-settings:easeofaccess");
        AddSetting(actions, "Gaming Settings", "\uE7FC", "ms-settings:gaming");
        AddSetting(actions, "About This PC", "\uE7F4", "ms-settings:about");
        AddSetting(actions, "System Info", "\uE946", "ms-settings:about");
        AddSetting(actions, "Night Light", "\uE793", "ms-settings:nightlight");
        AddSetting(actions, "Focus Assist", "\uE72C", "ms-settings:quiethours");

        AddCommand(actions, "Lock Computer", "\uE72E", "System",
            "rundll32.exe", "user32.dll,LockWorkStation");
        AddCommand(actions, "Sign Out", "\uE77B", "System",
            "shutdown.exe", "/l");
        AddCommand(actions, "Shutdown", "\uE7E8", "System",
            "shutdown.exe", "/s /t 0");
        AddCommand(actions, "Restart", "\uE72C", "System",
            "shutdown.exe", "/r /t 0");
        AddCommand(actions, "Sleep", "\uE708", "System",
            "rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0");
        AddCommand(actions, "Task Manager", "\uE9D9", "System",
            "taskmgr.exe", "");
        AddCommand(actions, "Control Panel", "\uE713", "System",
            "control.exe", "");
        AddCommand(actions, "Device Manager", "\uE7F4", "System",
            "devmgmt.msc", "");
        AddCommand(actions, "Disk Management", "\uEDA2", "System",
            "diskmgmt.msc", "");
        AddCommand(actions, "Services", "\uE713", "System",
            "services.msc", "");
        AddCommand(actions, "Event Viewer", "\uE7BC", "System",
            "eventvwr.msc", "");
        AddCommand(actions, "System Information", "\uE946", "System",
            "msinfo32.exe", "");
        AddCommand(actions, "Disk Cleanup", "\uEDA2", "System",
            "cleanmgr.exe", "");
        AddCommand(actions, "Registry Editor", "\uE7BC", "System",
            "regedit.exe", "");
        AddCommand(actions, "Environment Variables", "\uE713", "System",
            "rundll32.exe", "sysdm.cpl,EditEnvironmentVariables");
        AddCommand(actions, "Empty Recycle Bin", "\uE74D", "System",
            "cmd.exe", "/c rd /s /q C:\\$Recycle.Bin");

        return actions.ToArray();
    }

    private static void AddSetting(List<FinderEntry> list, string name, string glyph, string uri)
    {
        list.Add(new FinderEntry(name, "Settings", InstalledAppService.NormalizeText(name), null, glyph)
        {
            LaunchUri = uri
        });
    }

    private static void AddCommand(List<FinderEntry> list, string name, string glyph, string category,
        string command, string args)
    {
        list.Add(new FinderEntry(name, category, InstalledAppService.NormalizeText(name), null, glyph)
        {
            LaunchCommand = command,
            LaunchArgs = args
        });
    }
}
