using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace RobotVisionSortingTwin
{
    public partial class MainWindow : Window
    {
        private readonly DemoVisionService _vision = new();
        private readonly TelemetryServer _server = new();
        private readonly DispatcherTimer _timer = new(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Dictionary<Guid, TwinProductKind> _routes = new();
        private readonly SortingSimulation _simulation;
        private bool _ready;
        private bool _playing;
        private bool _refreshing;
        private bool _scrubbing;
        private bool _sourceRunning;
        private bool _closing;
        private bool _allowClose;
        private bool _emergencyRequested;
        private double _speed = 1;
        private double _lastTime;
        private double _accumulator;
        private Guid _lastRun;
        private long _lastSequence;
        private long _missingSamples;
        private long _duplicates;
        private DateTimeOffset _lastMessage;
        private TwinInspection? _displayedInspection;

        internal SortingSimulation Simulation => _simulation;

        /// <summary>独立启动沙盘与本机接收端，不加载现有平台的程序集和配置。</summary>
        public MainWindow()
        {
            _simulation = new SortingSimulation(_vision.Inspect);
            InitializeComponent();
            _simulation.Reset();
            _ready = true;
            Scene.DeviceSelected += Scene_DeviceSelected;
            _server.Received = async message =>
            {
                await Dispatcher.InvokeAsync(() => ReceiveTelemetry(message));
            };
            _server.StateChanged = async state =>
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_closing)
                    {
                        return;
                    }

                    ConnectionText.Text = state;
                    if (!state.StartsWith("平台已连接", StringComparison.Ordinal))
                    {
                        _sourceRunning = false;
                        if (_simulation.LiveMode)
                        {
                            _playing = false;
                        }
                    }
                });
            };
            _timer.Tick += Timer_Tick;
            Loaded += (_, _) =>
            {
                _lastTime = _clock.Elapsed.TotalSeconds;
                _server.Start();
                _timer.Start();
            };
            RefreshVisuals();
        }

        /// <summary>墙钟只决定推进步数；窗口停顿不会一次补算大量动作。</summary>
        private void Timer_Tick(object? sender, EventArgs e)
        {
            var now = _clock.Elapsed.TotalSeconds;
            var elapsed = Math.Clamp(now - _lastTime, 0, 0.2);
            _lastTime = now;
            if (_simulation.LiveMode && _lastMessage != default
                && DateTimeOffset.UtcNow - _lastMessage > TimeSpan.FromSeconds(4))
            {
                _sourceRunning = false;
                _playing = false;
                ConnectionText.Text = "平台消息超时 · 仿真已暂停，等待恢复";
            }

            if (_playing && !_scrubbing)
            {
                _accumulator += elapsed * (_simulation.LiveMode ? 1 : _speed);
                while (_accumulator >= SortingSimulation.StepSeconds)
                {
                    _simulation.Step();
                    _accumulator -= SortingSimulation.StepSeconds;
                    if (_simulation.IsComplete)
                    {
                        _playing = false;
                        _accumulator = 0;
                        break;
                    }
                }
            }

            if (WindowState != WindowState.Minimized)
            {
                RefreshVisuals();
            }
        }

        /// <summary>展示帧只读取仿真状态，回放拖动与参数更新使用保护标记。</summary>
        internal void RefreshVisuals()
        {
            if (!_ready)
            {
                return;
            }

            _refreshing = true;
            try
            {
                Scene.Render(_simulation);
                ReceivedText.Text = _simulation.Received.ToString(CultureInfo.InvariantCulture);
                InspectedText.Text = _simulation.Inspected.ToString(CultureInfo.InvariantCulture);
                SortedText.Text = _simulation.Sorted.ToString(CultureInfo.InvariantCulture);
                RejectedText.Text = $"{_simulation.Rejected} / {_simulation.InFlight}";
                ClockText.Text = TimeSpan.FromSeconds(_simulation.Time).ToString(@"mm\:ss\.f");
                PlayButton.Content = _playing ? "Ⅱ  暂停仿真"
                    : _simulation.LiveMode ? "▶  继续跟随" : "▶  开始演示";
                ModeBadge.Text = _simulation.LiveMode ? "● 平台联动 / 本机消息 / 仿真动作"
                    : "● 独立演示 / 纯 C# / 无需硬件";
                if (!_scrubbing && !_simulation.LiveMode)
                {
                    Timeline.Value = _simulation.Time;
                }

                Timeline.IsEnabled = !_simulation.LiveMode;
                PlaybackSpeed.IsEnabled = !_simulation.LiveMode;
                FeedSlider.IsEnabled = !_simulation.LiveMode;
                LightingCheck.IsEnabled = !_simulation.LiveMode;
                SceneStatus.Text = _simulation.Alarm.Length > 0 ? _simulation.Alarm
                    : _simulation.IsComplete ? $"班次结束 · 在途 {_simulation.InFlight} 件（保留现场）"
                    : _simulation.LiveMode && !_sourceRunning ? "等待平台开启自动识别"
                    : _playing ? "产线运行中 · 工业机器人自动分拣" : "产线暂停 · 可检查设备与回放";
                SpeedLabel.Text = $"输送速度   {BeltSlider.Value:F2} m/s";
                FeedLabel.Text = $"上料间隔   {FeedSlider.Value:F2} s";
                CycleLabel.Text = $"机器人节拍   {CycleSlider.Value:F2} s";
                CapacityLabel.Text = $"单仓容量   {CapacitySlider.Value:F0} 件";
                BinText.Text = $"料仓 A  {_simulation.Bins[0]}/{_simulation.BinCapacity}     "
                    + $"B  {_simulation.Bins[1]}/{_simulation.BinCapacity}     "
                    + $"C  {_simulation.Bins[2]}/{_simulation.BinCapacity}";
                EventText.Text = string.Join(Environment.NewLine,
                    _simulation.Events.TakeLast(4).Reverse().Select(entry =>
                        $"{entry.Time,5:F1}s  {entry.Device}  {entry.Description}"));
                var inspection = _simulation.LastInspection;
                if (!ReferenceEquals(inspection, _displayedInspection))
                {
                    _displayedInspection = inspection;
                    CameraImage.Source = inspection?.Image;
                    CameraHint.Visibility = inspection?.Image is null
                        ? Visibility.Visible : Visibility.Collapsed;
                    VisionResult.Text = inspection?.Description ?? "尚未检测到工件";
                    VisionFeatures.Text = $"面积 {inspection?.Area?.ToString("F0") ?? "—"} px² / "
                        + $"周长 {inspection?.Perimeter?.ToString("F1") ?? "—"} px";
                }

                EmergencyButton.Content = _emergencyRequested ? "恢复仿真" : "■  仿真急停";
                UpdateDeviceDetail();
            }
            finally
            {
                _refreshing = false;
            }
        }

        /// <summary>校验后的本地消息只驱动仿真；一次采样对应一件展示工件。</summary>
        private void ReceiveTelemetry(TelemetryMessage message)
        {
            if (_closing)
            {
                return;
            }

            _lastMessage = DateTimeOffset.UtcNow;
            var wasRunning = _sourceRunning;
            _sourceRunning = message.Running;
            ConnectionText.Text = message.Running ? "平台在线 · 自动识别运行中" : "平台在线 · 自动识别已停止";
            UpdateProductChoices(message.Products);
            if (!_simulation.LiveMode)
            {
                return;
            }

            if (!_sourceRunning)
            {
                _playing = false;
                return;
            }

            if (!wasRunning)
            {
                _playing = true;
                _lastTime = _clock.Elapsed.TotalSeconds;
            }

            if (message.Type != "sample")
            {
                return;
            }

            if (message.RunId != _lastRun)
            {
                _simulation.Reset(true);
                _lastRun = message.RunId;
                _lastSequence = 0;
                _missingSamples = 0;
                _duplicates = 0;
                _emergencyRequested = false;
                SyncParameters();
            }

            if (message.Sequence <= _lastSequence)
            {
                _duplicates++;
                return;
            }

            _missingSamples += Math.Max(0, message.Sequence - _lastSequence - 1);
            _lastSequence = message.Sequence;
            var route = message.Status == "Recognized" && message.ProductId is { } productId
                && _routes.TryGetValue(productId, out var mapped) ? mapped : TwinProductKind.Reject;
            var description = message.Status != "Recognized" ? $"{message.Status} · 进入复检"
                : route == TwinProductKind.Reject ? $"{message.ProductName} · 尚未映射料仓，进入复检"
                : $"{message.ProductName} → 料仓 {(char)('A' + (int)route)}";
            _simulation.ReceiveExternal(new TwinInspection(route,
                message.ProductName ?? "待复检工件", message.Status == "Recognized",
                message.Area, message.Perimeter, DecodeImage(message.ImageJpeg), description));
            LinkDetails.Text = $"采样 #{message.Sequence} · 序号缺口 {_missingSamples} · "
                + $"重复 {_duplicates} · 动画队列溢出 {_simulation.DroppedExternal}\n"
                + (message.Sent ? "产品类型已写入机器人 TCP；未确认实体机器人执行。"
                    : "此采样未写入机器人 TCP。")
                + "\n连续采样会重复展示同一实体工件；沙盘无实体去重依据。";
        }

        private static BitmapSource? DecodeImage(string? encoded)
        {
            if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > 160000)
            {
                return null;
            }

            try
            {
                using var stream = new MemoryStream(Convert.FromBase64String(encoded));
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 320;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException
                or IOException or ArgumentException)
            {
                return null;
            }
        }

        /// <summary>按产品 GUID 保留选择，列表变化不会按顺序错误重映射。</summary>
        private void UpdateProductChoices(TelemetryProduct[] products)
        {
            if (products.Length == 0)
            {
                return;
            }

            var current = RouteA.ItemsSource as TelemetryProduct[];
            if (current is not null && current.Skip(1).SequenceEqual(products))
            {
                return;
            }

            _refreshing = true;
            foreach (var combo in new[] { RouteA, RouteB, RouteC })
            {
                var id = combo.SelectedValue is Guid previous ? previous : Guid.Empty;
                combo.ItemsSource = new[] { new TelemetryProduct(Guid.Empty, "（未映射）") }
                    .Concat(products).ToArray();
                combo.SelectedValue = products.Any(product => product.Id == id) ? id : Guid.Empty;
            }

            _refreshing = false;
            RebuildRoutes();
        }

        private void RebuildRoutes()
        {
            _routes.Clear();
            var combos = new[] { RouteA, RouteB, RouteC };
            var selected = combos.Select(combo => combo.SelectedValue is Guid id ? id : Guid.Empty)
                .ToArray();
            for (var i = 0; i < combos.Length; i++)
            {
                var id = selected[i];
                if (id != Guid.Empty)
                {
                    if (selected.Count(value => value == id) > 1)
                    {
                        LinkDetails.Text = "同一产品被分配到多个料仓；该产品将进入复检，请修正映射。";
                    }
                    else
                    {
                        _routes.Add(id, (TwinProductKind)i);
                    }
                }
            }
        }

        private void Play_Click(object sender, RoutedEventArgs e)
        {
            if (_simulation.LiveMode && !_sourceRunning)
            {
                ConnectionText.Text = "请在现有平台开启自动识别，并启用“同步到独立三维沙盘”。";
                return;
            }

            if (_simulation.IsComplete)
            {
                _simulation.Reset();
                SyncParameters();
            }

            _playing = !_playing;
            _lastTime = _clock.Elapsed.TotalSeconds;
            _accumulator = 0;
            RefreshVisuals();
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            _simulation.Reset(_simulation.LiveMode);
            _playing = _simulation.LiveMode && _sourceRunning;
            _accumulator = 0;
            _emergencyRequested = false;
            SyncParameters();
            RefreshVisuals();
        }

        private void Live_Click(object sender, RoutedEventArgs e)
        {
            _simulation.Reset(LiveCheck.IsChecked == true);
            _playing = _simulation.LiveMode && _sourceRunning;
            _lastRun = Guid.Empty;
            _lastSequence = 0;
            _accumulator = 0;
            _emergencyRequested = false;
            SyncParameters();
            RefreshVisuals();
        }

        private void Parameter_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready || _refreshing || sender is not Slider { Tag: string tag }
                || !Enum.TryParse<TwinControl>(tag, out var control))
            {
                return;
            }

            _simulation.Control(control, e.NewValue);
        }

        private void Intervention_Changed(object sender, RoutedEventArgs e)
        {
            if (_ready)
            {
                ParameterPanel.IsEnabled = InterventionCheck.IsChecked == true;
            }
        }

        private void Lighting_Click(object sender, RoutedEventArgs e)
        {
            _simulation.Control(TwinControl.PoorLighting, LightingCheck.IsChecked == true ? 1 : 0);
        }

        private void Emergency_Click(object sender, RoutedEventArgs e)
        {
            _emergencyRequested = !_emergencyRequested;
            _simulation.Control(TwinControl.EmergencyStop, _emergencyRequested ? 1 : 0);
            RefreshVisuals();
        }

        private void Unload_Click(object sender, RoutedEventArgs e)
        {
            _simulation.Control(TwinControl.UnloadBins, 0);
        }

        private void Route_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_ready && !_refreshing)
            {
                RebuildRoutes();
            }
        }

        private void SyncParameters()
        {
            _refreshing = true;
            BeltSlider.Value = _simulation.BeltSpeed;
            FeedSlider.Value = _simulation.FeedInterval;
            CycleSlider.Value = _simulation.RobotCycle;
            CapacitySlider.Value = _simulation.BinCapacity;
            LightingCheck.IsChecked = _simulation.PoorLighting;
            _emergencyRequested = _simulation.EmergencyStopped;
            _refreshing = false;
        }

        private void Timeline_Down(object sender, MouseButtonEventArgs e)
        {
            _scrubbing = true;
        }

        private void Timeline_Up(object sender, MouseButtonEventArgs e)
        {
            _scrubbing = false;
            SeekTimeline();
        }

        private void Timeline_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_ready && !_refreshing && !_scrubbing)
            {
                SeekTimeline();
            }
        }

        private void SeekTimeline()
        {
            if (_simulation.LiveMode)
            {
                return;
            }

            _playing = false;
            _accumulator = 0;
            _simulation.Seek(Timeline.Value);
            SyncParameters();
            RefreshVisuals();
        }

        private void PlaybackSpeed_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_ready && PlaybackSpeed.SelectedItem is ComboBoxItem { Tag: string value })
            {
                _speed = double.Parse(value, CultureInfo.InvariantCulture);
            }
        }

        private void View_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string view })
            {
                Scene.SetView(view);
            }
        }

        private void LinkSettings_Click(object sender, RoutedEventArgs e)
        {
            InspectorScroll.ScrollToEnd();
        }

        private void Device_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_ready && DeviceList.SelectedItem is ListBoxItem { Tag: string id })
            {
                Scene.SelectDevice(id);
                RefreshVisuals();
            }
        }

        private void Scene_DeviceSelected(object? sender, string id)
        {
            DeviceList.SelectedItem = DeviceList.Items.OfType<ListBoxItem>()
                .FirstOrDefault(item => item.Tag as string == id);
        }

        private void UpdateDeviceDetail()
        {
            if (DeviceList.SelectedItem is not ListBoxItem { Tag: string id } item)
            {
                return;
            }

            DeviceTitle.Text = item.Content + " / " + id;
            DeviceDetail.Text = id switch
            {
                "CAM-01" => _simulation.LiveMode
                    ? "图像与测量结果来自平台自动识别。平台负责相机采集和视觉算法。"
                    : "模拟顶部工业相机。纯 C# 执行阈值分割、连通域测量和双特征唯一匹配。",
                "ROBOT-01" => $"状态：{(_simulation.ActiveItem is null ? "等待工件" : "抓取 / 转运")}\n"
                    + $"节拍 {_simulation.RobotCycle:F2} s · 两连杆逆解与垂直末端动画。",
                "BELT-01" => $"速度 {_simulation.BeltSpeed:F2} m/s · 在途 {_simulation.InFlight} 件。"
                    + "工件排队间距 0.85 m，待抓工件在取料位等待。",
                "FEED-01" => _simulation.LiveMode ? "上料由平台采样消息驱动；一次采样生成一件展示工件。"
                    : $"间隔 {_simulation.FeedInterval:F2} s，86 s 后停止上料并等待清线。",
                "REJECT-01" => $"复检累计 {_simulation.Rejected} 件。无法唯一识别或未映射产品进入复检。",
                _ => "料仓满时暂停输送；清空料仓后继续。累计分拣数量不会因清仓减少。"
            };
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "JSON 记录|*.json",
                FileName = $"sorting-twin_{DateTime.Now:yyyyMMdd_HHmmss}.json"
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            try
            {
                var report = new
                {
                    Schema = "robot-vision-twin-report/v1",
                    Mode = _simulation.LiveMode ? "platform" : "demo",
                    _simulation.Time,
                    _simulation.Received,
                    _simulation.Inspected,
                    _simulation.Sorted,
                    _simulation.Rejected,
                    _simulation.InFlight,
                    _simulation.Bins,
                    _simulation.DroppedExternal,
                    MissingSamples = _missingSamples,
                    Routes = _routes,
                    _simulation.Interventions,
                    RecentEvents = _simulation.Events
                };
                File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(report,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, ex.Message, "导出未完成");
            }
        }

        private async void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (_allowClose)
            {
                return;
            }

            e.Cancel = true;
            if (_closing)
            {
                return;
            }

            _closing = true;
            _timer.Stop();
            await _server.DisposeAsync();
            _allowClose = true;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Close));
        }
    }
}
