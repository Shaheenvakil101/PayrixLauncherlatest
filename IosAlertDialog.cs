using System.Windows;
using System.Windows.Input;
using WpfCursors = System.Windows.Input.Cursors;
using System.Windows.Media.Effects;
// Resolve WPF vs WinForms / Drawing ambiguities with explicit aliases
using WpfButton   = System.Windows.Controls.Button;
using WpfBorder   = System.Windows.Controls.Border;
using WpfGrid     = System.Windows.Controls.Grid;
using WpfStack    = System.Windows.Controls.StackPanel;
using WpfText     = System.Windows.Controls.TextBlock;
using WpfContent  = System.Windows.Controls.ContentPresenter;
using WpfColDef   = System.Windows.Controls.ColumnDefinition;
using WpfTemplate = System.Windows.Controls.ControlTemplate;
using WpfColor    = System.Windows.Media.Color;
using WpfBrush    = System.Windows.Media.SolidColorBrush;
using WpfBrushes  = System.Windows.Media.Brushes;
using WpfFont     = System.Windows.Media.FontFamily;
using HA = System.Windows.HorizontalAlignment;
using VA = System.Windows.VerticalAlignment;

namespace PayrixLauncher;

/// <summary>
/// Adaptive alert dialog — Dynamic Island dark style when dark mode is active,
/// iOS 17 frosted style in light mode.
/// 300 px card · auto corner radius · hairline separators · theme-aware palette.
/// </summary>
public sealed class IosAlertDialog : Window
{
    // ── Shared font ───────────────────────────────────────────────────────────
    private static readonly WpfFont Font = new(
        "Segoe UI Variable Display, Segoe UI Variable Text, SF Pro Display, SF Pro Text, Segoe UI, Arial");

    public bool Confirmed { get; private set; }

    // ── Theme mode — set by MainWindow before calling Show() ─────────────────
    /// <summary>Set to true when the app is in dark mode before calling Show().</summary>
    public static bool IsDark { get; set; } = true;

    // ── Palette record ────────────────────────────────────────────────────────
    private readonly record struct Palette(
        WpfColor Card,
        WpfColor CardBorder,
        WpfColor TitleFg,
        WpfColor MsgFg,
        WpfColor Sep,
        WpfColor Blue,
        WpfColor Red,
        WpfColor HoverTint,
        WpfColor PressTint,
        double   ShadowOpacity,
        double   CornerRadius,
        double   CardWidth);

    private static Palette DarkPalette() => new(
        Card:          WpfColor.FromRgb(0x16, 0x16, 0x16),   // #161616
        CardBorder:    WpfColor.FromRgb(0x2C, 0x2C, 0x2C),   // #2C2C2C
        TitleFg:       WpfColor.FromRgb(0xFF, 0xFF, 0xFF),   // white
        MsgFg:         WpfColor.FromRgb(0x8C, 0x8C, 0x8C),   // #8C8C8C
        Sep:           WpfColor.FromRgb(0x2C, 0x2C, 0x2C),   // #2C2C2C
        Blue:          WpfColor.FromRgb(0x0A, 0x84, 0xFF),   // #0A84FF
        Red:           WpfColor.FromRgb(0xFF, 0x45, 0x3A),   // #FF453A
        HoverTint:     WpfColor.FromArgb(0x18, 0xFF, 0xFF, 0xFF),
        PressTint:     WpfColor.FromArgb(0x32, 0xFF, 0xFF, 0xFF),
        ShadowOpacity: 0.90,
        CornerRadius:  20,
        CardWidth:     300);

    private static Palette LightPalette() => new(
        Card:          WpfColor.FromRgb(242, 242, 247),       // iOS card
        CardBorder:    WpfColor.FromRgb(210, 210, 215),
        TitleFg:       WpfColor.FromRgb( 28,  28,  30),
        MsgFg:         WpfColor.FromRgb( 99,  99, 102),
        Sep:           WpfColor.FromRgb(198, 198, 200),
        Blue:          WpfColor.FromRgb(  0, 122, 255),
        Red:           WpfColor.FromRgb(255,  59,  48),
        HoverTint:     WpfColor.FromArgb(22, 0, 0, 0),
        PressTint:     WpfColor.FromArgb(55, 0, 0, 0),
        ShadowOpacity: 0.18,
        CornerRadius:  14,
        CardWidth:     270);

    // ── Public factory ────────────────────────────────────────────────────────
    public static bool Show(
        string  title,
        string  message,
        string  actionLabel,
        bool    destructive = false,
        Window? owner       = null)
    {
        var dlg = new IosAlertDialog(title, message, actionLabel, destructive);
        if (owner != null) dlg.Owner = owner;

        // Dim the owner window while the dialog is open (replaces full-screen overlay)
        if (owner != null)
        {
            owner.Opacity = 0.45;
            dlg.Closed += (_, _) => owner.Opacity = 1.0;
        }

        dlg.ShowDialog();
        return dlg.Confirmed;
    }

    // ── Constructor ───────────────────────────────────────────────────────────
    private IosAlertDialog(string title, string message, string actionLabel, bool destructive)
    {
        WindowStyle           = WindowStyle.None;
        AllowsTransparency    = true;
        Background            = WpfBrushes.Transparent;
        ResizeMode            = ResizeMode.NoResize;
        SizeToContent         = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar         = false;
        Topmost               = true;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Confirmed = false; Close(); } };

        var pal = IsDark ? DarkPalette() : LightPalette();
        var cr  = pal.CornerRadius;

        // ── Card ──────────────────────────────────────────────────────────────
        var card = new WpfBorder
        {
            Width           = pal.CardWidth,
            CornerRadius    = new CornerRadius(cr),
            Background      = new WpfBrush(pal.Card),
            BorderBrush     = new WpfBrush(pal.CardBorder),
            BorderThickness = new Thickness(1),
            Effect          = new DropShadowEffect
            {
                BlurRadius  = IsDark ? 50 : 40,
                ShadowDepth = 0,
                Opacity     = pal.ShadowOpacity,
                Color       = WpfColor.FromRgb(0, 0, 0)
            }
        };

        // ── Title ─────────────────────────────────────────────────────────────
        var titleBlock = new WpfText
        {
            Text                = title,
            FontFamily          = Font,
            FontSize            = 17,
            FontWeight          = FontWeights.SemiBold,
            Foreground          = new WpfBrush(pal.TitleFg),
            TextAlignment       = TextAlignment.Center,
            TextWrapping        = TextWrapping.Wrap,
            Margin              = new Thickness(20, 20, 20, 0),
            HorizontalAlignment = HA.Center
        };

        // ── Message ───────────────────────────────────────────────────────────
        var msgBlock = new WpfText
        {
            Text                = message,
            FontFamily          = Font,
            FontSize            = 13,
            Foreground          = new WpfBrush(pal.MsgFg),
            TextAlignment       = TextAlignment.Center,
            TextWrapping        = TextWrapping.Wrap,
            Margin              = new Thickness(20, 6, 20, 20),
            HorizontalAlignment = HA.Center,
            MaxWidth            = pal.CardWidth - 40   // keep text inside card padding
        };

        // ── Horizontal separator ──────────────────────────────────────────────
        var hSep = new WpfBorder
        {
            Height              = 0.5,
            Background          = new WpfBrush(pal.Sep),
            HorizontalAlignment = HA.Stretch
        };

        // ── Button row ────────────────────────────────────────────────────────
        var btnGrid = new WpfGrid { Height = 48 };
        btnGrid.ColumnDefinitions.Add(new WpfColDef { Width = new GridLength(1, GridUnitType.Star) });
        btnGrid.ColumnDefinitions.Add(new WpfColDef { Width = new GridLength(0.5) });
        btnGrid.ColumnDefinitions.Add(new WpfColDef { Width = new GridLength(1, GridUnitType.Star) });

        var vSep = new WpfBorder
        {
            Width             = 0.5,
            Background        = new WpfBrush(pal.Sep),
            VerticalAlignment = VA.Stretch
        };
        WpfGrid.SetColumn(vSep, 1);

        var cancelBtn = Btn("Cancel", pal.Blue, FontWeights.Normal,
            bottomLeft: cr, bottomRight: 0, pal.HoverTint, pal.PressTint);
        WpfGrid.SetColumn(cancelBtn, 0);
        cancelBtn.Click += (_, _) => { Confirmed = false; Close(); };

        var actionBtn = Btn(actionLabel, destructive ? pal.Red : pal.Blue, FontWeights.SemiBold,
            bottomLeft: 0, bottomRight: cr, pal.HoverTint, pal.PressTint);
        WpfGrid.SetColumn(actionBtn, 2);
        actionBtn.Click += (_, _) => { Confirmed = true; Close(); };

        btnGrid.Children.Add(cancelBtn);
        btnGrid.Children.Add(vSep);
        btnGrid.Children.Add(actionBtn);

        // ── Assemble ──────────────────────────────────────────────────────────
        var stack = new WpfStack();
        stack.Children.Add(titleBlock);
        stack.Children.Add(msgBlock);
        stack.Children.Add(hSep);
        stack.Children.Add(btnGrid);
        card.Child = stack;

        // Window IS the card — no full-screen root grid needed.
        // Padding gives the drop-shadow room to render without clipping.
        Content = new WpfBorder
        {
            Background = WpfBrushes.Transparent,
            Padding    = new Thickness(32),
            Child      = card
        };
    }

    // ── Button helper ─────────────────────────────────────────────────────────
    private static WpfButton Btn(
        string text, WpfColor fg, FontWeight fw,
        double bottomLeft, double bottomRight,
        WpfColor hoverTint, WpfColor pressTint)
    {
        var btn = new WpfButton
        {
            Content                    = text,
            FontFamily                 = Font,
            FontSize                   = 17,
            FontWeight                 = fw,
            Foreground                 = new WpfBrush(fg),
            Background                 = WpfBrushes.Transparent,
            BorderThickness            = new Thickness(0),
            Cursor                     = WpfCursors.Hand,
            HorizontalAlignment        = HA.Stretch,
            VerticalAlignment          = VA.Stretch,
            HorizontalContentAlignment = HA.Center,
            VerticalContentAlignment   = VA.Center,
        };

        var tmpl  = new WpfTemplate(typeof(WpfButton));
        var bdFef = new FrameworkElementFactory(typeof(WpfBorder));
        bdFef.Name = "Bd";
        bdFef.SetValue(WpfBorder.BackgroundProperty,   WpfBrushes.Transparent);
        bdFef.SetValue(WpfBorder.CornerRadiusProperty, new CornerRadius(0, 0, bottomRight, bottomLeft));

        var cpFef = new FrameworkElementFactory(typeof(WpfContent));
        cpFef.SetValue(WpfContent.HorizontalAlignmentProperty, HA.Center);
        cpFef.SetValue(WpfContent.VerticalAlignmentProperty,   VA.Center);
        bdFef.AppendChild(cpFef);
        tmpl.VisualTree = bdFef;

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(WpfBorder.BackgroundProperty, new WpfBrush(hoverTint), "Bd"));
        tmpl.Triggers.Add(hover);

        var press = new Trigger { Property = WpfButton.IsPressedProperty, Value = true };
        press.Setters.Add(new Setter(WpfBorder.BackgroundProperty, new WpfBrush(pressTint), "Bd"));
        tmpl.Triggers.Add(press);

        btn.Template = tmpl;
        return btn;
    }
}
