using MudBlazor;

namespace SsisLineage.UI;

/// <summary>
/// Shared MudBlazor theme for the SSIS Lineage UI. The dark palette mirrors the
/// original slate/sky design; the light palette is a clean neutral counterpart.
/// </summary>
public static class LineageTheme
{
    public static readonly MudTheme Theme = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#0284c7",       // sky-600
            Secondary = "#7c3aed",     // violet-600
            Success = "#16a34a",
            Warning = "#d97706",
            Error = "#dc2626",
            Info = "#0ea5e9",
            Background = "#f1f5f9",
            BackgroundGray = "#e2e8f0",
            Surface = "#ffffff",
            AppbarBackground = "#0f172a",
            AppbarText = "#f8fafc",
            DrawerBackground = "#ffffff",
            TextPrimary = "#1e293b",
            TextSecondary = "#64748b",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#38bdf8",       // sky-400
            Secondary = "#a78bfa",     // violet-400
            Success = "#4ade80",
            Warning = "#fbbf24",
            Error = "#f87171",
            Info = "#38bdf8",
            Background = "#0f172a",     // slate-900
            BackgroundGray = "#1e293b",
            Surface = "#1e293b",        // slate-800
            AppbarBackground = "#0b1220",
            AppbarText = "#f8fafc",
            DrawerBackground = "#111c30",
            TextPrimary = "#f8fafc",
            TextSecondary = "#94a3b8",
            Tertiary = "#22d3ee",
            ActionDefault = "#94a3b8",
            DrawerText = "#e2e8f0",
            LinesDefault = "#334155",
            TableLines = "#334155",
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "8px",
            DrawerWidthLeft = "240px",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = new[] { "Segoe UI", "Inter", "Roboto", "Helvetica", "Arial", "sans-serif" } }
        }
    };
}
