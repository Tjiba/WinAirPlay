using System.Collections;
using System.Reflection;
using WinAirPlay.Core;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2 || !System.Net.IPAddress.TryParse(args[0], out _) || !System.Net.IPAddress.TryParse(args[1], out _) || args[0] == args[1])
        {
            Console.Error.WriteLine("Usage: WinAirPlay.UiTests SPEAKER_IP_1 SPEAKER_IP_2 (streams PC audio to both speakers)");
            return 2;
        }
        ApplicationConfiguration.Initialize();
        var type = Assembly.Load("WinAirPlay").GetType("WinAirPlay.MainForm")!;
        using var form = (Form)Activator.CreateInstance(type)!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object Field(string name) => type.GetField(name, flags)!.GetValue(form)!;
        Task Call(string name, params object[] args) => (Task)type.GetMethod(name, flags)!.Invoke(form, args)!;
        var connections = (IDictionary)Field("connections");
        bool Streaming(string key) => connections.Contains(key) && (bool)connections[key]!.GetType().GetProperty("Streaming")!.GetValue(connections[key])!;
        void Check(bool success, string message) { if (!success) throw new Exception(message); Console.WriteLine("PASS " + message); }
        var first = new Receiver("first", "Speaker 1", args[0], 7000);
        var second = new Receiver("second", "Speaker 2", args[1], 7000);
        var output = 0;
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinAirPlay", "settings-v2.json");
        var saved = File.Exists(path) ? File.ReadAllBytes(path) : null;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-20000, -20000);
        form.Shown += async (_, _) =>
        {
            try
            {
                ((NotifyIcon)Field("tray")).Visible = false;
                await Call("ToggleConnection", first);
                await Task.Delay(2000);
                Check(Streaming(args[0] + ":7000"), "first speaker streams");
                var original = connections[args[0] + ":7000"];
                await Call("ToggleConnection", second);
                await Task.Delay(6000);
                Check(connections.Count == 2 && Streaming(args[0] + ":7000") && Streaming(args[1] + ":7000"), "two speakers stream simultaneously");
                Check(ReferenceEquals(original, connections[args[0] + ":7000"]), "adding speaker preserves first session");
                type.GetMethod("RebuildCards", flags)!.Invoke(form, null);
                using (var bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, form.ClientRectangle);
                    Directory.CreateDirectory("build/ui-tests");
                    bitmap.Save("build/ui-tests/multi-speaker.png");
                }
                using (var menu = new ContextMenuStrip())
                {
                    type.GetMethod("PopulateMenu", flags)!.Invoke(form, [menu]);
                    var speakers = menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "Speakers");
                    Check(speakers.DropDownItems.OfType<ToolStripMenuItem>().Count(item => item.Checked) == 2, "tray marks both speakers connected");
                }
                await Call("ToggleConnection", new Receiver("failed", "Unavailable", "127.0.0.1", 1));
                Check(connections.Count == 2 && Streaming(args[0] + ":7000"), "failed third connection preserves playback");
                await Call("ToggleConnection", second);
                await Task.Delay(6000);
                Check(connections.Count == 1 && Streaming(args[0] + ":7000") && ReferenceEquals(original, connections[args[0] + ":7000"]), "disconnecting one preserves other session");
                var pending = Call("ToggleConnection", second);
                await Call("ToggleConnection", second);
                await pending;
                Check(connections.Count == 1, "cancel pending connection removes only that speaker");
                pending = Call("ToggleConnection", second);
                await Call("DisconnectAll");
                await pending;
                Check(connections.Count == 0, "disconnect all includes in-flight connection");
                Console.WriteLine("Live UI lifecycle passed; audible quality and inter-speaker synchronization not measured.");
            }
            catch (Exception error) { Console.Error.WriteLine(error); output = 1; }
            finally
            {
                type.GetField("exitRequested", flags)!.SetValue(form, true);
                form.Close();
            }
        };
        Application.Run(form);
        if (saved is null) File.Delete(path);
        else File.WriteAllBytes(path, saved);
        return output;
    }
}
