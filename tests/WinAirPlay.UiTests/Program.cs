using System.Collections;
using System.Reflection;
using WinAirPlay.Core;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--window"]) return TestWindow();
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

    private static int TestWindow()
    {
        ApplicationConfiguration.Initialize();
        var type = Assembly.Load("WinAirPlay").GetType("WinAirPlay.MainForm")!;
        using var form = (Form)Activator.CreateInstance(type)!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var tray = (NotifyIcon)type.GetField("tray", flags)!.GetValue(form)!;
        var output = 0;
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinAirPlay", "settings-v2.json");
        var saved = File.Exists(path) ? File.ReadAllBytes(path) : null;
        void Click() => typeof(NotifyIcon).GetMethod("OnMouseClick", flags)!.Invoke(tray, [new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0)]);
        void Check(bool success, string message) { if (!success) throw new Exception(message); Console.WriteLine("PASS " + message); }
        form.Shown += (_, _) => form.BeginInvoke(async () =>
        {
            try
            {
                var area = Screen.FromPoint(Cursor.Position).WorkingArea;
                Check(form.Location == new Point(Math.Max(area.Left, area.Right - form.Width - 12), Math.Max(area.Top, area.Bottom - form.Height - 12)), "initial window appears at bottom right");
                tray.Visible = false;
                form.Hide();
                Click();
                Check(form.Visible && form.WindowState == FormWindowState.Normal, "one tray click opens hidden window");
                using var other = new Form();
                other.Show(); other.Activate();
                await Task.Delay(100);
                Click();
                await Task.Delay(100);
                Check(form.Visible, "one tray click keeps covered window visible");
                Console.WriteLine(Form.ActiveForm == form ? "PASS window activated" : "SKIP foreground focus: requires a real user tray click");
                Click();
                Check(form.Visible, "repeated tray click keeps window open");
                form.WindowState = FormWindowState.Minimized;
                Click();
                Check(form.Visible && form.WindowState == FormWindowState.Normal, "one tray click restores minimized window");
                form.Hide();
                var integration = type.Assembly.GetType("WinAirPlay.WindowsIntegration")!;
                var message = Message.Create(form.Handle, (int)integration.GetField("ShowMessage")!.GetValue(null)!, IntPtr.Zero, IntPtr.Zero);
                type.GetMethod("WndProc", flags)!.Invoke(form, [message]);
                Check(form.Visible, "activation message restores existing window");
                var settingsType = type.Assembly.GetType("WinAirPlay.SettingsForm")!;
                using var dialog = (Form)Activator.CreateInstance(settingsType, [Array.Empty<AudioEndpoint>(), "", 100, false, true, true, true])!;
                dialog.Show();
                var checkbox = (CheckBox)settingsType.GetField("startAtLogin", flags)!.GetValue(dialog)!;
                Check(checkbox.Visible && checkbox.Bottom <= checkbox.Parent!.ClientSize.Height, "Windows startup option visible in Settings");
                var shared = (CheckBox)settingsType.GetField("sharedVolume", flags)!.GetValue(dialog)!;
                Check(shared.Visible && shared.Checked, "shared volume option visible and selected");
                using (var bitmap = new Bitmap(dialog.Width, dialog.Height))
                {
                    dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size));
                    Directory.CreateDirectory("build/ui-tests");
                    bitmap.Save("build/ui-tests/settings.png");
                }
                dialog.Hide();
                TestVolumes(form, type, flags, Check);
                Exception? addError = null;
                using var timer = new System.Windows.Forms.Timer { Interval = 100 };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    var addDialog = Application.OpenForms.Cast<Form>().Single(item => item.Text == "Add an AirPlay speaker");
                    try
                    {
                        ((Button)addDialog.AcceptButton!).PerformClick();
                        Check(addDialog.Visible, "invalid IP keeps add dialog open");
                        using var bitmap = new Bitmap(addDialog.Width, addDialog.Height);
                        addDialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, addDialog.Size));
                        bitmap.Save("build/ui-tests/add-speaker.png");
                    }
                    catch (Exception error) { addError = error; }
                    finally { addDialog.Close(); }
                };
                timer.Start();
                type.GetMethod("AddReceiver", flags)!.Invoke(form, null);
                if (addError is not null) throw addError;
            }
            catch (Exception error) { Console.Error.WriteLine(error); output = 1; }
            finally
            {
                type.GetField("exitRequested", flags)!.SetValue(form, true);
                form.Close();
            }
        });
        Application.Run(form);
        if (saved is null) File.Delete(path);
        else File.WriteAllBytes(path, saved);
        return output;
    }

    private static void TestVolumes(Form form, Type type, BindingFlags flags, Action<bool, string> check)
    {
        object Field(string name) => type.GetField(name, flags)!.GetValue(form)!;
        void Call(string name, params object[] args) => type.GetMethod(name, flags)!.Invoke(form, args);
        var receivers = new[] { new Receiver("test-bedroom", "Chambre", "127.0.0.1", 1), new Receiver("test-living", "Salon", "127.0.0.2", 1), new Receiver("test-living-2", "Salon (2)", "127.0.0.3", 1) };
        Call("UpdateReceivers", (object)receivers);
        var settings = Field("settings");
        var settingsType = settings.GetType();
        settingsType.GetProperty("ReceiverVolumes")!.SetValue(settings, new Dictionary<string, int>());
        Call("SetVolumeMode", true);
        var volume = Field("volume");
        var valueProperty = volume.GetType().GetProperty("Value")!;
        valueProperty.SetValue(volume, 43);
        using var first = new AirPlaySession(receivers[0], null, 100, 43, _ => { });
        using var second = new AirPlaySession(receivers[1], null, 100, 43, _ => { });
        var connections = (IDictionary)Field("connections");
        var connectionType = type.GetNestedType("Connection", BindingFlags.NonPublic)!;
        connections.Add("127.0.0.1:1", Activator.CreateInstance(connectionType, [receivers[0], first]));
        connections.Add("127.0.0.2:1", Activator.CreateInstance(connectionType, [receivers[1], second]));
        int SessionVolume(AirPlaySession session) => (int)typeof(AirPlaySession).GetField("volume", flags)!.GetValue(session)!;
        var cards = (FlowLayoutPanel)Field("cards");
        void Capture(string name)
        {
            form.PerformLayout();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, form.ClientRectangle);
            Directory.CreateDirectory("build/ui-tests");
            bitmap.Save("build/ui-tests/" + name + ".png");
        }
        try
        {
            valueProperty.SetValue(volume, 62);
            check(SessionVolume(first) == 62 && SessionVolume(second) == 62, "shared slider updates both sessions");
            Capture("minimal-shared");
            Call("SetVolumeMode", false);
            check(!((Control)Field("volumePanel")).Visible, "individual mode hides shared slider");
            var card = cards.Controls[0];
            var slider = (Control)card.GetType().GetField("volume", flags)!.GetValue(card)!;
            check(slider.Visible, "individual slider visible under speaker");
            slider.GetType().GetProperty("Value")!.SetValue(slider, 25);
            check(SessionVolume(first) == 25 && SessionVolume(second) == 62, "individual slider changes only its own session");
            Call("SetReceiverVolume", receivers[1], 78);
            Call("RebuildCards");
            check(SessionVolume(first) == 25 && SessionVolume(second) == 78, "speaker list rebuild preserves independent levels");
            Capture("minimal-individual");
            var loaded = settingsType.GetMethod("Load")!.Invoke(null, null)!;
            var savedVolumes = (Dictionary<string, int>)settingsType.GetProperty("ReceiverVolumes")!.GetValue(loaded)!;
            check(!(bool)settingsType.GetProperty("SharedVolume")!.GetValue(loaded)! && savedVolumes[receivers[0].Id] == 25 && savedVolumes[receivers[1].Id] == 78, "mode and individual volumes survive settings reload");
            Call("SetVolumeMode", true);
            check(SessionVolume(first) == 62 && SessionVolume(second) == 62, "switching to shared applies global level immediately");
            Call("SetVolumeMode", false);
            check(SessionVolume(first) == 25 && SessionVolume(second) == 78, "switching back restores each saved level");
            var moved = receivers[0] with { Host = "127.0.0.4" };
            check((int)type.GetMethod("ReceiverVolume", flags)!.Invoke(form, [moved])! == 25, "speaker volume follows identity after IP change");
        }
        finally { connections.Clear(); }
        Call("UpdateReceivers", (object)Array.Empty<Receiver>());
        check(!((Button)Field("stopAll")).Enabled, "Stop all disabled without connections");
        check(cards.Controls[0].Bottom <= cards.ClientSize.Height, "empty state fits without clipping");
        Capture("minimal-empty");
        var many = Enumerable.Range(0, 16).Select(index => new Receiver("ui-" + index, "HomePod with a very long room name " + index, "127.0.0." + (index + 1), 1)).ToArray();
        Call("UpdateReceivers", (object)many);
        check(form.Height <= Screen.FromControl(form).WorkingArea.Height - 24, "large speaker list stays inside screen");
        check(!cards.HorizontalScroll.Visible, "long speaker names do not cause horizontal scrolling");
        Capture("minimal-many");
    }
}
