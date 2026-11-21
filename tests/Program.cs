using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RobotVisionSortingTwin;

internal static class Program
{
    private static int _checks;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException("FAIL: " + name);
        }

        _checks++;
    }

    private static void ExpectFailure(Action action, string name)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or InvalidDataException)
        {
            _checks++;
            return;
        }

        throw new InvalidOperationException("FAIL: " + name);
    }

    public static async Task Main(string[] args)
    {
        if (args is ["--platform-sender", var senderExecutable])
        {
            await VerifyExternalPublisherAsync(senderExecutable);
            return;
        }

        if (args is ["--render", var output])
        {
            Render(output);
            return;
        }

        var vision = new DemoVisionService();
        foreach (var kind in Enum.GetValues<TwinProductKind>())
        {
            var normal = vision.Inspect(kind, 21, false);
            Check(normal.Route == kind, "pixel vision classifies " + kind);
            Check(normal.Recognized == (kind != TwinProductKind.Reject), "unique match " + kind);
            Check(!vision.Inspect(kind, 22, true).Recognized, "underexposure rejects " + kind);
        }

        var sim = new SortingSimulation(vision.Inspect);
        sim.Reset();
        for (var i = 0; i < 3600; i++)
        {
            sim.Step();
            Check(sim.Received == sim.Sorted + sim.Rejected + sim.InFlight, "material conservation");
            Check(sim.Bins.All(count => count <= sim.BinCapacity), "bin capacity bounded");
            var belt = sim.Items.Where(item => item.Stage == TwinItemStage.Conveyor)
                .OrderBy(item => item.X).ToArray();
            Check(belt.Zip(belt.Skip(1)).All(pair => pair.Second.X - pair.First.X >= 0.8499),
                "conveyor spacing");
            if (sim.ActiveItem is not null)
            {
                var tool = TwinScene.ToolPosition(sim);
                var elbow = TwinScene.ElbowPosition(tool);
                var wrist = tool + new System.Windows.Media.Media3D.Vector3D(0, 0.38, 0);
                Check(Math.Abs((elbow - TwinScene.RobotBase).Length - 2.3) < 1e-8
                    && Math.Abs((wrist - elbow).Length - 2.3) < 1e-8, "IK link lengths");
            }
        }

        Check(sim.IsComplete && sim.InFlight == 0 && sim.Sorted > 20 && sim.Rejected == 3,
            "default shift naturally clears all workpieces");
        var baseline = Snapshot(sim);
        sim.Seek(41.3);
        sim.Seek(120);
        Check(Snapshot(sim) == baseline, "seek reproduces default shift");
        sim.Reset();
        Advance(sim, 18);
        sim.Control(TwinControl.BeltSpeed, 0.8);
        sim.Control(TwinControl.RobotCycle, 3.6);
        sim.Control(TwinControl.PoorLighting, 1);
        Advance(sim, 13);
        sim.Control(TwinControl.PoorLighting, 0);
        Advance(sim, 19);
        var modified = Snapshot(sim);
        sim.Seek(8);
        sim.Seek(50);
        Check(Snapshot(sim) == modified, "interventions reproduce exact state");
        sim.Seek(10);
        sim.Control(TwinControl.FeedInterval, 4);
        sim.Seek(50);
        Check(sim.Interventions.Count == 1 && sim.BeltSpeed == 1.4,
            "editing historical state truncates future branch");
        sim.Reset();
        while (sim.ActiveItem is null)
        {
            sim.Step();
        }

        sim.Control(TwinControl.EmergencyStop, 1);
        sim.Step();
        var beforeStop = sim.Items.Select(item => item.X).ToArray();
        var progress = sim.RobotProgress;
        var beltTravel = sim.BeltTravel;
        Advance(sim, 3);
        Check(beforeStop.SequenceEqual(sim.Items.Select(item => item.X))
            && sim.RobotProgress == progress && sim.BeltTravel == beltTravel,
            "emergency stop freezes workpieces, robot and belt surface");
        sim.Control(TwinControl.EmergencyStop, 0);
        Advance(sim, 3);
        Check(!sim.EmergencyStopped && sim.Sorted > 0, "recover resumes work");
        sim.Reset();
        sim.Control(TwinControl.BinCapacity, 3);
        Advance(sim, 65);
        Check(sim.Alarm.Contains("已满"), "full bin interlock");
        var sorted = sim.Sorted;
        sim.Control(TwinControl.UnloadBins, 0);
        Advance(sim, 8);
        Check(sim.Sorted > sorted, "unloading bins resumes sorting");
        sim.Reset(true);
        var liveInspection = vision.Inspect(TwinProductKind.Block, 1, false);
        for (var i = 0; i < 70; i++)
        {
            sim.ReceiveExternal(liveInspection);
        }

        Check(sim.InFlight == 64 && sim.DroppedExternal == 6, "bounded external queue");
        Check(sim.Received == sim.InFlight, "accepted telemetry conservation");
        Advance(sim, 4);
        Check(sim.Items.All(item => item.X >= -8), "queued input stays inside factory");
        ExpectFailure(() => sim.Seek(4), "live timeline disabled");
        ExpectFailure(() => sim.Control(TwinControl.BeltSpeed, double.NaN), "reject nonfinite speed");
        ExpectFailure(() => new TelemetryMessage { Schema = "v99" }.Validate(), "reject protocol version");
        ExpectFailure(() => new TelemetryMessage
        {
            RunId = Guid.NewGuid(), Sequence = 1, Status = "Recognized"
        }.Validate(), "recognized telemetry must have product identity");
        VerifyWindowLink();
        await VerifyPipeAsync();
        Console.WriteLine($"SUCCESS: {_checks} checks; independent C# vision, simulation, IK, replay and IPC.");
    }

    private static void Advance(SortingSimulation sim, double seconds)
    {
        for (var i = 0; i < (int)Math.Round(seconds / SortingSimulation.StepSeconds); i++)
        {
            sim.Step();
        }
    }

    private static string Snapshot(SortingSimulation sim)
    {
        return JsonSerializer.Serialize(new
        {
            sim.Tick, sim.Received, sim.Inspected, sim.Sorted, sim.Rejected,
            sim.Bins, sim.Items, sim.RobotProgress, sim.BeltTravel, sim.Events, sim.Alarm
        });
    }

    private static async Task VerifyPipeAsync()
    {
        var messages = 0;
        var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new TelemetryServer();
        server.Received = message =>
        {
            if (Interlocked.Increment(ref messages) == 3)
            {
                allReceived.TrySetResult();
            }

            return Task.CompletedTask;
        };
        server.Start();
        var message = JsonSerializer.Serialize(new TelemetryMessage
        {
            Type = "sample", Running = true, RunId = Guid.NewGuid(), Sequence = 1,
            Status = "NoMatch", Time = DateTimeOffset.UtcNow
        }) + "\n";
        await using (var client = new NamedPipeClientStream(".", TelemetryServer.PipeName,
                         PipeDirection.Out, PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(3000);
            var bytes = Encoding.UTF8.GetBytes(message + message);
            await client.WriteAsync(bytes.AsMemory(0, 13));
            await client.WriteAsync(bytes.AsMemory(13));
        }

        await using (var client = new NamedPipeClientStream(".", TelemetryServer.PipeName,
                         PipeDirection.Out, PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(3000);
            await client.WriteAsync(Encoding.UTF8.GetBytes(message));
            await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Check(messages == 3, "IPC split packets, coalesced messages and reconnect");
    }

    /// <summary>启动原平台测试发送器，验证实际两个进程之间的协议、图像和启停。</summary>
    private static async Task VerifyExternalPublisherAsync(string executable)
    {
        var samples = new List<TelemetryMessage>();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedCatalog = false;
        await using var server = new TelemetryServer();
        server.Received = message =>
        {
            receivedCatalog |= message.Products.Any(product => product.Name == "跨进程验证工件");
            if (message.Type == "sample")
            {
                samples.Add(message);
            }

            if (samples.Count == 3 && !message.Running)
            {
                stopped.TrySetResult();
            }

            return Task.CompletedTask;
        };
        server.Start();
        var start = new System.Diagnostics.ProcessStartInfo(executable, "--publish-twin")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("无法启动平台遥测测试发送器。");
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var error = await process.StandardError.ReadToEndAsync();
        Check(process.ExitCode == 0 && string.IsNullOrWhiteSpace(error), "publisher child exits cleanly");
        Check(receivedCatalog && samples.Count == 3, "cross-process product catalog and sample count");
        Check(samples.Select(sample => sample.Sequence).SequenceEqual(new long[] { 1, 2, 3 }),
            "cross-process sample ordering");
        Check(samples.All(sample => sample.Status == "Recognized" && sample.ImageJpeg?.Length > 100),
            "cross-process recognition and JPEG preview");
        Console.WriteLine($"SUCCESS: {_checks} cross-process checks; original platform publisher → standalone twin receiver.");
    }

    /// <summary>检查真实窗口中的 GUID 映射、去重、重复映射复检及新会话复位。</summary>
    private static void VerifyWindowLink()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var window = new MainWindow();
                var receive = typeof(MainWindow).GetMethod("ReceiveTelemetry",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var product = new TelemetryProduct(Guid.NewGuid(), "现场产品 X");
                receive.Invoke(window, [new TelemetryMessage
                {
                    Type = "session", Running = true, Products = [product]
                }]);
                var routeB = (System.Windows.Controls.ComboBox)window.FindName("RouteB");
                routeB.SelectedValue = product.Id;
                var live = (System.Windows.Controls.CheckBox)window.FindName("LiveCheck");
                live.IsChecked = true;
                live.RaiseEvent(new RoutedEventArgs(
                    System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                var sample = new TelemetryMessage
                {
                    RunId = Guid.NewGuid(), Sequence = 1, Running = true,
                    ProductId = product.Id, ProductName = product.Name, Status = "Recognized",
                    Area = 1200, Perimeter = 180
                };
                receive.Invoke(window, [sample]);
                window.Simulation.Step();
                Check(window.Simulation.Items.Single().Route == TwinProductKind.Block,
                    "window routes product GUID to selected B bin");
                receive.Invoke(window, [sample]);
                Check(window.Simulation.Received == 1, "window ignores duplicate sample");
                sample.Sequence = 3;
                receive.Invoke(window, [sample]);
                var details = (System.Windows.Controls.TextBlock)window.FindName("LinkDetails");
                Check(details.Text.Contains("序号缺口 1"), "window exposes dropped sequence gap");
                ((System.Windows.Controls.ComboBox)window.FindName("RouteA")).SelectedValue = product.Id;
                ((System.Windows.Controls.ComboBox)window.FindName("RouteC")).SelectedValue = product.Id;
                sample.Sequence = 4;
                receive.Invoke(window, [sample]);
                Check(window.Simulation.LastInspection?.Route == TwinProductKind.Reject,
                    "all duplicate mappings reject instead of arbitrary route");
                sample.RunId = Guid.NewGuid();
                sample.Sequence = 1;
                receive.Invoke(window, [sample]);
                Check(window.Simulation.Received == 1, "new platform run resets previous scene");
                receive.Invoke(window, [new TelemetryMessage { Type = "session", Running = false }]);
                window.RefreshVisuals();
                Check(((System.Windows.Controls.Button)window.FindName("PlayButton"))
                    .Content.ToString()!.Contains("继续跟随"), "source stop pauses window playback");
                window.Close();
                var frame = new System.Windows.Threading.DispatcherFrame();
                _ = System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                    new Action(() =>
                    {
                        frame.Continue = false;
                    }));
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                app.Shutdown();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw failure;
        }
    }

    private static void Render(string output)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var window = new MainWindow();
                window.Simulation.Seek(23.5);
                window.RefreshVisuals();
                Directory.CreateDirectory(output);
                foreach (var size in new[] { new Size(1560, 960), new Size(1100, 760) })
                {
                    var content = (FrameworkElement)window.Content;
                    content.Measure(size);
                    content.Arrange(new Rect(size));
                    content.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height,
                        96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(output,
                        $"sorting-twin-{size.Width:0}.png"));
                    encoder.Save(stream);
                }

                app.Shutdown();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw failure;
        }

        Console.WriteLine("Rendered: " + output);
    }
}
