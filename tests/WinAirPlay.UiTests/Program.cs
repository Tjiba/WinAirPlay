using System.Collections;
using System.Reflection;
using WinAirPlay.Core;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--window"]) return TestWindow();
        if (args is ["--window-96"]) { Application.SetHighDpiMode(HighDpiMode.DpiUnaware); return TestWindow(); }
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
                var initializationDeadline = Environment.TickCount64 + 8000;
                while (type.GetField("defaultOutput", flags)!.GetValue(form) is null && Environment.TickCount64 < initializationDeadline) await Task.Delay(20);
                Check(type.GetField("defaultOutput", flags)!.GetValue(form) is not null, "audio outputs load asynchronously");
                ((Discovery)type.GetField("discovery", flags)!.GetValue(form)!).Dispose();
                ((System.Windows.Forms.Timer)type.GetField("audioTimer", flags)!.GetValue(form)!).Stop();
                ((System.Windows.Forms.Timer)type.GetField("recoveryTimer", flags)!.GetValue(form)!).Stop();
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
                CheckLayout(dialog);
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
                await TestVolumes(form, type, flags, Check);
                await TestRecovery(form, type, flags, Check);
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
                        CheckLayout(addDialog);
                        using var bitmap = new Bitmap(addDialog.Width, addDialog.Height);
                        addDialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, addDialog.Size));
                        bitmap.Save("build/ui-tests/add-speaker.png");
                        var panel = addDialog.Controls[0];
                        var address = panel.Controls.OfType<TextBox>().Single(item => item.AccessibleName == "IP address");
                        var name = panel.Controls.OfType<TextBox>().Single(item => item.AccessibleName == "Name");
                        address.Text = "127.0.0.55"; name.Text = "Saved test speaker";
                        ((Button)addDialog.AcceptButton!).PerformClick();
                    }
                    catch (Exception error) { addError = error; }
                    finally { addDialog.Close(); }
                };
                timer.Start();
                type.GetMethod("AddReceiver", flags)!.Invoke(form, null);
                if (addError is not null) throw addError;
                await TestSettingsAndLanguage(form, type, flags, Check);
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

    private static async Task TestVolumes(Form form, Type type, BindingFlags flags, Action<bool, string> check)
    {
        object Field(string name) => type.GetField(name, flags)!.GetValue(form)!;
        void Call(string name, params object[] args) => type.GetMethod(name, flags)!.Invoke(form, args);
        var receivers = new[] { new Receiver("test-bedroom", "Chambre", "127.0.0.1", 1) { Model = "AudioAccessory5,1" }, new Receiver("test-living", "Salon", "127.0.0.2", 1) { Model = "AudioAccessory6,1" }, new Receiver("test-living-2", "Salon (2)", "127.0.0.3", 1) };
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
            CheckLayout(form);
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
            await Task.Delay(600);
            var loaded = settingsType.GetMethod("Load")!.Invoke(null, null)!;
            var savedVolumes = (Dictionary<string, int>)settingsType.GetProperty("ReceiverVolumes")!.GetValue(loaded)!;
            check(!(bool)settingsType.GetProperty("SharedVolume")!.GetValue(loaded)! && savedVolumes[receivers[0].Id] == 25 && savedVolumes[receivers[1].Id] == 78, "mode and individual volumes survive settings reload");
            Call("SetVolumeMode", true);
            check(SessionVolume(first) == 62 && SessionVolume(second) == 62, "switching to shared applies global level immediately");
            Call("SetVolumeMode", false);
            check(SessionVolume(first) == 25 && SessionVolume(second) == 78, "switching back restores each saved level");
            var moved = receivers[0] with { Host = "127.0.0.4" };
            check((int)type.GetMethod("ReceiverVolume", flags)!.Invoke(form, [moved])! == 25, "speaker volume follows identity after IP change");
            slider = (Control)cards.Controls[0].GetType().GetField("volume", flags)!.GetValue(cards.Controls[0])!;
            check(slider.AccessibilityObject.Value == "25", "accessible slider exposes current value");
            slider.AccessibilityObject.Value = "31";
            check(SessionVolume(first) == 31, "accessible slider changes speaker volume");
            var mute = (Button)cards.Controls[0].GetType().GetField("mute", flags)!.GetValue(cards.Controls[0])!;
            mute.PerformClick(); check(SessionVolume(first) == 0, "speaker mute sends zero");
            mute.PerformClick(); check(SessionVolume(first) == 31, "speaker unmute restores previous level");
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
        check(form.Height <= 650 * form.DeviceDpi / 96 + 2, "speaker list has a compact maximum height");
    }

    private static async Task TestRecovery(Form form, Type type, BindingFlags flags, Action<bool, string> check)
    {
        object Field(string name) => type.GetField(name, flags)!.GetValue(form)!;
        object? Call(string name, params object[] args) => type.GetMethod(name, flags)!.Invoke(form, args);
        Task Run(string name, params object[] args) => (Task)Call(name, args)!;
        var settings = Field("settings");
        var settingsType = settings.GetType();
        settingsType.GetProperty("AutoReconnect")!.SetValue(settings, true);
        var desired = (IDictionary)Field("desired");
        var failures = (IDictionary)Field("failures");
        var connections = (IDictionary)Field("connections");
        var receiver = new Receiver("recovery-test", "Offline speaker", "127.0.0.1", 1);
        Call("UpdateReceivers", (object)new[] { receiver });
        await Run("ToggleConnection", receiver);
        check(connections.Count == 0 && desired.Contains(receiver.Id), "failed connection schedules recovery");
        var recovery = desired[receiver.Id]!;
        var recoveryType = recovery.GetType();
        check((long)recoveryType.GetProperty("NextAttempt")!.GetValue(recovery)! > Environment.TickCount64, "retry waits instead of spinning");
        recoveryType.GetProperty("NextAttempt")!.SetValue(recovery, Environment.TickCount64 - 1);
        Call("RetryConnections");
        var deadline = Environment.TickCount64 + 8000;
        while (connections.Count > 0 && Environment.TickCount64 < deadline) await Task.Delay(20);
        check(connections.Count == 0 && (int)recoveryType.GetProperty("Attempt")!.GetValue(recovery)! == 2, "retry failure increases backoff");
        Call("UpdateReceivers", (object)Array.Empty<Receiver>());
        check(failures.Contains(receiver.Id) && ((FlowLayoutPanel)Field("cards")).Controls.Count == 1, "error remains visible after discovery removes speaker");
        await Run("HandlePowerMode", Microsoft.Win32.PowerModes.Suspend);
        recoveryType.GetProperty("NextAttempt")!.SetValue(recovery, Environment.TickCount64 - 1);
        Call("RetryConnections");
        check(connections.Count == 0, "suspend prevents retry attempts");
        await Run("ToggleConnection", receiver);
        await Run("HandlePowerMode", Microsoft.Win32.PowerModes.Resume);
        check(desired.Count == 0 && connections.Count == 0, "cancel during suspend prevents reconnect on resume");

        var connectionType = type.GetNestedType("Connection", BindingFlags.NonPublic)!;
        using var session = new AirPlaySession(receiver, null, 100, 30, _ => { });
        var connection = Activator.CreateInstance(connectionType, [receiver, session])!;
        connectionType.GetProperty("Connecting")!.SetValue(connection, false);
        connectionType.GetProperty("Streaming")!.SetValue(connection, true);
        connections.Add("127.0.0.1:1", connection);
        desired.Add(receiver.Id, Activator.CreateInstance(recoveryType, [receiver]));
        await Run("HandlePowerMode", Microsoft.Win32.PowerModes.Suspend);
        check(connections.Count == 0 && desired.Count == 1, "suspend tears down active session but preserves playback intent");
        await Run("HandlePowerMode", Microsoft.Win32.PowerModes.Resume);
        check(desired.Count == 1 && !(bool)Field("suspended"), "resume retains scheduled recovery");
        await Run("DisconnectAll");
        Call("RetryConnections");
        check(desired.Count == 0 && connections.Count == 0, "Stop all cancels every scheduled retry");
        settingsType.GetProperty("AutoReconnect")!.SetValue(settings, false);
        await Run("ToggleConnection", receiver);
        check(desired.Count == 0 && failures.Contains(receiver.Id), "disabled recovery preserves error without retry");
        settingsType.GetProperty("AutoReconnect")!.SetValue(settings, true);
        failures.Clear();
        Call("UpdateReceivers", (object)Array.Empty<Receiver>());
        Call("RebuildCards");
    }

    private static async Task TestSettingsAndLanguage(Form form, Type type, BindingFlags flags, Action<bool, string> check)
    {
        object Field(string name) => type.GetField(name, flags)!.GetValue(form)!;
        object? Call(string name, params object[] args) => type.GetMethod(name, flags)!.Invoke(form, args);
        var settings = Field("settings");
        var settingsType = settings.GetType();
        var manual = (List<Receiver>)Field("manualReceivers");
        var added = manual.Single(item => item.Name == "Saved test speaker");
        var loaded = settingsType.GetMethod("Load")!.Invoke(null, null)!;
        check(((List<Receiver>)settingsType.GetProperty("ManualReceivers")!.GetValue(loaded)!).Any(item => item == added), "manual speaker survives settings reload");

        async Task Dialog(Action open, Action<Form> inspect)
        {
            Exception? failure = null;
            var done = new TaskCompletionSource();
            using var timer = new System.Windows.Forms.Timer { Interval = 50 };
            var deadline = Environment.TickCount64 + 8000;
            timer.Tick += (_, _) =>
            {
                var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(item => item.Owner == form);
                if (dialog is null)
                {
                    if (Environment.TickCount64 >= deadline) { timer.Stop(); done.TrySetException(new TimeoutException("Dialog did not open")); }
                    return;
                }
                timer.Stop();
                try { inspect(dialog); }
                catch (Exception error) { failure = error; }
                finally { dialog.DialogResult = DialogResult.OK; }
                form.BeginInvoke(() => done.TrySetResult());
            };
            timer.Start(); open();
            await done.Task;
            if (failure is not null) throw failure;
        }

        await Dialog(() => Call("EditReceiver", added), dialog =>
        {
            dialog.Controls[0].Controls.OfType<TextBox>().Single(item => item.AccessibleName == "Name").Text = "Renamed test speaker";
            ((Button)dialog.AcceptButton!).PerformClick();
        });
        check(manual.Any(item => item.Id == added.Id && item.Name == "Renamed test speaker"), "editing manual speaker keeps stable identity");
        Call("RemoveReceiver", manual.Single(item => item.Id == added.Id));
        loaded = settingsType.GetMethod("Load")!.Invoke(null, null)!;
        check(!((List<Receiver>)settingsType.GetProperty("ManualReceivers")!.GetValue(loaded)!).Any(item => item.Id == added.Id), "removed manual speaker stays removed after reload");

        await Dialog(() => Call("OpenSettings"), dialog =>
        {
            dialog.GetType().GetProperty("Language")!.SetValue(dialog, "fr");
        });
        var cards = (FlowLayoutPanel)Field("cards");
        var salon = new Receiver("french", "Salon", "127.0.0.56", 1) { Model = "AudioAccessory6,1" };
        var chambre = new Receiver("french-mini", "Chambre", "127.0.0.57", 1) { Model = "AudioAccessory5,1" };
        Call("UpdateReceivers", (object)new[] { salon, chambre });
        check(cards.Controls[0].Controls.OfType<Button>().Any(button => button.Text == "Diffuser"), "language change rebuilds dashboard in French");
        var volume = (Control)Field("volume");
        volume.GetType().GetProperty("Value")!.SetValue(volume, 36);
        check(!volume.IsDisposed && volume.AccessibilityObject.Value == "36", "language switch preserves live volume control");
        check(cards.Controls[0].Controls.Cast<Control>().Any(control => control.AccessibleName == "HomePod"), "full-size HomePod illustration selected from model");
        check(cards.Controls[1].Controls.Cast<Control>().Any(control => control.AccessibleName == "HomePod mini"), "HomePod mini illustration selected from model");
        var connections = (IDictionary)Field("connections");
        using var session = new AirPlaySession(salon, null, 100, 45, _ => { });
        var connectionType = type.GetNestedType("Connection", flags)!;
        var connection = Activator.CreateInstance(connectionType, [salon, session])!;
        connectionType.GetProperty("Connecting")!.SetValue(connection, false);
        connectionType.GetProperty("Streaming")!.SetValue(connection, true);
        connections.Add("127.0.0.56:1", connection);
        Call("SetReceiverVolume", salon, 45);
        Call("RebuildCards");
        type.GetMethod("UpdateStatus", flags)!.Invoke(form, new object?[] { null });
        using (var bitmap = new Bitmap(form.Width, form.Height))
        {
            CheckLayout(form);
            form.DrawToBitmap(bitmap, form.ClientRectangle);
            bitmap.Save("build/ui-tests/dashboard-fr.png");
        }
        connections.Clear();
        await Dialog(() => Call("OpenSettings"), dialog =>
        {
            CheckLayout(dialog);
            using var bitmap = new Bitmap(dialog.Width, dialog.Height);
            dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size));
            bitmap.Save("build/ui-tests/settings-fr.png");
            check(((Control)dialog.AcceptButton!).Bottom <= ((Control)dialog.AcceptButton!).Parent!.ClientSize.Height, "French save button fits dialog");
            dialog.GetType().GetProperty("Language")!.SetValue(dialog, "en");
        });
        settingsType.GetProperty("MinimizeOnClose")!.SetValue(settings, true);
        form.Close();
        check(!form.IsDisposed && !form.Visible, "close keeps app running when setting enabled");
        Call("RestoreWindow");
        settingsType.GetProperty("MinimizeOnClose")!.SetValue(settings, false);
        form.Close();
        check(form.IsDisposed, "close exits when keep-playing setting disabled");
    }

    private static void CheckLayout(Control parent)
    {
        var children = parent.Controls.Cast<Control>().Where(control => control.Visible).ToArray();
        foreach (var child in children)
        {
            if (child.AccessibleRole == AccessibleRole.Slider && !parent.ClientRectangle.Contains(child.Bounds))
                throw new Exception("Clipped volume slider: " + child.AccessibleName);
            if (child is Button button)
            {
                if (!parent.ClientRectangle.Contains(button.Bounds)) throw new Exception("Clipped button: " + button.Text);
                var text = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                if (text.Width > button.ClientSize.Width - 6 || text.Height > button.ClientSize.Height - 4)
                    throw new Exception("Clipped button text: " + button.Text + " " + button.ClientSize);
            }
            foreach (var sibling in children.Where(control => control != child))
                if (child.Bounds.IntersectsWith(sibling.Bounds)) throw new Exception("Overlapping controls: " + child.Text + " / " + sibling.Text);
            CheckLayout(child);
        }
    }
}
