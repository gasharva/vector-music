using SvgMusic.Scene;

namespace SvgMusic.Semantics;

public sealed record SemanticDocument(
    IReadOnlyList<MeasureScene> Measures);

public sealed record MeasureScene(
    int Number,
    string SystemId,
    string PairId,
    string LayoutMeasureId,
    double XStart,
    double XEnd,
    bool BreakBefore,
    StaffMeasureScene Upper,
    StaffMeasureScene Lower);

public sealed record StaffMeasureScene(
    int StaffNumber,
    string StaffId,
    IReadOnlyList<SemanticElement> Elements);

public abstract record SemanticElement
{
    public required string ShapeId { get; init; }
    public required BoundsD Bounds { get; init; }
    public required LogicalOwnership Ownership { get; init; }

    public double CenterX => Bounds.CenterX;
    public double CenterY => Bounds.CenterY;
}

public sealed record StrokeElement : SemanticElement
{
    public required Stroke Source { get; init; }
}

public sealed record CurveElement : SemanticElement
{
    public required CurvedStroke Source { get; init; }
}

public sealed record EllipseElement : SemanticElement
{
    public required EllipseLike Source { get; init; }
}

public sealed record ShapeElement : SemanticElement
{
    public required ShapeInstance Source { get; init; }
    public SymbolClassification? Classification => Source.Classification;
}
