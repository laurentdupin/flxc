# FLX Visual Studio C++ Integration

This is the first Visual Studio integration layer for `flxc`. It does not define a custom FLX project type. Instead, a normal Visual Studio C++ project imports `Flx.Cpp.props` and `Flx.Cpp.targets`.

Build flow:

1. MSBuild runs `flxc --emit-c` for all `FlxCompile` items, or `flxc --package ... --emit-c` for one `FlxPackage` item.
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

Or use package mode:

```xml
<ItemGroup>
  <FlxPackage Include="flx.package.json" />
  <FlxSource Include="src\Main.flx" />
</ItemGroup>
```

For now, a project may use either `FlxPackage` or `FlxCompile`, not both. Package mode supports one package manifest item per C++ project. In package mode, list the package's `.flx` files as `FlxSource` items. The package manifest remains the compiler input, but `FlxSource` makes source edits visible to Visual Studio and MSBuild incremental checks.

Static-library package projects are supported by setting the normal C++ project type:

```xml
<PropertyGroup Label="Configuration">
  <ConfigurationType>StaticLibrary</ConfigurationType>
</PropertyGroup>
```

In that mode the target passes `--build-library`, suppresses generated `main`, and writes package metadata/public headers to:

```text
$(FlxPackageMetadata)
$(FlxPublicIncludeDir)
```

An executable project can consume an already-built FLX library with a package manifest binary dependency plus an explicit MSBuild item for the generated include directory:

```xml
<ItemGroup>
  <FlxBinaryPackageReference Include="ZombieLib">
    <MetadataPath>..\flx\ZombieLib\ZombieLib.flxmeta.json</MetadataPath>
    <IncludeDir>..\flx\ZombieLib\include</IncludeDir>
    <Library>..\$(Platform)\$(Configuration)\ZombieLib.lib</Library>
  </FlxBinaryPackageReference>
</ItemGroup>
```

The `FlxBinaryPackageReference` include directory is attached to generated C compilation. `MetadataPath` and `Library` are tracked as build inputs so executable projects regenerate/relink after a referenced FLX library changes. Link the `.lib` through a normal C++ `ProjectReference` when the library is in the same solution, or through standard C++ linker settings when it is external.

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

- Editor support is provided separately by the FLX VSIX; this folder only owns the MSBuild/C++ project integration.
- Debugging is native C-level. `#line` directives give best-effort mapping back to `.flx`.
- Generated C is kept under `$(IntDir)\flx`.
- Visual Studio fast up-to-date checks are disabled for FLX projects by default so source changes cannot be skipped before MSBuild runs. The MSBuild target then uses `FlxCompile`, `FlxPackage`, `FlxSource`, tracked dependency manifests, and binary package metadata/library inputs to decide whether `flxc` itself needs to rerun.

For `.flx` editor support, see:

```text
tools\visualstudio\Flx.VisualStudio
```
