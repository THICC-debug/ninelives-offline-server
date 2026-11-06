# FreezeFinder (developer tool)

> [!NOTE]
> Made with AI (Anthropic's Claude, in Claude Code). Not part of the player release.

Finds where the game hangs. It hooks every method of the game's own code and keeps a call stack
for the main thread. If the game stops updating for 4 seconds, it writes that stack and the
most-called methods to `Mods\FreezeFinder.log` (again at 10 and 16 seconds).

An empty stack with no calls means the main thread is stuck outside the game's code, usually
in a mod. That's how the ContentLoader 1.2.0 export stall was found.

```
dotnet build -c Release      # build and install into the game
```

Remove `Mods\FreezeFinder.dll` when you're done: it slows the game a little.

License: MIT (code only; the game belongs to SmokymonkeyS).
