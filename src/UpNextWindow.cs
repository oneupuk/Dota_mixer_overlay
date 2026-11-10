using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace DotaGlass
{
    /// <summary>
    /// The "Up next" list under (in a match) or above (in the main menu) the widget: the same never-activating
    /// glass as the widget, one row per song. Clicking a row plays that song.
    /// </summary>
    internal sealed class UpNextWindow : Window
    {
        public const double BaseWidth = 360, Radius = 18;
        const double Pad = 8, HeaderH = 22, RowH = 36;

        public event Action<UpNext.Track> TrackClicked;
        /// <summary>Pointer left the list, or a click on it (restarts the widget's idle auto-hide).</summary>
        public event Action Interacted;

        readonly string glassMode;
        readonly GlassBackdrop backdrop;
        readonly Grid root;
        readonly StackPanel rows;
        IntPtr hwnd;

        public UpNextWindow(string glassMode)
        {
            this.glassMode = glassMode;
            Title = "Dota Glass Up Next";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = false;     // same "sheet of glass" as the widget
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            Left = -32000; Top = -32000;

            if (glassMode != "none" && glassMode != "acrylic") backdrop = GlassBackdrop.TryCreate();
            if (backdrop != null) new WindowInteropHelper(this).Owner = backdrop.Hwnd;

            root = new Grid { Width = BaseWidth, Background = Brushes.Transparent };
            TextElement.SetFontFamily(root, new FontFamily("Segoe UI"));
            TextElement.SetForeground(root, Brushes.White);
            root.Children.Add(new Rectangle { Fill = Gradient(0x33404858, 0x590C0F14, true), IsHitTestVisible = false });
            root.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(Radius), BorderThickness = new Thickness(1.25), IsHitTestVisible = false,
                BorderBrush = Gradient(0xF2FFFFFF, 0x0FFFFFFF, false)
            });
            var content = new StackPanel { Margin = new Thickness(Pad, 4, Pad, Pad) };
            content.Children.Add(new TextBlock
            {
                Text = "UP NEXT", FontSize = 9.5, FontWeight = FontWeights.SemiBold, Height = HeaderH, Padding = new Thickness(8, 6, 0, 0),
                Foreground = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255))
            });
            rows = new StackPanel();
            content.Children.Add(rows);
            root.Children.Add(content);
            Content = new Viewbox { Child = root, Stretch = Stretch.Uniform };

            root.MouseLeave += (s, e) => Fire(Interacted);
            root.PreviewMouseDown += (s, e) => Fire(Interacted);

            SourceInitialized += (s, e) =>
            {
                hwnd = new WindowInteropHelper(this).Handle;
                Native.MakeNoActivateToolWindow(this, hwnd);
                HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
                Native.ExtendGlass(hwnd);
                if (backdrop == null) Native.ApplyAccentGlass(hwnd, glassMode);
                Native.SetDwmCorners(hwnd, backdrop == null && glassMode != "none");
            };
            SetMessage("Loading…");
        }

        /// <summary>Height for a given width, following the number of rows shown.</summary>
        public int HeightFor(int width)
        {
            return (int)Math.Round(width * root.Height / BaseWidth);
        }

        public bool IsInUse { get { return IsVisible && root.IsMouseOver; } }

        public void SetMessage(string text)
        {
            rows.Children.Clear();
            rows.Children.Add(new TextBlock
            {
                Text = text, FontSize = 11.5, Height = RowH, Padding = new Thickness(8, 9, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(Color.FromArgb(0xB8, 255, 255, 255))
            });
            Resize(1);
        }

        public void SetTracks(List<UpNext.Track> tracks)
        {
            rows.Children.Clear();
            foreach (var t in tracks) rows.Children.Add(Row(t));
            Resize(tracks.Count);
        }

        void Resize(int rowCount)
        {
            root.Height = 4 + HeaderH + rowCount * RowH + Pad;
            root.Clip = new RectangleGeometry(new Rect(0, 0, BaseWidth, root.Height), Radius, Radius);
        }

        UIElement Row(UpNext.Track t)
        {
            var row = new Border { Height = RowH, CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            var grid = new Grid { Margin = new Thickness(8, 0, 8, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = t.Title, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            if (t.Artist.Length > 0)
                text.Children.Add(new TextBlock
                {
                    Text = t.Artist, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xB8, 255, 255, 255))
                });
            grid.Children.Add(text);
            var dur = new TextBlock
            {
                Text = t.Duration, FontSize = 10, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255))
            };
            Grid.SetColumn(dur, 1);
            grid.Children.Add(dur);
            row.Child = grid;

            var hover = new SolidColorBrush(Color.FromArgb(0x24, 255, 255, 255));
            row.MouseEnter += (s, e) => row.Background = hover;
            row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonDown += (s, e) => { row.CaptureMouse(); e.Handled = true; };
            row.MouseLeftButtonUp += (s, e) =>
            {
                if (!row.IsMouseCaptured) return;
                var p = e.GetPosition(row);
                row.ReleaseMouseCapture();
                if (p.X >= 0 && p.Y >= 0 && p.X <= row.ActualWidth && p.Y <= row.ActualHeight)
                {
                    var h = TrackClicked;
                    if (h != null) h(t);
                }
                e.Handled = true;
            };
            return row;
        }

        // ---------------------------------------------------------------- placement & visibility

        /// <summary>Physical screen pixels.</summary>
        public void Place(int x, int y, int w, int h)
        {
            if (hwnd == IntPtr.Zero) new WindowInteropHelper(this).EnsureHandle();
            if (backdrop != null) backdrop.Place(x, y, w, h, Radius * w / BaseWidth);
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE);
        }

        public void ShowList()
        {
            if (backdrop != null) { backdrop.Show(); backdrop.ResetVisual(); }
            if (!IsVisible) Show();
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        }

        public void HideList()
        {
            Hide();
            if (backdrop != null) backdrop.Hide();
        }

        // ---------------------------------------------------------------- helpers

        static Brush Gradient(uint from, uint to, bool vertical)
        {
            var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = vertical ? new Point(0, 1) : new Point(1, 1) };
            b.GradientStops.Add(new GradientStop(Argb(from), 0));
            b.GradientStops.Add(new GradientStop(Argb(to), 1));
            b.Freeze();
            return b;
        }

        static Color Argb(uint c) { return Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c); }

        static void Fire(Action a) { if (a != null) a(); }
    }
}
