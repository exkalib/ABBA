# NRftW Manager UI

Windows desktop UI shell for the item-manager workflow. It intentionally contains no game-process access, memory scanning, DLL loading, or write operations.

## Build on Windows

Install the .NET 8 SDK, then run:

```powershell
dotnet build
dotnet run
```

Every button currently records a preview action only. Wire a feature only after its separate in-game validation has passed.
