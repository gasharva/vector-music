namespace SvgMusic.Semantics;

public abstract class SemanticVisitor
{
    public void Visit(SemanticDocument document)
    {
        VisitDocument(document);
    }

    protected virtual void VisitDocument(SemanticDocument document)
    {
        foreach (var measure in document.Measures)
        {
            VisitMeasure(measure);
        }
    }

    protected virtual void VisitMeasure(MeasureScene measure)
    {
        VisitStaff(measure, measure.Upper);
        VisitStaff(measure, measure.Lower);
    }

    protected virtual void VisitStaff(
        MeasureScene measure,
        StaffMeasureScene staff)
    {
        foreach (var element in staff.Elements)
        {
            VisitElement(measure, staff, element);
        }
    }

    protected virtual void VisitElement(
        MeasureScene measure,
        StaffMeasureScene staff,
        SemanticElement element)
    {
        switch (element)
        {
            case StrokeElement stroke:
                VisitStroke(measure, staff, stroke);
                break;

            case CurveElement curve:
                VisitCurve(measure, staff, curve);
                break;

            case EllipseElement ellipse:
                VisitEllipse(measure, staff, ellipse);
                break;

            case ShapeElement shape:
                VisitShape(measure, staff, shape);
                break;

            case HairpinElement hairpin:
                VisitHairpin(measure, staff, hairpin);
                break;

            case BracketSpannerElement bracket:
                VisitBracketSpanner(measure, staff, bracket);
                break;

            default:
                throw new NotSupportedException(
                    $"Unsupported semantic element type: {element.GetType().Name}");
        }
    }

    protected virtual void VisitStroke(
        MeasureScene measure,
        StaffMeasureScene staff,
        StrokeElement stroke)
    {
    }

    protected virtual void VisitCurve(
        MeasureScene measure,
        StaffMeasureScene staff,
        CurveElement curve)
    {
    }

    protected virtual void VisitEllipse(
        MeasureScene measure,
        StaffMeasureScene staff,
        EllipseElement ellipse)
    {
    }

    protected virtual void VisitShape(
        MeasureScene measure,
        StaffMeasureScene staff,
        ShapeElement shape)
    {
    }

    protected virtual void VisitHairpin(
        MeasureScene measure,
        StaffMeasureScene staff,
        HairpinElement hairpin)
    {
    }

    protected virtual void VisitBracketSpanner(
        MeasureScene measure,
        StaffMeasureScene staff,
        BracketSpannerElement bracket)
    {
    }
}
