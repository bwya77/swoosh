using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Swoosh.Settings;

namespace Swoosh.SettingsApp;

public sealed record InstalledAppEntry(string DisplayName, string ProcessName, string IconPath);

public static class InstalledAppCatalog
{
    private static readonly string IconCacheDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Swoosh", "AppIcons");

    public static IReadOnlyList<InstalledAppEntry> Load()
    {
        Directory.CreateDirectory(IconCacheDir);

        var entries = new Dictionary<string, InstalledAppEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (string shortcut in StartMenuShortcuts())
        {
            if (!TryResolveShortcut(shortcut, out string target) ||
                !File.Exists(target) ||
                !string.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase))
                continue;

            string processName = AppCompatibility.NormalizeProcessName(target);
            if (processName.Length == 0) continue;

            string displayName = Path.GetFileNameWithoutExtension(shortcut);
            string key = $"{processName}|{displayName}";
            if (entries.ContainsKey(key)) continue;

            string iconPath = ExtractIcon(target, processName);
            entries[key] = new InstalledAppEntry(displayName, processName, iconPath);
        }

        return entries.Values
            .OrderBy(static app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static app => app.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> StartMenuShortcuts()
    {
        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        string user = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        foreach (string root in new[] { common, user })
        {
            if (!Directory.Exists(root)) continue;
            foreach (string shortcut in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                yield return shortcut;
        }
    }

    private static string ExtractIcon(string exePath, string processName)
    {
        string iconPath = Path.Combine(IconCacheDir, $"{processName}.png");
        if (File.Exists(iconPath)) return iconPath;

        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon == null) return string.Empty;
            using var bitmap = icon.ToBitmap();
            bitmap.Save(iconPath, ImageFormat.Png);
            return iconPath;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool TryResolveShortcut(string shortcutPath, out string targetPath)
    {
        targetPath = string.Empty;
        try
        {
            var link = (IShellLinkW)(object)new ShellLink();
            ((IPersistFile)link).Load(shortcutPath, 0);
            var path = new StringBuilder(1024);
            link.GetPath(path, path.Capacity, IntPtr.Zero, 0);
            targetPath = Environment.ExpandEnvironmentVariables(path.ToString());
            return targetPath.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
