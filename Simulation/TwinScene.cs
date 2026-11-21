using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace RobotVisionSortingTwin
{
    /// <summary>完全由 C# 几何构建的 WPF 三维产线；相机、工件和机械臂共用米制坐标。</summary>
    public sealed class TwinScene : UserControl
    {
        private readonly Viewport3D _viewport = new();
        private readonly PerspectiveCamera _camera = new() { FieldOfView = 43 };
        private readonly Model3DGroup _moving = new();
        private readonly Model3DGroup _highlight = new();
        private readonly Dictionary<Model3D, string> _devices = new();
        private static readonly Dictionary<string, Material> Materials = new();
        private static readonly MeshGeometry3D Cube = CreateCube();
        private static readonly MeshGeometry3D Round = CreateCylinder();
        private double _yaw = 39;
        private double _pitch = 37;
        private double _distance = 42;
        private Point _dragOrigin;
        private Point _lastMouse;
        private Point3D _target = new(0, 0, -1.2);
        private string _selected = "ROBOT-01";

        public event EventHandler<string>? DeviceSelected;

        public static Point3D RobotBase => new(1, 1.05, -2.7);
        public static Point3D Pickup => new(1, 1.35, 0);

        /// <summary>创建静态厂房、灯光与运动层；静态网格在所有帧间复用。</summary>
        public TwinScene()
        {
            ClipToBounds = true;
            Background = new SolidColorBrush(Color.FromRgb(14, 24, 39));
            Content = _viewport;
            _viewport.Camera = _camera;
            var world = new Model3DGroup();
            world.Children.Add(new AmbientLight(Color.FromRgb(112, 126, 145)));
            world.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-2, -5, -3)));
            world.Children.Add(new DirectionalLight(
                Color.FromRgb(116, 167, 203), new Vector3D(3, -2, 2)));
            BuildFactory(world);
            world.Children.Add(_moving);
            world.Children.Add(_highlight);
            _viewport.Children.Add(new ModelVisual3D { Content = world });
            MouseLeftButtonDown += BeginOrbit;
            MouseLeftButtonUp += EndOrbit;
            MouseMove += Orbit;
            MouseWheel += Zoom;
            UpdateCamera();
        }

        /// <summary>返回料仓中心；三维动画和机械臂逆解共享同一目标点。</summary>
        public static Point3D BinPosition(int index)
        {
            return index switch
            {
                0 => new Point3D(-2.1, 1.05, -4),
                1 => new Point3D(1, 1.05, -5.5),
                _ => new Point3D(4.1, 1.05, -4)
            };
        }

        /// <summary>按状态重建少量运动实例；所有实例复用冻结网格与材质。</summary>
        public void Render(SortingSimulation simulation)
        {
            _moving.Children.Clear();
            var tool = ToolPosition(simulation);
            DrawRobot(_moving, tool);
            foreach (var item in simulation.Items)
            {
                var position = new Point3D(item.X, 1.3, 0);
                if (item.Stage == TwinItemStage.Held)
                {
                    position = simulation.RobotProgress < 0.12 ? Pickup
                        : simulation.RobotProgress < 0.67 ? tool : BinPosition((int)item.Route);
                }

                DrawProduct(_moving, item.Shape, position);
            }

            for (var i = 0; i < 3; i++)
            {
                var bin = BinPosition(i);
                for (var n = 0; n < Math.Min(simulation.Bins[i], 24); n++)
                {
                    var position = new Point3D(
                        bin.X + (n % 3 - 1) * 0.4,
                        0.4 + n / 6 * 0.19,
                        bin.Z + (n / 3 % 2 - 0.5) * 0.5);
                    DrawProduct(_moving, (TwinProductKind)i, position, 0.6);
                }
            }

            for (var i = 0; i < 22; i++)
            {
                var x = -8.4 + (i * 0.77 + simulation.BeltTravel) % 16.8;
                Box(_moving, new Point3D(x, 1.153, 0), 0.025, 0.008, 1.18, "#44515E");
            }

            var lamp = simulation.EmergencyStopped ? "#F25565"
                : simulation.Alarm.Length > 0 ? "#FFC65C" : "#52E2AB";
            Cylinder(_moving, new Point3D(2, 2.28, -2), 0.08, 0.18, lamp);
            _highlight.Children.Clear();
            var anchor = DeviceAnchor(_selected);
            Cylinder(_highlight, new Point3D(anchor.X, 0.035, anchor.Z), 0.82, 0.015, "#37A4BF");
        }

        /// <summary>同步列表选择与场景选择；聚焦可由用户单独触发。</summary>
        public void SelectDevice(string id)
        {
            _selected = id;
        }

        /// <summary>整体、俯视及设备近景机位。</summary>
        public void SetView(string view)
        {
            _target = view == "focus" ? DeviceAnchor(_selected) : new Point3D(0, 0, -1.2);
            _distance = view == "focus" ? 12 : 42;
            _pitch = view == "top" ? 83 : 37;
            _yaw = view == "top" ? 0 : 39;
            UpdateCamera();
        }

        /// <summary>按节拍分段插值抓取、抬升、转移、放置与回程，急停时进度保持不变。</summary>
        public static Point3D ToolPosition(SortingSimulation simulation)
        {
            if (simulation.ActiveItem is null)
            {
                return new Point3D(1, 2.6, -0.6);
            }

            var destination = BinPosition((int)simulation.ActiveItem.Route);
            Point3D[] waypoints =
            [
                new Point3D(1, 2.6, -0.6),
                Pickup,
                new Point3D(Pickup.X, 2.9, Pickup.Z),
                new Point3D(destination.X, 2.9, destination.Z),
                destination,
                new Point3D(destination.X, 2.9, destination.Z),
                new Point3D(1, 2.6, -0.6)
            ];
            double[] times = [0, 0.12, 0.27, 0.52, 0.67, 0.82, 1];
            var progress = simulation.RobotProgress;
            for (var i = 0; i < times.Length - 1; i++)
            {
                if (progress <= times[i + 1])
                {
                    var t = (progress - times[i]) / (times[i + 1] - times[i]);
                    t = t * t * (3 - 2 * t);
                    return waypoints[i] + (waypoints[i + 1] - waypoints[i]) * t;
                }
            }

            return waypoints[^1];
        }

        /// <summary>两段臂长逆解；腕部和吸盘保持垂直，是展示运动学而非厂商控制程序。</summary>
        public static Point3D ElbowPosition(Point3D tool)
        {
            var wrist = tool + new Vector3D(0, 0.38, 0);
            var delta = wrist - RobotBase;
            var horizontal = Math.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
            var distance = Math.Sqrt(horizontal * horizontal + delta.Y * delta.Y);
            const double length = 2.3;
            var angle = Math.Atan2(delta.Y, horizontal)
                + Math.Acos(Math.Clamp(distance / (2 * length), -1, 1));
            var radius = length * Math.Cos(angle);
            var factor = horizontal > 0.0001 ? radius / horizontal : 0;
            return RobotBase + new Vector3D(
                delta.X * factor, length * Math.Sin(angle), delta.Z * factor);
        }

        /// <summary>设备位置用于选中高亮与近景机位。</summary>
        private static Point3D DeviceAnchor(string id)
        {
            return id switch
            {
                "FEED-01" => new Point3D(-7, 1, 0),
                "CAM-01" => new Point3D(-3.2, 1.7, 0),
                "BELT-01" => new Point3D(-0.6, 1, 0),
                "BIN-A" => BinPosition(0),
                "BIN-B" => BinPosition(1),
                "BIN-C" => BinPosition(2),
                "REJECT-01" => new Point3D(8.7, 0.5, 0),
                _ => RobotBase
            };
        }

        /// <summary>建立程序化厂房、输送机、工业相机、料仓与安全围栏。</summary>
        private void BuildFactory(Model3DGroup world)
        {
            var floor = new Model3DGroup();
            Box(floor, new Point3D(0, -0.32, -1), 22, 0.6, 15, "#202C3D");
            Box(floor, new Point3D(0, -0.005, -1), 21.6, 0.04, 14.6, "#465564");
            for (var x = -10; x <= 10; x++)
            {
                Box(floor, new Point3D(x, 0.023, -1), 0.013, 0.007, 14, "#566574");
            }

            for (var z = -8; z <= 6; z++)
            {
                Box(floor, new Point3D(0, 0.023, z), 21, 0.007, 0.013, "#566574");
            }

            Box(floor, new Point3D(0, 0.03, 3.5), 20, 0.015, 1.8, "#354F60");
            for (var x = -10; x <= 10; x += 2)
            {
                Box(floor, new Point3D(x, 0.045, 2.55), 1.1, 0.02, 0.06, "#EAB74A");
                Box(floor, new Point3D(x, 0.045, 4.45), 1.1, 0.02, 0.06, "#EAB74A");
            }

            for (var x = -10; x <= 10; x += 5)
            {
                Box(floor, new Point3D(x, 1.6, -7.9), 0.18, 3.2, 0.18, "#738899");
            }

            Box(floor, new Point3D(0, 1.05, -7.95), 20, 2.1, 0.12, "#60717F");
            Box(floor, new Point3D(0, 3.17, -7.95), 20.3, 0.18, 0.25, "#A3ADB3");
            Label(floor, "VISION SORTING CELL / 01", new Point3D(-7, 2.6, -7.8), 4.4);
            Label(floor, "ROBOT WORKSPACE", new Point3D(0.6, 0.5, -7.78), 4.2);
            world.Children.Add(floor);

            var conveyor = new Model3DGroup();
            Box(conveyor, new Point3D(-0.25, 1, 0), 17, 0.28, 1.5, "#8796A2");
            Box(conveyor, new Point3D(-0.25, 1.145, 0), 16.85, 0.025, 1.22, "#202A34");
            for (var x = -8; x <= 8; x += 2)
            {
                Box(conveyor, new Point3D(x, 0.47, -0.6), 0.12, 0.94, 0.12, "#9BA8B1");
                Box(conveyor, new Point3D(x, 0.47, 0.6), 0.12, 0.94, 0.12, "#9BA8B1");
                Box(conveyor, new Point3D(x, 0.08, 0), 0.45, 0.12, 1.5, "#667887");
            }

            Box(conveyor, new Point3D(-0.25, 1.26, -0.8), 17.15, 0.08, 0.065, "#C4D1D9");
            Box(conveyor, new Point3D(-0.25, 1.26, 0.8), 17.15, 0.08, 0.065, "#C4D1D9");
            Box(conveyor, new Point3D(-6.5, 0.86, 1), 0.65, 0.5, 0.55, "#337887");
            Label(conveyor, "BELT-01  >  >  >", new Point3D(-1.1, 0.85, 0.79), 2.1);
            Register(world, conveyor, "BELT-01");

            var feeder = new Model3DGroup();
            Box(feeder, new Point3D(-8.6, 0.75, -1.5), 1.7, 1.5, 1.4, "#337887");
            Box(feeder, new Point3D(-8.6, 1.55, -1.5), 1.9, 0.12, 1.6, "#C5D4DE");
            for (var i = 0; i < 7; i++)
            {
                DrawProduct(feeder, (TwinProductKind)(i % 3),
                    new Point3D(-9.15 + i % 3 * 0.48, 1.65, -1.8 + i / 3 * 0.4));
            }

            Label(feeder, "FEED-01", new Point3D(-8.6, 0.9, -0.78), 1.4);
            Register(world, feeder, "FEED-01");

            var camera = new Model3DGroup();
            for (var side = -1; side <= 1; side += 2)
            {
                Box(camera, new Point3D(-3.2, 1.5, side * 1.15), 0.12, 3, 0.12, "#A7B9C5");
                Box(camera, new Point3D(-3.2, 0.08, side * 1.15), 0.6, 0.12, 0.6, "#607889");
            }

            Box(camera, new Point3D(-3.2, 2.96, 0), 0.16, 0.15, 2.45, "#D1DCE2");
            Box(camera, new Point3D(-3.2, 2.72, 0), 0.47, 0.38, 0.5, "#2D4558");
            Cylinder(camera, new Point3D(-3.2, 2.38, 0), 0.16, 0.22, "#1A2531");
            Cylinder(camera, new Point3D(-3.2, 2.37, 0), 0.11, 0.025, "#4BD6F0");
            Box(camera, new Point3D(-3.55, 2.53, 0), 0.1, 0.08, 1.4, "#E0F5FF");
            Box(camera, new Point3D(-2.85, 2.53, 0), 0.1, 0.08, 1.4, "#E0F5FF");
            Box(camera, new Point3D(-3.2, 1.169, 0), 0.025, 0.01, 1.2, "#3AE3CF");
            Label(camera, "CAM-01", new Point3D(-3.2, 3.22, 0.2), 1.5);
            Register(world, camera, "CAM-01");

            var robotBase = new Model3DGroup();
            Box(robotBase, new Point3D(1, 0.25, -2.7), 1.45, 0.5, 1.45, "#A7B1B9");
            Cylinder(robotBase, new Point3D(1, 0.5, -2.7), 0.47, 0.4, "#E68036");
            Cylinder(robotBase, new Point3D(1, 0.9, -2.7), 0.35, 0.25, "#243243");
            Box(robotBase, new Point3D(5.8, 0.88, -2.7), 0.9, 1.76, 0.8, "#D1D9DF");
            Box(robotBase, new Point3D(5.8, 1.27, -2.25), 0.6, 0.36, 0.07, "#294F68");
            Cylinder(robotBase, new Point3D(2, 1.6, -2), 0.035, 0.65, "#718191");
            Label(robotBase, "ROBOT-01", new Point3D(1, 0.45, -1.95), 1.35);
            Register(world, robotBase, "ROBOT-01");

            string[] colors = ["#4DA9E8", "#52C8AC", "#B997EE"];
            for (var i = 0; i < 3; i++)
            {
                var group = new Model3DGroup();
                var p = BinPosition(i);
                Box(group, new Point3D(p.X, 0.15, p.Z), 1.9, 0.22, 1.65, "#243648");
                Box(group, new Point3D(p.X, 0.65, p.Z - 0.76), 1.9, 0.9, 0.1, colors[i]);
                Box(group, new Point3D(p.X - 0.9, 0.65, p.Z), 0.1, 0.9, 1.6, colors[i]);
                Box(group, new Point3D(p.X + 0.9, 0.65, p.Z), 0.1, 0.9, 1.6, colors[i]);
                Box(group, new Point3D(p.X, 0.4, p.Z + 0.76), 1.9, 0.4, 0.1, colors[i]);
                Label(group, $"BIN-{(char)('A' + i)}", new Point3D(p.X, 0.56, p.Z + 0.83), 1.4);
                Register(world, group, $"BIN-{(char)('A' + i)}");
            }

            var reject = new Model3DGroup();
            Box(reject, new Point3D(8.9, 0.4, 0), 1.6, 0.8, 1.7, "#BD8152");
            Box(reject, new Point3D(8.9, 0.82, 0), 1.35, 0.05, 1.45, "#624B3E");
            Label(reject, "RECHECK", new Point3D(8.9, 0.52, 0.9), 1.4);
            Register(world, reject, "REJECT-01");

            var fence = new Model3DGroup();
            for (var i = 0; i <= 6; i++)
            {
                Box(fence, new Point3D(-4.7 + i * 1.9, 0.8, -6.8), 0.08, 1.6, 0.08, "#E6B544");
            }

            Box(fence, new Point3D(1, 1.55, -6.8), 11.4, 0.06, 0.06, "#E6B544");
            Box(fence, new Point3D(1, 0.35, -6.8), 11.4, 0.06, 0.06, "#E6B544");
            for (var i = 0; i < 48; i++)
            {
                Box(fence, new Point3D(-4.6 + i * 0.24, 0.93, -6.8),
                    0.015, 1.2, 0.015, "#8C9A9B");
            }

            world.Children.Add(fence);
        }

        /// <summary>将可点选网格绑定到设备标识。</summary>
        private void Register(Model3DGroup world, Model3DGroup group, string device)
        {
            foreach (var model in group.Children)
            {
                _devices[model] = device;
            }

            world.Children.Add(group);
        }

        /// <summary>以逆解关节点构建工业机械臂、关节与真空末端。</summary>
        private static void DrawRobot(Model3DGroup group, Point3D tool)
        {
            var elbow = ElbowPosition(tool);
            var wrist = tool + new Vector3D(0, 0.38, 0);
            Beam(group, RobotBase, elbow, 0.22, "#EF8C3D");
            Beam(group, elbow, wrist, 0.16, "#EF8C3D");
            Beam(group, RobotBase + new Vector3D(0.16, 0, 0),
                elbow + new Vector3D(0.16, 0, 0), 0.065, "#283746");
            Cylinder(group, elbow - new Vector3D(0, 0.16, 0), 0.28, 0.32, "#263748");
            Cylinder(group, wrist - new Vector3D(0, 0.11, 0), 0.21, 0.22, "#DCE4E8");
            Beam(group, wrist, tool, 0.075, "#BBCAD4");
            Cylinder(group, tool - new Vector3D(0, 0.05, 0), 0.19, 0.1, "#263748");
        }

        /// <summary>工件颜色与目标料仓保持一致；未知件使用琥珀色。</summary>
        private static void DrawProduct(
            Model3DGroup group, TwinProductKind kind, Point3D position, double scale = 1)
        {
            switch (kind)
            {
                case TwinProductKind.Disc:
                    Cylinder(group, position, 0.27 * scale, 0.19 * scale, "#59B7F2");
                    Cylinder(group, position + new Vector3D(0, 0.19 * scale, 0),
                        0.11 * scale, 0.01, "#26445B");
                    break;
                case TwinProductKind.Block:
                    Box(group, position + new Vector3D(0, 0.12 * scale, 0),
                        0.62 * scale, 0.24 * scale, 0.4 * scale, "#5FD5B6");
                    break;
                case TwinProductKind.Plate:
                    Box(group, position + new Vector3D(0, 0.06 * scale, 0),
                        0.45 * scale, 0.12 * scale, 0.45 * scale, "#B79AF0");
                    break;
                default:
                    Box(group, position + new Vector3D(0, 0.1 * scale, 0),
                        0.35 * scale, 0.2 * scale, 0.3 * scale, "#E9AC57");
                    break;
            }
        }

        /// <summary>单位网格实例化为指定尺寸的箱体。</summary>
        private static void Box(
            Model3DGroup group, Point3D center, double x, double y, double z, string color)
        {
            var transform = new Transform3DGroup();
            transform.Children.Add(new ScaleTransform3D(x, y, z));
            transform.Children.Add(new TranslateTransform3D(center.X, center.Y, center.Z));
            group.Children.Add(new GeometryModel3D(Cube, MaterialFor(color))
            {
                Transform = transform
            });
        }

        /// <summary>圆柱的基点在底面中心。</summary>
        private static void Cylinder(
            Model3DGroup group, Point3D bottom, double radius, double height, string color)
        {
            var transform = new Transform3DGroup();
            transform.Children.Add(new ScaleTransform3D(radius, height, radius));
            transform.Children.Add(new TranslateTransform3D(bottom.X, bottom.Y, bottom.Z));
            group.Children.Add(new GeometryModel3D(Round, MaterialFor(color))
            {
                Transform = transform
            });
        }

        /// <summary>将单位圆柱沿两点连线放置，用于机械臂连杆。</summary>
        private static void Beam(
            Model3DGroup group, Point3D from, Point3D to, double radius, string color)
        {
            var direction = to - from;
            var length = direction.Length;
            direction.Normalize();
            var axis = Vector3D.CrossProduct(new Vector3D(0, 1, 0), direction);
            var angle = Vector3D.AngleBetween(new Vector3D(0, 1, 0), direction);
            if (axis.Length < 0.0001)
            {
                axis = new Vector3D(1, 0, 0);
            }

            var transform = new Transform3DGroup();
            transform.Children.Add(new ScaleTransform3D(radius, length, radius));
            transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(axis, angle)));
            transform.Children.Add(new TranslateTransform3D(from.X, from.Y, from.Z));
            group.Children.Add(new GeometryModel3D(Round, MaterialFor(color))
            {
                Transform = transform
            });
        }

        /// <summary>共享已冻结材质，控制场景对象分配量。</summary>
        private static Material MaterialFor(string hex)
        {
            if (!Materials.TryGetValue(hex, out var material))
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                var group = new MaterialGroup();
                group.Children.Add(new DiffuseMaterial(new SolidColorBrush(color)));
                group.Children.Add(new SpecularMaterial(new SolidColorBrush(
                    Color.FromRgb(72, 80, 88)), 26));
                group.Freeze();
                material = group;
                Materials.Add(hex, material);
            }

            return material;
        }

        /// <summary>设备铭牌由系统字体绘制为本地位图，不需要图片或模型资源。</summary>
        private static void Label(Model3DGroup group, string text, Point3D center, double width)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(23, 40, 54)),
                    null, new Rect(0, 0, 400, 72), 6, 6);
                var formatted = new FormattedText(text, CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 30, Brushes.White, 1);
                dc.DrawText(formatted, new Point((400 - formatted.Width) / 2, 15));
            }

            var bitmap = new RenderTargetBitmap(400, 72, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            var h = width * 72 / 400;
            var mesh = new MeshGeometry3D
            {
                Positions = new Point3DCollection
                {
                    new(center.X - width / 2, center.Y - h / 2, center.Z),
                    new(center.X + width / 2, center.Y - h / 2, center.Z),
                    new(center.X + width / 2, center.Y + h / 2, center.Z),
                    new(center.X - width / 2, center.Y + h / 2, center.Z)
                },
                TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 },
                TextureCoordinates = new PointCollection
                {
                    new(0, 1), new(1, 1), new(1, 0), new(0, 0)
                }
            };
            var material = new EmissiveMaterial(new ImageBrush(bitmap));
            group.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
        }

        /// <summary>每面独立顶点，保持立方体的硬表面法线。</summary>
        private static MeshGeometry3D CreateCube()
        {
            var mesh = new MeshGeometry3D();
            Point3D[] p =
            [
                new(-0.5, -0.5, -0.5), new(0.5, -0.5, -0.5),
                new(0.5, 0.5, -0.5), new(-0.5, 0.5, -0.5),
                new(-0.5, -0.5, 0.5), new(0.5, -0.5, 0.5),
                new(0.5, 0.5, 0.5), new(-0.5, 0.5, 0.5)
            ];
            int[][] faces =
            [
                [0, 3, 2, 1], [4, 5, 6, 7], [0, 4, 7, 3],
                [1, 2, 6, 5], [3, 7, 6, 2], [0, 1, 5, 4]
            ];
            foreach (var face in faces)
            {
                var start = mesh.Positions.Count;
                foreach (var index in face)
                {
                    mesh.Positions.Add(p[index]);
                }

                foreach (var index in new[] { 0, 1, 2, 0, 2, 3 })
                {
                    mesh.TriangleIndices.Add(start + index);
                }
            }

            mesh.Freeze();
            return mesh;
        }

        /// <summary>圆柱侧面与端盖分离，避免端面被平滑成球面。</summary>
        private static MeshGeometry3D CreateCylinder()
        {
            var mesh = new MeshGeometry3D();
            const int segments = 24;
            for (var i = 0; i < segments; i++)
            {
                var a = i * Math.PI * 2 / segments;
                var b = (i + 1) * Math.PI * 2 / segments;
                var start = mesh.Positions.Count;
                mesh.Positions.Add(new Point3D(Math.Cos(a), 0, Math.Sin(a)));
                mesh.Positions.Add(new Point3D(Math.Cos(a), 1, Math.Sin(a)));
                mesh.Positions.Add(new Point3D(Math.Cos(b), 1, Math.Sin(b)));
                mesh.Positions.Add(new Point3D(Math.Cos(b), 0, Math.Sin(b)));
                foreach (var index in new[] { 0, 1, 2, 0, 2, 3 })
                {
                    mesh.TriangleIndices.Add(start + index);
                }

                start = mesh.Positions.Count;
                mesh.Positions.Add(new Point3D(0, 1, 0));
                mesh.Positions.Add(new Point3D(Math.Cos(b), 1, Math.Sin(b)));
                mesh.Positions.Add(new Point3D(Math.Cos(a), 1, Math.Sin(a)));
                mesh.Positions.Add(new Point3D(0, 0, 0));
                mesh.Positions.Add(new Point3D(Math.Cos(a), 0, Math.Sin(a)));
                mesh.Positions.Add(new Point3D(Math.Cos(b), 0, Math.Sin(b)));
                for (var n = 0; n < 6; n++)
                {
                    mesh.TriangleIndices.Add(start + n);
                }
            }

            mesh.Freeze();
            return mesh;
        }

        /// <summary>依据球面坐标更新自由相机。</summary>
        private void UpdateCamera()
        {
            var yaw = _yaw * Math.PI / 180;
            var pitch = _pitch * Math.PI / 180;
            var offset = new Vector3D(
                _distance * Math.Cos(pitch) * Math.Sin(yaw),
                _distance * Math.Sin(pitch),
                _distance * Math.Cos(pitch) * Math.Cos(yaw));
            _camera.Position = _target + offset;
            _camera.LookDirection = -offset;
            _camera.UpDirection = new Vector3D(0, 1, 0);
        }

        private void BeginOrbit(object sender, MouseButtonEventArgs e)
        {
            _dragOrigin = _lastMouse = e.GetPosition(this);
            CaptureMouse();
        }

        private void Orbit(object sender, MouseEventArgs e)
        {
            if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            var point = e.GetPosition(this);
            _yaw -= (point.X - _lastMouse.X) * 0.35;
            _pitch = Math.Clamp(_pitch + (point.Y - _lastMouse.Y) * 0.3, 12, 85);
            _lastMouse = point;
            UpdateCamera();
        }

        private void EndOrbit(object sender, MouseButtonEventArgs e)
        {
            ReleaseMouseCapture();
            var point = e.GetPosition(_viewport);
            if ((point - _dragOrigin).Length > 5)
            {
                return;
            }

            VisualTreeHelper.HitTest(_viewport, null, result =>
            {
                if (result is RayMeshGeometry3DHitTestResult hit)
                {
                    if (_devices.TryGetValue(hit.ModelHit, out var id))
                    {
                        _selected = id;
                        DeviceSelected?.Invoke(this, id);
                        return HitTestResultBehavior.Stop;
                    }

                    if (_moving.Children.Contains(hit.ModelHit))
                    {
                        DeviceSelected?.Invoke(this, "ROBOT-01");
                        _selected = "ROBOT-01";
                        return HitTestResultBehavior.Stop;
                    }
                }

                return HitTestResultBehavior.Continue;
            }, new PointHitTestParameters(point));
        }

        private void Zoom(object sender, MouseWheelEventArgs e)
        {
            _distance = Math.Clamp(_distance * (e.Delta > 0 ? 0.9 : 1.1), 7, 55);
            UpdateCamera();
            e.Handled = true;
        }
    }
}
