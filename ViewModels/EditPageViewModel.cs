using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Windows.Foundation;

namespace ElliePdf.ViewModels;

/// <summary>
/// ViewModel for EditPage providing ink mode toggle, text annotation, and signature handling.
/// </summary>
public partial class EditPageViewModel : ObservableObject
{
    [ObservableProperty]
    public partial bool IsInkModeEnabled { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<TextAnnotationModel> TextAnnotations { get; set; } = new();

    [ObservableProperty]
    public partial ObservableCollection<SignatureModel> Signatures { get; set; } = new();

    public EditPageViewModel()
    {
        IsInkModeEnabled = false;
    }

    [RelayCommand]
    public void ToggleInkMode()
    {
        IsInkModeEnabled = !IsInkModeEnabled;
    }

    [RelayCommand]
    public void AddTextAnnotation(Point position)
    {
        var annotation = new TextAnnotationModel
        {
            Id = Guid.NewGuid().ToString(),
            Position = position,
            Text = "Type text here...",
            FontSize = 14
        };
        TextAnnotations.Add(annotation);
    }

    [RelayCommand]
    public void AddSignature(string imageDataBase64)
    {
        var signature = new SignatureModel
        {
            Id = Guid.NewGuid().ToString(),
            Position = new Point(100, 100),
            ImageDataBase64 = imageDataBase64,
            Width = 150,
            Height = 75
        };
        Signatures.Add(signature);
    }

    [RelayCommand]
    public void RemoveTextAnnotation(string annotationId)
    {
        var annotation = TextAnnotations.FirstOrDefault(a => a.Id == annotationId);
        if (annotation != null)
        {
            TextAnnotations.Remove(annotation);
        }
    }

    [RelayCommand]
    public void RemoveSignature(string signatureId)
    {
        var signature = Signatures.FirstOrDefault(s => s.Id == signatureId);
        if (signature != null)
        {
            Signatures.Remove(signature);
        }
    }
}

/// <summary>
/// Model for text annotation on the canvas.
/// </summary>
public class TextAnnotationModel : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    private Point _position;
    public Point Position
    {
        get => _position;
        set => SetProperty(ref _position, value);
    }

    private string _text = string.Empty;
    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    private double _fontSize = 14;
    public double FontSize
    {
        get => _fontSize;
        set => SetProperty(ref _fontSize, value);
    }
}

/// <summary>
/// Model for signature on the canvas.
/// </summary>
public class SignatureModel : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    private Point _position;
    public Point Position
    {
        get => _position;
        set => SetProperty(ref _position, value);
    }

    private string _imageDataBase64 = string.Empty;
    public string ImageDataBase64
    {
        get => _imageDataBase64;
        set => SetProperty(ref _imageDataBase64, value);
    }

    private double _width = 150;
    public double Width
    {
        get => _width;
        set => SetProperty(ref _width, value);
    }

    private double _height = 75;
    public double Height
    {
        get => _height;
        set => SetProperty(ref _height, value);
    }
}
