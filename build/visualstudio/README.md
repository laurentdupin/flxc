# FLX Visual Studio C++ Integration

This is the first Visual Studio integration layer for `flxc`. It does not define a custom FLX project type. Instead, a normal Visual Studio C++ project imports `Flx.Cpp.props` and `Flx.Cpp.targets`.

Build flow:

1. MSBuild runs `flxc --emit-c` for all `FlxCompile` items.
2. `flxc` writes generated C into `$(IntDir)\flx\`.
3. `flxc` writes `generated_c_files.txt`.
4. The target reads that list and adds the generated `.c` files to `ClCompile`.
5. Visual Studio compiles, links, launches, and debugs the native executable normally.

## Build flxc

From the repo root:

```powershell
dotnet build src\Flx.Compiler\Flx.Compiler.csproj -c Debug
```

The default property sheet expects:

```text
src\Flx.Compiler\bin\$(Configuration)\net10.0\flxc.exe
```

Override `FlxCompilerPath` in your `.vcxproj` if you keep `flxc.exe` elsewhere.

## Project Usage

Add imports to a `.vcxproj`:

```xml
<Import Project="path\to\build\visualstudio\Flx.Cpp.props" />
...
<ItemGroup>
  <FlxCompile Include="main.flx" />
</ItemGroup>
...
<Import Project="path\to\build\visualstudio\Flx.Cpp.targets" />
```

Useful project properties:

```xml
<PropertyGroup>
  <FlxCompilerPath>$(SolutionDir)..\..\src\Flx.Compiler\bin\Debug\net10.0\flxc.exe</FlxCompilerPath>
  <FlxPreprocessorDefinitions>WIN32_LEAN_AND_MEAN;UNICODE</FlxPreprocessorDefinitions>
  <FlxAdditionalIncludeDirectories>$(ProjectDir)include</FlxAdditionalIncludeDirectories>
  <FlxAdditionalOptions>--diagnostics-format msbuild --absolute-line-directives</FlxAdditionalOptions>
</PropertyGroup>
```

## Limitations

- No FLX IntelliSense or VSIX yet.
- Debugging is native C-level. `#line` directives give best-effort mapping back to `.flx`.
- Generated C is kept under `$(IntDir)\flx`.
- Incremental generation is intentionally simple for now; FLX C is regenerated before C compilation.
