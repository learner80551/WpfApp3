using System.Windows;
using WpfApp3.Authentication;
using WpfApp3.Database;
using WpfApp3.Theme;
using WpfApp3.UI.Login;

namespace WpfApp3
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Do not show any window automatically (set in App.xaml)
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // 1. Initialize database (creates schema if new)
            AppDatabase.Initialize();

            // 2. Apply saved theme before any UI appears
            bool isDark = ThemeManager.LoadThemePreference();
            ThemeManager.ApplyTheme(isDark);

            // 3. First-run check
            if (AuthenticationService.IsFirstRun())
            {
                var setup = new FirstRunSetupWindow();
                bool? ok = setup.ShowDialog();
                if (ok != true)
                {
                    // User closed setup without completing — shut down
                    Shutdown(0);
                    return;
                }
            }

            // 4. Login loop
            bool loggedIn = false;
            bool forceReset = false;

            while (!loggedIn)
            {
                var loginWindow = new LoginWindow();
                bool? result = loginWindow.ShowDialog();

                if (result != true)
                {
                    // User closed login — shut down
                    Shutdown(0);
                    return;
                }

                loggedIn   = loginWindow.LoginSucceeded;
                forceReset = loginWindow.ForcePasswordReset;
            }

            // 5. If forced password reset — show change-password dialog
            if (forceReset)
            {
                var changePwd = new UI.Settings.ChangePasswordWindow();
                changePwd.ShowDialog();
                // Even if they skip, session is still valid — let them in
            }

            // 6. Show main window
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            var main = new MainWindow();
            MainWindow = main;
            main.Show();
        }
    }
}

