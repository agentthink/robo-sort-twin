using System.Windows.Media.Imaging;

namespace RobotVisionSortingTwin
{
    public enum TwinProductKind
    {
        Disc,
        Block,
        Plate,
        Reject
    }

    public enum TwinItemStage
    {
        Conveyor,
        Held
    }

    /// <summary>一次图像测量的只读结果；位图已冻结，不持有原生图像资源。</summary>
    public sealed record TwinInspection(
        TwinProductKind Route,
        string Name,
        bool Recognized,
        double? Area,
        double? Perimeter,
        BitmapSource? Image,
        string Description);

    public sealed class TwinItem
    {
        public int Id { get; init; }
        public TwinProductKind Shape { get; init; }
        public TwinProductKind Route { get; set; } = TwinProductKind.Reject;
        public double X { get; set; } = -8;
        public bool Inspected { get; set; }
        public TwinItemStage Stage { get; set; }
        public string Name { get; set; } = "待识别工件";
    }

    public enum TwinControl
    {
        BeltSpeed,
        FeedInterval,
        RobotCycle,
        BinCapacity,
        PoorLighting,
        EmergencyStop,
        UnloadBins
    }

    public sealed record TwinIntervention(int Tick, TwinControl Control, double Value);
    public sealed record TwinEvent(double Time, string Device, string Description);

}
