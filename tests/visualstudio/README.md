# FLX Fixture Visual Studio Projects

Open `FlxFixtures.sln` in Visual Studio to browse and build representative FLX fixture programs.

- `FlxFixtureBrowser` is a utility project that includes every `tests/fixtures/**/*.flx` file for syntax highlighting and navigation. It does not compile the diagnostic fixtures.
- `FlxNumericComponentFields`, `FlxParallelZombies`, and `FlxParallelMutatingFields` are executable projects for buildable fixture programs.
- `FlxTaskDeclaration` is a static library project for the task declaration syntax fixture. It also shows the negative task fixtures as source files for review.
