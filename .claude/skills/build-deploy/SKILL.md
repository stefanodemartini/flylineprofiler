---
name: build-deploy
description: Build, publish and deploy the FlyLine Profiler WPF app. Use whenever code under app/ changed and Stefano needs to test it — covers the exe file-lock dance, the self-contained publish, and the Desktop copy he actually runs.
---

# Building and deploying FlyLine Profiler

`app/DiametroLineaDesktop.csproj` — .NET 8 WPF, `AssemblyName` = **FlyLineProfiler.exe** (note: project name and exe name differ).

## The one rule that matters

**Stefano runs the copy on his Desktop, not the Debug build.** A `dotnet build` alone updates only
`app/bin/Debug/net8.0-windows/FlyLineProfiler.exe`. If he tests from the Desktop after that, he is
testing *stale code* and will report bugs you already fixed. This wasted real time in a past session.
Every change he needs to see must end with the publish + Desktop copy.

## Never close the app yourself

He may be mid-acquisition with scan data in memory. Check, then **ask and wait**:

```bash
tasklist //FI "IMAGENAME eq FlyLineProfiler.exe"
```

If it's running, tell him you're ready and wait for his confirmation ("chiusa"). Building while it
runs fails with `MSB3027 / MSB3021: cannot access FlyLineProfiler.exe ... used by another process`
(the compile itself succeeds — only the exe copy step fails, so a "Build FAILED" with only MSB302x
errors means your code is fine).

## Full cycle

```bash
# 1. verify closed (see above), then compile-check
cd "C:\Users\Stefano\Documents\Arduino\flylineprofiler\app"
dotnet build DiametroLineaDesktop.csproj -c Debug -v q --nologo

# 2. self-contained single-file publish
dotnet publish DiametroLineaDesktop.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -o "C:\Users\Stefano\Documents\Arduino\flylineprofiler\publish"

# 3. the copy he actually runs (~200 MB, takes a moment)
cp "C:\Users\Stefano\Documents\Arduino\flylineprofiler\publish\FlyLineProfiler.exe" \
   "C:\Users\Stefano\Desktop\FlyLineProfiler.exe"
ls -la "C:\Users\Stefano\Desktop\FlyLineProfiler.exe"   # confirm the timestamp moved

# 4. relaunch for him
```
```powershell
Start-Process "C:\Users\Stefano\Desktop\FlyLineProfiler.exe"
```

Report the new Desktop timestamp back to him — it's how he confirms he's on the right build.

## Debug-only shortcut

For a quick compile check (no deploy needed yet), step 1 alone is enough, launching
`app\bin\Debug\net8.0-windows\FlyLineProfiler.exe`. But if he's going to *test* it, deploy properly.

## Diagnosing "the fix didn't work"

Before assuming your change is wrong:
1. Is the Desktop exe newer than your edit? (`ls -la`)
2. Did he reopen the project file? The app does **not** watch files — an .flp edited on disk while
   open keeps showing the stale in-memory version until File > Open again.
3. Is a stale process still running? A wedged instance has shown a blank chart until relaunched.
