namespace WinAirPlay;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        if (args.Length >= 2 && args[0] is "--probe" or "--stream-test")
        {
            Directory.CreateDirectory(Settings.Folder);
            var path = Path.Combine(Settings.Folder, "probe-v2.log");
            File.WriteAllText(path, "");
            var logGate = new object();
            void Log(string message) { lock (logGate) File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}\n"); }
            try
            {
                using var session = new Core.AirPlaySession(new(args[1], "Diagnostics", args[1], args.Length > 2 ? int.Parse(args[2]) : 7000), null, 100, 50, Log);
                string? failure = null;
                session.Failed += error => failure = error;
                session.Connect(startAudio: args[0] == "--stream-test");
                if (args[0] == "--stream-test") Thread.Sleep(15000);
                if (failure is not null) throw new IOException(failure);
                Log("PASS");
                return 0;
            }
            catch (Exception error) { Log(error.ToString()); return 1; }
        }
        using var mutex = new Mutex(true, "Local\\WinAirPlay.Native.v2", out var created);
        if (!created)
        {
            if (!args.Contains("--startup")) WindowsIntegration.ShowExistingWindow();
            return 0;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
