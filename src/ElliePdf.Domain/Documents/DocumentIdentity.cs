namespace ElliePdf.Domain.Documents;

public readonly record struct DocumentId(Guid Value)
{
    public static DocumentId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}

public readonly record struct PageId(Guid Value)
{
    public static PageId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}

public readonly record struct ContentRevision(long Value)
{
    public static ContentRevision Initial => new(0);

    public ContentRevision Next() => new(checked(Value + 1));
}

public readonly record struct StructureRevision(long Value)
{
    public static StructureRevision Initial => new(0);

    public StructureRevision Next() => new(checked(Value + 1));
}

public readonly record struct PageContentRevision(long Value)
{
    public static PageContentRevision Initial => new(0);

    public PageContentRevision Next() => new(checked(Value + 1));
}

public readonly record struct PageAppearanceRevision(long Value)
{
    public static PageAppearanceRevision Initial => new(0);

    public PageAppearanceRevision Next() => new(checked(Value + 1));
}

public readonly record struct RenderGeneration(long Value)
{
    public static RenderGeneration Initial => new(0);

    public RenderGeneration Next() => new(checked(Value + 1));
}

public readonly record struct SearchGeneration(long Value)
{
    public static SearchGeneration Initial => new(0);

    public SearchGeneration Next() => new(checked(Value + 1));
}
