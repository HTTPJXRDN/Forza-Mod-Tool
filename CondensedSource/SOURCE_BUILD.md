# Build the source package

Install the .NET 8 SDK for Windows, then open FH6ModTool.slnx in Visual Studio or run:

```powershell
dotnet build FH6ModTool.csproj -c Release
```

The package includes lib/FH6LocalCrypto.Runtime.dll, the protected crypto runtime. Keep it with the project; private key/context source files are not included.

FH2 saves and FM8 LZX operations need your own x64 xcompress64.dll beside the built EXE. See NATIVE_RUNTIME.md. The animation helpers and their licenses are included.

Help/*.txt files are editable. They copy beside the app when you build or publish. Keep permanent wording changes in the source copies.

The source ZIP has no bin or obj folders. Building it will create those folders locally.
