using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

// ponytail: whole app marked Windows-only (registry, shlwapi); split platform code out when porting
[assembly: SupportedOSPlatform("windows")]

namespace EzView;

// Registers ezview for every supported file type, per user (HKCU, no admin needed).
// Windows doesn't let apps make themselves the default; this puts ezview in "Open with" for all types
// and gives it a page under Settings > Apps > Default apps where the user picks it per type.
static partial class Registration
{
    const string Classes = @"Software\Classes";
    const string Capabilities = @"Software\ezview\Capabilities";
    static readonly string[] ProgIds = ["ezview.image", "ezview.video", "ezview.audio", "ezview.model"];

    static string ProgIdFor(string ext) => Media.AudioExt.Contains(ext) ? "ezview.audio" : Media.KindOf("x" + ext) switch
    {
        Kind.Video => "ezview.video",
        Kind.Model => "ezview.model",
        _ => "ezview.image",
    };

    public static void Register()
    {
        var exe = Environment.ProcessPath!;
        var open = $"\"{exe}\" \"%1\"";
        var root = Registry.CurrentUser;

        foreach (var (id, name) in ProgIds.Zip(["ezview Image", "ezview Video", "ezview Audio", "ezview 3D Model"]))
        {
            using var k = root.CreateSubKey($@"{Classes}\{id}");
            k.SetValue("", name);
            k.SetValue("FriendlyTypeName", name);
            using (var icon = k.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
            using (var cmd = k.CreateSubKey(@"shell\open\command")) cmd.SetValue("", open);
        }

        using var app = root.CreateSubKey($@"{Classes}\Applications\ezview.exe");
        app.SetValue("FriendlyAppName", "ezview");
        using (var cmd = app.CreateSubKey(@"shell\open\command")) cmd.SetValue("", open);
        using var supported = app.CreateSubKey("SupportedTypes");

        using var caps = root.CreateSubKey(Capabilities);
        caps.SetValue("ApplicationName", "ezview");
        caps.SetValue("ApplicationDescription", "Fast viewer for images, video, audio and 3D models");
        using var assoc = caps.CreateSubKey("FileAssociations");

        foreach (var ext in Media.AllExtensions)
        {
            var id = ProgIdFor(ext);
            using (var owp = root.CreateSubKey($@"{Classes}\{ext}\OpenWithProgids")) owp.SetValue(id, Array.Empty<byte>(), RegistryValueKind.None);
            supported.SetValue(ext, "");
            assoc.SetValue(ext, id);
        }

        using (var reg = root.CreateSubKey(@"Software\RegisteredApplications")) reg.SetValue("ezview", Capabilities);
        Refresh();
    }

    public static void Unregister()
    {
        var root = Registry.CurrentUser;
        foreach (var id in ProgIds) root.DeleteSubKeyTree($@"{Classes}\{id}", false);
        root.DeleteSubKeyTree($@"{Classes}\Applications\ezview.exe", false);
        root.DeleteSubKeyTree(@"Software\ezview", false);
        using (var reg = root.OpenSubKey(@"Software\RegisteredApplications", true)) reg?.DeleteValue("ezview", false);
        foreach (var ext in Media.AllExtensions)
            using (var owp = root.OpenSubKey($@"{Classes}\{ext}\OpenWithProgids", true))
                owp?.DeleteValue(ProgIdFor(ext), false);
        Refresh();
    }

    // The Default apps page for ezview, where each type is switched over with one click.
    public static void OpenDefaultAppsSettings() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=ezview") { UseShellExecute = true });

    // Tell Explorer the associations changed, so icons and "Open with" update without a restart.
    static void Refresh() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, 0, 0);

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}
