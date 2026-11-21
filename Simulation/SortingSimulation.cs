namespace RobotVisionSortingTwin
{
    /// <summary>固定步长产线；场景只读状态，参数干预按仿真步记录并可重放。</summary>
    public sealed class SortingSimulation
    {
        public const double StepSeconds = 1.0 / 30;
        public const double ShiftSeconds = 120;
        public const double CameraX = -3.2;
        public const double PickupX = 1;
        private readonly Func<TwinProductKind, int, bool, TwinInspection> _inspect;
        private readonly List<TwinItem> _items = new();
        private readonly Queue<TwinItem> _pendingItems = new();
        private readonly List<TwinEvent> _events = new();
        private readonly List<TwinIntervention> _interventions = new();
        private double _nextFeed = 1;
        private int _robotStart;
        private double _activeCycle;

        public int Tick { get; private set; }
        public double Time => Tick * StepSeconds;
        public double BeltSpeed { get; private set; } = 1.4;
        public double BeltTravel { get; private set; }
        public double FeedInterval { get; private set; } = 2.8;
        public double RobotCycle { get; private set; } = 2.2;
        public int BinCapacity { get; private set; } = 12;
        public bool PoorLighting { get; private set; }
        public bool EmergencyStopped { get; private set; }
        public bool LiveMode { get; private set; }
        public int Received { get; private set; }
        public int Inspected { get; private set; }
        public int Sorted { get; private set; }
        public int Rejected { get; private set; }
        public int[] Bins { get; } = new int[3];
        public TwinItem? ActiveItem { get; private set; }
        public TwinInspection? LastInspection { get; private set; }
        public string Alarm { get; private set; } = string.Empty;
        public int DroppedExternal { get; private set; }
        public IReadOnlyList<TwinItem> Items => _items;
        public IReadOnlyList<TwinEvent> Events => _events;
        public IReadOnlyList<TwinIntervention> Interventions => _interventions;
        public bool IsComplete => !LiveMode && Time >= ShiftSeconds;
        public double RobotProgress => ActiveItem is null ? 0
            : Math.Clamp((Tick - _robotStart) * StepSeconds / _activeCycle, 0, 1);
        public int InFlight => _items.Count + _pendingItems.Count;
        public int Pending => _pendingItems.Count;

        /// <summary>识别函数接收帧生成条件，返回实际视觉判定；状态机不自行猜测类别。</summary>
        public SortingSimulation(Func<TwinProductKind, int, bool, TwinInspection> inspect)
        {
            _inspect = inspect;
        }

        /// <summary>复位所有状态；外部联动使用无限时钟且关闭演示上料。</summary>
        public void Reset(bool liveMode = false)
        {
            _interventions.Clear();
            LiveMode = liveMode;
            ResetState();
        }

        /// <summary>保存下一仿真步生效的干预；从历史位置改参数时截去未来分支。</summary>
        public void Control(TwinControl control, double value)
        {
            var valid = double.IsFinite(value) && control switch
            {
                TwinControl.BeltSpeed => value is >= 0.4 and <= 2.2,
                TwinControl.FeedInterval => value is >= 1.5 and <= 5,
                TwinControl.RobotCycle => value is >= 1.2 and <= 5,
                TwinControl.BinCapacity => value is >= 3 and <= 30 && value == Math.Floor(value),
                TwinControl.PoorLighting or TwinControl.EmergencyStop => value is 0 or 1,
                TwinControl.UnloadBins => value == 0,
                _ => false
            };
            if (!valid)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _interventions.RemoveAll(entry => entry.Tick > Tick + 1);
            _interventions.Add(new TwinIntervention(Tick + 1, control, value));
        }

        /// <summary>通过重放同一干预日志回到指定时间；外部识别流不允许改写历史。</summary>
        public void Seek(double seconds)
        {
            if (LiveMode || !double.IsFinite(seconds))
            {
                throw new InvalidOperationException("平台联动模式不支持时间轴回放。");
            }

            var target = (int)Math.Round(Math.Clamp(seconds, 0, ShiftSeconds) / StepSeconds);
            ResetState();
            while (Tick < target)
            {
                Step();
            }
        }

        /// <summary>外部采样按次展示；最多保留 64 件在途工件，溢出明确计数而不默默丢弃。</summary>
        public bool ReceiveExternal(TwinInspection inspection)
        {
            if (!LiveMode)
            {
                return false;
            }

            LastInspection = inspection;
            if (InFlight >= 64)
            {
                DroppedExternal++;
                AddEvent("平台联动", "动画队列已满，本采样仅显示测量结果，未生成工件。");
                return false;
            }

            Received++;
            Inspected++;
            _pendingItems.Enqueue(new TwinItem
            {
                Id = Received,
                Shape = inspection.Route,
                Route = inspection.Route,
                Name = inspection.Name,
                Inspected = true
            });
            AddEvent("工业相机", inspection.Description);
            return true;
        }

        /// <summary>推进一步；急停冻结输送与机械臂，仍允许恢复指令和时间轴前进。</summary>
        public void Step()
        {
            if (IsComplete)
            {
                return;
            }

            Tick++;
            foreach (var entry in _interventions.Where(entry => entry.Tick == Tick))
            {
                Apply(entry);
            }

            Alarm = EmergencyStopped ? "急停已触发 · 输送与机器人冻结" : string.Empty;
            if (EmergencyStopped)
            {
                if (ActiveItem is not null)
                {
                    _robotStart++;
                }

                _nextFeed += StepSeconds;
                return;
            }

            if (ActiveItem is not null && RobotProgress >= 1)
            {
                var item = ActiveItem;
                Bins[(int)item.Route]++;
                Sorted++;
                _items.Remove(item);
                ActiveItem = null;
                AddEvent("工业机器人", $"工件 #{item.Id:D3} → 料仓 {(char)('A' + (int)item.Route)}");
            }

            var waiting = _items.FirstOrDefault(item => item.Stage == TwinItemStage.Conveyor
                && item.Inspected && item.Route != TwinProductKind.Reject && item.X >= PickupX);
            if (waiting is not null && Bins[(int)waiting.Route] >= BinCapacity)
            {
                Alarm = $"料仓 {(char)('A' + (int)waiting.Route)} 已满 · 请清空料仓";
                return;
            }

            if (waiting is not null && ActiveItem is null)
            {
                waiting.Stage = TwinItemStage.Held;
                ActiveItem = waiting;
                _robotStart = Tick;
                _activeCycle = RobotCycle;
            }

            BeltTravel += BeltSpeed * StepSeconds;
            double leading = double.PositiveInfinity;
            foreach (var item in _items.Where(item => item.Stage == TwinItemStage.Conveyor)
                         .OrderByDescending(item => item.X).ToArray())
            {
                var limit = item.Inspected && item.Route != TwinProductKind.Reject
                    ? PickupX : 8.3;
                item.X = Math.Min(item.X + BeltSpeed * StepSeconds,
                    Math.Min(limit, leading - 0.85));
                leading = item.X;
                if (!item.Inspected && item.X >= CameraX)
                {
                    LastInspection = _inspect(item.Shape, item.Id, PoorLighting);
                    item.Inspected = true;
                    item.Route = LastInspection.Route;
                    item.Name = LastInspection.Name;
                    Inspected++;
                    AddEvent("工业相机", $"#{item.Id:D3} · {LastInspection.Description}");
                }

                if (item.Route == TwinProductKind.Reject && item.X >= 8.3)
                {
                    _items.Remove(item);
                    Rejected++;
                    AddEvent("复检工位", $"工件 #{item.Id:D3} 已进入复检区");
                }
            }

            if (LiveMode && _pendingItems.Count > 0
                && !_items.Any(item => item.Stage == TwinItemStage.Conveyor && item.X < -7.1))
            {
                _items.Add(_pendingItems.Dequeue());
            }

            if (!LiveMode && Time < 86 && Time >= _nextFeed
                && !_items.Any(item => item.Stage == TwinItemStage.Conveyor && item.X < -7.1))
            {
                Received++;
                var shape = Received % 9 == 0 ? TwinProductKind.Reject
                    : (TwinProductKind)((Received - 1) % 3);
                _items.Add(new TwinItem { Id = Received, Shape = shape });
                _nextFeed = Time + FeedInterval;
            }
        }

        /// <summary>复位运行状态但保留回放日志。</summary>
        private void ResetState()
        {
            Tick = 0;
            Received = 0;
            Inspected = 0;
            Sorted = 0;
            Rejected = 0;
            DroppedExternal = 0;
            BeltSpeed = 1.4;
            BeltTravel = 0;
            FeedInterval = 2.8;
            RobotCycle = 2.2;
            BinCapacity = 12;
            PoorLighting = false;
            EmergencyStopped = false;
            ActiveItem = null;
            LastInspection = null;
            Alarm = string.Empty;
            _nextFeed = 1;
            _items.Clear();
            _pendingItems.Clear();
            _events.Clear();
            Array.Clear(Bins);
            AddEvent("产线", LiveMode ? "平台联动已就绪，等待识别采样。" : "演示班次已就绪。");
        }

        /// <summary>在固定步边界应用命令，急停不会阻止恢复指令。</summary>
        private void Apply(TwinIntervention entry)
        {
            switch (entry.Control)
            {
                case TwinControl.BeltSpeed:
                    BeltSpeed = entry.Value;
                    break;
                case TwinControl.FeedInterval:
                    FeedInterval = entry.Value;
                    break;
                case TwinControl.RobotCycle:
                    RobotCycle = entry.Value;
                    break;
                case TwinControl.BinCapacity:
                    BinCapacity = (int)entry.Value;
                    break;
                case TwinControl.PoorLighting:
                    PoorLighting = entry.Value == 1;
                    break;
                case TwinControl.EmergencyStop:
                    EmergencyStopped = entry.Value == 1;
                    break;
                case TwinControl.UnloadBins:
                    Array.Clear(Bins);
                    break;
            }

            AddEvent("参数干预", $"{entry.Control} = {entry.Value:0.##}");
        }

        /// <summary>界面事件保留最近 100 条，避免长期联动占用无限内存。</summary>
        private void AddEvent(string device, string description)
        {
            _events.Add(new TwinEvent(Time, device, description));
            if (_events.Count > 100)
            {
                _events.RemoveAt(0);
            }
        }
    }
}
