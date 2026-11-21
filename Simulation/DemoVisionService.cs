using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RobotVisionSortingTwin
{
    /// <summary>纯 C# 合成帧视觉：阈值分割、连通域、面积及像素边界周长、唯一模板匹配。</summary>
    public sealed class DemoVisionService
    {
        private const int Width = 256;
        private const int Height = 192;
        private readonly List<(string Name, double Area, double Perimeter)> _profiles = new();

        /// <summary>从像素测量得到判定，不使用工件类别作为识别结果。</summary>
        public TwinInspection Inspect(TwinProductKind shape, int sequence, bool poorLighting)
        {
            EnsureProfiles();
            var pixels = CreateFrame(shape, sequence, poorLighting);
            var measurement = Measure(pixels);
            var candidates = new List<int>();
            if (measurement.Count == 1)
            {
                for (var i = 0; i < _profiles.Count; i++)
                {
                    var profile = _profiles[i];
                    var areaError = Math.Abs(measurement.Area - profile.Area) / profile.Area;
                    var perimeterError = Math.Abs(measurement.Perimeter - profile.Perimeter)
                        / profile.Perimeter;
                    if (areaError <= 0.05 && perimeterError <= 0.05)
                    {
                        candidates.Add(i);
                    }
                }
            }

            var recognized = candidates.Count == 1;
            var route = recognized ? (TwinProductKind)candidates[0] : TwinProductKind.Reject;
            var name = recognized ? _profiles[candidates[0]].Name : "待复检工件";
            var description = recognized ? $"唯一匹配：{name}"
                : measurement.Count != 1 ? "无有效单目标 · 送复检"
                : "特征不匹配 · 送复检";
            var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Gray8,
                null, pixels, Width);
            bitmap.Freeze();
            return new TwinInspection(route, name, recognized,
                measurement.Count == 1 ? measurement.Area : null,
                measurement.Count == 1 ? measurement.Perimeter : null,
                bitmap, description);
        }

        /// <summary>使用与运行时相同的测量方法生成三个参考模板。</summary>
        private void EnsureProfiles()
        {
            if (_profiles.Count != 0)
            {
                return;
            }

            string[] names = ["圆形轴套", "矩形壳体", "方形盖板"];
            for (var i = 0; i < names.Length; i++)
            {
                var measurement = Measure(CreateFrame((TwinProductKind)i, 0, false));
                _profiles.Add((names[i], measurement.Area, measurement.Perimeter));
            }
        }

        /// <summary>四邻域连通域测量；周长单位为像素边，不等同于平台的 OpenCV 弧长。</summary>
        internal static (int Count, double Area, double Perimeter) Measure(byte[] pixels)
        {
            var visited = new bool[pixels.Length];
            var queue = new int[pixels.Length];
            var count = 0;
            var resultArea = 0;
            var resultPerimeter = 0;
            for (var seed = 0; seed < pixels.Length; seed++)
            {
                if (pixels[seed] < 120 || visited[seed])
                {
                    continue;
                }

                var head = 0;
                var tail = 1;
                var area = 0;
                var perimeter = 0;
                queue[0] = seed;
                visited[seed] = true;
                while (head < tail)
                {
                    var current = queue[head++];
                    area++;
                    var x = current % Width;
                    var y = current / Width;
                    for (var side = 0; side < 4; side++)
                    {
                        var nx = x + (side == 0 ? -1 : side == 1 ? 1 : 0);
                        var ny = y + (side == 2 ? -1 : side == 3 ? 1 : 0);
                        if (nx < 0 || nx >= Width || ny < 0 || ny >= Height
                            || pixels[ny * Width + nx] < 120)
                        {
                            perimeter++;
                            continue;
                        }

                        var next = ny * Width + nx;
                        if (!visited[next])
                        {
                            visited[next] = true;
                            queue[tail++] = next;
                        }
                    }
                }

                if (area >= 100)
                {
                    count++;
                    resultArea = area;
                    resultPerimeter = perimeter;
                }
            }

            return (count, resultArea, resultPerimeter);
        }

        /// <summary>欠曝光时目标灰度降至阈值以下，演示照明对识别的影响。</summary>
        private static byte[] CreateFrame(TwinProductKind shape, int sequence, bool poorLighting)
        {
            var pixels = new byte[Width * Height];
            Array.Fill(pixels, (byte)24);
            var offset = sequence % 7 - 3;
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    var dx = x - Width / 2 - offset;
                    var dy = y - Height / 2;
                    var inside = shape switch
                    {
                        TwinProductKind.Disc => dx * dx + dy * dy <= 34 * 34,
                        TwinProductKind.Block => Math.Abs(dx) <= 45 && Math.Abs(dy) <= 26,
                        TwinProductKind.Plate => Math.Abs(dx) <= 25 && Math.Abs(dy) <= 25,
                        _ => dy >= -30 && dy <= 30 && Math.Abs(dx) <= (dy + 30) / 2
                    };
                    if (inside)
                    {
                        pixels[y * Width + x] = poorLighting ? (byte)65 : (byte)220;
                    }
                }
            }

            return pixels;
        }
    }
}
