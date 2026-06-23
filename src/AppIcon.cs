using System.Drawing;
using System.Windows.Forms;

namespace TheTagHag;

/// <summary>Loads the embedded hag-tag app icon once and applies it to a window's titlebar /
/// alt-tab. The exe file icon is set separately via &lt;ApplicationIcon&gt;; this covers the
/// per-Form icon, which WinForms otherwise leaves as its generic default.</summary>
internal static class AppIcon
{
    private static Icon? _icon;
    private static bool _tried;

    public static Icon? Value
    {
        get
        {
            if (_tried) return _icon;
            _tried = true;
            try
            {
                var asm = typeof(AppIcon).Assembly;
                var name = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));
                if (name is null) return null;
                using var s = asm.GetManifestResourceStream(name)!;
                _icon = new Icon(s);
            }
            catch { _icon = null; }
            return _icon;
        }
    }

    public static void Apply(Form form)
    {
        var ico = Value;
        if (ico is not null) form.Icon = ico;
    }
}
