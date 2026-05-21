# Visual Studio Examples

Build the compiler first:

```powershell
dotnet build ..\..\src\Flx.Compiler\Flx.Compiler.csproj -c Debug
```

Then open one of the solutions:

```text
FlxHello\FlxHello.sln
FlxWindow\FlxWindow.sln
```

The projects are normal Visual Studio C++ application projects. They import:

```text
..\..\..\build\visualstudio\Flx.Cpp.props
..\..\..\build\visualstudio\Flx.Cpp.targets
```

The example projects are pinned to the VS 2026 C++ toolset `v145`. Retarget the project to `v143` if you are using Visual Studio 2022.

`FlxHello` builds a console executable.

`FlxWindow` builds a native Win32 window executable and links:

```text
User32.lib
Gdi32.lib
```

Set the project as the startup project and press F5 to build and debug with the native Visual Studio debugger.
