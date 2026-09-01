using System.Collections.Immutable;
using ElliePdf.Domain.Documents;

namespace ElliePdf.Pdf.Contracts;

public enum FormWidgetType
{
    Text,
    Checkbox,
    RadioButton,
    ComboBox,
    ListBox,
    PushButton,
    Signature,
    Unsupported
}

public enum FormValueKind
{
    None,
    Text,
    Boolean,
    Choice,
    Choices
}

public sealed record FormValue
{
    public FormValue(FormValueKind kind, string? text, bool? boolean, ImmutableArray<string> choices)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        var normalizedChoices = PdfContractLimits.ReadOnly(
            choices.IsDefault ? [] : choices,
            PdfContractLimits.MaxFormOptions,
            nameof(choices));
        foreach (var choice in normalizedChoices)
        {
            PdfContractLimits.RequiredString(choice, PdfContractLimits.MaxStringLength, nameof(choices));
        }
        switch (kind)
        {
            case FormValueKind.None when text is not null || boolean is not null || !normalizedChoices.IsEmpty:
                throw new ArgumentException("An empty form value cannot carry data.", nameof(kind));
            case FormValueKind.Text when text is null || boolean is not null || !normalizedChoices.IsEmpty:
                throw new ArgumentException("A text form value must contain only text.", nameof(kind));
            case FormValueKind.Choice when text is null || boolean is not null || !normalizedChoices.IsEmpty:
                throw new ArgumentException("A choice form value must contain one choice.", nameof(kind));
            case FormValueKind.Boolean when text is not null || boolean is null || !normalizedChoices.IsEmpty:
                throw new ArgumentException("A boolean form value must contain only a boolean.", nameof(kind));
            case FormValueKind.Choices when text is not null || boolean is not null:
                throw new ArgumentException("A multiple-choice form value must contain only choices.", nameof(kind));
        }
        if (text is not null && text.Length > PdfContractLimits.MaxStringLength)
        {
            throw new ArgumentOutOfRangeException(nameof(text));
        }
        Kind = kind;
        Text = text;
        Boolean = boolean;
        Choices = normalizedChoices;
    }

    public static FormValue None() => new(FormValueKind.None, null, null, []);

    public static FormValue TextValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > PdfContractLimits.MaxStringLength) throw new ArgumentOutOfRangeException(nameof(value));
        return new(FormValueKind.Text, value, null, []);
    }

    public static FormValue BooleanValue(bool value) => new(FormValueKind.Boolean, null, value, []);

    public static FormValue Choice(string value)
    {
        return new(FormValueKind.Choice, PdfContractLimits.RequiredString(value, PdfContractLimits.MaxStringLength, nameof(value)), null, []);
    }

    public static FormValue MultipleChoices(IEnumerable<string> values)
    {
        var choices = PdfContractLimits.ReadOnly(values, PdfContractLimits.MaxFormOptions, nameof(values));
        foreach (var choice in choices) PdfContractLimits.RequiredString(choice, PdfContractLimits.MaxStringLength, nameof(values));
        return new(FormValueKind.Choices, null, null, choices);
    }

    public PdfContractVersion ContractVersion => PdfContractVersion.Current;
    public FormValueKind Kind { get; }
    public string? Text { get; }
    public bool? Boolean { get; }
    public ImmutableArray<string> Choices { get; }
}

public sealed record FormWidget
{
    public FormWidget(FormFieldId id, DocumentId documentId, PageId pageId, int pageIndex, FormWidgetType type, string fieldName, PdfRect bounds, FormValue value, bool isReadOnly = false, bool isRequired = false, ImmutableArray<string> options = default, bool isSupported = true, string? unsupportedReason = null)
    {
        Id = id.Validate();
        if (documentId.Value == Guid.Empty) throw new ArgumentException("The document id must not be empty.", nameof(documentId));
        if (pageId.Value == Guid.Empty) throw new ArgumentException("The page id must not be empty.", nameof(pageId));
        PdfContractLimits.PageIndex(pageIndex);
        PdfContractLimits.RequiredString(fieldName, PdfContractLimits.MaxStringLength, nameof(fieldName));
        Bounds = bounds.Validate();
        Value = value ?? throw new ArgumentNullException(nameof(value));
        DocumentId = documentId;
        PageId = pageId;
        PageIndex = pageIndex;
        Type = type;
        FieldName = fieldName;
        IsReadOnly = isReadOnly;
        IsRequired = isRequired;
        Options = PdfContractLimits.ReadOnly(options.IsDefault ? [] : options, PdfContractLimits.MaxFormOptions, nameof(options));
        foreach (var option in Options) PdfContractLimits.RequiredString(option, PdfContractLimits.MaxStringLength, nameof(options));
        UnsupportedReason = PdfContractLimits.OptionalString(unsupportedReason, PdfContractLimits.MaxStringLength, nameof(unsupportedReason));
        if (!isSupported && string.IsNullOrWhiteSpace(UnsupportedReason))
            throw new ArgumentException("Unsupported form widgets require an accessible reason.", nameof(unsupportedReason));
        IsSupported = isSupported;
    }

    public PdfContractVersion ContractVersion => PdfContractVersion.Current;
    public FormFieldId Id { get; }
    public DocumentId DocumentId { get; }
    public PageId PageId { get; }
    public int PageIndex { get; }
    public FormWidgetType Type { get; }
    public string FieldName { get; }
    public PdfRect Bounds { get; }
    public FormValue Value { get; }
    public bool IsReadOnly { get; }
    public bool IsRequired { get; }
    public ImmutableArray<string> Options { get; }
    public bool IsSupported { get; }
    public string? UnsupportedReason { get; }
}

public sealed record FormWidgetsResult
{
    public FormWidgetsResult(DocumentId documentId, ImmutableArray<FormWidget> widgets)
    {
        if (documentId.Value == Guid.Empty) throw new ArgumentException("The document id must not be empty.", nameof(documentId));
        DocumentId = documentId;
        Widgets = PdfContractLimits.ReadOnly(widgets.IsDefault ? [] : widgets, PdfContractLimits.MaxCollectionCount, nameof(widgets));
    }

    public PdfContractVersion ContractVersion => PdfContractVersion.Current;
    public DocumentId DocumentId { get; }
    public ImmutableArray<FormWidget> Widgets { get; }
}

public sealed record FormValueChange
{
    public FormValueChange(DocumentId documentId, FormFieldId fieldId, FormValue value, ContentRevision expectedContentRevision)
    {
        if (documentId.Value == Guid.Empty) throw new ArgumentException("The document id must not be empty.", nameof(documentId));
        DocumentId = documentId;
        FieldId = fieldId.Validate();
        Value = value ?? throw new ArgumentNullException(nameof(value));
        ExpectedContentRevision = expectedContentRevision;
    }

    public PdfContractVersion ContractVersion => PdfContractVersion.Current;
    public DocumentId DocumentId { get; }
    public FormFieldId FieldId { get; }
    public FormValue Value { get; }
    public ContentRevision ExpectedContentRevision { get; }
}

/// <summary>
/// A value-free request to activate an actionless PDF push button.  Push buttons
/// are controls, not string-valued form fields, so this operation is deliberately
/// separate from <see cref="FormValueChange"/>.
/// </summary>
public sealed record PushButtonInvocation
{
    public PushButtonInvocation(DocumentId documentId, FormFieldId fieldId, ContentRevision expectedContentRevision)
    {
        if (documentId.Value == Guid.Empty) throw new ArgumentException("The document id must not be empty.", nameof(documentId));
        DocumentId = documentId;
        FieldId = fieldId.Validate();
        ExpectedContentRevision = expectedContentRevision;
    }

    public PdfContractVersion ContractVersion => PdfContractVersion.Current;
    public DocumentId DocumentId { get; }
    public FormFieldId FieldId { get; }
    public ContentRevision ExpectedContentRevision { get; }
}

[Flags]
public enum PdfPermissionFlags
{
    None = 0,
    Print = 1,
    Modify = 2,
    Copy = 4,
    Annotate = 8,
    FillForms = 16,
    Accessibility = 32,
    Assemble = 64,
    HighQualityPrint = 128
}

public sealed record PdfPermissions
{
    public PdfPermissions(bool canCopy = true, bool canPrint = true, bool canModify = true, bool canFillForms = true)
        : this((canCopy ? PdfPermissionFlags.Copy : PdfPermissionFlags.None)
            | (canPrint ? PdfPermissionFlags.Print : PdfPermissionFlags.None)
            | (canModify ? PdfPermissionFlags.Modify : PdfPermissionFlags.None)
            | (canModify ? PdfPermissionFlags.Annotate : PdfPermissionFlags.None)
            | (canFillForms ? PdfPermissionFlags.FillForms : PdfPermissionFlags.None), false, false)
    {
    }

    public PdfPermissions(PdfPermissionFlags allowed, bool isEncrypted, bool isOwnerPasswordAuthenticated)
    {
        Allowed = allowed;
        IsEncrypted = isEncrypted;
        IsOwnerPasswordAuthenticated = isOwnerPasswordAuthenticated;
    }

    public PdfContractVersion ContractVersion => PdfContractVersion.Current;
    public PdfPermissionFlags Allowed { get; }
    public bool IsEncrypted { get; }
    public bool IsOwnerPasswordAuthenticated { get; }
    public bool CanPrint => (Allowed & PdfPermissionFlags.Print) != 0;
    public bool CanModify => (Allowed & PdfPermissionFlags.Modify) != 0;
    public bool CanCopy => (Allowed & PdfPermissionFlags.Copy) != 0;
    public bool CanAnnotate => (Allowed & PdfPermissionFlags.Annotate) != 0;
    public bool CanFillForms => (Allowed & PdfPermissionFlags.FillForms) != 0;
    public bool CanAssemble => (Allowed & PdfPermissionFlags.Assemble) != 0;
}

public readonly record struct PrintPageRange
{
    public PrintPageRange(int firstPageIndex, int lastPageIndex)
    {
        PdfContractLimits.PageIndex(firstPageIndex, nameof(firstPageIndex));
        PdfContractLimits.PageIndex(lastPageIndex, nameof(lastPageIndex));
        if (lastPageIndex < firstPageIndex) throw new ArgumentOutOfRangeException(nameof(lastPageIndex));
        FirstPageIndex = firstPageIndex;
        LastPageIndex = lastPageIndex;
    }

    public int FirstPageIndex { get; }
    public int LastPageIndex { get; }
}

public sealed record PrintRequest
{
    public PrintRequest(DocumentId documentId, IEnumerable<int> pageIndices, bool fitToPage = true)
        : this(documentId, ToRanges(pageIndices), 1, true, false, fitToPage)
    {
    }

    public PrintRequest(DocumentId documentId, IEnumerable<PrintPageRange> pageRanges, double scale = 1, bool collate = true, bool landscape = false, bool fitToPage = true)
    {
        if (documentId.Value == Guid.Empty) throw new ArgumentException("The document id must not be empty.", nameof(documentId));
        var ranges = PdfContractLimits.ReadOnly(pageRanges, PdfContractLimits.MaxPageRanges, nameof(pageRanges));
        if (ranges.IsDefaultOrEmpty) throw new ArgumentException("At least one page range is required.", nameof(pageRanges));
        PdfContractLimits.FinitePositive(scale, nameof(scale));
        DocumentId = documentId;
        PageRanges = ranges;
        Scale = scale;
        Collate = collate;
        Landscape = landscape;
        FitToPage = fitToPage;
    }

    public PdfContractVersion ContractVersion => PdfContractVersion.Current;
    public DocumentId DocumentId { get; }
    public ImmutableArray<PrintPageRange> PageRanges { get; }
    public double Scale { get; }
    public bool Collate { get; }
    public bool Landscape { get; }
    public bool FitToPage { get; }

    private static IEnumerable<PrintPageRange> ToRanges(IEnumerable<int> pageIndices)
    {
        ArgumentNullException.ThrowIfNull(pageIndices);
        return pageIndices.Select(pageIndex => new PrintPageRange(pageIndex, pageIndex));
    }
}
