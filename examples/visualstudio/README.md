# Visual Studio Examples

Build the compiler first:

```powershell
dotnet build ..\..\src\Flx.Compiler\Flx.Compiler.csproj -c Debug
```

Then open one of the solutions:

```text
FlxHello\FlxHello.sln
FlxCrossFile\FlxCrossFile.sln
FlxPackageZombie\FlxPackageZombie.sln
FlxWindow\FlxWindow.sln
```

The projects are normal Visual Studio C++ application projects. They import:

```text
..\..\..\build\visualstudio\Flx.Cpp.props
..\..\..\build\visualstudio\Flx.Cpp.targets
```

The example projects are pinned to the VS 2026 C++ toolset `v145`. Retarget the project to `v143` if you are using Visual Studio 2022.

Each project also includes a `.vcxproj.filters` file that places explicitly listed `.flx` files under:

```text
FLX Source Files
```

Generated C remains under `$(IntDir)\flx` and is not shown as user source in Solution Explorer.

`FlxHello` builds a console executable.

`FlxCrossFile` builds a console executable from two `.flx` files. `Main.flx` calls a function defined in `Greeting.flx`, which exercises generated module headers and cross-file FLX symbol visibility.

`FlxPackageZombie` builds a console executable from a `flx.package.json` manifest. The app package depends on a source library package under `libs\ZombieLib`, which exercises package-mode dependency loading through MSBuild.

`FlxWindow` builds a native Win32 window executable and links:

```text
User32.lib
Gdi32.lib
```

Set the project as the startup project and press F5 to build and debug with the native Visual Studio debugger.

For basic `.flx` syntax coloring and editor behavior, build and install the VSIX in:

```text
..\..\tools\visualstudio\Flx.VisualStudio
```

For VS Code syntax coloring, open or install the extension folder at:

```text
..\..\tools\vscode\flx
```
