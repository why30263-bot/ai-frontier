# Reader v2 integration checks

This harness compiles the production `MainPageViewModel.cs` and
`PreferenceService.cs` directly, with minimal platform stubs. It does not modify
or copy their behavior.

Run from the repository root:

```powershell
$env:NUGET_PACKAGES='D:\DevTools\nuget-packages'
& 'D:\DevTools\dotnet\dotnet.exe' run `
  --project Tests\AIFrontier.ReaderV2.IntegrationTests.csproj `
  -p:BaseIntermediateOutputPath='D:\??\.build\reader-v2-tests\obj\' `
  -p:BaseOutputPath='D:\??\.build\reader-v2-tests\bin\' `
  -p:RestoreIgnoreFailedSources=true `
  -p:NuGetAudit=false
```

The process exits with code 1 while any release-blocking assertion fails.
