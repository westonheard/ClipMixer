using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ClipMixer{
    //one audio track row in the waveform view
    public sealed class WaveLane{
        public string Label { get; init; } = "";
        //peak 0..1 per bucket, null while its still loading
        public float[]? Peaks { get; set; }
        public double Gain { get; set; } = 1;
        public bool Dimmed { get; set; }//track removed or muted by solo, draw it faded
        public string? Note { get; set; }
    }

    //stacked waveforms under the timeline, same side padding so they line up w/ it
    public sealed class WaveformView : FrameworkElement{
        public const double LaneHeight = 38;
        public const double LaneGap = 4;
        private const double FloorDb = -54;//-54 so quiet mic speech still shows up, not just loud game audio

        private IReadOnlyList<WaveLane> lanes = Array.Empty<WaveLane>();
        private readonly Dictionary<WaveLane, (double W, double Dur, double Gain, float[]? Peaks, Geometry? G)> cache = new();

        public double Pad { get; set; } = 14;
        public int BucketsPerSecond { get; set; } = 100;
        public double Duration { get; private set; }
        public double InSec { get; private set; }
        public double OutSec { get; private set; }
        public double PlayheadSec { get; private set; }
        public double? KeyframeSec { get; private set; }
        public string? Message { get; private set; }
        //todo: right click to set in/out from here too

        public event Action<double>? SeekRequested;

        private static readonly Brush laneBg = freeze(new SolidColorBrush(Color.FromRgb(0x1F, 0x20, 0x25)));
        private static readonly Brush waveBrush = freeze(new SolidColorBrush(Color.FromRgb(0x4A, 0x9E, 0xFF)));
        private static readonly Brush outsideShade = freeze(new SolidColorBrush(Color.FromArgb(0x99, 0x10, 0x11, 0x14)));
        private static readonly Brush labelBrush = freeze(new SolidColorBrush(Color.FromRgb(0xC8, 0xC9, 0xD0)));
        private static readonly Brush labelBg = freeze(new SolidColorBrush(Color.FromArgb(0xAA, 0x1F, 0x20, 0x25)));
        private static readonly Brush dimLabelBrush = freeze(new SolidColorBrush(Color.FromRgb(0x7A, 0x7B, 0x84)));
        private static readonly Pen playheadPen = freezePen(new Pen(Brushes.White, 1.5));
        private static readonly Pen inPen = freezePen(new Pen(new SolidColorBrush(Color.FromRgb(0x7C, 0xD9, 0x92)), 1.5));
        private static readonly Pen outPen = freezePen(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x7C, 0x7C)), 1.5));
        private static readonly Pen keyframePen = freezePen(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x47)), 1) { DashStyle = DashStyles.Dash });

        private static T freeze<T>(T f) where T : Freezable{f.Freeze(); return f;}
        private static Pen freezePen(Pen p){p.Freeze(); return p;}

        public WaveformView(){
            Cursor = Cursors.Hand;
            ToolTip = "Audio waveforms - click or drag to jump there";
        }

        public void setLanes(IReadOnlyList<WaveLane> lanes, string? message = null){
            this.lanes = lanes;
            cache.Clear();
            Message = message;
            InvalidateMeasure();
            InvalidateVisual();
        }

        public void setMessage(string? message){Message = message; InvalidateVisual();}

        public void setTimes(double duration, double inSec, double outSec, double? keyframeSec){
            Duration = duration; InSec = inSec; OutSec = outSec; KeyframeSec = keyframeSec;
            InvalidateVisual();
        }

        public void setPlayhead(double sec){
            if(Math.Abs(sec - PlayheadSec) < 1e-4){return;}
            PlayheadSec = sec;
            InvalidateVisual();
        }

        public void refresh() => InvalidateVisual();

        protected override Size MeasureOverride(Size available){
            double h = lanes.Count == 0 ? 0 : lanes.Count * LaneHeight + (lanes.Count - 1) * LaneGap;
            return new Size(double.IsInfinity(available.Width) ? 0 : available.Width, h);
        }

        private double Left => Pad;
        private double LaneWidth => Math.Max(1, ActualWidth - 2 * Pad);
        private double x(double sec) => Left + (Duration > 0 ? Math.Clamp(sec, 0, Duration) / Duration : 0) * LaneWidth;
        private double t(double x) => Duration > 0 ? Math.Clamp((x - Left) / LaneWidth, 0, 1) * Duration : 0;

        protected override void OnRender(DrawingContext dc){
            if(lanes.Count == 0){return;}
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            double total = ActualHeight;

            for(int i = 0; i < lanes.Count; i++){
                WaveLane lane = lanes[i];
                double top = i * (LaneHeight + LaneGap);
                Rect rect = new Rect(Left, top, LaneWidth, LaneHeight);
                dc.DrawRoundedRectangle(laneBg, null, rect, 3, 3);

                if(lane.Peaks != null && Duration > 0){
                    Geometry? g = getGeometry(lane, top);
                    if(g != null){
                        if(lane.Dimmed){dc.PushOpacity(0.3);}
                        dc.DrawGeometry(waveBrush, null, g);
                        if(lane.Dimmed){dc.Pop();}
                    }
                }

                string label = lane.Label + (lane.Note != null ? "  -  " + lane.Note : "");
                FormattedText ft = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 11, lane.Dimmed ? dimLabelBrush : labelBrush, dpi);
                ft.MaxTextWidth = Math.Max(10, LaneWidth - 8);
                ft.MaxLineCount = 1;
                ft.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawRectangle(labelBg, null,
                    new Rect(Left + 3, top + 2, Math.Min(ft.Width + 6, LaneWidth - 6), ft.Height));
                dc.DrawText(ft, new Point(Left + 6, top + 2));
            }

            if(Duration > 0){
                double xi = x(InSec), xo = x(OutSec);
                if(xi > Left){dc.DrawRectangle(outsideShade, null, new Rect(Left, 0, xi - Left, total));}
                if(xo < Left + LaneWidth){dc.DrawRectangle(outsideShade, null, new Rect(xo, 0, Left + LaneWidth - xo, total));}
                dc.DrawLine(inPen, new Point(xi, 0), new Point(xi, total));
                dc.DrawLine(outPen, new Point(xo, 0), new Point(xo, total));
                if(KeyframeSec is double k){dc.DrawLine(keyframePen, new Point(x(k), 0), new Point(x(k), total));}
                double xp = x(PlayheadSec);
                dc.DrawLine(playheadPen, new Point(xp, 0), new Point(xp, total));
            }

            if(!string.IsNullOrEmpty(Message)){
                FormattedText ft = new FormattedText(Message, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 11, dimLabelBrush, dpi);
                dc.DrawText(ft, new Point(Left + LaneWidth - ft.Width - 6, 2));
            }
        }

        //cached, only rebuilds when width/duration/gain/peaks change
        private Geometry? getGeometry(WaveLane lane, double top){
            double w = LaneWidth;
            if(cache.TryGetValue(lane, out (double W, double Dur, double Gain, float[]? Peaks, Geometry? G) c) && c.W == w && c.Dur == Duration && c.Gain == lane.Gain && ReferenceEquals(c.Peaks, lane.Peaks)){
                return c.G;
            }

            float[] peaks = lane.Peaks!;
            int cols = Math.Max(1, (int)Math.Round(w));
            double half = (LaneHeight - 4) / 2, mid = top + LaneHeight / 2;
            double gainDb = lane.Gain > 0 ? 20 * Math.Log10(lane.Gain) : double.NegativeInfinity;
            double[] heights = new double[cols];
            for(int x = 0; x < cols; x++){//each pixel column takes the loudest peak in its time range
                double t0 = (double)x / cols * Duration, t1 = (double)(x + 1) / cols * Duration;
                int b0 = Math.Clamp((int)(t0 * BucketsPerSecond), 0, peaks.Length - 1);
                int b1 = Math.Clamp((int)Math.Ceiling(t1 * BucketsPerSecond), b0 + 1, peaks.Length);
                float p = 0;
                for(int b = b0; b < b1; b++){if(peaks[b] > p){p = peaks[b];}}
                //double h = p * lane.Gain * half;
                double db = p > 0 ? 20 * Math.Log10(p) + gainDb : double.NegativeInfinity;
                heights[x] = double.IsNegativeInfinity(db) ? 0 : Math.Clamp((db - FloorDb) / -FloorDb, 0, 1) * half;
            }

            StreamGeometry geo = new StreamGeometry();
            using(StreamGeometryContext ctx = geo.Open()){
                double step = w / cols;
                ctx.BeginFigure(new Point(Left, mid - Math.Max(0.5, heights[0])), true, true);
                List<Point> pts = new List<Point>(cols * 2);
                for(int x = 0; x < cols; x++){pts.Add(new Point(Left + x * step, mid - Math.Max(0.5, heights[x])));}
                for(int x = cols - 1; x >= 0; x--){pts.Add(new Point(Left + x * step, mid + Math.Max(0.5, heights[x])));}
                ctx.PolyLineTo(pts, false, false);
            }
            geo.Freeze();
            cache[lane] = (w, Duration, lane.Gain, lane.Peaks, geo);
            return geo;
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo info){
            base.OnRenderSizeChanged(info);
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e){
            base.OnMouseLeftButtonDown(e);
            if(Duration <= 0){return;}
            CaptureMouse();
            SeekRequested?.Invoke(t(e.GetPosition(this).X));
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e){
            base.OnMouseMove(e);
            if(IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed){
                SeekRequested?.Invoke(t(e.GetPosition(this).X));
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e){
            base.OnMouseLeftButtonUp(e);
            if(IsMouseCaptured){ReleaseMouseCapture();}
        }
    }
}
