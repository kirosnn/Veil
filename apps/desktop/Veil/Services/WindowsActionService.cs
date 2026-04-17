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

        // Display & Graphics
        AddSetting(actions, "Display Settings", "\uE7F8", "ms-settings:display");
        AddSetting(actions, "Night Light", "\uE793", "ms-settings:nightlight");
        AddSetting(actions, "HDR Settings", "\uE7F8", "ms-settings:display-advancedgraphics");
        AddSetting(actions, "Graphics Settings", "\uE7F8", "ms-settings:display-advancedgraphics");
        AddSetting(actions, "Scale & Layout", "\uE740", "ms-settings:display");

        // Sound & Audio
        AddSetting(actions, "Sound Settings", "\uE767", "ms-settings:sound");
        AddSetting(actions, "Volume Mixer", "\uE767", "ms-settings:apps-volume");
        AddSetting(actions, "Sound Input", "\uE720", "ms-settings:sound");
        AddSetting(actions, "Sound Output", "\uE767", "ms-settings:sound");

        // Notifications & Focus
        AddSetting(actions, "Notifications", "\uEA8F", "ms-settings:notifications");
        AddSetting(actions, "Focus Assist", "\uE72C", "ms-settings:quiethours");

        // Power & Battery
        AddSetting(actions, "Power & Battery", "\uEBA6", "ms-settings:powersleep");
        AddSetting(actions, "Power Plan", "\uEBA6", "ms-settings:powersleep");

        // Storage
        AddSetting(actions, "Storage", "\uEDA2", "ms-settings:storagesense");
        AddSetting(actions, "Storage Sense", "\uEDA2", "ms-settings:storagepolicies");
        AddSetting(actions, "Temporary Files", "\uEDA2", "ms-settings:storagesense");

        // Multitasking & Clipboard
        AddSetting(actions, "Multitasking", "\uE737", "ms-settings:multitasking");
        AddSetting(actions, "Clipboard", "\uE77F", "ms-settings:clipboard");
        AddSetting(actions, "Nearby Sharing", "\uE774", "ms-settings:crossdevice");
        AddSetting(actions, "Remote Desktop", "\uE7F4", "ms-settings:remotedesktop");
        AddSetting(actions, "Projecting to PC", "\uE7F4", "ms-settings:project");

        // Network & Internet
        AddSetting(actions, "Wi-Fi Settings", "\uE701", "ms-settings:network-wifi");
        AddSetting(actions, "Bluetooth Settings", "\uE702", "ms-settings:bluetooth");
        AddSetting(actions, "VPN Settings", "\uE705", "ms-settings:network-vpn");
        AddSetting(actions, "Network & Internet", "\uE774", "ms-settings:network");
        AddSetting(actions, "Airplane Mode", "\uE709", "ms-settings:network-airplanemode");
        AddSetting(actions, "Proxy Settings", "\uE774", "ms-settings:network-proxy");
        AddSetting(actions, "Mobile Hotspot", "\uE701", "ms-settings:network-mobilehotspot");
        AddSetting(actions, "Ethernet Settings", "\uE839", "ms-settings:network-ethernet");
        AddSetting(actions, "Data Usage", "\uE774", "ms-settings:datausage");
        AddSetting(actions, "Dial-up", "\uE774", "ms-settings:network-dialup");
        AddSetting(actions, "Advanced Network", "\uE774", "ms-settings:network-advancedsettings");

        // Apps
        AddSetting(actions, "Apps & Features", "\uE71D", "ms-settings:appsfeatures");
        AddSetting(actions, "Default Apps", "\uE737", "ms-settings:defaultapps");
        AddSetting(actions, "Startup Apps", "\uE7B5", "ms-settings:startupapps");
        AddSetting(actions, "Optional Features", "\uE71D", "ms-settings:optionalfeatures");
        AddSetting(actions, "Offline Maps", "\uE707", "ms-settings:maps");
        AddSetting(actions, "Apps for Websites", "\uE774", "ms-settings:appsforwebsites");
        AddSetting(actions, "Video Playback", "\uE714", "ms-settings:videoplayback");
        AddSetting(actions, "Autoplay", "\uE713", "ms-settings:autoplay");

        // Accounts
        AddSetting(actions, "Accounts", "\uE77B", "ms-settings:yourinfo");
        AddSetting(actions, "Sign-in Options", "\uE72E", "ms-settings:signinoptions");
        AddSetting(actions, "Email & Accounts", "\uE715", "ms-settings:emailandaccounts");
        AddSetting(actions, "Family & Other Users", "\uE77B", "ms-settings:otherusers");
        AddSetting(actions, "Sync Settings", "\uE895", "ms-settings:sync");
        AddSetting(actions, "Windows Backup", "\uE895", "ms-settings:backup");

        // Time & Language
        AddSetting(actions, "Time & Date", "\uE787", "ms-settings:dateandtime");
        AddSetting(actions, "Language & Region", "\uE774", "ms-settings:regionlanguage");
        AddSetting(actions, "Typing Settings", "\uE765", "ms-settings:typing");
        AddSetting(actions, "Speech Settings", "\uE720", "ms-settings:speech");

        // Personalization
        AddSetting(actions, "Personalization", "\uE771", "ms-settings:personalization");
        AddSetting(actions, "Background", "\uE7B5", "ms-settings:personalization-background");
        AddSetting(actions, "Colors", "\uE790", "ms-settings:colors");
        AddSetting(actions, "Themes", "\uE771", "ms-settings:themes");
        AddSetting(actions, "Lock Screen", "\uE72E", "ms-settings:lockscreen");
        AddSetting(actions, "Taskbar Settings", "\uE700", "ms-settings:taskbar");
        AddSetting(actions, "Start Menu Settings", "\uE700", "ms-settings:personalization-start");
        AddSetting(actions, "Fonts", "\uE8D2", "ms-settings:fonts");
        AddSetting(actions, "Dynamic Lighting", "\uE790", "ms-settings:personalization-lighting");
        AddSetting(actions, "Text Input", "\uE765", "ms-settings:personalization-textinput");
        AddSetting(actions, "Device Usage", "\uE713", "ms-settings:deviceusage");

        // Devices & Peripherals
        AddSetting(actions, "Mouse Settings", "\uE962", "ms-settings:mousetouchpad");
        AddSetting(actions, "Keyboard Settings", "\uE765", "ms-settings:easeofaccess-keyboard");
        AddSetting(actions, "Touchpad Settings", "\uE962", "ms-settings:devices-touchpad");
        AddSetting(actions, "Pen & Windows Ink", "\uE765", "ms-settings:pen");
        AddSetting(actions, "Printers & Scanners", "\uE772", "ms-settings:printers");
        AddSetting(actions, "Camera Settings", "\uE714", "ms-settings:camera");
        AddSetting(actions, "USB Settings", "\uE88E", "ms-settings:usb");

        // Update & Security
        AddSetting(actions, "Windows Update", "\uE777", "ms-settings:windowsupdate");
        AddSetting(actions, "Update History", "\uE777", "ms-settings:windowsupdate-history");
        AddSetting(actions, "Delivery Optimization", "\uE777", "ms-settings:delivery-optimization");
        AddSetting(actions, "Windows Security", "\uE72E", "ms-settings:windowsdefender");
        AddSetting(actions, "Firewall & Network", "\uE72E", "ms-settings:windowsdefender");
        AddSetting(actions, "Recovery", "\uE777", "ms-settings:recovery");
        AddSetting(actions, "Activation", "\uE72E", "ms-settings:activation");
        AddSetting(actions, "Find My Device", "\uE707", "ms-settings:findmydevice");
        AddSetting(actions, "Developer Settings", "\uEC7A", "ms-settings:developers");

        // Privacy
        AddSetting(actions, "Privacy & Security", "\uE72E", "ms-settings:privacy");
        AddSetting(actions, "Microphone Privacy", "\uE720", "ms-settings:privacy-microphone");
        AddSetting(actions, "Camera Privacy", "\uE714", "ms-settings:privacy-webcam");
        AddSetting(actions, "Location Privacy", "\uE707", "ms-settings:privacy-location");
        AddSetting(actions, "Activity History", "\uE81C", "ms-settings:privacy-activityhistory");
        AddSetting(actions, "Diagnostics & Feedback", "\uE9D9", "ms-settings:privacy-feedback");
        AddSetting(actions, "App Permissions", "\uE71D", "ms-settings:appsfeatures-app");
        AddSetting(actions, "Background Apps", "\uE71D", "ms-settings:privacy-backgroundapps");
        AddSetting(actions, "Search Permissions", "\uE721", "ms-settings:search-permissions");

        // Accessibility
        AddSetting(actions, "Accessibility", "\uE776", "ms-settings:easeofaccess");
        AddSetting(actions, "Text Size", "\uE8D2", "ms-settings:easeofaccess-display");
        AddSetting(actions, "Visual Effects", "\uE7F8", "ms-settings:easeofaccess-visualeffects");
        AddSetting(actions, "Mouse Pointer", "\uE962", "ms-settings:easeofaccess-mousepointer");
        AddSetting(actions, "Color Filters", "\uE790", "ms-settings:easeofaccess-colorfilter");
        AddSetting(actions, "High Contrast", "\uE790", "ms-settings:easeofaccess-highcontrast");
        AddSetting(actions, "Narrator", "\uE767", "ms-settings:easeofaccess-narrator");
        AddSetting(actions, "Magnifier", "\uE71E", "ms-settings:easeofaccess-magnifier");
        AddSetting(actions, "Closed Captions", "\uE8D2", "ms-settings:easeofaccess-closedcaptioning");
        AddSetting(actions, "Cursor & Pointer Speed", "\uE962", "ms-settings:easeofaccess-cursor");

        // Gaming
        AddSetting(actions, "Gaming Settings", "\uE7FC", "ms-settings:gaming");
        AddSetting(actions, "Game Bar", "\uE7FC", "ms-settings:gaming-gamebar");
        AddSetting(actions, "Game Mode", "\uE7FC", "ms-settings:gaming-gamemode");
        AddSetting(actions, "Game DVR", "\uE714", "ms-settings:gaming-gamedvr");

        // System Info
        AddSetting(actions, "About This PC", "\uE7F4", "ms-settings:about");
        AddSetting(actions, "System Info", "\uE946", "ms-settings:about");

        // Power & Session
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
        AddCommand(actions, "Hibernate", "\uE708", "System",
            "shutdown.exe", "/h");

        // System Tools
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
        AddCommand(actions, "Computer Management", "\uE713", "System",
            "compmgmt.msc", "");
        AddCommand(actions, "Resource Monitor", "\uE9D9", "System",
            "resmon.exe", "");
        AddCommand(actions, "Performance Monitor", "\uE9D9", "System",
            "perfmon.exe", "");
        AddCommand(actions, "Local Group Policy", "\uE713", "System",
            "gpedit.msc", "");
        AddCommand(actions, "Local Security Policy", "\uE72E", "System",
            "secpol.msc", "");
        AddCommand(actions, "Windows Firewall", "\uE72E", "System",
            "wf.msc", "");
        AddCommand(actions, "Network Connections", "\uE774", "System",
            "ncpa.cpl", "");
        AddCommand(actions, "Programs and Features", "\uE71D", "System",
            "appwiz.cpl", "");
        AddCommand(actions, "System Properties", "\uE713", "System",
            "sysdm.cpl", "");
        AddCommand(actions, "Advanced System Settings", "\uE713", "System",
            "SystemPropertiesAdvanced.exe", "");
        AddCommand(actions, "Internet Options", "\uE774", "System",
            "inetcpl.cpl", "");
        AddCommand(actions, "Power Options", "\uEBA6", "System",
            "powercfg.cpl", "");
        AddCommand(actions, "User Accounts", "\uE77B", "System",
            "netplwiz.exe", "");
        AddCommand(actions, "Credential Manager", "\uE72E", "System",
            "control.exe", "/name Microsoft.CredentialManager");
        AddCommand(actions, "File Explorer Options", "\uE8B7", "System",
            "control.exe", "folders");
        AddCommand(actions, "DirectX Diagnostic", "\uE7FC", "System",
            "dxdiag.exe", "");
        AddCommand(actions, "Shared Folders", "\uE8B7", "System",
            "fsmgmt.msc", "");
        AddCommand(actions, "Reliability Monitor", "\uE9D9", "System",
            "perfmon.exe", "/rel");

        // Utilities
        AddCommand(actions, "Snipping Tool", "\uE714", "Utility",
            "SnippingTool.exe", "");
        AddCommand(actions, "Calculator", "\uE8EF", "Utility",
            "calc.exe", "");
        AddCommand(actions, "Notepad", "\uE70F", "Utility",
            "notepad.exe", "");
        AddCommand(actions, "Paint", "\uE790", "Utility",
            "mspaint.exe", "");
        AddCommand(actions, "Character Map", "\uE8D2", "Utility",
            "charmap.exe", "");
        AddCommand(actions, "On-Screen Keyboard", "\uE765", "Utility",
            "osk.exe", "");
        AddCommand(actions, "Magnifier", "\uE71E", "Utility",
            "magnify.exe", "");
        AddCommand(actions, "Steps Recorder", "\uE714", "Utility",
            "psr.exe", "");
        AddCommand(actions, "Remote Desktop", "\uE7F4", "Utility",
            "mstsc.exe", "");
        AddCommand(actions, "Windows Terminal", "\uE756", "Utility",
            "wt.exe", "");
        AddCommand(actions, "PowerShell", "\uE756", "Utility",
            "powershell.exe", "");
        AddCommand(actions, "Command Prompt", "\uE756", "Utility",
            "cmd.exe", "");

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

    private static void AddAction(List<FinderEntry> list, string name, string glyph, string category, string actionId)
    {
        list.Add(new FinderEntry(name, category, InstalledAppService.NormalizeText(name), null, glyph)
        {
            ActionId = actionId
        });
    }
}
