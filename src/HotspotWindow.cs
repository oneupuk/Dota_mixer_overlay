using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace DotaGlass
{
    /// <summary>
    /// An invisible round button over the sun/moon in Dota's clock. Clicking it toggles the music widget.
    /// It's a layered window whose only visible pixels are an alpha-1 disc (so it catches clicks while
    /// looking like nothing), plus a liquid-glass halo that appears on hover.
    /// In Dota's main menu there is no clock, so the same window shows a visible glass music-note button.
    /// Clicks land here, not in Dota, and Dota keeps focus.
    /// </summary>
    internal sealed class HotspotWindow : Window
    {
        public const double BaseSize = 32;   // window, in 1080p pixels (room for the halo)
        const double HitSize = 22;           // clickable disc: covers the sun/moon and its rays
        const double DiscSize = 26;          // main menu: the visible glass button

        public event Action Clicked;

        readonly Ellipse halo, ring, hit, disc;
        readonly Path note;
        readonly ScaleTransform press;
        IntPtr hwnd;
        bool active, hover;

        public HotspotWindow()
        {
            Title = "Dota Glass Clock Button";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;      // per-pixel alpha => only the disc is hit-testable
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            Width = BaseSize; Height = BaseSize;
            Left = -32000; Top = -32000;

            var grid = new Grid { Width = BaseSize, Height = BaseSize, Cursor = Cursors.Hand };
            press = new ScaleTransform(1, 1, BaseSize / 2, BaseSize / 2);
            grid.RenderTransform = press;

            // soft glass halo around the icon (center stays clear so the sun/moon is untouched)
            var haloBrush = new RadialGradientBrush();
            haloBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 0.0));
            haloBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 0.48));
            haloBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x55, 255, 255, 255), 0.64));
            haloBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x1A, 255, 255, 255), 0.82));
            haloBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 1.0));
            haloBrush.Freeze();
            halo = new Ellipse { Width = BaseSize, Height = BaseSize, Fill = haloBrush, Opacity = 0, IsHitTestVisible = false };

            // specular rim, like the widget's edge
            var rimBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            rimBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0xE6, 255, 255, 255), 0.0));
            rimBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x26, 255, 255, 255), 0.5));
            rimBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x99, 255, 255, 255), 1.0));
            rimBrush.Freeze();
            ring = new Ellipse { Width = HitSize, Height = HitSize, Stroke = rimBrush, StrokeThickness = 1.1, Opacity = 0, IsHitTestVisible = false };

            // main-menu look: dark glass disc with a note, like the widget's artwork placeholder
            var discFill = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            discFill.GradientStops.Add(new GradientStop(Color.FromArgb(0xB3, 0x3A, 0x42, 0x52), 0.0));
            discFill.GradientStops.Add(new GradientStop(Color.FromArgb(0xD9, 0x0C, 0x0F, 0x14), 1.0));
            discFill.Freeze();
            var discRim = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            discRim.GradientStops.Add(new GradientStop(Color.FromArgb(0x99, 255, 255, 255), 0.0));
            discRim.GradientStops.Add(new GradientStop(Color.FromArgb(0x14, 255, 255, 255), 0.6));
            discRim.GradientStops.Add(new GradientStop(Color.FromArgb(0x4D, 255, 255, 255), 1.0));
            discRim.Freeze();
            disc = new Ellipse { Width = DiscSize, Height = DiscSize, Fill = discFill, Stroke = discRim, StrokeThickness = 1,
                                 Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            note = new Path
            {
                Width = 11, Height = 11, Stretch = Stretch.Uniform, Fill = new SolidColorBrush(Color.FromArgb(0xE6, 255, 255, 255)),
                Data = Geometry.Parse("M12,3 V13.55 C11.41,13.21 10.73,13 10,13 C7.79,13 6,14.79 6,17 C6,19.21 7.79,21 10,21 C12.21,21 14,19.21 14,17 V7 H18 V3 Z"),
                Visibility = Visibility.Collapsed, IsHitTestVisible = false
            };

            // the actual click target: alpha 1/255, invisible but hit-testable
            hit = new Ellipse { Width = HitSize, Height = HitSize, Fill = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)) };

            grid.Children.Add(halo);
            grid.Children.Add(disc);
            grid.Children.Add(note);
            grid.Children.Add(ring);
            grid.Children.Add(hit);
            Content = new Viewbox { Child = grid, Stretch = Stretch.Uniform };

            hit.MouseEnter += (s, e) => { hover = true; UpdateLook(); };
            hit.MouseLeave += (s, e) => { hover = false; UpdateLook(); };
            hit.MouseLeftButtonDown += (s, e) =>
            {
                hit.CaptureMouse();
                Animate(press, 0.88, 80, null);
                e.Handled = true;
            };
            hit.MouseLeftButtonUp += (s, e) =>
            {
                if (!hit.IsMouseCaptured) return;
                var p = e.GetPosition(hit);
                hit.ReleaseMouseCapture();
                if (p.X >= 0 && p.Y >= 0 && p.X <= hit.ActualWidth && p.Y <= hit.ActualHeight)
                {
                    var h = Clicked;
                    if (h != null) h();
                }
                e.Handled = true;
            };
            hit.LostMouseCapture += (s, e) =>
                Animate(press, 1.0, 420, new ElasticEase { Oscillations = 1, Springiness = 5, EasingMode = EasingMode.EaseOut });

            SourceInitialized += (s, e) =>
            {
                hwnd = new WindowInteropHelper(this).Handle;
                Native.MakeNoActivateToolWindow(this, hwnd);
            };
        }

        /// <summary>true = visible music-note button (main menu), false = invisible disc over the clock's sun/moon.</summary>
        public void SetMenuStyle(bool on)
        {
            disc.Visibility = note.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            hit.Width = hit.Height = ring.Width = ring.Height = on ? DiscSize : HitSize;
        }

        /// <summary>Widget open => keep a faint rim so you can see the toggle is "on".</summary>
        public void SetActive(bool on)
        {
            if (active == on) return;
            active = on;
            UpdateLook();
        }

        void UpdateLook()
        {
            Fade(halo, hover ? 1.0 : 0.0);
            Fade(ring, hover ? 1.0 : active ? 0.45 : 0.0);
        }

        public void Place(int x, int y, int size)
        {
            if (hwnd == IntPtr.Zero) new WindowInteropHelper(this).EnsureHandle();
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, size, size, Native.SWP_NOACTIVATE);
        }

        public void ShowButton()
        {
            if (!IsVisible) Show();
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        static void Fade(UIElement e, double to)
        {
            e.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(140)));
        }

        static void Animate(ScaleTransform st, double to, int ms, IEasingFunction ease)
        {
            var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
    }
}
