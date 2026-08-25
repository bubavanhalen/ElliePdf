namespace ElliePdf.ViewModels;

public enum ReaderEditTool
{
    Select,
    Ink,
    Text,
    Signature,
    Eraser,
    Rectangle,
    Ellipse,
    Line,
    Arrow
}

public static class ReaderEditToolExtensions
{
    public static bool IsShape(this ReaderEditTool tool) =>
        tool is ReaderEditTool.Rectangle or ReaderEditTool.Ellipse or ReaderEditTool.Line or ReaderEditTool.Arrow;

    public static Models.ShapeKind ToShapeKind(this ReaderEditTool tool) => tool switch
    {
        ReaderEditTool.Ellipse => Models.ShapeKind.Ellipse,
        ReaderEditTool.Line => Models.ShapeKind.Line,
        ReaderEditTool.Arrow => Models.ShapeKind.Arrow,
        _ => Models.ShapeKind.Rectangle
    };
}
