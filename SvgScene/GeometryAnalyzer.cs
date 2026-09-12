namespace SvgMusic.Scene;

public sealed record StrokeDiagnostic(
    string ShapeId,
    string Result,
    string Reason,
    string Metrics);

public interface IGeometryAnalyzer
{
    bool TryCreateStroke(
        GeometricShape shape,
        out Stroke stroke);

    IReadOnlyList<StrokeDiagnostic> Diagnostics { get; }

    void ClearDiagnostics();
}

/// <summary>
/// Detects straight line-like geometry independently of SVG representation.
///
/// The analyzer keeps the strict cases first:
/// 1. exact two-point open line;
/// 2. elongated filled closed contour;
/// 3. sampled open contour that is straight under PCA;
/// 4. tolerant fallback for a nearly straight open polyline.
///
/// The fallback lives in its own extractor so its rules can evolve without
/// making the main analysis method unreadable.
/// </summary>
public sealed class GeometryAnalyzer : IGeometryAnalyzer
{
    private readonly double _minElongation;
    private readonly double _minClosedFillRatio;
    private readonly OpenPolylineStrokeExtractor _openPolylineExtractor;
    private readonly List<StrokeDiagnostic> _diagnostics = [];

    public IReadOnlyList<StrokeDiagnostic> Diagnostics => _diagnostics;

    public GeometryAnalyzer(
        double minElongation = 5.0,
        double minClosedFillRatio = 0.55)
    {
        _minElongation = minElongation;
        _minClosedFillRatio = minClosedFillRatio;
        _openPolylineExtractor = new OpenPolylineStrokeExtractor();
    }

    public void ClearDiagnostics()
    {
        _diagnostics.Clear();
    }

    public bool TryCreateStroke(
        GeometricShape shape,
        out Stroke stroke)
    {
        stroke = default!;

        var contours = shape.EffectiveContours
            .Where(contour => contour.Points.Count > 0)
            .ToList();

        if (contours.Count != 1)
        {
            return Reject(
                shape,
                "multiple contours",
                $"kind={shape.SourceKind} " +
                $"contours={contours.Count} " +
                $"points={shape.Points.Count}");
        }

        var contour = contours[0];
        var points = contour.Points;

        if (points.Count < 2)
        {
            return Reject(
                shape,
                "too few points",
                $"kind={shape.SourceKind} " +
                $"closed={contour.IsClosed} " +
                $"points={points.Count}");
        }

        if (TryCreateExactOpenStroke(
            shape,
            contour,
            out stroke))
        {
            return true;
        }

        var axisAnalysis = AnalyzeAlongPrincipalAxis(points);

        if (axisAnalysis.Length <= 1e-9 ||
            axisAnalysis.EffectiveThickness(shape.StrokeWidth) <= 1e-9)
        {
            return Reject(
                shape,
                "degenerate geometry",
                Metrics(
                    shape,
                    contour,
                    axisAnalysis.Length,
                    axisAnalysis.EffectiveThickness(shape.StrokeWidth),
                    0,
                    null));
        }

        var effectiveThickness =
            axisAnalysis.EffectiveThickness(shape.StrokeWidth);

        var elongation =
            axisAnalysis.Length / effectiveThickness;

        if (contour.IsClosed)
        {
            return TryCreateClosedStroke(
                shape,
                contour,
                axisAnalysis,
                effectiveThickness,
                elongation,
                out stroke);
        }

        if (TryCreateStrictOpenStroke(
            shape,
            contour,
            axisAnalysis,
            effectiveThickness,
            elongation,
            out stroke))
        {
            return true;
        }

        var fallback = _openPolylineExtractor.TryExtract(shape);

        if (fallback.Accepted && fallback.Stroke is not null)
        {
            stroke = fallback.Stroke;

            Accept(
                shape,
                fallback.Reason,
                $"kind={shape.SourceKind} " + fallback.Metrics);

            return true;
        }

        return Reject(
            shape,
            fallback.Reason,
            $"kind={shape.SourceKind} " + fallback.Metrics);
    }

    private bool TryCreateExactOpenStroke(
        GeometricShape shape,
        GeometricContour contour,
        out Stroke stroke)
    {
        stroke = default!;

        if (contour.IsClosed || contour.Points.Count != 2)
        {
            return false;
        }

        var start = contour.Points[0];
        var end = contour.Points[1];
        var length = GeometryAlgorithms.Distance(start, end);

        if (length <= 1e-9)
        {
            Reject(
                shape,
                "zero length",
                Metrics(
                    shape,
                    contour,
                    length,
                    0,
                    0,
                    null));

            return false;
        }

        GeometryAlgorithms.OrderEndpoints(
            ref start,
            ref end);

        stroke = new Stroke(
            shape.Id,
            start,
            end,
            Math.Max(shape.StrokeWidth, 1.0),
            shape.SourceKind,
            shape.SourceIndex);

        Accept(
            shape,
            "two-point open contour",
            Metrics(
                shape,
                contour,
                length,
                shape.StrokeWidth,
                length / Math.Max(shape.StrokeWidth, 1e-9),
                null));

        return true;
    }

    private bool TryCreateClosedStroke(
        GeometricShape shape,
        GeometricContour contour,
        PrincipalAxisAnalysis analysis,
        double effectiveThickness,
        double elongation,
        out Stroke stroke)
    {
        stroke = default!;

        if (elongation < _minElongation)
        {
            return Reject(
                shape,
                "elongation too low",
                Metrics(
                    shape,
                    contour,
                    analysis.Length,
                    effectiveThickness,
                    elongation,
                    null));
        }

        var area = Math.Abs(
            GeometryAlgorithms.SignedArea(contour.Points));

        var orientedBoxArea =
            analysis.Length *
            Math.Max(analysis.ContourThickness, 1e-9);

        var fillRatio = area / orientedBoxArea;

        if (fillRatio < _minClosedFillRatio)
        {
            return Reject(
                shape,
                "closed contour fill too low",
                Metrics(
                    shape,
                    contour,
                    analysis.Length,
                    effectiveThickness,
                    elongation,
                    fillRatio));
        }

        stroke = CreateStrokeFromAxis(
            shape,
            analysis,
            effectiveThickness);

        Accept(
            shape,
            "elongated filled contour",
            Metrics(
                shape,
                contour,
                analysis.Length,
                effectiveThickness,
                elongation,
                fillRatio));

        return true;
    }

    private bool TryCreateStrictOpenStroke(
        GeometricShape shape,
        GeometricContour contour,
        PrincipalAxisAnalysis analysis,
        double effectiveThickness,
        double elongation,
        out Stroke stroke)
    {
        stroke = default!;

        if (elongation < _minElongation)
        {
            return false;
        }

        var straightnessTolerance = Math.Max(
            shape.StrokeWidth * 2.0,
            analysis.Length * 0.04);

        if (analysis.ContourThickness > straightnessTolerance)
        {
            return false;
        }

        stroke = CreateStrokeFromAxis(
            shape,
            analysis,
            effectiveThickness);

        Accept(
            shape,
            "straight open contour",
            Metrics(
                shape,
                contour,
                analysis.Length,
                effectiveThickness,
                elongation,
                null));

        return true;
    }

    private static Stroke CreateStrokeFromAxis(
        GeometricShape shape,
        PrincipalAxisAnalysis analysis,
        double effectiveThickness)
    {
        var acrossCenter =
            (analysis.MinAcross + analysis.MaxAcross) / 2.0;

        var start = new PointD(
            analysis.Centroid.X +
            analysis.AxisX * analysis.MinAlong +
            analysis.NormalX * acrossCenter,
            analysis.Centroid.Y +
            analysis.AxisY * analysis.MinAlong +
            analysis.NormalY * acrossCenter);

        var end = new PointD(
            analysis.Centroid.X +
            analysis.AxisX * analysis.MaxAlong +
            analysis.NormalX * acrossCenter,
            analysis.Centroid.Y +
            analysis.AxisY * analysis.MaxAlong +
            analysis.NormalY * acrossCenter);

        GeometryAlgorithms.OrderEndpoints(
            ref start,
            ref end);

        return new Stroke(
            shape.Id,
            start,
            end,
            effectiveThickness,
            shape.SourceKind,
            shape.SourceIndex);
    }

    private static PrincipalAxisAnalysis AnalyzeAlongPrincipalAxis(
        IReadOnlyList<PointD> points)
    {
        var centroid = new PointD(
            points.Average(point => point.X),
            points.Average(point => point.Y));

        double xx = 0;
        double xy = 0;
        double yy = 0;

        foreach (var point in points)
        {
            var dx = point.X - centroid.X;
            var dy = point.Y - centroid.Y;

            xx += dx * dx;
            xy += dx * dy;
            yy += dy * dy;
        }

        xx /= points.Count;
        xy /= points.Count;
        yy /= points.Count;

        var (axisX, axisY) =
            GeometryAlgorithms.PrincipalAxis(xx, xy, yy);

        var normalX = -axisY;
        var normalY = axisX;

        var minAlong = double.PositiveInfinity;
        var maxAlong = double.NegativeInfinity;
        var minAcross = double.PositiveInfinity;
        var maxAcross = double.NegativeInfinity;

        foreach (var point in points)
        {
            var dx = point.X - centroid.X;
            var dy = point.Y - centroid.Y;

            var along =
                dx * axisX +
                dy * axisY;

            var across =
                dx * normalX +
                dy * normalY;

            minAlong = Math.Min(minAlong, along);
            maxAlong = Math.Max(maxAlong, along);
            minAcross = Math.Min(minAcross, across);
            maxAcross = Math.Max(maxAcross, across);
        }

        return new PrincipalAxisAnalysis(
            centroid,
            axisX,
            axisY,
            normalX,
            normalY,
            minAlong,
            maxAlong,
            minAcross,
            maxAcross);
    }

    private bool Reject(
        GeometricShape shape,
        string reason,
        string metrics)
    {
        _diagnostics.Add(new StrokeDiagnostic(
            shape.Id,
            "REJECT",
            reason,
            metrics));

        return false;
    }

    private void Accept(
        GeometricShape shape,
        string reason,
        string metrics)
    {
        _diagnostics.Add(new StrokeDiagnostic(
            shape.Id,
            "ACCEPT",
            reason,
            metrics));
    }

    private static string Metrics(
        GeometricShape shape,
        GeometricContour contour,
        double length,
        double thickness,
        double elongation,
        double? fill)
    {
        var result =
            $"kind={shape.SourceKind} " +
            $"closed={contour.IsClosed} " +
            $"points={contour.Points.Count} " +
            $"length={length:0.###} " +
            $"thickness={thickness:0.###} " +
            $"elong={elongation:0.###}";

        if (fill is not null)
        {
            result += $" fill={fill:0.###}";
        }

        return result;
    }

    private sealed record PrincipalAxisAnalysis(
        PointD Centroid,
        double AxisX,
        double AxisY,
        double NormalX,
        double NormalY,
        double MinAlong,
        double MaxAlong,
        double MinAcross,
        double MaxAcross)
    {
        public double Length =>
            MaxAlong - MinAlong;

        public double ContourThickness =>
            MaxAcross - MinAcross;

        public double EffectiveThickness(double strokeWidth)
        {
            return Math.Max(
                ContourThickness,
                strokeWidth);
        }
    }
}
