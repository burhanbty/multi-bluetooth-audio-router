# Contributing

Thanks for taking the time to improve Multi Bluetooth Audio Router.

## Development setup

1. Use Windows 10 or 11 with the .NET 8 SDK.
2. Restore with `dotnet restore MultiBluetoothAudioRouter.sln --locked-mode`.
3. Build with `dotnet build MultiBluetoothAudioRouter.sln -c Release --no-restore`.
4. Run tests with `dotnet run --project MultiBluetoothAudioRouter.Tests/MultiBluetoothAudioRouter.Tests.csproj -c Release --no-build`.

## Pull requests

- Keep audio-engine behavior separate from WPF presentation code.
- Preserve cancellation and cleanup paths when changing endpoint lifecycle code.
- Add deterministic tests for classifier and error-mapping changes.
- Explain any new buffering or timing constant in code and in the pull request.
- Do not commit device logs, endpoint identifiers, build output, or local archives.

## Bug reports

Include:

- Windows version and architecture
- Bluetooth adapter and driver version, if applicable
- Endpoint types and whether each works independently
- Whether opening order changes the result
- The symbolic HRESULT and failure category

Diagnostic reports can contain local endpoint names and identifiers. Redact personal or machine-specific data before posting them.
