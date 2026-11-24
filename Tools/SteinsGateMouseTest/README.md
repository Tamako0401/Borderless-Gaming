# STEINS;GATE mouse input test

When the Steam game's 16:9 client is stretched to a larger 16:9 monitor, its input can still use the original client dimensions. The test package maps the game's screen-coordinate mouse poll back to the original client size while the stretched window has focus. The desktop pointer and game files stay untouched.

The native helper checks the SHA-256 of the Steam x86 Game.exe build it supports before installing a temporary hook on the game's window thread. The game-side helper validates the loaded image's cursor import and the fullscreen client geometry. It forwards every mouse poll normally when the game loses focus, its geometry changes, it is restored in Borderless Gaming, or Borderless Gaming exits. The installed helper stays loaded until the game exits.

Build on Windows with Visual Studio 2022, the .NET Framework 4.8 developer pack, CMake and Python. Run these commands from the repository root.

```nu
msbuild BorderlessGaming.sln /restore /t:Build /p:Configuration=Release
cmake -S Tools/SteinsGateMouseTest -B out/steins-gate-native -G 'Visual Studio 17 2022' -A Win32
cmake --build out/steins-gate-native --config Release
python Tools/SteinsGateMouseTest/package.py --native-build out/steins-gate-native --output out/Borderless-Gaming-SteinsGate-Mouse-Test-v2.zip
```

In the test package, `mouse-test.log` reports the native installation result and a sample of the first remapped game mouse poll. The marker file `steins-gate-test.txt` enables the test at launch. Without the marker, normal launches use the regular profile and cursor handling.
