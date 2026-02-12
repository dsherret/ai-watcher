namespace AIWatcher
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var window = new Window(new AppShell())
            {
                Width = 400
            };

#if WINDOWS
            window.Created += (_, _) =>
            {
                var nativeWindow = window.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
                if (nativeWindow == null) return;

                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);

                // restore saved position/size, or default to 75% screen height
                if (Preferences.ContainsKey("WindowX"))
                {
                    window.X = Preferences.Get("WindowX", 0.0);
                    window.Y = Preferences.Get("WindowY", 0.0);
                    window.Width = Preferences.Get("WindowWidth", 400.0);
                    window.Height = Preferences.Get("WindowHeight", 600.0);
                }
                else
                {
                    var screenHeight = GetSystemMetrics(SM_CYSCREEN);
                    window.Height = screenHeight * 0.75;
                }

                // always on top
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

                // 95% opacity
                var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, style | WS_EX_LAYERED);
                SetLayeredWindowAttributes(hwnd, 0, 242, LWA_ALPHA); // 242/255 ≈ 95%
            };

            window.Destroying += (_, _) =>
            {
                // save position and size for next launch
                Preferences.Set("WindowX", window.X);
                Preferences.Set("WindowY", window.Y);
                Preferences.Set("WindowWidth", window.Width);
                Preferences.Set("WindowHeight", window.Height);
            };
#elif MACCATALYST
            window.Created += (_, _) =>
            {
                if (Preferences.ContainsKey("WindowX"))
                {
                    window.X = Preferences.Get("WindowX", 0.0);
                    window.Y = Preferences.Get("WindowY", 0.0);
                    window.Width = Preferences.Get("WindowWidth", 400.0);
                    window.Height = Preferences.Get("WindowHeight", 600.0);
                }
                else
                {
                    window.Height = 600;
                }
            };

            window.Destroying += (_, _) =>
            {
                Preferences.Set("WindowX", window.X);
                Preferences.Set("WindowY", window.Y);
                Preferences.Set("WindowWidth", window.Width);
                Preferences.Set("WindowHeight", window.Height);
            };
#endif

            return window;
        }

#if WINDOWS
        const int GWL_EXSTYLE = -20;
        const int WS_EX_LAYERED = 0x80000;
        const int LWA_ALPHA = 0x2;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOACTIVATE = 0x0010;
        static readonly nint HWND_TOPMOST = -1;
        const int SM_CYSCREEN = 1;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter,
            int x, int y, int cx, int cy, uint flags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetLayeredWindowAttributes(nint hWnd, uint crKey, byte bAlpha, int dwFlags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern int GetSystemMetrics(int nIndex);
#endif
    }
}
