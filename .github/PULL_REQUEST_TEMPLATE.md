## Summary

<!-- What does this PR do? One or two sentences. -->

## Related issue

Closes #<!-- issue number -->

## Changes

<!-- Bullet list of key changes -->

## Testing

- [ ] `dotnet build ElliePdf.slnx -p:Platform=x64` passes
- [ ] `dotnet test ElliePdf.Tests/ElliePdf.Tests.csproj` passes
- [ ] Manually verified in the running app (if UI changes)

## Checklist

- [ ] No `PdfDocumentSession` leaks (every open has a matching close in a `finally` block)
- [ ] All new async methods accept and forward `CancellationToken`
- [ ] No `pdfium.dll` or other native binaries committed
- [ ] New services registered in `App.xaml.cs` DI container
- [ ] Tests added or updated in `ElliePdf.Tests/` for new logic
