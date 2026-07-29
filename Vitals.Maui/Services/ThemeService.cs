namespace Vitals.Maui.Services;

public static class ThemeService
{
    public static void Apply(string theme)
    {
        var res = Application.Current!.Resources;

        switch (theme)
        {
            case "light":
                res["PageBackground"] = Color.FromArgb("#f5f5f5");
                res["CardBackground"] = Color.FromArgb("#ffffff");
                res["CardStroke"] = Color.FromArgb("#e0e0e0");
                res["PrimaryAccent"] = Color.FromArgb("#1565c0");
                res["TextPrimary"] = Color.FromArgb("#212121");
                res["TextSecondary"] = Color.FromArgb("#757575");
                res["TextMuted"] = Color.FromArgb("#9e9e9e");
                res["ButtonBackground"] = Color.FromArgb("#1976d2");
                res["TileNormalBg"] = Color.FromArgb("#e8f5e9");
                res["TileLowBg"] = Color.FromArgb("#e3f2fd");
                res["TileSevereBg"] = Color.FromArgb("#ffebee");
                res["TileCriticalBg"] = Color.FromArgb("#fce4ec");
                res["DividerColor"] = Color.FromArgb("#e0e0e0");
                res["ShellForeground"] = Color.FromArgb("#212121");
                res["ShellTitle"] = Color.FromArgb("#212121");
                res["ButtonSecondary"] = Color.FromArgb("#90a4ae");
                res["ButtonSecondary"] = Color.FromArgb("#90a4ae");
                res["ButtonSecondaryText"] = Color.FromArgb("#ffffff");
                break;

            case "vitals_blue":
                res["PageBackground"] = Color.FromArgb("#e8f4f8");
                res["CardBackground"] = Color.FromArgb("#f0f9ff");
                res["CardStroke"] = Color.FromArgb("#b2dff2");
                res["PrimaryAccent"] = Color.FromArgb("#006e8c");
                res["TextPrimary"] = Color.FromArgb("#0d2137");
                res["TextSecondary"] = Color.FromArgb("#546e7a");
                res["TextMuted"] = Color.FromArgb("#78909c");
                res["ButtonBackground"] = Color.FromArgb("#00acc1");
                res["TileNormalBg"] = Color.FromArgb("#e0f4f0");
                res["TileLowBg"] = Color.FromArgb("#e1f5fe");
                res["TileSevereBg"] = Color.FromArgb("#e8f5fd");
                res["TileCriticalBg"] = Color.FromArgb("#b2ebf2");
                res["DividerColor"] = Color.FromArgb("#b2dff2");
                res["ShellForeground"] = Color.FromArgb("#0d2137");
                res["ShellTitle"] = Color.FromArgb("#0d2137");
                res["ButtonSecondary"] = Color.FromArgb("#b2dff2");
                res["ButtonSecondary"] = Color.FromArgb("#b2dff2");
                res["ButtonSecondaryText"] = Color.FromArgb("#0d2137");
                break;

            case "system":
                var isLight = Application.Current.RequestedTheme
                              == AppTheme.Light;
                Apply(isLight ? "light" : "dark");
                return;

            default: // "dark"
                res["PageBackground"] = Color.FromArgb("#121212");
                res["CardBackground"] = Color.FromArgb("#1e1e1e");
                res["CardStroke"] = Color.FromArgb("#333333");
                res["PrimaryAccent"] = Color.FromArgb("#90caf9");
                res["TextPrimary"] = Colors.White;
                res["TextSecondary"] = Color.FromArgb("#aaaaaa");
                res["TextMuted"] = Color.FromArgb("#666666");
                res["ButtonBackground"] = Color.FromArgb("#1a73e8");
                res["TileNormalBg"] = Color.FromArgb("#1b2a1b");
                res["TileLowBg"] = Color.FromArgb("#1a2a3a");
                res["TileSevereBg"] = Color.FromArgb("#2a1010");
                res["TileCriticalBg"] = Color.FromArgb("#1a0000");
                res["DividerColor"] = Color.FromArgb("#333333");
                res["ShellForeground"] = Colors.White;
                res["ShellTitle"] = Colors.White;
                res["ButtonSecondary"] = Color.FromArgb("#0f3460");
                res["ButtonSecondary"] = Color.FromArgb("#0f3460");
                res["ButtonSecondaryText"] = Color.FromArgb("#ffffff");
                break;
        }
    }
}