using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using RedBloom.Models;
using RedBloom.Services;

namespace RedBloom.Views;

/// <summary>The appearance page, shown as a tab rather than a modal dialog.</summary>
public partial class SettingsPage : UserControl
{
    private readonly AppSettings _settings = ThemeService.Settings;
    private readonly ObservableCollection<ColorEntry> _terminalColors = [];
    private readonly ObservableCollection<ColorEntry> _ansiColors = [];
    private readonly ObservableCollection<ColorEntry> _appColors = [];
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();

        BuildColorLists();
        BuildBackdropEditors();
        PopulateFonts();
        LoadValues();
        RefreshPreview();

        ThemeService.Applied += OnThemeApplied;
        LocalizationService.Changed += OnLanguageChanged;
        Unloaded += (_, _) =>
        {
            ThemeService.Applied -= OnThemeApplied;
            LocalizationService.Changed -= OnLanguageChanged;
        };

        _loading = false;
    }

    /// <summary>Rebuilds the text this page sets from code, which DynamicResource cannot reach.</summary>
    private void OnLanguageChanged()
    {
        // These carry their labels as plain strings fixed at build time, so the only way to
        // show them in the new language is to build them again.
        BuildColorLists();
        BuildBackdropEditors();
        RefreshBackdropVisibility();
        RefreshPreview();

        FpsText.Text = string.Format(LocalizationService.T("L_Fps"), _settings.WallpaperFps);
    }

    // Colour rows, paired with the string key that names each one, so the list can be rebuilt
    // in the other language without repeating the property wiring.
    private static readonly (string Property, string Key)[] TerminalColorDefs =
    [
        (nameof(AppSettings.TerminalBackground), "L_ColBackground"),
        (nameof(AppSettings.TerminalForeground), "L_ColText"),
        (nameof(AppSettings.TerminalCursor), "L_ColCursor"),
        (nameof(AppSettings.TerminalSelection), "L_ColSelection"),
    ];

    private static readonly (string Property, string Key)[] AnsiColorDefs =
    [
        (nameof(AppSettings.AnsiBlack), "L_AnsiBlack"),
        (nameof(AppSettings.AnsiBrightBlack), "L_AnsiBrightBlack"),
        (nameof(AppSettings.AnsiRed), "L_AnsiRed"),
        (nameof(AppSettings.AnsiBrightRed), "L_AnsiBrightRed"),
        (nameof(AppSettings.AnsiGreen), "L_AnsiGreen"),
        (nameof(AppSettings.AnsiBrightGreen), "L_AnsiBrightGreen"),
        (nameof(AppSettings.AnsiYellow), "L_AnsiYellow"),
        (nameof(AppSettings.AnsiBrightYellow), "L_AnsiBrightYellow"),
        (nameof(AppSettings.AnsiBlue), "L_AnsiBlue"),
        (nameof(AppSettings.AnsiBrightBlue), "L_AnsiBrightBlue"),
        (nameof(AppSettings.AnsiMagenta), "L_AnsiMagenta"),
        (nameof(AppSettings.AnsiBrightMagenta), "L_AnsiBrightMagenta"),
        (nameof(AppSettings.AnsiCyan), "L_AnsiCyan"),
        (nameof(AppSettings.AnsiBrightCyan), "L_AnsiBrightCyan"),
        (nameof(AppSettings.AnsiWhite), "L_AnsiWhite"),
        (nameof(AppSettings.AnsiBrightWhite), "L_AnsiBrightWhite"),
    ];

    private static readonly (string Property, string Key)[] AppColorDefs =
    [
        (nameof(AppSettings.Accent), "L_ColAccent"),
        (nameof(AppSettings.AccentDim), "L_ColAccentDim"),
        (nameof(AppSettings.Surface), "L_ColSurface"),
        (nameof(AppSettings.SurfaceRaised), "L_ColSurfaceRaised"),
        (nameof(AppSettings.Chrome), "L_ColChrome"),
        (nameof(AppSettings.ChromeHover), "L_ColChromeHover"),
        (nameof(AppSettings.Divider), "L_ColDivider"),
        (nameof(AppSettings.TextPrimary), "L_ColText"),
        (nameof(AppSettings.TextMuted), "L_ColTextMuted"),
        (nameof(AppSettings.TextFaint), "L_ColTextFaint"),
    ];

    private void BuildColorLists()
    {
        _terminalColors.Clear();
        _ansiColors.Clear();
        _appColors.Clear();

        foreach (var (property, key) in TerminalColorDefs)
        {
            _terminalColors.Add(new ColorEntry(_settings, property, LocalizationService.T(key)));
        }

        foreach (var (property, key) in AnsiColorDefs)
        {
            _ansiColors.Add(new ColorEntry(_settings, property, LocalizationService.T(key)));
        }

        foreach (var (property, key) in AppColorDefs)
        {
            _appColors.Add(new ColorEntry(_settings, property, LocalizationService.T(key)));
        }

        TerminalColors.ItemsSource = _terminalColors;
        AnsiColors.ItemsSource = _ansiColors;
        AppColors.ItemsSource = _appColors;
    }

    /// <summary>
    /// Lists fixed-width families for the terminal, and every family for the interface.
    /// Width is measured rather than guessed from the name, which catches fonts that are
    /// monospaced without saying so.
    /// </summary>
    private void PopulateFonts()
    {
        var monospaced = new List<string>();
        var all = new List<string>();

        foreach (var family in Fonts.SystemFontFamilies)
        {
            var name = family.Source;
            all.Add(name);

            try
            {
                var typeface = family.GetTypefaces().FirstOrDefault();
                if (typeface is null || !typeface.TryGetGlyphTypeface(out var glyphs))
                {
                    continue;
                }

                // Fixed-width means every glyph advances the same distance; 'i' against 'W'
                // is the cheapest way to tell, and beats trusting the family name.
                var narrow = AdvanceWidth(glyphs, 'i');
                var wide = AdvanceWidth(glyphs, 'W');
                if (narrow > 0 && Math.Abs(narrow - wide) < 0.001)
                {
                    monospaced.Add(name);
                }
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IOException)
            {
                // Some installed fonts cannot be inspected; leaving them out is fine.
            }
        }

        monospaced.Sort(StringComparer.OrdinalIgnoreCase);
        all.Sort(StringComparer.OrdinalIgnoreCase);

        // Keep whatever is configured selectable even if it is not installed here.
        if (!monospaced.Contains(_settings.TerminalFontFamily, StringComparer.OrdinalIgnoreCase))
        {
            monospaced.Insert(0, _settings.TerminalFontFamily);
        }

        FontBox.ItemsSource = monospaced;
        UiFontBox.ItemsSource = all;
    }

    private static double AdvanceWidth(GlyphTypeface glyphs, char character) =>
        glyphs.CharacterToGlyphMap.TryGetValue(character, out var index)
            ? glyphs.AdvanceWidths[index]
            : 0;

    private void LoadValues()
    {
        FontBox.SelectedItem = _settings.TerminalFontFamily;
        UiFontBox.SelectedItem = _settings.UiFontFamily;
        FontSizeBox.Text = _settings.TerminalFontSize.ToString(CultureInfo.InvariantCulture);
        LineHeightBox.Text = _settings.TerminalLineHeight.ToString(CultureInfo.InvariantCulture);
        ScrollbackBox.Text = _settings.Scrollback.ToString(CultureInfo.InvariantCulture);
        BlinkBox.IsChecked = _settings.CursorBlink;
        WindowOpacitySlider.Value = _settings.WindowOpacity;
        SidebarOpacitySlider.Value = _settings.SidebarOpacity;
        TabBarOpacitySlider.Value = _settings.TabBarOpacity;
        TerminalOpacitySlider.Value = _settings.TerminalOpacity;
        BgNone.IsChecked = _settings.BackgroundMode == BackgroundMode.None;
        BgWindow.IsChecked = _settings.BackgroundMode == BackgroundMode.Window;
        BgRegions.IsChecked = _settings.BackgroundMode == BackgroundMode.Regions;
        BgLive.IsChecked = _settings.BackgroundMode == BackgroundMode.LiveWallpaper;
        AlwaysOnTopBox.IsChecked = _settings.AlwaysOnTop;
        LangEn.IsChecked = _settings.Language == AppLanguage.English;
        LangRu.IsChecked = _settings.Language == AppLanguage.Russian;
        FpsSlider.Value = _settings.WallpaperFps;
        FpsText.Text = string.Format(LocalizationService.T("L_Fps"), _settings.WallpaperFps);
        WpAligned.IsChecked = _settings.WallpaperDisplay == WallpaperDisplay.AlignedToDesktop;
        WpFit.IsChecked = _settings.WallpaperDisplay == WallpaperDisplay.FitToWindow;
        CropLeftSlider.Value = _settings.WallpaperCropLeft;
        CropRightSlider.Value = _settings.WallpaperCropRight;
        CropTopSlider.Value = _settings.WallpaperCropTop;
        CropBottomSlider.Value = _settings.WallpaperCropBottom;
        RefreshCropLabels();
        RefreshOpacityLabels();
        RefreshBackdropVisibility();
        CursorBox.SelectedIndex = _settings.CursorStyle switch
        {
            "block" => 1,
            "underline" => 2,
            _ => 0,
        };
    }

    private void OnThemeApplied()
    {
        foreach (var entry in _terminalColors.Concat(_ansiColors).Concat(_appColors))
        {
            entry.Refresh();
        }

        foreach (var backdrop in _backdrops)
        {
            backdrop.Refresh();
        }

        RefreshPreview();
    }

    /// <summary>Draws a few sample lines using the configured terminal font and palette.</summary>
    private void RefreshPreview()
    {
        PreviewSurface.Background = new SolidColorBrush(
            ThemeService.ParseColor(_settings.TerminalBackground, Colors.Black));

        PreviewLines.Children.Clear();

        (string Text, string Color)[] lines =
        [
            ("user@host:~$ git status", _settings.TerminalForeground),
            ("  modified:   MainWindow.xaml", _settings.AnsiYellow),
            ("  new file:   SettingsPage.xaml", _settings.AnsiGreen),
            ("  deleted:    Legacy.cs", _settings.AnsiRed),
            ("nothing else to commit", _settings.AnsiBrightBlack),
        ];

        foreach (var (text, color) in lines)
        {
            PreviewLines.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(ThemeService.ParseColor(color, Colors.Gray)),
                FontFamily = new FontFamily($"{_settings.TerminalFontFamily}, Consolas, monospace"),
                FontSize = _settings.TerminalFontSize,
                Margin = new Thickness(0, 1, 0, 1),
            });
        }
    }

    // ================= handlers =================

    private void Font_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && FontBox.SelectedItem is string family)
        {
            _settings.TerminalFontFamily = family;
            RefreshPreview();
        }
    }

    private void UiFont_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && UiFontBox.SelectedItem is string family)
        {
            _settings.UiFontFamily = family;
        }
    }

    private void FontSize_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading && double.TryParse(FontSizeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var size))
        {
            _settings.TerminalFontSize = size;
            RefreshPreview();
        }
    }

    private void LineHeight_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading && double.TryParse(LineHeightBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
        {
            _settings.TerminalLineHeight = height;
        }
    }

    private void Scrollback_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading && int.TryParse(ScrollbackBox.Text, out var lines))
        {
            _settings.Scrollback = lines;
        }
    }

    private void Cursor_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.CursorStyle = CursorBox.SelectedIndex switch
        {
            1 => "block",
            2 => "underline",
            _ => "bar",
        };
    }

    private void Blink_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            _settings.CursorBlink = BlinkBox.IsChecked == true;
        }
    }

    private void Language_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            _settings.Language = LangRu.IsChecked == true ? AppLanguage.Russian : AppLanguage.English;
        }
    }

    // ================= background and see-through =================

    private readonly ObservableCollection<BackdropEntry> _backdrops = [];

    private void BuildBackdropEditors()
    {
        _backdrops.Clear();
        _backdrops.Add(new BackdropEntry(LocalizationService.T("L_BackdropWholeWindow"), _settings.WindowBackdrop));
        _backdrops.Add(new BackdropEntry(LocalizationService.T("L_BackdropSidebar"), _settings.SidebarBackdrop));
        _backdrops.Add(new BackdropEntry(LocalizationService.T("L_BackdropTerminal"), _settings.TerminalBackdrop));
        BackdropEditors.ItemsSource = _backdrops;
    }

    /// <summary>Shows only the layers the current mode actually draws.</summary>
    private void RefreshBackdropVisibility()
    {
        var mode = _settings.BackgroundMode;
        BackdropEditors.ItemsSource = mode switch
        {
            // The live wallpaper reuses the window layer's blur and overlay, minus its file picker.
            BackgroundMode.Window or BackgroundMode.LiveWallpaper => _backdrops.Take(1),
            BackgroundMode.Regions => _backdrops.Skip(1),
            _ => Enumerable.Empty<BackdropEntry>(),
        };

        LivePanel.Visibility = mode == BackgroundMode.LiveWallpaper
            ? Visibility.Visible
            : Visibility.Collapsed;

        foreach (var entry in _backdrops)
        {
            entry.ShowFilePicker = mode != BackgroundMode.LiveWallpaper;
        }
    }

    private void BackgroundMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.BackgroundMode =
            BgWindow.IsChecked == true ? BackgroundMode.Window :
            BgRegions.IsChecked == true ? BackgroundMode.Regions :
            BgLive.IsChecked == true ? BackgroundMode.LiveWallpaper :
            BackgroundMode.None;

        RefreshBackdropVisibility();
    }

    private void WallpaperDisplay_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            _settings.WallpaperDisplay = WpFit.IsChecked == true
                ? WallpaperDisplay.FitToWindow
                : WallpaperDisplay.AlignedToDesktop;
        }
    }

    private void Fps_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loading)
        {
            _settings.WallpaperFps = (int)Math.Round(FpsSlider.Value);
            FpsText.Text = string.Format(LocalizationService.T("L_Fps"), _settings.WallpaperFps);
        }
    }

    private void Crop_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        _settings.WallpaperCropLeft = CropLeftSlider.Value;
        _settings.WallpaperCropRight = CropRightSlider.Value;
        _settings.WallpaperCropTop = CropTopSlider.Value;
        _settings.WallpaperCropBottom = CropBottomSlider.Value;
        RefreshCropLabels();
    }

    private void RefreshCropLabels()
    {
        CropLeftText.Text = _settings.WallpaperCropLeft.ToString("P0", CultureInfo.CurrentCulture);
        CropRightText.Text = _settings.WallpaperCropRight.ToString("P0", CultureInfo.CurrentCulture);
        CropTopText.Text = _settings.WallpaperCropTop.ToString("P0", CultureInfo.CurrentCulture);
        CropBottomText.Text = _settings.WallpaperCropBottom.ToString("P0", CultureInfo.CurrentCulture);
    }

    private void AlwaysOnTop_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            _settings.AlwaysOnTop = AlwaysOnTopBox.IsChecked == true;
        }
    }

    private void BrowseImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: BackdropEntry entry })
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Choose a background picture",
            CheckFileExists = true,
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp"
                     + "|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            entry.ImagePath = dialog.FileName;
        }
    }

    private void ClearImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: BackdropEntry entry })
        {
            entry.ImagePath = string.Empty;
        }
    }

    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        _settings.WindowOpacity = WindowOpacitySlider.Value;
        _settings.SidebarOpacity = SidebarOpacitySlider.Value;
        _settings.TabBarOpacity = TabBarOpacitySlider.Value;
        _settings.TerminalOpacity = TerminalOpacitySlider.Value;
        RefreshOpacityLabels();
    }

    private void RefreshOpacityLabels()
    {
        WindowOpacityText.Text = _settings.WindowOpacity.ToString("P0", CultureInfo.CurrentCulture);
        SidebarOpacityText.Text = _settings.SidebarOpacity.ToString("P0", CultureInfo.CurrentCulture);
        TabBarOpacityText.Text = _settings.TabBarOpacity.ToString("P0", CultureInfo.CurrentCulture);
        TerminalOpacityText.Text = _settings.TerminalOpacity.ToString("P0", CultureInfo.CurrentCulture);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            Window.GetWindow(this),
            "Restore every appearance setting to its default?",
            "RedBloom",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        ThemeService.ResetToDefaults();

        _loading = true;
        LoadValues();
        _loading = false;

        OnThemeApplied();
    }
}


